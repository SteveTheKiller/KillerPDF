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

    [Fact]
    public void NativeLayoutPreservesInlineStyleThroughCollapsedSpacesAndParentRestoration()
    {
        var book = EpubDocument.Open(Book("""
            <p style="font-size:20px;line-height:20px">A<span style="font-size:10px;line-height:10px">  A </span>A</p>
            """));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        Assert.Equal("A A A", book.ReadText(0));
        var options = new EpubLayoutOptions(64, 40, font) {
            FontSize = 10, Margin = 8, TextColor = new PdfRgbColor(0.5001, 0.5001, 0.5001) };

        var layout = book.CreateLayout(options);

        var page = Assert.Single(layout.Pages);
        Assert.Equal("A A A", page.Text);
        Assert.Equal(0, page.SpineIndex);
        Assert.Equal(0, page.TextStart);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 8, 8, 12, 20),
            new EpubTextPlacement(0, 1, 1, 20, 16, 5, 10),
            new EpubTextPlacement(0, 2, 1, 25, 16, 6, 10),
            new EpubTextPlacement(0, 3, 1, 31, 16, 5, 10),
            new EpubTextPlacement(0, 4, 1, 36, 8, 12, 20) }, page.TextPlacements);

        var frame = layout.RenderPage(0);

        Assert.Equal(64, frame.Width);
        Assert.Equal(40, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        AssertInterior(frame, 30, 19);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((10 * 64 + 30) * 4, 4).ToArray());
        // Baseline 24 puts the small triangle apex at (30,14), with pixel area 1/4.
        // Coverage 64/255 over encoded white gives independent linear RGB .7379582286993577.
        int apexOffset = (14 * 64 + 30) * 4;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[apexOffset + channel], 0.73795803f, 0.73795843f);
        Assert.Equal(1f, frame.Pixels[apexOffset + 3]);
    }

    [Fact]
    public void InheritedNormalLineHeightUsesEachInlineFontSize()
    {
        var book = EpubDocument.Open(Book("""
            <p style="font-size:20px">A<span style="font-size:10px"> A </span>A</p>
            """));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));

        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(64, 40, font) {
            FontSize = 10, Margin = 8 }).Pages);

        // The synthetic font's ascent/descent are 800/-200 of 1000 units.
        // Normal line height uses those metrics at each size, sharing baseline 24.
        Assert.Equal("A A A", page.Text);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 8, 8, 12, 20),
            new EpubTextPlacement(0, 1, 1, 20, 16, 5, 10),
            new EpubTextPlacement(0, 2, 1, 25, 16, 6, 10),
            new EpubTextPlacement(0, 3, 1, 31, 16, 5, 10),
            new EpubTextPlacement(0, 4, 1, 36, 8, 12, 20) }, page.TextPlacements);
    }

    [Fact]
    public void NativeLayoutAppliesPackagedStylesheetThroughCollapsedSpacesAndParentRestoration()
    {
        const string stylesheet = "p { font-size:20px; line-height:20px } span { font-size:10px; line-height:10px }";
        byte[] stylesheetBytes = Encoding.UTF8.GetBytes(stylesheet);
        var book = EpubDocument.Open(Book("<p>A<span>  A </span>A</p>", stylesheet));
        Assert.Equal(stylesheetBytes, book.ReadResource("EPUB/styles/book.css"));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        Assert.Equal("A A A", book.ReadText(0));
        var options = new EpubLayoutOptions(64, 40, font) {
            FontSize = 10, Margin = 8, TextColor = new PdfRgbColor(0.5001, 0.5001, 0.5001) };

        var layout = book.CreateLayout(options);

        var page = Assert.Single(layout.Pages);
        Assert.Equal("A A A", page.Text);
        Assert.Equal(0, page.SpineIndex);
        Assert.Equal(0, page.TextStart);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 8, 8, 12, 20),
            new EpubTextPlacement(0, 1, 1, 20, 16, 5, 10),
            new EpubTextPlacement(0, 2, 1, 25, 16, 6, 10),
            new EpubTextPlacement(0, 3, 1, 31, 16, 5, 10),
            new EpubTextPlacement(0, 4, 1, 36, 8, 12, 20) }, page.TextPlacements);

        var frame = layout.RenderPage(0);

        Assert.Equal(64, frame.Width);
        Assert.Equal(40, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        AssertInterior(frame, 30, 19);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((10 * 64 + 30) * 4, 4).ToArray());
        int apexOffset = (14 * 64 + 30) * 4;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[apexOffset + channel], 0.73795803f, 0.73795843f);
        Assert.Equal(1f, frame.Pixels[apexOffset + 3]);
        Assert.Equal(stylesheetBytes, book.ReadResource("EPUB/styles/book.css"));
    }

    [Fact]
    public void NativeLayoutAppliesPackagedStylesheetWithCanonicalUtf8CharsetDeclaration()
    {
        const string stylesheet = "@charset \"utf-8\";\np { font-size:20px; line-height:20px } span { font-size:10px; line-height:10px }";
        byte[] stylesheetBytes = Encoding.UTF8.GetBytes(stylesheet);
        var book = EpubDocument.Open(Book("<p>A<span>  A </span>A</p>", stylesheet));
        Assert.Equal(stylesheetBytes, book.ReadResource("EPUB/styles/book.css"));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        Assert.Equal("A A A", book.ReadText(0));
        var options = new EpubLayoutOptions(64, 40, font) {
            FontSize = 10, Margin = 8, TextColor = new PdfRgbColor(0.5001, 0.5001, 0.5001) };

        var layout = book.CreateLayout(options);

        var page = Assert.Single(layout.Pages);
        Assert.Equal("A A A", page.Text);
        Assert.Equal(0, page.SpineIndex);
        Assert.Equal(0, page.TextStart);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 8, 8, 12, 20),
            new EpubTextPlacement(0, 1, 1, 20, 16, 5, 10),
            new EpubTextPlacement(0, 2, 1, 25, 16, 6, 10),
            new EpubTextPlacement(0, 3, 1, 31, 16, 5, 10),
            new EpubTextPlacement(0, 4, 1, 36, 8, 12, 20) }, page.TextPlacements);

        var frame = layout.RenderPage(0);

        Assert.Equal(64, frame.Width);
        Assert.Equal(40, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        AssertInterior(frame, 30, 19);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((10 * 64 + 30) * 4, 4).ToArray());
        int apexOffset = (14 * 64 + 30) * 4;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[apexOffset + channel], 0.73795803f, 0.73795843f);
        Assert.Equal(1f, frame.Pixels[apexOffset + 3]);
        Assert.Equal(stylesheetBytes, book.ReadResource("EPUB/styles/book.css"));
    }

    [Fact]
    public void StylesheetReferenceWorkIsBoundedEvenForEmptySharedResources()
    {
        var allowed = EpubDocument.Open(Book(stylesheet: "", stylesheetReferences: 1024));
        var font = TrueTypeFont.Load(allowed.ReadResource("EPUB/fonts/test.ttf"));
        var options = new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 };
        Assert.Equal("A A", Assert.Single(allowed.CreateLayout(options).Pages).Text);
        var excessive = EpubDocument.Open(Book(stylesheet: "", stylesheetReferences: 1025));
        Assert.Empty(excessive.ReadResource("EPUB/styles/book.css"));
        Assert.Equal("A A", excessive.ReadText(0));

        Assert.Throws<InvalidDataException>(() => excessive.CreateLayout(options));
    }

    [Fact]
    public void RootInheritanceCascadeOrderAndInlineOverrideRetainSourceRanges()
    {
        const string css = "html {font-size:20px;line-height:20px} span {font-size:20px;line-height:20px} span {font-size:10px;line-height:10px}";
        var book = EpubDocument.Open(Book("""
            <p>A<span> A </span><span style="font-size:20px;line-height:20px">A</span></p>
            """, css));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        Assert.Equal("A A A", book.ReadText(0));

        var layout = book.CreateLayout(new EpubLayoutOptions(64, 40, font) {
            FontSize = 10, Margin = 8, TextColor = new PdfRgbColor(0.5001, 0.5001, 0.5001) });

        var page = Assert.Single(layout.Pages);
        Assert.Equal("A A A", page.Text);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 8, 8, 12, 20),
            new EpubTextPlacement(0, 1, 1, 20, 16, 5, 10),
            new EpubTextPlacement(0, 2, 1, 25, 16, 6, 10),
            new EpubTextPlacement(0, 3, 1, 31, 16, 5, 10),
            new EpubTextPlacement(0, 4, 1, 36, 8, 12, 20) }, page.TextPlacements);
        var frame = layout.RenderPage(0);
        AssertInterior(frame, 30, 19);
        AssertInterior(frame, 46, 14);
    }

    [Fact]
    public void NativeLayoutAppliesGroupedCompoundClassSelectorsWithSpecificity()
    {
        const string stylesheet = """
            p, span.small.keep { font-size:20px; line-height:20px }
            span.small.keep { font-size:10px; line-height:10px }
            span { font-size:20px; line-height:20px }
            """;
        byte[] stylesheetBytes = Encoding.UTF8.GetBytes(stylesheet);
        var book = EpubDocument.Open(Book("""
            <p>A<span class="small keep"> A </span><span class="small">A</span></p>
            """, stylesheet));
        Assert.Equal(stylesheetBytes, book.ReadResource("EPUB/styles/book.css"));
        var font = TrueTypeFont.Load(book.ReadResource("EPUB/fonts/test.ttf"));
        Assert.Equal("A A A", book.ReadText(0));
        var options = new EpubLayoutOptions(64, 40, font) {
            FontSize = 10, Margin = 8, TextColor = new PdfRgbColor(0.5001, 0.5001, 0.5001) };

        var layout = book.CreateLayout(options);

        var page = Assert.Single(layout.Pages);
        Assert.Equal("A A A", page.Text);
        Assert.Equal(0, page.SpineIndex);
        Assert.Equal(0, page.TextStart);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 8, 8, 12, 20),
            new EpubTextPlacement(0, 1, 1, 20, 16, 5, 10),
            new EpubTextPlacement(0, 2, 1, 25, 16, 6, 10),
            new EpubTextPlacement(0, 3, 1, 31, 16, 5, 10),
            new EpubTextPlacement(0, 4, 1, 36, 8, 12, 20) }, page.TextPlacements);

        var frame = layout.RenderPage(0);

        Assert.Equal(64, frame.Width);
        Assert.Equal(40, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        AssertInterior(frame, 30, 19);
        AssertInterior(frame, 46, 14);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((10 * 64 + 30) * 4, 4).ToArray());
        int apexOffset = (14 * 64 + 30) * 4;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[apexOffset + channel], 0.73795803f, 0.73795843f);
        Assert.Equal(1f, frame.Pixels[apexOffset + 3]);
        Assert.Equal(stylesheetBytes, book.ReadResource("EPUB/styles/book.css"));
    }

    private static void AssertInterior(PdfRenderedFrame frame, int x, int y)
    {
        int offset = (y * frame.Width + x) * 4;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[offset + channel], 0.21413370f, 0.21413373f);
        Assert.Equal(1f, frame.Pixels[offset + 3]);
    }

    private static byte[] Book(string body = "<p>A A</p>", string? stylesheet = null, int stylesheetReferences = 1)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            string stylesheetManifest = stylesheet is null ? "" : "<item id=\"stylesheet\" href=\"styles/book.css\" media-type=\"text/css\"/>";
            string stylesheetLink = stylesheet is null ? "" : "<link rel=\"stylesheet\" type=\"text/css\" href=\"styles/book.css\"/>";
            stylesheetLink = string.Concat(Enumerable.Repeat(stylesheetLink, stylesheetReferences));
            Add("EPUB/book.opf", $"""
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:native-layout</dc:identifier><dc:title>Native layout</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-03T00:00:00Z</meta></metadata>
                  <manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"/><item id="font" href="fonts/test.ttf" media-type="font/ttf"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>{stylesheetManifest}</manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            Add("EPUB/text.xhtml", $"""
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Native layout</title>{stylesheetLink}</head><body>{body}</body></html>
                """);
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="text.xhtml">Native layout</a></li></ol></nav></body></html>
                """);
            if (stylesheet is not null)
                Add("EPUB/styles/book.css", stylesheet);
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
