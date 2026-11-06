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

public sealed class EpubSmallCapsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtensionSubtablesMustUseOneConsistentLookupType(bool mixedTypes)
    {
        var font = Font(variant: mixedTypes ? "mixed-extension" : "extension-pair");
        var book = EpubDocument.Open(Book("<p style='font-variant:small-caps'>Aa</p>"));
        var options = new EpubLayoutOptions(96, 96, font) { Margin = 8, FontSize = 20 };
        if (mixedTypes)
        {
            Assert.Throws<InvalidDataException>(() => book.CreateLayout(options));
            Assert.Throws<InvalidDataException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)));
            return;
        }
        var layout = book.CreateLayout(options);
        Assert.Equal(new[] { 20d, 8d }, Assert.Single(layout.Pages).TextPlacements.Select(p => p.Width));
        Assert.Contains(layout.RenderPage(0).Pixels.ToArray(), value => value == 0);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)));
        Assert.Equal("Aa", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(p => p.Value)));
    }

    [Fact]
    public void LaterLigatureCanConsumePartOfAnExpansionAndTheFollowingSourceCharacter()
    {
        var font = Font(variant: "multiple-follow-ligature");
        var book = EpubDocument.Open(Book("<p style='font-variant:small-caps'>AaA</p>"));
        var layout = book.CreateLayout(new EpubLayoutOptions(96, 96, font) { Margin = 8, FontSize = 20 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("AaA", page.Text);
        Assert.Equal(new[] { 0, 1 }, page.TextPlacements.Select(p => p.Start));
        Assert.Equal(new[] { 1, 2 }, page.TextPlacements.Select(p => p.Length));
        Assert.Equal(new[] { 20d, 16d }, page.TextPlacements.Select(p => p.Width));
        var native = layout.RenderPage(0);
        Assert.True(native.Pixels[(16 * 96 + 32) * 4] < .01);
        Assert.True(native.Pixels[(16 * 96 + 40) * 4] < .01);
        Assert.True(native.Pixels[(8 * 96 + 48) * 4] > .99);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 72, PageHeightPoints = 72, MarginPoints = 6, FontSizePoints = 15 }));
        Assert.Equal("AaA", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(p => p.Value)));
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(96, 96));
        Assert.Empty(frame.Diagnostics);
        Assert.True(frame.Pixels.Span[(16 * 96 + 32) * 4 + 2] < 3);
        Assert.True(frame.Pixels.Span[(16 * 96 + 40) * 4 + 2] < 3);
        Assert.True(frame.Pixels.Span[(8 * 96 + 48) * 4 + 2] > 252);
    }

    [Theory]
    [InlineData("multiple-empty")]
    [InlineData("multiple-overflow")]
    [InlineData("multiple-output-outside")]
    [InlineData("multiple-zero-offset")]
    [InlineData("multiple-coverage-count")]
    [InlineData("multiple-truncated")]
    public void InvalidMultipleSequencesAreRejectedWithoutChangingTheSource(string variant)
    {
        byte[] source = Book("<p style='font-variant:small-caps'>AaA</p>"), original = source.ToArray();
        var font = Font(variant: variant);
        var book = EpubDocument.Open(source);
        Assert.Throws<InvalidDataException>(() => book.CreateLayout(new EpubLayoutOptions(96, 96, font)));
        Assert.Throws<InvalidDataException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)));
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultipleSubstitutionDrawsEveryGlyphAndPreservesOneOriginalSourceRange(bool extension)
    {
        byte[] source = Book("<p style='font-variant:small-caps'>AaA</p>"), original = source.ToArray();
        var font = Font(variant: "multiple", extension: extension);
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(96, 96, font) { Margin = 8, FontSize = 20 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("AaA", page.Text);
        Assert.Equal(new[] { 0, 1, 2 }, page.TextPlacements.Select(p => p.Start));
        Assert.All(page.TextPlacements, p => Assert.Equal(1, p.Length));
        Assert.Equal(new[] { 20d, 28d, 20d }, page.TextPlacements.Select(p => p.Width));
        Assert.Equal(new[] { 8d, 28d, 56d }, page.TextPlacements.Select(p => p.Left));
        Assert.All(page.TextPlacements, p => Assert.Equal(20, p.Height));
        var native = layout.RenderPage(0);
        Assert.True(native.Pixels[(16 * 96 + 32) * 4] < .01);
        Assert.True(native.Pixels[(8 * 96 + 48) * 4] < .01);
        Assert.True(native.Pixels[(8 * 96 + 68) * 4] < .01);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 72, PageHeightPoints = 72, MarginPoints = 6, FontSizePoints = 15 }));
        Assert.Equal("AaA", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(p => p.Value)));
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(96, 96));
        Assert.Empty(frame.Diagnostics);
        Assert.True(frame.Pixels.Span[(16 * 96 + 32) * 4 + 2] < 3);
        Assert.True(frame.Pixels.Span[(8 * 96 + 48) * 4 + 2] < 3);
        Assert.True(frame.Pixels.Span[(8 * 96 + 68) * 4 + 2] < 3);
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FontProvidedSmallCapsPreserveOriginalTextAndUseSelectedMetricsAndInk(bool deltaMapping, bool extension)
    {
        byte[] source = Book("<p style='font-variant:small-caps'>Aa</p>"), original = source.ToArray();
        var font = Font(deltaMapping, extension: extension);
        Assert.Equal((ushort)1, font.GetGlyphId('A'));
        Assert.Equal((ushort)2, font.GetGlyphId('a'));
        Assert.Equal(400, font.GetPdfAdvanceWidth(3));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(80, 96, font) { Margin = 8, FontSize = 20 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("Aa", page.Text);
        Assert.Equal(new[] { 0, 1 }, page.TextPlacements.Select(p => p.Start));
        Assert.All(page.TextPlacements, p => Assert.Equal(1, p.Length));
        Assert.Equal(new[] { 20d, 8d }, page.TextPlacements.Select(p => p.Width));
        Assert.Equal(new[] { 8d, 28d }, page.TextPlacements.Select(p => p.Left));
        Assert.All(page.TextPlacements, p => Assert.Equal(20, p.Height));
        var native = layout.RenderPage(0);
        Assert.True(native.Pixels[(16 * 80 + 32) * 4] < .01);
        Assert.True(native.Pixels[(8 * 80 + 38) * 4] > .99);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 60, PageHeightPoints = 72, MarginPoints = 6, FontSizePoints = 15 }));
        Assert.Equal("Aa", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(p => p.Value)));
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(80, 96));
        Assert.Empty(frame.Diagnostics);
        Assert.True(frame.Pixels.Span[(16 * 80 + 32) * 4 + 2] < 3);
        Assert.True(frame.Pixels.Span[(8 * 80 + 38) * 4 + 2] > 252);
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("normal", 12d)]
    [InlineData("initial", 12d)]
    [InlineData("inherit", 8d)]
    [InlineData("unset", 8d)]
    public void InheritanceAndNormalResetKeepDistinctFontFeatureRequests(string reset, double middleWidth)
    {
        var font = Font();
        var book = EpubDocument.Open(Book("<p style='font-variant:small-caps'><span>a</span><span style='font-variant:"
            + reset + "'>a</span>a</p>"));
        var layout = book.CreateLayout(new EpubLayoutOptions(80, 96, font) { Margin = 8, FontSize = 20 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("aaa", page.Text);
        Assert.Equal(new[] { 0, 1, 2 }, page.TextPlacements.Select(p => p.Start));
        Assert.Equal(new[] { 8d, middleWidth, 8d }, page.TextPlacements.Select(p => p.Width));
        Assert.All(page.TextPlacements, p => Assert.Equal(20, p.Height));
        Assert.Contains(layout.RenderPage(0).Pixels.ToArray(), value => value == 0);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 60, PageHeightPoints = 72, MarginPoints = 6, FontSizePoints = 15 }));
        Assert.Equal("aaa", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(p => p.Value)));
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(80, 96));
        Assert.Empty(frame.Diagnostics);
        Assert.Contains(frame.Pixels.ToArray(), value => value == 0);
    }

    [Fact]
    public void PresentFeatureDoesNotSynthesizeAnUncoveredCharacter()
    {
        var font = Font(variant: "uncovered");
        var book = EpubDocument.Open(Book("<p style='font-variant:small-caps'>aa</p>"));
        var layout = book.CreateLayout(new EpubLayoutOptions(80, 96, font) { Margin = 8, FontSize = 20 });
        Assert.All(Assert.Single(layout.Pages).TextPlacements, p => Assert.Equal(12, p.Width));
        var native = layout.RenderPage(0);
        Assert.True(native.Pixels[(8 * 80 + 17) * 4] < .01);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 60, PageHeightPoints = 72, MarginPoints = 6, FontSizePoints = 15 }));
        Assert.Equal("aa", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(p => p.Value)));
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(80, 96));
        Assert.Empty(frame.Diagnostics);
        Assert.True(frame.Pixels.Span[(8 * 80 + 17) * 4 + 2] < 3);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("outside")]
    [InlineData("count")]
    [InlineData("truncated")]
    [InlineData("delta-zero")]
    [InlineData("delta-outside")]
    public void InvalidSelectedSingleSubstitutionsFailWithoutChangingTheSource(string variant)
    {
        byte[] source = Book("<p style='font-variant:small-caps'>Aa</p>"), original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = Font(variant: variant);
        Assert.Throws<InvalidDataException>(() => book.CreateLayout(new EpubLayoutOptions(80, 96, font)));
        Assert.Throws<InvalidDataException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)));
        Assert.Equal(original, source);
    }

    [Fact]
    public void SelectedSmallCapsHonorLayoutConversionAndRenderCancellation()
    {
        var book = EpubDocument.Open(Book("<p style='font-variant:small-caps'>Aa</p>"));
        var font = Font();
        var options = new EpubLayoutOptions(80, 96, font);
        var layout = book.CreateLayout(options);
        var canceled = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => book.CreateLayout(options, canceled));
        Assert.Throws<OperationCanceledException>(() => layout.RenderPage(0, cancellationToken: canceled));
        Assert.Throws<OperationCanceledException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font), canceled));
    }

    private static TrueTypeFont Font(bool deltaMapping = false, string variant = "valid", bool extension = false)
    {
        byte[] cmap = new byte[52];
        U16(cmap, 2, 1); U16(cmap, 4, 3); U16(cmap, 6, 10); U32(cmap, 8, 12);
        U16(cmap, 12, 12); U32(cmap, 16, 40); U32(cmap, 24, 2);
        U32(cmap, 28, 'A'); U32(cmap, 32, 'A'); U32(cmap, 36, 1);
        U32(cmap, 40, 'a'); U32(cmap, 44, 'a'); U32(cmap, 48, 2);
        byte[] basis = TrueTypeFontTests.BuildTestFont(format12: true, includeOutlines: true,
            includeCompound: true, cmap: cmap);
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        for (int i = 0, count = BinaryPrimitives.ReadUInt16BigEndian(basis.AsSpan(4)); i < count; i++)
        {
            int record = 12 + i * 16;
            string tag = Encoding.ASCII.GetString(basis, record, 4);
            int offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(basis.AsSpan(record + 8)));
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(basis.AsSpan(record + 12)));
            tables.Add(tag, basis.AsSpan(offset, length).ToArray());
        }
        U16(tables["maxp"], 4, 4); U16(tables["hhea"], 34, 4);
        byte[] metrics = new byte[16]; tables["hmtx"].CopyTo(metrics, 0);
        U16(metrics, 4, 1000); U16(metrics, 12, 400); tables["hmtx"] = metrics;
        byte[] outlines = new byte[68]; tables["glyf"].CopyTo(outlines, 0);
        tables["glyf"].AsSpan(20, 24).CopyTo(outlines.AsSpan(44));
        U16(outlines, 50, 400); U16(outlines, 52, 600); U16(outlines, 61, 400);
        BinaryPrimitives.WriteInt16BigEndian(outlines.AsSpan(63), -200); U16(outlines, 65, 600);
        tables["glyf"] = outlines;
        byte[] locations = new byte[20]; tables["loca"].CopyTo(locations, 0);
        U32(locations, 16, 68); tables["loca"] = locations;
        byte[] gsub = new byte[70];
        U32(gsub, 0, 0x10000); U16(gsub, 4, 10); U16(gsub, 6, 30); U16(gsub, 8, 44);
        U16(gsub, 10, 1); "latn"u8.CopyTo(gsub.AsSpan(12)); U16(gsub, 16, 8);
        U16(gsub, 18, 4); U16(gsub, 24, 0xFFFF); U16(gsub, 26, 1);
        U16(gsub, 30, 1); "smcp"u8.CopyTo(gsub.AsSpan(32)); U16(gsub, 36, 8); U16(gsub, 40, 1);
        U16(gsub, 44, 1); U16(gsub, 46, 4); U16(gsub, 48, 1); U16(gsub, 52, 1); U16(gsub, 54, 8);
        U16(gsub, 56, 2); U16(gsub, 58, 8); U16(gsub, 60, 1); U16(gsub, 62, 3);
        U16(gsub, 64, 1); U16(gsub, 66, 1); U16(gsub, 68, 2); tables["GSUB"] = gsub;
        if (deltaMapping)
        {
            U16(gsub, 56, 1); U16(gsub, 60, 1);
        }
        if (variant == "uncovered") U16(gsub, 68, 1);
        if (variant == "zero") U16(gsub, 62, 0);
        if (variant == "outside") U16(gsub, 62, 4);
        if (variant == "count") U16(gsub, 60, 2);
        if (variant == "truncated") tables["GSUB"] = gsub[..63];
        if (variant.StartsWith("delta-", StringComparison.Ordinal))
        {
            U16(gsub, 56, 1); U16(gsub, 60, variant == "delta-zero" ? 65534 : 2);
        }
        if (variant.StartsWith("multiple", StringComparison.Ordinal))
        {
            byte[] multiple = new byte[76];
            gsub.AsSpan(0, 56).CopyTo(multiple);
            U16(multiple, 48, 2); U16(multiple, 56, 1); U16(multiple, 58, 14); U16(multiple, 60, 1);
            U16(multiple, 62, 8); U16(multiple, 64, 2); U16(multiple, 66, 3); U16(multiple, 68, 1);
            U16(multiple, 70, 1); U16(multiple, 72, 1); U16(multiple, 74, 2);
            gsub = multiple; tables["GSUB"] = gsub;
            if (variant == "multiple-empty") U16(gsub, 64, 0);
            if (variant == "multiple-overflow") U16(gsub, 64, 257);
            if (variant == "multiple-output-outside") U16(gsub, 68, 4);
            if (variant == "multiple-zero-offset") U16(gsub, 62, 0);
            if (variant == "multiple-coverage-count") U16(gsub, 60, 2);
            if (variant == "multiple-truncated") tables["GSUB"] = gsub[..69];
            if (variant == "multiple-follow-ligature")
            {
                byte[] combined = new byte[112];
                gsub.AsSpan(0, 44).CopyTo(combined);
                U16(combined, 8, 46); U16(combined, 40, 2); U16(combined, 44, 1);
                U16(combined, 46, 2); U16(combined, 48, 6); U16(combined, 50, 34);
                gsub.AsSpan(48, 28).CopyTo(combined.AsSpan(52));
                U16(combined, 80, 4); U16(combined, 84, 1); U16(combined, 86, 8);
                U16(combined, 88, 1); U16(combined, 90, 8); U16(combined, 92, 1); U16(combined, 94, 14);
                U16(combined, 96, 1); U16(combined, 98, 1); U16(combined, 100, 1);
                U16(combined, 102, 1); U16(combined, 104, 4); U16(combined, 106, 3);
                U16(combined, 108, 2); U16(combined, 110, 1); tables["GSUB"] = combined;
            }
        }
        if (variant is "mixed-extension" or "extension-pair")
        {
            byte[] pair = new byte[108];
            gsub.AsSpan(0, 54).CopyTo(pair);
            U16(pair, 48, 7); U16(pair, 52, 2); U16(pair, 54, 10); U16(pair, 56, 32);
            U16(pair, 58, 1); U16(pair, 60, 1); U32(pair, 62, 8);
            gsub.AsSpan(56, 14).CopyTo(pair.AsSpan(66));
            U16(pair, 80, 1); U16(pair, 82, variant == "mixed-extension" ? 2 : 1); U32(pair, 84, 8);
            if (variant == "mixed-extension")
            {
                U16(pair, 88, 1); U16(pair, 90, 14); U16(pair, 92, 1); U16(pair, 94, 8);
                U16(pair, 96, 2); U16(pair, 98, 3); U16(pair, 100, 1);
                U16(pair, 102, 1); U16(pair, 104, 1); U16(pair, 106, 1);
            }
            else
            {
                U16(pair, 88, 2); U16(pair, 90, 8); U16(pair, 92, 1); U16(pair, 94, 1);
                U16(pair, 96, 1); U16(pair, 98, 1); U16(pair, 100, 1);
            }
            tables["GSUB"] = pair;
        }
        if (extension)
        {
            byte[] wrapped = new byte[gsub.Length + 8];
            gsub.AsSpan(0, 56).CopyTo(wrapped);
            U16(wrapped, 48, 7); U16(wrapped, 56, 1); U16(wrapped, 58, variant.StartsWith("multiple", StringComparison.Ordinal) ? 2 : 1); U32(wrapped, 60, 8);
            gsub.AsSpan(56).CopyTo(wrapped.AsSpan(64)); tables["GSUB"] = wrapped;
        }
        int directory = 12 + tables.Count * 16;
        byte[] result = new byte[directory + tables.Values.Sum(value => (value.Length + 3) & ~3)];
        U32(result, 0, 0x10000); U16(result, 4, tables.Count);
        int at = directory, entry = 12;
        foreach (var table in tables)
        {
            Encoding.ASCII.GetBytes(table.Key).CopyTo(result, entry);
            U32(result, entry + 8, at); U32(result, entry + 12, table.Value.Length);
            table.Value.CopyTo(result, at); at += (table.Value.Length + 3) & ~3; entry += 16;
        }
        return TrueTypeFont.Load(result);
    }

    private static byte[] Book(string body)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='book.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add("book.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>urn:small-caps</dc:identifier><dc:title>Small caps</dc:title><dc:language>en</dc:language></metadata><manifest><item id='c' href='text.xhtml' media-type='application/xhtml+xml'/></manifest><spine><itemref idref='c'/></spine></package>");
            Add("text.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><body>" + body + "</body></html>");
            void Add(string path, string value)
            {
                var entry = zip.CreateEntry(path, path == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open(); stream.Write(Encoding.UTF8.GetBytes(value));
            }
        }
        return output.ToArray();
    }

    private static void U16(byte[] bytes, int at, int value) => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(at), checked((ushort)value));
    private static void U32(byte[] bytes, int at, int value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), checked((uint)value));
}
