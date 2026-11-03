using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfRenderedFrameTests
{
    [Fact]
    public void CompatibilityOutputHonorsCancellation()
    {
        var frame = new PdfRenderedFrame(1, 1, [0f, 0f, 0f, 1f],
            PdfFrameColorEncoding.LinearSrgbD65);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => frame.ToSrgbBgra32(cancellation.Token));
    }

    [Theory]
    [InlineData(PdfFrameColorEncoding.LinearSrgbD65, 0.21404114f, 0.0031308f)]
    [InlineData(PdfFrameColorEncoding.SrgbD65, 0.5f, 0.04045f)]
    public void CompatibilityOutputEncodesSrgbOnlyAtTheByteBoundary(
        PdfFrameColorEncoding encoding, float red, float green)
    {
        var frame = new PdfRenderedFrame(2, 1,
            [red, green, 1.2f, 0.5f, -0.125f, 0f, 0f, 0f], encoding);

        Assert.Equal(new byte[] { 255, 10, 128, 128, 0, 0, 0, 0 }, frame.ToSrgbBgra32());
        Assert.Equal(1.2f, frame.Pixels[2]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void FloatFrameRejectsUnknownColorEncoding(int encoding)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PdfRenderedFrame(1, 1, [0f, 0f, 0f, 1f], (PdfFrameColorEncoding)encoding));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(536870913)]
    public void FloatFrameEnforcesItsStorageBudget(long maximumPixelBytes)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new PdfRenderedFrame(1, 1, [0f, 0f, 0f, 1f],
                PdfFrameColorEncoding.LinearSrgbD65, maximumPixelBytes));
    }

    [Fact]
    public void FloatFrameDescribesOwnedPixelsForPresentation()
    {
        float[] input = [0.1f, 0.2f, 0.3f, 0f, 0.4f, 0.5f, 0.6f, 1f];
        var frame = new PdfRenderedFrame(1, 2, input, PdfFrameColorEncoding.LinearSrgbD65);
        input[0] = 100f;

        Assert.Equal(1, frame.Width);
        Assert.Equal(2, frame.Height);
        Assert.Equal(16, frame.StrideBytes);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(0.1f, frame.Pixels[0]);
        Assert.Equal(0f, frame.Pixels[3]);
    }

    [Theory]
    [InlineData(0, float.NaN)]
    [InlineData(1, float.PositiveInfinity)]
    [InlineData(2, float.NegativeInfinity)]
    [InlineData(3, float.NaN)]
    [InlineData(3, -0.001f)]
    [InlineData(3, 1.001f)]
    public void FloatFrameRejectsInvalidChannelValues(int channel, float value)
    {
        float[] pixel = [0.5f, 0.5f, 0.5f, 1f];
        pixel[channel] = value;
        Assert.Throws<ArgumentException>(() =>
            new PdfRenderedFrame(1, 1, pixel, PdfFrameColorEncoding.LinearSrgbD65));
    }

    [Theory]
    [InlineData(0, 1, 4)]
    [InlineData(1, -1, 4)]
    [InlineData(1, 1, 3)]
    [InlineData(1, 1, 8)]
    [InlineData(int.MaxValue, int.MaxValue, 4)]
    public void FloatFrameRejectsInvalidRasterLayout(int width, int height, int channelCount)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new PdfRenderedFrame(width, height, new float[channelCount],
                PdfFrameColorEncoding.LinearSrgbD65));
    }

    [Fact]
    public void FloatFramePreservesExtendedColorAndSubByteDifferences()
    {
        var frame = new PdfRenderedFrame(2, 1,
            [-0.125f, 1.25f, 0.50001f, 1f, -0.125f, 1.25f, 0.50002f, 0.5f],
            PdfFrameColorEncoding.LinearSrgbD65);

        Assert.Equal(new float[]
        {
            -0.125f, 1.25f, 0.50001f, 1f, -0.125f, 1.25f, 0.50002f, 0.5f
        }, frame.Pixels.ToArray());
    }
}
