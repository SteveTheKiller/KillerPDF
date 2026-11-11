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
    // monitor conversion unless it is given the monitor converter, so it only runs when the primary will read the cache, proofing is
    // off, and, under a monitor profile, when it draws through the same conversion and stores under the monitor identity.
    [Fact]
    public void NeighbourPrefetchIsStoredUnderTheKeyThePrimaryRenderReads()
    {
        string viewport = Source("Controls", "Viewer", "PdfViewer.Viewport.cs");
        string prefetch = Between(viewport, "private void KickOffAdjacentPrefetch(", "/// <summary>The page's image boxes");

        Assert.Contains("int pgRot, bool primaryReadsCache,", prefetch);
        Assert.Contains("var proof = session?.ProofView.CaptureProof();", prefetch);
        Assert.Contains("if (!primaryReadsCache || proof is null || proof.Settings.Enabled) return;", prefetch);
        Assert.Contains("TryGetCachedRender(session, target, scaledMax, rot, proofStamp: proof.Stamp, monitorIdentity: monitorIdentity)", prefetch);
        Assert.Contains("CacheRender(session, localTarget, scaledMax, localRot, bitmap, proofStamp: proof.Stamp, monitorIdentity: monitorIdentity)", prefetch);
        Assert.Contains("KickOffAdjacentPrefetch(pageIndex, scaledMax, pgRot, primaryUsesCache, primaryMonitor, primaryMonitorIdentity);", viewport);
    }

    // viewer BF-008: RenderAllAnnotations clears every child of the page's canvas, link overlays included, so links
    // added to a Grid or Two-Page tile before its annotations were drawn were removed again.
    [Fact]
    public void TileLinkOverlaysAreAddedAfterTheTilesAnnotationsAreDrawn()
    {
        string viewport = Source("Controls", "Viewer", "PdfViewer.Viewport.cs");
        string tile = Between(viewport, "private void AddSecondaryTile(", "private void ScrollToGridTile(");

        Assert.Equal(3, Count(tile, "AddSecondaryPageLinks(pi, pageDipW, pageDipH);"));
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(tile,
            @"RenderAllAnnotations\(pi\);\s*AddSecondaryPageLinks\(pi, pageDipW, pageDipH\);").Count);
    }

    // viewer BF-013: a paginating book reads "340+" in the single-page badge; the Grid span badge printed the settled count.
    [Fact]
    public void TheGridSpanBadgeShowsTheSameProvisionalTotalAsTheSingleBadge()
    {
        string viewport = Source("Controls", "Viewer", "PdfViewer.Viewport.cs");
        string span = Between(viewport, "private void ShowPageBadgeSpan(", "private void ShowPageBadgeText(");

        Assert.Contains("$\"{first + 1} / {ReadPageTotal}\"", span);
        Assert.Contains("$\"{first + 1}-{last + 1} / {ReadPageTotal}\"", span);
        Assert.DoesNotContain("ReadPageCount}", span);
    }

    // viewer BF-005: a book has no working PDF (_doc), so the zoom status line kept its old text when zoom changed.
    [Fact]
    public void TheZoomStatusLineIsWrittenForBooksToo()
    {
        string viewport = Source("Controls", "Viewer", "PdfViewer.Viewport.cs");
        string setZoom = Between(viewport, "internal void SetZoom(double level)", "internal void SetTrueZoom(");
        string preset = Between(viewport, "SetTrueZoom(z);", "// A secondary tile shares the primary's displayed width.");

        Assert.Contains("if (HasReadDocument && _currentPage >= 0)", setZoom);
        Assert.Contains("_currentPage + 1, ReadPageTotal) + $\" - {DisplayZoomPct():F0}%\"", setZoom);
        Assert.DoesNotContain("_doc", setZoom);
        Assert.Contains("if (_currentPage >= 0 && HasReadDocument)", preset);
        Assert.Contains("_currentPage + 1, ReadPageTotal) + $\" - {DisplayZoomPct():F0}%\"", preset);
        Assert.DoesNotContain("_doc", preset);
    }

    // viewer BF-004: the Grid resize paths returned early for a book, whose reader is not a working PDF.
    [Fact]
    public void GridResizeKeepsItsColumnsForBooksToo()
    {
        string viewport = Source("Controls", "Viewer", "PdfViewer.Viewport.cs");
        string sizeChanged = Between(viewport, "private void PagePreviewPanelSizeChangedCore(", "// Coalesces resize ticks");
        string settled = Between(viewport, "private void OnResizeSettled()", "private int NearestContinuousPage(");

        Assert.Contains("if (!HasReadDocument || _gridColumns < 1) return;", sizeChanged);
        Assert.Contains("if (HasReadDocument && _gridColumns >= 1)", settled);
        Assert.DoesNotContain("_doc", sizeChanged);
        Assert.DoesNotContain("_doc", settled);
    }

    // viewer BF-007: every attached continuous page relaid out every slot, so a long document did work that grew with the
    // square of its length while it drew. The layout depends only on the slot heights, so it runs when one changed.
    [Fact]
    public void ContinuousSlotsAreRelaidOutOnlyWhenAPageChangesItsSlotHeight()
    {
        string viewport = Source("Controls", "Viewer", "PdfViewer.Viewport.cs");
        string slot = Between(viewport, "private void SetContinuousSlot(", "/// <summary>True when part of the page is in the document viewport now.");

        Assert.Equal(1, Count(slot, "LayoutContinuousSlots();"));
        Assert.Contains("if (Math.Abs(dipH - oldH) > 0.5) LayoutContinuousSlots();", slot);
    }

    // viewer BF-009: a book's Grid and Two-Page tile copied and scaled its pixels inside the UI thread callback, one tile
    // after another. The prepared bitmap is frozen, so the copy and the scaled bitmap are built on the render thread
    // and the callback only attaches the finished tile.
    [Fact]
    public void ABooksGridTileIsBuiltOffTheUiThread()
    {
        string viewport = Source("Controls", "Viewer", "PdfViewer.Viewport.cs");
        string native = Between(viewport, "PrepareNativePrimaryBitmap(nativeRevision, i, secondaryMax", "var rendered = await _viewportRenderQueue.Enqueue(");
        int invoke = native.IndexOf("Dispatcher.Invoke(", StringComparison.Ordinal);

        Assert.True(invoke > 0, "The tile is attached through the dispatcher.");
        Assert.InRange(native.IndexOf("source.CopyPixels(", StringComparison.Ordinal), 1, invoke);
        Assert.InRange(native.IndexOf("BitmapHelpers.BuildScaledBitmap(", StringComparison.Ordinal), 1, invoke);
        Assert.DoesNotContain("CopyPixels", native[invoke..]);
        Assert.DoesNotContain("BuildScaledBitmap", native[invoke..]);
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
