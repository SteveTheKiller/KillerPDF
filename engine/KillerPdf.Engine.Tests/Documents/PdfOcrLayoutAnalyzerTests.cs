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
    public void Analyze_SplitsProportionalWordsAtOrdinarySpaceWidths()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 60 * 12).ToArray();
        Paint(pixels, 60, 1, 2, 10, 6);
        Paint(pixels, 60, 13, 2, 10, 6);
        Paint(pixels, 60, 29, 2, 10, 6);
        Paint(pixels, 60, 41, 2, 10, 6);

        PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(Prepared(60, 12, pixels));

        Assert.Single(layout.Lines);
        Assert.Equal(2, layout.Words.Count);
        Assert.Equal(new PdfOcrImageRegion(1, 2, 23, 8), layout.Words[0].Bounds);
        Assert.Equal(new PdfOcrImageRegion(29, 2, 51, 8), layout.Words[1].Bounds);
    }

    [Fact]
    public void Analyze_SplitsSmallTextAtThreePixelWordGaps()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 36 * 10).ToArray();
        Paint(pixels, 36, 1, 2, 6, 6);
        Paint(pixels, 36, 8, 2, 6, 6);
        Paint(pixels, 36, 17, 2, 6, 6);
        Paint(pixels, 36, 24, 2, 6, 6);

        PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(
            Prepared(36, 10, pixels));

        Assert.Single(layout.Lines);
        Assert.Equal(2, layout.Words.Count);
        Assert.Equal(2, layout.Words[0].Components.Count);
        Assert.Equal(2, layout.Words[1].Components.Count);
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
    public void Analyze_RetainsSinglePixelPunctuationNearText()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 14 * 9).ToArray();
        Paint(pixels, 14, 3, 3, 2, 5);
        pixels[1 * 14 + 3] = 0;
        Paint(pixels, 14, 9, 3, 3, 5);
        pixels[7 * 14 + 13] = 0;

        PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(
            Prepared(14, 9, pixels));

        Assert.Equal(3, layout.Components.Count);
        Assert.Contains(new PdfOcrImageRegion(3, 1, 5, 8), layout.Components);
        Assert.Contains(new PdfOcrImageRegion(13, 7, 14, 8), layout.Components);
    }

    [Fact]
    public void Analyze_BoundsDenseSinglePixelNoise()
    {
        const int size = 210;
        byte[] pixels = Enumerable.Repeat((byte)255, size * size).ToArray();
        Paint(pixels, size, 1, 1, 2, 5);
        for (int y = 10; y < size; y += 3)
            for (int x = 0; x < size; x += 3)
                pixels[y * size + x] = 0;

        PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(
            Prepared(size, size, pixels));

        Assert.Equal(new PdfOcrImageRegion(1, 1, 3, 6),
            Assert.Single(layout.Components));
    }

    [Fact]
    public void Analyze_RejectsExcessiveConnectedComponentNoise()
    {
        const int width = 384;
        const int height = 387;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (int y = 0; y < height; y += 3)
            for (int x = 0; x + 1 < width; x += 3)
            {
                pixels[y * width + x] = 0;
                pixels[y * width + x + 1] = 0;
            }

        Assert.Throws<InvalidOperationException>(() =>
            PdfOcrLayoutAnalyzer.Analyze(Prepared(width, height, pixels)));
    }

    [Fact]
    public void Analyze_KeepsDiagonalStrokesConnected()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 8 * 8).ToArray();
        for (int offset = 0; offset < 5; offset++)
            pixels[(offset + 1) * 8 + offset + 1] = 0;

        PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(
            Prepared(8, 8, pixels));

        Assert.Equal(new PdfOcrImageRegion(1, 1, 6, 6),
            Assert.Single(layout.Components));
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
    public void Analyze_MergesAlignedPunctuationStrokes()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 14 * 9).ToArray();
        Paint(pixels, 14, 2, 1, 2, 2);
        Paint(pixels, 14, 2, 5, 2, 2);
        Paint(pixels, 14, 9, 2, 3, 5);

        PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(
            Prepared(14, 9, pixels));

        Assert.Single(layout.Lines);
        Assert.Equal(2, layout.Components.Count);
        Assert.Equal(new PdfOcrImageRegion(2, 1, 4, 7), layout.Components[0]);
        Assert.Equal(new PdfOcrImageRegion(9, 2, 12, 7), layout.Components[1]);
    }

    [Fact]
    public void Analyze_MergesColonDotsAcrossTheTextHeight()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 20 * 14).ToArray();
        Paint(pixels, 20, 2, 1, 2, 2);
        Paint(pixels, 20, 2, 8, 2, 2);
        Paint(pixels, 20, 10, 1, 3, 10);

        PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(
            Prepared(20, 14, pixels));

        Assert.Equal(2, layout.Components.Count);
        Assert.Contains(new PdfOcrImageRegion(2, 1, 4, 10), layout.Components);
    }

    [Fact]
    public void Analyze_IgnoresRuleLinesAndExtremeGraphicStrokes()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 80 * 80).ToArray();
        Paint(pixels, 80, 2, 10, 3, 3);
        Paint(pixels, 80, 2, 15, 3, 3);
        foreach (int x in new[] { 20, 28, 36, 44, 52 })
            Paint(pixels, 80, x, 10, 3, 5);
        Paint(pixels, 80, 70, 10, 2, 50);
        Paint(pixels, 80, 10, 70, 50, 2);
        Paint(pixels, 80, 30, 30, 20, 35);

        PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(
            Prepared(80, 80, pixels));

        Assert.Equal(8, layout.Components.Count);
        Assert.Contains(new PdfOcrImageRegion(2, 10, 5, 13), layout.Components);
        Assert.Contains(new PdfOcrImageRegion(2, 15, 5, 18), layout.Components);
        Assert.Contains(new PdfOcrImageRegion(30, 30, 50, 65), layout.Components);
        Assert.DoesNotContain(new PdfOcrImageRegion(70, 10, 72, 60), layout.Components);
        Assert.DoesNotContain(new PdfOcrImageRegion(10, 70, 60, 72), layout.Components);
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
    public void Analyze_OrdersThreeDetectedColumnsIndependently()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 42 * 14).ToArray();
        foreach (int left in new[] { 1, 15, 29 })
        {
            Paint(pixels, 42, left, 1, 2, 3);
            Paint(pixels, 42, left, 8, 2, 3);
        }

        PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(
            Prepared(42, 14, pixels), detectPageSegments: true);

        Assert.Equal(3, layout.Segments.Count);
        Assert.Equal([1, 1, 15, 15, 29, 29],
            layout.Lines.Select(line => line.Bounds.Left));
        Assert.Equal([1, 8, 1, 8, 1, 8],
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
