using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionGroupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IsolatedRgbGroupAppliesOuterOpacityOnceAfterInternalCompositing(bool transparent)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes(transparent ? "/Outer gs /Form Do"
                : "0.25 g 0 0 1 1 re f /Outer gs /Form Do")).Build());
        var catalog = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(source.Trailer[Name("Root")])));
        var pages = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[Name("Pages")])));
        var page = Assert.IsType<PdfIndirectReference>(Assert.Single(Assert.IsType<PdfArray>(pages[Name("Kids")])));
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page));
        var update = new PdfIncrementalUpdateBuilder(source);
        var half = new PdfDictionary([Entry("ca", new PdfReal(0.5))]);
        var group = new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", Name("DeviceRGB")),
            Entry("I", new PdfBoolean(true))]);
        var formResources = new PdfDictionary([Entry("ExtGState", new PdfDictionary([Entry("Half", half)]))]);
        var form = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Type", Name("XObject")), Entry("Subtype", Name("Form")),
            Entry("BBox", Numbers(0, 0, 1, 1)), Entry("Group", group), Entry("Resources", formResources)]),
            Encoding.ASCII.GetBytes("/Half gs 1 0 0 rg 0 0 1 1 re f 0 0 1 rg 0 0 1 1 re f")));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Form", form)])),
            Entry("ExtGState", new PdfDictionary([Entry("Outer", half)]))]);
        var document = PdfDocument.Open(update.ReplaceObject(page.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());

        PdfRenderedFrame frame = new PdfPageRenderer(document)
            .RenderFrame(0, new PdfRenderOptions(1, 1, transparentBackground: transparent));

        // Rational encoded RGB [9/32,5/32,13/32], decoded independently with Decimal60.
        float[] expected = transparent ? [0.09084171118340768f, 0, 0.4019777798321958f]
            : [0.06429594741141245f, 0.021072022394623186f, 0.13729079089421661f];
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[channel], expected[channel] - 0.0000003f, expected[channel] + 0.0000003f);
        Assert.Equal(transparent ? 0.375f : 1, frame.Pixels[3]);
    }

    [Fact]
    public void NestedGroupStorageRejectsOneByteBelowItsLimitAndRecovers()
    {
        var renderer = new PdfPageRenderer(MemoryDocument(depth: 2, siblings: false));

        Assert.Throws<ArgumentException>(() => renderer.RenderFrame(0,
            new PdfRenderOptions(1, 1, maximumPixelBytes: 75)));
        PdfRenderedFrame frame = renderer.RenderFrame(0, new PdfRenderOptions(1, 1, maximumPixelBytes: 76));

        Assert.InRange(frame.Pixels[0], 0.21404114f - 0.00000003f, 0.21404114f + 0.00000003f);
        Assert.Equal(1, frame.Pixels[3]);
    }

    [Fact]
    public void SequentialGroupsReleaseTheirPixelStorageReservation()
    {
        var renderer = new PdfPageRenderer(MemoryDocument(depth: 1, siblings: true));

        Assert.Throws<ArgumentException>(() => renderer.RenderFrame(0,
            new PdfRenderOptions(1, 1, maximumPixelBytes: 55)));
        PdfRenderedFrame frame = renderer.RenderFrame(0, new PdfRenderOptions(1, 1, maximumPixelBytes: 56));

        Assert.InRange(frame.Pixels[0], 0.21404114f - 0.00000003f, 0.21404114f + 0.00000003f);
        Assert.Equal(1, frame.Pixels[3]);
    }

    [Fact]
    public void TinyGroupAlphaIsNotRoundedAway()
    {
        var renderer = new PdfPageRenderer(MemoryDocument(depth: 1, siblings: false, opacity: 1d / 1024));

        PdfRenderedFrame frame = renderer.RenderFrame(0, new PdfRenderOptions(1, 1, transparentBackground: true));

        Assert.InRange(frame.Pixels[0], 0.21404114f - 0.00000003f, 0.21404114f + 0.00000003f);
        Assert.Equal(0.0009765625f, frame.Pixels[3]);
    }

    [Fact]
    public void EffectiveDefaultRgbGroupProfileIsDiagnosed()
    {
        var renderer = new PdfPageRenderer(MemoryDocument(depth: 1, siblings: false, defaultRgb: true));

        var error = Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(1, 1)));

        Assert.Contains("form group", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GroupStrokeResetsInvokingStrokeOpacityBeforeApplyingOuterFillOpacity()
    {
        var renderer = new PdfPageRenderer(MemoryDocument(depth: 1, siblings: false, stroke: true, outerOpacity: 0.5));

        PdfRenderedFrame frame = renderer.RenderFrame(0, new PdfRenderOptions(1, 1, transparentBackground: true));

        Assert.InRange(frame.Pixels[0], 0.21404114f - 0.00000003f, 0.21404114f + 0.00000003f);
        Assert.Equal(0.5f, frame.Pixels[3]);
    }

    [Fact]
    public void TranslatedGroupUsesLocalStorageAndMatchesTheFullFrameRegion()
    {
        var renderer = new PdfPageRenderer(MemoryDocument(depth: 1, siblings: false, translated: true));

        PdfRenderedFrame full = renderer.RenderFrame(0,
            new PdfRenderOptions(3, 1, transparentBackground: true, maximumPixelBytes: 128));
        PdfRenderedFrame region = renderer.RenderFrameRegion(0,
            new PdfRenderOptions(3, 1, transparentBackground: true, maximumPixelBytes: 56), 1, 0, 1, 1);

        Assert.Equal(0, full.Pixels[3]);
        Assert.Equal(1, full.Pixels[7]);
        Assert.Equal(0, full.Pixels[11]);
        Assert.InRange(full.Pixels[4], 0.21404114f - 0.00000003f, 0.21404114f + 0.00000003f);
        Assert.Equal(full.Pixels.Slice(4, 4).ToArray(), region.Pixels.ToArray());
    }

    private static PdfDocument MemoryDocument(int depth, bool siblings, double opacity = 1, bool defaultRgb = false,
        bool stroke = false, double outerOpacity = 1, bool translated = false)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(translated ? 3 : 1, 1,
            Encoding.ASCII.GetBytes(siblings ? "/Outer gs /Form Do /Form Do" : "/Outer gs /Form Do")).Build());
        var catalog = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(source.Trailer[Name("Root")])));
        var pages = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[Name("Pages")])));
        var page = Assert.IsType<PdfIndirectReference>(Assert.Single(Assert.IsType<PdfArray>(pages[Name("Kids")])));
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page));
        var update = new PdfIncrementalUpdateBuilder(source);
        var group = new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", Name("DeviceRGB")),
            Entry("I", new PdfBoolean(true))]);
        PdfIndirectReference? child = null;
        for (int level = 0; level < depth; level++)
        {
            var localResources = child is null ? new PdfDictionary([Entry("ExtGState", new PdfDictionary([
                Entry("Alpha", new PdfDictionary([Entry("ca", new PdfReal(opacity))]))]))])
                : new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Child", child)]))]);
            if (defaultRgb)
                localResources = new PdfDictionary(localResources.Append(Entry("ColorSpace", new PdfDictionary([
                    Entry("DefaultRGB", new PdfArray([Name("CalRGB"), new PdfDictionary([
                        Entry("WhitePoint", new PdfArray([new PdfReal(0.9642), new PdfInteger(1), new PdfReal(0.8249)]))])]))]))));
            child = update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Type", Name("XObject")), Entry("Subtype", Name("Form")),
                Entry("Matrix", Numbers(1, 0, 0, 1, translated && level == depth - 1 ? 1 : 0, 0)),
                Entry("BBox", Numbers(0, 0, 1, 1)), Entry("Group", group), Entry("Resources", localResources)]),
                Encoding.ASCII.GetBytes(child is not null ? "/Child Do" : stroke
                    ? "0.5 G 1 w 0 0.5 m 1 0.5 l S" : "/Alpha gs 0.5 g 0 0 1 1 re f")));
        }
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Form", child!)])),
            Entry("ExtGState", new PdfDictionary([Entry("Outer", new PdfDictionary([
                Entry("ca", new PdfReal(outerOpacity)), Entry("CA", new PdfReal(0.125))]))]))]);
        return PdfDocument.Open(update.ReplaceObject(page.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());
    }

    private static PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
