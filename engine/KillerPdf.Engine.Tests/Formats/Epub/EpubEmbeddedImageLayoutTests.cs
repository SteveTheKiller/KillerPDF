using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubEmbeddedImageLayoutTests
{
    [Theory]
    [InlineData("img{max-width:1px}img{max-width:none}", 2)]
    [InlineData("img{max-width:none}img{max-width:1px}", 1)]
    [InlineData("img.sized{max-width:1px}img{max-width:none}", 1)]
    [InlineData("img.sized{max-width:none}img{max-width:1px}", 2)]
    public void MaximumImageWidthCascadesIndependentlyOfDisplay(string css, int width)
    {
        var book = EpubDocument.Open(Package(RgbPng(),
            "<img class='sized' src='../images/check.png' alt=''/>", "img{display:block}" + css));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 12, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var image = Assert.Single(new PdfPageContentReader(pdf).Read(0).Images);
        Assert.Equal(width * .75, image.BoundingBox.Width, 6);
        Assert.Equal(width * .75, image.BoundingBox.Height, 6);
        Assert.Equal(10.5, image.BoundingBox.Top, 6);
    }

    [Fact]
    public void ZeroMaximumWidthOmitsImagePaintWithoutChangingThePackage()
    {
        byte[] source = Package(RgbPng(), "<img src='../images/check.png' alt=''/>", "img{display:block;max-width:0}");
        byte[] preserved = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        var layout = book.CreateLayout(new EpubLayoutOptions(16, 16, font) { Margin = 2, FontSize = 10 });
        Assert.All(layout.RenderPage(0).Pixels.ToArray(), value => Assert.Equal(1f, value));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 12, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        Assert.Empty(new PdfPageContentReader(pdf).Read(0).Images);
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MaximumImageWidthShrinksBothAxesWithoutChangingSourcePixels(bool inline)
    {
        byte[] png = RgbPng(scanlines: [0, 255, 0, 0, 255, 0, 0, 0, 255, 0, 0, 255, 0, 0]);
        byte[] source = Package(png, "<img " + (inline ? "style='display:block;max-width:1px' " : "")
            + "src='../images/check.png' alt=''/>", inline ? null : "img{display:block;max-width:1px}");
        byte[] preserved = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        var layout = book.CreateLayout(new EpubLayoutOptions(16, 16, font) { Margin = 2, FontSize = 10 });
        Assert.Single(layout.Pages);
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((2 * 16 + 2) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((2 * 16 + 3) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((3 * 16 + 2) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 12, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var embedded = Assert.Single(new PdfPageContentReader(pdf).Read(0).Images);
        Assert.Equal(2, embedded.PixelWidth);
        Assert.Equal(2, embedded.PixelHeight);
        Assert.Equal(.75, embedded.BoundingBox.Width, 6);
        Assert.Equal(.75, embedded.BoundingBox.Height, 6);
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(16, 16));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((2 * 16 + 2) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, saved.Pixels.Slice((2 * 16 + 3) * 4, 4).ToArray());
        Assert.Equal(png, book.ReadResource("EPUB/images/check.png"));
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("8.333333333333333%", true, 1)]
    [InlineData(".1em", true, 1)]
    [InlineData("100%", true, 2)]
    [InlineData("none", true, 2)]
    [InlineData("1px", false, 1)]
    public void MaximumImageWidthUsesContainingWidthAndOwnFontWithoutEnlargement(string maximum, bool block, int expectedWidth)
    {
        byte[] png = RgbPng(scanlines: [0, 255, 0, 0, 255, 0, 0, 0, 255, 0, 0, 255, 0, 0]);
        byte[] source = Package(png, "<img src='../images/check.png' alt=''/>",
            "img{display:" + (block ? "block" : "inline") + ";max-width:" + maximum + "}");
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        var layout = book.CreateLayout(new EpubLayoutOptions(16, 16, font) { Margin = 2, FontSize = 10 });
        int top = block ? 2 : 10 - expectedWidth;
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((top * 16 + 2) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((top * 16 + 2 + expectedWidth) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 12, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var embedded = Assert.Single(new PdfPageContentReader(pdf).Read(0).Images);
        Assert.Equal(expectedWidth * .75, embedded.BoundingBox.Width, 6);
        Assert.Equal(expectedWidth * .75, embedded.BoundingBox.Height, 6);
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(16, 16));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((top * 16 + 2) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, saved.Pixels.Slice((top * 16 + 2 + expectedWidth) * 4, 4).ToArray());
    }

    [Fact]
    public void PaddingPageBreaksPreserveFollowingImageEventsAndSourceRanges()
    {
        byte[] source = Package(RgbPng(), "<p style='margin:0;padding-bottom:95px'>A</p>"
            + "<p style='margin:0'><img src='../images/check.png' alt=''/></p><p style='margin:0'>A</p>");
        byte[] preserved = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 60, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(4, layout.Pages.Count);
        Assert.Equal(book.ReadText(0), string.Concat(layout.Pages.Select(page => page.Text)));
        for (int page = 1; page < layout.Pages.Count; page++)
            Assert.Equal(layout.Pages[page - 1].TextStart + layout.Pages[page - 1].Text.Length, layout.Pages[page].TextStart);
        Assert.Equal(26, Assert.Single(layout.Pages[3].TextPlacements).Top, 6);
        var native = layout.RenderPage(3);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((21 * 100 + 10) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 45, MarginPoints = 7.5, FontSizePoints = 12 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(4, reader.PageCount);
        Assert.Equal("A", string.Concat(reader.Read(3).Letters.Select(letter => letter.Value)));
        var saved = new PdfPageRenderer(pdf).Render(3, new PdfRenderOptions(100, 60));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((21 * 100 + 10) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData(0, 0, "0000B18F", false)]
    [InlineData(1, 1, "000186A0", true)]
    [InlineData(2, 0, "00000001", true)]
    [InlineData(3, 1, "FFFFFFFF", false)]
    public void SrgbIntentAndGammaPrecedenceRetainLiteralMidtoneAndPixelGeometry(
        byte intent, byte unit, string gamma, bool gammaFirst)
    {
        (string Type, byte[] Data) srgb = ("sRGB", [intent]);
        (string Type, byte[] Data) gama = ("gAMA", Convert.FromHexString(gamma));
        byte[] png = RgbPng(scanlines: [0, 128, 128, 128, 0, 255, 0, 0, 0, 0, 255, 0, 0, 0],
            metadata: [gammaFirst ? gama : srgb, gammaFirst ? srgb : gama,
                ("pHYs", [0, 0, 0, 1, 0, 0, 0, 1, unit])]);
        var book = EpubDocument.Open(Package(png));
        var layout = book.CreateLayout(new EpubLayoutOptions(16, 16,
            TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"))) { Margin = 2, FontSize = 10 });
        var frame = layout.RenderPage(0);
        Assert.Single(layout.Pages);
        Assert.Equal(16, frame.Width);
        Assert.Equal(16, frame.Height);
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[(8 * 16 + 2) * 4 + channel], .2158604f, .2158607f);
        Assert.Equal(new float[] { 0, 1, 0, 1 }, frame.Pixels.Slice((8 * 16 + 3) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 1, 1 }, frame.Pixels.Slice((9 * 16 + 2) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((9 * 16 + 3) * 4, 4).ToArray());
        Assert.Equal(png, book.ReadResource("EPUB/images/check.png"));
    }

    [Theory]
    [InlineData("sRGB", "03")]
    [InlineData("pHYs", "000000010000000100")]
    [InlineData("pHYs", "00000EC300000EC301")]
    public void IndependentSupportedMetadataRendersLiteralPixels(string type, string hex)
    {
        var frame = ImageLayout(RgbPng(metadata: [(type, Convert.FromHexString(hex))])).RenderPage(0);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, frame.Pixels.Slice((8 * 16 + 2) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 1, 0, 1 }, frame.Pixels.Slice((8 * 16 + 3) * 4, 4).ToArray());
    }

    [Theory]
    [InlineData("iCCP")]
    [InlineData("cHRM")]
    [InlineData("cICP")]
    [InlineData("acTL")]
    [InlineData("tEXt")]
    public void AdditionalColorAnimationAndUnimplementedChunksRemainUnsupported(string type)
    {
        Assert.Throws<NotSupportedException>(() => ImageLayout(RgbPng(metadata: [(type, [0])])));
    }

    [Theory]
    [InlineData("gAMA", "00000001")]
    [InlineData("gAMA", "0000B18F")]
    [InlineData("gAMA", "000186A0")]
    [InlineData("pHYs", "000000010000000200")]
    [InlineData("pHYs", "00000EC300000EC401")]
    [InlineData("pHYs", "000000000000000000")]
    [InlineData("pHYs", "000000000000000101")]
    public void UnsupportedGammaOrPixelAspectRatioIsDiagnosed(string type, string hex)
    {
        byte[] png = RgbPng(metadata: [(type, Convert.FromHexString(hex))]);
        var exception = Assert.Throws<NotSupportedException>(() => ImageLayout(png));
        Assert.Contains(type, exception.Message);
    }

    [Theory]
    [InlineData("sRGB", "00", "duplicate")]
    [InlineData("gAMA", "0000B18F", "duplicate")]
    [InlineData("pHYs", "00000EC300000EC301", "duplicate")]
    [InlineData("sRGB", "00", "late")]
    [InlineData("gAMA", "0000B18F", "late")]
    [InlineData("pHYs", "00000EC300000EC301", "late")]
    [InlineData("sRGB", "00", "crc")]
    [InlineData("gAMA", "0000B18F", "crc")]
    [InlineData("pHYs", "00000EC300000EC301", "crc")]
    public void DuplicateLateOrCorruptImageMetadataIsDiagnosed(string type, string hex, string fault)
    {
        (string Type, byte[] Data) chunk = (type, Convert.FromHexString(hex));
        byte[] png = RgbPng(metadata: fault == "duplicate" ? [chunk, chunk] : [chunk],
            afterImage: fault == "late");
        if (fault == "crc") png[41 + chunk.Data.Length] ^= 1;
        Assert.Throws<InvalidDataException>(() => ImageLayout(png));
    }

    [Theory]
    [InlineData("sRGB", "")]
    [InlineData("sRGB", "0000")]
    [InlineData("sRGB", "04")]
    [InlineData("gAMA", "000001")]
    [InlineData("gAMA", "0000000001")]
    [InlineData("gAMA", "00000000")]
    [InlineData("pHYs", "00000EC300000EC3")]
    [InlineData("pHYs", "00000EC300000EC30100")]
    [InlineData("pHYs", "00000EC300000EC302")]
    public void MalformedImageMetadataIsDiagnosed(string type, string hex)
    {
        byte[] png = RgbPng(metadata: [(type, Convert.FromHexString(hex))]);
        Assert.Throws<InvalidDataException>(() => ImageLayout(png));
    }

    private static EpubLayoutDocument ImageLayout(byte[] png)
    {
        var book = EpubDocument.Open(Package(png));
        return book.CreateLayout(new EpubLayoutOptions(16, 16,
            TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"))) { Margin = 2, FontSize = 10 });
    }

    [Fact]
    public void SrgbGammaAndSquarePixelMetadataPreserveIntrinsicPixelsAndResource()
    {
        byte[] png = RgbPng(metadata: [("sRGB", [0]), ("gAMA", [0, 0, 177, 143]),
            ("pHYs", [0, 0, 14, 195, 0, 0, 14, 195, 1])]);
        var book = EpubDocument.Open(Package(png));
        var layout = book.CreateLayout(new EpubLayoutOptions(16, 16,
            TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"))) { Margin = 2, FontSize = 10 });
        Assert.Single(layout.Pages);
        var frame = layout.RenderPage(0);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
        {
            float[] expected = (x, y) switch {
                (2, 8) => [1, 0, 0, 1], (3, 8) => [0, 1, 0, 1],
                (2, 9) => [0, 0, 1, 1], (3, 9) => [0, 0, 0, 1],
                _ => [1, 1, 1, 1] };
            Assert.Equal(expected, frame.Pixels.Slice((y * 16 + x) * 4, 4).ToArray());
        }
        Assert.Equal(png, book.ReadResource("EPUB/images/check.png"));
    }

    [Fact]
    public void ConsecutiveBreaksAfterImageReserveBlankLineBeforeOriginalTextOffset()
    {
        var book = EpubDocument.Open(Package(RgbPng(),
            "<p><img src='../images/check.png' alt=''/><br/><br/>A</p>"));
        var layout = book.CreateLayout(new EpubLayoutOptions(16, 40,
            TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"))) { Margin = 2, FontSize = 10 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("A", book.ReadText(0));
        Assert.Equal("A", page.Text);
        Assert.Equal(new[] { new EpubTextPlacement(0, 0, 1, 2, 22, 6, 10) }, page.TextPlacements);
        var frame = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, frame.Pixels.Slice((8 * 16 + 2) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 1, 0, 1 }, frame.Pixels.Slice((8 * 16 + 3) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 1, 1 }, frame.Pixels.Slice((9 * 16 + 2) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((9 * 16 + 3) * 4, 4).ToArray());
        for (int y = 12; y < 20; y++)
        for (int x = 0; x < 16; x++)
            Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((y * 16 + x) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((25 * 16 + 7) * 4, 4).ToArray());
    }

    [Fact]
    public void ExplicitBreakSeparatesImageOnlyLinesWithoutParagraphSpacing()
    {
        byte[] png = RgbPng();
        var book = EpubDocument.Open(Package(png,
            "<p><img src='../images/check.png' alt=''/><br/><img src='../images/check.png' alt=''/></p>"));
        var layout = book.CreateLayout(new EpubLayoutOptions(16, 30,
            TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"))) { Margin = 2, FontSize = 10 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("", book.ReadText(0));
        Assert.Equal("", page.Text);
        Assert.Empty(page.TextPlacements);
        var frame = layout.RenderPage(0);
        for (int y = 0; y < 30; y++)
        for (int x = 0; x < 16; x++)
        {
            float[] expected = (x, y) switch {
                (2, 8) or (2, 18) => [1, 0, 0, 1],
                (3, 8) or (3, 18) => [0, 1, 0, 1],
                (2, 9) or (2, 19) => [0, 0, 1, 1],
                (3, 9) or (3, 19) => [0, 0, 0, 1],
                _ => [1, 1, 1, 1] };
            Assert.Equal(expected, frame.Pixels.Slice((y * 16 + x) * 4, 4).ToArray());
        }
        Assert.Equal(png, book.ReadResource("EPUB/images/check.png"));
    }

    [Fact]
    public void ImageOnlyParagraphRendersPackagedRgbPngAtIntrinsicSizeOnBaseline()
    {
        byte[] png = RgbPng();
        var book = EpubDocument.Open(Package(png));
        Assert.Equal(png, book.ReadResource("EPUB/images/check.png"));
        var options = new EpubLayoutOptions(16, 16,
            TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"))) { Margin = 2, FontSize = 10 };

        var layout = book.CreateLayout(options);

        Assert.Equal("", book.ReadText(0));
        var page = Assert.Single(layout.Pages);
        Assert.Equal("", page.Text);
        Assert.Empty(page.TextPlacements);
        var frame = layout.RenderPage(0);
        Assert.Equal(16, frame.Width);
        Assert.Equal(16, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
        {
            float[] expected = (x, y) switch {
                (2, 8) => [1, 0, 0, 1],
                (3, 8) => [0, 1, 0, 1],
                (2, 9) => [0, 0, 1, 1],
                (3, 9) => [0, 0, 0, 1],
                _ => [1, 1, 1, 1] };
            Assert.Equal(expected, frame.Pixels.Slice((y * 16 + x) * 4, 4).ToArray());
        }
        Assert.Equal(png, book.ReadResource("EPUB/images/check.png"));
    }

    [Fact]
    public void ImageOnlyParagraphsRetainSeparateLinesAndParagraphSpacing()
    {
        const string image = "<p><img src='../images/check.png' alt=''/></p>";
        var book = EpubDocument.Open(Package(RgbPng(), image + image));
        var layout = book.CreateLayout(new EpubLayoutOptions(16, 40,
            TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"))) { Margin = 2, FontSize = 10 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("", page.Text);
        Assert.Empty(page.TextPlacements);
        var frame = layout.RenderPage(0);
        for (int y = 0; y < 40; y++)
        for (int x = 0; x < 16; x++)
        {
            float[] expected = (x, y) switch {
                (2, 8) or (2, 28) => [1, 0, 0, 1],
                (3, 8) or (3, 28) => [0, 1, 0, 1],
                (2, 9) or (2, 29) => [0, 0, 1, 1],
                (3, 9) or (3, 29) => [0, 0, 0, 1],
                _ => [1, 1, 1, 1] };
            Assert.Equal(expected, frame.Pixels.Slice((y * 16 + x) * 4, 4).ToArray());
        }
    }

    [Fact]
    public void InlineImageAdvancesFollowingTextWithoutInventingTextOffsets()
    {
        var book = EpubDocument.Open(Package(RgbPng(),
            "<p>A<img src='../images/check.png' alt='Illustration'/>A</p>"));
        var layout = book.CreateLayout(new EpubLayoutOptions(24, 16,
            TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"))) { Margin = 2, FontSize = 10 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("AA", book.ReadText(0));
        Assert.Equal("AA", page.Text);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10),
            new EpubTextPlacement(0, 1, 1, 10, 2, 6, 10)
        }, page.TextPlacements);
        var frame = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, frame.Pixels.Slice((8 * 24 + 8) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 1, 0, 1 }, frame.Pixels.Slice((8 * 24 + 9) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 1, 1 }, frame.Pixels.Slice((9 * 24 + 8) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((9 * 24 + 9) * 4, 4).ToArray());
    }

    [Fact]
    public void CollapsedSpaceBeforeImageKeepsItsSourceOrderAndPlacement()
    {
        var book = EpubDocument.Open(Package(RgbPng(),
            "<p>A <img src='../images/check.png' alt=''/>A</p>"));
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 16,
            TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"))) { Margin = 2, FontSize = 10 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("A A", book.ReadText(0));
        Assert.Equal("A A", page.Text);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10),
            new EpubTextPlacement(0, 1, 1, 8, 2, 5, 10),
            new EpubTextPlacement(0, 2, 1, 15, 2, 6, 10)
        }, page.TextPlacements);
        var frame = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, frame.Pixels.Slice((8 * 30 + 13) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 1, 0, 1 }, frame.Pixels.Slice((8 * 30 + 14) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 1, 1 }, frame.Pixels.Slice((9 * 30 + 13) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((9 * 30 + 14) * 4, 4).ToArray());
    }

    [Fact]
    public void RgbaPngCompositesItsAlphaBeforeSingleSrgbDecode()
    {
        byte[] png = RgbPng(6, [
            0, 255, 0, 0, 128, 0, 255, 0, 255,
            0, 0, 0, 255, 0, 0, 0, 0, 64 ]);
        var book = EpubDocument.Open(Package(png));
        var layout = book.CreateLayout(new EpubLayoutOptions(16, 16,
            TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"))) { Margin = 2, FontSize = 10 });
        var frame = layout.RenderPage(0);
        int red = (8 * 16 + 2) * 4, black = (9 * 16 + 3) * 4;
        Assert.Equal(1f, frame.Pixels[red]);
        Assert.InRange(frame.Pixels[red + 1], .2122306f, .2122309f);
        Assert.InRange(frame.Pixels[red + 2], .2122306f, .2122309f);
        Assert.Equal(new float[] { 0, 1, 0, 1 }, frame.Pixels.Slice((8 * 16 + 3) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((9 * 16 + 2) * 4, 4).ToArray());
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[black + channel], .5209954f, .5209958f);
        for (int alpha = 3; alpha < frame.Pixels.Length; alpha += 4) Assert.Equal(1f, frame.Pixels[alpha]);
        Assert.Equal(png, book.ReadResource("EPUB/images/check.png"));
    }

    [Fact]
    public void ImageAfterFinalTextParagraphSurvivesTrailingTextNormalization()
    {
        var book = EpubDocument.Open(Package(RgbPng(),
            "<p>A</p><p><img src='../images/check.png' alt=''/></p>"));
        var layout = book.CreateLayout(new EpubLayoutOptions(16, 40,
            TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"))) { Margin = 2, FontSize = 10 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("A", page.Text);
        Assert.Equal(new[] { new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10) }, page.TextPlacements);
        var frame = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, frame.Pixels.Slice((28 * 16 + 2) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 1, 0, 1 }, frame.Pixels.Slice((28 * 16 + 3) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 1, 1 }, frame.Pixels.Slice((29 * 16 + 2) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((29 * 16 + 3) * 4, 4).ToArray());
    }

    private static byte[] Package(byte[] png, string? body = null, string? css = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            Add("EPUB/book.opf", """
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:embedded-image</dc:identifier><dc:title>Embedded image</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-05T00:00:00Z</meta></metadata>
                  <manifest><item id="chapter" href="text/chapter.xhtml" media-type="application/xhtml+xml"/><item id="image" href="images/check.png" media-type="image/png"/><item id="font" href="fonts/test.ttf" media-type="font/ttf"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """.Replace("</manifest>", css is null ? "</manifest>"
                    : "<item id='css' href='style.css' media-type='text/css'/></manifest>"));
            if (css is not null) Add("EPUB/style.css", css);
            Add("EPUB/text/chapter.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Image</title>"
                + (css is null ? "" : "<link rel='stylesheet' href='../style.css'/>") + "</head><body>"
                + (body ?? "<p><img src='../images/check.png' alt=''/></p>") + "</body></html>");
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="text/chapter.xhtml">Image</a></li></ol></nav></body></html>
                """);
            using (var image = zip.CreateEntry("EPUB/images/check.png", CompressionLevel.NoCompression).Open())
                image.Write(png);
            using (var font = zip.CreateEntry("EPUB/fonts/test.ttf", CompressionLevel.NoCompression).Open())
                font.Write(TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true));

            void Add(string path, string content, CompressionLevel compression = CompressionLevel.Optimal)
            {
                using var entry = zip.CreateEntry(path, compression).Open();
                entry.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return output.ToArray();
    }

    private static byte[] RgbPng(byte colorType = 2, byte[]? scanlines = null,
        (string Type, byte[] Data)[]? metadata = null, bool afterImage = false)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Chunk("IHDR", [0, 0, 0, 2, 0, 0, 0, 2, 8, colorType, 0, 0, 0]);
        if (!afterImage && metadata is not null)
            foreach (var chunk in metadata) Chunk(chunk.Type, chunk.Data);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
            zlib.Write(scanlines ?? new byte[] { 0, 255, 0, 0, 0, 255, 0, 0, 0, 0, 255, 0, 0, 0 });
        Chunk("IDAT", compressed.ToArray());
        if (afterImage && metadata is not null)
            foreach (var chunk in metadata) Chunk(chunk.Type, chunk.Data);
        Chunk("IEND", []);
        return output.ToArray();

        void Chunk(string type, byte[] data)
        {
            Span<byte> integer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(integer, (uint)data.Length);
            output.Write(integer);
            byte[] name = Encoding.ASCII.GetBytes(type);
            output.Write(name);
            output.Write(data);
            uint crc = uint.MaxValue;
            foreach (byte value in name.Concat(data))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
            }
            BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc);
            output.Write(integer);
        }
    }
}
