using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfCmykVectorSpotOverprintTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(-1, 0)]
    public void TinyNonzeroCmykVector_UsesModeSpecificProcessInkAndSpotRetention(
        int overprintMode, int referenceMagenta)
    {
        AssertVectorPaint(overprintMode, referenceMagenta, deviceN: false);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(-1, 0)]
    public void TinyNonzeroDeviceNCyanYellowVector_RetainsAbsentProcessInkAndSpot(
        int overprintMode, int referenceMagenta)
    {
        AssertVectorPaint(overprintMode, referenceMagenta, deviceN: true);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void FractionalProcessVector_OverprintCommutesWithNamedSpot(int overprintMode, bool deviceN)
    {
        string background = "0 1 0 .5 k 0 0 5 1 re f ";
        string spot = "q /SpotOn gs /Green cs 1 scn 0 0 5 1 re f Q ";
        string process = deviceN ? "/CyanYellow cs .005 .01 scn" : ".005 0 .01 0 k";
        string vector = $"q /Vector gs {process} 1.5 0 1.5 1 re f Q ";
        foreach (string after in new[] { "", "q /WhiteOn gs 2 0 0 1 1 0 cm /White Do Q" })
        {
            PdfRenderedPage expected = RenderContent(background + vector + spot + after, overprintMode);
            PdfRenderedPage actual = RenderContent(background + spot + vector + after, overprintMode);

            Assert.NotEqual([255, 255, 255, 255], Pixel(expected, 1));
            Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
            Assert.Empty(expected.Diagnostics);
            Assert.Empty(actual.Diagnostics);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void ReducedCmykStencil_OverprintCommutesWithNamedSpot(
        int overprintMode, bool clipped)
    {
        string background = "0 1 0 .5 k 0 0 5 1 re f ";
        string spot = "q /SpotOn gs /Green cs 1 scn 0 0 5 1 re f Q ";
        string clip = clipped ? "1.5 0 m 4.5 0 l 4.5 1 l 1.5 1 l h W n " : "";
        string stencil = "q /Vector gs " + clip + ".005 0 .01 0 k "
            + "5 0 0 1 0 0 cm /Mask Do Q ";
        string white = "q /WhiteOn gs 2 0 0 1 1 0 cm /White Do Q";
        PdfRenderedPage pureSpot = RenderContent(
            "0 0 0 0 k 0 0 5 1 re f " + spot, overprintMode);

        foreach (string after in new[] { "", white })
        {
            PdfRenderedPage expected = RenderContent(
                background + stencil + spot + after, overprintMode);
            PdfRenderedPage actual = RenderContent(
                background + spot + stencil + after, overprintMode);

            Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
            if (after.Length != 0)
            {
                Assert.Equal(Pixel(pureSpot, 1), Pixel(actual, 1));
                Assert.Equal(Pixel(pureSpot, 2), Pixel(actual, 2));
            }
            Assert.Empty(expected.Diagnostics);
            Assert.Empty(actual.Diagnostics);
        }
        Assert.Empty(pureSpot.Diagnostics);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TranslucentProcessVector_OverprintCommutesWithNamedSpot(int overprintMode)
    {
        string background = "0 1 0 .5 k 0 0 5 1 re f ";
        string spot = "q /SpotOn gs /Green cs 1 scn 0 0 5 1 re f Q ";
        string vector = "q /Vector gs /Half gs .5 0 .25 0 k 1 0 2 1 re f Q ";
        foreach (string after in new[] { "", "q /WhiteOn gs 2 0 0 1 1 0 cm /White Do Q" })
        {
            PdfRenderedPage expected = RenderContent(background + vector + spot + after, overprintMode);
            PdfRenderedPage actual = RenderContent(background + spot + vector + after, overprintMode);

            Assert.NotEqual([255, 255, 255, 255], Pixel(expected, 1));
            Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
            Assert.Empty(expected.Diagnostics);
            Assert.Empty(actual.Diagnostics);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public void ZeroOpacityProcessVector_PreservesSpotForLaterProcessOverprint(int overprintMode)
    {
        string background = "0 1 0 .5 k 0 0 5 1 re f ";
        string spot = "q /SpotOn gs /Green cs 1 scn 0 0 5 1 re f Q ";
        string zeroOpacityVector = "q /Vector gs /Zero gs .5 0 .25 0 k 1 0 1 1 re f Q ";
        foreach (string after in new[] { "", "q /WhiteOn gs 1 0 0 1 1 0 cm /White Do Q" })
        {
            PdfRenderedPage expected = RenderContent(background + spot + after, overprintMode);
            PdfRenderedPage actual = RenderContent(background + spot + zeroOpacityVector + after, overprintMode);

            Assert.NotEqual([255, 255, 255, 255], Pixel(expected, 1));
            Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
            Assert.Empty(expected.Diagnostics);
            Assert.Empty(actual.Diagnostics);
        }
    }

    [Fact]
    public void FractionalKnockoutSpotStroke_RemainsVisibleUnderWhiteProcessOverprint()
    {
        string background = "0 0 0 0 k 0 0 5 1 re f ";
        string spot = "q /Off gs /Green cs 1 scn 0 0 5 1 re f "
            + "/Green CS .5 SCN .75 w 2.25 0 m 2.25 1 l S Q ";
        PdfRenderedPage expected = RenderContent(background + spot, 0);
        PdfRenderedPage actual = RenderContent(background + spot
            + "q /WhiteOn gs 1 g 0 0 5 1 re f Q", 0);

        Assert.NotEqual([255, 255, 255, 255], Pixel(expected, 2));
        Assert.Equal(Pixel(expected, 2), Pixel(actual, 2));
        Assert.Empty(expected.Diagnostics);
        Assert.Empty(actual.Diagnostics);
    }

    private static void AssertVectorPaint(int overprintMode, int referenceMagenta, bool deviceN)
    {
        PdfRenderedPage rendered = Render(overprintMode, referenceMagenta, deviceN);
        byte[] untouchedSpotOverMagenta = Pixel(rendered, 0);
        byte[] painted = Pixel(rendered, 1);
        byte[] reference = Pixel(rendered, 2);
        byte[] afterWhiteProcessImage = Pixel(rendered, 3);
        byte[] spotOnly = Pixel(rendered, 4);

        Assert.Empty(rendered.Diagnostics);
        Assert.NotEqual(untouchedSpotOverMagenta, spotOnly);
        Assert.NotEqual([255, 255, 255, 255], spotOnly);

        // The reference sets the final process ink first, then paints the named spot.
        // This avoids making RGB conversion or process-ink rounding part of the oracle.
        Assert.Equal(reference, painted);
        if (overprintMode < 0)
        {
            Assert.Equal([255, 255, 255, 255], afterWhiteProcessImage);
        }
        else
        {
            // White process overprint removes all process ink but must retain the spot.
            Assert.Equal(spotOnly, afterWhiteProcessImage);
        }
    }

    private static PdfRenderedPage Render(int overprintMode, int referenceMagenta, bool deviceN)
    {
        string vectorPaint = deviceN ? "/CyanYellow cs .005 .01 scn" : ".005 0 .01 0 k";
        string content = "0 1 0 0 k 0 0 5 1 re f "
            + "q /Off gs 0 0 0 0 k 4 0 1 1 re f Q "
            + "q /SpotOn gs /Green cs 1 scn "
            + "0 0 2 1 re f 3 0 2 1 re f Q "
            + $"q /Vector gs {vectorPaint} "
            + "1 0 1 1 re f 3 0 1 1 re f Q "
            + $"q /Off gs .005 {referenceMagenta} .01 0 k 2 0 1 1 re f Q "
            + (overprintMode < 0 ? "" : "q /SpotOn gs /Green cs 1 scn 2 0 1 1 re f Q ")
            + "q /WhiteOn gs 1 0 0 1 3 0 cm /White Do Q";
        return RenderContent(content, overprintMode);
    }

    private static PdfRenderedPage RenderContent(string content, int overprintMode)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(5, 1, Encoding.ASCII.GetBytes(content)).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(source).Pages[0];
        PdfDictionary originalPage = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference whiteImage = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(1)),
            Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(8)),
            Entry("ColorSpace", Name("DeviceCMYK"))
        ]), new byte[4]));
        // Each destination pixel samples one painted and one clear stencil bit per row.
        PdfIndirectReference maskImage = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(10)),
            Entry("Height", new PdfInteger(2)), Entry("BitsPerComponent", new PdfInteger(1)),
            Entry("ImageMask", new PdfBoolean(true))
        ]), [0x55, 0x40, 0x55, 0x40]));
        PdfIndirectReference cyanYellowFunction = update.AddObject(new PdfStream(
            new PdfDictionary([
                Entry("FunctionType", new PdfInteger(4)),
                Entry("Domain", Numbers(0, 1, 0, 1)),
                Entry("Range", Numbers(0, 1, 0, 1, 0, 1, 0, 1))
            ]), Encoding.ASCII.GetBytes("{ 0 exch 0 }")));
        PdfDictionary tint = new([
            Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
            Entry("C0", Numbers(0, 0, 0, 0)),
            Entry("C1", new PdfArray([new PdfReal(0.5), new PdfInteger(0),
                new PdfInteger(1), new PdfInteger(0)])),
            Entry("N", new PdfInteger(1))
        ]);
        PdfDictionary Overprint(bool enabled, int mode) => new([
            Entry("OP", new PdfBoolean(enabled)), Entry("op", new PdfBoolean(enabled)),
            Entry("OPM", new PdfInteger(mode))
        ]);
        PdfDictionary resources = new([
            Entry("ColorSpace", new PdfDictionary([
                Entry("Green", new PdfArray([
                    Name("Separation"), Name("GWG Green"), Name("DeviceCMYK"), tint
                ])),
                Entry("CyanYellow", new PdfArray([
                    Name("DeviceN"), new PdfArray([Name("Cyan"), Name("Yellow")]),
                    Name("DeviceCMYK"), cyanYellowFunction
                ]))
            ])),
            Entry("ExtGState", new PdfDictionary([
                Entry("Off", Overprint(false, 0)),
                Entry("SpotOn", Overprint(true, 0)),
                Entry("Vector", Overprint(overprintMode >= 0, Math.Max(overprintMode, 0))),
                Entry("Half", new PdfDictionary([Entry("ca", new PdfReal(0.5))])),
                Entry("Zero", new PdfDictionary([Entry("ca", new PdfInteger(0))])),
                Entry("WhiteOn", Overprint(true, 0))
            ])),
            Entry("XObject", new PdfDictionary([
                Entry("White", whiteImage), Entry("Mask", maskImage)
            ]))
        ]);
        PdfDictionary group = new([
            Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))
        ]);
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(originalPage
            .Where(entry => !entry.Key.Equals(Name("Resources")) && !entry.Key.Equals(Name("Group")))
            .Append(Entry("Resources", resources)).Append(Entry("Group", group))));
        return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0,
            new PdfRenderOptions(5, 1, includeAnnotations: false, includeFormFields: false));
    }

    private static byte[] Pixel(PdfRenderedPage page, int x) =>
        page.Pixels.Slice(x * 4, 4).ToArray();

    private static PdfArray Numbers(params int[] values) =>
        new(values.Select(value => (PdfObject)new PdfInteger(value)));

    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) =>
        new(Name(key), value);

    private static PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
}
