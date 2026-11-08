using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubUnicodeLayoutTests
{
    [Theory]
    [InlineData("\u00E9A")]
    [InlineData("\u03A9A")]
    [InlineData("\u0416A")]
    public void MappedPrecomposedLetterRetainsSourceOffsetsAndPaintsKnownTriangleInk(string text)
    {
        var book = EpubDocument.Open(Book("<p>" + text + "</p>"));
        var layout = book.CreateLayout(Options());

        var page = Assert.Single(layout.Pages);
        Assert.Equal(text, book.ReadText(0));
        Assert.Equal(text, page.Text);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10),
            new EpubTextPlacement(0, 1, 1, 8, 2, 6, 10) }, page.TextPlacements);
        var frame = layout.RenderPage(0);
        // The fixture maps both scalars to a triangle with vertices (0,0), (1000,0), (500,1000).
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((5 * 30 + 7) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((5 * 30 + 13) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((15 * 30 + 7) * 4, 4).ToArray());
    }

    [Fact]
    public void SupplementaryLetterWrapsAsOneGlyphAndKeepsUtf16PagesAndAnchorOffsets()
    {
        var book = EpubDocument.Open(Book("<p>A \U0001D400<span id=\"after\">A</span></p>"));
        var layout = book.CreateLayout(Options() with { PageWidth = 20, PageHeight = 14 });

        Assert.Equal("A \U0001D400A", book.ReadText(0));
        Assert.Equal(new[] { "A ", "\U0001D400A" }, layout.Pages.Select(page => page.Text));
        Assert.Equal(new[] { 0, 2 }, layout.Pages.Select(page => page.TextStart));
        Assert.Equal(new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10), Assert.Single(layout.Pages[0].TextPlacements));
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 2, 2, 2, 2, 6, 10),
            new EpubTextPlacement(0, 4, 1, 8, 2, 6, 10) }, layout.Pages[1].TextPlacements);
        Assert.Equal(new EpubNavigationDestination(1, 0, 4, 8, 2),
            layout.ResolveNavigationTarget("EPUB/text.xhtml#after"));
        var frame = layout.RenderPage(1);
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((5 * 20 + 7) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((5 * 20 + 13) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((12 * 20 + 7) * 4, 4).ToArray());
    }

    [Theory]
    [InlineData("<p>A&#8217;A</p>", "A\u2019A")]
    [InlineData("<p>A&#8220;A</p>", "A\u201CA")]
    [InlineData("<p>A&#8226;A</p>", "A\u2022A")]
    [InlineData("<p>A&#8230;A</p>", "A\u2026A")]
    [InlineData("<p>A&#8482;A</p>", "A\u2122A")]
    public void MappedTypographicQuotePreservesNativeInkAndSelectableConvertedText(string body, string expected)
    {
        byte[] source = Book(body);
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);

        var layout = book.CreateLayout(Options());

        var page = Assert.Single(layout.Pages);
        Assert.Equal(expected, page.Text);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10),
            new EpubTextPlacement(0, 1, 1, 8, 2, 6, 10),
            new EpubTextPlacement(0, 2, 1, 14, 2, 6, 10) }, page.TextPlacements);
        var frame = layout.RenderPage(0);
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((5 * 30 + 13) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((5 * 30 + 27) * 4, 4).ToArray());
        byte[] converted = book.ConvertToPdf(new EpubPdfConversionOptions(Font()));
        var reader = new PdfPageContentReader(PdfDocument.Open(converted));
        Assert.Equal(expected, string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));
        Assert.Equal(original, source);
        Assert.Equal(expected, book.ReadText(0));
    }

    [Fact]
    public void TypographicQuoteAfterSpaceRetainsTheWholeWordAndSourceOffsetsWhenWrapping()
    {
        var book = EpubDocument.Open(Book("<p>A A&#8217;A</p>"));
        var layout = book.CreateLayout(Options() with { PageWidth = 24, PageHeight = 24 });

        var page = Assert.Single(layout.Pages);
        Assert.Equal("A A\u2019A", page.Text);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10),
            new EpubTextPlacement(0, 2, 1, 2, 12, 6, 10),
            new EpubTextPlacement(0, 3, 1, 8, 12, 6, 10),
            new EpubTextPlacement(0, 4, 1, 14, 12, 6, 10) }, page.TextPlacements);
        var frame = layout.RenderPage(0);
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((15 * 24 + 13) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((23 * 24 + 13) * 4, 4).ToArray());
    }

    [Fact]
    public void MappedCurrencySymbolPreservesNativeLayoutAndSelectablePdfText()
    {
        var book = EpubDocument.Open(Book("<p>A&#8364;A</p>"));

        var layout = book.CreateLayout(Options());

        var page = Assert.Single(layout.Pages);
        Assert.Equal("A\u20ACA", page.Text);
        Assert.Equal(new EpubTextPlacement(0, 1, 1, 8, 2, 6, 10), page.TextPlacements[1]);
        var frame = layout.RenderPage(0);
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((5 * 30 + 13) * 4, 4).ToArray());
        byte[] converted = book.ConvertToPdf(new EpubPdfConversionOptions(Font()));
        var reader = new PdfPageContentReader(PdfDocument.Open(converted));
        Assert.Equal("A\u20ACA", string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));
    }

    private static EpubLayoutOptions Options() => new(30, 36, Font()) { FontSize = 10, Margin = 2 };

    [Theory]
    [InlineData("\u0301", "0301")]
    [InlineData("\u05D0", "05D0")]
    [InlineData("\u0627", "0627")]
    [InlineData("\u0915", "0915")]
    [InlineData("\u200D", "200D")]
    [InlineData("\u202E", "202E")]
    [InlineData("\uFE0F", "FE0F")]
    public void MappedComplexTextRequiresExplicitShapingOrDirectionSupport(string text, string codePoint)
    {
        Assert.Equal((ushort)1, Font().GetGlyphId(char.ConvertToUtf32(text, 0)));
        foreach (string prefix in new[] { "A", "A " })
        {
            var book = EpubDocument.Open(Book("<p>" + prefix + text + "</p>"));
            var error = Assert.Throws<NotSupportedException>(() => book.CreateLayout(Options()));
            Assert.Equal("Native EPUB layout requires unsupported shaping or bidirectional processing for U+"
                + codePoint + ".", error.Message);
        }
    }

    [Theory]
    [InlineData("\u03A3", "03A3")]
    [InlineData("\U0001D401", "1D401")]
    public void MissingSupportedGlyphShowsTheMissingGlyphAndReportsItsFullScalar(string text, string codePoint)
    {
        foreach (string prefix in new[] { "A", "A " })
        {
            var book = EpubDocument.Open(Book("<p>" + prefix + text + "</p>"));
            var layout = book.CreateLayout(Options());
            Assert.Empty(layout.ChapterDiagnostics);
            Assert.Equal("No available font has a glyph for U+" + codePoint + ", so a missing-glyph box is shown.",
                Assert.Single(layout.ContentDiagnostics).Message);
        }
    }

    [Fact]
    public void ExplicitPdfConversionReopensWithOriginalGreekAndSupplementaryTextAndPreservesSource()
    {
        byte[] source = Book("<p>\u03A9\U0001D400A</p>");
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);
        byte[] chapter = book.ReadResource("EPUB/text.xhtml");

        byte[] converted = book.ConvertToPdf(new EpubPdfConversionOptions(Font()));

        var reader = new PdfPageContentReader(PdfDocument.Open(converted));
        Assert.Equal(1, reader.PageCount);
        Assert.Equal("\u03A9\U0001D400A", string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));
        Assert.Equal("\u03A9\U0001D400A", book.ReadText(0));
        Assert.Equal(original, source);
        Assert.Equal(chapter, book.ReadResource("EPUB/text.xhtml"));
    }

    private static TrueTypeFont Font()
    {
        int[] scalars = [0x41, 0xE9, 0x301, 0x3A9, 0x416, 0x5D0, 0x627, 0x915, 0x200D, 0x2019, 0x201C, 0x2022, 0x2026, 0x202E, 0x20AC, 0x2122, 0xFE0F, 0x1D400];
        byte[] cmap = new byte[28 + scalars.Length * 12];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6), 10);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8), 12);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(12), 12);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(16), (uint)cmap.Length - 12);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(24), (uint)scalars.Length);
        for (int index = 0; index < scalars.Length; index++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(28 + index * 12), (uint)scalars[index]);
            BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(32 + index * 12), (uint)scalars[index]);
            BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(36 + index * 12), 1);
        }
        return TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true, cmap: cmap));
    }

    private static byte[] Book(string body)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            Add("EPUB/book.opf", """
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:unicode</dc:identifier><dc:title>Unicode</dc:title><dc:language>en</dc:language></metadata><manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="chapter"/></spine></package>
                """);
            Add("EPUB/text.xhtml", $"""
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Unicode</title></head><body>{body}</body></html>
                """);
            void Add(string path, string text)
            {
                using var stream = zip.CreateEntry(path, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return output.ToArray();
    }
}
