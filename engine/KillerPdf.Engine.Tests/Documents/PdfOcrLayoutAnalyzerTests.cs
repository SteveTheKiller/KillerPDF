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
    public void Analyze_MergesDetachedMarksIntoTheirGlyphs()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 14 * 9).ToArray();
        Paint(pixels, 14, 3, 1, 2, 2);
        Paint(pixels, 14, 3, 4, 2, 4);
        Paint(pixels, 14, 9, 4, 3, 4);

        PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(
            Prepared(14, 9, pixels));

        Assert.Single(layout.Lines);
        Assert.Equal(2, layout.Components.Count);
        Assert.Equal(new PdfOcrImageRegion(3, 1, 5, 8), layout.Components[0]);
        Assert.Equal(new PdfOcrImageRegion(9, 4, 12, 8), layout.Components[1]);
    }

    [Fact]
    public void Analyze_SplitsTouchingWideGlyphsAtVerticalValleys()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 10 * 7).ToArray();
        Paint(pixels, 10, 1, 1, 2, 4);
        Paint(pixels, 10, 5, 1, 2, 4);
        pixels[2 * 10 + 3] = 0;
        pixels[2 * 10 + 4] = 0;

        PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(
            Prepared(10, 7, pixels));

        Assert.Single(layout.Lines);
        Assert.Equal(2, layout.Components.Count);
        Assert.Equal(new PdfOcrImageRegion(1, 1, 4, 5), layout.Components[0]);
        Assert.Equal(new PdfOcrImageRegion(4, 1, 7, 5), layout.Components[1]);
    }

    [Fact]
    public void Analyze_OrdersDetectedColumnsBeforeMovingRight()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 30 * 14).ToArray();
        Paint(pixels, 30, 1, 1, 2, 3);
        Paint(pixels, 30, 1, 8, 2, 3);
        Paint(pixels, 30, 22, 2, 2, 3);
        Paint(pixels, 30, 22, 9, 2, 3);

        PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(
            Prepared(30, 14, pixels), detectPageSegments: true);

        Assert.Equal(2, layout.Segments.Count);
        Assert.Equal([1, 1, 22, 22],
            layout.Lines.Select(line => line.Bounds.Left));
        Assert.Equal([1, 8, 2, 9],
            layout.Lines.Select(line => line.Bounds.Top));
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
