using KillerPDF.Shell;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    /// <summary>
    /// Transitional forwards used by the moved render pipeline while the remaining bridge surface
    /// is converted to IViewerHost and per-document state.
    ///
    /// WHY THIS FILE EXISTS. PdfViewer.Viewport.cs and PdfViewer.Zoom.cs were moved across VERBATIM -
    /// roughly 2,100 lines carrying about 700 references to window members spelled bare (PageList,
    /// _doc, Loc, RenderAllAnnotations...). Rewriting those 700 sites in the same change that moved
    /// the files would have been unreviewable. Declaring the names here instead means the moved
    /// files did not change a character of logic, and the entire coupling surface between viewer
    /// and window is one readable list.
    ///
    /// THIS IS SCAFFOLDING, NOT THE DESTINATION. Read the groups below as a to-do list:
    ///   - Group B is per-DOCUMENT state that belongs in DocumentSession. When the viewer holds
    ///     its own active session, that block deletes itself.
    ///   - Group C members live in files that have not moved into the viewer. Each deletes itself
    ///     as its defining file arrives; the defining file is named against every one.
    ///   - Group A is the only group meant to survive, and it should end up expressed as
    ///     IViewerHost rather than as raw Owner reach.
    ///
    /// Host is null only between construction and the window wiring it up, which happens in the
    /// MainWindow constructor before any of this can run. The null-forgiving operator is therefore
    /// deliberate: a null here is a wiring bug and should throw loudly, not render nothing.
    /// </summary>
    public partial class PdfViewer
    {
        // ── The view's own state ─────────────────────────────────────────────────────────────
        // Not forwards: this viewer OWNS its ViewerState (see PdfViewer.xaml.cs). These mirror the
        // window's forwarding properties one for one, so the moved code reads identically.
        private ViewMode _viewMode { get => State.Mode; set => State.Mode = value; }
        private ViewMode? _pendingViewMode { get => State.Pending; set => State.Pending = value; }
        private double _zoomLevel { get => State.ZoomLevel; set => State.ZoomLevel = value; }
        private double _lastRenderZoom { get => State.LastRenderZoom; set => State.LastRenderZoom = value; }
        private int _renderedPrimaryPage { get => State.RenderedPrimaryPage; set => State.RenderedPrimaryPage = value; }
        private FitMode _fitMode { get => State.Fit; set => State.Fit = value; }
        private System.Windows.Threading.DispatcherTimer? _rerenderTimer { get => State.RerenderTimer; set => State.RerenderTimer = value; }
        private System.Threading.CancellationTokenSource? _secondaryRenderCts { get => State.SecondaryRenderCts; set => State.SecondaryRenderCts = value; }
        private System.Threading.CancellationTokenSource? _continuousRenderCts { get => State.ContinuousRenderCts; set => State.ContinuousRenderCts = value; }
        private System.Threading.CancellationTokenSource? _continuousSharpenCts { get => State.ContinuousSharpenCts; set => State.ContinuousSharpenCts = value; }
        private HashSet<int> _continuousSharpPages => State.ContinuousSharpPages;
        private int _continuousSharpW { get => State.ContinuousSharpW; set => State.ContinuousSharpW = value; }
        private List<double> _continuousTops => State.ContinuousTops;
        private int _gridScrollToPage { get => State.GridScrollToPage; set => State.GridScrollToPage = value; }
        private int _continuousScrollTarget { get => State.ContinuousScrollTarget; set => State.ContinuousScrollTarget = value; }
        private double _continuousPageW { get => State.ContinuousPageW; set => State.ContinuousPageW = value; }
        private Dictionary<int, Canvas> _pages => State.Pages;
        private Dictionary<int, Canvas> _continuousCanvases => State.ContinuousCanvases;
        private Canvas _annotationCanvas { get => State.AnnotationCanvas; set => State.AnnotationCanvas = value; }
        private Canvas _activeCanvas { get => State.ActiveCanvas; set => State.ActiveCanvas = value; }
        private Canvas? _gestureCanvas { get => State.GestureCanvas; set => State.GestureCanvas = value; }
        private int _gesturePage { get => State.GesturePage; set => State.GesturePage = value; }
        private Image PageImage { get => State.PageImage; set => State.PageImage = value; }
        private Canvas _continuousPanel { get => State.ContinuousPanel; set => State.ContinuousPanel = value; }
        private WrapPanel _pageContentPanel { get => State.PageContentPanel; set => State.PageContentPanel = value; }
        private Grid _pageContentGrid { get => State.PageContentGrid; set => State.PageContentGrid = value; }
        private int _currentPage
        {
            get => State.CurrentPage;
            set
            {
                State.CurrentPage = value;
                Host?.ViewerPageChanged(this, value);
            }
        }

        // ── Group A: host chrome and services ────────────────────────────────────────────────
        // One toolbar, one sidebar, one status line serving both panes. These are the forwards
        // meant to survive, and they should become direct IViewerHost calls.

        private string Loc(string key) => Host!.Loc(key);
        private void SetStatus(string text) => Host!.SetStatus(text);
        private void RepositionAnnotationBars() => Host!.RepositionAnnotationBars();

        private EditTool _currentTool = EditTool.Select;
        private bool _fullScreen => Host!.FullScreen;
        private bool _vScrollVisible { get => Host!.VerticalScrollVisible; set => Host!.VerticalScrollVisible = value; }
        private bool _spaceHeld => Host!.SpaceHeld;

        // Zoom limits stay defined on the window: MainWindow.xaml.cs and KeyboardShortcuts.cs read
        // them too, and a const aliases at no cost rather than being duplicated.
        private const double ZoomMin  = MainWindow.ZoomMin;
        private const double ZoomMax  = MainWindow.ZoomMax;
        private const double ZoomStep = MainWindow.ZoomStep;

        // ── Group B: per-document state ──────────────────────────────────────────────────────
        // NOT host services. Every one of these already rides in DocumentSession, which tab
        // switching swaps by reference. They forward for now because the window still owns the
        // active session; when the viewer holds its own, this whole block goes.
        private PdfWorkingDocument? _doc;
        private string? _currentFile;
        // Settable: the tab switch rebinds all three by reference.
        private Dictionary<int, List<PageAnnotation>> _annotations = [];
        private Dictionary<int, (int w, int h)> _renderDims = [];
        private Dictionary<int, int> _pageRotations = [];
        // _active is NOT forwarded: the session list lives in this class, so this pane owns its own
        // active document. The window reads it back via ActiveSession.
        private readonly List<Canvas> _linkOverlays = [];
        // _continuousLinks is no longer forwarded - the field itself arrived with Links.cs and the
        // viewer owns it now. ContextMenu.cs and FileOperations.cs read it from the window side
        // through MainWindowViewerBridge's ContinuousLinksRef, which points back here.

        // Live gesture state shared with the annotation and crop tools, which have not moved yet.
        private bool _isPanning;
        private Point _panStart;
        private double _panScrollH;
        private double _panScrollV;
        private bool _isDrawing;
        private Point _drawStart;
        private UIElement? _activePreview;
        private bool _isSelecting;
        private Point _selectStart;
        private Rectangle? _selectRect;
        private int _cropPageIndex = -1;
        // Crop.cs and Annotations.cs ASSIGN both, so these go through the settable pair on the
        // window side rather than a get-only forward.
        private Rectangle? _cropPreviewRect;
        private Border? _cropConfirmBar;

        // ── Group C: methods in partials that have NOT moved yet ─────────────────────────────
        // Only four are left - the ones whose defining files stay on the window. RenderAllAnnotations,
        // ClearSelection, UpdateMarquee, IsDescendantOf, the four Canvas_Mouse* handlers,
        // ClearTextSelection, AccentBrush, RenderPageLinks, AddSecondaryPageLinks and the
        // PageList_SelectionChanged delegate are real members of this class now.
        private void PopulateContextMenu(Point pt, int page) => Host!.PopulateContextMenu(this, pt, page);
        private void RefreshPageList() => Host!.RefreshPageList(this);
        private void LoadOutlines() => Host!.LoadOutlines(this);
        private Cursor CursorForTool(EditTool t) => Host!.CursorForTool(t);

        // The render cache is not forwarded either - TryGetCachedRender / CacheRender are real
        // members of this class. They work per-pane unchanged: the cache is keyed
        // (page, bucket, rot) and both accessors take the session explicitly, so
        // two panes at different zooms simply occupy different buckets of their own session's cache.
    }
}
