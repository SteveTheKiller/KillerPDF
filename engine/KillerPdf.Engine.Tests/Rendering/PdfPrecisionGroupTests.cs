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
    [Fact]
    public void KnockoutPageGroupsAreDiagnosedBeforeReturningAPreciseFrame()
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(1, 1).Build());
        var renderer = new PdfPageRenderer(WithRgbPageGroup(source, isolated: false, knockout: true));

        var error = Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(1, 1)));

        Assert.Contains("page group", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RgbPageGroupIsImposedOnlyForOpaqueOutput(bool isolated, bool transparent)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes("/Half gs 1 0 0 rg 0 0 1 1 re f")).Build());
        var renderer = new PdfPageRenderer(WithRgbPageGroup(source, isolated));

        PdfRenderedFrame frame = renderer.RenderFrame(0,
            new PdfRenderOptions(1, 1, transparentBackground: transparent));

        Assert.Equal(1, frame.Pixels[0]);
        float expected = transparent ? 0 : .21404114f;
        Assert.InRange(frame.Pixels[1], expected - .00000003f, expected + .00000003f);
        Assert.InRange(frame.Pixels[2], expected - .00000003f, expected + .00000003f);
        Assert.Equal(transparent ? .5f : 1, frame.Pixels[3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonisolatedGroupRetainsInternalMultiplyBeforeInvokingMultiply(bool fractionalBackdrop)
    {
        var renderer = new PdfPageRenderer(MemoryDocument(depth: 1, siblings: false,
            opacity: .5, outerOpacity: .5, multiply: true, backdrop: true, isolated: false,
            fractionalBackdrop: fractionalBackdrop, internalMultiply: true));

        PdfRenderedFrame frame = renderer.RenderFrame(0,
            new PdfRenderOptions(1, 1, transparentBackground: fractionalBackdrop));

        // Independent encoded results25/128 and265/896, decoded using Decimal60.
        float expected = fractionalBackdrop ? .07115657901938434f : .03166300922892793f;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[channel], expected - .00000003f, expected + .00000003f);
        Assert.Equal(fractionalBackdrop ? .4375f : 1, frame.Pixels[3]);
    }

    [Fact]
    public void TinyNonisolatedGroupAlphaKeepsItsFiniteMultiplyContribution()
    {
        var renderer = new PdfPageRenderer(MemoryDocument(depth: 1, siblings: false,
            opacity: 1d / 65536, outerOpacity: .5, multiply: true, backdrop: true, isolated: false));

        PdfRenderedFrame frame = renderer.RenderFrame(0, new PdfRenderOptions(1, 1));

        // Encoded gray is1/4 * (1 - 1/262144), independently decoded with Decimal60.
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[channel], .05087570638181978f - .00000003f,
                .05087570638181978f + .00000003f);
        Assert.Equal(1, frame.Pixels[3]);
    }

    [Theory]
    [InlineData(0, .5)]
    [InlineData(.5, 0)]
    public void ZeroGroupOrInvokingOpacityLeavesTheBackdropUnchanged(double opacity, double outerOpacity)
    {
        var renderer = new PdfPageRenderer(MemoryDocument(depth: 1, siblings: false,
            opacity: opacity, outerOpacity: outerOpacity, multiply: true, backdrop: true, isolated: false));

        PdfRenderedFrame frame = renderer.RenderFrame(0, new PdfRenderOptions(1, 1));

        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[channel], .05087608817155679f - .000000003f,
                .05087608817155679f + .000000003f);
        Assert.Equal(1, frame.Pixels[3]);
    }

    [Fact]
    public void NormalChildPropagatesItsOwnAlphaToAnOuterMultiplyGroup()
    {
        var renderer = new PdfPageRenderer(NestedMultiplyDocument());

        Assert.Throws<ArgumentException>(() => renderer.RenderFrame(0,
            new PdfRenderOptions(1, 1, maximumPixelBytes: 103)));
        PdfRenderedFrame frame = renderer.RenderFrame(0,
            new PdfRenderOptions(1, 1, maximumPixelBytes: 104));

        // Independent nested-group example: encoded159/256 after imposition on white.
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[channel], .3437246732540222f - .00000006f,
                .3437246732540222f + .00000006f);
        Assert.Equal(1, frame.Pixels[3]);
    }

    [Fact]
    public void TrackedGroupAlphaUsesLocalRegionCoordinatesAndExactStorageLimits()
    {
        var renderer = new PdfPageRenderer(MemoryDocument(depth: 1, siblings: false,
            opacity: .5, outerOpacity: .5, multiply: true, isolated: false, translated: true));

        Assert.Throws<ArgumentException>(() => renderer.RenderFrame(0,
            new PdfRenderOptions(3, 1, transparentBackground: true, maximumPixelBytes: 131)));
        PdfRenderedFrame full = renderer.RenderFrame(0,
            new PdfRenderOptions(3, 1, transparentBackground: true, maximumPixelBytes: 132));
        Assert.Throws<ArgumentException>(() => renderer.RenderFrameRegion(0,
            new PdfRenderOptions(3, 1, transparentBackground: true, maximumPixelBytes: 59), 1, 0, 1, 1));
        PdfRenderedFrame region = renderer.RenderFrameRegion(0,
            new PdfRenderOptions(3, 1, transparentBackground: true, maximumPixelBytes: 60), 1, 0, 1, 1);

        Assert.Equal(0, full.Pixels[3]);
        Assert.Equal(.25f, full.Pixels[7]);
        Assert.Equal(0, full.Pixels[11]);
        Assert.InRange(full.Pixels[4], .21404114f - .00000003f, .21404114f + .00000003f);
        Assert.Equal(full.Pixels.Slice(4, 4).ToArray(), region.Pixels.ToArray());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void GroupAppliesInvokingMultiplyToTheCompletedGroup(bool fractionalBackdrop, bool isolated)
    {
        var renderer = new PdfPageRenderer(MemoryDocument(depth: 1, siblings: false,
            opacity: .5, outerOpacity: .5, multiply: true, backdrop: true,
            fractionalBackdrop: fractionalBackdrop, isolated: isolated));

        PdfRenderedFrame frame = renderer.RenderFrame(0,
            new PdfRenderOptions(1, 1, transparentBackground: fractionalBackdrop));

        // Gray1/2 at alpha1/2, invoked at alpha1/2, multiplies the gray1/4 backdrop.
        // Encoded output is7/32, or19/56 at alpha7/16 for a backdrop at alpha1/4.
        // Independent Decimal60 sRGB decoding is frozen here.
        float expected = fractionalBackdrop ? 0.09421945717187386f : 0.03925039247382094f;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[channel], expected - .00000003f, expected + .00000003f);
        Assert.Equal(fractionalBackdrop ? .4375f : 1, frame.Pixels[3]);
    }

    [Theory]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, true)]
    public void RgbGroupAppliesOuterOpacityOnceAfterInternalCompositing(
        bool transparent, bool isolated, bool multiply, bool fractionalBackdrop)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes(fractionalBackdrop ? "/Backdrop gs 0.25 g 0 0 1 1 re f /Outer gs /Form Do"
                : transparent ? "/Outer gs /Form Do"
                : "0.25 g 0 0 1 1 re f /Outer gs /Form Do")).Build());
        var catalog = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(source.Trailer[Name("Root")])));
        var pages = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[Name("Pages")])));
        var page = Assert.IsType<PdfIndirectReference>(Assert.Single(Assert.IsType<PdfArray>(pages[Name("Kids")])));
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page));
        var update = new PdfIncrementalUpdateBuilder(source);
        var half = new PdfDictionary([Entry("ca", new PdfReal(0.5))]);
        var internalState = multiply ? new PdfDictionary(half.Append(Entry("BM", Name("Multiply")))) : half;
        var group = new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", Name("DeviceRGB")),
            Entry("I", new PdfBoolean(isolated))]);
        var formResources = new PdfDictionary([Entry("ExtGState", new PdfDictionary([Entry("Half", internalState)]))]);
        var form = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Type", Name("XObject")), Entry("Subtype", Name("Form")),
            Entry("BBox", Numbers(0, 0, 1, 1)), Entry("Group", group), Entry("Resources", formResources)]),
            Encoding.ASCII.GetBytes("/Half gs 1 0 0 rg 0 0 1 1 re f 0 0 1 rg 0 0 1 1 re f")));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Form", form)])),
            Entry("ExtGState", new PdfDictionary([Entry("Outer", half),
                Entry("Backdrop", new PdfDictionary([Entry("ca", new PdfReal(0.25))]))]))]);
        var document = PdfDocument.Open(update.ReplaceObject(page.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());

        PdfRenderedFrame frame = new PdfPageRenderer(document)
            .RenderFrame(0, new PdfRenderOptions(1, 1, transparentBackground: transparent));

        // Independent rational examples decoded with Decimal60. Non-isolated Multiply
        // produces [3/16,5/32,3/16]; treating its backdrop as transparent would differ.
        // With backdrop alpha1/4, straight RGB is [9/34,5/68,9/34] and alpha17/32.
        float[] expected = fractionalBackdrop ? [0.05696336044816294f, 0.006394405075904889f, 0.05696336044816294f]
            : !isolated ? [0.02934284908133909f, 0.021072022394623186f, 0.02934284908133909f]
            : transparent ? [0.09084171118340768f, 0, 0.4019777798321958f]
            : [0.06429594741141245f, 0.021072022394623186f, 0.13729079089421661f];
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[channel], expected[channel] - 0.0000003f, expected[channel] + 0.0000003f);
        Assert.Equal(fractionalBackdrop ? 0.53125f : transparent ? 0.375f : 1, frame.Pixels[3]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NestedGroupStorageRejectsOneByteBelowItsLimitAndRecovers(bool isolated)
    {
        var renderer = new PdfPageRenderer(MemoryDocument(depth: 2, siblings: false, isolated: isolated));

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TranslatedGroupUsesLocalStorageAndMatchesTheFullFrameRegion(bool isolated)
    {
        var renderer = new PdfPageRenderer(MemoryDocument(depth: 1, siblings: false, translated: true, isolated: isolated));

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
        bool stroke = false, double outerOpacity = 1, bool translated = false, bool isolated = true,
        bool multiply = false, bool backdrop = false, bool fractionalBackdrop = false, bool internalMultiply = false)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(translated ? 3 : 1, 1,
            Encoding.ASCII.GetBytes((fractionalBackdrop ? "/Backdrop gs " : "")
                + (backdrop ? "0.25 g 0 0 1 1 re f " : "")
                + (siblings ? "/Outer gs /Form Do /Form Do" : "/Outer gs /Form Do"))).Build());
        var catalog = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(source.Trailer[Name("Root")])));
        var pages = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[Name("Pages")])));
        var page = Assert.IsType<PdfIndirectReference>(Assert.Single(Assert.IsType<PdfArray>(pages[Name("Kids")])));
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page));
        var update = new PdfIncrementalUpdateBuilder(source);
        var group = new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", Name("DeviceRGB")),
            Entry("I", new PdfBoolean(isolated))]);
        PdfIndirectReference? child = null;
        for (int level = 0; level < depth; level++)
        {
            var localResources = child is null ? new PdfDictionary([Entry("ExtGState", new PdfDictionary([
                Entry("Alpha", new PdfDictionary([Entry("ca", new PdfReal(opacity)),
                    Entry("BM", Name(internalMultiply ? "Multiply" : "Normal"))]))]))])
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
                Entry("ca", new PdfReal(outerOpacity)), Entry("CA", new PdfReal(0.125)),
                Entry("BM", Name(multiply ? "Multiply" : "Normal"))])),
                Entry("Backdrop", new PdfDictionary([Entry("ca", new PdfReal(.25))]))]))]);
        return PdfDocument.Open(update.ReplaceObject(page.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());
    }

    private static PdfDocument NestedMultiplyDocument()
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes("/Container Do")).Build());
        var catalog = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(source.Trailer[Name("Root")])));
        var pages = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[Name("Pages")])));
        var page = Assert.IsType<PdfIndirectReference>(Assert.Single(Assert.IsType<PdfArray>(pages[Name("Kids")])));
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page));
        var update = new PdfIncrementalUpdateBuilder(source);
        var half = new PdfDictionary([Entry("ca", new PdfReal(.5))]);
        var multiply = new PdfDictionary([Entry("ca", new PdfReal(.5)), Entry("BM", Name("Multiply"))]);
        PdfIndirectReference Form(string stream, bool isolated, PdfDictionary resources) =>
            update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Type", Name("XObject")), Entry("Subtype", Name("Form")),
                Entry("BBox", Numbers(0, 0, 1, 1)), Entry("Resources", resources),
                Entry("Group", new PdfDictionary([Entry("S", Name("Transparency")),
                    Entry("CS", Name("DeviceRGB")), Entry("I", new PdfBoolean(isolated))]))]),
                Encoding.ASCII.GetBytes(stream)));
        PdfDictionary Resources(string? childName = null, PdfIndirectReference? child = null) =>
            new([Entry("ExtGState", new PdfDictionary([Entry("Half", half), Entry("Multiply", multiply)])),
                Entry("XObject", childName is null ? new PdfDictionary([])
                    : new PdfDictionary([Entry(childName, child!)]))]);
        var inner = Form("/Half gs 0.75 g 0 0 1 1 re f", false, Resources());
        var outer = Form("/Half gs 0.25 g 0 0 1 1 re f /Half gs /H Do", false, Resources("H", inner));
        var container = Form("/Half gs 0.5 g 0 0 1 1 re f /Multiply gs /G Do", true, Resources("G", outer));
        return PdfDocument.Open(update.ReplaceObject(page.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", Resources("Container", container))))).Build());
    }

    private static PdfDocument WithRgbPageGroup(PdfDocument source, bool isolated, bool knockout = false)
    {
        var catalog = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(source.Trailer[Name("Root")])));
        var pages = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[Name("Pages")])));
        var reference = Assert.IsType<PdfIndirectReference>(Assert.Single(Assert.IsType<PdfArray>(pages[Name("Kids")])));
        var page = Assert.IsType<PdfDictionary>(source.Resolve(reference));
        var group = new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", Name("DeviceRGB")),
            Entry("I", new PdfBoolean(isolated)), Entry("K", new PdfBoolean(knockout))]);
        var resources = new PdfDictionary([Entry("ExtGState", new PdfDictionary([
            Entry("Half", new PdfDictionary([Entry("ca", new PdfReal(.5)), Entry("BM", Name("Multiply"))]))]))]);
        return PdfDocument.Open(new PdfIncrementalUpdateBuilder(source).ReplaceObject(reference.ObjectNumber,
            new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)).Append(Entry("Group", group)))).Build());
    }

    private static PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
