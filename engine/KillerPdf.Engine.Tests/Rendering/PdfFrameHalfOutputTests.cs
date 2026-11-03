using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfFrameHalfOutputTests
{
    [Fact]
    public void HalfOutputRetainsAdjacentActualEngineGrayPatches()
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            Encoding.ASCII.GetBytes(
                "0.5 g 0 0 1 1 re f 0.5001 0.5001 0.5001 rg 1 0 1 1 re f")).Build());
        var frame = new PdfPageRenderer(document).RenderFrame(0, new PdfRenderOptions(2, 1));
        float[] original = frame.Pixels.ToArray();

        Half[] output = frame.ToPremultipliedLinearHalfRgba();

        // Independent IEEE binary16 round-to-nearest-even encoding of decoded
        // sRGB .5 and .5001: linear values approximately .21404114/.21413372.
        // Python struct.pack('>e', value) independently gives 32D9 and 32DA.
        Assert.Equal(new ushort[] { 0x32D9, 0x32D9, 0x32D9, 0x3C00,
            0x32DA, 0x32DA, 0x32DA, 0x3C00 }, Bits(output));
        Assert.Equal(original, frame.Pixels.ToArray());
    }

    [Fact]
    public void HalfOutputScalesWhiteAndPremultipliesRgbOnceWithoutScalingAlpha()
    {
        var frame = new PdfRenderedFrame(1, 1, [1f, 2f, 4f, 0.25f], PdfFrameColorEncoding.LinearSrgbD65);
        Half[] output = frame.ToPremultipliedLinearHalfRgba(referenceWhiteScale: 2);
        Assert.Equal(new ushort[] { 0x3800, 0x3C00, 0x4000, 0x3400 }, Bits(output));
        Assert.Equal(new float[] { 1f, 2f, 4f, 0.25f }, frame.Pixels.ToArray());
        output[0] = (Half)99;
        Assert.Equal(new ushort[] { 0x3800, 0x3C00, 0x4000, 0x3400 },
            Bits(frame.ToPremultipliedLinearHalfRgba(referenceWhiteScale: 2)));
    }

    [Fact]
    public void HalfOutputRetainsExtendedRgbAndWritesCanonicalZeroAtExactZeroAlpha()
    {
        var frame = new PdfRenderedFrame(2, 1,
            [-0.25f, 2f, 1f, 0.5f, float.MaxValue, -float.MaxValue, 1f, 0f],
            PdfFrameColorEncoding.LinearSrgbD65);
        Assert.Equal(new ushort[] { 0xB000, 0x3C00, 0x3800, 0x3800, 0, 0, 0, 0 },
            Bits(frame.ToPremultipliedLinearHalfRgba()));
        Assert.Equal(-float.MaxValue, frame.Pixels[5]);
    }

    [Theory]
    [InlineData(65520f, 1d)]
    [InlineData(-65520f, 1d)]
    [InlineData(1f, double.MaxValue)]
    public void HalfOutputRejectsOverflowInsteadOfReturningNonfiniteChannels(float red, double scale)
    {
        var frame = new PdfRenderedFrame(1, 1, [red, 0f, 0f, 1f], PdfFrameColorEncoding.LinearSrgbD65);
        Assert.Throws<NotSupportedException>(() => frame.ToPremultipliedLinearHalfRgba(scale));
    }

    [Fact]
    public void HalfOutputRejectsLostPositiveAlphaWhenPremultipliedColorRemainsMeaningful()
    {
        var frame = new PdfRenderedFrame(1, 1, [1e12f, 0f, 0f, 1e-12f],
            PdfFrameColorEncoding.LinearSrgbD65);
        // Alpha rounds to binary16 zero while premultiplied red is about one.
        Assert.Throws<NotSupportedException>(() => frame.ToPremultipliedLinearHalfRgba());
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(23L)]
    [InlineData(536870913L)]
    public void HalfOutputEnforcesCombinedInputAndOutputPixelBudget(long maximumCombinedPixelBytes)
    {
        var frame = new PdfRenderedFrame(1, 1, [0f, 0f, 0f, 1f], PdfFrameColorEncoding.LinearSrgbD65);
        // One RGBA32F input pixel plus one RGBA16F output pixel needs 24 bytes.
        Assert.ThrowsAny<ArgumentException>(() =>
            frame.ToPremultipliedLinearHalfRgba(maximumCombinedPixelBytes: maximumCombinedPixelBytes));
    }

    [Fact]
    public void HalfOutputRequiresLinearEncodingAndFinitePositiveReferenceWhite()
    {
        var encoded = new PdfRenderedFrame(1, 1, [1f, 0f, 0f, 1f], PdfFrameColorEncoding.SrgbD65);
        Assert.Throws<NotSupportedException>(() => encoded.ToPremultipliedLinearHalfRgba());
        var linear = new PdfRenderedFrame(1, 1, [1f, 0f, 0f, 1f], PdfFrameColorEncoding.LinearSrgbD65);
        foreach (double scale in new[] { -1d, 0d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => linear.ToPremultipliedLinearHalfRgba(scale));
    }

    [Fact]
    public void HalfOutputHonorsCancellationBeforeAllocatingOutput()
    {
        var frame = new PdfRenderedFrame(1024, 128, new float[1024 * 128 * 4],
            PdfFrameColorEncoding.LinearSrgbD65);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<OperationCanceledException>(() =>
            frame.ToPremultipliedLinearHalfRgba(cancellationToken: cancellation.Token));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 256 * 1024, $"Canceled output allocated {allocated} bytes.");
    }

    [Fact]
    public void HalfOutputRoundsOriginalAlphaOnlyAfterPremultiplication()
    {
        var frame = new PdfRenderedFrame(1, 1, [3f, 0f, 0f, 0.1f], PdfFrameColorEncoding.LinearSrgbD65);
        // Original float alpha gives red bits 34CD. Premultiplying by alpha
        // rounded first to Half would give 34CC, an independently checked result.
        Assert.Equal(new ushort[] { 0x34CD, 0, 0, 0x2E66 }, Bits(frame.ToPremultipliedLinearHalfRgba()));
    }

    [Fact]
    public void HalfOutputAcceptsTinyAlphaOnlyWhenAllRoundedColorIsZero()
    {
        var frame = new PdfRenderedFrame(1, 1, [1f, 1f, 1f, 1e-12f], PdfFrameColorEncoding.LinearSrgbD65);
        Assert.Equal(new ushort[] { 0, 0, 0, 0 }, Bits(frame.ToPremultipliedLinearHalfRgba()));
    }

    [Fact]
    public void HalfOutputSkipsHugeTransparentColorEvenWithHugeFiniteWhiteScale()
    {
        var frame = new PdfRenderedFrame(1, 1, [float.MaxValue, -float.MaxValue, 1f, 0f],
            PdfFrameColorEncoding.LinearSrgbD65);
        Assert.Equal(new ushort[] { 0, 0, 0, 0 },
            Bits(frame.ToPremultipliedLinearHalfRgba(double.MaxValue)));
    }

    [Fact]
    public void HalfOutputAllowsFiniteHalfLimitsAfterPremultiplyingExtendedSource()
    {
        var frame = new PdfRenderedFrame(1, 1, [131008f, -131008f, 0f, 0.5f],
            PdfFrameColorEncoding.LinearSrgbD65);
        Assert.Equal(new ushort[] { 0x7BFF, 0xFBFF, 0, 0x3800 },
            Bits(frame.ToPremultipliedLinearHalfRgba()));
    }

    [Fact]
    public void HalfOutputKeepsTopLeftRgbaRowOrderWithExactCombinedBudget()
    {
        var frame = new PdfRenderedFrame(1, 2, [1f, 0f, 0f, 1f, 0f, 1f, 0f, 1f],
            PdfFrameColorEncoding.LinearSrgbD65);
        Assert.Equal(new ushort[] { 0x3C00, 0, 0, 0x3C00, 0, 0x3C00, 0, 0x3C00 },
            Bits(frame.ToPremultipliedLinearHalfRgba(maximumCombinedPixelBytes: 48)));
    }

    [Fact]
    public void HalfOutputRejectsInsufficientBudgetBeforeAllocatingThePixelArray()
    {
        var frame = new PdfRenderedFrame(1024, 128, new float[1024 * 128 * 4],
            PdfFrameColorEncoding.LinearSrgbD65);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ArgumentException>(() => frame.ToPremultipliedLinearHalfRgba(
            maximumCombinedPixelBytes: 1024L * 128 * 16));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 256 * 1024, $"Rejected output allocated {allocated} bytes.");
    }

    private static ushort[] Bits(Half[] values) => values.Select(BitConverter.HalfToUInt16Bits).ToArray();
}
