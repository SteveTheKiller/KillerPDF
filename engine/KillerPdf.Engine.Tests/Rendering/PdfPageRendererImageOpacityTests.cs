using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererImageOpacityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_ImageOpacityMultipliesImageSoftMask(bool transparent)
    {
        var image = PdfImage.FromRgba(1, 1, new byte[] { 255, 0, 0, 128 });
        var content = new PdfContentStreamBuilder().SetOpacity(0.5, 1)
            .DrawImage(image, 0, 0, 1, 1);
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1, content).Build());
        PdfRenderedPage page = new PdfPageRenderer(document).Render(0,
            new PdfRenderOptions(1, 1, transparentBackground: transparent));

        Assert.Equal(transparent ? new byte[] { 0, 0, 255, 64 }
            : new byte[] { 191, 191, 255, 255 }, page.Pixels.ToArray());
        Assert.Empty(page.Diagnostics);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 0.5)]
    [InlineData(1, 1)]
    [InlineData(3, 0)]
    [InlineData(3, 0.5)]
    [InlineData(3, 1)]
    [InlineData(4, 0)]
    [InlineData(4, 0.5)]
    [InlineData(4, 1)]
    public void Render_ImageUsesNonstrokingOpacity(int components, double opacity)
    {
        PdfImage image = components switch
        {
            1 => PdfImage.FromGray(1, 1, new byte[] { 0 }),
            3 => PdfImage.FromRgb(1, 1, new byte[] { 0, 0, 0 }),
            _ => PdfImage.FromCmyk(1, 1, new byte[] { 0, 0, 0, 255 })
        };
        var content = new PdfContentStreamBuilder().SetOpacity(opacity, 1)
            .DrawImage(image, 0, 0, 1, 1)
            .SetFillGray(0).Rectangle(1, 0, 1, 1).Fill();
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1, content).Build());
        PdfRenderedPage page = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(2, 1));

        Assert.Equal(page.Pixels.Slice(4, 4).ToArray(), page.Pixels.Slice(0, 4).ToArray());
        Assert.Empty(page.Diagnostics);
    }
}
