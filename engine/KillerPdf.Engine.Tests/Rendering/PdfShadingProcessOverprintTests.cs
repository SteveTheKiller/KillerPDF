using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfShadingProcessOverprintTests
{
    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    public void AxialCmykShading_ReplacesZeroProcessChannelsAndHonorsSpotOverprint(
        bool overprint, int mode)
    {
        PdfRenderedPage rendered = Render(overprint, mode);
        byte[] shadedProcess = Pixel(rendered, 0);
        byte[] shadedOverSpot = Pixel(rendered, 1);
        byte[] processReference = Pixel(rendered, 2);
        byte[] spotReference = Pixel(rendered, 3);

        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(processReference, shadedProcess);
        if (overprint)
        {
            Assert.Equal(spotReference, shadedOverSpot);
            Assert.NotEqual(shadedProcess, shadedOverSpot);
        }
        else
        {
            Assert.Equal(shadedProcess, shadedOverSpot);
            Assert.Equal(processReference, spotReference);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ClippedCmykShading_OverprintCommutesWithNamedSpot(int mode)
    {
        PdfRenderedPage expected = Render(true, mode, clipped: true, spotLast: true);
        PdfRenderedPage actual = Render(true, mode, clipped: true);

        Assert.NotEqual([255, 255, 255, 255], Pixel(expected, 1));
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        Assert.Empty(expected.Diagnostics);
        Assert.Empty(actual.Diagnostics);
    }

    private static PdfRenderedPage Render(bool overprint, int mode,
        bool clipped = false, bool spotLast = false)
    {
        string spot = "q /SpotOn gs /Green cs 1 scn 1 0 1 1 re f Q ";
        string content = "0 1 0 0 k 0 0 4 1 re f "
            + (spotLast ? "" : spot)
            + "q /Shade gs " + (clipped ? ".5 0 1 1 re W n " : "") + "/Sh1 sh Q "
            + (spotLast ? spot : "")
            + "q /Off gs .5 0 0 0 k 2 0 2 1 re f Q "
            + (overprint ? "q /SpotOn gs /Green cs 1 scn 3 0 1 1 re f Q" : "");
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(4, 1, Encoding.ASCII.GetBytes(content)).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(source).Pages[0];
        PdfDictionary originalPage = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfDictionary greenTint = new([
            Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
            Entry("C0", Numbers(0, 0, 0, 0)), Entry("C1", Numbers(0, 1, 0, 0)),
            Entry("N", new PdfInteger(1))
        ]);
        PdfArray halfCyan = new([new PdfReal(0.5), new PdfInteger(0),
            new PdfInteger(0), new PdfInteger(0)]);
        PdfDictionary shadingFunction = new([
            Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
            Entry("C0", halfCyan), Entry("C1", halfCyan), Entry("N", new PdfInteger(1))
        ]);
        PdfDictionary shading = new([
            Entry("ShadingType", new PdfInteger(2)), Entry("ColorSpace", Name("DeviceCMYK")),
            Entry("Coords", Numbers(0, 0, 2, 0)), Entry("Function", shadingFunction),
            Entry("Extend", new PdfArray([new PdfBoolean(true), new PdfBoolean(true)])),
            Entry("BBox", Numbers(0, 0, 2, 1))
        ]);
        static PdfDictionary Overprint(bool enabled, int overprintMode) => new([
            Entry("OP", new PdfBoolean(enabled)), Entry("op", new PdfBoolean(enabled)),
            Entry("OPM", new PdfInteger(overprintMode))
        ]);
        PdfDictionary resources = new([
            Entry("ColorSpace", new PdfDictionary([
                Entry("Green", new PdfArray([
                    Name("Separation"), Name("GWG Green"), Name("DeviceCMYK"), greenTint
                ]))
            ])),
            Entry("ExtGState", new PdfDictionary([
                Entry("Off", Overprint(false, 0)),
                Entry("SpotOn", Overprint(true, 0)),
                Entry("Shade", Overprint(overprint, mode))
            ])),
            Entry("Shading", new PdfDictionary([Entry("Sh1", shading)]))
        ]);
        PdfDictionary group = new([
            Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))
        ]);
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(originalPage
            .Where(entry => !entry.Key.Equals(Name("Resources")) && !entry.Key.Equals(Name("Group")))
            .Append(Entry("Resources", resources)).Append(Entry("Group", group))));
        return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0,
            new PdfRenderOptions(4, 1, includeAnnotations: false, includeFormFields: false));
    }

    private static byte[] Pixel(PdfRenderedPage page, int x) =>
        page.Pixels.Slice(x * 4, 4).ToArray();

    private static PdfArray Numbers(params int[] values) =>
        new(values.Select(value => (PdfObject)new PdfInteger(value)));

    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) =>
        new(Name(key), value);

    private static PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
}
