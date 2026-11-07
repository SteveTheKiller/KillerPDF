using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfImageFlateSoftMaskAssetTests
{
    [Fact]
    public void FlateGrayMaskPreservesEncodedBytesAndLiteralAlphaThroughImageReuse()
    {
        byte[] encoded = Compress([128, 0, 255]);
        byte[] bytes = Fixture(encoded), original = bytes.ToArray();
        var source = PdfDocument.Open(bytes);
        var asset = PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0)));
        Assert.Throws<InvalidOperationException>(() => new PdfDocumentBuilder(new PdfVersion(1, 3))
            .AddPage(3, 1, new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 3, 1)).Build());
        var authored = PdfDocument.Open(new PdfDocumentBuilder(new PdfVersion(1, 4))
            .AddPage(3, 1, new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 3, 1)).Build());
        foreach (var document in new[] { source, PdfDocument.Open(asset.ToPdfBytes()), authored })
        {
            var image = Image(document);
            Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255 }, image.EncodedData.ToArray());
            var mask = Assert.IsType<PdfStream>(Resolve(document, image.Dictionary[N("SMask")]));
            Assert.Equal("DeviceGray", Assert.IsType<PdfName>(mask.Dictionary[N("ColorSpace")]).ValueAsLatin1());
            Assert.Equal(8, Assert.IsType<PdfInteger>(mask.Dictionary[N("BitsPerComponent")]).Value);
            Assert.Equal("FlateDecode", Assert.IsType<PdfName>(mask.Dictionary[N("Filter")]).ValueAsLatin1());
            Assert.Equal(encoded, mask.EncodedData.ToArray());
            var pixels = new PdfPageRenderer(document).RenderFrame(0,
                new PdfRenderOptions(3, 1, transparentBackground: true)).Pixels.ToArray();
            Assert.InRange(pixels[0], .999f, 1.001f);
            Assert.InRange(pixels[1], -.001f, .001f);
            Assert.InRange(pixels[2], -.001f, .001f);
            Assert.InRange(pixels[3], .5019f, .5021f);
            Assert.Equal(0f, pixels[7]);
            Assert.InRange(pixels[8], -.001f, .001f);
            Assert.InRange(pixels[9], -.001f, .001f);
            Assert.InRange(pixels[10], .999f, 1.001f);
            Assert.Equal(1f, pixels[11]);
            Assert.All(pixels, value => Assert.True(float.IsFinite(value)));
        }
        Assert.Equal(original, bytes);
    }

    [Fact]
    public void SingleFlateMaskFilterArrayNormalizesWithoutReencoding()
    {
        byte[] encoded = Compress([128, 0, 255]);
        var source = PdfDocument.Open(Fixture(encoded,
            entries => entries[N("Filter")] = new PdfArray([N("FlateDecode")])));
        var asset = PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0)));
        var target = PdfDocument.Open(new PdfDocumentBuilder(new PdfVersion(1, 4))
            .AddPage(3, 1, new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 3, 1)).Build());
        var mask = Assert.IsType<PdfStream>(Resolve(target, Image(target).Dictionary[N("SMask")]));
        Assert.Equal("FlateDecode", Assert.IsType<PdfName>(mask.Dictionary[N("Filter")]).ValueAsLatin1());
        Assert.Equal(encoded, mask.EncodedData.ToArray());
        var pixels = new PdfPageRenderer(target).RenderFrame(0,
            new PdfRenderOptions(3, 1, transparentBackground: true)).Pixels.ToArray();
        Assert.InRange(pixels[3], .5019f, .5021f);
        Assert.Equal(0f, pixels[7]);
        Assert.Equal(1f, pixels[11]);
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("checksum")]
    [InlineData("missing-trailer")]
    [InlineData("truncated-body")]
    [InlineData("short-samples")]
    [InlineData("extra-samples")]
    [InlineData("expansion-budget")]
    public void InvalidOrOverexpandingFlateMasksCannotPublishAnAsset(string kind)
    {
        byte[] encoded = Compress([128, 0, 255]);
        switch (kind)
        {
            case "raw": encoded = [128, 0, 255]; break;
            case "checksum": encoded[^1] ^= 1; break;
            case "missing-trailer": encoded = encoded[..^4]; break;
            case "truncated-body": encoded = encoded[..4]; break;
            case "short-samples": encoded = Compress([128, 0]); break;
            case "extra-samples": encoded = Compress([128, 0, 255, 0]); break;
            case "expansion-budget": encoded = Compress(new byte[5 * 1024 * 1024 + 1]); break;
        }
        byte[] bytes = Fixture(encoded), original = bytes.ToArray();
        var source = PdfDocument.Open(bytes);
        var error = Record.Exception(() => PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0))));
        Assert.NotNull(error);
        Assert.True(error is FormatException or PdfFilterException, error.ToString());
        Assert.Equal(original, bytes);
        Assert.Equal(encoded, Assert.IsType<PdfStream>(Resolve(source,
            Image(source).Dictionary[N("SMask")])).EncodedData.ToArray());
    }

    [Theory]
    [InlineData("Width")]
    [InlineData("Height")]
    [InlineData("BitsPerComponent")]
    [InlineData("ColorSpace")]
    [InlineData("Decode")]
    [InlineData("DecodeParms")]
    [InlineData("Matte")]
    [InlineData("SMask")]
    [InlineData("Mask")]
    [InlineData("OC")]
    [InlineData("Filter")]
    [InlineData("filter-chain")]
    public void UnsupportedCompressedMaskInterpretationRemainsRefused(string key)
    {
        byte[] bytes = Fixture(Compress([128, 0, 255]), entries =>
        {
            if (key == "filter-chain") entries[N("Filter")] = new PdfArray([N("FlateDecode"), N("FlateDecode")]);
            else entries[N(key)] = key switch
            {
                "Width" or "Height" => new PdfInteger(2), "BitsPerComponent" => new PdfInteger(16),
                "ColorSpace" => N("DeviceRGB"), "Filter" => N("LZWDecode"),
                "Decode" => new PdfArray([new PdfInteger(1), new PdfInteger(0)]),
                "Matte" => new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(0)]),
                _ => new PdfDictionary([])
            };
        }), original = bytes.ToArray();
        var source = PdfDocument.Open(bytes);
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0))));
        Assert.Equal(original, bytes);
    }

    [Fact]
    public void EncodedFlateMaskBudgetIsCheckedBeforeDecoding()
    {
        var source = PdfDocument.Open(Fixture(new byte[5 * 1024 * 1024 + 1]));
        var error = Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0))));
        Assert.Contains("size limit", error.Message);
    }

    [Fact]
    public void CompressedMasksStillShareTheCombinedDecodedPixelBudget()
    {
        var source = PdfDocument.Open(Fixture(Compress(new byte[1_400_000]), width: 1400, height: 1000));
        var error = Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0))));
        Assert.Contains("size limit", error.Message);
    }

    [Fact]
    public void CompressedMaskExtractionObservesCancellationAndAssetOwnership()
    {
        byte[] bytes = Fixture(Compress([128, 0, 255]));
        var source = PdfDocument.Open(bytes);
        var selection = Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0));
        Assert.Throws<OperationCanceledException>(() => PdfImagePlacementExtractor.Extract(source, selection,
            new CancellationToken(true)));
        var asset = PdfImagePlacementExtractor.Extract(source, selection);
        byte[] expected = asset.ToPdfBytes(), returned = asset.ToPdfBytes();
        Array.Clear(bytes);
        Array.Clear(returned);
        Assert.Equal(expected, asset.ToPdfBytes());
    }

    private static byte[] Fixture(byte[] maskBytes,
        Action<Dictionary<PdfName, PdfObject>>? configure = null, int width = 3, int height = 1)
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
            [N("BitsPerComponent")] = new PdfInteger(8), [N("ColorSpace")] = N("DeviceGray"),
            [N("Filter")] = N("FlateDecode")
        };
        configure?.Invoke(maskEntries);
        var mask = update.AddObject(new PdfStream(new PdfDictionary(maskEntries), maskBytes));
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            new(N("Type"), N("XObject")), new(N("Subtype"), N("Image")),
            new(N("Width"), new PdfInteger(width)), new(N("Height"), new PdfInteger(height)),
            new(N("BitsPerComponent"), new PdfInteger(8)), new(N("ColorSpace"), N("DeviceRGB")),
            new(N("SMask"), mask)]), width == 3 && height == 1
                ? [255, 0, 0, 0, 255, 0, 0, 0, 255] : new byte[width * height * 3]));
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

    private static byte[] Compress(byte[] samples)
    {
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            compressor.Write(samples);
        return output.ToArray();
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
        => value is PdfIndirectReference reference ? document.Resolve(reference) : value;
    private static PdfName N(string value) => new(Encoding.ASCII.GetBytes(value));
}
