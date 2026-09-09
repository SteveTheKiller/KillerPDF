using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

[Collection(nameof(SoftMaskAllocationCollection))]
public sealed class PdfPageRendererStencilStorageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_StencilDoesNotAllocateAColorPlane(bool cmyk)
    {
        const int size = 1024;
        byte[] samples = new byte[size * size / 8];
        for (int row = 0; row < size; row++)
            samples.AsSpan(row * size / 8, size / 8).Fill(row % 2 == 0 ? (byte)0x55 : (byte)0xAA);
        byte[] prefix = Encoding.ASCII.GetBytes(
            "1 0 0 rg 1024 0 0 1024 0 0 cm BI /W 1024 /H 1024 /IM true ID ");
        byte[] content = [.. prefix, .. samples, .. Encoding.ASCII.GetBytes(" EI")];
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(size, size, content).Build());
        if (cmyk)
        {
            var catalog = (PdfDictionary)document.Resolve((PdfIndirectReference)document.Trailer[Name("Root")]);
            var pages = (PdfDictionary)document.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
            var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
            var page = (PdfDictionary)document.Resolve(reference);
            var group = new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))]);
            document = PdfDocument.Open(new PdfIncrementalUpdateBuilder(document)
                .ReplaceObject(reference.ObjectNumber, new PdfDictionary(page.Append(Entry("Group", group)))).Build());
        }
        var renderer = new PdfPageRenderer(document);
        var options = new PdfRenderOptions(size, size, transparentBackground: true);
        byte[] pixels = new byte[size * size * 4];
        long before = GC.GetAllocatedBytesForCurrentThread();
        var diagnostics = renderer.RenderInto(0, options, pixels);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 3 * 1024 * 1024, $"Stencil rendering allocated {allocated} bytes.");
        Assert.Empty(diagnostics);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool paints = (x + y) % 2 == 0;
                int offset = (y * size + x) * 4;
                Assert.Equal(paints ? cmyk ? 35 : 0 : 255, pixels[offset]);
                Assert.Equal(paints ? cmyk ? 29 : 0 : 255, pixels[offset + 1]);
                Assert.Equal(paints && cmyk ? 238 : 255, pixels[offset + 2]);
                Assert.Equal(paints ? 255 : 0, pixels[offset + 3]);
            }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Render_StencilPreservesClipOpacityDecodeAndQuarterTurn(bool reverse, bool rotated)
    {
        byte[] samples = Enumerable.Range(0, 9).SelectMany(_ => new byte[] { 0x0F, 0x80 }).ToArray();
        string matrix = rotated ? "0 9 -9 0 9 0" : "9 0 0 9 0 0";
        string decode = reverse ? "1 0" : "0 1";
        byte[] prefix = Encoding.ASCII.GetBytes(
            $"2 2 5 5 re W n /Half gs 1 0 0 rg {matrix} cm BI /W 9 /H 9 /IM true /D [{decode}] ID ");
        byte[] content = [.. prefix, .. samples, .. Encoding.ASCII.GetBytes(" EI")];
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(9, 9, content).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var resources = new PdfDictionary([Entry("ExtGState", new PdfDictionary([
            Entry("Half", new PdfDictionary([Entry("ca", new PdfReal(0.5))]))]))]);
        PdfDocument document = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(reference.ObjectNumber, new PdfDictionary(page
                .Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources)))).Build());
        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(9, 9));
        Assert.Empty(rendered.Diagnostics);
        for (int y = 0; y < 9; y++)
            for (int x = 0; x < 9; x++)
            {
                bool paints = ((rotated ? y >= 5 : x < 4) != reverse)
                    && x >= 2 && x < 7 && y >= 2 && y < 7;
                byte level = paints ? (byte)127 : (byte)255;
                Assert.Equal(new byte[] { level, level, 255, 255 },
                    rendered.Pixels.Slice((y * 9 + x) * 4, 4).ToArray());
            }
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
