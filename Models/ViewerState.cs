using System.Collections.Generic;
using System.Windows.Controls;

namespace KillerPDF
{
    /// <summary>
    /// Everything ONE document view owns. Split pane needs two of these; each PdfViewer control
    /// owns one, and the window reads the active one back through its `_view` property, so the ~500
    /// call sites behind the window's forwarding properties are untouched.
    ///
    /// This is deliberately the PER-VIEW cut, not the per-document one. Per-document state
    /// (annotations, undo, form values, search hits) already travels in DocumentSession, which
    /// tab switching swaps by reference - see the comment above _annotations in
    /// MainWindow.xaml.cs. A second pane needs its own live visual maps and its own view mode
    /// and zoom; it does NOT need a second copy of the per-document machinery, because each
    /// pane will simply own its own set of sessions.
    ///
    /// TOP-LEVEL, not nested in MainWindow. The viewer lives in KillerPDF.Controls and cannot own a
    /// type nested in the window without every reference spelling out MainWindow.ViewerState.
    /// ViewMode and FitMode live in Models/ViewTypes.cs for the same reason.
    /// </summary>
    internal sealed class ViewerState
    {
            /// <summary>Unified page -> overlay map covering EVERY rendered page, the primary
            /// included. The single source of truth the canvas accessors read from.</summary>
            public readonly Dictionary<int, Canvas> Pages = [];

            /// <summary>Per-page overlay canvases for the multi-page tile systems (continuous
            /// overlays, or grid / two-page secondaries). Holds only secondary tiles and is driven
            /// by the tile-recycling machinery.</summary>
            public readonly Dictionary<int, Canvas> ContinuousCanvases = [];

            /// <summary>The page this view is showing (0-based; -1 = no document).
            ///
            /// This exists because reading the SIDEBAR's selected thumbnail,
            /// `PageList.SelectedIndex` (118 times across 24 files), works with one pane and cannot
            /// work with two - there is one sidebar and two current pages, so a viewer inside the
            /// control has nothing to ask.
            ///
            /// This is the storage; the sidebar FOLLOWS it. Kept in sync in exactly two places,
            /// which between them cover every write:
            ///   - PageList_SelectionChanged (PageSelection.cs) mirrors the sidebar back into here,
            ///     unconditionally and before its own >= 0 guard, so clearing the list to -1 (tab
            ///     close, document close) is mirrored too.
            ///   - SyncCurrentPageTo (Viewport.cs), which detaches that handler to avoid re-entry
            ///     and so would otherwise slip past the mirror.
            /// Everything that sets PageList.SelectedIndex directly still routes through the
            /// handler, so those need no change.
            ///
            /// The 118 call sites are deliberately NOT repointed at this field: the render pipeline
            /// switches over to reading it as it moves into the control.</summary>
            public int CurrentPage = -1;

            /// <summary>Current view mode for this view.</summary>
            public ViewMode Mode = ViewMode.Continuous;

            /// <summary>Mode a fade is transitioning to, if one is in flight. Reads that need the
            /// destination rather than the current mode use `Pending ?? Mode` - the fade takes
            /// ~90ms and Mode lags behind it, which is what made wheel-cycling need several
            /// notches before it was fixed.</summary>
            public ViewMode? Pending;

            // ── Zoom / fit ──────────────────────────────────────────────────────────────────
            public double ZoomLevel = 1.0;
            /// <summary>Zoom the current bitmaps were rasterized at, so the re-sharpen pass knows
            /// whether what is on screen is still crisp enough.</summary>
            public double LastRenderZoom = 1.0;
            /// <summary>Primary (spread-left) page currently rasterized.</summary>
            public int RenderedPrimaryPage = -1;
            public FitMode Fit = FitMode.None;

            // ── In-flight render work ───────────────────────────────────────────────────────
            // Each view cancels and reschedules its own rendering, so two panes must not share
            // these - one pane's mode switch would otherwise cancel the other's render.
            public System.Windows.Threading.DispatcherTimer? RerenderTimer;
            public System.Threading.CancellationTokenSource? SecondaryRenderCts;
            public System.Threading.CancellationTokenSource? ContinuousRenderCts;
            /// <summary>#85 visible-page re-sharpen.</summary>
            public System.Threading.CancellationTokenSource? ContinuousSharpenCts;

            // ── Continuous-view bookkeeping ─────────────────────────────────────────────────
            /// <summary>Slots currently holding a hi-res bitmap.</summary>
            public readonly HashSet<int> ContinuousSharpPages = [];
            /// <summary>Budget those slots were sharpened at.</summary>
            public int ContinuousSharpW;
            public readonly List<double> ContinuousTops = [];
            /// <summary>Page to scroll to once its grid tile streams in (-1 = none).</summary>
            public int GridScrollToPage = -1;
            /// <summary>Re-scroll here once its true height is known.</summary>
            public int ContinuousScrollTarget = -1;
            public double ContinuousPageW;

            // ── Gesture routing ─────────────────────────────────────────────────────────────
            /// <summary>The page surface a pointer gesture started on, captured on mouse-down.
            /// Kept separate from the active canvas because RenderAllAnnotations reuses that as its
            /// render target, and in Grid view tiles stream in asynchronously and re-point it
            /// mid-gesture - which committed annotations to the wrong page.</summary>
            public Canvas? GestureCanvas;
            public int GesturePage = -1;

            // ── Visual hosts ────────────────────────────────────────────────────────────────
            // References only - the window still creates and owns the actual elements. Today
            // ContinuousPanel / PageContentPanel / PageContentGrid come from FindName in the
            // window ctor and are the ONE window's XAML; AnnotationCanvas / PageImage are the
            // code-built primary tile (Viewport.BuildPrimaryTile) and ActiveCanvas is re-pointed
            // on mouse-down. Holding them here is what lets the next stage hand each viewer its
            // own tile tree without touching any of the ~250 call sites that use them.
            public StackPanel ContinuousPanel = null!;
            public WrapPanel PageContentPanel = null!;
            public Grid PageContentGrid = null!;
            /// <summary>The hardcoded primary tile's overlay, shown in Single/Grid/TwoPage.</summary>
            public Canvas AnnotationCanvas = null!;
            public Image PageImage = null!;
            /// <summary>Active annotation surface. Single view: always AnnotationCanvas.
            /// Continuous: set on mouse-down to the clicked page's overlay.</summary>
            public Canvas ActiveCanvas = null!;
    }
}
