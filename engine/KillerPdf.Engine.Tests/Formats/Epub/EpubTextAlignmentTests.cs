using System.IO.Compression;
using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubTextAlignmentTests
{
    [Theory]
    [InlineData("", 2, 1.5, 7)]
    [InlineData("p{text-align:center}", 12, 9, 17)]
    [InlineData("p{text-align:left}", 2, 1.5, 7)]
    [InlineData("p{text-align:right}", 22, 16.5, 27)]
    public void AlignmentPreservesLiteralNativeAndConvertedGeometryAndInk(string css, double x, double pdfX, int inkX)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] source = Book("<p>A</p>", css);
        byte[] preserved = source.ToArray();
        var book = EpubDocument.Open(source, deadline.Token);
        var font = Font();
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 }, deadline.Token);
        var page = Assert.Single(layout.Pages);
        Assert.Equal("A", page.Text);
        Assert.Equal(new EpubTextPlacement(0, 0, 1, x, 2, 6, 10), Assert.Single(page.TextPlacements));
        var native = layout.RenderPage(0, cancellationToken: deadline.Token);
        Assert.Equal(new float[] { 0, 0, 0, 1 }, native.Pixels.Slice((5 * 30 + inkX) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice(0, 4).ToArray());

        byte[] bytes = book.ConvertToPdf(new EpubPdfConversionOptions(font) {
            PageWidthPoints = 22.5, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }, deadline.Token);
        var pdf = PdfDocument.Open(bytes);
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        var letter = Assert.Single(reader.Read(0).Letters);
        Assert.Equal("A", letter.Value);
        Assert.Equal(pdfX, letter.StartBaseLine.X);
        var converted = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 16), deadline.Token);
        Assert.Empty(converted.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, converted.Pixels.Slice((5 * 30 + inkX) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, converted.Pixels.Slice(0, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void JustificationExpandsOnlyNonfinalLineSpacesAndPreservesGlyphSizes()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(Book("<p>A A A</p>", "p{text-align:justify}"), deadline.Token);
        var font = FontWithSpace();
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 26, font) { FontSize = 10, Margin = 2 }, deadline.Token);
        var page = Assert.Single(layout.Pages);
        Assert.Equal("A A A", page.Text);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10),
            new EpubTextPlacement(0, 1, 1, 8, 2, 14, 10),
            new EpubTextPlacement(0, 2, 1, 22, 2, 6, 10),
            new EpubTextPlacement(0, 4, 1, 2, 12, 6, 10) }, page.TextPlacements);
        var native = layout.RenderPage(0, cancellationToken: deadline.Token);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((5 * 30 + 13) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 0, 1 }, native.Pixels.Slice((5 * 30 + 27) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 0, 1 }, native.Pixels.Slice((15 * 30 + 7) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((15 * 30 + 27) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font) {
            PageWidthPoints = 22.5, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5 }, deadline.Token));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        var letters = reader.Read(0).Letters.Where(letter => letter.Value == "A").ToArray();
        Assert.Equal(new[] { 1.5, 16.5, 1.5 }, letters.Select(letter => letter.StartBaseLine.X));
        Assert.Equal(new[] { "A", "A", "A" }, letters.Select(letter => letter.Value));
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 26), deadline.Token);
        Assert.Empty(frame.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, frame.Pixels.Slice((5 * 30 + 27) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, frame.Pixels.Slice((15 * 30 + 7) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, frame.Pixels.Slice((15 * 30 + 27) * 4, 4).ToArray());
    }

    [Theory]
    [InlineData("<p>A</p>", "body{text-align:center}", 12, 9)]
    [InlineData("<p>A</p>", "body{text-align:center}p{text-align:right}", 22, 16.5)]
    [InlineData("<p class=\"a\">A</p>", "p.a{text-align:center}p{text-align:right}", 12, 9)]
    [InlineData("<p class=\"a\">A</p>", "p.a{text-align:left}p.a{text-align:right}", 22, 16.5)]
    [InlineData("<p style=\"text-align:left\">A</p>", "body{text-align:center}p{text-align:right}", 2, 1.5)]
    [InlineData("<p style=\"text-align:inherit\">A</p>", "body{text-align:center}p{text-align:right}", 12, 9)]
    [InlineData("<p>A</p>", "body{margin-left:3px;margin-right:1px;text-align:center}", 13, 9.75)]
    public void AlignmentCascadesAndInheritsWithinTheActualContentBox(string body, string css, double x, double pdfX)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(Book(body, css), deadline.Token);
        var font = Font();
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 }, deadline.Token);
        Assert.Equal(new EpubTextPlacement(0, 0, 1, x, 2, 6, 10), Assert.Single(Assert.Single(layout.Pages).TextPlacements));
        Assert.Equal(new float[] { 0, 0, 0, 1 }, layout.RenderPage(0, cancellationToken: deadline.Token)
            .Pixels.Slice((5 * 30 + (int)x + 5) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font) {
            PageWidthPoints = 22.5, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }, deadline.Token));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        var letter = Assert.Single(reader.Read(0).Letters);
        Assert.Equal("A", letter.Value);
        Assert.Equal(pdfX, letter.StartBaseLine.X);
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 16), deadline.Token);
        Assert.Empty(frame.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, frame.Pixels.Slice((5 * 30 + (int)x + 5) * 4, 4).ToArray());
    }

    [Theory]
    [InlineData("left", 2, 13, 2, 5, 1.5, 9.75, 1.5)]
    [InlineData("center", 6.5, 17.5, 12, 5, 4.875, 13.125, 9)]
    [InlineData("right", 11, 22, 22, 5, 8.25, 16.5, 16.5)]
    [InlineData("justify", 2, 22, 2, 14, 1.5, 16.5, 1.5)]
    public void AlignmentPreservesWrappingPaginationAndAnchors(string alignment, double first, double second,
        double final, double spaceWidth, double pdfFirst, double pdfSecond, double pdfFinal)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(Book("<p>A A <span id=\"last\">A</span></p>", "p{text-align:" + alignment + "}"), deadline.Token);
        var font = FontWithSpace();
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 }, deadline.Token);
        Assert.Equal(new[] { "A A ", "A" }, layout.Pages.Select(page => page.Text));
        Assert.Equal(new[] { 0, 4 }, layout.Pages.Select(page => page.TextStart));
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, first, 2, 6, 10),
            new EpubTextPlacement(0, 1, 1, first + 6, 2, spaceWidth, 10),
            new EpubTextPlacement(0, 2, 1, second, 2, 6, 10) }, layout.Pages[0].TextPlacements);
        Assert.Equal(new EpubTextPlacement(0, 4, 1, final, 2, 6, 10), Assert.Single(layout.Pages[1].TextPlacements));
        Assert.Equal(new EpubNavigationDestination(1, 0, 4, final, 2), layout.ResolveNavigationTarget("doc.xhtml#last"));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font) {
            PageWidthPoints = 22.5, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }, deadline.Token));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(2, reader.PageCount);
        Assert.Equal(new[] { pdfFirst, pdfSecond }, reader.Read(0).Letters.Where(letter => letter.Value == "A").Select(letter => letter.StartBaseLine.X));
        var last = Assert.Single(reader.Read(1).Letters);
        Assert.Equal("A", last.Value);
        Assert.Equal(pdfFinal, last.StartBaseLine.X);
        Assert.Equal(4.5, last.StartBaseLine.Y);
        for (int page = 0; page < 2; page++)
        {
            int inkX = page == 0 ? (int)second + 5 : (int)final + 5;
            Assert.Equal(new float[] { 0, 0, 0, 1 }, layout.RenderPage(page, cancellationToken: deadline.Token)
                .Pixels.Slice((5 * 30 + inkX) * 4, 4).ToArray());
            var frame = new PdfPageRenderer(pdf).Render(page, new PdfRenderOptions(30, 16), deadline.Token);
            Assert.Empty(frame.Diagnostics);
            Assert.Equal(new byte[] { 0, 0, 0, 255 }, frame.Pixels.Slice((5 * 30 + inkX) * 4, 4).ToArray());
        }
    }

    [Fact]
    public void InlineAlignmentKeepsOneBlockAndMovesInlineBackgroundWithItsText()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(Book("<p>A<span style=\"text-align:right;background-color:red\">A</span></p>",
            "p{text-align:center;background-color:blue}"), deadline.Token);
        var font = Font();
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 }, deadline.Token);
        var page = Assert.Single(layout.Pages);
        Assert.Equal("AA", page.Text);
        Assert.Equal(new[] { new EpubTextPlacement(0, 0, 1, 9, 2, 6, 10), new EpubTextPlacement(0, 1, 1, 15, 2, 6, 10) }, page.TextPlacements);
        var frame = layout.RenderPage(0, cancellationToken: deadline.Token);
        Assert.Equal(new float[] { 0, 0, 1, 1 }, frame.Pixels.Slice((11 * 30 + 2) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 0, 0, 1 }, frame.Pixels.Slice((11 * 30 + 15) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font) {
            PageWidthPoints = 22.5, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }, deadline.Token));
        Assert.Equal(new[] { 6.75, 11.25 }, new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.StartBaseLine.X));
        var converted = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 16), deadline.Token);
        Assert.Empty(converted.Diagnostics);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, converted.Pixels.Slice((11 * 30 + 2) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, converted.Pixels.Slice((11 * 30 + 15) * 4, 4).ToArray());
    }

    [Theory]
    [InlineData("<p>A A</p>", 3)]
    [InlineData("<p>A A<br/>A</p>", 4)]
    public void JustificationLeavesMultiwordFinalAndForcedBreakLinesUnexpanded(string body, int count)
    {
        var book = EpubDocument.Open(Book(body, "p{text-align:justify}"));
        var font = FontWithSpace();
        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(30, 26, font) { FontSize = 10, Margin = 2 }).Pages);
        Assert.Equal(count, page.TextPlacements.Count);
        Assert.Equal(new[] { 2d, 8, 13 }, page.TextPlacements.Take(3).Select(item => item.Left));
        Assert.Equal(5, page.TextPlacements[1].Width);
        if (count == 4) Assert.Equal((2d, 12d), (page.TextPlacements[3].Left, page.TextPlacements[3].Top));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font) {
            PageWidthPoints = 22.5, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters.Where(letter => letter.Value == "A").ToArray();
        Assert.Equal(count == 4 ? new[] { 1.5, 9.75, 1.5 } : new[] { 1.5, 9.75 }, letters.Select(letter => letter.StartBaseLine.X));
    }

    [Fact]
    public void CenteringPreservesMixedFontRunsAndIndependentlyCascadedColor()
    {
        var book = EpubDocument.Open(Book("<p>A<span style=\"font-family:Other;font-size:5px;color:red;text-align:right\">A</span></p>", "p{text-align:center}"));
        var font = Font();
        var other = Font();
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 16, font) {
            FontSize = 10, Margin = 2, FontFamilyResolver = _ => other });
        var page = Assert.Single(layout.Pages);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 10.5, 2, 6, 10),
            new EpubTextPlacement(0, 1, 1, 16.5, 6, 3, 5) { Color = new PdfRgbColor(1, 0, 0) } }, page.TextPlacements);
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((7 * 30 + 19) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font) {
            PageWidthPoints = 22.5, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5,
            FontFamilyResolver = _ => other }));
        Assert.Equal(new[] { 7.875, 12.375 }, new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.StartBaseLine.X));
        var converted = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 16));
        Assert.Empty(converted.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, converted.Pixels.Slice((7 * 30 + 19) * 4, 4).ToArray());
    }

    [Fact]
    public void CenteringMovesInlineImagesWithTheLineWithoutResizingThem()
    {
        byte[] image = Convert.FromHexString("89504E470D0A1A0A0000000D4948445200000002000000020802000000FDD49A730000001049444154789C63F8CFC0C000C260020019F402FEAB4156BC0000000049454E44AE426082");
        var book = EpubDocument.Open(Book("<p>A<img src=\"img.png\" alt=\"\"/></p>", "p{text-align:center}", image));
        var font = Font();
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 });
        Assert.Equal(new EpubTextPlacement(0, 0, 1, 11, 2, 6, 10), Assert.Single(Assert.Single(layout.Pages).TextPlacements));
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((8 * 30 + 17) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 1, 0, 1 }, native.Pixels.Slice((8 * 30 + 18) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 1, 1 }, native.Pixels.Slice((9 * 30 + 17) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font) {
            PageWidthPoints = 22.5, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        Assert.Equal(8.25, Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).StartBaseLine.X);
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 16));
        Assert.Empty(frame.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, frame.Pixels.Slice((8 * 30 + 17) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, frame.Pixels.Slice((8 * 30 + 18) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, frame.Pixels.Slice((9 * 30 + 17) * 4, 4).ToArray());
        Assert.Equal(image, book.ReadResource("img.png"));
    }

    [Fact]
    public void AlignmentPreservesCancellationAtEveryPublicEntryPoint()
    {
        byte[] bytes = Book("<p>A</p>", "p{text-align:center}");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => EpubDocument.Open(bytes, canceled.Token));
        var book = EpubDocument.Open(bytes);
        var font = Font();
        var options = new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 };
        Assert.Throws<OperationCanceledException>(() => book.CreateLayout(options, canceled.Token));
        var layout = book.CreateLayout(options);
        Assert.Throws<OperationCanceledException>(() => layout.RenderPage(0, cancellationToken: canceled.Token));
        Assert.Throws<OperationCanceledException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font), canceled.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlignmentHandlesBoundedLongLinesAndManyStyleRuns(bool manyRuns)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string body = manyRuns ? string.Concat(Enumerable.Repeat("<span>A</span>", 2000)) : new string('A', 12000);
        var book = EpubDocument.Open(Book("<p>" + body + "</p>", "p{text-align:center}"), deadline.Token);
        var layout = book.CreateLayout(new EpubLayoutOptions(1024, 16, Font()) { FontSize = .1, Margin = 2 }, deadline.Token);
        Assert.Equal(manyRuns ? 2000 : 12000, Assert.Single(layout.Pages).TextPlacements.Count);
    }

    [Theory]
    [InlineData("&#x05D0;")]
    [InlineData("&#x4E00;")]
    [InlineData("&#x202E;")]
    public void AlignmentDoesNotSilentlyEnableUnsupportedShaping(string scalar)
    {
        var book = EpubDocument.Open(Book("<p>" + scalar + "</p>", "p{text-align:center}"));
        var font = Font();
        Assert.Contains("shaping or bidirectional", Assert.Throws<NotSupportedException>(() => book.CreateLayout(
            new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 })).Message);
        Assert.Contains("shaping or bidirectional", Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(
            new EpubPdfConversionOptions(font))).Message);
    }

    private static TrueTypeFont Font() => TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));

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
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(36), 2);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(40), 65);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(44), 65);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(48), 1);
        byte[] bytes = TrueTypeFontTests.BuildTestFont(false, includeOutlines: true, cmap: cmap, includeCompound: true);
        int count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4));
        for (int record = 12; record < 12 + count * 16; record += 16)
        {
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(record + 8));
            string tag = Encoding.ASCII.GetString(bytes, record, 4);
            if (tag == "loca")
            {
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 4), 20);
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 8), 44);
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 12), 44);
            }
            if (tag == "hmtx") BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 8), 500);
        }
        return TrueTypeFont.Load(bytes);
    }

    private static byte[] Book(string body, string css, byte[]? image = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"package.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            Add("package.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">alignment</dc:identifier><dc:title>Alignment</dc:title><dc:language>en</dc:language></metadata><manifest><item id=\"doc\" href=\"doc.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>" + (image is null ? "" : "<item id=\"image\" href=\"img.png\" media-type=\"image/png\"/>") + "</manifest><spine><itemref idref=\"doc\"/></spine></package>");
            Add("doc.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><link rel=\"stylesheet\" type=\"text/css\" href=\"style.css\"/></head><body>" + body + "</body></html>");
            Add("style.css", css);
            if (image is not null)
            {
                using var stream = zip.CreateEntry("img.png", CompressionLevel.NoCompression).Open();
                stream.Write(image);
            }
            void Add(string name, string value)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(value));
            }
        }
        return output.ToArray();
    }
}
