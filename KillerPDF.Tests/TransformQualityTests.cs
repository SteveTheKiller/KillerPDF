using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class TransformQualityTests
{
    [Fact]
    public void ApplyColorModeToBgra_ProducesOpaqueGrayAndBitonalSamples()
    {
        byte[] grayscale = [10, 20, 30, 255, 200, 150, 100, 255];
        PageQualityConverter.ApplyColorModeToBgra(
            grayscale, PageColorMode.Grayscale, 160);

        Assert.Equal(new byte[] { 22, 22, 22, 255, 141, 141, 141, 255 }, grayscale);

        byte[] bitonal = [10, 20, 30, 255, 200, 150, 100, 255];
        PageQualityConverter.ApplyColorModeToBgra(
            bitonal, PageColorMode.BlackAndWhite, 100);

        Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 }, bitonal);
    }

    [Fact]
    public void ApplyColorModeToBgra_RejectsIncompletePixels()
    {
        Assert.Throws<ArgumentException>(() =>
            PageQualityConverter.ApplyColorModeToBgra(
                new byte[3], PageColorMode.Grayscale, 160));
    }

    [Fact]
    public void Grayscale_UsesPerceptualLuminanceAndPreservesAlpha()
    {
        BitmapSource source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32,
            null, new byte[] { 0, 0, 255, 123 }, 4);

        BitmapSource result = PageQualityConverter.ApplyColorMode(
            source, PageColorMode.Grayscale, 160);

        var pixels = new byte[4];
        result.CopyPixels(pixels, 4, 0);
        Assert.Equal(pixels[0], pixels[1]);
        Assert.Equal(pixels[1], pixels[2]);
        Assert.InRange(pixels[0], 75, 77);
        Assert.Equal(123, pixels[3]);
    }

    [Fact]
    public void BlackAndWhite_UsesRequestedThreshold()
    {
        BitmapSource source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32,
            null, new byte[] { 100, 100, 100, 255, 180, 180, 180, 255 }, 8);

        BitmapSource result = PageQualityConverter.ApplyColorMode(
            source, PageColorMode.BlackAndWhite, 160);

        var pixels = new byte[8];
        result.CopyPixels(pixels, 8, 0);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 }, pixels);
    }
}
