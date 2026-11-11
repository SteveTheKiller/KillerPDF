using System.Buffers.Binary;
using System.Text;
using KillerPDF.Services;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPDF.Tests;

public sealed class NativePhotoConversionTests
{
    [Fact]
    public void ConvertingAColorDjvuPhotoUsesTheAppJpegEncoderAndKeepsThePageGeometry()
    {
        using var source = NativeWorkingDocument.OpenDjvu(FlatColorPage());

        byte[] bytes = source.ConvertDjvuToPdf();

        Assert.Contains("/DCTDecode", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        var pdf = PdfDocument.Open(bytes);
        var image = Assert.Single(new PdfPageContentReader(pdf).ReadImagePlacements(0));
        Assert.Equal((16, 16), (image.PixelWidth, image.PixelHeight));
        var page = Assert.Single(PdfPageInformation.Read(pdf));
        Assert.Equal(3.84, page.Width, 6);
        Assert.Equal(3.84, page.Height, 6);
        Assert.Empty(new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(16, 16)).Diagnostics);
    }

    private static byte[] FlatColorPage() => [.. "AT&T"u8, .. Chunk("FORM",
        [.. "DJVU"u8, .. Chunk("INFO", [0, 16, 0, 16, 26, 0, 44, 1, 22, 1]),
            .. Chunk("BG44", [0, 0, 1, 2, 0, 16, 0, 16, 0])])];

    private static byte[] Chunk(string name, byte[] data)
    {
        byte[] result = new byte[8 + data.Length + (data.Length & 1)];
        Encoding.ASCII.GetBytes(name).CopyTo(result, 0);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(4), data.Length);
        data.CopyTo(result, 8);
        return result;
    }
}
