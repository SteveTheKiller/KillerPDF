using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubRelativeTypographyTests
{
    [Fact]
    public void InlinePercentageFontSizeUsesParentComputedSize()
    {
        AssertMixedSizes("<p style='font-size:20px'>A<span style='font-size:50%'>A</span></p>");
    }

    [Fact]
    public void PackagedEmFontSizeUsesParentComputedSize()
    {
        AssertMixedSizes("<p>A<span>A</span></p>", "p{font-size:20px}span{font-size:.5em}");
    }

    [Fact]
    public void InlineWinnerResolvesAgainstParentInsteadOfEarlierStylesheetSize()
    {
        AssertMixedSizes("<p>A<span style='font-size:50%'>A</span></p>",
            "p{font-size:20px}span{font-size:.5em}");
    }

    [Fact]
    public void InheritedUnitlessLineHeightScalesWithChildFontSize()
    {
        byte[] source = Book("<p style='font-size:10px;line-height:1.5'><span style='font-size:20px'>A<br/>A</span></p>");
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(64, 96, font) { FontSize = 10, Margin = 8 });

        var page = Assert.Single(layout.Pages);
        Assert.Equal("A\nA", page.Text);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 8, 13, 12, 20),
            new EpubTextPlacement(0, 2, 1, 8, 43, 12, 20) }, page.TextPlacements);
        var frame = layout.RenderPage(0);
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((49 * 64 + 18) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((35 * 64 + 18) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font) {
            PageWidthPoints = 48, PageHeightPoints = 72, MarginPoints = 6, FontSizePoints = 7.5 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal("AA", string.Concat(letters.Select(letter => letter.Value)));
        Assert.Equal(new double[] { 15, 15 }, letters.Select(letter => letter.FontSize));
        Assert.Equal(new double[] { 50.25, 27.75 }, letters.Select(letter => letter.StartBaseLine.Y));
        var convertedFrame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(64, 96));
        Assert.Empty(convertedFrame.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, convertedFrame.Pixels.Slice((49 * 64 + 18) * 4, 4).ToArray());
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("150%")]
    [InlineData("1.5em")]
    public void RelativeLineHeightComputesAgainstOwnFontThenInheritsAsLength(string lineHeight)
    {
        var book = EpubDocument.Open(Book("<p><span style='font-size:20px;line-height:" + lineHeight
            + "'><span style='font-size:10px'>A<br/>A</span></span></p>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true));

        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(64, 96, font) {
            FontSize = 10, Margin = 8 }).Pages);

        Assert.Equal("A\nA", page.Text);
        // The 20px ancestor contributes ascent 21; its 10px child contributes descent 12.
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 8, 21, 6, 10),
            new EpubTextPlacement(0, 2, 1, 8, 54, 6, 10) }, page.TextPlacements);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font) {
            PageWidthPoints = 48, PageHeightPoints = 72, MarginPoints = 6, FontSizePoints = 7.5 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal("AA", string.Concat(letters.Select(letter => letter.Value)));
        Assert.Equal(new double[] { 7.5, 7.5 }, letters.Select(letter => letter.FontSize));
        Assert.Equal(new double[] { 50.25, 25.5 }, letters.Select(letter => letter.StartBaseLine.Y));
    }

    [Fact]
    public void NestedPercentagesEachUseTheirImmediateParentComputedSize()
    {
        var book = EpubDocument.Open(Book("<p style='font-size:40px'><span style='font-size:50%'>A<span style='font-size:50%'>A</span></span></p>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true));

        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(64, 64, font) {
            FontSize = 10, Margin = 8 }).Pages);

        Assert.Equal("AA", page.Text);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 8, 24, 12, 20),
            new EpubTextPlacement(0, 1, 1, 20, 32, 6, 10) }, page.TextPlacements);
    }

    [Theory]
    [InlineData(".5em")]
    [InlineData("50%")]
    public void SmallRelativeLineHeightInheritsItsComputedLength(string lineHeight)
    {
        var book = EpubDocument.Open(Book("<p><span style='font-size:20px;line-height:" + lineHeight
            + "'><span style='font-size:10px'>A<br/>A</span></span></p>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true));

        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(64, 96, font) {
            FontSize = 5, Margin = 8 }).Pages);

        // Negative leading still leaves the ancestor ascent 11 and child descent 2.
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 8, 11, 6, 10),
            new EpubTextPlacement(0, 2, 1, 8, 24, 6, 10) }, page.TextPlacements);
    }

    [Theory]
    [InlineData("font-size:400%")]
    [InlineData("font-size:4em")]
    [InlineData("line-height:400%")]
    [InlineData("line-height:4em")]
    [InlineData("line-height:4")]
    [InlineData("font-size:0%")]
    [InlineData("font-size:-.5em")]
    [InlineData("font-size:NaN%")]
    [InlineData("font-size:Infinityem")]
    [InlineData("font-size:1e2%")]
    [InlineData("font-size:1rem")]
    [InlineData("font-size:20")]
    [InlineData("line-height:normal")]
    [InlineData("line-height:0")]
    [InlineData("line-height:-1")]
    [InlineData("width:50%")]
    public void RelativeTypographyPreservesInvalidUnsupportedAndComputedDimensionGuards(string declaration)
    {
        AssertRejected(declaration);
    }

    [Fact]
    public void RelativeMultiplicationRejectsNonfiniteComputedDimensions()
    {
        AssertRejected("font-size:" + new string('9', 308) + "em");
        AssertRejected("line-height:" + new string('9', 308));
    }

    private static void AssertRejected(string declaration)
    {
        byte[] source = Book("<p style='font-size:20px'><span style='" + declaration + "'>A</span></p>");
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true));

        Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(64, 64, font) {
            FontSize = 10, Margin = 8 }));
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font) {
            PageWidthPoints = 48, PageHeightPoints = 48, MarginPoints = 6, FontSizePoints = 7.5 }));
        Assert.Equal(original, source);
    }

    private static void AssertMixedSizes(string body, string? stylesheet = null)
    {
        byte[] source = Book(body, stylesheet);
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(64, 64, font) { FontSize = 10, Margin = 8 });

        var page = Assert.Single(layout.Pages);
        Assert.Equal("AA", page.Text);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 8, 8, 12, 20),
            new EpubTextPlacement(0, 1, 1, 20, 16, 6, 10) }, page.TextPlacements);
        var frame = layout.RenderPage(0);
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((19 * 64 + 25) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((10 * 64 + 25) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font) {
            PageWidthPoints = 48, PageHeightPoints = 48, MarginPoints = 6, FontSizePoints = 7.5 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        var letters = reader.Read(0).Letters;
        Assert.Equal("AA", string.Concat(letters.Select(letter => letter.Value)));
        Assert.Equal(new double[] { 15, 7.5 }, letters.Select(letter => letter.FontSize));
        Assert.Equal(new double[] { 6, 15 }, letters.Select(letter => letter.StartBaseLine.X));
        Assert.Equal(new double[] { 30, 30 }, letters.Select(letter => letter.StartBaseLine.Y));
        var convertedFrame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(64, 64));
        Assert.Empty(convertedFrame.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, convertedFrame.Pixels.Slice((19 * 64 + 25) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, convertedFrame.Pixels.Slice((10 * 64 + 25) * 4, 4).ToArray());
        Assert.Equal(original, source);
    }

    private static byte[] Book(string body, string? stylesheet = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='book.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            string manifest = stylesheet is null ? "" : "<item id='css' href='style.css' media-type='text/css'/>";
            Add("book.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>urn:relative-typography</dc:identifier><dc:title>Typography</dc:title><dc:language>en</dc:language></metadata><manifest><item id='chapter' href='text.xhtml' media-type='application/xhtml+xml'/>" + manifest + "</manifest><spine><itemref idref='chapter'/></spine></package>");
            string link = stylesheet is null ? "" : "<link rel='stylesheet' href='style.css'/>";
            Add("text.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head>" + link + "</head><body>" + body + "</body></html>");
            if (stylesheet is not null) Add("style.css", stylesheet);
            void Add(string path, string text)
            {
                var entry = zip.CreateEntry(path, path == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return output.ToArray();
    }
}
