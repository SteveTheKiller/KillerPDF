using System.Text;
using System.IO.Compression;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfImageSoftMaskAssetTests
{
    [Fact]
    public void SoftMaskMetadataReferencesShareOneBoundedTraversalBudget()
    {
        var source = PdfDocument.Open(Fixture());
        var image = Image(source);
        var maskReference = Assert.IsType<PdfIndirectReference>(image.Dictionary[N("SMask")]);
        var mask = Assert.IsType<PdfStream>(source.Resolve(maskReference));
        var update = new PdfIncrementalUpdateBuilder(source);
        var entries = mask.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (string key in new[] { "Width", "Height", "ColorSpace", "BitsPerComponent" })
        {
            PdfObject value = entries[N(key)];
            for (int link = 0; link < 31; link++) value = update.AddObject(value);
            entries[N(key)] = value;
        }
        byte[] bytes = update.ReplaceObject(maskReference.ObjectNumber,
            new PdfStream(new PdfDictionary(entries), mask.EncodedData.Span)).Build();
        source = PdfDocument.Open(bytes);
        var selection = Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0));
        var error = Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(source, selection));
        Assert.Contains("node limit", error.Message);
        Assert.Equal(new byte[] { 255, 128, 0 }, Assert.IsType<PdfStream>(Resolve(source,
            Image(source).Dictionary[N("SMask")])).EncodedData.ToArray());
    }

    [Fact]
    public void RawGraySoftMaskKeepsEncodedBytesAndStraightAlphaThroughImageReuse()
    {
        byte[] bytes = Fixture(), original = bytes.ToArray();
        var source = PdfDocument.Open(bytes);
        var asset = PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0)));
        Assert.Throws<InvalidOperationException>(() => new PdfDocumentBuilder(new PdfVersion(1, 3))
            .AddPage(3, 1, new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 3, 1)).Build());
        var target = PdfDocument.Open(new PdfDocumentBuilder(new PdfVersion(1, 4))
            .AddPage(3, 1, new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 3, 1)).Build());
        foreach (var document in new[] { source, PdfDocument.Open(asset.ToPdfBytes()), target })
        {
            var image = Image(document);
            Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255 }, image.EncodedData.ToArray());
            var mask = Assert.IsType<PdfStream>(Resolve(document, image.Dictionary[N("SMask")]));
            Assert.Equal("DeviceGray", Assert.IsType<PdfName>(mask.Dictionary[N("ColorSpace")]).ValueAsLatin1());
            Assert.Equal(8, Assert.IsType<PdfInteger>(mask.Dictionary[N("BitsPerComponent")]).Value);
            Assert.Equal(new byte[] { 255, 128, 0 }, mask.EncodedData.ToArray());
            var pixels = new PdfPageRenderer(document).RenderFrame(0,
                new PdfRenderOptions(3, 1, transparentBackground: true)).Pixels.ToArray();
            Assert.InRange(pixels[0], .999f, 1.001f);
            Assert.InRange(pixels[1], -.001f, .001f);
            Assert.InRange(pixels[2], -.001f, .001f);
            Assert.Equal(1f, pixels[3]);
            Assert.InRange(pixels[4], -.001f, .001f);
            Assert.InRange(pixels[5], .999f, 1.001f);
            Assert.InRange(pixels[6], -.001f, .001f);
            Assert.InRange(pixels[7], .5019f, .5021f);
            Assert.Equal(0f, pixels[11]);
            Assert.All(pixels, value => Assert.True(float.IsFinite(value)));
        }
        Assert.Equal(original, bytes);
    }

    [Theory]
    [InlineData("Width")]
    [InlineData("Height")]
    [InlineData("BitsPerComponent")]
    [InlineData("ColorSpace")]
    [InlineData("Decode")]
    [InlineData("DecodeParms")]
    [InlineData("Matte")]
    [InlineData("Mask")]
    [InlineData("SMask")]
    [InlineData("ImageMask")]
    [InlineData("OC")]
    [InlineData("Filter")]
    [InlineData("Interpolate")]
    public void UnsupportedSoftMaskInterpretationRefusesExtractionWithoutChangingSource(string key)
    {
        byte[] bytes = Fixture(entries => entries[N(key)] = key switch
        {
            "Width" or "Height" => new PdfInteger(2), "BitsPerComponent" => new PdfInteger(16),
            "ColorSpace" => N("DeviceRGB"), "Filter" => N("LZWDecode"),
            "Decode" => new PdfArray([new PdfInteger(1), new PdfInteger(0)]),
            "Matte" => new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(0)]),
            "ImageMask" => new PdfBoolean(true), _ => new PdfDictionary([])
        }), original = bytes.ToArray();
        var source = PdfDocument.Open(bytes);
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0))));
        Assert.Equal(original, bytes);
        Assert.True(Image(source).Dictionary.ContainsKey(N("SMask")));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void SoftMaskPayloadMustMatchEveryPixel(int length)
    {
        byte[] bytes = Fixture(maskBytes: new byte[length]), original = bytes.ToArray();
        var source = PdfDocument.Open(bytes);
        Assert.Throws<FormatException>(() => PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0))));
        Assert.Equal(original, bytes);
    }

    [Fact]
    public void ImageAndMaskShareTheDetachedAssetBudget()
    {
        var source = PdfDocument.Open(Fixture(maskBytes: new byte[1_400_000], width: 1400, height: 1000));
        var error = Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0))));
        Assert.Contains("size limit", error.Message);
    }

    [Theory]
    [InlineData("Group")]
    [InlineData("clip")]
    [InlineData("rotation")]
    [InlineData("OCProperties")]
    public void ASoftMaskCannotBypassTheOccurrenceSafetyGuards(string kind)
    {
        var source = PdfDocument.Open(Fixture());
        var catalog = (PdfDictionary)Resolve(source, source.Trailer[N("Root")]);
        var pages = (PdfDictionary)Resolve(source, catalog[N("Pages")]);
        var pageReference = (PdfIndirectReference)((PdfArray)Resolve(source, pages[N("Kids")]))[0];
        var page = ((PdfDictionary)source.Resolve(pageReference)).ToDictionary(pair => pair.Key, pair => pair.Value);
        var update = new PdfIncrementalUpdateBuilder(source);
        if (kind == "Group") page[N("Group")] = new PdfDictionary([new(N("S"), N("Transparency"))]);
        else if (kind == "clip") page[N("CropBox")] = new PdfArray([
            new PdfInteger(0), new PdfInteger(0), new PdfInteger(2), new PdfInteger(1)]);
        else if (kind == "rotation") page[N("Contents")] = update.AddObject(new PdfStream(new PdfDictionary([]),
            "q 3 1 0 1 0 0 cm /Image Do Q"u8));
        else
        {
            var entries = catalog.ToDictionary(pair => pair.Key, pair => pair.Value);
            entries[N("OCProperties")] = new PdfDictionary([]);
            update.ReplaceObject(((PdfIndirectReference)source.Trailer[N("Root")]).ObjectNumber, new PdfDictionary(entries));
        }
        source = PdfDocument.Open(update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(page)).Build());
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0))));
    }

    [Fact]
    public void SoftMaskExtractionRetainsDocumentOwnershipAndCancellationGuards()
    {
        byte[] bytes = Fixture();
        var source = PdfDocument.Open(bytes);
        var selection = Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0));
        Assert.Throws<ArgumentException>(() => PdfImagePlacementExtractor.Extract(PdfDocument.Open(bytes), selection));
        Assert.Throws<OperationCanceledException>(() => PdfImagePlacementExtractor.Extract(source, selection,
            new CancellationToken(true)));
    }

    [Fact]
    public void FlateRgbImageRetainsItsEncodedBytesBesideTheRawMask()
    {
        var source = PdfDocument.Open(Fixture(imageFlate: true));
        byte[] encoded = Image(source).EncodedData.ToArray();
        var asset = PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0)));
        var form = new PdfFormXObject(3, 1, new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 3, 1));
        Assert.Throws<InvalidOperationException>(() => new PdfDocumentBuilder(new PdfVersion(1, 3))
            .AddPage(3, 1, new PdfContentStreamBuilder().DrawForm(form, 0, 0)).Build());
        var target = PdfDocument.Open(new PdfDocumentBuilder(new PdfVersion(1, 4))
            .AddPage(3, 1, new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 3, 1)).Build());
        var image = Image(target);
        Assert.Equal(encoded, image.EncodedData.ToArray());
        Assert.Equal("FlateDecode", Assert.IsType<PdfName>(image.Dictionary[N("Filter")]).ValueAsLatin1());
        Assert.Equal(new byte[] { 255, 128, 0 }, Assert.IsType<PdfStream>(Resolve(target,
            image.Dictionary[N("SMask")])).EncodedData.ToArray());
        var pixels = new PdfPageRenderer(target).RenderFrame(0,
            new PdfRenderOptions(3, 1, transparentBackground: true)).Pixels.ToArray();
        Assert.Equal(1f, pixels[3]);
        Assert.InRange(pixels[5], .999f, 1.001f);
        Assert.InRange(pixels[7], .5019f, .5021f);
        Assert.Equal(0f, pixels[11]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SoftMasksDoNotChangeContentCopyPermissionEnforcement(bool copying)
    {
        byte[] bytes = PdfDocumentEncryptionWriter.EncryptWithPasswords(PdfDocument.Open(Fixture()),
            new PdfPasswordEncryptionOptions { UserPassword = "read", OwnerPassword = "owner",
                AllowContentCopying = copying, AllowDocumentModification = false });
        var source = PdfDocument.Open(bytes, "read");
        var selection = Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0));
        if (copying) Assert.Equal(3, PdfImagePlacementExtractor.Extract(source, selection).Width);
        else Assert.Throws<InvalidOperationException>(() => PdfImagePlacementExtractor.Extract(source, selection));
    }

    [Fact]
    public void MalformedIccProfileWithASoftMaskIsRefusedByProfileValidation()
    {
        var source = PdfDocument.Open(Fixture());
        var image = Image(source);
        var entries = image.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
        var update = new PdfIncrementalUpdateBuilder(source);
        entries[N("ColorSpace")] = new PdfArray([N("ICCBased"), update.AddObject(new PdfStream(
            new PdfDictionary([new(N("N"), new PdfInteger(3))]), new byte[128]))]);
        var catalog = (PdfDictionary)Resolve(source, source.Trailer[N("Root")]);
        var pages = (PdfDictionary)Resolve(source, catalog[N("Pages")]);
        var page = (PdfDictionary)Resolve(source, ((PdfArray)Resolve(source, pages[N("Kids")]))[0]);
        var resources = (PdfDictionary)Resolve(source, page[N("Resources")]);
        var images = (PdfDictionary)Resolve(source, resources[N("XObject")]);
        var reference = Assert.IsType<PdfIndirectReference>(Assert.Single(images).Value);
        byte[] revised = update.ReplaceObject(reference.ObjectNumber,
            new PdfStream(new PdfDictionary(entries), image.EncodedData.Span)).Build(), original = revised.ToArray();
        source = PdfDocument.Open(revised);
        var error = Assert.Throws<FormatException>(() => PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0))));
        Assert.Equal("An ICC profile must contain its 128-byte header and tag-table count.", error.Message);
        Assert.Equal(original, revised);
    }

    private static byte[] Fixture(Action<Dictionary<PdfName, PdfObject>>? configure = null,
        byte[]? maskBytes = null, int width = 3, int height = 1, bool imageFlate = false)
    {
        var seed = PdfDocument.Open(new PdfDocumentBuilder().AddPage(width, height,
            Encoding.ASCII.GetBytes($"q {width} 0 0 {height} 0 0 cm /Image Do Q")).Build());
        var catalog = (PdfDictionary)Resolve(seed, seed.Trailer[N("Root")]);
        var pages = (PdfDictionary)Resolve(seed, catalog[N("Pages")]);
        var pageReference = (PdfIndirectReference)((PdfArray)Resolve(seed, pages[N("Kids")]))[0];
        var page = (PdfDictionary)seed.Resolve(pageReference);
        var update = new PdfIncrementalUpdateBuilder(seed);
        var maskEntries = new Dictionary<PdfName, PdfObject>
        {
            [N("Type")] = N("XObject"), [N("Subtype")] = N("Image"),
            [N("Width")] = new PdfInteger(width), [N("Height")] = new PdfInteger(height),
            [N("BitsPerComponent")] = new PdfInteger(8), [N("ColorSpace")] = N("DeviceGray")
        };
        configure?.Invoke(maskEntries);
        var mask = update.AddObject(new PdfStream(new PdfDictionary(maskEntries), maskBytes ?? [255, 128, 0]));
        var imageEntries = new Dictionary<PdfName, PdfObject>([
            new(N("Type"), N("XObject")), new(N("Subtype"), N("Image")),
            new(N("Width"), new PdfInteger(width)), new(N("Height"), new PdfInteger(height)),
            new(N("BitsPerComponent"), new PdfInteger(8)), new(N("ColorSpace"), N("DeviceRGB")),
            new(N("SMask"), mask)]);
        byte[] samples = width == 3 && height == 1
            ? [255, 0, 0, 0, 255, 0, 0, 0, 255] : new byte[width * height * 3];
        if (imageFlate)
        {
            using var output = new MemoryStream();
            using (var compressor = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
                compressor.Write(samples);
            samples = output.ToArray();
            imageEntries[N("Filter")] = N("FlateDecode");
        }
        var image = update.AddObject(new PdfStream(new PdfDictionary(imageEntries), samples));
        var entries = page.ToDictionary(pair => pair.Key, pair => pair.Value);
        entries[N("Resources")] = new PdfDictionary([new(N("XObject"), new PdfDictionary([new(N("Image"), image)]))]);
        return update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(entries)).Build();
    }

    private static PdfStream Image(PdfDocument document)
    {
        var catalog = (PdfDictionary)Resolve(document, document.Trailer[N("Root")]);
        var pages = (PdfDictionary)Resolve(document, catalog[N("Pages")]);
        var page = (PdfDictionary)Resolve(document, ((PdfArray)Resolve(document, pages[N("Kids")]))[0]);
        var resources = (PdfDictionary)Resolve(document, page[N("Resources")]);
        var images = (PdfDictionary)Resolve(document, resources[N("XObject")]);
        return Assert.IsType<PdfStream>(Resolve(document, Assert.Single(images).Value));
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
        => value is PdfIndirectReference reference ? document.Resolve(reference) : value;
    private static PdfName N(string value) => new(Encoding.ASCII.GetBytes(value));
}
