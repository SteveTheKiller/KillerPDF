using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfGray16LookupTests
{
    [Theory]
    [InlineData(false, false, false, "7550C19351DE86E16F911706262B2B82904D0BD52948436659EE6883E07A4924")]
    [InlineData(false, false, true, "C109A1B8EA135D645DE8F04929BAA2B171B48CA6D5C0403080C6D10F24639955")]
    [InlineData(false, true, false, "90B21014ED288AC1B1A6C6E6984C15D68FBEF561E5EC485136668E36EAD8DD9D")]
    [InlineData(false, true, true, "6614E8089C1275EE880D648734EF9B685928EE9E97A42F582B0FCD387BE648EC")]
    [InlineData(true, false, false, "919FC5904E1F61D787E741A9D2F26BC36FCAE98F62816A3104B015528031ED52")]
    [InlineData(true, false, true, "53FD892B48E7738DF76E8D18B5E69A27EB301C6B0CDC9DEE71E668B73A28EF5B")]
    [InlineData(true, true, false, "CA95DEA41F951AC1F5D400EA190256E2DDE6480DD581D112D3DDA1C90393F6B9")]
    [InlineData(true, true, true, "4B5CB1211EFE8C19820873E23FE385EAC2FC078FA29E11202EE17444A43254A0")]
    public void FullSampleRangeMatchesUncachedReference(bool matte, bool inverted, bool reduced, string expected)
    {
        // References precede 16-bit lookup caching. Every sample occurs twice,
        // with different alpha values when matte correction is enabled.
        var result = new PdfPageRenderer(Create(matte, inverted)).Render(0,
            new PdfRenderOptions(reduced ? 63 : 256, reduced ? 127 : 512, transparentBackground: true));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(result.Pixels.Span)));
    }

    private static PdfDocument Create(bool matte, bool inverted)
    {
        const int width = 256, height = 512;
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(width, height,
            Encoding.ASCII.GetBytes("256 0 0 512 0 0 cm /Image Do")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        byte[] samples = new byte[width * height * 2];
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            samples[pixel * 2] = (byte)(pixel >> 8);
            samples[pixel * 2 + 1] = (byte)pixel;
        }
        var space = new PdfArray([Name("CalGray"), new PdfDictionary([
            Entry("WhitePoint", new PdfArray([new PdfReal(.9505), new PdfInteger(1), new PdfReal(1.089)])),
            Entry("Gamma", new PdfReal(2.2))])]);
        var entries = new List<KeyValuePair<PdfName, PdfObject>> {
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(width)),
            Entry("Height", new PdfInteger(height)), Entry("ColorSpace", space),
            Entry("BitsPerComponent", new PdfInteger(16)),
            Entry("Decode", new PdfArray([new PdfInteger(inverted ? 1 : 0), new PdfInteger(inverted ? 0 : 1)])) };
        if (matte)
        {
            var mask = update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(width)),
                Entry("Height", new PdfInteger(height)), Entry("ColorSpace", Name("DeviceGray")),
                Entry("BitsPerComponent", new PdfInteger(8)), Entry("Matte", new PdfArray([new PdfReal(.2)]))]),
                Enumerable.Range(0, width * height).Select(pixel => (byte)(32 + (pixel / 65536 * 97 + pixel % 251) % 224)).ToArray()));
            entries.Add(Entry("SMask", mask));
        }
        var image = update.AddObject(new PdfStream(new PdfDictionary(entries), samples));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Image", image)]))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page
            .Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources))));
        return PdfDocument.Open(update.Build());
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
