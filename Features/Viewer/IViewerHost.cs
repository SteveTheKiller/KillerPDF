using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using KillerPDF.Controls;

namespace KillerPDF.Features
{
    /// <summary>
    /// What a document viewer needs from the window around it.
    ///
    /// EXTENDS IShellServices, per the family rule in that file - the shell implements Window /
    /// Loc / SetStatus once, not once per feature. Those three cover the viewer's two heaviest
    /// call groups on their own (Loc 70 uses, SetStatus 68) plus the modal-dialog owner that
    /// TextEditing and Links need for KillerDialog (3 uses).
    ///
    /// DERIVED FROM MEASUREMENT, not guessed: every member here came from grepping what the nine
    /// files bound for the viewer control (Viewport, Zoom, Annotations, Selection, TextEditing,
    /// Crop, Links, Forms, PageSelection) actually reach for on MainWindow today. Use counts are
    /// noted per member so the cost of each is visible.
    ///
    /// That audit split the coupling into three groups, and only the first belongs here:
    ///
    ///  A. HOST SERVICES - chrome and app-level services the viewer asks for. This interface.
    ///
    ///  B. PER-DOCUMENT STATE - _doc (120 uses), _currentFile (33), _annotations (56),
    ///     _renderDims (34), _pageRotations (11), _undoStack (4). These are NOT host services:
    ///     they already ride in DocumentSession, which tab switching swaps by reference. The
    ///     viewer will hold its own active session and read them from it. Routing them through
    ///     the host would be a mistake that undoes the session design.
    ///
    ///  C. PageList - 84 uses, the single biggest coupling, and a DESIGN DECISION rather than a
    ///     mechanical one. The sidebar's page-thumbnail list is window chrome, but the viewer
    ///     drives it constantly (selection sync, scroll-to-page). With two panes there is still
    ///     ONE sidebar, so it has to follow the FOCUSED pane. The viewer therefore must not touch
    ///     PageList directly; it raises the notifications below and the window decides whether
    ///     this viewer is the focused one before acting. Getting this wrong is how the two panes
    ///     would end up fighting over the sidebar.
    /// </summary>
    internal interface IViewerHost : IShellServices
    {
        // ── Chrome the viewer updates (group A) ─────────────────────────────────────────────
        /// <summary>Mark the active document dirty (unsaved changes). 32 uses. Stays a host
        /// service even though dirtiness is per-document, because it also drives window chrome -
        /// the tab's dirty dot and the title bar.</summary>
        void MarkDirty(bool dirty = true);

        // PushUndo is deliberately NOT here, despite 9 uses. Undo is per-document state (group B):
        // _undoStack rides in DocumentSession, so the viewer pushes onto the session it is showing
        // rather than asking the window. It was in this interface briefly and the compiler caught
        // it - UndoEntry is a private nested record struct on MainWindow, and widening it plus its
        // UndoKind enum just to satisfy the signature would have been the wrong fix for a member
        // that should not have been here. (2026-08-01.)

        /// <summary>Switch tools - Crop uses this to drop back to Select when it finishes. 2 uses.</summary>
        void SetTool(EditTool tool);
        bool SidebarShowingOutlines { get; }
        void PopulateRecentFilesList(PdfViewer viewer);
        void SwitchSidebarToPagesTab();
        void SyncSidebarToDocState(bool hasDoc, bool startup);
        void OpenFile(string path);
        void UpdateFooterFade();
        void UpdateTabStripFade();
        Border? SearchBar { get; }
        SearchController Search { get; }
        TextBlock FileNameLabel { get; }
        TreeView OutlineTree { get; }
        Button SidebarOutlinesTab { get; }
        TextBlock StatusText { get; }
        FrameworkElement ShortcutOverlay { get; }
        CheckBox LinkConfirmCheck { get; }
        ContextMenu MakeThemedMenu();
        void CloseSearchBar();
        void HideSignaturePopup();
        void SaveTempAndReload(bool keepAnnotations, bool preserveZoom,
            Action<string>? finalizeSavedFile = null,
            Action<Dictionary<int, int>>? remapRotations = null,
            int? selectedPageAfterReload = null,
            UndoEntry? documentUndo = null,
            bool preserveRenderedPages = false);
        void RecordNavJump();
        PageAnnotation? PairPartner(PageAnnotation annotation);
        void RenderStamps(int page);
        void OpenStampTool();
        bool StampHitTest(int page, Point position);
        void ApplySearchHighlights(int page, Canvas canvas);
        void HighlightSearchResultsOnCurrentPage();
        void ShowTextSettings();
        void HideTextSettings();
        void StyleEditBox(TextBox textBox);
        void ApplyTextStyleToSelection();
        void ShowDrawSettings(EditTool tool);
        void HideDrawSettings();
        Border MakeBarGrip(int dotCount);
        FrameworkElement BuildBarHost(FrameworkElement content);
        void PlaceAnnotationBar(Border bar, Border grip, bool fadeIn);
        void PlaceImageFromDialog(Point position, int pageIndex);
        void PlaceSignature(Point position, int pageIndex);
        void ShowSignaturePopup();
        void FillSignField(bool initials, int objectNumber, int pageIndex,
            double x, double y, double width, double height);
        void ShapeToolMouseDown(int pageIndex, Point position, MouseButtonEventArgs e);
        void CommitShapeDrag(int pageIndex);
        void UpdateShapePolyRubber(MouseEventArgs e);
        void OcrRegion(int pageIndex, Rect canvasBounds);
        void ShowShortcutsOverlayExclusive();
        System.Windows.Media.SolidColorBrush SwatchDimBorder { get; }
        PageAnnotation? CloneAnnotation(PageAnnotation annotation);
        System.Windows.TextDecorationCollection? BuildDecorations(bool underline, bool strike);
        System.Windows.Media.Effects.DropShadowEffect AnnotBarShadow();
        void FadeOverlayOut(UIElement element);
        void FadeOutAndRemoveBar(Border? bar);
        string WordsToText(System.Collections.Generic.IEnumerable<KillerPdf.Engine.Documents.PdfExtractedWord> words);
        MenuItem MakeMenuItem(string header, RoutedEventHandler click, string? gesture, string? glyph);
        bool FullScreen { get; }
        bool VerticalScrollVisible { get; set; }
        bool SpaceHeld { get; }
        void RepositionAnnotationBars();
        void PopulateContextMenu(PdfViewer viewer, Point point, int pageIndex);
        void RefreshPageList(PdfViewer viewer);
        void LoadOutlines(PdfViewer viewer);
        Cursor CursorForTool(EditTool tool);

        // ── Notifications, so the window can update chrome for the FOCUSED viewer only ───────
        // These replace the viewer poking at PageList / ZoomBox / PageLabel / StatusText itself.
        /// <summary>This viewer scrolled or paged to a different page.</summary>
        void ViewerPageChanged(PdfViewer viewer, int pageIndex);
        void EnsureSidebarPageVisible(PdfViewer viewer, int pageIndex);
        void ScrollSidebar(PdfViewer viewer, double delta);
        void ClearSidebarPages(PdfViewer viewer);
        string PageJumpText { get; set; }
        bool PageJumpEnabled { set; }
        bool CloseFileEnabled { set; }
        string PageTotalText { set; }
        void SelectAllPageJumpText();
        void SyncZoomDisplay(string? fitTag, string displayText);
        string? SelectedZoomTag { get; }
        void CollapseZoomTextSelection();

        /// <summary>This viewer's zoom or fit mode changed (updates the zoom box). 16 uses of
        /// ZoomBox today.</summary>
        void ViewerZoomChanged(PdfViewer viewer, double zoomLevel);

        /// <summary>The document viewport moved. Used by comparison mode to keep both panes aligned.</summary>
        void ViewerScrolled(PdfViewer viewer, double horizontalRatio, double verticalRatio);

        /// <summary>This viewer took focus - the window repoints the sidebar, page list and
        /// status line at it, and moves the accent halo.</summary>
        void ViewerFocused();

        // Window-owned chrome and start-screen actions raised by one viewer instance.
        void ViewerSizeChanged(PdfViewer viewer, object sender, SizeChangedEventArgs e);
        void ViewerDrop(PdfViewer viewer, object sender, DragEventArgs e);
        void ViewerDragOver(PdfViewer viewer, object sender, DragEventArgs e);
        void ViewerDropZoneClick(object sender, MouseButtonEventArgs e);
        void ClearRecentFiles(object sender, MouseButtonEventArgs e);
        void ViewerBackgroundRightClick(object sender, MouseButtonEventArgs e);
        void ViewerTabStripMouseDown(object sender, MouseButtonEventArgs e);

        bool IsViewerFocused(PdfViewer viewer);
        bool IsSplitView { get; }
        void FocusViewer(PdfViewer viewer);
        bool OtherViewerHasFile(PdfViewer viewer, string? originalFile);

        PdfViewer? TabDropTarget(PdfViewer source, MouseEventArgs e);
        void UpdateTabDragFeedback(PdfViewer source, PdfViewer.DocumentSession session,
            MouseEventArgs e, PdfViewer? target);
        void HideTabDragFeedback();
        void MoveTabToPane(PdfViewer source, PdfViewer target,
            PdfViewer.DocumentSession session, MouseEventArgs e);

        void RunWithViewerContext(PdfViewer viewer, System.Action work);
    }
}
