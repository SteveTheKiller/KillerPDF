using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionColorKeyMaskTests
{
    [Theory]
    [InlineData(16, false, 0.21406233f)]
    [InlineData(16, true, 0.21406233f)]
    [InlineData(8, false, 0.21952620f)]
    [InlineData(8, true, 0.21952620f)]
    public void RawColorKeyPreservesTheUnmaskedFractionalColor(int bits, bool gray, float expected)
    {
        var renderer = Renderer(bits: bits, gray: gray);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        Assert.Equal(0f, frame.Pixels[3]);
        // Only the first raw sample matches. The visible sample's linear value
        // was independently calculated with 60-digit decimal sRGB arithmetic.
        AssertGray(frame, 1, expected);
    }

    [Fact]
    public void NearestMaskingPrecedesReversedDecodeAndHonorsClipAndOpacity()
    {
        var renderer = Renderer(width: 4, reverseDecode: true,
            content: "1 0 3 1 re W n /Half gs 4 0 0 1 0 0 cm /Image Do");

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(4, 1, transparentBackground: true));

        Assert.Equal(0f, frame.Pixels[3]);
        Assert.Equal(0f, frame.Pixels[4 + 3]);
        // Reversed Decode maps the unmasked raw 32769 to 32766/65535.
        AssertGray(frame, 2, 0.21401996f, 0.5f);
        AssertGray(frame, 3, 0.21401996f, 0.5f);
    }

    [Fact]
    public void RawMaskIntervalIncludesBothEndpoints()
    {
        var renderer = Renderer(width: 4, sourceWidth: 4, gray: true,
            samples: [127, 255, 128, 0, 128, 1, 128, 2], mask: [32768, 32769]);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(4, 1, transparentBackground: true));

        AssertGray(frame, 0, 0.21403408f);
        Assert.Equal(0f, frame.Pixels[4 + 3]);
        Assert.Equal(0f, frame.Pixels[8 + 3]);
        AssertGray(frame, 3, 0.21407645f);
    }

    [Fact]
    public void AllRgbComponentsMustMatchTheRawMask()
    {
        var renderer = Renderer(width: 4, sourceWidth: 4, samples: [
            128, 0, 128, 0, 128, 0, 128, 1, 128, 0, 128, 0,
            128, 0, 128, 1, 128, 0, 128, 0, 128, 0, 128, 1]);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(4, 1, transparentBackground: true));

        Assert.Equal(0f, frame.Pixels[3]);
        for (int pixel = 1; pixel < 4; pixel++)
        {
            Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
            for (int channel = 0; channel < 3; channel++)
            {
                float expected = channel == pixel - 1 ? 0.21406233f : 0.21404820f;
                Assert.InRange(frame.Pixels[pixel * 4 + channel], expected - 0.00000003f, expected + 0.00000003f);
            }
        }
    }

    [Theory]
    [InlineData(4, 1, 1, true)]
    [InlineData(4, 4, 2, false)]
    [InlineData(1, 1, 2, false)]
    public void KeyMasksWithRequestedOrAutomaticSmoothingOrAreaReductionRemainRejected(
        int width, int height, int sourceHeight, bool interpolate)
    {
        var renderer = Renderer(width: width, height: height, sourceHeight: sourceHeight, interpolate: interpolate);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(width, height)));
    }

    [Fact]
    public void CompatibilityRenderingKeepsItsRawKeyMaskAndBytes()
    {
        var renderer = Renderer();
        var options = new PdfRenderOptions(2, 1, transparentBackground: true);

        var page = renderer.Render(0, options);

        Assert.Empty(page.Diagnostics);
        Assert.Equal(0, page.Pixels.Span[3]);
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, page.Pixels.Slice(4, 4).ToArray());
        Assert.Equal(page.Pixels.ToArray(), renderer.RenderFrame(0, options).ToSrgbBgra32());
    }

    private static PdfPageRenderer Renderer(int width = 2, bool reverseDecode = false, string? content = null,
        int bits = 16, bool gray = false, int sourceWidth = 2, int sourceHeight = 1, int height = 1,
        byte[]? samples = null, int[]? mask = null, bool interpolate = false)
    {
        int components = gray ? 1 : 3;
        int rawKey = bits == 16 ? 32768 : 128;
        samples ??= Enumerable.Range(0, sourceHeight).SelectMany(_ => Enumerable.Range(0, sourceWidth)
            .SelectMany(pixel => Enumerable.Repeat(rawKey + pixel, components))
            .SelectMany(sample => bits == 16 ? new byte[] { (byte)(sample >> 8), (byte)sample } : [(byte)sample])).ToArray();
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(width, height,
            Encoding.ASCII.GetBytes(content ?? $"{width} 0 0 {height} 0 0 cm /Image Do")).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(sourceWidth)),
            Entry("Height", new PdfInteger(sourceHeight)), Entry("ColorSpace", Name(gray ? "DeviceGray" : "DeviceRGB")),
            Entry("BitsPerComponent", new PdfInteger(bits)), Entry("Interpolate", new PdfBoolean(interpolate)),
            Entry("Decode", Integers(Enumerable.Range(0, components * 2)
                .Select(index => reverseDecode ? 1 - index % 2 : index % 2).ToArray())),
            Entry("Mask", Integers(mask ?? Enumerable.Repeat(rawKey, components * 2).ToArray()))]), samples));
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

    private static PdfArray Integers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
