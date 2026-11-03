using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionImageSamplingTests
{
    [Fact]
    public void NearestEnlargementRetainsAdjacentSixteenBitSamples()
    {
        var renderer = Renderer();

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(4, 1, transparentBackground: true));

        // Original samples 32768/65535 and 32769/65535 decode to these frozen
        // linear values, independently calculated with 60-digit decimal arithmetic.
        AssertGray(frame, 0, 0.21404820f);
        AssertGray(frame, 1, 0.21404820f);
        AssertGray(frame, 2, 0.21406233f);
        AssertGray(frame, 3, 0.21406233f);
        Assert.True(frame.Pixels[8] > frame.Pixels[0]);
    }

    [Fact]
    public void EnlargedSamplesHonorDecodeClipAndOpacity()
    {
        var renderer = Renderer(content: "1 0 2 1 re W n /Half gs 4 0 0 1 0 0 cm /Image Do",
            reverseDecode: true);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(4, 1, transparentBackground: true));

        Assert.Equal(0f, frame.Pixels[3]);
        // Reversed Decode maps the source samples to 32767/65535 and 32766/65535.
        AssertGray(frame, 1, 0.21403408f, 0.5f);
        AssertGray(frame, 2, 0.21401996f, 0.5f);
        Assert.Equal(0f, frame.Pixels[3 * 4 + 3]);
    }

    [Fact]
    public void NearestGrayEnlargementRetainsAdjacentSixteenBitSamples()
    {
        var renderer = Renderer(gray: true);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(4, 1, transparentBackground: true));

        AssertGray(frame, 0, 0.21404820f);
        AssertGray(frame, 1, 0.21404820f);
        AssertGray(frame, 2, 0.21406233f);
        AssertGray(frame, 3, 0.21406233f);
    }

    [Fact]
    public void ReductionThatNeedsAreaSamplingIsRejected()
    {
        var renderer = Renderer(content: "1 0 0 1 0 0 cm /Image Do", pageWidth: 1);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(1, 1)));
    }

    [Theory]
    [InlineData("4 0 0 1 0.5 0 cm /Image Do")]
    [InlineData("4 0 1 1 0 0 cm /Image Do")]
    public void UnportedScaledImageEdgesAndShearAreRejected(string content)
    {
        var renderer = Renderer(content: content);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(4, 1)));
    }

    [Fact]
    public void CompatibilityRenderingKeepsItsByteSamples()
    {
        var renderer = Renderer();
        var options = new PdfRenderOptions(4, 1, transparentBackground: true);

        var page = renderer.Render(0, options);

        Assert.Empty(page.Diagnostics);
        Assert.Equal(new byte[] { 128, 128, 128, 255, 128, 128, 128, 255,
            128, 128, 128, 255, 128, 128, 128, 255 }, page.Pixels.ToArray());
        Assert.Equal(page.Pixels.ToArray(), renderer.RenderFrame(0, options).ToSrgbBgra32());
    }

    private static PdfPageRenderer Renderer(string content = "4 0 0 1 0 0 cm /Image Do",
        bool reverseDecode = false, bool interpolate = false, int sourceHeight = 1,
        int pageWidth = 4, int pageHeight = 1, bool gray = false)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(pageWidth, pageHeight,
            Encoding.ASCII.GetBytes(content)).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        byte[] row = gray ? [128, 0, 128, 1] : [128, 0, 128, 0, 128, 0, 128, 1, 128, 1, 128, 1];
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)),
            Entry("Height", new PdfInteger(sourceHeight)), Entry("ColorSpace", Name(gray ? "DeviceGray" : "DeviceRGB")),
            Entry("BitsPerComponent", new PdfInteger(16)), Entry("Interpolate", new PdfBoolean(interpolate)),
            Entry("Decode", new PdfArray(Enumerable.Range(0, gray ? 2 : 6).Select(index =>
                (PdfObject)new PdfInteger(reverseDecode ? 1 - index % 2 : index % 2))))]),
            Enumerable.Range(0, sourceHeight).SelectMany(_ => row).ToArray()));
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

    private static void AssertGray(PdfRenderedFrame frame, int x, float expected, float alpha = 1f)
    {
        int offset = x * 4;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[offset + channel], expected - 0.00000003f, expected + 0.00000003f);
        Assert.Equal(alpha, frame.Pixels[offset + 3]);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
