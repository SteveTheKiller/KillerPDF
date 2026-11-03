using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionTextTests
{
    [Fact]
    public void EmbeddedGlyphFillsRetainSubByteGrayAndRgbSourceColors()
    {
        var content = new PdfContentStreamBuilder()
            .SetFillGray(0.5)
            .BeginText().SetFont(TestFont(), 10)
            .SetTextMatrix(1, 0, 0, 1, 2, 2).ShowUnicodeText("A")
            .SetFillRgb(0.5001, 0.5001, 0.5001)
            .SetTextMatrix(1, 0, 0, 1, 16, 2).ShowUnicodeText("A").EndText();
        var renderer = Renderer(content);
        var options = new PdfRenderOptions(30, 16, transparentBackground: true);

        var frame = renderer.RenderFrame(0, options);

        // The synthetic glyph is the triangle (0,0), (1000,0), (500,1000).
        // At 10 points these samples are wholly inside its two translated copies.
        AssertGray(frame, 7, 9, 0.21404113f, 0.21404116f);
        AssertGray(frame, 21, 9, 0.21413370f, 0.21413373f);
        Assert.Equal(0f, frame.Pixels[3]);
        AssertPaintedExtents(frame, 2, 4, 25, 13);
        Assert.Equal(renderer.Render(0, options).Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    [Fact]
    public void PositionedGlyphArrayPreservesPreciseColorAndPdfSpacing()
    {
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(0.5001, 0.5001, 0.5001)
            .BeginText().SetFont(TestFont(), 10)
            .SetTextMatrix(1, 0, 0, 1, 2, 2)
            .ShowPositionedUnicodeText(["A", "A"], [-800]).EndText();
        var renderer = Renderer(content);
        var options = new PdfRenderOptions(30, 16, transparentBackground: true);

        var frame = renderer.RenderFrame(0, options);

        // The 600-unit advance is six points, then -800 adds eight points.
        // The second glyph therefore begins at x=16, leaving this gap unpainted.
        AssertGray(frame, 7, 9, 0.21413370f, 0.21413373f);
        AssertGray(frame, 21, 9, 0.21413370f, 0.21413373f);
        Assert.Equal(0f, frame.Pixels[(9 * 30 + 14) * 4 + 3]);
        AssertPaintedExtents(frame, 2, 4, 25, 13);
        Assert.Equal(renderer.Render(0, options).Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    [Fact]
    public void StrokedGlyphsRetainSubByteColorsWithoutFillingTheirInteriors()
    {
        var content = new PdfContentStreamBuilder()
            .SetLineWidth(2).SetStrokeGray(0.5)
            .BeginText().SetFont(TestFont(), 10)
            .SetTextRenderingMode(PdfTextRenderingMode.Stroke)
            .SetTextMatrix(1, 0, 0, 1, 2, 2).ShowUnicodeText("A")
            .SetStrokeRgb(0.5001, 0.5001, 0.5001)
            .SetTextMatrix(1, 0, 0, 1, 16, 2).ShowUnicodeText("A").EndText();
        var renderer = Renderer(content);
        var options = new PdfRenderOptions(30, 16, transparentBackground: true);

        var frame = renderer.RenderFrame(0, options);

        // At each triangle's horizontal base, a two-point stroke covers this whole pixel.
        AssertGray(frame, 7, 14, 0.21404113f, 0.21404116f);
        AssertGray(frame, 21, 14, 0.21413370f, 0.21413373f);
        Assert.Equal(0f, frame.Pixels[(9 * 30 + 7) * 4 + 3]);
        Assert.Equal(0f, frame.Pixels[(9 * 30 + 21) * 4 + 3]);
        Assert.Equal(0f, frame.Pixels[3]);
        Assert.Equal(renderer.Render(0, options).Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    private static TrueTypeFont TestFont() => TrueTypeFont.Load(
        TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true));

    private static PdfPageRenderer Renderer(PdfContentStreamBuilder content) => new(PdfDocument.Open(
        new PdfDocumentBuilder().AddPage(30, 16, content).Build()));

    private static void AssertGray(PdfRenderedFrame frame, int x, int y, float minimum, float maximum)
    {
        int offset = (y * frame.Width + x) * 4;
        // Independent literal sRGB decoding ranges, before byte quantization.
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[offset + channel], minimum, maximum);
        Assert.Equal(1f, frame.Pixels[offset + 3]);
    }

    private static void AssertPaintedExtents(PdfRenderedFrame frame,
        int expectedLeft, int expectedTop, int expectedRight, int expectedBottom)
    {
        int left = frame.Width, top = frame.Height, right = -1, bottom = -1;
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
                if (frame.Pixels[(y * frame.Width + x) * 4 + 3] > 0)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
        Assert.Equal((expectedLeft, expectedTop, expectedRight, expectedBottom),
            (left, top, right, bottom));
    }
}
