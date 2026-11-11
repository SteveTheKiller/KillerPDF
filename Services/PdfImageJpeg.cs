using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerPDF.Services;

/// <summary>The JPEG encoder the app supplies for PDF images and DjVu photo conversion.</summary>
internal static class PdfImageJpeg
{
    internal static byte[] EncodePhoto(ReadOnlySpan<byte> samples, int width, int height, int components, int quality)
        => EncodePhoto(samples, width, height, components, quality, CancellationToken.None);

    internal static byte[] EncodePhoto(ReadOnlySpan<byte> samples, int width, int height, int components, int quality,
        CancellationToken cancellationToken)
        => NativeWicJpegEncoder.EncodePhoto(samples, width, height, components, quality, cancellationToken);

    /// <summary>Encodes tightly packed 8-bit gray or RGB samples at the given quality.</summary>
    internal static byte[] Encode(ReadOnlySpan<byte> samples, int width, int height, int components, int quality)
    {
        if (components is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(components));
        PixelFormat format = components == 1 ? PixelFormats.Gray8 : PixelFormats.Rgb24;
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, format, null,
            samples.ToArray(), width * components);
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
