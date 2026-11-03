using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfHighPrecisionRenderingTests
{
    [Fact]
    public void SixteenBitImageSamplesRemainDistinctInTheRenderedFrame()
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            Encoding.ASCII.GetBytes("2 0 0 1 0 0 cm /Image Do")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)),
            Entry("Height", new PdfInteger(1)), Entry("ColorSpace", Name("DeviceRGB")),
            Entry("BitsPerComponent", new PdfInteger(16))]),
            [128, 0, 128, 0, 128, 0, 128, 1, 128, 1, 128, 1]));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Image", image)]))]);
        var document = PdfDocument.Open(update.ReplaceObject(reference.ObjectNumber,
            new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());

        var frame = new PdfPageRenderer(document).RenderFrame(0, new PdfRenderOptions(2, 1));

        Assert.InRange(frame.Pixels[0], 0.21404818f, 0.21404823f);
        Assert.InRange(frame.Pixels[4], 0.21406230f, 0.21406235f);
        Assert.True(frame.Pixels[4] > frame.Pixels[0]);
        Assert.Equal(new byte[] { 128, 128, 128, 255, 128, 128, 128, 255 }, frame.ToSrgbBgra32());
    }

    [Theory]
    [InlineData(false, 0.5, 0.21404114f, 1f)]
    [InlineData(true, 0.0009765625, 0f, 0.0009765625f)]
    public void OpacityPreservesPdfEncodedBlendingAndSmallStraightAlpha(
        bool transparent, double opacity, float expectedColor, float expectedAlpha)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes("0 g /Opacity gs 0 0 1 1 re f")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var resources = new PdfDictionary([Entry("ExtGState", new PdfDictionary([
            Entry("Opacity", new PdfDictionary([Entry("ca", new PdfReal(opacity))]))]))]);
        var document = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(reference.ObjectNumber, new PdfDictionary(page
                .Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources))))
            .Build());

        var frame = new PdfPageRenderer(document).RenderFrame(0,
            new PdfRenderOptions(1, 1, transparentBackground: transparent));

        Assert.InRange(frame.Pixels[0], expectedColor - 0.00000002f, expectedColor + 0.00000002f);
        Assert.Equal(expectedAlpha, frame.Pixels[3]);
    }

    [Fact]
    public void DeviceRgbPathColorsRemainDistinctBeforeCompatibilityQuantization()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(2, 1, Encoding.ASCII.GetBytes(
                "0.5 0.5 0.5 rg 0 0 1 1 re f 0.5001 0.5001 0.5001 rg 1 0 1 1 re f"))
            .Build()));

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 1));

        // Independently evaluated sRGB decoding with 60-digit decimal arithmetic.
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.InRange(frame.Pixels[0], 0.21404113f, 0.21404116f);
        Assert.InRange(frame.Pixels[4], 0.21413370f, 0.21413373f);
        Assert.True(frame.Pixels[4] > frame.Pixels[0]);
        Assert.Equal(1f, frame.Pixels[3]);
        Assert.Equal(1f, frame.Pixels[7]);
    }

    [Fact]
    public void CalibratedGrayRetainsLinearLightSourcePrecision()
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            Encoding.ASCII.GetBytes("/Cal cs 0.5 sc 0 0 1 1 re f 0.5001 sc 1 0 1 1 re f")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var calibrated = new PdfArray([Name("CalGray"), new PdfDictionary([
            Entry("WhitePoint", new PdfArray([new PdfReal(0.95047), new PdfInteger(1), new PdfReal(1.08883)]))])]);
        var resources = new PdfDictionary([Entry("ColorSpace", new PdfDictionary([Entry("Cal", calibrated)]))]);
        var document = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(reference.ObjectNumber, new PdfDictionary(page
                .Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources))))
            .Build());

        var frame = new PdfPageRenderer(document).RenderFrame(0, new PdfRenderOptions(2, 1));

        // Gamma-one CalGray with a D65 white describes linear neutral luminance.
        Assert.InRange(frame.Pixels[0], 0.499998f, 0.500002f);
        Assert.InRange(frame.Pixels[4], 0.500098f, 0.500102f);
        Assert.True(frame.Pixels[4] > frame.Pixels[0]);
        Assert.Equal(frame.ToSrgbBgra32()[0], frame.ToSrgbBgra32()[4]);
    }

    [Fact]
    public void DeviceRgbStrokesRetainSubByteColorDifferences()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(2, 1, Encoding.ASCII.GetBytes(
                "0.5 0.5 0.5 RG 0.5 0 m 0.5 1 l S "
                + "0.5001 0.5001 0.5001 RG 1.5 0 m 1.5 1 l S"))
            .Build()));

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 1));

        // Independently evaluated sRGB decoding with 60-digit decimal arithmetic.
        Assert.InRange(frame.Pixels[0], 0.21404113f, 0.21404116f);
        Assert.InRange(frame.Pixels[4], 0.21413370f, 0.21413373f);
        Assert.True(frame.Pixels[4] > frame.Pixels[0]);
        Assert.Equal(1f, frame.Pixels[3]);
        Assert.Equal(1f, frame.Pixels[7]);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
