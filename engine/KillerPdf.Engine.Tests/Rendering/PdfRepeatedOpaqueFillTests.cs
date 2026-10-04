using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfRepeatedOpaqueFillTests
{
    private const string Background = ".002 .002 .002 .1 k 0 0 32 32 re f ";
    private const string Shape = "4.25 16.1 m 15.8 4.35 l 27.3 15.8 l 16.35 27.2 l h f ";
    private const string ShiftedShape = "4.75 16.1 m 16.3 4.35 l 27.8 15.8 l 16.85 27.2 l h f ";
    private const string Red = "0 1 1 0 k " + Shape;
    private const string Final = ".002 .002 .002 0 k " + Shape;
    private const string Barrier = "q 0 0 m 0 0 l S Q ";

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void IdenticalOpaqueProcessFill_ReplacesEarlierPaintAtAntialiasedEdges(
        int overprintMode, bool zeroTintSpot)
    {
        PdfRenderedPage expected = Render(overprintMode, zeroTintSpot, firstPaint: false);
        PdfRenderedPage actual = Render(overprintMode, zeroTintSpot, firstPaint: true);

        AssertSame(expected, actual);
    }

    [Theory]
    [InlineData(-1, "")]
    [InlineData(0, "")]
    [InlineData(1, "")]
    [InlineData(1, "q /SpotOn gs /Spot cs .5 scn 0 0 32 32 re f Q ")]
    public void IdenticalOpaqueProcessFill_AcrossGraphicsStateScopesMatchesFinalOnly(
        int overprintMode, string spot)
    {
        string prefix = Background + spot;
        PdfRenderedPage expected = RenderContent(prefix + "q /Paint gs " + Final + "Q ", overprintMode);
        PdfRenderedPage actual = RenderContent(prefix
            + "q /Paint gs " + Red + "Q q /Paint gs " + Final + "Q ", overprintMode);

        AssertSame(expected, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Opm1SecondFill_PreservesProcessChannelPaintedOnlyByFirst(bool withSpot)
    {
        string prefix = Background + (withSpot
            ? "q /SpotOn gs /Spot cs .5 scn 0 0 32 32 re f Q " : "");
        string cyan = ".5 0 0 0 k " + Shape;
        string magenta = "0 .5 0 0 k " + Shape;
        string combined = ".5 .5 0 0 k " + Shape;
        PdfRenderedPage expected = RenderContent(prefix + "q /Paint gs " + combined + "Q ", 1);
        PdfRenderedPage actual = RenderContent(prefix
            + "q /Paint gs " + cyan + "Q q /Paint gs " + magenta + "Q ", 1);

        AssertSame(expected, actual);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void IdenticalOpaqueProcessFill_WithDifferentFinalColorMatchesFinalOnly(int overprintMode)
    {
        string final = ".4 .2 .1 0 k " + Shape;
        string prefix = Background + "q /SpotOn gs /Spot cs .5 scn 0 0 32 32 re f Q ";
        PdfRenderedPage expected = RenderContent(prefix + "q /Paint gs " + final + "Q ", overprintMode);
        PdfRenderedPage actual = RenderContent(prefix
            + "q /Paint gs " + Red + "Q q /Paint gs " + final + "Q ", overprintMode);

        AssertSame(expected, actual);
    }

    [Fact]
    public void NestedFractionalFormClip_IdenticalOpaqueFillsMatchFinalOnly()
    {
        string prefix = Background + "q /SpotOn gs /Spot cs 0 scn 0 0 32 32 re f Q ";
        string final = "q /Paint gs " + Final + "Q ";
        PdfRenderedPage expected = RenderContent(prefix + final, 1,
            nestedFractionalForms: true);
        PdfRenderedPage actual = RenderContent(prefix
            + "q /Paint gs " + Red + "Q " + final, 1,
            nestedFractionalForms: true);

        AssertSame(expected, actual);
    }

    [Fact]
    public void HarmlessStrokeBarrier_DoesNotChangeReferencePixels()
    {
        PdfRenderedPage expected = RenderContent(Background + Final, 1);
        PdfRenderedPage actual = RenderContent(Background + Barrier + Final, 1);

        AssertSame(expected, actual);
    }

    [Fact]
    public void ShiftedSecondPath_KeepsExposedFirstPaint()
    {
        string first = "q /Paint gs " + Red + "Q ";
        string second = "q /Paint gs .002 .002 .002 0 k " + ShiftedShape + "Q ";
        PdfRenderedPage expected = RenderContent(Background + first + Barrier + second, 1);
        PdfRenderedPage actual = RenderContent(Background + first + second, 1);
        PdfRenderedPage finalOnly = RenderContent(Background + second, 1);

        AssertSame(expected, actual);
        Assert.False(actual.Pixels.Span.SequenceEqual(finalOnly.Pixels.Span));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TranslucentFill_DoesNotMergeWithOpaqueFill(bool firstIsTranslucent)
    {
        string first = "q /Paint gs " + (firstIsTranslucent ? "/Half gs " : "") + Red + "Q ";
        string second = "q /Paint gs " + (firstIsTranslucent ? "" : "/Half gs ") + Final + "Q ";
        PdfRenderedPage expected = RenderContent(Background + first + Barrier + second, 1);
        PdfRenderedPage actual = RenderContent(Background + first + second, 1);

        AssertSame(expected, actual);
    }

    [Theory]
    [InlineData("clip")]
    [InlineData("transform")]
    [InlineData("intent")]
    public void ChangedFillState_DoesNotMergeAcrossDifferentEffectivePaint(string difference)
    {
        string first = difference switch
        {
            "clip" => "q /Paint gs 0 0 18 32 re W n " + Red + "Q ",
            "transform" => "q /Paint gs 1 0 0 1 .5 0 cm " + Red + "Q ",
            "intent" => "q /Paint gs /RelativeColorimetric ri " + Red + "Q ",
            _ => throw new ArgumentOutOfRangeException(nameof(difference))
        };
        string second = "q /Paint gs "
            + (difference == "intent" ? "/Perceptual ri " : "") + Final + "Q ";
        PdfRenderedPage expected = RenderContent(Background + first + Barrier + second, 1);
        PdfRenderedPage actual = RenderContent(Background + first + second, 1);

        AssertSame(expected, actual);
    }

    [Fact]
    public void NonrectangularClip_DoesNotMergeWithUnclippedFill()
    {
        string first = "q /Paint gs " + Red + "Q ";
        string second = "q /Paint gs 4 4 m 28 4 l 16 28 l h W n " + Final + "Q ";
        PdfRenderedPage expected = RenderContent(Background + first + Barrier + second, 1);
        PdfRenderedPage actual = RenderContent(Background + first + second, 1);

        AssertSame(expected, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedSpotOverprintPolicies_KeepSequentialPaintBehavior(bool firstOverprints)
    {
        string spot = "q /SpotOn gs /Spot cs .5 scn 0 0 32 32 re f Q ";
        string first = "q /" + (firstOverprints ? "Paint" : "Off") + " gs " + Red + "Q ";
        string second = "q /" + (firstOverprints ? "Off" : "Paint") + " gs " + Final + "Q ";
        PdfRenderedPage expected = RenderContent(Background + spot + first + Barrier + second, 1);
        PdfRenderedPage actual = RenderContent(Background + spot + first + second, 1);

        AssertSame(expected, actual);
    }

    [Fact]
    public void DefaultCmykReplacement_DoesNotUseNativeFillCoalescing()
    {
        string first = "q /Paint gs " + Red + "Q ";
        string second = "q /Paint gs " + Final + "Q ";
        PdfRenderedPage expected = RenderContent(Background + first + Barrier + second, 1,
            defaultCmykReplacement: true);
        PdfRenderedPage actual = RenderContent(Background + first + second, 1,
            defaultCmykReplacement: true);
        PdfRenderedPage finalOnly = RenderContent(Background + second, 1,
            defaultCmykReplacement: true);

        AssertSame(expected, actual);
        Assert.False(actual.Pixels.Span.SequenceEqual(finalOnly.Pixels.Span));
    }

    [Theory]
    [InlineData("stroke")]
    [InlineData("image")]
    [InlineData("form")]
    public void InterveningPaint_FlushesEarlierFill(string kind)
    {
        string middle = kind switch
        {
            "stroke" => "q 0 0 0 1 k 3 14 m 8 18 l S Q ",
            "image" => "q 4 0 0 4 3 14 cm /Im Do Q ",
            "form" => "q 1 0 0 1 3 14 cm /Fm Do Q ",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        string first = "q /Paint gs " + Red + "Q ";
        string second = "q /Paint gs " + Final + "Q ";
        PdfRenderedPage expected = RenderContent(Background + first + Barrier + middle + second, 1);
        PdfRenderedPage actual = RenderContent(Background + first + middle + second, 1);

        AssertSame(expected, actual);
    }

    [Fact]
    public void PendingFillAtEndOfStream_IsPainted()
    {
        string first = Background + "q /Paint gs " + Red + "Q ";
        PdfRenderedPage expected = RenderContent(first + Barrier, 1);
        PdfRenderedPage actual = RenderContent(first, 1);

        AssertSame(expected, actual);
    }

    private static PdfRenderedPage Render(int overprintMode, bool zeroTintSpot, bool firstPaint)
    {
        string content = Background
            + (zeroTintSpot ? "q /SpotOn gs /Spot cs 0 scn 0 0 32 32 re f Q " : "")
            + "q /Paint gs " + (firstPaint ? Red : "") + Final + "Q ";
        return RenderContent(content, overprintMode);
    }

    private static PdfRenderedPage RenderContent(string content, int overprintMode,
        bool defaultCmykReplacement = false, bool nestedFractionalForms = false)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(32, 32, Encoding.ASCII.GetBytes(
                nestedFractionalForms ? "/Outer Do" : content)).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(source).Pages[0];
        PdfDictionary original = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfDictionary tint = new([
            Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
            Entry("C0", Numbers(0, 0, 0, 0)), Entry("C1", Numbers(1, 0, 1, 0)),
            Entry("N", new PdfInteger(1))
        ]);
        PdfDictionary Overprint(bool enabled, int mode) => new([
            Entry("OP", new PdfBoolean(enabled)), Entry("op", new PdfBoolean(enabled)),
            Entry("OPM", new PdfInteger(mode))
        ]);
        PdfIndirectReference image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(1)),
            Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(8)),
            Entry("ColorSpace", Name("DeviceCMYK"))
        ]), [0, 0, 0, 255]));
        PdfIndirectReference form = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Form")), Entry("BBox", Numbers(0, 0, 4, 4)),
            Entry("Resources", new PdfDictionary([]))
        ]), Encoding.ASCII.GetBytes("0 0 0 1 k 0 0 4 4 re f")));
        var colorSpaces = new List<KeyValuePair<PdfName, PdfObject>>
        {
            Entry("Spot", new PdfArray([
                Name("Separation"), Name("ProbeSpot"), Name("DeviceCMYK"), tint
            ]))
        };
        if (defaultCmykReplacement)
            colorSpaces.Add(Entry("DefaultCMYK", Name("DeviceCMYK")));
        PdfDictionary resources = new([
            Entry("ColorSpace", new PdfDictionary(colorSpaces)),
            Entry("ExtGState", new PdfDictionary([
                Entry("Paint", Overprint(overprintMode >= 0, Math.Max(overprintMode, 0))),
                Entry("Off", Overprint(false, 0)),
                Entry("SpotOn", Overprint(true, 1)),
                Entry("Half", new PdfDictionary([Entry("ca", new PdfReal(0.5))]))
            ])),
            Entry("XObject", new PdfDictionary([Entry("Im", image), Entry("Fm", form)]))
        ]);
        if (nestedFractionalForms)
        {
            PdfArray bounds = new PdfArray([
                new PdfReal(0.1), new PdfReal(0.1), new PdfReal(31.9), new PdfReal(31.9)
            ]);
            PdfIndirectReference inner = update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Subtype", Name("Form")), Entry("BBox", bounds),
                Entry("Resources", resources)
            ]), Encoding.ASCII.GetBytes(content)));
            PdfIndirectReference outer = update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Subtype", Name("Form")), Entry("BBox", bounds),
                Entry("Resources", new PdfDictionary([
                    Entry("XObject", new PdfDictionary([Entry("Inner", inner)]))
                ]))
            ]), Encoding.ASCII.GetBytes("/Inner Do")));
            resources = new PdfDictionary([
                Entry("XObject", new PdfDictionary([Entry("Outer", outer)]))
            ]);
        }
        PdfDictionary group = new([
            Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))
        ]);
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(original
            .Where(entry => !entry.Key.Equals(Name("Resources")) && !entry.Key.Equals(Name("Group")))
            .Append(Entry("Resources", resources)).Append(Entry("Group", group))));
        return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0,
            new PdfRenderOptions(nestedFractionalForms ? 512 : 32,
                nestedFractionalForms ? 512 : 32,
                includeAnnotations: false, includeFormFields: false));
    }

    private static void AssertSame(PdfRenderedPage expected, PdfRenderedPage actual)
    {
        Assert.Empty(expected.Diagnostics);
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    private static PdfArray Numbers(params int[] values) =>
        new(values.Select(value => (PdfObject)new PdfInteger(value)));

    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) =>
        new(Name(key), value);

    private static PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
}
