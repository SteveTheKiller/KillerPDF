using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Syntax;
using Xunit;

namespace KillerPdf.Engine.Tests.Authoring;

public sealed class PdfCalibratedGrayImageTests
{
    [Fact]
    public void CalibratedGrayPreservesSamplesAndCalibrationAfterBuildAndReopen()
    {
        byte[] samples = [0, 30, 127, 128, 228, 255];
        var space = new PdfCalGrayColorSpace(whiteX: 0.95047, whiteZ: 1.08883, gamma: 2.2);
        PdfImage image = PdfImage.FromCalibratedGray(6, 1, samples, space);
        Array.Fill(samples, (byte)42);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder(new PdfVersion(1, 2))
            .AddPage(6, 1, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 6, 1)).Build());
        PdfStream stream = Image(document);
        Assert.Equal(new byte[] { 0, 30, 127, 128, 228, 255 },
            PdfStreamDecoder.Decode(stream, document.Resolve, 6));
        Assert.Same(space, image.CalibratedGray);
        Assert.Equal(PdfImageColorSpace.Gray, image.ColorSpace);
        Assert.Equal(8, image.BitsPerComponent);
        Assert.Equal(8, Number(stream.Dictionary[Name("BitsPerComponent")]));
        PdfArray colorSpace = Assert.IsType<PdfArray>(stream.Dictionary[Name("ColorSpace")]);
        Assert.Equal("CalGray", Assert.IsType<PdfName>(colorSpace[0]).ValueAsLatin1());
        PdfDictionary calibration = Assert.IsType<PdfDictionary>(colorSpace[1]);
        Assert.Equal(new[] { 0.95047, 1, 1.08883 },
            Assert.IsType<PdfArray>(calibration[Name("WhitePoint")]).Select(Number));
        Assert.Equal(new[] { 0d, 0, 0 },
            Assert.IsType<PdfArray>(calibration[Name("BlackPoint")]).Select(Number));
        Assert.Equal(2.2, Number(calibration[Name("Gamma")]));
    }

    [Fact]
    public void NaturalPrecisionRenderingRetainsCalibratedLinearLevels()
    {
        PdfImage image = PdfImage.FromCalibratedGray(6, 1,
            new byte[] { 0, 30, 127, 128, 228, 255 },
            new PdfCalGrayColorSpace(whiteX: 0.95047, whiteZ: 1.08883, gamma: 2.2));
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(6, 1, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 6, 1)).Build());
        var frame = new PdfPageRenderer(document).RenderFrame(0,
            new PdfRenderOptions(6, 1, transparentBackground: true));
        // Independent power-law values for the original eight-bit samples in D65.
        float[] expected = [0, 0.0090214919f, 0.2157644f, 0.2195197181f, 0.7817506778f, 1];
        for (int pixel = 0; pixel < 6; pixel++)
        {
            for (int component = 0; component < 3; component++)
                Assert.InRange(frame.Pixels[pixel * 4 + component],
                    expected[pixel] - 0.000001f, expected[pixel] + 0.000001f);
            Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
        }
    }

    [Fact]
    public void CalibratedGrayReusesAdaptivePredictorWithoutChangingSamples()
    {
        byte[] samples = new byte[256 * 100];
        for (int row = 0; row < 100; row++)
            for (int column = 0; column < 256; column++) samples[row * 256 + column] = (byte)column;
        PdfImage image = PdfImage.FromCalibratedGray(256, 100, samples, new PdfCalGrayColorSpace());
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(256, 100, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 256, 100)).Build());
        PdfStream stream = Image(document);
        PdfDictionary parameters = Assert.IsType<PdfDictionary>(stream.Dictionary[Name("DecodeParms")]);
        Assert.Equal(15, Number(parameters[Name("Predictor")]));
        Assert.Equal(1, Number(parameters[Name("Colors")]));
        Assert.Equal(samples, PdfStreamDecoder.Decode(stream, document.Resolve, samples.Length));
        Assert.Equal(PdfImage.FromGray(256, 100, samples).Data.ToArray(), image.Data.ToArray());
    }

    [Theory]
    [InlineData("page", 2)]
    [InlineData("thumbnail", 2)]
    [InlineData("form", 2)]
    [InlineData("pattern", 2)]
    [InlineData("stamp", 3)]
    [InlineData("icon", 2)]
    [InlineData("rollover", 2)]
    [InlineData("down", 2)]
    public void CalibratedImagesInEveryAuthoredCollectionRespectVersionAndSurviveReopen(string placement, int minimumMinor)
    {
        PdfImage image = PdfImage.FromCalibratedGray(1, 1, new byte[] { 127 }, new PdfCalGrayColorSpace());
        Assert.Throws<InvalidOperationException>(() => Place(new PdfVersion(1, 1), image, placement).Build());
        byte[] bytes = Place(new PdfVersion(1, minimumMinor), image, placement).Build();
        Assert.NotNull(PdfDocument.Open(bytes));
        Assert.Contains("/CalGray", Encoding.Latin1.GetString(bytes));
    }

    [Fact]
    public void FactoryRejectsNullAndInvalidBoundsWhileOrdinaryGrayStaysDeviceGray()
    {
        var space = new PdfCalGrayColorSpace();
        Assert.Throws<ArgumentNullException>(() => PdfImage.FromCalibratedGray(1, 1, new byte[] { 0 }, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfImage.FromCalibratedGray(0, 1, [], space));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfImage.FromCalibratedGray(1, -1, [], space));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfImage.FromCalibratedGray(int.MaxValue, 2, [], space));
        Assert.Throws<ArgumentException>(() => PdfImage.FromCalibratedGray(2, 1, new byte[] { 0 }, space));
        PdfImage ordinary = PdfImage.FromGray(1, 1, new byte[] { 127 });
        Assert.Null(ordinary.CalibratedGray);
        PdfDocument document = PdfDocument.Open(Place(new PdfVersion(1, 1), ordinary, "page").Build());
        Assert.Equal("DeviceGray", Assert.IsType<PdfName>(Image(document).Dictionary[Name("ColorSpace")]).ValueAsLatin1());
    }

    private static PdfDocumentBuilder Place(PdfVersion version, PdfImage image, string placement)
    {
        var builder = new PdfDocumentBuilder(version);
        var content = new PdfContentStreamBuilder().DrawImage(image, 0, 0, 1, 1);
        if (placement == "page") return builder.AddPage(10, 10, content);
        if (placement == "form")
        {
            var inner = new PdfFormXObject(1, 1, content);
            var outer = new PdfFormXObject(1, 1, new PdfContentStreamBuilder().DrawForm(inner, 0, 0));
            return builder.AddPage(10, 10, new PdfContentStreamBuilder().DrawForm(outer, 0, 0));
        }
        if (placement == "pattern")
        {
            var pattern = new PdfTilingPattern(1, 1, content);
            var form = new PdfFormXObject(1, 1, new PdfContentStreamBuilder()
                .SetFillPattern(pattern).Rectangle(0, 0, 1, 1).Fill());
            return builder.AddPage(10, 10, new PdfContentStreamBuilder().DrawForm(form, 0, 0));
        }
        builder.AddBlankPage();
        if (placement == "thumbnail") return builder.SetPageThumbnail(0, image);
        if (placement == "stamp") return builder.AddImageStamp(0, 0, 0, 1, 1, image);
        return builder.AddUriPushButton(0, "button", 0, 0, 20, 20, "Open", "https://example.com",
            appearanceOptions: new PdfPushButtonAppearanceOptions
            {
                Icon = placement == "icon" ? image : null,
                RolloverIcon = placement == "rollover" ? image : null,
                DownIcon = placement == "down" ? image : null
            });
    }

    private static PdfStream Image(PdfDocument document)
    {
        PdfDictionary catalog = Resolve(document, document.Trailer[Name("Root")]);
        PdfDictionary pages = Resolve(document, catalog[Name("Pages")]);
        PdfDictionary page = Resolve(document, Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary objects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        return Assert.IsType<PdfStream>(document.Resolve(Assert.IsType<PdfIndirectReference>(objects[Name("Im1")])));
    }

    private static PdfDictionary Resolve(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(value)));

    private static double Number(PdfObject value) => value switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => throw new Xunit.Sdk.XunitException("Expected number")
    };

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
