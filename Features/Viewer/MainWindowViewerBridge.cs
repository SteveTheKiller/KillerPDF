using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PdfSharpCore.Pdf;

namespace KillerPDF
{
    /// <summary>
    /// The window's half of the viewer bridge. Read this alongside
    /// Controls/PdfViewer.Bridge.cs, which is the other end of every line here.
    ///
    /// TWO DIRECTIONS, and they are kept apart on purpose:
    ///
    ///   INWARD  - accessors the viewer reads. MainWindow's own members are private and a control
    ///             in another namespace cannot see them. Rather than widen ~40 fields in place and
    ///             scatter `internal` through fifteen files, each is exposed once, here, under a
    ///             name that says it is a bridge rather than an ordinary member. The private
    ///             fields stay private, so nothing else in the app gains reach by accident.
    ///
    ///   OUTWARD - stubs for the ~30 viewer methods that the rest of the window calls
    ///             (RenderPage from WindowChrome/SettingsPanel/PageSelection/Annotations,
    ///             SetViewMode from KeyboardShortcuts/RailFlyouts, and so on). Keeping the old
    ///             names resolvable meant roughly 60 call sites across 20 files were not touched
    ///             by this change at all.
    ///
    /// Both halves shrink as more owning code moves across; nothing here is meant to be permanent
    /// except what eventually becomes IViewerHost.
    /// </summary>
    public partial class MainWindow
    {
        // ══ INWARD: chrome the viewer reads ═════════════════════════════════════════════════
        // PageList is not here - x:Name fields are generated internal, so the control already
        // sees it. These four are hand-declared private fields assigned from FindName, so they
        // are not.
        internal ComboBox  ZoomBoxCtl        => _zoomBox;
        internal TextBox   PageJumpBoxCtl    => _pageJumpBox;
        internal Button    CloseFileBtnCtl   => _closeFileBtnRef;
        internal TextBlock PageTotalLabelCtl => _pageTotalLabel;

        internal string LocText(string key)   => Loc(key);
        internal void   SetStatusText(string text) => SetStatus(text);

        // Settable: ApplySessionState restores each document's remembered tool.
        internal EditTool CurrentToolValue { get => _currentTool; set => _currentTool = value; }
        internal bool FullScreenRef => _fullScreen;
        internal bool VScrollVisible { get => _vScrollVisible; set => _vScrollVisible = value; }
        internal bool SpaceHeld => _spaceHeld;

        /// <summary>The window's ONE PageList selection delegate. Handed out rather than rebuilt
        /// because SyncCurrentPageTo detaches and reattaches it - a method group would make a new
        /// delegate per call and the -= would quietly remove nothing.</summary>
        internal SelectionChangedEventHandler PageListSelectionHandler
            => _pageListSelectionHandler ??= PageList_SelectionChanged;
        private SelectionChangedEventHandler? _pageListSelectionHandler;

        // ══ INWARD: per-document state (group B - goes when the viewer owns its session) ═════
        // Settable: the xref-repair path in Annotations.cs, which lives in the viewer, reopens the
        // document and re-points the temp file.
        internal PdfDocument? DocRef { get => _doc; set => _doc = value; }
        internal string? CurrentFileRef { get => _currentFile; set => _currentFile = value; }
        // Settable: ApplySessionState, which lives in the viewer, swaps all three by reference on a
        // tab switch.
        internal Dictionary<int, List<PageAnnotation>> AnnotationsRef { get => _annotations; set => _annotations = value; }
        internal Dictionary<int, (int w, int h)> RenderDimsRef { get => _renderDims; set => _renderDims = value; }
        internal Dictionary<int, int> PageRotationsRef { get => _pageRotations; set => _pageRotations = value; }
        // Reads OUT of the focused pane rather than exposing a window field: the session list
        // belongs to the viewer. Callers still spell it `_active` - see the alias below.
        internal Controls.PdfViewer.DocumentSession? ActiveSession => ActiveViewer.ActiveSessionRef;
        private Controls.PdfViewer.DocumentSession? _active => ActiveViewer.ActiveSessionRef;
        internal List<Canvas> LinkOverlaysRef => _linkOverlays;
        // Reads OUT of the control rather than exposing a window field: the link-rect map belongs
        // to the viewer, alongside Links.cs. ContextMenu.cs and FileOperations.cs still call it by
        // this name.
        private Dictionary<int, List<LinkInfo>> _continuousLinks => ActiveViewer.ContinuousLinks;

        // Live gesture state, shared with the annotation and crop tools that have not moved yet.
        internal bool   IsPanning     { get => _isPanning;     set => _isPanning = value; }
        internal Point  PanStart      { get => _panStart;      set => _panStart = value; }
        internal double PanScrollH    { get => _panScrollH;    set => _panScrollH = value; }
        internal double PanScrollV    { get => _panScrollV;    set => _panScrollV = value; }
        internal bool   IsDrawing     { get => _isDrawing;     set => _isDrawing = value; }
        internal Point  DrawStart     { get => _drawStart;     set => _drawStart = value; }
        internal UIElement? ActivePreview { get => _activePreview; set => _activePreview = value; }
        internal bool   IsSelecting   { get => _isSelecting;   set => _isSelecting = value; }
        internal Point  SelectStart   { get => _selectStart;   set => _selectStart = value; }
        internal Rectangle? SelectRect { get => _selectRect;   set => _selectRect = value; }
        internal int    CropPageIndex { get => _cropPageIndex; set => _cropPageIndex = value; }
        internal Rectangle? CropPreviewRect => _cropPreviewRect;
        internal Border?    CropConfirmBar  => _cropConfirmBar;

        // ══ OUTWARD: the viewer's members, under the names the window already calls ══════════
        // Signatures mirror the originals exactly, defaults included, so no call site changed.
        private void RenderPage(int pageIndex, bool keepTiles = false) => ActiveViewer.RenderPage(pageIndex, keepTiles);
        private void SetupContinuousView(int initialPage, bool fitDefault = true) => ActiveViewer.SetupContinuousView(initialPage, fitDefault);
        private System.Threading.Tasks.Task RenderContinuousPages(int centerPage) => ActiveViewer.RenderContinuousPages(centerPage);
        private void BootstrapDocumentView(int initialPage, bool autoFit, bool restoreFitMode = false)
            => ActiveViewer.BootstrapDocumentView(initialPage, autoFit, restoreFitMode);
        private void RefreshPageView(int pageIndex) => ActiveViewer.RefreshPageView(pageIndex);
        private void ScrollContinuousToPage(int pageIndex) => ActiveViewer.ScrollContinuousToPage(pageIndex);

        private void ApplyZoom(bool lite = false) => ActiveViewer.ApplyZoom(lite);
        private void StartRerenderTimer() => ActiveViewer.StartRerenderTimer();
        private void SetZoom(double level) => ActiveViewer.SetZoom(level);
        private void SetTrueZoom(double trueZoom) => ActiveViewer.SetTrueZoom(trueZoom);
        private void GridZoomStep(bool zoomOut) => ActiveViewer.GridZoomStep(zoomOut);
        private double GridZoomForN(int n) => ActiveViewer.GridZoomForN(n);
        private double DisplayZoomPct() => ActiveViewer.DisplayZoomPct();
        private void SyncZoomBox() => ActiveViewer.SyncZoomBox();
        private void FitToWidth(bool lite = false) => ActiveViewer.FitToWidth(lite);
        private void FitToPage(bool lite = false) => ActiveViewer.FitToPage(lite);
        private void ReapplyGridOrFit() => ActiveViewer.ReapplyGridOrFit();

        private void SetViewMode(ViewMode mode) => ActiveViewer.SetViewMode(mode);
        private void SelectViewMode(ViewMode mode) => ActiveViewer.SelectViewMode(mode);
        private void ApplyViewMode(ViewMode mode) => ActiveViewer.ApplyViewMode(mode);
        private ViewMode? _pendingViewMode { get => ActiveViewer.PendingViewMode; set => ActiveViewer.PendingViewMode = value; }

        private bool NavigatePageStep(int direction) => ActiveViewer.NavigatePageStep(direction);
        private void NavigatePageByWheel(int delta) => ActiveViewer.NavigatePageByWheel(delta);

        private int _gridColumns { get => ActiveViewer.GridColumns; set => ActiveViewer.GridColumns = value; }

        private void BuildPrimaryTile() => ActiveViewer.BuildPrimaryTile();
        private void PagePreviewPanel_SizeChanged(object sender, SizeChangedEventArgs e)
            => ActiveViewer.PagePreviewPanel_SizeChanged(sender, e);

        // Bound from MainWindow.xaml (the zoom toolbar stays on the window) and from
        // ContextMenu.cs, so these three cannot simply live on the control.
        private void ZoomIn_Click(object sender, RoutedEventArgs e) => ActiveViewer.ZoomIn_Click(sender, e);
        private void ZoomOut_Click(object sender, RoutedEventArgs e) => ActiveViewer.ZoomOut_Click(sender, e);

        // ACTIVEVIEWER, like every other stub in this file - this one said `Viewer` (pane A,
        // hardcoded) and was the split's cross-zoom bug: FocusPane(B) -> SyncZoomBox writes the
        // shared box -> SelectionChanged -> this stub ran PANE A's handler, which FitToWidth'd
        // pane A against pane B's document (proven by zoomtrace, 2026-08-01).
        // NULL-CONDITIONAL, and it must stay that way. ZoomBox declares
        // <ComboBoxItem Tag="1.0" IsSelected="True"> (MainWindow.xaml), so SelectionChanged fires
        // while InitializeComponent is still walking the tree - ActiveViewer is not assigned until
        // InitSplitPanes runs after it, so the `?.` no-ops the mid-parse fire exactly like the old
        // `_zoomBox?.SelectedItem` guard did. Click handlers do not need this - a click cannot
        // happen mid-parse.
        private void ZoomBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => ActiveViewer?.ZoomBox_SelectionChanged(sender, e);
    }
}
