using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;
using Xunit.Abstractions;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubAllShorthandTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("inherit", false, 42, 31.5)]
    [InlineData("inherit", true, 42, 31.5)]
    [InlineData("initial", false, 22, 16.5)]
    [InlineData("initial", true, 22, 16.5)]
    public void AllResetsParticipateInCascadeAndUseComputedParentMargins(string keyword,
        bool convert, double nativeLeft, double pdfLeft)
    {
        output.WriteLine("Loaded engine SHA256: " + Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(typeof(EpubDocument).Assembly.Location))));
        byte[] source = Book("body{font-size:20px;margin:0 0 0 1em}"
            + ".pick{margin-left:9px;all:" + keyword + ";font-size:.5em;display:block}"
            + "h2{font-size:30px;margin:9px}", "<h2 class='pick'>A</h2>");
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);
        byte[] chapter = book.ReadResource("doc.xhtml");
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        Assert.Equal(800, font.Ascender);
        Assert.Equal(1000, font.UnitsPerEm);
        Assert.Equal("A", book.ReadText(0));
        if (convert)
        {
            var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
            { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 1.5, FontSizePoints = 7.5 }));
            var letter = Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters);
            Assert.Equal("A", letter.Value);
            Assert.Equal(pdfLeft, letter.StartBaseLine.X);
            Assert.Equal(67.5, letter.StartBaseLine.Y);
            Assert.Equal(4.5, letter.EndBaseLine.X - letter.StartBaseLine.X);
            Assert.EndsWith("KillerTest", letter.FontName);
        }
        else
        {
            var placement = Assert.Single(Assert.Single(book.CreateLayout(
                new EpubLayoutOptions(100, 100, font) { FontSize = 10, Margin = 2 }).Pages).TextPlacements);
            Assert.Equal(0, placement.Start);
            Assert.Equal(nativeLeft, placement.Left);
            Assert.Equal(2, placement.Top);
            Assert.Equal(10, placement.Height);
            Assert.Equal(6, placement.Width);
        }
        Assert.Equal(original, source);
        Assert.Equal(chapter, book.ReadResource("doc.xhtml"));
    }

    [Theory]
    [InlineData(".pick{margin-left:8px;all:initial;display:block}", 2)]
    [InlineData(".pick{all:initial;display:block;margin-left:8px}", 10)]
    [InlineData(".pick{margin-left:8px;display:block}p{all:initial}", 10)]
    [InlineData("p{all:initial}.pick{margin-left:8px;display:block}", 10)]
    public void ShorthandDeclarationOrderAndPropertySpecificityRemainIndependent(string css, double left)
    {
        var layout = Layout("body{margin:0;font-size:10px}" + css, "<p class='pick'>A</p>");
        var placement = Assert.Single(Assert.Single(layout.Pages).TextPlacements);
        Assert.Equal(left, placement.Left);
        Assert.Equal(2, placement.Top);
        Assert.Equal(10, placement.Height);
    }

    [Fact]
    public void InlineShorthandOverridesMoreSpecificStylesheetAndKeepsPageInset()
    {
        var placement = Assert.Single(Assert.Single(Layout("body{margin:0}.pick{margin-left:8px}",
            "<p class='pick' style='all:initial;display:block'>A</p>").Pages).TextPlacements);
        Assert.Equal(2, placement.Left);
        Assert.Equal(2, placement.Top);
    }

    [Theory]
    [InlineData("20px")]
    [InlineData("2")]
    public void InitialNormalLineHeightClearsBothParentLengthAndUnitlessFactor(string lineHeight)
    {
        var page = Assert.Single(Layout("body{font-size:10px;margin:0;line-height:" + lineHeight + "}p{all:initial;display:block}",
            "<p>A<br/>A</p>").Pages);
        Assert.Equal(new double[] { 2, 12 }, page.TextPlacements.Select(p => p.Top));
        Assert.Equal(new double[] { 10, 10 }, page.TextPlacements.Select(p => p.Height));
    }

    [Theory]
    [InlineData("20px", 2, 22)]
    [InlineData("2em", 2, 22)]
    [InlineData("2", 12, 52)]
    public void InheritedLineHeightKeepsComputedPixelsOrUnitlessFactor(string lineHeight, double firstTop, double secondTop)
    {
        var page = Assert.Single(Layout("body{font-size:10px;margin:0;line-height:" + lineHeight + "}p{all:inherit;font-size:20px}",
            "<p>A<br/>A</p>").Pages);
        Assert.Equal(new double[] { firstTop, secondTop }, page.TextPlacements.Select(p => p.Top));
        Assert.Equal(new double[] { 20, 20 }, page.TextPlacements.Select(p => p.Height));
    }

    [Theory]
    [InlineData("initial", false)]
    [InlineData("inherit", true)]
    public void FamilyAndColorResetOrUseActualParent(string keyword, bool inherits)
    {
        var families = new List<string?>();
        var font = Font();
        var book = EpubDocument.Open(Book("body{margin:0;font-family:ParentFace;color:red}p{all:" + keyword + ";display:block}", "<p>A</p>"));
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font)
        {
            FontSize = 10, Margin = 2, TextColor = new(0, 1, 0),
            FontFaceResolver = request => { families.Add(request.Family?.Name); return new(font, request.Weight, EpubFontMatch.Exact); }
        });
        Assert.Equal(inherits ? new PdfRgbColor(1, 0, 0) : new PdfRgbColor(0, 0, 0), Assert.Single(Assert.Single(layout.Pages).TextPlacements).Color);
        Assert.Contains(inherits ? "ParentFace" : null, families);
        if (!inherits) Assert.DoesNotContain("ParentFace", families);
    }

    [Fact]
    public void InheritedColorUsesCallerFallbackWithoutChangingOrdinaryNullableColor()
    {
        var font = Font();
        var ordinary = EpubDocument.Open(Book("p{margin:0}", "<p>A</p>")).CreateLayout(new EpubLayoutOptions(100, 100, font)
        { FontSize = 10, Margin = 2, TextColor = new(0, 1, 0) });
        Assert.Null(Assert.Single(Assert.Single(ordinary.Pages).TextPlacements).Color);
        var inherited = EpubDocument.Open(Book("p{all:inherit;display:block}", "<p>A</p>")).CreateLayout(new EpubLayoutOptions(100, 100, font)
        { FontSize = 10, Margin = 2, TextColor = new(0, 1, 0) });
        Assert.Equal(new PdfRgbColor(0, 1, 0), Assert.Single(Assert.Single(inherited.Pages).TextPlacements).Color);
    }

    [Theory]
    [InlineData("initial")]
    [InlineData("inherit")]
    public void BackgroundMarkerWinsOverEarlierPaintWithoutChangingAncestorPaint(string keyword)
    {
        var layout = Layout("body{margin:0;background-color:blue}p{background-color:red;all:" + keyword + ";display:block}", "<p>A</p>");
        var frame = layout.RenderPage(0);
        Assert.Equal(new float[] { 0, 0, 1, 1 }, frame.Pixels.Slice((3 * 100 + 80) * 4, 4).ToArray());
    }

    [Theory]
    [InlineData("initial")]
    [InlineData("inherit")]
    public void ExplicitZeroMarginsSuppressParagraphGapFallback(string keyword)
    {
        var page = Assert.Single(Layout("body{margin:0}p{all:" + keyword + ";display:block}", "<p>A</p><p>A</p>").Pages);
        Assert.Equal(new double[] { 2, 12 }, page.TextPlacements.Select(p => p.Top));
    }

    [Theory]
    [InlineData("all:unset")]
    [InlineData("all:revert")]
    [InlineData("all:initial;position:absolute")]
    [InlineData("all:initial;height:10px")]
    [InlineData("all:initial;font-family:inherit")]
    public void UnsupportedShorthandValuesAndNewLonghandKeywordsStayRefused(string declaration)
    {
        var book = EpubDocument.Open(Book("p{" + declaration + "}", "<p>A</p>"));
        var font = Font();
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(100, 100, font) { FontSize = 10, Margin = 2 }));
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)));
    }

    private static TrueTypeFont Font() => TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
    private static EpubLayoutDocument Layout(string css, string body) => EpubDocument.Open(Book(css, body)).CreateLayout(
        new EpubLayoutOptions(100, 100, Font()) { FontSize = 10, Margin = 2 });

    private static byte[] Book(string css, string body)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='package.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add("package.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>all-cascade</dc:identifier><dc:title>All cascade</dc:title><dc:language>en</dc:language></metadata><manifest><item id='doc' href='doc.xhtml' media-type='application/xhtml+xml'/><item id='css' href='style.css' media-type='text/css'/></manifest><spine><itemref idref='doc'/></spine></package>");
            Add("doc.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head><link rel='stylesheet' href='style.css'/></head><body>" + body + "</body></html>");
            Add("style.css", css);
            void Add(string name, string value)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(value));
            }
        }
        return output.ToArray();
    }
}
