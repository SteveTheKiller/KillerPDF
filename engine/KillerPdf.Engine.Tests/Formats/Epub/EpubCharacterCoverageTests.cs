using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// The test font draws A 6px wide at 10px and has no other mapped glyph; its .notdef is 5px wide.
// A 22px page with 2px margins leaves an 18px line (right edge 20).
public sealed class EpubCharacterCoverageTests
{
    [Theory]
    [InlineData("&#173;")]
    [InlineData("&#x200B;")]
    public void SoftHyphenAndZeroWidthSpaceAreInvisibleBreakOpportunities(string separator)
    {
        var page = Assert.Single(Layout(new Book { Chapters = { Chapter("<p>A" + separator + "AAA</p>") } }, 22, 40).Pages);

        // AAA does not fit beside the first A, so the line breaks at the invisible opportunity.
        Assert.Equal(new[] { (2d, 2d), (2d, 12d), (8d, 12d), (14d, 12d) }, Letters(page));
        Assert.DoesNotContain(page.TextPlacements, placement => page.Text[placement.Start - page.TextStart] != 'A' && placement.Width != 0);
    }

    [Fact]
    public void ZeroWidthNoBreakSpaceIsInvisibleAndKeepsTheWordTogether()
    {
        var page = Assert.Single(Layout(new Book { Chapters = { Chapter("<p>A&#xFEFF;AAA</p>") } }, 22, 40).Pages);

        // With no opportunity the four letters fill the 18px line by width: three fit, the fourth breaks.
        Assert.Equal(new[] { (2d, 2d), (8d, 2d), (14d, 2d), (2d, 12d) }, Letters(page));
    }

    // Unicode 16 chapter 6, table 6-2: en 1/2 em, em 1 em, three-per-em 1/3, four-per-em 1/4, six-per-em 1/6,
    // thin 1/5. The test font has no digit or period, so figure and punctuation spaces use their common
    // fallbacks of 1/2 and 1/4 em, and the hair space, defined only as narrower than thin, is 1/10 em.
    [Theory]
    [InlineData("2000", 5)]
    [InlineData("2001", 10)]
    [InlineData("2002", 5)]
    [InlineData("2003", 10)]
    [InlineData("2004", 10d / 3)]
    [InlineData("2005", 2.5)]
    [InlineData("2006", 10d / 6)]
    [InlineData("2007", 5)]
    [InlineData("2008", 2.5)]
    [InlineData("2009", 2)]
    [InlineData("200A", 1)]
    public void FixedWidthSpacesAdvanceByTheirTypographicWidth(string codePoint, double advance)
    {
        var layout = Layout(new Book { Chapters = { Chapter("<p>A&#x" + codePoint + ";A</p>") } }, 60, 40);

        Assert.Empty(layout.ChapterDiagnostics);
        var page = Assert.Single(layout.Pages);
        Assert.Equal(new[] { 2d, 8 + advance }, Letters(page).Select(letter => letter.Left).ToArray(), new ToleranceComparer());
    }

    [Fact]
    public void AThinSpaceIsABreakOpportunity()
    {
        var page = Assert.Single(Layout(new Book { Chapters = { Chapter("<p>AA&#x2009;AA</p>") } }, 22, 40).Pages);

        // AA and the 2px thin space use 14px; AA needs 12 more, so it moves to the next line whole.
        Assert.Equal(new[] { (2d, 2d), (8d, 2d), (2d, 12d), (8d, 12d) }, Letters(page));
    }

    [Fact]
    public void ACharacterNoFontCanDrawShowsTheMissingGlyphAndIsReportedNotRefused()
    {
        var book = EpubDocument.Open(new Book { Chapters = { Chapter("<p>A&#x2720;A</p>") } }.Build());

        var layout = book.CreateLayout(Options(60, 40));

        Assert.Empty(layout.ChapterDiagnostics);
        // The 5px .notdef box stands in for U+2720 between the two letters.
        Assert.Equal(new[] { (2d, 2d), (13d, 2d) }, Letters(Assert.Single(layout.Pages)));
        var diagnostic = Assert.Single(layout.ContentDiagnostics);
        Assert.Equal("OEBPS/text/c0.xhtml", diagnostic.Href);
        Assert.Contains("U+2720", diagnostic.Message);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont)));
        Assert.Equal(2, new PdfPageContentReader(pdf).Read(0).Letters.Count(letter => letter.Value == "A"));
    }

    // U+0338 is an overlay mark (canonical combining class 1): it is drawn centered over its base with no
    // anchor height, so drawing it at zero advance needs no mark positioning. Accents still need GPOS.
    [Fact]
    public void TheLongSolidusOverlayIsAcceptedWhileAnAccentStillNeedsShaping()
    {
        var overlay = Layout(new Book { Chapters = { Chapter("<p>A&#x0338;A</p>") } }, 60, 40);
        Assert.Empty(overlay.ChapterDiagnostics);
        Assert.Contains("U+0338", Assert.Single(overlay.ContentDiagnostics).Message);

        var accent = Layout(new Book { Chapters = { Chapter("<p>A&#x0301;A</p>") } }, 60, 40);
        Assert.Contains("shaping or bidirectional processing for U+0301", Assert.Single(accent.ChapterDiagnostics).Message);
    }

    [Fact]
    public void HebrewRemainsASanctionedChapterRefusal()
    {
        var layout = Layout(new Book { Chapters = { Chapter("<p>A&#x05D7;A</p>") } }, 60, 40);

        Assert.Equal("Native EPUB layout requires unsupported shaping or bidirectional processing for U+05D7.",
            Assert.Single(layout.ChapterDiagnostics).Message);
    }

    private static (double Left, double Top)[] Letters(EpubLayoutPage page) => page.TextPlacements
        .Where(placement => page.Text[placement.Start - page.TextStart] == 'A')
        .Select(placement => (placement.Left, placement.Top)).ToArray();

    private sealed class ToleranceComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-9;
        public int GetHashCode(double value) => 0;
    }
}
