using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KillerPDF
{
    /// <summary>
    /// The editing state the annotation, text, crop, form and link code reaches for, exposed to the
    /// viewer control. Kept separate from MainWindowViewerBridge.cs only because merging the two
    /// lists would make neither readable.
    ///
    /// WHY THESE ARE STILL ON THE WINDOW. The obvious alternative is to move ~80 field declarations
    /// across with their code. They stay for two reasons:
    ///
    ///  1. A dozen of them are also touched by files that live on the window - AnnotationBars.cs,
    ///     TextSettingsBar.cs, ContextMenu.cs, Shapes.cs, Signing.cs, Ocr.cs, Search.cs,
    ///     DirtyTracking.cs, Tabs.cs. Splitting the block would mean classifying every field by
    ///     hand, and a silent half-move is easy to get wrong.
    ///  2. They are per-DOCUMENT state, not per-VIEW. The end state is that they ride in
    ///     DocumentSession and the viewer reads them off the session it is showing - the same
    ///     conclusion that applies to _doc and _annotations. Relocating them onto the control now
    ///     would be a second move to undo later.
    ///
    /// So this file is scaffolding with a known expiry, exactly like group B in the other bridge.
    /// It deletes itself when the viewer owns its session.
    ///
    /// Collections are get-only on purpose: the moved code mutates them in place (Add/Clear) and
    /// never rebinds the reference. Anything the moved code ASSIGNS gets a setter.
    /// </summary>
    public partial class MainWindow
    {
        // ── Selection ────────────────────────────────────────────────────────────────────────
        internal PageAnnotation? SelectedAnnotationRef { get => _selectedAnnotation; set => _selectedAnnotation = value; }
        internal Border? SelectionBorderRef { get => _selectionBorder; set => _selectionBorder = value; }
        internal List<PageAnnotation> SelectedSetRef => _selectedSet;
        internal List<Border> SelectionOutlinesRef => _selectionOutlines;
        internal Rectangle? PairedCoverOutlineRef { get => _pairedCoverOutline; set => _pairedCoverOutline = value; }
        internal Rectangle? ReeditCoverOutlineRef { get => _reeditCoverOutline; set => _reeditCoverOutline = value; }
        internal string? SelectedTextRef { get => _selectedText; set => _selectedText = value; }
        internal List<(PageAnnotation a, Point orig)> DragGroupOrigRef => _dragGroupOrig;   // ContextMenu.cs

        // ── Draw / highlight tool ────────────────────────────────────────────────────────────
        internal Color DrawColorRef { get => _drawColor; set => _drawColor = value; }
        internal double DrawWidthRef { get => _drawWidth; set => _drawWidth = value; }
        internal byte DrawOpacityRef { get => _drawOpacity; set => _drawOpacity = value; }
        internal bool LineLevelRef { get => _lineLevel; set => _lineLevel = value; }
        internal bool HighlightEraseRef { get => _highlightErase; set => _highlightErase = value; }
        internal bool DrawEraseRef { get => _drawErase; set => _drawErase = value; }
        internal Color HighlightColorRef { get => _highlightColor; set => _highlightColor = value; }
        internal Color LineAnnotColorRef { get => _lineAnnotColor; set => _lineAnnotColor = value; }
        internal InkAnnotation? ActiveInkRef { get => _activeInk; set => _activeInk = value; }

        // ── Text (typewriter) tool ───────────────────────────────────────────────────────────
        internal TextBox? ActiveTextBoxRef { get => _activeTextBox; set => _activeTextBox = value; }
        internal double TextFontSizeRef { get => _textFontSize; set => _textFontSize = value; }
        internal string TextFontNameRef { get => _textFontName; set => _textFontName = value; }
        internal bool TextBoldRef { get => _textBold; set => _textBold = value; }
        internal bool TextItalicRef { get => _textItalic; set => _textItalic = value; }
        internal bool TextStrikeRef { get => _textStrike; set => _textStrike = value; }
        internal bool TextUnderlineRef { get => _textUnderline; set => _textUnderline = value; }
        internal Color TextColorRef { get => _textColor; set => _textColor = value; }
        internal byte TextOpacityRef { get => _textOpacity; set => _textOpacity = value; }
        internal Color TextFillColorRef { get => _textFillColor; set => _textFillColor = value; }
        internal TextAnnotation? ReeditOriginalRef { get => _reeditOriginal; set => _reeditOriginal = value; }
        internal CoverAnnotation? PendingCoverRef { get => _pendingCover; set => _pendingCover = value; }
        internal bool PendingEditWasDirtyRef { get => _pendingEditWasDirty; set => _pendingEditWasDirty = value; }
        internal Border? TextSettingsBarRef { get => _textSettingsBar; set => _textSettingsBar = value; }
        // Aliased rather than duplicated - TextSettingsBar.cs reads them too.
        internal const double EditTextSizeCorrectionShared = EditTextSizeCorrection;
        internal const double TextBoxDefaultWidthShared = TextBoxDefaultWidth;

        // ── Resize handles (placed annotations, and the live edit box) ───────────────────────
        internal bool IsResizingSigRef { get => _isResizingSig; set => _isResizingSig = value; }
        internal Point ResizeSigStartRef { get => _resizeSigStart; set => _resizeSigStart = value; }
        internal double ResizeSigStartScaleRef { get => _resizeSigStartScale; set => _resizeSigStartScale = value; }
        internal PlacedAnnotation? ResizeSigAnnotRef { get => _resizeSigAnnot; set => _resizeSigAnnot = value; }
        internal TextAnnotation? ResizeTextAnnotRef { get => _resizeTextAnnot; set => _resizeTextAnnot = value; }
        internal HighlightAnnotation? ResizeHlAnnotRef { get => _resizeHlAnnot; set => _resizeHlAnnot = value; }
        internal InkAnnotation? ResizeInkAnnotRef { get => _resizeInkAnnot; set => _resizeInkAnnot = value; }
        internal List<Point>? ResizeInkOrigPointsRef { get => _resizeInkOrigPoints; set => _resizeInkOrigPoints = value; }
        internal Rect ResizeInkOrigBoundsRef { get => _resizeInkOrigBounds; set => _resizeInkOrigBounds = value; }
        internal List<Rectangle> ResizeHandlesRef => _resizeHandles;
        internal string ResizeCornerRef { get => _resizeCorner; set => _resizeCorner = value; }
        internal Point ResizeAnchorRef { get => _resizeAnchor; set => _resizeAnchor = value; }

        internal List<Rectangle> TextEditHandlesRef => _textEditHandles;
        internal bool DraggingTextEditHandleRef { get => _draggingTextEditHandle; set => _draggingTextEditHandle = value; }
        internal string TehCornerRef { get => _tehCorner; set => _tehCorner = value; }
        internal Point TehAnchorRef { get => _tehAnchor; set => _tehAnchor = value; }
        internal TextBox? TehBoxRef { get => _tehBox; set => _tehBox = value; }

        // ── Drag-to-move ─────────────────────────────────────────────────────────────────────
        internal bool IsDraggingAnnotRef { get => _isDraggingAnnot; set => _isDraggingAnnot = value; }
        internal Point DragAnnotStartRef { get => _dragAnnotStart; set => _dragAnnotStart = value; }
        internal Point DragAnnotOrigPosRef { get => _dragAnnotOrigPos; set => _dragAnnotOrigPos = value; }
        internal PageAnnotation? DragAnnotRef { get => _dragAnnot; set => _dragAnnot = value; }

        // ── Crop tool ────────────────────────────────────────────────────────────────────────
        internal Rect CropCanvasRectRef { get => _cropCanvasRect; set => _cropCanvasRect = value; }
        internal Rectangle? CropPreviewRectBorderRef { get => _cropPreviewRectBorder; set => _cropPreviewRectBorder = value; }
        internal List<System.Windows.Shapes.Path> CropBracketsRef => _cropBrackets;
        internal List<Rectangle> CropHandlesRef => _cropHandles;
        internal string? ActiveCropHandleTagRef { get => _activeCropHandleTag; set => _activeCropHandleTag = value; }
        internal Point CropHandleDragStartRef { get => _cropHandleDragStart; set => _cropHandleDragStart = value; }
        internal Rect CropRectAtHandleDragRef { get => _cropRectAtHandleDrag; set => _cropRectAtHandleDrag = value; }
        internal TextBox? CropXBoxRef { get => _cropXBox; set => _cropXBox = value; }
        internal TextBox? CropYBoxRef { get => _cropYBox; set => _cropYBox = value; }
        internal TextBox? CropWBoxRef { get => _cropWBox; set => _cropWBox = value; }
        internal TextBox? CropHBoxRef { get => _cropHBox; set => _cropHBox = value; }
        internal TextBox? CropRangeBoxRef { get => _cropRangeBox; set => _cropRangeBox = value; }
        internal string CropUnitRef { get => _cropUnit; set => _cropUnit = value; }
        internal bool UpdatingCropInputsRef { get => _updatingCropInputs; set => _updatingCropInputs = value; }
        // Settable, not get-only: the moved crop code ASSIGNS both.
        internal Rectangle? CropPreviewRectSet { get => _cropPreviewRect; set => _cropPreviewRect = value; }
        internal Border? CropConfirmBarSet { get => _cropConfirmBar; set => _cropConfirmBar = value; }

        // ── Form filling ─────────────────────────────────────────────────────────────────────
        // Settable: ApplySessionState swaps the whole dictionary by reference on a tab switch, so
        // these cannot be get-only. Mutating in place is still what the editing code does; only the
        // tab switch rebinds them.
        internal Dictionary<int, string> FormTextValuesRef { get => _formTextValues; set => _formTextValues = value; }
        internal Dictionary<int, bool> FormCheckValuesRef { get => _formCheckValues; set => _formCheckValues = value; }
        internal Dictionary<string, string> FormRadioValuesRef { get => _formRadioValues; set => _formRadioValues = value; }
        internal Dictionary<int, double> FormFontSizesRef { get => _formFontSizes; set => _formFontSizes = value; }
        internal Border? FormSizeBarRef { get => _formSizeBar; set => _formSizeBar = value; }
        internal TextBox? ActiveFormTbRef { get => _activeFormTb; set => _activeFormTb = value; }
        internal int ActiveFormObjRef { get => _activeFormObj; set => _activeFormObj = value; }
        internal double ActiveFormScaleRef { get => _activeFormScale; set => _activeFormScale = value; }
        internal const string FormOverlayTagShared = "FormFieldOverlay";

        // ── Undo / dirty ─────────────────────────────────────────────────────────────────────
        // Settable: the tab switch swaps these by reference so each document keeps its own history.
        internal Stack<UndoEntry> UndoStackRef { get => _undoStack; set => _undoStack = value; }
        internal Stack<UndoEntry> RedoStackRef { get => _redoStack; set => _redoStack = value; }
        internal bool IsDirtyRef { get => _isDirty; set => _isDirty = value; }

        // ── State owned by files that did not move ───────────────────────────────────────────
        internal Border? SearchBarRef => _searchBar;                       // Search.cs
        internal Features.SearchController SearchCtl => Search;            // Search.cs
        internal bool OcrRegionModeRef { get => _ocrRegionMode; set => _ocrRegionMode = value; }   // Ocr.cs
        internal SavedSignature? PendingSignatureRef { get => _pendingSignature; set => _pendingSignature = value; }
        internal List<Point> ShapePolyPointsRef => _shapePolyPoints;       // Shapes.cs
        internal EditTool? AnnotBarToolRef { get => _annotBarTool; set => _annotBarTool = value; }
        internal bool AnnotBarMinimizedRef { get => _annotBarMinimized; set => _annotBarMinimized = value; }
        internal List<FrameworkElement> AnnotBarDragInnersRef => _annotBarDragInners;
        internal static SolidColorBrush SwatchDimBorderRef => _swatchDimBorder;

        // ══ What Tabs.cs reaches for, now that it lives in the viewer ═══════════════════════
        internal string? OriginalFileRef { get => _originalFile; set => _originalFile = value; }
        internal bool OpenedFromProtectedRef { get => _openedFromProtected; set => _openedFromProtected = value; }
        internal bool AsyncOpenPendingRef { get => _asyncOpenPending; set => _asyncOpenPending = value; }
        // Kept for the window-side callers; resolves to the FOCUSED pane's token (PageOperations.cs).
        internal System.Threading.CancellationTokenSource? ThumbCtsRef { get => _thumbCts; set => _thumbCts = value; }
        internal bool SidebarShowingOutlinesRef => _sidebarShowingOutlines;
        internal System.Collections.Generic.Stack<int> NavBackRef => _navBack;
        internal System.Collections.Generic.Stack<int> NavForwardRef => _navForward;

        internal TextBlock FileNameLabelCtl => FileNameLabel;
        internal TreeView OutlineTreeCtl => OutlineTree;
        internal Button SidebarOutlinesTabCtl => SidebarOutlinesTab;

        internal ContextMenu MakeThemedMenuBridge() => MakeThemedMenu();
        internal void CloseSearchBarBridge() => CloseSearchBar();
        internal void HideSignaturePopupBridge() => HideSignaturePopup();
        // Takes the calling pane: a pane's start screen is its own, not the focused pane's.
        internal void PopulateRecentFilesListBridge(Controls.PdfViewer pane) => PopulateRecentFilesList(pane);
        internal void SwitchSidebarToPagesTabBridge() => SwitchSidebarToPagesTab();
        internal void SyncSidebarToDocStateBridge(bool hasDoc, bool startup) => SyncSidebarToDocState(hasDoc, startup);
        internal void OpenFileBridge(string path) => OpenFile(path);
        internal void UpdateFooterFadeBridge() => UpdateFooterFade();
        internal void UpdateTabStripFadeBridge() => UpdateTabStripFade();
        /// <summary>Empty space on a pane's tab strip drags the window - the strips moved into the
        /// panes but dragging the window is still window chrome.</summary>
        internal void TitleBar_MouseLeftButtonDown_Bridge(object sender, MouseButtonEventArgs e)
            => TitleBar_MouseLeftButtonDown(sender, e);

        // ── Chrome the moved code touches ────────────────────────────────────────────────────
        internal TextBlock StatusTextCtl => StatusText;
        internal FrameworkElement ShortcutOverlayCtl => ShortcutOverlay;
        internal CheckBox LinkConfirmCheckCtl => LinkConfirmCheck;

        // ── Methods in partials that stayed on the window ────────────────────────────────────
        // Signatures mirror the originals exactly - verified against the definitions rather than
        // remembered, since several differ from the obvious guess (SaveTempAndReload takes two
        // optional flags, ApplySearchHighlights takes the target canvas, PlaceSignature and
        // PlaceImageFromDialog take a position and page).
        internal void MarkDirtyBridge(bool dirty = true) => MarkDirty(dirty);
        internal void SetToolBridge(EditTool t) => SetTool(t);
        internal void SaveTempAndReloadBridge(bool keepAnnotations = false, bool preserveZoom = false)
            => SaveTempAndReload(keepAnnotations, preserveZoom);
        internal void RecordNavJumpBridge() => RecordNavJump();
        internal static PageAnnotation? CloneAnnotationBridge(PageAnnotation a) => CloneAnnotation(a);
        internal PageAnnotation? PairPartnerBridge(PageAnnotation a) => PairPartner(a);
        internal void RenderStampsBridge(int page) => RenderStamps(page);
        internal void OpenStampToolBridge() => OpenStampTool();
        internal bool StampHitTestBridge(int page, Point pos) => StampHitTest(page, pos);
        internal void ApplySearchHighlightsBridge(int page, Canvas canvas) => ApplySearchHighlights(page, canvas);
        internal void HighlightSearchResultsOnCurrentPageBridge() => HighlightSearchResultsOnCurrentPage();
        internal void ShowTextSettingsBridge() => ShowTextSettings();
        internal void HideTextSettingsBridge() => HideTextSettings();
        internal void StyleEditBoxBridge(TextBox tb) => StyleEditBox(tb);
        internal void ApplyTextStyleToSelectionBridge() => ApplyTextStyleToSelection();
        internal static TextDecorationCollection? BuildDecorationsBridge(bool underline, bool strike)
            => BuildDecorations(underline, strike);
        internal void ShowDrawSettingsBridge(EditTool t) => ShowDrawSettings(t);
        internal void HideDrawSettingsBridge() => HideDrawSettings();
        internal Border MakeBarGripBridge(int dotCount = 3) => MakeBarGrip(dotCount);
        internal FrameworkElement BuildBarHostBridge(FrameworkElement content) => BuildBarHost(content);
        internal void PlaceAnnotationBarBridge(Border bar, Border grip, bool fadeIn = false)
            => PlaceAnnotationBar(bar, grip, fadeIn);
        internal static System.Windows.Media.Effects.DropShadowEffect AnnotBarShadowBridge() => AnnotBarShadow();
        internal void PlaceImageFromDialogBridge(Point pos, int pageIdx) => PlaceImageFromDialog(pos, pageIdx);
        internal void PlaceSignatureBridge(Point pos, int pageIdx) => PlaceSignature(pos, pageIdx);
        internal void ShowSignaturePopupBridge() => ShowSignaturePopup();
        internal void FillSignFieldBridge(bool initials, int objNum, int pageIndex,
                                          double x, double y, double w, double h)
            => FillSignField(initials, objNum, pageIndex, x, y, w, h);
        internal void ShapeToolMouseDownBridge(int pageIdx, Point pos, System.Windows.Input.MouseButtonEventArgs e)
            => ShapeToolMouseDown(pageIdx, pos, e);
        internal void CommitShapeDragBridge(int pageIdx) => CommitShapeDrag(pageIdx);
        internal void UpdateShapePolyRubberBridge(System.Windows.Input.MouseEventArgs e) => UpdateShapePolyRubber(e);
        internal void OcrRegionBridge(int pageIdx, Rect canvasBounds) => OcrRegion(pageIdx, canvasBounds);
        internal void ShowShortcutsOverlayExclusiveBridge() => ShowShortcutsOverlayExclusive();
        internal static void FadeOverlayOutBridge(UIElement el) => FadeOverlayOut(el);
        internal static void FadeOutAndRemoveBarBridge(Border? bar) => FadeOutAndRemoveBar(bar);
        internal static PdfSharpCore.Pdf.PdfItem DerefItemBridge(PdfSharpCore.Pdf.PdfItem item) => DerefItem(item);
        internal static string WordsToTextBridge(IEnumerable<UglyToad.PdfPig.Content.Word> src) => WordsToText(src);
        internal static MenuItem MakeMenuItemBridge(string header, RoutedEventHandler click,
                                                    string? gesture = null, string? glyph = null)
            => MakeMenuItem(header, click, gesture, glyph);
    }
}
