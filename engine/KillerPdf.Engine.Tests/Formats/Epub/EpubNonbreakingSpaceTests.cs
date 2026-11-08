using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubNonbreakingSpaceTests
{
    [Fact]
    public void AutomaticTableColumnsIncludeNonbreakingWordSpacingInTheirIntrinsicWidths()
    {
        var font = TrueTypeFont.Load(FontBytes());
        var book = EpubDocument.Open(Book("<table><tr><td>A\u00A0A</td><td>A</td></tr></table>",
            "body{word-spacing:3px}", paragraph: false));
        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(30, 26, font)
        { FontSize = 10, Margin = 2 }).Pages);
        Assert.Equal(new double[] { 2, 8, 16, 22 }, page.TextPlacements.Select(item => item.Left));
        Assert.Equal(new double[] { 6, 8, 6, 6 }, page.TextPlacements.Select(item => item.Width));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        Assert.Equal(new double[] { 1.5, 6, 12, 16.5 },
            new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.StartBaseLine.X));
    }

    [Fact]
    public void OrdinaryAndRepeatedNonbreakingSpacesSharingOneGlyphKeepDistinctPdfUnicode()
    {
        const string text = "\u00A0A \u00A0\u00A0A\u00A0";
        byte[] source = Book(text), original = source.ToArray();
        var font = TrueTypeFont.Load(FontBytes());
        var book = EpubDocument.Open(source);
        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(48, 26, font)
        { FontSize = 10, Margin = 2 }).Pages);
        Assert.Equal(text, page.Text);
        Assert.Equal(new double[] { 5, 6, 5, 5, 5, 6, 5 }, page.TextPlacements.Select(item => item.Width));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 36, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        Assert.Equal(text, string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value)));
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("font-weight:700", 700, EpubFontStyle.Normal)]
    [InlineData("font-style:italic", 400, EpubFontStyle.Italic)]
    [InlineData("font-style:italic;font-weight:700", 700, EpubFontStyle.Italic)]
    public void BlankFallbackRetainsTheRequestedStyleAndWeight(string declaration, int weight, EpubFontStyle style)
    {
        var normal = TrueTypeFont.Load(FontBytes());
        var styled = TrueTypeFont.Load(FontBytes(spaceWidth: 700));
        var book = EpubDocument.Open(Book("A\u00A0A", "p{margin:0;" + declaration + "}"));
        EpubFontFaceResult Resolve(EpubFontFaceRequest request)
        {
            Assert.Equal(weight, request.Weight);
            Assert.Equal(style, request.Style);
            return new(styled, weight, EpubFontMatch.Exact) { ActualStyle = style };
        }
        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(36, 26, normal)
        { FontSize = 10, Margin = 2, FontFaceResolver = Resolve }).Pages);
        Assert.Equal(new double[] { 6, 7, 6 }, page.TextPlacements.Select(item => item.Width));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(normal)
        { PageWidthPoints = 27, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5,
            FontFaceResolver = Resolve }));
        Assert.Equal(new double[] { 1.5, 6, 11.25 },
            new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.StartBaseLine.X));
    }

    [Fact]
    public void NonbreakingUnitWiderThanTheLineBreaksWhereItOverflows()
    {
        var font = TrueTypeFont.Load(FontBytes());
        var book = EpubDocument.Open(Book("A\u00A0A"));
        // The 16px line holds A and the no-break space; the second A starts the next line (decision 0009).
        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(20, 26, font) { FontSize = 10, Margin = 2 }).Pages);
        Assert.Equal(new double[] { 2, 2 }, page.TextPlacements.Where(item => page.Text[item.Start - page.TextStart] == 'A')
            .Select(item => item.Left));
        Assert.Equal(2, new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 15, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5 }))).Read(0).Letters
            .Count(letter => letter.Value == "A"));
    }

    [Fact]
    public void RealNonbreakingGlyphInLaterFamilyWinsBeforeAnyBlankSpaceFallback()
    {
        var first = TrueTypeFont.Load(FontBytes());
        var second = TrueTypeFont.Load(FontBytes(mapNonbreaking: true, spaceWidth: 700));
        var book = EpubDocument.Open(Book("A\u00A0A", "p{margin:0;font-family:First,Second}"));
        EpubFontFaceResult Resolve(EpubFontFaceRequest request)
            => new(request.Family?.Name == "Second" ? second : first, 400, EpubFontMatch.Exact);
        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(36, 26, first)
        { FontSize = 10, Margin = 2, FontFaceResolver = Resolve }).Pages);
        Assert.Equal(new double[] { 6, 7, 6 }, page.TextPlacements.Select(item => item.Width));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(first)
        { PageWidthPoints = 27, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5,
            FontFaceResolver = Resolve }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(new[] { "A", "\u00A0", "A" }, letters.Select(letter => letter.Value));
        Assert.Equal(new double[] { 1.5, 6, 11.25 }, letters.Select(letter => letter.StartBaseLine.X));
    }

    [Theory]
    [InlineData("A\u00A0A", false, false)]
    [InlineData("A\u00A0A", true, true)]
    [InlineData("A\u00E9A", false, true)]
    public void MissingOrVisibleSpaceDoesNotSupplyUnrelatedOrUnsafeGlyphs(string text, bool visibleSpace, bool mappedSpace)
    {
        var font = TrueTypeFont.Load(FontBytes(visibleSpace: visibleSpace, mappedSpace: mappedSpace));
        byte[] source = Book(text), original = source.ToArray();
        var book = EpubDocument.Open(source);
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(36, 26, font)
        { FontSize = 10, Margin = 2 }));
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)));
        Assert.Equal(original, source);
    }

    [Fact]
    public void WordSpacingCountsNonbreakingSpaceInFollowingWordBeforeWrapping()
    {
        byte[] source = Book("A A\u00A0A", "p{margin:0;word-spacing:3px}"), original = source.ToArray();
        var font = TrueTypeFont.Load(FontBytes());
        var book = EpubDocument.Open(source);
        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(36, 36, font)
        { FontSize = 10, Margin = 2 }).Pages);
        Assert.Equal(new double[] { 2, 2, 8, 16 }, page.TextPlacements.Select(item => item.Left));
        Assert.Equal(new double[] { 2, 12, 12, 12 }, page.TextPlacements.Select(item => item.Top));
        Assert.Equal(new double[] { 6, 6, 8, 6 }, page.TextPlacements.Select(item => item.Width));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 27, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(new[] { "A", "A", "\u00A0", "A" }, letters.Select(letter => letter.Value));
        Assert.Equal(new double[] { 1.5, 1.5, 6, 12 }, letters.Select(letter => letter.StartBaseLine.X));
        Assert.Equal(original, source);
    }

    [Fact]
    public void MissingNonbreakingSpaceUsesBlankSpaceWithoutBreakingOrChangingSourceUnicode()
    {
        byte[] source = Book("A\u00A0A A"), original = source.ToArray();
        byte[] fontBytes = FontBytes(), originalFont = fontBytes.ToArray();
        var font = TrueTypeFont.Load(fontBytes);
        Assert.Equal((ushort)0, font.GetGlyphId(0xA0));
        Assert.Equal((ushort)2, font.GetGlyphId(' '));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(26, 36, font) { FontSize = 10, Margin = 2 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("A\u00A0A A", book.ReadText(0));
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10),
            new EpubTextPlacement(0, 1, 1, 8, 2, 5, 10),
            new EpubTextPlacement(0, 2, 1, 13, 2, 6, 10),
            new EpubTextPlacement(0, 4, 1, 2, 12, 6, 10) }, page.TextPlacements);
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((5 * 26 + 11) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 19.5, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(new[] { "A", "\u00A0", "A", "A" }, letters.Select(letter => letter.Value));
        Assert.Equal(new double[] { 1.5, 6, 9.75, 1.5 }, letters.Select(letter => letter.StartBaseLine.X));
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(26, 36));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, rendered.Pixels.Slice((5 * 26 + 11) * 4, 4).ToArray());
        Assert.Equal(original, source);
        Assert.Equal(originalFont, fontBytes);
    }

    private static byte[] FontBytes(bool mapNonbreaking = false, int spaceWidth = 500,
        bool visibleSpace = false, bool mappedSpace = true)
    {
        byte[] source = TrueTypeFontTests.BuildTestFont(false, includeOutlines: true);
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        int count = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(4));
        for (int index = 0; index < count; index++)
        {
            int record = 12 + index * 16;
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(record + 8));
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(record + 12));
            tables.Add(Encoding.ASCII.GetString(source, record, 4), source.AsSpan(offset, length).ToArray());
        }
        U16(tables["maxp"], 4, 3);
        U16(tables["hhea"], 34, 3);
        tables["hmtx"] = new byte[12];
        U16(tables["hmtx"], 0, 500); U16(tables["hmtx"], 4, 600); U16(tables["hmtx"], 8, spaceWidth);
        tables["loca"] = new byte[16];
        U32(tables["loca"], 8, 24); U32(tables["loca"], 12, 24);
        if (visibleSpace)
        {
            tables["glyf"] = [.. tables["glyf"], .. tables["glyf"]];
            U32(tables["loca"], 12, 48);
        }
        byte[] cmap = new byte[52];
        U16(cmap, 2, 1); U16(cmap, 4, 3); U16(cmap, 6, 1); U32(cmap, 8, 12);
        U16(cmap, 12, 4); U16(cmap, 14, 40); U16(cmap, 18, 6);
        U16(cmap, 20, 4); U16(cmap, 22, 1); U16(cmap, 24, 2);
        U16(cmap, 26, 0x20); U16(cmap, 28, 0x41); U16(cmap, 30, 0xFFFF);
        U16(cmap, 34, 0x20); U16(cmap, 36, 0x41); U16(cmap, 38, 0xFFFF);
        U16(cmap, 40, 0xFFE2); U16(cmap, 42, 0xFFC0); U16(cmap, 44, 1);
        if (!mappedSpace) U16(cmap, 40, 0xFFE0);
        if (mapNonbreaking)
        {
            cmap = new byte[64];
            U16(cmap, 2, 1); U16(cmap, 4, 3); U16(cmap, 6, 10); U32(cmap, 8, 12);
            U16(cmap, 12, 12); U32(cmap, 16, 52); U32(cmap, 24, 3);
            U32(cmap, 28, 0x20); U32(cmap, 32, 0x20); U32(cmap, 36, 2);
            U32(cmap, 40, 0x41); U32(cmap, 44, 0x41); U32(cmap, 48, 1);
            U32(cmap, 52, 0xA0); U32(cmap, 56, 0xA0); U32(cmap, 60, 2);
        }
        tables["cmap"] = cmap;
        int directory = 12 + tables.Count * 16;
        byte[] result = new byte[directory + tables.Values.Sum(value => (value.Length + 3) & ~3)];
        U32(result, 0, 0x00010000); U16(result, 4, tables.Count);
        int entry = 12, position = directory;
        foreach (var (tag, bytes) in tables)
        {
            Encoding.ASCII.GetBytes(tag).CopyTo(result, entry);
            U32(result, entry + 8, position); U32(result, entry + 12, bytes.Length);
            bytes.CopyTo(result, position);
            entry += 16; position += (bytes.Length + 3) & ~3;
        }
        return result;
    }

    private static void U16(byte[] bytes, int offset, int value)
        => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), (ushort)value);
    private static void U32(byte[] bytes, int offset, int value)
        => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), (uint)value);

    private static byte[] Book(string text, string css = "p{margin:0}", bool paragraph = true)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='package.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add("package.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>nbsp</dc:identifier><dc:title>Spacing</dc:title><dc:language>en</dc:language></metadata><manifest><item id='doc' href='doc.xhtml' media-type='application/xhtml+xml'/><item id='css' href='style.css' media-type='text/css'/></manifest><spine><itemref idref='doc'/></spine></package>");
            Add("style.css", css);
            Add("doc.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head><link rel='stylesheet' href='style.css'/></head><body>"
                + (paragraph ? "<p>" + text + "</p>" : text) + "</body></html>");
            void Add(string name, string value)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(value));
            }
        }
        return output.ToArray();
    }
}
