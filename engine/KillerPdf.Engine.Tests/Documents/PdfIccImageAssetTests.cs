using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfIccImageAssetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColoredPatternsAndTypedStampsRetainTheAdditionalProfileDependency(bool stamp)
    {
        byte[] profile = Convert.FromBase64String(BlueFromRedProfile);
        var source = PdfDocument.Open(Fixture(profile));
        var asset = PdfImagePlacementExtractor.Extract(source,
            Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0)));
        PdfDocument target;
        if (stamp)
        {
            var appearance = new PdfFormXObject(1, 1, new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 1, 1));
            var blank = PdfDocument.Open(new PdfDocumentBuilder(PdfVersion.Pdf10).AddBlankPage(1, 1).Build());
            target = PdfDocument.Open(new PdfIncrementalAnnotationEditor(blank).AddStamp(0, 0, 0, 1, 1, appearance).Build());
        }
        else
        {
            var pattern = new PdfTilingPattern(1, 1, new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 1, 1));
            target = PdfDocument.Open(new PdfDocumentBuilder(new PdfVersion(1, 3)).AddPage(1, 1,
                new PdfContentStreamBuilder().SetFillPattern(pattern).Rectangle(0, 0, 1, 1).Fill()).Build());
        }
        AssertBlue(target);
        var catalog = (PdfDictionary)Resolve(target, target.Trailer[N("Root")]);
        var pages = (PdfDictionary)Resolve(target, catalog[N("Pages")]);
        var page = (PdfDictionary)Resolve(target, ((PdfArray)Resolve(target, pages[N("Kids")]))[0]);
        PdfStream appearanceStream;
        if (stamp)
        {
            Assert.Equal("1.4", Assert.IsType<PdfName>(catalog[N("Version")]).ValueAsLatin1());
            var annotations = Assert.IsType<PdfArray>(Resolve(target, page[N("Annots")]));
            var annotation = Assert.IsType<PdfDictionary>(Resolve(target, Assert.Single(annotations)));
            var ap = Assert.IsType<PdfDictionary>(Resolve(target, annotation[N("AP")]));
            appearanceStream = Assert.IsType<PdfStream>(Resolve(target, ap[N("N")]));
        }
        else
        {
            var pageResources = (PdfDictionary)Resolve(target, page[N("Resources")]);
            var patterns = (PdfDictionary)Resolve(target, pageResources[N("Pattern")]);
            appearanceStream = Assert.IsType<PdfStream>(Resolve(target, Assert.Single(patterns).Value));
        }
        var resources = (PdfDictionary)Resolve(target, appearanceStream.Dictionary[N("Resources")]);
        var images = (PdfDictionary)Resolve(target, resources[N("XObject")]);
        var image = Assert.IsType<PdfStream>(Resolve(target, Assert.Single(images).Value));
        Assert.Equal(new byte[] { 255, 0, 0 }, image.EncodedData.ToArray());
        var space = Assert.IsType<PdfArray>(image.Dictionary[N("ColorSpace")]);
        var profileReference = Assert.IsType<PdfIndirectReference>(space[1]);
        Assert.Equal(profile, Assert.IsType<PdfStream>(target.Resolve(profileReference)).EncodedData.ToArray());
    }

    [Fact]
    public void SharedSourceImageOccurrencesCaptureTheirOwnIntentsInSeparateAssets()
    {
        byte[] profile = Convert.FromBase64String(IntentProfile);
        var document = PdfDocument.Open(Fixture(profile,
            "q /Perceptual ri /Image Do Q q /RelativeColorimetric ri 1 0 0 1 1 0 cm /Image Do Q", pageWidth: 2));
        var selections = new PdfPageContentReader(document).ReadImagePlacements(0);
        Assert.Equal(2, selections.Count);
        Assert.All(selections, selection => Assert.True(selection.CanSetBounds));
        var first = PdfImagePlacementExtractor.Extract(document, selections[0]);
        var second = PdfImagePlacementExtractor.Extract(document, selections[1]);
        var target = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            new PdfContentStreamBuilder().SetRenderingIntent(PdfRenderingIntent.Saturation)
                .DrawImage(first, 0, 0, 1, 1).DrawImage(second, 1, 0, 1, 1)).Build());
        var rendered = new PdfPageRenderer(target).Render(0, new PdfRenderOptions(2, 1));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 }, rendered.Pixels.ToArray());
        var catalog = (PdfDictionary)Resolve(target, target.Trailer[N("Root")]);
        var pages = (PdfDictionary)Resolve(target, catalog[N("Pages")]);
        var page = (PdfDictionary)Resolve(target, ((PdfArray)Resolve(target, pages[N("Kids")]))[0]);
        var resources = (PdfDictionary)Resolve(target, page[N("Resources")]);
        var images = (PdfDictionary)Resolve(target, resources[N("XObject")]);
        Assert.Equal(2, images.Count);
        foreach (var entry in images)
        {
            var image = Assert.IsType<PdfStream>(Resolve(target, entry.Value));
            Assert.Equal(new byte[] { 255, 0, 0 }, image.EncodedData.ToArray());
            var space = Assert.IsType<PdfArray>(image.Dictionary[N("ColorSpace")]);
            var reference = Assert.IsType<PdfIndirectReference>(space[1]);
            Assert.Equal(profile, Assert.IsType<PdfStream>(target.Resolve(reference)).EncodedData.ToArray());
        }
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("Lab")]
    [InlineData("CMYK")]
    public void UnsupportedDeclaredProfileInputsRemainExplicitRefusals(string mode)
    {
        byte[] profile = Convert.FromBase64String(BlueFromRedProfile);
        if (mode == "revision") { profile[8] = 4; profile[9] = 0x20; }
        else Encoding.ASCII.GetBytes(mode == "Lab" ? "Lab " : "CMYK").CopyTo(profile, 16);
        byte[] source = Fixture(profile), original = source.ToArray();
        var document = PdfDocument.Open(source);
        var selection = Assert.Single(new PdfPageContentReader(document).ReadImagePlacements(0));
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(document, selection));
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("missing-trailer")]
    [InlineData("truncated-body")]
    [InlineData("decoded-budget")]
    public void CorruptOrOversizedFlateProfilesNeverBecomeDetachedAssets(string mode)
    {
        byte[] profile = mode == "decoded-budget" ? new byte[5 * 1024 * 1024 + 1]
            : Convert.FromBase64String(BlueFromRedProfile);
        byte[] source = Fixture(profile, profileFlate: true, encodedProfile: bytes =>
        {
            if (mode == "checksum") bytes[^1] ^= 1;
            return mode switch { "missing-trailer" => bytes[..^4], "truncated-body" => bytes[..4], _ => bytes };
        }), original = source.ToArray();
        var document = PdfDocument.Open(source);
        var selection = Assert.Single(new PdfPageContentReader(document).ReadImagePlacements(0));
        var error = Record.Exception(() => PdfImagePlacementExtractor.Extract(document, selection));
        Assert.NotNull(error);
        Assert.True(error is FormatException or PdfFilterException, error.ToString());
        Assert.Equal(original, source);
        Assert.Single(new PdfPageContentReader(document).ReadImagePlacements(0));
    }

    [Fact]
    public void NestedFormAssetsNegotiateTheirIccVersionBeforeWriting()
    {
        var document = PdfDocument.Open(Fixture(Convert.FromBase64String(BlueFromRedProfile)));
        var asset = PdfImagePlacementExtractor.Extract(document,
            Assert.Single(new PdfPageContentReader(document).ReadImagePlacements(0)));
        var inner = new PdfFormXObject(1, 1, new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 1, 1));
        var outer = new PdfFormXObject(1, 1, new PdfContentStreamBuilder().DrawForm(inner, 0, 0));
        Assert.Throws<InvalidOperationException>(() => new PdfDocumentBuilder(new PdfVersion(1, 2))
            .AddPage(1, 1, new PdfContentStreamBuilder().DrawForm(outer, 0, 0)).Build());
        var target = PdfDocument.Open(new PdfDocumentBuilder(new PdfVersion(1, 3))
            .AddPage(1, 1, new PdfContentStreamBuilder().DrawForm(outer, 0, 0)).Build());
        Assert.Equal(new PdfVersion(1, 3), target.Header.Version);
        AssertBlue(target);
        Assert.Single(new PdfPageContentReader(target).Read(0).Images);
    }

    [Theory]
    [InlineData("N")]
    [InlineData("Alternate")]
    [InlineData("Range")]
    [InlineData("DecodeParms")]
    [InlineData("Metadata")]
    [InlineData("Filter")]
    public void UnsupportedProfileMetadataRefusesExtractionWithoutChangingTheSource(string property)
    {
        byte[] source = Fixture(Convert.FromBase64String(BlueFromRedProfile), configureProfile: entries =>
        {
            entries[N(property)] = property switch
            {
                "N" => new PdfInteger(1), "Alternate" => N("DeviceGray"), "Filter" => N("DCTDecode"),
                "Range" => new PdfArray([new PdfInteger(0), new PdfInteger(1), new PdfInteger(0),
                    new PdfInteger(2), new PdfInteger(0), new PdfInteger(1)]),
                _ => new PdfDictionary([])
            };
        }), original = source.ToArray();
        var document = PdfDocument.Open(source);
        var selection = Assert.Single(new PdfPageContentReader(document).ReadImagePlacements(0));
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(document, selection));
        Assert.Equal(original, source);
        Assert.Single(new PdfPageContentReader(document).ReadImagePlacements(0));
    }

    [Fact]
    public void ExplicitUnitRealRangesPreserveTheOriginalProfileAndCancellationIsObserved()
    {
        byte[] profile = Convert.FromBase64String(BlueFromRedProfile);
        var document = PdfDocument.Open(Fixture(profile, configureProfile: entries =>
            entries[N("Range")] = new PdfArray([new PdfReal(0), new PdfReal(1), new PdfReal(0),
                new PdfReal(1), new PdfReal(0), new PdfReal(1)])));
        var selection = Assert.Single(new PdfPageContentReader(document).ReadImagePlacements(0));
        Assert.Throws<OperationCanceledException>(() => PdfImagePlacementExtractor.Extract(document, selection,
            new CancellationToken(true)));
        var asset = PdfImagePlacementExtractor.Extract(document, selection);
        var detached = PdfDocument.Open(asset.ToPdfBytes());
        AssertGraph(detached, profile);
        AssertBlue(detached);
    }

    [Fact]
    public void UndefinedIccProfileClassIsNotAdmittedAsADetachedColorResource()
    {
        byte[] profile = Convert.FromBase64String(BlueFromRedProfile);
        "bad!"u8.CopyTo(profile.AsSpan(12));
        byte[] source = Fixture(profile), original = source.ToArray();
        var document = PdfDocument.Open(source);
        var selection = Assert.Single(new PdfPageContentReader(document).ReadImagePlacements(0));
        Assert.Throws<NotSupportedException>(() => PdfImagePlacementExtractor.Extract(document, selection));
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RawAndFlateProfileAndImagePayloadsAreRetainedWithoutReencoding(bool imageFlate, bool profileFlate)
    {
        byte[] profile = Convert.FromBase64String(BlueFromRedProfile);
        byte[] samples = [255, 0, 0];
        byte[] encodedProfile = profileFlate ? Compress(profile) : profile;
        byte[] encodedSamples = imageFlate ? Compress(samples) : samples;
        var document = PdfDocument.Open(Fixture(profile, imageFlate: imageFlate, profileFlate: profileFlate));
        var asset = PdfImagePlacementExtractor.Extract(document,
            Assert.Single(new PdfPageContentReader(document).ReadImagePlacements(0)));
        var detached = PdfDocument.Open(asset.ToPdfBytes());
        AssertGraph(detached, encodedProfile, encodedSamples);
        AssertBlue(detached);
        var target = PdfDocument.Open(new PdfDocumentBuilder(new PdfVersion(1, 3)).AddPage(1, 1,
            new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 1, 1)).Build());
        AssertGraph(target, encodedProfile, encodedSamples);
        AssertBlue(target);
    }

    [Fact]
    public void ImageRenderingIntentOverridesPageStateAndSurvivesReuse()
    {
        byte[] profile = Convert.FromBase64String(IntentProfile);
        var document = PdfDocument.Open(Fixture(profile, "/Perceptual ri /Image Do", "RelativeColorimetric"));
        byte[] red = [0, 0, 255, 255];
        Assert.Equal(red, new PdfPageRenderer(document).Render(0, new PdfRenderOptions(1, 1)).Pixels.ToArray());
        var asset = PdfImagePlacementExtractor.Extract(document,
            Assert.Single(new PdfPageContentReader(document).ReadImagePlacements(0)));
        var target = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            new PdfContentStreamBuilder().SetRenderingIntent(PdfRenderingIntent.Perceptual)
                .DrawImage(asset, 0, 0, 1, 1)).Build());
        var rendered = new PdfPageRenderer(target).Render(0, new PdfRenderOptions(1, 1));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(red, rendered.Pixels.ToArray());
        AssertGraph(target, profile);
    }

    [Theory]
    [InlineData("q /Perceptual ri /Image Do Q", true)]
    [InlineData("/Perceptual ri q /RelativeColorimetric ri Q /Image Do", true)]
    [InlineData("q /Perceptual ri Q /Image Do", false)]
    [InlineData("/Image Do /Perceptual ri", false)]
    [InlineData("/Perceptual ri /Image Do /RelativeColorimetric ri", true)]
    [InlineData("q /Perceptual ri q /RelativeColorimetric ri /Image Do Q Q", false)]
    public void CapturedIntentTracksGraphicsStateAtTheSelectedOccurrence(string content, bool blue)
    {
        var document = PdfDocument.Open(Fixture(Convert.FromBase64String(IntentProfile), content));
        byte[] expected = blue ? [255, 0, 0, 255] : [0, 0, 255, 255];
        var source = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(1, 1));
        Assert.Empty(source.Diagnostics);
        Assert.Equal(expected, source.Pixels.ToArray());
        var asset = PdfImagePlacementExtractor.Extract(document,
            Assert.Single(new PdfPageContentReader(document).ReadImagePlacements(0)));
        var target = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            new PdfContentStreamBuilder().SetRenderingIntent(blue ? PdfRenderingIntent.RelativeColorimetric
                : PdfRenderingIntent.Perceptual).DrawImage(asset, 0, 0, 1, 1)).Build());
        var rendered = new PdfPageRenderer(target).Render(0, new PdfRenderOptions(1, 1));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(expected, rendered.Pixels.ToArray());
    }

    // Independent constant D50 PCS tables map relative intent to red and perceptual intent to blue.
    private const string IntentProfile = "AAACRAAAAAACEAAAc2NuclJHQiBYWVogB+oACwAHAAAAAAAAYWNzcAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAPbWAAEAAAAA0y0AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFZGVzYwAAAMAAAABkY3BydAAAASQAAAAUd3RwdAAAATgAAAAUQTJCMAAAAUwAAAB8QTJCMQAAAcgAAAB8ZGVzYwAAAAAAAAAKSW50ZW50UkdCAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAHRleHQAAAAAU3ludGhldGljAAAAWFlaIAAAAAAAAPbWAAEAAAAA0y1tZnQyAAAAAAMDAgAAAQAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAABAAAAAgACAAD//wAA//8AAP//ElAHwltqElAHwltqElAHwltqElAHwltqElAHwltqElAHwltqElAHwltqElAHwltqAAD//wAA//8AAP//bWZ0MgAAAAADAwIAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAQAAAAIAAgAA//8AAP//AAD//zfRHHsByTfRHHsByTfRHHsByTfRHHsByTfRHHsByTfRHHsByTfRHHsByTfRHHsByQAA//8AAP//AAD//w==";

    [Fact]
    public void ExtractedImageKeepsPerceptualSourceIntentInADefaultRelativeDestination()
    {
        byte[] profile = Convert.FromBase64String(IntentProfile);
        byte[] source = Fixture(profile, "/Perceptual ri /Image Do"), original = source.ToArray();
        var document = PdfDocument.Open(source);
        AssertBlue(document);
        var selection = Assert.Single(new PdfPageContentReader(document).ReadImagePlacements(0));
        Assert.True(selection.CanSetBounds);
        var asset = PdfImagePlacementExtractor.Extract(document, selection);
        AssertBlue(PdfDocument.Open(asset.ToPdfBytes()));
        var target = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 1, 1)).Build());
        AssertBlue(target);
        AssertGraph(target, profile);
        Assert.Equal(original, source);
    }

    // A v2.1 linear RGB profile with D50-adapted red and blue primary columns exchanged.
    private const string BlueFromRedProfile = "AAAB2AAAAAACEAAAbW50clJHQiBYWVogB+oACwAHAAAAAAAAYWNzcAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAPbVAAEAAAAA0ywAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAJZGVzYwAAAPAAAABgY3BydAAAAVAAAAAUd3RwdAAAAWQAAAAUclRSQwAAAXgAAAAMZ1RSQwAAAYQAAAAMYlRSQwAAAZAAAAAMclhZWgAAAZwAAAAUZ1hZWgAAAbAAAAAUYlhZWgAAAcQAAAAUZGVzYwAAAAAAAAAGU3ludGgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAdGV4dAAAAABTeW50aGV0aWMAAABYWVogAAAAAAAA9tUAAQAAAADTLGN1cnYAAAAAAAAAAGN1cnYAAAAAAAAAAGN1cnYAAAAAAAAAAFhZWiAAAAAAAAAkoAAAD4QAALbUWFlaIAAAAAAAAGKTAAC3hQAAGNtYWVogAAAAAAAAb6IAADj2AAADkQ==";

    [Fact]
    public void ExtractionAndAuthoringPreserveRgbIccBytesAndSourceColor()
    {
        byte[] profile = Convert.FromBase64String(BlueFromRedProfile);
        byte[] source = Fixture(profile), original = source.ToArray();
        var document = PdfDocument.Open(source);
        AssertBlue(document);
        var selection = Assert.Single(new PdfPageContentReader(document).ReadImagePlacements(0));
        Assert.True(selection.CanSetBounds);
        var asset = PdfImagePlacementExtractor.Extract(document, selection);
        Array.Clear(asset.ToPdfBytes());
        var detached = PdfDocument.Open(asset.ToPdfBytes());
        AssertGraph(detached, profile);
        AssertBlue(detached);
        var target = PdfDocument.Open(new PdfDocumentBuilder(new PdfVersion(1, 3)).AddPage(1, 1,
            new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 1, 1)).Build());
        AssertGraph(target, profile);
        AssertBlue(target);
        Assert.Equal(original, source);
        AssertGraph(document, profile);
        Assert.Single(new PdfPageContentReader(document).ReadImagePlacements(0));
    }

    private static void AssertBlue(PdfDocument document)
    {
        var frame = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(1, 1));
        Assert.Empty(frame.Diagnostics);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, frame.Pixels.ToArray());
    }

    private static void AssertGraph(PdfDocument document, byte[] profile, byte[]? samples = null)
    {
        var catalog = (PdfDictionary)Resolve(document, document.Trailer[N("Root")]);
        var pages = (PdfDictionary)Resolve(document, catalog[N("Pages")]);
        var page = (PdfDictionary)Resolve(document, ((PdfArray)Resolve(document, pages[N("Kids")]))[0]);
        var resources = (PdfDictionary)Resolve(document, page[N("Resources")]);
        var images = (PdfDictionary)Resolve(document, resources[N("XObject")]);
        var image = Assert.IsType<PdfStream>(Resolve(document, Assert.Single(images).Value));
        Assert.Equal(samples ?? [255, 0, 0], image.EncodedData.ToArray());
        var space = Assert.IsType<PdfArray>(image.Dictionary[N("ColorSpace")]);
        Assert.Equal(2, space.Count);
        Assert.Equal("ICCBased", Assert.IsType<PdfName>(space[0]).ValueAsLatin1());
        var reference = Assert.IsType<PdfIndirectReference>(space[1]);
        var retained = Assert.IsType<PdfStream>(document.Resolve(reference));
        Assert.Equal(profile, retained.EncodedData.ToArray());
        Assert.Equal(3, Assert.IsType<PdfInteger>(retained.Dictionary[N("N")]).Value);
    }

    private static byte[] Fixture(byte[] profile, string content = "/Image Do", string? imageIntent = null,
        bool imageFlate = false, bool profileFlate = false,
        Action<Dictionary<PdfName, PdfObject>>? configureProfile = null,
        Func<byte[], byte[]>? encodedProfile = null, int pageWidth = 1)
    {
        var seed = PdfDocument.Open(new PdfDocumentBuilder().AddPage(pageWidth, 1, Encoding.ASCII.GetBytes(content)).Build());
        var catalog = (PdfDictionary)Resolve(seed, seed.Trailer[N("Root")]);
        var pages = (PdfDictionary)Resolve(seed, catalog[N("Pages")]);
        var pageReference = (PdfIndirectReference)((PdfArray)Resolve(seed, pages[N("Kids")]))[0];
        var page = (PdfDictionary)seed.Resolve(pageReference);
        var update = new PdfIncrementalUpdateBuilder(seed);
        var profileEntries = new Dictionary<PdfName, PdfObject>
        { [N("N")] = new PdfInteger(3), [N("Alternate")] = N("DeviceRGB") };
        if (profileFlate) profileEntries.Add(N("Filter"), N("FlateDecode"));
        configureProfile?.Invoke(profileEntries);
        byte[] profilePayload = profileFlate ? Compress(profile) : profile;
        if (encodedProfile is not null) profilePayload = encodedProfile(profilePayload);
        var icc = update.AddObject(new PdfStream(new PdfDictionary(profileEntries),
            profilePayload));
        var imageEntries = new Dictionary<PdfName, PdfObject>
        {
            [N("Type")] = N("XObject"), [N("Subtype")] = N("Image"),
            [N("Width")] = new PdfInteger(1), [N("Height")] = new PdfInteger(1),
            [N("BitsPerComponent")] = new PdfInteger(8),
            [N("ColorSpace")] = new PdfArray([N("ICCBased"), icc])
        };
        if (imageIntent is not null) imageEntries.Add(N("Intent"), N(imageIntent));
        if (imageFlate) imageEntries.Add(N("Filter"), N("FlateDecode"));
        byte[] samples = [255, 0, 0];
        var image = update.AddObject(new PdfStream(new PdfDictionary(imageEntries), imageFlate ? Compress(samples) : samples));
        var entries = page.ToDictionary(pair => pair.Key, pair => pair.Value);
        entries[N("Resources")] = new PdfDictionary([
            new(N("XObject"), new PdfDictionary([new(N("Image"), image)]))]);
        update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(entries));
        return update.Build();
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var stream = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) stream.Write(bytes);
        return output.ToArray();
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
        => value is PdfIndirectReference reference ? document.Resolve(reference) : value;
    private static PdfName N(string value) => new(Encoding.ASCII.GetBytes(value));
}
