using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
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

    private static byte[] Pixel(PdfRenderedPage page, int x, int y) =>
        page.Pixels.Slice((y * page.Width + x) * 4, 4).ToArray();
}
