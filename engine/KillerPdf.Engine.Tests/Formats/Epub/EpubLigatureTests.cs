using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubLigatureTests
{
    [Fact]
    public void SelectedLigatureOwnsOriginalRangeAndSurvivesNativeAndPdfRendering()
    {
        byte[] source = Book(), original = source.ToArray();
        var font = TrueTypeFont.Load(Font());
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(200, 200, font) { Margin = 8, FontSize = 20 });
        var page = Assert.Single(layout.Pages);
        var placement = Assert.Single(page.TextPlacements);
        Assert.Equal(0, placement.Start);
        Assert.Equal(2, placement.Length);
        Assert.Equal(18, placement.Width, 6);
        Assert.Equal("fi", page.Text);
        Assert.Equal(0, font.GetGlyphId(0xFB01));
        Assert.True(layout.RenderPage(0).Pixels[(15 * 200 + 18) * 4] < .01);
        Assert.Equal(1, layout.RenderPage(0).Pixels[(2 * 200 + 2) * 4]);
        byte[] pdf = book.ConvertToPdf(new EpubPdfConversionOptions(font) { PageWidthPoints = 150, PageHeightPoints = 150, MarginPoints = 6, FontSizePoints = 15 });
        var reopened = PdfDocument.Open(pdf);
        Assert.Equal("fi", string.Concat(new PdfPageContentReader(reopened).Read(0).Letters.Select(letter => letter.Value)));
        var frame = new PdfPageRenderer(reopened).Render(0, new PdfRenderOptions(200, 200));
        Assert.Empty(frame.Diagnostics);
        Assert.True(frame.Pixels.Span[(15 * 200 + 18) * 4 + 2] < 3);
        Assert.Equal(255, frame.Pixels.Span[(2 * 200 + 2) * 4 + 2]);
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("script")]
    [InlineData("feature")]
    [InlineData("unselected")]
    public void UnselectedLigaturesKeepNaturalText(string variant)
    {
        var font = TrueTypeFont.Load(Font(variant));
        var book = EpubDocument.Open(Book());
        var layout = book.CreateLayout(new EpubLayoutOptions(200, 200, font) { Margin = 8, FontSize = 20 });
        Assert.Equal(2, layout.Pages.Single().TextPlacements.Count);
        Assert.All(layout.Pages.Single().TextPlacements, p => Assert.Equal(12, p.Width));
        Assert.Equal(1, layout.RenderPage(0).Pixels[(15 * 200 + 18) * 4]);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)));
        Assert.Equal("fi", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value)));
    }

    [Theory]
    [InlineData("<p>f<span style='color:#ff0000'>i</span></p>")]
    [InlineData("<p>f<br/>i</p>")]
    [InlineData("<p>f</p><p>i</p>")]
    public void StyleBlockAndBreakBoundariesKeepComponentsSeparate(string body)
    {
        var font = TrueTypeFont.Load(Font());
        var layout = EpubDocument.Open(Book(body)).CreateLayout(
            new EpubLayoutOptions(200, 200, font) { Margin = 8, FontSize = 20 });
        var placements = layout.Pages.SelectMany(page => page.TextPlacements).ToArray();
        Assert.Equal(2, placements.Length);
        Assert.All(placements, p => { Assert.Equal(1, p.Length); Assert.Equal(12, p.Width); });
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("offset")]
    [InlineData("glyph")]
    [InlineData("work")]
    public void InvalidSubstitutionDataHasBoundedDiagnosis(string variant)
    {
        var font = TrueTypeFont.Load(Font(variant));
        var book = EpubDocument.Open(Book());
        var error = Assert.Throws<InvalidDataException>(() => book.CreateLayout(new EpubLayoutOptions(200, 200, font)));
        Assert.Contains("GSUB", error.Message);
        if (variant == "work") Assert.Contains("work limit", error.Message);
    }

    [Fact]
    public void UnsupportedLookupFlagsAreDiagnosedAndCancellationIsHonored()
    {
        var font = TrueTypeFont.Load(Font("flags"));
        var book = EpubDocument.Open(Book());
        var options = new EpubLayoutOptions(200, 200, font);
        Assert.Contains("flags", Assert.Throws<NotSupportedException>(() => book.CreateLayout(options)).Message);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => book.CreateLayout(options, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font), cancellation.Token));
    }

    [Fact]
    public void WordLookaheadUsesSelectedAdvancesAndPdfSubsetRetainsNonCmapLigature()
    {
        var font = TrueTypeFont.Load(Font("space"));
        var book = EpubDocument.Open(Book("<p>fi fi</p>"));
        var layout = book.CreateLayout(new EpubLayoutOptions(63, 200, font) { Margin = 8, FontSize = 20 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("fi fi", page.Text);
        Assert.Equal(new[] { 2, 1, 2 }, page.TextPlacements.Select(p => p.Length));
        Assert.Equal(new[] { 18d, 10d, 18d }, page.TextPlacements.Select(p => p.Width));
        Assert.All(page.TextPlacements, p => Assert.Equal(8, p.Top));
        var document = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 47.25, PageHeightPoints = 150, MarginPoints = 6, FontSizePoints = 15 }));
        Assert.Equal("fi fi", string.Concat(new PdfPageContentReader(document).Read(0).Letters.Select(letter => letter.Value)));
        PdfDictionary catalog = Dictionary(document.Trailer[Name("Root")]);
        PdfDictionary pages = Dictionary(catalog[Name("Pages")]);
        PdfDictionary pdfPage = Dictionary(Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary fonts = Dictionary(Dictionary(pdfPage[Name("Resources")])[Name("Font")]);
        PdfDictionary type0 = Dictionary(fonts[Name("F1")]);
        PdfDictionary descendant = Dictionary(Assert.IsType<PdfArray>(type0[Name("DescendantFonts")])[0]);
        PdfDictionary descriptor = Dictionary(descendant[Name("FontDescriptor")]);
        var fontFile = Assert.IsType<PdfStream>(document.Resolve(Assert.IsType<PdfIndirectReference>(descriptor[Name("FontFile2")])));
        var subset = TrueTypeFont.Load(PdfStreamDecoder.Decode(fontFile));
        Assert.Equal(0, subset.GetGlyphId(0xFB01));
        Assert.Equal(900, subset.GetPdfAdvanceWidth(3));
        Assert.NotNull(PdfFontResourceReader.Read(document, type0).GetGlyphOutline(1));
        var frame = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(63, 200));
        Assert.True(frame.Pixels.Span[(15 * 63 + 18) * 4 + 2] < 3);
        Assert.True(frame.Pixels.Span[(15 * 63 + 46) * 4 + 2] < 3);

        PdfDictionary Dictionary(PdfObject value) => Assert.IsType<PdfDictionary>(
            value is PdfIndirectReference reference ? document.Resolve(reference) : value);
        static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    }

    [Fact]
    public void UnsupportedGsubVersionCannotSilentlyIgnoreVariationSelection()
    {
        var font = TrueTypeFont.Load(Font("version"));
        var book = EpubDocument.Open(Book());
        Assert.Contains("version", Assert.Throws<NotSupportedException>(() =>
            book.CreateLayout(new EpubLayoutOptions(200, 200, font))).Message);
    }

    [Fact]
    public void NaturalConversionKeepsUnicodeMappingsForReadersIgnoringActualText()
    {
        var font = TrueTypeFont.Load(Font("absent"));
        var book = EpubDocument.Open(Book());
        var document = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)));
        PdfDictionary catalog = Dictionary(document.Trailer[Name("Root")]);
        PdfDictionary pages = Dictionary(catalog[Name("Pages")]);
        PdfDictionary page = Dictionary(Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary fonts = Dictionary(Dictionary(page[Name("Resources")])[Name("Font")]);
        PdfDictionary type0 = Dictionary(fonts[Name("F1")]);
        var map = Assert.IsType<PdfStream>(document.Resolve(Assert.IsType<PdfIndirectReference>(type0[Name("ToUnicode")])));
        string unicode = Encoding.ASCII.GetString(PdfStreamDecoder.Decode(map));
        Assert.Contains("<0066>", unicode);
        Assert.Contains("<0069>", unicode);
        Assert.Equal("fi", string.Concat(PdfFontResourceReader.Read(document, type0)
            .Decode(new byte[] { 0, 1, 0, 2 }).Select(character => character.Text)));

        PdfDictionary Dictionary(PdfObject value) => Assert.IsType<PdfDictionary>(
            value is PdfIndirectReference reference ? document.Resolve(reference) : value);
        static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    }

    [Fact]
    public void GreekTextDoesNotActivateUnsupportedLatinLookup()
    {
        var font = TrueTypeFont.Load(Font("greek-flags"));
        var book = EpubDocument.Open(Book("<p>\u03B1\u03B2</p>"));
        var layout = book.CreateLayout(new EpubLayoutOptions(200, 200, font) { FontSize = 20, Margin = 8 });
        Assert.Equal("\u03B1\u03B2", layout.Pages.Single().Text);
        Assert.Equal(2, layout.Pages.Single().TextPlacements.Count);
        Assert.All(layout.Pages.Single().TextPlacements, p => Assert.Equal(12, p.Width));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)));
        Assert.Equal("\u03B1\u03B2", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value)));
    }

    [Fact]
    public void UnsupportedRequiredFeatureIsDiagnosed()
    {
        var font = TrueTypeFont.Load(Font("required"));
        var book = EpubDocument.Open(Book());
        Assert.Contains("required", Assert.Throws<NotSupportedException>(() =>
            book.CreateLayout(new EpubLayoutOptions(200, 200, font))).Message);
    }

static byte[] Book(string body = "<p>fi</p>")
{
    using var output = new MemoryStream();
    using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
    {
        Add("mimetype", "application/epub+zip");
        Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='book.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
        Add("book.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>urn:ligature</dc:identifier><dc:title>Ligature</dc:title><dc:language>en</dc:language></metadata><manifest><item id='c' href='text.xhtml' media-type='application/xhtml+xml'/></manifest><spine><itemref idref='c'/></spine></package>");
        Add("text.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><body>" + body + "</body></html>");
        void Add(string path, string value)
        {
            var entry = zip.CreateEntry(path, path == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
            using var stream = entry.Open(); stream.Write(Encoding.UTF8.GetBytes(value));
        }
    }
    return output.ToArray();
}

static byte[] Font(string variant = "selected")
{
    var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
    byte[] head = new byte[54]; U16(head, 18, 1000); U16(head, 40, 900); U16(head, 42, 1000); U16(head, 50, 1); tables["head"] = head;
    byte[] hhea = new byte[36]; U16(hhea, 4, 800); BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(6), -200); U16(hhea, 34, 4); tables["hhea"] = hhea;
    byte[] maxp = new byte[6]; U16(maxp, 4, 4); tables["maxp"] = maxp;
    byte[] hmtx = new byte[16]; U16(hmtx, 0, 500); U16(hmtx, 4, 600); U16(hmtx, 8, 600); U16(hmtx, 12, 900); tables["hmtx"] = hmtx;
    byte[] cmap = new byte[52]; U16(cmap, 2, 1); U16(cmap, 4, 3); U16(cmap, 6, 10); U32(cmap, 8, 12);
    U16(cmap, 12, 12); U32(cmap, 16, 40); U32(cmap, 24, 2);
    U32(cmap, 28, 'f'); U32(cmap, 32, 'f'); U32(cmap, 36, 1);
    U32(cmap, 40, 'i'); U32(cmap, 44, 'i'); U32(cmap, 48, 2); tables["cmap"] = cmap;
    if (variant == "greek-flags")
    {
        U32(cmap, 28, 0x03B1); U32(cmap, 32, 0x03B1);
        U32(cmap, 40, 0x03B2); U32(cmap, 44, 0x03B2);
    }
    byte[] nameText = Encoding.BigEndianUnicode.GetBytes("KillerLigature"); byte[] name = new byte[18 + nameText.Length];
    U16(name, 2, 1); U16(name, 4, 18); U16(name, 6, 3); U16(name, 8, 1); U16(name, 10, 0x409); U16(name, 12, 6); U16(name, 14, nameText.Length); nameText.CopyTo(name, 18); tables["name"] = name;
    byte[] glyf = new byte[72], loca = new byte[20];
    for (int g = 1; g <= 3; g++)
    {
        int at = (g - 1) * 24, width = g == 1 ? 300 : g == 2 ? 100 : 900;
        U16(glyf, at, 1); U16(glyf, at + 6, width); U16(glyf, at + 8, 1000); U16(glyf, at + 10, 2);
        glyf[at + 14] = 0x31; glyf[at + 15] = 0x21; glyf[at + 16] = 0x01;
        U16(glyf, at + 17, width); BinaryPrimitives.WriteInt16BigEndian(glyf.AsSpan(at + 19), (short)(-width / 2)); U16(glyf, at + 21, 1000);
        U32(loca, (g + 1) * 4, g * 24);
    }
    tables["glyf"] = glyf; tables["loca"] = loca;
    if (variant == "space")
    {
        U16(maxp, 4, 5); U16(hhea, 34, 5);
        byte[] metrics = new byte[20]; hmtx.CopyTo(metrics, 0); U16(metrics, 16, 500); tables["hmtx"] = metrics;
        byte[] locations = new byte[24]; loca.CopyTo(locations, 0); U32(locations, 20, 72); tables["loca"] = locations;
        byte[] mapping = new byte[64]; cmap.AsSpan(0, 28).CopyTo(mapping); U32(mapping, 16, 52); U32(mapping, 24, 3);
        U32(mapping, 28, ' '); U32(mapping, 32, ' '); U32(mapping, 36, 4);
        cmap.AsSpan(28, 24).CopyTo(mapping.AsSpan(40)); tables["cmap"] = mapping;
    }
    byte[] gsub = new byte[80]; U32(gsub, 0, 0x10000); U16(gsub, 4, 10); U16(gsub, 6, 30); U16(gsub, 8, 44);
    U16(gsub, 10, 1); Encoding.ASCII.GetBytes("latn").CopyTo(gsub, 12); U16(gsub, 16, 8);
    U16(gsub, 18, 4); U16(gsub, 24, 0xFFFF); U16(gsub, 26, 1);
    U16(gsub, 30, 1); Encoding.ASCII.GetBytes("liga").CopyTo(gsub, 32); U16(gsub, 36, 8); U16(gsub, 40, 1);
    U16(gsub, 44, 1); U16(gsub, 46, 4); U16(gsub, 48, 4); U16(gsub, 52, 1); U16(gsub, 54, 8);
    U16(gsub, 56, 1); U16(gsub, 58, 8); U16(gsub, 60, 1); U16(gsub, 62, 14);
    U16(gsub, 64, 1); U16(gsub, 66, 1); U16(gsub, 68, 1);
    U16(gsub, 70, 1); U16(gsub, 72, 4); U16(gsub, 74, 3); U16(gsub, 76, 2); U16(gsub, 78, 2); tables["GSUB"] = gsub;
    if (variant == "absent") tables.Remove("GSUB");
    if (variant == "script") Encoding.ASCII.GetBytes("arab").CopyTo(gsub, 12);
    if (variant == "feature") Encoding.ASCII.GetBytes("dlig").CopyTo(gsub, 32);
    if (variant == "unselected") U16(gsub, 26, 0);
    if (variant == "required") { U16(gsub, 24, 0); Encoding.ASCII.GetBytes("rlig").CopyTo(gsub, 32); }
    if (variant == "truncated") tables["GSUB"] = gsub[..79];
    if (variant == "offset") U16(gsub, 58, 65535);
    if (variant == "glyph") U16(gsub, 74, 4);
    if (variant == "flags") U16(gsub, 50, 8);
    if (variant == "greek-flags") U16(gsub, 50, 8);
    if (variant == "version") U16(gsub, 2, 1);
    if (variant == "work")
    {
        gsub = new byte[360012]; U32(gsub, 0, 0x10000); U16(gsub, 4, 10); U16(gsub, 6, 30); U16(gsub, 8, 44);
        U16(gsub, 10, 60000); tables["GSUB"] = gsub;
    }
    int directory = 12 + tables.Count * 16, total = directory + tables.Values.Sum(value => (value.Length + 3) & ~3);
    byte[] result = new byte[total]; U32(result, 0, 0x10000); U16(result, 4, tables.Count);
    int record = 12, offset = directory;
    foreach (var pair in tables)
    {
        Encoding.ASCII.GetBytes(pair.Key).CopyTo(result, record); U32(result, record + 8, offset); U32(result, record + 12, pair.Value.Length);
        pair.Value.CopyTo(result, offset); offset += (pair.Value.Length + 3) & ~3; record += 16;
    }
    return result;
}
static void U16(byte[] bytes, int at, int value) => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(at), checked((ushort)value));
static void U32(byte[] bytes, int at, int value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), checked((uint)value));


}

