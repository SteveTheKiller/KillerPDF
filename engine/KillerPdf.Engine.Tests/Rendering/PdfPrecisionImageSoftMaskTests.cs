using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionImageSoftMaskTests
{
    [Theory]
    [InlineData(16, false, 16, 0.21404820f, 0.21406233f, 0.50000763f, 0.5000229f)]
    [InlineData(16, true, 16, 0.21404820f, 0.21406233f, 0.50000763f, 0.5000229f)]
    [InlineData(8, false, 16, 0.21586050f, 0.21952620f, 0.50000763f, 0.5000229f)]
    [InlineData(8, true, 16, 0.21586050f, 0.21952620f, 0.50000763f, 0.5000229f)]
    [InlineData(16, false, 8, 0.21404820f, 0.21406233f, 0.5019608f, 0.5058824f)]
    [InlineData(16, true, 8, 0.21404820f, 0.21406233f, 0.5019608f, 0.5058824f)]
    [InlineData(8, false, 8, 0.21586050f, 0.21952620f, 0.5019608f, 0.5058824f)]
    [InlineData(8, true, 8, 0.21586050f, 0.21952620f, 0.5019608f, 0.5058824f)]
    public void MatchingSoftMaskRetainsDistinctFractionalAlphaAndColor(int baseBits, bool gray,
        int maskBits, float firstColor, float secondColor, float firstAlpha, float secondAlpha)
    {
        var renderer = Renderer(baseBits: baseBits, gray: gray, maskBits: maskBits);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        // Independent raw alpha ratios round to these exact float32 values.
        // Color literals come from 60-digit decimal sRGB decoding of the source.
        AssertPixel(frame, 0, firstColor, firstAlpha);
        AssertPixel(frame, 1, secondColor, secondAlpha);
        Assert.NotEqual(frame.Pixels[3], frame.Pixels[7]);
    }

    [Fact]
    public void NearestSoftMaskHonorsReversedDecodeOpacityAndClip()
    {
        var renderer = Renderer(width: 4, reverseMaskDecode: true,
            content: "1 0 3 1 re W n /Half gs 4 0 0 1 0 0 cm /Image Do");

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(4, 1, transparentBackground: true));

        Assert.Equal(0f, frame.Pixels[3]);
        AssertPixel(frame, 1, 0.21404820f, 0.24999619f);
        AssertPixel(frame, 2, 0.21406233f, 0.24998856f);
        AssertPixel(frame, 3, 0.21406233f, 0.24998856f);
    }

    [Fact]
    public void ZeroMaskAlphaLeavesTheExistingBackdropUntouched()
    {
        var renderer = Renderer(maskSamples: [0, 0, 255, 255],
            content: "0.25 g 0 0 2 1 re f 2 0 0 1 0 0 cm /Image Do");

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        AssertPixel(frame, 0, 0.05087609f, 1f);
        AssertPixel(frame, 1, 0.21406233f, 1f);
    }

    [Fact]
    public void CompatibilityRenderCacheDoesNotQuantizeTheLaterPreciseMask()
    {
        var renderer = Renderer();
        var options = new PdfRenderOptions(2, 1, transparentBackground: true);

        var compatibility = renderer.Render(0, options);
        var frame = renderer.RenderFrame(0, options);

        Assert.Equal(128, compatibility.Pixels.Span[3]);
        Assert.Equal(128, compatibility.Pixels.Span[7]);
        AssertPixel(frame, 0, 0.21404820f, 0.50000763f);
        AssertPixel(frame, 1, 0.21406233f, 0.5000229f);
        Assert.Equal(compatibility.Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalMaskDimensionMismatchIsRejectedBeforeMaskSampleDecode(bool truncated)
    {
        var renderer = Renderer(maskWidth: 3, maskSamples: truncated ? [] : null);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 2)]
    public void MaskDecodeOutsideTheUnitRangeIsRejected(double minimum, double maximum)
    {
        var renderer = Renderer(maskDecode: [minimum, maximum]);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Theory]
    [InlineData(false, true)]
    public void MatteAndNonDeviceGrayMaskRecoveryRemainRejected(bool matte, bool calibrated)
    {
        var renderer = Renderer(matte: matte, calibratedMask: calibrated);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void UnportedPackedMaskDepthsRemainRejected(int bits)
    {
        var renderer = Renderer(maskBits: bits);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Theory]
    [InlineData(4, 1, 1, true)]
    [InlineData(4, 4, 2, false)]
    [InlineData(1, 1, 2, false)]
    public void OpaqueMasksDoNotBypassSmoothingAndReductionRejections(int width, int height,
        int sourceHeight, bool interpolate)
    {
        var renderer = Renderer(width: width, height: height, sourceHeight: sourceHeight, interpolate: interpolate,
            maskBits: 8, maskSamples: Enumerable.Repeat((byte)255, 2 * sourceHeight).ToArray());

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(width, height)));
    }

    [Fact]
    public void StencilFlagOnTheSoftMaskRemainsRejected()
    {
        var renderer = Renderer(maskStencil: true);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Fact]
    public void CanceledSoftMaskRenderingStopsAtThePublicBoundary()
    {
        var renderer = Renderer();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => renderer.RenderFrame(0,
            new PdfRenderOptions(2, 1), cancellation.Token));
    }

    private static PdfPageRenderer Renderer(int width = 2, bool reverseMaskDecode = false, string? content = null,
        int maskBits = 16, int baseBits = 16, bool gray = false, int height = 1, int sourceHeight = 1,
        int? maskWidth = null, byte[]? maskSamples = null, double[]? maskDecode = null,
        bool matte = false, bool calibratedMask = false, bool interpolate = false, bool maskStencil = false)
    {
        int maskColumns = maskWidth ?? 2;
        maskSamples ??= maskBits is 8 or 16
            ? Enumerable.Range(0, sourceHeight).SelectMany(_ => Enumerable.Range(0, maskColumns)
                .SelectMany(x => maskBits == 16 ? new byte[] { 128, (byte)x } : [(byte)(128 + x)])).ToArray()
            : new byte[((maskColumns * maskBits + 7) / 8) * sourceHeight];
        byte[] sourceSamples = Enumerable.Range(0, sourceHeight).SelectMany(_ => Enumerable.Range(0, 2)
            .SelectMany(x => Enumerable.Range(0, gray ? 1 : 3)
                .SelectMany(_ => baseBits == 16 ? new byte[] { 128, (byte)x } : [(byte)(128 + x)]))).ToArray();
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(width, height,
            Encoding.ASCII.GetBytes(content ?? $"{width} 0 0 {height} 0 0 cm /Image Do")).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfObject maskSpace = calibratedMask
            ? new PdfArray([Name("CalGray"), new PdfDictionary([Entry("WhitePoint",
                new PdfArray([new PdfReal(0.95047), new PdfInteger(1), new PdfReal(1.08883)]))])])
            : Name("DeviceGray");
        var maskEntries = new List<KeyValuePair<PdfName, PdfObject>> {
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(maskColumns)),
            Entry("Height", new PdfInteger(sourceHeight)), Entry("ColorSpace", maskSpace),
            Entry("BitsPerComponent", new PdfInteger(maskBits)),
            Entry("Decode", new PdfArray((maskDecode ?? (reverseMaskDecode ? [1d, 0d] : [0d, 1d]))
                .Select(value => (PdfObject)new PdfReal(value))))
        };
        if (matte) maskEntries.Add(Entry("Matte", Integers(0, 0, 0)));
        if (maskStencil) maskEntries.Add(Entry("ImageMask", new PdfBoolean(true)));
        var mask = update.AddObject(new PdfStream(new PdfDictionary(maskEntries), maskSamples));
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)),
            Entry("Height", new PdfInteger(sourceHeight)), Entry("ColorSpace", Name(gray ? "DeviceGray" : "DeviceRGB")),
            Entry("BitsPerComponent", new PdfInteger(baseBits)), Entry("Interpolate", new PdfBoolean(interpolate)),
            Entry("SMask", mask)]), sourceSamples));
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

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static PdfArray Integers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
