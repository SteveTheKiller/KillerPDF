using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionImageReductionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothAxisReductionRetainsTheSixteenBitEncodedAreaMean(bool gray)
    {
        var renderer = Renderer(gray: gray);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(1, 1, transparentBackground: true));

        // Four identical rows contain encoded sample integers 32768, 32769, 32770,
        // and 32771. Their independent area mean is 32769.5/65535, whose linear
        // sRGB value from 60-digit decimal arithmetic is 0.2140693885614967.
        AssertGray(frame, 0, 0.21406939f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoDimensionalCheckerReductionAveragesEncodedSamples(bool gray)
    {
        var renderer = Renderer(checker: true, gray: gray);
        var options = new PdfRenderOptions(1, 1, transparentBackground: true);

        var frame = renderer.RenderFrame(0, options);

        // Equal black and white source coverage has encoded mean 0.5.
        AssertGray(frame, 0, 0.21404114f);
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, renderer.Render(0, options).Pixels.ToArray());
        Assert.Equal(renderer.Render(0, options).Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    [Fact]
    public void AreaSamplesHonorDecodeClipAndOpacity()
    {
        var renderer = Renderer(outputWidth: 2, outputHeight: 2,
            content: "1 0 1 2 re W n /Half gs 2 0 0 2 0 0 cm /Image Do",
            decode: [0.25, 0.75, 0.25, 0.75, 0.25, 0.75]);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 2, transparentBackground: true));

        // The visible source block averages 32770.5/65535. Decode [0.25,0.75]
        // maps it to 32769/65535 before compositing, independently of the clip.
        Assert.Equal(0f, frame.Pixels[3]);
        AssertGray(frame, 1, 0.21406233f, 0.5f);
        Assert.Equal(0f, frame.Pixels[2 * 4 + 3]);
        AssertGray(frame, 3, 0.21406233f, 0.5f);
    }

    [Fact]
    public void DistinctIntegerAxisFactorsKeepTheirOwnSourceFootprints()
    {
        var renderer = Renderer(outputWidth: 2);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        // The two disjoint source column pairs average 32768.5 and 32770.5.
        AssertGray(frame, 0, 0.21405526f);
        AssertGray(frame, 1, 0.21408351f);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 2)]
    public void AreaDecodeOutsideTheDeviceRangeIsRejected(double minimum, double maximum)
    {
        var renderer = Renderer(checker: true, decode: [minimum, maximum]);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(1, 1)));
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(2, 8)]
    public void FractionalFootprintsAndMixedScalingRetainSourceMeans(int width, int height)
    {
        var renderer = Renderer(outputWidth: width, outputHeight: height);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(width, height));
        double first = (width == 3 ? 32768.25 : 32768.5) / 65535;
        double last = (width == 3 ? 32770.75 : 32770.5) / 65535;
        for (int y = 0; y < height; y++)
        {
            AssertGray(frame, y * width, (float)Math.Pow((first + .055) / 1.055, 2.4));
            AssertGray(frame, y * width + width - 1, (float)Math.Pow((last + .055) / 1.055, 2.4));
        }
    }

    private static PdfPageRenderer Renderer(bool checker = false, int outputWidth = 1, int outputHeight = 1,
        string? content = null, double[]? decode = null, bool? gray = null)
    {
        bool deviceGray = gray ?? checker;
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(outputWidth, outputHeight,
            Encoding.ASCII.GetBytes(content ?? $"{outputWidth} 0 0 {outputHeight} 0 0 cm /Image Do")).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        byte[] row = deviceGray ? [128, 0, 128, 1, 128, 2, 128, 3]
            : [128, 0, 128, 0, 128, 0, 128, 1, 128, 1, 128, 1,
                128, 2, 128, 2, 128, 2, 128, 3, 128, 3, 128, 3];
        var entries = new List<KeyValuePair<PdfName, PdfObject>> {
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(checker ? 2 : 4)),
            Entry("Height", new PdfInteger(checker ? 2 : 4)), Entry("ColorSpace", Name(deviceGray ? "DeviceGray" : "DeviceRGB")),
            Entry("BitsPerComponent", new PdfInteger(checker ? 8 : 16)), Entry("Interpolate", new PdfBoolean(false))
        };
        if (decode is not null)
            entries.Add(Entry("Decode", new PdfArray(decode.Select(value => (PdfObject)new PdfReal(value)))));
        byte[] samples = checker
            ? deviceGray ? [0, 255, 255, 0] : [0, 0, 0, 255, 255, 255, 255, 255, 255, 0, 0, 0]
            : Enumerable.Range(0, 4).SelectMany(_ => row).ToArray();
        var image = update.AddObject(new PdfStream(new PdfDictionary(entries), samples));
        var resources = new PdfDictionary([
            Entry("XObject", new PdfDictionary([Entry("Image", image)])),
            Entry("ExtGState", new PdfDictionary([Entry("Half",
                new PdfDictionary([Entry("ca", new PdfReal(0.5))]))]))
        ]);
        var document = PdfDocument.Open(update.ReplaceObject(page.Reference.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());
        return new PdfPageRenderer(document);
    }

    private static void AssertGray(PdfRenderedFrame frame, int pixel, float expected, float alpha = 1f)
    {
        int offset = pixel * 4;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[offset + channel], expected - 0.00000003f, expected + 0.00000003f);
        Assert.Equal(alpha, frame.Pixels[offset + 3]);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
