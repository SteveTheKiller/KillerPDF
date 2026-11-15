namespace KillerPDF.Services;

/// <summary>
/// The file Save picture as writes for a picture. A picture kept as an image file is written byte for byte under
/// its own type, so nothing is re-encoded; a picture lifted from the page is kept as its own one-page PDF, which is
/// drawn at the image's own size and written as a PNG.
/// </summary>
internal static class PictureFile
{
    internal static (byte[] Bytes, string Extension) Bytes(string base64, Func<byte[], byte[]> renderPdfToPng)
    {
        byte[] data = Convert.FromBase64String(base64);
        if (ImageExtension(data) is { } extension) return (data, extension);
        return (renderPdfToPng(data), ".png");
    }

    private static string? ImageExtension(ReadOnlySpan<byte> data) =>
        data.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']) ? ".png"
        : data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? ".jpg"
        : data.StartsWith("GIF8"u8) ? ".gif"
        : data.StartsWith("BM"u8) ? ".bmp"
        : data.StartsWith((ReadOnlySpan<byte>)[(byte)'I', (byte)'I', 42, 0]) || data.StartsWith((ReadOnlySpan<byte>)[(byte)'M', (byte)'M', 0, 42]) ? ".tif"
        : null;
}
