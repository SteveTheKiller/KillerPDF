using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using KillerPDF.Services;

namespace KillerPDF
{
    /// <summary>
    /// Outward stubs: the annotation, text, crop, form and link members under the names the rest of
    /// the window already calls them by.
    ///
    /// The point of this file is that ~170 call sites across ContextMenu.cs, TextSettingsBar.cs,
    /// KeyboardShortcuts.cs, FileOperations.cs, Tabs.cs, Signing.cs, Shapes.cs, Search.cs,
    /// SidebarOutline.cs, Stamps.cs, ToolSelection.cs, TempReload.cs, Rotate.cs, Ocr.cs and
    /// DirtyTracking.cs need no changes at all.
    ///
    /// THE XAML ONES ARE NOT OPTIONAL. WPF resolves Click="Undo_Click" against the code-behind of
    /// the XAML ROOT - MainWindow - not against whichever class the method ended up in. Eleven
    /// handlers in MainWindow.xaml point at members that now live in the viewer, and without these
    /// declarations InitializeComponent throws XamlParseException before the window ever appears.
    /// Verified against MainWindow.xaml rather than remembered: Undo_Click (2 bindings),
    /// ClearAllAnnotations_Click (2), PageJumpBox_KeyDown, PageJumpBox_GotFocus,
    /// PageList_SelectionChanged, ShortcutHelp_Click, ShortcutOverlay_MouseLeftButtonDown (2),
    /// ShortcutOverlayCard_MouseLeftButtonDown (2), ShortcutOverlayClose_Click,
    /// Hyperlink_RequestNavigate.
    /// </summary>
    public partial class MainWindow
    {
        // ── Annotations ──────────────────────────────────────────────────────────────────────
        private void RenderAllAnnotations(int pageIndex) => ActiveViewer.RenderAllAnnotations(pageIndex);
        private void ClearSelection() => ActiveViewer.ClearSelection();
        private void ClearTextSelection() => ActiveViewer.ClearTextSelection();
        private SolidColorBrush AccentBrush(byte alpha = 255) => ActiveViewer.AccentBrush(alpha);
        private void AddAnnotation(PageAnnotation a) => ActiveViewer.AddAnnotationExt(a);
        private static Rect AnnotBounds(PageAnnotation a) => Controls.PdfViewer.AnnotBoundsExt(a);
        private static Point AnnotGetPos(PageAnnotation a) => Controls.PdfViewer.AnnotGetPosExt(a);
        private static void AnnotSetPos(PageAnnotation a, Point pos) => Controls.PdfViewer.AnnotSetPosExt(a, pos);
        private Point ClampAnnotPos(PageAnnotation a) => ActiveViewer.ClampAnnotPosExt(a);
        private static bool HitTestAnnotation(PageAnnotation a, Point pos, out Rect bounds)
            => Controls.PdfViewer.HitTestAnnotationExt(a, pos, out bounds);
        private static bool IsDraggable(PageAnnotation a) => Controls.PdfViewer.IsDraggableExt(a);
        private void SelectAnnotation(PageAnnotation a, Rect bounds) => ActiveViewer.SelectAnnotationExt(a, bounds);
        private void ToggleMultiSelect(PageAnnotation a, Rect bounds, Canvas canvas)
            => ActiveViewer.ToggleMultiSelectExt(a, bounds, canvas);
        private void SelectGroup(PageAnnotation lead) => ActiveViewer.SelectGroupExt(lead);
        private PageAnnotation? SelectedPaired() => ActiveViewer.SelectedPairedExt();
        private int SelectionCount() => ActiveViewer.SelectionCountExt();
        private void ReattachSelectionVisuals() => ActiveViewer.ReattachSelectionVisualsExt();
        private void UnpairSelected() => ActiveViewer.UnpairSelectedExt();
        private void GroupSelected() => ActiveViewer.GroupSelectedExt();
        private void UngroupAnnotation(PageAnnotation a) => ActiveViewer.UngroupAnnotationExt(a);
        private void RemoveFromGroup(PageAnnotation a) => ActiveViewer.RemoveFromGroupExt(a);
        private void DeleteSelected() => ActiveViewer.DeleteSelectedExt();
        private void RasterizeSelectedZone(PageAnnotation hit) => ActiveViewer.RasterizeZoneExt(hit);
        private bool SelectAllAnnotations() => ActiveViewer.SelectAllAnnotationsExt();
        private void HideBrushPreview() => ActiveViewer.HideBrushPreviewExt();
        private void FinishStuckGesture() => ActiveViewer.FinishStuckGestureExt();
        private void RefreshSelectionAccent() => ActiveViewer.RefreshSelectionAccentExt();

        // ── Page canvases ────────────────────────────────────────────────────────────────────
        private Canvas CanvasForPage(int page) => ActiveViewer.CanvasForPageExt(page);
        private Canvas? VisibleCanvasForPage(int page) => ActiveViewer.VisibleCanvasForPageExt(page);
        private IEnumerable<Canvas> AllPageCanvases() => ActiveViewer.AllPageCanvasesExt();

        // ── Undo ─────────────────────────────────────────────────────────────────────────────
        private UndoEntry? CaptureDocumentUndo() => ActiveViewer.CaptureDocumentUndoExt();
        private void PushPageSnapshotUndo(int pageIdx) => ActiveViewer.PushPageSnapshotUndoExt(pageIdx);

        // ── Text editing ─────────────────────────────────────────────────────────────────────
        private void CommitActiveTextBox() => ActiveViewer.CommitActiveTextBoxExt();
        private void RemoveTextEditHandles() => ActiveViewer.RemoveTextEditHandlesExt();
        private void EditTextAtPosition(Point canvasPos, int pageIdx) => ActiveViewer.EditTextAtPositionExt(canvasPos, pageIdx);
        private void PlaceTextBox(Point pos, int pageIdx) => ActiveViewer.PlaceTextBoxExt(pos, pageIdx);
        private Brush TextEditBackground() => ActiveViewer.TextEditBackgroundExt();
        private static ControlTemplate FlatTextBoxTemplate() => Controls.PdfViewer.FlatTextBoxTemplateExt();

        // ── Text selection ───────────────────────────────────────────────────────────────────
        private void CopySelectedText() => ActiveViewer.CopySelectedTextExt();
        private void SelectAllText() => ActiveViewer.SelectAllTextExt();

        // ── Crop ─────────────────────────────────────────────────────────────────────────────
        private void ApplyCrop(int[] pageIndices) => ActiveViewer.ApplyCropExt(pageIndices);
        private void HideCropConfirmBar() => ActiveViewer.HideCropConfirmBarExt();
        private void ShowDefaultCropBox() => ActiveViewer.ShowDefaultCropBoxExt();
        private void RebuildCropBarForLocale() => ActiveViewer.RebuildCropBarForLocaleExt();

        // ── Links ────────────────────────────────────────────────────────────────────────────
        private void CloseEngineDocumentSession() => ActiveViewer.CloseEngineDocumentSessionExt();
        private PdfEngineDocumentSession EnsureEngineDocumentSession() =>
            ActiveViewer.EnsureEngineDocumentSessionExt();
        private void AddLinkMenuItems(ContextMenu menu, object target, int annotIndex, int pageIndex)
            => ActiveViewer.AddLinkMenuItemsExt(menu, target, annotIndex, pageIndex);
        private const double LinkHitPad = Controls.PdfViewer.LinkHitPadShared;
        internal const string ConfirmLinksSetting = Controls.PdfViewer.ConfirmLinksSetting;

        // ── Save paths ───────────────────────────────────────────────────────────────────────
        private void WriteFormValuesToDocument(string path) => ActiveViewer.WriteFormValuesToDocumentExt(path);

        // ── Bound from MainWindow.xaml - see the class comment, these are load-bearing ───────
        private void Undo_Click(object sender, RoutedEventArgs e) => ActiveViewer.UndoClickExt(sender, e);
        private void Redo_Click(object sender, RoutedEventArgs e) => ActiveViewer.RedoClickExt(sender, e);
        private void ClearAnnotations_Click(object sender, RoutedEventArgs e) => ActiveViewer.ClearAnnotationsClickExt(sender, e);
        private void ClearAllAnnotations_Click(object sender, RoutedEventArgs e) => ActiveViewer.ClearAllAnnotationsClickExt(sender, e);
        private void PageJumpBox_KeyDown(object sender, KeyEventArgs e) => ActiveViewer.PageJumpBoxKeyDownExt(sender, e);
        private void PageJumpBox_GotFocus(object sender, RoutedEventArgs e) => ActiveViewer.PageJumpBoxGotFocusExt(sender, e);
        private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => ActiveViewer.PageListSelectionChangedExt(sender, e);
        private void ShortcutHelp_Click(object sender, RoutedEventArgs e) => ActiveViewer.ShortcutHelpClickExt(sender, e);
        private void ShortcutOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => FadeOverlayOut(ShortcutOverlay);
        private void ShortcutOverlayCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => e.Handled = true;
        private void ShortcutOverlayClose_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            FadeOverlayOut(ShortcutOverlay);
        }
        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
            => ActiveViewer.HyperlinkRequestNavigateExt(sender, e);
    }
}
