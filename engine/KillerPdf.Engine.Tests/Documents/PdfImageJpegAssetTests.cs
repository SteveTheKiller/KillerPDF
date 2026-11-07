using System.Security.Cryptography;
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

public sealed class PdfImageJpegAssetTests
{
    [Fact]
    public void ExistingRgb8JpegRetainsItsEncodedPayloadAndIndependentColorsThroughAssetReuse()
    {
        byte[] jpeg = Convert.FromBase64String(Jpeg), original = jpeg.ToArray();
        Assert.Equal("45F3C92BC74308C43E88EB51614023E23983DFD3BB85B0016C38A6B9CF924C39",
            Convert.ToHexString(SHA256.HashData(jpeg)));
        var image = PdfImage.FromJpeg(jpeg);
        Assert.Equal(8, image.Width);
        Assert.Equal(16, image.Height);
        Assert.Equal(8, image.BitsPerComponent);
        Assert.Equal(PdfImageColorSpace.Rgb, image.ColorSpace);
        byte[] sourceBytes = new PdfDocumentBuilder().AddPage(8, 16,
            new PdfContentStreamBuilder().DrawImage(image, 0, 0, 8, 16)).Build();
        byte[] sourceBefore = sourceBytes.ToArray();
        var source = PdfDocument.Open(sourceBytes);
        AssertGraph(source, jpeg);
        AssertIndependentColors(source);
        PdfImageAsset asset;
        try
        {
            asset = PdfImagePlacementExtractor.Extract(source,
                Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0)));
        }
        finally
        {
            Assert.Equal(sourceBefore, sourceBytes);
            Assert.Equal(original, jpeg);
            AssertGraph(source, original);
        }
        Assert.Equal(8, asset.Width);
        Assert.Equal(16, asset.Height);
        var target = PdfDocument.Open(new PdfDocumentBuilder().AddPage(8, 16,
            new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 8, 16)).Build());
        foreach (var document in new[] { PdfDocument.Open(asset.ToPdfBytes()), target })
        {
            AssertGraph(document, jpeg);
            AssertIndependentColors(document);
        }
        Assert.Equal(sourceBefore, sourceBytes);
        Assert.Equal(original, jpeg);
        AssertGraph(source, original);
    }

    [Theory]
    [InlineData("missing-eoi")]
    [InlineData("truncated-entropy")]
    [InlineData("truncated-segment")]
    [InlineData("trailing-data")]
    [InlineData("dimensions")]
    public void IncompleteOrMismatchedJpegCannotPublishAnAsset(string kind)
    {
        byte[] jpeg = Convert.FromBase64String(Jpeg);
        jpeg = kind switch
        {
            "missing-eoi" => jpeg[..^2], "truncated-entropy" => [.. jpeg[..150], 0xff, 0xd9],
            "truncated-segment" => jpeg[..4], "trailing-data" => [.. jpeg, 0], _ => jpeg
        };
        byte[] bytes = Fixture(jpeg, entries =>
        {
            if (kind == "dimensions")
            {
                entries[N("Width")] = new PdfInteger(16);
                entries[N("Height")] = new PdfInteger(8);
            }
        }), original = bytes.ToArray();
        var source = PdfDocument.Open(bytes);
        var error = Record.Exception(() => PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0))));
        Assert.NotNull(error);
        Assert.True(error is FormatException or PdfFilterException, error.ToString());
        Assert.Equal(original, bytes);
        Assert.Equal(jpeg, Image(source).EncodedData.ToArray());
    }

    [Theory]
    [InlineData("progressive")]
    [InlineData("arithmetic")]
    [InlineData("twelve-bit")]
    [InlineData("ycbcr-identifiers")]
    [InlineData("jfif")]
    [InlineData("adobe")]
    [InlineData("profile")]
    [InlineData("second-scan")]
    public void OtherJpegProcessesTransformsAndMetadataRemainRefused(string kind)
    {
        byte[] jpeg = Convert.FromBase64String(Jpeg);
        int frame = jpeg.AsSpan().IndexOf(new byte[] { 0xff, 0xc0 });
        if (kind == "progressive") jpeg[frame + 1] = 0xc2;
        else if (kind == "arithmetic") jpeg[frame + 1] = 0xc9;
        else if (kind == "twelve-bit") jpeg[frame + 4] = 12;
        else if (kind == "ycbcr-identifiers")
        {
            jpeg[frame + 10] = 1;
            jpeg[frame + 13] = 2;
            jpeg[frame + 16] = 3;
        }
        else if (kind == "second-scan")
        {
            int scan = jpeg.AsSpan().IndexOf(new byte[] { 0xff, 0xda });
            jpeg = [.. jpeg[..^2], .. jpeg.AsSpan(scan, 14), 0xff, 0xd9];
        }
        else
        {
            byte marker = kind == "jfif" ? (byte)0xe0 : kind == "adobe" ? (byte)0xee : (byte)0xe2;
            byte[] payload = kind switch
            {
                "jfif" => [74, 70, 73, 70, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0],
                "adobe" => [65, 100, 111, 98, 101, 0, 100, 0, 0, 0, 0, 0],
                _ => [73, 67, 67, 95, 80, 82, 79, 70, 73, 76, 69, 0, 1, 1]
            };
            jpeg = [0xff, 0xd8, 0xff, marker, 0, (byte)(payload.Length + 2), .. payload, .. jpeg[2..]];
        }
        byte[] bytes = Fixture(jpeg), original = bytes.ToArray();
        var source = PdfDocument.Open(bytes);
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0))));
        Assert.Equal(original, bytes);
        Assert.Equal(jpeg, Image(source).EncodedData.ToArray());
    }

    [Theory]
    [InlineData("Decode")]
    [InlineData("DecodeParms")]
    [InlineData("SMask")]
    [InlineData("ColorSpace")]
    [InlineData("Intent")]
    [InlineData("BitsPerComponent")]
    [InlineData("filter-chain")]
    public void JpegAdmissionDoesNotExpandOtherImageInterpretation(string key)
    {
        byte[] bytes = Fixture(Convert.FromBase64String(Jpeg), entries =>
        {
            if (key == "filter-chain") entries[N("Filter")] = new PdfArray([N("DCTDecode"), N("DCTDecode")]);
            else entries[N(key)] = key switch
            {
                "BitsPerComponent" => new PdfInteger(16), "Intent" => N("RelativeColorimetric"),
                "ColorSpace" => new PdfArray([N("ICCBased"), new PdfDictionary([])]),
                _ => new PdfArray([new PdfInteger(0), new PdfInteger(1)])
            };
        }), original = bytes.ToArray();
        var source = PdfDocument.Open(bytes);
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0))));
        Assert.Equal(original, bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JpegAssetLimitsApplyBeforeDecoding(bool decoded)
    {
        byte[] bytes = Fixture(decoded ? Convert.FromBase64String(Jpeg) : new byte[5 * 1024 * 1024 + 1], entries =>
        {
            if (decoded)
            {
                entries[N("Width")] = new PdfInteger(1800);
                entries[N("Height")] = new PdfInteger(1000);
            }
        });
        var source = PdfDocument.Open(bytes);
        var error = Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0))));
        Assert.Contains("size limit", error.Message);
    }

    [Fact]
    public void JpegAssetsKeepTheirPdf10VersionAndObserveCancellation()
    {
        var source = PdfDocument.Open(Fixture(Convert.FromBase64String(Jpeg)));
        var selected = Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0));
        Assert.Throws<OperationCanceledException>(() => PdfImagePlacementExtractor.Extract(source, selected,
            new CancellationToken(true)));
        var asset = PdfImagePlacementExtractor.Extract(source, selected);
        var target = PdfDocument.Open(new PdfDocumentBuilder(PdfVersion.Pdf10).AddPage(8, 16,
            new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 8, 16)).Build());
        Assert.Equal(PdfVersion.Pdf10, target.Header.Version);
        AssertGraph(target, Convert.FromBase64String(Jpeg));
        AssertIndependentColors(target);
    }

    private static byte[] Fixture(byte[] jpeg, Action<Dictionary<PdfName, PdfObject>>? configure = null)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(8, 16,
            new PdfContentStreamBuilder().DrawImage(PdfImage.FromJpeg(Convert.FromBase64String(Jpeg)), 0, 0, 8, 16)).Build());
        var catalog = (PdfDictionary)Resolve(source, source.Trailer[N("Root")]);
        var pages = (PdfDictionary)Resolve(source, catalog[N("Pages")]);
        var page = (PdfDictionary)Resolve(source, ((PdfArray)Resolve(source, pages[N("Kids")]))[0]);
        var resources = (PdfDictionary)Resolve(source, page[N("Resources")]);
        var images = (PdfDictionary)Resolve(source, resources[N("XObject")]);
        var reference = Assert.IsType<PdfIndirectReference>(Assert.Single(images).Value);
        var entries = Image(source).Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
        configure?.Invoke(entries);
        return new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(reference.ObjectNumber, new PdfStream(new PdfDictionary(entries), jpeg)).Build();
    }

    private static void AssertIndependentColors(PdfDocument document)
    {
        var frame = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(8, 16));
        Assert.Empty(frame.Diagnostics);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 8; x++)
                Assert.Equal(y < 8 ? new byte[] { 96, 80, 64, 255 } : new byte[] { 224, 208, 192, 255 },
                    frame.Pixels.Span.Slice((y * 8 + x) * 4, 4).ToArray());
    }

    private static void AssertGraph(PdfDocument document, byte[] jpeg)
    {
        var image = Image(document);
        Assert.Equal("DeviceRGB", Assert.IsType<PdfName>(image.Dictionary[N("ColorSpace")]).ValueAsLatin1());
        Assert.Equal("DCTDecode", Assert.IsType<PdfName>(image.Dictionary[N("Filter")]).ValueAsLatin1());
        Assert.False(image.Dictionary.ContainsKey(N("Decode")));
        Assert.False(image.Dictionary.ContainsKey(N("DecodeParms")));
        Assert.Equal(jpeg, image.EncodedData.ToArray());
    }

    private static PdfStream Image(PdfDocument document)
    {
        var catalog = Assert.IsType<PdfDictionary>(Resolve(document, document.Trailer[N("Root")]));
        var pages = Assert.IsType<PdfDictionary>(Resolve(document, catalog[N("Pages")]));
        var kids = Assert.IsType<PdfArray>(Resolve(document, pages[N("Kids")]));
        var page = Assert.IsType<PdfDictionary>(Resolve(document, Assert.Single(kids)));
        var resources = Assert.IsType<PdfDictionary>(Resolve(document, page[N("Resources")]));
        var images = Assert.IsType<PdfDictionary>(Resolve(document, resources[N("XObject")]));
        return Assert.IsType<PdfStream>(Resolve(document, Assert.Single(images).Value));
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
        => value is PdfIndirectReference reference ? document.Resolve(reference) : value;
    private static PdfName N(string value) => new(Encoding.ASCII.GetBytes(value));

    // Existing synthetic direct-RGB fixture, independently decoded with Windows WIC.
    private const string Jpeg = "/9j/2wBDAAEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/wAARCAAQAAgDUhEARxEAQhEA/8QAKQAAAAQAAAAAAAAAAAAAAAAAAAkKCxABAAAAAAAAAAAAAAAAAAAAAP/aAAwDUgBHAEIAAD8AT/ifxf5wAOABwAf/2Q==";
}
