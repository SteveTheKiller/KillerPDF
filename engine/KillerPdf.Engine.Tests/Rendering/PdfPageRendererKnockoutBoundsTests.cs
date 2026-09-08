using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererKnockoutBoundsTests
{
    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(false, 0.5, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 0.5, false)]
    [InlineData(false, 1, true)]
    [InlineData(false, 0.5, true)]
    [InlineData(true, 1, true)]
    [InlineData(true, 0.5, true)]
    public void Render_TranslatedKnockoutGroupPreservesBackdropAndOverlap(bool isolated, double opacity, bool cmyk)
    {
        PdfRenderedPage origin = Render(0, 0, isolated, opacity, cmyk);
        PdfRenderedPage moved = Render(7, 9, isolated, opacity, cmyk);
        for (int y = 0; y < 10; y++)
            for (int x = 0; x < 10; x++)
                Assert.Equal(Pixel(origin, x, 20 + y), Pixel(moved, 7 + x, 11 + y));
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, Pixel(moved, 6, 15));
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, Pixel(moved, 17, 15));
        Assert.NotEqual(Pixel(moved, 8, 15), Pixel(moved, 12, 15));
        Assert.Empty(moved.Diagnostics);
    }

    private static byte[] Pixel(PdfRenderedPage page, int x, int y) =>
        page.Pixels.Slice((y * page.Width + x) * 4, 4).ToArray();

    private static PdfRenderedPage Render(int x, int y, bool isolated, double opacity, bool cmyk)
    {
        var form = new PdfFormXObject(10, 10, new PdfContentStreamBuilder()
            .SetOpacity(0.5).SetFillRgb(1, 0, 0).Rectangle(0, 0, 7, 10).Fill()
            .SetFillRgb(0, 0, 1).Rectangle(3, 0, 7, 10).Fill());
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(40, 30,
            new PdfContentStreamBuilder().SetFillRgb(0.5, 0.5, 0.5).Rectangle(0, 0, 40, 30).Fill()
                .SetOpacity(opacity).DrawForm(form, x, y)).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var pageReference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(pageReference);
        var resources = (PdfDictionary)page[Name("Resources")];
        var reference = (PdfIndirectReference)((PdfDictionary)resources[Name("XObject")]).Single().Value;
        var stream = (PdfStream)source.Resolve(reference);
        var group = new PdfDictionary([
            new(Name("S"), Name("Transparency")), new(Name("I"), new PdfBoolean(isolated)),
            new(Name("K"), new PdfBoolean(true))]);
        var dictionary = new PdfDictionary(stream.Dictionary.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("Group"), group)));
        var update = new PdfIncrementalUpdateBuilder(source).ReplaceObject(reference.ObjectNumber,
            new PdfStream(dictionary, stream.EncodedData.Span));
        if (cmyk)
            update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(page.Append(
                new KeyValuePair<PdfName, PdfObject>(Name("Group"), new PdfDictionary([
                    new(Name("S"), Name("Transparency")), new(Name("CS"), Name("DeviceCMYK"))])))));
        return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(40, 30));
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
