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

public sealed class EpubGsubExtensionTests
{
    [Theory]
    [InlineData(true, 12d)]
    [InlineData(false, 18d)]
    public void SingleComponentSubstitutionPreservesTextAndUsesTheSelectedGlyph(bool identity, double advance)
    {
        byte[] source = Book("<p>ff</p>"), original = source.ToArray();
        var font = TrueTypeFont.Load(Font(identity ? "single-identity" : "single-replacement"));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(63, 200, font) { Margin = 8, FontSize = 20 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("ff", page.Text);
        Assert.Equal(new[] { 0, 1 }, page.TextPlacements.Select(p => p.Start));
        Assert.All(page.TextPlacements, p => Assert.Equal(1, p.Length));
        Assert.All(page.TextPlacements, p => Assert.Equal(advance, p.Width));
        Assert.Equal(new[] { 8d, 8d + advance }, page.TextPlacements.Select(p => p.Left));
        var native = layout.RenderPage(0);
        Assert.Equal(identity, native.Pixels[(15 * 63 + 18) * 4] > .99);
        var document = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 47.25, PageHeightPoints = 150, MarginPoints = 6, FontSizePoints = 15 }));
        Assert.Equal("ff", string.Concat(new PdfPageContentReader(document).Read(0).Letters.Select(letter => letter.Value)));
        var frame = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(63, 200));
        Assert.Empty(frame.Diagnostics);
        Assert.Equal(identity, frame.Pixels.Span[(15 * 63 + 18) * 4 + 2] > 252);
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtensionLigaturePreservesSourceRangesAdvancesWrappingAndNativeAndPdfInk(bool rangeCoverage)
    {
        byte[] source = Book("<p>fi fi</p>"), original = source.ToArray();
        var font = TrueTypeFont.Load(Font(rangeCoverage ? "coverage2" : "space"));
        Assert.Equal((ushort)1, font.GetGlyphId('f'));
        Assert.Equal((ushort)2, font.GetGlyphId('i'));
        Assert.Equal((ushort)0, font.GetGlyphId(0xFB01));
        Assert.Equal(900, font.GetPdfAdvanceWidth(3));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(63, 200, font) { Margin = 8, FontSize = 20 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("fi fi", page.Text);
        Assert.Equal(new[] { 0, 2, 3 }, page.TextPlacements.Select(p => p.Start));
        Assert.Equal(new[] { 2, 1, 2 }, page.TextPlacements.Select(p => p.Length));
        Assert.Equal(new[] { 18d, 10d, 18d }, page.TextPlacements.Select(p => p.Width));
        Assert.All(page.TextPlacements, p => Assert.Equal(8, p.Top));
        var native = layout.RenderPage(0);
        Assert.True(native.Pixels[(15 * 63 + 18) * 4] < .01);
        Assert.True(native.Pixels[(15 * 63 + 46) * 4] < .01);
        Assert.Equal(1, native.Pixels[(2 * 63 + 2) * 4]);
        var document = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 47.25, PageHeightPoints = 150, MarginPoints = 6, FontSizePoints = 15 }));
        Assert.Equal("fi fi", string.Concat(new PdfPageContentReader(document).Read(0).Letters.Select(letter => letter.Value)));
        var frame = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(63, 200));
        Assert.Empty(frame.Diagnostics);
        Assert.True(frame.Pixels.Span[(15 * 63 + 18) * 4 + 2] < 3);
        Assert.True(frame.Pixels.Span[(15 * 63 + 46) * 4 + 2] < 3);
        Assert.Equal(255, frame.Pixels.Span[(2 * 63 + 2) * 4 + 2]);
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("inside")]
    [InlineData("outside")]
    [InlineData("overflow")]
    [InlineData("truncated-extension")]
    [InlineData("range-reversed")]
    [InlineData("range-index")]
    [InlineData("range-overflow")]
    [InlineData("range-missing")]
    [InlineData("range-unsorted")]
    [InlineData("range-truncated")]
    [InlineData("components-zero")]
    [InlineData("components-overflow")]
    public void MalformedSelectedExtensionIsDiagnosedByLayoutAndConversion(string variant)
    {
        var font = TrueTypeFont.Load(Font(variant));
        var book = EpubDocument.Open(Book());
        Assert.Contains("GSUB", Assert.Throws<InvalidDataException>(() =>
            book.CreateLayout(new EpubLayoutOptions(200, 200, font))).Message);
        Assert.Contains("GSUB", Assert.Throws<InvalidDataException>(() =>
            book.ConvertToPdf(new EpubPdfConversionOptions(font))).Message);
    }

    [Theory]
    [InlineData("extension-format")]
    [InlineData("nested")]
    [InlineData("other-type")]
    [InlineData("extension-flags")]
    [InlineData("coverage-format")]
    public void UnsupportedSelectedExtensionStillHasAnExplicitDiagnosis(string variant)
    {
        var font = TrueTypeFont.Load(Font(variant));
        var book = EpubDocument.Open(Book());
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(200, 200, font)));
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)));
    }

    [Fact]
    public void ExtensionLayoutAndConversionHonorCancellation()
    {
        var font = TrueTypeFont.Load(Font("coverage2"));
        var book = EpubDocument.Open(Book());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            book.CreateLayout(new EpubLayoutOptions(200, 200, font), cancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            book.ConvertToPdf(new EpubPdfConversionOptions(font), cancellation.Token));
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

static byte[] Font(string variant = "space")
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
    if (variant is "space" or "coverage2")
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
    // ExtensionSubst format 1 uses a 32-bit offset from its own start.
    byte[] extension = new byte[gsub.Length + 8];
    gsub.AsSpan(0, 56).CopyTo(extension);
    U16(extension, 48, 7); U16(extension, 56, 1); U16(extension, 58, 4); U32(extension, 60, 8);
    gsub.AsSpan(56).CopyTo(extension.AsSpan(64)); tables["GSUB"] = extension;
    if (variant == "coverage2" || variant.StartsWith("range-", StringComparison.Ordinal))
    {
        byte[] ranges = new byte[extension.Length + 4];
        extension.AsSpan(0, 72).CopyTo(ranges);
        U16(ranges, 70, 18);
        U16(ranges, 72, 2); U16(ranges, 74, 1);
        U16(ranges, 76, 1); U16(ranges, 78, 1); U16(ranges, 80, 0);
        extension.AsSpan(78).CopyTo(ranges.AsSpan(82));
        if (variant == "range-reversed") U16(ranges, 76, 2);
        if (variant == "range-index") U16(ranges, 80, 1);
        if (variant == "range-overflow") U16(ranges, 78, 3);
        if (variant == "range-missing") U16(ranges, 74, 0);
        if (variant == "range-unsorted")
        {
            U16(ranges, 68, 2); U16(ranges, 74, 2);
            U16(ranges, 82, 1); U16(ranges, 84, 1); U16(ranges, 86, 1);
        }
        tables["GSUB"] = variant == "range-truncated" ? ranges[..81] : ranges;
    }
    if (variant == "zero") U32(extension, 60, 0);
    if (variant == "inside") U32(extension, 60, 4);
    if (variant == "outside") U32(extension, 60, 1000);
    if (variant == "overflow") BinaryPrimitives.WriteUInt32BigEndian(extension.AsSpan(60), uint.MaxValue);
    if (variant == "truncated-extension") tables["GSUB"] = extension[..63];
    if (variant == "extension-format") U16(extension, 56, 2);
    if (variant == "nested") U16(extension, 58, 7);
    if (variant == "other-type") U16(extension, 58, 1);
    if (variant == "extension-flags") U16(extension, 50, 8);
    if (variant == "coverage-format") U16(extension, 72, 3);
    if (variant == "components-zero") U16(extension, 84, 0);
    if (variant == "components-overflow") U16(extension, 84, 257);
    if (variant is "single-identity" or "single-replacement")
    {
        U16(extension, 82, variant == "single-identity" ? 1 : 3);
        U16(extension, 84, 1);
        tables["GSUB"] = extension[..86];
    }
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
