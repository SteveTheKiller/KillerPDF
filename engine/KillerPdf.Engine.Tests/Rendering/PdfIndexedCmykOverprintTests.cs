using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfIndexedCmykOverprintTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CmykImage_OverprintsNamedSpotAndKnocksItOutWhenDisabled(bool indexed)
    {
        PdfRenderedPage overprint = Render(indexed, overprint: true);
        PdfRenderedPage knockout = Render(indexed, overprint: false);

        byte[] spot = Pixel(overprint, 0);
        Assert.Equal(spot, Pixel(overprint, 3));
        Assert.Equal(spot, Pixel(overprint, 1));
        Assert.True(Brightness(Pixel(overprint, 2)) < Brightness(spot));
        Assert.Equal([255, 255, 255, 255], Pixel(knockout, 1));
        Assert.Equal(spot, Pixel(knockout, 0));
        Assert.Equal(spot, Pixel(knockout, 3));
        Assert.Empty(overprint.Diagnostics);
        Assert.Empty(knockout.Diagnostics);
    }

    [Fact]
    public void IndexedAndDirectCmykImages_HaveTheSameNativeOverprintResult()
    {
        PdfRenderedPage indexed = Render(indexed: true, overprint: true);
        PdfRenderedPage direct = Render(indexed: false, overprint: true);

        Assert.Equal(direct.Pixels.ToArray(), indexed.Pixels.ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CmykImage_WhiteSampleReplacesProcessBackdropRegardlessOfOverprintMode(bool indexed)
    {
        PdfRenderedPage modeZero = Render(indexed, overprint: true, overprintMode: 0,
            processBackdrop: true);
        PdfRenderedPage modeOne = Render(indexed, overprint: true, overprintMode: 1,
            processBackdrop: true);

        Assert.Equal(modeZero.Pixels.ToArray(), modeOne.Pixels.ToArray());
        Assert.NotEqual([255, 255, 255, 255], Pixel(modeZero, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(modeZero, 1));
        Assert.Empty(modeZero.Diagnostics);
        Assert.Empty(modeOne.Diagnostics);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CmykImage_WhiteSampleReplacesProcessInkButKeepsSpotInBothOverprintModes(bool indexed)
    {
        PdfRenderedPage modeZero = Render(indexed, overprint: true, overprintMode: 0,
            combinedBackdrop: true);
        PdfRenderedPage modeOne = Render(indexed, overprint: true, overprintMode: 1,
            combinedBackdrop: true);

        byte[] spotOnly = Pixel(modeZero, 3);
        Assert.NotEqual([255, 255, 255, 255], spotOnly);
        Assert.True(Pixel(modeZero, 0)[1] < spotOnly[1]);
        Assert.Equal(spotOnly, Pixel(modeZero, 1));
        Assert.Equal(modeZero.Pixels.ToArray(), modeOne.Pixels.ToArray());
        Assert.Empty(modeZero.Diagnostics);
        Assert.Empty(modeOne.Diagnostics);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    public void CmykImage_PathClipKeepsSpotAtFullAndFractionalCoverage(bool indexed, int overprintMode)
    {
        PdfRenderedPage overprint = Render(indexed, overprint: true, overprintMode,
            pathClip: true, whiteSamples: true);
        PdfRenderedPage knockout = Render(indexed, overprint: false, overprintMode,
            pathClip: true, whiteSamples: true);

        byte[] spot = Pixel(overprint, 0);
        Assert.NotEqual([255, 255, 255, 255], spot);
        Assert.Equal(spot, Pixel(overprint, 1));
        Assert.Equal(spot, Pixel(overprint, 2));
        Assert.Equal(spot, Pixel(overprint, 3));
        Assert.Equal(spot, Pixel(knockout, 0));
        Assert.Equal(spot, Pixel(knockout, 3));
        Assert.Equal([255, 255, 255, 255], Pixel(knockout, 2));
        Assert.True(Brightness(Pixel(knockout, 1)) > Brightness(spot));
        Assert.True(Brightness(Pixel(knockout, 1)) < 3 * 255);
        Assert.Empty(overprint.Diagnostics);
        Assert.Empty(knockout.Diagnostics);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    public void CmykImage_PathClipPartiallyAppliesBlackInkOverSpot(bool indexed, int overprintMode)
    {
        PdfRenderedPage page = Render(indexed, overprint: true, overprintMode,
            pathClip: true, blackSamples: true);

        byte[] spot = Pixel(page, 0);
        Assert.Equal(spot, Pixel(page, 3));
        int halfCovered = Brightness(Pixel(page, 1));
        int fullyCovered = Brightness(Pixel(page, 2));
        Assert.True(fullyCovered < halfCovered);
        Assert.True(halfCovered < Brightness(spot));
        Assert.Empty(page.Diagnostics);
    }

    private static PdfRenderedPage Render(bool indexed, bool overprint, int overprintMode = 0,
        bool processBackdrop = false, bool combinedBackdrop = false,
        bool pathClip = false, bool whiteSamples = false, bool blackSamples = false)
    {
        string imagePaint = pathClip
            ? "q /O gs 1.5 0 m 3 0 l 3 1 l 1.5 1 l h W n 2 0 0 1 1 0 cm /Im Do Q"
            : "q /O gs 2 0 0 1 1 0 cm /Im Do Q";
        string content = combinedBackdrop
            ? "0 1 0 0 k 0 0 4 1 re f 0 0 0 0 k 3 0 1 1 re f " +
                "q /O gs /Green cs 1 scn 0 0 4 1 re f 2 0 0 1 1 0 cm /Im Do Q"
            : processBackdrop
                ? "0 1 0 0 k 0 0 4 1 re f " + imagePaint
                : "/Green cs 1 scn 0 0 4 1 re f " + imagePaint;
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(4, 1, Encoding.ASCII.GetBytes(content)).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(source).Pages[0];
        PdfDictionary originalPage = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfObject imageSpace = indexed
            ? new PdfArray([Name("Indexed"), Name("DeviceCMYK"), new PdfInteger(1),
                new PdfString([0, 0, 0, 0, 0, 0, 0, 128], PdfStringForm.Hexadecimal)])
            : Name("DeviceCMYK");
        byte[] samples = indexed
            ? blackSamples ? [1, 1] : whiteSamples ? [0, 0] : [0, 1]
            : blackSamples ? [0, 0, 0, 128, 0, 0, 0, 128]
                : whiteSamples ? [0, 0, 0, 0, 0, 0, 0, 0] : [0, 0, 0, 0, 0, 0, 0, 128];
        PdfIndirectReference image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)),
            Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(8)),
            Entry("ColorSpace", imageSpace)
        ]), samples));
        PdfDictionary tint = new([
            Entry("FunctionType", new PdfInteger(2)),
            Entry("Domain", Numbers(0, 1)), Entry("C0", Numbers(0, 0, 0, 0)),
            Entry("C1", new PdfArray([new PdfReal(0.5), new PdfInteger(0),
                new PdfInteger(1), new PdfInteger(0)])), Entry("N", new PdfInteger(1))
        ]);
        PdfDictionary resources = new([
            Entry("ColorSpace", new PdfDictionary([Entry("Green", new PdfArray([
                Name("Separation"), Name("GWGGreen"), Name("DeviceCMYK"), tint
            ]))])),
            Entry("ExtGState", new PdfDictionary([Entry("O", new PdfDictionary([
                Entry("OP", new PdfBoolean(overprint)),
                Entry("op", new PdfBoolean(overprint)),
                Entry("OPM", new PdfInteger(overprintMode))
            ]))])),
            Entry("XObject", new PdfDictionary([Entry("Im", image)]))
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

    private static int Brightness(byte[] pixel) => pixel[0] + pixel[1] + pixel[2];

    private static PdfArray Numbers(params int[] values) =>
        new(values.Select(value => (PdfObject)new PdfInteger(value)));

    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) =>
        new(Name(key), value);

    private static PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
}
