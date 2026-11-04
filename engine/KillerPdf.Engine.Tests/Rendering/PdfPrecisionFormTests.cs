using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionFormTests
{
    [Fact]
    public void UngroupedFormRetainsCmykPrecisionThroughItsMatrixAndBoundingBox()
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            Encoding.ASCII.GetBytes("/Form Do")).Build());
        var catalog = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(source.Trailer[Name("Root")])));
        var pages = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[Name("Pages")])));
        var page = Assert.IsType<PdfIndirectReference>(Assert.Single(Assert.IsType<PdfArray>(pages[Name("Kids")])));
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page));
        var update = new PdfIncrementalUpdateBuilder(source);
        var form = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Type", Name("XObject")), Entry("Subtype", Name("Form")),
            Entry("BBox", Numbers(0, 0, 1, 1)), Entry("Matrix", Numbers(1, 0, 0, 1, 1, 0)),
            Entry("Resources", new PdfDictionary([]))]),
            Encoding.ASCII.GetBytes("1 0 0 0 k -1 0 3 1 re f")));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Form", form)]))]);
        var document = PdfDocument.Open(update.ReplaceObject(page.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());

        PdfRenderedFrame frame = new PdfPageRenderer(document)
            .RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        Assert.Equal(0, frame.Pixels[3]);
        // The frozen independent cyan profile corner must survive Form processing unchanged.
        Assert.InRange(frame.Pixels[4], -0.20839846805136676f - 0.000002f, -0.20839846805136676f + 0.000002f);
        Assert.InRange(frame.Pixels[5], 0.42131251226180745f - 0.000002f, 0.42131251226180745f + 0.000002f);
        Assert.InRange(frame.Pixels[6], 0.86494485126038379f - 0.000002f, 0.86494485126038379f + 0.000002f);
        Assert.Equal(1, frame.Pixels[7]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TransparencyGroupIsDiagnosedInsteadOfReturningAQuantizedFrame(bool isolated, bool knockout)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes("/Form Do")).Build());
        var catalog = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(source.Trailer[Name("Root")])));
        var pages = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[Name("Pages")])));
        var page = Assert.IsType<PdfIndirectReference>(Assert.Single(Assert.IsType<PdfArray>(pages[Name("Kids")])));
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page));
        var update = new PdfIncrementalUpdateBuilder(source);
        var group = new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", Name("DeviceRGB")),
            Entry("I", new PdfBoolean(isolated)), Entry("K", new PdfBoolean(knockout))]);
        var form = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Type", Name("XObject")), Entry("Subtype", Name("Form")),
            Entry("BBox", Numbers(0, 0, 1, 1)), Entry("Group", group),
            Entry("Resources", new PdfDictionary([]))]), Encoding.ASCII.GetBytes("0.5 g 0 0 1 1 re f")));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Form", form)]))]);
        var document = PdfDocument.Open(update.ReplaceObject(page.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());

        var error = Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(document)
            .RenderFrame(0, new PdfRenderOptions(1, 1)));

        Assert.Contains("form group", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
