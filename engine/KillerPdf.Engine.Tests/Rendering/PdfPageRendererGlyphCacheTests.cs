using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererGlyphCacheTests
{
    private static PdfDocument TextPage(double width, double height, PdfStandardFont font,
        double size, double x, double y, string text) =>
        PdfDocument.Open(new PdfDocumentBuilder().AddPage(width, height,
            new PdfContentStreamBuilder().BeginText().SetFont(font, size)
                .SetTextMatrix(1, 0, 0, 1, x, y).ShowLatin1Text(text).EndText()).Build());

    private static PdfRenderedPage Render(PdfDocument document, int width, int height,
        bool useCache, out PdfPageRenderer renderer)
    {
        renderer = new PdfPageRenderer(document) { UseGlyphMaskCache = useCache };
        return renderer.Render(0, new PdfRenderOptions(width, height,
            includeAnnotations: false, includeFormFields: false));
    }

    private static (int Differing, int MaximumDelta) Compare(PdfRenderedPage first,
        PdfRenderedPage second)
    {
        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);
        ReadOnlySpan<byte> a = first.Pixels.Span, b = second.Pixels.Span;
        int differing = 0, maximum = 0;
        for (int offset = 0; offset < a.Length; offset += 4)
        {
            int delta = 0;
            for (int channel = 0; channel < 4; channel++)
                delta = Math.Max(delta, Math.Abs(a[offset + channel] - b[offset + channel]));
            if (delta == 0) continue;
            differing++;
            maximum = Math.Max(maximum, delta);
        }
        return (differing, maximum);
    }

    [Fact]
    public void Render_CachedGlyphsStayWithinSubpixelToleranceOfDirectFills()
    {
        // Fractional origin and a non-integer scale exercise the quarter-pixel quantization.
        PdfDocument document = TextPage(120, 30, PdfStandardFont.TimesRoman, 9, 3.37, 10.61,
            "Quick brown foxes jump");
        PdfRenderedPage direct = Render(document, 300, 75, useCache: false, out _);
        PdfRenderedPage cached = Render(document, 300, 75, useCache: true, out PdfPageRenderer renderer);

        (int differing, int maximumDelta) = Compare(direct, cached);
        int inked = 0;
        ReadOnlySpan<byte> pixels = direct.Pixels.Span;
        for (int offset = 0; offset < pixels.Length; offset += 4)
            if (pixels[offset] != 255) inked++;

        Assert.True(inked > 500, $"Expected painted text, found {inked} inked pixels.");
        // A cached glyph sits at most one eighth of a pixel from its exact position, so only
        // antialiased edge pixels may change and none of them by more than a quarter step.
        Assert.True(differing <= inked, $"{differing} pixels changed for {inked} inked pixels.");
        Assert.True(maximumDelta <= 96, $"Maximum channel delta was {maximumDelta}.");
        Assert.True(renderer.GlyphMaskCacheCount > 0);
    }

    [Fact]
    public void Render_CachedGlyphsAtWholePixelOriginsMatchDirectFills()
    {
        // Courier advances 600 units, so 10 pt at 2x puts every glyph origin on a whole pixel.
        // Those land on the quantization grid, so only floating-point rounding of the two
        // transform paths can differ.
        PdfDocument document = TextPage(80, 20, PdfStandardFont.CourierBold, 10, 4, 5, "Hamburg");
        PdfRenderedPage direct = Render(document, 160, 40, useCache: false, out _);
        PdfRenderedPage cached = Render(document, 160, 40, useCache: true, out _);

        (_, int maximumDelta) = Compare(direct, cached);
        Assert.True(maximumDelta <= 2, $"Maximum channel delta was {maximumDelta}.");
    }

    [Fact]
    public void Render_CachedGlyphsRepeatExactlyAtWholePixelAdvances()
    {
        // Courier advances 600 units: 10 pt at 2x is exactly 12 device pixels per glyph.
        PdfDocument document = TextPage(60, 20, PdfStandardFont.Courier, 10, 4, 6, "IIII");
        PdfRenderedPage rendered = Render(document, 120, 40, useCache: true,
            out PdfPageRenderer renderer);

        int stride = rendered.Width * 4;
        ReadOnlySpan<byte> pixels = rendered.Pixels.Span;
        int inked = 0;
        for (int y = 0; y < rendered.Height; y++)
            for (int x = 8; x < 20; x++)
            {
                int offset = y * stride + x * 4;
                if (pixels[offset] != 255) inked++;
                for (int copy = 1; copy < 4; copy++)
                    Assert.Equal(pixels.Slice(offset, 4).ToArray(),
                        pixels.Slice(offset + copy * 12 * 4, 4).ToArray());
            }
        Assert.True(inked > 20, $"Expected a painted glyph, found {inked} inked pixels.");
        // The same mask served every occurrence.
        Assert.Equal(1, renderer.GlyphMaskCacheCount);
    }

    [Theory]
    [InlineData(0, 1, -1, 0)]
    [InlineData(-1, 0, 0, 1)]
    [InlineData(1, 0.5, 0.25, 1)]
    [InlineData(0.5, 0, 0, 1.5)]
    public void Render_TransformedCachedGlyphsMatchDirectFills(
        double a, double b, double c, double d)
    {
        var content = new PdfContentStreamBuilder().BeginText()
            .SetFont(PdfStandardFont.HelveticaBold, 24)
            .SetTextMatrix(a, b, c, d, 40, 40).ShowLatin1Text("B").EndText();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(80, 80, content).Build());
        PdfRenderedPage direct = Render(document, 160, 160, useCache: false, out _);
        PdfRenderedPage cached = Render(document, 160, 160, useCache: true,
            out PdfPageRenderer renderer);

        (_, int maximumDelta) = Compare(direct, cached);
        Assert.True(maximumDelta <= 2, $"Maximum channel delta was {maximumDelta}.");
        Assert.True(renderer.GlyphMaskCacheCount > 0);
        Assert.Contains(cached.Pixels.ToArray(), value => value < 255);
    }

    [Fact]
    public void Render_LargeGlyphsBypassTheCacheAndMatchDirectFillsExactly()
    {
        PdfDocument document = TextPage(200, 200, PdfStandardFont.Helvetica, 220, 10, 30, "W");
        PdfRenderedPage direct = Render(document, 200, 200, useCache: false, out _);
        PdfRenderedPage cached = Render(document, 200, 200, useCache: true, out _);

        // The cache records the glyph as too large and the direct fill path paints it.
        Assert.Equal(direct.Pixels.ToArray(), cached.Pixels.ToArray());
    }

    [Fact]
    public void Render_TextClipUsesTheSameCachedCoverage()
    {
        var content = new PdfContentStreamBuilder().SaveState().BeginText()
            .SetFont(PdfStandardFont.HelveticaBold, 24).SetTextRenderingMode(PdfTextRenderingMode.Clip)
            .SetTextMatrix(1, 0, 0, 1, 5.5, 8.25).ShowLatin1Text("HH").EndText()
            .SetFillRgb(0, 0, 1).Rectangle(0, 0, 60, 40).Fill().RestoreState();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(60, 40, content).Build());
        PdfRenderedPage direct = Render(document, 120, 80, useCache: false, out _);
        PdfRenderedPage cached = Render(document, 120, 80, useCache: true,
            out PdfPageRenderer renderer);

        (int differing, int maximumDelta) = Compare(direct, cached);
        int blue = 0;
        ReadOnlySpan<byte> pixels = cached.Pixels.Span;
        for (int offset = 0; offset < pixels.Length; offset += 4)
            if (pixels[offset + 2] == 0 && pixels[offset] == 255) blue++;

        Assert.True(blue > 100, $"Expected clipped blue text, found {blue} blue pixels.");
        Assert.True(maximumDelta <= 96, $"Maximum channel delta was {maximumDelta}.");
        Assert.True(differing < blue, $"{differing} pixels changed for {blue} blue pixels.");
        Assert.InRange(renderer.GlyphMaskCacheCount, 1, 2);
    }
}
