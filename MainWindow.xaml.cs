using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using KillerPDF.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace KillerPDF
{
    public partial class MainWindow : Window
    {
        private PdfDocument? _doc;
        private string? _currentFile;
        private string? _originalFile;  // user's real file path; survives temp swaps from crop/rotate, used by Save
        private Point _dragStartPoint;

        // Zoom
        private double _zoomLevel = 1.0;
        private double _lastRenderZoom = 1.0;
        private int _renderedPrimaryPage = -1;   // primary (spread-left) page currently rasterised
        private const double ZoomMin = 0.05;
        private const double ZoomMax = 5.0;
        private const double ZoomStep = 0.15;
        private enum FitMode { None, Width, Page }
        private FitMode _fitMode = FitMode.None;
        private System.Windows.Threading.DispatcherTimer? _rerenderTimer;
        private System.Threading.CancellationTokenSource? _secondaryRenderCts;
        private enum ViewMode { Single, Continuous, TwoPage, Grid }
        private ViewMode _viewMode = ViewMode.Continuous;
        private readonly StackPanel _continuousPanel = null!;
        private System.Threading.CancellationTokenSource? _continuousRenderCts;
        private System.Threading.CancellationTokenSource? _continuousSharpenCts;   // #85 visible-page re-sharpen
        private readonly HashSet<int> _continuousSharpPages = [];   // slots currently holding a hi-res bitmap
        private int _continuousSharpW;                              // budget those slots were sharpened at
        private readonly List<double> _continuousTops = [];
        private int _gridScrollToPage = -1;   // page to scroll to once its grid tile streams in (-1 = none)
        private int _continuousScrollTarget = -1;  // re-scroll here once its true height is known
        private double _continuousPageW;

        // Editing
        private EditTool _currentTool = EditTool.Select;
        // Per-document state. Not readonly: tab switching swaps these by reference so each
        // open document keeps its own annotations, undo history, form values, and search hits.
        private Dictionary<int, List<PageAnnotation>> _annotations = [];
        private Dictionary<int, (int w, int h)> _renderDims = [];
        // Stores the PDF /Rotate value for each page.  The temp file used by Docnet has
        // rotation stripped to zero so FPDF_GetPageWidth/Height returns MediaBox dims and
        // the content isn't clipped; RotateBitmap is applied at render time instead.
        private Dictionary<int, int> _pageRotations = [];

        // Form filling - text/check keyed by widget object number; radio keyed by field name
        private Dictionary<int, string>    _formTextValues  = [];
        private Dictionary<int, bool>      _formCheckValues = [];
        private Dictionary<string, string> _formRadioValues = [];
        private Dictionary<int, double>    _formFontSizes   = [];   // per-field user font-size override (points)
        // Floating font-size stepper shown while a form text field is focused.
        private Border?  _formSizeBar;
        private TextBox? _activeFormTb;
        private int      _activeFormObj;
        private double   _activeFormScale = 1;
        private const string FormOverlayTag = "FormFieldOverlay";

        // Undo stack - each entry is either an annotation removal or a full document snapshot.
        // AnnotationGroup removes a specific set of annotations in one step (a text edit = cover + text).
        private enum UndoKind { Annotation, Document, StampBatch, ClearAnnotations, AnnotationGroup, PageSnapshot }
        private readonly record struct UndoEntry(UndoKind Kind, int PageIdx = -1, byte[]? DocBytes = null, bool WasDirty = false, int[]? Pages = null, PageAnnotation? Annot = null, Dictionary<int, List<PageAnnotation>>? AnnotSnapshot = null, List<PageAnnotation>? AnnotGroup = null);
        private Stack<UndoEntry> _undoStack = new();
        private bool _isDrawing;
        private Point _drawStart;
        private UIElement? _activePreview;
        private InkAnnotation? _activeInk;
        private TextBox? _activeTextBox;
        private PageAnnotation? _selectedAnnotation;
        private Border? _selectionBorder;
        // Shift+click multi-selection (Select tool): extra annotations selected alongside the
        // primary _selectedAnnotation. Each gets its own outline. Delete removes the whole set.
        private readonly List<PageAnnotation> _selectedSet = [];
        private readonly List<Border> _selectionOutlines = [];

        // Draw/Highlight settings
        private Color _drawColor = Colors.Red;
        private double _drawWidth = 3;
        private byte _drawOpacity = 255;
        private bool _lineLevel = true;   // Line tool: keep the line perfectly horizontal (default on)
        private bool _highlightErase;     // Highlight tool: drag a box to delete annotations inside it
        private bool _drawErase;          // Draw tool: brush over annotations to delete them
        private Color _highlightColor = Color.FromArgb(80, 255, 255, 0);
        // Strikethrough / underline lines: opaque red by default.
        private Color _lineAnnotColor = Color.FromArgb(255, 220, 38, 38);
        private Border? _drawSettingsBar;

        // Text (typewriter) tool settings
        private double _textFontSize = 24;
        // Current text-tool typeface and style (mirrors the text bar; carried onto each new/edited box).
        private string _textFontName = "Segoe UI";
        private bool _textBold;
        private bool _textItalic;
        private bool _textStrike;
        private bool _textUnderline;
        // Installed font-family names, sorted, computed once (the text bar rebuilds often).
        private static List<string>? _systemFontNamesCache;
        internal static List<string> SystemFontNames => _systemFontNamesCache ??=
            [.. System.Windows.Media.Fonts.SystemFontFamilies
                .Select(f => f.Source).Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase)];
        // WPF renders a given point size visually ~25% larger than the source PDF text, so scale the
        // detected size down when seeding an existing-text edit. The user can still fine-tune after.
        private const double EditTextSizeCorrection = 0.8;
        private bool _suppressSizeSync;   // guards the slider<->size-box two-way binding from feedback loops
        private TextAnnotation? _reeditOriginal;  // placed-text annotation currently being re-edited
        // The opaque cover dropped when starting an existing-text edit, awaiting its paired text commit.
        // Held so the text commit can group both into one undo, and so cancel/empty removes the cover.
        private CoverAnnotation? _pendingCover;
        // Dirty state captured before the cover was dropped, so undoing the grouped edit restores it.
        private bool _pendingEditWasDirty;
        private Color _textColor = Colors.Black;
        private byte _textOpacity = 255;          // alpha applied to placed text, like the draw tool
        private Color _textFillColor = Color.FromArgb(0, 255, 255, 255);  // text box background fill; A==0 = no fill
        private const double TextBoxDefaultWidth = 220;  // canvas-unit width of a freshly placed text box
        private Border? _textSettingsBar;

        // Signature / image resize
        private bool _isResizingSig;
        private Point _resizeSigStart;
        private double _resizeSigStartScale;
        private PlacedAnnotation? _resizeSigAnnot;
        private TextAnnotation? _resizeTextAnnot;               // text box being width-resized (height auto-fits)
        private HighlightAnnotation? _resizeHlAnnot;            // highlight/line being corner-resized (Bounds)
        private InkAnnotation? _resizeInkAnnot;                 // ink stroke being corner-resized (points scaled)
        private List<Point>? _resizeInkOrigPoints;             // snapshot of ink points at resize start
        private Rect _resizeInkOrigBounds;                      // ink bounding box at resize start
        private readonly List<Rectangle> _resizeHandles = [];   // 4 corner handles for placed annotations
        private string _resizeCorner = "SE";                    // which corner is being dragged
        private Point _resizeAnchor;                            // opposite corner, held fixed during resize

        // Mid-edit resize handles: 4 corners shown around the live editing TextBox so the user can
        // resize the box (and continue typing) without committing and re-selecting first.
        private readonly List<Rectangle> _textEditHandles = [];
        private bool _draggingTextEditHandle;
        private string _tehCorner = "SE";
        private Point _tehAnchor;
        private TextBox? _tehBox;

        // Placed annotation drag-to-move
        private bool _isDraggingAnnot;
        private Point _dragAnnotStart;

        // Middle-mouse / spacebar pan
        private bool _isPanning;
        private bool _spaceHeld;
        private Point _panStart;
        private double _panScrollH;
        private double _panScrollV;
        private Point _dragAnnotOrigPos;
        private PageAnnotation? _dragAnnot;   // placed image/signature OR typewriter text

        // Crop tool
        private Rect _cropCanvasRect;
        private Rectangle? _cropPreviewRect;
        private Rectangle? _cropPreviewRectBorder;  // unused after refactor; kept to avoid null-ref in cleanup
        private readonly List<System.Windows.Shapes.Path> _cropBrackets = []; // L-bracket corner visuals
        private Border? _cropConfirmBar;
        private readonly Button _toolCropBtn = null!;
        private readonly Button _toolRotateBtn = null!;
        private readonly List<Rectangle> _cropHandles = [];
        private string? _activeCropHandleTag; // "NW" | "NE" | "SE" | "SW"
        private Point _cropHandleDragStart;
        private Rect _cropRectAtHandleDrag;
        private int _cropPageIndex = -1;   // page the crop rect was drawn on (grid/two-page aware)
        private TextBox? _cropXBox, _cropYBox, _cropWBox, _cropHBox;   // GIMP-style: top-left X/Y + width/height
        private TextBox? _cropRangeBox;
        private string   _cropUnit = "pt";   // crop coordinate display unit: "pt" | "in" | "%"
        private bool     _updatingCropInputs;

        // PDF link overlays (rendered on top of the annotation canvas)
        private readonly List<Canvas> _linkOverlays = [];

        // Sidebar + multi-page view
        private bool _sidebarCollapsed;
        private bool _sidebarRight;   // false = sidebar on the left (default), true = on the right
        private bool   _sidebarShowingOutlines;
        private bool   _outlinesFitted     = false;
        private double _savedPagesWidth    = 180;
        private double _savedOutlinesWidth = 300;
        private readonly Button _sidebarToggleBtn = null!;
        private readonly Border _sidebarBorder = null!;
        private ColumnDefinition _sidebarCol = null!;   // sized column (left or right per _sidebarRight)
        private readonly WrapPanel _pageContentPanel = null!;

        // Text selection
        private bool _isSelecting;
        private Point _selectStart;
        private Rectangle? _selectRect;
        private Rectangle? _pairedCoverOutline;   // dashed hint over a cover while its paired text is selected
        private Rectangle? _reeditCoverOutline;   // dashed hint over a cover while its paired text is being re-edited
        private string? _selectedText;

        // Search
        private Border? _searchBar;
        private TextBox? _searchBox;
        private TextBlock? _searchStatus;
        private readonly List<Rect> _searchHighlights = [];

        // Signatures
        private readonly SignatureStore _signatureStore = new();
        private SavedSignature? _pendingSignature;
        private Border? _signaturePopup;
        // Guided AcroForm signing: "pick once, reuse" - the chosen signature/initials are remembered
        // and dropped into every matching field. _pendingSignField, when set, routes the next pick from
        // the popup into that field instead of free placement.
        private SavedSignature? _activeSignatureChoice;
        private SavedSignature? _activeInitialsChoice;
        private (bool Initials, int ObjNum, int Page, double X, double Y, double W, double H)? _pendingSignField;
        // Form fields already signed, so re-clicking one offers change/remove instead of re-stamping.
        private readonly Dictionary<int, SignatureAnnotation> _signedFields = [];

        // Manual element refs. Tile-0's Image + overlay are built in code (BuildPrimaryTile) now that the
        // primary page is no longer a hardcoded XAML singleton - both are reassignable.
        private Canvas _annotationCanvas = null!;
        private Image PageImage = null!;
        // Active annotation surface. Single view: always _annotationCanvas. Continuous view:
        // set on mouse-down to the clicked page's overlay. Shared handlers target this.
        private Canvas _activeCanvas = null!;
        // The page surface a pointer gesture started on, captured on mouse-down. Kept separate
        // from _activeCanvas because RenderAllAnnotations reuses _activeCanvas as its render
        // target; in Grid view tiles stream in asynchronously and each one re-points _activeCanvas
        // mid-gesture, which previously committed annotations to the wrong page and broke
        // select/delete. Mouse-move/up resolve the gesture page and surface from these instead.
        private Canvas? _gestureCanvas;
        private int _gesturePage = -1;
        // Per-page overlay canvases for Continuous view, keyed by page index.
        private readonly Dictionary<int, Canvas> _continuousCanvases = [];
        // Unified page -> overlay map covering EVERY rendered page, the primary included (unlike
        // _continuousCanvases, which holds only secondary tiles and is driven by the tile-recycling
        // machinery). This is the single source of truth the canvas accessors read from, so the
        // primary stops being a special case in routing/search/links.
        private readonly Dictionary<int, Canvas> _pages = [];
        private readonly Grid _pageContentGrid = null!;
        private readonly Button _toolSelectBtn = null!;
        private readonly Button _toolTextBtn = null!;
        private readonly Button _toolHighlightBtn = null!;
        private readonly Button _toolUnderlineBtn = null!;
        private readonly Button _toolDrawBtn = null!;
        private readonly Button _toolSignatureBtn = null!;
        private readonly Button _toolImageBtn = null!;
        private readonly Button _saveAsBtnRef = null!;
        private readonly Button _closeFileBtnRef = null!;
        private readonly ComboBox _zoomBox = null!;
        private readonly StackPanel _portableBadge = null!;
        private readonly TextBox _pageJumpBox = null!;
        private readonly TextBlock _pageTotalLabel = null!;

        // Dirty / unsaved-change tracking
        private bool _isDirty = false;

        // Whole-document search results (PDF-space rects per page)
        private Dictionary<int, List<(double left, double bottom, double right, double top)>> _allSearchRects = [];
        private List<int> _searchResultPages = [];
        private int _searchPageCursor = -1;

        public MainWindow()
        {
            InitializeComponent();
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (v != null) VersionLabel.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
            // Accept dropped files/folders/archives anywhere on the window (not just the empty drop zone),
            // so dropping onto an open document works too. The empty-state DropZone marks its own drop
            // handled, so a drop there isn't processed twice.
            AllowDrop = true;
            DragOver += DropZone_DragOver;
            Drop     += DropZone_Drop;
            // Safety net: if the window loses focus mid-drag/resize (e.g. Alt-Tab away to type elsewhere),
            // the mouse-up can be lost and the dragged annotation would stay glued to the cursor with the
            // canvas still holding mouse capture. End any in-progress gesture on deactivate so control is
            // restored the moment the user comes back.
            Deactivated += (_, _) => { if (_isDraggingAnnot || _isResizingSig || _textDragging) FinishStuckGesture(); };
            _pageContentGrid = (Grid)FindName("PageContentGrid")!;
            _toolSelectBtn = (Button)FindName("ToolSelectBtn")!;
            _toolTextBtn = (Button)FindName("ToolTextBtn")!;
            _toolHighlightBtn = (Button)FindName("ToolHighlightBtn")!;
            _toolUnderlineBtn = (Button)FindName("ToolUnderlineBtn")!;
            _toolDrawBtn = (Button)FindName("ToolDrawBtn")!;
            _toolSignatureBtn = (Button)FindName("ToolSignatureBtn")!;
            _toolImageBtn = (Button)FindName("ToolImageBtn")!;
            _toolCropBtn = (Button)FindName("ToolCropBtn")!;
            _toolRotateBtn = (Button)FindName("ToolRotateBtn")!;
            _sidebarToggleBtn = (Button)FindName("SidebarToggleBtn")!;
            _sidebarBorder = (Border)FindName("SidebarBorder")!;
            _sidebarCol = (ColumnDefinition)FindName("SidebarCol")!;
            _pageContentPanel = (WrapPanel)FindName("PageContentPanel")!;
            BuildPrimaryTile();              // code-built tile-0 (replaces the former XAML PageImage + AnnotationCanvas)
            _activeCanvas = _annotationCanvas;
            _saveAsBtnRef = (Button)FindName("SaveAsBtn")!;
            _closeFileBtnRef = (Button)FindName("CloseFileBtn")!;
            _zoomBox = (ComboBox)FindName("ZoomBox")!;
            // Read-only editable combo: hide its text-selection highlight so the displayed % never
            // looks like selected text after a pick.
            _zoomBox.Loaded += (_, _) =>
            {
                if (_zoomBox.Template?.FindName("PART_EditableTextBox", _zoomBox) is TextBox etb)
                    etb.SelectionBrush = System.Windows.Media.Brushes.Transparent;
            };
            _portableBadge = (StackPanel)FindName("PortableBadge")!;
            _pageJumpBox = (TextBox)FindName("PageJumpBox")!;
            _pageTotalLabel = (TextBlock)FindName("PageTotalLabel")!;
            _continuousPanel = (StackPanel)FindName("ContinuousPanel")!;
            PagePreviewPanel.ScrollChanged += PagePreviewPanel_ScrollChanged;
            PreviewMouseDown += SettingsDismiss_PreviewMouseDown;   // non-modal Settings: close on outside click
            // Keep the (bottom-anchored) Settings panel sized to the document area while the window
            // resizes, so it tracks smoothly instead of snapping on the next open.
            MainContentGrid.SizeChanged += (_, _) =>
            {
                if (SettingsOverlay.Visibility == Visibility.Visible) PositionSettingsPanel();
                ScheduleFadeRefresh();
            };
            // The sidebar column resizes via the splitter / collapse; track its width so the tab-strip
            // shadow gradient stays clipped to the document column.
            if (FindName("SidebarOuterGrid") is FrameworkElement sidebarOuter)
                sidebarOuter.SizeChanged += (_, _) => ScheduleFadeRefresh();
            // The footer shadow tracks the document pane's actual position; re-anchor when it (or the
            // tab strip, which shifts the document) changes size.
            DocPaneBorder.SizeChanged += (_, _) => ScheduleFadeRefresh();
            TabStripBorder.SizeChanged += (_, _) => { ScheduleFadeRefresh(); ScheduleTabReflow(); };
            // After a sidebar-splitter drag, snap fully closed if dragged too narrow, else save the width.
            SidebarSplitter.PreviewMouseLeftButtonUp += (_, _) => OnSidebarResized();
            // Grabbing the splitter while collapsed reveals the page list so it can be pulled open.
            SidebarSplitter.PreviewMouseLeftButtonDown += (_, _) => { _sidebarWantClose = false; OnSidebarSplitterPress(); };
            // Pull the splitter well past the minimum mid-drag to close (the column itself can't clip).
            SidebarSplitter.PreviewMouseMove += OnSidebarSplitterMove;
            // If a drag is interrupted (alt-tab, focus loss, taking a screenshot), finalize it so the
            // sidebar can't get stuck half-open with its content hidden.
            SidebarSplitter.LostMouseCapture += (_, _) => OnSidebarResized();
            if (Enum.TryParse<ViewMode>(App.GetSetting("ViewMode"), out var savedVm))
                _viewMode = savedVm;
            if (Enum.TryParse<ToolbarStyle>(App.GetSetting("ToolbarStyle"), out var savedTb))
                _toolbarStyle = savedTb;
            if (string.Equals(App.GetSetting("SidebarSide"), "Right", StringComparison.OrdinalIgnoreCase))
                _sidebarRight = true;
            RestoreToolSettings();   // Draw + Text tool styles carry across sessions
            Loaded += (_, _) => AdjustZoomBoxWidth();   // fit the zoom box to the longest localized term
            IndexToolbarButtons();
            OutlineTree.SelectedItemChanged += OutlineTree_SelectedItemChanged;
            LoadSignatures();
            BuildContextMenu();
            SetTool(EditTool.Select);
            ApplyGrainTexture();
            ApplyToolNumberTooltips();   // append the 1-9 toolbar positions to the tool tooltips
            BuildShortcutsOverlay();     // generate the shortcuts card from the single-source table (ShortcutsOverlay.cs)
            SourceInitialized += MainWindow_SourceInitialized;
            Closed += (_, _) => { _continuousRenderCts?.Cancel(); _doc?.Close(); CloseLinkPdfiumDoc(); App.CleanupSessionTemps(); };

            // Open a file passed via command-line / file association (e.g. double-clicking a .pdf)
            // Also show the portable badge when running outside the install location.
            bool contentRevealed = false;
            ContentRendered += (_, _) =>
            {
                Services.ThemeManager.RefreshIcons();
                // Final pass once the layout has real widths. The tab-strip / footer shadow gradients
                // were intermittently blank at startup (their feather mask + margin were computed
                // before the sidebar column had measured), and only a manual sidebar tweak forced a
                // correct re-layout. Re-running it here reproduces that fix automatically.
                UpdateTabStripFade();
                // The content is held invisible (RootClipGrid.Opacity=0 in XAML) until this final
                // positioning pass has run; fade it in once so the brief unpositioned first frame
                // (the "load deform" - shadows/toolbars snapping into place) is never visible.
                if (!contentRevealed)
                {
                    contentRevealed = true;
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
                    {
                        var reveal = new System.Windows.Media.Animation.DoubleAnimation(0, 1,
                            new Duration(TimeSpan.FromMilliseconds(140)));
                        RootClipGrid.BeginAnimation(OpacityProperty, reveal);
                    }));
                }
            };
            Services.ThemeManager.ThemeChanged += OnThemeChanged;

            Loaded += (_, _) =>
            {
                RestoreWindowSettings();
                ApplySidebarSide();   // place the sidebar on the saved side (default left)
                if (_toolbarStyle != ToolbarStyle.SmallIcons) ApplyToolbarAppearance();

                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1 && System.IO.File.Exists(args[1]))
                {
                    OpenInNewTab(args[1]);
                }
                else
                {
                    // Reopen every tab from the last session (falls back to the single LastFile for
                    // settings written before multi-tab restore existed).
                    var saved = App.GetSetting("OpenTabs");
                    string[] paths = !string.IsNullOrEmpty(saved)
                        ? saved!.Split('|')
                        : (App.GetSetting("LastFile") is { Length: > 0 } lf ? [lf] : []);
                    // Lazy restore: create a placeholder tab for each saved file but load only the
                    // focused one. The rest materialize (load + render) the first time they're clicked,
                    // so startup cost no longer scales with how many tabs were open last session.
                    _sessions.Clear();
                    bool openedAny = false;
                    foreach (var f in paths)
                        if (!string.IsNullOrEmpty(f) && System.IO.File.Exists(f))
                        {
                            _sessions.Add(new DocumentSession { OriginalFile = f, CurrentFile = f, DeferredPath = f });
                            openedAny = true;
                        }
                    if (!openedAny)
                    {
                        _active = null;
                        PopulateRecentFilesList();   // empty state: show the recent list
                        EnsureInitialSession();
                        RebuildTabStrip();
                    }
                    else
                    {
                        var wantActive = App.GetSetting("ActiveTab");
                        var activeTarget = (!string.IsNullOrEmpty(wantActive)
                                ? _sessions.FirstOrDefault(ss => string.Equals(ss.OriginalFile, wantActive, StringComparison.OrdinalIgnoreCase))
                                : null)
                            ?? _sessions[0];
                        _active = activeTarget;
                        ApplySessionState(activeTarget);
                        MaterializeDeferred(activeTarget);   // load + render only the focused tab
                        RebuildTabStrip();
                    }
                }

                if (App.IsPortable())
                    _portableBadge.Visibility = Visibility.Visible;
            };
        }

        // ============================================================
        // Maximize-respects-taskbar fix (WindowStyle=None needs WM_GETMINMAXINFO)
        // ============================================================

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            ThemeManager.ApplyDwm(hwnd);
            // Snapping moves the window without changing WindowState, so re-evaluate the rounded vs
            // squared chrome on every move (and once now that the handle exists).
            LocationChanged += OnWindowLocationChanged;
            UpdateWindowChrome();
        }

        // ============================================================
        // Settings persistence (window size, zoom, last file)
        // ============================================================

        private void SaveWindowSettings()
        {
            try
            {
                App.SetSetting("WindowState", WindowState.ToString());
                if (WindowState == WindowState.Normal)
                {
                    App.SetSetting("WindowWidth",  ((int)ActualWidth).ToString());
                    App.SetSetting("WindowHeight", ((int)ActualHeight).ToString());
                    App.SetSetting("WindowTop",  ((int)Top).ToString());
                    App.SetSetting("WindowLeft", ((int)Left).ToString());
                }
                App.SetSetting("FitMode",   _fitMode.ToString());
                App.SetSetting("ZoomLevel", _zoomLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
                // #105: honor the "remember open files" privacy choice. Unset or "1" = remember
                // (default, preserves prior behavior); "0" = forget the session so nothing persists.
                bool rememberFiles = App.GetSetting("RememberOpenFiles") != "0";
                if (rememberFiles)
                {
                    if (_currentFile is not null)
                        App.SetSetting("LastFile", _currentFile);
                    else
                        App.RemoveSetting("LastFile");
                    // Remember every open tab so the whole session restores next launch. Manually-closed
                    // tabs are already gone from _sessions, so they won't come back (Issue #75 still holds).
                    var openFiles = _sessions
                        .Select(ss => ss.OriginalFile)
                        .Where(f => !string.IsNullOrEmpty(f) && System.IO.File.Exists(f))
                        .Distinct()
                        .ToList();
                    if (openFiles.Count > 0)
                        App.SetSetting("OpenTabs", string.Join("|", openFiles!));
                    else
                        App.RemoveSetting("OpenTabs");
                    if (_active?.OriginalFile is { Length: > 0 } af && System.IO.File.Exists(af))
                        App.SetSetting("ActiveTab", af);
                    else
                        App.RemoveSetting("ActiveTab");
                }
                else
                {
                    // Privacy: drop any remembered session so no file paths linger on disk.
                    App.RemoveSetting("LastFile");
                    App.RemoveSetting("OpenTabs");
                    App.RemoveSetting("ActiveTab");
                }
                PersistToolSettings();
                // The active tab may not have been captured yet at exit; persist its view state directly.
                if (_active != null)
                    SaveDocState(_originalFile, _fitMode, _zoomLevel, _viewMode,
                        PageList.SelectedIndex >= 0 ? PageList.SelectedIndex : 0);
            }
            catch { /* best-effort */ }
        }

        // Persist the Draw and Text tool styles so they carry across sessions. The eraser toggle is
        // deliberately NOT saved (it's a transient mode, not a style).
        private void PersistToolSettings()
        {
            try
            {
                App.SetSetting("DrawColor",     ToolColorHex(_drawColor));
                App.SetSetting("DrawWidth",     _drawWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
                App.SetSetting("DrawOpacity",   _drawOpacity.ToString());
                App.SetSetting("TextFontSize",  _textFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
                App.SetSetting("TextFontName",  _textFontName);
                App.SetSetting("TextBold",      _textBold ? "1" : "0");
                App.SetSetting("TextItalic",    _textItalic ? "1" : "0");
                App.SetSetting("TextStrike",    _textStrike ? "1" : "0");
                App.SetSetting("TextUnderline", _textUnderline ? "1" : "0");
                App.SetSetting("TextColor",     ToolColorHex(_textColor));
                App.SetSetting("TextOpacity",   _textOpacity.ToString());
                App.SetSetting("TextFillColor", ToolColorHexA(_textFillColor));
            }
            catch { /* best-effort */ }
        }

        private void RestoreToolSettings()
        {
            try
            {
                if (ParseToolColor(App.GetSetting("DrawColor")) is Color dc) _drawColor = dc;
                if (double.TryParse(App.GetSetting("DrawWidth"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double dw) && dw > 0) _drawWidth = dw;
                if (byte.TryParse(App.GetSetting("DrawOpacity"), out byte dop)) _drawOpacity = dop;

                if (double.TryParse(App.GetSetting("TextFontSize"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double tfs) && tfs > 0) _textFontSize = tfs;
                if (App.GetSetting("TextFontName") is { Length: > 0 } tfn) _textFontName = tfn;
                _textBold      = App.GetSetting("TextBold") == "1";
                _textItalic    = App.GetSetting("TextItalic") == "1";
                _textStrike    = App.GetSetting("TextStrike") == "1";
                _textUnderline = App.GetSetting("TextUnderline") == "1";
                if (ParseToolColor(App.GetSetting("TextColor")) is Color tc) _textColor = tc;
                if (byte.TryParse(App.GetSetting("TextOpacity"), out byte top)) _textOpacity = top;
                if (ParseToolColorA(App.GetSetting("TextFillColor")) is Color tfc) _textFillColor = tfc;

                // Keep each color's alpha in sync with its opacity byte (the bars store them coupled).
                _drawColor = Color.FromArgb(_drawOpacity, _drawColor.R, _drawColor.G, _drawColor.B);
                _textColor = Color.FromArgb(_textOpacity, _textColor.R, _textColor.G, _textColor.B);
            }
            catch { /* best-effort */ }
        }

        private static string ToolColorHex(Color c)  => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        private static string ToolColorHexA(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        private static Color? ParseToolColor(string? s)
        {
            if (s is null || s.Length != 7 || s[0] != '#') return null;
            try
            {
                return Color.FromRgb(Convert.ToByte(s.Substring(1, 2), 16),
                                     Convert.ToByte(s.Substring(3, 2), 16),
                                     Convert.ToByte(s.Substring(5, 2), 16));
            }
            catch { return null; }
        }

        private static Color? ParseToolColorA(string? s)
        {
            if (s is null || s.Length != 9 || s[0] != '#') return null;
            try
            {
                return Color.FromArgb(Convert.ToByte(s.Substring(1, 2), 16),
                                      Convert.ToByte(s.Substring(3, 2), 16),
                                      Convert.ToByte(s.Substring(5, 2), 16),
                                      Convert.ToByte(s.Substring(7, 2), 16));
            }
            catch { return null; }
        }

        private void RestoreWindowSettings()
        {
            try
            {
                if (int.TryParse(App.GetSetting("WindowWidth"),  out int w) &&
                    int.TryParse(App.GetSetting("WindowHeight"), out int h) && w > 200 && h > 200)
                {
                    Width  = w;
                    Height = h;
                }
                if (int.TryParse(App.GetSetting("WindowTop"),  out int savedTop) &&
                    int.TryParse(App.GetSetting("WindowLeft"), out int savedLeft))
                {
                    // Verify the saved position is visible on the virtual desktop
                    // (covers all monitors). Falls back to CenterScreen (XAML default)
                    // if the monitor it was on is no longer connected.
                    double vLeft   = SystemParameters.VirtualScreenLeft;
                    double vTop    = SystemParameters.VirtualScreenTop;
                    double vRight  = vLeft + SystemParameters.VirtualScreenWidth;
                    double vBottom = vTop  + SystemParameters.VirtualScreenHeight;
                    bool onScreen  = savedLeft + 100 < vRight  && savedLeft + Width  > vLeft
                                  && savedTop  + 50  < vBottom && savedTop  + Height > vTop;
                    if (onScreen)
                    {
                        Left = savedLeft;
                        Top  = savedTop;
                    }
                }
                if (Enum.TryParse<WindowState>(App.GetSetting("WindowState"), out var ws) &&
                    ws == WindowState.Maximized)
                {
                    WindowState = WindowState.Maximized;
                }
                if (double.TryParse(App.GetSetting("ZoomLevel"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double z) && z > 0)
                {
                    _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, z));
                }
                if (Enum.TryParse<FitMode>(App.GetSetting("FitMode"), out var fm))
                    _fitMode = fm;
            }
            catch { /* best-effort */ }
        }

        // ============================================================
        // Core helpers (localization, status, PDF object refs)
        // ============================================================

        /// <summary>Look up a localized string. Falls back to the key name if missing.</summary>
        private string Loc(string key)
            => Application.Current.TryFindResource(key) as string ?? key;

        private void SetStatus(string text)
        {
            StatusText.Text = text;
            CrashReporter.PushStatusMessage(text);
        }

        /// <summary>
        /// Dereferences a PdfItem if it is an indirect reference (PdfReference is internal;
        /// we detect it by looking for a public "Value" property returning PdfObject).
        /// </summary>
        private static PdfItem DerefItem(PdfItem item)
        {
            var valueProp = item.GetType().GetProperty("Value",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (valueProp?.GetValue(item) is PdfObject resolved)
                return resolved;
            return item;
        }

        /// <summary>
        /// Returns the PDF object number of a PdfItem that is an indirect reference, or -1.
        /// Handles the internal PdfReference type via reflection.
        /// </summary>
        private static int GetObjectNumber(PdfItem? item)
        {
            if (item is null) return -1;
            var prop = item.GetType().GetProperty("ObjectNumber",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            return prop?.GetValue(item) is int n ? n : -1;
        }

        // ============================================================
        // Search (Ctrl+F)
        // ============================================================

        /// <summary>
        /// Converts a collection of PdfPig words to a properly ordered string.
        /// Sorts top-to-bottom then left-to-right, groups into lines using a
        /// dynamic threshold (~40% of average word height) so words at slightly
        /// different baselines still land on the correct line.
        /// </summary>
        private static string WordsToText(IEnumerable<UglyToad.PdfPig.Content.Word> source)
        {
            var words = source
                .OrderByDescending(w => w.BoundingBox.Top)
                .ThenBy(w => w.BoundingBox.Left)
                .ToList();
            if (words.Count == 0) return string.Empty;

            // Dynamic threshold: 40% of average word height, minimum 4 PDF units
            double avgH   = words.Average(w => w.BoundingBox.Height);
            double thresh = Math.Max(4.0, avgH * 0.4);

            var lines = new List<List<UglyToad.PdfPig.Content.Word>>();
            double lineY = double.MaxValue;
            foreach (var w in words)
            {
                if (Math.Abs(w.BoundingBox.Top - lineY) > thresh)
                {
                    lines.Add([]);
                    lineY = w.BoundingBox.Top;
                }
                lines[^1].Add(w);
            }

            // Re-sort each line by X in case the top-Y sort caused any grouping
            // to pull words into the wrong order within a line.
            return string.Join("\n", lines.Select(l =>
                string.Join(" ", l.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text))));
        }
    }
}
