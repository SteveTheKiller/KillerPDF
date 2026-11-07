using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;
using Xunit.Abstractions;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubAutoHeightTests(ITestOutputHelper output)
{
    [Fact]
    public void PercentageContainerKeepsContentHeightAtBothPublicSeams()
    {
        output.WriteLine("Loaded engine SHA256: " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EpubDocument).Assembly.Location))));
        Check("div.cover{height:100%}");
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("AuTo")]
    [InlineData("0%")]
    [InlineData("50%")]
    [InlineData("200%")]
    [InlineData("32768%")]
    public void AutomaticAndPercentageHeightsDoNotReserveSpace(string value) => Check("div.cover{height:" + value + "}");

    [Fact]
    public void NestedPercentagesAndEmptyContainerKeepContentFlow() =>
        Check("div{height:200%}", "<div><div><div/></div><div><p>A</p></div></div><p>A</p>");

    [Fact]
    public void BackgroundEndsAtContentHeight()
    {
        var book = EpubDocument.Open(Book("<div class='cover'><p>A</p></div><p>A</p>",
            "p{font-size:10px;font-weight:normal;margin:0}div.cover{height:100%;background-color:blue}"));
        var font = Font();
        var native = book.CreateLayout(NativeOptions(font)).RenderPage(0);
        Assert.Equal(new float[] { 0, 0, 1, 1 }, native.Pixels.Slice((3 * 40 + 30) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((13 * 40 + 30) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(PdfOptions(font)));
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(40, 50));
        Assert.Empty(saved.Diagnostics);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, saved.Pixels.Slice((3 * 40 + 30) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, saved.Pixels.Slice((13 * 40 + 30) * 4, 4).ToArray());
    }

    [Theory]
    [InlineData("html{height:100%}.clear{height:auto}")]
    [InlineData("html.clear{height:auto}html{height:100%}")]
    [InlineData("html.clear{height:auto}html{width:auto;height:100%}")]
    [InlineData("html.clear{height:100%}html.clear{height:auto}")]
    public void RootHeightCascadeUsesIndependentSpecificityAndSourceOrder(string css) => Check(css, rootClass: "clear");

    [Theory]
    [InlineData("div.cover{height:50%}", "auto")]
    [InlineData("div.cover{height:auto}", "200%")]
    public void InlineHeightOverridesTheStylesheet(string css, string inline) =>
        Check(css, "<div class='cover' style='height:" + inline + "'><p>A</p></div><p>A</p>");

    [Theory]
    [InlineData("html{height:0%}")]
    [InlineData("html{height:100%}")]
    [InlineData("html.clear{height:100%}html{height:auto}")]
    public void RootPercentageHeightIsExplicitlyRefused(string css)
    {
        var book = EpubDocument.Open(Book("<p>A</p>", css, "clear"));
        var font = Font();
        Assert.Equal("Native EPUB root percentage heights are not implemented.", Assert.Throws<NotSupportedException>(() => book.CreateLayout(NativeOptions(font))).Message);
        Assert.Equal("Native EPUB root percentage heights are not implemented.", Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(PdfOptions(font))).Message);
    }

    [Fact]
    public void SuppressedRootPercentageDoesNotPaintOrLayout()
    {
        var book = EpubDocument.Open(Book("<p>A</p>", "html{height:100%;display:none}"));
        var font = Font();
        Assert.Empty(Assert.Single(book.CreateLayout(NativeOptions(font)).Pages).TextPlacements);
        var pdf = PdfDocument.Open(book.ConvertToPdf(PdfOptions(font)));
        Assert.Empty(new PdfPageContentReader(pdf).Read(0).Letters);
    }

    [Theory]
    [InlineData("height:10px")]
    [InlineData("height:0")]
    [InlineData("height:1em")]
    [InlineData("height:-1%")]
    [InlineData("height:NaN%")]
    [InlineData("height:Infinity%")]
    [InlineData("height:32769%")]
    [InlineData("height:calc(100%)")]
    [InlineData("height:inherit")]
    [InlineData("height:initial")]
    [InlineData("height:unset")]
    [InlineData("height:min-content")]
    [InlineData("height:auto;position:absolute")]
    [InlineData("height:auto;position:fixed")]
    [InlineData("height:auto;float:left")]
    public void UnsupportedHeightAndPositioningStillRefuse(string declaration)
    {
        var book = EpubDocument.Open(Book("<div><p>A</p></div>", "div{" + declaration + "}"));
        var font = Font();
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(NativeOptions(font)));
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(PdfOptions(font)));
    }

    private static void Check(string css, string? body = null, string rootClass = "")
    {
        byte[] source = Book(body ?? "<div class='cover'><p>A</p></div><p>A</p>", "p{font-size:10px;font-weight:normal;margin:0}" + css, rootClass);
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);
        byte[] chapter = book.ReadResource("doc.xhtml");
        var font = Font();
        Assert.Equal(800, font.Ascender);
        Assert.Equal(1000, font.UnitsPerEm);
        var page = Assert.Single(book.CreateLayout(NativeOptions(font)).Pages);
        Assert.Equal("A\n\nA", page.Text);
        Assert.Equal(new[] { 0, 3 }, page.TextPlacements.Select(p => p.Start));
        Assert.Equal(new double[] { 2, 2 }, page.TextPlacements.Select(p => p.Left));
        Assert.Equal(new double[] { 2, 12 }, page.TextPlacements.Select(p => p.Top));
        Assert.Equal(new double[] { 10, 10 }, page.TextPlacements.Select(p => p.Height));
        Assert.Equal(new double[] { 6, 6 }, page.TextPlacements.Select(p => p.Width));
        var pdf = PdfDocument.Open(book.ConvertToPdf(PdfOptions(font)));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        var letters = reader.Read(0).Letters;
        Assert.Equal(new[] { "A", "A" }, letters.Select(p => p.Value));
        Assert.Equal(new double[] { 1.5, 1.5 }, letters.Select(p => p.StartBaseLine.X));
        Assert.Equal(new double[] { 30, 22.5 }, letters.Select(p => p.StartBaseLine.Y));
        Assert.Equal(new double[] { 4.5, 4.5 }, letters.Select(p => p.EndBaseLine.X - p.StartBaseLine.X));
        Assert.Equal(original, source);
        Assert.Equal(chapter, book.ReadResource("doc.xhtml"));
    }

    private static TrueTypeFont Font() => TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
    private static EpubLayoutOptions NativeOptions(TrueTypeFont font) => new(40, 50, font) { FontSize = 10, Margin = 2 };
    private static EpubPdfConversionOptions PdfOptions(TrueTypeFont font) => new(font)
    { PageWidthPoints = 30, PageHeightPoints = 37.5, MarginPoints = 1.5, FontSizePoints = 7.5 };
    private static byte[] Book(string body, string css, string rootClass = "")
    {
        using var result = new MemoryStream();
        using (var zip = new ZipArchive(result, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='package.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add("package.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>auto-height</dc:identifier><dc:title>Auto height</dc:title><dc:language>en</dc:language></metadata><manifest><item id='doc' href='doc.xhtml' media-type='application/xhtml+xml'/><item id='css' href='style.css' media-type='text/css'/></manifest><spine><itemref idref='doc'/></spine></package>");
            Add("doc.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'" + (rootClass.Length == 0 ? "" : " class='" + rootClass + "'") + "><head><link rel='stylesheet' href='style.css'/></head><body>" + body + "</body></html>");
            Add("style.css", css);
            void Add(string name, string value)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(value));
            }
        }
        return result.ToArray();
    }
}
