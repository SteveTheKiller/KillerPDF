using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using System.Text;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererCmykTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_CmykImageMaskCombinesWithGroupOpacity(bool transparent)
    {
        var image = PdfImage.FromCmyka(3, 1,
            new byte[] { 0, 255, 0, 0, 0, 0, 255, 0, 0, 128, 0, 255, 0, 0, 255 });
        var form = new PdfFormXObject(3, 1, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 3, 1),
            isolatedTransparencyGroup: true, transparencyGroupColorSpace: PdfTransparencyGroupColorSpace.Cmyk);
        var content = new PdfContentStreamBuilder().SetOpacity(0.5).DrawForm(form, 0, 0);
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(3, 1, content).Build());
        PdfRenderedPage page = new PdfPageRenderer(document).Render(0,
            new PdfRenderOptions(3, 1, transparentBackground: transparent));

        Assert.Equal(transparent
            ? new byte[] { 255, 255, 255, 0, 255, 0, 255, 64, 255, 0, 255, 128 }
            : new byte[] { 255, 255, 255, 255, 255, 191, 255, 255, 255, 128, 255, 255 },
            page.Pixels.ToArray());
        Assert.Empty(page.Diagnostics);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void Render_CmykScreenDistinguishesBlackInkFromProcessBlack(bool images, bool isolated, bool cmykPage)
    {
        var content = new PdfContentStreamBuilder()
            .SetFillCmyk(0, 1, 0, 0).Rectangle(0, 0, 2, 1).Fill()
            .SetBlendMode(PdfBlendMode.Screen);
        if (images)
            content.DrawImage(PdfImage.FromCmyk(1, 1, new byte[] { 0, 0, 0, 255 }), 0, 0, 1, 1)
                .DrawImage(PdfImage.FromCmyk(1, 1, new byte[] { 255, 255, 255, 0 }), 1, 0, 1, 1);
        else
            content.SetFillCmyk(0, 0, 0, 1).Rectangle(0, 0, 1, 1).Fill()
                .SetFillCmyk(1, 1, 1, 0).Rectangle(1, 0, 1, 1).Fill();
        if (isolated)
            content = new PdfContentStreamBuilder().DrawForm(new PdfFormXObject(2, 1, content,
                isolatedTransparencyGroup: true, transparencyGroupColorSpace: PdfTransparencyGroupColorSpace.Cmyk), 0, 0);
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1, content).Build());
        PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var group = new PdfDictionary([
            new(Name("S"), Name("Transparency")), new(Name("CS"), Name(cmykPage ? "DeviceCMYK" : "DeviceRGB"))]);
        var update = new PdfIncrementalUpdateBuilder(source).ReplaceObject(reference.ObjectNumber,
            new PdfDictionary(page.Append(new KeyValuePair<PdfName, PdfObject>(Name("Group"), group))));
        PdfRenderedPage rendered = new PdfPageRenderer(PdfDocument.Open(update.Build()))
            .Render(0, new PdfRenderOptions(2, 1));
        Assert.Equal(new byte[] { 255, 255, 255, 255, 255, 0, 255, 255 }, rendered.Pixels.ToArray());
        Assert.Empty(rendered.Diagnostics);
    }

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
