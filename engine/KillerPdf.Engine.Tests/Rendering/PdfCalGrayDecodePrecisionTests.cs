using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfCalGrayDecodePrecisionTests
{
    [Fact]
    public void NaturalReversedEightBitDecodeRetainsLiteralLinearColor()
    {
        var document = Build([0, 127, 254, 255], 8, 4, 4);
        byte[] original = Snapshot(document);
        var frame = new PdfPageRenderer(document).RenderFrame(0, Options(4));
        Pixel(frame, 0, 1, 1e-6);
        Pixel(frame, 1, .2519646289888504, 1e-6);
        Pixel(frame, 2, .000015378700499807766, 1e-10);
        Pixel(frame, 3, 0, 0);
        Assert.Equal(original, Snapshot(document));
    }

    [Fact]
    public void NaturalReversedSixteenBitDecodeKeepsTinyLevelsEndpointsAndSource()
    {
        var document = Build([0, 0, 255, 255, 255, 254, 128, 0, 127, 255], 16, 5, 5);
        byte[] original = Snapshot(document);
        var frame = new PdfPageRenderer(document).RenderFrame(0, Options(5));
        Pixel(frame, 0, 1, 1e-6);
        Pixel(frame, 1, 0, 0);
        Pixel(frame, 2, 2.3283774924386086e-10, 1e-14);
        Pixel(frame, 3, .24999237054726198, 1e-6);
        Pixel(frame, 4, .2500076295691578, 1e-6);
        Assert.True(frame.Pixels[16] > frame.Pixels[12]);
        Assert.Equal(original, Snapshot(document));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void NaturalReversedDecodeRemovesMatteBeforeGammaAndRetainsMaskPrecision(int bits)
    {
        var document = Build(bits == 8 ? [127] : [127, 255], bits,
            maskSamples: bits == 8 ? [128] : [128, 0]);
        byte[] original = Snapshot(document);
        var frame = new PdfPageRenderer(document).RenderFrame(0, Options(1));
        double low = bits == 8 ? .000061034 : 9.31312e-10;
        double high = bits == 8 ? .000061037 : 9.31333e-10;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange((double)frame.Pixels[channel], low, high);
        Assert.InRange((double)frame.Pixels[3], bits == 8 ? .50196077 : .50000760,
            bits == 8 ? .50196082 : .50000766);
        Assert.Equal(original, Snapshot(document));
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(8, true)]
    [InlineData(16, false)]
    [InlineData(16, true)]
    public void EnlargedReversedDecodeRemainsDiagnosed(int bits, bool interpolate)
    {
        var document = Build(bits == 8 ? [127] : [128, 0], bits, outputWidth: 2,
            interpolate: interpolate);
        byte[] original = Snapshot(document);
        Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(document).RenderFrame(0, Options(2)));
        Assert.Equal(original, Snapshot(document));
    }

    private static byte[] Snapshot(PdfDocument document)
    {
        var root = Assert.IsType<PdfIndirectReference>(document.Trailer[Name("Root")]);
        return new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(root.ObjectNumber, document.Resolve(root)).Build();
    }

    private static PdfDocument Build(byte[] samples, int bits, int width = 1, int outputWidth = 1,
        bool interpolate = false, byte[]? maskSamples = null)
    {
        var image = PdfImage.FromCalibratedGray(width, 1, new byte[width],
            new PdfCalGrayColorSpace(whiteX: .95047, whiteZ: 1.08883, gamma: 2));
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(outputWidth, 1,
            new PdfContentStreamBuilder().DrawImage(image, 0, 0, outputWidth, 1)).Build());
        var catalog = Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(document.Trailer[Name("Root")])));
        var pages = Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(catalog[Name("Pages")])));
        var page = Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(Assert.Single(Assert.IsType<PdfArray>(pages[Name("Kids")])))));
        var resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var objects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        var reference = Assert.IsType<PdfIndirectReference>(Assert.Single(objects).Value);
        var stream = Assert.IsType<PdfStream>(document.Resolve(reference));
        var update = new PdfIncrementalUpdateBuilder(document);
        var entries = stream.Dictionary.Where(entry =>
            !entry.Key.Equals(Name("Filter")) && !entry.Key.Equals(Name("DecodeParms"))
            && !entry.Key.Equals(Name("BitsPerComponent")))
            .Append(Entry("BitsPerComponent", new PdfInteger(bits)))
            .Append(Entry("Interpolate", new PdfBoolean(interpolate)))
            .Append(Entry("Decode", new PdfArray([new PdfInteger(1), new PdfInteger(0)]))).ToList();
        if (maskSamples is not null)
            entries.Add(Entry("SMask", update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Type", Name("XObject")), Entry("Subtype", Name("Image")),
                Entry("Width", new PdfInteger(width)), Entry("Height", new PdfInteger(1)),
                Entry("BitsPerComponent", new PdfInteger(bits)), Entry("ColorSpace", Name("DeviceGray")),
                Entry("Matte", new PdfArray([new PdfInteger(1)]))]), maskSamples))));
        return PdfDocument.Open(update.ReplaceObject(reference.ObjectNumber,
            new PdfStream(new PdfDictionary(entries), samples)).Build());
    }

    private static void Pixel(PdfRenderedFrame frame, int index, double expected, double tolerance)
    {
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange((double)frame.Pixels[index * 4 + channel], expected - tolerance, expected + tolerance);
        Assert.Equal(1f, frame.Pixels[index * 4 + 3]);
    }

    private static PdfRenderOptions Options(int width) => new(width, 1, transparentBackground: true);
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
}
