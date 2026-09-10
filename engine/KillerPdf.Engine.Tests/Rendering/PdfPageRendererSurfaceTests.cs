using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

[Collection(nameof(SoftMaskAllocationCollection))]
public sealed class PdfPageRendererSurfaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenderInto_RestoringGraphicsStateReusesDiscardedClipBuffers(bool saveEach)
    {
        string clip = "0 0 m 512 0 l 256 512 l h W n 1 0 0 rg 0 0 512 512 re f ";
        if (saveEach) clip = "q " + clip + "Q ";
        string content = "q " + string.Concat(Enumerable.Repeat(clip, 32)) + "Q 0 0 1 rg 0 0 16 16 re f";
        var renderer = new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(512, 512, Encoding.ASCII.GetBytes(content)).Build()));
        var options = new PdfRenderOptions(512, 512);
        byte[] pixels = new byte[512 * 512 * 4];
        renderer.RenderInto(0, options, pixels);
        long before = GC.GetAllocatedBytesForCurrentThread();
        renderer.RenderInto(0, options, pixels);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 2 * 1024 * 1024);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels.AsSpan((511 * 512) * 4, 4).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_NestedSmallGroupsDoNotAllocatePageSizedScratch(bool knockout)
    {
        var renderer = new PdfPageRenderer(CreateDocument(2048, knockout));
        var options = new PdfRenderOptions(2048, 2048) { CacheResult = false };
        renderer.Render(0, options);
        long before = GC.GetAllocatedBytesForCurrentThread();
        PdfRenderedPage large = renderer.Render(0, options);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // The output needs 16 MiB. Eight small groups should not need eight more pages.
        Assert.InRange(allocated, 16 * 1024 * 1024, 20 * 1024 * 1024);
        PdfRenderedPage small = new PdfPageRenderer(CreateDocument(64, knockout))
            .Render(0, new PdfRenderOptions(64, 64));
        for (int y = 0; y < 64; y++)
            Assert.Equal(small.Pixels.Slice(y * 64 * 4, 64 * 4).ToArray(),
                large.Pixels.Slice(((2048 - 64 + y) * 2048) * 4, 64 * 4).ToArray());
        Assert.Empty(large.Diagnostics);
    }

    private static PdfDocument CreateDocument(int size, bool knockout)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(size, size,
            Encoding.ASCII.GetBytes("0.5 g 0 0 2048 2048 re f q 1 0 0 1 17 19 cm /F Do Q")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var pageRef = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(pageRef);
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference? child = null;
        for (int depth = 0; depth < 8; depth++)
        {
            var resources = new List<KeyValuePair<PdfName, PdfObject>> {
                Entry("ExtGState", new PdfDictionary([Entry("Half", new PdfDictionary([
                    Entry("ca", new PdfReal(0.5))]))])) };
            if (child is not null)
                resources.Add(Entry("XObject", new PdfDictionary([Entry("F", child)])));
            string content = "/Half gs 1 0 0 rg 0 0 8 12 re f 0 0 1 rg 4 0 8 12 re f";
            if (child is not null) content += " q 1 0 0 1 0.25 0.5 cm /F Do Q";
            child = update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Subtype", Name("Form")),
                Entry("BBox", new PdfArray([new PdfInteger(0), new PdfInteger(0),
                    new PdfInteger(12), new PdfInteger(12)])),
                Entry("Resources", new PdfDictionary(resources)),
                Entry("Group", new PdfDictionary([Entry("S", Name("Transparency")),
                    Entry("I", new PdfBoolean(true)), Entry("K", new PdfBoolean(knockout))]))
            ]), Encoding.ASCII.GetBytes(content)));
        }
        var pageResources = new PdfDictionary([Entry("XObject",
            new PdfDictionary([Entry("F", child!)]))]);
        update.ReplaceObject(pageRef.ObjectNumber, new PdfDictionary(page
            .Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", pageResources))));
        return PdfDocument.Open(update.Build());
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
