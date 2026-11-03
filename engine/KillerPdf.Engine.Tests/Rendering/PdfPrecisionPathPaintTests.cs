using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionPathPaintTests
{
    [Fact]
    public void CombinedFillAndStrokeRetainsDistinctSubByteSourceColors()
    {
        var renderer = Renderer("2 w 0.5 0.5 0.5 rg 0.5001 0.5001 0.5001 RG 2 2 6 4 re B");

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(10, 8, transparentBackground: true));

        // Interior and left stroke samples have full coverage, away from corners.
        // Independent sRGB decoding gives these literal linear channel ranges.
        AssertGray(frame, 5, 4, 0.21404113f, 0.21404116f);
        AssertGray(frame, 2, 4, 0.21413370f, 0.21413373f);
        Assert.Equal(0f, frame.Pixels[3]);
        Assert.Equal(renderer.Render(0, new PdfRenderOptions(10, 8, transparentBackground: true)).Pixels.ToArray(),
            frame.ToSrgbBgra32());
    }

    [Fact]
    public void CloseFillAndStrokePaintsTheImplicitClosingEdgePrecisely()
    {
        var renderer = Renderer("2 w 0.5 0.5 0.5 rg 0.5001 0.5001 0.5001 RG "
            + "2 2 m 8 2 l 8 6 l 2 6 l b");

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(10, 8, transparentBackground: true));

        AssertGray(frame, 5, 4, 0.21404113f, 0.21404116f);
        AssertGray(frame, 2, 4, 0.21413370f, 0.21413373f);
        Assert.Equal(0f, frame.Pixels[3]);
        Assert.Equal(renderer.Render(0, new PdfRenderOptions(10, 8, transparentBackground: true)).Pixels.ToArray(),
            frame.ToSrgbBgra32());
    }

    [Fact]
    public void EvenOddFillAndStrokeLeavesTheNestedInteriorTransparent()
    {
        var renderer = Renderer("2 w 0.5 0.5 0.5 rg 0.5001 0.5001 0.5001 RG "
            + "2 2 16 12 re 6 6 8 4 re B*", 20, 16);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(20, 16, transparentBackground: true));

        // Both rectangles wind the same way, so only even-odd filling leaves a hole.
        AssertGray(frame, 4, 8, 0.21404113f, 0.21404116f);
        AssertGray(frame, 6, 8, 0.21413370f, 0.21413373f);
        Assert.Equal(0f, frame.Pixels[(8 * 20 + 10) * 4 + 3]);
        Assert.Equal(0f, frame.Pixels[3]);
        Assert.Equal(renderer.Render(0, new PdfRenderOptions(20, 16, transparentBackground: true)).Pixels.ToArray(),
            frame.ToSrgbBgra32());
    }

    [Fact]
    public void CloseEvenOddFillAndStrokeClosesTheNestedEdgeAndPreservesTheHole()
    {
        var renderer = Renderer("2 w 0.5 0.5 0.5 rg 0.5001 0.5001 0.5001 RG "
            + "2 2 16 12 re 6 6 m 14 6 l 14 10 l 6 10 l b*", 20, 16);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(20, 16, transparentBackground: true));

        AssertGray(frame, 4, 8, 0.21404113f, 0.21404116f);
        AssertGray(frame, 6, 8, 0.21413370f, 0.21413373f);
        Assert.Equal(0f, frame.Pixels[(8 * 20 + 10) * 4 + 3]);
        Assert.Equal(0f, frame.Pixels[3]);
        Assert.Equal(renderer.Render(0, new PdfRenderOptions(20, 16, transparentBackground: true)).Pixels.ToArray(),
            frame.ToSrgbBgra32());
    }

    [Fact]
    public void ClosedStrokesRetainSubByteDifferencesWithoutFillingTheirInteriors()
    {
        var renderer = Renderer("2 w 0.5 0.5 0.5 RG 2 2 m 8 2 l 8 6 l 2 6 l s "
            + "0.5001 0.5001 0.5001 RG 12 2 m 18 2 l 18 6 l 12 6 l s", 20, 8);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(20, 8, transparentBackground: true));

        AssertGray(frame, 2, 4, 0.21404113f, 0.21404116f);
        AssertGray(frame, 12, 4, 0.21413370f, 0.21413373f);
        Assert.Equal(0f, frame.Pixels[(4 * 20 + 5) * 4 + 3]);
        Assert.Equal(0f, frame.Pixels[(4 * 20 + 15) * 4 + 3]);
        Assert.Equal(0f, frame.Pixels[3]);
        Assert.Equal(renderer.Render(0, new PdfRenderOptions(20, 8, transparentBackground: true)).Pixels.ToArray(),
            frame.ToSrgbBgra32());
    }

    private static PdfPageRenderer Renderer(string content, int width = 10, int height = 8) => new(PdfDocument.Open(
        new PdfDocumentBuilder().AddPage(width, height, Encoding.ASCII.GetBytes(content)).Build()));

    private static void AssertGray(PdfRenderedFrame frame, int x, int y, float minimum, float maximum)
    {
        int offset = (y * frame.Width + x) * 4;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[offset + channel], minimum, maximum);
        Assert.Equal(1f, frame.Pixels[offset + 3]);
    }
}
