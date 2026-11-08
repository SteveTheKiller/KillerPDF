using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubExplicitLineBreakTests
{
    [Theory]
    [InlineData("", 2, 19.5)]
    [InlineData("<br/>", 12, 12)]
    public void LeadingBodyBreakReservesOneLineWithoutInventingSourceText(string prefix, double top, double baseline)
    {
        byte[] source = Book(prefix + "A"), original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(30, 36, font)
        { FontSize = 10, Margin = 2 }).Pages);
        Assert.Equal("A", page.Text);
        var placement = Assert.Single(page.TextPlacements);
        Assert.Equal(0, placement.Start);
        Assert.Equal(top, placement.Top);
        Assert.Equal(2, placement.Left);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var letter = Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters);
        Assert.Equal("A", letter.Value);
        Assert.Equal(baseline, letter.StartBaseLine.Y);
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("<div>A<br/>A</div>")]
    [InlineData("A<br/>A")]
    [InlineData("<div>A<br></br>A</div>")]
    public void DirectContainerBreakKeepsNativeAndConvertedLinePositions(string body)
    {
        byte[] source = Book(body), original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(30, 36, font)
        { FontSize = 10, Margin = 2 }).Pages);
        Assert.Equal("A\nA", page.Text);
        Assert.Equal(new double[] { 2, 12 }, page.TextPlacements.Select(item => item.Top));
        Assert.All(page.TextPlacements, item => { Assert.Equal(2, item.Left); Assert.Equal(6, item.Width); });
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(new[] { "A", "A" }, letters.Select(item => item.Value));
        Assert.Equal(new double[] { 19.5, 12 }, letters.Select(item => item.StartBaseLine.Y));
        Assert.All(letters, item => Assert.Equal(1.5, item.StartBaseLine.X));
        Assert.Equal(original, source);
    }

    [Fact]
    public void BreakStartsNextLineAtMarginAndPreservesFollowingStyleAndSourceOffset()
    {
        var book = EpubDocument.Open(Book("<p>A<br/><span style=\"color: red\">A</span></p>"));
        Assert.Equal("A\nA", book.ReadText(0));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));

        var layout = book.CreateLayout(new EpubLayoutOptions(30, 36, font) { FontSize = 10, Margin = 2 });

        var page = Assert.Single(layout.Pages);
        Assert.Equal("A\nA", page.Text);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10),
            new EpubTextPlacement(0, 2, 1, 2, 12, 6, 10) { Color = new PdfRgbColor(1, 0, 0) } }, page.TextPlacements);
        var frame = layout.RenderPage(0);
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((5 * 30 + 7) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 0, 0, 1 }, frame.Pixels.Slice((15 * 30 + 7) * 4, 4).ToArray());
    }

    [Fact]
    public void ConsecutiveBreaksReserveBlankLinesInsteadOfCollapsingIntoParagraphSeparators()
    {
        var book = EpubDocument.Open(Book("<p style=\"line-height: 12px\">A<br/><br/><br/>A</p>"));
        Assert.Equal("A\n\n\nA", book.ReadText(0));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));

        var layout = book.CreateLayout(new EpubLayoutOptions(30, 60, font) { FontSize = 10, Margin = 2 });

        var page = Assert.Single(layout.Pages);
        Assert.Equal("A\n\n\nA", page.Text);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 2, 3, 6, 10),
            new EpubTextPlacement(0, 4, 1, 2, 39, 6, 10) }, page.TextPlacements);
        var frame = layout.RenderPage(0);
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((6 * 30 + 7) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((42 * 30 + 7) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((25 * 30 + 7) * 4, 4).ToArray());
    }

    [Fact]
    public void BlankBreaksPaginateWithContiguousSourceSlicesAndRestartedLineOrigins()
    {
        var book = EpubDocument.Open(Book("<p>A<br/><br/><br/>A</p>"));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));

        var layout = book.CreateLayout(new EpubLayoutOptions(30, 24, font) { FontSize = 10, Margin = 2 });

        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(new[] { "A\n\n", "\nA" }, layout.Pages.Select(page => page.Text));
        Assert.Equal(new[] { 0, 3 }, layout.Pages.Select(page => page.TextStart));
        Assert.Equal(book.ReadText(0), string.Concat(layout.Pages.Select(page => page.Text)));
        Assert.Equal(new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10), Assert.Single(layout.Pages[0].TextPlacements));
        Assert.Equal(new EpubTextPlacement(0, 4, 1, 2, 12, 6, 10), Assert.Single(layout.Pages[1].TextPlacements));
        var frame = layout.RenderPage(1);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((5 * 30 + 7) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((15 * 30 + 7) * 4, 4).ToArray());
    }

    [Theory]
    [InlineData("<br/>")]
    [InlineData("<br></br>")]
    public void BothBreakSpellingsWorkWithinAnInheritedInlineStyle(string lineBreak)
    {
        var book = EpubDocument.Open(Book($"<p><span style=\"line-height: 12px\">A{lineBreak}A</span></p>"));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));

        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(30, 36, font) {
            FontSize = 10, Margin = 2 }).Pages);

        Assert.Equal("A\nA", page.Text);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 2, 3, 6, 10),
            new EpubTextPlacement(0, 2, 1, 2, 15, 6, 10) }, page.TextPlacements);
    }

    [Fact]
    public void LiteralNewlinesStillCollapseAsWhitespaceInsteadOfBecomingExplicitBreaks()
    {
        var book = EpubDocument.Open(Book("<p>A\n  A</p>"));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));

        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(30, 36, font) {
            FontSize = 10, Margin = 2 }).Pages);

        Assert.Equal("A A", book.ReadText(0));
        Assert.Equal("A A", page.Text);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10),
            new EpubTextPlacement(0, 1, 1, 8, 2, 5, 10),
            new EpubTextPlacement(0, 2, 1, 13, 2, 6, 10) }, page.TextPlacements);
    }

    [Fact]
    public void AnUnmappedLetterAfterABreakShowsTheMissingGlyphAndIsReported()
    {
        var book = EpubDocument.Open(Book("<p>A<br/>&#937;</p>"));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));

        var layout = book.CreateLayout(new EpubLayoutOptions(16, 36, font) { FontSize = 10, Margin = 2 });

        Assert.Contains("U+03A9", Assert.Single(layout.ContentDiagnostics).Message);
    }

    [Theory]
    // Event handlers are scripting, which the reader refuses for the chapter rather than ignoring silently.
    [InlineData("<p>A<br onclick=\"go()\"/>A</p>", "Scripted EPUB XHTML text is unsupported.")]
    public void BreakSupportKeepsScriptingRefused(string body, string message)
    {
        var book = EpubDocument.Open(Book(body));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));

        var error = Assert.Throws<NotSupportedException>(() => book.CreateLayout(
            new EpubLayoutOptions(16, 36, font) { FontSize = 10, Margin = 2 }));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void BlankLineThatCannotFitMovesToNextPageWithoutLosingItsHeight()
    {
        var book = EpubDocument.Open(Book("<p style=\"line-height: 12px\">A<br/><br/><br/>A</p>"));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));

        var layout = book.CreateLayout(new EpubLayoutOptions(30, 26, font) { FontSize = 10, Margin = 2 });

        // A 22px content box fits one 12px line, including a blank explicit line.
        Assert.Equal(4, layout.Pages.Count);
        Assert.Equal(new[] { "A\n", "\n", "\n", "A" }, layout.Pages.Select(page => page.Text));
        Assert.Equal(new[] { 0, 2, 3, 4 }, layout.Pages.Select(page => page.TextStart));
        Assert.Equal(book.ReadText(0), string.Concat(layout.Pages.Select(page => page.Text)));
        Assert.Equal(new EpubTextPlacement(0, 0, 1, 2, 3, 6, 10), Assert.Single(layout.Pages[0].TextPlacements));
        Assert.Empty(layout.Pages[1].TextPlacements);
        Assert.Empty(layout.Pages[2].TextPlacements);
        Assert.Equal(new EpubTextPlacement(0, 4, 1, 2, 3, 6, 10), Assert.Single(layout.Pages[3].TextPlacements));
        Assert.All(layout.RenderPage(1).Pixels.ToArray(), channel => Assert.Equal(1f, channel));
        Assert.All(layout.RenderPage(2).Pixels.ToArray(), channel => Assert.Equal(1f, channel));
    }

    private static byte[] Book(string body)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            Add("EPUB/book.opf", """
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:explicit-break</dc:identifier><dc:title>Explicit break</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-04T00:00:00Z</meta></metadata>
                  <manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"/><item id="font" href="fonts/test.ttf" media-type="font/ttf"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            Add("EPUB/text.xhtml", $"""
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Explicit break</title></head><body>{body}</body></html>
                """);
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="text.xhtml">Explicit break</a></li></ol></nav></body></html>
                """);
            using var font = zip.CreateEntry("EPUB/fonts/test.ttf", CompressionLevel.NoCompression).Open();
            font.Write(TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true));

            void Add(string path, string content, CompressionLevel compression = CompressionLevel.Optimal)
            {
                using var stream = zip.CreateEntry(path, compression).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return output.ToArray();
    }
}
