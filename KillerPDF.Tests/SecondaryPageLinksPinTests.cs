using Xunit;
using static KillerPDF.Tests.ViewerSourcePin;

namespace KillerPDF.Tests;

// SOURCE PIN (viewer BF-010 remainder). The viewer is not compiled into this project, so the call count of
// GetPageLinks cannot be observed here; the written window check in the 1.9.3 notes covers behavior (needs-vm).
public sealed class SecondaryPageLinksPinTests
{
    // Every redraw of a tile re-read the page's links. The overlays are rebuilt from the page's cached links instead,
    // and the links are read again only when the page size or the document is not the one they were read for, or when
    // the cache was cleared.
    [Fact]
    public void ARedrawRebuildsTheLinkOverlaysFromTheCachedLinks()
    {
        string links = Viewer("PdfViewer.Links.cs");
        string method = Between(links, "internal void AddSecondaryPageLinks(int pageIndex, int bitmapW, int bitmapH)", "foreach (var lnk in links)");

        Assert.Equal(1, Count(method, "GetPageLinks("));
        Assert.Contains("object? owner = (object?)_nativeDoc ?? _doc;", method);
        Assert.Contains("_continuousLinks.TryGetValue(pageIndex, out var links)", method);
        Assert.Contains("_continuousLinkKeys.TryGetValue(pageIndex, out var key)", method);
        Assert.Contains("key.W == bitmapW && key.H == bitmapH", method);
        Assert.Contains("key.Owner.TryGetTarget(out var held) && ReferenceEquals(held, owner)", method);
        Assert.Contains("_continuousLinkKeys[pageIndex] = (bitmapW, bitmapH, owner is null ? null : new WeakReference<object>(owner));", method);
        Assert.Contains("private readonly Dictionary<int, (int W, int H, WeakReference<object>? Owner)> _continuousLinkKeys = [];", links);
    }
}
