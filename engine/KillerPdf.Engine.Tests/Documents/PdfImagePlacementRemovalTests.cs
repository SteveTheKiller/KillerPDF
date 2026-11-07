using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfImagePlacementRemovalTests
{
    [Fact]
    public void RemovalRefusesToLeaveTaggedStructureStale()
    {
        var source = Fixture();
        var catalog = Assert.IsType<PdfDictionary>(source.Resolve(1)).ToDictionary(pair => pair.Key, pair => pair.Value);
        catalog[Name("StructTreeRoot")] = new PdfDictionary([
            new(Name("Type"), Name("StructTreeRoot")), new(Name("K"), new PdfArray([]))]);
        byte[] original = new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(1, new PdfDictionary(catalog)).Build();
        var tagged = PdfDocument.Open(original);
        var selection = new PdfPageContentReader(tagged).ReadImagePlacements(0)[1];
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementEditor.Remove(tagged, selection));
        Assert.Equal(2, new PdfPageContentReader(tagged).Read(0).Images.Count);
        Assert.True(Assert.IsType<PdfDictionary>(tagged.Resolve(1)).ContainsKey(Name("StructTreeRoot")));
    }

    [Fact]
    public void RemovalHonorsUserPasswordRestrictionsAndAllowsTheAuthenticatedOwner()
    {
        byte[] bytes = new PdfDocumentBuilder().AddPage(100, 100,
            new PdfContentStreamBuilder().DrawImage(PdfImage.FromRgb(1, 1, new byte[] { 255, 0, 0 }), 10, 10, 20, 20))
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            { UserPassword = "fixture-user", OwnerPassword = "fixture-owner", AllowContentCopying = true,
                AllowDocumentModification = false }).Build();
        var user = PdfDocument.Open(bytes, "fixture-user");
        var selected = Assert.Single(new PdfPageContentReader(user).ReadImagePlacements(0));
        Assert.Throws<InvalidOperationException>(() => PdfImagePlacementEditor.Remove(user, selected));
        Assert.Single(new PdfPageContentReader(user).Read(0).Images);
        var owner = PdfDocument.Open(bytes, "fixture-owner");
        var ownerSelection = Assert.Single(new PdfPageContentReader(owner).ReadImagePlacements(0));
        var revised = PdfDocument.Open(PdfImagePlacementEditor.Remove(owner, ownerSelection), "fixture-owner");
        Assert.Empty(new PdfPageContentReader(revised).Read(0).Images);
    }

    [Fact]
    public void RemovalFailsClosedOnMalformedCertificationPermissions()
    {
        var source = Fixture();
        var catalog = Assert.IsType<PdfDictionary>(source.Resolve(1)).ToDictionary(pair => pair.Key, pair => pair.Value);
        catalog[Name("Perms")] = new PdfDictionary([
            new(Name("DocMDP"), new PdfDictionary([new(Name("Type"), Name("Sig"))]))]);
        var certified = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(1, new PdfDictionary(catalog)).Build());
        var selection = new PdfPageContentReader(certified).ReadImagePlacements(0)[1];
        Assert.Throws<InvalidOperationException>(() => PdfImagePlacementEditor.Remove(certified, selection));
        Assert.Equal(2, new PdfPageContentReader(certified).Read(0).Images.Count);
    }

    const string First = "q 20 0 0 20 10 10 cm /Im0 Do Q";
    const string Other = "0 0 1 rg 40 10 10 10 re f BT /F1 8 Tf 10 70 Td (Keep) Tj ET";
    const string Second = "q 20 0 0 20 70 10 cm /Im0 Do Q";

    [Fact]
    public void RemovesOnlySelectedOccurrenceAndPreservesSharedImageAndOtherContent()
    {
        var document = Fixture();
        var reader = new PdfPageContentReader(document);
        var selections = reader.ReadImagePlacements(0);
        Assert.Equal(2, selections.Count);
        Assert.Equal(new PdfContentBounds(70, 10, 90, 30), selections[1].BoundingBox);
        Assert.True(selections[1].CanRemove);
        var edited = PdfDocument.Open(PdfImagePlacementEditor.Remove(document, selections[1]));
        var actual = new PdfPageContentReader(edited);
        Assert.Equal(new PdfContentBounds(10, 10, 30, 30), Assert.Single(actual.Read(0).Images).BoundingBox);
        Assert.Single(actual.Read(1).Images);
        Assert.Equal(2, reader.Read(0).Images.Count);
        Assert.Equal("Keep", string.Concat(actual.Read(0).Letters.Select(x => x.Value)));
        Assert.Equal(new PdfContentBounds(40, 10, 50, 20), Assert.Single(actual.Read(0).Paths).BoundingBox);
        Assert.Equal(reader.ReadInstructions(0).Where((_, i) => i != 14).Select(x => x.Operator), actual.ReadInstructions(0).Select(x => x.Operator));
        AssertImageResource(document);
        AssertImageResource(edited);
        var originalRender = new PdfPageRenderer(document).Render(0, new(100, 100));
        var editedRender = new PdfPageRenderer(edited).Render(0, new(100, 100));
        AssertPixel(originalRender, 15, 85, 0, 0, 255);
        AssertPixel(originalRender, 75, 85, 0, 0, 255);
        AssertPixel(editedRender, 15, 85, 0, 0, 255);
        AssertPixel(editedRender, 75, 85, 255, 255, 255);
        AssertPixel(editedRender, 45, 85, 255, 0, 0);
        Assert.Equal(new PdfPageRenderer(document).Render(1, new(100, 100)).Pixels.ToArray(),
            new PdfPageRenderer(edited).Render(1, new(100, 100)).Pixels.ToArray());
        for (int y = 0; y < 100; y++)
        for (int x = 0; x < 100; x++)
            if (x < 70 || x >= 90 || y < 70 || y >= 90)
                Assert.Equal(originalRender.Pixels.Slice((y * 100 + x) * 4, 4).ToArray(), editedRender.Pixels.Slice((y * 100 + x) * 4, 4).ToArray());
    }

    [Fact]
    public void RejectsOtherOwnersAndSelectionsFromAnEarlierDocumentInstance()
    {
        byte[] source = FixtureBytes();
        var original = PdfDocument.Open(source);
        var selection = new PdfPageContentReader(original).ReadImagePlacements(0)[1];
        Assert.Throws<ArgumentException>(() => PdfImagePlacementEditor.Remove(Fixture(), selection));
        Assert.Throws<ArgumentException>(() => PdfImagePlacementEditor.Remove(PdfDocument.Open(source), selection));
        var revised = PdfDocument.Open(PdfImagePlacementEditor.Remove(original, selection));
        Assert.Throws<ArgumentException>(() => PdfImagePlacementEditor.Remove(revised, selection));
        Assert.Equal(2, new PdfPageContentReader(original).ReadImagePlacements(0).Count);
    }

    [Fact]
    public void EqualOverlappingOccurrencesRemainDistinctByTheirInstructionPosition()
    {
        var original = Fixture(First + " " + Other + " " + First);
        var selections = new PdfPageContentReader(original).ReadImagePlacements(0);
        Assert.Equal(selections[0].BoundingBox, selections[1].BoundingBox);
        var revised = PdfDocument.Open(PdfImagePlacementEditor.Remove(original, selections[1]));
        Assert.Equal(new[] { "q", "cm", "Do", "Q", "rg", "re", "f", "BT", "Tf", "Td", "Tj", "ET", "q", "cm", "Q" },
            new PdfPageContentReader(revised).ReadInstructions(0).Select(x => x.Operator));
        Assert.Single(new PdfPageContentReader(revised).ReadImagePlacements(0));
    }

    [Fact]
    public void HandlesOriginalInstructionsAcrossContentsStreams()
    {
        var original = Fixture(split: true);
        var selection = new PdfPageContentReader(original).ReadImagePlacements(0)[1];
        var revised = PdfDocument.Open(PdfImagePlacementEditor.Remove(original, selection));
        Assert.Equal(new PdfContentBounds(10, 10, 30, 30), Assert.Single(new PdfPageContentReader(revised).Read(0).Images).BoundingBox);
        Assert.Equal("Keep", string.Concat(new PdfPageContentReader(revised).Read(0).Letters.Select(x => x.Value)));
        AssertImageResource(revised);
    }

    [Fact]
    public void HonorsCancellationBeforeSelectionAndBeforeRemoval()
    {
        var document = Fixture();
        var reader = new PdfPageContentReader(document);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.ReadImagePlacements(0, cancellation.Token));
        var selection = reader.ReadImagePlacements(0)[1];
        Assert.Throws<OperationCanceledException>(() => PdfImagePlacementEditor.Remove(document, selection, cancellation.Token));
        Assert.Equal(2, reader.Read(0).Images.Count);
    }

    [Theory]
    [InlineData("q 0 0 100 100 re W n Q ", "", "", "ComplexPageState")]
    [InlineData("/Missing gs ", "", "", "ComplexPageState")]
    [InlineData("BT 4 Tr ET ", "", "", "TextClipping")]
    [InlineData("BT 5 Tr ET ", "", "", "TextClipping")]
    [InlineData("BT 6 Tr ET ", "", "", "TextClipping")]
    [InlineData("BT 7 Tr ET ", "", "", "TextClipping")]
    [InlineData("BX EX ", "", "", "UnknownPageState")]
    [InlineData("", "/Group << /S /Transparency >>", "", "PageTransparencyGroup")]
    [InlineData("", "", "/Mask [0 0 0 0 0 0]", "ImageMask")]
    [InlineData("", "", "/SMask /None", "ImageMask")]
    [InlineData("", "", "/ImageMask false", "ImageMask")]
    [InlineData("", "", "/OC << /Type /OCG /Name (Optional) >>", "OptionalContent")]
    public void ConservativelyRefusesComplexWholePageOrImageState(string prefix, string pageExtra, string imageExtra, string reason)
    {
        var document = Fixture(prefix + First + " " + Other + " " + Second, pageExtra, imageExtra);
        var selections = new PdfPageContentReader(document).ReadImagePlacements(0);
        Assert.Equal(2, selections.Count);
        foreach (var selection in selections)
        {
            Assert.False(selection.CanRemove);
            Assert.Equal(reason, selection.UnsupportedReason);
            Assert.Throws<NotSupportedException>(() => PdfImagePlacementEditor.Remove(document, selection));
        }
        Assert.Equal(2, new PdfPageContentReader(document).Read(0).Images.Count);
    }

    [Fact]
    public void RefusesRecoveryModeAndInlineImageOccurrences()
    {
        var recovery = PdfDocument.OpenWithCompatibilityRecovery(FixtureBytes());
        var recovered = new PdfPageContentReader(recovery).ReadImagePlacements(0)[0];
        Assert.Equal("CompatibilityRecovery", recovered.UnsupportedReason);
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementEditor.Remove(recovery, recovered));
        var inline = Fixture("q 20 0 0 20 10 10 cm BI /W 1 /H 1 /CS /RGB /BPC 8 ID abc EI Q");
        var inlineSelection = Assert.Single(new PdfPageContentReader(inline).ReadImagePlacements(0));
        Assert.Equal("InlineImage", inlineSelection.UnsupportedReason);
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementEditor.Remove(inline, inlineSelection));
    }

    [Fact]
    public void RefusesImageOccurrencesInsideAForm()
    {
        var document = Fixture(form: true);
        var nested = new PdfPageContentReader(document).ReadImagePlacements(0);
        Assert.Equal(2, nested.Count);
        Assert.All(nested, selection =>
        {
            Assert.Equal("NestedImage", selection.UnsupportedReason);
            Assert.Throws<NotSupportedException>(() => PdfImagePlacementEditor.Remove(document, selection));
        });
    }

    static void AssertPixel(PdfRenderedPage page, int x, int y, byte b, byte g, byte r) =>
        Assert.Equal(new byte[] { b, g, r, 255 }, page.Pixels.Slice((y * 100 + x) * 4, 4).ToArray());

    static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    static void AssertImageResource(PdfDocument document)
    {
        var page = Assert.IsType<PdfDictionary>(document.Resolve(3));
        var resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var images = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        var image = Assert.IsType<PdfIndirectReference>(images[Name("Im0")]);
        Assert.Equal(5, image.ObjectNumber);
        Assert.Equal(0, image.Generation);
        Assert.Equal(new byte[] { 255, 0, 0 }, Assert.IsType<PdfStream>(document.Resolve(image)).EncodedData.ToArray());
    }

    static PdfDocument Fixture(string? page0 = null, string pageExtra = "", string imageExtra = "", bool split = false, bool form = false)
        => PdfDocument.Open(FixtureBytes(page0, pageExtra, imageExtra, split, form));

    static byte[] FixtureBytes(string? page0 = null, string pageExtra = "", string imageExtra = "", bool split = false, bool form = false)
    {
        string content = page0 ?? First + " " + Other + " " + Second;
        string Stream(string value) => $"<< /Length {Encoding.Latin1.GetByteCount(value)} >>\nstream\n{value}\nendstream";
        string resources = "/Resources << /XObject << /Im0 5 0 R >> /Font << /F1 8 0 R >> >>";
        int splitIndex = First.Length;
        string image = $"<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 3 {imageExtra} >>\nstream\n" + "\u00ff\0\0" + "\nendstream";
        string formContent = "/Inner Do";
        string[] objects = [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] /MediaBox [0 0 100 100] >>",
            $"<< /Type /Page /Parent 2 0 R {resources} /Contents {(split ? "[6 0 R 9 0 R]" : "6 0 R")} {pageExtra} >>",
            $"<< /Type /Page /Parent 2 0 R {resources} /Contents 7 0 R >>",
            form ? $"<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /Inner 10 0 R >> >> /Length {formContent.Length} >>\nstream\n{formContent}\nendstream" : image,
            Stream(split ? content[..splitIndex] : content),
            Stream("q 20 0 0 20 40 40 cm /Im0 Do Q"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Stream(split ? content[splitIndex..] : ""),
            image
        ];
        var source = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(source.ToString()));
            source.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(source.ToString());
        source.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) source.Append($"{offset:D10} 00000 n \n");
        source.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(source.ToString());
    }
}
