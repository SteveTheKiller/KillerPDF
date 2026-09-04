using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Rendering;
using System.Text;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererTests
{
    [Fact]
    public void Render_BlankPageProducesOpaqueWhiteBgra()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(10, 20).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(2, 4, includeAnnotations: false, includeFormFields: false));

        Assert.Equal(2, page.Width);
        Assert.Equal(4, page.Height);
        Assert.Equal(32, page.Pixels.Length);
        Assert.All(page.Pixels.ToArray().Chunk(4), pixel =>
            Assert.Equal([255, 255, 255, 255], pixel));
        Assert.Empty(page.Diagnostics);
    }

    [Fact]
    public void Render_TransformsAndFillsRgbRectangle()
    {
        byte[] content = "q 1 0 0 1 2 3 cm 1 0 0 rg 1 1 2 2 re f Q"u8.ToArray();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 3, 4));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 0, 0));
    }

    [Fact]
    public void OptionsRejectUnboundedPixelBuffers()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfRenderOptions(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PdfRenderOptions(PdfRenderOptions.MaximumDimension + 1, 1));
        Assert.Throws<ArgumentException>(() => new PdfRenderOptions(20_000, 20_000));
    }

    [Fact]
    public void Render_FillsGeneralPathAndStrokesCurve()
    {
        byte[] content = "0 1 0 rg 1 1 m 8 1 l 4 8 l h f 0 0 1 RG 1 w 1 9 m 3 5 7 5 9 9 c S"u8.ToArray();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 255, 0, 255], Pixel(page, 4, 7));
        Assert.Equal([255, 0, 0, 255], Pixel(page, 1, 1));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 0, 9));
    }

    [Fact]
    public void Render_DecodesAndTransformsRgbImageXObject()
    {
        PdfImage image = PdfImage.FromRgb(2, 1, new byte[]
        {
            255, 0, 0,
            0, 255, 0
        });
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(image, 2, 3, 6, 2))
            .Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 3, 6));
        Assert.Equal([0, 255, 0, 255], Pixel(page, 7, 6));
        Assert.DoesNotContain("Image rendering is not implemented.", page.Diagnostics);
    }

    [Fact]
    public void Render_FillsAndStrokesPathsAndSupportsCurveShorthands()
    {
        byte[] content = "1 0 0 rg 0 0 1 RG 1 w 2 2 4 4 re B 1 8 m 3 6 5 8 v 5 8 m 7 6 9 8 y S"u8.ToArray();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 4, 6));
        Assert.Equal([255, 0, 0, 255], Pixel(page, 2, 7));
        Assert.NotEqual([255, 255, 255, 255], Pixel(page, 3, 3));
        Assert.NotEqual([255, 255, 255, 255], Pixel(page, 7, 3));
    }

    [Fact]
    public void Render_UsesCropOriginAndClockwisePageRotation()
    {
        byte[] content = "1 0 0 rg 10 5 5 5 re f"u8.ToArray();
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(20, 15, content).Build());
        PdfDocument document = PdfDocument.Open(new PdfIncrementalPageEditor(source)
            .SetCropBox(0, 10, 5, 10, 10)
            .SetRotation(0, 90)
            .Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 2, 2));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 7, 7));
    }

    [Fact]
    public void Render_IntersectsNestedGraphicsStateClippingPaths()
    {
        byte[] content = "2 2 6 6 re W n q 4 0 4 10 re W* n 1 0 0 rg 0 0 10 10 re f Q 0 0 1 rg 0 0 2 2 re f"u8.ToArray();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 5, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 3, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 8, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 1, 8));
    }

    [Fact]
    public void Render_DecodesInlineRgbImages()
    {
        byte[] prefix = Encoding.ASCII.GetBytes(
            "q 4 0 0 2 2 3 cm BI /W 2 /H 1 /BPC 8 /CS /RGB ID ");
        byte[] suffix = Encoding.ASCII.GetBytes(" EI Q");
        byte[] content = [.. prefix, 255, 0, 0, 0, 255, 0, .. suffix];
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 2, 6));
        Assert.Equal([0, 255, 0, 255], Pixel(page, 5, 6));
        Assert.DoesNotContain("Inline-image rendering is not implemented.", page.Diagnostics);
    }

    [Fact]
    public void Render_DistinguishesNonzeroAndEvenOddCompoundFills()
    {
        byte[] content = "1 0 0 rg 1 1 8 8 re 3 3 4 4 re f 0 0 1 rg 11 1 8 8 re 13 3 4 4 re f*"u8.ToArray();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(20, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(20, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 5, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 15, 5));
        Assert.Equal([255, 0, 0, 255], Pixel(page, 12, 5));
    }

    [Fact]
    public void Render_CompositesGraphicsStateOpacityOverTheBackground()
    {
        var content = new PdfContentStreamBuilder()
            .SetOpacity(0.5)
            .SetFillRgb(1, 0, 0)
            .Rectangle(0, 0, 10, 10)
            .Fill();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage opaque = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));
        PdfRenderedPage transparent = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, transparentBackground: true,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([128, 128, 255, 255], Pixel(opaque, 5, 5));
        Assert.Equal([0, 0, 255, 128], Pixel(transparent, 5, 5));
    }

    private static byte[] Pixel(PdfRenderedPage page, int x, int y) =>
        page.Pixels.Slice((y * page.Width + x) * 4, 4).ToArray();
}
