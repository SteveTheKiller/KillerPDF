using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerPDF.Services;

internal static class PrintColorConverter
{
    internal static BitmapSource CreateGrayscaleBitmap(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = checked(bgra.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * bgra.PixelHeight)];
        bgra.CopyPixels(pixels, stride, 0);
        for (int index = 0; index < pixels.Length; index += 4)
        {
            byte gray = (byte)((pixels[index + 2] * 77
                + pixels[index + 1] * 150 + pixels[index] * 29 + 128) >> 8);
            pixels[index] = gray;
            pixels[index + 1] = gray;
            pixels[index + 2] = gray;
        }

        BitmapSource result = BitmapSource.Create(
            bgra.PixelWidth, bgra.PixelHeight, bgra.DpiX, bgra.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    /// <summary>A frozen copy of a page showing how the printer profile predicts it prints (#520).</summary>
    /// <remarks>For the print preview only. The job itself sends the converted device values instead.</remarks>
    internal static BitmapSource CreateProofBitmap(BitmapSource source,
        KillerPdf.Engine.ColorManagement.PdfPrinterColorConversion conversion)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(conversion);
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = checked(bgra.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * bgra.PixelHeight)];
        bgra.CopyPixels(pixels, stride, 0);
        conversion.ProofSrgbBgra32(pixels);
        BitmapSource result = BitmapSource.Create(
            bgra.PixelWidth, bgra.PixelHeight, bgra.DpiX, bgra.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
