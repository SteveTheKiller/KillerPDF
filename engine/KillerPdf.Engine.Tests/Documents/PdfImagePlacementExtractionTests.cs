using System.Globalization;
using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfImagePlacementExtractionTests
{
    [Fact]
    public void FlateImageDetachesItsExactEncodedPayloadAndOriginalPixels()
    {
        byte[] compressed = Compress([255, 0, 0, 0, 255, 0]);
        byte[] source = FlateFixture(compressed), unchanged = source.ToArray();
        var document = PdfDocument.Open(source);
        var selection = new PdfPageContentReader(document).ReadImagePlacements(0)[1];
        var asset = PdfImagePlacementExtractor.Extract(document, selection);
        Assert.Equal(2, asset.Width);
        Assert.Equal(1, asset.Height);
        var detached = PdfDocument.Open(asset.ToPdfBytes());
        var page = new PdfPageContentReader(detached).Read(0);
        Assert.Equal(new PdfContentBounds(0, 0, 2, 1), Assert.Single(page.Images).BoundingBox);
        Assert.Empty(page.Letters);
        Assert.Empty(page.Paths);
        var image = DetachedImage(detached);
        Assert.Equal("FlateDecode", Assert.IsType<PdfName>(image.Dictionary[Name("Filter")]).ValueAsLatin1());
        Assert.Equal(compressed, image.EncodedData.ToArray());
        var frame = new PdfPageRenderer(detached).Render(0, new PdfRenderOptions(2, 1));
        Assert.Empty(frame.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 }, frame.Pixels.ToArray());
        Assert.Equal(unchanged, source);
        Assert.Equal(compressed, Assert.IsType<PdfStream>(document.Resolve(7)).EncodedData.ToArray());
        Assert.Equal(2, new PdfPageContentReader(document).Read(0).Images.Count);
        Assert.Single(new PdfPageContentReader(document).Read(1).Images);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleFlateFilterArrayAndIndirectMetadataNormalizeWithoutReencoding(bool indirect)
    {
        byte[] payload = Compress([255, 0, 0, 0, 255, 0]);
        var source = PdfDocument.Open(FlateFixture(payload, new PdfArray([Name("FlateDecode")]), indirect));
        var asset = PdfImagePlacementExtractor.Extract(source, new PdfPageContentReader(source).ReadImagePlacements(0)[1]);
        var detached = PdfDocument.Open(asset.ToPdfBytes());
        var image = DetachedImage(detached);
        Assert.Equal(payload, image.EncodedData.ToArray());
        Assert.Equal("FlateDecode", Assert.IsType<PdfName>(image.Dictionary[Name("Filter")]).ValueAsLatin1());
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 },
            new PdfPageRenderer(detached).Render(0, new PdfRenderOptions(2, 1)).Pixels.ToArray());
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("checksum")]
    [InlineData("missing-trailer")]
    [InlineData("truncated-body")]
    [InlineData("short-samples")]
    [InlineData("extra-samples")]
    [InlineData("expansion-budget")]
    public void InvalidOrOverexpandingFlateCannotPublishAnAsset(string kind)
    {
        byte[] payload = Compress([255, 0, 0, 0, 255, 0]);
        switch (kind)
        {
            case "raw": payload = [255, 0, 0, 0, 255, 0]; break;
            case "checksum": payload[^1] ^= 1; break;
            case "missing-trailer": payload = payload[..^4]; break;
            case "truncated-body": payload = payload[..4]; break;
            case "short-samples": payload = Compress([255, 0, 0, 0, 255]); break;
            case "extra-samples": payload = Compress([255, 0, 0, 0, 255, 0, 0]); break;
            case "expansion-budget": payload = Compress(new byte[5 * 1024 * 1024 + 1]); break;
        }
        byte[] bytes = FlateFixture(payload), unchanged = bytes.ToArray();
        var document = PdfDocument.Open(bytes);
        var selection = new PdfPageContentReader(document).ReadImagePlacements(0)[1];
        Exception? error = Record.Exception(() => PdfImagePlacementExtractor.Extract(document, selection));
        Assert.NotNull(error);
        Assert.True(error is FormatException or PdfFilterException, error.ToString());
        Assert.Equal(unchanged, bytes);
        Assert.Equal(payload, Assert.IsType<PdfStream>(document.Resolve(7)).EncodedData.ToArray());
    }

    [Fact]
    public void OversizedEncodedFlateIsRefusedBeforeDecoding()
    {
        var document = PdfDocument.Open(FlateFixture(new byte[5 * 1024 * 1024 + 1]));
        var selection = new PdfPageContentReader(document).ReadImagePlacements(0)[1];
        var error = Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(document, selection));
        Assert.Contains("size limit", error.Message);
    }

    // An ASCII text layer over Flate only spells the compressed bytes out; the picture extracts as that Flate data.
    [Theory]
    [InlineData("ASCII85Decode")]
    [InlineData("ASCIIHexDecode")]
    [InlineData("A85")]
    [InlineData("AHx")]
    public void FlateInsideAnAsciiTextLayerExtractsAsTheFlateUnderIt(string layer)
    {
        byte[] payload = Compress([255, 0, 0, 0, 255, 0]);
        byte[] wrapped = layer is "ASCII85Decode" or "A85" ? AsciiText.Ascii85(payload) : AsciiText.Hex(payload);
        var source = PdfDocument.Open(FlateFixture(wrapped, new PdfArray([Name(layer), Name("FlateDecode")])));

        var asset = PdfImagePlacementExtractor.Extract(source, new PdfPageContentReader(source).ReadImagePlacements(0)[1]);

        var detached = PdfDocument.Open(asset.ToPdfBytes());
        var image = DetachedImage(detached);
        Assert.Equal("FlateDecode", Assert.IsType<PdfName>(image.Dictionary[Name("Filter")]).ValueAsLatin1());
        Assert.Equal(payload, image.EncodedData.ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 },
            new PdfPageRenderer(detached).Render(0, new PdfRenderOptions(2, 1)).Pixels.ToArray());
    }

    // A text layer under the image filter, or two text layers, is not the producer pattern and stays refused.
    [Theory]
    [InlineData("FlateDecode", "ASCII85Decode")]
    [InlineData("ASCII85Decode", "ASCIIHexDecode")]
    public void OtherAsciiArrangementsStayRefused(string first, string second)
    {
        var source = PdfDocument.Open(FlateFixture(Compress([255, 0, 0, 0, 255, 0]), new PdfArray([Name(first), Name(second)])));
        Assert.Throws<NotSupportedException>(() =>
            PdfImagePlacementExtractor.Extract(source, new PdfPageContentReader(source).ReadImagePlacements(0)[1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FilterChainsAndDecodeParametersRemainUnsupported(bool decodeParameters)
    {
        byte[] bytes = FlateFixture(Compress([255, 0, 0, 0, 255, 0]), decodeParameters
            ? Name("FlateDecode") : new PdfArray([Name("FlateDecode"), Name("FlateDecode")]));
        var source = PdfDocument.Open(bytes);
        if (decodeParameters)
        {
            var image = Assert.IsType<PdfStream>(source.Resolve(7));
            var entries = image.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
            entries[Name("DecodeParms")] = new PdfDictionary([new(Name("Predictor"), new PdfInteger(1))]);
            source = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
                .ReplaceObject(7, new PdfStream(new PdfDictionary(entries), image.EncodedData.Span)).Build());
        }
        var selection = new PdfPageContentReader(source).ReadImagePlacements(0)[1];
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(source, selection));
    }

    [Fact]
    public void CompressedAssetOwnsItsBytesAndHonorsCancellation()
    {
        byte[] payload = Compress([255, 0, 0, 0, 255, 0]);
        byte[] bytes = FlateFixture(payload), unchanged = bytes.ToArray();
        var document = PdfDocument.Open(bytes);
        var selection = new PdfPageContentReader(document).ReadImagePlacements(0)[1];
        Assert.Throws<OperationCanceledException>(() => PdfImagePlacementExtractor.Extract(document, selection, new CancellationToken(true)));
        var asset = PdfImagePlacementExtractor.Extract(document, selection);
        Assert.Equal(unchanged, bytes);
        byte[] returned = asset.ToPdfBytes(), expected = returned.ToArray();
        Array.Clear(payload);
        Array.Clear(bytes);
        Array.Clear(returned);
        Assert.Equal(expected, asset.ToPdfBytes());
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 },
            new PdfPageRenderer(PdfDocument.Open(asset.ToPdfBytes())).Render(0, new PdfRenderOptions(2, 1)).Pixels.ToArray());
    }

    [Theory]
    [InlineData("-100 0 0 50 150 100", 2, 1, new byte[] { 255, 0, 0, 0, 0, 255 }, 75, 75, 125, 75)]
    [InlineData("100 0 0 -50 50 150", 1, 2, new byte[] { 255, 0, 0, 0, 0, 255 }, 100, 60, 100, 90)]
    public void ExtractingAFlippedPictureKeepsItFlippedWhenTheAssetIsDrawnAgain(
        string matrix, int width, int height, byte[] samples, int x1, int y1, int x2, int y2)
    {
        byte[] source = FlippedFixture(matrix, width, height, Compress(samples));
        var document = PdfDocument.Open(source);
        var selection = Assert.Single(new PdfPageContentReader(document).ReadImagePlacements(0));
        PdfImageAsset asset = PdfImagePlacementExtractor.Extract(document, selection);
        var target = PdfDocument.Open(new KillerPdf.Engine.Authoring.PdfDocumentBuilder()
            .AddPage(200, 200, new KillerPdf.Engine.Authoring.PdfContentStreamBuilder()
                .DrawImage(asset, 50, 100, 100, 50)).Build());

        var shown = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(200, 200));
        var pasted = new PdfPageRenderer(target).Render(0, new PdfRenderOptions(200, 200));

        foreach ((int px, int py) in new[] { (x1, y1), (x2, y2) })
        {
            Assert.Equal(PixelAt(shown, px, py), PixelAt(pasted, px, py));
        }
        Assert.NotEqual(PixelAt(shown, x1, y1), PixelAt(shown, x2, y2));
    }

    [Theory]
    [InlineData("-100 0 0 50 150 100", 2, 1, new byte[] { 255, 0, 0, 0, 0, 255 }, 75, 75, 125, 75)]
    [InlineData("100 0 0 -50 50 150", 1, 2, new byte[] { 255, 0, 0, 0, 0, 255 }, 100, 60, 100, 90)]
    public void AFlippedPictureKeptAsPdfBytesStaysFlippedWhenReadAndDrawnAgain(
        string matrix, int width, int height, byte[] samples, int x1, int y1, int x2, int y2)
    {
        // The viewer keeps a copied picture as the asset's PDF bytes and reads them back on paste and on save.
        byte[] source = FlippedFixture(matrix, width, height, Compress(samples));
        var document = PdfDocument.Open(source);
        PdfImageAsset copied = PdfImagePlacementExtractor.Extract(document, Assert.Single(new PdfPageContentReader(document).ReadImagePlacements(0)));
        var kept = PdfDocument.Open(copied.ToPdfBytes());
        PdfImageAsset pasted = PdfImagePlacementExtractor.Extract(kept, Assert.Single(new PdfPageContentReader(kept).ReadImagePlacements(0)));
        var target = PdfDocument.Open(new KillerPdf.Engine.Authoring.PdfDocumentBuilder()
            .AddPage(200, 200, new KillerPdf.Engine.Authoring.PdfContentStreamBuilder()
                .DrawImage(pasted, 50, 100, 100, 50)).Build());

        var shown = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(200, 200));
        var result = new PdfPageRenderer(target).Render(0, new PdfRenderOptions(200, 200));
        var preview = new PdfPageRenderer(kept).Render(0, new PdfRenderOptions(width * 2, height * 2));

        foreach ((int px, int py) in new[] { (x1, y1), (x2, y2) })
            Assert.Equal(PixelAt(shown, px, py), PixelAt(result, px, py));
        // The asset's own page, which the viewer shows as the picture, is mirrored the same way.
        Assert.Equal(PixelAt(shown, x1, y1), PixelAt(preview, 0, 0));
        Assert.Equal(PixelAt(shown, x2, y2), PixelAt(preview, preview.Width - 1, preview.Height - 1));
    }

    [Fact]
    public void AnUnmirroredAssetDrawsWithTheCallersMatrixUnchanged()
    {
        var document = PdfDocument.Open(FlateFixture(Compress([255, 0, 0, 0, 255, 0])));
        PdfImageAsset asset = PdfImagePlacementExtractor.Extract(document,
            new PdfPageContentReader(document).ReadImagePlacements(0)[1]);

        string plain = Encoding.ASCII.GetString(new KillerPdf.Engine.Authoring.PdfContentStreamBuilder()
            .DrawImage(asset, 50, 100, 100, 50).Build());

        Assert.Equal("q\n100 0 0 50 50 100 cm\n/Ia1 Do\nQ\n", plain);
    }

    private static (byte, byte, byte) PixelAt(PdfRenderedPage page, int x, int y)
    {
        int offset = (y * page.Width + x) * 4;
        var pixels = page.Pixels.Span;
        return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    private static byte[] FlippedFixture(string matrix, int width, int height, byte[] payload)
    {
        string content = $"q {matrix} cm /Im Do Q";
        using var pdf = new MemoryStream();
        var offsets = new List<long>();
        void Write(string text) => pdf.Write(Encoding.Latin1.GetBytes(text));
        void Object(string head, byte[]? body = null, string tail = "")
        {
            offsets.Add(pdf.Length);
            Write($"{offsets.Count} 0 obj\n{head}");
            if (body is not null) { Write("\nstream\n"); pdf.Write(body); Write("\nendstream"); }
            Write(tail + "\nendobj\n");
        }
        Write("%PDF-1.7\n");
        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im 5 0 R >> >> /Contents 4 0 R >>");
        Object($"<< /Length {content.Length} >>", Encoding.Latin1.GetBytes(content));
        Object($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {payload.Length} >>", payload);
        long xref = pdf.Length;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets) Write($"{offset:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    private static byte[] Compress(byte[] samples)
    {
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            compressor.Write(samples);
        return output.ToArray();
    }

    private static byte[] FlateFixture(byte[] payload, PdfObject? filter = null, bool indirect = false)
    {
        var source = PdfDocument.Open(Fixture());
        var image = Assert.IsType<PdfStream>(source.Resolve(7));
        var entries = image.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfObject value = filter ?? Name("FlateDecode");
        entries[Name("Filter")] = indirect ? update.AddObject(value) : value;
        return update
            .ReplaceObject(7, new PdfStream(new PdfDictionary(entries), payload)).Build();
    }

    private static PdfStream DetachedImage(PdfDocument detached)
    {
        var catalog = Assert.IsType<PdfDictionary>(Resolve(detached, detached.Trailer[Name("Root")]));
        var pages = Assert.IsType<PdfDictionary>(Resolve(detached, catalog[Name("Pages")]));
        var kids = Assert.IsType<PdfArray>(Resolve(detached, pages[Name("Kids")]));
        var page = Assert.IsType<PdfDictionary>(Resolve(detached, Assert.Single(kids)));
        var resources = Assert.IsType<PdfDictionary>(Resolve(detached, page[Name("Resources")]));
        var images = Assert.IsType<PdfDictionary>(Resolve(detached, resources[Name("XObject")]));
        return Assert.IsType<PdfStream>(Resolve(detached, Assert.Single(images).Value));
    }

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
            "Filter" => Name("LZWDecode"), "ColorSpace" => Name("DeviceCMYK"),
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
