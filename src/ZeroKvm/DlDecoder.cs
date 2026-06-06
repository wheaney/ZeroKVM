using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ZeroKvm;

internal static class DlDecoder
{
    private const byte CommandHeader = 0xaf;

    /*
     * Width (in bits) of the non-root pre-expanded decode tables. The input byte is
     * consumed in 8/NonRootBits chunks, so this is the number of serial dependent
     * loads per non-root byte (vs 8 for a bit-by-bit walk). Must divide 8 (2 or 4).
     * Footprint per reachable state = 2^NonRootBits * (2B + NonRootBits*colorSize).
     * k=4: comp16 has ~286 reachable states → ~27KB non-root table, fits A72 L2.
     *      comp8 has ~9 reachable states → trivially small.
     * k=2 is the safe fallback if a wildly different host sends far more states.
     */
    private const int NonRootBits = 4;

    private enum Commands : ushort
    {
        SetRegister = CommandHeader | (0x20 << 8),
        Write8 = CommandHeader | (0x60 << 8),
        Write16 = CommandHeader | (0x68 << 8),
        Fill8 = CommandHeader | (0x61 << 8),
        Fill16 = CommandHeader | (0x69 << 8),
        Copy8 = CommandHeader | (0x62 << 8),
        Copy16 = CommandHeader | (0x6a << 8),
        WriteRlx8 = CommandHeader | (0x63 << 8),
        WriteRlx16 = CommandHeader | (0x6b << 8),
        WriteComp8 = CommandHeader | (0x70 << 8),
        WriteComp16 = CommandHeader | (0x78 << 8),
        FlushPipe = CommandHeader | (0xa0 << 8),
        LoadDecompTable = CommandHeader | (0xe0 << 8),
        NoOp = CommandHeader | (CommandHeader << 8),
        TrailingZero = CommandHeader << 8,
        TrailingDoubleZero = 0,
    }

    public static int Process(ReadOnlySpan<byte> commandStream, DlMemory memory)
    {
        ref byte streamStart = ref Unsafe.AsRef(in commandStream[0]);
        ref byte stream = ref streamStart;
        ref byte streamEnd = ref Unsafe.Add(ref stream, commandStream.Length);
        ref byte fb = ref MemoryMarshal.GetReference(memory.FrameBuffer);
        ref ushort hotLookup8 = ref Unsafe.NullRef<ushort>();
        ref byte hotColors8 = ref Unsafe.NullRef<byte>();
        ref ushort hotLookup16 = ref Unsafe.NullRef<ushort>();
        ref ushort hotColors16 = ref Unsafe.NullRef<ushort>();
        ref ushort nonRootLookup8 = ref Unsafe.NullRef<ushort>();
        ref byte nonRootColors8 = ref Unsafe.NullRef<byte>();
        ref ushort nonRootLookup16 = ref Unsafe.NullRef<ushort>();
        ref ushort nonRootColors16 = ref Unsafe.NullRef<ushort>();

        /*
         * Note: the dirty range is NOT reset here.  It accumulates across every
         * Process() call and is consumed+reset by CopyFrameBufferTo() at sync
         * time.  This lets the sink throttle the expensive full-frame convert
         * (syncing once per display interval rather than once per packet) without
         * losing the rows touched by packets decoded between syncs.
         */
        memory.StatsPackets++;

        try
        {
            while (Unsafe.ByteOffset(ref stream, ref streamEnd) >= 2)
            {
                int commandLength;
                ushort header = Unsafe.As<byte, ushort>(ref stream);
                ref byte commandStart = ref Unsafe.Add(ref stream, 2);
                switch ((Commands)header)
                {
                    case Commands.WriteRlx8:
                    {
                        uint hdr = Unsafe.As<byte, uint>(ref commandStart);
                        commandLength = WriteRlx8(ref commandStart, ref streamEnd, ref fb);
                        if (commandLength > 0) {
                            int px = (int)Wrap256(hdr >> 24);
                            memory.MarkDirty(UInt24BeLsbToInt32(hdr), px);
                            memory.StatsWriteRlx8Pixels += px;
                        }
                        break;
                    }

                    case Commands.WriteRlx16:
                    {
                        uint hdr = Unsafe.As<byte, uint>(ref commandStart);
                        commandLength = WriteRlx16(ref commandStart, ref streamEnd, ref fb);
                        if (commandLength > 0) {
                            int px = (int)Wrap256(hdr >> 24);
                            memory.MarkDirty(UInt24BeLsbToInt32(hdr), px * sizeof(ushort));
                            memory.StatsWriteRlx16Pixels += px;
                        }
                        break;
                    }

                    case Commands.WriteComp8:
                    {
                        if (Unsafe.IsNullRef(ref hotLookup8))
                        {
                            hotLookup8 = ref GetArrayRef(memory.HotLookup8);
                            hotColors8 = ref GetArrayRef(memory.HotColors8);
                            nonRootLookup8 = ref GetArrayRef(memory.NonRootLookup8);
                            nonRootColors8 = ref GetArrayRef(memory.NonRootColors8);
                        }

                        uint hdr = Unsafe.As<byte, uint>(ref commandStart);
                        commandLength = WriteComp8(ref commandStart, ref streamEnd, ref fb, in hotLookup8, in hotColors8, in nonRootLookup8, in nonRootColors8, memory);
                        if (commandLength > 0) {
                            int px = (int)Wrap256(hdr >> 24);
                            int addr = UInt24BeLsbToInt32(hdr);
                            memory.MarkDirty(addr, px);
                            memory.MarkCompWritten(addr, px);
                            memory.StatsWriteComp8Pixels += px;
                        }
                        break;
                    }

                    case Commands.WriteComp16:
                    {
                        if (Unsafe.IsNullRef(ref hotLookup16))
                        {
                            hotLookup16 = ref GetArrayRef(memory.HotLookup16);
                            hotColors16 = ref GetArrayRef(memory.HotColors16);
                            nonRootLookup16 = ref GetArrayRef(memory.NonRootLookup16);
                            nonRootColors16 = ref GetArrayRef(memory.NonRootColors16);
                        }

                        uint hdr = Unsafe.As<byte, uint>(ref commandStart);
                        commandLength = WriteComp16(ref commandStart, ref streamEnd, ref fb, in hotLookup16, in hotColors16, in nonRootLookup16, in nonRootColors16, memory);
                        if (commandLength > 0) {
                            int px = (int)Wrap256(hdr >> 24);
                            int addr = UInt24BeLsbToInt32(hdr);
                            memory.MarkDirty(addr, px * sizeof(ushort));
                            memory.MarkCompWritten(addr, px * sizeof(ushort));
                            memory.StatsWriteComp16Pixels += px;
                        }
                        break;
                    }

                    case Commands.FlushPipe:
                    case Commands.TrailingDoubleZero:
                        stream = ref Unsafe.Add(ref stream, 2);
                        continue;

                    case Commands.NoOp:
                    case Commands.TrailingZero:
                        stream = ref Unsafe.Add(ref stream, 1);
                        continue;

                    default:
                        commandLength = ProcessOther(header, ref streamStart, ref commandStart, ref streamEnd, memory);
                        if (commandLength < 0)
                        {
                            return commandStream.Length;
                        }

                        break;
                }

                if (commandLength == 0)
                {
                    return (int)Unsafe.ByteOffset(ref streamStart, ref stream);
                }
                else
                {
                    stream = ref Unsafe.Add(ref commandStart, commandLength);
                }
            }
        }
        catch
        {
            PrintCommandError("Error in command", ref streamStart, ref stream, ref streamEnd);
            throw;
        }

        return (int)Unsafe.ByteOffset(ref streamStart, ref stream);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int ProcessOther(ushort header, ref byte streamStart, ref byte commandStart, ref byte streamEnd, DlMemory memory)
        {
            ref byte fb = ref MemoryMarshal.GetReference(memory.FrameBuffer);
            switch ((Commands)header)
            {
                case Commands.SetRegister:
                    return SetRegister(ref commandStart, ref streamEnd, memory);

                case Commands.Write8:
                {
                    uint hdr = Unsafe.As<byte, uint>(ref commandStart);
                    int n = Write8(ref commandStart, ref streamEnd, ref fb);
                    if (n > 0) { int px = (int)Wrap256(hdr >> 24); memory.MarkDirty(UInt24BeLsbToInt32(hdr), px); memory.StatsWrite8Pixels += px; }
                    return n;
                }

                case Commands.Write16:
                {
                    uint hdr = Unsafe.As<byte, uint>(ref commandStart);
                    int n = Write16(ref commandStart, ref streamEnd, ref fb);
                    if (n > 0) { int px = (int)Wrap256(hdr >> 24); memory.MarkDirty(UInt24BeLsbToInt32(hdr), px * sizeof(ushort)); memory.StatsWrite16Pixels += px; }
                    return n;
                }

                case Commands.Fill8:
                {
                    uint hdr = Unsafe.As<byte, uint>(ref commandStart);
                    int n = Fill8(ref commandStart, ref streamEnd, ref fb);
                    if (n > 0) { int px = (int)Wrap256(hdr >> 24); memory.MarkDirty(UInt24BeLsbToInt32(hdr), px); memory.StatsOtherPixels += px; }
                    return n;
                }

                case Commands.Fill16:
                {
                    uint hdr = Unsafe.As<byte, uint>(ref commandStart);
                    int n = Fill16(ref commandStart, ref streamEnd, ref fb);
                    if (n > 0) { int px = (int)Wrap256(hdr >> 24); memory.MarkDirty(UInt24BeLsbToInt32(hdr), px * sizeof(ushort)); memory.StatsFill16Pixels += px; }
                    return n;
                }

                case Commands.Copy8:
                {
                    uint hdr = Unsafe.As<byte, uint>(ref commandStart);
                    int n = Copy8(ref commandStart, ref streamEnd, ref fb);
                    if (n > 0) {
                        int px = (int)Wrap256(hdr >> 24);
                        int srcAddr = UInt24BeLsbToInt32(Unsafe.As<byte, uint>(ref Unsafe.Add(ref commandStart, 4)));
                        memory.StatsCopyTotal++;
                        if (memory.CheckCopySourceOverlapsComp(srcAddr, px)) memory.StatsCopySourceOverlapsComp++;
                        memory.MarkDirty(UInt24BeLsbToInt32(hdr), px);
                        memory.StatsOtherPixels += px;
                    }
                    return n;
                }

                case Commands.Copy16:
                {
                    uint hdr = Unsafe.As<byte, uint>(ref commandStart);
                    int n = Copy16(ref commandStart, ref streamEnd, ref fb);
                    if (n > 0) {
                        int px = (int)Wrap256(hdr >> 24);
                        int srcAddr = UInt24BeLsbToInt32(Unsafe.As<byte, uint>(ref Unsafe.Add(ref commandStart, 4)));
                        memory.StatsCopyTotal++;
                        if (memory.CheckCopySourceOverlapsComp(srcAddr, px * sizeof(ushort))) memory.StatsCopySourceOverlapsComp++;
                        memory.MarkDirty(UInt24BeLsbToInt32(hdr), px * sizeof(ushort));
                        memory.StatsCopy16Pixels += px;
                    }
                    return n;
                }

                case Commands.LoadDecompTable:
                    return LoadDecompTable(ref commandStart, ref streamEnd, memory);

                default:
                    PrintCommandError("Unknown command header", ref streamStart, ref Unsafe.Subtract(ref commandStart, 2), ref streamEnd);
                    return -1;
            }
        }

        static void PrintCommandError(string message, ref byte streamStart, ref byte stream, ref byte streamEnd)
        {
            ReadOnlySpan<byte> before = MemoryMarshal.CreateReadOnlySpan(in streamStart, (int)Unsafe.ByteOffset(ref streamStart, ref stream));
            ReadOnlySpan<byte> data = MemoryMarshal.CreateReadOnlySpan(in stream, (int)Unsafe.ByteOffset(ref stream, ref streamEnd));
            ushort header = Unsafe.As<byte, ushort>(ref stream);
            Logger.LogError($"""
                {message}: {header & 0xff:x2} {header >> 8:x2}
                Previous data: {Convert.ToHexStringLower(before.Length > 1024 ? before[^1024..] : before)}
                Data: {Convert.ToHexStringLower(data.Length > 1024 ? data[0..1024] : data)}
                """);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static ref T GetArrayRef<T>(T[]? array)
        {
            return ref array![0];
        }
    }

    /*
    address: uint8
    value: uint8
    */
    private static int SetRegister(ref byte stream, ref byte streamEnd, DlMemory memory)
    {
        if (Unsafe.ByteOffset(ref stream, ref streamEnd) < 2)
        {
            return 0;
        }

        ref byte streamStart = ref stream;
        ushort word = Unsafe.As<byte, ushort>(ref stream);
        stream = ref Unsafe.Add(ref stream, 2);
        memory.SetRegister((byte)word, (byte)(word >> 8));

        ref byte streamDwordEnd = ref Unsafe.Subtract(ref streamEnd, Unsafe.ByteOffset(ref stream, ref streamEnd) % 4);
        while (Unsafe.IsAddressLessThan(ref stream, ref streamDwordEnd))
        {
            uint dword = Unsafe.As<byte, uint>(ref stream);
            stream = ref Unsafe.Add(ref stream, 4);
            if ((dword & 0xffffUL) != (uint)Commands.SetRegister)
            {
                stream = ref Unsafe.Subtract(ref stream, 4);
                break;
            }

            memory.SetRegister((byte)(dword >> 16), (byte)(dword >> 24));
        }

        return (int)Unsafe.ByteOffset(ref streamStart, ref stream);
    }

    /*
    target_offset: uint24_be
    pixel_count: uint8_wrap256
    {
        pixel: uint8_rgb323
    } repeat until pixel_count is rendered
    */
    private static int Write8(ref byte stream, ref byte streamEnd, ref byte fb)
    {
        if (Unsafe.ByteOffset(ref stream, ref streamEnd) < 5)
        {
            return 0;
        }

        uint dword = Unsafe.As<byte, uint>(ref stream);
        stream = ref Unsafe.Add(ref stream, 4);

        int address = UInt24BeLsbToInt32(dword);
        int pixelCount = Wrap256((int)(dword >> 24));

        if (Unsafe.ByteOffset(ref stream, ref streamEnd) < pixelCount)
        {
            return 0;
        }

        ref byte fbDst = ref Unsafe.Add(ref fb, address);
        if (!MemoryMarshal.CreateReadOnlySpan(in stream, pixelCount)
                .SequenceEqual(MemoryMarshal.CreateReadOnlySpan(in fbDst, pixelCount)))
        {
            MemoryMarshal.CreateReadOnlySpan(in stream, pixelCount)
                .CopyTo(MemoryMarshal.CreateSpan(ref fbDst, pixelCount));
        }

        return 4 + pixelCount;
    }

    /*
    target_offset: uint24_be
    pixel_count: uint8_wrap256
    {
        pixel: uint16_rgb565
    } repeat pixel_count times
    */
    private static int Write16(ref byte stream, ref byte streamEnd, ref byte fb)
    {
        if (Unsafe.ByteOffset(ref stream, ref streamEnd) < 5)
        {
            return 0;
        }

        uint dword = Unsafe.As<byte, uint>(ref stream);
        stream = ref Unsafe.Add(ref stream, 4);

        int address = UInt24BeLsbToInt32(dword);
        int pixelCount = Wrap256((int)(dword >> 24));

        if (Unsafe.ByteOffset(ref stream, ref streamEnd) < pixelCount * sizeof(ushort))
        {
            return 0;
        }

        ref ushort fbPixels16 = ref Unsafe.As<byte, ushort>(ref Unsafe.Add(ref fb, address));
        ref ushort streamPixels16 = ref Unsafe.As<byte, ushort>(ref stream);
        if (!ColorConvert.SpanMatchesBe(ref streamPixels16, ref fbPixels16, pixelCount))
        {
            ColorConvert.CopyRgb565BeToRgb565Le(
                ref Unsafe.As<byte, ushort>(ref stream),
                ref Unsafe.As<byte, ushort>(ref Unsafe.Add(ref fb, address)),
                pixelCount);
        }

        return 4 + (pixelCount * sizeof(ushort));
    }

    /*
    target_offset: uint24_be
    total_pixel_count: uint8_wrap256
    {
        pixel_count: uint8_wrap256
        pixel: uint8_rgb323
    } repeat until total_pixel_count is rendered
    */
    private static int Fill8(ref byte stream, ref byte streamEnd, ref byte fb)
    {
        if (Unsafe.ByteOffset(ref stream, ref streamEnd) < 6)
        {
            return 0;
        }

        ref byte streamStart = ref stream;
        uint dword = Unsafe.As<byte, uint>(ref stream);
        stream = ref Unsafe.Add(ref stream, 4);

        int address = UInt24BeLsbToInt32(dword);
        int totalCount = Wrap256((int)(dword >> 24));
        ref byte fbPixels = ref Unsafe.Add(ref fb, address);
        do
        {
            if (Unsafe.ByteOffset(ref stream, ref streamEnd) < 2)
            {
                return 0;
            }

            uint word = Unsafe.As<byte, ushort>(ref stream);
            stream = ref Unsafe.Add(ref stream, 2);
            int pixelCount = Wrap256((byte)word);
            byte fillByte = (byte)(word >> 8);
            if (!ColorConvert.SpanAllEqual(ref fbPixels, pixelCount, fillByte))
            {
                MemoryMarshal.CreateSpan(ref fbPixels, pixelCount).Fill(fillByte);
            }

            fbPixels = ref Unsafe.Add(ref fbPixels, pixelCount);
            totalCount -= pixelCount;
        }
        while (totalCount > 0);

        return (int)Unsafe.ByteOffset(ref streamStart, ref stream);
    }

    /*
    target_offset: uint24_be
    total_pixel_count: uint8_wrap256
    {
        pixel_count: uint8_wrap256
        pixel: uint16_rgb565be
    } repeat until total_pixel_count is rendered
    */
    private static int Fill16(ref byte stream, ref byte streamEnd, ref byte fb)
    {
        if (Unsafe.ByteOffset(ref stream, ref streamEnd) < 7)
        {
            return 0;
        }

        ref byte streamStart = ref stream;
        uint dword = Unsafe.As<byte, uint>(ref stream);
        stream = ref Unsafe.Add(ref stream, 4);

        int address = UInt24BeLsbToInt32(dword);
        int totalCount = Wrap256((int)(dword >> 24));
        ref ushort fbPixels = ref Unsafe.As<byte, ushort>(ref Unsafe.Add(ref fb, address));
        do
        {
            if (Unsafe.ByteOffset(ref stream, ref streamEnd) < 3)
            {
                return 0;
            }

            int pixelCount = Wrap256(stream);
            stream = ref Unsafe.Add(ref stream, 1);
            ushort pixelValue = Unsafe.As<byte, ushort>(ref stream);
            stream = ref Unsafe.Add(ref stream, 2);
            if (!ColorConvert.SpanAllEqual(ref fbPixels, pixelCount, pixelValue))
            {
                MemoryMarshal.CreateSpan(ref fbPixels, pixelCount).Fill(pixelValue);
            }

            fbPixels = ref Unsafe.Add(ref fbPixels, pixelCount);
            totalCount -= pixelCount;
        }
        while (totalCount > 0);

        return (int)Unsafe.ByteOffset(ref streamStart, ref stream);
    }

    /*
    target_offset: uint24_be
    pixel_count: uint8_wrap256
    source_offset: uint24_be
    */
    private static int Copy8(ref byte stream, ref byte streamEnd, ref byte fb)
    {
        if (Unsafe.ByteOffset(ref stream, ref streamEnd) < 7)
        {
            return 0;
        }

        ulong qword = BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, ulong>(ref Unsafe.Subtract(ref stream, 1)));
        int targetAddress = (int)((qword >> 32) & 0xffffffU);
        int count = Wrap256((int)((qword >> 24) & 0xffU));
        int sourceAddress = (int)(qword & 0xffffffUL);
        if (targetAddress != sourceAddress)
        {
            MemoryMarshal.CreateSpan(ref Unsafe.Add(ref fb, sourceAddress), count)
                .TryCopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref fb, targetAddress), count));
        }

        return 7;
    }

    /*
    target_offset: uint24_be
    pixel_count: uint8_wrap256
    source_offset: uint24_be
    */
    private static int Copy16(ref byte stream, ref byte streamEnd, ref byte fb)
    {
        if (Unsafe.ByteOffset(ref stream, ref streamEnd) < 7)
        {
            return 0;
        }

        ulong qword = BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, ulong>(ref Unsafe.Subtract(ref stream, 1)));
        int targetAddress = (int)((qword >> 32) & 0xffffffU);
        int count = Wrap256((int)((qword >> 24) & 0xffU));
        int sourceAddress = (int)(qword & 0xffffffUL);
        if (targetAddress != sourceAddress)
        {
            count *= sizeof(ushort);
            MemoryMarshal.CreateSpan(ref Unsafe.Add(ref fb, sourceAddress), count)
                .TryCopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref fb, targetAddress), count));
        }

        return 7;
    }

    /*
    offset: uint24_be
    total_pixel_count: uint8_wrap256
    {
        pixel_count: uint8_wrap256
        {
            pixel: uint8_rgb323
        } * pixel_count
        last_pixel_repeat_count: uint8 (omitted for the last chunk if all pixels are rendered)
    } repeat until total_pixel_count is rendered
    */
    private static int WriteRlx8(ref byte stream, ref byte streamEnd, ref byte fb)
    {
        if (Unsafe.ByteOffset(ref stream, ref streamEnd) < 6)
        {
            return 0;
        }

        ref byte streamStart = ref stream;
        uint header = Unsafe.As<byte, uint>(ref stream);
        stream = ref Unsafe.Add(ref stream, 4);

        int address = UInt24BeLsbToInt32(header);
        int totalPixelCount = Wrap256((int)(header >> 24));
        ref byte fbPixels = ref Unsafe.Add(ref fb, address);
        do
        {
            if (Unsafe.ByteOffset(ref stream, ref streamEnd) < 2)
            {
                return 0;
            }

            int pixelCount = stream;
            stream = ref Unsafe.Add(ref stream, 1);
            pixelCount = Wrap256(pixelCount);
            if (Unsafe.ByteOffset(ref stream, ref streamEnd) < pixelCount)
            {
                return 0;
            }

            if (!MemoryMarshal.CreateReadOnlySpan(in stream, pixelCount)
                    .SequenceEqual(MemoryMarshal.CreateReadOnlySpan(in fbPixels, pixelCount)))
            {
                MemoryMarshal.CreateReadOnlySpan(in stream, pixelCount)
                    .CopyTo(MemoryMarshal.CreateSpan(ref fbPixels, pixelCount));
            }

            stream = ref Unsafe.Add(ref stream, pixelCount);
            fbPixels = ref Unsafe.Add(ref fbPixels, pixelCount);

            totalPixelCount -= pixelCount;
            if (totalPixelCount > 0)
            {
                if (!Unsafe.IsAddressLessThan(ref stream, ref streamEnd))
                {
                    return 0;
                }

                byte repeat = stream;
                stream = ref Unsafe.Add(ref stream, 1);
                if (repeat > 0)
                {
                    byte repeatPixel = Unsafe.Add(ref stream, -2);
                    if (!ColorConvert.SpanAllEqual(ref fbPixels, repeat, repeatPixel))
                    {
                        MemoryMarshal.CreateSpan(ref fbPixels, repeat).Fill(repeatPixel);
                    }

                    fbPixels = ref Unsafe.Add(ref fbPixels, repeat);
                    totalPixelCount -= repeat;
                }
            }
        }
        while (totalPixelCount > 0);

        return (int)Unsafe.ByteOffset(ref streamStart, ref stream);
    }

    /*
    offset: uint24_be
    total_pixel_count: uint8_wrap256
    {
        pixel_count: uint8_wrap256
        {
            pixel: uint16_rgb565
        } * pixel_count
        last_pixel_repeat_count: uint8 (omitted for the last chunk if all pixels are rendered)
    } repeat until total_pixel_count is rendered
    */
    private static int WriteRlx16(ref byte stream, ref byte streamEnd, ref byte fb)
    {
        if (Unsafe.ByteOffset(ref stream, ref streamEnd) < 7)
        {
            return 0;
        }

        ref byte streamStart = ref stream;
        uint header = Unsafe.As<byte, uint>(ref stream);
        stream = ref Unsafe.Add(ref stream, 4);

        int address = UInt24BeLsbToInt32(header);
        int totalPixelCount = Wrap256((int)(header >> 24));
        ref ushort fbPixels = ref Unsafe.As<byte, ushort>(ref Unsafe.Add(ref fb, address));
        do
        {
            int pixelCount = stream;
            stream = ref Unsafe.Add(ref stream, 1);
            pixelCount = Wrap256(pixelCount);
            if (Unsafe.IsAddressGreaterThan(ref Unsafe.Add(ref stream, pixelCount * 2), ref streamEnd))
            {
                break;
            }

            ref ushort srcPixels = ref Unsafe.As<byte, ushort>(ref stream);

            // Merged fill fast-path: single raw pixel + non-zero repeat → one fill of (1+repeat) pixels.
            // The repeat always reuses the last raw pixel, so when pixelCount==1 they are the same value.
            if (pixelCount == 1 &&
                totalPixelCount > 1 &&
                Unsafe.IsAddressLessThan(ref Unsafe.Add(ref stream, 2), ref streamEnd))
            {
                byte peekRepeat = Unsafe.Add(ref stream, 2);
                if (peekRepeat > 0)
                {
                    ushort fillValueLe = BinaryPrimitives.ReverseEndianness(srcPixels);
                    int fillCount = 1 + peekRepeat;
                    if (!ColorConvert.SpanAllEqual(ref fbPixels, fillCount, fillValueLe))
                    {
                        MemoryMarshal.CreateSpan(ref fbPixels, fillCount).Fill(fillValueLe);
                    }

                    stream = ref Unsafe.Add(ref stream, 3); // 2-byte pixel + 1 repeat byte
                    fbPixels = ref Unsafe.Add(ref fbPixels, fillCount);
                    totalPixelCount -= fillCount;
                    if (totalPixelCount <= 0)
                    {
                        break;
                    }

                    continue;
                }
            }

            ushort lastPixelBe = Unsafe.Add(ref srcPixels, pixelCount - 1);
            if (!ColorConvert.SpanMatchesBe(ref srcPixels, ref fbPixels, pixelCount))
            {
                ColorConvert.CopyRgb565BeToRgb565Le(
                    ref Unsafe.As<byte, ushort>(ref stream), ref fbPixels, pixelCount);
            }

            stream = ref Unsafe.Add(ref stream, pixelCount * 2);
            fbPixels = ref Unsafe.Add(ref fbPixels, pixelCount);

            totalPixelCount -= pixelCount;
            if (totalPixelCount <= 0 || !Unsafe.IsAddressLessThan(ref stream, ref streamEnd))
            {
                break;
            }

            byte repeat = stream;
            stream = ref Unsafe.Add(ref stream, 1);
            if (repeat > 0)
            {
                ushort fillValueLe = BinaryPrimitives.ReverseEndianness(lastPixelBe);
                if (!ColorConvert.SpanAllEqual(ref fbPixels, repeat, fillValueLe))
                {
                    MemoryMarshal.CreateSpan(ref fbPixels, repeat).Fill(fillValueLe);
                }

                fbPixels = ref Unsafe.Add(ref fbPixels, repeat);
                totalPixelCount -= repeat;
                if (totalPixelCount <= 0)
                {
                    break;
                }
            }
        }
        while (Unsafe.IsAddressLessThan(ref stream, ref streamEnd));

        return totalPixelCount > 0 ? 0 : (int)Unsafe.ByteOffset(ref streamStart, ref stream);
    }

    /*
    offset: uint24_be
    pixel_count: uint8_wrap256
    {
        table_lookup: bit
    } repeat until pixel_count is rendered
    */
    private static int WriteComp8(ref byte stream, ref byte streamEnd, ref byte fb, in ushort hotLookup8, in byte hotColors8, in ushort nonRootLookup8, in byte nonRootColors8, DlMemory? stats = null)
    {
        if (Unsafe.ByteOffset(ref stream, ref streamEnd) < 5)
        {
            return 0;
        }

        ref byte streamStart = ref stream;
        uint header = Unsafe.As<byte, uint>(ref stream);
        stream = ref Unsafe.Add(ref stream, 4);

        int address = UInt24BeLsbToInt32(header);
        uint pixelCount = Wrap256(header >> 24);
        uint tableIndex = 0; // comp8 root state
        ref byte fbPixels = ref Unsafe.Add(ref fb, address);
        ulong pixelBuf = default;
        uint pixelBufLength = 0;
        byte accumulator = 0;
        do
        {
            byte bits = stream;
            stream = ref Unsafe.Add(ref stream, 1);

            Vector64<byte> entryColorsVector;
            uint colorCount;
            if (tableIndex == 0) // hot path: root state, likely L1 hit
            {
                ushort rawValue = Unsafe.Add(ref Unsafe.AsRef(in hotLookup8), (nuint)bits);
                colorCount = (uint)rawValue & 0xFu;
                tableIndex = (uint)rawValue >> 4;
                if (pixelCount < colorCount) colorCount = pixelCount;
                pixelCount -= colorCount;

                ref byte hotColorEntry = ref Unsafe.Add(ref Unsafe.AsRef(in hotColors8), (nuint)bits << 3);
                if (colorCount >= 4)
                {
                    entryColorsVector = Unsafe.As<byte, Vector64<byte>>(ref hotColorEntry);
                }
                else
                {
                    entryColorsVector = Vector64.Create(Unsafe.As<byte, uint>(ref hotColorEntry)).AsByte();
                }

                entryColorsVector += Vector64.Create(accumulator);
                accumulator = entryColorsVector.GetElement(7);
                // colorCount==8 would shift by 64 (UL wraps to 0), so special-case it.
                ulong entryColors = colorCount == 8
                    ? entryColorsVector.AsUInt64().ToScalar()
                    : entryColorsVector.AsUInt64().ToScalar() & ((1UL << ((int)colorCount * 8)) - 1);
                pixelBuf |= entryColors << (int)(pixelBufLength * 8);
                pixelBufLength += colorCount;
                if (pixelBufLength >= 8)
                {
                    Unsafe.As<byte, ulong>(ref fbPixels) = pixelBuf;
                    fbPixels = ref Unsafe.Add(ref fbPixels, 8);
                    pixelBufLength -= 8;
                    pixelBuf = entryColors >> (int)((colorCount - pixelBufLength) * 8);
                }
            }
            else
            {
                // Non-root: consume the byte in NonRootBits-wide chunks (one dependent
                // load each) into the pre-expanded table. Emits flush through pixelBuf
                // like the hot path. tableIndex returns to root (0) on emit.
                uint b = bits;
                for (int chunk = 0; chunk < 8 / NonRootBits; chunk++)
                {
                    if (pixelCount == 0) break;
                    uint chunkBits = b & ((1u << NonRootBits) - 1);
                    b >>= NonRootBits;
                    nuint idx = (tableIndex << NonRootBits) | chunkBits;

                    ushort rawValue = Unsafe.Add(ref Unsafe.AsRef(in nonRootLookup8), idx);
                    uint chunkColorCount = (uint)rawValue & 0xFu;
                    tableIndex = (uint)rawValue >> 4;
                    if (pixelCount < chunkColorCount) chunkColorCount = pixelCount;
                    pixelCount -= chunkColorCount;

                    ref byte colorsRef = ref Unsafe.Add(ref Unsafe.AsRef(in nonRootColors8), idx * NonRootBits);
                    for (uint i = 0; i < chunkColorCount; i++)
                    {
                        byte pixel = (byte)(accumulator + Unsafe.Add(ref colorsRef, i));
                        pixelBuf |= (ulong)pixel << (int)(pixelBufLength * 8);
                        pixelBufLength++;
                        if (pixelBufLength >= 8)
                        {
                            Unsafe.As<byte, ulong>(ref fbPixels) = pixelBuf;
                            fbPixels = ref Unsafe.Add(ref fbPixels, 8);
                            pixelBufLength = 0;
                            pixelBuf = 0;
                        }
                    }
                    // Advance the running accumulator by the entry's total delta:
                    // colors[chunkColorCount] when not all bits emitted, else the last prefix.
                    accumulator = (byte)(accumulator + Unsafe.Add(ref colorsRef, chunkColorCount == NonRootBits ? NonRootBits - 1 : (int)chunkColorCount));
                }
            }
        }
        while (pixelCount > 0 && Unsafe.IsAddressLessThan(ref stream, ref streamEnd));

        if (pixelCount > 0)
        {
            return 0;
        }

        if (pixelBufLength >= 4)
        {
            Unsafe.As<byte, uint>(ref fbPixels) = (uint)pixelBuf;
            fbPixels = ref Unsafe.Add(ref fbPixels, 4);
            pixelBuf >>= 32;
            pixelBufLength -= 4;
        }

        if (pixelBufLength >= 2)
        {
            Unsafe.As<byte, ushort>(ref fbPixels) = (ushort)pixelBuf;
            fbPixels = ref Unsafe.Add(ref fbPixels, 2);
            pixelBuf >>= 16;
            pixelBufLength -= 2;
        }

        if (pixelBufLength == 1)
        {
            fbPixels = (byte)pixelBuf;
            fbPixels = ref Unsafe.Add(ref fbPixels, 1);
        }

        return (int)Unsafe.ByteOffset(ref streamStart, ref stream);
    }

    /*
    offset: uint24_be
    pixel_count: uint8_wrap256
    {
        table_lookup: bit
    } repeat until pixel_count is rendered
    */
    private static int WriteComp16(ref byte stream, ref byte streamEnd, ref byte fb, in ushort hotLookup16, in ushort hotColors16, in ushort nonRootLookup16, in ushort nonRootColors16, DlMemory? stats = null)
    {
        if (Unsafe.ByteOffset(ref stream, ref streamEnd) < 5)
        {
            return 0;
        }

        ref byte streamStart = ref stream;
        uint header = Unsafe.As<byte, uint>(ref stream);
        stream = ref Unsafe.Add(ref stream, 4);

        int address = UInt24BeLsbToInt32(header);
        uint pixelCount = Wrap256(header >> 24);
        uint tableIndex = 8; // comp16 root state
        ushort accumulator = 0;
        ref ushort fbPixels = ref Unsafe.As<byte, ushort>(ref Unsafe.Add(ref fb, address));
        do
        {
            byte bits = stream;
            stream = ref Unsafe.Add(ref stream, 1);

            if (tableIndex == 8) // hot path: byte starts at root, 8-bit L1 lookup
            {
                ushort rawValue = Unsafe.Add(ref Unsafe.AsRef(in hotLookup16), (nuint)bits);
                uint colorCount = (uint)rawValue & 0xFu;
                tableIndex = (uint)rawValue >> 4;
                if (pixelCount < colorCount) colorCount = pixelCount;
                pixelCount -= colorCount;

                ref ushort colorsRef = ref Unsafe.Add(ref Unsafe.AsRef(in hotColors16), (nuint)bits << 3);
                if (colorCount == 8)
                {
                    Vector128<ushort> entryColors = Unsafe.As<ushort, Vector128<ushort>>(ref colorsRef);
                    entryColors += Vector128.Create(accumulator);
                    Unsafe.As<ushort, Vector128<ushort>>(ref fbPixels) = entryColors;
                    fbPixels = ref Unsafe.Add(ref fbPixels, Vector128<ushort>.Count);
                    accumulator = entryColors.GetElement(7);
                }
                else
                {
                    ref ushort colorsEnd = ref Unsafe.Add(ref colorsRef, colorCount);
                    while (Unsafe.IsAddressLessThan(ref colorsRef, ref colorsEnd))
                    {
                        ushort c = colorsRef;
                        colorsRef = ref Unsafe.Add(ref colorsRef, 1);
                        fbPixels = (ushort)(accumulator + c);
                        fbPixels = ref Unsafe.Add(ref fbPixels, 1);
                    }
                    accumulator += colorsRef;
                }
            }
            else
            {
                // Non-root: consume the byte in NonRootBits-wide chunks. Each chunk is one
                // dependent load into the pre-expanded table (L1/L2-resident), so a byte
                // costs 8/NonRootBits dependent loads instead of 8. tableIndex returns to
                // root (8) on emit; the outer loop's hot path catches it at the next byte.
                uint b = bits;
                for (int chunk = 0; chunk < 8 / NonRootBits; chunk++)
                {
                    if (pixelCount == 0) break;
                    uint chunkBits = b & ((1u << NonRootBits) - 1);
                    b >>= NonRootBits;
                    nuint idx = (tableIndex << NonRootBits) | chunkBits;

                    ushort rawValue = Unsafe.Add(ref Unsafe.AsRef(in nonRootLookup16), idx);
                    uint colorCount = (uint)rawValue & 0xFu;
                    tableIndex = (uint)rawValue >> 4;
                    if (pixelCount < colorCount) colorCount = pixelCount;
                    pixelCount -= colorCount;

                    ref ushort colorsRef = ref Unsafe.Add(ref Unsafe.AsRef(in nonRootColors16), idx * NonRootBits);
                    for (uint i = 0; i < colorCount; i++)
                    {
                        fbPixels = (ushort)(accumulator + Unsafe.Add(ref colorsRef, i));
                        fbPixels = ref Unsafe.Add(ref fbPixels, 1);
                    }
                    // Advance the running accumulator by the entry's total delta. When all
                    // chunk bits emitted (colorCount==NonRootBits) the last prefix sum is the
                    // total; otherwise colors[colorCount] is the fill slot holding the total.
                    accumulator = (ushort)(accumulator + Unsafe.Add(ref colorsRef, colorCount == NonRootBits ? NonRootBits - 1 : (int)colorCount));
                }
            }
        }
        while (pixelCount > 0 && Unsafe.IsAddressLessThan(ref stream, ref streamEnd));

        if (pixelCount > 0)
        {
            return 0;
        }

        return (int)Unsafe.ByteOffset(ref streamStart, ref stream);
    }

    /*
    header: 26 38 71 CD
    padding: uint16
    length: uint16_be
    {
        colorA: uint16_rgb565be
        repeatA: uint8
        unknownA: uint3_msb
        jumpA_msb: uint5_lsb
        jumpA_lsb: uint4_msb
        jumpB_lsb: uint4_lsb
        colorB: uint16_rgb565be
        repeatB: uint8
        unknownB: uint3_msb
        jumpB_msb: uint5_lsb
    } * length
    */
    private static int LoadDecompTable(ref byte stream, ref byte streamEnd, DlMemory memory)
    {
        if (Unsafe.ByteOffset(ref stream, ref streamEnd) < 8)
        {
            return 0;
        }

        uint header = Unsafe.As<byte, uint>(ref stream);
        stream = ref Unsafe.Add(ref stream, 6);
        if (header != 0xcd713826U)
        {
            Logger.LogError($"{nameof(LoadDecompTable)}: unknown header {BinaryPrimitives.ReverseEndianness(header):x8}");
        }

        int length = BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, ushort>(ref stream));
        stream = ref Unsafe.Add(ref stream, 2);

        ArgumentOutOfRangeException.ThrowIfLessThan(length, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 4096);
        if (Unsafe.ByteOffset(ref stream, ref streamEnd) < length * DecompEntry.ByteLength)
        {
            return 0;
        }

        // Store the compact raw binary tree — length*2 nodes, 4 bytes each, max 32KB.
        // The decode hot loop walks this bit-by-bit for non-root states (L2 hit ~8 cycles/bit).
        DecompEntry[] tree = new DecompEntry[length * 2];
        ReadOnlySpan<byte> tableBytes = MemoryMarshal.CreateReadOnlySpan(in stream, length * DecompEntry.ByteLength);
        for (int i = 0; i < length; i++)
        {
            (tree[i * 2], tree[(i * 2) + 1]) = DecompEntry.Parse(
                tableBytes.Slice(i * DecompEntry.ByteLength, DecompEntry.ByteLength),
                length);
        }
        memory.CompactTree = tree;

        // Pre-expand the root-state row 8 bits wide into hot tables that stay in L1 (~4.5KB
        // each), used when an input byte starts at root (the common case): 1 load/byte.
        // comp8 root = state 0, comp16 root = state 8.
        const int HotSize = 256;
        ReallocArray(ref memory.HotLookup16, HotSize);
        ReallocArray(ref memory.HotColors16, HotSize * 8);
        ReallocArray(ref memory.HotLookup8,  HotSize);
        ReallocArray(ref memory.HotColors8,  HotSize * 8);
        BuildRow(tree, memory.HotLookup16!, memory.HotColors16!, rootState: 8, state: 8, bits: 8);
        BuildRow(tree, memory.HotLookup8!,  memory.HotColors8!,  rootState: 0, state: 0, bits: 8);

        // Pre-expand every reachable state NonRootBits wide for the non-root decode path.
        // Indexed by (state << NonRootBits) | chunk; allocated sparsely by raw state number
        // so the index space matches the residual states produced by the hot table above.
        int rows = length;
        int rowEntries = 1 << NonRootBits;
        ReallocArray(ref memory.NonRootLookup16, rows * rowEntries);
        ReallocArray(ref memory.NonRootColors16, rows * rowEntries * NonRootBits);
        ReallocArray(ref memory.NonRootLookup8,  rows * rowEntries);
        ReallocArray(ref memory.NonRootColors8,  rows * rowEntries * NonRootBits);
        bool[] visited = new bool[length];
        memory.ReachableStates16 = BuildReachable(tree, memory.NonRootLookup16!, memory.NonRootColors16!, rootState: 8, visited);
        Array.Clear(visited);
        memory.ReachableStates8 = BuildReachable(tree, memory.NonRootLookup8!, memory.NonRootColors8!, rootState: 0, visited);

        Logger.LogDebug($"{nameof(LoadDecompTable)}: length={length} reachable16={memory.ReachableStates16} reachable8={memory.ReachableStates8} nonRootBits={NonRootBits}");

        return 8 + (length * DecompEntry.ByteLength);

        static void ReallocArray<T>([NotNull] ref T[]? array, int length)
            where T : unmanaged
        {
            if (array is null || array.Length != length)
                array = GC.AllocateArray<T>(length, true);
            else
                array.AsSpan().Clear();
        }

        // Build the NonRootBits-wide rows for rootState and every state transitively
        // reachable from it. Returns the number of rows built (reachable state count).
        static int BuildReachable<T>(DecompEntry[] tree, ushort[] lookup, T[] colors, uint rootState, bool[] visited)
            where T : unmanaged, IBinaryInteger<T>
        {
            int count = 0;
            BuildState(rootState);
            return count;

            void BuildState(uint state)
            {
                if (visited[state]) return;
                visited[state] = true;
                count++;

                for (uint chunk = 0; chunk < (1u << NonRootBits); chunk++)
                {
                    int idx = (int)((state << NonRootBits) | chunk);
                    uint next = BuildRowAt(tree, lookup, colors, rootState, state, chunk, NonRootBits, idx);
                    if (!visited[next])
                        BuildState(next);
                }
            }
        }

        // Build a single (state, chunk) entry: walk `bits` input bits from `state`,
        // store the emitted prefix-sum colors, return the residual next state.
        static uint BuildRowAt<T>(DecompEntry[] tree, ushort[] lookup, T[] colors, uint rootState, uint state, uint chunkBits, int bits, int idx)
            where T : unmanaged, IBinaryInteger<T>
        {
            uint tableIndex = state;
            uint accumulator = 0;
            int colorCount = 0;
            uint b = chunkBits;
            Span<T> entryColors = colors.AsSpan(idx * bits, bits);

            for (int bitPos = 0; bitPos < bits; bitPos++)
            {
                DecompEntry node = tree[(tableIndex << 1) + (b & 1)];
                accumulator += node.Color;
                tableIndex = node.Jump;
                b >>= 1;

                if (tableIndex == 0)
                {
                    entryColors[colorCount] = T.CreateTruncating(accumulator);
                    colorCount++;
                    tableIndex = rootState;
                }
            }

            // Fill trailing slots with the running total so the decode loop reads
            // colors[colorCount] as the entry's total delta (residual accumulator).
            entryColors[colorCount..].Fill(T.CreateTruncating(accumulator));
            lookup[idx] = (ushort)((colorCount & 0xFu) | (tableIndex << 4));
            return tableIndex;
        }

        // Build a single row at the canonical index (state << bits). Used for the 8-bit
        // hot tables where the array is indexed directly by input byte.
        static void BuildRow<T>(DecompEntry[] tree, ushort[] lookup, T[] colors, uint rootState, uint state, int bits)
            where T : unmanaged, IBinaryInteger<T>
        {
            for (uint chunk = 0; chunk < (1u << bits); chunk++)
                BuildRowAt(tree, lookup, colors, rootState, state, chunk, bits, (int)chunk);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Wrap256(int value)
    {
        return value == 0 ? 256 : value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Wrap256(uint value)
    {
        return value == 0 ? 256 : value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int UInt24BeLsbToInt32(uint dword)
    {
        return (int)BinaryPrimitives.ReverseEndianness(dword << 8);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct DecompEntry
    {
        public const int ByteLength = 9;

        public static (DecompEntry, DecompEntry) Parse(ReadOnlySpan<byte> bytes, int totalEntries)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(bytes.Length, ByteLength);

            ushort colorA = BinaryPrimitives.ReadUInt16BigEndian(bytes);
            ushort jumpA = (ushort)(((bytes[3] & 0x1fU) << 4) | ((uint)bytes[4] >> 4));

            ushort colorB = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(5));
            ushort jumpB = (ushort)(((bytes[8] & 0x1fU) << 4) | (bytes[4] & 0xfU));

            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(jumpA, totalEntries);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(jumpB, totalEntries);

            return (new(colorA, jumpA), new(colorB, jumpB));
        }

        private DecompEntry(ushort color, ushort jump)
        {
            Color = color;
            Jump = jump;
        }

        public readonly ushort Color;
        public readonly ushort Jump;
    }
}
