using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

[Collection(nameof(SoftMaskAllocationCollection))]
public sealed class PdfDeviceImageStorageTests
{
    [Theory]
    [InlineData(3, false, false)]
    [InlineData(3, false, true)]
    [InlineData(3, true, false)]
    [InlineData(3, true, true)]
    [InlineData(4, false, false)]
    [InlineData(4, false, true)]
    [InlineData(4, true, false)]
    [InlineData(4, true, true)]
    public void WideSampleCache_PreservesFullKeysAndDecodeRanges(int components, bool targetInk, bool reverse)
    {
        const int width = 128, height = 96;
        byte[] samples = new byte[width * height * components * 2];
        new Random(17331).NextBytes(samples);
        double[] decode = Enumerable.Range(0, components)
            .SelectMany(_ => reverse ? new[] { 1d, 0d } : new[] { 0d, 1d }).ToArray();
        const System.Reflection.BindingFlags nested = System.Reflection.BindingFlags.NonPublic;
        var spaceType = typeof(PdfPageRenderer).GetNestedType("ImageColorSpace", nested)!;
        var spaceConstructor = spaceType.GetConstructors().Single();
        object?[] spaceArguments = spaceConstructor.GetParameters()
            .Select(parameter => parameter.HasDefaultValue ? parameter.DefaultValue : null).ToArray();
        spaceArguments[0] = components;
        object space = spaceConstructor.Invoke(spaceArguments);
        var converterType = typeof(PdfPageRenderer).GetNestedType("ImageSampleConverter", nested)!;
        var constructor = converterType.GetConstructors(System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic).Single();
        Func<int, int, uint> Create(bool matte)
        {
            object?[] arguments = constructor.GetParameters()
                .Select(parameter => parameter.HasDefaultValue ? parameter.DefaultValue : null).ToArray();
            object?[] supplied = [samples, width * components * 2, components, 16, decode, space,
                targetInk, null, matte];
            supplied.CopyTo(arguments, 0);
            object converter = constructor.Invoke(arguments);
            return converterType.GetMethod("Convert", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!.CreateDelegate<Func<int, int, uint>>(converter);
        }
        var cached = Create(false);
        var uncached = Create(true);
        for (int pass = 0; pass < 2; pass++)
        for (int index = 0; index < width * height; index++)
        {
            int pixel = pass == 0 ? index : width * height - index - 1;
            Assert.Equal(uncached(pixel % width, pixel / width), cached(pixel % width, pixel / width));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void MaskedDeviceImageDoesNotAllocateAColorPlane(int components)
    {
        const int size = 1024;
        byte[] samples = new byte[size * size * (components + 1)];
        Array.Fill(samples, (byte)128);
        PdfImage image = components == 1 ? PdfImage.FromGrayAlpha(size, size, samples)
            : PdfImage.FromRgba(size, size, samples);
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(size, size,
            new PdfContentStreamBuilder().DrawImage(image, 0, 0, size, size)).Build());
        var renderer = new PdfPageRenderer(document);
        var options = new PdfRenderOptions(size, size);
        byte[] pixels = new byte[size * size * 4];
        Assert.Empty(renderer.RenderInto(0, options, pixels));
        var held = new List<byte[]>();
        try
        {
            for (int index = 0; index < 8; index++)
                held.Add(PdfScratchBuffers.Bytes.Rent(pixels.Length));
            long before = GC.GetAllocatedBytesForCurrentThread();
            var diagnostics = renderer.RenderInto(0, options, pixels);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Empty(diagnostics);
            Assert.True(allocated < 128 * 1024, $"Masked device image allocated {allocated} bytes.");
        }
        finally
        {
            foreach (byte[] buffer in held) PdfScratchBuffers.Bytes.Return(buffer);
        }
    }

    public static TheoryData<int, int, double, bool> Cases
    {
        get
        {
            var data = new TheoryData<int, int, double, bool>();
            foreach (int components in new[] { 1, 3 })
                foreach (int size in new[] { 5, 23 })
                    foreach (double opacity in new[] { 0.5, 1 })
                        foreach (bool rotated in new[] { false, true })
                            data.Add(components, size, opacity, rotated);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DirectSamplesMatchFullConversion(int components, int size, double opacity, bool rotated)
    {
        var options = new PdfRenderOptions(size, size, transparentBackground: true);
        var direct = new PdfPageRenderer(Create(components, false, opacity, rotated)).Render(0, options);
        var converted = new PdfPageRenderer(Create(components, true, opacity, rotated)).Render(0, options);
        Assert.Empty(direct.Diagnostics);
        Assert.Empty(converted.Diagnostics);
        Assert.Equal(converted.Pixels.ToArray(), direct.Pixels.ToArray());
    }

    private static PdfDocument Create(int components, bool wideSamples, double opacity, bool rotated)
    {
        const int size = 17;
        string matrix = rotated ? "0 17 -17 0 17 0" : "17 0 0 17 0 0";
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(size, size,
            Encoding.ASCII.GetBytes($"1 1 15 15 re W n /Opacity gs {matrix} cm /Image Do")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        byte[] samples = new byte[size * size * components * (wideSamples ? 2 : 1)];
        for (int index = 0; index < size * size * components; index++)
        {
            byte sample = (byte)(index * 37 + index / (size * components) * 13);
            if (wideSamples) samples[index * 2] = samples[index * 2 + 1] = sample;
            else samples[index] = sample;
        }
        var alpha = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(size)),
            Entry("Height", new PdfInteger(size)), Entry("ColorSpace", Name("DeviceGray")),
            Entry("BitsPerComponent", new PdfInteger(8))]),
            Enumerable.Range(0, size * size).Select(index => (byte)(index * 53)).ToArray()));
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(size)),
            Entry("Height", new PdfInteger(size)), Entry("ColorSpace", Name(components == 1 ? "DeviceGray" : "DeviceRGB")),
            Entry("BitsPerComponent", new PdfInteger(wideSamples ? 16 : 8)), Entry("SMask", alpha)]), samples));
        var resources = new PdfDictionary([
            Entry("XObject", new PdfDictionary([Entry("Image", image)])),
            Entry("ExtGState", new PdfDictionary([Entry("Opacity", new PdfDictionary([Entry("ca", new PdfReal(opacity))]))]))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page
            .Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources))));
        return PdfDocument.Open(update.Build());
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
