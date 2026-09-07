using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfGraphicsStateFontTests
{
    [Theory]
    [InlineData(20, false)]
    [InlineData(-20, false)]
    [InlineData(0, false)]
    [InlineData(20, true)]
    [InlineData(-20, true)]
    [InlineData(0, true)]
    public void GraphicsStateFontMatchesTextFontOperator(int size, bool indirectArray)
    {
        PdfRenderedPage expected = Render($"BT /F1 {size} Tf 1 0 0 1 100 50 Tm (Hello) Tj ET", size);
        PdfRenderedPage actual = Render("/G1 gs BT 1 0 0 1 100 50 Tm (Hello) Tj ET", size, indirectArray);

        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        if (size != 0) Assert.Contains(actual.Pixels.ToArray(), channel => channel < 255);
        else Assert.All(actual.Pixels.ToArray(), channel => Assert.Equal(255, channel));
    }

    [Fact]
    public void FontChangesInvalidateCachedFontAndRestoreSavedState()
    {
        const string content = "BT /F1 20 Tf 1 0 0 1 10 75 Tm (Hello) Tj ET " +
            "q /G2 gs BT 1 0 0 1 10 45 Tm (World) Tj ET Q " +
            "/NoFont gs BT 1 0 0 1 10 15 Tm (Again) Tj ET";
        PdfRenderedPage expected = Render(content.Replace("/G2 gs", "BT /F2 12 Tf ET", StringComparison.Ordinal), 20);
        PdfRenderedPage actual = Render(content, 20);

        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    private static PdfRenderedPage Render(string content, int size, bool indirectArray = false)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(200, 100, Encoding.ASCII.GetBytes(content)).Build());
        var catalog = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(source.Trailer[Name("Root")])));
        var pages = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[Name("Pages")])));
        var pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        var page = Assert.IsType<PdfDictionary>(source.Resolve(pageReference));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference helvetica = update.AddObject(Font("Helvetica"));
        PdfIndirectReference courier = update.AddObject(Font("Courier"));
        PdfObject fontArray = new PdfArray([helvetica, new PdfInteger(size)]);
        if (indirectArray) fontArray = update.AddObject(fontArray);
        var resources = new PdfDictionary([
            Entry("Font", new PdfDictionary([Entry("F1", helvetica), Entry("F2", courier)])),
            Entry("ExtGState", new PdfDictionary([
                Entry("G1", new PdfDictionary([Entry("Font", fontArray)])),
                Entry("G2", new PdfDictionary([Entry("Font", new PdfArray([courier, new PdfInteger(12)]))])),
                Entry("NoFont", new PdfDictionary([Entry("ca", new PdfInteger(1))]))
            ]))
        ]);
        var updatedPage = new PdfDictionary(page.Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", resources)));
        PdfDocument document = PdfDocument.Open(update.ReplaceObject(pageReference.ObjectNumber, updatedPage).Build());
        return new PdfPageRenderer(document).Render(0,
            new PdfRenderOptions(200, 100, includeAnnotations: false, includeFormFields: false));
    }

    private static PdfDictionary Font(string name) => new([
        Entry("Type", Name("Font")), Entry("Subtype", Name("Type1")), Entry("BaseFont", Name(name))
    ]);

    private static PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
