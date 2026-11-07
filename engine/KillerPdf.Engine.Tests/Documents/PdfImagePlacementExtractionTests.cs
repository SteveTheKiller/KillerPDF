using System.Globalization;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfImagePlacementExtractionTests
{
    [Fact]
    public void DefaultRgbSubstitutionIsNotSilentlyDroppedFromExtraction()
    {
        var source = PdfDocument.Open(Fixture());
        var page = Assert.IsType<PdfDictionary>(source.Resolve(3)).ToDictionary(pair => pair.Key, pair => pair.Value);
        var pages = Assert.IsType<PdfDictionary>(source.Resolve(2));
        var resources = Assert.IsType<PdfDictionary>(pages[Name("Resources")]).ToDictionary(pair => pair.Key, pair => pair.Value);
        resources[Name("ColorSpace")] = new PdfDictionary([new(Name("DefaultRGB"), new PdfArray([
            Name("CalRGB"), new PdfDictionary([new(Name("WhitePoint"), new PdfArray([
                new PdfReal(.9505), new PdfInteger(1), new PdfReal(1.089)]))])]))]);
        page[Name("Resources")] = new PdfDictionary(resources);
        var document = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(3, new PdfDictionary(page)).Build());
        var selected = new PdfPageContentReader(document).ReadImagePlacements(0)[1];
        var error = Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(document, selected));
        Assert.Contains("default color-space", error.Message);
    }

    [Fact]
    public void AssetBudgetRejectsAValidLargeUnfilteredImage()
    {
        var source = PdfDocument.Open(Fixture());
        var image = Assert.IsType<PdfStream>(source.Resolve(7));
        var entries = image.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
        entries[Name("Width")] = new PdfInteger(1800);
        entries[Name("Height")] = new PdfInteger(1000);
        var document = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(7, new PdfStream(new PdfDictionary(entries), new byte[5_400_000])).Build());
        var selected = new PdfPageContentReader(document).ReadImagePlacements(0)[1];
        var error = Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(document, selected));
        Assert.Contains("size limit", error.Message);
    }

    [Fact]
    public void TruncatedSourcePixelsCannotProduceADetachedAsset()
    {
        var source = PdfDocument.Open(Fixture());
        var image = Assert.IsType<PdfStream>(source.Resolve(7));
        var document = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(7, new PdfStream(image.Dictionary, new byte[] { 255, 0, 0, 0, 255 })).Build());
        var selected = new PdfPageContentReader(document).ReadImagePlacements(0)[1];
        Assert.Throws<FormatException>(() => PdfImagePlacementExtractor.Extract(document, selected));
    }

    [Theory]
    [InlineData("Filter")]
    [InlineData("Decode")]
    [InlineData("Mask")]
    [InlineData("SMask")]
    [InlineData("ImageMask")]
    [InlineData("Intent")]
    [InlineData("ColorSpace")]
    [InlineData("BitsPerComponent")]
    public void UnsupportedImageInterpretationIsRefusedBeforePublishingAnAsset(string key)
    {
        var source = PdfDocument.Open(Fixture());
        var image = Assert.IsType<PdfStream>(source.Resolve(7));
        var entries = image.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
        entries[Name(key)] = key switch
        {
            "Filter" => Name("FlateDecode"), "ColorSpace" => Name("DeviceCMYK"),
            "BitsPerComponent" => new PdfInteger(16), "ImageMask" => new PdfBoolean(true),
            "Intent" => Name("RelativeColorimetric"),
            _ => new PdfArray([new PdfInteger(0), new PdfInteger(1)])
        };
        byte[] revised = new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(7, new PdfStream(new PdfDictionary(entries), image.EncodedData.Span)).Build();
        byte[] original = revised.ToArray();
        var document = PdfDocument.Open(revised);
        var selection = new PdfPageContentReader(document).ReadImagePlacements(0)[1];
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(document, selection));
        Assert.Equal(original, revised);
    }

    [Fact]
    public void ExtractionDropsUnrelatedImageDictionaryDataWithoutChangingTheOriginal()
    {
        var source = PdfDocument.Open(Fixture());
        var image = Assert.IsType<PdfStream>(source.Resolve(7));
        var entries = image.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
        entries[Name("AA")] = new PdfDictionary([new(Name("S"), Name("JavaScript"))]);
        var document = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(7, new PdfStream(new PdfDictionary(entries), image.EncodedData.Span)).Build());
        var selected = new PdfPageContentReader(document).ReadImagePlacements(0)[1];
        byte[] bytes = PdfImagePlacementExtractor.Extract(document, selected).ToPdfBytes();
        Assert.DoesNotContain("JavaScript", Encoding.Latin1.GetString(bytes));
        Assert.Single(new PdfPageContentReader(PdfDocument.Open(bytes)).Read(0).Images);
        Assert.True(Assert.IsType<PdfStream>(document.Resolve(7)).Dictionary.ContainsKey(Name("AA")));
    }

    [Fact]
    public void CroppedOccurrenceIsRefusedWhileTheCompleteOccurrenceCanDetach()
    {
        var source = PdfDocument.Open(Fixture());
        var page = Assert.IsType<PdfDictionary>(source.Resolve(3)).ToDictionary(pair => pair.Key, pair => pair.Value);
        page[Name("CropBox")] = new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(80), new PdfInteger(100)]);
        var document = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(3, new PdfDictionary(page)).Build());
        var selections = new PdfPageContentReader(document).ReadImagePlacements(0);
        Assert.Equal(2, PdfImagePlacementExtractor.Extract(document, selections[0]).Width);
        Assert.Equal(new PdfContentBounds(70, 10, 80, 30), selections[1].BoundingBox);
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(document, selections[1]));
    }

    [Fact]
    public void AssetCopiesRemainIndependentOfReturnedBytesAndSourceBuffers()
    {
        byte[] source = Fixture();
        var document = PdfDocument.Open(source);
        var asset = PdfImagePlacementExtractor.Extract(document,
            new PdfPageContentReader(document).ReadImagePlacements(0)[0]);
        byte[] serialized = asset.ToPdfBytes(), expected = serialized.ToArray();
        Array.Clear(serialized);
        Array.Clear(source);
        Assert.Equal(expected, asset.ToPdfBytes());
        Assert.Single(new PdfPageContentReader(PdfDocument.Open(asset.ToPdfBytes())).Read(0).Images);
        Assert.Throws<OperationCanceledException>(() => asset.ToPdfBytes(new CancellationToken(true)));
    }

    [Fact]
    public void ExtractionRejectsAnotherDocumentAndHonorsCancellationWithoutChangingSource()
    {
        byte[] source = Fixture(), original = source.ToArray();
        var document = PdfDocument.Open(source);
        var selection = new PdfPageContentReader(document).ReadImagePlacements(0)[1];
        Assert.Throws<ArgumentException>(() => PdfImagePlacementExtractor.Extract(PdfDocument.Open(source), selection));
        Assert.Throws<OperationCanceledException>(() => PdfImagePlacementExtractor.Extract(document, selection, new CancellationToken(true)));
        Assert.Equal(original, source);
        Assert.Equal(2, new PdfPageContentReader(document).Read(0).Images.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtractionHonorsContentCopyPermissionIndependentlyOfModification(bool copying)
    {
        byte[] encrypted = PdfDocumentEncryptionWriter.EncryptWithPasswords(PdfDocument.Open(Fixture()),
            new PdfPasswordEncryptionOptions { UserPassword = "read", OwnerPassword = "owner",
                AllowContentCopying = copying, AllowDocumentModification = false });
        byte[] original = encrypted.ToArray();
        var user = PdfDocument.Open(encrypted, "read");
        var selected = new PdfPageContentReader(user).ReadImagePlacements(0)[1];
        if (copying)
            Assert.Equal(2, PdfImagePlacementExtractor.Extract(user, selected).Width);
        else
            Assert.Throws<InvalidOperationException>(() => PdfImagePlacementExtractor.Extract(user, selected));
        var owner = PdfDocument.Open(encrypted, "owner");
        Assert.Equal(2, PdfImagePlacementExtractor.Extract(owner,
            new PdfPageContentReader(owner).ReadImagePlacements(0)[1]).Width);
        Assert.Equal(original, encrypted);
    }

    [Fact]
    public void SelectedImageDetachesItsOriginalPixelsWithoutCopyingOtherPageContent()
    {
        byte[] source = Fixture(), original = source.ToArray();
        var document = PdfDocument.Open(source);
        var reader = new PdfPageContentReader(document);
        var selection = reader.ReadImagePlacements(0)[1];
        Assert.Equal(new PdfContentBounds(70, 10, 90, 30), selection.BoundingBox);

        var asset = PdfImagePlacementExtractor.Extract(document, selection);
        Assert.Equal(2, asset.Width);
        Assert.Equal(1, asset.Height);
        var detached = PdfDocument.Open(asset.ToPdfBytes());
        var content = new PdfPageContentReader(detached);
        Assert.Equal(1, content.PageCount);
        var page = content.Read(0);
        var image = Assert.Single(page.Images);
        Assert.Equal(new PdfContentBounds(0, 0, 2, 1), image.BoundingBox);
        Assert.Equal(2, image.PixelWidth);
        Assert.Equal(1, image.PixelHeight);
        Assert.Empty(page.Letters);
        Assert.Empty(page.Paths);
        Assert.Empty(page.Shadings);
        var catalog = Assert.IsType<PdfDictionary>(Resolve(detached, detached.Trailer[Name("Root")]));
        var pages = Assert.IsType<PdfDictionary>(Resolve(detached, catalog[Name("Pages")]));
        var kids = Assert.IsType<PdfArray>(Resolve(detached, pages[Name("Kids")]));
        var dictionary = Assert.IsType<PdfDictionary>(Resolve(detached, Assert.Single(kids)));
        var resources = Assert.IsType<PdfDictionary>(Resolve(detached, dictionary[Name("Resources")]));
        Assert.False(resources.ContainsKey(Name("Font")));
        var xobjects = Assert.IsType<PdfDictionary>(Resolve(detached, resources[Name("XObject")]));
        var stream = Assert.IsType<PdfStream>(Resolve(detached, Assert.Single(xobjects).Value));
        Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0 }, stream.EncodedData.ToArray());
        var frame = new PdfPageRenderer(detached).Render(0, new PdfRenderOptions(2, 1));
        Assert.Empty(frame.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 }, frame.Pixels.ToArray());
        Assert.Equal(2, reader.Read(0).Images.Count);
        Assert.Single(reader.Read(1).Images);
        Assert.Equal("Keep", string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));
        Assert.Single(reader.Read(0).Paths);
        Assert.Equal(original, source);
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value) =>
        value is PdfIndirectReference reference ? document.Resolve(reference) : value;
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static byte[] Fixture()
    {
        const string first = "q 20 0 0 20 10 10 cm /Im0 Do Q 0 0 1 rg 40 10 10 10 re f BT /F1 8 Tf 10 70 Td (Keep) Tj ET q 20 0 0 20 70 10 cm /Im0 Do Q";
        const string second = "q 20 0 0 20 10 10 cm /Im0 Do Q";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 100 100] /Resources << /XObject << /Im0 7 0 R >> /Font << /F1 8 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /Contents 6 0 R >>",
            "<< /Length " + first.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + first + "\nendstream",
            "<< /Length " + second.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + second + "\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 6 >>\nstream\n" + Encoding.Latin1.GetString([255, 0, 0, 0, 255, 0]) + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        ];
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(text.Length);
            text.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        int xref = text.Length;
        text.Append("xref\n0 9\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        text.Append("trailer\n<< /Size 9 /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }
}
