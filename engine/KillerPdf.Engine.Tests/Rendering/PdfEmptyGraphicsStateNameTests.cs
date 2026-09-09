using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfEmptyGraphicsStateNameTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("Opacity", false)]
    [InlineData("Opacity", true)]
    public void Render_AppliesFillAndStrokeOpacityFromResourceName(string resourceName, bool indirect)
    {
        // An empty name is a valid resource key, as used by CompactedPDFSyntaxTest.pdf.
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(20, 10,
            Encoding.ASCII.GetBytes($"/{resourceName} gs 1 0 0 rg 0 0 8 10 re f 2 w 12 2 m 12 8 l S")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        var state = new PdfDictionary([Entry("ca", new PdfReal(0.33)), Entry("CA", new PdfReal(0.66))]);
        var resources = new PdfDictionary([Entry("ExtGState", new PdfDictionary([
            Entry(resourceName, indirect ? update.AddObject(state) : state)]))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page
            .Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources))));
        var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0,
            new PdfRenderOptions(20, 10, transparentBackground: true));

        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 84 }, rendered.Pixels.Slice((5 * 20 + 4) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 168 }, rendered.Pixels.Slice((5 * 20 + 12) * 4, 4).ToArray());
        Assert.Equal(0, rendered.Pixels.Span[(5 * 20 + 18) * 4 + 3]);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
