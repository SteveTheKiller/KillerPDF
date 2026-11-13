using System.IO;
using System.Windows;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// Print's 1 / 2 / Book preview (redesign spec 2.4): which sheets face each other, and how big the
// page or spread is drawn in the preview frame. Expected values are worked by hand from the spec.
public sealed class PreviewSpreadsTests
{
    [Theory]
    [InlineData(true, true, "Book")]
    [InlineData(true, false, "TwoPage")]
    [InlineData(false, false, "Single")]
    [InlineData(false, true, "Single")]
    public void ThePreviewStartsFromTheViewersLayout(bool twoPage, bool bookMode, string expected)
        => Assert.Equal(Enum.Parse<PreviewLayout>(expected), PreviewSpreads.FromViewer(twoPage, bookMode));

    // Viewport.cs: BookMode ? (page == 0 ? 0 : page - ((page + 1) % 2)) : page - page % 2.
    [Theory]
    [InlineData(0, true, 0)]
    [InlineData(1, true, 1)]
    [InlineData(2, true, 1)]
    [InlineData(3, true, 3)]
    [InlineData(4, true, 3)]
    [InlineData(3, false, 2)]
    [InlineData(4, false, 4)]
    public void SpreadsStartWhereTheViewersDo(int index, bool book, int start)
        => Assert.Equal(start, PreviewSpreads.SpreadStart(index, book));

    // The viewer draws the page a step would land on before the step. In Book layout the cover stands alone, so the
    // spread after it starts at page 1 and the spread before pages 1-2 is the cover. Worked by hand from the spreads:
    // Book 10 is [0] [1,2] [3,4] [5,6] [7,8] [9]; Book 9 is [0] [1,2] [3,4] [5,6] [7,8].
    [Theory]
    [InlineData("Single", 10, 4, new[] { 5, 3 })]
    [InlineData("Single", 10, 0, new[] { 1 })]
    [InlineData("Single", 10, 9, new[] { 8 })]
    [InlineData("TwoPage", 10, 0, new[] { 2 })]
    [InlineData("TwoPage", 10, 3, new[] { 4, 0 })]
    [InlineData("TwoPage", 9, 7, new[] { 8, 4 })]
    [InlineData("TwoPage", 9, 8, new[] { 6 })]
    [InlineData("Book", 10, 0, new[] { 1 })]
    [InlineData("Book", 10, 1, new[] { 3, 0 })]
    [InlineData("Book", 10, 2, new[] { 3, 0 })]
    [InlineData("Book", 10, 8, new[] { 9, 5 })]
    [InlineData("Book", 10, 9, new[] { 7 })]
    [InlineData("Book", 9, 8, new[] { 5 })]
    [InlineData("Book", 9, 4, new[] { 5, 1 })]
    [InlineData("Single", 1, 0, new int[0])]
    [InlineData("TwoPage", 1, 0, new int[0])]
    [InlineData("Book", 1, 0, new int[0])]
    public void TheNextAndPreviousSpreadsAreDrawnAheadFromTheirFirstRealPage(string layout, int count, int page,
        int[] expected)
        => Assert.Equal(expected, PreviewSpreads.NeighborStarts(page, count, Enum.Parse<PreviewLayout>(layout)));

    // SOURCE PIN: the viewer is not compiled into this project. Its prefetch takes its targets from the function above.
    [Fact]
    public void TheViewersPrefetchDrawsTheNeighborStarts()
    {
        string viewport = ViewerSourcePin.Viewer("PdfViewer.Viewport.cs");
        string prefetch = ViewerSourcePin.Between(viewport, "private void KickOffAdjacentPrefetch(", "var rotations = ");
        Assert.Contains("int[] targets = PreviewSpreads.NeighborStarts(pageIndex, total,", prefetch);
    }

    [Fact]
    public void BookShowsTheFirstSheetAloneThenPairs()
    {
        Assert.Equal([(0, -1), (1, 2), (3, 4)], PreviewSpreads.Spreads(5, PreviewLayout.Book));
        // An even count leaves the last sheet alone on the left.
        Assert.Equal([(0, -1), (1, 2), (3, 4), (5, -1)], PreviewSpreads.Spreads(6, PreviewLayout.Book));
    }

    [Fact]
    public void TwoPagePairsFromTheFirstSheet()
    {
        Assert.Equal([(0, 1), (2, 3), (4, -1)], PreviewSpreads.Spreads(5, PreviewLayout.TwoPage));
        Assert.Equal([(0, 1), (2, 3), (4, 5)], PreviewSpreads.Spreads(6, PreviewLayout.TwoPage));
    }

    [Fact]
    public void SingleShowsOneSheetAtATime()
        => Assert.Equal([(0, -1), (1, -1), (2, -1)], PreviewSpreads.Spreads(3, PreviewLayout.Single));

    [Fact]
    public void AOneSheetJobIsOneSpreadInEveryLayoutAndNoSheetsIsNone()
    {
        foreach (PreviewLayout layout in Enum.GetValues<PreviewLayout>())
        {
            Assert.Equal([(0, -1)], PreviewSpreads.Spreads(1, layout));
            Assert.Empty(PreviewSpreads.Spreads(0, layout));
        }
    }

    [Theory]
    [InlineData(0, "Book", 0)]
    [InlineData(2, "Book", 1)]
    [InlineData(4, "Book", 2)]
    [InlineData(5, "Book", 3)]
    [InlineData(5, "TwoPage", 2)]
    [InlineData(4, "Single", 4)]
    public void TheCurrentSheetOpensOnTheSpreadThatHoldsIt(int index, string layout, int spread)
        => Assert.Equal(spread, PreviewSpreads.SpreadOf(index, 6, Enum.Parse<PreviewLayout>(layout)));

    // 2 x 162 + 12 + 2 x 16 = 368.
    [Fact]
    public void TwoPagesNeedA368WideFrame()
    {
        Assert.True(PreviewSpreads.FitsTwo(368));
        Assert.False(PreviewSpreads.FitsTwo(367.9));
    }

    // Page aspect 1.5 in an 800 x 600 frame. One page: height binds, (600 - 32) / 1.5 = 378.67
    // wide by 568. Two pages: width binds, (800 - 32 - 12) / 2 = 378 wide by 567.
    [Fact]
    public void APageFillsTheFrameLessTheMargin()
    {
        Size one = PreviewSpreads.PageSize(new Size(800, 600), 1.5, 1);
        Assert.Equal(378.667, one.Width, 3);
        Assert.Equal(568, one.Height, 3);

        Size two = PreviewSpreads.PageSize(new Size(800, 600), 1.5, 2);
        Assert.Equal(378, two.Width, 3);
        Assert.Equal(567, two.Height, 3);
    }

    // A wide, short frame is height-limited: (400 - 32) / 1.5 = 245.33 by 368, so the spread
    // (2 x 245.33 + 12 = 502.67) never runs past the 1000 wide frame.
    [Fact]
    public void AWideShortFrameIsLimitedByItsHeight()
    {
        Size two = PreviewSpreads.PageSize(new Size(1000, 400), 1.5, 2);
        Assert.Equal(245.333, two.Width, 3);
        Assert.Equal(368, two.Height, 3);
    }

    // The fitted default: a 600 tall frame fills with 2 x 378.67 + 12 + 32 = 801.33 of width, or
    // 378.67 + 32 = 410.67 for one page.
    [Fact]
    public void TheFittedFrameWidthLetsThePagesFillItsHeight()
    {
        Assert.Equal(801.333, PreviewSpreads.FrameWidthFor(600, 1.5, 2), 3);
        Assert.Equal(410.667, PreviewSpreads.FrameWidthFor(600, 1.5, 1), 3);
    }

    // The viewer and Print agree on what faces what: every page's spread in Print's list starts
    // where the viewer's formula puts its left page, for book and two-page, odd and even counts.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PrintSpreadsStartWhereTheViewerPutsTheLeftPage(bool book)
    {
        var layout = book ? PreviewLayout.Book : PreviewLayout.TwoPage;
        for (int count = 1; count <= 9; count++)
        {
            var spreads = PreviewSpreads.Spreads(count, layout);
            for (int page = 0; page < count; page++)
                Assert.Equal(PreviewSpreads.SpreadStart(page, book), spreads[PreviewSpreads.SpreadOf(page, count, layout)].First);
        }
    }

    // One formula: the viewer's two-page navigation calls the shared helper instead of its own copy.
    [Fact]
    public void TheViewerPairsSpreadsThroughTheSharedHelper()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        string viewport = File.ReadAllText(Path.Combine(directory!.FullName, "Controls", "Viewer", "PdfViewer.Viewport.cs"));

        Assert.Contains("PreviewSpreads.SpreadStart(page, BookMode)", viewport, StringComparison.Ordinal);
        Assert.DoesNotContain("page - ((page + 1) % 2)", viewport, StringComparison.Ordinal);
    }
}
