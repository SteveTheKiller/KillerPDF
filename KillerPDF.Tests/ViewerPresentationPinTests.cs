using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// These are SOURCE PINS, not behavior tests. The viewer is not part of this test project and a viewer cannot be built
// here without a window: CapturePrimaryDisplay reads DisplayCapture only once the control has a window handle, so no
// window-free test reaches the display hook. Each pin names the exact expression a finding changed, and the finding's
// written window check (in the 1.9.3 review notes) is the behavior evidence.
public sealed class ViewerPresentationPinTests
{
    // VIEW-001: on an HDR or Windows automatic color management display the page used to be drawn in a native child
    // window, and everything the main window floats over the page (find bar, form size bar, page badge, notices) was
    // hidden under it. 1.9.3 keeps the page a plain WPF image there, as 1.9.2 did; the presenter code stays for 1.9.4.
    [Fact]
    public void AdvancedColorDisplaysDoNotCreateTheNativeChildWindowIn193()
    {
        string presentation = Source("Controls", "Viewer", "PdfViewer.Presentation.cs");
        string viewport = Source("Controls", "Viewer", "PdfViewer.Viewport.cs");

        Assert.Contains("static readonly bool NativePrimaryViewportEnabled = false;", presentation);
        string linear = Between(viewport, "bool linear = ", ";");
        Assert.Contains("NativePrimaryViewportEnabled", linear);
        // The host is only built by the present step, which only runs for a frame the linear path produced.
        Assert.Equal(1, Count(presentation, "new WindowsPdfViewportHost("));
        Assert.Equal(1, Count(viewport, "await PresentPrimaryFrameAsync("));
    }

    // VIEW-002: Grid draws page 0 as its primary tile and the rest as neighbours. A display color change redrew the
    // current page as the primary, which collapsed the grid to one tile showing the wrong page.
    [Fact]
    public void ADisplayChangeRedrawsTheGridFromItsFirstPageLikeEveryOtherCaller()
    {
        string presentation = Source("Controls", "Viewer", "PdfViewer.Presentation.cs");
        string rerender = Between(presentation, "private void RerenderForDisplayChange()", "private PdfDisplayDiscoverySnapshot ObservePresentationDisplay(");

        Assert.Contains("else RenderPage(_viewMode == ViewMode.Grid ? 0 : _currentPage);", rerender);
        Assert.DoesNotContain("else RenderPage(_currentPage);", rerender);
    }

    // VIEW-003: the primary render looks its bitmap up under the page's proof stamp. The background neighbour render
    // stored under no stamp, so Page Down and Page Up never found it. The neighbour render draws without proofing or
    // monitor conversion, so it only runs when the primary will read the cache and proofing is off.
    [Fact]
    public void NeighbourPrefetchIsStoredUnderTheKeyThePrimaryRenderReads()
    {
        string viewport = Source("Controls", "Viewer", "PdfViewer.Viewport.cs");
        string prefetch = Between(viewport, "private void KickOffAdjacentPrefetch(", "/// <summary>The page's image boxes");

        Assert.Contains("int pgRot, bool primaryReadsCache)", prefetch);
        Assert.Contains("var proof = session?.ProofView.CaptureProof();", prefetch);
        Assert.Contains("if (!primaryReadsCache || proof is null || proof.Settings.Enabled) return;", prefetch);
        Assert.Contains("TryGetCachedRender(session, target, scaledMax, rot, proofStamp: proof.Stamp)", prefetch);
        Assert.Contains("CacheRender(session, localTarget, scaledMax, localRot, wb, proofStamp: proof.Stamp)", prefetch);
        Assert.Contains("KickOffAdjacentPrefetch(pageIndex, scaledMax, pgRot, !(linear || primaryMonitor is not null));", viewport);
    }

    private static int Count(string source, string text)
    {
        int count = 0;
        for (int at = source.IndexOf(text, StringComparison.Ordinal); at >= 0; at = source.IndexOf(text, at + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string Between(string source, string start, string end)
    {
        int from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "Not found: " + start);
        int to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, "Not found: " + end);
        return source[from..to];
    }

    private static string Source(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }
}
