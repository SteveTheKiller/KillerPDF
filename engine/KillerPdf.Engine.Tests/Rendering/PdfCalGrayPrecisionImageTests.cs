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
    [InlineData("enlargement")]
    [InlineData("reduction")]
    [InlineData("one-axis")]
    [InlineData("quarter-turn")]
    [InlineData("half-pixel")]
    public void UnsupportedGeometryRemainsDiagnosed(string geometry)
    {
        PdfDocument document;
        int width = 1, height = 1;
        if (geometry == "enlargement")
        {
            width = height = 2;
            var image = PdfImage.FromCalibratedGray(1, 1, new byte[] { 127 }, Space(2.2));
            document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 2,
                new PdfContentStreamBuilder().DrawImage(image, 0, 0, 2, 2)).Build());
        }
        else if (geometry == "reduction")
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
    [InlineData("color-key")]
    [InlineData("soft-mask")]
    [InlineData("matte")]
    public void NaturalSizeMasksRemainDiagnosed(string kind)
    {
        var document = Build(1, 1, [127], Space(2.2));
        var (reference, stream) = Image(document);
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfStream modified;
        if (kind == "color-key") modified = With(stream, "Mask", new PdfArray([new PdfInteger(0), new PdfInteger(0)]));
        else
        {
            var entries = new List<KeyValuePair<PdfName, PdfObject>>
            {
                Entry("Type", Name("XObject")), Entry("Subtype", Name("Image")),
                Entry("Width", new PdfInteger(1)), Entry("Height", new PdfInteger(1)),
                Entry("BitsPerComponent", new PdfInteger(8)), Entry("ColorSpace", Name("DeviceGray"))
            };
            if (kind == "matte") entries.Add(Entry("Matte", new PdfArray([new PdfReal(.5)])));
            var mask = update.AddObject(new PdfStream(new PdfDictionary(entries), new byte[] { 128 }));
            modified = With(stream, "SMask", mask);
        }
        document = PdfDocument.Open(update.ReplaceObject(reference.ObjectNumber, modified).Build());
        Assert.Empty(new PdfPageRenderer(document).Render(0, new PdfRenderOptions(1, 1)).Diagnostics);
        Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(document).RenderFrame(0, Options(1, 1)));
    }

    [Fact]
    public void SixteenBitCalGrayRemainsDiagnosedAsAValidUnsupportedImage()
    {
        var document = ModifyImage(Build(1, 1, [127], Space(2.2)), stream => new PdfStream(
            new PdfDictionary(stream.Dictionary.Where(entry => !entry.Key.Equals(Name("Filter"))
                && !entry.Key.Equals(Name("DecodeParms")) && !entry.Key.Equals(Name("BitsPerComponent")))
                .Append(Entry("BitsPerComponent", new PdfInteger(16)))), new byte[] { 128, 0 }));
        Assert.Empty(new PdfPageRenderer(document).Render(0, new PdfRenderOptions(1, 1)).Diagnostics);
        Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(document).RenderFrame(0, Options(1, 1)));
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
