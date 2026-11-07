using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfCalGrayPrecisionImageTests
{
    [Theory]
    [InlineData(127, 1, 0.49803922)]
    [InlineData(127, 1.8, 0.28515065)]
    [InlineData(127, 2.2, 0.2157644)]
    [InlineData(127, 2.8, 0.1420162)]
    [InlineData(1, 2, 0.000015378700499807766)]
    public void DeclaredGammaRetainsLiteralLinearLevels(byte sample, double gamma, double expected)
    {
        var document = Build(1, 1, [sample], Space(gamma));
        var frame = new PdfPageRenderer(document).RenderFrame(0, Options(1, 1));
        Pixel(frame, 0, expected);
    }

    [Fact]
    public void D50CalibrationKeepsItsMetadataAndAdaptsToNeutralLinearRgb()
    {
        var document = Build(1, 1, [127], new PdfCalGrayColorSpace(gamma: 2.2));
        var colorSpace = Assert.IsType<PdfArray>(Image(document).Stream.Dictionary[Name("ColorSpace")]);
        var parameters = Assert.IsType<PdfDictionary>(colorSpace[1]);
        Assert.Equal(new[] { .9642, 1, .8249 }, Assert.IsType<PdfArray>(parameters[Name("WhitePoint")]).Select(Number));
        Pixel(new PdfPageRenderer(document).RenderFrame(0, Options(1, 1)), 0, .2157644);
    }

    [Fact]
    public void OmittedBlackPointAndDirectNamedAliasRetainTheSupportedCalibration()
    {
        var document = Build(1, 1, [127], Space(2.2));
        document = ModifyImage(document, stream =>
        {
            var space = Assert.IsType<PdfArray>(stream.Dictionary[Name("ColorSpace")]);
            var parameters = Assert.IsType<PdfDictionary>(space[1]);
            return With(stream, "ColorSpace", new PdfArray([space[0],
                new PdfDictionary(parameters.Where(entry => !entry.Key.Equals(Name("BlackPoint"))))]));
        });
        Pixel(new PdfPageRenderer(document).RenderFrame(0, Options(1, 1)), 0, .2157644);
        document = NamedSpace(document, useDefault: false);
        Pixel(new PdfPageRenderer(document).RenderFrame(0, Options(1, 1)), 0, .2157644);
    }

    [Fact]
    public void TwoDimensionalSamplesAndHorizontalReflectionRetainLiteralOrder()
    {
        byte[] samples = [0, 127, 255, 30];
        var normal = Build(2, 2, samples, Space(2.2));
        var reflected = Build(2, 2, samples, Space(2.2), (content, image) => content.DrawImage(image, 2, 0, -2, 2));
        var first = new PdfPageRenderer(normal).RenderFrame(0, Options(2, 2));
        var second = new PdfPageRenderer(reflected).RenderFrame(0, Options(2, 2));
        double[] expected = [0, .2157644, 1, .0090214919];
        double[] flipped = [.2157644, 0, .0090214919, 1];
        for (int index = 0; index < 4; index++)
        {
            Pixel(first, index, expected[index]);
            Pixel(second, index, flipped[index]);
        }
    }

    [Fact]
    public void IntegerTranslationAndRectangularClipKeepSamplingAndTransparentNeighbors()
    {
        var image = PdfImage.FromCalibratedGray(2, 2, new byte[] { 0, 127, 255, 30 }, Space(2.2));
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(4, 3,
            new PdfContentStreamBuilder().Rectangle(1, 0, 1, 2).Clip().DrawImage(image, 1, 0, 2, 2)).Build());
        var frame = new PdfPageRenderer(document).RenderFrame(0, Options(4, 3));
        Pixel(frame, 5, 0);
        Pixel(frame, 9, 1);
        for (int index = 0; index < 12; index++)
            if (index is not (5 or 9)) Assert.Equal(0f, frame.Pixels[index * 4 + 3]);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    public void NondefaultDecodeIsDiagnosedWithoutReturningANonfiniteFrame(int first, int second)
    {
        var document = ModifyImage(Build(1, 1, [127], Space(2.2)),
            stream => With(stream, "Decode", new PdfArray([new PdfInteger(first), new PdfInteger(second)])));
        Assert.Empty(new PdfPageRenderer(document).Render(0, new PdfRenderOptions(1, 1)).Diagnostics);
        Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(document).RenderFrame(0, Options(1, 1)));
    }

    [Fact]
    public void NonzeroBlackPointAndNonrelativeImageIntentRemainDiagnosed()
    {
        var nonzero = Build(1, 1, [127], new PdfCalGrayColorSpace(whiteX: .95047, whiteZ: 1.08883,
            blackX: .01, blackY: .01, blackZ: .01, gamma: 2.2));
        var intent = ModifyImage(Build(1, 1, [127], Space(2.2)), stream => With(stream, "Intent", Name("Perceptual")));
        foreach (var document in new[] { nonzero, intent })
        {
            Assert.Empty(new PdfPageRenderer(document).Render(0, new PdfRenderOptions(1, 1)).Diagnostics);
            Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(document).RenderFrame(0, Options(1, 1)));
        }
    }

    [Fact]
    public void CalibratedDefaultGrayRemainsOutsideTheInitialDirectImageSubset()
    {
        var document = NamedSpace(Build(1, 1, [127], Space(2.2)), useDefault: true);
        Assert.Empty(new PdfPageRenderer(document).Render(0, new PdfRenderOptions(1, 1)).Diagnostics);
        Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(document).RenderFrame(0, Options(1, 1)));
    }

    [Theory]
    [InlineData("reduction")]
    [InlineData("one-axis")]
    [InlineData("quarter-turn")]
    [InlineData("half-pixel")]
    public void UnsupportedGeometryRemainsDiagnosed(string geometry)
    {
        PdfDocument document;
        int width = 1, height = 1;
        if (geometry == "reduction")
        {
            var image = PdfImage.FromCalibratedGray(2, 2, new byte[] { 0, 127, 255, 30 }, Space(2.2));
            document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
                new PdfContentStreamBuilder().DrawImage(image, 0, 0, 1, 1)).Build());
        }
        else if (geometry == "one-axis")
        {
            var image = PdfImage.FromCalibratedGray(2, 1, new byte[] { 0, 127 }, Space(2.2));
            document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
                new PdfContentStreamBuilder().DrawImage(image, 0, 0, 1, 1)).Build());
        }
        else if (geometry == "quarter-turn")
        {
            height = 2;
            var image = PdfImage.FromCalibratedGray(2, 1, new byte[] { 0, 127 }, Space(2.2));
            document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 2,
                new PdfContentStreamBuilder().Transform(0, 1, -1, 0, 1, 0).DrawImage(image, 0, 0, 2, 1)).Build());
        }
        else
        {
            width = 2;
            var image = PdfImage.FromCalibratedGray(1, 1, new byte[] { 127 }, Space(2.2));
            document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
                new PdfContentStreamBuilder().DrawImage(image, .5, 0, 1, 1)).Build());
        }
        Assert.Empty(new PdfPageRenderer(document).Render(0, new PdfRenderOptions(width, height)).Diagnostics);
        Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(document).RenderFrame(0, Options(width, height)));
    }

    [Theory]
    [InlineData(false, 8)]
    [InlineData(true, 8)]
    [InlineData(false, 16)]
    [InlineData(true, 16)]
    public void CalGrayEnlargementSamplesBeforeGammaWithoutByteQuantization(bool interpolate, int bits)
    {
        var image = PdfImage.FromCalibratedGray(2, 1, new byte[] { 0, 255 }, Space(2));
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(4, 2,
            new PdfContentStreamBuilder().DrawImage(image, 0, 0, 4, 2)).Build());
        if (bits == 16)
            document = ModifyImage(document, stream => new PdfStream(
                new PdfDictionary(stream.Dictionary.Where(entry => !entry.Key.Equals(Name("Filter"))
                    && !entry.Key.Equals(Name("DecodeParms")) && !entry.Key.Equals(Name("BitsPerComponent")))
                    .Append(Entry("BitsPerComponent", new PdfInteger(16)))), new byte[] { 0, 0, 255, 255 }));
        document = ModifyImage(document, stream => With(stream, "Interpolate", new PdfBoolean(interpolate)));
        var frame = new PdfPageRenderer(document).RenderFrame(0, Options(4, 2));
        double[] expected = interpolate ? [0, .0625, .5625, 1] : [0, 0, 1, 1];
        for (int row = 0; row < 2; row++)
            for (int column = 0; column < 4; column++) Pixel(frame, row * 4 + column, expected[column]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CalGrayEnlargementRetainsRawMasksWithNearestSamplingAndDiagnosesMaskedBilinear(bool softMask, bool interpolate)
    {
        var sourceImage = PdfImage.FromCalibratedGray(2, 1, new byte[] { 0, 0 }, Space(2));
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(4, 2,
            new PdfContentStreamBuilder().DrawImage(sourceImage, 0, 0, 4, 2)).Build());
        document = ModifyImage(document, stream => new PdfStream(
            new PdfDictionary(stream.Dictionary.Where(entry => !entry.Key.Equals(Name("Filter"))
                && !entry.Key.Equals(Name("DecodeParms")) && !entry.Key.Equals(Name("BitsPerComponent")))
                .Append(Entry("BitsPerComponent", new PdfInteger(16)))
                .Append(Entry("Interpolate", new PdfBoolean(interpolate)))), new byte[] { 128, 0, 128, 1 }));
        var (reference, imageStream) = Image(document);
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfStream masked;
        if (softMask)
        {
            var mask = update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Type", Name("XObject")), Entry("Subtype", Name("Image")),
                Entry("Width", new PdfInteger(2)), Entry("Height", new PdfInteger(1)),
                Entry("BitsPerComponent", new PdfInteger(16)), Entry("ColorSpace", Name("DeviceGray"))]),
                new byte[] { 0, 0, 0, 1 }));
            masked = With(imageStream, "SMask", mask);
        }
        else masked = With(imageStream, "Mask", new PdfArray([new PdfInteger(32768), new PdfInteger(32768)]));
        document = PdfDocument.Open(update.ReplaceObject(reference.ObjectNumber, masked).Build());
        var options = Options(4, 2);
        if (interpolate)
        {
            Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(document).RenderFrame(0, options));
            return;
        }
        var frame = new PdfPageRenderer(document).RenderFrame(0, options);
        for (int row = 0; row < 2; row++)
            for (int column = 0; column < 4; column++)
            {
                int offset = (row * 4 + column) * 4;
                double alpha = column < 2 ? 0 : softMask ? .000015259021896696422 : 1;
                Assert.InRange((double)frame.Pixels[offset + 3], alpha - 1e-9, alpha + 1e-9);
                if (column < 2) continue;
                for (int component = 0; component < 3; component++)
                    Assert.InRange((double)frame.Pixels[offset + component], .2500218, .2500239);
            }
    }

    [Fact]
    public void CalGrayNaturalSizeKeepsItsExistingFloatingPointAlignmentTolerance()
    {
        var image = PdfImage.FromCalibratedGray(2, 1, new byte[] { 0, 255 }, Space(2));
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            new PdfContentStreamBuilder().DrawImage(image, 0, 0, 2 - 1e-10, 1)).Build());
        var frame = new PdfPageRenderer(document).RenderFrame(0, Options(2, 1));
        Pixel(frame, 0, 0);
        Pixel(frame, 1, 1);
    }

    [Fact]
    public void CalGrayEnlargedSoftMaskMatteRemainsDiagnosed()
    {
        var image = PdfImage.FromCalibratedGray(1, 1, new byte[] { 127 }, Space(2.2));
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 2,
            new PdfContentStreamBuilder().DrawImage(image, 0, 0, 2, 2)).Build());
        var (reference, stream) = Image(document);
        var update = new PdfIncrementalUpdateBuilder(document);
        var entries = new List<KeyValuePair<PdfName, PdfObject>>
        {
            Entry("Type", Name("XObject")), Entry("Subtype", Name("Image")),
            Entry("Width", new PdfInteger(1)), Entry("Height", new PdfInteger(1)),
            Entry("BitsPerComponent", new PdfInteger(8)), Entry("ColorSpace", Name("DeviceGray")),
            Entry("Matte", new PdfArray([new PdfReal(.5)]))
        };
        var mask = update.AddObject(new PdfStream(new PdfDictionary(entries), new byte[] { 128 }));
        document = PdfDocument.Open(update.ReplaceObject(reference.ObjectNumber, With(stream, "SMask", mask)).Build());
        Assert.Empty(new PdfPageRenderer(document).Render(0, new PdfRenderOptions(2, 2)).Diagnostics);
        Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(document).RenderFrame(0, Options(2, 2)));
    }

    [Fact]
    public void AdjacentSixteenBitCalGraySamplesRetainDistinctLinearLevels()
    {
        var document = ModifyImage(Build(2, 1, [127, 127], Space(2)), stream => new PdfStream(
            new PdfDictionary(stream.Dictionary.Where(entry => !entry.Key.Equals(Name("Filter"))
                && !entry.Key.Equals(Name("DecodeParms")) && !entry.Key.Equals(Name("BitsPerComponent")))
                .Append(Entry("BitsPerComponent", new PdfInteger(16)))), new byte[] { 128, 0, 128, 1 }));
        var frame = new PdfPageRenderer(document).RenderFrame(0, Options(2, 1));
        Pixel(frame, 0, .2500076295691578);
        Pixel(frame, 1, .25002288905673);
        Assert.True(frame.Pixels[4] > frame.Pixels[0]);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void CalGrayColorKeyUsesRawSamplesBeforeCalibration(int bits)
    {
        var document = Build(3, 1, [0, 127, 255], Space(2));
        document = ModifyImage(document, stream => new PdfStream(
            new PdfDictionary(stream.Dictionary.Where(entry => !entry.Key.Equals(Name("Filter"))
                && !entry.Key.Equals(Name("DecodeParms")) && !entry.Key.Equals(Name("BitsPerComponent")))
                .Append(Entry("BitsPerComponent", new PdfInteger(bits)))
                .Append(Entry("Mask", new PdfArray([new PdfInteger(127), new PdfInteger(127)])))),
            bits == 8 ? new byte[] { 0, 127, 255 } : new byte[] { 0, 0, 0, 127, 255, 255 }));
        var frame = new PdfPageRenderer(document).RenderFrame(0, Options(3, 1));
        Pixel(frame, 0, 0);
        Pixel(frame, 2, 1);
        Assert.Equal(0f, frame.Pixels[7]);
    }

    [Theory]
    [InlineData(8, .00392156862745098)]
    [InlineData(16, .000015259021896696422)]
    public void CalGraySoftMaskRetainsFractionalAlphaWithoutQuantizingColor(int maskBits, double alpha)
    {
        var document = ModifyImage(Build(1, 1, [127], Space(2)), stream => new PdfStream(
            new PdfDictionary(stream.Dictionary.Where(entry => !entry.Key.Equals(Name("Filter"))
                && !entry.Key.Equals(Name("DecodeParms")) && !entry.Key.Equals(Name("BitsPerComponent")))
                .Append(Entry("BitsPerComponent", new PdfInteger(16)))), new byte[] { 128, 0 }));
        var (reference, image) = Image(document);
        var update = new PdfIncrementalUpdateBuilder(document);
        var mask = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Type", Name("XObject")), Entry("Subtype", Name("Image")),
            Entry("Width", new PdfInteger(1)), Entry("Height", new PdfInteger(1)),
            Entry("BitsPerComponent", new PdfInteger(maskBits)), Entry("ColorSpace", Name("DeviceGray"))]),
            maskBits == 8 ? new byte[] { 1 } : new byte[] { 0, 1 }));
        document = PdfDocument.Open(update.ReplaceObject(reference.ObjectNumber, With(image, "SMask", mask)).Build());
        var frame = new PdfPageRenderer(document).RenderFrame(0, Options(1, 1));
        for (int component = 0; component < 3; component++)
            Assert.InRange((double)frame.Pixels[component], .2500066, .2500087);
        Assert.InRange((double)frame.Pixels[3], alpha - 1e-9, alpha + 1e-9);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SixteenBitD50RowsKeepEndpointsTinyLevelsAndHorizontalReflection(bool reflected)
    {
        var document = Build(2, 2, [0, 0, 0, 0], new PdfCalGrayColorSpace(gamma: 2),
            (content, image) => content.DrawImage(image, reflected ? 2 : 0, 0, reflected ? -2 : 2, 2));
        document = ModifyImage(document, stream => new PdfStream(
            new PdfDictionary(stream.Dictionary.Where(entry => !entry.Key.Equals(Name("Filter"))
                && !entry.Key.Equals(Name("DecodeParms")) && !entry.Key.Equals(Name("BitsPerComponent")))
                .Append(Entry("BitsPerComponent", new PdfInteger(16)))),
            new byte[] { 0, 0, 255, 255, 0, 1, 128, 0 }));
        var frame = new PdfPageRenderer(document).RenderFrame(0, Options(2, 2));
        Pixel(frame, reflected ? 1 : 0, 0);
        Pixel(frame, reflected ? 0 : 1, 1);
        Pixel(frame, reflected ? 2 : 3, .2500076295691578);
        int tiny = (reflected ? 3 : 2) * 4;
        for (int component = 0; component < 3; component++)
            Assert.InRange((double)frame.Pixels[tiny + component], 2.3283e-10, 2.3285e-10);
        Assert.Equal(1f, frame.Pixels[tiny + 3]);
    }

    private static PdfDocument Build(int width, int height, byte[] samples, PdfCalGrayColorSpace space,
        Action<PdfContentStreamBuilder, PdfImage>? paint = null)
    {
        var image = PdfImage.FromCalibratedGray(width, height, samples, space);
        var content = new PdfContentStreamBuilder();
        if (paint is null) content.DrawImage(image, 0, 0, width, height);
        else paint(content, image);
        return PdfDocument.Open(new PdfDocumentBuilder().AddPage(width, height, content).Build());
    }

    private static PdfDocument ModifyImage(PdfDocument document, Func<PdfStream, PdfStream> modify)
    {
        var (reference, stream) = Image(document);
        return PdfDocument.Open(new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(reference.ObjectNumber, modify(stream)).Build());
    }

    private static PdfDocument NamedSpace(PdfDocument document, bool useDefault)
    {
        var (pageReference, page) = Page(document);
        var resources = Assert.IsType<PdfDictionary>(Resolve(document, page[Name("Resources")]));
        var (imageReference, image) = Image(document);
        var spaces = new PdfDictionary([Entry(useDefault ? "DefaultGray" : "Alias", image.Dictionary[Name("ColorSpace")])]);
        var updatedResources = new PdfDictionary(resources.Where(entry => !entry.Key.Equals(Name("ColorSpace")))
            .Append(Entry("ColorSpace", spaces)));
        var updatedPage = new PdfDictionary(page.Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", updatedResources)));
        return PdfDocument.Open(new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(pageReference.ObjectNumber, updatedPage)
            .ReplaceObject(imageReference.ObjectNumber, With(image, "ColorSpace", Name(useDefault ? "DeviceGray" : "Alias"))).Build());
    }

    private static (PdfIndirectReference Reference, PdfDictionary Dictionary) Page(PdfDocument document)
    {
        var catalog = Assert.IsType<PdfDictionary>(Resolve(document, document.Trailer[Name("Root")]));
        var pages = Assert.IsType<PdfDictionary>(Resolve(document, catalog[Name("Pages")]));
        var reference = Assert.IsType<PdfIndirectReference>(Assert.Single(Assert.IsType<PdfArray>(Resolve(document, pages[Name("Kids")]))));
        return (reference, Assert.IsType<PdfDictionary>(document.Resolve(reference)));
    }

    private static (PdfIndirectReference Reference, PdfStream Stream) Image(PdfDocument document)
    {
        var page = Page(document).Dictionary;
        var resources = Assert.IsType<PdfDictionary>(Resolve(document, page[Name("Resources")]));
        var objects = Assert.IsType<PdfDictionary>(Resolve(document, resources[Name("XObject")]));
        var reference = Assert.IsType<PdfIndirectReference>(Assert.Single(objects).Value);
        return (reference, Assert.IsType<PdfStream>(document.Resolve(reference)));
    }

    private static PdfStream With(PdfStream stream, string key, PdfObject value) =>
        new(new PdfDictionary(stream.Dictionary.Where(entry => !entry.Key.Equals(Name(key))).Append(Entry(key, value))), stream.EncodedData.Span);
    private static PdfObject Resolve(PdfDocument document, PdfObject value) => value is PdfIndirectReference reference ? document.Resolve(reference) : value;
    private static PdfCalGrayColorSpace Space(double gamma) => new(whiteX: .95047, whiteZ: 1.08883, gamma: gamma);
    private static PdfRenderOptions Options(int width, int height) => new(width, height, transparentBackground: true);
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
    private static double Number(PdfObject value) => value switch { PdfInteger integer => integer.Value, PdfReal real => real.Value, _ => throw new InvalidOperationException() };
    private static void Pixel(PdfRenderedFrame frame, int index, double expected)
    {
        for (int component = 0; component < 3; component++)
            Assert.InRange((double)frame.Pixels[index * 4 + component], expected - .000001, expected + .000001);
        Assert.Equal(1f, frame.Pixels[index * 4 + 3]);
    }
}
