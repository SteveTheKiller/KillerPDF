using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfImagePlacementBoundsTests
{
    [Fact]
    public void MovesAndResizesOnlySelectedSharedImageOccurrence()
    {
        byte[] bytes = Fixture();
        byte[] unchanged = bytes.ToArray();
        var original = PdfDocument.Open(bytes);
        var selected = new PdfPageContentReader(original).ReadImagePlacements(0)[1];
        Assert.Equal(new PdfContentBounds(70, 10, 90, 30), selected.BoundingBox);
        byte[] revised = PdfImagePlacementEditor.SetBounds(original, selected, new(60, 40, 90, 50));
        var edited = PdfDocument.Open(revised);
        var actual = new PdfPageContentReader(edited);
        Assert.Equal(new[] { new PdfContentBounds(10, 10, 30, 30), new PdfContentBounds(60, 40, 90, 50) },
            actual.Read(0).Images.Select(image => image.BoundingBox));
        Assert.Equal(new PdfContentBounds(40, 40, 60, 60), Assert.Single(actual.Read(1).Images).BoundingBox);
        Assert.Equal("Keep", string.Concat(actual.Read(0).Letters.Select(letter => letter.Value)));
        Assert.Equal(new PdfContentBounds(40, 10, 50, 20), Assert.Single(actual.Read(0).Paths).BoundingBox);
        Assert.Equal(unchanged, bytes);
        Assert.Equal(unchanged, revised[..unchanged.Length]);
        Assert.Equal(new PdfContentBounds(70, 10, 90, 30), new PdfPageContentReader(original).Read(0).Images[1].BoundingBox);
        foreach (var document in new[] { original, edited })
        {
            var image = Assert.IsType<PdfStream>(document.Resolve(5));
            Assert.Equal(new byte[] { 255, 0, 0 }, image.EncodedData.ToArray());
        }
        var before = new PdfPageRenderer(original).Render(0, new(100, 100));
        var after = new PdfPageRenderer(edited).Render(0, new(100, 100));
        AssertPixel(after, 75, 85, 255, 255, 255);
        AssertPixel(after, 65, 55, 0, 0, 255);
        AssertPixel(after, 15, 85, 0, 0, 255);
        AssertPixel(after, 45, 85, 255, 0, 0);
        for (int y = 0; y < 100; y++)
        for (int x = 0; x < 100; x++)
            if (!(x >= 70 && x < 90 && y >= 70 && y < 90)
                && !(x >= 60 && x < 90 && y >= 50 && y < 60))
                Assert.Equal(before.Pixels.Slice((y * 100 + x) * 4, 4).ToArray(), after.Pixels.Slice((y * 100 + x) * 4, 4).ToArray());
        Assert.Equal(new PdfPageRenderer(original).Render(1, new(100, 100)).Pixels.ToArray(),
            new PdfPageRenderer(edited).Render(1, new(100, 100)).Pixels.ToArray());
    }

    [Theory]
    [InlineData(60, 40, 80, 60)]
    [InlineData(70, 10, 100, 20)]
    [InlineData(-20, -10, 10, 10)]
    public void SupportsSeparateMoveResizeAndPositionsOutsideThePage(double left, double bottom, double right, double top)
    {
        var original = PdfDocument.Open(Fixture());
        var selection = new PdfPageContentReader(original).ReadImagePlacements(0)[1];
        Assert.True(selection.CanSetBounds);
        Assert.Null(selection.BoundsUnsupportedReason);
        var edited = PdfDocument.Open(PdfImagePlacementEditor.SetBounds(original, selection, new(left, bottom, right, top)));
        var expected = left < 0 ? new PdfContentBounds(0, 0, 10, 10) : new PdfContentBounds(left, bottom, right, top);
        Assert.Equal(expected, new PdfPageContentReader(edited).Read(0).Images[1].BoundingBox);
    }

    [Theory]
    [InlineData("-20 0 0 20 90 10", 0, 255, 0)]
    [InlineData("20 0 0 -20 70 30", 255, 0, 0)]
    [InlineData("-20 0 0 -20 90 30", 0, 255, 255)]
    public void SupportsNegativeAxes(string placement, byte b, byte g, byte r)
    {
        var original = PdfDocument.Open(Fixture(placement, asymmetric: true));
        var selection = new PdfPageContentReader(original).ReadImagePlacements(0)[1];
        Assert.True(selection.CanSetBounds);
        var edited = PdfDocument.Open(PdfImagePlacementEditor.SetBounds(original, selection, new(60, 40, 90, 50)));
        Assert.Equal(new PdfContentBounds(60, 40, 90, 50), new PdfPageContentReader(edited).Read(0).Images[1].BoundingBox);
        AssertPixel(new PdfPageRenderer(edited).Render(0, new(100, 100)), 65, 52, b, g, r);
    }

    [Fact]
    public void UsesCropRelativeBounds()
    {
        var original = PdfDocument.Open(Fixture(pageExtra: "/CropBox [5 5 95 95]"));
        var selection = new PdfPageContentReader(original).ReadImagePlacements(0)[1];
        Assert.Equal(new PdfContentBounds(65, 5, 85, 25), selection.BoundingBox);
        Assert.True(selection.CanSetBounds);
        var edited = PdfDocument.Open(PdfImagePlacementEditor.SetBounds(original, selection, new(55, 35, 85, 45)));
        Assert.Equal(new PdfContentBounds(55, 35, 85, 45), new PdfPageContentReader(edited).Read(0).Images[1].BoundingBox);
    }

    [Theory]
    [InlineData("20 0 0 20 90 10", "ClippedImage")]
    [InlineData("20 1 0 20 70 10", "NonAxisAlignedPlacement")]
    [InlineData("0 20 -20 0 90 10", "InvalidPlacement")]
    public void RefusesClippedSkewedAndRotatedPlacementsWithoutChangingRemoval(string placement, string reason)
    {
        var original = PdfDocument.Open(Fixture(placement));
        var selection = new PdfPageContentReader(original).ReadImagePlacements(0)[1];
        Assert.True(selection.CanRemove);
        Assert.False(selection.CanSetBounds);
        Assert.Equal(reason, selection.BoundsUnsupportedReason);
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementEditor.SetBounds(original, selection, new(60, 40, 90, 50)));
        var removed = PdfDocument.Open(PdfImagePlacementEditor.Remove(original, selection));
        Assert.Single(new PdfPageContentReader(removed).Read(0).Images);
    }

    [Theory]
    [InlineData("q 0 0 100 100 re W n Q ", "", "ComplexPageState")]
    [InlineData("", "/Mask [0 0 0 0 0 0]", "ImageMask")]
    [InlineData("BT 7 Tr ET ", "", "TextClipping")]
    public void RetainsExistingPageAndImageRefusals(string prefix, string imageExtra, string reason)
    {
        var original = PdfDocument.Open(Fixture(prefix: prefix, imageExtra: imageExtra));
        var selection = new PdfPageContentReader(original).ReadImagePlacements(0)[1];
        Assert.False(selection.CanRemove);
        Assert.False(selection.CanSetBounds);
        Assert.Equal(reason, selection.BoundsUnsupportedReason);
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementEditor.SetBounds(original, selection, new(60, 40, 90, 50)));
    }

    [Theory]
    [InlineData(double.NaN, 0, 10, 10)]
    [InlineData(0, double.NegativeInfinity, 10, 10)]
    [InlineData(0, 0, double.PositiveInfinity, 10)]
    [InlineData(0, 0, 10, double.NaN)]
    [InlineData(0, 0, 0, 10)]
    [InlineData(0, 0, 10, 0)]
    [InlineData(10, 0, 0, 10)]
    [InlineData(-double.MaxValue, 0, double.MaxValue, 10)]
    public void RejectsInvalidBounds(double left, double bottom, double right, double top)
    {
        var original = PdfDocument.Open(Fixture());
        var selection = new PdfPageContentReader(original).ReadImagePlacements(0)[1];
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfImagePlacementEditor.SetBounds(original, selection, new(left, bottom, right, top)));
        Assert.Equal(new PdfContentBounds(70, 10, 90, 30), new PdfPageContentReader(original).Read(0).Images[1].BoundingBox);
    }

    [Fact]
    public void RejectsForeignReopenedAndStaleSelectionsAndHonorsCancellation()
    {
        byte[] bytes = Fixture();
        var original = PdfDocument.Open(bytes);
        var selection = new PdfPageContentReader(original).ReadImagePlacements(0)[1];
        Assert.Throws<ArgumentException>(() => PdfImagePlacementEditor.SetBounds(PdfDocument.Open(Fixture()), selection, new(60, 40, 90, 50)));
        Assert.Throws<ArgumentException>(() => PdfImagePlacementEditor.SetBounds(PdfDocument.Open(bytes), selection, new(60, 40, 90, 50)));
        var revised = PdfDocument.Open(PdfImagePlacementEditor.SetBounds(original, selection, new(60, 40, 90, 50)));
        Assert.Throws<ArgumentException>(() => PdfImagePlacementEditor.SetBounds(revised, selection, new(60, 40, 90, 50)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PdfImagePlacementEditor.SetBounds(original, selection, new(60, 40, 90, 50), cancellation.Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HonorsTaggedStructureAndCertificationRestrictions(bool tagged)
    {
        var source = PdfDocument.Open(Fixture());
        var catalog = Assert.IsType<PdfDictionary>(source.Resolve(1)).ToDictionary(pair => pair.Key, pair => pair.Value);
        PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
        catalog[Name(tagged ? "StructTreeRoot" : "Perms")] = tagged
            ? new PdfDictionary([new(Name("Type"), Name("StructTreeRoot")), new(Name("K"), new PdfArray([]))])
            : new PdfDictionary([new(Name("DocMDP"), new PdfDictionary([new(Name("Type"), Name("Sig"))]))]);
        var restricted = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source).ReplaceObject(1, new PdfDictionary(catalog)).Build());
        var selection = new PdfPageContentReader(restricted).ReadImagePlacements(0)[1];
        if (tagged) Assert.Throws<NotSupportedException>(() => PdfImagePlacementEditor.SetBounds(restricted, selection, new(60, 40, 90, 50)));
        else Assert.Throws<InvalidOperationException>(() => PdfImagePlacementEditor.SetBounds(restricted, selection, new(60, 40, 90, 50)));
    }

    [Fact]
    public void HonorsModificationPermissionsAndAllowsAuthenticatedOwner()
    {
        byte[] bytes = new PdfDocumentBuilder().AddPage(100, 100,
            new PdfContentStreamBuilder().DrawImage(PdfImage.FromRgb(1, 1, new byte[] { 255, 0, 0 }), 10, 10, 20, 20))
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            { UserPassword = "fixture-user", OwnerPassword = "fixture-owner", AllowContentCopying = true,
                AllowDocumentModification = false }).Build();
        var user = PdfDocument.Open(bytes, "fixture-user");
        var selection = Assert.Single(new PdfPageContentReader(user).ReadImagePlacements(0));
        Assert.Throws<InvalidOperationException>(() => PdfImagePlacementEditor.SetBounds(user, selection, new(60, 40, 90, 50)));
        var owner = PdfDocument.Open(bytes, "fixture-owner");
        var ownerSelection = Assert.Single(new PdfPageContentReader(owner).ReadImagePlacements(0));
        var revised = PdfDocument.Open(PdfImagePlacementEditor.SetBounds(owner, ownerSelection, new(60, 40, 90, 50)), "fixture-owner");
        Assert.Equal(new PdfContentBounds(60, 40, 90, 50), Assert.Single(new PdfPageContentReader(revised).Read(0).Images).BoundingBox);
    }

    static void AssertPixel(PdfRenderedPage page, int x, int y, byte b, byte g, byte r) =>
        Assert.Equal(new byte[] { b, g, r, 255 }, page.Pixels.Slice((y * 100 + x) * 4, 4).ToArray());

    static byte[] Fixture(string placement = "20 0 0 20 70 10", string pageExtra = "", string imageExtra = "", string prefix = "", bool asymmetric = false)
    {
        string Stream(string value) => $"<< /Length {Encoding.Latin1.GetByteCount(value)} >>\nstream\n{value}\nendstream";
        string resources = "/Resources << /XObject << /Im0 5 0 R >> /Font << /F1 8 0 R >> >>";
        string pixels = asymmetric ? "\u00ff\0\0\0\u00ff\0\0\0\u00ff\u00ff\u00ff\0" : "\u00ff\0\0";
        int side = asymmetric ? 2 : 1;
        string[] objects = [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] /MediaBox [0 0 100 100] >>",
            $"<< /Type /Page /Parent 2 0 R {resources} /Contents 6 0 R {pageExtra} >>",
            $"<< /Type /Page /Parent 2 0 R {resources} /Contents 7 0 R >>",
            $"<< /Type /XObject /Subtype /Image /Width {side} /Height {side} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {pixels.Length} {imageExtra} >>\nstream\n{pixels}\nendstream",
            Stream(prefix + "q 20 0 0 20 10 10 cm /Im0 Do Q 0 0 1 rg 40 10 10 10 re f BT /F1 8 Tf 10 70 Td (Keep) Tj ET q " + placement + " cm /Im0 Do Q"),
            Stream("q 20 0 0 20 40 40 cm /Im0 Do Q"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
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
