using System.IO.Compression;
using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;
using Xunit.Abstractions;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public class EpubPageBreakBeforeTests
{
    public EpubPageBreakBeforeTests(ITestOutputHelper output) => output.WriteLine("Loaded engine SHA256: " +
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EpubDocument).Assembly.Location))));

    [Fact]
    public void PendingBreakAfterDoesNotCreateAPageForAZeroWidthImage()
    {
        var book = EpubDocument.Open(Book("p{margin:0;page-break-after:always}",
            "<p>A</p><img src='image.png' style='display:block;max-width:0'/>", true));
        var layout = book.CreateLayout(Options());
        Assert.Single(layout.Pages);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        Assert.Equal("A", Assert.Single(reader.Read(0).Letters).Value);
        Assert.Empty(reader.Read(0).Images);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BreakAfterRetainsStandaloneImageOrderAndPlacement(bool imageFirst)
    {
        string image = "<img src='image.png'/>";
        var book = EpubDocument.Open(Book("p{margin:0}p,img{page-break-after:always}img{display:block}",
            imageFirst ? image + "<p>A</p>" : "<p>A</p>" + image, true));
        var layout = book.CreateLayout(Options());
        Assert.Equal(2, layout.Pages.Count);
        int imagePage = imageFirst ? 0 : 1, textPage = 1 - imagePage;
        Assert.Empty(layout.Pages[imagePage].TextPlacements);
        Assert.Equal(2, Assert.Single(layout.Pages[textPage].TextPlacements).Top);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, layout.RenderPage(imagePage).Pixels.Slice((2 * 30 + 2) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(2, reader.PageCount);
        Assert.Single(reader.Read(imagePage).Images);
        Assert.Equal("A", Assert.Single(reader.Read(textPage).Letters).Value);
    }

    [Theory]
    [InlineData("avoid")]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("inherit")]
    [InlineData("12px")]
    public void UnsupportedBreakAfterValuesAreExplicit(string value)
    {
        var book = EpubDocument.Open(Book("p{page-break-after:" + value + "}"));
        Assert.Contains("page-break-after", Assert.Throws<NotSupportedException>(() => book.CreateLayout(Options())).Message);
        Assert.Contains(value, Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(Font()))).Message);
    }

    [Theory]
    [InlineData("body{page-break-after:always}", "<p>A</p><p>A</p>", 1)]
    [InlineData("span{page-break-after:always}", "<p>A<span>A</span></p>", 1)]
    [InlineData("p.first{page-break-after:auto}p{page-break-after:always}", "<p class='first'>A</p><p>A</p>", 1)]
    [InlineData("p{page-break-after:always}", "<p style='page-break-after:auto'>A</p><p>A</p>", 1)]
    [InlineData("p{page-break-after:auto}", "<p style='page-break-after:always'>A</p><p>A</p>", 2)]
    [InlineData("p.first{page-break-after:auto;page-break-after:always}", "<p class='first'>A</p><p>A</p>", 2)]
    [InlineData("p{page-break-after:always;page-break-before:always}", "<p>A</p><p>A</p>", 2)]
    public void BreakAfterIsBlockOnlyAndUsesIndependentCascade(string css, string body, int pages)
    {
        var book = EpubDocument.Open(Book("p{margin:0}" + css, body));
        var layout = book.CreateLayout(Options());
        Assert.Equal(pages, layout.Pages.Count);
        Assert.Equal("AA", string.Concat(layout.Pages.SelectMany(page => page.TextPlacements)
            .Select(placement => book.ReadText(0).Substring(placement.Start, placement.Length))));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(pages, reader.PageCount);
        Assert.Equal("AA", string.Concat(Enumerable.Range(0, pages).SelectMany(index => reader.Read(index).Letters).Select(letter => letter.Value)));
    }

    [Fact]
    public void PendingBreakAfterMovesAnEmptyPaddingBoxBeforeFollowingText()
    {
        var book = EpubDocument.Open(Book("p{margin:0}p.after{page-break-after:always}",
            "<p class='after'>A</p><p style='padding-top:4px;background-color:red'/><p>A</p>"));
        var layout = book.CreateLayout(Options());
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(6, Assert.Single(layout.Pages[1].TextPlacements).Top);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, layout.RenderPage(1).Pixels.Slice((2 * 30 + 20) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, layout.RenderPage(0).Pixels.Slice((12 * 30 + 20) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(2, reader.PageCount);
        Assert.Equal(16.5, Assert.Single(reader.Read(1).Letters).StartBaseLine.Y, 6);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, new PdfPageRenderer(pdf).Render(1,
            new PdfRenderOptions(30, 36)).Pixels.Slice((2 * 30 + 20) * 4, 4).ToArray());
    }

    [Theory]
    [InlineData("<p class='after'>A</p>", 1)]
    [InlineData("<p class='after'>A</p><p/>", 1)]
    [InlineData("<p class='after'>A</p><div><p/></div>", 1)]
    [InlineData("<p class='after'>A</p><div> \n </div>", 1)]
    [InlineData("<p class='after'>A</p><p> \t </p>", 1)]
    [InlineData("<p class='after'>A</p><p/><p>A</p>", 2)]
    [InlineData("<p class='after'/><p>A</p>", 1)]
    [InlineData("<p class='after'>A</p><p class='after'>A</p>", 2)]
    public void PendingBreakAfterDoesNotCreateTerminalOrRepeatedEmptyPages(string body, int pages)
    {
        var book = EpubDocument.Open(Book("p{margin:0}p.after{page-break-after:always}", body));
        var layout = book.CreateLayout(Options());
        Assert.Equal(pages, layout.Pages.Count);
        Assert.All(layout.Pages, page => Assert.Equal(2, Assert.Single(page.TextPlacements).Top));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(pages, reader.PageCount);
        Assert.All(Enumerable.Range(0, pages), index => Assert.Single(reader.Read(index).Letters));
    }

    [Fact]
    public void ForcedBreakAfterANestedBlockMovesItsParentsFollowingInlineText()
    {
        byte[] source = Book("p{margin:0}p.first{page-break-after:always}",
            "<div><p class='first'>A</p>A</div>");
        byte[] preserved = source.ToArray();
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(Options());
        Assert.Equal(2, layout.Pages.Count);
        Assert.All(layout.Pages, page => Assert.Equal(2, Assert.Single(page.TextPlacements).Top));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(2, reader.PageCount);
        Assert.All(Enumerable.Range(0, 2), index => Assert.Equal("A", Assert.Single(reader.Read(index).Letters).Value));
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void ForcedBreakAfterAParagraphMovesFollowingTextAndNavigation()
    {
        byte[] source = Book("p{margin:0}p.first{page-break-after:always}",
            "<p class='first'>A</p><p id='target'>A</p>");
        byte[] preserved = source.ToArray();
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(Options());
        Assert.Equal(2, layout.Pages.Count);
        Assert.All(layout.Pages, page => Assert.Equal(2, Assert.Single(page.TextPlacements).Top));
        Assert.Equal(new EpubNavigationDestination(1, 0, 3, 2, 2), layout.ResolveNavigationTarget("doc.xhtml#target"));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(2, reader.PageCount);
        Assert.All(Enumerable.Range(0, 2), index =>
        {
            Assert.Equal("A", Assert.Single(reader.Read(index).Letters).Value);
            Assert.Empty(new PdfPageRenderer(pdf).Render(index, new PdfRenderOptions(30, 36)).Diagnostics);
        });
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void ForcedBreakAfterAnUnpaintedBlankLineStartsANewPage()
    {
        var book = EpubDocument.Open(Book("p.next{page-break-before:always}", "<p><br/></p><p class='next'>A</p>"));
        var layout = book.CreateLayout(Options());
        Assert.Equal(2, layout.Pages.Count);
        Assert.Empty(layout.Pages[0].TextPlacements);
        Assert.Equal(2, Assert.Single(layout.Pages[1].TextPlacements).Top);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font()) {
            PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(2, reader.PageCount);
        Assert.Empty(reader.Read(0).Letters);
        Assert.Single(reader.Read(1).Letters);
    }

    [Fact]
    public void EmptyForcedParagraphMovesFollowingTextWithoutAnEmptyPage()
    {
        byte[] source = Book("p.next{page-break-before:always}",
            "<p>A</p><p class='next' id='empty'/><p id='target'>A</p>");
        byte[] preserved = source.ToArray();
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(Options());
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(new[] { 0, 3 }, layout.Pages.Select(page => page.TextStart));
        Assert.All(layout.Pages, page => Assert.Single(page.TextPlacements));
        Assert.Equal(2, layout.Pages[1].TextPlacements[0].Top);
        Assert.Equal(new EpubNavigationDestination(1, 0, 3, 2, 2), layout.ResolveNavigationTarget("doc.xhtml#empty"));
        Assert.Equal(new EpubNavigationDestination(1, 0, 3, 2, 2), layout.ResolveNavigationTarget("doc.xhtml#target"));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font()) {
            PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(2, reader.PageCount);
        Assert.All(Enumerable.Range(0, 2), page => {
            Assert.Equal("A", Assert.Single(reader.Read(page).Letters).Value);
            Assert.Empty(new PdfPageRenderer(pdf).Render(page, new PdfRenderOptions(30, 36)).Diagnostics);
        });
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("<p class='next'/><p class='next'/><p>A</p>", 1)]
    [InlineData("<p>A</p><p class='next'/><p class='next'/><p>A</p>", 2)]
    [InlineData("<p><img src='image.png'/></p><p class='next'/><p class='next'/><p>A</p>", 2)]
    public void CoincidentParagraphAndImageEventsRetainOrderWithoutBlankPages(string body, int pages)
    {
        var book = EpubDocument.Open(Book("p.next{page-break-before:always}", body, true));
        var layout = book.CreateLayout(Options());
        Assert.Equal(pages, layout.Pages.Count);
        Assert.Equal(2, layout.Pages[^1].TextPlacements[0].Top);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font()) {
            PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(pages, reader.PageCount);
        if (body.Contains("img", StringComparison.Ordinal))
        {
            Assert.Single(reader.Read(0).Images);
            Assert.Empty(reader.Read(0).Letters);
            Assert.Empty(reader.Read(1).Images);
            Assert.Single(reader.Read(1).Letters);
        }
    }

    [Fact]
    public void EmptyParagraphWorkIsBounded()
    {
        var source = Book("p{page-break-before:always}", string.Concat(Enumerable.Repeat("<p/>", 100_001)));
        Assert.Contains("processing limits", Assert.Throws<InvalidDataException>(() => EpubDocument.Open(source).CreateLayout(Options())).Message);
    }

    [Theory]
    [InlineData("<p>A</p>", 36, 1)]
    [InlineData("<p>A</p><p>A</p><p>A</p>", 36, 3)]
    [InlineData("<p>A</p><p>A</p>", 24, 2)]
    [InlineData("<p><span>A</span><span>A</span></p>", 36, 1)]
    public void FirstSuccessiveAndNaturalBreaksKeepOnlyContentPages(string body, double height, int pages)
    {
        var layout = EpubDocument.Open(Book("p{page-break-before:always}", body))
            .CreateLayout(Options(height));
        Assert.Equal(pages, layout.Pages.Count);
        Assert.All(layout.Pages, page => {
            Assert.NotEmpty(page.TextPlacements);
            Assert.Equal(2, page.TextPlacements[0].Top);
        });
    }

    [Theory]
    [InlineData("body{page-break-before:always}", "<p>A</p><p>A</p>", 1)]
    [InlineData("span{page-break-before:always}", "<p>A<span>A</span></p>", 1)]
    [InlineData("p.next{page-break-before:auto}p{page-break-before:always}", "<p>A</p><p class='next'>A</p>", 1)]
    [InlineData("p{page-break-before:always}", "<p>A</p><p style='page-break-before:auto'>A</p>", 1)]
    [InlineData("p{page-break-before:auto}", "<p>A</p><p style='page-break-before:always'>A</p>", 2)]
    [InlineData("p.next{page-break-before:auto;page-break-before:always}", "<p>A</p><p class='next'>A</p>", 2)]
    public void BreaksAreBlockOnlyNoninheritedAndFollowTheCascade(string css, string body, int pages)
    {
        var layout = EpubDocument.Open(Book(css, body)).CreateLayout(Options());
        Assert.Equal(pages, layout.Pages.Count);
        Assert.Equal("AA", string.Concat(layout.Pages.Select(page => page.Text.Replace("\n", ""))));
    }

    [Theory]
    [InlineData("avoid")]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("inherit")]
    [InlineData("12px")]
    public void UnsupportedBreakValuesAreExplicitInNativeAndConversion(string value)
    {
        var book = EpubDocument.Open(Book("p{page-break-before:" + value + "}"));
        Assert.Contains("page-break-before", Assert.Throws<NotSupportedException>(() => book.CreateLayout(Options())).Message);
        Assert.Contains(value, Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(Font()))).Message);
    }

    [Fact]
    public void ForcedBreakRetainsNavigationAndSourceIndices()
    {
        var layout = EpubDocument.Open(Book("p.next{page-break-before:always}", "<p>A</p><p class='next' id='target'>A</p>"))
            .CreateLayout(Options());
        Assert.Equal(new EpubNavigationDestination(1, 0, 3, 2, 2), layout.ResolveNavigationTarget("doc.xhtml#target"));
        Assert.Equal(new EpubTextPlacement(0, 3, 1, 2, 2, 6, 10), layout.Pages[1].TextPlacements[0]);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => EpubDocument.Open(Book("p{page-break-before:always}"))
            .CreateLayout(Options(), canceled.Token));
    }

    private static EpubLayoutOptions Options(double height = 36) => new(30, height, Font()) { FontSize = 10, Margin = 2 };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForcedPagesPreserveWrappingAlignmentBackgroundsImagesAndPhysicalPdfGeometry(bool image)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string css = "p.next{page-break-before:always;background-color:#ff0000;text-align:right}span{background-color:#00ff00}";
        string body = image ? "<p>A</p><p class='next'><img src='image.png'/>A</p>"
            : "<p>A</p><p class='next'><span>A A</span></p>";
        int width = image ? 30 : 16;
        byte[] source = Book(css, body, image); byte[] preserved = source.ToArray();
        var book = EpubDocument.Open(source);
        var native = book.CreateLayout(new EpubLayoutOptions(width, 36, Font()) { FontSize = 10, Margin = 2 }, deadline.Token);
        Assert.Equal(2, native.Pages.Count);
        var last = native.Pages[1].TextPlacements[^1];
        Assert.Equal(image ? 3 : 5, last.Start);
        Assert.Equal(image ? 22 : 8, last.Left);
        Assert.Equal(image ? 2 : 12, last.Top);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font()) {
            PageWidthPoints = image ? 22.5 : 12, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }, deadline.Token));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(2, reader.PageCount);
        Assert.Equal(image ? "AA" : "AAA", string.Concat(Enumerable.Range(0, 2)
            .SelectMany(page => reader.Read(page).Letters).Select(letter => letter.Value)));
        Assert.All(PdfPageInformation.Read(pdf), page => {
            Assert.Equal(image ? 22.5 : 12, page.PhysicalWidth);
            Assert.Equal(27, page.PhysicalHeight);
        });
        if (image)
        {
            var placed = Assert.Single(reader.Read(1).Images);
            Assert.Equal(2, placed.PixelWidth); Assert.Equal(2, placed.PixelHeight);
            Assert.Equal(15, placed.BoundingBox.Left);
            Assert.Equal(16.5, placed.BoundingBox.Right);
            Assert.Equal(19.5, placed.BoundingBox.Bottom);
            Assert.Equal(21, placed.BoundingBox.Top);
            Assert.Equal(Png(), book.ReadResource("image.png"));
        }
        var frame = native.RenderPage(1, cancellationToken: deadline.Token);
        var converted = new PdfPageRenderer(pdf).Render(1, new PdfRenderOptions(width, 36), deadline.Token);
        Assert.Empty(converted.Diagnostics);
        Check(2, 3, [1, 0, 0, 1], [0, 0, 255, 255]);
        if (image) Check(20, 8, [1, 0, 0, 1], [0, 0, 255, 255]);
        else Check(8, 11, [0, 1, 0, 1], [0, 255, 0, 255]);
        Check(image ? 27 : 13, image ? 5 : 15, [0, 0, 0, 1], [0, 0, 0, 255]);
        Assert.Equal(preserved, source);
        void Check(int x, int y, float[] expectedNative, byte[] expectedPdf)
        {
            Assert.Equal(expectedNative, frame.Pixels.Slice((y * width + x) * 4, 4).ToArray());
            Assert.Equal(expectedPdf, converted.Pixels.Slice((y * width + x) * 4, 4).ToArray());
        }
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("p{text-align:left}", 1)]
    [InlineData("p.next{page-break-before:always}", 2)]
    [InlineData("p.next{page-break-before:auto}", 1)]
    [InlineData("p.next{page-break-before:always}p.next{page-break-before:auto}", 1)]
    public void PublicNativePaginationPreservesTextGeometryAndInk(string css, int expectedPages)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var source = Book(css); var preserved = source.ToArray();
        var font = Font();
        var layout = EpubDocument.Open(source).CreateLayout(new EpubLayoutOptions(30, 36, font) { FontSize = 10, Margin = 2 }, deadline.Token);
        Assert.Equal(expectedPages, layout.Pages.Count);
        Assert.Equal("AA", string.Concat(layout.Pages.Select(p => p.Text.Replace("\n", ""))));
        Assert.Equal(new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10), layout.Pages[0].TextPlacements[0]);
        var last = layout.Pages[^1].TextPlacements[^1];
        Assert.Equal(2, last.Left);
        Assert.Equal(expectedPages == 2 ? 2 : 22, last.Top);
        foreach (int page in Enumerable.Range(0, expectedPages))
        {
            var frame = layout.RenderPage(page, cancellationToken: deadline.Token);
            Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((5 * 30 + 7) * 4, 4).ToArray());
            Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice(0, 4).ToArray());
        }
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("p{text-align:left}", 1)]
    [InlineData("p.next{page-break-before:always}", 2)]
    [InlineData("p.next{page-break-before:auto}", 1)]
    [InlineData("p.next{page-break-before:always}p.next{page-break-before:auto}", 1)]
    public void PublicConversionPaginationReopensWithTextGeometryAndInk(string css, int expectedPages)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var source = Book(css); var preserved = source.ToArray();
        var bytes = EpubDocument.Open(source).ConvertToPdf(new EpubPdfConversionOptions(Font()) {
            PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }, deadline.Token);
        var pdf = PdfDocument.Open(bytes); var reader = new PdfPageContentReader(pdf);
        Assert.Equal(expectedPages, reader.PageCount);
        Assert.Equal("AA", string.Concat(Enumerable.Range(0,reader.PageCount).SelectMany(p => reader.Read(p).Letters).Select(l => l.Value)));
        Assert.Equal(1.5, reader.Read(0).Letters[0].StartBaseLine.X);
        foreach (int page in Enumerable.Range(0, expectedPages))
        {
            var frame = new PdfPageRenderer(pdf).Render(page,new PdfRenderOptions(30,36), deadline.Token);
            Assert.Empty(frame.Diagnostics);
            Assert.Equal(new byte[] { 0,0,0,255 }, frame.Pixels.Slice((5 * 30 + 7) * 4,4).ToArray());
            Assert.Equal(new byte[] { 255,255,255,255 }, frame.Pixels.Slice(0,4).ToArray());
        }
        Assert.Equal(preserved,source);
    }

    [Fact]
    public void UnmatchedOriginalH2RuleMustNotPreventPublicNativeLayout()
    {
        var layout = EpubDocument.Open(Book("h2{page-break-before:always}", "<p>A</p>")).CreateLayout(
            new EpubLayoutOptions(30,36,Font()) { FontSize=10,Margin=2 });
        Assert.Single(layout.Pages);
        Assert.Equal("A",layout.Pages[0].Text);
    }

    private static TrueTypeFont Font() => TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false,includeOutlines:true));
    private static byte[] Book(string css, string body = "<p>A</p><p class=\"next\">A</p>", bool image = false)
    {
        using var output = new MemoryStream();
        using(var zip = new ZipArchive(output, ZipArchiveMode.Create,true))
        {
            Add("mimetype","application/epub+zip");
            Add("META-INF/container.xml","<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"package.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            Add("package.opf","<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">next-pagination</dc:identifier><dc:title>Pagination</dc:title><dc:language>en</dc:language></metadata><manifest><item id=\"doc\" href=\"doc.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"css\" href=\"style.css\" media-type=\"text/css\"/></manifest><spine><itemref idref=\"doc\"/></spine></package>");
            Add("doc.xhtml","<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><link rel=\"stylesheet\" type=\"text/css\" href=\"style.css\"/></head><body>" + body + "</body></html>");
            Add("style.css",css);
            if (image) { using var stream = zip.CreateEntry("image.png").Open(); stream.Write(Png()); }
            void Add(string name,string value) {
                if (image && name == "package.opf") value = value.Replace("</manifest>", "<item id='image' href='image.png' media-type='image/png'/></manifest>");
                using var stream=zip.CreateEntry(name,CompressionLevel.NoCompression).Open(); stream.Write(Encoding.UTF8.GetBytes(value)); }
        }
        return output.ToArray();
    }

    private static byte[] Png()
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Chunk("IHDR", [0, 0, 0, 2, 0, 0, 0, 2, 8, 2, 0, 0, 0]);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
            zlib.Write(new byte[] { 0, 255, 0, 0, 0, 255, 0, 0, 0, 0, 255, 0, 0, 0 });
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []);
        return output.ToArray();
        void Chunk(string type, byte[] data)
        {
            Span<byte> integer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(integer, (uint)data.Length); output.Write(integer);
            byte[] name = Encoding.ASCII.GetBytes(type); output.Write(name); output.Write(data);
            uint crc = uint.MaxValue;
            foreach (byte value in name.Concat(data))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
            }
            BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc); output.Write(integer);
        }
    }
}
