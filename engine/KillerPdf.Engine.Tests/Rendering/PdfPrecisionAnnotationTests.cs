using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionAnnotationTests
{
    [Fact]
    public void SynthesizedAnnotationColorsRetainTheirSourceDistinction()
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(2, 1).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(source);
        var annotations = new List<PdfObject>();
        for (int index = 0; index < 2; index++)
        {
            double gray = index == 0 ? .50011 : .50012;
            annotations.Add(update.AddObject(new PdfDictionary([
                Entry("Type", Name("Annot")), Entry("Subtype", Name("Square")),
                Entry("Rect", Numbers(index, 0, index + 1, 1)),
                Entry("IC", new PdfArray([new PdfReal(gray), new PdfReal(gray), new PdfReal(gray)])),
                Entry("BS", new PdfDictionary([Entry("W", new PdfInteger(0))]))])));
        }
        byte[] bytes = update.ReplaceObject(page.Reference.ObjectNumber,
            new PdfDictionary(page.Dictionary.Append(Entry("Annots", new PdfArray(annotations))))).Build();
        byte[] preserved = bytes.ToArray();
        var frame = new PdfPageRenderer(PdfDocument.Open(bytes)).RenderFrame(0, new PdfRenderOptions(2, 1));
        for (int channel = 0; channel < 3; channel++)
        {
            Assert.InRange(frame.Pixels[channel], .21414295f, .21414300f);
            Assert.InRange(frame.Pixels[4 + channel], .21415221f, .21415226f);
        }
        Assert.True(frame.Pixels[4] - frame.Pixels[0] > .000009f);
        Assert.Equal(preserved, bytes);
    }

    [Fact]
    public void SavedRgbAnnotationAppearanceRetainsFractionalPageAndAppearanceColors()
    {
        byte[] bytes = DocumentWithAppearance();
        byte[] preserved = bytes.ToArray();
        var renderer = new PdfPageRenderer(PdfDocument.Open(bytes));
        var frame = renderer.RenderFrame(0, new PdfRenderOptions(3, 1));

        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        for (int channel = 0; channel < 3; channel++)
        {
            Assert.InRange(frame.Pixels[channel], .2141336f, .2141339f);
            Assert.InRange(frame.Pixels[4 + channel], .2142261f, .2142265f);
            Assert.Equal(1f, frame.Pixels[8 + channel]);
        }
        Assert.True(frame.Pixels[4] - frame.Pixels[0] > .00008f);
        Assert.Equal(1f, frame.Pixels[3]);
        Assert.Equal(1f, frame.Pixels[7]);
        Assert.Equal(1f, frame.Pixels[11]);
        Assert.Equal(preserved, bytes);
    }

    [Theory]
    [InlineData(4, true, false, true)]
    [InlineData(4, false, false, false)]
    [InlineData(2, true, false, false)]
    [InlineData(32, true, false, false)]
    [InlineData(0, true, true, false)]
    [InlineData(4, true, true, true)]
    public void AnnotationVisibilityAndRegionPreservePrecisePageColors(int flags,
        bool included, bool printing, bool visible)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(DocumentWithAppearance(flags)));
        var options = new PdfRenderOptions(3, 1, includeAnnotations: included) { ForPrinting = printing };
        var frame = renderer.RenderFrameRegion(0, options, 0, 0, 2, 1);
        Assert.Equal(2, frame.Width);
        for (int channel = 0; channel < 3; channel++)
        {
            Assert.InRange(frame.Pixels[channel], .2141336f, .2141339f);
            if (visible) Assert.InRange(frame.Pixels[4 + channel], .2142261f, .2142265f);
            else Assert.Equal(1f, frame.Pixels[4 + channel]);
        }
        Assert.Equal(1f, frame.Pixels[3]);
        Assert.Equal(1f, frame.Pixels[7]);
    }

    [Fact]
    public void UnsupportedAnnotationPaintStillDiagnosesPrecisionInsteadOfReturningBytes()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(DocumentWithAppearance(
            appearanceContent: "0 Tr BT /Missing 12 Tf (A) Tj ET")));
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(3, 1)));
    }

    [Fact]
    public void AnnotationProofUsesTheSameColorTransformAndRetainsSourceAlpha()
    {
        byte[] bytes = DocumentWithAppearance(appearanceContent: "/Half gs .25 .25 .25 rg 0 0 1 1 re f");
        byte[] preserved = bytes.ToArray();
        var renderer = new PdfPageRenderer(PdfDocument.Open(bytes));
        var options = new PdfRenderOptions(3, 1, transparentBackground: true)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
                options: new PdfProofOptions { BlackPointCompensation = true, SimulateBlack = true })
        };
        var frame = renderer.RenderFrameRegion(0, options, 1, 0, 1, 1);
        // Encoded .25 has Y=.05087608817; printer black is 8192/65535.
        // Compensated Y is .16951838749, independently of the retained half alpha.
        double luminance = .2126 * frame.Pixels[0] + .7152 * frame.Pixels[1] + .0722 * frame.Pixels[2];
        Assert.InRange(luminance, .16948838749, .16954838749);
        Assert.Equal(.5f, frame.Pixels[3]);
        Assert.Equal(preserved, bytes);
    }

    private static PdfColorProfile Printer()
    {
        byte[] data = new byte[192];
        Write32(0, data.Length); data[8] = 4;
        "prtr"u8.CopyTo(data.AsSpan(12)); "GRAY"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20)); "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(128, 2);
        "kTRC"u8.CopyTo(data.AsSpan(132)); Write32(136, 156); Write32(140, 16);
        "wtpt"u8.CopyTo(data.AsSpan(144)); Write32(148, 172); Write32(152, 20);
        "curv"u8.CopyTo(data.AsSpan(156)); Write32(164, 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(168), 8192);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(170), 65535);
        "XYZ "u8.CopyTo(data.AsSpan(172));
        Write32(180, 63190); Write32(184, 65536); Write32(188, 54061);
        return PdfColorProfile.Load(data);
        void Write32(int offset, int value) => BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
    }

    private static byte[] DocumentWithAppearance(int flags = 4,
        string appearanceContent = ".5002 .5002 .5002 rg 0 0 1 1 re f")
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(3, 1,
            Encoding.ASCII.GetBytes(".5001 .5001 .5001 rg 0 0 1 1 re f")).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(source);
        var appearance = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Type", Name("XObject")), Entry("Subtype", Name("Form")),
            Entry("BBox", Numbers(0, 0, 1, 1)), Entry("Resources", new PdfDictionary([
                Entry("ExtGState", new PdfDictionary([Entry("Half", new PdfDictionary([
                    Entry("ca", new PdfReal(.5))]))]))]))]),
            Encoding.ASCII.GetBytes(appearanceContent)));
        var annotation = update.AddObject(new PdfDictionary([
            Entry("Type", Name("Annot")), Entry("Subtype", Name("Square")),
            Entry("Rect", Numbers(1, 0, 2, 1)), Entry("F", new PdfInteger(flags)),
            Entry("AP", new PdfDictionary([Entry("N", appearance)]))]));
        return update.ReplaceObject(page.Reference.ObjectNumber,
            new PdfDictionary(page.Dictionary.Append(Entry("Annots", new PdfArray([annotation]))))).Build();
    }

    private static PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
