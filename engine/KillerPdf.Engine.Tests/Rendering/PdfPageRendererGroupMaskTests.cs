using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Writing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererGroupMaskTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_NonIsolatedGroupKeepsOuterMaskAcrossInnerReset(bool resetMask)
    {
        PdfDocument document = Create("", "1 g 0 0 5 10 re f",
            "1 0 0 rg 0 0 10 10 re f", resetMask, "Normal", 1);
        PdfRenderedPage page = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(10, 10));

        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(page, 2));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(page, 7));
        Assert.Empty(page.Diagnostics);
    }

    [Fact]
    public void Render_NonIsolatedMaskedGroupBlendsAgainstItsBackdrop()
    {
        PdfDocument document = Create("0.5 g 0 0 10 10 re f", "1 g 0 0 5 10 re f",
            "1 0 0 rg 0 0 10 10 re f", true, "Multiply", 0.5);
        PdfRenderedPage page = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(10, 10));

        Assert.Equal(new byte[] { 64, 64, 128, 255 }, Pixel(page, 2));
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, Pixel(page, 7));
        Assert.Empty(page.Diagnostics);
    }

    [Fact]
    public void Render_NonIsolatedGroupAppliesMaskOnceToOverlappingObjects()
    {
        PdfDocument document = Create("", "0.5 g 0 0 10 10 re f",
            "1 0 0 rg 0 0 10 10 re f 0 0 1 rg 0 0 10 10 re f", true, "Normal", 1);
        PdfRenderedPage page = new PdfPageRenderer(document).Render(0,
            new PdfRenderOptions(10, 10, transparentBackground: true));

        Assert.Equal(new byte[] { 255, 0, 0, 128 }, Pixel(page, 2));
        Assert.Empty(page.Diagnostics);
    }

    private static byte[] Pixel(PdfRenderedPage page, int x) =>
        page.Pixels.Span.Slice((5 * page.Width + x) * 4, 4).ToArray();

    private static PdfDocument Create(string backdrop, string maskContent, string painting,
        bool resetMask, string blendMode, double opacity)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(10, 10,
            Encoding.ASCII.GetBytes(backdrop + " q /Mask gs /Paint Do Q")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var pageReference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(pageReference);
        var update = new PdfIncrementalUpdateBuilder(source);
        var group = new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", Name("DeviceRGB"))]);
        var mask = new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Form")), Entry("BBox", Bounds()), Entry("Group", group),
            Entry("Resources", new PdfDictionary([]))]), Encoding.ASCII.GetBytes(maskContent));
        var innerState = new PdfDictionary(new[] {
            Entry("BM", Name(blendMode)), Entry("ca", new PdfReal(opacity))
        }.Concat(resetMask ? [Entry("SMask", Name("None"))] : []));
        var form = new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Form")), Entry("BBox", Bounds()), Entry("Group", group),
            Entry("Resources", new PdfDictionary([Entry("ExtGState",
                new PdfDictionary([Entry("Inner", innerState)]))]))
        ]), Encoding.ASCII.GetBytes("/Inner gs " + painting));
        var resources = new PdfDictionary([
            Entry("ExtGState", new PdfDictionary([Entry("Mask", new PdfDictionary([
                Entry("SMask", new PdfDictionary([Entry("S", Name("Luminosity")),
                    Entry("BC", new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(0)])),
                    Entry("G", update.AddObject(mask))]))]))])),
            Entry("XObject", new PdfDictionary([Entry("Paint", update.AddObject(form))]))
        ]);
        return PdfDocument.Open(update.ReplaceObject(pageReference.ObjectNumber,
            new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());
    }

    private static PdfArray Bounds() => new([new PdfInteger(0), new PdfInteger(0), new PdfInteger(10), new PdfInteger(10)]);
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
