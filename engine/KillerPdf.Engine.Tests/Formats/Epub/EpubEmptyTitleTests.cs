using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;
using Xunit.Abstractions;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubEmptyTitleTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyHeadingTitlePreservesTextAndLiteralGeometry(bool convert)
    {
        output.WriteLine("Loaded engine SHA256: " + Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(typeof(EpubDocument).Assembly.Location))));
        byte[] source = Book("title=''");
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);
        byte[] chapter = book.ReadResource("doc.xhtml");
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        Assert.Equal(800, font.Ascender);
        Assert.Equal(1000, font.UnitsPerEm);
        Assert.Equal("A\n\nA", book.ReadText(0));
        if (convert)
        {
            var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
            { PageWidthPoints = 30, PageHeightPoints = 37.5, MarginPoints = 1.5, FontSizePoints = 7.5 }));
            var letters = new PdfPageContentReader(pdf).Read(0).Letters;
            Assert.Equal(new[] { "A", "A" }, letters.Select(item => item.Value));
            Assert.Equal(new double[] { 1.5, 1.5 }, letters.Select(item => item.StartBaseLine.X));
            Assert.Equal(new double[] { 30, 22.5 }, letters.Select(item => item.StartBaseLine.Y));
            Assert.Equal(new double[] { 4.5, 4.5 }, letters.Select(item => item.EndBaseLine.X - item.StartBaseLine.X));
        }
        else
        {
            var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(40, 50, font)
            { FontSize = 10, Margin = 2 }).Pages);
            Assert.Equal("A\n\nA", page.Text);
            Assert.Equal(new[] { 0, 3 }, page.TextPlacements.Select(item => item.Start));
            Assert.Equal(new double[] { 2, 2 }, page.TextPlacements.Select(item => item.Left));
            Assert.Equal(new double[] { 2, 12 }, page.TextPlacements.Select(item => item.Top));
            Assert.Equal(new double[] { 10, 10 }, page.TextPlacements.Select(item => item.Height));
            Assert.Equal(new double[] { 6, 6 }, page.TextPlacements.Select(item => item.Width));
        }
        Assert.Equal(original, source);
        Assert.Equal(chapter, book.ReadResource("doc.xhtml"));
    }

    [Fact]
    public void EmptyTitleRemainsAvailableToCssAttributeSelectors()
    {
        var book = EpubDocument.Open(Book("title=''", "h2[title='']{color:red}"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(40, 50, font)
        { FontSize = 10, Margin = 2 }).Pages);
        Assert.Equal(new PdfRgbColor(1, 0, 0), page.TextPlacements[0].Color);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 30, PageHeightPoints = 37.5, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(40, 50));
        Assert.Empty(rendered.Diagnostics);
        Assert.Contains(rendered.Pixels.ToArray().Chunk(4), pixel => pixel[2] > 0
            && pixel[0] == 0 && pixel[1] == 0 && pixel[3] > 0);
    }

    [Theory]
    [InlineData("title='Advisory'")]
    [InlineData("title=' '")]
    [InlineData("xmlns:t='urn:test' t:title=''")]
    [InlineData("data-unknown=''")]
    public void OtherHeadingMetadataRemainsDiagnosed(string attributes)
    {
        var book = EpubDocument.Open(Book(attributes));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        Assert.Contains("attributes", Assert.Throws<NotSupportedException>(() => book.CreateLayout(
            new EpubLayoutOptions(40, 50, font) { FontSize = 10, Margin = 2 })).Message);
        Assert.Contains("attributes", Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(
            new EpubPdfConversionOptions(font))).Message);
    }

    private static byte[] Book(string attributes, string? css = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='package.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add("package.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>empty-title</dc:identifier><dc:title>Empty title</dc:title><dc:language>en</dc:language></metadata><manifest><item id='doc' href='doc.xhtml' media-type='application/xhtml+xml'/>" + (css is null ? "" : "<item id='css' href='style.css' media-type='text/css'/>") + "</manifest><spine><itemref idref='doc'/></spine></package>");
            const string typography = "font-size:10px;font-weight:normal;margin:0";
            Add("doc.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'>" + (css is null ? "" : "<head><link rel='stylesheet' href='style.css'/></head>") + "<body><h2 " + attributes + " style='" + typography + "'>A</h2><p style='" + typography + "'>A</p></body></html>");
            if (css is not null) Add("style.css", css);
            void Add(string name, string value)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(value));
            }
        }
        return output.ToArray();
    }
}
