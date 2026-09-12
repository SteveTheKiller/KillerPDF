using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PrintPreviewColorTests
{
    [Fact]
    public void CreateGrayscaleBitmap_EqualizesColorChannelsAndPreservesAlpha()
    {
        byte[] sourcePixels =
        [
            0, 0, 255, 255,
            255, 128, 0, 73
        ];
        BitmapSource source = BitmapSource.Create(
            2, 1, 96, 96, PixelFormats.Bgra32, null, sourcePixels, 8);

        BitmapSource result = PrintColorConverter.CreateGrayscaleBitmap(source);
        byte[] pixels = new byte[8];
        result.CopyPixels(pixels, 8, 0);

        Assert.Equal(pixels[0], pixels[1]);
        Assert.Equal(pixels[1], pixels[2]);
        Assert.Equal(255, pixels[3]);
        Assert.Equal(pixels[4], pixels[5]);
        Assert.Equal(pixels[5], pixels[6]);
        Assert.Equal(73, pixels[7]);
        Assert.True(result.IsFrozen);
    }

    [Fact]
    public void CreateBitonalBitmap_ThresholdsToPureBlackAndWhite()
    {
        byte[] sourcePixels =
        [
            0, 0, 0, 255,        // black stays black
            255, 255, 255, 200,  // white stays white
            0, 0, 255, 255,      // pure red -> dark -> black
            200, 200, 200, 255,  // light gray -> white
        ];
        BitmapSource source = BitmapSource.Create(
            4, 1, 96, 96, PixelFormats.Bgra32, null, sourcePixels, 16);

        BitmapSource result = PrintColorConverter.CreateBitonalBitmap(source);
        byte[] pixels = new byte[16];
        result.CopyPixels(pixels, 16, 0);

        for (int i = 0; i < 16; i += 4)
        {
            byte channel = pixels[i];
            Assert.True(channel == 0 || channel == 255);   // no gray tones survive
            Assert.Equal(channel, pixels[i + 1]);
            Assert.Equal(channel, pixels[i + 2]);
        }
        Assert.Equal(0, pixels[0]);
        Assert.Equal(255, pixels[4]);
        Assert.Equal(200, pixels[7]);    // alpha preserved on the second pixel
        Assert.Equal(255, pixels[15]);   // alpha preserved on the last pixel
        Assert.True(result.IsFrozen);
    }
}
