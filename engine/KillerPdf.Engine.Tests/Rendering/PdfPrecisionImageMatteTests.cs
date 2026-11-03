using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionImageMatteTests
{
    [Theory]
    [InlineData(16, 16, false, 0.21406938f, 0.50000763f)]
    [InlineData(16, 16, true, 0.21406938f, 0.50000763f)]
    [InlineData(8, 8, false, 0.22134365f, 0.5019608f)]
    [InlineData(8, 8, true, 0.22134365f, 0.5019608f)]
    [InlineData(16, 8, false, 0.21587458f, 0.5019608f)]
    [InlineData(16, 8, true, 0.21587458f, 0.5019608f)]
    [InlineData(8, 16, false, 0.21953328f, 0.50000763f)]
    [InlineData(8, 16, true, 0.21953328f, 0.50000763f)]
    public void MatteUsesTheOriginalFractionalMaskAlpha(int bits, int maskBits, bool gray,
        float expectedColor, float expectedAlpha)
    {
        var renderer = Renderer(bits: bits, maskBits: maskBits, gray: gray);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(1, 1, transparentBackground: true));

        // Unmatting source 49152/65535 against white with alpha 32768/65535
        // gives encoded 16385/32768. Independent Decimal60 sRGB decoding gives
        // 0.214069388130448621582559531860155607891870046007321521075000.
        AssertPixel(frame, 0, expectedColor, expectedAlpha);
    }

    [Fact]
    public void ReversedMaskDecodeUsesRawAlphaBeforeGraphicsOpacityAndClip()
    {
        var renderer = Renderer(width: 2, reverseMaskDecode: true,
            content: "1 0 1 1 re W n /Half gs 2 0 0 1 0 0 cm /Image Do");

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        Assert.Equal(0f, frame.Pixels[3]);
        // The original mask alpha is 32767/65535. Unmatting gives encoded
        // 16384/32767; graphics opacity 0.5 only changes the final alpha.
        AssertPixel(frame, 1, 0.21405526f, 0.24999619f);
    }

    [Fact]
    public void ZeroMaskAlphaLeavesTheBackdropUntouchedWithoutUnmatting()
    {
        var renderer = Renderer(maskSample: 0,
            content: "0.25 g 0 0 1 1 re f 1 0 0 1 0 0 cm /Image Do");

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(1, 1, transparentBackground: true));

        AssertPixel(frame, 0, 0.05087609f, 1f);
    }

    [Theory]
    [InlineData(65535, 0, 1f)]
    [InlineData(0, 1, 0f)]
    [InlineData(65534, 1, 0f)]
    public void SmallMaskAlphaKeepsRecoveryFiniteAndClamped(int source, int matteValue, float expected)
    {
        var renderer = Renderer(sourceSample: source, maskSample: 1,
            matte: Numbers(matteValue, matteValue, matteValue));

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(1, 1, transparentBackground: true));

        AssertPixel(frame, 0, expected, 0.000015259022f);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void MatteOutsideTheDeviceRangeIsRejected(double value)
    {
        var renderer = Renderer(matte: Numbers(value, value, value));

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(1, 1)));
    }

    [Fact]
    public void InvalidMatteCannotHideBehindZeroMaskAlpha()
    {
        var renderer = Renderer(maskSample: 0, matte: Numbers(-0.01, 0, 0));

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(1, 1)));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 3)]
    public void MatteShapeMustMatchTheActualSourceComponentCount(bool gray, int count)
    {
        var renderer = Renderer(gray: gray, matte: Numbers(Enumerable.Repeat(1d, count).ToArray()));

        Assert.Throws<FormatException>(() => renderer.RenderFrame(0, new PdfRenderOptions(1, 1)));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void NonNumericMatteEntriesAreRejected(string token)
    {
        var renderer = Renderer(matte: new PdfArray([Name(token), new PdfInteger(1), new PdfInteger(1)]));

        Assert.Throws<FormatException>(() => renderer.RenderFrame(0, new PdfRenderOptions(1, 1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatteImagesWithSmoothingOrAreaReductionRemainRejected(bool reduction)
    {
        int output = reduction ? 1 : 2;
        var renderer = Renderer(width: output, height: output, sourceSize: reduction ? 2 : 1,
            interpolate: !reduction);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(output, output)));
    }

    [Fact]
    public void CompatibilityCacheKeepsRawMatteInputsForThePreciseRender()
    {
        var renderer = Renderer();
        var options = new PdfRenderOptions(1, 1, transparentBackground: true);

        var compatibility = renderer.Render(0, options);
        var frame = renderer.RenderFrame(0, options);

        Assert.Equal(new byte[] { 128, 128, 128, 128 }, compatibility.Pixels.ToArray());
        AssertPixel(frame, 0, 0.21406938f, 0.50000763f);
        Assert.Equal(compatibility.Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    private static PdfPageRenderer Renderer(int width = 1, bool reverseMaskDecode = false, string? content = null,
        int bits = 16, int? maskBits = null, bool gray = false, int? sourceSample = null, int? maskSample = null,
        PdfObject? matte = null, int height = 1, int sourceSize = 1, bool interpolate = false)
    {
        int maskDepth = maskBits ?? bits;
        int rawSource = sourceSample ?? (bits == 16 ? 49152 : 192);
        int rawMask = maskSample ?? (maskDepth == 16 ? 32768 : 128);
        byte[] maskSamples = Enumerable.Range(0, sourceSize * sourceSize)
            .SelectMany(_ => maskDepth == 16 ? new byte[] { (byte)(rawMask >> 8), (byte)rawMask } : [(byte)rawMask]).ToArray();
        byte[] sourceSamples = Enumerable.Range(0, sourceSize * sourceSize * (gray ? 1 : 3))
            .SelectMany(_ => bits == 16 ? new byte[] { (byte)(rawSource >> 8), (byte)rawSource } : [(byte)rawSource]).ToArray();
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(width, height,
            Encoding.ASCII.GetBytes(content ?? $"{width} 0 0 {height} 0 0 cm /Image Do")).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        var mask = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(sourceSize)),
            Entry("Height", new PdfInteger(sourceSize)), Entry("ColorSpace", Name("DeviceGray")),
            Entry("BitsPerComponent", new PdfInteger(maskDepth)),
            Entry("Matte", matte ?? Numbers(Enumerable.Repeat(1d, gray ? 1 : 3).ToArray())),
            Entry("Decode", Numbers(reverseMaskDecode ? [1, 0] : [0, 1]))]), maskSamples));
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(sourceSize)),
            Entry("Height", new PdfInteger(sourceSize)), Entry("ColorSpace", Name(gray ? "DeviceGray" : "DeviceRGB")),
            Entry("BitsPerComponent", new PdfInteger(bits)), Entry("SMask", mask),
            Entry("Interpolate", new PdfBoolean(interpolate))]), sourceSamples));
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

    private static void AssertPixel(PdfRenderedFrame frame, int pixel, float color, float alpha)
    {
        int offset = pixel * 4;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[offset + channel], color - 0.00000003f, color + 0.00000003f);
        Assert.Equal(alpha, frame.Pixels[offset + 3]);
    }

    private static PdfArray Numbers(params double[] values) => new(values.Select(value => (PdfObject)new PdfReal(value)));
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
