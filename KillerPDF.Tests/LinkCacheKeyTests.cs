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

    // An EPUB link read while its chapter was not paginated yet is labeled "Open link". Once pagination moves on, the
    // list is read again so the label can name the page; a list whose labels are all final stays.
    [Fact]
    public void AListReadBeforePaginationReachedALinkTargetIsReadAgainOncePaginationMovesOn()
    {
        var book = new object();
        var links = new List<int> { 1 };
        var provisional = LinkCacheKey.For(300, 400, book, links, unresolvedAt: 3);

        Assert.True(provisional.Matches(links, 300, 400, book, pagination: 3));
        Assert.False(provisional.Matches(links, 300, 400, book, pagination: 4));
        Assert.True(LinkCacheKey.For(300, 400, book, links).Matches(links, 300, 400, book, pagination: 4));
    }

    // The key map outlives a document change (only the link map is cleared), so a key must not keep the old
    // document's link list reachable.
    [Fact]
    public void AKeyDoesNotKeepItsLinkListAlive()
    {
        var (key, weak) = KeyForALinkListNothingElseHolds();

        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weak.TryGetTarget(out _));
        Assert.False(key.Matches(new List<int>(), 300, 400, null));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (LinkCacheKey Key, WeakReference<object> List) KeyForALinkListNothingElseHolds()
    {
        var links = new List<int> { 1, 2, 3 };
        return (LinkCacheKey.For(300, 400, null, links), new WeakReference<object>(links));
    }
}
