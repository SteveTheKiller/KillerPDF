using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerPDF.Features;

namespace KillerPDF.Controls
{
    /// <summary>
    /// One document view: its tab strip, its card and everything inside. Two instances make the
    /// split.
    ///
    /// The handlers here are one-line forwards to the host, following KillerShell's FilePane idiom -
    /// chrome that belongs to the window stays on the window.
    /// </summary>
    public partial class PdfViewer : UserControl
    {
        /// <summary>The explicit shell boundary used by the viewer.</summary>
        internal IViewerHost? Host { get; private set; }

        /// <summary>Document dark-mode invert, PER PANE (was a global that flipped both panes of
        /// a split at once). Display only; every render path in this pane reads this flag. The
        /// moon toggles the focused pane and its lit state follows pane focus.</summary>
        internal bool DocInvert;

        internal void AttachHost(IViewerHost host) => Host = host;

        /// <summary>This viewer's per-view state - page maps, view mode, zoom, render cancellation,
        /// continuous bookkeeping. MainWindow's `_view` reads it back from here, so a second pane
        /// gets its own simply by existing.</summary>
        internal ViewerState State { get; } = new();

        public PdfViewer()
        {
            InitializeComponent();
            // ScrollViewer consumes manipulation events for one-finger panning. Listen even when
            // they are already marked handled so a two-finger scale can share that input surface.
            PagePreviewPanel.AddHandler(ManipulationStartingEvent,
                new EventHandler<ManipulationStartingEventArgs>(PagePreview_ManipulationStarting), true);
            PagePreviewPanel.AddHandler(ManipulationDeltaEvent,
                new EventHandler<ManipulationDeltaEventArgs>(PagePreview_ManipulationDelta), true);
            PagePreviewPanel.AddHandler(ManipulationCompletedEvent,
                new EventHandler<ManipulationCompletedEventArgs>(PagePreview_ManipulationCompleted), true);
        }

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
        /// The brush moves, never the thickness - a thickness change would reflow the pane on every
        /// click between panes.
        ///
        /// PaneHasFocus below records the same state for the tab halo, which continues the ring up
        /// around the active tab in the focused pane only.</summary>
        internal bool PaneHasFocus { get; private set; }

        internal void SetFocusHalo(bool focused)
        {
            PaneHasFocus = focused;
            bool retro = Services.ThemeManager.Current == Services.Theme.SE98;
            string key = focused && !retro ? "SelectionAccent" : "PaneBorderBrush";
            PaneBorder.SetResourceReference(Border.BorderBrushProperty, key);
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

        private void DocPane_SizeChanged(object s, SizeChangedEventArgs e)
        {
            Host?.ViewerSizeChanged(this, s, e);

            // Window resizing changes a pane's usable width without going through the split-pane
            // callbacks. Keep the empty-state recents panel on the same width gate in that path too.
            // SyncRecentBoxWidth only writes materially changed values and guards re-entry, so the
            // follow-up layout pass caused by crossing the threshold settles immediately.
            if (e.WidthChanged) SyncRecentBoxWidth();
        }

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
        private void DropZone_Drop(object s, DragEventArgs e) => Host?.ViewerDrop(this, s, e);
        private void DropZone_DragOver(object s, DragEventArgs e) => Host?.ViewerDragOver(this, s, e);
        private void DropZone_DragLeave(object s, DragEventArgs e) => HidePageImportDropIndicator();
        private void DropZone_Click(object s, MouseButtonEventArgs e) => Host?.ViewerDropZoneClick(s, e);

        private void RecentClearAll_Click(object s, MouseButtonEventArgs e) => Host?.ClearRecentFiles(s, e);

        // The five preview/scroll handlers do NOT forward from here: their bodies are in this class
        // (PdfViewer.Zoom.cs, PdfViewer.Viewport.cs), so a forward would call straight back into
        // itself. The XAML binds them directly.
        private void DocPaneBackground_RightClick(object s, MouseButtonEventArgs e) => Host?.ViewerBackgroundRightClick(s, e);

        /// <summary>Empty space on this pane's tab strip drags the window, the way a strip in the
        /// title-bar row would. Named apart from MainWindow's TitleBar_MouseLeftButtonDown, which
        /// keeps the body - it is window chrome, not pane behavior.</summary>
        private void TabScroll_MouseLeftButtonDown(object s, MouseButtonEventArgs e) => Host?.ViewerTabStripMouseDown(s, e);

        /// <summary>This pane's active document, for the window's chrome. The session list lives
        /// here, so the window asks the focused pane rather than owning one itself.</summary>
        internal DocumentSession? ActiveSessionRef => _active;

        /// <summary>Every open document in THIS pane. The quit prompt has to union both panes to
        /// decide whether anything is unsaved, and the settings writer needs each pane's list.</summary>
        internal System.Collections.ObjectModel.ObservableCollection<DocumentSession> SessionsRef => _sessions;
    }
}
