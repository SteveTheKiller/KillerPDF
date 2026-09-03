using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KillerPDF.Controls
{
    /// <summary>
    /// Editing state and narrow shell-facing adapters owned by each viewer instance. Annotations,
    /// text editing, crop, forms, links, selection and the current tool remain independent between
    /// panes; only window chrome is routed through the host.
    /// </summary>
    public partial class PdfViewer
    {
        // ── Selection ────────────────────────────────────────────────────────────────────────
        private PageAnnotation? _selectedAnnotation;
        private Border? _selectionBorder;
        private readonly List<PageAnnotation> _selectedSet = [];
        private readonly List<Border> _selectionOutlines = [];
        private Rectangle? _pairedCoverOutline;
        private Rectangle? _reeditCoverOutline;
        private string? _selectedText;
        private readonly List<(PageAnnotation a, Point orig)> _dragGroupOrig = [];

        // ── Draw / highlight tool ────────────────────────────────────────────────────────────
        private Color _drawColor = Colors.Red;
        private double _drawWidth = 3;
        private byte _drawOpacity = 255;
        private bool _lineLevel = true;
        private bool _highlightErase;
        private bool _drawErase;
        private Color _highlightColor = Color.FromArgb(80, 255, 255, 0);
        private Color _lineAnnotColor = Color.FromArgb(255, 220, 38, 38);
        private InkAnnotation? _activeInk;

        // ── Text (typewriter) tool ───────────────────────────────────────────────────────────
        private TextBox? _activeTextBox;
        private double _textFontSize = 24;
        private double _textLetterSpacing;
        private string _textFontName = "Segoe UI";
        private bool _textBold;
        private bool _textItalic;
        private bool _textStrike;
        private bool _textUnderline;
        private Color _textColor = Colors.Black;
        private byte _textOpacity = 255;
        private Color _textFillColor = Color.FromArgb(0, 255, 255, 255);
        private TextAnnotation? _reeditOriginal;
        private CoverAnnotation? _pendingCover;
        private bool _pendingEditWasDirty;
        private Border? _textSettingsBar;
        private const double EditTextSizeCorrection = 0.8;
        private const double TextBoxDefaultWidth = 220;

        // ── Resize handles ───────────────────────────────────────────────────────────────────
        private bool _isResizingSig;
        private Point _resizeSigStart;
        private double _resizeSigStartScale;
        private PlacedAnnotation? _resizeSigAnnot;
        private TextAnnotation? _resizeTextAnnot;
        private HighlightAnnotation? _resizeHlAnnot;
        private InkAnnotation? _resizeInkAnnot;
        private List<Point>? _resizeInkOrigPoints;
        private Rect _resizeInkOrigBounds;
        private readonly List<Rectangle> _resizeHandles = [];
        private string _resizeCorner = "SE";
        private Point _resizeAnchor;

        private readonly List<Rectangle> _textEditHandles = [];
        private bool _draggingTextEditHandle;
        private string _tehCorner = "SE";
        private Point _tehAnchor;
        private TextBox? _tehBox;

        // ── Drag-to-move ─────────────────────────────────────────────────────────────────────
        private bool _isDraggingAnnot;
        private Point _dragAnnotStart;
        private Point _dragAnnotOrigPos;
        private PageAnnotation? _dragAnnot;

        // ── Crop tool ────────────────────────────────────────────────────────────────────────
        private Rect _cropCanvasRect;
        private Rectangle? _cropPreviewRectBorder;
        private readonly List<System.Windows.Shapes.Path> _cropBrackets = [];
        private readonly List<Rectangle> _cropHandles = [];
        private string? _activeCropHandleTag;
        private Point _cropHandleDragStart;
        private Rect _cropRectAtHandleDrag;
        private TextBox? _cropXBox;
        private TextBox? _cropYBox;
        private TextBox? _cropWBox;
        private TextBox? _cropHBox;
        private TextBox? _cropRangeBox;
        private string _cropUnit = "pt";
        private bool _updatingCropInputs;

        // ── Form filling ─────────────────────────────────────────────────────────────────────
        private Dictionary<string, string> _formTextValues = [];
        private Dictionary<string, string> _formChoiceValues = [];
        private Dictionary<string, IReadOnlyList<string>> _formMultiChoiceValues = [];
        private Dictionary<string, bool> _formCheckValues = [];
        private Dictionary<string, string> _formRadioValues = [];
        private Dictionary<string, double> _formFontSizes = [];
        private Border? _formSizeBar;
        private TextBox? _activeFormTb;
        private string _activeFormName = string.Empty;
        private double _activeFormScale = 1;
        private const string FormOverlayTag = "FormFieldOverlay";

        // ── Undo / dirty ─────────────────────────────────────────────────────────────────────
        private Stack<UndoEntry> _undoStack = new();
        private Stack<UndoEntry> _redoStack = new();
        private bool _isDirty;

        // ── State owned by files that did not move ───────────────────────────────────────────
        private Border? _searchBar => Host!.SearchBar;
        private Features.SearchController Search => Host!.Search;
        private bool _ocrRegionMode;
        private SavedSignature? _pendingSignature;
        private readonly List<Point> _shapePolyPoints = [];
        private EditTool? _annotBarTool;
        private bool _annotBarMinimized;
        private readonly List<FrameworkElement> _annotBarDragInners = [];
        private SolidColorBrush _swatchDimBorder => Host!.SwatchDimBorder;

        // ══ What Tabs.cs reaches for, now that it lives here ════════════════════════════════
        private string? _originalFile;
        private bool _openedFromProtected;
        private bool _asyncOpenPending;
        // This pane's own loader token, NOT the window's - see ThumbCts in PdfViewer.TabsApi.cs.
        private System.Threading.CancellationTokenSource? _thumbCts { get => ThumbCts; set => ThumbCts = value; }
        private bool _sidebarShowingOutlines => Host!.SidebarShowingOutlines;
        private readonly System.Collections.Generic.Stack<int> _navBack = new();
        private readonly System.Collections.Generic.Stack<int> _navForward = new();

        private TextBlock FileNameLabel => Host!.FileNameLabel;
        private TreeView OutlineTree => Host!.OutlineTree;
        private Button SidebarOutlinesTab => Host!.SidebarOutlinesTab;

        private ContextMenu MakeThemedMenu() => Host!.MakeThemedMenu();
        private void CloseSearchBar() => Host!.CloseSearchBar();
        private void HideSignaturePopup() => Host!.HideSignaturePopup();
        private void PopulateRecentFilesList() => Host!.PopulateRecentFilesList(this);
        private void SwitchSidebarToPagesTab() => Host!.SwitchSidebarToPagesTab();
        private void SyncSidebarToDocState(bool hasDoc, bool startup) => Host!.SyncSidebarToDocState(hasDoc, startup);
        private void OpenFile(string path) => Host!.OpenFile(path);
        private void UpdateFooterFade() => Host!.UpdateFooterFade();
        private void UpdateTabStripFade() => Host!.UpdateTabStripFade();

        // ── Chrome ───────────────────────────────────────────────────────────────────────────
        private TextBlock StatusText => Host!.StatusText;
        private FrameworkElement ShortcutOverlay => Host!.ShortcutOverlay;
        private CheckBox LinkConfirmCheck => Host!.LinkConfirmCheck;

        // ── Methods still on the window ──────────────────────────────────────────────────────
        private void MarkDirty(bool dirty = true) => Host!.MarkDirty(dirty);
        private void SetTool(EditTool t) => Host!.SetTool(t);
        private void SaveTempAndReload(bool keepAnnotations = false, bool preserveZoom = false,
            Action<string>? finalizeSavedFile = null,
            Action<Dictionary<int, int>>? remapRotations = null,
            int? selectedPageAfterReload = null,
            UndoEntry? documentUndo = null,
            bool preserveRenderedPages = false)
            => Host!.SaveTempAndReload(
                keepAnnotations, preserveZoom, finalizeSavedFile, remapRotations,
                selectedPageAfterReload, documentUndo, preserveRenderedPages);
        private void RecordNavJump() => Host!.RecordNavJump();
        private PageAnnotation? CloneAnnotation(PageAnnotation a) => Host!.CloneAnnotation(a);
        private PageAnnotation? PairPartner(PageAnnotation a) => Host!.PairPartner(a);
        private void RenderStamps(int page) => Host!.RenderStamps(page);
        private void OpenStampTool() => Host!.OpenStampTool();
        private bool StampHitTest(int page, Point pos) => Host!.StampHitTest(page, pos);
        private void ApplySearchHighlights(int page, Canvas canvas) => Host!.ApplySearchHighlights(page, canvas);
        private void HighlightSearchResultsOnCurrentPage() => Host!.HighlightSearchResultsOnCurrentPage();
        private void ShowTextSettings() => Host!.ShowTextSettings();
        private void HideTextSettings() => Host!.HideTextSettings();
        private void StyleEditBox(TextBox tb) => Host!.StyleEditBox(tb);
        private void ApplyTextStyleToSelection() => Host!.ApplyTextStyleToSelection();
        private TextDecorationCollection? BuildDecorations(bool underline, bool strike)
            => Host!.BuildDecorations(underline, strike);
        private void ShowDrawSettings(EditTool t) => Host!.ShowDrawSettings(t);
        private void HideDrawSettings() => Host!.HideDrawSettings();
        private Border MakeBarGrip(int dotCount = 3) => Host!.MakeBarGrip(dotCount);
        private FrameworkElement BuildBarHost(FrameworkElement content) => Host!.BuildBarHost(content);
        private void PlaceAnnotationBar(Border bar, Border grip, bool fadeIn = false)
            => Host!.PlaceAnnotationBar(bar, grip, fadeIn);
        private System.Windows.Media.Effects.DropShadowEffect AnnotBarShadow() => Host!.AnnotBarShadow();
        private void PlaceImageFromDialog(Point pos, int pageIdx) => Host!.PlaceImageFromDialog(pos, pageIdx);
        private void PlaceSignature(Point pos, int pageIdx) => Host!.PlaceSignature(pos, pageIdx);
        private void ShowSignaturePopup() => Host!.ShowSignaturePopup();
        private void FillSignField(bool initials, int objNum, int pageIndex,
                                   double x, double y, double w, double h)
            => Host!.FillSignField(initials, objNum, pageIndex, x, y, w, h);
        private void ShapeToolMouseDown(int pageIdx, Point pos, MouseButtonEventArgs e)
            => Host!.ShapeToolMouseDown(pageIdx, pos, e);
        private void CommitShapeDrag(int pageIdx) => Host!.CommitShapeDrag(pageIdx);
        private void UpdateShapePolyRubber(MouseEventArgs e) => Host!.UpdateShapePolyRubber(e);
        private void OcrRegion(int pageIdx, Rect canvasBounds) => Host!.OcrRegion(pageIdx, canvasBounds);
        private void ShowShortcutsOverlayExclusive() => Host!.ShowShortcutsOverlayExclusive();
        private void FadeOverlayOut(UIElement el) => Host!.FadeOverlayOut(el);
        private void FadeOutAndRemoveBar(Border? bar) => Host!.FadeOutAndRemoveBar(bar);
        private string WordsToText(IEnumerable<KillerPdf.Engine.Documents.PdfExtractedWord> src) => Host!.WordsToText(src);
        private MenuItem MakeMenuItem(string header, RoutedEventHandler click,
                                      string? gesture = null, string? glyph = null)
            => Host!.MakeMenuItem(header, click, gesture, glyph);
    }
}
