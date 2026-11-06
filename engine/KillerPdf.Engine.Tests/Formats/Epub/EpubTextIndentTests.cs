using System.IO.Compression;
using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubTextIndentTests
{
    [Theory]
    [InlineData("72px", 80d, 60d)]
    [InlineData("96px", 104d, 78d)]
    public void IndentedFirstWordMayOverflowWhileTheNextWordWraps(string indent, double nativeLeft, double pdfLeft)
    {
        byte[] source = Book("<p>AA AA</p>", "p{text-indent:" + indent + "}"), original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = FontWithSpace();
        var layout = book.CreateLayout(new EpubLayoutOptions(96, 96, font) { FontSize = 18, Margin = 8 });
        var lines = Assert.Single(layout.Pages).TextPlacements.GroupBy(p => p.Top).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal(nativeLeft, lines[0].First().Left, 6);
        Assert.Equal(2, lines[0].Count());
        Assert.Equal(8, lines[1].First().Left, 6);
        Assert.Equal(2, lines[1].Count());
        Assert.Contains(layout.RenderPage(0).Pixels.ToArray(), channel => channel == 0);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font) {
            PageWidthPoints = 72, PageHeightPoints = 72, MarginPoints = 6, FontSizePoints = 13.5 }));
        var pdfLines = new PdfPageContentReader(pdf).Read(0).Letters.GroupBy(letter => letter.StartBaseLine.Y).ToArray();
        Assert.Equal(2, pdfLines.Length);
        Assert.Equal(pdfLeft, pdfLines[0].First().StartBaseLine.X, 6);
        Assert.Equal(2, pdfLines[0].Count());
        Assert.Equal(6, pdfLines[1].First().StartBaseLine.X, 6);
        Assert.Equal(2, pdfLines[1].Count());
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(96, 96));
        Assert.Empty(frame.Diagnostics);
        Assert.Contains(frame.Pixels.ToArray(), channel => channel == 0);
        Assert.Equal(original, source);
    }

    [Fact]
    public void ParagraphIndentMovesOnlyFirstLineInNativeAndReopenedPdf()
    {
        var book = EpubDocument.Open(Book("<p>AA AA AA AA AA AA</p>", "p{text-indent:1.25em}"));
        var font = FontWithSpace();
        var layout = book.CreateLayout(new EpubLayoutOptions(96, 96, font) { FontSize = 18, Margin = 8 });
        var lines = layout.Pages[0].TextPlacements.GroupBy(p => p.Top).ToArray();
        Assert.True(lines.Length >= 2);
        Assert.Equal(30.5, lines[0].First().Left, 6);
        Assert.Equal(8, lines[1].First().Left, 6);
        Assert.Contains(layout.RenderPage(0).Pixels.ToArray(), channel => channel == 0);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font) {
            PageWidthPoints = 72, PageHeightPoints = 72, MarginPoints = 6, FontSizePoints = 13.5 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(22.875, letters[0].BoundingBox.Left, 6);
        Assert.Contains(letters, letter => Math.Abs(letter.BoundingBox.Left - 6) < 0.000001);
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(96, 96));
        Assert.Empty(frame.Diagnostics);
        Assert.Contains(frame.Pixels.ToArray(), channel => channel == 0);
    }

    [Fact]
    public void InheritedPercentageUsesContainingBlockWidthAfterFontSizeChange()
    {
        AssertFirstPosition("<p style='font-size:12px;text-indent:inherit'>AA AA AA</p>",
            "body{text-indent:20%}", 24, 18);
    }

    [Theory]
    [InlineData("12px", 20, 15)]
    [InlineData("-4px", 4, 3)]
    [InlineData("-12px", -4, -3)]
    [InlineData("0", 8, 6)]
    [InlineData("+.5em", 17, 12.75)]
    [InlineData("-5%", 4, 3)]
    public void SignedLengthsAndZeroPreserveCoordinates(string value, double nativeLeft, double pdfLeft)
        => AssertFirstPosition("<p style='text-indent:" + value + "'>AA AA</p>", null, nativeLeft, pdfLeft);

    [Theory]
    [InlineData("", 56, 42)]
    [InlineData("text-indent:inherit", 56, 42)]
    [InlineData("text-indent:unset", 56, 42)]
    [InlineData("text-indent:initial", 8, 6)]
    public void EmIsComputedOnDeclaringElementBeforeInheritance(string declaration, double nativeLeft, double pdfLeft)
        => AssertFirstPosition("<p style='font-size:12px;" + declaration + "'>AA</p>",
            "body{font-size:24px;text-indent:2em}", nativeLeft, pdfLeft);

    [Fact]
    public void InlineOnlyIndentDoesNotMoveParentLine()
        => AssertFirstPosition("<p><span style='text-indent:12px'>AA</span></p>", null, 8, 6);

    [Fact]
    public void InlineDeclarationOverridesSpecificClassAndLaterTypeRules()
        => AssertFirstPosition("<p class='lead' style='text-indent:4px'>AA</p>",
            "p.lead{text-indent:12px}p{text-indent:20px}", 12, 9);

    [Fact]
    public void ClassSpecificityWinsOverLaterTypeRule()
        => AssertFirstPosition("<p class='lead'>AA</p>",
            "p.lead{text-indent:12px}p{text-indent:20px}", 20, 15);

    [Fact]
    public void ForcedBreakAndPageContinuationDoNotReindentButNextParagraphDoes()
    {
        var book = EpubDocument.Open(Book("<p>AA<br/>AA<br/>AA<br/>AA</p><p>AA</p>", "p{text-indent:1.25em}"));
        var font = FontWithSpace();
        var layout = book.CreateLayout(new EpubLayoutOptions(96, 64, font) { FontSize = 18, Margin = 8 });
        Assert.True(layout.Pages.Count >= 2);
        Assert.Equal(new double[] { 30.5, 8, 8, 8, 30.5 }, layout.Pages
            .SelectMany(page => page.TextPlacements.GroupBy(p => p.Top).Select(line => line.First().Left)));
        foreach (int page in Enumerable.Range(0, layout.Pages.Count))
            Assert.Contains(layout.RenderPage(page).Pixels.ToArray(), channel => channel == 0);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font) {
            PageWidthPoints = 72, PageHeightPoints = 48, MarginPoints = 6, FontSizePoints = 13.5 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(new double[] { 22.875, 6, 6, 6, 22.875 }, Enumerable.Range(0, reader.PageCount)
            .SelectMany(page => reader.Read(page).Letters.GroupBy(letter => letter.StartBaseLine.Y)
                .Select(line => line.First().StartBaseLine.X)));
        foreach (int page in Enumerable.Range(0, reader.PageCount))
        {
            var frame = new PdfPageRenderer(pdf).Render(page, new PdfRenderOptions(96, 64));
            Assert.Empty(frame.Diagnostics);
            Assert.Contains(frame.Pixels.ToArray(), channel => channel == 0);
        }
    }

    [Theory]
    [InlineData("center", 48.45, 36.3375)]
    [InlineData("right", 66.4, 49.8)]
    public void AlignmentUsesReducedFirstLineBox(string alignment, double nativeLeft, double pdfLeft)
        => AssertFirstPosition("<p>AA</p>", "p{text-indent:1.25em;text-align:" + alignment + "}", nativeLeft, pdfLeft);

    [Fact]
    public void JustificationFillsReducedFirstLineBoxAndFullContinuationBox()
    {
        var book = EpubDocument.Open(Book("<p>AA AA AA AA AA AA AA AA</p>", "p{text-indent:1.25em;text-align:justify}"));
        var font = FontWithSpace();
        var layout = book.CreateLayout(new EpubLayoutOptions(96, 96, font) { FontSize = 18, Margin = 8 });
        var lines = layout.Pages[0].TextPlacements.GroupBy(p => p.Top).ToArray();
        Assert.Equal(30.5, lines[0].First().Left, 6);
        Assert.Equal(88, lines[0].Last().Left + lines[0].Last().Width, 6);
        Assert.Equal(8, lines[1].First().Left, 6);
        Assert.Equal(88, lines[1].Last().Left + lines[1].Last().Width, 6);
        Assert.Contains(layout.RenderPage(0).Pixels.ToArray(), channel => channel == 0);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font) {
            PageWidthPoints = 72, PageHeightPoints = 72, MarginPoints = 6, FontSizePoints = 13.5 }));
        var pdfLines = new PdfPageContentReader(pdf).Read(0).Letters.GroupBy(letter => letter.StartBaseLine.Y).ToArray();
        Assert.Equal(22.875, pdfLines[0].First().StartBaseLine.X, 6);
        Assert.Equal(66, pdfLines[0].Last().EndBaseLine.X, 6);
        Assert.Equal(6, pdfLines[1].First().StartBaseLine.X, 6);
        Assert.Equal(66, pdfLines[1].Last().EndBaseLine.X, 6);
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(96, 96));
        Assert.Empty(frame.Diagnostics);
        Assert.Contains(frame.Pixels.ToArray(), channel => channel == 0);
    }

    [Fact]
    public void WrappedParagraphDoesNotRestartIndentOnNewPage()
    {
        var book = EpubDocument.Open(Book("<p>AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA</p>", "p{text-indent:1.25em}"));
        var font = FontWithSpace();
        var layout = book.CreateLayout(new EpubLayoutOptions(96, 64, font) { FontSize = 18, Margin = 8 });
        Assert.True(layout.Pages.Count >= 2);
        Assert.Equal(30.5, layout.Pages[0].TextPlacements[0].Left, 6);
        Assert.All(layout.Pages.Skip(1), page => Assert.Equal(8, page.TextPlacements[0].Left, 6));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font) {
            PageWidthPoints = 72, PageHeightPoints = 48, MarginPoints = 6, FontSizePoints = 13.5 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(layout.Pages.Count, reader.PageCount);
        Assert.Equal(22.875, reader.Read(0).Letters[0].StartBaseLine.X, 6);
        foreach (int page in Enumerable.Range(0, reader.PageCount))
        {
            if (page > 0) Assert.Equal(6, reader.Read(page).Letters[0].StartBaseLine.X, 6);
            Assert.Contains(layout.RenderPage(page).Pixels.ToArray(), channel => channel == 0);
            var frame = new PdfPageRenderer(pdf).Render(page, new PdfRenderOptions(96, 64));
            Assert.Empty(frame.Diagnostics);
            Assert.Contains(frame.Pixels.ToArray(), channel => channel == 0);
        }
    }

    [Theory]
    [InlineData("1px hanging")]
    [InlineData("1em each-line")]
    [InlineData("calc(1px + 1em)")]
    [InlineData("NaNpx")]
    [InlineData("32769px")]
    [InlineData("2000em")]
    [InlineData("32769%")]
    public void UnsupportedAndOutOfBoundsValuesRetainExplicitFailure(string value)
    {
        byte[] source = Book("<p style='text-indent:" + value + "'>AA</p>");
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = FontWithSpace();
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(96, 96, font)
            { FontSize = 18, Margin = 8 }));
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)
            { PageWidthPoints = 72, PageHeightPoints = 72, MarginPoints = 6, FontSizePoints = 13.5 }));
        Assert.Equal(original, source);
    }

    [Fact]
    public void PercentageIndentLimitAppliesToTheComputedContainingBlockWidth()
    {
        var book = EpubDocument.Open(Book("<p>AA</p>", "p{text-indent:32768%}"));
        var font = FontWithSpace();
        var narrow = book.CreateLayout(new EpubLayoutOptions(96, 96, font) { FontSize = 18, Margin = 8 });
        Assert.Equal(26222.4, Assert.Single(narrow.Pages).TextPlacements[0].Left, 6);
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(144, 96, font)
            { FontSize = 18, Margin = 8 }));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font) {
            PageWidthPoints = 72, PageHeightPoints = 72, MarginPoints = 6, FontSizePoints = 13.5 }));
        Assert.Equal(19666.8, new PdfPageContentReader(pdf).Read(0).Letters[0].StartBaseLine.X, 6);
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)
            { PageWidthPoints = 108, PageHeightPoints = 72, MarginPoints = 6, FontSizePoints = 13.5 }));
    }

    [Fact]
    public void IndentedLayoutAndConversionRetainCancellationAndSourceBytes()
    {
        byte[] source = Book("<p style='text-indent:1.25em'>AA AA</p>");
        byte[] original = source.ToArray();
        var font = FontWithSpace();
        var book = EpubDocument.Open(source);
        var options = new EpubLayoutOptions(96, 96, font) { FontSize = 18, Margin = 8 };
        var layout = book.CreateLayout(options);
        var canceled = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => book.CreateLayout(options, canceled));
        Assert.Throws<OperationCanceledException>(() => layout.RenderPage(0, cancellationToken: canceled));
        Assert.Throws<OperationCanceledException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font), canceled));
        Assert.Equal(original, source);
    }

    private static void AssertFirstPosition(string body, string? css, double nativeLeft, double pdfLeft)
    {
        var book = EpubDocument.Open(Book(body, css));
        var font = FontWithSpace();
        var layout = book.CreateLayout(new EpubLayoutOptions(96, 96, font) { FontSize = 18, Margin = 8 });
        Assert.Equal(nativeLeft, layout.Pages[0].TextPlacements[0].Left, 6);
        Assert.Contains(layout.RenderPage(0).Pixels.ToArray(), channel => channel == 0);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font) {
            PageWidthPoints = 72, PageHeightPoints = 72, MarginPoints = 6, FontSizePoints = 13.5 }));
        Assert.Equal(pdfLeft, new PdfPageContentReader(pdf).Read(0).Letters[0].BoundingBox.Left, 6);
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(96, 96));
        Assert.Empty(frame.Diagnostics);
        Assert.Contains(frame.Pixels.ToArray(), channel => channel == 0);
    }

    private static TrueTypeFont FontWithSpace()
    {
        byte[] cmap = new byte[52];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6), 10);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8), 12);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(12), 12);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(16), 40);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(24), 2);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(28), 32);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(32), 32);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(36), 1);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(40), 65);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(44), 65);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(48), 2);
        byte[] bytes = TrueTypeFontTests.BuildTestFont(format12: true, includeOutlines: true,
            includeCompound: true, cmap: cmap);
        int tables = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4));
        for (int table = 0; table < tables; table++)
        {
            int record = 12 + table * 16;
            if (!bytes.AsSpan(record, 4).SequenceEqual("loca"u8)) continue;
            int offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(record + 8)));
            // Glyph 1 is an empty space; glyph 2 retains the fixture's A outline.
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 4), 20);
            return TrueTypeFont.Load(bytes);
        }
        throw new InvalidOperationException("The synthetic font must contain a location table.");
    }

    private static byte[] Book(string body, string? stylesheet = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='book.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            string manifest = stylesheet is null ? "" : "<item id='css' href='style.css' media-type='text/css'/>";
            Add("book.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>urn:font-keywords</dc:identifier><dc:title>Typography</dc:title><dc:language>en</dc:language></metadata><manifest><item id='chapter' href='text.xhtml' media-type='application/xhtml+xml'/>" + manifest + "</manifest><spine><itemref idref='chapter'/></spine></package>");
            string link = stylesheet is null ? "" : "<link rel='stylesheet' href='style.css'/>";
            Add("text.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head>" + link + "</head><body>" + body + "</body></html>");
            if (stylesheet is not null) Add("style.css", stylesheet);
            void Add(string path, string text)
            {
                var entry = zip.CreateEntry(path, path == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return output.ToArray();
    }
}
