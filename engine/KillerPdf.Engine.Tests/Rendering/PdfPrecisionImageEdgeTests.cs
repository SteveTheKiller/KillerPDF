using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionImageEdgeTests
{
    [Fact]
    public void FractionalHorizontalEdgesRetainStraightRedAndCoverBothPixels()
    {
        var renderer = Renderer("1 0 0 1 .25 0 cm /Image Do");

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        AssertPixel(frame, 0, 1, 0, 0, .75f);
        AssertPixel(frame, 1, 1, 0, 0, .25f);
    }

    [Fact]
    public void FractionalVerticalEdgesAndCornersUseAreaCoverage()
    {
        var vertical = Renderer("1 0 0 1 0 .25 cm /Image Do", pageWidth: 1, pageHeight: 2)
            .RenderFrame(0, new PdfRenderOptions(1, 2, transparentBackground: true));
        AssertPixel(vertical, 0, 1, 0, 0, .25f);
        AssertPixel(vertical, 1, 1, 0, 0, .75f);

        var corner = Renderer("1 0 0 1 .25 .25 cm /Image Do", pageHeight: 2)
            .RenderFrame(0, new PdfRenderOptions(2, 2, transparentBackground: true));
        AssertPixel(corner, 0, 1, 0, 0, .1875f);
        AssertPixel(corner, 1, 1, 0, 0, .0625f);
        AssertPixel(corner, 2, 1, 0, 0, .5625f);
        AssertPixel(corner, 3, 1, 0, 0, .1875f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FractionalEdgesPreserveAdjacentSixteenBitSourceSamples(bool gray)
    {
        byte[] samples = gray ? [128, 0, 128, 1] : [128, 0, 128, 0, 128, 0, 128, 1, 128, 1, 128, 1];
        byte[] original = samples.ToArray();
        var renderer = Renderer("2 0 0 1 .25 0 cm /Image Do", pageWidth: 3,
            sourceWidth: 2, gray: gray, bits: 16, samples: samples);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(3, 1, transparentBackground: true));

        // Frozen linear sRGB values for 32768/65535 and 32769/65535.
        AssertPixel(frame, 0, .21404820f, .21404820f, .21404820f, .75f);
        AssertPixel(frame, 1, .21406233f, .21406233f, .21406233f, 1);
        AssertPixel(frame, 2, .21406233f, .21406233f, .21406233f, .25f);
        Assert.True(frame.Pixels[4] > frame.Pixels[0]);
        Assert.Equal(original, samples);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FractionalReflectionPreservesSourceOrientation(bool reflect)
    {
        var renderer = Renderer(reflect ? "-2 0 0 1 2.25 0 cm /Image Do" : "2 0 0 1 .25 0 cm /Image Do",
            pageWidth: 3, sourceWidth: 2, samples: [255, 0, 0, 0, 0, 255]);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(3, 1, transparentBackground: true));

        AssertPixel(frame, 0, reflect ? 0 : 1, 0, reflect ? 1 : 0, .75f);
        AssertPixel(frame, 1, reflect ? 1 : 0, 0, reflect ? 0 : 1, 1);
        AssertPixel(frame, 2, reflect ? 1 : 0, 0, reflect ? 0 : 1, .25f);
    }

    [Fact]
    public void FractionalGrayEdgesRespectRectangularClipAndAlignedControl()
    {
        var clipped = Renderer("1 0 1 1 re W n 1 0 0 1 .25 0 cm /Image Do", gray: true, samples: [255])
            .RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));
        Assert.Equal(0, clipped.Pixels[3]);
        AssertPixel(clipped, 1, 1, 1, 1, .25f);

        var aligned = Renderer("1 0 0 1 0 0 cm /Image Do", gray: true, samples: [255])
            .RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));
        AssertPixel(aligned, 0, 1, 1, 1, 1);
        Assert.Equal(0, aligned.Pixels[7]);
    }

    [Theory]
    [InlineData(400, 40, 1f)]
    [InlineData(401, 40, .9f)]
    [InlineData(512, 51, .8f)]
    [InlineData(1024, 102, .6f)]
    [InlineData(2048, 204, .2f)]
    public void FittedPageRetainsFractionalImageCoverageAndCompatibilityBytes(int size, int edgeX, float edgeAlpha)
    {
        var renderer = Renderer("120 0 0 60 40 200 cm /Image Do", pageWidth: 400, pageHeight: 400,
            sourceWidth: 2, samples: [255, 0, 0, 255, 0, 0]);
        var options = new PdfRenderOptions(size, size, transparentBackground: true);
        var compatibility = renderer.Render(0, options);
        Assert.Empty(compatibility.Diagnostics);
        byte[] bytes = compatibility.Pixels.ToArray();

        var frame = renderer.RenderFrame(0, options);

        AssertPixel(frame, (size * 2 / 5) * size + edgeX, 1, 0, 0, edgeAlpha);
        AssertPixel(frame, (size * 2 / 5) * size + size / 4, 1, 0, 0, 1);
        Assert.Equal(bytes, renderer.Render(0, options).Pixels.ToArray());
    }

    private static PdfPageRenderer Renderer(string content, int pageWidth = 2, int pageHeight = 1,
        int sourceWidth = 1, int sourceHeight = 1, bool gray = false, int bits = 8, byte[]? samples = null)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(pageWidth, pageHeight,
            Encoding.ASCII.GetBytes(content)).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(sourceWidth)),
            Entry("Height", new PdfInteger(sourceHeight)), Entry("ColorSpace", Name(gray ? "DeviceGray" : "DeviceRGB")),
            Entry("BitsPerComponent", new PdfInteger(bits))]), samples ?? [255, 0, 0]));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Image", image)]))]);
        var document = PdfDocument.Open(update.ReplaceObject(page.Reference.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());
        return new PdfPageRenderer(document);
    }

    private static void AssertPixel(PdfRenderedFrame frame, int pixel, float red, float green, float blue, float alpha)
    {
        float[] expected = [red, green, blue, alpha];
        for (int channel = 0; channel < 4; channel++)
            Assert.InRange(frame.Pixels[pixel * 4 + channel], expected[channel] - .00000003f, expected[channel] + .00000003f);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
