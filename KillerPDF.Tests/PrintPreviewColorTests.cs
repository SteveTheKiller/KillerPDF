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

    // The preview shows what the printer profile predicts, not the device values the job sends:
    // a linear printer receives byte 55 for sRGB 128 but reproduces it as 128 again.
    [Fact]
    public void CreateProofBitmap_ShowsThePredictedPrintAndLeavesTheSourceAlone()
    {
        var printer = KillerPdf.Engine.ColorManagement.PdfColorProfile.Load(ProofProfileLibraryTests.RgbProfile("prtr"));
        var conversion = new KillerPdf.Engine.ColorManagement.PdfPrinterColorConversion(printer,
            KillerPdf.Engine.Authoring.PdfRenderingIntent.RelativeColorimetric, blackPointCompensation: false);
        byte[] sourcePixels = [128, 128, 128, 255, 90, 30, 200, 61];
        BitmapSource source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, sourcePixels, 8);

        BitmapSource proof = PrintColorConverter.CreateProofBitmap(source, conversion);
        byte[] shown = new byte[8];
        proof.CopyPixels(shown, 8, 0);
        byte[] sent = (byte[])sourcePixels.Clone();
        conversion.ConvertSrgbBgra32(sent);

        Assert.True(proof.IsFrozen);
        Assert.Equal(55, sent[0]);
        for (int index = 0; index < shown.Length; index++)
            Assert.InRange(shown[index], sourcePixels[index] - 2, sourcePixels[index] + 2);
        Assert.Equal(61, shown[7]);
        byte[] unchanged = new byte[8];
        source.CopyPixels(unchanged, 8, 0);
        Assert.Equal(sourcePixels, unchanged);
    }
}
