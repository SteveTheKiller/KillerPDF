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

    // A scan's pixels, whatever size the page is, turned up the way the page is shown.
    [Fact]
    public void ReadScanSizes_GivesThePageInTheLargestImagesPixels()
    {
        PdfImage image = PdfImage.FromBitonal(400, 200, new byte[80000]);
        PdfImage strip = PdfImage.FromBitonal(40, 10, new byte[400]);
        byte[] source = new PdfDocumentBuilder()
            .AddPage(100, 50, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 100, 50))
            .AddPage(100, 100, new PdfContentStreamBuilder().DrawImage(strip, 0, 0, 10, 2.5).DrawImage(image, 0, 0, 50, 25))
            .AddPage(100, 100, new PdfContentStreamBuilder())
            .Build();
        Assert.Equal(new (int, int)?[] { (400, 200), (800, 800), null },
            PdfPageRasterInformation.ReadScanSizes(PdfDocument.Open(source)));
    }

    // A logo's resolution says nothing about the page around it, and a picture page's is the page's.
    [Fact]
    public void ReadPageImageSize_OnlyAnswersForAPageOneImageCovers()
    {
        PdfImage image = PdfImage.FromBitonal(500, 500, new byte[250000]);
        byte[] source = new PdfDocumentBuilder()
            .AddPage(340, 340, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 340, 340))
            .AddPage(340, 340, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 50, 50))
            .AddBlankPage()
            .Build();
        PdfDocument document = PdfDocument.Open(source);

        Assert.Equal((500, 500), PdfPageRasterInformation.ReadPageImageSize(document, 0));
        Assert.Null(PdfPageRasterInformation.ReadPageImageSize(document, 1));
        Assert.Null(PdfPageRasterInformation.ReadPageImageSize(document, 2));
    }

    // A caller that reads every page reads the page information once and hands it to each page.
    [Fact]
    public void ReadPageImageSize_GivesTheSameAnswerFromPageInformationReadBeforehand()
    {
        PdfImage image = PdfImage.FromBitonal(500, 500, new byte[250000]);
        byte[] source = new PdfDocumentBuilder()
            .AddPage(340, 340, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 340, 340))
            .AddPage(340, 340, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 50, 50))
            .AddBlankPage()
            .Build();
        PdfDocument document = PdfDocument.Open(source);
        IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(document);

        for (int page = 0; page < pages.Count; page++)
            Assert.Equal(PdfPageRasterInformation.ReadPageImageSize(document, page),
                PdfPageRasterInformation.ReadPageImageSize(document, pages, page));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfPageRasterInformation.ReadPageImageSize(document, pages, pages.Count));
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

    [Fact]
    public void TryReadFullPageJpeg_ReturnsExactPayloadForSafeImageOnlyPage()
    {
        byte[] sourceJpeg = MinimalJpeg(2, 1);
        byte[] source = new PdfDocumentBuilder()
            .AddPage(100, 50, new PdfContentStreamBuilder()
                .DrawImage(PdfImage.FromJpeg(sourceJpeg), 0, 0, 100, 50))
            .Build();

        bool found = PdfPageRasterInformation.TryReadFullPageJpeg(
            PdfDocument.Open(source), 0, out PdfImage? image);

        Assert.True(found);
        Assert.NotNull(image);
        Assert.Equal(sourceJpeg, image.Data.ToArray());
    }

    [Fact]
    public void TryReadFullPageJpeg_AcceptsCanonicalPlacementAtCropOrigin()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddPage(160, 100, new PdfContentStreamBuilder()
                .DrawImage(PdfImage.FromJpeg(MinimalJpeg(2, 1)), 20, 30, 100, 50))
            .Build();
        byte[] cropped = new KillerPdf.Engine.Editing.PdfIncrementalPageEditor(PdfDocument.Open(source))
            .SetCropBox(0, 20, 30, 100, 50).Build();
        Assert.True(PdfPageRasterInformation.TryReadFullPageJpeg(PdfDocument.Open(cropped), 0, out _));
    }

    [Theory]
    [InlineData(-1, 0, 0, 1, 100, 0)]
    [InlineData(1, 0, 0, -1, 0, 50)]
    [InlineData(-1, 0, 0, -1, 100, 50)]
    [InlineData(0, 0.5, -2, 0, 100, 0)]
    [InlineData(0, -0.5, 2, 0, 0, 50)]
    [InlineData(0.9, 0, 0.2, 1, 0, 0)]
    public void TryReadFullPageJpeg_RejectsNoncanonicalPlacement(
        double a, double b, double c, double d, double e, double f)
    {
        byte[] source = new PdfDocumentBuilder()
            .AddPage(100, 50, new PdfContentStreamBuilder()
                .Transform(a, b, c, d, e, f)
                .DrawImage(PdfImage.FromJpeg(MinimalJpeg(2, 1)), 0, 0, 100, 50))
            .Build();

        Assert.False(PdfPageRasterInformation.TryReadFullPageJpeg(
            PdfDocument.Open(source), 0, out _));
    }

    [Fact]
    public void TryReadFullPageJpeg_RejectsPartialAndMultipleImagePages()
    {
        PdfImage jpeg = PdfImage.FromJpeg(MinimalJpeg(2, 1));
        byte[] source = new PdfDocumentBuilder()
            .AddPage(100, 50, new PdfContentStreamBuilder()
                .DrawImage(jpeg, 0, 0, 90, 50))
            .AddPage(100, 50, new PdfContentStreamBuilder()
                .DrawImage(jpeg, 0, 0, 50, 50)
                .DrawImage(jpeg, 50, 0, 50, 50))
            .Build();
        PdfDocument document = PdfDocument.Open(source);

        Assert.False(PdfPageRasterInformation.TryReadFullPageJpeg(
            document, 0, out _));
        Assert.False(PdfPageRasterInformation.TryReadFullPageJpeg(
            document, 1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfPageRasterInformation.TryReadFullPageJpeg(document, 2, out _));
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
