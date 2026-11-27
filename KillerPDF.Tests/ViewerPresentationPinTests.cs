using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// These are SOURCE PINS, not behavior tests. The viewer is not part of this test project and a viewer cannot be built
// here without a window: CapturePrimaryDisplay reads DisplayCapture only once the control has a window handle, so no
// window-free test reaches the display hook. Each pin names the exact expression a finding changed, and the finding's
// written window check (in the 1.9.3 review notes) is the behavior evidence.
public sealed class ViewerPresentationPinTests
{
    // VIEW-001 and #534: on an HDR or Windows automatic color management display the page is drawn in a native child
    // window. Everything the main window floats over the page (find bar, form size bar, page badge, notices, the floating
    // tool bar, signature hint and popup, drop indicator, full-window overlays) was hidden under it, so 1.9.3 turned the
    // window off. 1.9.7 turns it back on, with each of those marked as an occluder that the window's region cuts out.
    [Fact]
    public void TheNativeChildWindowCutsOutEverythingTheMainWindowFloatsOverThePage()
    {
        string presentation = Source("Controls", "Viewer", "PdfViewer.Presentation.cs");
        string viewport = Source("Controls", "Viewer", "PdfViewer.Viewport.cs");
        string host = Source("Services", "Presentation", "WindowsPdfViewportHost.cs");

        Assert.Contains("static readonly bool NativePrimaryViewportEnabled = true;", presentation);
        string linear = Between(viewport, "bool linear = ", ";");
        Assert.Contains("NativePrimaryViewportEnabled", linear);
        // The host is only built by the present step, which only runs for a frame the linear path produced.
        Assert.Equal(1, Count(presentation, "new WindowsPdfViewportHost("));
        Assert.Equal(1, Count(viewport, "await PresentPrimaryFrameAsync("));

        // The region the host installs has every visible occluder cut out, and it is cut again when one changes.
        Assert.Contains("ViewportOcclusion.Cut(WindowsPdfViewportLayout.Capture(this), holes)", host);
        Assert.Contains("ViewportOcclusion.Holes(this)", host);
        Assert.Contains("ViewportOcclusion.Changed += OnOcclusionChanged;", host);
        Assert.Contains("ViewportOcclusion.Changed -= OnOcclusionChanged;", host);
        // A cut shows the page's sRGB bitmap from the tile behind the host, not a blank tile.
        Assert.Contains("tile.Background = new ImageBrush(source) { Stretch = Stretch.Fill };", presentation);

        string mark = "ViewportOcclusion.SetOccludes(";
        Assert.Contains(mark + "PageBadge, true)", Source("Controls", "Viewer", "PdfViewer.xaml.cs"));
        Assert.Contains(mark + "_searchBar, true)", Source("Shell", "Search.cs"));
        Assert.Contains(mark + "bar, true)", Source("Controls", "Viewer", "PdfViewer.Forms.cs"));
        Assert.Contains(mark + "bar, true)", Source("Controls", "Viewer", "PdfViewer.Restriction.cs"));
        Assert.Contains(mark + "bar, true)", Source("Controls", "Viewer", "PdfViewer.NativeNotice.cs"));
        Assert.Contains(mark + "bar, true)", Source("Controls", "Viewer", "PdfViewer.SessionNotice.cs"));
        Assert.Contains(mark + "indicator, true)", Source("Controls", "Viewer", "PdfViewer.PageDrop.cs"));
        Assert.Contains(mark + "hint, true)", Source("Shell", "SignaturePlacement.cs"));
        Assert.Contains(mark + "_signaturePopup, true)", Source("Shell", "Signing.cs"));
        Assert.Contains(mark + "overlay, true)", Source("Shell", "DirtyTracking.cs"));
        Assert.Contains(mark + "overlay, true)", Source("Shell", "FileOperations.cs"));
        Assert.Contains(mark + "cover, true)", Source("Shell", "FullScreen.cs"));
        Assert.Contains(mark + "toast, true)", Source("Shell", "FullScreen.cs"));
        Assert.Contains("{ TabDragGhost, TabDropCaret, ShortcutsOverlay, AboutOverlay, WhatsNewOverlay }", Source("Shell", "MainWindow.xaml.cs"));
        string dock = Source("Controls", "ToolOptionsDock.cs");
        foreach (string part in new[] { "value", "_strip", "_grip", "_corners" })
            Assert.Contains(mark + part + ", true)", dock);
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
        string preset = Between(viewport, "SetTrueZoomAnchored(z);", "// A secondary tile shares the primary's displayed width.");

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

    // #534 sitting on SHREDDER: turning HDR off and on again with the native page window up froze the window and then
    // crashed inside the AMD driver (AMDXN64.DLL, 0xc0000005). The surface kept drawing through a factory and device made
    // for the old display. Before every frame the presenter now asks whether its surface is stale and builds a new one.
    [Fact]
    public void AFrameIsNeverDrawnIntoASurfaceMadeForTheOldDisplay()
    {
        string presenter = Source("Services", "Presentation", "WindowsPdfFramePresenter.cs");
        string transport = Source("Services", "Presentation", "WindowsPdfNativeTransport.cs");
        string stale = Between(transport, "internal bool IsStale", "internal void Resize(");

        Assert.Contains("if (_transport is { IsStale: true } stale) ReleaseTransport(stale);", presenter);
        Assert.True(presenter.IndexOf("IsStale: true", StringComparison.Ordinal)
            < presenter.IndexOf("var transport = _transport ??", StringComparison.Ordinal));
        Assert.Contains("Slot(_factory, 13))(_factory) == 0", stale);       // IDXGIFactory1::IsCurrent
        Assert.Contains("Slot(_device, 39))(_device) != 0", stale);         // ID3D11Device::GetDeviceRemovedReason
        Assert.Contains("Slot(_composition, 26))(_composition, &valid)", stale); // IDCompositionDevice::CheckDeviceState
        Assert.Contains("_device = device; _context = context;", transport);
        Assert.Contains("_factory = factory;", transport);
    }

    // #534 sitting on SHREDDER: leaving full screen on a desktop that mixes display scales put the window back far too
    // big. Restoring Left, Top, Width and Height in WPF units is what went wrong; the native placement goes back instead.
    [Fact]
    public void LeavingFullScreenPutsBackTheNativeWindowPlacement()
    {
        string fullScreen = Source("Shell", "FullScreen.cs");

        Assert.Contains("GetWindowPlacement(new WindowInteropHelper(this).Handle, ref _fsPrevPlacement)", fullScreen);
        Assert.Contains("SetWindowPlacement(new WindowInteropHelper(this).Handle, ref placement);", fullScreen);
        Assert.DoesNotContain("Width = _fsPrevW", fullScreen);
        Assert.True(fullScreen.IndexOf("GetWindowPlacement(new", StringComparison.Ordinal)
            < fullScreen.IndexOf("WindowState = WindowState.Normal;", StringComparison.Ordinal),
            "The placement has to be read before full screen changes the window state.");
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
