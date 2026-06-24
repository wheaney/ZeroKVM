using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace ZeroKvm;

internal class DlMemory
{
    private const int MaxPixels = 1152 * 2048;
    private const int RegisterMemoryOffset = 0xc300;

    public DlMemory()
    {
        _ram = GC.AllocateArray<byte>(64 * 1024, true);
        _frameBuffer = GC.AllocateUninitializedArray<byte>((20 * 1024 * 1024) + 256, true); // 256 more bytes to allow overflowing commands
        MemoryMarshal.Cast<byte, ushort>(_frameBuffer.AsSpan()).Fill(0b0000011111100000);
        _frameBufferDiff16 = GC.AllocateArray<ushort>(MaxPixels, true);
        _frameBufferDiff8 = GC.AllocateArray<byte>(MaxPixels, true);
        _compWrittenBits = new byte[(_frameBuffer.Length / CompWrittenGranularity + 7) / 8];
    }

    private readonly byte[] _ram;
    public Span<byte> Ram => _ram;

    public RgbColorDepth ColorDepth => (RgbColorDepth)_ram[RegisterMemoryOffset + (int)DlRegisterAddress.ColorDepth];

    public bool BlankOutput => _ram[RegisterMemoryOffset + (int)DlRegisterAddress.BlankOutput] != 0;
    public int HorizontalResolution => _horizontalResolution;
    public int VerticalResolution => _verticalResolution;
    public int FrameBuffer16BaseOffset => _fb16BaseOffset;
    public int FrameBuffer16LineStride => _fb16LineStride;
    public int FrameBuffer8BaseOffset => _fb8BaseOffset;
    public int FrameBuffer8LineStride => _fb8LineStride;

    private int _horizontalResolution;
    private int _verticalResolution;
    private int _fb16BaseOffset;
    private int _fb16LineStride;
    private int _fb8BaseOffset;
    private int _fb8LineStride;

    private readonly byte[] _frameBuffer;
    public Span<byte> FrameBuffer => _frameBuffer;

    private readonly ushort[] _frameBufferDiff16;
    private readonly byte[] _frameBufferDiff8;

    /* Bitset tracking which 256-byte blocks of the framebuffer were written by WriteComp this frame.
     * Reset at CopyFrameBufferTo time. Used to measure Copy-source-overlaps-Comp frequency. */
    private readonly byte[] _compWrittenBits;
    private const int CompWrittenGranularity = 256;

    /* Dirty byte range tracked by MarkDirty(); reset by ResetDirtyRange().
     * Kept for the empty/fallback decision in CopyFrameBufferTo. */
    private int _dirtyByteMin = int.MaxValue;
    private int _dirtyByteMax = int.MinValue;

    /* Per-row dirty bitset (1 bit/row), keyed on the 16-bit-plane row geometry.
     * Set by MarkDirty(), consumed and cleared at CopyFrameBufferTo time. This
     * is what keeps the converted/blitted region TIGHT: a single 1D byte min/max
     * spanning a cursor at the top and a caret at the bottom would otherwise mark
     * the whole frame dirty. We track the actual rows instead so we convert/blit
     * only those, not everything in between. _dirtyRowMin/Max bound the scan. */
    private const int MaxRows = 2048;
    private readonly byte[] _dirtyRowBits = new byte[(MaxRows + 7) / 8];
    private int _dirtyRowMin = int.MaxValue;
    private int _dirtyRowMax = int.MinValue;
    private long _lastDirtyDebugMs;
    private int _markDirtyCalls, _markRowSetCalls, _markMissCalls;
    private int _dbgBaseAtMark = -1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkDirty(int byteAddress, int byteCount)
    {
        _markDirtyCalls++;
        if (byteCount <= 0)
            return;
        /* DEBUG: snapshot the scanout base as seen at write time, to compare with
         * the base at sync time and the actual write address. */
        _dbgBaseAtMark = _fb16BaseOffset;
        if (byteAddress < _dirtyByteMin)
            _dirtyByteMin = byteAddress;
        int end = byteAddress + byteCount;
        if (end > _dirtyByteMax)
            _dirtyByteMax = end;

        /* Resolve the touched rows in 16-bit-plane terms. A write command lands
         * in either the 16-bit (RGB565) plane or the 8-bit (RGB323) plane; both
         * describe the same logical rows but at different base/stride. Map each
         * plane's byte address to a row so the bitset is plane-agnostic. */
        int rowFirst, rowLast;
        if (_fb16LineStride > 0 && byteAddress >= _fb16BaseOffset &&
            byteAddress < _fb16BaseOffset + _fb16LineStride * MaxRows)
        {
            int rel = byteAddress - _fb16BaseOffset;
            rowFirst = rel / _fb16LineStride;
            rowLast = (end - 1 - _fb16BaseOffset) / _fb16LineStride;
        }
        else if (_fb8LineStride > 0 && byteAddress >= _fb8BaseOffset &&
                 byteAddress < _fb8BaseOffset + _fb8LineStride * MaxRows)
        {
            int rel = byteAddress - _fb8BaseOffset;
            rowFirst = rel / _fb8LineStride;
            rowLast = (end - 1 - _fb8BaseOffset) / _fb8LineStride;
        }
        else
        {
            _markMissCalls++;
            return;
        }

        if (rowFirst < 0)
            rowFirst = 0;
        if (rowLast >= MaxRows)
            rowLast = MaxRows - 1;
        if (rowLast < rowFirst)
            return;

        _markRowSetCalls++;
        if (rowFirst < _dirtyRowMin)
            _dirtyRowMin = rowFirst;
        if (rowLast > _dirtyRowMax)
            _dirtyRowMax = rowLast;
        for (int r = rowFirst; r <= rowLast; r++)
            _dirtyRowBits[r >> 3] |= (byte)(1 << (r & 7));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsRowDirty(int row)
    {
        return (uint)row < MaxRows &&
               (_dirtyRowBits[row >> 3] & (byte)(1 << (row & 7))) != 0;
    }

    public void ResetDirtyRange()
    {
        _dirtyByteMin = int.MaxValue;
        _dirtyByteMax = int.MinValue;
        if (_dirtyRowMin <= _dirtyRowMax)
        {
            /* Clear only the touched span of the bitset, not the whole array. */
            int firstByte = _dirtyRowMin >> 3;
            int lastByte = _dirtyRowMax >> 3;
            _dirtyRowBits.AsSpan(firstByte, lastByte - firstByte + 1).Clear();
        }
        _dirtyRowMin = int.MaxValue;
        _dirtyRowMax = int.MinValue;
        _compWrittenBits.AsSpan().Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkCompWritten(int byteAddress, int byteCount)
    {
        int first = byteAddress / CompWrittenGranularity;
        int last = (byteAddress + byteCount - 1) / CompWrittenGranularity;
        for (int i = first; i <= last; i++)
            _compWrittenBits[i >> 3] |= (byte)(1 << (i & 7));
    }

    public bool CheckCopySourceOverlapsComp(int byteAddress, int byteCount)
    {
        int first = byteAddress / CompWrittenGranularity;
        int last = (byteAddress + byteCount - 1) / CompWrittenGranularity;
        for (int i = first; i <= last; i++)
            if ((_compWrittenBits[i >> 3] & (byte)(1 << (i & 7))) != 0)
                return true;
        return false;
    }

    /* Per-command-type pixel counters — accumulated across all Process() calls. */
    public long StatsWriteRlx16Pixels;
    public long StatsWriteComp16Pixels;
    public long StatsWriteComp8Pixels;
    public long StatsWriteRlx8Pixels;
    public long StatsWrite16Pixels;
    public long StatsWrite8Pixels;
    public long StatsFill16Pixels;
    public long StatsCopy16Pixels;
    public long StatsOtherPixels;
    public long StatsPackets;
    public long StatsCopyTotal;            /* total Copy8 + Copy16 commands */
    public long StatsCopySourceOverlapsComp; /* subset of above where source overlapped a WriteComp region this frame */

    /*
     * Compact raw binary tree from the wire table: length*2 nodes, 4 bytes each.
     * Max size: 4096*2*4 = 32KB. Build source for the expanded tables below; not
     * read in the decode hot path.
     */
    public DlDecoder.DecompEntry[]? CompactTree;

    /* Hot lookup tables: root-state rows only, pre-expanded 8 bits wide.
     * 256 entries × (2B lookup + 16B colors) = ~4.5 KB each — stays in L1.
     * Used when an input byte STARTS at the root state (the common case): 1 load/byte. */
    public ushort[]? HotLookup16;
    public ushort[]? HotColors16;
    public ushort[]? HotLookup8;
    public byte[]?   HotColors8;

    /* Non-root lookup tables: every reachable state pre-expanded NonRootBits wide
     * (see DlDecoder.NonRootBits). Indexed by (state << NonRootBits) | chunk.
     * Footprint = reachableStates × 2^NonRootBits × (2B lookup + NonRootBits colors),
     * sized so the touched set stays in L2 (k=2 fits any legal table; ~96KB worst case).
     * Consumes the input byte in 8/NonRootBits chunks → 8/NonRootBits dependent loads
     * instead of 8 (one per bit). */
    public ushort[]? NonRootLookup16;
    public ushort[]? NonRootColors16;
    public ushort[]? NonRootLookup8;
    public byte[]?   NonRootColors8;

    /* Count of reachable states (rows actually built) — diagnostic for tuning NonRootBits. */
    public int ReachableStates16;
    public int ReachableStates8;

    public event Action? RegistersUpdate;
    public long LastRegistersUpdateTimestamp;

    public void SetRegister(byte address, byte value)
    {
        ref byte registers = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_ram), RegisterMemoryOffset);
        if (address == (byte)DlRegisterAddress.RegistersUpdate)
        {
            Unsafe.Add(ref registers, (nint)DlRegisterAddress.RegistersUpdate) = value;
            if (value != 0)
            {
                ApplyRegisters(ref registers);
                RegistersUpdate?.Invoke();
                LastRegistersUpdateTimestamp = Stopwatch.GetTimestamp();
            }
        }
        else
        {
            Unsafe.Add(ref registers, address) = value;
            if (Unsafe.Add(ref registers, (nint)DlRegisterAddress.RegistersUpdate) != 0)
            {
                ApplyRegisters(ref registers);
            }
        }
    }

    private void ApplyRegisters(ref byte registers)
    {
        int horizontalResolution = ReadUInt16Be(ref Unsafe.Add(ref registers, (nint)DlRegisterAddress.HPixels));
        _horizontalResolution = horizontalResolution;
        _verticalResolution = ReadUInt16Be(ref Unsafe.Add(ref registers, (nint)DlRegisterAddress.VPixels));
        _fb16BaseOffset = ReadUInt24Be(ref Unsafe.Add(ref registers, (nint)DlRegisterAddress.BaseOffset16));
        int fb16LineStride = ReadUInt24Be(ref Unsafe.Add(ref registers, (nint)DlRegisterAddress.LineStride16));
        _fb16LineStride = fb16LineStride == 0 ? horizontalResolution * 2 : fb16LineStride;
        _fb8BaseOffset = ReadUInt24Be(ref Unsafe.Add(ref registers, (nint)DlRegisterAddress.BaseOffset8));
        int fb8LineStride = ReadUInt24Be(ref Unsafe.Add(ref registers, (nint)DlRegisterAddress.LineStride8));
        _fb8LineStride = fb8LineStride == 0 ? horizontalResolution : fb8LineStride;

        static int ReadUInt16Be(ref byte buf)
        {
            return BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, ushort>(ref buf));
        }

        static int ReadUInt24Be(ref byte buf)
        {
            return (int)(BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, uint>(ref buf)) >> 8);
        }
    }

    public FrameArea CopyFrameBufferTo(Span<uint> fb)
    {
        int lineStride = _fb16LineStride / 2;
        return CopyFrameBufferTo(fb, lineStride);
    }

    public FrameArea CopyFrameBufferTo(Span<uint> fb, int stridePixels)
    {
        // TODO: properly handle different line strides for 16 and 8 bits buffers
        int lineStride = _fb16LineStride / 2;
        if (lineStride <= 0 || stridePixels <= 0)
        {
            return default;
        }

        int width = _horizontalResolution;
        int height = _verticalResolution;
        int fb16Size = lineStride * height * 2;
        if (_fb16BaseOffset < 0 || (long)_fb16BaseOffset + fb16Size > _frameBuffer.Length)
        {
            return default;
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(fb.Length, checked(stridePixels * height));

        /*
         * Scan only the rows the decoder actually touched this pass. The per-row
         * dirty bitset (set by MarkDirty) localizes the work even when the dirty
         * rows are scattered (e.g. a cursor near the top and a caret near the
         * bottom) — the copy loops skip clean rows inside this window via the
         * bitset, so we convert/blit only the real changes, not the whole span.
         * Fall back to the full frame when no dirty rows were recorded (e.g. a
         * flush after a register-only packet that made no pixel writes), so the
         * initial frame and forced full syncs still paint.
         */
        bool haveDirtyRows = _dirtyRowMin <= _dirtyRowMax;
        int dirtyRowFirst = haveDirtyRows ? Math.Max(0, _dirtyRowMin) : 0;
        int dirtyRowLast = haveDirtyRows ? Math.Min(height, _dirtyRowMax + 1) : height;

        /* Snapshot the bitset before ResetDirtyRange() clears it. Copy loops use
         * this to skip clean rows; null means "every row in the window is dirty"
         * (the full-frame fallback). */
        byte[]? rowBits = haveDirtyRows ? _dirtyRowBits : null;

        /*
         * Consume the accumulated dirty range now that we have read it.  Pixels
         * decoded after this point belong to the next sync.  (A full-frame
         * fallback above already covers the empty-range / initial case.)
         */
        ResetDirtyRange();

        ReadOnlySpan<ushort> fb16 = MemoryMarshal.Cast<byte, ushort>(_frameBuffer.AsSpan(_fb16BaseOffset, fb16Size));
        int modifiedX1, modifiedY1, modifiedX2, modifiedY2;
        if (ColorDepth == RgbColorDepth.Rgb24Bits)
        {
            int fb8Size = lineStride * height;
            if (_fb8BaseOffset < 0 || (long)_fb8BaseOffset + fb8Size > _frameBuffer.Length)
            {
                return default;
            }
            (modifiedX1, modifiedY1, modifiedX2, modifiedY2) = CopyPixels24(
                fb16,
                _frameBufferDiff16,
                lineStride,
                _frameBuffer.AsSpan(_fb8BaseOffset, fb8Size),
                _frameBufferDiff8,
                _fb8LineStride,
                stridePixels,
                fb,
                dirtyRowFirst,
                dirtyRowLast,
                rowBits);
        }
        else
        {
            (modifiedX1, modifiedY1, modifiedX2, modifiedY2) = CopyPixels16(
                fb16,
                _frameBufferDiff16,
                lineStride,
                stridePixels,
                fb,
                dirtyRowFirst,
                dirtyRowLast,
                rowBits);
        }

        return new()
        {
            Width = (ushort)width,
            Height = (ushort)height,
            LineStride = (ushort)lineStride,
            ModifiedX1 = (ushort)modifiedX1,
            ModifiedY1 = (ushort)modifiedY1,
            ModifiedX2 = (ushort)modifiedX2,
            ModifiedY2 = (ushort)modifiedY2,
        };
    }

    public FrameArea CopyFrameBuffer16To(Span<uint> fb)
    {
        int lineStride = _fb16LineStride / 2;
        if (lineStride <= 0)
        {
            return default;
        }

        int width = _horizontalResolution;
        int height = _verticalResolution;
        int fb16Size = lineStride * height * 2;
        if (_fb16BaseOffset < 0 || (long)_fb16BaseOffset + fb16Size > _frameBuffer.Length)
        {
            return default;
        }
        var (modifiedX1, modifiedY1, modifiedX2, modifiedY2) = CopyPixels16(
            MemoryMarshal.Cast<byte, ushort>(_frameBuffer.AsSpan(_fb16BaseOffset, fb16Size)),
            _frameBufferDiff16,
            lineStride,
            lineStride,
            fb);

        return new()
        {
            Width = (ushort)width,
            Height = (ushort)height,
            LineStride = (ushort)lineStride,
            ModifiedX1 = (ushort)modifiedX1,
            ModifiedY1 = (ushort)modifiedY1,
            ModifiedX2 = (ushort)modifiedX2,
            ModifiedY2 = (ushort)modifiedY2,
        };
    }

    /*
     * Raw RGB565 copy used by the live scanout path (GPU does RGB565->RGB in the
     * shader, so no diff/convert here). This is the path that actually runs on
     * the box — NOT CopyFrameBufferTo, which serves the abandoned XRGB8888 path.
     *
     * Damage comes straight from the DisplayLink protocol: MarkDirty's per-row
     * bitset tells us exactly which rows the decoder wrote, so we copy only those
     * and report a tight ModifiedY1/Y2. No per-pixel diff — there's no diff buffer
     * to keep coherent on this path, and the protocol's row-granular damage is
     * enough to stop the full-frame WC blit. X stays full-width (rows dominate
     * the blit cost; tight columns would need a diff or 2-D protocol damage).
     */
    public FrameArea CopyFrameBuffer16To(Span<ushort> fb, int stridePixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stridePixels, 1);

        int lineStride = _fb16LineStride / 2;
        int width = _horizontalResolution;
        int height = _verticalResolution;
        if (lineStride <= 0 || width <= 0 || height <= 0)
        {
            return default;
        }

        int copyWidth = Math.Min(Math.Min(width, stridePixels), lineStride);
        ArgumentOutOfRangeException.ThrowIfLessThan(fb.Length, stridePixels * height);

        int fb16Size = lineStride * height * 2;
        if (_fb16BaseOffset < 0 || (long)_fb16BaseOffset + fb16Size > _frameBuffer.Length)
        {
            return default;
        }

        bool haveDirtyRows = _dirtyRowMin <= _dirtyRowMax;
        int rowFirst = haveDirtyRows ? Math.Max(0, _dirtyRowMin) : 0;
        int rowLast = haveDirtyRows ? Math.Min(height, _dirtyRowMax + 1) : height;
        byte[]? rowBits = haveDirtyRows ? _dirtyRowBits : null;

        /* TEMP DIAGNOSTIC: confirm this build is deployed and see the real dirty
         * range. Throttled to ~1/sec. Remove once avg_dirty_rows is confirmed. */
        if (Environment.GetEnvironmentVariable("ZEROKVM_DIRTY_DEBUG") is not null)
        {
            long nowTicks = Environment.TickCount64;
            if (nowTicks - _lastDirtyDebugMs >= 1000)
            {
                _lastDirtyDebugMs = nowTicks;
                Console.Error.WriteLine(
                    $"[dirty16] have={haveDirtyRows} rowMin={_dirtyRowMin} rowMax={_dirtyRowMax} " +
                    $"-> rows[{rowFirst},{rowLast}) h={height} " +
                    $"byteMin={_dirtyByteMin} byteMax={_dirtyByteMax} baseAtMark={_dbgBaseAtMark} markCalls={_markDirtyCalls} markRowSet={_markRowSetCalls} markMiss={_markMissCalls} " +
                    $"fb16Base={_fb16BaseOffset} fb16Stride={_fb16LineStride} " +
                    $"fb8Base={_fb8BaseOffset} fb8Stride={_fb8LineStride} depth={ColorDepth}");
                _markDirtyCalls = _markRowSetCalls = _markMissCalls = 0;
            }
        }

        ResetDirtyRange();

        ReadOnlySpan<ushort> source = MemoryMarshal.Cast<byte, ushort>(_frameBuffer.AsSpan(_fb16BaseOffset, fb16Size));
        int modifiedY1 = 0, modifiedY2 = 0;
        for (int y = rowFirst; y < rowLast; y++)
        {
            if (rowBits != null && (rowBits[y >> 3] & (byte)(1 << (y & 7))) == 0)
                continue;

            source.Slice(y * lineStride, copyWidth)
                .CopyTo(fb.Slice(y * stridePixels, copyWidth));

            if (modifiedY2 == 0) { modifiedY1 = y; modifiedY2 = y + 1; } else { modifiedY2 = y + 1; }
        }

        return new()
        {
            Width = (ushort)width,
            Height = (ushort)height,
            LineStride = (ushort)lineStride,
            ModifiedX1 = 0,
            ModifiedY1 = (ushort)modifiedY1,
            ModifiedX2 = (ushort)(modifiedY2 > 0 ? width : 0),
            ModifiedY2 = (ushort)modifiedY2,
        };
    }

    private static (int X1, int Y1, int X2, int Y2) CopyPixels16(
        ReadOnlySpan<ushort> source,
        Span<ushort> sourceDiff,
        int lineStride,
        int destinationStride,
        Span<uint> destination,
        int dirtyRowFirst = 0,
        int dirtyRowLast = int.MaxValue,
        byte[]? rowBits = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(source.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceDiff.Length, source.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, checked(destinationStride * (source.Length / lineStride)));
        ArgumentOutOfRangeException.ThrowIfLessThan(lineStride, 16);

        int x1 = ushort.MaxValue;
        int y1 = 0;
        int x2 = 0;
        int y2 = 0;
        ref ushort sourceRef = ref MemoryMarshal.GetReference(source);
        ref ushort sourceDiffRef = ref MemoryMarshal.GetReference(sourceDiff);
        ref uint destinationRef = ref MemoryMarshal.GetReference(destination);
        int length = source.Length;
        int totalRows = length / lineStride;
        int rowFirst = Math.Max(0, dirtyRowFirst);
        int rowLast  = Math.Min(totalRows, dirtyRowLast);
        int vectorLineLength = lineStride - (lineStride % Vector128<ushort>.Count);
        for (int y = rowFirst; y < rowLast; y++)
        {
            /*
             * Skip rows the decoder never wrote this pass. rowBits is the per-row
             * dirty bitset from MarkDirty; null means "diff every row in the
             * window" (full-frame fallback). This is what keeps a cursor-top +
             * caret-bottom update from converting the whole frame between them.
             */
            if (rowBits != null && (rowBits[y >> 3] & (byte)(1 << (y & 7))) == 0)
                continue;

            int i = y * lineStride;
            ref uint lineDestinationRef = ref Unsafe.Add(ref destinationRef, y * destinationStride);

            /*
             * Run the diffing copy even on known-dirty rows: it gives tight X
             * bounds (the decoder confirmed the ROW changed, not which columns)
             * and keeps _frameBufferDiff16 consistent so later diff passes (and
             * the 24-bit path) localize correctly instead of seeing stale data.
             */
            (int lineX1, int lineX2) = CopyLine16(
                ref Unsafe.Add(ref sourceRef, i),
                ref Unsafe.Add(ref sourceDiffRef, i),
                ref lineDestinationRef,
                lineStride,
                vectorLineLength);

            if (lineX1 >= 0)
            {
                if (lineX1 < x1) x1 = lineX1;
                if (lineX2 > x2) x2 = lineX2;
                if (y2 == 0) { y1 = y; y2 = y + 1; } else { y2 = y + 1; }
            }
        }

        return (x1, y1, x2, y2);
    }

    private static (int X1, int X2) CopyLine16(ref ushort source, ref ushort sourceDiff, ref uint destination, int lineLength, int vectorLineLength)
    {
        scoped ref ushort diffStart = ref Unsafe.NullRef<ushort>();
        scoped ref ushort diffEnd = ref Unsafe.NullRef<ushort>();
        scoped ref ushort sourceStart = ref source;
        scoped ref ushort sourceVectorEnd = ref Unsafe.Add(ref source, vectorLineLength);

        Vector128<ushort> sourcePixels;

    diffLoop:
        do
        {
            (ulong, ulong) sourcePixels2 = Unsafe.As<ushort, (ulong, ulong)>(ref source);
            source = ref Unsafe.Add(ref source, Vector128<ushort>.Count);
            (ulong, ulong) sourceDiffPixels = Unsafe.As<ushort, (ulong, ulong)>(ref sourceDiff);
            sourceDiff = ref Unsafe.Add(ref sourceDiff, Vector128<ushort>.Count);

            if (sourcePixels2 != sourceDiffPixels)
            {
                source = ref Unsafe.Subtract(ref source, Vector128<ushort>.Count);
                sourceDiff = ref Unsafe.Subtract(ref sourceDiff, Vector128<ushort>.Count);
                sourcePixels = Vector128.Create(sourcePixels2.Item1, sourcePixels2.Item2).AsUInt16();
                goto copyLoop;
            }
        }
        while (Unsafe.IsAddressLessThan(ref source, ref sourceVectorEnd));

        goto remaining;

    copyLoop:
        if (Unsafe.IsNullRef(ref diffStart))
        {
            diffStart = ref source;
        }

        scoped ref Vector256<uint> copyDestination = ref Unsafe.As<uint, Vector256<uint>>(ref Unsafe.AddByteOffset(ref destination, Unsafe.ByteOffset(ref sourceStart, ref source) * 2));
        do
        {
            Unsafe.As<ushort, Vector128<ushort>>(ref sourceDiff) = sourcePixels;
            sourceDiff = ref Unsafe.Add(ref sourceDiff, Vector128<ushort>.Count);
            ColorConvert.Rgb565LeToRgbx(sourcePixels, ref copyDestination);
            copyDestination = ref Unsafe.Add(ref copyDestination, 1);
            source = ref Unsafe.Add(ref source, Vector128<ushort>.Count);
            if (!Unsafe.IsAddressLessThan(ref source, ref sourceVectorEnd))
            {
                diffEnd = ref source;
                goto remaining;
            }

            sourcePixels = Unsafe.As<ushort, Vector128<ushort>>(ref source);
        }
        while (sourcePixels != Unsafe.As<ushort, Vector128<ushort>>(ref sourceDiff));

        diffEnd = ref source;
        goto diffLoop;

    remaining:
        if (lineLength != vectorLineLength)
        {
            nuint offset = (nuint)Vector128<ushort>.Count - (nuint)(lineLength - vectorLineLength);
            sourcePixels = Unsafe.As<ushort, Vector128<ushort>>(ref Unsafe.Subtract(ref source, offset));
            if (sourcePixels != Unsafe.As<ushort, Vector128<ushort>>(ref Unsafe.Subtract(ref sourceDiff, offset)))
            {
                Unsafe.As<ushort, Vector128<ushort>>(ref Unsafe.Subtract(ref sourceDiff, offset)) = sourcePixels;
                ColorConvert.Rgb565LeToRgbx(sourcePixels, ref Unsafe.As<uint, Vector256<uint>>(ref Unsafe.Add(ref destination, lineLength - Vector256<uint>.Count)));
                return (
                    Unsafe.IsNullRef(ref diffStart) ? 0 : (int)Unsafe.ByteOffset(ref sourceStart, ref diffStart) / sizeof(ushort),
                    lineLength
                );
            }
        }

        return Unsafe.IsNullRef(ref diffStart) ?
            (-1, -1) :
            (
                (int)Unsafe.ByteOffset(ref sourceStart, ref diffStart) / sizeof(ushort),
                (int)Unsafe.ByteOffset(ref sourceStart, ref diffEnd) / sizeof(ushort)
            );
    }

    private static (int X1, int Y1, int X2, int Y2) CopyPixels24(
        ReadOnlySpan<ushort> source16,
        Span<ushort> sourceDiff16,
        int lineStride16,
        ReadOnlySpan<byte> source8,
        Span<byte> sourceDiff8,
        int lineStride8,
        int destinationStride,
        Span<uint> destination,
        int dirtyRowFirst = 0,
        int dirtyRowLast = int.MaxValue,
        byte[]? rowBits = null)
    {
        // Combine the 16-bit plane (RGB565, providing MSBs of each channel) with the
        // 8-bit plane (RGB323, providing LSBs).  The per-channel merge is:
        //   R8 = R565<<3 | R323   (5+3 = 8 bits)
        //   G8 = G565<<2 | G323   (6+2 = 8 bits)
        //   B8 = B565<<3 | B323   (5+3 = 8 bits)
        // Output format is XBGR8888 (matching Rgb565LeToRgbx): B at bits 23:16,
        // G at bits 15:8, R at bits 7:0.
        int height = source16.Length / lineStride16;
        int x1 = ushort.MaxValue;
        int y1 = 0;
        int x2 = 0;
        int y2 = 0;
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, checked(destinationStride * height));

        int rowFirst = Math.Max(0, dirtyRowFirst);
        int rowLast  = Math.Min(height, dirtyRowLast);

        ref ushort src16Ref = ref MemoryMarshal.GetReference(source16);
        ref ushort diff16Ref = ref MemoryMarshal.GetReference(sourceDiff16);
        ref byte src8Ref = ref MemoryMarshal.GetReference(source8);
        ref byte diff8Ref = ref MemoryMarshal.GetReference(sourceDiff8);
        ref uint dstRef = ref MemoryMarshal.GetReference(destination);
        bool useNeon = AdvSimd.Arm64.IsSupported;

        for (int y = rowFirst; y < rowLast; y++)
        {
            /*
             * Skip rows the decoder never wrote this pass (see CopyPixels16).
             * Symmetric with the 16-bit path: the bitset localizes ROWS, the
             * per-pixel diff below localizes COLUMNS (tight lineX1/lineX2) and
             * keeps the diff buffer coherent. Do not add a no-diff "row is fully
             * dirty" fast path on one plane only — that desyncs the diff buffer
             * and reports full-width damage, which is the bug this replaced.
             */
            if (rowBits != null && (rowBits[y >> 3] & (byte)(1 << (y & 7))) == 0)
                continue;

            int base16 = y * lineStride16;
            int base8 = y * lineStride8;
            int baseDst = y * destinationStride;
            int lineX1 = -1;
            int lineX2 = -1;
            int x = 0;

            if (useNeon)
            {
                for (; x <= lineStride16 - 8; x += 8)
                {
                    ref ushort src16 = ref Unsafe.Add(ref src16Ref, base16 + x);
                    ref ushort diff16 = ref Unsafe.Add(ref diff16Ref, base16 + x);
                    ref byte src8 = ref Unsafe.Add(ref src8Ref, base8 + x);
                    ref byte diff8 = ref Unsafe.Add(ref diff8Ref, base8 + x);

                    Vector128<ushort> v16 = Unsafe.As<ushort, Vector128<ushort>>(ref src16);
                    Vector128<ushort> d16 = Unsafe.As<ushort, Vector128<ushort>>(ref diff16);
                    Vector64<byte> v8 = Unsafe.As<byte, Vector64<byte>>(ref src8);
                    Vector64<byte> d8 = Unsafe.As<byte, Vector64<byte>>(ref diff8);

                    if (Vector128.EqualsAll(v16, d16) &&
                        v8.AsUInt64().ToScalar() == d8.AsUInt64().ToScalar())
                    {
                        continue;
                    }

                    Unsafe.As<ushort, Vector128<ushort>>(ref diff16) = v16;
                    Unsafe.As<byte, Vector64<byte>>(ref diff8) = v8;

                    Vector128<ushort> px8_u16 = AdvSimd.ZeroExtendWideningLower(v8);
                    Vector128<uint> px8_u32_lo = AdvSimd.ZeroExtendWideningLower(px8_u16.GetLower());
                    Vector128<uint> px8_u32_hi = AdvSimd.ZeroExtendWideningLower(px8_u16.GetUpper());

                    // px8 bit layout: r2r1r0 g1g0 b2b1b0 → fill lower bits of each 8-bit channel
                    Vector128<uint> contrib_lo =
                        ((px8_u32_lo >> 5) & Vector128.Create(0x07u)) |
                        (((px8_u32_lo >> 3) & Vector128.Create(0x03u)) << 8) |
                        ((px8_u32_lo & Vector128.Create(0x07u)) << 16);
                    Vector128<uint> contrib_hi =
                        ((px8_u32_hi >> 5) & Vector128.Create(0x07u)) |
                        (((px8_u32_hi >> 3) & Vector128.Create(0x03u)) << 8) |
                        ((px8_u32_hi & Vector128.Create(0x07u)) << 16);

                    Vector256<uint> rgbx = default;
                    ColorConvert.Rgb565LeToRgbx(v16, ref rgbx);
                    Vector128<uint> rgbxLo = Unsafe.As<Vector256<uint>, Vector128<uint>>(ref rgbx);
                    Vector128<uint> rgbxHi = Unsafe.Add(ref Unsafe.As<Vector256<uint>, Vector128<uint>>(ref rgbx), 1);

                    Unsafe.As<uint, Vector128<uint>>(ref Unsafe.Add(ref dstRef, baseDst + x)) = rgbxLo | contrib_lo;
                    Unsafe.As<uint, Vector128<uint>>(ref Unsafe.Add(ref dstRef, baseDst + x + 4)) = rgbxHi | contrib_hi;

                    if (lineX1 < 0) lineX1 = x;
                    lineX2 = x + 8;
                }
            }

            // Scalar path for tail (and full loop on non-NEON)
            for (; x < lineStride16; x++)
            {
                ushort px16 = Unsafe.Add(ref src16Ref, base16 + x);
                byte px8 = Unsafe.Add(ref src8Ref, base8 + x);

                if (px16 != Unsafe.Add(ref diff16Ref, base16 + x) ||
                    px8 != Unsafe.Add(ref diff8Ref, base8 + x))
                {
                    Unsafe.Add(ref diff16Ref, base16 + x) = px16;
                    Unsafe.Add(ref diff8Ref, base8 + x) = px8;

                    uint r8 = (((uint)px16 >> 8) & 0xF8u) | ((uint)px8 >> 5);
                    uint g8 = (((uint)px16 >> 3) & 0xFCu) | (((uint)px8 >> 3) & 0x03u);
                    uint b8 = (((uint)px16 << 3) & 0xF8u) | ((uint)px8 & 0x07u);
                    Unsafe.Add(ref dstRef, baseDst + x) = (b8 << 16) | (g8 << 8) | r8;

                    if (lineX1 < 0) lineX1 = x;
                    lineX2 = x + 1;
                }
            }

            if (lineX1 >= 0)
            {
                if (lineX1 < x1) x1 = lineX1;
                if (lineX2 > x2) x2 = lineX2;
                if (y2 == 0)
                {
                    y1 = y;
                    y2 = y + 1;
                }
                else
                {
                    y2 = y + 1;
                }
            }
        }

        return (x1, y1, x2, y2);
    }

    public readonly struct DecompLookupEntry
    {
        public DecompLookupEntry(ushort colorCount, ushort jump)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(colorCount, (1 << 4) - 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(jump, (1 << 12) - 1);
            _value = (ushort)(colorCount | ((uint)jump << 4));
        }

        private readonly ushort _value;

        public uint ColorCount => _value & 0xfU;
        public uint Jump => (uint)_value >> 4;
        internal ushort RawValue => _value;

        public bool IsSet => _value != 0;
    }
}
