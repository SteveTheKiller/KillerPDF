using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Authoring;

public sealed class PdfOptionalContentTests
{
    [Fact]
    public void Build_WritesLayerCatalogVisibilityAndPagePropertyResources()
    {
        var measurements = new PdfOptionalContentGroup("Measurements Ω", initiallyVisible: false);
        var artwork = new PdfOptionalContentGroup(
            "Artwork", visibleWhenPrinting: true, visibleWhenExporting: false);
        var content = new PdfContentStreamBuilder()
            .BeginOptionalContent(measurements)
            .Rectangle(10, 10, 20, 20).Stroke()
            .EndMarkedContent()
            .BeginOptionalContent(artwork)
            .Rectangle(40, 40, 20, 20).Fill()
            .EndMarkedContent();

        PdfDocument document = PdfDocument.Open(
            new PdfDocumentBuilder().AddPage(100, 100, content).Build());
        PdfDictionary catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        PdfDictionary properties = Assert.IsType<PdfDictionary>(catalog[Name("OCProperties")]);
        PdfArray groups = Assert.IsType<PdfArray>(properties[Name("OCGs")]);
        PdfDictionary configuration = Assert.IsType<PdfDictionary>(properties[Name("D")]);
        PdfArray order = Assert.IsType<PdfArray>(configuration[Name("Order")]);
        PdfArray hidden = Assert.IsType<PdfArray>(configuration[Name("OFF")]);
        PdfDictionary pages = ResolveDictionary(document, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(document,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary pageProperties = Assert.IsType<PdfDictionary>(resources[Name("Properties")]);
        PdfStream stream = ResolveStream(document, page[Name("Contents")]);
        PdfIndirectReference artworkReference = Assert.IsType<PdfIndirectReference>(groups[0]);
        PdfIndirectReference measurementsReference = Assert.IsType<PdfIndirectReference>(groups[1]);

        Assert.Equal(groups.Select(value => Assert.IsType<PdfIndirectReference>(value).ObjectNumber),
            order.Select(value => Assert.IsType<PdfIndirectReference>(value).ObjectNumber));
        Assert.Single(hidden);
        Assert.Equal(measurementsReference.ObjectNumber,
            Assert.IsType<PdfIndirectReference>(hidden[0]).ObjectNumber);
        Assert.Equal("Artwork", DecodeUnicode(Assert.IsType<PdfString>(
            ResolveDictionary(document, artworkReference)[Name("Name")])));
        PdfDictionary artworkDictionary = ResolveDictionary(document, artworkReference);
        PdfDictionary usage = Assert.IsType<PdfDictionary>(artworkDictionary[Name("Usage")]);
        Assert.Equal("ON", Assert.IsType<PdfName>(
            Assert.IsType<PdfDictionary>(usage[Name("Print")])[Name("PrintState")])
            .ValueAsLatin1());
        Assert.Equal("OFF", Assert.IsType<PdfName>(
            Assert.IsType<PdfDictionary>(usage[Name("Export")])[Name("ExportState")])
            .ValueAsLatin1());
        Assert.Equal("Measurements Ω", DecodeUnicode(Assert.IsType<PdfString>(
            ResolveDictionary(document, measurementsReference)[Name("Name")])));
        Assert.Equal(measurementsReference.ObjectNumber,
            Assert.IsType<PdfIndirectReference>(pageProperties[Name("OC1")]).ObjectNumber);
        Assert.Equal(artworkReference.ObjectNumber,
            Assert.IsType<PdfIndirectReference>(pageProperties[Name("OC2")]).ObjectNumber);
        Assert.Contains("/OC /OC1 BDC", Encoding.ASCII.GetString(stream.EncodedData.Span));
        Assert.Contains("/OC /OC2 BDC", Encoding.ASCII.GetString(stream.EncodedData.Span));
    }

    [Fact]
    public void Build_SharesOneLayerObjectAcrossPages()
    {
        var layer = new PdfOptionalContentGroup("Shared layer");
        PdfContentStreamBuilder Content() => new PdfContentStreamBuilder()
            .BeginOptionalContent(layer).Rectangle(0, 0, 10, 10).Fill().EndMarkedContent();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, Content())
            .AddPage(100, 100, Content())
            .Build());
        PdfDictionary catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        PdfIndirectReference groupReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(
                Assert.IsType<PdfDictionary>(catalog[Name("OCProperties")])[Name("OCGs")])[0]);
        PdfDictionary pages = ResolveDictionary(document, catalog[Name("Pages")]);

        foreach (PdfObject pageValue in Assert.IsType<PdfArray>(pages[Name("Kids")]))
        {
            PdfDictionary page = ResolveDictionary(document, pageValue);
            PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
            PdfDictionary properties = Assert.IsType<PdfDictionary>(resources[Name("Properties")]);
            Assert.Equal(groupReference.ObjectNumber,
                Assert.IsType<PdfIndirectReference>(properties[Name("OC1")]).ObjectNumber);
        }
    }

    [Fact]
    public void Build_RejectsAmbiguousLayerNamesAndUnclosedSequences()
    {
        var first = new PdfOptionalContentGroup("Layer");
        var second = new PdfOptionalContentGroup("Layer");
        var content = new PdfContentStreamBuilder()
            .BeginOptionalContent(first).Rectangle(0, 0, 10, 10).Fill().EndMarkedContent()
            .BeginOptionalContent(second).Rectangle(20, 20, 10, 10).Fill().EndMarkedContent();

        Assert.Throws<InvalidOperationException>(() =>
            new PdfDocumentBuilder().AddPage(100, 100, content).Build());
        Assert.Throws<InvalidOperationException>(() =>
            new PdfContentStreamBuilder()
                .BeginOptionalContent(new PdfOptionalContentGroup("Open"))
                .Build());
        Assert.Throws<ArgumentException>(() => new PdfOptionalContentGroup(" "));
    }

    [Fact]
    public void PdfUa2Mode_DoesNotMistakeAVisualLayerForSemanticTagging()
    {
        var layer = new PdfOptionalContentGroup("Accessible layer");
        var untagged = new PdfContentStreamBuilder()
            .BeginOptionalContent(layer).Rectangle(0, 0, 10, 10).Fill().EndMarkedContent();
        var invalid = new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Layers", Language = "en-US" })
            .EnablePdfUa2Conformance()
            .AddPage(100, 100, untagged)
            .AddStructureContainer(PdfStructureType.Document);

        Assert.Throws<InvalidOperationException>(() => invalid.Build());

        var tagged = new PdfContentStreamBuilder()
            .BeginOptionalContent(layer)
            .BeginMarkedContent(PdfStructureType.Figure, 0)
            .Rectangle(0, 0, 10, 10).Fill()
            .EndMarkedContent()
            .EndMarkedContent();
        byte[] result = new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Layers", Language = "en-US" })
            .EnablePdfUa2Conformance()
            .AddPage(100, 100, tagged)
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Figure, 0, 0, 1,
                alternateDescription: "A square")
            .Build();

        Assert.NotEmpty(result);
    }

    private static PdfDictionary ResolveDictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(value)));
    private static PdfStream ResolveStream(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfStream>(document.Resolve(Assert.IsType<PdfIndirectReference>(value)));
    private static string DecodeUnicode(PdfString value) =>
        Encoding.BigEndianUnicode.GetString(value.Bytes.Span[2..]);
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
