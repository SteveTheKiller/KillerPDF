using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfCalGrayMattePrecisionTests
{
    [Fact]
    public void NaturalEightBitMatteIsRemovedBeforeGammaWithoutLosingTinyLinearColor()
    {
        var document = Build([128], [128], 8);
        var frame = new PdfPageRenderer(document).RenderFrame(0, new PdfRenderOptions(1, 1, transparentBackground: true));
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange((double)frame.Pixels[channel], .000061034, .000061037);
        Assert.InRange((double)frame.Pixels[3], .50196077, .50196082);
    }

    [Fact]
    public void NaturalSixteenBitMatteRetainsSourceAndMaskPrecisionBeforeGamma()
    {
        var document = Build([128, 0], [128, 0], 16);
        var frame = new PdfPageRenderer(document).RenderFrame(0, new PdfRenderOptions(1, 1, transparentBackground: true));
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange((double)frame.Pixels[channel], 9.31312e-10, 9.31333e-10);
        Assert.InRange((double)frame.Pixels[3], .50000760, .50000766);
    }

    [Theory]
    [InlineData(0, 255, 0, 1)]
    [InlineData(255, 255, 1, 1)]
    [InlineData(255, 0, 0, 0)]
    public void NaturalMatteKeepsGammaEndpointsTransparencyAndSourceBytes(byte sample, byte alpha, float color, float expectedAlpha)
    {
        var document = Build([sample], [alpha], 8);
        byte[] original = Snapshot(document);
        var frame = new PdfPageRenderer(document).RenderFrame(0, new PdfRenderOptions(1, 1, transparentBackground: true));
        for (int channel = 0; channel < 3; channel++)
        {
            Assert.True(float.IsFinite(frame.Pixels[channel]));
            if (expectedAlpha != 0)
                Assert.InRange(frame.Pixels[channel], color - .000001f, color + .000001f);
        }
        Assert.Equal(expectedAlpha, frame.Pixels[3]);
        Assert.Equal(original, Snapshot(document));
    }

    private static byte[] Snapshot(PdfDocument document)
    {
        var root = Assert.IsType<PdfIndirectReference>(document.Trailer[Name("Root")]);
        return new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(root.ObjectNumber, document.Resolve(root)).Build();
    }

    private static PdfDocument Build(byte[] samples, byte[] maskSamples, int bits)
    {
        var image = PdfImage.FromCalibratedGray(1, 1, new byte[] { 0 },
            new PdfCalGrayColorSpace(whiteX: .95047, whiteZ: 1.08883, gamma: 2));
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            new PdfContentStreamBuilder().DrawImage(image, 0, 0, 1, 1)).Build());
        var catalog = Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(document.Trailer[Name("Root")])));
        var pages = Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(catalog[Name("Pages")])));
        var page = Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(Assert.Single(Assert.IsType<PdfArray>(pages[Name("Kids")])))));
        var resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var objects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        var reference = Assert.IsType<PdfIndirectReference>(Assert.Single(objects).Value);
        var stream = Assert.IsType<PdfStream>(document.Resolve(reference));
        var update = new PdfIncrementalUpdateBuilder(document);
        var mask = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Type", Name("XObject")), Entry("Subtype", Name("Image")),
            Entry("Width", new PdfInteger(1)), Entry("Height", new PdfInteger(1)),
            Entry("BitsPerComponent", new PdfInteger(bits)), Entry("ColorSpace", Name("DeviceGray")),
            Entry("Matte", new PdfArray([new PdfInteger(1)]))]), maskSamples));
        var replacement = new PdfStream(new PdfDictionary(stream.Dictionary.Where(entry =>
            !entry.Key.Equals(Name("Filter")) && !entry.Key.Equals(Name("DecodeParms"))
            && !entry.Key.Equals(Name("BitsPerComponent")))
            .Append(Entry("BitsPerComponent", new PdfInteger(bits))).Append(Entry("SMask", mask))), samples);
        return PdfDocument.Open(update.ReplaceObject(reference.ObjectNumber, replacement).Build());
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
}
