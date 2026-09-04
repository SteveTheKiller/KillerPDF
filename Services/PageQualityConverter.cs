using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerPDF.Services;

internal enum PageColorMode { Color, Grayscale, BlackAndWhite }

internal static class PageQualityConverter
{
    internal static void ApplyColorModeToBgra(
        Span<byte> pixels, PageColorMode mode, int threshold)
    {
        if (mode == PageColorMode.Color) return;
        if (pixels.Length % 4 != 0)
            throw new ArgumentException("BGRA pixel data must contain four bytes per pixel.", nameof(pixels));
        int clampedThreshold = Math.Clamp(threshold, 0, 255);
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            int gray = (pixels[offset + 2] * 77
                + pixels[offset + 1] * 150 + pixels[offset] * 29 + 128) >> 8;
            byte value = mode == PageColorMode.BlackAndWhite
                ? (byte)(gray >= clampedThreshold ? 255 : 0)
                : (byte)gray;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
        }
    }

    internal static BitmapSource ApplyColorMode(
        BitmapSource source, PageColorMode mode, int threshold)
    {
        if (mode == PageColorMode.Color) return source;
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth, height = converted.PixelHeight, stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        ApplyColorModeToBgra(pixels, mode, threshold);
        var result = BitmapSource.Create(width, height, converted.DpiX, converted.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
