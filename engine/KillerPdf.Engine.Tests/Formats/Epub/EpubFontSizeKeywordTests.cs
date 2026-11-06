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

public sealed class EpubFontSizeKeywordTests
{
    [Theory]
    [InlineData("xx-small", "10.8px", 10.8, 8.1)]
    [InlineData("x-small", "13.5px", 13.5, 10.125)]
    [InlineData("small", "16px", 16, 12)]
    [InlineData("MeDiUm", "18px", 18, 13.5)]
    [InlineData("large", "21.6px", 21.6, 16.2)]
    [InlineData("x-large", "27px", 27, 20.25)]
    [InlineData("xx-large", "36px", 36, 27)]
    [InlineData("xxx-large", "54px", 54, 40.5)]
    public void AbsoluteKeywordsUseInitialReaderSizeDespiteAuthorParentSize(
        string keyword, string pixels, double nativeSize, double pdfSize)
    {
        AssertEquivalent("<p style='font-size:24px'><span style='font-size:" + keyword + "'>" + Text + "</span></p>",
            "<p style='font-size:24px'><span style='font-size:" + pixels + "'>" + Text + "</span></p>",
            nativeSize, pdfSize);
    }

    [Theory]
    [InlineData("smaller", 24, "20px", 20, 15)]
    [InlineData("LaRgEr", 20, "24px", 24, 18)]
    public void RelativeKeywordsUseParentComputedSize(string keyword, int parent, string pixels,
        double nativeSize, double pdfSize)
    {
        AssertEquivalent("<p style='font-size:" + parent + "px'><span style='font-size:" + keyword + "'>" + Text + "</span></p>",
            "<p style='font-size:" + parent + "px'><span style='font-size:" + pixels + "'>" + Text + "</span></p>",
            nativeSize, pdfSize);
    }

    [Fact]
    public void AbsoluteKeywordDoesNotCompoundWithAnAncestorKeyword()
        => AssertEquivalent("<p style='font-size:xx-large'><span style='font-size:small'>" + Text + "</span></p>",
            "<p style='font-size:36px'><span style='font-size:16px'>" + Text + "</span></p>", 16, 12);

    [Fact]
    public void NestedRelativeKeywordsFollowEachComputedParent()
        => AssertEquivalent("<p style='font-size:36px'><span style='font-size:smaller'><span style='font-size:smaller'>" + Text + "</span></span></p>",
            "<p style='font-size:36px'><span style='font-size:30px'><span style='font-size:25px'>" + Text + "</span></span></p>", 25, 18.75);

    [Fact]
    public void InlineRelativeKeywordUsesParentInsteadOfLosingStylesheetValue()
        => AssertEquivalent("<p><span style='font-size:smaller'>" + Text + "</span></p>",
            "<p><span style='font-size:20px'>" + Text + "</span></p>", 20, 15,
            "p{font-size:24px}span{font-size:xx-large}");

    [Theory]
    [InlineData("font-size:tiny")]
    [InlineData("line-height:small")]
    [InlineData("line-height:larger")]
    [InlineData("font-size:xxx-large")]
    public void KeywordsRetainUnsupportedPropertyAndComputedHeightGuards(string declaration)
    {
        byte[] source = Book("<p style='" + declaration + "'>A</p>");
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = FontWithSpace();
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(96, 64, font)
            { FontSize = 18, Margin = 8 }));
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)
            { PageWidthPoints = 72, PageHeightPoints = 48, MarginPoints = 6, FontSizePoints = 13.5 }));
        Assert.Equal(original, source);
    }

    [Fact]
    public void KeywordLayoutPreservesPublicCancellation()
    {
        byte[] source = Book("<p style='font-size:small'>A</p>");
        byte[] original = source.ToArray();
        var font = FontWithSpace();
        var book = EpubDocument.Open(source);
        var options = new EpubLayoutOptions(96, 96, font) { FontSize = 18, Margin = 8 };
        var layout = book.CreateLayout(options);
        var canceled = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => EpubDocument.Open(source, canceled));
        Assert.Throws<OperationCanceledException>(() => book.CreateLayout(options, canceled));
        Assert.Throws<OperationCanceledException>(() => layout.RenderPage(0, cancellationToken: canceled));
        Assert.Throws<OperationCanceledException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font), canceled));
        Assert.Equal(original, source);
    }

    private const string Text = "AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA";

    private static void AssertEquivalent(string body, string controlBody, double nativeSize, double pdfSize,
        string? stylesheet = null)
    {
        byte[] source = Book(body, stylesheet);
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);
        var control = EpubDocument.Open(Book(controlBody, stylesheet));
        var font = FontWithSpace();
        Assert.Equal((ushort)1, font.GetGlyphId(' '));
        Assert.Equal((ushort)2, font.GetGlyphId('A'));
        var options = new EpubLayoutOptions(96, 96, font) { FontSize = 18, Margin = 8 };
        var layout = book.CreateLayout(options);
        var controlLayout = control.CreateLayout(options);
        Assert.NotEmpty(layout.Pages);
        Assert.Equal(controlLayout.Pages.Count, layout.Pages.Count);
        Assert.All(layout.Pages.SelectMany(page => page.TextPlacements), placement =>
            Assert.Equal(nativeSize, placement.Height, 8));
        for (int page = 0; page < layout.Pages.Count; page++)
        {
            Assert.Equal(controlLayout.Pages[page].Text, layout.Pages[page].Text);
            Assert.Equal(controlLayout.Pages[page].TextPlacements, layout.Pages[page].TextPlacements);
            var frame = layout.RenderPage(page);
            Assert.Equal(controlLayout.RenderPage(page).Pixels.ToArray(), frame.Pixels.ToArray());
            Assert.Contains(frame.Pixels.ToArray(), channel => channel == 0);
        }
        var conversion = new EpubPdfConversionOptions(font) {
            PageWidthPoints = 72, PageHeightPoints = 72, MarginPoints = 6, FontSizePoints = 13.5 };
        var pdf = PdfDocument.Open(book.ConvertToPdf(conversion));
        var controlPdf = PdfDocument.Open(control.ConvertToPdf(conversion));
        var reader = new PdfPageContentReader(pdf);
        var controlReader = new PdfPageContentReader(controlPdf);
        Assert.Equal(layout.Pages.Count, reader.PageCount);
        Assert.Equal(controlReader.PageCount, reader.PageCount);
        for (int page = 0; page < reader.PageCount; page++)
        {
            var letters = reader.Read(page).Letters;
            Assert.NotEmpty(letters);
            Assert.All(letters, letter => Assert.Equal(pdfSize, letter.FontSize, 6));
            Assert.Equal(controlReader.Read(page).Letters, letters);
            var frame = new PdfPageRenderer(pdf).Render(page, new PdfRenderOptions(96, 96));
            Assert.Empty(frame.Diagnostics);
            Assert.Equal(new PdfPageRenderer(controlPdf).Render(page, new PdfRenderOptions(96, 96)).Pixels.ToArray(),
                frame.Pixels.ToArray());
            Assert.Contains(frame.Pixels.ToArray(), channel => channel == 0);
        }
        Assert.Equal(original, source);
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
