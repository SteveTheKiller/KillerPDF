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
        byte[] samples = Enumerable.Range(0, rowBytes * height).Select(index => (byte)(index * 37 + 91)).ToArray();
        for (int y = 0; y < outputHeight; y++)
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
        }
    }

    [Fact]
    public void HalfCoverageUsesEvenRoundingForEitherPolarity()
    {
        Assert.Equal(0xFF808080u, PdfBinaryAreaSampler.Sample([0x80], 1, 2, 1,
            0, 0, 1, 1, 0xFF000000, 0xFFFFFFFF, default));
        Assert.Equal(0xFF808080u, PdfBinaryAreaSampler.Sample([0x80], 1, 2, 1,
            0, 0, 1, 1, 0xFFFFFFFF, 0xFF000000, default));
    }
}
