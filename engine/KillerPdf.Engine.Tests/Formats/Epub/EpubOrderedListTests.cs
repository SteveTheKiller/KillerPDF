using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// HTML ordered lists draw "N. " as an outside marker that ends where the item content starts.
// The test font draws A 6px wide and its .notdef (digits, period, space) 5px wide at 10px; the list's
// 40px start padding puts item content at x = 42 on a page with 2px margins.
public sealed class EpubOrderedListTests
{
    [Fact]
    public void OrderedListItemsDrawTheirNumbersBeforeTheContent()
    {
        var page = Single("<ol><li>A</li><li>A</li></ol>");

        // "1. " is three 5px advances, so the digit starts at 42 - 15 = 27 and the period at 32.
        var markers = Markers(page);
        Assert.Equal(new[] { new[] { 27d, 32d }, new[] { 27d, 32d } }, markers);
    }

    [Fact]
    public void TheStartAttributeAndWiderNumbersMoveTheMarkerLeft()
    {
        var page = Single("<ol start='9'><li>A</li><li>A</li></ol>");

        // "10. " is four advances: 42 - 20 = 22.
        Assert.Equal(new[] { new[] { 27d, 32d }, new[] { 22d, 27d, 32d } }, Markers(page));
    }

    [Fact]
    public void TheTypeAttributeSelectsAlphabeticNumbering()
    {
        var page = Single("<ol type='A' start='27'><li>A</li></ol>");

        // Item 27 in upper-alpha is AA: 6 + 6 + 5 for the period + 5 for the space = 22, so it starts at 20.
        Assert.Equal(new[] { new[] { 20d, 26d, 32d } }, Markers(page));
    }

    [Fact]
    public void ConvertedOrderedListsKeepTheirNumbersAsText()
    {
        using Stream stream = typeof(TrueTypeFont).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var font = TrueTypeFont.Load(bytes.ToArray());
        var book = EpubDocument.Open(new Book { Chapters = { Chapter("<ol><li>ALPHA</li><li>BRAVO</li></ol>") } }.Build());

        var reader = new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font))));

        var letters = reader.Read(0).Letters.OrderBy(letter => -letter.StartBaseLine.Y).ThenBy(letter => letter.StartBaseLine.X);
        Assert.Equal("1.ALPHA2.BRAVO", string.Concat(letters.Select(letter => letter.Value)).Replace(" ", ""));
    }

    private static EpubLayoutPage Single(string body)
    {
        var layout = Layout(new Book { Chapters = { Chapter(body) } }, 120, 120);
        Assert.Empty(layout.ChapterDiagnostics);
        return Assert.Single(layout.Pages);
    }

    // Glyph paints left of the item content, grouped by baseline in reading order.
    private static double[][] Markers(EpubLayoutPage page) => page.Glyphs.OfType<NativeGlyphPaint>()
        .Where(glyph => glyph.Left < 42 - 1e-9)
        .GroupBy(glyph => glyph.Baseline).OrderBy(group => group.Key)
        .Select(group => group.Select(glyph => Math.Round(glyph.Left, 6)).OrderBy(left => left).ToArray()).ToArray();
}
