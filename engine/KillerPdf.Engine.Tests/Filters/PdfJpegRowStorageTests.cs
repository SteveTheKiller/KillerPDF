using KillerPdf.Engine.Filters;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

public sealed class PdfJpegRowStorageTests
{
    public static IEnumerable<object[]> Shapes()
    {
        foreach (var (components, horizontal, vertical) in new[] { (1, 1, 1), (3, 1, 1), (3, 2, 1), (3, 2, 2), (3, 3, 1), (3, 4, 1), (4, 1, 1), (4, 1, 2), (4, 2, 1) })
            foreach (int reduction in new[] { 1, 2, 4, 8 })
                foreach (bool progressive in new[] { false, true })
                    yield return [components, horizontal, vertical, reduction, progressive];
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void OddSizedRowsPreserveComponentBlocks(int components, int horizontal, int vertical, int reduction, bool progressive)
    {
        byte[] jpeg = Encode(35, 37, components, horizontal, vertical, progressive);
        var image = PdfJpegDecoder.DecodeImage(jpeg, 10000, reduction, colorTransform: 0);
        int blockSize = 8 / reduction;
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                for (int component = 0; component < components; component++)
                {
                    int h = component == 0 ? horizontal : 1;
                    int v = component == 0 ? vertical : 1;
                    int column = x / (horizontal * blockSize);
                    int row = y / (vertical * blockSize);
                    int blockX = x * h / horizontal / blockSize % h;
                    int blockY = y * v / vertical / blockSize % v;
                    Assert.Equal(Level(row, column, blockY, blockX, component),
                        image.Samples[(y * image.Width + x) * components + component]);
                }
    }

    [Fact]
    public void BaselineScratchDoesNotGrowWithImageHeight()
    {
        byte[] jpeg = Encode(1025, 1025, 3, 2, 2);
        _ = PdfJpegDecoder.DecodeImage(Encode(17, 17, 3, 2, 2), 10000, 1, colorTransform: 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var image = PdfJpegDecoder.DecodeImage(jpeg, 4 * 1024 * 1024, 1, colorTransform: 0);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, image.Samples.Length, image.Samples.Length + 256 * 1024);
        Assert.Equal(1025 * 1025 * 3, image.Samples.Length);
    }

    private static byte Level(int row, int column, int vertical, int horizontal, int component) =>
        (byte)(64 + (row * 3 + column * 5 + vertical * 7 + horizontal * 11 + component * 13) % 128);

    // A DC-only encoder with fixed Huffman codes makes the expected component
    // samples independent of the decoder, including rows that reuse scratch storage.
    private static byte[] Encode(int width, int height, int components, int horizontal, int vertical, bool progressive = false)
    {
        var bytes = new List<byte> { 0xff, 0xd8 };
        void Segment(byte marker, byte[] payload)
        {
            bytes.AddRange([0xff, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2)]);
            bytes.AddRange(payload);
        }
        Segment(0xdb, [0, .. Enumerable.Repeat((byte)1, 64)]);
        var frame = new List<byte> { 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)components };
        for (int c = 0; c < components; c++)
            frame.AddRange([(byte)(c + 1), c == 0 ? (byte)(horizontal * 16 + vertical) : (byte)0x11, 0]);
        Segment(progressive ? (byte)0xc2 : (byte)0xc0, frame.ToArray());
        byte[] dcCounts = new byte[16];
        dcCounts[3] = 12;
        Segment(0xc4, [0, .. dcCounts, .. Enumerable.Range(0, 12).Select(i => (byte)i),
            0x10, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        var scan = new List<byte> { (byte)components };
        for (int c = 0; c < components; c++) scan.AddRange([(byte)(c + 1), 0]);
        scan.AddRange([0, progressive ? (byte)0 : (byte)63, 0]);
        Segment(0xda, scan.ToArray());
        int pending = 0, bits = 0;
        void Write(int value, int count)
        {
            for (int bit = count - 1; bit >= 0; bit--)
            {
                pending = (pending << 1) | ((value >> bit) & 1);
                if (++bits != 8) continue;
                bytes.Add((byte)pending);
                if ((byte)pending == 0xff) bytes.Add(0);
                pending = bits = 0;
            }
        }
        int[] predictors = new int[components];
        for (int row = 0; row < (height + vertical * 8 - 1) / (vertical * 8); row++)
            for (int column = 0; column < (width + horizontal * 8 - 1) / (horizontal * 8); column++)
                for (int c = 0; c < components; c++)
                    for (int v = 0; v < (c == 0 ? vertical : 1); v++)
                        for (int h = 0; h < (c == 0 ? horizontal : 1); h++)
                        {
                            int dc = (Level(row, column, v, h, c) - 128) * 8;
                            int delta = dc - predictors[c];
                            predictors[c] = dc;
                            int category = 0;
                            for (int magnitude = Math.Abs(delta); magnitude > 0; magnitude >>= 1) category++;
                            Write(category, 4);
                            Write(delta < 0 ? delta + (1 << category) - 1 : delta, category);
                            if (!progressive) Write(0, 1);
                        }
        if (bits != 0) Write((1 << (8 - bits)) - 1, 8 - bits);
        bytes.AddRange([0xff, 0xd9]);
        return bytes.ToArray();
    }
}
