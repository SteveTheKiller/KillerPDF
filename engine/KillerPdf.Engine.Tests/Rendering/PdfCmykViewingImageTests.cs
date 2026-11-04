using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfCmykViewingImageTests
{
    [Fact]
    public void NaturalUntaggedCmykImageUsesTheUnclippedViewingAssumption()
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes("/Image Do")).Build());
        PdfIndirectReference page = FirstPageReference(source);
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page));
        var update = new PdfIncrementalUpdateBuilder(source);
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(1)),
            Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(8)),
            Entry("ColorSpace", Name("DeviceCMYK"))]), [255, 0, 0, 0]));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Image", image)]))]);
        var document = PdfDocument.Open(update.ReplaceObject(page.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());

        PdfRenderedFrame frame = new PdfPageRenderer(document).RenderFrame(0, new PdfRenderOptions(1, 1));

        // Same frozen Decimal60 profile-corner oracle as the CMYK vector fixture.
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.InRange(frame.Pixels[0], -0.20839846805136676f - 0.000002f, -0.20839846805136676f + 0.000002f);
        Assert.InRange(frame.Pixels[1], 0.42131251226180745f - 0.000002f, 0.42131251226180745f + 0.000002f);
        Assert.InRange(frame.Pixels[2], 0.86494485126038379f - 0.000002f, 0.86494485126038379f + 0.000002f);
        Assert.Equal(1, frame.Pixels[3]);
    }

    [Fact]
    public void AdjacentSixteenBitCmykImageSamplesRemainDistinct()
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            Encoding.ASCII.GetBytes("2 0 0 1 0 0 cm /Image Do")).Build());
        PdfIndirectReference page = FirstPageReference(source);
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page));
        var update = new PdfIncrementalUpdateBuilder(source);
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)),
            Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(16)),
            Entry("ColorSpace", Name("DeviceCMYK"))]),
            [255, 254, 0, 0, 0, 0, 0, 0, 255, 255, 0, 0, 0, 0, 0, 0]));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Image", image)]))]);
        var document = PdfDocument.Open(update.ReplaceObject(page.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());

        PdfRenderedFrame frame = new PdfPageRenderer(document).RenderFrame(0, new PdfRenderOptions(2, 1));

        // Independent Decimal60 input-curve and one-axis CLUT evaluation for cyan65534/65535.
        Assert.InRange(frame.Pixels[0], -0.20839294970949245f - 0.000002f, -0.20839294970949245f + 0.000002f);
        Assert.InRange(frame.Pixels[1], 0.42131641797532583f - 0.000002f, 0.42131641797532583f + 0.000002f);
        Assert.InRange(frame.Pixels[2], 0.86494605952974034f - 0.000002f, 0.86494605952974034f + 0.000002f);
        Assert.InRange(frame.Pixels[4], -0.20839846805136676f - 0.000002f, -0.20839846805136676f + 0.000002f);
        Assert.InRange(frame.Pixels[5], 0.42131251226180745f - 0.000002f, 0.42131251226180745f + 0.000002f);
        Assert.InRange(frame.Pixels[6], 0.86494485126038379f - 0.000002f, 0.86494485126038379f + 0.000002f);
        Assert.Equal(1, frame.Pixels[3]);
        Assert.Equal(1, frame.Pixels[7]);
        Assert.True(frame.Pixels[0] > frame.Pixels[4]);
        byte[] quantized = frame.ToSrgbBgra32();
        Assert.Equal(quantized[..4], quantized[4..]);
    }

    [Fact]
    public void NearestEnlargedUntaggedCmykImagePreservesBothSourceColors()
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(4, 1,
            Encoding.ASCII.GetBytes("4 0 0 1 0 0 cm /Image Do")).Build());
        PdfIndirectReference page = FirstPageReference(source);
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page));
        var update = new PdfIncrementalUpdateBuilder(source);
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)),
            Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(8)),
            Entry("ColorSpace", Name("DeviceCMYK"))]), [255, 0, 0, 0, 0, 0, 0, 0]));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Image", image)]))]);
        var document = PdfDocument.Open(update.ReplaceObject(page.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());

        PdfRenderedFrame frame = new PdfPageRenderer(document).RenderFrame(0, new PdfRenderOptions(4, 1));

        float[] cyan = [-0.20839846805136676f, 0.42131251226180745f, 0.86494485126038379f];
        for (int pixel = 0; pixel < 4; pixel++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                float expected = pixel < 2 ? cyan[channel] : 1f;
                Assert.InRange(frame.Pixels[pixel * 4 + channel], expected - 0.000002f, expected + 0.000002f);
            }
            Assert.Equal(1, frame.Pixels[pixel * 4 + 3]);
        }
    }

    [Theory]
    [InlineData(8, 0.5019608f, 0.5058824f)]
    [InlineData(16, 0.50000763f, 0.5000229f)]
    public void MatchingSoftMaskRetainsAlphaAndUnclippedCmykColor(int maskBits, float firstAlpha, float secondAlpha)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            Encoding.ASCII.GetBytes("2 0 0 1 0 0 cm /Image Do")).Build());
        PdfIndirectReference page = FirstPageReference(source);
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page));
        var update = new PdfIncrementalUpdateBuilder(source);
        var mask = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)),
            Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(maskBits)),
            Entry("ColorSpace", Name("DeviceGray"))]), maskBits == 8 ? [128, 129] : [128, 0, 128, 1]));
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)),
            Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(8)),
            Entry("ColorSpace", Name("DeviceCMYK")), Entry("SMask", mask)]), [255, 0, 0, 0, 255, 0, 0, 0]));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Image", image)]))]);
        var document = PdfDocument.Open(update.ReplaceObject(page.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());

        PdfRenderedFrame frame = new PdfPageRenderer(document)
            .RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        for (int pixel = 0; pixel < 2; pixel++)
        {
            Assert.InRange(frame.Pixels[pixel * 4], -0.20839846805136676f - 0.000002f, -0.20839846805136676f + 0.000002f);
            Assert.InRange(frame.Pixels[pixel * 4 + 1], 0.42131251226180745f - 0.000002f, 0.42131251226180745f + 0.000002f);
            Assert.InRange(frame.Pixels[pixel * 4 + 2], 0.86494485126038379f - 0.000002f, 0.86494485126038379f + 0.000002f);
        }
        Assert.Equal(firstAlpha, frame.Pixels[3]);
        Assert.Equal(secondAlpha, frame.Pixels[7]);
        Assert.NotEqual(frame.Pixels[3], frame.Pixels[7]);
    }

    private static PdfIndirectReference FirstPageReference(PdfDocument document)
    {
        var catalog = Assert.IsType<PdfDictionary>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(document.Trailer[Name("Root")])));
        var pages = Assert.IsType<PdfDictionary>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[Name("Pages")])));
        var kids = Assert.IsType<PdfArray>(pages[Name("Kids")]);
        return Assert.IsType<PdfIndirectReference>(Assert.Single(kids));
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
