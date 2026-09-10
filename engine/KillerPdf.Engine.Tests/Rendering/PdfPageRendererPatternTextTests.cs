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
