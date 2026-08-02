using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KillerPDF.Controls
{
    /// <summary>
    /// One document view: its tab strip, its card and everything inside. Two instances make the
    /// split.
    ///
    /// The handlers here are one-line forwards to Owner, following KillerShell's FilePane idiom -
    /// chrome that belongs to the window stays on the window.
    /// </summary>
    public partial class PdfViewer : UserControl
    {
        /// <summary>The window that owns this viewer. Set immediately after construction; every
        /// handler below is inert until it is.</summary>
        internal MainWindow? Owner { get; set; }

        /// <summary>This viewer's per-view state - page maps, view mode, zoom, render cancellation,
        /// continuous bookkeeping. MainWindow's `_view` reads it back from here, so a second pane
        /// gets its own simply by existing.</summary>
        internal ViewerState State { get; } = new();

        public PdfViewer() => InitializeComponent();

        /// <summary>
        /// Build this pane's tile tree. Every pane must do this for itself: routed through
        /// ActiveViewer it would run twice on pane A and leave pane B's State.AnnotationCanvas
        /// null, which AllPageCanvases() yields first and the first text-selection repaint then
        /// dereferences. The panel references must be assigned before BuildPrimaryTile, which
        /// inserts into PageContentPanel.
        /// </summary>
        internal void InitTiles()
        {
            State.PageContentGrid  = PageContentGrid;
            State.PageContentPanel = PageContentPanel;
            State.ContinuousPanel  = ContinuousPanel;
            BuildPrimaryTile();
            State.ActiveCanvas = State.AnnotationCanvas;
        }

        /// <summary>Accent ring marking this pane as the focused one in a split.
        ///
        /// SetResourceReference, not a brush snapshot, on both states: an assigned brush would not
        /// follow a live theme switch.
        ///
        /// "SelectionAccent", not "AccentBrush". KillerPDF uses the older family resource set
        /// (BgCanvas / AccentLogo / TextPrimary) and has no AccentBrush key in any theme.
        /// SetResourceReference to a missing key does not throw, it silently leaves the property
        /// unset, which blanks the border instead of accenting it.
        ///
        /// Both borders, matching KillerShell's UpdatePaneFocusRing: TabBarRing is the card's top
        /// border drawn again inside the tab band, so lighting only the card leaves the ring open
        /// along its whole top edge.
        ///
        /// The brush moves, never the thickness - a thickness change would reflow the pane on every
        /// click between panes.
        ///
        /// PaneHasFocus below records the same state for the tab halo, which continues the ring up
        /// around the active tab in the focused pane only.</summary>
        internal bool PaneHasFocus { get; private set; }

        internal void SetFocusHalo(bool focused)
        {
            PaneHasFocus = focused;
            string key = focused ? "SelectionAccent" : "PaneBorderBrush";
            PaneBorder.SetResourceReference(Border.BorderBrushProperty, key);
            TabBarRing.SetResourceReference(Border.BorderBrushProperty, key);
            // The ring runs on around the active tab, so it moves with the pane border. The tab's own
            // share of that is a template trigger on PaneFocused / PaneDimmed, which this sets, plus
            // the band-drawn outer verticals.
            UpdatePaneFocusRing();
        }

        // ---- Element access for the window --------------------------------------------------
        // A UserControl is its OWN NAMESCOPE, so the window's FindName cannot reach any of these -
        // and it fails SILENTLY, returning null rather than throwing. The window ctor assigns
        // _view.X from these instead, which works because those fields are forwarding properties
        // onto ViewerState.
        internal Border PaneShadowBorder => PaneShadow;
        internal Border PaneCardBorder => PaneBorder;
        internal Grid ContentHost => DocPaneContent;
        internal StackPanel ContinuousHost => ContinuousPanel;
        internal WrapPanel PageHost => PageContentPanel;
        internal Grid PageGrid => PageContentGrid;
        internal ScrollViewer PreviewScroller => PagePreviewPanel;
        internal Border DropSurface => DropZone;
        internal Border RecentBox => RecentFilesBox;
        internal ItemsControl RecentList => RecentFilesList;
        internal Canvas Marquee => MarqueeLayer;
        internal Border SurfacePad => DocSurfacePad;
        internal System.Windows.Media.ImageBrush Grain => GrainBrush;

        // ---- Forwards to the owning window ----------------------------------------------------
        // MainWindow's copies were private; they are internal now purely so these can reach them.

        // No SyncRecentBoxWidth from here. It writes Width and Visibility, both of which re-run
        // layout, and this IS a layout event - it fed itself. The recents panel is sized when it is
        // populated and when the split opens, which is every time its width can actually change.
        private void DocPane_SizeChanged(object s, SizeChangedEventArgs e)
            => Owner?.DocPane_SizeChanged(s, e);

        /// <summary>Size the start screen's Recent panel to this pane, and drop it entirely once the
        /// pane is too narrow to carry both it and the drop target. At its old fixed 340 it took
        /// most of a half-width pane and left the "Drop PDF here" zone as a sliver. Owns the
        /// panel's visibility outright, so PopulateRecentFilesList defers to it rather than the two
        /// of them setting it from different rules.</summary>
        private bool _syncingRecentBox;
        internal void SyncRecentBoxWidth()
        {
            if (RecentFilesBox is null || RecentFilesList is null || _syncingRecentBox) return;
            _syncingRecentBox = true;
            try
            {
                double w = ActualWidth;
                double want = Math.Min(340, Math.Max(220, w * 0.4));
                var vis = RecentFilesList.Items.Count > 0 && w >= 560
                    ? Visibility.Visible : Visibility.Collapsed;
                // Only write when the value actually changes. Both of these re-trigger layout, and
                // this runs FROM a size handler - an unconditional assignment gives the layout pass
                // something new to react to every time round and it never settles.
                if (Math.Abs(RecentFilesBox.Width - want) > 0.5) RecentFilesBox.Width = want;
                if (RecentFilesBox.Visibility != vis) RecentFilesBox.Visibility = vis;
            }
            finally { _syncingRecentBox = false; }
        }

        // Focus THIS pane before forwarding: the open path routes through ActiveViewer, and a
        // drag-drop raises no PreviewMouseDown (the focus trigger), so a drop on the unfocused
        // pane opened the file in the OTHER pane. FocusPane is cheap and idempotent.
        private void DropZone_Drop(object s, DragEventArgs e) { Owner?.FocusPane(this); Owner?.DropZone_Drop(s, e); }
        private void DropZone_DragOver(object s, DragEventArgs e) => Owner?.DropZone_DragOver(s, e);
        private void DropZone_Click(object s, MouseButtonEventArgs e) => Owner?.DropZone_Click(s, e);

        private void RecentClearAll_Click(object s, MouseButtonEventArgs e) => Owner?.RecentClearAll_Click(s, e);

        // The five preview/scroll handlers do NOT forward from here: their bodies are in this class
        // (PdfViewer.Zoom.cs, PdfViewer.Viewport.cs), so a forward would call straight back into
        // itself. The XAML binds them directly.
        private void DocPaneBackground_RightClick(object s, MouseButtonEventArgs e) => Owner?.DocPaneBackground_RightClick(s, e);

        /// <summary>Empty space on this pane's tab strip drags the window, the way a strip in the
        /// title-bar row would. Named apart from MainWindow's TitleBar_MouseLeftButtonDown, which
        /// keeps the body - it is window chrome, not pane behavior.</summary>
        private void TabScroll_MouseLeftButtonDown(object s, MouseButtonEventArgs e) => Owner?.TitleBar_MouseLeftButtonDown(s, e);

        /// <summary>This pane's active document, for the window's chrome. The session list lives
        /// here, so the window asks the focused pane rather than owning one itself.</summary>
        internal DocumentSession? ActiveSessionRef => _active;

        /// <summary>Every open document in THIS pane. The quit prompt has to union both panes to
        /// decide whether anything is unsaved, and the settings writer needs each pane's list.</summary>
        internal System.Collections.ObjectModel.ObservableCollection<DocumentSession> SessionsRef => _sessions;
    }
}
