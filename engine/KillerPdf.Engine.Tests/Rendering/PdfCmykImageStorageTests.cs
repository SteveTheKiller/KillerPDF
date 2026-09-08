using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

[Collection(nameof(SoftMaskAllocationCollection))]
public sealed class PdfCmykImageStorageTests
{
    [Fact]
    public void MatchingInkImageDoesNotAllocateASecondImagePlane()
    {
        const int size = 1024;
        var renderer = new PdfPageRenderer(Create(size, false, false, false));
        var options = new PdfRenderOptions(size, size);
        byte[] pixels = new byte[size * size * 4];
        Assert.Empty(renderer.RenderInto(0, options, pixels));
        var held = new List<byte[]>();
        try
        {
            // Drain the entire idle scratch budget so a pooled plane cannot hide the copy.
            for (int index = 0; index < 8; index++)
                held.Add(PdfScratchBuffers.Bytes.Rent(pixels.Length));
            long before = GC.GetAllocatedBytesForCurrentThread();
            var diagnostics = renderer.RenderInto(0, options, pixels);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Empty(diagnostics);
            Assert.True(allocated < 128 * 1024, $"Matching CMYK image allocated {allocated} bytes.");
        }
        finally
        {
            foreach (byte[] buffer in held) PdfScratchBuffers.Bytes.Return(buffer);
        }
    }

    [Theory]
    [InlineData(5, false, false)]
    [InlineData(5, true, false)]
    [InlineData(5, false, true)]
    [InlineData(5, true, true)]
    [InlineData(23, false, false)]
    [InlineData(23, true, false)]
    [InlineData(23, false, true)]
    [InlineData(23, true, true)]
    public void DirectInkSamplesMatchFullConversion(int outputSize, bool rotated, bool masked)
    {
        var options = new PdfRenderOptions(outputSize, outputSize, transparentBackground: true);
        var direct = new PdfPageRenderer(Create(17, false, rotated, masked)).Render(0, options);
        var converted = new PdfPageRenderer(Create(17, true, rotated, masked)).Render(0, options);
        Assert.Empty(direct.Diagnostics);
        Assert.Empty(converted.Diagnostics);
        Assert.Equal(converted.Pixels.ToArray(), direct.Pixels.ToArray());
    }

    private static PdfDocument Create(int size, bool wideSamples, bool rotated, bool masked)
    {
        string matrix = rotated ? $"0 {size} -{size} 0 {size} 0" : $"{size} 0 0 {size} 0 0";
        string clip = masked ? $"1 1 {size - 2} {size - 2} re W n /Half gs " : "";
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(size, size,
            Encoding.ASCII.GetBytes($"{clip}{matrix} cm /Image Do")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        byte[] samples = new byte[size * size * 4 * (wideSamples ? 2 : 1)];
        for (int index = 0; index < size * size * 4; index++)
        {
            byte sample = (byte)(index * 37 + index / (size * 4) * 13);
            if (wideSamples) samples[index * 2] = samples[index * 2 + 1] = sample;
            else samples[index] = sample;
        }
        var entries = new List<KeyValuePair<PdfName, PdfObject>> {
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(size)),
            Entry("Height", new PdfInteger(size)), Entry("ColorSpace", Name("DeviceCMYK")),
            Entry("BitsPerComponent", new PdfInteger(wideSamples ? 16 : 8)) };
        if (masked)
        {
            byte[] alpha = Enumerable.Range(0, size * size).Select(index => (byte)(index * 53)).ToArray();
            entries.Add(Entry("SMask", update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(size)),
                Entry("Height", new PdfInteger(size)), Entry("ColorSpace", Name("DeviceGray")),
                Entry("BitsPerComponent", new PdfInteger(8))]), alpha))));
        }
        var resources = new PdfDictionary([
            Entry("XObject", new PdfDictionary([Entry("Image", update.AddObject(new PdfStream(new PdfDictionary(entries), samples)))])),
            Entry("ExtGState", new PdfDictionary([Entry("Half", new PdfDictionary([Entry("ca", new PdfReal(0.5))]))]))]);
        var group = new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page
            .Where(pair => !pair.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", resources)).Append(Entry("Group", group))));
        return PdfDocument.Open(update.Build());
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
