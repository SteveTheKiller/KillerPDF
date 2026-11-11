using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionIccImageScalingTests
{
    private const string IdentityRgbProfile =
        "AAAB/AAAAAACQAAAbW50clJHQiBYWVogB+oACwADAAAAAAAAYWNzcE1TRlQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAPbWAAEAAAAA0y10ZXN0AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAJZGVzYwAAAPAAAAByY3BydAAAAWQAAAAjd3RwdAAAAYgAAAAUclhZWgAAAZwAAAAUZ1hZWgAAAbAAAAAUYlhZWgAAAcQAAAAUclRSQwAAAdgAAAAMZ1RSQwAAAeQAAAAMYlRSQwAAAfAAAAAMZGVzYwAAAAAAAAAYU3ludGhldGljIFhZWi1iYXNpcyBSR0IAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB0ZXh0AAAAAFB1YmxpYyBkb21haW4gdGVzdCBmaXh0dXJlAABYWVogAAAAAAAA9tYAAQAAAADTLVhZWiAAAAAAAAD21gAAAAAAAAAAWFlaIAAAAAAAAAAAAAEAAAAAAABYWVogAAAAAAAAAAAAAAAAAADTLWN1cnYAAAAAAAAAAGN1cnYAAAAAAAAAAGN1cnYAAAAAAAAAAA==";
    private const string GammaRgbProfile =
        "AAACCAAAAAACQAAAbW50clJHQiBYWVogB+oACwADAAAAAAAAYWNzcE1TRlQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAPbWAAEAAAAA0y10ZXN0AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAJZGVzYwAAAPAAAAByY3BydAAAAWQAAAAjd3RwdAAAAYgAAAAUclhZWgAAAZwAAAAUZ1hZWgAAAbAAAAAUYlhZWgAAAcQAAAAUclRSQwAAAdgAAAAOZ1RSQwAAAegAAAAOYlRSQwAAAfgAAAAOZGVzYwAAAAAAAAAYU3ludGhldGljIFhZWi1iYXNpcyBSR0IAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB0ZXh0AAAAAFB1YmxpYyBkb21haW4gdGVzdCBmaXh0dXJlAABYWVogAAAAAAAA9tYAAQAAAADTLVhZWiAAAAAAAAD21gAAAAAAAAAAWFlaIAAAAAAAAAAAAAEAAAAAAABYWVogAAAAAAAAAAAAAAAAAADTLWN1cnYAAAAAAAAAAQIAAABjdXJ2AAAAAAAAAAECAAAAY3VydgAAAAAAAAABAgAAAA==";

    private const string GammaGrayProfile =
        "AAABbAAAAAACQAAAbW50ckdSQVlYWVogB+oACwADAAAAAAAAYWNzcE1TRlQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAPbWAAEAAAAA0y10ZXN0AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEZGVzYwAAALQAAABwY3BydAAAASQAAAAjd3RwdAAAAUgAAAAUa1RSQwAAAVwAAAAOZGVzYwAAAAAAAAAWU3ludGhldGljIGxpbmVhciBHcmF5AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAHRleHQAAAAAUHVibGljIGRvbWFpbiB0ZXN0IGZpeHR1cmUAAFhZWiAAAAAAAAD21gABAAAAANMtY3VydgAAAAAAAAABAgAAAA==";

    [Fact]
    public void NearestIccEnlargementRetainsAdjacentSixteenBitLevelsAndExtendedGamut()
    {
        var renderer = Renderer(Convert.FromBase64String(IdentityRgbProfile));

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(4, 1, transparentBackground: true));

        // Independent ICC matrix and Bradford vectors repeat twice under nearest sampling.
        AssertPixel(frame, 0, 1.51082417f, -0.47186967f, 0.03469589f);
        AssertPixel(frame, 1, 1.51082417f, -0.47186967f, 0.03469589f);
        AssertPixel(frame, 2, 1.51087027f, -0.47188407f, 0.03469695f);
        AssertPixel(frame, 3, 1.51087027f, -0.47188407f, 0.03469695f);
        Assert.True(frame.Pixels[8] > frame.Pixels[0]);
        Assert.True(frame.Pixels[0] > 1f);
        Assert.True(frame.Pixels[1] < 0f);
    }

    [Fact]
    public void RoundedNaturalIccTransformRetainsExistingGeometryTolerance()
    {
        var renderer = Renderer(Convert.FromBase64String(IdentityRgbProfile),
            content: "0.7 0 0 1 0 0 cm 2.8571428571428568 0 0 1 0 0 cm /Image Do");

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(4, 1, transparentBackground: true));

        AssertPixel(frame, 0, 1.51082417f, -0.47186967f, 0.03469589f);
        AssertPixel(frame, 1, 1.51087027f, -0.47188407f, 0.03469695f);
        Assert.Equal(0f, frame.Pixels[11]);
        Assert.Equal(0f, frame.Pixels[15]);
    }

    [Fact]
    public void BilinearIccEnlargementInterpolatesEncodedValuesBeforeGammaTrc()
    {
        var renderer = Renderer(Convert.FromBase64String(GammaRgbProfile), interpolate: true,
            first: 16384, second: 49152,
            profileHash: "16757CE04B74548F55CD75A2BCD4F732C3D48EF4515FE1DEB1CB0DEC712CB3C4");

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(4, 1, transparentBackground: true));

        // Encoded source interpolation gives raw16384,24576,40960,49152.
        // Gamma2 TRCs apply after this interpolation and before matrix/Bradford conversion.
        AssertPixel(frame, 0, 0.18885590f, -0.05898461f, 0.00433705f);
        AssertPixel(frame, 1, 0.42492578f, -0.13271537f, 0.00975837f);
        AssertPixel(frame, 2, 1.18034939f, -0.36865380f, 0.02710658f);
        AssertPixel(frame, 3, 1.69970312f, -0.53086148f, 0.03903347f);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void GrayBilinearEnlargementRetainsEncodedGammaSampling(int bits)
    {
        var renderer = Renderer(Convert.FromBase64String(GammaGrayProfile), interpolate: true,
            first: bits == 8 ? 64 : 16384, second: bits == 8 ? 192 : 49152,
            profileHash: "736FE315493475E60B7EE28585B729B2C93CBD0C288530831497CB87170BDE99",
            components: 1, bits: bits);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(4, 1, transparentBackground: true));

        // Independent quantized profile-white vectors, with the existing Gray PCS approximation tolerance.
        float[][] expected = bits == 8
            ? [[0.06299156f, 0.06299099f, 0.06299165f], [0.14173102f, 0.14172972f, 0.14173121f],
               [0.39369727f, 0.39369368f, 0.39369780f], [0.56692407f, 0.56691890f, 0.56692483f]]
            : [[0.06250231f, 0.06250174f, 0.06250239f], [0.14063020f, 0.14062892f, 0.14063039f],
               [0.39063944f, 0.39063588f, 0.39063996f], [0.56252079f, 0.56251566f, 0.56252155f]];
        for (int pixel = 0; pixel < 4; pixel++)
            AssertPixel(frame, pixel, expected[pixel][0], expected[pixel][1], expected[pixel][2], 0.000006f);
    }

    [Fact]
    public void EightBitRgbBilinearEnlargementUsesOriginalEncodedSamples()
    {
        var renderer = Renderer(Convert.FromBase64String(GammaRgbProfile), interpolate: true,
            first: 64, second: 192,
            profileHash: "16757CE04B74548F55CD75A2BCD4F732C3D48EF4515FE1DEB1CB0DEC712CB3C4", bits: 8);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(4, 1, transparentBackground: true));

        AssertPixel(frame, 0, 0.19033422f, -0.05944633f, 0.00437100f);
        AssertPixel(frame, 1, 0.42825200f, -0.13375423f, 0.00983475f);
        AssertPixel(frame, 2, 1.18958888f, -0.37153954f, 0.02731876f);
        AssertPixel(frame, 3, 1.71300799f, -0.53501693f, 0.03933901f);
    }

    [Fact]
    public void AutomaticTwoDimensionalSmoothingUsesTheSameEncodedIccSampler()
    {
        var renderer = Renderer(Convert.FromBase64String(GammaRgbProfile), first: 16384, second: 49152,
            profileHash: "16757CE04B74548F55CD75A2BCD4F732C3D48EF4515FE1DEB1CB0DEC712CB3C4",
            sourceHeight: 2, height: 4);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(4, 4, transparentBackground: true));

        for (int row = 0; row < 4; row++)
        {
            AssertPixel(frame, row * 4, 0.18885590f, -0.05898461f, 0.00433705f);
            AssertPixel(frame, row * 4 + 1, 0.42492578f, -0.13271537f, 0.00975837f);
            AssertPixel(frame, row * 4 + 2, 1.18034939f, -0.36865380f, 0.02710658f);
            AssertPixel(frame, row * 4 + 3, 1.69970312f, -0.53086148f, 0.03903347f);
        }
    }

    [Fact]
    public void SingleAxisReductionRetainsSourcePrecision()
    {
        var renderer = Renderer(Convert.FromBase64String(IdentityRgbProfile), interpolate: false,
            width: 1, content: "1 0 0 1 0 0 cm /Image Do");
        var frame = renderer.RenderFrame(0, new PdfRenderOptions(1, 1));
        // The identity TRC makes the mean of the two independent natural-size vectors exact.
        AssertPixel(frame, 0, (1.51082417f + 1.51087027f) / 2,
            (-.47186967f - .47188407f) / 2, (.03469589f + .03469695f) / 2);
    }

    [Theory]
    [InlineData(4, "4 0 0 1 0.5 0 cm /Image Do", false)]
    [InlineData(4, "4 0 1 1 0 0 cm /Image Do", true)]
    public void UnalignedEdgesAndShearRemainRejected(int width, string content, bool interpolate)
    {
        var renderer = Renderer(Convert.FromBase64String(IdentityRgbProfile), interpolate: interpolate,
            width: width, content: content);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(width, 1)));
    }

    [Fact]
    public void BilinearDecodeOutsideDeviceRangeRemainsRejected()
    {
        var renderer = Renderer(Convert.FromBase64String(IdentityRgbProfile), interpolate: true,
            decode: new PdfArray([new PdfInteger(-1), new PdfInteger(1), new PdfInteger(0),
                new PdfInteger(1), new PdfInteger(0), new PdfInteger(1)]));

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(4, 1)));
    }

    [Fact]
    public void NearestCompatibilityCacheLeavesExtendedPrecisionSamplesIntact()
    {
        var renderer = Renderer(Convert.FromBase64String(IdentityRgbProfile));
        var options = new PdfRenderOptions(4, 1, transparentBackground: true);

        var compatibility = renderer.Render(0, options);
        var frame = renderer.RenderFrame(0, options);

        Assert.Equal(Enumerable.Repeat(new byte[] { 52, 0, 255, 255 }, 4).SelectMany(value => value),
            compatibility.Pixels.ToArray());
        AssertPixel(frame, 0, 1.51082417f, -0.47186967f, 0.03469589f);
        AssertPixel(frame, 3, 1.51087027f, -0.47188407f, 0.03469695f);
    }

    private static PdfPageRenderer Renderer(byte[] profile, bool interpolate = false,
        int first = 32768, int second = 32769,
        string profileHash = "044A5A9A7551EC7229EF682863DF9E7137A24D8F123BFFA14801B883D195D640",
        int components = 3, int bits = 16, int sourceHeight = 1, int height = 1,
        int width = 4, string? content = null, PdfArray? decode = null)
    {
        Assert.Equal(profileHash, Convert.ToHexString(SHA256.HashData(profile)));
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(width, height,
            Encoding.ASCII.GetBytes(content ?? $"{width} 0 0 {height} 0 0 cm /Image Do")).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        var icc = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(components))]), profile));
        byte[] samples = Enumerable.Range(0, sourceHeight).SelectMany(_ => new[] { first, second })
            .SelectMany(value => Enumerable.Range(0, components)
            .SelectMany(channel => bits == 16
                ? new byte[] { (byte)((channel == 0 ? value : 0) >> 8), (byte)(channel == 0 ? value : 0) }
                : new byte[] { (byte)(channel == 0 ? value : 0) })).ToArray();
        var imageEntries = new List<KeyValuePair<PdfName, PdfObject>> {
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)), Entry("Height", new PdfInteger(sourceHeight)),
            Entry("ColorSpace", new PdfArray([Name("ICCBased"), icc])), Entry("BitsPerComponent", new PdfInteger(bits)),
            Entry("Interpolate", new PdfBoolean(interpolate)) };
        if (decode is not null) imageEntries.Add(Entry("Decode", decode));
        var image = update.AddObject(new PdfStream(new PdfDictionary(imageEntries), samples));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Image", image)]))]);
        return new PdfPageRenderer(PdfDocument.Open(update.ReplaceObject(page.Reference.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build()));
    }

    private static void AssertPixel(PdfRenderedFrame frame, int pixel, float red, float green, float blue,
        float tolerance = 0.000002f)
    {
        float[] expected = [red, green, blue];
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[pixel * 4 + channel], expected[channel] - tolerance, expected[channel] + tolerance);
        Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
