using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PdfSharpCore.Pdf;

namespace KillerPDF.Controls
{
    /// <summary>
    /// The viewer's outward surface: what the window still calls into.
    ///
    /// WHY A FACADE RATHER THAN WIDENING. Roughly 55 of these are private members of the seven
    /// moved files. Making them internal in place would mean 55 edits scattered through code that
    /// is otherwise VERBATIM - and "verbatim" is the property that makes the move reviewable by
    /// diff. This file is part of the same partial class, so it can see those privates and
    /// re-expose them without touching a line of the moved code.
    ///
    /// The `Ext` suffix exists only because a wrapper cannot share a name with the member it wraps
    /// inside one class. Members that were already internal (RenderAllAnnotations, ClearSelection,
    /// ClearTextSelection, AccentBrush) are absent here - the window calls those directly.
    ///
    /// Every entry is one call site away from deletion: when a caller moves into the viewer, its
    /// line here goes with it.
    /// </summary>
    public partial class PdfViewer
    {
        // ── Annotations: selection, hit-testing, geometry ────────────────────────────────────
        internal void AddAnnotationExt(PageAnnotation a) => AddAnnotation(a);
        internal Rect AnnotBoundsExt(PageAnnotation a) => AnnotBounds(a);
        internal static Point AnnotGetPosExt(PageAnnotation a) => AnnotGetPos(a);
        internal static void AnnotSetPosExt(PageAnnotation a, Point pos) => AnnotSetPos(a, pos);
        internal Point ClampAnnotPosExt(PageAnnotation a) => ClampAnnotPos(a);
        internal bool HitTestAnnotationExt(PageAnnotation a, Point pos, out Rect bounds)
            => HitTestAnnotation(a, pos, out bounds);
        internal static bool IsDraggableExt(PageAnnotation a) => IsDraggable(a);
        internal void SelectAnnotationExt(PageAnnotation a, Rect bounds) => SelectAnnotation(a, bounds);
        internal void ToggleMultiSelectExt(PageAnnotation a, Rect bounds, Canvas canvas)
            => ToggleMultiSelect(a, bounds, canvas);
        internal void SelectGroupExt(PageAnnotation lead) => SelectGroup(lead);
        internal PageAnnotation? SelectedPairedExt() => SelectedPaired();
        internal int SelectionCountExt() => SelectionCount();
        internal void ReattachSelectionVisualsExt() => ReattachSelectionVisuals();
        internal void UnpairSelectedExt() => UnpairSelected();
        internal void GroupSelectedExt() => GroupSelected();
        internal void UngroupAnnotationExt(PageAnnotation a) => UngroupAnnotation(a);
        internal void RemoveFromGroupExt(PageAnnotation a) => RemoveFromGroup(a);
        internal void DeleteSelectedExt() => DeleteSelected();
        internal bool SelectAllAnnotationsExt() => SelectAllAnnotations();
        internal void HideBrushPreviewExt() => HideBrushPreview();
        internal void FinishStuckGestureExt() => FinishStuckGesture();
        internal void RefreshSelectionAccentExt() => RefreshSelectionAccent();

        // ── Page canvases ────────────────────────────────────────────────────────────────────
        internal Canvas CanvasForPageExt(int page) => CanvasForPage(page);
        internal Canvas? VisibleCanvasForPageExt(int page) => VisibleCanvasForPage(page);
        internal IEnumerable<Canvas> AllPageCanvasesExt() => AllPageCanvases();

        // ── Undo / commands bound from MainWindow.xaml and the context menu ──────────────────
        internal void PushDocUndoExt() => PushDocUndo();
        internal void PushPageSnapshotUndoExt(int pageIdx) => PushPageSnapshotUndo(pageIdx);
        internal void UndoClickExt(object sender, RoutedEventArgs e) => Undo_Click(sender, e);
        internal void RedoClickExt(object sender, RoutedEventArgs e) => Redo_Click(sender, e);
        internal void ClearAnnotationsClickExt(object sender, RoutedEventArgs e) => ClearAnnotations_Click(sender, e);
        internal void ClearAllAnnotationsClickExt(object sender, RoutedEventArgs e) => ClearAllAnnotations_Click(sender, e);

        // ── Text editing ─────────────────────────────────────────────────────────────────────
        internal void CommitActiveTextBoxExt() => CommitActiveTextBox();
        internal void RemoveTextEditHandlesExt() => RemoveTextEditHandles();
        internal void EditTextAtPositionExt(Point canvasPos, int pageIdx) => EditTextAtPosition(canvasPos, pageIdx);
        internal void PlaceTextBoxExt(Point pos, int pageIdx) => PlaceTextBox(pos, pageIdx);
        internal Brush TextEditBackgroundExt() => TextEditBackground();
        internal static ControlTemplate FlatTextBoxTemplateExt() => FlatTextBoxTemplate();

        // ── Text selection ───────────────────────────────────────────────────────────────────
        internal void CopySelectedTextExt() => CopySelectedText();
        internal void SelectAllTextExt() => SelectAllText();

        // ── Crop ─────────────────────────────────────────────────────────────────────────────
        internal void ApplyCropExt(int[] pageIndices) => ApplyCrop(pageIndices);
        internal void HideCropConfirmBarExt() => HideCropConfirmBar();
        internal void ShowDefaultCropBoxExt() => ShowDefaultCropBox();
        internal void RebuildCropBarForLocaleExt() => RebuildCropBarForLocale();

        // ── Links ────────────────────────────────────────────────────────────────────────────
        internal void CloseLinkPdfiumDocExt() => CloseLinkPdfiumDoc();
        internal void AddLinkMenuItemsExt(ContextMenu menu, object target, int annotIndex, int pageIndex)
            => AddLinkMenuItems(menu, target, annotIndex, pageIndex);
        internal int? ResolveDestExt(PdfItem? destItem) => ResolveDest(destItem);

        // ── Save paths ───────────────────────────────────────────────────────────────────────
        internal void DrawAnnotationsOnDocumentExt(int? onlyPage = null) => DrawAnnotationsOnDocument(onlyPage);
        internal void WriteFormValuesToDocumentExt() => WriteFormValuesToDocument();

        // ── Handlers bound from MainWindow.xaml ──────────────────────────────────────────────
        // WPF resolves Click="X" against the XAML root's code-behind, which is still MainWindow, so
        // these keep working only because MainWindowViewerStubs.cs re-declares each name and points
        // it here. Moving them without that would throw XamlParseException at startup.
        internal void PageJumpBoxKeyDownExt(object sender, KeyEventArgs e) => PageJumpBox_KeyDown(sender, e);
        internal void PageJumpBoxGotFocusExt(object sender, RoutedEventArgs e) => PageJumpBox_GotFocus(sender, e);
        internal void PageListSelectionChangedExt(object sender, SelectionChangedEventArgs e)
            => PageList_SelectionChanged(sender, e);
        internal void ShortcutHelpClickExt(object sender, RoutedEventArgs e) => ShortcutHelp_Click(sender, e);
        internal void ShortcutOverlayMouseDownExt(object sender, MouseButtonEventArgs e)
            => ShortcutOverlay_MouseLeftButtonDown(sender, e);
        internal void ShortcutOverlayCardMouseDownExt(object sender, MouseButtonEventArgs e)
            => ShortcutOverlayCard_MouseLeftButtonDown(sender, e);
        internal void ShortcutOverlayCloseClickExt(object sender, RoutedEventArgs e)
            => ShortcutOverlayClose_Click(sender, e);
        internal void HyperlinkRequestNavigateExt(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
            => Hyperlink_RequestNavigate(sender, e);
    }
}
