using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfPageRasterInformationTests
{
    [Fact]
    public void ReadBitonalImagePageHints_DistinguishesBitonalAndColorPages()
    {
        PdfImage bitonal = PdfImage.FromBitonal(2, 1, new byte[] { 0, 255 });
        PdfImage color = PdfImage.FromRgb(1, 1, new byte[] { 255, 0, 0 });
        byte[] source = new PdfDocumentBuilder()
            .AddPage(100, 100,
                new PdfContentStreamBuilder().DrawImage(bitonal, 0, 0, 100, 100))
            .AddPage(100, 100,
                new PdfContentStreamBuilder().DrawImage(color, 0, 0, 100, 100))
            .AddPage(100, 100, new PdfContentStreamBuilder())
            .Build();

        Assert.Equal([true, false, false],
            PdfPageRasterInformation.ReadBitonalImagePageHints(PdfDocument.Open(source)));
    }

    [Fact]
    public void ReadJpegImagePageHints_DistinguishesJpegAndLosslessPages()
    {
        PdfImage jpeg = PdfImage.FromJpeg(MinimalJpeg(2, 1));
        PdfImage color = PdfImage.FromRgb(1, 1, new byte[] { 255, 0, 0 });
        byte[] source = new PdfDocumentBuilder()
            .AddPage(100, 100,
                new PdfContentStreamBuilder().DrawImage(jpeg, 0, 0, 100, 100))
            .AddPage(100, 100,
                new PdfContentStreamBuilder().DrawImage(color, 0, 0, 100, 100))
            .AddPage(100, 100, new PdfContentStreamBuilder())
            .Build();

        Assert.Equal([true, false, false],
            PdfPageRasterInformation.ReadJpegImagePageHints(PdfDocument.Open(source)));
    }

    [Fact]
    public void ReadJpegImagePageHints_FindsJpegInsideNestedForm()
    {
        PdfImage jpeg = PdfImage.FromJpeg(MinimalJpeg(2, 1));
        var nestedJpeg = new PdfFormXObject(100, 100,
            new PdfContentStreamBuilder().DrawImage(jpeg, 0, 0, 100, 100));
        byte[] source = new PdfDocumentBuilder()
            .AddPage(100, 100,
                new PdfContentStreamBuilder().DrawForm(nestedJpeg, 0, 0))
            .Build();

        Assert.Equal([true],
            PdfPageRasterInformation.ReadJpegImagePageHints(PdfDocument.Open(source)));
    }

    [Fact]
    public void ReadJpegImagePageHints_RejectsMixedImageCompression()
    {
        PdfImage jpeg = PdfImage.FromJpeg(MinimalJpeg(2, 1));
        PdfImage color = PdfImage.FromRgb(1, 1, new byte[] { 255, 0, 0 });
        byte[] source = new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .DrawImage(jpeg, 0, 0, 50, 100)
                .DrawImage(color, 50, 0, 50, 100))
            .Build();

        Assert.Equal([false],
            PdfPageRasterInformation.ReadJpegImagePageHints(PdfDocument.Open(source)));
    }

    private static byte[] MinimalJpeg(int width, int height)
    {
        const int components = 3;
        int frameLength = 8 + components * 3;
        var bytes = new List<byte>
        {
            0xFF, 0xD8,
            0xFF, 0xC0, (byte)(frameLength >> 8), (byte)frameLength,
            0x08,
            (byte)(height >> 8), (byte)height,
            (byte)(width >> 8), (byte)width,
            components
        };
        for (int component = 0; component < components; component++)
        {
            bytes.Add((byte)(component + 1));
            bytes.Add(0x11);
            bytes.Add(0);
        }
        int scanLength = 6 + components * 2;
        bytes.AddRange([0xFF, 0xDA, (byte)(scanLength >> 8), (byte)scanLength, components]);
        for (int component = 0; component < components; component++)
        {
            bytes.Add((byte)(component + 1));
            bytes.Add(0);
        }
        bytes.AddRange([0, 63, 0, 0, 0xFF, 0xD9]);
        return [.. bytes];
    }
}
