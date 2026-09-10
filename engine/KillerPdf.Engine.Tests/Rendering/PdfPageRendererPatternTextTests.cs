using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererPatternTextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_TransparentPatternRetainsBackdropForInternalBlending(bool multiply)
    {
        var cell = new PdfContentStreamBuilder().SetGraphicsState(new PdfGraphicsState(
            fillOpacity: 0.5, strokeOpacity: 0.5,
            blendMode: multiply ? PdfBlendMode.Multiply : PdfBlendMode.Normal));
        cell.SetFillRgb(1, 0, 0).Rectangle(0, 0, 10, 10).Fill();
        var pattern = new PdfTilingPattern(10, 10, cell);
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(10, 10,
            new PdfContentStreamBuilder().SetFillRgb(0, 0, 1).Rectangle(0, 0, 10, 10).Fill()
                .SetOpacity(0.5).SetFillPattern(pattern).Rectangle(0, 0, 10, 10).Fill()).Build());
        PdfRenderedPage page = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(10, 10));
        byte[] pixel = page.Pixels.Slice((5 * 10 + 5) * 4, 4).ToArray();
        Assert.InRange(pixel[0], 190, 192);
        Assert.Equal(0, pixel[1]);
        Assert.InRange(pixel[2], multiply ? 0 : 63, multiply ? 0 : 65);
        Assert.Equal(255, pixel[3]);
        Assert.Empty(page.Diagnostics);
    }

    [Fact]
    public void Render_TilingPatternAppliesOuterOpacityOnceToOverlappingMarks()
    {
        var pattern = new PdfTilingPattern(10, 10, new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0).Rectangle(0, 0, 8, 10).Fill()
            .Rectangle(2, 0, 8, 10).Fill());
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(10, 10,
            new PdfContentStreamBuilder().SetOpacity(0.5).SetFillPattern(pattern)
                .Rectangle(0, 0, 10, 10).Fill()).Build());
        PdfRenderedPage page = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(10, 10));
        Assert.Equal(new byte[] { 128, 128, 255, 255 }, page.Pixels.Slice((5 * 10 + 5) * 4, 4).ToArray());
        Assert.Equal(page.Pixels.Slice((5 * 10) * 4, 4).ToArray(),
            page.Pixels.Slice((5 * 10 + 5) * 4, 4).ToArray());
        Assert.Empty(page.Diagnostics);
    }

    [Fact]
    public void Render_TilingPatternStrokeUsesStrokeOpacity()
    {
        var pattern = new PdfTilingPattern(10, 10, new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0).Rectangle(0, 0, 10, 10).Fill());
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(10, 10,
            new PdfContentStreamBuilder().SetOpacity(0.75, 0.25).SetStrokePattern(pattern)
                .SetLineWidth(4).MoveTo(0, 5).LineTo(10, 5).Stroke()).Build());
        PdfRenderedPage page = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(10, 10));
        Assert.Equal(new byte[] { 191, 191, 255, 255 }, page.Pixels.Slice((5 * 10 + 5) * 4, 4).ToArray());
        Assert.Empty(page.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_PatternStrokeStaysInsideGlyphOutline(bool fill)
    {
        var pattern = new PdfTilingPattern(8, 8, new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0).Rectangle(0, 0, 4, 8).Fill());
        PdfRenderedPage Render(bool patterned)
        {
            var content = new PdfContentStreamBuilder().SetLineWidth(3).SetFillRgb(0, 1, 0);
            if (patterned) content.SetStrokePattern(pattern);
            else content.SetStrokeRgb(0, 0, 0);
            content.BeginText().SetFont(PdfStandardFont.HelveticaBold, 70)
                .SetTextRenderingMode(fill ? PdfTextRenderingMode.FillAndStroke : PdfTextRenderingMode.Stroke)
                .SetTextMatrix(1, 0, 0, 1, 15, 15).ShowLatin1Text("O").EndText();
            var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100, content).Build());
            return new PdfPageRenderer(document).Render(0, new PdfRenderOptions(200, 200,
                includeAnnotations: false, includeFormFields: false));
        }

        PdfRenderedPage patterned = Render(true), solid = Render(false);
        ReadOnlySpan<byte> actual = patterned.Pixels.Span, reference = solid.Pixels.Span;
        int red = 0, gaps = 0, green = 0;
        for (int offset = 0; offset < actual.Length; offset += 4)
        {
            if (actual[offset] < 100 && actual[offset + 1] < 100 && actual[offset + 2] > 200) red++;
            if (reference[offset] == 0 && reference[offset + 1] == 0 && actual[offset + 1] == 255) gaps++;
            if (actual[offset] == 0 && actual[offset + 1] == 255 && actual[offset + 2] == 0) green++;
            if (reference[offset] == 255)
                Assert.Equal(255, actual[offset]);
        }
        Assert.True(red > 100, $"Expected red stroke pattern, found {red} pixels.");
        Assert.True(gaps > 100, $"Expected gaps in the patterned stroke, found {gaps} pixels.");
        Assert.Equal(fill, green > 100);
        Assert.Empty(patterned.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_PatternTextStaysInsideGlyph(bool useCache)
    {
        var pattern = new PdfTilingPattern(8, 8, new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0).Rectangle(0, 0, 4, 8).Fill());
        PdfRenderedPage Render(bool patterned)
        {
            var content = new PdfContentStreamBuilder();
            if (patterned) content.SetFillPattern(pattern);
            else content.SetFillRgb(0, 0, 0);
            content.BeginText().SetFont(PdfStandardFont.HelveticaBold, 70)
                .SetTextMatrix(1, 0, 0, 1, 15, 15).ShowLatin1Text("O").EndText();
            var document = PdfDocument.Open(new PdfDocumentBuilder()
                .AddPage(100, 100, content).Build());
            return new PdfPageRenderer(document) { UseGlyphMaskCache = useCache }
                .Render(0, new PdfRenderOptions(200, 200,
                    includeAnnotations: false, includeFormFields: false));
        }

        PdfRenderedPage patterned = Render(true), solid = Render(false);
        ReadOnlySpan<byte> actual = patterned.Pixels.Span, mask = solid.Pixels.Span;
        int red = 0, gaps = 0;
        for (int offset = 0; offset < actual.Length; offset += 4)
        {
            if (actual[offset] < 100 && actual[offset + 1] < 100 && actual[offset + 2] > 200)
                red++;
            if (mask[offset] == 0 && actual[offset] == 255) gaps++;
            if (mask[offset] == 255)
                Assert.Equal(255, actual[offset]);
        }
        Assert.True(red > 100, $"Expected red pattern ink, found {red} pixels.");
        Assert.True(gaps > 100, $"Expected gaps inside the glyph, found {gaps} pixels.");
        Assert.Empty(patterned.Diagnostics);
    }
}
