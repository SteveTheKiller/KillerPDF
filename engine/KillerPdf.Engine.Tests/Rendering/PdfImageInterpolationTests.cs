using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfImageInterpolationTests
{
    public static IEnumerable<object[]> Orientations()
    {
        foreach (int components in new[] { 1, 3 })
        foreach (bool masked in new[] { false, true })
        foreach (int rotation in new[] { 0, 90, 180, 270 })
        foreach (bool flip in new[] { false, true })
            yield return [components, masked, rotation, flip];
    }

    [Theory]
    [MemberData(nameof(Orientations))]
    public void RequestedMagnification_InterpolatesPixelCentersWithoutTransparentColorFringes(
        int components, bool masked, int rotation, bool flip)
    {
        PdfRenderedPage page = Render(Build(components, masked, rotation, flip), 8);
        // These are the fixed eight-pixel ramps for a two-sample image, including
        // clamped half-pixel borders. The masked RGB endpoint is blue, not transparent red.
        int[] ramp = [0, 0, 32, 96, 159, 223, 255, 255];
        int[] gray = masked ? [255, 255, 248, 234, 221, 207, 200, 200]
            : [10, 10, 34, 81, 129, 176, 200, 200];
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            int column = rotation switch { 90 => 7 - y, 180 => 7 - x, 270 => y, _ => x };
            if (flip) column = 7 - column;
            int red = components == 1 ? gray[column] : 255 - ramp[column];
            int green = components == 1 ? gray[column] : masked ? 255 - ramp[column] : 0;
            int blue = components == 1 ? gray[column] : masked ? 255 : ramp[column];
            int offset = (y * 8 + x) * 4;
            Assert.InRange((int)page.Pixels.Span[offset], blue - 1, blue + 1);
            Assert.InRange((int)page.Pixels.Span[offset + 1], green - 1, green + 1);
            Assert.InRange((int)page.Pixels.Span[offset + 2], red - 1, red + 1);
            Assert.Equal(255, page.Pixels.Span[offset + 3]);
        }
    }

    [Fact]
    public void TransparentBackground_PreservesInterpolatedAlphaAndVisibleEndpointColor()
    {
        var page = new PdfPageRenderer(PdfDocument.Open(Build(3, true, 0, false))).Render(0,
            new PdfRenderOptions(8, 8, transparentBackground: true,
                includeAnnotations: false, includeFormFields: false));
        int[] alpha = [0, 0, 32, 96, 159, 223, 255, 255];
        for (int x = 0; x < 8; x++)
        {
            int offset = (3 * 8 + x) * 4;
            Assert.InRange((int)page.Pixels.Span[offset + 3], Math.Max(0, alpha[x] - 1),
                Math.Min(255, alpha[x] + 1));
            if (alpha[x] == 0) continue;
            Assert.Equal(255, page.Pixels.Span[offset]);
            Assert.Equal(0, page.Pixels.Span[offset + 1]);
            Assert.Equal(0, page.Pixels.Span[offset + 2]);
        }
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("false")]
    [InlineData("integer")]
    [InlineData("dangling")]
    [InlineData("cycle")]
    public void MissingFalseOrInvalidHint_KeepsOrdinaryImagePainting(string hint)
    {
        PdfRenderedPage page = Render(Build(3, false, 0, false, hint), 8);
        for (int x = 0; x < 8; x++)
        {
            int offset = (3 * 8 + x) * 4;
            Assert.Equal(x < 4 ? 0 : 255, page.Pixels.Span[offset]);
            Assert.Equal(0, page.Pixels.Span[offset + 1]);
            Assert.Equal(x < 4 ? 255 : 0, page.Pixels.Span[offset + 2]);
        }
    }

    [Fact]
    public void IndirectTrueHint_MatchesDirectTrueHint()
    {
        Assert.True(Render(Build(3, true, 0, false, "indirect"), 17).Pixels.Span.SequenceEqual(
            Render(Build(3, true, 0, false), 17).Pixels.Span));
    }

    [Fact]
    public void Reduction_RetainsExistingSampling()
    {
        Assert.True(Render(Build(3, false, 0, false), 1).Pixels.Span.SequenceEqual(
            Render(Build(3, false, 0, false, "false"), 1).Pixels.Span));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FractionalClip_RegionAndParallelRenderingMatchWholePage(bool masked)
    {
        byte[] pdf = Build(3, masked, 90, true,
            clip: "1.1 1.3 m 10.7 2.2 l 9.3 10.6 l 2.2 9.7 l h W n ");
        var renderer = new PdfPageRenderer(PdfDocument.Open(pdf));
        var options = new PdfRenderOptions(1024, 1024, includeAnnotations: false,
            includeFormFields: false) { CacheResult = false };
        PdfRenderedPage full = renderer.Render(0, options);
        Assert.True(full.Pixels.Span.SequenceEqual(renderer.Render(0,
            options with { MaximumParallelism = 4 }).Pixels.Span));
        PdfRenderedPage region = renderer.RenderRegion(0, options, 193, 221, 461, 397);
        for (int y = 0; y < 397; y++)
            Assert.True(region.Pixels.Span.Slice(y * 461 * 4, 461 * 4).SequenceEqual(
                full.Pixels.Span.Slice(((y + 221) * 1024 + 193) * 4, 461 * 4)));
    }

    private static PdfRenderedPage Render(byte[] pdf, int size)
    {
        PdfRenderedPage page = new PdfPageRenderer(PdfDocument.Open(pdf)).Render(0,
            new PdfRenderOptions(size, size, includeAnnotations: false, includeFormFields: false));
        Assert.Empty(page.Diagnostics);
        return page;
    }

    private static byte[] Build(int components, bool masked, int rotation, bool flip,
        string hint = "true", string clip = "")
    {
        (int a, int b, int c, int d, int e, int f) = rotation switch
        {
            90 => (0, 12, -12, 0, 12, 0), 180 => (-12, 0, 0, -12, 12, 12),
            270 => (0, -12, 12, 0, 0, 12), _ => (12, 0, 0, 12, 0, 0)
        };
        if (flip) { e += a; f += b; a = -a; b = -b; }
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(12, 12,
            Encoding.ASCII.GetBytes($"q {clip}{a} {b} {c} {d} {e} {f} cm /Im Do Q")).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var original = (PdfDictionary)source.Resolve(page.Reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        List<KeyValuePair<PdfName, PdfObject>> entries = [Entry("Subtype", Name("Image")),
            Entry("Width", new PdfInteger(2)), Entry("Height", new PdfInteger(1)),
            Entry("BitsPerComponent", new PdfInteger(8)),
            Entry("ColorSpace", Name(components == 1 ? "DeviceGray" : "DeviceRGB"))];
        PdfObject? flag = hint switch
        {
            "absent" => null, "false" => new PdfBoolean(false), "integer" => new PdfInteger(7),
            "dangling" => new PdfIndirectReference(99999, 0), "cycle" => Cycle(),
            "indirect" => update.AddObject(new PdfBoolean(true)), _ => new PdfBoolean(true)
        };
        if (flag is not null) entries.Add(Entry("Interpolate", flag));
        if (masked) entries.Add(Entry("SMask", update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)),
            Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(8)),
            Entry("ColorSpace", Name("DeviceGray"))]), [0, 255]))));
        var image = update.AddObject(new PdfStream(new PdfDictionary(entries),
            components == 1 ? [10, 200] : [255, 0, 0, 0, 0, 255]));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Im", image)]))]);
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(original
            .Where(entry => !entry.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources))));
        return update.Build();

        PdfIndirectReference Cycle()
        {
            var reference = update.ReserveObject();
            update.SetObject(reference, reference);
            return reference;
        }
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
}
