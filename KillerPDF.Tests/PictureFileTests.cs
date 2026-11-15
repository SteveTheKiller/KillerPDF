using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// Save picture as: a picture kept as an image file is written byte for byte under its own type; a picture lifted
// from the page (kept as its own PDF) is drawn at its own size and written as a PNG.
public sealed class PictureFileTests
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    [Fact]
    public void AnImageFileIsWrittenUnchangedUnderItsOwnType()
    {
        var (png, pngExtension) = PictureFile.Bytes(Convert.ToBase64String(Png), _ => throw new InvalidOperationException());
        var (jpeg, jpegExtension) = PictureFile.Bytes(Convert.ToBase64String(Jpeg), _ => throw new InvalidOperationException());

        Assert.Equal(Png, png);
        Assert.Equal(".png", pngExtension);
        Assert.Equal(Jpeg, jpeg);
        Assert.Equal(".jpg", jpegExtension);
    }

    [Fact]
    public void ALiftedPictureIsDrawnAndWrittenAsPng()
    {
        byte[] pdf = "%PDF-1.7 asset"u8.ToArray();
        byte[]? asked = null;

        var (bytes, extension) = PictureFile.Bytes(Convert.ToBase64String(pdf), data => { asked = data; return Png; });

        Assert.Equal(pdf, asked);
        Assert.Equal(Png, bytes);
        Assert.Equal(".png", extension);
    }

    [Theory]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, ".gif")]
    [InlineData(new byte[] { (byte)'B', (byte)'M', 0, 0 }, ".bmp")]
    [InlineData(new byte[] { (byte)'I', (byte)'I', 42, 0 }, ".tif")]
    [InlineData(new byte[] { (byte)'M', (byte)'M', 0, 42 }, ".tif")]
    public void OtherImageTypesKeepTheirExtension(byte[] data, string extension) =>
        Assert.Equal(extension, PictureFile.Bytes(Convert.ToBase64String(data), _ => throw new InvalidOperationException()).Extension);
}
