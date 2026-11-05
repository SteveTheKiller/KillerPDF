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

public sealed class PdfPrecisionProofFormTests
{
    [Fact]
    public void OrdinaryFormProofPreservesGrayPaintMatrixBoundingBoxAndSourceAlpha()
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(3, 1,
            Encoding.ASCII.GetBytes("/Half gs /Form Do")).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(source);
        var form = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Type", Name("XObject")), Entry("Subtype", Name("Form")),
            Entry("BBox", Numbers(0, 0, 1, 1)), Entry("Matrix", Numbers(1, 0, 0, 1, 1, 0)),
            Entry("Resources", new PdfDictionary([]))]),
            Encoding.ASCII.GetBytes(".25 g -1 0 3 1 re f")));
        var resources = new PdfDictionary([
            Entry("XObject", new PdfDictionary([Entry("Form", form)])),
            Entry("ExtGState", new PdfDictionary([Entry("Half", new PdfDictionary([
                Entry("ca", new PdfReal(.5))]))]))]);
        var document = PdfDocument.Open(update.ReplaceObject(page.Reference.ObjectNumber,
            new PdfDictionary(page.Dictionary.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());

        var frame = new PdfPageRenderer(document).RenderFrame(0, Options(3));

        Assert.Equal(0f, frame.Pixels[3]);
        Assert.Equal(0f, frame.Pixels[11]);
        // Source encoded gray .25 has linear Y=.050876088171556791.
        // BPC into printer black p=8192/65535 gives p+(1-p)*Y=.169518387487931350.
        // Proof runs once on straight color, preserving the inherited half alpha.
        double luminance = .2126 * frame.Pixels[4] + .7152 * frame.Pixels[5] + .0722 * frame.Pixels[6];
        Assert.InRange(luminance, .169518387487931350 - .00003, .169518387487931350 + .00003);
        Assert.Equal(.5f, frame.Pixels[7]);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
    }

    [Fact]
    public void NestedOrdinaryFormProofPreservesRgbPaintComposedMatricesAndIntersectedClips()
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(5, 1,
            Encoding.ASCII.GetBytes("/Half gs /Form Do")).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(source);
        var inner = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Form")), Entry("BBox", Numbers(0, 0, 1, 1)),
            Entry("Matrix", Numbers(2, 0, 0, 1, 1, 0)),
            Entry("Resources", new PdfDictionary([]))]),
            Encoding.ASCII.GetBytes(".25 .25 .25 rg -1 0 3 1 re f")));
        var outer = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Form")), Entry("BBox", Numbers(0, 0, 2, 1)),
            Entry("Matrix", Numbers(1, 0, 0, 1, 1, 0)),
            Entry("Resources", new PdfDictionary([
                Entry("XObject", new PdfDictionary([Entry("Inner", inner)]))]))]),
            Encoding.ASCII.GetBytes("/Inner Do")));
        var resources = new PdfDictionary([
            Entry("XObject", new PdfDictionary([Entry("Form", outer)])),
            Entry("ExtGState", new PdfDictionary([Entry("Half", new PdfDictionary([
                Entry("ca", new PdfReal(.5))]))]))]);
        var document = PdfDocument.Open(update.ReplaceObject(page.Reference.ObjectNumber,
            new PdfDictionary(page.Dictionary.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());

        var frame = new PdfPageRenderer(document).RenderFrame(0, Options(5));

        // Inner matrix covers x=2..4; outer BBox clips it to x=2..3.
        foreach (int x in new[] { 0, 1, 3, 4 }) Assert.Equal(0f, frame.Pixels[x * 4 + 3]);
        double luminance = .2126 * frame.Pixels[8] + .7152 * frame.Pixels[9] + .0722 * frame.Pixels[10];
        Assert.InRange(luminance, .169518387487931350 - .00003, .169518387487931350 + .00003);
        Assert.Equal(.5f, frame.Pixels[11]);
    }

    [Theory]
    [InlineData(true, ".25 g 0 0 1 1 re f")]
    [InlineData(false, "0 1 0 0 k 0 0 1 1 re f")]
    [InlineData(false, "/Spot cs .5 scn 0 0 1 1 re f")]
    public void FormProofStillDiagnosesExplicitGroupsAndUnsupportedSourcePaints(bool explicitGroup, string content)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes("/Form Do")).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(source);
        List<KeyValuePair<PdfName, PdfObject>> entries = [
            Entry("Subtype", Name("Form")), Entry("BBox", Numbers(0, 0, 1, 1)),
            Entry("Resources", new PdfDictionary([]))];
        if (explicitGroup) entries.Add(Entry("Group", new PdfDictionary([
            Entry("S", Name("Transparency")), Entry("CS", Name("DeviceRGB"))])));
        var form = update.AddObject(new PdfStream(new PdfDictionary(entries), Encoding.ASCII.GetBytes(content)));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Form", form)]))]);
        var document = PdfDocument.Open(update.ReplaceObject(page.Reference.ObjectNumber,
            new PdfDictionary(page.Dictionary.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());

        var error = Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(document)
            .RenderFrame(0, Options(1)));

        Assert.Contains("printer proof", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static PdfRenderOptions Options(int width) => new(width, 1, transparentBackground: true)
    {
        Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
            options: new PdfProofOptions { BlackPointCompensation = true, SimulateBlack = true })
    };

    private static PdfColorProfile Printer()
    {
        // Independent two-node gray printer fixture: linear TRC from black 8192/65535 to white 1.
        byte[] data = new byte[192];
        Write32(data, 0, data.Length);
        data[8] = 4;
        "prtr"u8.CopyTo(data.AsSpan(12));
        "GRAY"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(data, 128, 2);
        "kTRC"u8.CopyTo(data.AsSpan(132));
        Write32(data, 136, 156); Write32(data, 140, 16);
        "wtpt"u8.CopyTo(data.AsSpan(144));
        Write32(data, 148, 172); Write32(data, 152, 20);
        "curv"u8.CopyTo(data.AsSpan(156));
        Write32(data, 164, 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(168), 8192);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(170), 65535);
        "XYZ "u8.CopyTo(data.AsSpan(172));
        Write32(data, 180, 63190); Write32(data, 184, 65536); Write32(data, 188, 54061);
        return PdfColorProfile.Load(data);
    }

    private static void Write32(byte[] data, int offset, int value) =>
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
    private static PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
