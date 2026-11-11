using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

public sealed class LinkCacheKeyTests
{
    [Fact]
    public void SameListSizeAndDocumentMatches()
    {
        var doc = new object();
        var links = new List<int>();
        var key = LinkCacheKey.For(300, 400, doc, links);
        Assert.True(key.Matches(links, 300, 400, doc));
    }

    [Fact]
    public void ListRefilledByAnotherLayoutAtTheSameSizeDoesNotMatch()
    {
        // Grid read the page at 300 x 400; Continuous then cleared the map and stored its own list at the
        // 2048-based size. Back in Grid the map holds that other list, so the grid key must not reuse it.
        var doc = new object();
        var gridLinks = new List<int> { 1 };
        var key = LinkCacheKey.For(300, 400, doc, gridLinks);
        var continuousLinks = new List<int> { 2 };
        Assert.False(key.Matches(continuousLinks, 300, 400, doc));
    }

    [Fact]
    public void DifferentSizeOrDocumentDoesNotMatch()
    {
        var doc = new object();
        var links = new List<int>();
        var key = LinkCacheKey.For(300, 400, doc, links);
        Assert.False(key.Matches(links, 301, 400, doc));
        Assert.False(key.Matches(links, 300, 399, doc));
        Assert.False(key.Matches(links, 300, 400, new object()));
        Assert.False(key.Matches(links, 300, 400, null));
    }

    [Fact]
    public void NoDocumentMatchesNoDocument()
    {
        var links = new List<int>();
        Assert.True(LinkCacheKey.For(1, 1, null, links).Matches(links, 1, 1, null));
    }
}
