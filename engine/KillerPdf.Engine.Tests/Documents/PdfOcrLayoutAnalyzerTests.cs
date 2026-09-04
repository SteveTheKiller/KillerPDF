using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOcrLayoutAnalyzerTests
{
    [Fact]
    public void Analyze_GroupsComponentsIntoTopToBottomLines()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 12 * 8).ToArray();
        Paint(pixels, 12, 1, 1, 2, 3);
        Paint(pixels, 12, 5, 1, 2, 3);
        Paint(pixels, 12, 2, 6, 2, 2);
        var image = Prepared(12, 8, pixels);

        PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(image);

        Assert.Equal(2, layout.Lines.Count);
        Assert.Equal(2, layout.Lines[0].Components.Count);
        Assert.Single(layout.Lines[0].Words);
        Assert.Equal(1, layout.Lines[0].Components[0].Left);
        Assert.Equal(5, layout.Lines[0].Components[1].Left);
        Assert.Equal(6, layout.Lines[1].Bounds.Top);
    }

    [Fact]
    public void Analyze_SplitsWordsAtGlyphScaledHorizontalGaps()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 24 * 6).ToArray();
        Paint(pixels, 24, 1, 1, 2, 3);
        Paint(pixels, 24, 5, 1, 2, 3);
        Paint(pixels, 24, 14, 1, 2, 3);
        Paint(pixels, 24, 18, 1, 2, 3);

        PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(Prepared(24, 6, pixels));

        Assert.Single(layout.Lines);
        Assert.Equal(2, layout.Words.Count);
        Assert.Equal(new PdfOcrImageRegion(1, 1, 7, 4), layout.Words[0].Bounds);
        Assert.Equal(new PdfOcrImageRegion(14, 1, 20, 4), layout.Words[1].Bounds);
    }

    [Fact]
    public void Analyze_IgnoresSinglePixelNoiseAndHonorsCancellation()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 16).ToArray();
        pixels[5] = 0;
        Assert.Empty(PdfOcrLayoutAnalyzer.Analyze(Prepared(4, 4, pixels)).Components);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            PdfOcrLayoutAnalyzer.Analyze(Prepared(4, 4, pixels), canceled.Token));
    }

    private static PdfOcrPreparedImage Prepared(int width, int height, byte[] gray)
    {
        byte[] bgra = new byte[gray.Length * 4];
        for (int i = 0; i < gray.Length; i++)
        {
            bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = gray[i];
            bgra[i * 4 + 3] = 255;
        }
        var options = new PdfOcrOptions(["eng"], deskew: false,
            correctOrientation: false, detectPageSegments: false);
        return PdfOcrImagePreprocessor.PrepareBgra(bgra, width, height, options);
    }

    private static void Paint(byte[] pixels, int stride, int left, int top, int width, int height)
    {
        for (int y = top; y < top + height; y++)
            for (int x = left; x < left + width; x++) pixels[y * stride + x] = 0;
    }
}
