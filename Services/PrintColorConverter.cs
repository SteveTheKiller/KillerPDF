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

    /// <summary>
    /// Reduces ink/toner usage by lightening the image: darkens near-white pixels to pure white,
    /// and shifts mid-tones toward white by ~20%. This produces a lighter print that uses less
    /// ink while keeping text readable. Works best on documents with white backgrounds.
    /// </summary>
    internal static BitmapSource CreateReducedInkBitmap(BitmapSource source)
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
            byte b = pixels[index], g = pixels[index + 1], r = pixels[index + 2];
            // Lighten: push lighter colors toward white, preserve dark text
            // Pixels brighter than 180 become pure white (removes light gray noise/artifacts)
            // Darker pixels (text) are lightened slightly (~15%) to save ink
            byte nr = r > 180 ? (byte)255 : (byte)Math.Min(255, r + (255 - r) / 7);
            byte ng = g > 180 ? (byte)255 : (byte)Math.Min(255, g + (255 - g) / 7);
            byte nb = b > 180 ? (byte)255 : (byte)Math.Min(255, b + (255 - b) / 7);
            pixels[index]     = nb;
            pixels[index + 1] = ng;
            pixels[index + 2] = nr;
        }

        BitmapSource result = BitmapSource.Create(
            bgra.PixelWidth, bgra.PixelHeight, bgra.DpiX, bgra.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    /// <summary>
    /// Pure 1-bit-style black &amp; white: every pixel becomes fully black or fully white
    /// using the same luminance weights as the grayscale pass with a mid-point threshold.
    /// Photocopy-look output with no gray tones; the alpha channel is preserved.
    /// </summary>
    internal static BitmapSource CreateBitonalBitmap(BitmapSource source)
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
            int luminance = (pixels[index + 2] * 77
                + pixels[index + 1] * 150 + pixels[index] * 29 + 128) >> 8;
            byte bw = luminance >= 128 ? (byte)255 : (byte)0;
            pixels[index] = bw;
            pixels[index + 1] = bw;
            pixels[index + 2] = bw;
        }

        BitmapSource bitonal = BitmapSource.Create(
            bgra.PixelWidth, bgra.PixelHeight, bgra.DpiX, bgra.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        bitonal.Freeze();
        return bitonal;
    }
}
