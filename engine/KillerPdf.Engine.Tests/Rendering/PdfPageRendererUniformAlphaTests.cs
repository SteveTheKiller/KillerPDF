using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

[Collection(nameof(SoftMaskAllocationCollection))]
public sealed class PdfPageRendererUniformAlphaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_OpaqueCmykPageDoesNotAllocateAnAlphaPlane(bool paint)
    {
        const int size = 1024;
        byte[] content = paint ? Encoding.ASCII.GetBytes("0 1 1 0 k 0 0 1024 1024 re f") : [];
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(size, size, content).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var group = new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))]);
        PdfDocument document = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(reference.ObjectNumber, new PdfDictionary(page.Append(Entry("Group", group)))).Build());
        var renderer = new PdfPageRenderer(document);
        var options = new PdfRenderOptions(size, size);
        byte[] pixels = new byte[size * size * 4];
        var held = new List<byte[]>();
        try
        {
            // Occupy every possible cached alpha plane within the 32 MiB idle budget.
            // Otherwise an existing rental could hide the allocation being tested.
            for (int index = 0; index < 32; index++) held.Add(PdfScratchBuffers.Bytes.Rent(size * size));
            long before = GC.GetAllocatedBytesForCurrentThread();
            var diagnostics = renderer.RenderInto(0, options, pixels);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 128 * 1024, $"Opaque CMYK rendering allocated {allocated} bytes.");
            Assert.Empty(diagnostics);
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                Assert.Equal(paint ? 35 : 255, pixels[offset]);
                Assert.Equal(paint ? 29 : 255, pixels[offset + 1]);
                Assert.Equal(paint ? 238 : 255, pixels[offset + 2]);
                Assert.Equal(255, pixels[offset + 3]);
            }
        }
        finally
        {
            foreach (byte[] buffer in held) PdfScratchBuffers.Bytes.Return(buffer);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Render_CmykGroupCopiesUniformAndVaryingBackdropAlpha(bool transparent, bool knockout)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(3, 1,
            Encoding.ASCII.GetBytes("0 1 1 0 k 0 0 1 1 re f /Opacity gs /G Do")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var groupSpace = new PdfDictionary([Entry("S", Name("Transparency")),
            Entry("CS", Name("DeviceCMYK")), Entry("K", new PdfBoolean(knockout))]);
        string painting = knockout ? "0 1 1 0 k 0 0 2 1 re f 1 1 0 0 k 1 0 1 1 re f"
            : "1 1 0 0 k 1 0 1 1 re f";
        var form = new PdfStream(new PdfDictionary([Entry("Subtype", Name("Form")),
            Entry("BBox", new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(3), new PdfInteger(1)])),
            Entry("Group", groupSpace), Entry("Resources", new PdfDictionary([]))]),
            Encoding.ASCII.GetBytes(painting));
        var update = new PdfIncrementalUpdateBuilder(source);
        var opacity = new PdfReal(knockout ? 1 : 0.5);
        var resources = new PdfDictionary([
            Entry("XObject", new PdfDictionary([Entry("G", update.AddObject(form))])),
            Entry("ExtGState", new PdfDictionary([Entry("Opacity", new PdfDictionary([
                Entry("ca", opacity), Entry("CA", opacity)]))]))]);
        var rootSpace = new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))]);
        PdfDocument document = PdfDocument.Open(update.ReplaceObject(reference.ObjectNumber,
            new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)).Append(Entry("Group", rootSpace)))).Build());
        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(0,
            new PdfRenderOptions(3, 1, transparentBackground: transparent));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(PdfDeviceCmykTests.RenderInk(0, 255, 255), rendered.Pixels.Slice(0, 4).ToArray());
        byte faded = !transparent && !knockout ? (byte)127 : (byte)0;
        byte middleAlpha = transparent && !knockout ? (byte)128 : (byte)255;
        byte[] middle = PdfDeviceCmykTests.RenderInk((byte)(255 - faded), (byte)(255 - faded));
        middle[3] = middleAlpha;
        Assert.Equal(middle, rendered.Pixels.Slice(4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, transparent ? (byte)0 : (byte)255 },
            rendered.Pixels.Slice(8, 4).ToArray());
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
