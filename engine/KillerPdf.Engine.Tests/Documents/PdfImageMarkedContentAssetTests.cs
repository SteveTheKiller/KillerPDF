using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfImageMarkedContentAssetTests
{
    [Fact]
    public void BalancedOrdinaryMetadataAfterImagePreservesExtractionAndAuthoredReuse()
    {
        byte[] bytes = new PdfDocumentBuilder().AddPage(2, 1, new PdfContentStreamBuilder()
            .DrawImage(PdfImage.FromRgb(2, 1, new byte[] { 255, 0, 0, 0, 255, 0 }), 0, 0, 2, 1)
            .BeginArtifact().EndMarkedContent()).Build();
        byte[] original = bytes.ToArray();
        var source = PdfDocument.Open(bytes);
        var selection = Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0));

        var asset = PdfImagePlacementExtractor.Extract(source, selection);
        var reused = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 2, 1)).Build());

        Assert.True(selection.CanRemove);
        Assert.True(selection.CanSetBounds);
        Assert.Equal(new float[] { 1, 0, 0, 1, 0, 1, 0, 1 },
            new PdfPageRenderer(reused).RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true)).Pixels.ToArray());
        Assert.Equal(original, bytes);
        Assert.Equal(new[] { "q", "cm", "Do", "Q", "BMC", "EMC" },
            new PdfPageContentReader(source).ReadInstructions(0).Select(instruction => instruction.Operator));
    }
    [Fact]
    public void RemovalKeepsMarkersOtherInstructionsAndSharedOccurrenceOwnership()
    {
        byte[] bytes = Fixture(SharedContent), original = bytes.ToArray();
        var document = PdfDocument.Open(bytes);
        var reader = new PdfPageContentReader(document);
        var selections = reader.ReadImagePlacements(0);
        Assert.Equal(2, selections.Count);
        Assert.True(selections[1].CanRemove);

        var removed = PdfDocument.Open(PdfImagePlacementEditor.Remove(document, selections[1]));

        var actual = new PdfPageContentReader(removed);
        Assert.Equal(new PdfContentBounds(0, 0, 2, 1), Assert.Single(actual.ReadImagePlacements(0)).BoundingBox);
        Assert.Equal(PdfContentStreamWriter.Write(reader.ReadInstructions(0).Where((_, index) => index != 7)),
            PdfContentStreamWriter.Write(actual.ReadInstructions(0)));
        Assert.Equal("Span", Assert.IsType<PdfName>(actual.ReadInstructions(0).Single(item => item.Operator == "BMC").Operands[0]).ValueAsLatin1());
        Assert.Equal(new float[] { 1, 0, 0, 1, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 1, 1, 1, 1, 1, 1 },
            new PdfPageRenderer(removed).RenderFrame(0, new PdfRenderOptions(6, 1)).Pixels.ToArray());
        Assert.Equal(original, bytes);
        Assert.Equal(2, reader.ReadImagePlacements(0).Count);
    }

    [Fact]
    public void BoundsEditKeepsMarkersAndChangesOnlySelectedOccurrence()
    {
        byte[] bytes = Fixture(SharedContent), original = bytes.ToArray();
        var document = PdfDocument.Open(bytes);
        var selected = new PdfPageContentReader(document).ReadImagePlacements(0)[1];
        Assert.True(selected.CanSetBounds);

        var edited = PdfDocument.Open(PdfImagePlacementEditor.SetBounds(document, selected, new(4, 0, 6, 1)));

        var reader = new PdfPageContentReader(edited);
        Assert.Equal(new[] { new PdfContentBounds(0, 0, 2, 1), new PdfContentBounds(4, 0, 6, 1) },
            reader.ReadImagePlacements(0).Select(item => item.BoundingBox));
        Assert.Equal(new[] { "q", "cm", "Do", "Q", "BMC", "q", "cm", "q", "cm", "Do", "Q", "Q", "EMC", "rg", "re", "f" },
            reader.ReadInstructions(0).Select(item => item.Operator));
        Assert.Equal("Span", Assert.IsType<PdfName>(reader.ReadInstructions(0).Single(item => item.Operator == "BMC").Operands[0]).ValueAsLatin1());
        Assert.Equal(new float[] { 1, 0, 0, 1, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 1, 1, 0, 1, 0, 1 },
            new PdfPageRenderer(edited).RenderFrame(0, new PdfRenderOptions(6, 1)).Pixels.ToArray());
        Assert.Equal(original, bytes);
        Assert.Equal(new PdfContentBounds(2, 0, 4, 1), new PdfPageContentReader(document).ReadImagePlacements(0)[1].BoundingBox);
    }

    [Theory]
    [InlineData("EMC /Span BMC", false)]
    [InlineData("/Artifact BMC EMC", true)]
    public void FormMarkedContentMustStayWithinItsOwnScope(string formContent, bool editable)
    {
        var seed = PdfDocument.Open(Fixture("q 2 0 0 1 0 0 cm /Im1 Do Q /Span BMC /Metadata Do EMC"));
        var catalog = Assert.IsType<PdfDictionary>(seed.Resolve(Assert.IsType<PdfIndirectReference>(seed.Trailer[Name("Root")])));
        var pages = Assert.IsType<PdfDictionary>(seed.Resolve(Assert.IsType<PdfIndirectReference>(catalog[Name("Pages")])));
        var pageReference = Assert.IsType<PdfIndirectReference>(Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        var dictionary = Assert.IsType<PdfDictionary>(seed.Resolve(pageReference));
        var resources = Assert.IsType<PdfDictionary>(dictionary[Name("Resources")]);
        var images = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        var update = new PdfIncrementalUpdateBuilder(seed);
        var form = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Form")), Entry("BBox", new PdfArray([
                new PdfInteger(0), new PdfInteger(0), new PdfInteger(1), new PdfInteger(1)]))]),
            Encoding.ASCII.GetBytes(formContent)));
        var changedResources = new PdfDictionary(resources.Where(item => !item.Key.Equals(Name("XObject")))
            .Append(Entry("XObject", new PdfDictionary(images.Append(Entry("Metadata", form))))));
        byte[] bytes = update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(
            dictionary.Where(item => !item.Key.Equals(Name("Resources"))).Append(Entry("Resources", changedResources)))).Build();
        byte[] original = bytes.ToArray();
        var document = PdfDocument.Open(bytes);

        var selection = Assert.Single(new PdfPageContentReader(document).ReadImagePlacements(0));

        if (editable)
        {
            Assert.True(selection.CanRemove);
            Assert.True(selection.CanSetBounds);
            var asset = PdfImagePlacementExtractor.Extract(document, selection);
            Assert.Equal(new float[] { 1, 0, 0, 1, 0, 1, 0, 1 },
                new PdfPageRenderer(PdfDocument.Open(asset.ToPdfBytes())).RenderFrame(0,
                    new PdfRenderOptions(2, 1, transparentBackground: true)).Pixels.ToArray());
            Assert.Equal(original, bytes);
            return;
        }
        Assert.False(selection.CanRemove);
        Assert.False(selection.CanSetBounds);
        Assert.Equal("ComplexPageState", selection.UnsupportedReason);
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(document, selection));
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementEditor.Remove(document, selection));
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementEditor.SetBounds(document, selection, new(2, 0, 4, 1)));
        Assert.Equal(original, bytes);
    }

    [Theory]
    [InlineData("/Span BMC EMC ", "")]
    [InlineData("/Artifact BMC /Span BMC ", " EMC EMC")]
    public void OrdinaryMetadataBeforeAndAroundSharedImagesKeepsThemEditable(string prefix, string suffix)
    {
        byte[] bytes = Fixture(prefix + SharedContent + suffix), original = bytes.ToArray();
        var document = PdfDocument.Open(bytes);
        var reader = new PdfPageContentReader(document);
        var selections = reader.ReadImagePlacements(0);
        Assert.Equal(2, selections.Count);
        foreach (var selection in selections)
        {
            Assert.True(selection.CanRemove);
            Assert.True(selection.CanSetBounds);
            var asset = PdfImagePlacementExtractor.Extract(document, selection);
            Assert.Equal(new float[] { 1, 0, 0, 1, 0, 1, 0, 1 },
                new PdfPageRenderer(PdfDocument.Open(asset.ToPdfBytes())).RenderFrame(0,
                    new PdfRenderOptions(2, 1, transparentBackground: true)).Pixels.ToArray());
        }
        Assert.Empty(reader.Read(0).Diagnostics);
        Assert.Equal(original, bytes);
    }

    [Theory]
    [InlineData("/OC BMC EMC")]
    [InlineData("/Span << >> BDC EMC")]
    [InlineData("/Span MP")]
    [InlineData("/Span << >> DP")]
    public void UnsupportedMetadataStillRefusesExtractionRemovalAndBoundsChanges(string metadata)
    {
        byte[] bytes = Fixture("q 2 0 0 1 0 0 cm /Im1 Do Q " + metadata), original = bytes.ToArray();
        var document = PdfDocument.Open(bytes);

        var selection = Assert.Single(new PdfPageContentReader(document).ReadImagePlacements(0));

        Assert.False(selection.CanRemove);
        Assert.False(selection.CanSetBounds);
        Assert.Equal("ComplexPageState", selection.UnsupportedReason);
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(document, selection));
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementEditor.Remove(document, selection));
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementEditor.SetBounds(document, selection, new(2, 0, 4, 1)));
        Assert.Equal(original, bytes);
    }

    [Theory]
    [InlineData("1 BMC EMC")]
    [InlineData("/Span BMC")]
    [InlineData("EMC")]
    [InlineData("/Span BMC EMC EMC")]
    [InlineData("/Span /Other BMC EMC")]
    [InlineData("BMC EMC")]
    [InlineData("/Span BMC 1 EMC")]
    public void MalformedMarkedContentKeepsStrictReadDiagnosis(string metadata)
    {
        byte[] bytes = Fixture("q 2 0 0 1 0 0 cm /Im1 Do Q " + metadata), original = bytes.ToArray();
        var reader = new PdfPageContentReader(PdfDocument.Open(bytes));

        Assert.Throws<FormatException>(() => reader.ReadImagePlacements(0));

        Assert.Equal(original, bytes);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
    private const string SharedContent = "q 2 0 0 1 0 0 cm /Im1 Do Q /Span BMC q 2 0 0 1 2 0 cm /Im1 Do Q EMC 0 0 1 rg 4 0 1 1 re f";

    private static byte[] Fixture(string content)
    {
        var image = PdfImage.FromRgb(2, 1, new byte[] { 255, 0, 0, 0, 255, 0 });
        var seed = PdfDocument.Open(new PdfDocumentBuilder().AddPage(6, 1,
            new PdfContentStreamBuilder().DrawImage(image, 0, 0, 2, 1)).Build());
        return new PdfIncrementalPageEditor(seed).SetPageContent(0, Encoding.ASCII.GetBytes(content)).Build();
    }
}
