using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubNativeLayoutTests
{
    [Fact]
    public void NativeLayoutKeepsLogicalTextGeometrySeparateFromPreciseGlyphInk()
    {
        var book = EpubDocument.Open(Book());
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        Assert.Equal(1000, font.UnitsPerEm);
        Assert.Equal(800, font.Ascender);
        Assert.Equal(-200, font.Descender);
        Assert.Equal((ushort)1, font.GetGlyphId('A'));
        Assert.Equal((ushort)0, font.GetGlyphId(' '));
        Assert.Equal(600, font.GetPdfAdvanceWidth(1));
        Assert.Equal(500, font.GetPdfAdvanceWidth(0));
        Assert.Equal("A A", book.ReadText(0));
        var options = new EpubLayoutOptions(30, 16, font) {
            FontSize = 10, Margin = 2, TextColor = new PdfRgbColor(0.5001, 0.5001, 0.5001) };

        var layout = book.CreateLayout(options);

        var page = Assert.Single(layout.Pages);
        Assert.Equal("A A", page.Text);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10),
            new EpubTextPlacement(0, 1, 1, 8, 2, 5, 10),
            new EpubTextPlacement(0, 2, 1, 13, 2, 6, 10) }, page.TextPlacements);

        var frame = layout.RenderPage(0);

        Assert.Equal(30, frame.Width);
        Assert.Equal(16, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        // Logical top is 2; baseline is 2+800*10/1000=10. Triangle ink reaches top 0.
        AssertInterior(frame, 7, 5);
        AssertInterior(frame, 18, 5);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice(15 * 30 * 4, 4).ToArray());
        // At apex (7,0), pixel[7,8]x[0,1] intersects width y/2, with area 1/4.
        // Coverage 64/255 over encoded white gives independent linear RGB .7379582286993577.
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[7 * 4 + channel], 0.73795803f, 0.73795843f);
        Assert.Equal(1f, frame.Pixels[7 * 4 + 3]);
    }

    private static void AssertInterior(PdfRenderedFrame frame, int x, int y)
    {
        int offset = (y * frame.Width + x) * 4;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[offset + channel], 0.21413370f, 0.21413373f);
        Assert.Equal(1f, frame.Pixels[offset + 3]);
    }

    private static byte[] Book()
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
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:native-layout</dc:identifier><dc:title>Native layout</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-03T00:00:00Z</meta></metadata>
                  <manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"/><item id="font" href="fonts/test.ttf" media-type="font/ttf"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            Add("EPUB/text.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Native layout</title></head><body><p>A A</p></body></html>
                """);
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="text.xhtml">Native layout</a></li></ol></nav></body></html>
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
