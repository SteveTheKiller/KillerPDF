using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererCmykTests
{
    [Fact]
    public void Render_MixedCmykInkRetainsShadowDetailInImagesAndPaths()
    {
        byte[] samples = [192, 128, 64, 128];
        var content = new PdfContentStreamBuilder()
            .SetFillCmyk(192 / 255d, 128 / 255d, 64 / 255d, 128 / 255d)
            .Rectangle(0, 0, 1, 1).Fill()
            .DrawImage(PdfImage.FromCmyk(1, 1, samples), 1, 0, 1, 1);
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(2, 1));

        Assert.Equal(new byte[] { 95, 63, 31, 255, 95, 63, 31, 255 }, page.Pixels.ToArray());
        Assert.Empty(page.Diagnostics);
    }

    [Fact]
    public void Render_BlackInkDoesNotFlattenTheRemainingCyanRamp()
    {
        byte[] samples = [0, 0, 0, 128, 64, 0, 0, 128, 128, 0, 0, 128, 192, 0, 0, 128];
        var content = new PdfContentStreamBuilder().DrawImage(PdfImage.FromCmyk(4, 1, samples), 0, 0, 4, 1);
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(4, 1, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(4, 1));

        Assert.Equal(new byte[] { 127, 95, 63, 31 },
            Enumerable.Range(0, 4).Select(x => page.Pixels.Span[x * 4 + 2]).ToArray());
        Assert.Empty(page.Diagnostics);
    }
}
