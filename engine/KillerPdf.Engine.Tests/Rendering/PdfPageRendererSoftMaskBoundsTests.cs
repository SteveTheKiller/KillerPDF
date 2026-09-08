using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

[Collection(nameof(SoftMaskAllocationCollection))]
public sealed class PdfPageRendererSoftMaskBoundsTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void Render_CompactSoftMaskRetainsOutsideBackdropAndTransfer(bool luminosity, bool invert,
        bool partial)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(10, 10,
            Encoding.ASCII.GetBytes("/Mask gs 1 0 0 rg 0 0 10 10 re f")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var group = new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Form")), Entry("BBox", Array(0, 0, 2, 2)),
            Entry("Matrix", Array(1, 0, 0, 1, 4, 5)),
            Entry("Group", new PdfDictionary([Entry("S", Name("Transparency")),
                Entry("CS", Name("DeviceGray"))])), Entry("Resources", new PdfDictionary([]))
        ]), Encoding.ASCII.GetBytes(partial ? "1 g 0 0 1 2 re f" : "1 g 0 0 2 2 re f"));
        var update = new PdfIncrementalUpdateBuilder(source);
        var maskEntries = new List<KeyValuePair<PdfName, PdfObject>> {
            Entry("S", Name(luminosity ? "Luminosity" : "Alpha")),
            Entry("G", update.AddObject(group)), Entry("BC", Array(0.25)) };
        if (invert)
            maskEntries.Add(Entry("TR", new PdfDictionary([
                Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Array(0, 1)),
                Entry("C0", Array(1)), Entry("C1", Array(0)), Entry("N", new PdfInteger(1))])));
        var resources = new PdfDictionary([Entry("ExtGState", new PdfDictionary([
            Entry("Mask", new PdfDictionary([Entry("SMask", new PdfDictionary(maskEntries))]))]))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page
            .Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources))));
        PdfRenderedPage rendered = new PdfPageRenderer(PdfDocument.Open(update.Build()))
            .Render(0, new PdfRenderOptions(10, 10));
        int outsideAlpha = luminosity ? 64 : 0;
        if (invert) outsideAlpha = 255 - outsideAlpha;
        for (int y = 0; y < 10; y++)
            for (int x = 0; x < 10; x++)
            {
                bool inside = x >= 4 && x < (partial ? 5 : 6) && y >= 3 && y < 5;
                int alpha = inside ? invert ? 0 : 255 : outsideAlpha;
                Assert.Equal(new byte[] { (byte)(255 - alpha), (byte)(255 - alpha), 255, 255 },
                    rendered.Pixels.Slice((y * 10 + x) * 4, 4).ToArray());
            }
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_UniformSoftMaskDoesNotAllocateAPixelPlane(bool luminosity)
    {
        const int size = 1024;
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(size, size,
            Encoding.ASCII.GetBytes("/Mask gs 1 0 0 rg 0 0 1024 1024 re f")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        var group = new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Form")), Entry("BBox", Array(0, 0, size, size)),
            Entry("Group", new PdfDictionary([Entry("S", Name("Transparency")),
                Entry("CS", Name("DeviceGray"))])), Entry("Resources", new PdfDictionary([]))
        ]), []);
        var mask = new PdfDictionary([Entry("S", Name(luminosity ? "Luminosity" : "Alpha")),
            Entry("G", update.AddObject(group)), Entry("BC", Array(0.25))]);
        var resources = new PdfDictionary([Entry("ExtGState", new PdfDictionary([
            Entry("Mask", new PdfDictionary([Entry("SMask", mask)]))]))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page
            .Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources))));
        var renderer = new PdfPageRenderer(PdfDocument.Open(update.Build()));
        var options = new PdfRenderOptions(size, size);
        byte[] pixels = new byte[size * size * 4];
        renderer.RenderInto(0, options, pixels);
        renderer.RenderInto(0, options, pixels);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var diagnostics = renderer.RenderInto(0, options, pixels);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 64 * 1024, $"Uniform mask allocated {allocated} bytes.");
        Assert.Empty(diagnostics);
        byte expected = luminosity ? (byte)191 : (byte)255;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            Assert.Equal(expected, pixels[offset]);
            Assert.Equal(expected, pixels[offset + 1]);
            Assert.Equal(255, pixels[offset + 2]);
            Assert.Equal(255, pixels[offset + 3]);
        }
    }

    private static PdfArray Array(params double[] values) => new(values.Select(value => (PdfObject)new PdfReal(value)));
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}

// Other render tests can evict warmed scratch buffers and contaminate allocation measurements.
[CollectionDefinition(nameof(SoftMaskAllocationCollection), DisableParallelization = true)]
public sealed class SoftMaskAllocationCollection;
