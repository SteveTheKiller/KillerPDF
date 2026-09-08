using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfCalibratedColorTransformTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoundTrip_PreservesSamplesAcrossWhitePointAdaptation(bool gray)
    {
        var transform = Create(gray);
        double[] samples = gray ? [0.37] : [0.19, 0.47, 0.83];
        Span<double> xyz = stackalloc double[3];
        Span<double> result = stackalloc double[samples.Length];
        transform.ToXyz(samples, xyz);
        transform.FromXyz(xyz, result);
        for (int channel = 0; channel < samples.Length; channel++)
            Assert.InRange(Math.Abs(result[channel] - samples[channel]), 0, 1e-12);
        if (!gray)
        {
            transform.ToXyz(samples, samples);
            transform.FromXyz(samples, samples);
            Assert.InRange(Math.Abs(samples[0] - 0.19), 0, 1e-12);
            Assert.InRange(Math.Abs(samples[1] - 0.47), 0, 1e-12);
            Assert.InRange(Math.Abs(samples[2] - 0.83), 0, 1e-12);
        }
    }

    [Fact]
    public void GrayWhite_MapsToD50ConnectionWhite()
    {
        Span<double> xyz = stackalloc double[3];
        Create(true).ToXyz([1], xyz);
        Assert.InRange(Math.Abs(xyz[0] - 0.9642), 0, 1e-6);
        Assert.InRange(Math.Abs(xyz[1] - 1), 0, 1e-6);
        Assert.InRange(Math.Abs(xyz[2] - 0.8249), 0, 1e-6);
    }

    [Fact]
    public void SingularMatrix_RejectsReverseBeforeWriting()
    {
        var transform = new PdfCalibratedColorTransform([0.9642, 1, 0.8249], [1, 1, 1], new double[9]);
        Assert.False(transform.CanConvertFromXyz);
        double[] result = [7, 8, 9];
        Assert.Throws<NotSupportedException>(() => transform.FromXyz([0.2, 0.3, 0.4], result));
        Assert.Equal(new double[] { 7, 8, 9 }, result);
    }

    [Fact]
    public void Conversion_DoesNotAllocatePerSample()
    {
        var transform = Create(false);
        ReadOnlySpan<double> input = stackalloc double[3] { 0.2, 0.4, 0.8 };
        Span<double> xyz = stackalloc double[3];
        Span<double> samples = stackalloc double[3];
        for (int index = 0; index < 100; index++)
        {
            transform.ToXyz(input, xyz);
            transform.FromXyz(xyz, samples);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++)
        {
            transform.ToXyz(input, xyz);
            transform.FromXyz(xyz, samples);
        }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    private static PdfCalibratedColorTransform Create(bool gray) => gray
        ? new([0.95047, 1, 1.08883], [2.2])
        : new([0.95047, 1, 1.08883], [1.8, 2.2, 2.4],
            [0.4124564, 0.2126729, 0.0193339, 0.3575761, 0.7151522, 0.119192,
                0.1804375, 0.072175, 0.9503041]);
}
