using System.Buffers.Binary;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfIccCurveTests
{
    [Fact]
    public void Inverse_RoundTripsGammaSampledAndParametricCurves()
    {
        PdfIccCurve[] curves = [new(Sampled()), new(Sampled(512)), new(Sampled(0, 16384, 65535)),
            new(Parametric(3, [2, 1, 0, 0.25, 0.25])),
            new(Parametric(4, [2, 1, 0, 0.25, 0.25, 0.125, 0.125]))];
        foreach (var curve in curves)
            foreach (double input in new double[] { 0.05, 0.2, 0.4, 0.75 })
                Assert.Equal(input, curve.Inverse(curve.Evaluate(input)), 10);
        Assert.Throws<NotSupportedException>(() => new PdfIccCurve(Sampled(0, 65535, 32768)).Inverse(0.5));
    }

    [Fact]
    public void Evaluate_HandlesIdentityGammaAndSampledCurves()
    {
        Assert.Equal(0.37, new PdfIccCurve(Sampled()).Evaluate(0.37));
        Assert.Equal(0.25, new PdfIccCurve(Sampled(512)).Evaluate(0.5));
        var sampled = new PdfIccCurve(Sampled(0, 16384, 65535));
        Assert.Equal(8192d / 65535, sampled.Evaluate(0.25), 12);
        Assert.Equal((16384d + 65535) / 2 / 65535, sampled.Evaluate(0.75), 12);
        Assert.Equal(0, sampled.Evaluate(-1));
        Assert.Equal(1, sampled.Evaluate(2));
    }

    [Theory]
    [InlineData(0, 0.25, 0.0625)]
    [InlineData(1, 0.25, 0)]
    [InlineData(1, 0.75, 0.25)]
    [InlineData(2, 0.25, 0.125)]
    [InlineData(2, 0.75, 0.375)]
    [InlineData(3, 0.25, 0.0625)]
    [InlineData(3, 0.75, 0.25)]
    [InlineData(4, 0.25, 0.1875)]
    [InlineData(4, 0.75, 0.375)]
    public void Evaluate_UsesEveryParametricBranch(int type, double input, double expected)
    {
        double[] parameters = type switch {
            0 => [2], 1 => [2, 2, -1], 2 => [2, 2, -1, 0.125],
            3 => [2, 2, -1, 0.25, 0.5], _ => [2, 2, -1, 0.25, 0.5, 0.125, 0.125] };
        Assert.Equal(expected, new PdfIccCurve(Parametric(type, parameters)).Evaluate(input), 12);
    }

    [Fact]
    public void Evaluate_UsesDistinctUpperAndLowerOffsetsAndClipsRange()
    {
        var curve = new PdfIccCurve(Parametric(4, [2, 2, -1, 0.25, 0.5, 0.25, 0.125]));
        Assert.Equal(0.1875, curve.Evaluate(0.25));
        Assert.Equal(0.5, curve.Evaluate(0.75));
        Assert.Equal(1, curve.Evaluate(1));
    }

    [Fact]
    public void Constructor_RejectsTruncationAndUndefinedParameters()
    {
        byte[] data = Parametric(4, [2, 2, -1, 0.25, 0.5, 0.25, 0.125]);
        for (int size = 0; size < data.Length; size++)
            Assert.Throws<FormatException>(() => new PdfIccCurve(data.AsMemory(0, size)));
        Assert.Throws<FormatException>(() => new PdfIccCurve(Sampled(0)));
        Assert.Throws<FormatException>(() => new PdfIccCurve(Parametric(1, [2, 0, 0])));
        Assert.Throws<FormatException>(() => new PdfIccCurve(Parametric(3, [2, 2, -1, 1, 0.25])));
    }

    private static byte[] Sampled(params ushort[] values)
    {
        byte[] data = new byte[12 + values.Length * 2];
        "curv"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)values.Length);
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12 + index * 2), values[index]);
        return data;
    }

    private static byte[] Parametric(int type, double[] values)
    {
        byte[] data = new byte[12 + values.Length * 4];
        "para"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8), (ushort)type);
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12 + index * 4), (int)Math.Round(values[index] * 65536));
        return data;
    }
}
