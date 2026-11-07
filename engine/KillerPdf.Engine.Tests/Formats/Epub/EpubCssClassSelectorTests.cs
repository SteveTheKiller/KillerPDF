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

public sealed class EpubCssClassSelectorTests
{
    [Theory]
    [InlineData("<td/><td>A</td>", "A", 14)]
    [InlineData("<td>A</td><td/>", "A", 2)]
    [InlineData("<td/><td/>", "", 0)]
    public void EmptyTableCellsKeepTheirColumnsWithoutInventingText(string cells, string text, double left)
    {
        var book = EpubDocument.Open(Book("table{width:24px;border-spacing:0}td{width:12px;padding:0}",
            "<table><tr>" + cells + "</tr></table>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 36, font) { FontSize = 10, Margin = 2 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal(text, page.Text);
        if (text.Length == 0) Assert.Empty(page.TextPlacements);
        else Assert.Equal(new EpubTextPlacement(0, 0, 1, left, 2, 6, 10), Assert.Single(page.TextPlacements));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        if (text.Length == 0) Assert.Empty(letters);
        else Assert.Equal(left * .75, Assert.Single(letters).StartBaseLine.X);
    }

    [Fact]
    public void TableCellTallerThanAFreshPageIsDiagnosedWithoutChangingSource()
    {
        byte[] source = Book("table{width:24px;border-spacing:0}td{width:12px;padding:0}",
            "<table><tr><td>A A A</td><td>A</td></tr></table>"), original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        Assert.Equal("Native EPUB rows taller than one page are not implemented.",
            Assert.Throws<NotSupportedException>(() => book.CreateLayout(
                new EpubLayoutOptions(30, 26, font) { FontSize = 10, Margin = 2 })).Message);
        Assert.Equal("Native EPUB rows taller than one page are not implemented.",
            Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)
                { PageWidthPoints = 22.5, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5 })).Message);
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("<table><tbody><tr><td>A</td><td>A</td></tr></tbody></table>")]
    [InlineData("<table><caption>A</caption><tr><td>A</td><td>A</td></tr></table>")]
    [InlineData("<table><tr><td colspan='2'>A</td></tr></table>")]
    [InlineData("<table><tr><td rowspan='2'>A</td><td>A</td></tr></table>")]
    [InlineData("<table><tr><td><table><tr><td>A</td></tr></table></td><td>A</td></tr></table>")]
    [InlineData("<table>A<tr><td>A</td><td>A</td></tr></table>")]
    public void InitialTableSliceDiagnosesUnimplementedStructure(string body)
    {
        var book = EpubDocument.Open(Book("table{width:24px;border-spacing:0}td{width:12px;padding:0}", body));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(
            new EpubLayoutOptions(30, 46, font) { FontSize = 10, Margin = 2 }));
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)));
    }

    [Theory]
    [InlineData("<table><tr><td>A</td><td>A</td></tr><tr style='page-break-before:always'><td>A</td><td>A</td></tr></table>")]
    [InlineData("<table><tr style='page-break-after:always'><td>A</td><td>A</td></tr><tr><td>A</td><td>A</td></tr></table>")]
    [InlineData("<table><tr><td style='page-break-before:always'>A</td><td>A</td></tr></table>")]
    [InlineData("<table><tr><td style='page-break-after:always'>A</td><td>A</td></tr></table>")]
    public void UnsupportedResolvedRowAndCellBreaksAreDiagnosed(string body)
    {
        var book = EpubDocument.Open(Book("table{width:24px;border-spacing:0}td{width:12px;padding:0}", body));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        Assert.Equal("Native EPUB row and cell page breaks are not implemented.",
            Assert.Throws<NotSupportedException>(() => book.CreateLayout(
                new EpubLayoutOptions(30, 46, font) { FontSize = 10, Margin = 2 })).Message);
        Assert.Equal("Native EPUB row and cell page breaks are not implemented.",
            Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)
                { PageWidthPoints = 22.5, PageHeightPoints = 34.5, MarginPoints = 1.5, FontSizePoints = 7.5 })).Message);
    }

    [Theory]
    [InlineData("<tr><td style='width:6px'>A</td><td style='width:18px'>A</td></tr><tr><td>A</td><td>A</td></tr>")]
    [InlineData("<tr><td style='width:6px'>A</td><td style='width:6px'>A</td></tr>")]
    [InlineData("<tr><td>A</td><td>A</td></tr><tr><td style='width:24px'>A</td></tr>")]
    public void ExplicitTableColumnsDiagnoseUnimplementedWidthDistribution(string rows)
    {
        var book = EpubDocument.Open(Book("table{width:24px;border-spacing:0}td{width:12px;padding:0}",
            "<table>" + rows + "</table>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        Assert.Equal("Native EPUB explicit table columns must fill the width and match across rows.",
            Assert.Throws<NotSupportedException>(() => book.CreateLayout(
                new EpubLayoutOptions(30, 46, font) { FontSize = 10, Margin = 2 })).Message);
        Assert.Equal("Native EPUB explicit table columns must fill the width and match across rows.",
            Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)
                { PageWidthPoints = 22.5, PageHeightPoints = 34.5, MarginPoints = 1.5, FontSizePoints = 7.5 })).Message);
    }

    [Theory]
    [InlineData(46, 1, 19)]
    [InlineData(26, 2, 2)]
    public void ParagraphBottomMarginPrecedesTheTableAndItsRowFit(int height, int pageCount, double tableTop)
    {
        var book = EpubDocument.Open(Book("p{margin:0 0 7px}table{width:24px;border-spacing:0}td{width:12px;padding:0}",
            "<p>A</p><table><tr><td>A</td><td>A</td></tr></table>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(30, height, font) { FontSize = 10, Margin = 2 });
        Assert.Equal(pageCount, layout.Pages.Count);
        Assert.Equal(new[] { 2d, tableTop, tableTop },
            layout.Pages.SelectMany(page => page.TextPlacements).Select(item => item.Top));
        Assert.Equal(new[] { 0, 3, 5 },
            layout.Pages.SelectMany(page => page.TextPlacements).Select(item => item.Start));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = height * .75, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(pageCount, reader.PageCount);
        Assert.Equal(new[] { (height - 10) * .75, (height - tableTop - 8) * .75, (height - tableTop - 8) * .75 },
            Enumerable.Range(0, pageCount).SelectMany(page => reader.Read(page).Letters).Select(item => item.StartBaseLine.Y));
    }

    [Fact]
    public void TableCellsKeepTheirOuterContainerBackgroundWithoutSharingFlowBounds()
    {
        var book = EpubDocument.Open(Book("table{width:24px;border-spacing:0}td{width:12px;padding:0}",
            "<div style='background-color:blue'><table><tr><td>A</td><td>A</td></tr></table></div>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 36, font) { FontSize = 10, Margin = 2 });
        Assert.Equal(2, Assert.Single(layout.Pages).TextPlacements.Count);
        Assert.Equal(new float[] { 0, 0, 1, 1 }, layout.RenderPage(0).Pixels.Slice((11 * 30 + 2) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 36));
        Assert.Empty(frame.Diagnostics);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, frame.Pixels.Slice((11 * 30 + 2) * 4, 4).ToArray());
    }

    [Fact]
    public void TwoExplicitTableCellsKeepIndependentColumnsAndSourceText()
    {
        byte[] source = Book("table{width:24px;border-spacing:0}td{width:12px;padding:0}",
            "<table><tr><td>A</td><td>A</td></tr></table>"), original = source.ToArray();
        var book = EpubDocument.Open(source);
        Assert.Equal("A\nA", book.ReadText(0));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 36, font) { FontSize = 10, Margin = 2 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("A\nA", page.Text);
        Assert.Equal(new[] { (0, 2d, 2d, 6d, 10d), (2, 14d, 2d, 6d, 10d) },
            page.TextPlacements.Select(item => (item.Start, item.Left, item.Top, item.Width, item.Height)));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var converted = new PdfPageContentReader(pdf).Read(0);
        Assert.Equal(new[] { ("A", 1.5, 19.5), ("A", 10.5, 19.5) },
            converted.Letters.Select(item => (item.Value, item.StartBaseLine.X, item.StartBaseLine.Y)));
        Assert.Equal(original, source);
    }

    [Fact]
    public void TableRowsMoveTogetherAcrossPagesWithoutChangingSourceOffsets()
    {
        byte[] source = Book("table{width:24px;border-spacing:0}td{width:12px;padding:0}",
            "<table><tr><td>A</td><td>A</td></tr><tr><td>A</td><td>A</td></tr></table>"), original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 });
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(new[] { "A\nA\n", "A\nA" }, layout.Pages.Select(page => page.Text));
        Assert.Equal(new[] { (0, 2d, 2d), (2, 14d, 2d), (4, 2d, 2d), (6, 14d, 2d) },
            layout.Pages.SelectMany(page => page.TextPlacements).Select(item => (item.Start, item.Left, item.Top)));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(2, reader.PageCount);
        foreach (int page in new[] { 0, 1 })
            Assert.Equal(new[] { ("A", 1.5, 4.5), ("A", 10.5, 4.5) },
                reader.Read(page).Letters.Select(item => (item.Value, item.StartBaseLine.X, item.StartBaseLine.Y)));
        Assert.Equal(original, source);
    }

    [Fact]
    public void PercentageCellWidthsResolveOnceAgainstTheTable()
    {
        var book = EpubDocument.Open(Book("table{width:24px;border-spacing:0}td{width:50%;padding:0}",
            "<table><tr><td>AA</td><td>AA</td></tr></table>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 36, font) { FontSize = 10, Margin = 2 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("AA\nAA", page.Text);
        Assert.Equal(new[] { (0, 2d, 2d), (1, 8d, 2d), (3, 14d, 2d), (4, 20d, 2d) },
            page.TextPlacements.Select(item => (item.Start, item.Left, item.Top)));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        Assert.Equal(new[] { 1.5, 6, 10.5, 15 },
            new PdfPageContentReader(pdf).Read(0).Letters.Select(item => item.StartBaseLine.X));
    }

    [Fact]
    public void WrappedCellSetsRowHeightWithoutMovingTheOtherColumnDown()
    {
        byte[] source = Book("table{width:24px;border-spacing:0}td{width:12px;padding:0}p{margin:0}",
            "<table><tr><td>A A</td><td>A</td></tr></table><p>A</p>"), original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 36, font) { FontSize = 10, Margin = 2 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("A A\nA\n\nA", page.Text);
        Assert.Equal(new[] { (0, 2d, 2d), (2, 2d, 12d), (4, 14d, 2d), (7, 2d, 22d) },
            page.TextPlacements.Select(item => (item.Start, item.Left, item.Top)));
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 0, 0, 0, 1 }, native.Pixels.Slice((5 * 30 + 19) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((15 * 30 + 19) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        Assert.Equal(new[] { (1.5, 19.5), (1.5, 12), (10.5, 19.5), (1.5, 4.5) },
            new PdfPageContentReader(pdf).Read(0).Letters.Select(item => (item.StartBaseLine.X, item.StartBaseLine.Y)));
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 36));
        Assert.Empty(frame.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, frame.Pixels.Slice((5 * 30 + 19) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, frame.Pixels.Slice((15 * 30 + 19) * 4, 4).ToArray());
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("", 13, 7, 11.25, true)]
    [InlineData("hr{margin:0}", 3, 2, 18.75, true)]
    [InlineData("hr{margin:0;border:0}", 2, 2, 19.5, false)]
    [InlineData("hr{margin:0;display:none}", 2, 2, 19.5, false)]
    [InlineData("hr{margin:0;visibility:hidden}", 3, 2, 18.75, false)]
    [InlineData("hr{all:initial}", 2, 2, 19.5, false)]
    [InlineData("hr{all:inherit}", 2, 2, 19.5, false)]
    public void HorizontalRuleDefaultsAndAuthorResetsKeepLiteralFlowAndPaint(string css,
        double textTop, int lineY, double baseline, bool painted)
    {
        byte[] source = Book("p{margin:0}" + css, "<hr/><p>A</p>"), original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 36, font) { FontSize = 10, Margin = 2 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("A", page.Text);
        Assert.Equal(textTop, Assert.Single(page.TextPlacements).Top);
        Assert.Equal(painted ? new float[] { 0, 0, 0, 1 } : [1, 1, 1, 1],
            layout.RenderPage(0).Pixels.Slice((lineY * 30 + 15) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var converted = new PdfPageContentReader(pdf).Read(0);
        Assert.Equal(painted ? 1 : 0, converted.Paths.Count);
        Assert.Equal(baseline, Assert.Single(converted.Letters).StartBaseLine.Y);
        Assert.Equal(original, source);
    }

    [Fact]
    public void HorizontalRuleWidthUsesItsAutomaticHorizontalMargins()
    {
        var book = EpubDocument.Open(Book("hr{width:50%;margin-top:0;margin-bottom:0}", "<hr/>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 36, font) { FontSize = 10, Margin = 2 });
        var frame = layout.RenderPage(0);
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((2 * 30 + 15) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((2 * 30 + 3) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((2 * 30 + 25) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        Assert.Equal(new PdfContentBounds(6.375, 24.75, 16.125, 25.5),
            Assert.Single(new PdfPageContentReader(pdf).Read(0).Paths).BoundingBox);
    }

    [Theory]
    [InlineData("<hr>A</hr>")]
    [InlineData("<hr><span>A</span></hr>")]
    [InlineData("<p><hr/></p>")]
    [InlineData("<hr onclick='execute()'/>")]
    [InlineData("<hr xmlns='urn:other'/>")]
    public void HorizontalRuleAdmissionRetainsContentParentAndAttributeGuards(string body)
    {
        var book = EpubDocument.Open(Book("", body));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(
            new EpubLayoutOptions(30, 36, font) { FontSize = 10, Margin = 2 }));
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)));
    }

    [Fact]
    public void HorizontalRuleUsesReaderFallbackColorWithoutChangingPdfSourceColors()
    {
        byte[] source = Book("hr{margin:0}", "<hr/>"), original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 16, font)
        { FontSize = 10, Margin = 2, TextColor = new PdfRgbColor(0, 0, 1) });
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 0, 0, 1, 1 }, native.Pixels.Slice((2 * 30 + 15) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 16));
        Assert.Empty(frame.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, frame.Pixels.Slice((2 * 30 + 15) * 4, 4).ToArray());
        Assert.Equal(original, source);
    }

    [Fact]
    public void HorizontalRulePaintsOneLineAndKeepsFollowingTextAtBothSeams()
    {
        byte[] source = Book("hr{margin:0}p{margin:0}", "<hr/><p>A</p>"), original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 36, font) { FontSize = 10, Margin = 2 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("A", page.Text);
        var placement = Assert.Single(page.TextPlacements);
        Assert.Equal(3, placement.Top);
        Assert.Equal(0, placement.Start);
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 0, 0, 0, 1 }, native.Pixels.Slice((2 * 30 + 15) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((1 * 30 + 15) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((2 * 30 + 29) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var content = new PdfPageContentReader(pdf).Read(0);
        Assert.Equal(new PdfContentBounds(1.5, 24.75, 21, 25.5), Assert.Single(content.Paths).BoundingBox);
        Assert.Equal(18.75, Assert.Single(content.Letters).StartBaseLine.Y);
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 36));
        Assert.Empty(frame.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, frame.Pixels.Slice((2 * 30 + 15) * 4, 4).ToArray());
        Assert.Equal(original, source);
    }

    [Fact]
    public void DirectContainerSpanKeepsItsStyledTextAtBothSeams()
        => Verify("span{color:red}", "<div><span>A</span></div>", 2, 1.5, true, false);

    [Theory]
    [InlineData("https://www.gutenberg.org/", "https://www.gutenberg.org/")]
    [InlineData("http://example.invalid/page#detail", "http://example.invalid/page#detail")]
    [InlineData("mailto:reader@example.invalid", "mailto:reader@example.invalid")]
    [InlineData("HTTPS://example.invalid/résumé", "https://example.invalid/r%C3%A9sum%C3%A9")]
    public void ExternalTextHyperlinkRemainsClickableInNativeLayoutAndConvertedPdf(string reference, string target)
    {
        byte[] source = Book("", "<div><a href='" + reference + "'>A</a></div>");
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);
        Assert.Equal("A", book.ReadText(0));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(30, 16, font)
        { FontSize = 10, Margin = 2 }).Pages);
        Assert.Equal("A", page.Text);
        var placement = Assert.Single(page.TextPlacements);
        Assert.Equal(target, placement.LinkTarget);
        Assert.Equal(2, placement.Left);
        Assert.Equal(6, placement.Width);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var link = Assert.Single(new PdfLinkReader(pdf).ReadPage(0));
        Assert.Equal(target, link.Uri);
        Assert.Null(link.DestinationPageIndex);
        Assert.Equal("A", Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).Value);
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("<div><a href='#target'>A</a></div><div id='target'/>")]
    [InlineData("<a href='#target'>A</a><div id='target'/>")]
    [InlineData("<div><a href='#target'><span>A</span></a></div><div id='target'/>")]
    public void GroupingContainerRetainsItsDirectTextHyperlinkAtBothSeams(string body)
        => Verify("a:link{color:red}",
            body,
            2, 1.5, true, false, "text.xhtml#target");

    [Theory]
    [InlineData(":visited")]
    [InlineData("a.note:visited")]
    [InlineData("a[href]:VISITED")]
    [InlineData("a:visited span")]
    public void VisitedPredicatesNeverChangeTheCapturedUnvisitedPresentation(string selector)
        => Verify(selector + "{color:red}",
            "<p><a class='note' href='#target'><span>A</span></a></p><div id='target'/>",
            2, 1.5, false, false, "text.xhtml#target");

    [Fact]
    public void VisitedSelectorKeepsLocalLinksInTheUnvisitedState()
        => Verify("a:link{color:red}a:visited{color:lime}",
            "<p><a href='#target'>A</a></p><div id='target'/>", 2, 1.5, true, false, "text.xhtml#target");

    [Theory]
    [InlineData("<p><a href='#target'>AA</a></p>", 1)]
    [InlineData("<p><a href='#target'>A</a>A<a href='#target'>A</a></p>", 2)]
    [InlineData("<p><a href='#target'>A<br/>A</a></p>", 2)]
    [InlineData("<p><a href='#target' style='visibility:hidden'>A</a>A</p>", 0)]
    [InlineData("<p><a href='#target' style='display:none'>A</a>A</p>", 0)]
    [InlineData("<p><a href='#target' style='visibility:hidden'><span style='visibility:visible'>A</span></a></p>", 1)]
    public void NativeLinkRegionsRespectVisibleRunsAndLineBoundaries(string body, int count)
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var snapshot = PdfReadSnapshot.OpenEpub(Book("", body + "<div id='target'/>"),
            new EpubLayoutOptions(30, 48, font) { FontSize = 10, Margin = 2 });
        var links = snapshot.ReadLinkRegions(0);
        Assert.Equal(count, links.Count);
        Assert.All(links, link => Assert.Equal("text.xhtml#target", link.Target));
    }

    [Fact]
    public void NativeLinkReadsHonorCancellationIndexesAndCollectionOwnership()
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var snapshot = PdfReadSnapshot.OpenEpub(Book("", "<p><a href='#target'>A</a></p><div id='target'/>"),
            new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 });
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.ReadLinkRegions(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.ReadLinkRegions(1));
        Assert.Throws<OperationCanceledException>(() => snapshot.ReadLinkRegions(-1, new CancellationToken(true)));
        var links = snapshot.ReadLinkRegions(0);
        var mutable = Assert.IsAssignableFrom<IList<PdfReadLinkRegion>>(links);
        Assert.Throws<NotSupportedException>(() => mutable.Clear());
        Assert.Equal("text.xhtml#target", Assert.Single(snapshot.ReadLinkRegions(0)).Target);
    }

    [Fact]
    public void NativeSnapshotExposesLocalLinkRegionsFromItsCapturedLayout()
    {
        byte[] source = Book("", "<p><a href='#target'>A</a></p><div id='target'/>");
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var snapshot = PdfReadSnapshot.OpenEpub(source,
            new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 });
        Array.Clear(source);
        var link = Assert.Single(snapshot.ReadLinkRegions(0));
        Assert.Equal("text.xhtml#target", link.Target);
        Assert.Equal(new PdfContentBounds(1.5, 3, 6, 10.5), link.Bounds);
        Assert.Equal(0, snapshot.ResolveNavigationTarget(link.Target).PageIndex);
    }

    [Fact]
    public void NativeLinkTargetsRetainTheNavigationLengthBound()
    {
        var book = EpubDocument.Open(Book("", "<p><a href='#" + new string('a', 4096) + "'>A</a></p>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        Assert.Throws<InvalidDataException>(() => book.CreateLayout(new EpubLayoutOptions(80, 80, font)));
    }

    [Fact]
    public void LinksAcrossExplicitLineBreaksHaveSeparateRectangles()
    {
        var book = EpubDocument.Open(Book("", "<p><a href='#target'>AA<br/>AA</a></p><div id='target'/>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 60, MarginPoints = 1.5, FontSizePoints = 15 }));
        var links = new PdfLinkReader(pdf).ReadPage(0);
        Assert.Equal(2, links.Count);
        Assert.Equal(2, links.Select(link => link.Top).Distinct().Count());
        Assert.All(links, link => { Assert.Equal(1.5, link.Left, 6); Assert.Equal(19.5, link.Right, 6); });
    }

    [Theory]
    [InlineData("visibility:hidden", false)]
    [InlineData("display:none", false)]
    [InlineData("", true)]
    public void HiddenAnchorsDoNotCreatePdfLinkRegions(string style, bool linked)
    {
        var book = EpubDocument.Open(Book("", "<p><a style='" + style + "' href='#target'>A</a><span>A</span></p><div id='target'/>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(80, 80, font) { Margin = 2, FontSize = 20 });
        Assert.Equal(linked ? 1 : 0, layout.Pages[0].TextPlacements.Count(placement => placement.LinkTarget is not null));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 60, PageHeightPoints = 60, MarginPoints = 1.5, FontSizePoints = 15 }));
        Assert.Equal(linked ? 1 : 0, new PdfLinkReader(pdf).ReadPage(0).Count);
    }

    [Fact]
    public void MissingAnchorFragmentIsDiagnosedDuringConversion()
    {
        var book = EpubDocument.Open(Book("", "<p><a href='#missing'>A</a></p>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        Assert.Equal("target", Assert.Throws<ArgumentException>(() =>
            book.ConvertToPdf(new EpubPdfConversionOptions(font))).ParamName);
    }

    [Fact]
    public void ConvertedLinkUsesOneRectangleForAnUnbrokenTextRun()
    {
        var book = EpubDocument.Open(Book("", "<p><a href='#target'>AA</a></p><p id='target' style='page-break-before:always'>A</p>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 60, PageHeightPoints = 60, MarginPoints = 1.5, FontSizePoints = 15 }));
        var link = Assert.Single(new PdfLinkReader(pdf).ReadPage(0));
        Assert.Equal(1.5, link.Left, 6);
        Assert.Equal(19.5, link.Right, 6);
        Assert.Equal(1, link.DestinationPageIndex);
    }

    [Fact]
    public void NestedAnchorsAreDiagnosedInsteadOfChangingTheOuterTarget()
    {
        var book = EpubDocument.Open(Book("", "<p><a href='#outer'><span><a href='#inner'>A</a></span></a></p><div id='outer'/><div id='inner'/>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(80, 80, font)));
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)));
    }

    [Theory]
    [InlineData(":link", "<p><a href='#target'>A</a></p>", true, "text.xhtml#target")]
    [InlineData("a:LINK", "<p><a href='#target'>A</a></p>", true, "text.xhtml#target")]
    [InlineData("a.note:link", "<p><a class='note' href='#target'>A</a></p>", true, "text.xhtml#target")]
    [InlineData("a[href]:link", "<p><a href='#target'><span>A</span></a></p>", true, "text.xhtml#target")]
    [InlineData("a:link", "<p><a>A</a></p>", false, null)]
    [InlineData("p:link", "<p><a href='#target'>A</a></p>", false, "text.xhtml#target")]
    [InlineData("a.note:link", "<p><a href='#target'>A</a></p>", false, "text.xhtml#target")]
    public void LinkPredicatesRetainHrefTypeClassAndInlineAncestry(string selector, string body, bool red, string? target)
        => Verify(selector + "{color:red}", body + "<div id='target'/>", 2, 1.5, red, false, target);

    [Theory]
    [InlineData("a:link{color:red}a{color:lime}")]
    [InlineData("a{color:lime}a:link{color:red}")]
    [InlineData("a.note{color:lime}a:link{color:red}")]
    [InlineData("a:link{color:lime}a:link{color:red}")]
    public void LinkPseudoClassUsesClassSpecificityAndSourceOrder(string css)
        => Verify(css, "<p><a class='note' href='#target'>A</a></p><div id='target'/>", 2, 1.5, true, false, "text.xhtml#target");

    [Fact]
    public void ConvertedAnchorPreservesItsLocalPageDestination()
    {
        var book = EpubDocument.Open(Book("a:link{color:red}",
            "<p><a href='#target'>A</a></p><p id='target' style='page-break-before:always'>A</p>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 60, PageHeightPoints = 60, MarginPoints = 1.5, FontSizePoints = 15 }));
        Assert.Equal(2, new PdfPageContentReader(pdf).PageCount);
        var link = Assert.Single(new PdfLinkReader(pdf).ReadPage(0));
        Assert.Equal(1, link.DestinationPageIndex);
        Assert.Null(link.Uri);
        Assert.InRange(link.Left, 1.49, 1.51);
    }

    [Fact]
    public void LinkPseudoClassStylesAHyperlinkAtBothSeams()
        => Verify("a:link{color:red}", "<p><a href='#target'>A</a></p><div id='target'/>", 2, 1.5, true, false, "text.xhtml#target");

    [Fact]
    public void IdSelectorStylesItsMatchingElementInNativeAndPdfPages()
        => Verify("#target{color:red}", "<p id='target'>A</p>", 2, 1.5, true, false);

    [Theory]
    [InlineData("p#target", "<p id='target'>A</p>", true)]
    [InlineData(".note#target", "<p class='note' id='target'>A</p>", true)]
    [InlineData("p#target.note", "<p class='note' id='target'>A</p>", true)]
    [InlineData("#target#target", "<p id='target'>A</p>", true)]
    [InlineData("#target#other", "<p id='target'>A</p>", false)]
    [InlineData("#Target", "<p id='target'>A</p>", false)]
    [InlineData("#target", "<p id='other target'>A</p>", false)]
    [InlineData("#target", "<p>A</p>", false)]
    [InlineData("#root #target", "<div id='root'><p id='target'>A</p></div>", true)]
    [InlineData("#root #target", "<div id='root'/><p id='target'>A</p>", false)]
    [InlineData("p[class~='#target']", "<p id='other' class='#target'>A</p>", true)]
    public void IdPredicatesRetainExactCompoundAndAncestorMeaning(string selector, string body, bool red)
        => Verify(selector + "{color:red}", body, 2, 1.5, red, false);

    [Theory]
    [InlineData("#target{color:red}p.note{color:lime}")]
    [InlineData("p.note{color:lime}#target{color:red}")]
    [InlineData("p#target{color:red}#target{color:lime}")]
    [InlineData("#target{color:lime}#target{color:red}")]
    [InlineData("#root p{color:red}p.note.note.note{color:lime}")]
    [InlineData("#target#target{color:red}p#target{color:lime}")]
    [InlineData("#root #target{color:red}p#target{color:lime}")]
    public void IdSpecificityOutranksClassesAndTypesAndKeepsSourceOrder(string css)
        => Verify(css, "<div id='root'><p class='note' id='target'>A</p></div>", 2, 1.5, true, false);

    [Fact]
    public void IdSpecificityCascadesEachPropertyIndependently()
        => Verify("#target{color:red}p.note{background-color:blue}", "<p class='note' id='target'>A</p>", 2, 1.5, true, true);

    [Theory]
    [InlineData("@namespace h 'http://www.w3.org/1999/xhtml';h|p#target{color:red}", true)]
    [InlineData("@namespace 'http://www.w3.org/1999/xhtml';#target{color:red}", true)]
    [InlineData("@namespace 'urn:other';#target{color:red}", false)]
    public void IdCompoundsKeepTheElementNamespaceConstraint(string css, bool red)
        => Verify(css, "<p id='target'>A</p>", 2, 1.5, red, false);

    [Fact]
    public void DescendantAncestorMissesRespectTheSelectorWorkBudget()
    {
        string body = "<p>" + string.Concat(Enumerable.Repeat("<span>", 120)) + "A"
            + string.Concat(Enumerable.Repeat("</span>", 120)) + "</p>";
        Verify("div.missing span{}", body, 2, 1.5, false, false);
        byte[] source = Book(string.Concat(Enumerable.Repeat("div.missing span{}", 4096)), body), preserved = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        const string diagnostic = "Native EPUB CSS selector matching exceeds its work limit.";
        Assert.Equal(diagnostic, Assert.Throws<InvalidDataException>(() => book.CreateLayout(
            new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 }, deadline.Token)).Message);
        Assert.Equal(diagnostic, Assert.Throws<InvalidDataException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font), deadline.Token)).Message);
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("<div class='outer'><div class='middle'><p><span class='caption'>A</span></p></div></div>", true)]
    [InlineData("<div class='middle'><div class='outer'><p><span class='caption'>A</span></p></div></div>", false)]
    [InlineData("<div class='outer middle'><p><span class='caption'>A</span></p></div>", false)]
    [InlineData("<div class='outer'><div class='middle'><div class='middle'><p><span class='caption'>A</span></p></div></div></div>", true)]
    public void DescendantChainsConsumeDistinctAncestorsInOrder(string body, bool styled)
        => Verify(".outer .middle span.caption{color:red}", body, 2, 1.5, styled, false);

    [Theory]
    [InlineData("div span.caption\u00A0")]
    [InlineData("\u00A0div span.caption")]
    [InlineData("div[class\u00A0] span")]
    public void NonCssWhitespaceDoesNotSilentlyChangeADescendantSelector(string selector)
        => UnsupportedGrammarKeepsExplicitRefusalAtBothPublicSeams(selector);

    [Theory]
    [InlineData("@namespace h 'http://www.w3.org/1999/xhtml';h|div.figcenter h|span.caption{color:red}", true)]
    [InlineData("@namespace h 'urn:other';h|div.figcenter span.caption{color:red}", false)]
    [InlineData("@namespace h 'urn:other';div.figcenter h|span.caption{color:red}", false)]
    [InlineData("div[class~='figcenter'] span.caption{color:red}", true)]
    [InlineData("div[class~='figcenter other'] span.caption{color:red}", false)]
    public void DescendantCompoundsRetainNamespacesAndQuotedAttributeBoundaries(string css, bool styled)
        => Verify(css, "<div class='figcenter other'><p><span class='caption'>A</span></p></div>", 2, 1.5, styled, false);

    [Theory]
    [InlineData("body div p span{color:lime}.caption{color:red}")]
    [InlineData(".caption{color:red}body div p span{color:lime}")]
    [InlineData(".figcenter span{color:lime}div .caption{color:red}")]
    [InlineData("div.figcenter span.caption{color:red}span.caption{color:lime}")]
    public void DescendantSpecificityCountsClassesBeforeTypesAndBreaksTiesBySourceOrder(string css)
        => Verify(css, "<div class='figcenter'><p><span class='caption'>A</span></p></div>", 2, 1.5, true, false);

    [Theory]
    [InlineData("<div class='figcenter'><p><span class='caption'>A</span></p></div>", true)]
    [InlineData("<div><p><span class='caption'>A</span></p></div>", false)]
    [InlineData("<div class='figcenter'/><p><span class='caption'>A</span></p>", false)]
    [InlineData("<div class='figcenter'><p><span class='other'>A</span></p></div>", false)]
    [InlineData("<div class='caption'><p><span class='figcenter'>A</span></p></div>", false)]
    public void DescendantRulesRequireTheActualAncestorAndTarget(string body, bool styled)
        => Verify("div.figcenter span.caption{color:red}", body, 2, 1.5, styled, false);

    [Theory]
    [InlineData("div.figcenter span.caption")]
    [InlineData(".figcenter .caption")]
    [InlineData("div span")]
    [InlineData("p span")]
    public void DescendantRuleMatchesAcrossAnInterveningElement(string selector)
        => Verify(selector + "{color:red}",
            "<div class='figcenter'><p><span class='caption'>A</span></p></div>", 2, 1.5, true, false);

    [Theory]
    [InlineData("", false)]
    [InlineData("p.note{color:red;text-align:center;background-color:blue}", true)]
    [InlineData(".note{color:red;text-align:center;background-color:blue}", true)]
    public void ClassOnlyRulesPaintAndAlignRealParagraphs(string css, bool styled)
        => Verify(css, "<p class=\"note\">A</p>", styled ? 12 : 2, styled ? 9 : 1.5, styled, styled);

    [Theory]
    [InlineData(".note{color:red;background-color:blue}", "<p><span class=\"note\">A</span></p>", true, true)]
    [InlineData(".a.b{color:red}", "<p class=\"b a\">A</p>", true, false)]
    [InlineData(".a.b{color:red}", "<p class=\"a\">A</p>", false, false)]
    [InlineData(".a.a{color:red}", "<p class=\"a\">A</p>", true, false)]
    [InlineData(".note{color:red}", "<p class=\"notebook Note\">A</p>", false, false)]
    [InlineData(".note{color:red}", "<p class=\"other&#x9;note&#xA;last\">A</p>", true, false)]
    [InlineData(".unused,.note{color:red}", "<p class=\"note\">A</p>", true, false)]
    public void TokensCombinationsGroupsAndSpansUseActualClassMembership(string css, string body, bool red, bool blue)
        => Verify(css, body, 2, 1.5, red, blue);

    [Theory]
    [InlineData("p{color:lime}.note{color:red}")]
    [InlineData(".note{color:red}p{color:lime}")]
    [InlineData("p.note{color:red}.note{color:lime}")]
    [InlineData(".note{color:lime}p.note{color:red}")]
    [InlineData(".note.other{color:red}p.note{color:lime}")]
    [InlineData("p.note{color:lime}.note.other{color:red}")]
    [InlineData(".note{color:lime}.other{color:red}")]
    [InlineData(".other{color:lime}.note{color:red}")]
    [InlineData(".note.note{color:red}p.note{color:lime}")]
    public void ClassSpecificityAndSourceOrderPreserveIndependentCascade(string css)
        => Verify(css, "<p class=\"other note\">A</p>", 2, 1.5, true, false);

    [Fact]
    public void PropertiesCascadeIndependentlyAndInlineDeclarationsOverrideRules()
    {
        Verify("p.note{color:red}.note{text-align:center;background-color:blue}", "<p class=\"note\">A</p>", 12, 9, true, true);
        Verify(".note.other{color:lime;text-align:right;background-color:blue}",
            "<p class=\"other note\" style=\"color:red;text-align:center\">A</p>", 12, 9, true, true);
    }

    [Theory]
    [InlineData(".note{color:red}", true)]
    [InlineData("@namespace 'http://www.w3.org/1999/xhtml';.note{color:red}", true)]
    [InlineData("@namespace 'urn:other';.note{color:red}", false)]
    [InlineData("@namespace '';.note{color:red}", false)]
    [InlineData("@namespace h 'urn:other';.note{color:red}", true)]
    [InlineData("@namespace h 'http://www.w3.org/1999/xhtml';h|p.note{color:red}", true)]
    [InlineData("@namespace h 'urn:other';h|p.note{color:red}", false)]
    [InlineData("@namespace 'urn:other';*|p.note{color:red}", true)]
    [InlineData("@namespace 'http://www.w3.org/1999/xhtml';|p.note{color:red}", false)]
    [InlineData("@namespace h 'http://www.w3.org/1999/xhtml';h|p.note{color:red}.note{color:lime}", true)]
    public void ImpliedUniversalRespectsDefaultNamespaceAndQualifiedTypesKeepTheirMeaning(string css, bool red)
        => Verify(css, "<p class=\"note\">A</p>", 2, 1.5, red, false);

    [Theory]
    [InlineData(".")]
    [InlineData(".note.")]
    [InlineData(".note..other")]
    [InlineData(".1note")]
    [InlineData(".n\\6fte")]
    [InlineData(".n\u00f6te")]
    [InlineData(".note>.other")]
    [InlineData(".note+.other")]
    [InlineData(".note~.other")]
    [InlineData(".note[title]")]
    [InlineData(".note:first-child")]
    [InlineData("#")]
    [InlineData("##id")]
    [InlineData("#id#")]
    [InlineData("#1id")]
    [InlineData("#id..note")]
    [InlineData("h#id|p")]
    [InlineData("#id|p")]
    [InlineData("h|.note")]
    public void UnsupportedGrammarKeepsExplicitRefusalAtBothPublicSeams(string selector)
    {
        VerifyEngine();
        string css = "@namespace h 'http://www.w3.org/1999/xhtml';" + selector + "{color:red}";
        var book = EpubDocument.Open(Book(css, "<p class=\"note\">A</p>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        Assert.Equal("Native EPUB CSS selector is not implemented.", Assert.Throws<NotSupportedException>(() => book.CreateLayout(
            new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 })).Message);
        Assert.Equal("Native EPUB CSS selector is not implemented.", Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font))).Message);
    }

    [Fact]
    public void MatchingClassOnlyMarginsRemainAnExplicitLayoutBoundary()
    {
        VerifyEngine();
        var book = EpubDocument.Open(Book(".subhd{margin-top:0.5em}", "<p><span class=\"subhd\">A</span></p>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        const string diagnostic = "Native EPUB nonzero margins are implemented only on the body.";
        Assert.Equal(diagnostic, Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 })).Message);
        Assert.Equal(diagnostic, Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font))).Message);
    }

    [Fact]
    public void ClassOnlySelectorsKeepCountLengthWorkAndCancellationBounds()
    {
        VerifyEngine();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        void Reject(string css, string body, string diagnostic)
        {
            var book = EpubDocument.Open(Book(css, body));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal(diagnostic, Assert.Throws<InvalidDataException>(() => book.CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 }, deadline.Token)).Message);
            Assert.Equal(diagnostic, Assert.Throws<InvalidDataException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font), deadline.Token)).Message);
        }
        Reject(string.Concat(Enumerable.Repeat(".note{}", 4097)), "<p>A</p>", "Native EPUB CSS selectors exceed their 4,096-selector limit.");
        Reject("@namespace 'http://www.w3.org/1999/xhtml';" + string.Concat(Enumerable.Repeat(".note{}", 4096)),
            string.Concat(Enumerable.Repeat("<p class=\"note\">A</p>", 16)), "Native EPUB CSS selector matching exceeds its work limit.");
        UnsupportedGrammarKeepsExplicitRefusalAtBothPublicSeams("." + new string('a', 128));
        Verify("." + new string('a', 127) + "{color:red}", "<p class=\"" + new string('a', 127) + "\">A</p>", 2, 1.5, true, false);
        byte[] source = Book(".note{color:red}", "<p class=\"note\">A</p>");
        var canceled = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => EpubDocument.Open(source, canceled));
        var opened = EpubDocument.Open(source);
        var options = new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 };
        Assert.Throws<OperationCanceledException>(() => opened.CreateLayout(options, canceled));
        Assert.Throws<OperationCanceledException>(() => opened.ConvertToPdf(new EpubPdfConversionOptions(font), canceled));
        var layout = opened.CreateLayout(options);
        Assert.Throws<OperationCanceledException>(() => layout.RenderPage(0, cancellationToken: canceled));
    }

    [Theory]
    [InlineData("", 1.5, 7, false)]
    [InlineData("p.note{color:red;text-align:center;background-color:blue}", 9, 17, true)]
    [InlineData(".note{color:red;text-align:center;background-color:blue}", 9, 17, true)]
    public void ClassOnlyRulesConvertAndReopenIndependently(string css, double x, int inkX, bool styled)
    {
        VerifyEngine();
        byte[] source = Book(css, "<p class=\"note\">A</p>"), original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        var letter = Assert.Single(reader.Read(0).Letters);
        Assert.Equal("A", letter.Value);
        Assert.Equal(x, letter.StartBaseLine.X);
        Assert.Equal(4.5, letter.StartBaseLine.Y);
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 16));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(styled ? new byte[] { 0, 0, 255, 255 } : [0, 0, 0, 255], rendered.Pixels.Slice((5 * 30 + inkX) * 4, 4).ToArray());
        Assert.Equal(styled ? new byte[] { 255, 0, 0, 255 } : [255, 255, 255, 255], rendered.Pixels.Slice((11 * 30 + 2) * 4, 4).ToArray());
        Assert.Equal(original, source);
    }

    private static void VerifyEngine()
    {
        string? pinned = Environment.GetEnvironmentVariable("EPUB_EXPECTED_ENGINE_SHA256");
        string loaded = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EpubDocument).Assembly.Location)));
        Console.WriteLine("Loaded engine SHA256: " + loaded);
        if (pinned is not null) Assert.Equal(pinned, loaded);
    }

    private static void Verify(string css, string body, double x, double pdfX, bool red, bool blue, string? linkTarget = null)
    {
        VerifyEngine();
        byte[] source = Book(css, body), original = source.ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(source, deadline.Token);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 }, deadline.Token);
        var page = Assert.Single(layout.Pages);
        Assert.Equal("A", page.Text);
        Assert.Equal(new EpubTextPlacement(0, 0, 1, x, 2, 6, 10)
        { Color = red ? new PdfRgbColor(1, 0, 0) : null, LinkTarget = linkTarget }, Assert.Single(page.TextPlacements));
        var native = layout.RenderPage(0, cancellationToken: deadline.Token);
        Assert.Equal(red ? new float[] { 1, 0, 0, 1 } : [0, 0, 0, 1], native.Pixels.Slice((5 * 30 + (int)x + 5) * 4, 4).ToArray());
        Assert.Equal(blue ? new float[] { 0, 0, 1, 1 } : [1, 1, 1, 1], native.Pixels.Slice((11 * 30 + 2) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }, deadline.Token));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        var letter = Assert.Single(reader.Read(0).Letters);
        Assert.Equal("A", letter.Value);
        Assert.Equal(pdfX, letter.StartBaseLine.X);
        Assert.Equal(4.5, letter.StartBaseLine.Y);
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 16), deadline.Token);
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(red ? new byte[] { 0, 0, 255, 255 } : [0, 0, 0, 255], rendered.Pixels.Slice((5 * 30 + (int)x + 5) * 4, 4).ToArray());
        Assert.Equal(blue ? new byte[] { 255, 0, 0, 255 } : [255, 255, 255, 255], rendered.Pixels.Slice((11 * 30 + 2) * 4, 4).ToArray());
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
            Add("book.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">urn:test:class</dc:identifier><dc:title>Classes</dc:title><dc:language>en</dc:language></metadata><manifest><item id=\"doc\" href=\"text.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"css\" href=\"style.css\" media-type=\"text/css\"/></manifest><spine><itemref idref=\"doc\"/></spine></package>");
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
