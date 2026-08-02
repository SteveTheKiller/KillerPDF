using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KillerPDF.Controls
{
    /// <summary>
    /// The viewer end of MainWindowViewerBridge2.cs. Every name here is spelled exactly as the moved
    /// editing code already spells it, so Annotations, TextEditing, Crop, Forms, Links and Selection
    /// came across without a character of their logic changing.
    ///
    /// Same discipline as Bridge.cs, and the same expiry: this is per-DOCUMENT state that belongs in
    /// DocumentSession. When the viewer holds its own active session, this file and its window-side
    /// twin both go.
    /// </summary>
    public partial class PdfViewer
    {
        // ── Selection ────────────────────────────────────────────────────────────────────────
        private PageAnnotation? _selectedAnnotation { get => W.SelectedAnnotationRef; set => W.SelectedAnnotationRef = value; }
        private Border? _selectionBorder { get => W.SelectionBorderRef; set => W.SelectionBorderRef = value; }
        private List<PageAnnotation> _selectedSet => W.SelectedSetRef;
        private List<Border> _selectionOutlines => W.SelectionOutlinesRef;
        private Rectangle? _pairedCoverOutline { get => W.PairedCoverOutlineRef; set => W.PairedCoverOutlineRef = value; }
        private Rectangle? _reeditCoverOutline { get => W.ReeditCoverOutlineRef; set => W.ReeditCoverOutlineRef = value; }
        private string? _selectedText { get => W.SelectedTextRef; set => W.SelectedTextRef = value; }
        private List<(PageAnnotation a, Point orig)> _dragGroupOrig => W.DragGroupOrigRef;

        // ── Draw / highlight tool ────────────────────────────────────────────────────────────
        private Color _drawColor { get => W.DrawColorRef; set => W.DrawColorRef = value; }
        private double _drawWidth { get => W.DrawWidthRef; set => W.DrawWidthRef = value; }
        private byte _drawOpacity { get => W.DrawOpacityRef; set => W.DrawOpacityRef = value; }
        private bool _lineLevel { get => W.LineLevelRef; set => W.LineLevelRef = value; }
        private bool _highlightErase { get => W.HighlightEraseRef; set => W.HighlightEraseRef = value; }
        private bool _drawErase { get => W.DrawEraseRef; set => W.DrawEraseRef = value; }
        private Color _highlightColor { get => W.HighlightColorRef; set => W.HighlightColorRef = value; }
        private Color _lineAnnotColor { get => W.LineAnnotColorRef; set => W.LineAnnotColorRef = value; }
        private InkAnnotation? _activeInk { get => W.ActiveInkRef; set => W.ActiveInkRef = value; }

        // ── Text (typewriter) tool ───────────────────────────────────────────────────────────
        private TextBox? _activeTextBox { get => W.ActiveTextBoxRef; set => W.ActiveTextBoxRef = value; }
        private double _textFontSize { get => W.TextFontSizeRef; set => W.TextFontSizeRef = value; }
        private string _textFontName { get => W.TextFontNameRef; set => W.TextFontNameRef = value; }
        private bool _textBold { get => W.TextBoldRef; set => W.TextBoldRef = value; }
        private bool _textItalic { get => W.TextItalicRef; set => W.TextItalicRef = value; }
        private bool _textStrike { get => W.TextStrikeRef; set => W.TextStrikeRef = value; }
        private bool _textUnderline { get => W.TextUnderlineRef; set => W.TextUnderlineRef = value; }
        private Color _textColor { get => W.TextColorRef; set => W.TextColorRef = value; }
        private byte _textOpacity { get => W.TextOpacityRef; set => W.TextOpacityRef = value; }
        private Color _textFillColor { get => W.TextFillColorRef; set => W.TextFillColorRef = value; }
        private TextAnnotation? _reeditOriginal { get => W.ReeditOriginalRef; set => W.ReeditOriginalRef = value; }
        private CoverAnnotation? _pendingCover { get => W.PendingCoverRef; set => W.PendingCoverRef = value; }
        private bool _pendingEditWasDirty { get => W.PendingEditWasDirtyRef; set => W.PendingEditWasDirtyRef = value; }
        private Border? _textSettingsBar { get => W.TextSettingsBarRef; set => W.TextSettingsBarRef = value; }
        private const double EditTextSizeCorrection = MainWindow.EditTextSizeCorrectionShared;
        private const double TextBoxDefaultWidth = MainWindow.TextBoxDefaultWidthShared;

        // ── Resize handles ───────────────────────────────────────────────────────────────────
        private bool _isResizingSig { get => W.IsResizingSigRef; set => W.IsResizingSigRef = value; }
        private Point _resizeSigStart { get => W.ResizeSigStartRef; set => W.ResizeSigStartRef = value; }
        private double _resizeSigStartScale { get => W.ResizeSigStartScaleRef; set => W.ResizeSigStartScaleRef = value; }
        private PlacedAnnotation? _resizeSigAnnot { get => W.ResizeSigAnnotRef; set => W.ResizeSigAnnotRef = value; }
        private TextAnnotation? _resizeTextAnnot { get => W.ResizeTextAnnotRef; set => W.ResizeTextAnnotRef = value; }
        private HighlightAnnotation? _resizeHlAnnot { get => W.ResizeHlAnnotRef; set => W.ResizeHlAnnotRef = value; }
        private InkAnnotation? _resizeInkAnnot { get => W.ResizeInkAnnotRef; set => W.ResizeInkAnnotRef = value; }
        private List<Point>? _resizeInkOrigPoints { get => W.ResizeInkOrigPointsRef; set => W.ResizeInkOrigPointsRef = value; }
        private Rect _resizeInkOrigBounds { get => W.ResizeInkOrigBoundsRef; set => W.ResizeInkOrigBoundsRef = value; }
        private List<Rectangle> _resizeHandles => W.ResizeHandlesRef;
        private string _resizeCorner { get => W.ResizeCornerRef; set => W.ResizeCornerRef = value; }
        private Point _resizeAnchor { get => W.ResizeAnchorRef; set => W.ResizeAnchorRef = value; }

        private List<Rectangle> _textEditHandles => W.TextEditHandlesRef;
        private bool _draggingTextEditHandle { get => W.DraggingTextEditHandleRef; set => W.DraggingTextEditHandleRef = value; }
        private string _tehCorner { get => W.TehCornerRef; set => W.TehCornerRef = value; }
        private Point _tehAnchor { get => W.TehAnchorRef; set => W.TehAnchorRef = value; }
        private TextBox? _tehBox { get => W.TehBoxRef; set => W.TehBoxRef = value; }

        // ── Drag-to-move ─────────────────────────────────────────────────────────────────────
        private bool _isDraggingAnnot { get => W.IsDraggingAnnotRef; set => W.IsDraggingAnnotRef = value; }
        private Point _dragAnnotStart { get => W.DragAnnotStartRef; set => W.DragAnnotStartRef = value; }
        private Point _dragAnnotOrigPos { get => W.DragAnnotOrigPosRef; set => W.DragAnnotOrigPosRef = value; }
        private PageAnnotation? _dragAnnot { get => W.DragAnnotRef; set => W.DragAnnotRef = value; }

        // ── Crop tool ────────────────────────────────────────────────────────────────────────
        private Rect _cropCanvasRect { get => W.CropCanvasRectRef; set => W.CropCanvasRectRef = value; }
        private Rectangle? _cropPreviewRectBorder { get => W.CropPreviewRectBorderRef; set => W.CropPreviewRectBorderRef = value; }
        private List<System.Windows.Shapes.Path> _cropBrackets => W.CropBracketsRef;
        private List<Rectangle> _cropHandles => W.CropHandlesRef;
        private string? _activeCropHandleTag { get => W.ActiveCropHandleTagRef; set => W.ActiveCropHandleTagRef = value; }
        private Point _cropHandleDragStart { get => W.CropHandleDragStartRef; set => W.CropHandleDragStartRef = value; }
        private Rect _cropRectAtHandleDrag { get => W.CropRectAtHandleDragRef; set => W.CropRectAtHandleDragRef = value; }
        private TextBox? _cropXBox { get => W.CropXBoxRef; set => W.CropXBoxRef = value; }
        private TextBox? _cropYBox { get => W.CropYBoxRef; set => W.CropYBoxRef = value; }
        private TextBox? _cropWBox { get => W.CropWBoxRef; set => W.CropWBoxRef = value; }
        private TextBox? _cropHBox { get => W.CropHBoxRef; set => W.CropHBoxRef = value; }
        private TextBox? _cropRangeBox { get => W.CropRangeBoxRef; set => W.CropRangeBoxRef = value; }
        private string _cropUnit { get => W.CropUnitRef; set => W.CropUnitRef = value; }
        private bool _updatingCropInputs { get => W.UpdatingCropInputsRef; set => W.UpdatingCropInputsRef = value; }

        // ── Form filling ─────────────────────────────────────────────────────────────────────
        private Dictionary<int, string> _formTextValues { get => W.FormTextValuesRef; set => W.FormTextValuesRef = value; }
        private Dictionary<int, bool> _formCheckValues { get => W.FormCheckValuesRef; set => W.FormCheckValuesRef = value; }
        private Dictionary<string, string> _formRadioValues { get => W.FormRadioValuesRef; set => W.FormRadioValuesRef = value; }
        private Dictionary<int, double> _formFontSizes { get => W.FormFontSizesRef; set => W.FormFontSizesRef = value; }
        private Border? _formSizeBar { get => W.FormSizeBarRef; set => W.FormSizeBarRef = value; }
        private TextBox? _activeFormTb { get => W.ActiveFormTbRef; set => W.ActiveFormTbRef = value; }
        private int _activeFormObj { get => W.ActiveFormObjRef; set => W.ActiveFormObjRef = value; }
        private double _activeFormScale { get => W.ActiveFormScaleRef; set => W.ActiveFormScaleRef = value; }
        private const string FormOverlayTag = MainWindow.FormOverlayTagShared;

        // ── Undo / dirty ─────────────────────────────────────────────────────────────────────
        private Stack<UndoEntry> _undoStack { get => W.UndoStackRef; set => W.UndoStackRef = value; }
        private Stack<UndoEntry> _redoStack { get => W.RedoStackRef; set => W.RedoStackRef = value; }
        private bool _isDirty { get => W.IsDirtyRef; set => W.IsDirtyRef = value; }

        // ── State owned by files that did not move ───────────────────────────────────────────
        private Border? _searchBar => W.SearchBarRef;
        private Features.SearchController Search => W.SearchCtl;
        private bool _ocrRegionMode { get => W.OcrRegionModeRef; set => W.OcrRegionModeRef = value; }
        private SavedSignature? _pendingSignature { get => W.PendingSignatureRef; set => W.PendingSignatureRef = value; }
        private List<Point> _shapePolyPoints => W.ShapePolyPointsRef;
        private EditTool? _annotBarTool { get => W.AnnotBarToolRef; set => W.AnnotBarToolRef = value; }
        private bool _annotBarMinimized { get => W.AnnotBarMinimizedRef; set => W.AnnotBarMinimizedRef = value; }
        private List<FrameworkElement> _annotBarDragInners => W.AnnotBarDragInnersRef;
        private static SolidColorBrush _swatchDimBorder => MainWindow.SwatchDimBorderRef;

        // ══ What Tabs.cs reaches for, now that it lives here ════════════════════════════════
        private string? _originalFile { get => W.OriginalFileRef; set => W.OriginalFileRef = value; }
        private bool _openedFromProtected { get => W.OpenedFromProtectedRef; set => W.OpenedFromProtectedRef = value; }
        private bool _asyncOpenPending { get => W.AsyncOpenPendingRef; set => W.AsyncOpenPendingRef = value; }
        // This pane's own loader token, NOT the window's - see ThumbCts in PdfViewer.TabsApi.cs.
        private System.Threading.CancellationTokenSource? _thumbCts { get => ThumbCts; set => ThumbCts = value; }
        private bool _sidebarShowingOutlines => W.SidebarShowingOutlinesRef;
        private System.Collections.Generic.Stack<int> _navBack => W.NavBackRef;
        private System.Collections.Generic.Stack<int> _navForward => W.NavForwardRef;

        private TextBlock FileNameLabel => W.FileNameLabelCtl;
        private TreeView OutlineTree => W.OutlineTreeCtl;
        private Button SidebarOutlinesTab => W.SidebarOutlinesTabCtl;

        private ContextMenu MakeThemedMenu() => W.MakeThemedMenuBridge();
        private void CloseSearchBar() => W.CloseSearchBarBridge();
        private void HideSignaturePopup() => W.HideSignaturePopupBridge();
        private void PopulateRecentFilesList() => W.PopulateRecentFilesListBridge(this);
        private void SwitchSidebarToPagesTab() => W.SwitchSidebarToPagesTabBridge();
        private void SyncSidebarToDocState(bool hasDoc, bool startup) => W.SyncSidebarToDocStateBridge(hasDoc, startup);
        private void OpenFile(string path) => W.OpenFileBridge(path);
        private void UpdateFooterFade() => W.UpdateFooterFadeBridge();
        private void UpdateTabStripFade() => W.UpdateTabStripFadeBridge();

        // ── Chrome ───────────────────────────────────────────────────────────────────────────
        private TextBlock StatusText => W.StatusTextCtl;
        private FrameworkElement ShortcutOverlay => W.ShortcutOverlayCtl;
        private CheckBox LinkConfirmCheck => W.LinkConfirmCheckCtl;

        // ── Methods still on the window ──────────────────────────────────────────────────────
        private void MarkDirty(bool dirty = true) => W.MarkDirtyBridge(dirty);
        private void SetTool(EditTool t) => W.SetToolBridge(t);
        private void SaveTempAndReload(bool keepAnnotations = false, bool preserveZoom = false)
            => W.SaveTempAndReloadBridge(keepAnnotations, preserveZoom);
        private void RecordNavJump() => W.RecordNavJumpBridge();
        private static PageAnnotation? CloneAnnotation(PageAnnotation a) => MainWindow.CloneAnnotationBridge(a);
        private PageAnnotation? PairPartner(PageAnnotation a) => W.PairPartnerBridge(a);
        private void RenderStamps(int page) => W.RenderStampsBridge(page);
        private void OpenStampTool() => W.OpenStampToolBridge();
        private bool StampHitTest(int page, Point pos) => W.StampHitTestBridge(page, pos);
        private void ApplySearchHighlights(int page, Canvas canvas) => W.ApplySearchHighlightsBridge(page, canvas);
        private void HighlightSearchResultsOnCurrentPage() => W.HighlightSearchResultsOnCurrentPageBridge();
        private void ShowTextSettings() => W.ShowTextSettingsBridge();
        private void HideTextSettings() => W.HideTextSettingsBridge();
        private void StyleEditBox(TextBox tb) => W.StyleEditBoxBridge(tb);
        private void ApplyTextStyleToSelection() => W.ApplyTextStyleToSelectionBridge();
        private static TextDecorationCollection? BuildDecorations(bool underline, bool strike)
            => MainWindow.BuildDecorationsBridge(underline, strike);
        private void ShowDrawSettings(EditTool t) => W.ShowDrawSettingsBridge(t);
        private void HideDrawSettings() => W.HideDrawSettingsBridge();
        private Border MakeBarGrip(int dotCount = 3) => W.MakeBarGripBridge(dotCount);
        private FrameworkElement BuildBarHost(FrameworkElement content) => W.BuildBarHostBridge(content);
        private void PlaceAnnotationBar(Border bar, Border grip, bool fadeIn = false)
            => W.PlaceAnnotationBarBridge(bar, grip, fadeIn);
        private static System.Windows.Media.Effects.DropShadowEffect AnnotBarShadow()
            => MainWindow.AnnotBarShadowBridge();
        private void PlaceImageFromDialog(Point pos, int pageIdx) => W.PlaceImageFromDialogBridge(pos, pageIdx);
        private void PlaceSignature(Point pos, int pageIdx) => W.PlaceSignatureBridge(pos, pageIdx);
        private void ShowSignaturePopup() => W.ShowSignaturePopupBridge();
        private void FillSignField(bool initials, int objNum, int pageIndex,
                                   double x, double y, double w, double h)
            => W.FillSignFieldBridge(initials, objNum, pageIndex, x, y, w, h);
        private void ShapeToolMouseDown(int pageIdx, Point pos, MouseButtonEventArgs e)
            => W.ShapeToolMouseDownBridge(pageIdx, pos, e);
        private void CommitShapeDrag(int pageIdx) => W.CommitShapeDragBridge(pageIdx);
        private void UpdateShapePolyRubber(MouseEventArgs e) => W.UpdateShapePolyRubberBridge(e);
        private void OcrRegion(int pageIdx, Rect canvasBounds) => W.OcrRegionBridge(pageIdx, canvasBounds);
        private void ShowShortcutsOverlayExclusive() => W.ShowShortcutsOverlayExclusiveBridge();
        private static void FadeOverlayOut(UIElement el) => MainWindow.FadeOverlayOutBridge(el);
        private static void FadeOutAndRemoveBar(Border? bar) => MainWindow.FadeOutAndRemoveBarBridge(bar);
        private static PdfSharpCore.Pdf.PdfItem DerefItem(PdfSharpCore.Pdf.PdfItem item)
            => MainWindow.DerefItemBridge(item);
        private static string WordsToText(IEnumerable<UglyToad.PdfPig.Content.Word> src)
            => MainWindow.WordsToTextBridge(src);
        private static MenuItem MakeMenuItem(string header, RoutedEventHandler click,
                                             string? gesture = null, string? glyph = null)
            => MainWindow.MakeMenuItemBridge(header, click, gesture, glyph);
    }
}
