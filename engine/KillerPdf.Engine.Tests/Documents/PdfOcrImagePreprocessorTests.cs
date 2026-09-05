using System.Numerics;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOcrImagePreprocessorTests
{
    [Fact]
    public void PrepareBgra_CompositesAlphaAndConvertsToGrayscale()
    {
        var options = new PdfOcrOptions(["eng"], deskew: false,
            correctOrientation: false, detectPageSegments: false);
        byte[] bgra = [0, 0, 255, 255, 0, 0, 0, 0];

        PdfOcrPreparedImage image = PdfOcrImagePreprocessor.PrepareBgra(bgra, 2, 1, options);

        Assert.Equal([77, 255], image.Pixels.ToArray());
        Assert.False(image.IsBinary);
        Assert.Empty(image.Diagnostics);
    }

    [Fact]
    public void PrepareBgra_VectorizedOpaqueConversionMatchesExactLuma()
    {
        int width = Vector<byte>.Count + 3;
        var bgra = new byte[width * 4];
        var expected = new byte[width];
        for (int pixel = 0; pixel < width; pixel++)
        {
            int offset = pixel * 4;
            byte blue = bgra[offset] = (byte)(pixel * 17 + 3);
            byte green = bgra[offset + 1] = (byte)(pixel * 29 + 5);
            byte red = bgra[offset + 2] = (byte)(pixel * 43 + 7);
            bgra[offset + 3] = 255;
            expected[pixel] = (byte)((29 * blue + 150 * green + 77 * red + 128) >> 8);
        }
        var options = new PdfOcrOptions(["eng"], deskew: false,
            correctOrientation: false, removeBackground: false, removeNoise: false,
            detectPageSegments: false);

        PdfOcrPreparedImage image = PdfOcrImagePreprocessor.PrepareBgra(
            bgra, width, 1, options);

        Assert.Equal(expected, image.Pixels.ToArray());
    }

    [Fact]
    public void PrepareBgra_AdaptiveThresholdAndMedianRemoveIsolatedNoise()
    {
        byte[] bgra = Enumerable.Repeat((byte)255, 7 * 7 * 4).ToArray();
        for (int pixel = 0; pixel < 49; pixel++) bgra[pixel * 4 + 3] = 255;
        int center = (3 * 7 + 3) * 4;
        bgra[center] = bgra[center + 1] = bgra[center + 2] = 0;
        var options = new PdfOcrOptions(["eng"], deskew: false,
            correctOrientation: false, removeBackground: true, removeNoise: true,
            detectPageSegments: false);

        PdfOcrPreparedImage image = PdfOcrImagePreprocessor.PrepareBgra(bgra, 7, 7, options);

        Assert.True(image.IsBinary);
        Assert.Equal(255, image.Pixels.Span[3 * 7 + 3]);
    }

    [Fact]
    public void PrepareBgra_AdaptiveThresholdMatchesBoundedNeighborhoodMeans()
    {
        const int width = 19, height = 13;
        var gray = new byte[width * height];
        var bgra = new byte[gray.Length * 4];
        for (int index = 0; index < gray.Length; index++)
        {
            byte value = gray[index] = (byte)((index * 37 + index / width * 19) % 256);
            int offset = index * 4;
            bgra[offset] = bgra[offset + 1] = bgra[offset + 2] = value;
            bgra[offset + 3] = 255;
        }
        var options = new PdfOcrOptions(["eng"], deskew: false,
            correctOrientation: false, removeBackground: true, removeNoise: false,
            detectPageSegments: false);

        PdfOcrPreparedImage image = PdfOcrImagePreprocessor.PrepareBgra(
            bgra, width, height, options);

        const int radius = 4;
        var expected = new byte[gray.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int left = Math.Max(0, x - radius), right = Math.Min(width, x + radius + 1);
                int top = Math.Max(0, y - radius), bottom = Math.Min(height, y + radius + 1);
                long sum = 0;
                for (int yy = top; yy < bottom; yy++)
                    for (int xx = left; xx < right; xx++) sum += gray[yy * width + xx];
                int mean = (int)(sum / ((right - left) * (bottom - top)));
                expected[y * width + x] = gray[y * width + x] < mean - 12
                    ? (byte)0 : (byte)255;
            }
        Assert.Equal(expected, image.Pixels.ToArray());
    }

    [Fact]
    public void PrepareBgra_DeskewsSlantedTextRows()
    {
        const int width = 41, height = 21;
        byte[] bgra = Enumerable.Repeat(byte.MaxValue, width * height * 4).ToArray();
        for (int x = 0; x < width; x++)
            foreach (int baseline in new[] { 5, 12 })
            {
                int y = baseline + x / 10;
                int offset = (y * width + x) * 4;
                bgra[offset] = bgra[offset + 1] = bgra[offset + 2] = 0;
            }
        var options = new PdfOcrOptions(["eng"], deskew: true,
            correctOrientation: false, removeBackground: false, removeNoise: false,
            detectPageSegments: false);

        PdfOcrPreparedImage image = PdfOcrImagePreprocessor.PrepareBgra(
            bgra, width, height, options);

        int longestRow = 0;
        for (int y = 0; y < height; y++)
        {
            int dark = 0;
            foreach (byte value in image.Pixels.Span.Slice(y * width, width))
                if (value < 128) dark++;
            longestRow = Math.Max(longestRow, dark);
        }
        Assert.True(longestRow > 10, $"Longest corrected row was {longestRow} pixels.");
        Assert.Empty(image.Diagnostics);
    }

    [Fact]
    public void PrepareBgra_ValidatesLengthAndCancellation()
    {
        var options = new PdfOcrOptions(["eng"]);
        Assert.Throws<ArgumentException>(() =>
            PdfOcrImagePreprocessor.PrepareBgra(new byte[3], 1, 1, options));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            PdfOcrImagePreprocessor.PrepareBgra(new byte[4], 1, 1, options, canceled.Token));
    }

    [Fact]
    public void CropBgra_ClampsBoundsAndCopiesWholePixels()
    {
        byte[] bgra = Enumerable.Range(0, 4 * 3 * 4).Select(value => (byte)value).ToArray();

        PdfOcrBgraImage crop = PdfOcrImagePreprocessor.CropBgra(
            bgra, 4, 3, left: 2, top: 1, width: 8, height: 8);

        Assert.Equal(2, crop.Width);
        Assert.Equal(2, crop.Height);
        Assert.Equal(bgra.AsSpan((1 * 4 + 2) * 4, 8).ToArray(),
            crop.Pixels.Span[..8].ToArray());
        Assert.Equal(bgra.AsSpan((2 * 4 + 2) * 4, 8).ToArray(),
            crop.Pixels.Span[8..].ToArray());
    }

    [Fact]
    public void CropBgra_ValidatesSourceAndHonorsCancellation()
    {
        Assert.Throws<ArgumentException>(() =>
            PdfOcrImagePreprocessor.CropBgra(new byte[3], 1, 1, 0, 0, 1, 1));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            PdfOcrImagePreprocessor.CropBgra(
                new byte[16], 2, 2, 0, 0, 1, 1, canceled.Token));
    }

    [Theory]
    [InlineData(90, 3, 2, new byte[] { 5, 3, 1, 6, 4, 2 })]
    [InlineData(180, 2, 3, new byte[] { 6, 5, 4, 3, 2, 1 })]
    [InlineData(270, 3, 2, new byte[] { 2, 4, 6, 1, 3, 5 })]
    public void RotateBgra_RotatesWholePixelsClockwise(
        int degrees, int expectedWidth, int expectedHeight, byte[] expected)
    {
        var bgra = new byte[2 * 3 * 4];
        for (int pixel = 0; pixel < 6; pixel++)
        {
            bgra[pixel * 4] = (byte)(pixel + 1);
            bgra[pixel * 4 + 3] = 255;
        }

        PdfOcrBgraImage rotated = PdfOcrImagePreprocessor.RotateBgra(
            bgra, 2, 3, degrees);

        Assert.Equal(expectedWidth, rotated.Width);
        Assert.Equal(expectedHeight, rotated.Height);
        Assert.Equal(expected,
            Enumerable.Range(0, 6).Select(pixel => rotated.Pixels.Span[pixel * 4]));
        Assert.All(Enumerable.Range(0, 6),
            pixel => Assert.Equal(255, rotated.Pixels.Span[pixel * 4 + 3]));
    }

    [Fact]
    public void RotateBgra_RejectsNonRightAnglesAndHonorsCancellation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfOcrImagePreprocessor.RotateBgra(new byte[4], 1, 1, 45));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            PdfOcrImagePreprocessor.RotateBgra(
                new byte[4], 1, 1, 90, canceled.Token));
    }
}
