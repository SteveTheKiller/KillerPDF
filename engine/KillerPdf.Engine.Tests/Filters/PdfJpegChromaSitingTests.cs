using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

// Subsampled components are reconstructed at the centered sample positions of JFIF
// (ITU-T T.871): a component sample covering s full-resolution samples sits at their
// center, so full-resolution sample x reads the component at (x + 0.5) / s - 0.5, blended
// linearly between the two nearest component samples and clamped at the image edges.
// The fixtures are DC-only, so every 8x8 component block is one known level and the
// expected values below are worked by hand where two blocks with different levels meet.
public sealed class PdfJpegChromaSitingTests
{
    private const byte A = 65, B = 192, C = 128, D = 0;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FourTwoZeroChromaIsInterpolatedInBothDirections(bool progressive)
    {
        // 32x32, two by two MCUs. Cb blocks: A top left, B top right, C bottom left, D bottom right.
        byte[] jpeg = Encode(32, 32, [(2, 2), (1, 1), (1, 1)], progressive,
            (component, x, y) => component switch
            {
                0 => 128,
                1 => (x, y) switch { (0, 0) => A, (1, 0) => B, (0, 1) => C, _ => D },
                _ => 100,
            });
        var image = PdfJpegDecoder.DecodeImage(jpeg, 1 << 20, 1, colorTransform: 0);

        // Luma x=15 reads chroma 7.25: 3/4 of column 7 and 1/4 of column 8; x=16 reads 7.75.
        Assert.Equal(65, Cb(image, 0, 0));
        Assert.Equal(97, Cb(image, 15, 0));    // (3*65 + 192) / 4 = 96.75
        Assert.Equal(81, Cb(image, 14, 15));   // (3*65 + 128) / 4 = 80.75
        Assert.Equal(97, Cb(image, 15, 15));   // (9*65 + 3*192 + 3*128 + 0) / 16 = 96.5625
        Assert.Equal(128, Cb(image, 16, 15));  // (3*65 + 9*192 + 128 + 3*0) / 16 = 128.1875
        Assert.Equal(96, Cb(image, 15, 16));   // (3*65 + 192 + 9*128 + 3*0) / 16 = 96.1875
        Assert.Equal(64, Cb(image, 16, 16));   // (65 + 3*192 + 3*128 + 9*0) / 16 = 64.0625
        Assert.Equal(0, Cb(image, 31, 31));
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                Assert.Equal(128, image.Samples[(y * 32 + x) * 3]);
                Assert.Equal(100, image.Samples[(y * 32 + x) * 3 + 2]);
            }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FourTwoTwoChromaIsInterpolatedAcrossColumns(bool progressive)
    {
        byte[] jpeg = Encode(32, 8, [(2, 1), (1, 1), (1, 1)], progressive,
            (component, x, _) => component == 1 ? (x == 0 ? A : B) : (byte)128);
        var image = PdfJpegDecoder.DecodeImage(jpeg, 1 << 20, 1, colorTransform: 0);

        int[] expected = new int[32];
        for (int x = 0; x < 32; x++) expected[x] = x < 16 ? A : B;
        expected[15] = 97;   // (3*65 + 192) / 4 = 96.75
        expected[16] = 160;  // (65 + 3*192) / 4 = 160.25
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 32; x++)
                Assert.Equal(expected[x], Cb(image, x, y));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FourFourZeroChromaIsInterpolatedAcrossRows(bool progressive)
    {
        byte[] jpeg = Encode(8, 32, [(1, 2), (1, 1), (1, 1)], progressive,
            (component, _, y) => component == 1 ? (y == 0 ? A : B) : (byte)128);
        var image = PdfJpegDecoder.DecodeImage(jpeg, 1 << 20, 1, colorTransform: 0);

        for (int y = 0; y < 32; y++)
        {
            int expected = y switch { 15 => 97, 16 => 160, < 16 => A, _ => B };
            for (int x = 0; x < 8; x++)
                Assert.Equal(expected, Cb(image, x, y));
        }
    }

    [Fact]
    public void FourOneOneChromaUsesQuarterSiting()
    {
        // Chroma column 7 sits at luma 29.5 and column 8 at 33.5.
        byte[] jpeg = Encode(64, 8, [(4, 1), (1, 1), (1, 1)], false,
            (component, x, _) => component == 1 ? (x == 0 ? A : B) : (byte)128);
        var image = PdfJpegDecoder.DecodeImage(jpeg, 1 << 20, 1, colorTransform: 0);

        Assert.Equal(65, Cb(image, 29, 3));
        Assert.Equal(81, Cb(image, 30, 3));   // (7*65 + 192) / 8 = 80.875
        Assert.Equal(113, Cb(image, 31, 3));  // (5*65 + 3*192) / 8 = 112.625
        Assert.Equal(144, Cb(image, 32, 3));  // (3*65 + 5*192) / 8 = 144.375
        Assert.Equal(176, Cb(image, 33, 3));  // (65 + 7*192) / 8 = 176.125
        Assert.Equal(192, Cb(image, 34, 3));
        Assert.Equal(65, Cb(image, 0, 0));
        Assert.Equal(192, Cb(image, 63, 7));
    }

    [Fact]
    public void ThreeToOneChromaUsesThirdSiting()
    {
        // Chroma column 7 sits at luma 22 and column 8 at 25.
        byte[] jpeg = Encode(48, 8, [(3, 1), (1, 1), (1, 1)], false,
            (component, x, _) => component == 1 ? (x == 0 ? A : B) : (byte)128);
        var image = PdfJpegDecoder.DecodeImage(jpeg, 1 << 20, 1, colorTransform: 0);

        Assert.Equal(65, Cb(image, 22, 0));
        Assert.Equal(107, Cb(image, 23, 0));  // (2*65 + 192) / 3 = 107.33
        Assert.Equal(150, Cb(image, 24, 0));  // (65 + 2*192) / 3 = 149.67
        Assert.Equal(192, Cb(image, 25, 0));
    }

    [Fact]
    public void ReducedDecodeUsesTheSameSitingOnTheReducedGrid()
    {
        // At one eighth each block is one sample: 4x2 luma over 2x1 chroma.
        byte[] jpeg = Encode(32, 16, [(2, 2), (1, 1), (1, 1)], false,
            (component, x, _) => component == 1 ? (x == 0 ? A : B) : (byte)128);
        var image = PdfJpegDecoder.DecodeImage(jpeg, 1 << 20, 8, colorTransform: 0);

        Assert.Equal(4, image.Width);
        Assert.Equal(2, image.Height);
        for (int y = 0; y < 2; y++)
        {
            Assert.Equal(65, Cb(image, 0, y));
            Assert.Equal(97, Cb(image, 1, y));   // (3*65 + 192) / 4 = 96.75
            Assert.Equal(160, Cb(image, 2, y));  // (65 + 3*192) / 4 = 160.25
            Assert.Equal(192, Cb(image, 3, y));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EdgeSamplesClampToTheNearestChromaSample(bool progressive)
    {
        // One MCU column and two MCU rows, so the right and bottom edges have no chroma
        // beyond them; the last luma row and column reuse the last chroma sample.
        byte[] jpeg = Encode(16, 32, [(2, 2), (1, 1), (1, 1)], progressive,
            (component, _, y) => component == 1 ? (y == 0 ? A : B) : (byte)128);
        var image = PdfJpegDecoder.DecodeImage(jpeg, 1 << 20, 1, colorTransform: 0);

        for (int x = 0; x < 16; x++)
        {
            Assert.Equal(65, Cb(image, x, 0));
            Assert.Equal(65, Cb(image, x, 14));
            Assert.Equal(97, Cb(image, x, 15));
            Assert.Equal(160, Cb(image, x, 16));
            Assert.Equal(192, Cb(image, x, 17));
            Assert.Equal(192, Cb(image, x, 31));
        }
    }

    [Fact]
    public void YCbCrColorUsesTheInterpolatedChroma()
    {
        // Y = Cr = 128, so blue is 128 + 1.772 (Cb - 128) by T.871.
        byte[] jpeg = Encode(32, 8, [(2, 1), (1, 1), (1, 1)], false,
            (component, x, _) => component == 1 ? (x == 0 ? A : B) : (byte)128);
        var image = PdfJpegDecoder.DecodeImage(jpeg, 1 << 20, 1);

        AssertNear(73, image.Samples[15 * 3 + 2]);   // Cb 96.75: 128 - 1.772 * 31.25 = 72.6
        AssertNear(185, image.Samples[16 * 3 + 2]);  // Cb 160.25: 128 + 1.772 * 32.25 = 185.1
        AssertNear(16, image.Samples[14 * 3 + 2]);   // Cb 65: 128 - 1.772 * 63 = 16.4
        AssertNear(128, image.Samples[15 * 3]);
    }

    [Fact]
    public void SubsampledYcckUsesTheInterpolatedChroma()
    {
        // Adobe transform 2: CMY are the inverted YCbCr colors, K is carried as is.
        byte[] jpeg = Encode(32, 8, [(2, 1), (1, 1), (1, 1), (1, 1)], false,
            (component, x, _) => component switch { 1 => x == 0 ? A : B, 3 => 50, _ => 128 },
            adobeTransform: 2);
        var image = PdfJpegDecoder.DecodeImage(jpeg, 1 << 20, 1);

        AssertNear(255 - 73, image.Samples[15 * 4 + 2]);
        AssertNear(255 - 185, image.Samples[16 * 4 + 2]);
        Assert.Equal(50, image.Samples[15 * 4 + 3]);
    }

    [Fact]
    public void SubsampledCmykComponentsAreInterpolated()
    {
        // A four-component image with a subsampled second component and no transform.
        byte[] jpeg = Encode(32, 8, [(2, 1), (1, 1), (1, 1), (1, 1)], false,
            (component, x, _) => component == 1 ? (x == 0 ? A : B) : (byte)(40 + component),
            adobeTransform: 0);
        var image = PdfJpegDecoder.DecodeImage(jpeg, 1 << 20, 1);

        Assert.Equal(97, image.Samples[15 * 4 + 1]);
        Assert.Equal(160, image.Samples[16 * 4 + 1]);
        Assert.Equal(40, image.Samples[15 * 4]);
        Assert.Equal(42, image.Samples[16 * 4 + 2]);
        Assert.Equal(43, image.Samples[16 * 4 + 3]);
    }

    [Fact]
    public void RecoveryKeepsCompleteSubsampledRowsBeforeTruncation()
    {
        // Two MCU rows; the stream is cut inside the second row, so the first row is kept
        // with its chroma clamped at its own last row and the rest stays neutral.
        byte[] complete = Encode(16, 32, [(2, 2), (1, 1), (1, 1)], false,
            (component, _, y) => component == 1 ? (y == 0 ? A : B) : (byte)128,
            adobeTransform: 0);
        byte[] truncated = Encode(16, 32, [(2, 2), (1, 1), (1, 1)], false,
            (component, _, y) => component == 1 ? (y == 0 ? A : B) : (byte)128,
            adobeTransform: 0, truncateAfterMcuRows: 1);
        Assert.Equal(97, Cb(PdfJpegDecoder.DecodeImage(complete, 1 << 20, 1), 0, 15));
        PdfStream Stream(byte[] bytes) => new(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(new PdfName("Filter"u8), new PdfName("DCTDecode"u8))]), bytes);
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(Stream(truncated), 16 * 32 * 3));
        byte[] recovered = PdfStreamDecoder.DecodeWithCompatibilityRecovery(Stream(truncated), 16 * 32 * 3);

        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 16; x++)
                Assert.Equal(y < 16 ? A : 128, recovered[(y * 16 + x) * 3 + 1]);
    }

    private static int Cb(JpegDecodedImage image, int x, int y) =>
        image.Samples[(y * image.Width + x) * image.Components + 1];

    private static void AssertNear(int expected, int actual) =>
        Assert.InRange(actual, expected - 1, expected + 1);

    // A DC-only baseline or progressive encoder with fixed Huffman codes and unit
    // quantization: a block with level L carries DC coefficient 8 (L - 128) and decodes to L.
    // Levels are given per component block, in that component's own block grid.
    private static byte[] Encode(int width, int height, (int H, int V)[] sampling, bool progressive,
        Func<int, int, int, byte> level, int? adobeTransform = null, int? truncateAfterMcuRows = null)
    {
        int components = sampling.Length;
        var bytes = new List<byte> { 0xff, 0xd8 };
        void Segment(byte marker, byte[] payload)
        {
            bytes.AddRange([0xff, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2)]);
            bytes.AddRange(payload);
        }
        if (adobeTransform is int transform)
            Segment(0xee, [.. "Adobe"u8, 0, 100, 0, 0, 0, 0, (byte)transform]);
        Segment(0xdb, [0, .. Enumerable.Repeat((byte)1, 64)]);
        var frame = new List<byte> { 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)components };
        for (int c = 0; c < components; c++)
            frame.AddRange([(byte)(c + 1), (byte)(sampling[c].H * 16 + sampling[c].V), 0]);
        Segment(progressive ? (byte)0xc2 : (byte)0xc0, [.. frame]);
        byte[] dcCounts = new byte[16];
        dcCounts[3] = 12;
        Segment(0xc4, [0, .. dcCounts, .. Enumerable.Range(0, 12).Select(i => (byte)i),
            0x10, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        var scan = new List<byte> { (byte)components };
        for (int c = 0; c < components; c++) scan.AddRange([(byte)(c + 1), 0]);
        scan.AddRange([0, progressive ? (byte)0 : (byte)63, 0]);
        Segment(0xda, [.. scan]);
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
        int maxH = sampling.Max(item => item.H), maxV = sampling.Max(item => item.V);
        int mcuColumns = (width + maxH * 8 - 1) / (maxH * 8);
        int mcuRows = (height + maxV * 8 - 1) / (maxV * 8);
        int[] predictors = new int[components];
        for (int row = 0; row < mcuRows; row++)
        {
            if (row == truncateAfterMcuRows)
            {
                // Keep the partial byte that finishes the last complete row, then stop.
                if (bits != 0) Write((1 << (8 - bits)) - 1, 8 - bits);
                return [.. bytes];
            }
            for (int column = 0; column < mcuColumns; column++)
                for (int c = 0; c < components; c++)
                    for (int v = 0; v < sampling[c].V; v++)
                        for (int h = 0; h < sampling[c].H; h++)
                        {
                            int dc = (level(c, column * sampling[c].H + h, row * sampling[c].V + v) - 128) * 8;
                            int delta = dc - predictors[c];
                            predictors[c] = dc;
                            int category = 0;
                            for (int magnitude = Math.Abs(delta); magnitude > 0; magnitude >>= 1) category++;
                            Write(category, 4);
                            Write(delta < 0 ? delta + (1 << category) - 1 : delta, category);
                            if (!progressive) Write(0, 1);
                        }
        }
        if (bits != 0) Write((1 << (8 - bits)) - 1, 8 - bits);
        bytes.AddRange([0xff, 0xd9]);
        return [.. bytes];
    }
}
