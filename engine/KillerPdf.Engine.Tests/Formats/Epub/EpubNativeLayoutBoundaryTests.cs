using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
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
    [InlineData("<p>A</p>", "<style>p { color:red }</style>", false, false)]
    [InlineData("<p><a href='text.xhtml'>A</a></p>", "", false, false)]
    [InlineData("<p>A</p>", "<script type='text/javascript'>void 0;</script>", false, true)]
    [InlineData("<p>\u00e9</p>", "", false, false)]
    [InlineData("<p>B</p>", "", false, false)]
    public void DiagnosesUnsupportedContentWithoutSilentlyOmittingIt(string body, string head, bool css, bool scripted)
    {
        var book = Book(body: body, head: head, css: css, scripted: scripted);

        Assert.Throws<NotSupportedException>(() => book.CreateLayout(Options(book)));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    public void DiagnosesFixedAndNonlinearSpines(bool fixedLayout, bool multipleSpines, bool nonlinear)
    {
        var book = Book(fixedLayout: fixedLayout, multipleSpines: multipleSpines, nonlinear: nonlinear);

        Assert.Throws<NotSupportedException>(() => book.CreateLayout(Options(book)));
    }

    [Fact]
    public void MultipleLinearSpinesStartFreshPagesWithOriginalEntryOffsets()
    {
        var book = Book(multipleSpines: true);
        var layout = book.CreateLayout(Options(book));

        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(new[] { "A A", "A" }, layout.Pages.Select(page => page.Text));
        Assert.Equal(new[] { 0, 1 }, layout.Pages.Select(page => page.SpineIndex));
        Assert.Equal(new[] { 0, 0 }, layout.Pages.Select(page => page.TextStart));
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10),
            new EpubTextPlacement(0, 1, 1, 8, 2, 5, 10),
            new EpubTextPlacement(0, 2, 1, 13, 2, 6, 10) }, layout.Pages[0].TextPlacements);
        Assert.Equal(new[] { new EpubTextPlacement(1, 0, 1, 2, 2, 6, 10) }, layout.Pages[1].TextPlacements);
    }

    [Fact]
    public void PaginatesWrappedLineOverflowWithoutChangingSourceOffsets()
    {
        var book = Book();

        var layout = book.CreateLayout(Options(book) with { PageWidth = 20 });

        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(new[] { "A ", "A" }, layout.Pages.Select(page => page.Text));
        Assert.Equal(new[] { 0, 2 }, layout.Pages.Select(page => page.TextStart));
        Assert.Equal(new[] { new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10) }, layout.Pages[0].TextPlacements);
        Assert.Equal(new[] { new EpubTextPlacement(0, 2, 1, 2, 2, 6, 10) }, layout.Pages[1].TextPlacements);
    }

    [Fact]
    public void CanonicalXhtml11DeclarationPreservesOriginalTextInLayout()
    {
        var book = Book(xhtml: """
            <!DOCTYPE html PUBLIC '-//W3C//DTD XHTML 1.1//EN' 'http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd'>
            <html xmlns='http://www.w3.org/1999/xhtml'><head><title>Boundaries</title></head><body><p>A A</p></body></html>
            """);

        var layout = book.CreateLayout(Options(book));

        var page = Assert.Single(layout.Pages);
        Assert.Equal("A A", page.Text);
    }

    [Fact]
    public void MalformedLateHeadIsRejectedInsteadOfIgnoringItsStylesheet()
    {
        var book = Book(css: true, xhtml: """
            <html xmlns='http://www.w3.org/1999/xhtml'>
              <head><title>Boundaries</title></head><body><p>A A</p></body>
              <head><link rel='stylesheet' href='style.css'/></head>
            </html>
            """);
        Assert.Equal("p { color:red }", Encoding.UTF8.GetString(book.ReadResource("EPUB/style.css")));
        Assert.Equal("A A", book.ReadText(0));

        Assert.Throws<InvalidDataException>(() => book.CreateLayout(Options(book)));
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

    [Fact]
    public void LocalHeadIconMetadataPreservesBodyLayoutAndResourceBytes()
    {
        var book = Book(head: "<link href='cover.png' rel='icon' type='image/x-cover' id='id-cover'/>", icon: true);
        byte[] before = book.ReadResource("EPUB/cover.png");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, before);
        Assert.Equal("A A", book.ReadText(0));

        var layout = book.CreateLayout(Options(book));

        var page = Assert.Single(layout.Pages);
        Assert.Equal("A A", page.Text);
        Assert.Equal(0, page.SpineIndex);
        Assert.Equal(0, page.TextStart);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10),
            new EpubTextPlacement(0, 1, 1, 8, 2, 5, 10),
            new EpubTextPlacement(0, 2, 1, 13, 2, 6, 10) }, page.TextPlacements);

        var frame = layout.RenderPage(0);

        Assert.Equal(30, frame.Width);
        Assert.Equal(16, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((5 * 30 + 7) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice(15 * 30 * 4, 4).ToArray());
        Assert.Equal(before, book.ReadResource("EPUB/cover.png"));
    }

    private static EpubLayoutOptions Options(EpubDocument book) =>
        new(30, 16, TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"))) { FontSize = 10, Margin = 2 };

    private static EpubDocument Book(string body = "<p>A A</p>", string head = "", bool css = false,
        bool scripted = false, bool fixedLayout = false, bool multipleSpines = false,
        bool nonlinear = false, string? xhtml = null, bool icon = false)
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
            string iconItem = icon ? "<item id='cover' href='cover.png' media-type='image/png'/>" : "";
            string secondItem = multipleSpines ? "<item id='second' href='second.xhtml' media-type='application/xhtml+xml'/>" : "";
            string secondSpine = multipleSpines ? "<itemref idref='second'/>" : "";
            string linear = nonlinear ? " linear='no'" : "";
            Add("EPUB/book.opf", $"""
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:native-boundary</dc:identifier><dc:title>Native boundaries</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-03T00:00:00Z</meta>{layoutMetadata}</metadata>
                  <manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"{properties}/><item id="font" href="fonts/test.ttf" media-type="font/ttf"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>{cssItem}{secondItem}{iconItem}</manifest>
                  <spine><itemref idref="chapter"{linear}/>{secondSpine}</spine>
                </package>
                """);
            string viewport = fixedLayout ? "<meta name='viewport' content='width=30,height=16'/>" : "";
            Add("EPUB/text.xhtml", xhtml ?? $"<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Boundaries</title>{viewport}{head}</head><body>{body}</body></html>");
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="text.xhtml">Boundaries</a></li></ol></nav></body></html>
                """);
            if (css) Add("EPUB/style.css", "p { color:red }");
            if (icon)
            {
                using var cover = zip.CreateEntry("EPUB/cover.png", CompressionLevel.NoCompression).Open();
                cover.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            }
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
