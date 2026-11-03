using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubWordWrapLayoutTests
{
    [Fact]
    public void SpaceBeforeAWholeWordWrapsWithoutChangingItsSourceRange()
    {
        var book = EpubDocument.Open(Book());
        Assert.Equal("A A", book.ReadText(0));
        Assert.Equal(3, book.ReadText(0).Length);
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        var options = new EpubLayoutOptions(16, 26, font) {
            FontSize = 10, Margin = 2, TextColor = new PdfRgbColor(0.5001, 0.5001, 0.5001) };

        var layout = book.CreateLayout(options);

        var page = Assert.Single(layout.Pages);
        Assert.Equal("A A", page.Text);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10),
            new EpubTextPlacement(0, 2, 1, 2, 12, 6, 10) }, page.TextPlacements);

        var frame = layout.RenderPage(0);

        Assert.Equal(16, frame.Width);
        Assert.Equal(26, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        // The 12-point content width fits either six-point word, but not 6+5+6.
        // The separator remains source index1, without placement, before the word at index2.
        // Logical tops2/12 give baselines10/20 and triangle ink tops0/10.
        AssertInterior(frame, 7, 5);
        AssertInterior(frame, 7, 15);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((5 * 16 + 14) * 4, 4).ToArray());
        for (int alpha = 3; alpha < frame.Pixels.Length; alpha += 4)
            Assert.Equal(1f, frame.Pixels[alpha]);
    }

    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    public void FittingWordsKeepTheirSeparatorPlacement(double pageWidth)
    {
        var book = EpubDocument.Open(Book());
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));

        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(pageWidth, 16, font) {
            FontSize = 10, Margin = 2 }).Pages);

        Assert.Equal("A A", page.Text);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10),
            new EpubTextPlacement(0, 1, 1, 8, 2, 5, 10),
            new EpubTextPlacement(0, 2, 1, 13, 2, 6, 10) }, page.TextPlacements);
    }

    [Theory]
    [InlineData("AAA")]
    [InlineData("A AAA")]
    public void DiagnosesWordsThatCannotFitWithoutCharacterBreaking(string text)
    {
        var book = EpubDocument.Open(Book(text));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));

        var error = Assert.Throws<NotSupportedException>(() => book.CreateLayout(
            new EpubLayoutOptions(16, 26, font) { FontSize = 10, Margin = 2 }));

        Assert.Equal("The native EPUB word exceeds the available line width.", error.Message);
    }

    [Fact]
    public void DiagnosesAWrappedLineThatExceedsThePageHeight()
    {
        var book = EpubDocument.Open(Book());
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));

        var error = Assert.Throws<NotSupportedException>(() => book.CreateLayout(
            new EpubLayoutOptions(16, 23, font) { FontSize = 10, Margin = 2 }));

        Assert.Equal("Native EPUB pagination is not implemented.", error.Message);
    }

    private static void AssertInterior(PdfRenderedFrame frame, int x, int y)
    {
        int offset = (y * frame.Width + x) * 4;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[offset + channel], 0.21413370f, 0.21413373f);
        Assert.Equal(1f, frame.Pixels[offset + 3]);
    }

    private static byte[] Book(string text = "A A")
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
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:word-wrap</dc:identifier><dc:title>Word wrap</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-03T00:00:00Z</meta></metadata>
                  <manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"/><item id="font" href="fonts/test.ttf" media-type="font/ttf"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            Add("EPUB/text.xhtml", $"""
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Word wrap</title></head><body><p>{text}</p></body></html>
                """);
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="text.xhtml">Word wrap</a></li></ol></nav></body></html>
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
