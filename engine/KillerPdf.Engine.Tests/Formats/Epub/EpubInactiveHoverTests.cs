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

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubInactiveHoverTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InactiveSnapshotKeepsNormalAnchorColorAndPlacement(bool convert)
    {
        Verify("a{color:red}a:hover{color:blue;text-align:center;font-size:20px}",
            "<p><a>A</a></p>", convert);
    }

    [Theory]
    [InlineData(":hover")]
    [InlineData("a:HoVeR")]
    [InlineData("a.note:hover")]
    [InlineData("a#link:hover")]
    [InlineData("a[id]:hover")]
    [InlineData("p a.note:hover")]
    [InlineData("p>a.note:hover")]
    [InlineData("p:hover>a#link")]
    [InlineData("p:hover a")]
    [InlineData("p.note:hover a#link")]
    public void SupportedBaseCompoundsAndDescendantsStayInactive(string selector)
    {
        string css = "a{color:red}" + selector + "{color:blue;font-size:20px;text-align:center}";
        const string body = "<p class='note'><a class='note' id='link'>A</a></p>";
        Verify(css, body, false);
        Verify(css, body, true);
    }

    [Fact]
    public void SelectorGroupRetainsNormalBranchAndOrdinaryCascade()
    {
        const string css = "a{color:blue}a,a:hover{color:red}a:hover{color:lime;font-size:20px;text-align:center}";
        Verify(css, "<p><a>A</a></p>", false);
        Verify(css, "<p><a>A</a></p>", true);
    }

    [Theory]
    [InlineData("a:unknown")]
    [InlineData("a:unknown:hover")]
    [InlineData("a:hover:unknown")]
    [InlineData("a::hover")]
    [InlineData("a:hover()")]
    [InlineData("a:hover:hover")]
    [InlineData("a:hover.note")]
    [InlineData("a+.note:hover")]
    [InlineData("a..note:hover")]
    [InlineData("a:hover,")]
    public void UnknownAndMalformedSelectorsKeepExplicitDiagnostic(string selector)
    {
        var book = EpubDocument.Open(Book("a{color:red}" + selector + "{color:blue}", "<p><a>A</a></p>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        const string diagnostic = "Native EPUB CSS selector is not implemented.";
        Assert.Equal(diagnostic, Assert.Throws<NotSupportedException>(() => book.CreateLayout(
            new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 })).Message);
        Assert.Equal(diagnostic, Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(
            new EpubPdfConversionOptions(font))).Message);
    }

    private static void Verify(string css, string body, bool convert)
    {
        string loaded = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EpubDocument).Assembly.Location)));
        Console.WriteLine("Loaded engine SHA256: " + loaded);
        string? expected = Environment.GetEnvironmentVariable("EPUB_EXPECTED_ENGINE_SHA256");
        if (expected is not null) Assert.Equal(expected, loaded);
        byte[] source = Book(css, body), original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        if (!convert)
        {
            var layout = book.CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 });
            var page = Assert.Single(layout.Pages);
            Assert.Equal("A", page.Text);
            Assert.Equal(new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10) { Color = new PdfRgbColor(1, 0, 0) },
                Assert.Single(page.TextPlacements));
            var frame = layout.RenderPage(0);
            Assert.Equal(new float[] { 1, 0, 0, 1 }, frame.Pixels.Slice((5 * 30 + 7) * 4, 4).ToArray());
        }
        else
        {
            var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
            { PageWidthPoints = 22.5, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }));
            var reader = new PdfPageContentReader(pdf);
            Assert.Equal(1, reader.PageCount);
            var letter = Assert.Single(reader.Read(0).Letters);
            Assert.Equal("A", letter.Value);
            Assert.Equal(1.5, letter.StartBaseLine.X);
            Assert.Equal(4.5, letter.StartBaseLine.Y);
            var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 16));
            Assert.Empty(frame.Diagnostics);
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, frame.Pixels.Slice((5 * 30 + 7) * 4, 4).ToArray());
        }
        Assert.Equal(original, source);
        Assert.Equal(Encoding.UTF8.GetBytes(css), book.ReadResource("style.css"));
    }

    private static byte[] Book(string css, string body)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"book.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            Add("book.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">urn:test:hover</dc:identifier><dc:title>Hover</dc:title><dc:language>en</dc:language></metadata><manifest><item id=\"doc\" href=\"text.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"css\" href=\"style.css\" media-type=\"text/css\"/></manifest><spine><itemref idref=\"doc\"/></spine></package>");
            Add("text.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><link rel=\"stylesheet\" href=\"style.css\"/></head><body>" + body + "</body></html>");
            Add("style.css", css);
            void Add(string path, string text)
            {
                using var stream = zip.CreateEntry(path, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return buffer.ToArray();
    }
}
