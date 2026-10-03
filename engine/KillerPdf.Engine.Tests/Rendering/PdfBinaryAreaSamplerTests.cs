using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfBinaryAreaSamplerTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 3)]
    [InlineData(19, 7)]
    [InlineData(127, 3)]
    [InlineData(256, 3)]
    public void PackedCountsMatchExactCoverageGrid(int outputWidth, int outputHeight)
    {
        const int width = 257, height = 19, rowBytes = 33;
        const uint zero = 0x12EF34CD, one = 0xFE019876;
        byte[] samples = [.. Enumerable.Range(0, rowBytes * height).Select(index => (byte)(index * 37 + 91))];
        PdfBinaryAreaSampler.Column[] columns = PdfBinaryAreaSampler.CreateColumns(width, outputWidth);
        long[] coverage = new long[outputWidth];
        uint[] rowColors = new uint[outputWidth];
        for (int y = 0; y < outputHeight; y++)
        {
            PdfBinaryAreaSampler.Row row = PdfBinaryAreaSampler.Row.Create(y, height, outputHeight);
            PdfBinaryAreaSampler.SampleRow(samples, rowBytes, width, height, row, columns,
                outputHeight, zero, one, coverage, rowColors, default);
            for (int x = 0; x < outputWidth; x++)
            {
                // Expand each footprint onto an exact integer coverage grid as a scalar oracle.
                int selected = 0;
                for (int gy = y * height; gy < (y + 1) * height; gy++)
                for (int gx = x * width; gx < (x + 1) * width; gx++)
                {
                    int sx = gx / outputWidth, sy = gy / outputHeight;
                    selected += (samples[sy * rowBytes + sx / 8] >> (7 - sx % 8)) & 1;
                }
                uint expected = 0;
                for (int shift = 0; shift < 32; shift += 8)
                {
                    decimal numerator = (byte)(zero >> shift) * (width * height - selected)
                        + (byte)(one >> shift) * selected;
                    expected |= (uint)Math.Round(numerator / (width * height)) << shift;
                }
                Assert.Equal(expected, PdfBinaryAreaSampler.Sample(samples, rowBytes, width, height,
                    x, y, outputWidth, outputHeight, zero, one, default));
                Assert.Equal(expected, PdfBinaryAreaSampler.Sample(samples, rowBytes, width, height,
                    columns[x], row, outputWidth, outputHeight, zero, one, default));
                Assert.Equal(expected, rowColors[x]);
            }
        }
    }

    [Fact]
    public void HalfCoverageUsesEvenRoundingForEitherPolarity()
    {
        Assert.Equal(0xFF808080u, PdfBinaryAreaSampler.Sample([0x80], 1, 2, 1,
            0, 0, 1, 1, 0xFF000000, 0xFFFFFFFF, default));
        Assert.Equal(0xFF808080u, PdfBinaryAreaSampler.Sample([0x80], 1, 2, 1,
            0, 0, 1, 1, 0xFFFFFFFF, 0xFF000000, default));
        PdfBinaryAreaSampler.Column[] columns = PdfBinaryAreaSampler.CreateColumns(2, 1);
        PdfBinaryAreaSampler.Row row = PdfBinaryAreaSampler.Row.Create(0, 1, 1);
        long[] coverage = new long[1];
        uint[] result = new uint[1];
        PdfBinaryAreaSampler.SampleRow([0x80], 1, 2, 1, row, columns, 1,
            0xFF000000, 0xFFFFFFFF, coverage, result, default);
        Assert.Equal(0xFF808080u, result[0]);
        PdfBinaryAreaSampler.SampleRow([0x80], 1, 2, 1, row, columns, 1,
            0xFFFFFFFF, 0xFF000000, coverage, result, default);
        Assert.Equal(0xFF808080u, result[0]);
    }

    [Fact]
    public void CanceledTokenStopsBeforeSampling()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() => PdfBinaryAreaSampler.Sample([0x80], 1, 1, 1,
            0, 0, 1, 1, 0xFF000000, 0xFFFFFFFF, source.Token));
        PdfBinaryAreaSampler.Column[] columns = PdfBinaryAreaSampler.CreateColumns(1, 1);
        PdfBinaryAreaSampler.Row row = PdfBinaryAreaSampler.Row.Create(0, 1, 1);
        long[] coverage = [42];
        uint[] destination = [0xDEADBEEFu];
        Assert.Throws<OperationCanceledException>(() => PdfBinaryAreaSampler.SampleRow([0x80], 1, 1, 1,
            row, columns, 1, 0xFF000000, 0xFFFFFFFF, coverage, destination, source.Token));
        Assert.Equal(42, coverage[0]);
        Assert.Equal(0xDEADBEEFu, destination[0]);
    }
}
