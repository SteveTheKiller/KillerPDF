using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfCalGrayImageEdgePrecisionTests
{
    [Fact]
    public void FractionalHorizontalEdgesRetainCalibratedColorAndCoverBothPixels()
    {
        var image = PdfImage.FromCalibratedGray(1, 1, new byte[] { 128 }, Space());
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            new PdfContentStreamBuilder().DrawImage(image, .25, 0, 1, 1)).Build());

        var frame = new PdfPageRenderer(document).RenderFrame(0, Options(2, 1));

        // Independent Gamma 2 result for 128/255, retained as straight linear RGB.
        Pixel(frame, 0, .2519646289888504, .75f);
        Pixel(frame, 1, .2519646289888504, .25f);
    }

    [Theory]
    [InlineData(8, .2519646289888504, .255916955017301)]
    [InlineData(16, .2500076295691578, .25002288905673)]
    public void FractionalVerticalEdgesKeepAdjacentSourceLevelsAndDocumentBytes(int bits, double first, double second)
    {
        var document = Build(1, 3, 1, 2, bits == 8 ? [128, 129] : [128, 0, 128, 1], bits,
            0, .25, 1, 2);
        byte[] original = Snapshot(document);

        var frame = new PdfPageRenderer(document).RenderFrame(0, Options(1, 3));

        Pixel(frame, 0, first, .25f);
        Pixel(frame, 1, first, 1);
        Pixel(frame, 2, second, .75f);
        Assert.True(frame.Pixels[8] > frame.Pixels[0]);
        Assert.Equal(original, Snapshot(document));
    }

    [Fact]
    public void FractionalCornersMultiplyHorizontalAndVerticalCoverage()
    {
        var document = Build(2, 2, 1, 1, [128], 8, .25, .25, 1, 1);
        byte[] original = Snapshot(document);

        var frame = new PdfPageRenderer(document).RenderFrame(0, Options(2, 2));

        Pixel(frame, 0, .2519646289888504, .1875f);
        Pixel(frame, 1, .2519646289888504, .0625f);
        Pixel(frame, 2, .2519646289888504, .5625f);
        Pixel(frame, 3, .2519646289888504, .1875f);
        Assert.Equal(original, Snapshot(document));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FractionalNearestEnlargementRetainsSourceOrientation(bool reflected)
    {
        var document = Build(5, 1, 2, 1, [0, 255], 8, reflected ? 4.25 : .25, 0, reflected ? -4 : 4, 1);
        byte[] original = Snapshot(document);

        var frame = new PdfPageRenderer(document).RenderFrame(0, Options(5, 1));

        Pixel(frame, 0, reflected ? 1 : 0, .75f);
        Pixel(frame, 1, reflected ? 1 : 0, 1);
        Pixel(frame, 2, reflected ? 0 : 1, 1);
        Pixel(frame, 3, reflected ? 0 : 1, 1);
        Pixel(frame, 4, reflected ? 0 : 1, .25f);
        Assert.Equal(original, Snapshot(document));
    }

    [Fact]
    public void FractionalBilinearEnlargementInterpolatesSamplesBeforeGamma()
    {
        var document = Build(5, 1, 2, 1, [0, 255], 8, .25, 0, 4, 1, interpolate: true);
        byte[] original = Snapshot(document);

        var frame = new PdfPageRenderer(document).RenderFrame(0, Options(5, 1));

        // Encoded source interpolation gives 0, .125, .625, 1 and 1 before Gamma 2.
        Pixel(frame, 0, 0, .75f);
        Pixel(frame, 1, .015625, 1);
        Pixel(frame, 2, .390625, 1);
        Pixel(frame, 3, 1, 1);
        Pixel(frame, 4, 1, .25f);
        Assert.Equal(original, Snapshot(document));
    }

    [Fact]
    public void FractionalNaturalSizeRetainsReversedDecodeBeforeGamma()
    {
        var document = Build(3, 1, 2, 1, [128, 129], 8, .25, 0, 2, 1, reverseDecode: true);
        byte[] original = Snapshot(document);

        var frame = new PdfPageRenderer(document).RenderFrame(0, Options(3, 1));

        Pixel(frame, 0, .24804306036139947, .75f);
        Pixel(frame, 1, .2441522491349481, 1);
        Pixel(frame, 2, .2441522491349481, .25f);
        Assert.True(frame.Pixels[0] > frame.Pixels[4]);
        Assert.Equal(original, Snapshot(document));
    }

    [Theory]
    [InlineData("soft-mask")]
    [InlineData("color-key")]
    [InlineData("matte")]
    public void FractionalMaskedCalGrayEdgesRemainUnsupported(string mask)
    {
        var document = Build(2, 1, 1, 1, [128], 8, .25, 0, 1, 1, mask: mask);
        byte[] original = Snapshot(document);
        var renderer = new PdfPageRenderer(document);
        Assert.Empty(renderer.Render(0, Options(2, 1)).Diagnostics);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, Options(2, 1)));

        Assert.Equal(original, Snapshot(document));
    }

    private static PdfDocument Build(int pageWidth, int pageHeight, int sourceWidth, int sourceHeight,
        byte[] samples, int bits, double x, double y, double width, double height, bool reverseDecode = false,
        bool interpolate = false, string? mask = null)
    {
        var image = PdfImage.FromCalibratedGray(sourceWidth, sourceHeight, new byte[sourceWidth * sourceHeight], Space());
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(pageWidth, pageHeight,
            new PdfContentStreamBuilder().DrawImage(image, x, y, width, height)).Build());
        var page = Assert.IsType<PdfDictionary>(document.Resolve(PdfPageTree.Read(document).Pages[0].Reference));
        var resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var objects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        var reference = Assert.IsType<PdfIndirectReference>(Assert.Single(objects).Value);
        var stream = Assert.IsType<PdfStream>(document.Resolve(reference));
        var entries = stream.Dictionary.Where(entry => !entry.Key.Equals(Name("Filter"))
            && !entry.Key.Equals(Name("DecodeParms")) && !entry.Key.Equals(Name("BitsPerComponent")))
            .Append(Entry("BitsPerComponent", new PdfInteger(bits))).ToList();
        entries.Add(Entry("Interpolate", new PdfBoolean(interpolate)));
        if (reverseDecode) entries.Add(Entry("Decode", new PdfArray([new PdfInteger(1), new PdfInteger(0)])));
        var update = new PdfIncrementalUpdateBuilder(document);
        if (mask == "color-key")
            entries.Add(Entry("Mask", new PdfArray([new PdfInteger(0), new PdfInteger(0)])));
        else if (mask is not null)
        {
            var maskEntries = new List<KeyValuePair<PdfName, PdfObject>> {
                Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(sourceWidth)),
                Entry("Height", new PdfInteger(sourceHeight)), Entry("ColorSpace", Name("DeviceGray")),
                Entry("BitsPerComponent", new PdfInteger(8)) };
            if (mask == "matte") maskEntries.Add(Entry("Matte", new PdfArray([new PdfReal(.5)])));
            entries.Add(Entry("SMask", update.AddObject(new PdfStream(new PdfDictionary(maskEntries),
                Enumerable.Repeat((byte)128, sourceWidth * sourceHeight).ToArray()))));
        }
        return PdfDocument.Open(update.ReplaceObject(reference.ObjectNumber,
            new PdfStream(new PdfDictionary(entries), samples)).Build());
    }

    private static byte[] Snapshot(PdfDocument document)
    {
        var root = Assert.IsType<PdfIndirectReference>(document.Trailer[Name("Root")]);
        return new PdfIncrementalUpdateBuilder(document).ReplaceObject(root.ObjectNumber, document.Resolve(root)).Build();
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
    private static PdfCalGrayColorSpace Space() => new(whiteX: .95047, whiteZ: 1.08883, gamma: 2);
    private static PdfRenderOptions Options(int width, int height) => new(width, height, transparentBackground: true);
    private static void Pixel(PdfRenderedFrame frame, int pixel, double expected, float alpha)
    {
        for (int component = 0; component < 3; component++)
            Assert.InRange((double)frame.Pixels[pixel * 4 + component], expected - .000001, expected + .000001);
        Assert.Equal(alpha, frame.Pixels[pixel * 4 + 3]);
    }
}
