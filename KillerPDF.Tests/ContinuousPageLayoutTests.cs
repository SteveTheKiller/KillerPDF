using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ContinuousPageLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleColumnKeepsPageOrderAndIgnoresTheBookToggle(bool book)
    {
        var pages = ContinuousPageLayout.Create([200, 300, 100], 100, twoPage: false, book: book);
        Assert.Equal(new double[] { 0, 212, 524 }, pages.Select(page => page.Top));
        Assert.All(pages, page => Assert.Equal(0, page.Left));
        Assert.Empty(ContinuousPageLayout.Create([], 100, twoPage: true, book: book));
        var cover = Assert.Single(ContinuousPageLayout.Create([200], 100, twoPage: true, book: book));
        Assert.Equal(book ? 56 : 0, cover.Left);
    }

    [Fact]
    public void BookCoverIsCenteredAloneBeforeFacingPages()
    {
        var pages = ContinuousPageLayout.Create([200, 300, 100, 150], 100, twoPage: true, book: true);
        Assert.Equal(56, pages[0].Left);
        Assert.Equal(0, pages[0].Top);
        Assert.Equal(0, pages[1].Left);
        Assert.Equal(212, pages[1].Top);
        Assert.Equal(112, pages[2].Left);
        Assert.Equal(212, pages[2].Top);
        Assert.Equal(524, pages[3].Top);
    }

    [Fact]
    public void FacingPagesShareARowAndNextSpreadClearsTheTallerPage()
    {
        var pages = ContinuousPageLayout.Create([200, 300, 100], 100, twoPage: true, book: false);
        Assert.Equal(0, pages[0].Left);
        Assert.Equal(0, pages[0].Top);
        Assert.Equal(112, pages[1].Left);
        Assert.Equal(0, pages[1].Top);
        Assert.Equal(0, pages[2].Left);
        Assert.Equal(312, pages[2].Top);
    }
}
