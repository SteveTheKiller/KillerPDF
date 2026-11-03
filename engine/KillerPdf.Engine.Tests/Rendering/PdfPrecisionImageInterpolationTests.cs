using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionImageInterpolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequestedInterpolationRetainsFractionalSixteenBitSamples(bool gray)
    {
        var renderer = Renderer(gray: gray);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(4, 1, transparentBackground: true));

        // Bilinear pixel centers produce source samples 32768, 32768.25,
        // 32768.75 and 32769. These independent 60-digit decimal sRGB results
        // are frozen before float32 storage, without using renderer calculations.
        AssertGray(frame, 0, 0.21404820f);
        AssertGray(frame, 1, 0.21405173f);
        AssertGray(frame, 2, 0.21405880f);
        AssertGray(frame, 3, 0.21406233f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutomaticTwoDimensionalInterpolationUsesEncodedDeviceSamples(bool gray)
    {
        var renderer = Renderer(checker: true, interpolate: false, gray: gray);
        var options = new PdfRenderOptions(4, 4, transparentBackground: true);

        var frame = renderer.RenderFrame(0, options);

        // At these centers the independent encoded checker weights are 0.375
        // and 0.625, whose linear sRGB values are 0.1160161342 and 0.3485101952.
        AssertGray(frame, 0, 0f);
        AssertGray(frame, 5, 0.11601613f);
        AssertGray(frame, 6, 0.34851020f);
        AssertGray(frame, 9, 0.34851020f);
        AssertGray(frame, 15, 0f);
        Assert.Equal(renderer.Render(0, options).Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    [Fact]
    public void BilinearSamplesHonorDecodeClipAndOpacity()
    {
        var renderer = Renderer(checker: true, decode: [1, 0],
            content: "1 1 2 2 re W n /Half gs 4 0 0 4 0 0 cm /Image Do");

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(4, 4, transparentBackground: true));

        Assert.Equal(0f, frame.Pixels[3]);
        AssertGray(frame, 5, 0.34851020f, 0.5f);
        AssertGray(frame, 6, 0.11601613f, 0.5f);
        AssertGray(frame, 9, 0.11601613f, 0.5f);
        AssertGray(frame, 10, 0.34851020f, 0.5f);
        Assert.Equal(0f, frame.Pixels[15 * 4 + 3]);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 2)]
    public void BilinearDecodeOutsideTheDeviceRangeIsRejected(double minimum, double maximum)
    {
        var renderer = Renderer(checker: true, decode: [minimum, maximum]);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(4, 4)));
    }

    private static PdfPageRenderer Renderer(bool checker = false, bool interpolate = true,
        double[]? decode = null, string? content = null, bool? gray = null)
    {
        bool deviceGray = gray ?? checker;
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(4, checker ? 4 : 1,
            Encoding.ASCII.GetBytes(content ?? (checker ? "4 0 0 4 0 0 cm /Image Do" : "4 0 0 1 0 0 cm /Image Do"))).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        var entries = new List<KeyValuePair<PdfName, PdfObject>> {
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)),
            Entry("Height", new PdfInteger(checker ? 2 : 1)), Entry("ColorSpace", Name(deviceGray ? "DeviceGray" : "DeviceRGB")),
            Entry("BitsPerComponent", new PdfInteger(checker ? 8 : 16)), Entry("Interpolate", new PdfBoolean(interpolate))
        };
        if (decode is not null)
            entries.Add(Entry("Decode", new PdfArray(decode.Select(value => (PdfObject)new PdfReal(value)))));
        byte[] samples = checker
            ? deviceGray ? [0, 255, 255, 0] : [0, 0, 0, 255, 255, 255, 255, 255, 255, 0, 0, 0]
            : deviceGray ? [128, 0, 128, 1] : [128, 0, 128, 0, 128, 0, 128, 1, 128, 1, 128, 1];
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
