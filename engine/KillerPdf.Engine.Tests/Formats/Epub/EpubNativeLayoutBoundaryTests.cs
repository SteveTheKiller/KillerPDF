using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubNativeLayoutBoundaryTests
{
    [Fact]
    public void HonorsCancellationBeforeCreatingLayout()
    {
        var book = Book();

        Assert.Throws<OperationCanceledException>(() =>
            book.CreateLayout(Options(book), new CancellationToken(true)));
    }

    [Fact]
    public void HonorsCancellationBeforeRenderingPage()
    {
        var book = Book();
        var layout = book.CreateLayout(Options(book));

        Assert.Throws<OperationCanceledException>(() => layout.RenderPage(0, new CancellationToken(true)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void RejectsInvalidPageIndexes(int index)
    {
        var book = Book();
        var layout = book.CreateLayout(Options(book));

        Assert.Throws<ArgumentOutOfRangeException>(() => layout.RenderPage(index));
    }

    [Theory]
    [InlineData(double.NaN, 16, 2, 10)]
    [InlineData(0, 16, 2, 10)]
    [InlineData(32769, 16, 2, 10)]
    [InlineData(30, double.PositiveInfinity, 2, 10)]
    [InlineData(30, 0, 2, 10)]
    [InlineData(30, 32769, 2, 10)]
    [InlineData(30, 16, double.NaN, 10)]
    [InlineData(30, 16, double.PositiveInfinity, 10)]
    [InlineData(30, 16, -1, 10)]
    [InlineData(30, 16, 2, double.NaN)]
    [InlineData(30, 16, 2, double.PositiveInfinity)]
    [InlineData(30, 16, 2, 0)]
    [InlineData(30, 16, 15, 10)]
    [InlineData(30, 16, 2, 13)]
    public void RejectsInvalidPageAndTypographyOptions(double width, double height, double margin, double fontSize)
    {
        var book = Book();
        var options = Options(book) with {
            PageWidth = width, PageHeight = height, Margin = margin, FontSize = fontSize };

        Assert.Throws<ArgumentOutOfRangeException>(() => book.CreateLayout(options));
    }

    [Fact]
    public void RejectsKnownCombinedPixelBudgetOverflowDuringLayout()
    {
        var book = Book();
        var options = Options(book) with { PageWidth = 4096, PageHeight = 4096 };

        // 4096 squared at 32 bytes per pixel needs 512 MiB, exceeding the 128 MiB contract.
        // No render call or large image fixture is needed to exercise this preallocation boundary.
        Assert.Throws<InvalidDataException>(() => book.CreateLayout(options));
    }

    [Theory]
    [InlineData("<p style='color:red'>A</p>", "", false, false)]
    [InlineData("<p>A</p>", "<style>p { color:red }</style>", false, false)]
    [InlineData("<p>A</p>", "<link rel='stylesheet' href='style.css'/>", true, false)]
    [InlineData("<p><a href='text.xhtml'>A</a></p>", "", false, false)]
    [InlineData("<p>A</p>", "<script type='text/javascript'>void 0;</script>", false, true)]
    [InlineData("<p>A</p><p>A</p>", "", false, false)]
    [InlineData("<p>\u00e9</p>", "", false, false)]
    [InlineData("<p>B</p>", "", false, false)]
    public void DiagnosesUnsupportedContentWithoutSilentlyOmittingIt(string body, string head, bool css, bool scripted)
    {
        var book = Book(body: body, head: head, css: css, scripted: scripted);

        Assert.Throws<NotSupportedException>(() => book.CreateLayout(Options(book)));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void DiagnosesFixedMultipleAndNonlinearSpines(bool fixedLayout, bool multipleSpines, bool nonlinear)
    {
        var book = Book(fixedLayout: fixedLayout, multipleSpines: multipleSpines, nonlinear: nonlinear);

        Assert.Throws<NotSupportedException>(() => book.CreateLayout(Options(book)));
    }

    [Fact]
    public void DiagnosesOneLineOverflowWithoutWrapping()
    {
        var book = Book();

        Assert.Throws<NotSupportedException>(() => book.CreateLayout(Options(book) with { PageWidth = 20 }));
    }

    [Theory]
    [InlineData("<html xmlns='http://www.w3.org/1999/xhtml'><body><p>A</body></html>")]
    [InlineData("<!DOCTYPE html [<!ENTITY text 'A'>]><html xmlns='http://www.w3.org/1999/xhtml'><body><p>&text;</p></body></html>")]
    public void RejectsMalformedOrDtdContainingXhtml(string xhtml)
    {
        var book = Book(xhtml: xhtml);

        Assert.Throws<InvalidDataException>(() => book.CreateLayout(Options(book)));
    }

    [Fact]
    public void RepeatedRenderingPreservesAlreadyReturnedFramePixels()
    {
        var book = Book();
        var layout = book.CreateLayout(Options(book));
        var first = layout.RenderPage(0);
        float[] retained = first.Pixels.ToArray();

        var second = layout.RenderPage(0);

        Assert.Equal(retained, first.Pixels.ToArray());
        Assert.Equal(retained, second.Pixels.ToArray());
    }

    [Fact]
    public void PagesAndSourcePlacementsAreReadOnlyCollections()
    {
        var book = Book();
        var layout = book.CreateLayout(Options(book));
        var page = Assert.Single(layout.Pages);
        var pages = Assert.IsAssignableFrom<IList<EpubLayoutPage>>(layout.Pages);
        var placements = Assert.IsAssignableFrom<IList<EpubTextPlacement>>(page.TextPlacements);

        Assert.True(pages.IsReadOnly);
        Assert.True(placements.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => pages.Clear());
        Assert.Throws<NotSupportedException>(() => placements.Clear());
        Assert.Same(page, Assert.Single(layout.Pages));
        Assert.Equal("A A", page.Text);
    }

    private static EpubLayoutOptions Options(EpubDocument book) =>
        new(30, 16, TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"))) { FontSize = 10, Margin = 2 };

    private static EpubDocument Book(string body = "<p>A A</p>", string head = "", bool css = false,
        bool scripted = false, bool fixedLayout = false, bool multipleSpines = false,
        bool nonlinear = false, string? xhtml = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            string layoutMetadata = fixedLayout ? "<meta property='rendition:layout'>pre-paginated</meta>" : "";
            string properties = scripted ? " properties='scripted'" : "";
            string cssItem = css ? "<item id='css' href='style.css' media-type='text/css'/>" : "";
            string secondItem = multipleSpines ? "<item id='second' href='second.xhtml' media-type='application/xhtml+xml'/>" : "";
            string secondSpine = multipleSpines ? "<itemref idref='second'/>" : "";
            string linear = nonlinear ? " linear='no'" : "";
            Add("EPUB/book.opf", $"""
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:native-boundary</dc:identifier><dc:title>Native boundaries</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-03T00:00:00Z</meta>{layoutMetadata}</metadata>
                  <manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"{properties}/><item id="font" href="fonts/test.ttf" media-type="font/ttf"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>{cssItem}{secondItem}</manifest>
                  <spine><itemref idref="chapter"{linear}/>{secondSpine}</spine>
                </package>
                """);
            string viewport = fixedLayout ? "<meta name='viewport' content='width=30,height=16'/>" : "";
            Add("EPUB/text.xhtml", xhtml ?? $"<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Boundaries</title>{viewport}{head}</head><body>{body}</body></html>");
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="text.xhtml">Boundaries</a></li></ol></nav></body></html>
                """);
            if (css) Add("EPUB/style.css", "p { color:red }");
            if (multipleSpines)
                Add("EPUB/second.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Second</title></head><body><p>A</p></body></html>");
            using var font = zip.CreateEntry("EPUB/fonts/test.ttf", CompressionLevel.NoCompression).Open();
            font.Write(TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true));

            void Add(string path, string content, CompressionLevel compression = CompressionLevel.Optimal)
            {
                using var stream = zip.CreateEntry(path, compression).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return EpubDocument.Open(output.ToArray());
    }
}
