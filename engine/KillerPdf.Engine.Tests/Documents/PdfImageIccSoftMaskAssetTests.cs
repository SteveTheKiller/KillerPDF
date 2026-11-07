using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfImageIccSoftMaskAssetTests
{
    [Fact]
    public void RgbIccProfileAndRawSoftMaskSurviveExtractionAndAuthoredImageReuse()
    {
        byte[] profile = Convert.FromBase64String(BlueFromRedProfile);
        byte[] bytes = Fixture(profile), original = bytes.ToArray();
        var source = PdfDocument.Open(bytes);
        AssertBlueAlpha(source);
        var asset = PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0)));
        Assert.Throws<InvalidOperationException>(() => new PdfDocumentBuilder(new PdfVersion(1, 3))
            .AddPage(3, 1, new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 3, 1)).Build());
        var target = PdfDocument.Open(new PdfDocumentBuilder(new PdfVersion(1, 4))
            .AddPage(3, 1, new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 3, 1)).Build());
        foreach (var document in new[] { source, PdfDocument.Open(asset.ToPdfBytes()), target })
        {
            AssertGraph(document, profile, [128, 0, 255]);
            AssertBlueAlpha(document);
            var frame = new PdfPageRenderer(document).RenderFrame(0,
                new PdfRenderOptions(3, 1, transparentBackground: true));
            // Independent Decimal60 Bradford inversion from the fixed16 PCS column
            // (.14306640625, .06060791015625, .71417236328125), using ICC D50
            // (.9642, 1, .8249), gives these straight linear sRGB literals.
            float[] expected = [-.000046330892f, -.000012172480f, 1.000391902f];
            foreach (int pixel in new[] { 0, 2 })
                for (int channel = 0; channel < 3; channel++)
                    Assert.InRange(frame.Pixels[pixel * 4 + channel], expected[channel] - .000002f,
                        expected[channel] + .000002f);
            Assert.InRange(frame.Pixels[3], .50196075f, .50196082f);
            Assert.Equal(0, frame.Pixels[7]);
            Assert.Equal(1, frame.Pixels[11]);
            Assert.All(frame.Pixels.ToArray(), value => Assert.True(float.IsFinite(value)));
        }
        Assert.Equal(original, bytes);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void RawAndFlateImageProfileAndMaskPayloadsRemainIndependent(bool imageFlate, bool profileFlate, bool maskFlate)
    {
        byte[] profile = Convert.FromBase64String(BlueFromRedProfile);
        byte[] encodedProfile = profileFlate ? Compress(profile) : profile;
        byte[] mask = maskFlate ? Compress([128, 0, 255]) : [128, 0, 255];
        byte[] imageBytes = imageFlate ? Compress([255, 0, 0, 255, 0, 0, 255, 0, 0])
            : [255, 0, 0, 255, 0, 0, 255, 0, 0];
        byte[] bytes = Fixture(encodedProfile, mask, maskFlate, profileFlate: profileFlate, imageBytes: imageBytes,
            imageFlate: imageFlate), original = bytes.ToArray();
        var source = PdfDocument.Open(bytes);
        var asset = PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0)));
        var target = PdfDocument.Open(new PdfDocumentBuilder(new PdfVersion(1, 4))
            .AddPage(3, 1, new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 3, 1)).Build());
        foreach (var document in new[] { source, PdfDocument.Open(asset.ToPdfBytes()), target })
        {
            AssertGraph(document, encodedProfile, mask, imageBytes);
            AssertBlueAlpha(document);
        }
        Assert.Equal(original, bytes);
    }

    [Fact]
    public void CapturedPerceptualIntentSurvivesMaskedAssetReuseInRelativeDestination()
    {
        byte[] profile = Convert.FromBase64String(IntentProfile);
        var source = PdfDocument.Open(Fixture(profile, intent: "Perceptual"));
        AssertBlueAlpha(source);
        var asset = PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0)));
        var target = PdfDocument.Open(new PdfDocumentBuilder(new PdfVersion(1, 4)).AddPage(3, 1,
            new PdfContentStreamBuilder().SetRenderingIntent(PdfRenderingIntent.RelativeColorimetric)
                .DrawImage(asset, 0, 0, 3, 1)).Build());
        AssertBlueAlpha(target);
        Assert.Equal("Perceptual", Assert.IsType<PdfName>(Assert.Single(Images(target)).Dictionary[N("Intent")]).ValueAsLatin1());
        AssertGraph(target, profile, [128, 0, 255]);
    }

    [Fact]
    public void RepeatedAssetUseOwnsDistinctProfileAndMaskDependenciesWithoutChangingSource()
    {
        byte[] profile = Convert.FromBase64String(BlueFromRedProfile);
        byte[] bytes = Fixture(profile), original = bytes.ToArray();
        var source = PdfDocument.Open(bytes);
        var sourceImage = Assert.Single(Images(source));
        var asset = PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0)));
        var target = PdfDocument.Open(new PdfDocumentBuilder(new PdfVersion(1, 4)).AddPage(6, 1,
            new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 3, 1).DrawImage(asset, 3, 0, 3, 1)).Build());
        var targetImage = Assert.Single(Images(target));
        Assert.NotSame(sourceImage, targetImage);
        var sourceProfile = (PdfArray)sourceImage.Dictionary[N("ColorSpace")];
        var targetProfile = (PdfArray)targetImage.Dictionary[N("ColorSpace")];
        Assert.NotSame(Resolve(source, sourceProfile[1]), Resolve(target, targetProfile[1]));
        Assert.NotSame(Resolve(source, sourceImage.Dictionary[N("SMask")]), Resolve(target, targetImage.Dictionary[N("SMask")]));
        AssertGraph(source, profile, [128, 0, 255]);
        AssertGraph(target, profile, [128, 0, 255]);
        var rendered = new PdfPageRenderer(target).Render(0, new PdfRenderOptions(6, 1, transparentBackground: true));
        Assert.Empty(rendered.Diagnostics);
        byte[] pixels = rendered.Pixels.ToArray();
        Assert.Equal(new byte[] { 255, 0, 0, 128 }, pixels[..4]);
        Assert.Equal(new byte[] { 255, 0, 0, 128 }, pixels[12..16]);
        Assert.Equal(0, pixels[7]);
        Assert.Equal(0, pixels[19]);
        Assert.Equal(original, bytes);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("class")]
    [InlineData("CMYK")]
    [InlineData("signature")]
    public void MaskedAssetsStillValidateTheDeclaredIccProfile(string kind)
    {
        byte[] profile = Convert.FromBase64String(BlueFromRedProfile);
        if (kind == "revision") profile[8] = 4;
        else if (kind == "class") "bad!"u8.CopyTo(profile.AsSpan(12));
        else if (kind == "CMYK") "CMYK"u8.CopyTo(profile.AsSpan(16));
        else profile[36] = 0;
        byte[] bytes = Fixture(profile), original = bytes.ToArray();
        var source = PdfDocument.Open(bytes);
        var error = Record.Exception(() => PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0))));
        Assert.NotNull(error);
        if (kind == "signature") Assert.IsType<FormatException>(error);
        else Assert.IsType<NotSupportedException>(error);
        Assert.Equal(original, bytes);
    }

    [Theory]
    [InlineData("Decode")]
    [InlineData("Matte")]
    [InlineData("DecodeParms")]
    [InlineData("SMask")]
    [InlineData("Width")]
    public void ValidIccProfileDoesNotBypassUnsupportedMaskProperties(string key)
    {
        byte[] bytes = Fixture(Convert.FromBase64String(BlueFromRedProfile), configureMask: entries =>
            entries[N(key)] = key == "Width" ? new PdfInteger(2) : new PdfArray([new PdfInteger(1), new PdfInteger(0)])),
            original = bytes.ToArray();
        var source = PdfDocument.Open(bytes);
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0))));
        Assert.Equal(original, bytes);
    }

    [Fact]
    public void ValidIccProfileDoesNotBypassCorruptCompressedMaskValidation()
    {
        byte[] encoded = Compress([128, 0, 255]);
        encoded[^1] ^= 1;
        var source = PdfDocument.Open(Fixture(Convert.FromBase64String(BlueFromRedProfile), encoded, maskFlate: true));
        Assert.Throws<KillerPdf.Engine.Filters.PdfFilterException>(() => PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0))));
    }

    [Fact]
    public void TheSerializedAssetBudgetIncludesProfileImageAndMaskTogether()
    {
        byte[] profile = new byte[5 * 1024 * 1024];
        Convert.FromBase64String(BlueFromRedProfile).CopyTo(profile, 0);
        var source = PdfDocument.Open(Fixture(profile));
        var error = Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0))));
        Assert.Contains("detached image asset exceeds its size limit", error.Message);
    }

    private static void AssertBlueAlpha(PdfDocument document)
    {
        var rendered = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(3, 1, transparentBackground: true));
        Assert.Empty(rendered.Diagnostics);
        byte[] pixels = rendered.Pixels.ToArray();
        Assert.Equal(new byte[] { 255, 0, 0, 128 }, pixels[..4]);
        Assert.Equal(0, pixels[7]);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels[8..]);
    }

    private static void AssertGraph(PdfDocument document, byte[] profile, byte[] maskBytes, byte[]? imageBytes = null)
    {
        var image = Assert.Single(Images(document));
        Assert.Equal(imageBytes ?? [255, 0, 0, 255, 0, 0, 255, 0, 0], image.EncodedData.ToArray());
        var space = Assert.IsType<PdfArray>(image.Dictionary[N("ColorSpace")]);
        Assert.Equal("ICCBased", Assert.IsType<PdfName>(space[0]).ValueAsLatin1());
        var profileReference = Assert.IsType<PdfIndirectReference>(space[1]);
        var maskReference = Assert.IsType<PdfIndirectReference>(image.Dictionary[N("SMask")]);
        Assert.NotEqual(profileReference, maskReference);
        Assert.Equal(profile, Assert.IsType<PdfStream>(document.Resolve(profileReference)).EncodedData.ToArray());
        var mask = Assert.IsType<PdfStream>(document.Resolve(maskReference));
        Assert.Equal("DeviceGray", Assert.IsType<PdfName>(mask.Dictionary[N("ColorSpace")]).ValueAsLatin1());
        Assert.Equal(8, Assert.IsType<PdfInteger>(mask.Dictionary[N("BitsPerComponent")]).Value);
        Assert.Equal(maskBytes, mask.EncodedData.ToArray());
    }

    private static byte[] Fixture(byte[] profile, byte[]? maskBytes = null, bool maskFlate = false,
        string intent = "RelativeColorimetric", Action<Dictionary<PdfName, PdfObject>>? configureMask = null,
        bool profileFlate = false, byte[]? imageBytes = null, bool imageFlate = false)
    {
        var seed = PdfDocument.Open(new PdfDocumentBuilder().AddPage(3, 1,
            Encoding.ASCII.GetBytes($"/{intent} ri q 3 0 0 1 0 0 cm /Image Do Q")).Build());
        var catalog = (PdfDictionary)Resolve(seed, seed.Trailer[N("Root")]);
        var pages = (PdfDictionary)Resolve(seed, catalog[N("Pages")]);
        var pageReference = (PdfIndirectReference)((PdfArray)Resolve(seed, pages[N("Kids")]))[0];
        var page = (PdfDictionary)seed.Resolve(pageReference);
        var update = new PdfIncrementalUpdateBuilder(seed);
        var profileEntries = new Dictionary<PdfName, PdfObject>
        { [N("N")] = new PdfInteger(3), [N("Alternate")] = N("DeviceRGB") };
        if (profileFlate) profileEntries[N("Filter")] = N("FlateDecode");
        var profileReference = update.AddObject(new PdfStream(new PdfDictionary(profileEntries), profile));
        var maskEntries = new Dictionary<PdfName, PdfObject>
        {
            [N("Type")] = N("XObject"), [N("Subtype")] = N("Image"),
            [N("Width")] = new PdfInteger(3), [N("Height")] = new PdfInteger(1),
            [N("BitsPerComponent")] = new PdfInteger(8), [N("ColorSpace")] = N("DeviceGray")
        };
        if (maskFlate) maskEntries[N("Filter")] = N("FlateDecode");
        configureMask?.Invoke(maskEntries);
        var mask = update.AddObject(new PdfStream(new PdfDictionary(maskEntries), maskBytes ?? [128, 0, 255]));
        var imageEntries = new Dictionary<PdfName, PdfObject>([
            new(N("Type"), N("XObject")), new(N("Subtype"), N("Image")),
            new(N("Width"), new PdfInteger(3)), new(N("Height"), new PdfInteger(1)),
            new(N("BitsPerComponent"), new PdfInteger(8)),
            new(N("ColorSpace"), new PdfArray([N("ICCBased"), profileReference])),
            new(N("SMask"), mask)]);
        if (imageFlate) imageEntries[N("Filter")] = N("FlateDecode");
        var image = update.AddObject(new PdfStream(new PdfDictionary(imageEntries),
            imageBytes ?? [255, 0, 0, 255, 0, 0, 255, 0, 0]));
        var entries = page.ToDictionary(pair => pair.Key, pair => pair.Value);
        entries[N("Resources")] = new PdfDictionary([new(N("XObject"), new PdfDictionary([new(N("Image"), image)]))]);
        return update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(entries)).Build();
    }

    private static PdfStream[] Images(PdfDocument document)
    {
        var catalog = (PdfDictionary)Resolve(document, document.Trailer[N("Root")]);
        var pages = (PdfDictionary)Resolve(document, catalog[N("Pages")]);
        var page = (PdfDictionary)Resolve(document, ((PdfArray)Resolve(document, pages[N("Kids")]))[0]);
        var resources = (PdfDictionary)Resolve(document, page[N("Resources")]);
        var images = (PdfDictionary)Resolve(document, resources[N("XObject")]);
        return images.Select(entry => Assert.IsType<PdfStream>(Resolve(document, entry.Value))).ToArray();
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
        => value is PdfIndirectReference reference ? document.Resolve(reference) : value;
    private static PdfName N(string value) => new(Encoding.ASCII.GetBytes(value));

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            compressor.Write(bytes);
        return output.ToArray();
    }

    // Independent constant D50 PCS tables map relative intent to red and perceptual intent to blue.
    private const string IntentProfile = "AAACRAAAAAACEAAAc2NuclJHQiBYWVogB+oACwAHAAAAAAAAYWNzcAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAPbWAAEAAAAA0y0AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFZGVzYwAAAMAAAABkY3BydAAAASQAAAAUd3RwdAAAATgAAAAUQTJCMAAAAUwAAAB8QTJCMQAAAcgAAAB8ZGVzYwAAAAAAAAAKSW50ZW50UkdCAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAHRleHQAAAAAU3ludGhldGljAAAAWFlaIAAAAAAAAPbWAAEAAAAA0y1tZnQyAAAAAAMDAgAAAQAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAABAAAAAgACAAD//wAA//8AAP//ElAHwltqElAHwltqElAHwltqElAHwltqElAHwltqElAHwltqElAHwltqElAHwltqAAD//wAA//8AAP//bWZ0MgAAAAADAwIAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAQAAAAIAAgAA//8AAP//AAD//zfRHHsByTfRHHsByTfRHHsByTfRHHsByTfRHHsByTfRHHsByTfRHHsByTfRHHsByQAA//8AAP//AAD//w==";

    // A v2.1 linear RGB profile with D50-adapted red and blue primary columns exchanged.
    private const string BlueFromRedProfile = "AAAB2AAAAAACEAAAbW50clJHQiBYWVogB+oACwAHAAAAAAAAYWNzcAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAPbVAAEAAAAA0ywAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAJZGVzYwAAAPAAAABgY3BydAAAAVAAAAAUd3RwdAAAAWQAAAAUclRSQwAAAXgAAAAMZ1RSQwAAAYQAAAAMYlRSQwAAAZAAAAAMclhZWgAAAZwAAAAUZ1hZWgAAAbAAAAAUYlhZWgAAAcQAAAAUZGVzYwAAAAAAAAAGU3ludGgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAdGV4dAAAAABTeW50aGV0aWMAAABYWVogAAAAAAAA9tUAAQAAAADTLGN1cnYAAAAAAAAAAGN1cnYAAAAAAAAAAGN1cnYAAAAAAAAAAFhZWiAAAAAAAAAkoAAAD4QAALbUWFlaIAAAAAAAAGKTAAC3hQAAGNtYWVogAAAAAAAAb6IAADj2AAADkQ==";
}
