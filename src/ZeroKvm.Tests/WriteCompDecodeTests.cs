using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ZeroKvm;

namespace ZeroKvm.Tests;

// Tests for the WriteComp16/WriteComp8 bit-walk decode path.
//
// Biggest concerns:
//   (A) Hot path (byte starts at root) and non-root chunk path must produce identical
//       pixels for the same input — tested by comparing against a reference bit-serial
//       decoder written here from scratch, independent of production code.
//   (B) Accumulator must carry correctly across chunk and byte boundaries.
//   (C) pixelCount clamping must stop exactly at the requested pixel count.
//   (D) A non-root residual state left by one input byte must be honoured by the next.
//
// All tests work by:
//   1. Building a hand-crafted LoadDecompTable wire payload with a known small tree.
//   2. Building a WriteComp16 or WriteComp8 wire payload for a known bit sequence.
//   3. Running DlDecoder.Process() to get the production output.
//   4. Running RefDecode() (a simple bit-serial walk here in the test) to get expected output.
//   5. Asserting they match.
public class WriteCompDecodeTests
{
    // ──────────────────────────────────────────────────────────────────
    // Reference decoder — plain bit-serial walk, no tables, ground truth.
    // comp16: rootState=8, emits ushort; comp8: rootState=0, emits byte.
    // ──────────────────────────────────────────────────────────────────

    private static ushort[] RefDecode16(DlDecoder.DecompEntry[] tree, byte[] inputBytes, int pixelCount)
    {
        var pixels = new ushort[pixelCount];
        int written = 0;
        uint tableIndex = 8; // comp16 root
        uint accumulator = 0;

        foreach (byte b in inputBytes)
        {
            uint bits = b;
            for (int i = 0; i < 8 && written < pixelCount; i++)
            {
                var node = tree[(tableIndex << 1) + (bits & 1)];
                accumulator += node.Color;
                tableIndex = node.Jump;
                bits >>= 1;
                if (tableIndex == 0)
                {
                    pixels[written++] = (ushort)accumulator;
                    tableIndex = 8;
                }
            }
            if (written >= pixelCount) break;
        }
        return pixels;
    }

    private static byte[] RefDecode8(DlDecoder.DecompEntry[] tree, byte[] inputBytes, int pixelCount)
    {
        var pixels = new byte[pixelCount];
        int written = 0;
        uint tableIndex = 0; // comp8 root
        uint accumulator = 0;

        foreach (byte b in inputBytes)
        {
            uint bits = b;
            for (int i = 0; i < 8 && written < pixelCount; i++)
            {
                var node = tree[(tableIndex << 1) + (bits & 1)];
                accumulator += node.Color;
                tableIndex = node.Jump;
                bits >>= 1;
                if (tableIndex == 0)
                {
                    pixels[written++] = (byte)accumulator;
                    tableIndex = 0;
                }
            }
            if (written >= pixelCount) break;
        }
        return pixels;
    }

    // ──────────────────────────────────────────────────────────────────
    // Wire format helpers
    // ──────────────────────────────────────────────────────────────────

    // Builds a LoadDecompTable command stream from raw (color, jump) pairs.
    // entries.Length must be even; each pair is (nodeForBit0, nodeForBit1).
    private static byte[] BuildLoadDecompTable(IReadOnlyList<(ushort Color, int Jump)> nodes)
    {
        // nodes[i*2] = bit-0 branch of entry i, nodes[i*2+1] = bit-1 branch.
        int length = nodes.Count / 2;
        Assert.True(length >= 16, "length must be >= 16 (protocol minimum)");
        var buf = new List<byte>();

        // header: af e0 (LoadDecompTable command)
        buf.Add(0xaf); buf.Add(0xe0);
        // magic: 26 38 71 CD
        buf.AddRange(new byte[] { 0x26, 0x38, 0x71, 0xcd });
        // padding uint16
        buf.Add(0x00); buf.Add(0x00);
        // length uint16_be
        buf.Add((byte)(length >> 8)); buf.Add((byte)length);

        // Per entry (9 bytes): colorA[2] repeatA[1] (unknownA[3]+jumpA[9]split) colorB[2] repeatB[1] (unknownB[3]+jumpB[9]split)
        //   bytes[0:1] = colorA BE
        //   bytes[2]   = repeatA (ignored by decoder, set 0)
        //   bytes[3]   = unknownA[3] << 5 | jumpA bits[8:4]
        //   bytes[4]   = jumpA bits[3:0] << 4 | jumpB bits[3:0]
        //   bytes[5:6] = colorB BE
        //   bytes[7]   = repeatB (0)
        //   bytes[8]   = unknownB[3] << 5 | jumpB bits[8:4]
        for (int i = 0; i < length; i++)
        {
            var (colorA, jumpA) = nodes[i * 2];
            var (colorB, jumpB) = nodes[i * 2 + 1];
            buf.Add((byte)(colorA >> 8)); buf.Add((byte)colorA);
            buf.Add(0x00); // repeatA
            buf.Add((byte)((jumpA >> 4) & 0x1f));
            buf.Add((byte)(((jumpA & 0xf) << 4) | (jumpB & 0xf)));
            buf.Add((byte)(colorB >> 8)); buf.Add((byte)colorB);
            buf.Add(0x00); // repeatB
            buf.Add((byte)((jumpB >> 4) & 0x1f));
        }

        return buf.ToArray();
    }

    // Builds a WriteComp16 command for `pixelCount` pixels at framebuffer offset 0.
    private static byte[] BuildWriteComp16(byte[] inputBits, int pixelCount)
    {
        var buf = new List<byte>();
        buf.Add(0xaf); buf.Add(0x78); // WriteComp16 header
        // offset uint24_be = 0
        buf.Add(0x00); buf.Add(0x00); buf.Add(0x00);
        // pixel_count uint8 (0 means 256)
        buf.Add((byte)(pixelCount == 256 ? 0 : pixelCount));
        buf.AddRange(inputBits);
        return buf.ToArray();
    }

    // Builds a WriteComp8 command for `pixelCount` pixels at framebuffer offset 0.
    private static byte[] BuildWriteComp8(byte[] inputBits, int pixelCount)
    {
        var buf = new List<byte>();
        buf.Add(0xaf); buf.Add(0x70); // WriteComp8 header
        buf.Add(0x00); buf.Add(0x00); buf.Add(0x00);
        buf.Add((byte)(pixelCount == 256 ? 0 : pixelCount));
        buf.AddRange(inputBits);
        return buf.ToArray();
    }

    // Concatenate two command streams
    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }

    // Extract decoded comp16 pixels from framebuffer at offset 0
    private static ushort[] ReadFb16(DlMemory mem, int count)
    {
        var pixels = new ushort[count];
        MemoryMarshal.Cast<byte, ushort>(mem.FrameBuffer)[..count].CopyTo(pixels);
        return pixels;
    }

    // Extract decoded comp8 pixels from framebuffer at offset 0
    private static byte[] ReadFb8(DlMemory mem, int count)
        => mem.FrameBuffer[..count].ToArray();

    // ──────────────────────────────────────────────────────────────────
    // The test tree (shared across tests)
    //
    // 16 entries (minimum), using only the first few meaningfully.
    // comp16 root = state 8, comp8 root = state 0.
    //
    // Tree design (same for both codecs; root just differs):
    //   entry 0 (state 0):  bit=0 → color=0x0010, jump=0 (emit, return to root)
    //                        bit=1 → color=0x0100, jump=1 (continue to entry 1)
    //   entry 1 (state 1):  bit=0 → color=0x0005, jump=0 (emit after 2 bits: total=0x0105)
    //                        bit=1 → color=0x0200, jump=0 (emit after 2 bits: total=0x0300)
    //   entries 2-7: unused, set to (0, 0) — both bits → color=0, jump=0 (emit immediately)
    //   entry 8 (state 8, comp16 root): same structure as entry 0 but comp16 uses this
    //     bit=0 → color=0x0020, jump=0  (1-bit code)
    //     bit=1 → color=0x0400, jump=9  (continue to entry 9)
    //   entry 9 (state 9):
    //     bit=0 → color=0x0003, jump=0  (2-bit code: total=0x0403)
    //     bit=1 → color=0x0800, jump=0  (2-bit code: total=0x0c00)
    //   entries 10-15: (0, 0)
    //
    // So for comp16 (root=8):
    //   bit 0  → emit 0x0020
    //   bits 10 → emit 0x0403
    //   bits 11 → emit 0x0c00
    //   (LSB-first bit order)
    //
    // For comp8 (root=0):
    //   bit 0  → emit 0x10 (truncated to byte)
    //   bits 10 → emit 0x05 (0x0105 truncated)
    //   bits 11 → emit 0x00 (0x0300 truncated)
    // ──────────────────────────────────────────────────────────────────

    private static (ushort Color, int Jump)[] BuildTestNodes()
    {
        var n = new (ushort Color, int Jump)[32]; // 16 entries × 2 nodes
        // entry 0: comp8 root
        n[0] = (0x0010, 0); n[1] = (0x0100, 1);
        // entry 1
        n[2] = (0x0005, 0); n[3] = (0x0200, 0);
        // entries 2-7: noop (emit 0, stay root=0 for comp8)
        for (int i = 2; i < 8; i++) { n[i*2] = (0, 0); n[i*2+1] = (0, 0); }
        // entry 8: comp16 root
        n[16] = (0x0020, 0); n[17] = (0x0400, 9);
        // entry 9
        n[18] = (0x0003, 0); n[19] = (0x0800, 0);
        // entries 10-15
        for (int i = 10; i < 16; i++) { n[i*2] = (0, 0); n[i*2+1] = (0, 0); }
        return n;
    }

    private static DlDecoder.DecompEntry[] BuildRawTree(IReadOnlyList<(ushort Color, int Jump)> nodes)
    {
        var tree = new DlDecoder.DecompEntry[nodes.Count];
        // DecompEntry has public Color and Jump fields but a private ctor — use unsafe cast
        // to write the struct directly (matches the StructLayout Sequential Pack=1 layout).
        for (int i = 0; i < nodes.Count; i++)
        {
            var (color, jump) = nodes[i];
            // Layout: ushort Color, ushort Jump (4 bytes total)
            byte[] bytes = new byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, color);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)jump);
            tree[i] = MemoryMarshal.Read<DlDecoder.DecompEntry>(bytes);
        }
        return tree;
    }

    // ──────────────────────────────────────────────────────────────────
    // Tests
    // ──────────────────────────────────────────────────────────────────

    // (A1) comp16: all bytes start at root — exercises only the hot path.
    // Input: 0b00000000 (all zero bits) → each bit is a 1-bit code → 8 pixels/byte.
    [Fact]
    public void WriteComp16_AllRootBytes_MatchReference()
    {
        var nodes = BuildTestNodes();
        var rawTree = BuildRawTree(nodes);
        byte[] tableCmd = BuildLoadDecompTable(nodes);
        byte[] inputBytes = [0x00, 0x00, 0x00, 0x00]; // 4 bytes × 8 pixels = 32 pixels
        byte[] compCmd = BuildWriteComp16(inputBytes, 32);

        var mem = new DlMemory();
        DlDecoder.Process(Concat(tableCmd, compCmd), mem);
        ushort[] got = ReadFb16(mem, 32);
        ushort[] expected = RefDecode16(rawTree, inputBytes, 32);
        Assert.Equal(expected, got);
    }

    // (A2) comp16: bit pattern that leaves non-root state mid-byte (bits 10 or 11).
    // 0b10101010 = alternating → every pair is a 2-bit code, all non-root after first bit.
    [Fact]
    public void WriteComp16_NonRootWithinByte_MatchReference()
    {
        var nodes = BuildTestNodes();
        var rawTree = BuildRawTree(nodes);
        byte[] tableCmd = BuildLoadDecompTable(nodes);
        byte[] inputBytes = [0b10101010, 0b10101010];
        byte[] compCmd = BuildWriteComp16(inputBytes, 8);

        var mem = new DlMemory();
        DlDecoder.Process(Concat(tableCmd, compCmd), mem);
        ushort[] got = ReadFb16(mem, 8);
        ushort[] expected = RefDecode16(rawTree, inputBytes, 8);
        Assert.Equal(expected, got);
    }

    // (B) comp16: residual non-root state carries into next byte.
    // 0xFF = 0b11111111: bit-0=1 → state 9, bit-1=1 → emit (2-bit code spans bits 0-1),
    // then state returns to root. Bits 2-3=1,1 → same again. Gives 4 pixels.
    // Second byte 0x00 starts at root and gives 8 more. Accumulator must carry.
    [Fact]
    public void WriteComp16_AccumulatorCarriesAcrossBytes_MatchReference()
    {
        var nodes = BuildTestNodes();
        var rawTree = BuildRawTree(nodes);
        byte[] tableCmd = BuildLoadDecompTable(nodes);
        byte[] inputBytes = [0xFF, 0x00, 0xFF, 0x00];
        int pixelCount = 24;
        byte[] compCmd = BuildWriteComp16(inputBytes, pixelCount);

        var mem = new DlMemory();
        DlDecoder.Process(Concat(tableCmd, compCmd), mem);
        ushort[] got = ReadFb16(mem, pixelCount);
        ushort[] expected = RefDecode16(rawTree, inputBytes, pixelCount);
        Assert.Equal(expected, got);
    }

    // (C) comp16: pixelCount clamp stops exactly — no extra pixels written beyond count.
    // Request 5 pixels from a stream that would produce many more.
    [Fact]
    public void WriteComp16_PixelCountClamp_StopsExactly()
    {
        var nodes = BuildTestNodes();
        var rawTree = BuildRawTree(nodes);
        byte[] tableCmd = BuildLoadDecompTable(nodes);
        byte[] inputBytes = [0x00, 0x00, 0x00, 0x00, 0x00];
        int pixelCount = 5;
        byte[] compCmd = BuildWriteComp16(inputBytes, pixelCount);

        var mem = new DlMemory();
        DlDecoder.Process(Concat(tableCmd, compCmd), mem);
        ushort[] got = ReadFb16(mem, pixelCount);
        ushort[] expected = RefDecode16(rawTree, inputBytes, pixelCount);
        Assert.Equal(expected, got);
        // The pixel after count must still be the initial framebuffer value (green = 0x07e0).
        // FrameBuffer is initialised to 0b0000011111100000 = 0x07e0 per DlMemory ctor.
        ushort sentinel = MemoryMarshal.Cast<byte, ushort>(mem.FrameBuffer)[pixelCount];
        Assert.Equal(0x07e0, sentinel);
    }

    // (D) comp16: a code that straddles the 2-bit NonRootBits chunk boundary.
    // 0b00000001: bits 0=1 → state 9 (enters non-root), then bit 1=0 → emit 0x0403.
    // The first chunk (bits 0-1 = 0b01) leaves state 9 with a partial pixel.
    // The second chunk (bits 2-3 = 0b00) reads from state 9 → emits.
    // This directly tests that the non-root chunk loop carries tableIndex between chunks.
    [Fact]
    public void WriteComp16_CodeStraddles2BitChunkBoundary_MatchReference()
    {
        var nodes = BuildTestNodes();
        var rawTree = BuildRawTree(nodes);
        byte[] tableCmd = BuildLoadDecompTable(nodes);
        // 0b00000001 LSB-first: bit0=1 (→state9), bit1=0 (→emit), bits2-7=0 (1-bit codes)
        byte[] inputBytes = [0b00000001, 0b00000001, 0b00000001];
        int pixelCount = 18;
        byte[] compCmd = BuildWriteComp16(inputBytes, pixelCount);

        var mem = new DlMemory();
        DlDecoder.Process(Concat(tableCmd, compCmd), mem);
        ushort[] got = ReadFb16(mem, pixelCount);
        ushort[] expected = RefDecode16(rawTree, inputBytes, pixelCount);
        Assert.Equal(expected, got);
    }

    // (E) comp8: accumulator carries correctly after the non-root path into the next byte.
    // 0xFF leaves state 1 residual after bit7=1 (no emit). Next byte 0x00 continues from
    // state 1: bit0=0 → emit with accumulated color, then returns to root for remaining bits.
    [Fact]
    public void WriteComp8_AccumulatorCarriesAcrossBytes_MatchReference()
    {
        var nodes = BuildTestNodes();
        var rawTree = BuildRawTree(nodes);
        byte[] tableCmd = BuildLoadDecompTable(nodes);
        // 0xFF: 4 two-bit codes from root, but bit7=1 leaves state 1 (non-root residual).
        // 0x00: bit0=0 from state1 → emit then root, bits1-7=0 → 7 more one-bit codes.
        byte[] inputBytes = [0xFF, 0x00, 0xFF, 0x00];
        int pixelCount = 24;
        byte[] compCmd = BuildWriteComp8(inputBytes, pixelCount);

        var mem = new DlMemory();
        DlDecoder.Process(Concat(tableCmd, compCmd), mem);
        byte[] got = ReadFb8(mem, pixelCount);
        byte[] expected = RefDecode8(rawTree, inputBytes, pixelCount);
        Assert.Equal(expected, got);
    }

    // (F) comp16 followed by comp16: second command sees a clean accumulator (=0).
    // Accumulator is local to each WriteComp command, not global state.
    [Fact]
    public void WriteComp16_TwoCommands_SecondCommandHasFreshAccumulator()
    {
        var nodes = BuildTestNodes();
        var rawTree = BuildRawTree(nodes);
        byte[] tableCmd = BuildLoadDecompTable(nodes);
        // First command: 8 pixels at offset 0
        byte[] input1 = [0xFF, 0xFF]; // builds up a large accumulator
        byte[] comp1 = BuildWriteComp16(input1, 8);
        // Second command: 8 pixels at offset 16 (8 ushorts = 16 bytes)
        byte[] input2 = [0xFF, 0xFF];
        var comp2Buf = new List<byte>();
        comp2Buf.Add(0xaf); comp2Buf.Add(0x78);
        comp2Buf.Add(0x00); comp2Buf.Add(0x00); comp2Buf.Add(0x10); // offset 16
        comp2Buf.Add(8);
        comp2Buf.AddRange(input2);

        var mem = new DlMemory();
        DlDecoder.Process(Concat(tableCmd, Concat(comp1, comp2Buf.ToArray())), mem);

        // Both commands use the same input bytes from the same root state, so both
        // should produce the same pixel sequence (accumulator starts at 0 each time).
        ushort[] from1 = ReadFb16(mem, 8);
        ushort[] from2 = MemoryMarshal.Cast<byte, ushort>(mem.FrameBuffer)[8..16].ToArray();
        Assert.Equal(from1, from2);

        // Also verify against reference
        ushort[] expected = RefDecode16(rawTree, input1, 8);
        Assert.Equal(expected, from1);
    }

    // (G) comp8: single byte that exercises the non-root chunk path from root.
    // 0xFF = 0b11111111: bit0=1→state1, bit1=1→emit. Four 2-bit codes = 4 pixels.
    [Fact]
    public void WriteComp8_NonRootChunk_MatchReference()
    {
        var nodes = BuildTestNodes();
        var rawTree = BuildRawTree(nodes);
        byte[] tableCmd = BuildLoadDecompTable(nodes);
        byte[] inputBytes = [0xFF];
        int pixelCount = 4;
        byte[] compCmd = BuildWriteComp8(inputBytes, pixelCount);

        var mem = new DlMemory();
        DlDecoder.Process(Concat(tableCmd, compCmd), mem);
        byte[] got = ReadFb8(mem, pixelCount);
        byte[] expected = RefDecode8(rawTree, inputBytes, pixelCount);
        Assert.Equal(expected, got);
    }
}
