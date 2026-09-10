using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfInkBlendTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(double.Epsilon)]
    [InlineData(1e-100)]
    [InlineData(0.00392156862745098)]
    [InlineData(0.123456789)]
    [InlineData(0.5)]
    [InlineData(0.9)]
    [InlineData(0.9999999999999999)]
    [InlineData(1)]
    public void OpaqueBlendPreservesScalarRoundingAndChannelOrder(double alpha)
    {
        double outputAlpha = alpha + (1 - alpha);
        for (uint sourceByte = 0; sourceByte < 256; sourceByte++)
            for (uint backdropByte = 0; backdropByte < 256; backdropByte++)
            {
                uint source = Channels(sourceByte), backdrop = Channels(backdropByte ^ 0x55);
                uint expected = 0;
                for (int channel = 0; channel < 4; channel++)
                {
                    double s = (byte)(source >> (channel * 8)) / 255d;
                    double b = (byte)(backdrop >> (channel * 8)) / 255d;
                    double value = ((1 - alpha) * b + alpha * (1 - (1 - s))) / outputAlpha;
                    expected |= (uint)(byte)Math.Round(Math.Clamp(value, 0, 1) * 255) << (channel * 8);
                }
                Assert.Equal(expected, PdfPageRenderer.BlendOpaqueInk(source, backdrop, alpha, outputAlpha));
            }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultiplyAndScreenPreserveScalarRounding(bool screen)
    {
        foreach (double alpha in new[] { 0, double.Epsilon, 1e-100, 1 / 255d, 0.123456789, 0.5, 0.9, 1 })
        foreach (double backdropAlpha in new[] { 1 / 255d, 0.5, 1 })
        for (uint sourceByte = 0; sourceByte < 256; sourceByte++)
        for (uint backdropByte = 0; backdropByte < 256; backdropByte++)
        {
            uint source = Channels(sourceByte), backdrop = Channels(backdropByte ^ 0x55);
            double outputAlpha = alpha + backdropAlpha * (1 - alpha);
            uint expected = 0;
            for (int channel = 0; channel < 4; channel++)
            {
                double s = (byte)(source >> (channel * 8)) / 255d;
                double b = (byte)(backdrop >> (channel * 8)) / 255d;
                double lightBackdrop = 1 - b, lightSource = 1 - s;
                double blended = screen ? lightBackdrop + lightSource - lightBackdrop * lightSource
                    : lightBackdrop * lightSource;
                double mixed = 1 - blended;
                double value = alpha == 1 && backdropAlpha == 1 ? mixed
                    : ((1 - backdropAlpha) * alpha * s + (1 - alpha) * backdropAlpha * b
                        + alpha * backdropAlpha * mixed) / outputAlpha;
                expected |= (uint)(byte)Math.Round(Math.Clamp(value, 0, 1) * 255) << (channel * 8);
            }
            Assert.Equal(expected, PdfPageRenderer.BlendMultiplyScreenInk(source, backdrop,
                alpha, backdropAlpha, outputAlpha, screen));
        }
    }

    private static uint Channels(uint value) => value | ((value ^ 0x55) << 8)
        | ((uint)(byte)(value + 91) << 16) | ((value ^ 0xff) << 24);
}
