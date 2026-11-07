using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfSoftMaskImageEdgePrecisionTests
{
    [Fact]
    public void FractionalHorizontalEdgesMultiplyMaskAlphaWithoutPremultiplyingSourceColor()
    {
        var document = Build("1 0 0 1 .25 0 cm /Image Do", [255, 0, 0], [128]);
        byte[] original = Snapshot(document);

        var frame = new PdfPageRenderer(document).RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        // Independent .75 and .25 pixel coverage multiplied by the raw alpha ratio 128/255.
        Pixel(frame, 0, [1, 0, 0], .3764705882352941);
        Pixel(frame, 1, [1, 0, 0], .12549019607843137);
        Assert.Equal(original, Snapshot(document));
    }

    [Theory]
    [InlineData(false, 8, 8)]
    [InlineData(false, 8, 16)]
    [InlineData(false, 16, 8)]
    [InlineData(false, 16, 16)]
    [InlineData(true, 8, 8)]
    [InlineData(true, 8, 16)]
    [InlineData(true, 16, 8)]
    [InlineData(true, 16, 16)]
    public void FractionalEdgesKeepIndependentSourceAndMaskDepths(bool gray, int bits, int maskBits)
    {
        byte[] samples = Enumerable.Range(0, 2).SelectMany(pixel => Enumerable.Range(0, gray ? 1 : 3)
            .SelectMany(_ => bits == 16 ? new byte[] { 128, (byte)pixel } : [(byte)(128 + pixel)])).ToArray();
        var document = Build("2 0 0 1 .25 0 cm /Image Do", samples, maskBits == 16 ? [128, 0, 128, 1] : [128, 129],
            pageWidth: 3, sourceWidth: 2, gray: gray, bits: bits, maskBits: maskBits);
        byte[] original = Snapshot(document);

        var frame = new PdfPageRenderer(document).RenderFrame(0, new PdfRenderOptions(3, 1, transparentBackground: true));

        float first = bits == 16 ? .21404820f : .21586050f;
        float second = bits == 16 ? .21406233f : .21952620f;
        Pixel(frame, 0, [first, first, first], maskBits == 16 ? .3750057221332112 : .3764705882352941);
        Pixel(frame, 1, [second, second, second], maskBits == 16 ? .500022888532845 : .5058823529411764);
        Pixel(frame, 2, [second, second, second], maskBits == 16 ? .1250057221332112 : .1264705882352941);
        Assert.True(frame.Pixels[4] > frame.Pixels[0]);
        Assert.Equal(original, Snapshot(document));
    }

    [Fact]
    public void FractionalSwatchEnlargementCoversCornersWithOpaqueZeroAndPartialMaskSamples()
    {
        var document = Build("40 0 0 40 .25 .25 cm /Image Do", [0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0, 255],
            [128, 0, 255, 128], pageWidth: 41, pageHeight: 41, sourceWidth: 2, sourceHeight: 2);
        byte[] original = Snapshot(document);
        var renderer = new PdfPageRenderer(document);
        var options = new PdfRenderOptions(41, 41, transparentBackground: true);
        var compatibility = renderer.Render(0, options);
        Assert.Empty(compatibility.Diagnostics);
        byte[] bytes = compatibility.Pixels.ToArray();

        var frame = renderer.RenderFrame(0, options);

        Pixel(frame, 0, [0, 0, 1], .09411764705882353);
        Assert.Equal(0, frame.Pixels[40 * 4 + 3]);
        Pixel(frame, 40 * 41, [0, 0, 1], .5625);
        Pixel(frame, 40 * 41 + 40, [0, 0, 1], .09411764705882353);
        Pixel(frame, 21 * 41 + 1, [0, 0, 1], 1);
        Assert.All(frame.Pixels.ToArray(), value => Assert.True(float.IsFinite(value)));
        Assert.Equal(bytes, renderer.Render(0, options).Pixels.ToArray());
        Assert.Equal(original, Snapshot(document));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FractionalReflectionsKeepColorAndMaskSamplesTogether(bool vertical)
    {
        var document = Build(vertical ? "1 0 0 -2 0 2.25 cm /Image Do" : "-2 0 0 1 2.25 0 cm /Image Do",
            [255, 0, 0, 0, 0, 255], [128, 255], pageWidth: vertical ? 1 : 3, pageHeight: vertical ? 3 : 1,
            sourceWidth: vertical ? 1 : 2, sourceHeight: vertical ? 2 : 1);
        byte[] original = Snapshot(document);

        var frame = new PdfPageRenderer(document).RenderFrame(0,
            new PdfRenderOptions(vertical ? 1 : 3, vertical ? 3 : 1, transparentBackground: true));

        if (vertical)
        {
            Pixel(frame, 0, [0, 0, 1], .25);
            Pixel(frame, 1, [0, 0, 1], 1);
            Pixel(frame, 2, [1, 0, 0], .3764705882352941);
        }
        else
        {
            Pixel(frame, 0, [0, 0, 1], .75);
            Pixel(frame, 1, [1, 0, 0], .5019607843137255);
            Pixel(frame, 2, [1, 0, 0], .12549019607843137);
        }
        Assert.Equal(original, Snapshot(document));
    }

    [Theory]
    [InlineData("matte")]
    [InlineData("color-key")]
    [InlineData("smoothed")]
    [InlineData("reduced")]
    [InlineData("sheared")]
    [InlineData("bad-mask-decode")]
    public void UnsupportedFractionalMaskPaintsRemainRefused(string refusal)
    {
        string content = refusal switch {
            "smoothed" => "2 0 0 1 .25 0 cm /Image Do",
            "reduced" => ".5 0 0 .5 .25 0 cm /Image Do",
            "sheared" => "1 0 1 1 .25 0 cm /Image Do",
            _ => "1 0 0 1 .25 0 cm /Image Do" };
        var document = Build(content, [255, 0, 0], [128], refusal: refusal);
        byte[] original = Snapshot(document);

        Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(document).RenderFrame(0,
            new PdfRenderOptions(2, 1, transparentBackground: true)));

        Assert.Equal(original, Snapshot(document));
    }

    [Theory]
    [InlineData(false, 8)]
    [InlineData(false, 16)]
    [InlineData(true, 8)]
    [InlineData(true, 16)]
    public void FractionalMaskEndpointsRetainFinitePixelsAndOpaqueCoverage(bool gray, int maskBits)
    {
        byte[] samples = gray ? [128, 0, 128, 1] : [128, 0, 128, 0, 128, 0, 128, 1, 128, 1, 128, 1];
        var document = Build("2 0 0 1 .25 0 cm /Image Do", samples, maskBits == 16 ? [0, 0, 255, 255] : [0, 255],
            pageWidth: 3, sourceWidth: 2, gray: gray, bits: 16, maskBits: maskBits);
        byte[] original = Snapshot(document);

        var frame = new PdfPageRenderer(document).RenderFrame(0, new PdfRenderOptions(3, 1, transparentBackground: true));

        Assert.Equal(0, frame.Pixels[3]);
        Assert.All(frame.Pixels.ToArray(), value => Assert.True(float.IsFinite(value)));
        Pixel(frame, 1, [.21406233f, .21406233f, .21406233f], 1);
        Pixel(frame, 2, [.21406233f, .21406233f, .21406233f], .25);
        Assert.Equal(original, Snapshot(document));
    }

    private static PdfDocument Build(string content, byte[] samples, byte[] maskSamples, int pageWidth = 2,
        int pageHeight = 1, int sourceWidth = 1, int sourceHeight = 1, bool gray = false, int bits = 8, int maskBits = 8,
        string? refusal = null)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(pageWidth, pageHeight,
            Encoding.ASCII.GetBytes(content)).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        var maskEntries = new List<KeyValuePair<PdfName, PdfObject>> {
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(sourceWidth)),
            Entry("Height", new PdfInteger(sourceHeight)), Entry("ColorSpace", Name("DeviceGray")),
            Entry("BitsPerComponent", new PdfInteger(maskBits)) };
        if (refusal == "matte") maskEntries.Add(Entry("Matte", new PdfArray([
            new PdfInteger(0), new PdfInteger(0), new PdfInteger(0)])));
        if (refusal == "bad-mask-decode") maskEntries.Add(Entry("Decode", new PdfArray([new PdfInteger(-1), new PdfInteger(1)])));
        var mask = update.AddObject(new PdfStream(new PdfDictionary(maskEntries), maskSamples));
        var imageEntries = new List<KeyValuePair<PdfName, PdfObject>> {
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(sourceWidth)),
            Entry("Height", new PdfInteger(sourceHeight)), Entry("ColorSpace", Name(gray ? "DeviceGray" : "DeviceRGB")),
            Entry("BitsPerComponent", new PdfInteger(bits)), Entry("Interpolate", new PdfBoolean(refusal == "smoothed")) };
        if (refusal == "color-key") imageEntries.Add(Entry("Mask", new PdfArray(
            Enumerable.Repeat<PdfObject>(new PdfInteger(0), (gray ? 1 : 3) * 2))));
        else imageEntries.Add(Entry("SMask", mask));
        var image = update.AddObject(new PdfStream(new PdfDictionary(imageEntries), samples));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Image", image)]))]);
        return PdfDocument.Open(update.ReplaceObject(page.Reference.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());
    }

    private static byte[] Snapshot(PdfDocument document)
    {
        var root = Assert.IsType<PdfIndirectReference>(document.Trailer[Name("Root")]);
        return new PdfIncrementalUpdateBuilder(document).ReplaceObject(root.ObjectNumber, document.Resolve(root)).Build();
    }

    private static void Pixel(PdfRenderedFrame frame, int pixel, float[] color, double alpha)
    {
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[pixel * 4 + channel], color[channel] - .00000003f, color[channel] + .00000003f);
        Assert.InRange((double)frame.Pixels[pixel * 4 + 3], alpha - .00000003, alpha + .00000003);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
