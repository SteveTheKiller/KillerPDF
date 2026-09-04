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
}
