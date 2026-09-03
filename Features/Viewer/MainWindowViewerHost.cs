using KillerPDF.Features;
using KillerPDF.Controls;
using System.Windows;
using System.Windows.Input;
using System;
using System.Linq;

namespace KillerPDF
{
    // MainWindow's half of IViewerHost.
    //
    // Explicit implementation throughout, matching Shell/About.cs: MainWindow's own members are
    // private, and a private member cannot implement an interface. Forwarding explicitly satisfies
    // the contract without widening anything to public - a change nobody asked for.
    //
    // IShellServices (Window / Loc / SetStatus) is already implemented in Shell/About.cs and is
    // inherited through IViewerHost, so it is deliberately NOT repeated here.
    public partial class MainWindow : IViewerHost
    {
        void IViewerHost.MarkDirty(bool dirty) => MarkDirty(dirty);

        void IViewerHost.SetTool(EditTool tool) => SetTool(tool);
        bool IViewerHost.SidebarShowingOutlines => _sidebarShowingOutlines;
        void IViewerHost.PopulateRecentFilesList(PdfViewer viewer) => PopulateRecentFilesList(viewer);
        void IViewerHost.SwitchSidebarToPagesTab() => SwitchSidebarToPagesTab();
        void IViewerHost.SyncSidebarToDocState(bool hasDoc, bool startup)
            => SyncSidebarToDocState(hasDoc, startup);
        void IViewerHost.OpenFile(string path) => OpenFile(path);
        void IViewerHost.UpdateFooterFade() => UpdateFooterFade();
        void IViewerHost.UpdateTabStripFade() => UpdateTabStripFade();
        System.Windows.Controls.Border? IViewerHost.SearchBar => _searchBar;
        Features.SearchController IViewerHost.Search => Search;
        System.Windows.Controls.TextBlock IViewerHost.FileNameLabel => FileNameLabel;
        System.Windows.Controls.TreeView IViewerHost.OutlineTree => OutlineTree;
        System.Windows.Controls.Button IViewerHost.SidebarOutlinesTab => SidebarOutlinesTab;
        System.Windows.Controls.TextBlock IViewerHost.StatusText => StatusText;
        FrameworkElement IViewerHost.ShortcutOverlay => ShortcutOverlay;
        System.Windows.Controls.CheckBox IViewerHost.LinkConfirmCheck => LinkConfirmCheck;
        System.Windows.Controls.ContextMenu IViewerHost.MakeThemedMenu() => MakeThemedMenu();
        void IViewerHost.CloseSearchBar() => CloseSearchBar();
        void IViewerHost.HideSignaturePopup() => HideSignaturePopup();
        void IViewerHost.SaveTempAndReload(bool keepAnnotations, bool preserveZoom,
            Action<string>? finalizeSavedFile, Action<Dictionary<int, int>>? remapRotations,
            int? selectedPageAfterReload, UndoEntry? documentUndo, bool preserveRenderedPages)
            => SaveTempAndReload(keepAnnotations, preserveZoom, finalizeSavedFile,
                remapRotations, selectedPageAfterReload, documentUndo, preserveRenderedPages);
        void IViewerHost.RecordNavJump() => RecordNavJump();
        PageAnnotation? IViewerHost.PairPartner(PageAnnotation annotation) => PairPartner(annotation);
        void IViewerHost.RenderStamps(int page) => RenderStamps(page);
        void IViewerHost.OpenStampTool() => OpenStampTool();
        bool IViewerHost.StampHitTest(int page, Point position) => StampHitTest(page, position);
        void IViewerHost.ApplySearchHighlights(int page, System.Windows.Controls.Canvas canvas)
            => ApplySearchHighlights(page, canvas);
        void IViewerHost.HighlightSearchResultsOnCurrentPage() => HighlightSearchResultsOnCurrentPage();
        void IViewerHost.ShowTextSettings() => ShowTextSettings();
        void IViewerHost.HideTextSettings() => HideTextSettings();
        void IViewerHost.StyleEditBox(System.Windows.Controls.TextBox textBox) => StyleEditBox(textBox);
        void IViewerHost.ApplyTextStyleToSelection() => ApplyTextStyleToSelection();
        void IViewerHost.ShowDrawSettings(EditTool tool) => ShowDrawSettings(tool);
        void IViewerHost.HideDrawSettings() => HideDrawSettings();
        System.Windows.Controls.Border IViewerHost.MakeBarGrip(int dotCount) => MakeBarGrip(dotCount);
        FrameworkElement IViewerHost.BuildBarHost(FrameworkElement content) => BuildBarHost(content);
        void IViewerHost.PlaceAnnotationBar(System.Windows.Controls.Border bar,
            System.Windows.Controls.Border grip, bool fadeIn) => PlaceAnnotationBar(bar, grip, fadeIn);
        void IViewerHost.PlaceImageFromDialog(Point position, int pageIndex)
            => PlaceImageFromDialog(position, pageIndex);
        void IViewerHost.PlaceSignature(Point position, int pageIndex) => PlaceSignature(position, pageIndex);
        void IViewerHost.ShowSignaturePopup() => ShowSignaturePopup();
        void IViewerHost.FillSignField(bool initials, int objectNumber, int pageIndex,
            double x, double y, double width, double height)
            => FillSignField(initials, objectNumber, pageIndex, x, y, width, height);
        void IViewerHost.ShapeToolMouseDown(int pageIndex, Point position, MouseButtonEventArgs e)
            => ShapeToolMouseDown(pageIndex, position, e);
        void IViewerHost.CommitShapeDrag(int pageIndex) => CommitShapeDrag(pageIndex);
        void IViewerHost.UpdateShapePolyRubber(MouseEventArgs e) => UpdateShapePolyRubber(e);
        void IViewerHost.OcrRegion(int pageIndex, Rect canvasBounds) => OcrRegion(pageIndex, canvasBounds);
        void IViewerHost.ShowShortcutsOverlayExclusive() => ShowShortcutsOverlayExclusive();
        System.Windows.Media.SolidColorBrush IViewerHost.SwatchDimBorder => _swatchDimBorder;
        PageAnnotation? IViewerHost.CloneAnnotation(PageAnnotation annotation) => CloneAnnotation(annotation);
        System.Windows.TextDecorationCollection? IViewerHost.BuildDecorations(bool underline, bool strike)
            => BuildDecorations(underline, strike);
        System.Windows.Media.Effects.DropShadowEffect IViewerHost.AnnotBarShadow() => AnnotBarShadow();
        void IViewerHost.FadeOverlayOut(UIElement element) => FadeOverlayOut(element);
        void IViewerHost.FadeOutAndRemoveBar(System.Windows.Controls.Border? bar) => FadeOutAndRemoveBar(bar);
        string IViewerHost.WordsToText(System.Collections.Generic.IEnumerable<UglyToad.PdfPig.Content.Word> words)
            => WordsToText(words);
        System.Windows.Controls.MenuItem IViewerHost.MakeMenuItem(string header, RoutedEventHandler click,
            string? gesture, string? glyph) => MakeMenuItem(header, click, gesture, glyph);

        bool IViewerHost.FullScreen => _fullScreen;
        bool IViewerHost.VerticalScrollVisible
        {
            get => _vScrollVisible;
            set => _vScrollVisible = value;
        }
        bool IViewerHost.SpaceHeld => _spaceHeld;
        void IViewerHost.RepositionAnnotationBars() => RepositionAnnotationBars();
        void IViewerHost.PopulateContextMenu(PdfViewer viewer, Point point, int pageIndex)
            => ((IViewerHost)this).RunWithViewerContext(viewer, () => PopulateContextMenu(point, pageIndex));
        void IViewerHost.RefreshPageList(PdfViewer viewer)
            => ((IViewerHost)this).RunWithViewerContext(viewer, RefreshPageList);
        void IViewerHost.LoadOutlines(PdfViewer viewer)
            => ((IViewerHost)this).RunWithViewerContext(viewer, LoadOutlines);
        Cursor IViewerHost.CursorForTool(EditTool tool) => CursorForTool(tool);

        // ---- Focused-viewer notifications ----------------------------------------------------
        // Single pane today, so these just drive the existing chrome directly. When there are two,
        // each body gains a "is the caller the focused viewer?" guard - the sidebar, page list and
        // status line follow focus rather than whichever pane happened to update last. Keeping the
        // calls routed through here means that guard lands in three known places instead of being
        // hunted through 84 PageList call sites. (BACKLOG.md, group C.)

        void IViewerHost.ViewerPageChanged(PdfViewer viewer, int pageIndex)
        {
            ComparisonPageChanged(viewer, pageIndex);
            if (!ReferenceEquals(ActiveViewer, viewer)) return;
            UpdatePageSizeDisplay();
            // Direct assignment, because that is what the 84 existing call sites do - there is no
            // SyncPageListSelection helper today. The guard avoids re-entering the selection
            // handler when the list already agrees.
            if (pageIndex < 0 || PageList is null) return;
            if (PageList.SelectedIndex != pageIndex) PageList.SelectedIndex = pageIndex;
        }

        private bool? FooterPageSizeMetric => App.GetSetting("FooterPageSizeUnit") switch
        {
            "Metric" => true,
            "Imperial" => false,
            _ => null
        };

        private void PageSizeLabel_Click(object sender, RoutedEventArgs e)
        {
            var size = ActiveViewer?.CurrentPageSizeExt(FooterPageSizeMetric);
            if (size is null) return;
            App.SetSetting("FooterPageSizeUnit", size.Value.Metric ? "Imperial" : "Metric");
            UpdatePageSizeDisplay();
        }

        private void UpdatePageSizeDisplay()
        {
            var size = ActiveViewer?.CurrentPageSizeExt(FooterPageSizeMetric);
            PageSizeLabel.Content = size?.Label ?? string.Empty;
            PageSizeLabel.ToolTip = size?.Details;
            PageSizeLabel.IsEnabled = size is not null;
        }

        void IViewerHost.EnsureSidebarPageVisible(PdfViewer viewer, int pageIndex)
        {
            if (!ReferenceEquals(ActiveViewer, viewer) || pageIndex < 0 || pageIndex >= PageList.Items.Count) return;
            PageList.ScrollIntoView(PageList.Items[pageIndex]);
        }

        void IViewerHost.ScrollSidebar(PdfViewer viewer, double delta)
        {
            if (!ReferenceEquals(ActiveViewer, viewer)) return;
            var scroller = FindSidebarDescendant<System.Windows.Controls.ScrollViewer>(PageList);
            scroller?.ScrollToVerticalOffset(scroller.VerticalOffset + delta);
        }

        void IViewerHost.ClearSidebarPages(PdfViewer viewer)
        {
            if (ReferenceEquals(ActiveViewer, viewer)) PageList.ItemsSource = null;
        }

        string IViewerHost.PageJumpText { get => _pageJumpBox.Text; set => _pageJumpBox.Text = value; }
        bool IViewerHost.PageJumpEnabled { set => _pageJumpBox.IsEnabled = value; }
        bool IViewerHost.CloseFileEnabled { set => _closeFileBtnRef.IsEnabled = value; }
        string IViewerHost.PageTotalText { set => _pageTotalLabel.Text = value; }
        void IViewerHost.SelectAllPageJumpText() => _pageJumpBox.SelectAll();

        void IViewerHost.SyncZoomDisplay(string? fitTag, string displayText)
        {
            if (fitTag != null)
                foreach (System.Windows.Controls.ComboBoxItem item in _zoomBox.Items)
                    if (item.Tag?.ToString() == fitTag) { _zoomBox.SelectedItem = item; return; }
            foreach (System.Windows.Controls.ComboBoxItem item in _zoomBox.Items)
                if (item.Content?.ToString() == displayText) { _zoomBox.SelectedItem = item; return; }
            _zoomBox.SelectedItem = null;
            _zoomBox.Text = displayText;
        }

        string? IViewerHost.SelectedZoomTag
            => (_zoomBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();

        void IViewerHost.CollapseZoomTextSelection()
        {
            if (_zoomBox.Template?.FindName("PART_EditableTextBox", _zoomBox) is System.Windows.Controls.TextBox box)
                box.Select(box.Text.Length, 0);
        }

        // The host contract reports the new value, while comparison reads the true visible zoom
        // from the viewer so fitted modes remain synchronized correctly.
        void IViewerHost.ViewerZoomChanged(PdfViewer viewer, double _)
            => ComparisonZoomChanged(viewer);

        void IViewerHost.ViewerScrolled(PdfViewer viewer, double horizontalRatio, double verticalRatio)
            => ComparisonScrolled(viewer, horizontalRatio, verticalRatio);

        void IViewerHost.ViewerFocused()
        {
            // Nothing to do while there is one viewer. With two panes this moves the accent halo
            // here and repoints the sidebar at the caller.
        }

        void IViewerHost.ViewerSizeChanged(PdfViewer viewer, object sender, SizeChangedEventArgs e)
            => DocPane_SizeChanged(sender, e);

        void IViewerHost.ViewerDrop(PdfViewer viewer, object _, DragEventArgs e)
        {
            DropZone_Drop(viewer, e);
        }

        void IViewerHost.ViewerDragOver(PdfViewer viewer, object _, DragEventArgs e)
            => DropZone_DragOver(viewer, e);

        void IViewerHost.ViewerDropZoneClick(object sender, MouseButtonEventArgs e)
            => DropZone_Click(sender, e);

        void IViewerHost.ClearRecentFiles(object sender, MouseButtonEventArgs e)
            => RecentClearAll_Click(sender, e);

        void IViewerHost.ViewerBackgroundRightClick(object sender, MouseButtonEventArgs e)
            => DocPaneBackground_RightClick(sender, e);

        void IViewerHost.ViewerTabStripMouseDown(object sender, MouseButtonEventArgs e)
            => TitleBar_MouseLeftButtonDown(sender, e);

        bool IViewerHost.IsViewerFocused(PdfViewer viewer)
            => ReferenceEquals(ActiveViewer, viewer);

        bool IViewerHost.IsSplitView => _isSplit;

        void IViewerHost.FocusViewer(PdfViewer viewer) => FocusPane(viewer);

        bool IViewerHost.OtherViewerHasFile(PdfViewer viewer, string? originalFile)
        {
            if (string.IsNullOrEmpty(originalFile)) return false;
            var other = ReferenceEquals(Viewer, viewer) ? ViewerB : Viewer;
            return other.SessionsRef.Any(x => (x.Doc != null || x.DeferredPath != null)
                && string.Equals(x.OriginalFile, originalFile, StringComparison.OrdinalIgnoreCase));
        }

        PdfViewer? IViewerHost.TabDropTarget(PdfViewer source, MouseEventArgs e)
            => TabDropTargetPane(source, e);

        void IViewerHost.UpdateTabDragFeedback(PdfViewer source,
            PdfViewer.DocumentSession session, MouseEventArgs e, PdfViewer? target)
            => UpdateTabDragFeedback(source, session, e, target);

        void IViewerHost.HideTabDragFeedback() => HideTabDragFeedback();

        void IViewerHost.MoveTabToPane(PdfViewer source, PdfViewer target,
            PdfViewer.DocumentSession session, MouseEventArgs e)
            => MoveTabToPane(source, target, session, e);

        void IViewerHost.RunWithViewerContext(PdfViewer viewer, Action work)
        {
            var focused = ActiveViewer;
            focused.CaptureActiveIfAny();
            var previous = SwapActiveViewer(viewer);
            try { work(); }
            finally
            {
                SwapActiveViewer(previous);
                focused.RestoreActiveFieldsOnly();
            }
        }
    }
}
