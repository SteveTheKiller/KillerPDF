using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererTransparencyBandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_LargeIsolatedGroupStitchesBoundedBandsExactly(bool knockout)
    {
        const int size = 1500;
        var form = new PdfFormXObject(size, size, new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0).Rectangle(0, 0, 1000, size).Fill()
            .SetOpacity(0.5)
            .SetFillRgb(0, 0, 1).Rectangle(500, 0, 1000, size).Fill());
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(size, size, new PdfContentStreamBuilder()
                .SetOpacity(0.5).DrawForm(form, 0, 0))
            .Build());
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        var pageReference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        PdfDictionary page = ResolveDictionary(source, pageReference);
        PdfDictionary resources = ResolveDictionary(source, page[Name("Resources")]);
        PdfDictionary xObjects = ResolveDictionary(source, resources[Name("XObject")]);
        var formReference = (PdfIndirectReference)Assert.Single(xObjects).Value;
        var formStream = (PdfStream)source.Resolve(formReference);
        var group = new PdfDictionary(new[]
        {
            Entry("S", Name("Transparency")),
            Entry("I", new PdfBoolean(true))
        }.Concat(knockout ? [Entry("K", new PdfBoolean(true))] : []));
        var dictionary = new PdfDictionary(formStream.Dictionary.Append(Entry("Group", group)));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfDocument document = PdfDocument.Open(update.ReplaceObject(formReference.ObjectNumber,
            new PdfStream(dictionary, formStream.EncodedData.Span)).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(size, size));

        Assert.Empty(rendered.Diagnostics);
        foreach (int y in new[] { 100, 1397, 1398, 1450 })
        {
            Assert.Equal(new byte[] { 128, 128, 255, 255 }, Pixel(100, y));
            Assert.Equal(knockout
                ? new byte[] { 255, 191, 191, 255 }
                : new byte[] { 192, 128, 192, 255 }, Pixel(750, y));
            Assert.Equal(new byte[] { 255, 191, 191, 255 }, Pixel(1400, y));
        }

        byte[] Pixel(int x, int y) =>
            rendered.Pixels.Slice((y * size + x) * 4, 4).ToArray();
    }

    private static PdfDictionary ResolveDictionary(PdfDocument document, PdfObject value) =>
        value is PdfDictionary dictionary
            ? dictionary
            : (PdfDictionary)document.Resolve((PdfIndirectReference)value);

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) =>
        new(Name(name), value);
}
