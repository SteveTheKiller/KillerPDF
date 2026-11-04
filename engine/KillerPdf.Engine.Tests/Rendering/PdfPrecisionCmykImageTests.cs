using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionCmykImageTests
{
    // Synthetic affine XYZ fixture, not an ICC conformance specimen.
    private const string ProfileBase64 =
        "AAABQAAAAAAEAAAAcHJ0ckNNWUtYWVogAAAAAAAAAAAAAAAAYWNzcAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQTJCMQAAAJAAAACwbWZ0MgAAAAAEAwIAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAQAAAAIAAgAA//8AAP//AAD//wAA//9AAEAAQAAgACAAIABAAEAAIAAgACAAAABAACAAQAAgAAAAIABAACAAIAAgAAAAAAAgAEAAQAAAACAAIAAgAEAAIAAAACAAAAAgACAAQAAAAAAAIAAgACAAIAAAAAAAAAAAAP//AAD//wAA//8=";

    [Fact]
    public void NaturalColorimetricCmykImageRetainsAdjacentSixteenBitLevels()
    {
        byte[] profile = Convert.FromBase64String(ProfileBase64);
        Assert.Equal("5D23FAF99558BAAB5B83FED13B42532C9CD7E839CE07C5960BDEED8B701E55CF",
            Convert.ToHexString(SHA256.HashData(profile)));
        var renderer = Renderer(profile);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        // Frozen Decimal60 oracle: affine PCS XYZ, independently inverted Bradford,
        // then standard linear sRGB. ICC.1:2022 sections 6.3.4 and 10.10 define PCS encoding.
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        AssertPixel(frame, 0, 0.25656640931229085f, 0.24270025515688283f, 0.31217781759250244f);
        AssertPixel(frame, 1, 0.25655445469789561f, 0.24270398889375892f, 0.31217754305628471f);
        Assert.True(frame.Pixels[0] > frame.Pixels[4]);
        byte[] quantized = frame.ToSrgbBgra32();
        Assert.Equal(quantized[..4], quantized[4..]);
    }

    [Fact]
    public void DefaultCmykSourceProfileRetainsAdjacentSixteenBitLevels()
    {
        var renderer = Renderer(Convert.FromBase64String(ProfileBase64), scenario: "default");

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        // The same independent affine PCS oracle applies when the resource default
        // supplies this ICC profile for the original DeviceCMYK samples.
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        AssertPixel(frame, 0, 0.25656640931229085f, 0.24270025515688283f, 0.31217781759250244f);
        AssertPixel(frame, 1, 0.25655445469789561f, 0.24270398889375892f, 0.31217754305628471f);
        Assert.True(frame.Pixels[0] > frame.Pixels[4]);
        byte[] quantized = frame.ToSrgbBgra32();
        Assert.Equal(quantized[..4], quantized[4..]);
    }

    [Fact]
    public void VersionTwoColorimetricCmykImageUsesTheSameXyzEncoding()
    {
        byte[] profile = Convert.FromBase64String(ProfileBase64);
        profile[8] = 2;
        Assert.Equal("119344650B9FA7F075E5896F845C979EDD787C4E640769C604DDE54BA516AD28",
            Convert.ToHexString(SHA256.HashData(profile)));

        var frame = Renderer(profile).RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        AssertPixel(frame, 0, 0.25656640931229085f, 0.24270025515688283f, 0.31217781759250244f);
        AssertPixel(frame, 1, 0.25655445469789561f, 0.24270398889375892f, 0.31217754305628471f);
    }

    [Fact]
    public void NaturalEightBitColorimetricCmykImageUsesTheProfile()
    {
        var frame = Renderer(Convert.FromBase64String(ProfileBase64), bits: 8)
            .RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        AssertPixel(frame, 0, 0.25556416618988192f, 0.24175217835216830f, 0.31095833577690014f);
        AssertPixel(frame, 1, 0.25249183029030578f, 0.24271174872932391f, 0.31088777996894317f);
    }

    [Fact]
    public void ReversedCyanDecodeIsAppliedBeforeColorimetricCmykConversion()
    {
        var frame = Renderer(Convert.FromBase64String(ProfileBase64), reverseCyan: true)
            .RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        AssertPixel(frame, 0, 0.25657836392668608f, 0.24269652142000674f, 0.31217809212872017f);
        AssertPixel(frame, 1, 0.25659031854108132f, 0.24269278768313064f, 0.31217836666493790f);
        Assert.True(frame.Pixels[4] > frame.Pixels[0]);
    }

    [Fact]
    public void NaturalSizeInterpolationHintPreservesOriginalPreciseSamples()
    {
        var frame = Renderer(Convert.FromBase64String(ProfileBase64), scenario: "naturalInterpolated")
            .RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        AssertPixel(frame, 0, 0.25656640931229085f, 0.24270025515688283f, 0.31217781759250244f);
        AssertPixel(frame, 1, 0.25655445469789561f, 0.24270398889375892f, 0.31217754305628471f);
        Assert.True(frame.Pixels[0] > frame.Pixels[4]);
    }

    [Fact]
    public void NearestEnlargementRetainsBothAdjacentSixteenBitCmykLevels()
    {
        var renderer = Renderer(Convert.FromBase64String(ProfileBase64), scenario: "scaledFullUp");

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(4, 1, transparentBackground: true));

        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        AssertPixel(frame, 0, 0.25656640931229085f, 0.24270025515688283f, 0.31217781759250244f);
        AssertPixel(frame, 1, 0.25656640931229085f, 0.24270025515688283f, 0.31217781759250244f);
        AssertPixel(frame, 2, 0.25655445469789561f, 0.24270398889375892f, 0.31217754305628471f);
        AssertPixel(frame, 3, 0.25655445469789561f, 0.24270398889375892f, 0.31217754305628471f);
        Assert.True(frame.Pixels[0] > frame.Pixels[8]);
    }

    [Fact]
    public void ClippedNearestCmykEnlargementKeepsTheOriginalSampleFootprint()
    {
        var frame = Renderer(Convert.FromBase64String(ProfileBase64), scenario: "scaledUp")
            .RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        AssertPixel(frame, 0, 0.25656640931229085f, 0.24270025515688283f, 0.31217781759250244f);
        AssertPixel(frame, 1, 0.25656640931229085f, 0.24270025515688283f, 0.31217781759250244f);
    }

    [Theory]
    [InlineData("scaledDown")]
    [InlineData("interpolated")]
    [InlineData("rotated")]
    [InlineData("mask")]
    [InlineData("invalid")]
    [InlineData("missingColorimetricTable")]
    [InlineData("range")]
    public void UnsupportedCmykImageCasesRemainRejected(string scenario)
    {
        byte[] profile = Convert.FromBase64String(ProfileBase64);
        if (scenario == "invalid") profile = [0, 1, 2];
        if (scenario == "missingColorimetricTable") "A2B0"u8.CopyTo(profile.AsSpan(132));
        var renderer = Renderer(profile, scenario: scenario);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    private static PdfPageRenderer Renderer(byte[] profile, int bits = 16, bool reverseCyan = false,
        string? scenario = null)
    {
        string content = scenario switch {
            "scaledUp" or "scaledFullUp" or "interpolated" => "4 0 0 1 0 0 cm /Image Do",
            "scaledDown" => "1 0 0 1 0 0 cm /Image Do",
            "rotated" => "0 2 -1 0 1 0 cm /Image Do",
            _ => "2 0 0 1 0 0 cm /Image Do"
        };
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(scenario == "scaledFullUp" ? 4 : 2, 1,
            Encoding.ASCII.GetBytes(content)).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        var profileEntries = new List<KeyValuePair<PdfName, PdfObject>> { Entry("N", new PdfInteger(4)) };
        if (scenario == "range")
            profileEntries.Add(Entry("Range", new PdfArray(new[] { 0, 0.75, 0, 1, 0, 1, 0, 1 }
                .Select(value => (PdfObject)new PdfReal(value)))));
        var icc = update.AddObject(new PdfStream(new PdfDictionary(profileEntries), profile));
        var iccSpace = new PdfArray([Name("ICCBased"), icc]);
        var imageEntries = new List<KeyValuePair<PdfName, PdfObject>> {
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)), Entry("Height", new PdfInteger(1)),
            Entry("ColorSpace", scenario == "default" ? Name("DeviceCMYK") : iccSpace),
            Entry("Decode", new PdfArray((reverseCyan ? new[] { 1, 0, 0, 1, 0, 1, 0, 1 }
                : [0, 1, 0, 1, 0, 1, 0, 1]).Select(value => (PdfObject)new PdfInteger(value)))),
            Entry("BitsPerComponent", new PdfInteger(bits)),
            Entry("Interpolate", new PdfBoolean(scenario is "interpolated" or "naturalInterpolated"))
        };
        if (scenario == "mask")
            imageEntries.Add(Entry("Mask", new PdfArray(Enumerable.Repeat<PdfObject>(new PdfInteger(0), 8))));
        var image = update.AddObject(new PdfStream(new PdfDictionary(imageEntries),
            bits == 16 ? [128, 0, 128, 0, 128, 0, 128, 0, 128, 1, 128, 0, 128, 0, 128, 0]
                : [128, 128, 128, 128, 129, 128, 128, 128]));
        var resourceEntries = new List<KeyValuePair<PdfName, PdfObject>> {
            Entry("XObject", new PdfDictionary([Entry("Image", image)]))
        };
        if (scenario == "default")
            resourceEntries.Add(Entry("ColorSpace", new PdfDictionary([Entry("DefaultCMYK", iccSpace)])));
        var resources = new PdfDictionary(resourceEntries);
        var document = PdfDocument.Open(update.ReplaceObject(page.Reference.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());
        return new PdfPageRenderer(document);
    }

    private static void AssertPixel(PdfRenderedFrame frame, int pixel, float red, float green, float blue)
    {
        const float tolerance = 0.000002f;
        float[] expected = [red, green, blue];
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[pixel * 4 + channel], expected[channel] - tolerance, expected[channel] + tolerance);
        Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
