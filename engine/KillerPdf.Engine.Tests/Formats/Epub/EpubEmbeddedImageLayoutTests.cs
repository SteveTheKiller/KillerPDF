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
    [Fact]
    public void PercentageImageFitsTheFractionalContainingWidthAtBothSeams()
    {
        byte[] png = RgbPng(scanlines: new byte[800 * (1 + 800 * 3)], size: 800);
        var book = EpubDocument.Open(Package(png,
            "<div style='text-align:center'><img src='../images/check.png' alt=''/></div>",
            "body{margin:.5em}img{max-width:100%}"));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        var layout = book.CreateLayout(new EpubLayoutOptions(793.7066666666666, 1200, font) { Margin = 48, FontSize = 16 });
        Assert.Single(layout.Pages);
        Assert.Contains(layout.RenderPage(0, new PdfRenderOptions(240, 340)).Pixels.ToArray(), value => value == 0);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 595.28, PageHeightPoints = 900, MarginPoints = 36, FontSizePoints = 12 }));
        var image = Assert.Single(new PdfPageContentReader(pdf).Read(0).Images);
        Assert.Equal(511.28, image.BoundingBox.Width, 6);
        Assert.Equal(800, image.PixelWidth);
        var defaultPaper = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)));
        var defaultImage = Assert.Single(new PdfPageContentReader(defaultPaper).Read(0).Images);
        Assert.Equal(498.236220472441, defaultImage.BoundingBox.Width, 6);
    }

    [Fact]
    public void SvgManifestDeclarationDoesNotAdmitScriptedOrRemoteResourceProperties()
    {
        foreach (string properties in new[] { "svg scripted", "svg remote-resources", "svg mathml", "svg unknown" })
        {
            var book = EpubDocument.Open(Package(RgbPng(), "<p>A</p>", properties: properties));
            var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
            Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(16, 16, font)
            { Margin = 2, FontSize = 10 }));
            Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)
            { PageWidthPoints = 12, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        }
    }

    [Fact]
    public void SvgManifestDeclarationAdmitsTheSupportedStaticCoverAtBothSeams()
    {
        var book = EpubDocument.Open(Package(RgbPng(),
            "<svg xmlns='http://www.w3.org/2000/svg' width='100%' height='100%' viewBox='0 0 2 2'>"
            + "<image width='2' height='2' href='../images/check.png'/></svg>", properties: "svg"));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        var layout = book.CreateLayout(new EpubLayoutOptions(16, 16, font) { Margin = 2, FontSize = 10 });
        Assert.Single(layout.Pages);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, layout.RenderPage(0).Pixels.Slice((2 * 16 + 2) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 12, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        Assert.Single(new PdfPageContentReader(pdf).Read(0).Images);
    }

    [Fact]
    public void RetainedPngImagesCountAgainstPaddedProgressiveJpegStorageBeforeDecoding()
    {
        byte[] png = RgbPng(scanlines: new byte[1024 * (1 + 1024 * 3)], size: 1024);
        string body = string.Concat(Enumerable.Range(0, 7).Select(i => $"<img src='../images/prior{i}.png' alt=''/>") )
            + "<img src='../images/large.jpg' alt=''/>";
        byte[] baseline = Package(png, body, "img{display:block;max-width:1px}");
        using var output = new MemoryStream();
        output.Write(baseline);
        using (var zip = new ZipArchive(output, ZipArchiveMode.Update, true))
        {
            var opf = zip.GetEntry("EPUB/book.opf")!;
            string manifest;
            using (var reader = new StreamReader(opf.Open())) manifest = reader.ReadToEnd();
            opf.Delete();
            string entries = "<item id='large' href='images/large.jpg' media-type='image/jpeg'/>";
            for (int i = 0; i < 7; i++)
            {
                entries += $"<item id='prior{i}' href='images/prior{i}.png' media-type='image/png'/>";
                using var entry = zip.CreateEntry($"EPUB/images/prior{i}.png").Open();
                entry.Write(png);
            }
            using (var writer = new StreamWriter(zip.CreateEntry("EPUB/book.opf").Open()))
                writer.Write(manifest.Replace("</manifest>", entries + "</manifest>"));
            // The SOF2 describes 32768x1 RGB with three 4x4-sampled components.
            // Its padded coefficient and row buffers need 15 MiB before output.
            byte[] jpeg = Convert.FromHexString("FFD8FFC20011080001800003014400024400034400FFDA000C03010002000300000000FFD9");
            using var image = zip.CreateEntry("EPUB/images/large.jpg").Open();
            image.Write(jpeg);
        }
        var book = EpubDocument.Open(output.ToArray());
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        var error = Assert.Throws<InvalidDataException>(() => book.CreateLayout(
            new EpubLayoutOptions(16, 16, font) { Margin = 2, FontSize = 10 }));
        Assert.Contains("storage limit", error.Message);
    }

    [Theory]
    [InlineData(false, "margin")]
    [InlineData(true, "margin")]
    [InlineData(false, "padding")]
    [InlineData(true, "padding")]
    public void SvgViewportFitsInsideBodyMarginsWithoutAnEmptyLeadingPage(bool container, string inset)
    {
        string cover = "<svg xmlns='http://www.w3.org/2000/svg' width='100%' height='100%' viewBox='0 0 2 2'>"
            + "<image width='2' height='2' href='../images/check.png'/></svg>";
        var book = EpubDocument.Open(Package(RgbPng(), container ? "<div>" + cover + "</div>" : cover, "body{" + inset + ":2px}"));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        var layout = book.CreateLayout(new EpubLayoutOptions(16, 16, font) { Margin = 2, FontSize = 6 });
        Assert.Single(layout.Pages);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, layout.RenderPage(0).Pixels.Slice((4 * 16 + 4) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 12, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 4.5 }));
        var image = Assert.Single(new PdfPageContentReader(pdf).Read(0).Images);
        Assert.Equal(3, image.BoundingBox.Left, 6);
        Assert.Equal(9, image.BoundingBox.Top, 6);
        Assert.Equal(6, image.BoundingBox.Width, 6);
        Assert.Equal(6, image.BoundingBox.Height, 6);
    }

    [Theory]
    [InlineData("4 2", "4", "2", 5, 3.75, 8.25, 4.5)]
    [InlineData("2 4", "2", "4", 5, 3.75, 8.25, 4.5)]
    [InlineData("2 2", "2", "2", 2, 1.5, 10.5, 9)]
    public void StaticSvgCoverCentersItsPackagedImageInThePageViewport(string box, string width,
        string height, int start, double left, double top, double extent)
    {
        byte[] png = RgbPng();
        byte[] source = Package(png, $"""
            <svg xmlns='http://www.w3.org/2000/svg' width='100%' height='100%' version='1.1' viewBox='0 0 {box}' preserveAspectRatio='xMidYMid meet' xmlns:xlink='http://www.w3.org/1999/xlink'>
              <image width='{width}' height='{height}' xlink:href='../images/check.png'/>
            </svg>
            """);
        byte[] preserved = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        var layout = book.CreateLayout(new EpubLayoutOptions(16, 16, font) { Margin = 2, FontSize = 10 });
        Assert.Single(layout.Pages);
        Assert.Equal("", book.ReadText(0));
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((start * 16 + start - 1) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((start * 16 + start) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 1, 0, 1 }, native.Pixels.Slice((start * 16 + 8) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 1, 1 }, native.Pixels.Slice((8 * 16 + start) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 12, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var image = Assert.Single(new PdfPageContentReader(pdf).Read(0).Images);
        Assert.Equal(2, image.PixelWidth);
        Assert.Equal(2, image.PixelHeight);
        Assert.Equal(left, image.BoundingBox.Left, 6);
        Assert.Equal(top, image.BoundingBox.Top, 6);
        Assert.Equal(extent, image.BoundingBox.Width, 6);
        Assert.Equal(extent, image.BoundingBox.Height, 6);
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(16, 16));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((start * 16 + start) * 4, 4).ToArray());
        Assert.Equal(png, book.ReadResource("EPUB/images/check.png"));
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("<script/>")]
    [InlineData("<foreignObject/>")]
    [InlineData("<use href='../images/check.png'/>")]
    [InlineData("<image width='2' height='2' href='https://example.invalid/image.png'/>")]
    [InlineData("<image width='2' height='2' href='../images/check.png#fragment'/>")]
    [InlineData("<image width='2' height='2' href='../images/check.png' onload='x'/>")]
    [InlineData("<image width='2' height='2' href='../images/check.png' transform='scale(2)'/>")]
    [InlineData("<image width='2' height='2' href='../images/check.png' opacity='.5'/>")]
    [InlineData("<image width='2' height='2' href='../images/check.png'><animate/></image>")]
    [InlineData("<image width='2' height='2' href='../images/check.png'/><image width='2' height='2' href='../images/check.png'/>")]
    [InlineData("<image width='1' height='2' href='../images/check.png'/>")]
    [InlineData("<image width='2' height='2'/>")]
    [InlineData("")]
    public void SvgCoverRejectsActiveExternalAndUnimplementedContentAtBothPublicSeams(string content)
    {
        var book = EpubDocument.Open(Package(RgbPng(),
            "<svg xmlns='http://www.w3.org/2000/svg' width='100%' height='100%' viewBox='0 0 2 2'>"
            + content + "</svg>"));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(16, 16, font) { Margin = 2, FontSize = 10 }));
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 12, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }));
    }

    [Theory]
    [InlineData("div{padding-top:1px}")]
    [InlineData("div{display:inline}")]
    [InlineData("svg{color:red}")]
    public void UnsupportedSvgContainerGeometryAndCssRemainExplicit(string css)
    {
        var book = EpubDocument.Open(Package(RgbPng(),
            "<div><svg xmlns='http://www.w3.org/2000/svg' width='100%' height='100%' viewBox='0 0 2 2'>"
            + "<image width='2' height='2' href='../images/check.png'/></svg></div>", css));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(16, 16, font)
        { Margin = 2, FontSize = 10 }));
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 12, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }));
    }

    [Theory]
    [InlineData("visibility:hidden")]
    [InlineData("display:none")]
    public void HiddenSvgCoversKeepRasterPaintOutOfNativeAndConvertedPages(string style)
    {
        var book = EpubDocument.Open(Package(RgbPng(),
            "<div style='" + style + "'><svg xmlns='http://www.w3.org/2000/svg' width='100%' height='100%' viewBox='0 0 2 2'>"
            + "<image width='2' height='2' href='../images/check.png'/></svg></div>"));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        var layout = book.CreateLayout(new EpubLayoutOptions(16, 16, font) { Margin = 2, FontSize = 10 });
        Assert.All(layout.RenderPage(0).Pixels.ToArray(), value => Assert.Equal(1f, value));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 12, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        Assert.Empty(new PdfPageContentReader(pdf).Read(0).Images);
    }

    [Fact]
    public void SvgCoverReservesItsViewportBeforeFollowingText()
    {
        var book = EpubDocument.Open(Package(RgbPng(),
            "<svg xmlns='http://www.w3.org/2000/svg' width='100%' height='100%' viewBox='0 0 2 2'>"
            + "<image width='2' height='2' href='../images/check.png'/></svg><p>A</p>"));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        var layout = book.CreateLayout(new EpubLayoutOptions(16, 16, font) { Margin = 2, FontSize = 10 });
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal("", layout.Pages[0].Text);
        Assert.Equal("A", layout.Pages[1].Text);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 12, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(2, reader.PageCount);
        Assert.Single(reader.Read(0).Images);
        Assert.Empty(reader.Read(1).Images);
        Assert.Equal("A", string.Concat(reader.Read(1).Letters.Select(letter => letter.Value)));
    }

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

    private static byte[] Package(byte[] png, string? body = null, string? css = null, string? properties = null)
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
                """.Replace("id=\"chapter\" href=", properties is null ? "id=\"chapter\" href="
                    : "id=\"chapter\" properties='" + properties + "' href=")
                    .Replace("</manifest>", css is null ? "</manifest>"
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
        (string Type, byte[] Data)[]? metadata = null, bool afterImage = false, int size = 2)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = [0, 0, 0, 0, 0, 0, 0, 0, 8, colorType, 0, 0, 0];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)size);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)size);
        Chunk("IHDR", header);
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
