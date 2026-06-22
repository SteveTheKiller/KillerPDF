using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
        private readonly List<double> _continuousTops = [];
        private int _continuousScrollTarget = -1;  // re-scroll here once its true height is known
        private double _continuousPageW;

        // Editing
        private EditTool _currentTool = EditTool.Select;
        private readonly Dictionary<int, List<PageAnnotation>> _annotations = [];
        private readonly Dictionary<int, (int w, int h)> _renderDims = [];
        // Stores the PDF /Rotate value for each page.  The temp file used by Docnet has
        // rotation stripped to zero so FPDF_GetPageWidth/Height returns MediaBox dims and
        // the content isn't clipped; RotateBitmap is applied at render time instead.
        private readonly Dictionary<int, int> _pageRotations = [];

        // Form filling — text/check keyed by widget object number; radio keyed by field name
        private readonly Dictionary<int, string>    _formTextValues  = [];
        private readonly Dictionary<int, bool>      _formCheckValues = [];
        private readonly Dictionary<string, string> _formRadioValues = [];
        private const string FormOverlayTag = "FormFieldOverlay";

        // Undo stack — each entry is either an annotation removal or a full document snapshot.
        private enum UndoKind { Annotation, Document }
        private readonly record struct UndoEntry(UndoKind Kind, int PageIdx = -1, byte[]? DocBytes = null, bool WasDirty = false);
        private readonly Stack<UndoEntry> _undoStack = new();
        private bool _isDrawing;
        private Point _drawStart;
        private UIElement? _activePreview;
        private InkAnnotation? _activeInk;
        private TextBox? _activeTextBox;
        private PageAnnotation? _selectedAnnotation;
        private Border? _selectionBorder;

        // Draw/Highlight settings
        private Color _drawColor = Colors.Red;
        private double _drawWidth = 3;
        private byte _drawOpacity = 255;
        private Color _highlightColor = Color.FromArgb(80, 255, 255, 0);
        private Border? _drawSettingsBar;

        // Text (typewriter) tool settings
        private double _textFontSize = 24;
        private TextAnnotation? _reeditOriginal;  // placed-text annotation currently being re-edited
        private Color _textColor = Colors.Black;
        private Border? _textSettingsBar;

        // Signature / image resize
        private bool _isResizingSig;
        private Point _resizeSigStart;
        private double _resizeSigStartScale;
        private PlacedAnnotation? _resizeSigAnnot;
        private readonly List<Rectangle> _resizeHandles = [];   // 4 corner handles for placed annotations
        private string _resizeCorner = "SE";                    // which corner is being dragged
        private Point _resizeAnchor;                            // opposite corner, held fixed during resize

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
        private readonly List<Rectangle> _cropHandles = [];
        private string? _activeCropHandleTag; // "NW" | "NE" | "SE" | "SW"
        private Point _cropHandleDragStart;
        private Rect _cropRectAtHandleDrag;
        private int _cropPageIndex = -1;   // page the crop rect was drawn on (grid/two-page aware)
        private TextBox? _cropX1Box, _cropY1Box, _cropX2Box, _cropY2Box;
        private TextBox? _cropRangeBox;
        private bool     _updatingCropInputs;
        private bool     _cropBarDragging;
        private Point    _cropBarDragOffset;

        // PDF link overlays (rendered on top of the annotation canvas)
        private readonly List<Canvas> _linkOverlays = [];

        // Sidebar + multi-page view
        private bool _sidebarCollapsed;
        private bool   _sidebarShowingOutlines;
        private bool   _outlinesFitted     = false;
        private double _savedPagesWidth    = 180;
        private double _savedOutlinesWidth = 300;
        private readonly Button _sidebarToggleBtn = null!;
        private readonly Border _sidebarBorder = null!;
        private readonly ColumnDefinition _sidebarCol = null!;
        private readonly WrapPanel _pageContentPanel = null!;

        // Text selection
        private bool _isSelecting;
        private Point _selectStart;
        private Rectangle? _selectRect;
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

        // Manual element refs (XAML codegen doesn't resolve these)
        private readonly Canvas _annotationCanvas = null!;
        // Active annotation surface. Single view: always _annotationCanvas. Continuous view:
        // set on mouse-down to the clicked page's overlay. Shared handlers target this.
        private Canvas _activeCanvas = null!;
        // Per-page overlay canvases for Continuous view, keyed by page index.
        private readonly Dictionary<int, Canvas> _continuousCanvases = [];
        private readonly Grid _pageContentGrid = null!;
        private readonly Button _toolSelectBtn = null!;
        private readonly Button _toolTextBtn = null!;
        private readonly Button _toolHighlightBtn = null!;
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
        private readonly Dictionary<int, List<(double left, double bottom, double right, double top)>> _allSearchRects = [];
        private readonly List<int> _searchResultPages = [];
        private int _searchPageCursor = -1;

        public MainWindow()
        {
            InitializeComponent();
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (v != null) VersionLabel.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
            _annotationCanvas = (Canvas)FindName("AnnotationCanvas")!;
            _activeCanvas = _annotationCanvas;
            _pageContentGrid = (Grid)FindName("PageContentGrid")!;
            _toolSelectBtn = (Button)FindName("ToolSelectBtn")!;
            _toolTextBtn = (Button)FindName("ToolTextBtn")!;
            _toolHighlightBtn = (Button)FindName("ToolHighlightBtn")!;
            _toolDrawBtn = (Button)FindName("ToolDrawBtn")!;
            _toolSignatureBtn = (Button)FindName("ToolSignatureBtn")!;
            _toolImageBtn = (Button)FindName("ToolImageBtn")!;
            _toolCropBtn = (Button)FindName("ToolCropBtn")!;
            _sidebarToggleBtn = (Button)FindName("SidebarToggleBtn")!;
            _sidebarBorder = (Border)FindName("SidebarBorder")!;
            _sidebarCol = (ColumnDefinition)FindName("SidebarCol")!;
            _pageContentPanel = (WrapPanel)FindName("PageContentPanel")!;
            _saveAsBtnRef = (Button)FindName("SaveAsBtn")!;
            _closeFileBtnRef = (Button)FindName("CloseFileBtn")!;
            _zoomBox = (ComboBox)FindName("ZoomBox")!;
            _portableBadge = (StackPanel)FindName("PortableBadge")!;
            _pageJumpBox = (TextBox)FindName("PageJumpBox")!;
            _pageTotalLabel = (TextBlock)FindName("PageTotalLabel")!;
            _continuousPanel = (StackPanel)FindName("ContinuousPanel")!;
            PagePreviewPanel.ScrollChanged += PagePreviewPanel_ScrollChanged;
            if (Enum.TryParse<ViewMode>(App.GetSetting("ViewMode"), out var savedVm))
                _viewMode = savedVm;
            OutlineTree.SelectedItemChanged += OutlineTree_SelectedItemChanged;
            LoadSignatures();
            BuildContextMenu();
            SetTool(EditTool.Select);
            ApplyGrainTexture();
            SourceInitialized += MainWindow_SourceInitialized;
            Closed += (_, _) => { _doc?.Close(); App.CleanupSessionTemps(); };

            // Open a file passed via command-line / file association (e.g. double-clicking a .pdf)
            // Also show the portable badge when running outside the install location.
            ContentRendered += (_, _) => Services.ThemeManager.RefreshIcons();
            Services.ThemeManager.ThemeChanged += OnThemeChanged;

            Loaded += (_, _) =>
            {
                RestoreWindowSettings();

                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1 && System.IO.File.Exists(args[1]))
                {
                    OpenFile(args[1]);
                }
                else
                {
                    // Reopen the last file if no CLI arg was provided
                    var lastFile = App.GetSetting("LastFile");
                    if (!string.IsNullOrEmpty(lastFile) && System.IO.File.Exists(lastFile))
                        OpenFile(lastFile!);
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
        }

        // ============================================================
        // Settings panel
        // ============================================================

        private void SettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            // Sync radio buttons to current theme before showing
            var cur = ThemeManager.Current;
            ThemeDarkRadio.IsChecked  = cur == Theme.Dark;
            ThemeLightRadio.IsChecked = cur == Theme.Light;
            ThemeHCRadio.IsChecked    = cur == Theme.HighContrast;
            ThemeBloodRadio.IsChecked = cur == Theme.Blood;
            ThemeGreedRadio.IsChecked    = cur == Theme.Greed;
            ThemeCyanoticRadio.IsChecked = cur == Theme.Cyanotic;
            // Sync language radios
            var curLoc = KillerPDF.Services.LocaleManager.Current;
            LangEnRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.EnUS;
            LangEsRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.Es;
            LangZhTWRadio.IsChecked = curLoc == KillerPDF.Services.Locale.ZhTW;
            LangZhCNRadio.IsChecked = curLoc == KillerPDF.Services.Locale.ZhCN;
            LangTrRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.Tr;
            // Sync view mode radios
            ViewSingleRadio.IsChecked     = _viewMode == ViewMode.Single;
            ViewContinuousRadio.IsChecked = _viewMode == ViewMode.Continuous;
            ViewTwoPageRadio.IsChecked    = _viewMode == ViewMode.TwoPage;
            ViewGridRadio.IsChecked       = _viewMode == ViewMode.Grid;
            SettingsOverlay.Visibility = Visibility.Visible;
        }

        private void SettingsOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => SettingsOverlay.Visibility = Visibility.Collapsed;

        private void SettingsOverlayCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => e.Handled = true;

        private void SettingsOverlayClose_Click(object sender, RoutedEventArgs e)
            => SettingsOverlay.Visibility = Visibility.Collapsed;

        private void OnThemeChanged()
        {
            // Refresh snapshot FindResource calls that were set as local values.
            // SetResourceReference bindings update automatically; sidebar tabs and
            // active tool button background still need an explicit refresh.
            SetTool(_currentTool);
            if (_sidebarShowingOutlines)
                SwitchSidebarToOutlinesTab();
            else
                SwitchSidebarToPagesTab();
            RefreshSelectionAccent();
        }

        private void ThemeDarkRadio_Checked(object sender, RoutedEventArgs e)
            => ThemeManager.Apply(Theme.Dark);

        private void ThemeLightRadio_Checked(object sender, RoutedEventArgs e)
            => ThemeManager.Apply(Theme.Light);

        private void ThemeHCRadio_Checked(object sender, RoutedEventArgs e)
            => ThemeManager.Apply(Theme.HighContrast);

        private void ThemeBloodRadio_Checked(object sender, RoutedEventArgs e)
            => ThemeManager.Apply(Theme.Blood);

        private void ThemeGreedRadio_Checked(object sender, RoutedEventArgs e)
            => ThemeManager.Apply(Theme.Greed);

        private void ThemeCyanoticRadio_Checked(object sender, RoutedEventArgs e)
            => ThemeManager.Apply(Theme.Cyanotic);

        private void LangEnRadio_Checked(object sender, RoutedEventArgs e)
            => KillerPDF.Services.LocaleManager.Apply(KillerPDF.Services.Locale.EnUS);

        private void LangEsRadio_Checked(object sender, RoutedEventArgs e)
            => KillerPDF.Services.LocaleManager.Apply(KillerPDF.Services.Locale.Es);

        private void LangZhTWRadio_Checked(object sender, RoutedEventArgs e)
            => KillerPDF.Services.LocaleManager.Apply(KillerPDF.Services.Locale.ZhTW);

        private void LangZhCNRadio_Checked(object sender, RoutedEventArgs e)
            => KillerPDF.Services.LocaleManager.Apply(KillerPDF.Services.Locale.ZhCN);

        private void LangTrRadio_Checked(object sender, RoutedEventArgs e)
            => KillerPDF.Services.LocaleManager.Apply(KillerPDF.Services.Locale.Tr);

        private void ViewSingleRadio_Checked(object sender, RoutedEventArgs e)     { SetViewMode(ViewMode.Single);     SettingsOverlay.Visibility = Visibility.Collapsed; }
        private void ViewContinuousRadio_Checked(object sender, RoutedEventArgs e) { SetViewMode(ViewMode.Continuous); SettingsOverlay.Visibility = Visibility.Collapsed; }
        private void ViewTwoPageRadio_Checked(object sender, RoutedEventArgs e)    { SetViewMode(ViewMode.TwoPage);    SettingsOverlay.Visibility = Visibility.Collapsed; }
        private void ViewGridRadio_Checked(object sender, RoutedEventArgs e)       { SetViewMode(ViewMode.Grid);       SettingsOverlay.Visibility = Visibility.Collapsed; }

        private const int  WM_GETMINMAXINFO   = 0x0024;
        private const int  WM_DPICHANGED      = 0x02E0;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const uint SWP_NOZORDER       = 0x0004;
        private const uint SWP_NOACTIVATE     = 0x0010;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            else if (msg == WM_DPICHANGED)
            {
                // Apply Windows' suggested rect so the window's apparent size is preserved
                // on the new monitor. handled stays false so WPF's HwndSource also processes
                // the message — updating its internal DPI scale and firing Window.DpiChanged.
                var r = Marshal.PtrToStructure<RECT>(lParam);
                SetWindowPos(hwnd, IntPtr.Zero, r.left, r.top,
                             r.right - r.left, r.bottom - r.top,
                             SWP_NOZORDER | SWP_NOACTIVATE);
                // Re-render at the new DPI. DispatcherPriority.Loaded fires after WPF has
                // finished its own DPI update, so VisualTreeHelper.GetDpi already reflects
                // the new scale factor when RenderPage calls it.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() =>
                    {
                        int idx = PageList.SelectedIndex;
                        if (idx >= 0) RenderPage(idx);
                    }));
            }
            else if (msg == WM_NCHITTEST && WindowState == WindowState.Normal)
            {
                int ht = WmNcHitTest(hwnd, lParam);
                if (ht != 0) { handled = true; return new IntPtr(ht); }
            }
            return IntPtr.Zero;
        }

        private int WmNcHitTest(IntPtr hwnd, IntPtr lParam)
        {
            // lParam is screen coords: lo-word = X, hi-word = Y.
            // Cast through short to preserve sign (handles negative coords on left/above primary monitor).
            long lp  = lParam.ToInt64();
            int  mx  = unchecked((short)(lp & 0xFFFF));
            int  my  = unchecked((short)((lp >> 16) & 0xFFFF));

            if (!GetWindowRect(hwnd, out RECT rc)) return 0;

            bool onLeft   = mx < rc.left   + ResizeBorder;
            bool onRight  = mx >= rc.right  - ResizeBorder;
            bool onTop    = my < rc.top    + ResizeBorder;
            bool onBottom = my >= rc.bottom - ResizeBorder;

            if (onTop    && onLeft)  return HTTOPLEFT;
            if (onTop    && onRight) return HTTOPRIGHT;
            if (onBottom && onLeft)  return HTBOTTOMLEFT;
            if (onBottom && onRight) return HTBOTTOMRIGHT;
            if (onLeft)              return HTLEFT;
            if (onRight)             return HTRIGHT;
            if (onTop)               return HTTOP;
            if (onBottom)            return HTBOTTOM;

            return 0;
        }

        private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                GetMonitorInfo(monitor, ref info);
                RECT work = info.rcWork;
                RECT mon = info.rcMonitor;
                mmi.ptMaxPosition.x = Math.Abs(work.left - mon.left);
                mmi.ptMaxPosition.y = Math.Abs(work.top - mon.top);
                mmi.ptMaxSize.x = Math.Abs(work.right - work.left);
                mmi.ptMaxSize.y = Math.Abs(work.bottom - work.top);
                mmi.ptMaxTrackSize.x = mmi.ptMaxSize.x;
                mmi.ptMaxTrackSize.y = mmi.ptMaxSize.y;
                Marshal.StructureToPtr(mmi, lParam, true);
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCHITTEST     = 0x0084;
        private const int HTCAPTION        = 2;
        private const int HTLEFT           = 10;
        private const int HTRIGHT          = 11;
        private const int HTTOP            = 12;
        private const int HTTOPLEFT        = 13;
        private const int HTTOPRIGHT       = 14;
        private const int HTBOTTOM         = 15;
        private const int HTBOTTOMLEFT     = 16;
        private const int HTBOTTOMRIGHT    = 17;
        private const int ResizeBorder     = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        // ============================================================
        // Window chrome
        // ============================================================

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                MaximizeBtn_Click(sender, e);
                return;
            }
            // Delegate drag to Windows via WM_NCLBUTTONDOWN(HTCAPTION).
            // This gives native restore-from-maximized-and-drag behavior:
            // if the window is maximized, Windows restores it and follows the cursor
            // exactly as a native title bar would.
            e.Handled = true;
            var hwnd = new WindowInteropHelper(this).Handle;
            SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
        }

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            var res = KillerDialog.Show(this,
                Loc("Str_Dlg_InstallMsg"),
                Loc("Str_Dlg_InstallTitle"), MessageBoxButton.OKCancel);
            if (res != MessageBoxResult.OK) return;

            // Hide the badge immediately so it doesn't flash if relaunch is slow
            _portableBadge.Visibility = Visibility.Collapsed;

            App.InstallAndRelaunch(_currentFile, wantDesktop: true);
        }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void MaximizeBtn_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_isDirty)
            {
                var res = KillerDialog.Show(this,
                    Loc("Str_Dlg_UnsavedExit"),
                    Loc("Str_Dlg_AppTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            SaveWindowSettings();
            base.OnClosing(e);
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
                if (_currentFile is not null)
                    App.SetSetting("LastFile", _currentFile);
            }
            catch { /* best-effort */ }
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
        // Context menu
        // ============================================================

        private void ApplyGrainTexture()
        {
            // Sparse bright-speck film grain — same style as the first pass,
            // tuned so the texture is visible without being chunky.
            const int size = 256;
            var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
            var pixels = new byte[size * size * 4]; // start fully transparent
            var rng = new Random(1337);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (rng.Next(4) != 0) continue;       // ~25% pixel density
                byte v = (byte)rng.Next(160, 255);     // bright specks
                byte a = (byte)rng.Next(30, 80);       // low-ish alpha for subtlety
                pixels[i]     = v;
                pixels[i + 1] = v;
                pixels[i + 2] = v;
                pixels[i + 3] = a;
            }
            bmp.WritePixels(new System.Windows.Int32Rect(0, 0, size, size), pixels, size * 4, 0);
            GrainBrush.ImageSource = bmp;
        }

        /// <summary>Generated film-grain tile, exposed so secondary windows (e.g. the
        /// print preview) can paint the same texture over their document area.</summary>
        public ImageSource? GrainTexture => GrainBrush?.ImageSource;

        private void BuildContextMenu()
        {
            var menu = new ContextMenu();

            menu.Items.Add(MakeMenuItem("Copy Text", (s, e) => CopySelectedText(), "Ctrl+C"));
            menu.Items.Add(MakeMenuItem("Print", (s, e) => Print_Click(s!, e), "Ctrl+P"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("Select Tool", (s, e) => SetTool(EditTool.Select)));
            menu.Items.Add(MakeMenuItem("Text Tool", (s, e) => SetTool(EditTool.Text)));
            menu.Items.Add(MakeMenuItem("Highlight Tool", (s, e) => SetTool(EditTool.Highlight)));
            menu.Items.Add(MakeMenuItem("Draw Tool", (s, e) => SetTool(EditTool.Draw)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("Rotate Page CW",  (s, e) => RotatePages_Click(90)));
            menu.Items.Add(MakeMenuItem("Rotate Page CCW", (s, e) => RotatePages_Click(-90)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("Delete Selected", (s, e) => DeleteSelected(), "Delete"));
            menu.Items.Add(MakeMenuItem("Undo Last", (s, e) => Undo_Click(s!, e), "Ctrl+Z"));
            menu.Items.Add(MakeMenuItem("Clear Page Annotations", (s, e) => ClearAnnotations_Click(s!, e)));

            _annotationCanvas.ContextMenu = menu;
        }

        private void PageList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_doc is null) return;
            var menu = new ContextMenu();
            menu.Items.Add(MakeMenuItem("Insert Blank Page After", (s, ev) => InsertBlankPage_Click(s!, ev)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("Rotate CW",  (s, ev) => RotatePages_Click(90)));
            menu.Items.Add(MakeMenuItem("Rotate CCW", (s, ev) => RotatePages_Click(-90)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("Move Page Up",   (s, ev) => MoveUp_Click(s!, ev)));
            menu.Items.Add(MakeMenuItem("Move Page Down", (s, ev) => MoveDown_Click(s!, ev)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("Extract Page(s)", (s, ev) => Split_Click(s!, ev)));
            menu.Items.Add(MakeMenuItem("Delete Page(s)", (s, ev) => Delete_Click(s!, ev)));
            menu.PlacementTarget = PageList;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void RotatePages_Click(int delta)
        {
            if (_doc is null) return;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) return;
            try
            {
                var indices = new List<int>();
                foreach (PageThumbnailVm vm in selected) indices.Add(vm.PageIndex);
                foreach (var idx in indices)
                    _doc.Pages[idx].Rotate = ((_doc.Pages[idx].Rotate + delta) % 360 + 360) % 360;
                int restoreIdx = PageList.SelectedIndex;
                SaveTempAndReload();
                PageList.SelectedIndex = Math.Min(restoreIdx, PageList.Items.Count - 1);
                // After a rotation the page aspect ratio changes; always fit-to-page so the
                // full rotated page is visible regardless of the previous zoom level.
                FitToPage();
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)FitToPage);
                SetStatus(string.Format(Loc("Str_Rotated"), indices.Count));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_RotateFailed"), ex.Message), Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static MenuItem MakeMenuItem(string header, RoutedEventHandler click, string? gesture = null)
        {
            var item = new MenuItem { Header = header };
            item.Click += click;
            if (gesture != null)
                item.InputGestureText = gesture;
            return item;
        }

        // ============================================================
        // File operations
        // ============================================================

        private void OpenFile(string path)
        {
            // Files on UNC / network shares - notably the WSL \\wsl$ 9P filesystem - can hand
            // back partial reads, making the PDF parser see a truncated file ("Unexpected EOF").
            // Copy such files to a local temp via File.ReadAllBytes (which reads to EOF) and open
            // from there. `path` stays the user's real path for display and Save.
            string srcPath = path;
            if (IsNetworkPath(path))
            {
                try
                {
                    var localCopy = App.MakeTempFile("netopen");
                    File.WriteAllBytes(localCopy, File.ReadAllBytes(path));
                    srcPath = localCopy;
                }
                catch { srcPath = path; }
            }

            try
            {
                if (_doc is not null) { _doc.Close(); _doc = null; }
                _doc = PdfReader.Open(srcPath, PdfDocumentOpenMode.Modify);
                // PdfSharp cannot save modified encrypted PDFs — it copies unmodified encrypted
                // stream bytes verbatim but fails when it has to re-serialize a dirty object.
                // Strip encryption silently at open time via Import so all edits work correctly.
                if (PdfFileHasEncryption(srcPath))
                {
                    // PdfSharp can read encrypted PDFs but cannot re-save them once modified.
                    // Strip encryption now via PDFium (lossless), falling back to Import mode.
                    _doc.Close(); _doc = null;
                    var repairedPath = App.MakeTempFile("repaired");
                    bool ok = TryPdfiumStripEncryption(srcPath, repairedPath)
                           || TryImportRepairToPath(srcPath, repairedPath);
                    if (!ok) { TryRepairAndOpen(srcPath); return; }
                    _doc = PdfReader.Open(repairedPath, PdfDocumentOpenMode.Modify);
                    _currentFile = repairedPath;
                    FinishOpenFile(path, repairedPath);
                    MarkDirty(true);
                    return;
                }
                _currentFile = srcPath;
                FinishOpenFile(path, srcPath);
            }
            catch (Exception ex) when (IsOwnerPasswordException(ex))
            {
                // PDF has owner/permissions restrictions but no open password —
                // open read-only so the user can still view and print it.
                try
                {
                    if (_doc is not null) { _doc.Close(); _doc = null; }
                    _doc = PdfReader.Open(srcPath, PdfDocumentOpenMode.ReadOnly);
                    _currentFile = srcPath;
                    FinishOpenFile(path, srcPath);
                    SetStatus(string.Format(Loc("Str_OpenedReadOnly"), System.IO.Path.GetFileName(path), _doc.PageCount));
                }
                catch (Exception ex2)
                {
                    KillerDialog.Show(this, string.Format(Loc("Str_Dlg_FailedOpen"), ex2.Message), Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex) when (IsPasswordException(ex))
            {
                string? pw = PromptForPassword(path);
                if (pw is null) return;
                try
                {
                    if (_doc is not null) { _doc.Close(); _doc = null; }
                    _doc = PdfReader.Open(srcPath, pw, PdfDocumentOpenMode.Modify);
                    // Save a decrypted temp copy so Docnet can render without needing the password
                    var tempDec = App.MakeTempFile("dec");
                    _doc.Save(tempDec);
                    _doc.Close();
                    _doc = PdfReader.Open(tempDec, PdfDocumentOpenMode.Modify);
                    _currentFile = tempDec;
                    FinishOpenFile(path, tempDec);
                }
                catch (Exception ex2)
                {
                    KillerDialog.Show(this, string.Format(Loc("Str_Dlg_FailedOpen"), ex2.Message), Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex) when (IsXRefException(ex))
            {
                // Some PDFs have malformed or non-standard XRef tables that PdfSharp can't
                // open in Modify mode. Fall back to ReadOnly; if that also fails, offer repair.
                try
                {
                    if (_doc is not null) { _doc.Close(); _doc = null; }
                    _doc = PdfReader.Open(srcPath, PdfDocumentOpenMode.ReadOnly);
                    _currentFile = srcPath;
                    FinishOpenFile(path, srcPath);
                    SetStatus(string.Format(Loc("Str_OpenedReadOnlyXRef"), System.IO.Path.GetFileName(path), _doc.PageCount));
                    KillerDialog.Show(this,
                        $"\"{System.IO.Path.GetFileName(path)}\" has a non-standard structure and was opened read-only.\n\nEditing, saving, and some other features may not work correctly.",
                        "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch
                {
                    // ReadOnly also failed — offer to repair.
                    var result = KillerDialog.Show(this,
                        $"This PDF has a damaged structure and couldn't be opened.\n\nWould you like KillerPDF to attempt a repair? A repaired copy will be created — the original file will not be changed.\n\nNote: repaired files may be missing bookmarks, forms, and other interactive features.",
                        "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result == MessageBoxResult.Yes)
                        TryRepairAndOpen(srcPath);
                }
            }
            catch (Exception ex) when (IsEofParseException(ex))
            {
                // PdfSharpCore rejects some structurally-valid PDFs with "Unexpected EOF" even
                // though PDFium (and every common viewer) reads them fine. Re-save the file
                // losslessly through PDFium to a clean temp and open that; fall back to an
                // import repair, then to a rasterize repair as a last resort.
                try
                {
                    if (_doc is not null) { _doc.Close(); _doc = null; }
                    var repairedPath = App.MakeTempFile("repaired");
                    bool ok = TryPdfiumStripEncryption(srcPath, repairedPath)
                           || TryImportRepairToPath(srcPath, repairedPath);
                    if (!ok) { TryRepairAndOpen(srcPath); return; }
                    _doc = PdfReader.Open(repairedPath, PdfDocumentOpenMode.Modify);
                    _currentFile = repairedPath;
                    FinishOpenFile(path, repairedPath);
                    // Open clean: the normalized copy is content-equivalent, so a view-only open
                    // should not nag to save. _currentFile points at the temp and _originalFile at
                    // the user's path, so the original is only ever overwritten if they choose to
                    // Save after making edits (FinishOpenFile already cleared the dirty flag).
                    SetStatus($"Opened {System.IO.Path.GetFileName(path)} ({_doc.PageCount} pages) - recovered via PDFium.");
                }
                catch (Exception ex2)
                {
                    KillerDialog.Show(this, string.Format(Loc("Str_Dlg_FailedOpen"), ex2.Message), Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_Dlg_FailedOpen"), ex.Message), Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // PdfSharpCore throws on some structurally-valid PDFs that PDFium opens fine - most
        // often "Unexpected EOF" from SharpZipLib's Flate inflater while reading a FlateDecode
        // cross-reference stream (multi-revision PDFs with incremental updates / dangling xref
        // entries that tolerant parsers ignore). Match by message AND exception type across the
        // whole inner-exception chain so a wrapped SharpZipBaseException is still recovered.
        private static bool IsEofParseException(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                string msg  = e.Message ?? string.Empty;
                string type = e.GetType().FullName ?? string.Empty;
                if (msg.IndexOf("EOF", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("end of file", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("Inflater", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("FlateDecode", StringComparison.OrdinalIgnoreCase) >= 0
                    || type.IndexOf("SharpZip", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static bool IsXRefException(Exception ex) =>
            ex.Message.IndexOf("XRef", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("cross-reference", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("trailer", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("Invalid PDF file", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("startxref", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("Unexpected token", StringComparison.OrdinalIgnoreCase) >= 0;

        // True for UNC paths (\\server\share, \\wsl$\..., \\wsl.localhost\...) and mapped
        // network drives. Such files are copied locally before opening to avoid 9P short reads.
        private static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            try
            {
                var root = System.IO.Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root) && root!.Length >= 2 && root[1] == ':')
                    return new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Imports pages from <paramref name="sourcePath"/> into a fresh PdfDocument and saves it
        /// to <paramref name="destPath"/>. Returns true on success, false on failure.
        /// Unlike TryRepairAndOpen this has no UI side-effects and can be used mid-operation.
        /// </summary>
        /// <param name="stripRotations">
        // ── PDFium P/Invoke ──────────────────────────────────────────────────────────
        // PDFium (pdfium.dll) is already shipped with Docnet. We use it here to strip
        // encryption from PDFs that PdfSharpCore can read but cannot re-save when modified.

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_LoadDocument(
            [MarshalAs(UnmanagedType.LPStr)] string filePath,
            [MarshalAs(UnmanagedType.LPStr)] string? password);

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_CloseDocument(IntPtr document);

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDF_SaveWithVersion(
            IntPtr document, ref FPDF_FILEWRITE fileWrite, uint flags, int fileVersion);

        [StructLayout(LayoutKind.Sequential)]
        private struct FPDF_FILEWRITE
        {
            public int version;          // must be 1
            public IntPtr WriteBlock;    // cdecl: int WriteBlock(FPDF_FILEWRITE*, const void*, unsigned long)
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PdfWriteBlockDelegate(IntPtr pThis, IntPtr pData, uint size);

        private const uint FPDF_REMOVE_SECURITY = 3;

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDF_GetPageCount(IntPtr document);

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_LoadPage(IntPtr document, int page_index);

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_ClosePage(IntPtr page);

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFPage_SetRotation(IntPtr page, int rotation);

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFPage_GenerateContent(IntPtr page);

        /// <summary>
        /// Returns true if the PDF file has an /Encrypt entry in its trailer.
        /// Scans the last 2 KB so it's fast; works regardless of how PdfSharp
        /// reports security state after authenticating with an empty password.
        /// </summary>
        private static bool PdfFileHasEncryption(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                long scan = Math.Min(2048, fs.Length);
                fs.Seek(-scan, SeekOrigin.End);
                var buf = new byte[scan];
                _ = fs.Read(buf, 0, buf.Length);
                // Look for /Encrypt in the raw bytes (Latin-1 safe)
                var text = System.Text.Encoding.GetEncoding(1252).GetString(buf);
                return text.Contains("/Encrypt");
            }
            catch { return false; }
        }

        /// <summary>
        /// Uses PDFium to save a copy of <paramref name="sourcePath"/> with all security/encryption
        /// removed. Returns true on success. Falls back gracefully if PDFium is unavailable.
        /// PDFium is already initialised by Docnet; no separate init call is needed.
        /// </summary>
        private static bool TryPdfiumStripEncryption(string sourcePath, string destPath)
        {
            try
            {
                // Ensure PDFium is initialised — Docnet does this lazily on first use,
                // so force it now before we call PDFium P/Invoke directly.
                try { _ = DocLib.Instance; } catch { }

                var doc = FPDF_LoadDocument(sourcePath, null);
                if (doc == IntPtr.Zero) return false;
                try
                {
                    using var ms = new MemoryStream();
                    PdfWriteBlockDelegate cb = (_, pData, size) =>
                    {
                        var buf = new byte[size];
                        Marshal.Copy(pData, buf, 0, (int)size);
                        ms.Write(buf, 0, (int)size);
                        return 1;
                    };
                    var gch = GCHandle.Alloc(cb);
                    try
                    {
                        var fw = new FPDF_FILEWRITE
                        {
                            version = 1,
                            WriteBlock = Marshal.GetFunctionPointerForDelegate(cb)
                        };
                        if (!FPDF_SaveWithVersion(doc, ref fw, FPDF_REMOVE_SECURITY, 0))
                            return false;
                    }
                    finally { gch.Free(); }
                    File.WriteAllBytes(destPath, ms.ToArray());
                    return true;
                }
                finally { FPDF_CloseDocument(doc); }
            }
            catch { return false; }
        }

        // ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Uses PDFium to load <paramref name="sourcePath"/>, zero-out all page /Rotate values,
        /// strip encryption, and save to <paramref name="destPath"/>. Returns true on success.
        /// Called from SaveTempAndReload's xref-error fallback — PDFium is guaranteed to be
        /// initialised by then because the page preview has already rendered via Docnet.
        /// </summary>
        private static bool TryPdfiumSaveWithZeroRotations(string sourcePath, string destPath)
        {
            try
            {
                var doc = FPDF_LoadDocument(sourcePath, null);
                if (doc == IntPtr.Zero)
                {
                    try { File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "killerpdf_pdfium_debug.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] FPDF_LoadDocument returned null for: {sourcePath}\n\n"); } catch { }
                    return false;
                }
                try
                {
                    int pageCount = FPDF_GetPageCount(doc);
                    for (int i = 0; i < pageCount; i++)
                    {
                        var page = FPDF_LoadPage(doc, i);
                        if (page == IntPtr.Zero) continue;
                        try
                        {
                            FPDFPage_SetRotation(page, 0);   // strip /Rotate so Docnet renders cleanly
                            FPDFPage_GenerateContent(page);
                        }
                        finally { FPDF_ClosePage(page); }
                    }

                    using var ms = new MemoryStream();
                    PdfWriteBlockDelegate cb = (_, pData, size) =>
                    {
                        var buf = new byte[size];
                        Marshal.Copy(pData, buf, 0, (int)size);
                        ms.Write(buf, 0, (int)size);
                        return 1;
                    };
                    var gch = GCHandle.Alloc(cb);
                    try
                    {
                        var fw = new FPDF_FILEWRITE
                        {
                            version = 1,
                            WriteBlock = Marshal.GetFunctionPointerForDelegate(cb)
                        };
                        if (!FPDF_SaveWithVersion(doc, ref fw, FPDF_REMOVE_SECURITY, 0))
                            return false;
                    }
                    finally { gch.Free(); }

                    File.WriteAllBytes(destPath, ms.ToArray());
                    return true;
                }
                finally { FPDF_CloseDocument(doc); }
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "killerpdf_pdfium_debug.txt"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TryPdfiumSaveWithZeroRotations failed\n" +
                        $"  source: {sourcePath}\n" +
                        $"  type:   {ex.GetType().FullName}\n" +
                        $"  msg:    {ex.Message}\n" +
                        $"  stack:  {ex.StackTrace}\n\n");
                }
                catch { /* log failure is non-fatal */ }
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────

        /// <param name="stripRotations">
        /// Pass true when called from SaveTempAndReload (rotations already stripped in source).
        /// Pass false for open-time repair so original page rotations are preserved.
        /// </param>
        private static bool TryImportRepairToPath(string sourcePath, string destPath, bool stripRotations = false)
        {
            try
            {
                using var importDoc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
                var cleanDoc = new PdfDocument();
                for (int i = 0; i < importDoc.PageCount; i++)
                    cleanDoc.Pages.Add(importDoc.Pages[i]);
                if (stripRotations)
                    for (int i = 0; i < cleanDoc.PageCount; i++)
                        cleanDoc.Pages[i].Rotate = 0;
                cleanDoc.Save(destPath);
                cleanDoc.Close();
                return true;
            }
            catch { return false; }
        }

        private void TryRepairAndOpen(string path)
        {
            // Strategy 1: PdfSharpCore Import mode — page-copy, more lenient than Modify/ReadOnly.
            // Works when the XRef is partially corrupt but the object data is intact.
            try
            {
                if (_doc is not null) { _doc.Close(); _doc = null; }
                PdfDocument repairedDoc;
                using (var importDoc = PdfReader.Open(path, PdfDocumentOpenMode.Import))
                {
                    repairedDoc = new PdfDocument();
                    for (int i = 0; i < importDoc.PageCount; i++)
                        repairedDoc.Pages.Add(importDoc.Pages[i]);
                }
                var repairedPath = App.MakeTempFile("repaired");
                repairedDoc.Save(repairedPath);
                repairedDoc.Close();
                _doc = PdfReader.Open(repairedPath, PdfDocumentOpenMode.Modify);
                _currentFile = repairedPath;
                FinishOpenFile(path, repairedPath);
                MarkDirty(true); // repaired copy lives in temp — user must Save As
                SetStatus(string.Format(Loc("Str_OpenedRepaired"), System.IO.Path.GetFileName(path), _doc.PageCount));
                KillerDialog.Show(this,
                    $"\"{System.IO.Path.GetFileName(path)}\" was repaired successfully.\n\nBookmarks, forms, and other interactive features may have been lost. Use Save As to write the repaired file to a new location.",
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.None);
                return;
            }
            catch { }

            // Strategy 2: PDFium rasterize repair.
            // PDFium has its own internal XRef recovery that handles damage PdfSharpCore cannot.
            // Each page is rendered to a bitmap and rebuilt into a clean PDF.
            // Text will not be selectable in the result, but the file will open and be printable.
            try
            {
                RepairViaDocnetRasterize(path);
                return;
            }
            catch { }

            KillerDialog.Show(this,
                "Repair failed — the file is too severely damaged to recover.\n\nTry opening the original in a different application (Adobe Acrobat, browsers) which may have additional recovery options.",
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        /// <summary>
        /// Repair fallback: uses PDFium (Docnet) to render each page to a bitmap, then rebuilds
        /// a clean PdfSharpCore document from those bitmaps. Works on files where the XRef/trailer
        /// is too damaged for PdfSharpCore's parser but PDFium's recovery logic can still render.
        /// </summary>
        private void RepairViaDocnetRasterize(string path)
        {
            const int RenderPx = 2048;

            using var docReader = DocLib.Instance.GetDocReader(path, new PageDimensions(RenderPx, RenderPx));
            int pageCount = docReader.GetPageCount();
            if (pageCount <= 0) throw new InvalidOperationException("PDFium could not read any pages.");

            var newDoc = new PdfDocument();

            for (int i = 0; i < pageCount; i++)
            {
                using var pr = docReader.GetPageReader(i);
                int bw = pr.GetPageWidth();
                int bh = pr.GetPageHeight();
                if (bw <= 0 || bh <= 0) continue;

                var raw = pr.GetImage();
                if (raw is null || raw.Length == 0) continue;

                // Encode the raw BGRA frame as PNG via WPF BitmapEncoder (UI thread is fine here).
                var wb = new WriteableBitmap(bw, bh, 96, 96, PixelFormats.Bgra32, null);
                wb.WritePixels(new Int32Rect(0, 0, bw, bh), raw, bw * 4, 0);
                wb.Freeze();

                byte[] pngBytes;
                using (var ms = new System.IO.MemoryStream())
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(wb));
                    enc.Save(ms);
                    pngBytes = ms.ToArray();
                }

                // Build the page at correct aspect ratio scaled to A4-ish width.
                double pageW = 595.28;
                double pageH = pageW * bh / bw;

                var page = newDoc.AddPage();
                page.Width  = XUnit.FromPoint(pageW);
                page.Height = XUnit.FromPoint(pageH);

                using var gfx = XGraphics.FromPdfPage(page);
                var xImg = XImage.FromStream(() => new System.IO.MemoryStream(pngBytes));
                gfx.DrawImage(xImg, 0, 0, pageW, pageH);
            }

            if (newDoc.PageCount == 0)
                throw new InvalidOperationException("PDFium rendered 0 usable pages.");

            var repairedPath = App.MakeTempFile("repaired");
            newDoc.Save(repairedPath);
            newDoc.Close();

            if (_doc is not null) { _doc.Close(); _doc = null; }
            _doc = PdfReader.Open(repairedPath, PdfDocumentOpenMode.Modify);
            _currentFile = repairedPath;
            FinishOpenFile(path, repairedPath);
            MarkDirty(true); // repaired copy lives in temp — user must Save As
            SetStatus(string.Format(Loc("Str_OpenedRasterRepair"), System.IO.Path.GetFileName(path), _doc.PageCount));
            KillerDialog.Show(this,
                $"\"{System.IO.Path.GetFileName(path)}\" was repaired by rasterizing through PDFium.\n\nText is not selectable in the repaired copy. Use Save As to write it to a new location.",
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.None);
        }

        private static bool IsOwnerPasswordException(Exception ex) =>
            ex.Message.IndexOf("owner", StringComparison.OrdinalIgnoreCase) >= 0 &&
            ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0;

        private void FinishOpenFile(string displayPath, string workingPath)
        {
            _currentFile = workingPath;
            _originalFile = displayPath;
            FileNameLabel.Text = System.IO.Path.GetFileName(displayPath);
            _annotations.Clear();
            _undoStack.Clear();
            _renderDims.Clear();
            _formTextValues.Clear();
            _formCheckValues.Clear();
            _formRadioValues.Clear();
            _allSearchRects.Clear();
            _searchResultPages.Clear();
            _searchPageCursor = -1;
            ClearSecondaryPages();
            ClearSelection();
            RefreshPageList();
            LoadOutlines();
            DropZone.Visibility = Visibility.Collapsed;
            PagePreviewPanel.Visibility = Visibility.Visible;
            if (_closeFileBtnRef != null) _closeFileBtnRef.IsEnabled = true;
            _pageJumpBox.IsEnabled = true;
            _pageTotalLabel.Text = $"/ {_doc!.PageCount}";
            MarkDirty(false);
            if (_doc!.PageCount > 0)
            {
                PageList.SelectedIndex = 0;
                // If Continuous mode is persisted from a previous session, SelectionChanged
                // returns early (no RenderPage call), so we have to bootstrap the panels here.
                if (_viewMode == ViewMode.Continuous)
                {
                    _pageContentPanel.Visibility = Visibility.Collapsed;
                    _continuousPanel.Visibility  = Visibility.Visible;
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                        () => SetupContinuousView(0));
                }
                // Auto-fit to width once the first page has rendered and layout has settled.
                // DispatcherPriority.Background is lower than Loaded, so this fires after
                // all pending RenderPage / RefreshPageView callbacks have completed.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    (Action)(() =>
                    {
                        // Grid opens to its 3-across default; other modes fit to width. Background
                        // runs after every Loaded callback, so this is the final word on open and
                        // must be view-aware or it collapses the grid back to a single page.
                        if (_viewMode == ViewMode.Grid)
                            SetZoom(GridZoomForN(Math.Min(_doc?.PageCount ?? 1, 3)));
                        else
                            FitToPage();   // Single, Two-Page, and Continuous open fit-to-page
                    }));
            }
            SetStatus(string.Format(Loc("Str_Opened"), System.IO.Path.GetFileName(displayPath), _doc.PageCount));
        }

        private static bool IsPasswordException(Exception ex) =>
            ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("protected", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("encrypted", StringComparison.OrdinalIgnoreCase) >= 0;

        private string? PromptForPassword(string filename)
        {
            string? result = null;
            var win = new Window
            {
                Title = "Password Required",
                Width = 360,
                Height = 165,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22))
            };
            var sp = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            sp.Children.Add(new TextBlock
            {
                Text = $"\"{System.IO.Path.GetFileName(filename)}\" is password protected.",
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
            var pwBox = new PasswordBox { Margin = new Thickness(0, 0, 0, 14) };
            sp.Children.Add(pwBox);
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = "Open", Width = 76, Margin = new Thickness(0, 0, 8, 0) };
            var cancelBtn = new Button { Content = "Cancel", Width = 76 };
            okBtn.Click += (s, ev) => { result = pwBox.Password; win.DialogResult = true; };
            cancelBtn.Click += (s, ev) => { win.DialogResult = false; };
            pwBox.KeyDown += (s, ev) => { if (ev.Key == Key.Enter) { result = pwBox.Password; win.DialogResult = true; } };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);
            sp.Children.Add(btnRow);
            win.Content = sp;
            return win.ShowDialog() == true ? result : null;
        }

        // Cancels the previous thumbnail background load when the file changes.
        private System.Threading.CancellationTokenSource? _thumbCts;

        private void RefreshPageList()
        {
            // Cancel any in-flight thumbnail load for the previous file.
            _thumbCts?.Cancel();
            _thumbCts = new System.Threading.CancellationTokenSource();
            var ct = _thumbCts.Token;

            if (_doc is null || _currentFile is null)
            {
                PageList.ItemsSource = null;
                return;
            }

            int    pageCount = _doc.PageCount;
            string filePath  = _currentFile;

            // Snapshot rotations on the UI thread before going to background.
            var rotSnap = new Dictionary<int, int>(_pageRotations);

            // Carry forward any existing thumbnails so the list never flashes blank
            // during reload (e.g. after a rotation).  New thumbnails replace them as
            // the background loader finishes each page.
            var oldItems = PageList.ItemsSource is PageThumbnailVm[] oi ? oi : null;

            var items = new PageThumbnailVm[pageCount];
            for (int i = 0; i < pageCount; i++)
            {
                rotSnap.TryGetValue(i, out int rot);
                items[i] = new PageThumbnailVm(i, filePath, rot);
                // Seed with stale thumbnail — better than blank while reloading
                if (oldItems != null && i < oldItems.Length)
                {
                    var prev = oldItems[i].Thumbnail;
                    if (prev != null) items[i].SetThumbnailDirect(prev);
                }
            }
            PageList.ItemsSource = items;

            // Load thumbnails sequentially on a background thread via a single doc reader.
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var docReader = DocLib.Instance.GetDocReader(filePath, new PageDimensions(128, 256));
                    for (int i = 0; i < pageCount; i++)
                    {
                        if (ct.IsCancellationRequested) return;
                        try
                        {
                            using var pr  = docReader.GetPageReader(i);
                            int tw  = pr.GetPageWidth();
                            int th  = pr.GetPageHeight();
                            var raw = pr.GetImage();
                            if (tw <= 0 || th <= 0 || raw == null || raw.Length < tw * th * 4)
                                continue;
                            rotSnap.TryGetValue(i, out int rot);
                            if (rot != 0)
                                (raw, tw, th) = RotateBitmap(raw, tw, th, rot);
                            var src = PageThumbnailVm.BuildThumbFromRaw(raw, tw, th);
                            if (src != null && !ct.IsCancellationRequested)
                                items[i].SetThumbnail(src);
                        }
                        catch { /* skip failed thumbnail; item shows label-only */ }
                    }
                }
                catch { /* docReader open failed; all items remain label-only */ }
            }, ct);
        }

        private void RenderPage(int pageIndex)
        {
            if (_currentFile is null || _doc is null) return;
            try
            {
                // Scale render resolution to match display DPI AND current zoom so the
                // bitmap stays sharp when zoomed in.  Base 2048 means Fit Width on a
                // wide monitor stays crisp; zoom factor ensures 1:1 pixels at 2× zoom.
                // Capped at 6144 to keep memory manageable.
                var dpiInfo = VisualTreeHelper.GetDpi(this);
                double dpiScaleX = dpiInfo.DpiScaleX;
                double dpiScaleY = dpiInfo.DpiScaleY;
                int scaledMax = (int)Math.Min(6144,
                    2048 * Math.Max(dpiScaleX, dpiScaleY) * Math.Max(1.0, _zoomLevel));
                _lastRenderZoom = _zoomLevel;

                using var docReader = DocLib.Instance.GetDocReader(_currentFile, new PageDimensions(scaledMax, scaledMax));
                using var pageReader = docReader.GetPageReader(pageIndex);

                int width = pageReader.GetPageWidth();
                int height = pageReader.GetPageHeight();
                var rawBytes = pageReader.GetImage();

                // Apply rotation: the temp file has /Rotate stripped so Docnet renders
                // unrotated (no clipping); rotate the pixel buffer to match the visual.
                if (_pageRotations.TryGetValue(pageIndex, out int pgRot) && pgRot != 0)
                    (rawBytes, width, height) = RotateBitmap(rawBytes, width, height, pgRot);

                if (width <= 0 || height <= 0 || rawBytes == null || rawBytes.Length == 0)
                {
                    PageImage.Source = null;
                    SetStatus(string.Format(Loc("Str_PageRenderError"), pageIndex + 1));
                    return;
                }

                // Convert pixel dimensions to WPF DIPs so the annotation canvas and
                // link overlays are sized in the same coordinate space that WPF uses for
                // layout.  Divide by the zoom factor so the canvas size (and therefore the
                // coordinate map used by DrawAnnotationsOnDocument) stays stable across
                // zoom re-renders — the bitmap just gets more pixels per DIP.
                // LayoutTransform handles the visual zoom, not the canvas dimensions.
                double zoomFactor = Math.Max(1.0, _zoomLevel);
                int dipW = (int)Math.Round(width  / dpiScaleX / zoomFactor);
                int dipH = (int)Math.Round(height / dpiScaleY / zoomFactor);
                _renderDims[pageIndex] = (dipW, dipH);

                // Scale bitmap DPI up so the extra pixels display within the same DIP area.
                double bitmapDpiX = 96.0 * width  / dipW;
                double bitmapDpiY = 96.0 * height / dipH;
                var bitmap = new WriteableBitmap(width, height, bitmapDpiX, bitmapDpiY, PixelFormats.Bgra32, null);
                bitmap.WritePixels(new Int32Rect(0, 0, width, height), rawBytes, width * 4, 0);

                PageImage.Source = bitmap;
                _annotationCanvas.Width  = dipW;
                _annotationCanvas.Height = dipH;
                _annotationCanvas.Tag    = pageIndex;   // so clicks on the primary page resolve to the
                                                        // page actually shown (page 0 in grid), not the
                                                        // selected index - otherwise annotations on it
                                                        // are unhittable and clicks "do nothing".
                ClearSelection();
                ClearSecondaryPages();
                RenderAllAnnotations(pageIndex);
                SetStatus(string.Format(Loc("Str_PageOf"), pageIndex + 1, _doc!.PageCount));
                // Defer additional pages until layout has settled so ActualWidth is valid.
                // RenderPageLinks runs AFTER RenderAdditionalPages so ClearSecondaryPages
                // inside RenderAdditionalPages doesn't wipe the overlays we just added.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                {
                    RenderAdditionalPages(pageIndex);
                    RenderPageLinks(pageIndex, dipW, dipH);
                });
            }
            catch (Exception ex)
            {
                PageImage.Source = null;
                SetStatus(string.Format(Loc("Str_RenderError"), ex.Message));
            }
        }

        /// <summary>
        /// Clears all dynamically-added secondary page borders from the panel,
        /// leaving only the first child (the primary page border).
        /// </summary>
        private void ClearSecondaryPages()
        {
            if (_pageContentPanel is null) return;
            // Explicitly null out Image sources before removing so the GC can
            // reclaim the WriteableBitmap backing arrays promptly.
            while (_pageContentPanel.Children.Count > 1)
            {
                var child = _pageContentPanel.Children[^1];
                if (child is Border b && b.Child is Grid g)
                {
                    foreach (var gc in g.Children)
                        if (gc is Image img) img.Source = null;
                }
                _pageContentPanel.Children.RemoveAt(_pageContentPanel.Children.Count - 1);
            }
            // NOTE: do NOT reset _pageContentPanel.Width here.  Width is managed exclusively
            // by RenderAdditionalPages (which runs only via Dispatcher) so that no synchronous
            // call to ClearSecondaryPages triggers an intermediate layout pass that would cause
            // the primary page to flash centered and then jerk back to left-aligned.
            // Clear any link overlays from the annotation canvas.
            foreach (var lo in _linkOverlays)
                _annotationCanvas.Children.Remove(lo);
            _linkOverlays.Clear();
        }

        /// <summary>
        /// Renders secondary pages as a grid. Panel-width setup is synchronous so layout
        /// is correct immediately; Docnet pixel rendering runs on a background thread so
        /// the UI stays responsive. WPF element creation returns to the UI thread.
        /// </summary>
        private async void RenderAdditionalPages(int primaryPageIdx)
        {
            if (_currentFile is null || _doc is null) return;
            // Grid is a stable overview anchored at page 0 (independent of the selected page), so it
            // always shows the whole document instead of only the selected page onward.
            if (_viewMode == ViewMode.Grid) primaryPageIdx = 0;
            ClearSecondaryPages();

            double viewportW = PagePreviewPanel.ActualWidth;
            if (viewportW <= 0 || _doc.PageCount <= 1)
            {
                _pageContentPanel.Width = double.NaN;
                return;
            }

            // Snap the WrapPanel width to a whole number of page-width slots.
            double primaryPageW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 595;
            double pageSlotW = primaryPageW + 12;
            double availablePreZoom = (viewportW - 24) / _zoomLevel;
            int pagesPerRow = _viewMode == ViewMode.TwoPage ? 2 : Math.Max(1, (int)(availablePreZoom / pageSlotW));
            double panelW = pagesPerRow * pageSlotW;
            if (panelW > 0) _pageContentPanel.Width = panelW;

            // Cancel any previously running secondary render.
            _secondaryRenderCts?.Cancel();
            _secondaryRenderCts = new System.Threading.CancellationTokenSource();
            var cts = _secondaryRenderCts;

            // Secondary pages: fixed 1536 px cap regardless of DPI/zoom.
            const int SecondaryMax = 1536;
            // Grid shows the whole document; Two-Page shows one secondary; other modes peek ahead.
            int limit = _viewMode == ViewMode.Grid
                ? _doc.PageCount
                : Math.Min(_doc.PageCount, primaryPageIdx + 1 + (_viewMode == ViewMode.TwoPage ? 1 : 25));
            if (limit <= primaryPageIdx + 1) return;

            string currentFile = _currentFile;

            // Collect rotations on the UI thread before the background task.
            var secRotations = new Dictionary<int, int>();
            for (int i = primaryPageIdx + 1; i < limit; i++)
                if (_pageRotations.TryGetValue(i, out int r) && r != 0)
                    secRotations[i] = r;

            // Capture the primary page width and reset the tile map on the UI thread before
            // streaming tiles in from the background render.
            double primaryDipW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 595;
            _continuousCanvases.Clear();

            // Render pixels on a background thread and attach each page tile to the UI as soon
            // as it is ready, so large documents fill in progressively instead of blocking
            // until every page has been rendered.
            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    using var docReader = DocLib.Instance.GetDocReader(currentFile, new PageDimensions(SecondaryMax, SecondaryMax));
                    for (int i = primaryPageIdx + 1; i < limit; i++)
                    {
                        if (cts.IsCancellationRequested) break;
                        using var pageReader = docReader.GetPageReader(i);
                        int w = pageReader.GetPageWidth();
                        int h = pageReader.GetPageHeight();
                        var rawBytes = pageReader.GetImage();
                        if (w <= 0 || h <= 0 || rawBytes is null) continue;
                        if (secRotations.TryGetValue(i, out int rot))
                            (rawBytes, w, h) = RotateBitmap(rawBytes, w, h, rot);

                        int pi = i, pw = w, ph = h;
                        byte[] bytes = rawBytes;
                        Dispatcher.Invoke(() =>
                        {
                            if (cts.IsCancellationRequested || _doc is null) return;
                            if (_viewMode != ViewMode.Grid && _viewMode != ViewMode.TwoPage) return;
                            AddSecondaryTile(pi, pw, ph, bytes, primaryDipW);
                        });
                    }
                }, cts.Token);
            }
            catch { return; }
        }

        /// <summary>
        /// Builds one secondary-page tile (image + annotation overlay + links) and appends it
        /// to the page content panel. Must run on the UI thread.
        /// </summary>
        private void AddSecondaryTile(int pi, int w, int h, byte[] rawBytes, double primaryDipW)
        {
            int pageDipW = (int)Math.Round(primaryDipW);
            int pageDipH = (int)Math.Round(primaryDipW * h / w);
            double bitmapDpiX = 96.0 * w / pageDipW;
            double bitmapDpiY = 96.0 * h / pageDipH;

            // Do NOT overwrite _renderDims if the page was already rendered as primary -
            // its annotation coordinate mapping must stay intact.
            if (!_renderDims.ContainsKey(pi))
                _renderDims[pi] = (pageDipW, pageDipH);

            var bitmap = new WriteableBitmap(w, h, bitmapDpiX, bitmapDpiY, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, w, h), rawBytes, w * 4, 0);

            var img = new Image { Source = bitmap, Stretch = Stretch.None };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var overlay = new Canvas
            {
                Width = pageDipW, Height = pageDipH,
                Background = Brushes.Transparent,
                Cursor = CursorForTool(_currentTool),
                Tag = pi,
                ToolTip = $"Page {pi + 1}"
            };
            int capturedPi = pi;
            overlay.PreviewMouseLeftButtonDown += (s, ev) =>
            {
                if (_currentTool == EditTool.Select)
                {
                    var hit = ev.GetPosition((Canvas)s);
                    bool onAnnot = (_annotations.TryGetValue(capturedPi, out var list)
                                    && list.Any(a => HitTestAnnotation(a, hit, out _)))
                                   || _selectedAnnotation?.PageIndex == capturedPi;
                    if (onAnnot) Canvas_MouseLeftButtonDown(s, ev);
                    else PageList.SelectedIndex = capturedPi;
                }
                else Canvas_MouseLeftButtonDown(s, ev);
            };
            overlay.MouseMove                += Canvas_MouseMove;
            overlay.PreviewMouseLeftButtonUp += Canvas_MouseLeftButtonUp;
            _continuousCanvases[pi] = overlay;

            var pageGrid = new Grid();
            pageGrid.Children.Add(img);
            pageGrid.Children.Add(overlay);
            AddSecondaryPageLinks(pi, pageGrid, pageDipW, pageDipH);

            _pageContentPanel.Children.Add(new Border
            {
                Background = Brushes.White,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 12, 12),
                Child = pageGrid
            });
            RenderAllAnnotations(pi);
        }

        /// <summary>Look up a localized string. Falls back to the key name if missing.</summary>
        private string Loc(string key)
            => Application.Current.TryFindResource(key) as string ?? key;

                private void SetStatus(string text)
        {
            StatusText.Text = text;
            CrashReporter.PushStatusMessage(text);
        }

        private void VersionLabel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ShowAboutOverlay();
        }

        /// <summary>
        /// Re-renders secondary pages and then link overlays for the current page.
        /// Must be called via Dispatcher so layout is settled before RenderAdditionalPages
        /// reads ActualWidth. All zoom-change and sidebar-toggle dispatch sites use this
        /// instead of a bare RenderAdditionalPages call so link overlays are never left
        /// cleared without being re-added.
        /// </summary>
        private void RefreshPageView(int pageIndex)
        {
            if (_viewMode == ViewMode.Continuous)
                return; // continuous mode manages its own rendering

            // Grid fits its columns to the viewport, so it never needs a horizontal scrollbar.
            // Leaving it on Auto shows a stray (green) thumb across the bottom when the tile panel
            // overflows by the vertical scrollbar's width. Disable it for grid, Auto elsewhere.
            PagePreviewPanel.HorizontalScrollBarVisibility =
                _viewMode == ViewMode.Grid ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            // Single page is centered; drop the right/bottom tile-gap margin that grid/two-page
            // need for spacing (it would otherwise push the lone page a few px left of center).
            if (_pageContentPanel is not null && _pageContentPanel.Children.Count > 0
                && _pageContentPanel.Children[0] is Border primaryBorder)
                primaryBorder.Margin = _viewMode == ViewMode.Single
                    ? new Thickness(0) : new Thickness(0, 0, 12, 12);
            if (_viewMode == ViewMode.Grid || _viewMode == ViewMode.TwoPage)
                RenderAdditionalPages(pageIndex);
            else
            {
                ClearSecondaryPages();
                if (_pageContentPanel is not null)
                    _pageContentPanel.Width = double.NaN;
            }
            if (_renderDims.TryGetValue(pageIndex, out var dims))
                RenderPageLinks(pageIndex, dims.w, dims.h);
        }



        // ============================================================
        // PDF Link Annotation Overlays
        // ============================================================

        private readonly record struct LinkInfo(double Cx, double Cy, double Cw, double Ch, object Tag, string Tip, int AnnotIndex);

        /// <summary>
        /// Carries the link target (page index or URI string) plus the annotation's location in
        /// the PDF so the overlay can be used to remove the native annotation on demand.
        /// </summary>
        private sealed class LinkAnnotInfo(object target, int pageIndex, int annotIndex)
        {
            public object   Target     { get; } = target;      // int pageIndex or string URI
            public int      PageIndex  { get; } = pageIndex;   // 0-based page in _doc
            public int      AnnotIndex { get; } = annotIndex;  // index inside page /Annots array
        }

        /// <summary>
        /// Parses all link annotations from a PDF page and converts them to canvas-space
        /// rectangles. Works for both primary and secondary page renders.
        /// </summary>
        private List<LinkInfo> GetPageLinks(int pageIndex, int bitmapW, int bitmapH)
        {
            var links = new List<LinkInfo>();
            if (_doc is null) return links;
            try
            {
                var pdfPage = _doc.Pages[pageIndex];
                var annotsArr = pdfPage.Elements.GetArray("/Annots");
                if (annotsArr is null || annotsArr.Elements.Count == 0) return links;

                double pageWidthPt  = pdfPage.Width.Point;
                double pageHeightPt = pdfPage.Height.Point;
                if (pageWidthPt  <= 0) pageWidthPt  = 595.28;
                if (pageHeightPt <= 0) pageHeightPt = 841.89;

                for (int i = 0; i < annotsArr.Elements.Count; i++)
                {
                    PdfItem? elem = annotsArr.Elements[i];
                    PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                    if (ann is null) continue;

                    var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                    if (!subtype.Contains("Link")) continue;

                    var rectArr = ann.Elements.GetArray("/Rect");
                    if (rectArr is null || rectArr.Elements.Count < 4) continue;
                    double rx1 = rectArr.Elements.GetReal(0);
                    double ry1 = rectArr.Elements.GetReal(1);
                    double rx2 = rectArr.Elements.GetReal(2);
                    double ry2 = rectArr.Elements.GetReal(3);
                    if (rx1 > rx2) (rx1, rx2) = (rx2, rx1);
                    if (ry1 > ry2) (ry1, ry2) = (ry2, ry1);

                    double cx = rx1 / pageWidthPt  * bitmapW;
                    double cy = (pageHeightPt - ry2) / pageHeightPt * bitmapH;
                    double cw = (rx2 - rx1) / pageWidthPt  * bitmapW;
                    double ch = (ry2 - ry1) / pageHeightPt * bitmapH;
                    if (cw < 1 || ch < 1) continue;

                    int? targetPage = null;
                    string? uri = null;

                    var actionDict = ann.Elements.GetDictionary("/A");
                    if (actionDict != null)
                    {
                        var s = actionDict.Elements["/S"]?.ToString() ?? "";
                        if (s.Contains("GoTo"))
                            targetPage = ResolveDest(actionDict.Elements["/D"]);
                        else if (s.Contains("URI"))
                            uri = actionDict.Elements.GetString("/URI");
                    }
                    else
                    {
                        targetPage = ResolveDest(ann.Elements["/Dest"]);
                    }

                    if (targetPage is null && uri is null) continue;

                    object tag = targetPage.HasValue ? (object)targetPage.Value : uri!;
                    string tip = targetPage.HasValue ? $"Go to page {targetPage.Value + 1}" : uri!;
                    links.Add(new LinkInfo(cx, cy, cw, ch, tag, tip, i));
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GetPageLinks: {ex}"); }
            return links;
        }

        /// <summary>
        /// Renders link overlays for the primary page onto the annotation canvas.
        /// Uses a manual bounds-check in Canvas_MouseLeftButtonDown for hit detection
        /// (transparent Canvas children are unreliable for WPF hit-testing alone).
        /// </summary>
        private void RenderPageLinks(int pageIndex, int bitmapW, int bitmapH)
        {
            if (_doc is null || _currentFile is null) return;

            var links = GetPageLinks(pageIndex, bitmapW, bitmapH);
            foreach (var lnk in links)
            {
                var info = new LinkAnnotInfo(lnk.Tag, pageIndex, lnk.AnnotIndex);
                var overlay = new Canvas
                {
                    Width            = lnk.Cw,
                    Height           = lnk.Ch,
                    Background       = Brushes.Transparent,
                    Cursor           = Cursors.Hand,
                    ToolTip          = lnk.Tip,
                    Tag              = info,
                    IsHitTestVisible = true,
                };
                Canvas.SetLeft(overlay, lnk.Cx);
                Canvas.SetTop(overlay, lnk.Cy);

                // Right-click context menu: remove the native PDF annotation or copy the URL.
                var cm = new ContextMenu();
                if (lnk.Tag is string uriTag && uriTag.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    cm.Items.Add(MakeMenuItem("Copy Email Address", (_, _) =>
                        Clipboard.SetText(uriTag["mailto:".Length..])));
                else if (lnk.Tag is string httpTag)
                    cm.Items.Add(MakeMenuItem("Copy URL", (_, _) => Clipboard.SetText(httpTag)));
                cm.Items.Add(MakeMenuItem("Remove Link from PDF", (_, _) =>
                    RemoveLinkAnnotation(info.PageIndex, info.AnnotIndex)));
                overlay.ContextMenu = cm;

                _annotationCanvas.Children.Add(overlay);
                _linkOverlays.Add(overlay);
            }

            if (links.Count > 0)
                SetStatus(string.Format(Loc("Str_PageOfLinks"), pageIndex + 1, _doc.PageCount, links.Count));
        }

        /// <summary>
        /// Removes a native PDF link annotation from the page /Annots array and persists the change.
        /// Called from the "Remove Link from PDF" context-menu item on link overlays.
        /// </summary>
        private void RemoveLinkAnnotation(int pageIndex, int annotIndex)
        {
            if (_doc is null || pageIndex >= _doc.PageCount) return;
            try
            {
                var pdfPage = _doc.Pages[pageIndex];
                var annotsArr = pdfPage.Elements.GetArray("/Annots");
                if (annotsArr is null || annotIndex >= annotsArr.Elements.Count) return;

                // Neutralize the annotation object before removing the /Annots reference.
                // If PdfSharpCore writes the orphaned indirect object to the output file,
                // aggressive PDF viewers that scan cross-reference tables directly (rather
                // than following /Annots) would still trigger the link without this step.
                PdfItem? elem = annotsArr.Elements[annotIndex];
                PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                if (ann != null)
                {
                    ann.Elements.Remove("/A");
                    ann.Elements.Remove("/Dest");
                    ann.Elements.Remove("/Subtype");
                }

                annotsArr.Elements.RemoveAt(annotIndex);
                MarkDirty();
                SaveTempAndReload();
                // Refresh the current page view so the overlay disappears.
                int sel = PageList.SelectedIndex;
                PageList.SelectedIndex = -1;
                PageList.SelectedIndex = sel;
                SetStatus(Loc("Str_LinkRemoved"));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Remove link failed:\n{ex.Message}", "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Strips visual styling (border, color, appearance stream) from all Link annotations
        /// in the document so they render as invisible clickable areas rather than colored
        /// rectangles that can look like strikethroughs in other PDF viewers.
        /// </summary>
        private static void StripLinkAnnotationBorders(PdfDocument doc)
        {
            foreach (var pdfPage in doc.Pages)
            {
                var annotsArr = pdfPage.Elements.GetArray("/Annots");
                if (annotsArr is null) continue;
                for (int i = 0; i < annotsArr.Elements.Count; i++)
                {
                    PdfItem? elem = annotsArr.Elements[i];
                    PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                    if (ann is null) continue;

                    // Dereference subtype in case it is an indirect name.
                    var subtypeItem = ann.Elements["/Subtype"];
                    var subtype = (subtypeItem as PdfDictionary ?? DerefItem(subtypeItem) as PdfDictionary) is null
                        ? subtypeItem?.ToString() ?? ""
                        : "";
                    if (!subtype.Contains("Link")) continue;

                    // Remove appearance stream and color.
                    ann.Elements.Remove("/AP");
                    ann.Elements.Remove("/C");

                    // /BS (border style dict) takes precedence over /Border in PDF spec;
                    // set W=0 explicitly.  Also set /Border [0 0 0] for older viewers.
                    var bs = new PdfDictionary();
                    bs.Elements["/W"] = new PdfInteger(0);
                    ann.Elements["/BS"] = bs;

                    var borderArr = new PdfArray();
                    borderArr.Elements.Add(new PdfInteger(0));
                    borderArr.Elements.Add(new PdfInteger(0));
                    borderArr.Elements.Add(new PdfInteger(0));
                    ann.Elements["/Border"] = borderArr;
                }
            }
        }

        /// <summary>
        /// Adds link overlays to a secondary-page Grid so PDF links within that page are
        /// clickable even when the page is visible only in the multi-page grid view.
        ///
        /// Canvas.SetLeft/Top attached properties ONLY take effect when the element's
        /// direct parent is a Canvas.  Adding link elements straight into the Grid (as
        /// siblings of the page-nav overlay) would leave them all at (0,0), causing every
        /// click to hit the wrong element.  Instead we create a transparent Canvas
        /// container the same size as the page and use it as the coordinate space.
        // ============================================================
        // PDF Form Field Overlays
        // ============================================================

        private readonly record struct FormFieldInfo(
            int    ObjNum,        // widget annotation object number (used as key)
            string FieldType,     // /Tx, /Btn, /Ch
            bool   IsCheckBox,
            bool   IsRadio,
            bool   IsMultiLine,   // /Tx with Multiline flag (bit 12)
            string FieldName,
            string CurrentValue,
            string OnValue,       // radio/checkbox on-state value (e.g. "/Yes")
            bool   IsReadOnly,
            double Cx, double Cy, double Cw, double Ch,
            List<string> Options);

        /// <summary>
        /// Scans the current page's /Annots for Widget subtypes and overlays interactive
        /// WPF controls on the annotation canvas so the user can fill in form fields.
        /// </summary>
        private void RenderFormFields(int pageIndex, int canvasW, int canvasH)
        {
            if (_doc is null || _currentFile is null) return;
            if (pageIndex >= _doc.PageCount) return;

            // Remove stale overlays without wiping the entire canvas.
            for (int i = _annotationCanvas.Children.Count - 1; i >= 0; i--)
                if (_annotationCanvas.Children[i] is FrameworkElement fe && fe.Tag as string == FormOverlayTag)
                    _annotationCanvas.Children.RemoveAt(i);

            var fields = GetPageFormFields(pageIndex, canvasW, canvasH);
            if (fields.Count == 0) return;

            var green      = Color.FromRgb(0x4a, 0xde, 0x80);
            var greenBrush = new SolidColorBrush(green);
            var darkBrush  = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
            var fieldBg    = new SolidColorBrush(Color.FromArgb(200, 255, 253, 231));

            // Collect radio buttons per group so we can wire mutual exclusion after the loop.
            var radioGroups = new Dictionary<string, List<(Ellipse dot, string onVal)>>();

            bool anyField = false;
            foreach (var f in fields)
            {
                UIElement? ctrl = null;

                // ── Text field ────────────────────────────────────────────────────
                if (!f.IsCheckBox && !f.IsRadio && f.FieldType != "/Ch")
                {
                    string cur     = _formTextValues.TryGetValue(f.ObjNum, out var tv) ? tv : f.CurrentValue;
                    // Use the shorter canvas dimension as the font size reference so that
                    // rotated fields (where Cw and Ch are swapped vs. portrait) don't blow up.
                    double fieldShort = Math.Min(f.Cw, f.Ch);
                    double fontSize = f.IsMultiLine
                        ? fieldShort * 0.18
                        : fieldShort * 0.65;
                    fontSize = Math.Max(10, fontSize);
                    var tb = new TextBox
                    {
                        Tag              = FormOverlayTag,
                        Width            = f.Cw,
                        Height           = f.Ch,
                        Text             = cur,
                        IsReadOnly       = f.IsReadOnly,
                        AcceptsReturn    = f.IsMultiLine,
                        TextWrapping     = f.IsMultiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
                        VerticalScrollBarVisibility = f.IsMultiLine
                            ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
                        Background       = fieldBg,
                        Foreground       = Brushes.Black,
                        CaretBrush       = Brushes.Black,
                        BorderBrush      = greenBrush,
                        BorderThickness  = new Thickness(1),
                        FontSize         = fontSize,
                        Padding          = new Thickness(3, 0, 3, 0),
                        VerticalContentAlignment = f.IsMultiLine
                            ? VerticalAlignment.Top : VerticalAlignment.Center,
                        ToolTip          = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };
                    // Highlight border on focus so users can see which field is active.
                    tb.GotFocus  += (_, _) => tb.BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                    tb.LostFocus += (_, _) => tb.BorderBrush = greenBrush;
                    int capturedKey = f.ObjNum;
                    tb.TextChanged += (_, _) => { _formTextValues[capturedKey] = tb.Text; MarkDirty(true); };
                    ctrl = tb;
                }

                // ── Dropdown / choice ─────────────────────────────────────────────
                else if (f.FieldType == "/Ch" && f.Options.Count > 0)
                {
                    string cur = _formTextValues.TryGetValue(f.ObjNum, out var tv) ? tv : f.CurrentValue;
                    var combo = new ComboBox
                    {
                        Tag       = FormOverlayTag,
                        Width     = f.Cw,
                        Height    = f.Ch,
                        IsEnabled = !f.IsReadOnly,
                        Foreground = Brushes.Black,
                        FontSize  = Math.Max(10, Math.Min(f.Cw, f.Ch) * 0.65),
                        ToolTip   = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };
                    foreach (var opt in f.Options) combo.Items.Add(opt);
                    combo.SelectedItem = cur;
                    int capturedKey = f.ObjNum;
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (combo.SelectedItem is string s) { _formTextValues[capturedKey] = s; MarkDirty(true); }
                    };
                    ctrl = combo;
                }

                // ── Checkbox ──────────────────────────────────────────────────────
                else if (f.IsCheckBox)
                {
                    bool isChecked = _formCheckValues.TryGetValue(f.ObjNum, out var cv) ? cv
                        : !string.IsNullOrEmpty(f.CurrentValue)
                          && f.CurrentValue != "/Off" && f.CurrentValue != "Off";

                    // Custom border-based checkbox — WPF's built-in CheckBox indicator
                    // doesn't scale with Width/Height, so we draw it ourselves.
                    double checkFs = Math.Min(f.Cw, f.Ch) * 0.72;
                    var checkMark = new TextBlock
                    {
                        Text       = "✓",
                        FontSize   = checkFs,
                        FontWeight = FontWeights.Bold,
                        Foreground = darkBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed,
                    };
                    var box = new Border
                    {
                        Tag             = FormOverlayTag,
                        Width           = f.Cw,
                        Height          = f.Ch,
                        Background      = fieldBg,
                        BorderBrush     = greenBrush,
                        BorderThickness = new Thickness(1.5),
                        CornerRadius    = new CornerRadius(2),
                        Cursor          = f.IsReadOnly ? Cursors.Arrow : Cursors.Hand,
                        Child           = checkMark,
                        ToolTip         = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };
                    if (!f.IsReadOnly)
                    {
                        int capturedKey = f.ObjNum;
                        box.MouseLeftButtonDown += (_, e) =>
                        {
                            bool now = !(_formCheckValues.TryGetValue(capturedKey, out var v) ? v : isChecked);
                            _formCheckValues[capturedKey] = now;
                            checkMark.Visibility = now ? Visibility.Visible : Visibility.Collapsed;
                            MarkDirty(true);
                            e.Handled = true;
                        };
                    }
                    ctrl = box;
                }

                // ── Radio button ──────────────────────────────────────────────────
                else if (f.IsRadio)
                {
                    string groupSelected = _formRadioValues.TryGetValue(f.FieldName, out var rv) ? rv
                        : f.CurrentValue; // CurrentValue = parent /V = currently selected on-value
                    bool isSelected = groupSelected == f.OnValue;

                    double size  = Math.Min(f.Cw, f.Ch) * 0.88;
                    double inner = size * 0.52;

                    var dot = new Ellipse
                    {
                        Width      = inner,
                        Height     = inner,
                        Fill       = darkBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed,
                    };
                    var ring = new Ellipse
                    {
                        Width           = size,
                        Height          = size,
                        Stroke          = greenBrush,
                        StrokeThickness = 1.5,
                        Fill            = fieldBg,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                    };
                    var grid = new Grid { Width = f.Cw, Height = f.Ch };
                    grid.Children.Add(ring);
                    grid.Children.Add(dot);

                    var radioBorder = new Border
                    {
                        Tag    = FormOverlayTag,
                        Width  = f.Cw,
                        Height = f.Ch,
                        Background = Brushes.Transparent,
                        Cursor = f.IsReadOnly ? Cursors.Arrow : Cursors.Hand,
                        Child  = grid,
                        ToolTip = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };

                    // Register dot for mutual-exclusion wiring after the loop.
                    if (!radioGroups.TryGetValue(f.FieldName, out var groupList))
                        radioGroups[f.FieldName] = groupList = [];
                    groupList.Add((dot, f.OnValue));

                    if (!f.IsReadOnly)
                    {
                        string capturedGroup = f.FieldName;
                        string capturedOn    = f.OnValue;
                        radioBorder.MouseLeftButtonDown += (_, e) =>
                        {
                            _formRadioValues[capturedGroup] = capturedOn;
                            // Deselect all in group, then select this one.
                            if (radioGroups.TryGetValue(capturedGroup, out var gl))
                                foreach (var (d, ov) in gl)
                                    d.Visibility = ov == capturedOn ? Visibility.Visible : Visibility.Collapsed;
                            MarkDirty(true);
                            e.Handled = true;
                        };
                    }
                    ctrl = radioBorder;
                }

                if (ctrl is null) continue;
                Canvas.SetLeft(ctrl, f.Cx);
                Canvas.SetTop(ctrl, f.Cy);
                _annotationCanvas.Children.Add(ctrl);
                anyField = true;
            }

            if (anyField)
                SetStatus(string.Format(Loc("Str_PageFormFields"), pageIndex + 1, _doc.PageCount));
        }

        /// <summary>
        /// Parses Widget annotations from the given page into field descriptors with canvas coordinates.
        /// Walks the parent chain for each widget to resolve inherited /FT, /T, /V, and /Ff.
        /// </summary>
        private List<FormFieldInfo> GetPageFormFields(int pageIndex, int canvasW, int canvasH)
        {
            var result = new List<FormFieldInfo>();
            if (_doc is null || pageIndex >= _doc.PageCount) return result;

            var page = _doc.Pages[pageIndex];
            // Use the MediaBox directly — PdfSharpCore swaps page.Width/Height for 90°/270°
            // rotated pages to return visual dimensions, but field /Rect coords are always
            // in the unrotated MediaBox coordinate space.
            var mediaBox = page.MediaBox;
            double pageW = mediaBox.Width  > 0 ? mediaBox.Width  : 595.28;
            double pageH = mediaBox.Height > 0 ? mediaBox.Height : 841.89;
            int rotation = ((page.Rotate % 360) + 360) % 360;

            try
            {
                var annotsArr = page.Elements.GetArray("/Annots");
                if (annotsArr is null || annotsArr.Elements.Count == 0) return result;

                for (int i = 0; i < annotsArr.Elements.Count; i++)
                {
                    PdfItem? elem   = annotsArr.Elements[i];
                    PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                    if (ann is null) continue;

                    var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                    if (!subtype.Contains("Widget")) continue;

                    // Get rect
                    var rectArr = ann.Elements.GetArray("/Rect");
                    if (rectArr is null || rectArr.Elements.Count < 4) continue;
                    double rx1 = rectArr.Elements.GetReal(0);
                    double ry1 = rectArr.Elements.GetReal(1);
                    double rx2 = rectArr.Elements.GetReal(2);
                    double ry2 = rectArr.Elements.GetReal(3);
                    if (rx1 > rx2) (rx1, rx2) = (rx2, rx1);
                    if (ry1 > ry2) (ry1, ry2) = (ry2, ry1);

                    // Map PDF rect (bottom-left origin, unrotated) to canvas coords.
                    // The canvas matches the Docnet-rendered bitmap which has already applied
                    // the page rotation, so we must transform accordingly.
                    double cx, cy, cw, ch;
                    switch (rotation)
                    {
                        case 90: // 90° CW: bottom→left, left→top; canvas is pageH-wide × pageW-tall
                            // (px,py) → canvas (py, px)
                            cx = ry1             / pageH * canvasW;
                            cy = rx1             / pageW * canvasH;
                            cw = (ry2 - ry1)     / pageH * canvasW;
                            ch = (rx2 - rx1)     / pageW * canvasH;
                            break;
                        case 180: // 180°: both axes flipped
                            // (px,py) → canvas (pageW-px, py)
                            cx = (pageW - rx2)   / pageW * canvasW;
                            cy = ry1             / pageH * canvasH;
                            cw = (rx2 - rx1)     / pageW * canvasW;
                            ch = (ry2 - ry1)     / pageH * canvasH;
                            break;
                        case 270: // 270° CW (= 90° CCW): bottom→right, right→top; canvas is pageH-wide × pageW-tall
                            // (px,py) → canvas (pageH-py, pageW-px)
                            cx = (pageH - ry2)   / pageH * canvasW;
                            cy = (pageW - rx2)   / pageW * canvasH;
                            cw = (ry2 - ry1)     / pageH * canvasW;
                            ch = (rx2 - rx1)     / pageW * canvasH;
                            break;
                        default: // 0° — standard bottom-left PDF → top-left canvas
                            cx = rx1             / pageW * canvasW;
                            cy = (pageH - ry2)   / pageH * canvasH;
                            cw = (rx2 - rx1)     / pageW * canvasW;
                            ch = (ry2 - ry1)     / pageH * canvasH;
                            break;
                    }
                    if (cw < 2 || ch < 2) continue;

                    // Walk the parent chain to resolve inherited attributes
                    string ft     = "";
                    string name   = "";
                    string curVal = "";
                    int    flags  = 0;
                    var    options = new List<string>();

                    PdfDictionary? node = ann;
                    while (node is not null)
                    {
                        if (string.IsNullOrEmpty(ft)   && node.Elements["/FT"] is not null)
                            ft = node.Elements["/FT"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(name) && node.Elements["/T"] is PdfString ts)
                            name = ts.Value;
                        if (string.IsNullOrEmpty(curVal) && node.Elements["/V"] is not null)
                        {
                            var vElem = node.Elements["/V"];
                            curVal = vElem is PdfString vs ? vs.Value : vElem?.ToString() ?? "";
                        }
                        if (flags == 0 && node.Elements["/Ff"] is PdfInteger fi)
                            flags = fi.Value;
                        if (options.Count == 0 && node.Elements.GetArray("/Opt") is PdfArray optArr)
                        {
                            for (int j = 0; j < optArr.Elements.Count; j++)
                            {
                                var o = optArr.Elements[j];
                                if (o is PdfString ps2) options.Add(ps2.Value);
                                else if (o is PdfArray pa2 && pa2.Elements.Count >= 2)
                                    options.Add((pa2.Elements[1] as PdfString)?.Value ?? "");
                            }
                        }

                        // Move to parent
                        var parentItem = node.Elements["/Parent"];
                        if (parentItem is null) break;
                        node = parentItem as PdfDictionary ?? DerefItem(parentItem) as PdfDictionary;
                    }

                    if (string.IsNullOrEmpty(ft)) ft = "/Tx";

                    bool isReadOnly  = (flags & 1) != 0;
                    bool isMultiLine = ft.Contains("Tx") && (flags & 4096) != 0;
                    bool isPushBtn   = ft.Contains("Btn") && (flags & (1 << 16)) != 0;
                    bool isRadio     = ft.Contains("Btn") && !isPushBtn && (flags & (1 << 15)) != 0;
                    bool isCheckBox  = ft.Contains("Btn") && !isPushBtn && !isRadio;

                    // Extract the "on" value for this widget (radio/checkbox selected state).
                    // Found in /AP /N as the key that is NOT /Off.
                    string onValue = "/Yes";
                    try
                    {
                        var apDict = ann.Elements.GetDictionary("/AP");
                        var nDict  = apDict?.Elements.GetDictionary("/N");
                        if (nDict is not null)
                            foreach (var k in nDict.Elements.Keys)
                                if (k != "/Off") { onValue = k; break; }
                    }
                    catch { }

                    int objNum = GetObjectNumber(elem);
                    if (objNum < 0)
                        objNum = -(pageIndex * 10000 + i); // synthetic key for inline dicts

                    result.Add(new FormFieldInfo(objNum, ft, isCheckBox, isRadio, isMultiLine,
                        name, curVal, onValue, isReadOnly, cx, cy, cw, ch, options));
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GetPageFormFields: {ex}"); }

            return result;
        }

        /// <summary>
        /// Writes all filled form values back into the PDF document's AcroForm field dictionaries.
        /// Called just before saving so values are persisted in the output file.
        /// </summary>
        private void WriteFormValuesToDocument()
        {
            if (_doc is null) return;
            if (_formTextValues.Count == 0 && _formCheckValues.Count == 0 && _formRadioValues.Count == 0) return;

            try
            {
                for (int p = 0; p < _doc.PageCount; p++)
                {
                    var page = _doc.Pages[p];
                    var annotsArr = page.Elements.GetArray("/Annots");
                    if (annotsArr is null) continue;

                    for (int i = 0; i < annotsArr.Elements.Count; i++)
                    {
                        PdfItem? elem = annotsArr.Elements[i];
                        PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                        if (ann is null) continue;

                        var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                        if (!subtype.Contains("Widget")) continue;

                        int objNum = GetObjectNumber(elem);
                        if (objNum < 0) objNum = -(p * 10000 + i);

                        // Walk parent chain to find the canonical field dict (owns /FT)
                        PdfDictionary? fieldDict = ann;
                        PdfDictionary? node = ann;
                        while (node is not null)
                        {
                            if (node.Elements["/FT"] is not null) { fieldDict = node; break; }
                            var pi = node.Elements["/Parent"];
                            if (pi is null) break;
                            node = pi as PdfDictionary ?? DerefItem(pi) as PdfDictionary;
                        }

                        // Gather field rect for AP stream sizing
                        var rectArr = ann.Elements.GetArray("/Rect");
                        double fieldW = 100, fieldH = 20;
                        if (rectArr?.Elements.Count >= 4)
                        {
                            double rx1 = rectArr.Elements.GetReal(0), ry1 = rectArr.Elements.GetReal(1);
                            double rx2 = rectArr.Elements.GetReal(2), ry2 = rectArr.Elements.GetReal(3);
                            fieldW = Math.Abs(rx2 - rx1);
                            fieldH = Math.Abs(ry2 - ry1);
                        }

                        // Resolve /DA for font name/size (walk parent chain)
                        string? daStr = null;
                        node = ann;
                        while (node is not null && daStr is null)
                        {
                            if (node.Elements["/DA"] is PdfString ds) daStr = ds.Value;
                            var pi = node.Elements["/Parent"];
                            if (pi is null) break;
                            node = pi as PdfDictionary ?? DerefItem(pi) as PdfDictionary;
                        }

                        if (_formTextValues.TryGetValue(objNum, out var textVal) && fieldDict is not null)
                        {
                            fieldDict.Elements["/V"] = new PdfString(textVal);
                            GenerateTextFieldAppearance(ann, textVal, daStr, fieldW, fieldH);
                        }
                        else if (_formCheckValues.TryGetValue(objNum, out var checkVal) && fieldDict is not null)
                        {
                            string onVal = "/Yes";
                            try
                            {
                                var apDict = ann.Elements.GetDictionary("/AP");
                                var nDict  = apDict?.Elements.GetDictionary("/N");
                                if (nDict is not null)
                                    foreach (var k in nDict.Elements.Keys)
                                        if (k != "/Off") { onVal = k; break; }
                            }
                            catch { }

                            fieldDict.Elements["/V"]  = new PdfName(checkVal ? onVal : "/Off");
                            fieldDict.Elements["/AS"] = new PdfName(checkVal ? onVal : "/Off");
                            ann.Elements["/AS"]        = new PdfName(checkVal ? onVal : "/Off");
                            GenerateCheckBoxAppearance(ann, checkVal, onVal, fieldW, fieldH);
                        }
                        else if (_formRadioValues.Count > 0 && fieldDict is not null)
                        {
                            // Radio button: look up by field name (shared across all widgets in the group)
                            string ft2 = fieldDict.Elements["/FT"]?.ToString() ?? "";
                            if (ft2.Contains("Btn"))
                            {
                                // Walk to find /T on the parent field node
                                string fieldName2 = "";
                                var n2 = fieldDict;
                                while (n2 is not null && string.IsNullOrEmpty(fieldName2))
                                {
                                    if (n2.Elements["/T"] is PdfString ts2) fieldName2 = ts2.Value;
                                    var pi2 = n2.Elements["/Parent"];
                                    if (pi2 is null) break;
                                    n2 = pi2 as PdfDictionary ?? DerefItem(pi2) as PdfDictionary;
                                }
                                if (_formRadioValues.TryGetValue(fieldName2, out var radioSel))
                                {
                                    // Set /V on the parent field
                                    fieldDict.Elements["/V"] = new PdfName(radioSel);
                                    // Set /AS on this widget to show selected or off
                                    string onVal2 = "/Yes";
                                    try
                                    {
                                        var apD = ann.Elements.GetDictionary("/AP");
                                        var nD  = apD?.Elements.GetDictionary("/N");
                                        if (nD is not null)
                                            foreach (var k in nD.Elements.Keys)
                                                if (k != "/Off") { onVal2 = k; break; }
                                    }
                                    catch { }
                                    ann.Elements["/AS"] = new PdfName(onVal2 == radioSel ? onVal2 : "/Off");
                                }
                            }
                        }
                    }
                }

                // Belt-and-suspenders: also set NeedAppearances in case any AP generation failed
                try
                {
                    var acroForm = _doc.Internals.Catalog.Elements.GetDictionary("/AcroForm");
                    if (acroForm is not null)
                        acroForm.Elements["/NeedAppearances"] = new PdfBoolean(true);
                }
                catch { }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"WriteFormValuesToDocument: {ex}"); }
        }

        /// <summary>
        /// Generates a /AP /N form XObject appearance stream for a text field and sets it
        /// on the widget annotation. Uses reflection to access PdfSharpCore's internal
        /// PdfDictionary.PdfStream constructor since there is no public factory method.
        /// </summary>
        private void GenerateTextFieldAppearance(PdfDictionary widgetAnn, string text, string? da, double fieldW, double fieldH)
        {
            try
            {
                var (fontName, fontSize) = ParseDaString(da);
                if (fontSize <= 0) fontSize = Math.Max(6, Math.Min(fieldH * 0.65, 12));
                fontSize = Math.Max(6, Math.Min(fontSize, fieldH * 0.85));

                // Vertical centering: PDF baseline is measured from bottom of the field rect.
                double textY = (fieldH - fontSize) / 2 + fontSize * 0.2;
                if (textY < 1) textY = 1;

                string escaped = EscapePdfString(text);
                string content =
                    $"/Tx BMC\nq\n0 0 {fieldW:F2} {fieldH:F2} re W n\n" +
                    $"BT\n{fontName} {fontSize:F2} Tf\n0 g\n2 {textY:F2} Td\n({escaped}) Tj\nET\nQ\nEMC";

                var xobj = BuildFormXObject(fontName, fieldW, fieldH, content);
                if (xobj is null) return;

                AttachAppearance(widgetAnn, xobj);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GenerateTextFieldAppearance: {ex}"); }
        }

        /// <summary>
        /// Generates /AP /N (checked) and /AP /Off (unchecked) appearance streams for a
        /// checkbox widget and sets them on the annotation.
        /// </summary>
#pragma warning disable IDE0060 // isChecked unused — both AP states are always generated; /AS selects the active one
        private void GenerateCheckBoxAppearance(PdfDictionary widgetAnn, bool isChecked, string onVal, double fieldW, double fieldH)
#pragma warning restore IDE0060
        {
            try
            {
                double m = Math.Min(fieldW, fieldH) * 0.1; // margin
                double iw = fieldW - m * 2;
                double ih = fieldH - m * 2;

                // Checked: ZapfDingbats "4" = ✔, centred in the field
                double fs = Math.Min(iw, ih) * 0.85;
                double tx = (fieldW - fs * 0.6) / 2;
                double ty = (fieldH - fs) / 2 + fs * 0.15;

                string checkedContent =
                    $"q\nBT\n/ZaDb {fs:F2} Tf\n0 g\n{tx:F2} {ty:F2} Td\n(4) Tj\nET\nQ";

                string offContent = "q\nQ"; // empty — just clears

                // /Resources needs ZapfDingbats font for the checked state
                var checkedXobj = BuildFormXObject("/ZaDb", fieldW, fieldH, checkedContent, isZaDb: true);
                var offXobj     = BuildFormXObject("/ZaDb", fieldW, fieldH, offContent,     isZaDb: true);
                if (checkedXobj is null || offXobj is null) return;

                // /AP dictionary with /N being a sub-dict keyed by state name
                var nDict = new PdfDictionary(_doc);
                nDict.Elements[onVal]  = checkedXobj.Reference;
                nDict.Elements["/Off"] = offXobj.Reference;

                var apDict = new PdfDictionary(_doc);
                apDict.Elements["/N"] = nDict;

                widgetAnn.Elements["/AP"] = apDict;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GenerateCheckBoxAppearance: {ex}"); }
        }

        /// <summary>
        /// Creates an indirect PdfDictionary stream object representing a Form XObject,
        /// suitable for use as an /AP /N appearance stream.
        /// </summary>
        private PdfDictionary? BuildFormXObject(string fontName, double w, double h, string content, bool isZaDb = false)
        {
            byte[] bytes = System.Text.Encoding.GetEncoding("iso-8859-1").GetBytes(content);

            var xobj = new PdfDictionary(_doc);
            xobj.Elements["/Type"]     = new PdfName("/XObject");
            xobj.Elements["/Subtype"]  = new PdfName("/Form");
            xobj.Elements["/FormType"] = new PdfInteger(1);

            var bbox = new PdfArray(_doc);
            bbox.Elements.Add(new PdfReal(0));
            bbox.Elements.Add(new PdfReal(0));
            bbox.Elements.Add(new PdfReal(w));
            bbox.Elements.Add(new PdfReal(h));
            xobj.Elements["/BBox"] = bbox;

            // Inline font resource — avoids adding top-level objects for every field.
            var fontEntry = new PdfDictionary(_doc);
            fontEntry.Elements["/Type"]    = new PdfName("/Font");
            fontEntry.Elements["/Subtype"] = new PdfName("/Type1");
            fontEntry.Elements["/BaseFont"] = isZaDb
                ? new PdfName("/ZapfDingbats")
                : new PdfName("/Helvetica");
            if (!isZaDb)
                fontEntry.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");

            var fontDict = new PdfDictionary(_doc);
            fontDict.Elements[fontName] = fontEntry;

            var res = new PdfDictionary(_doc);
            res.Elements["/Font"] = fontDict;
            xobj.Elements["/Resources"] = res;

            if (!TryAttachStreamBytes(xobj, bytes)) return null;

            _doc!.Internals.AddObject(xobj);
            return xobj;
        }

        /// <summary>
        /// Sets /AP /N on a widget annotation to the given form XObject (indirect ref).
        /// Replaces any existing AP entry.
        /// </summary>
        private static void AttachAppearance(PdfDictionary widgetAnn, PdfDictionary xobj)
        {
            var apDict = new PdfDictionary();
            apDict.Elements["/N"] = xobj.Reference;
            widgetAnn.Elements["/AP"] = apDict;
        }

        /// <summary>
        /// Attaches raw content bytes to a PdfDictionary as a stream.
        /// Accesses PdfDictionary.PdfStream via reflection because its constructor is internal.
        /// Falls back to the backing field if the property setter is protected.
        /// </summary>
        private static bool TryAttachStreamBytes(PdfDictionary dict, byte[] bytes)
        {
            try
            {
                var dictType   = typeof(PdfDictionary);
                var streamType = dictType.GetNestedType("PdfStream",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (streamType is null) return false;

                // Try (byte[], PdfDictionary) ctor first, then (byte[]) only
                System.Reflection.ConstructorInfo? ctor =
                    streamType.GetConstructor(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                        null, [typeof(byte[]), typeof(PdfDictionary)], null) ??
                    streamType.GetConstructor(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                        null, [typeof(byte[])], null);
                if (ctor is null) return false;

                object streamObj = ctor.GetParameters().Length == 2
                    ? ctor.Invoke([bytes, dict])
                    : ctor.Invoke([bytes]);

                // Try public Stream property setter first
                var prop = dictType.GetProperty("Stream",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (prop?.CanWrite == true)
                {
                    prop.SetValue(dict, streamObj);
                    return true;
                }

                // Fall back to the backing field
                var field = dictType.GetField("_stream",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field is not null)
                {
                    field.SetValue(dict, streamObj);
                    return true;
                }

                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Parses a PDF Default Appearance string ("/Helv 12 Tf 0 g") to extract
        /// the font resource name and point size.
        /// </summary>
        private static (string fontName, double fontSize) ParseDaString(string? da)
        {
            string fontName = "/Helv";
            double fontSize = 0;
            if (string.IsNullOrWhiteSpace(da)) return (fontName, fontSize);

            var tokens = da!.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 2 < tokens.Length; i++)
            {
                if (tokens[i + 2] == "Tf" &&
                    double.TryParse(tokens[i + 1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double fs))
                {
                    fontName = tokens[i]; // e.g. "/Helv"
                    fontSize = fs;
                    break;
                }
            }
            return (fontName, fontSize);
        }

        /// <summary>
        /// Escapes a string for use in a PDF literal string (parentheses syntax).
        /// </summary>
        private static string EscapePdfString(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '(':  sb.Append("\\(");  break;
                    case ')':  sb.Append("\\)");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\n': sb.Append("\\n");  break;
                    default:
                        // Keep Latin-1 range; replace anything outside with '?'
                        sb.Append(c < 256 ? c : '?');
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// The container uses Background=null so non-link areas are hit-test-transparent
        /// and clicks fall through to the full-page nav overlay beneath it.  Link
        /// overlays inside the container use Background=Transparent so they ARE hit-
        /// testable and receive clicks.  The container is added last → topmost z-order.
        /// </summary>
        private void AddSecondaryPageLinks(int pageIndex, Grid pageGrid, int bitmapW, int bitmapH)
        {
            var links = GetPageLinks(pageIndex, bitmapW, bitmapH);
            if (links.Count == 0) return;

            // Container: not hit-testable itself (Background=null), but its children are.
            var linkCanvas = new Canvas { Width = bitmapW, Height = bitmapH, Background = null };

            foreach (var lnk in links)
            {
                var lo = new Canvas
                {
                    Width            = lnk.Cw,
                    Height           = lnk.Ch,
                    Background       = Brushes.Transparent,   // must be non-null to be hittable
                    Cursor           = Cursors.Hand,
                    ToolTip          = lnk.Tip,
                    IsHitTestVisible = true,
                };
                Canvas.SetLeft(lo, lnk.Cx);   // works because parent IS a Canvas
                Canvas.SetTop(lo, lnk.Cy);

                var capturedTag = lnk.Tag;
                lo.PreviewMouseLeftButtonDown += (_, args) =>
                {
                    if (capturedTag is int tp)
                        PageList.SelectedIndex = tp;
                    else if (capturedTag is string u)
                        try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
                    args.Handled = true;
                };

                linkCanvas.Children.Add(lo);
            }

            // Add container last so it is topmost in z-order; non-link areas fall through.
            pageGrid.Children.Add(linkCanvas);
        }

        /// <summary>
        /// Resolves a /Dest value (PdfArray, PdfString, or PdfName) to a 0-based page index.
        /// Returns null if the destination cannot be resolved.
        /// Note: PdfReference is internal to PdfSharpCore so we use reflection for ObjectNumber
        /// and var-inferred types instead of the type name.
        /// </summary>
        private int? ResolveDest(PdfItem? destItem)
        {
            if (destItem is null || _doc is null) return null;

            // Dereference indirect object if needed (PdfReference is internal, use duck-typing).
            destItem = DerefItem(destItem);

            PdfArray? arr = null;

            if (destItem is PdfArray a)
            {
                arr = a;
            }
            else if (destItem is PdfString || destItem is PdfName)
            {
                // Named destination — look up in the document catalog
                arr = ResolveNamedDest(destItem);
            }

            if (arr is null || arr.Elements.Count == 0) return null;

            // First element of the destination array is an indirect page reference.
            // PdfReference.ObjectNumber is public but its type is internal; use reflection.
            var pageRefItem = arr.Elements[0];
            int elemObjNum = GetObjectNumber(pageRefItem);
            if (elemObjNum > 0)
            {
                for (int i = 0; i < _doc.PageCount; i++)
                {
                    // PdfPage.Reference (public) gives us access to ObjectNumber
                    var pgRef = _doc.Pages[i].Reference;
                    if (pgRef != null && pgRef.ObjectNumber == elemObjNum)
                        return i;
                }
            }
            else if (pageRefItem is PdfInteger pageInt)
            {
                int pn = pageInt.Value;
                if (pn >= 0 && pn < _doc.PageCount) return pn;
            }

            return null;
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

        /// <summary>
        /// Resolves a named destination (string or name) to a destination array using the
        /// catalog's /Dests dictionary or /Names /Dests name tree.
        /// </summary>
        private PdfArray? ResolveNamedDest(PdfItem nameItem)
        {
            if (_doc is null) return null;
            string name = nameItem switch
            {
                PdfString s => s.Value,
                PdfName   n => n.Value.TrimStart('/'),
                _           => ""
            };
            if (string.IsNullOrEmpty(name)) return null;

            var catalog = _doc.Internals.Catalog;

            // Legacy /Dests dictionary (direct mapping)
            var dests = catalog.Elements.GetDictionary("/Dests");
            if (dests != null)
            {
                PdfItem? val = DerefItem(dests.Elements[name] ?? dests.Elements["/" + name] ?? new PdfInteger(-1));
                if (val is PdfArray da) return da;
                if (val is PdfDictionary dd) return dd.Elements.GetArray("/D");
            }

            // Modern /Names /Dests name tree
            var names = catalog.Elements.GetDictionary("/Names");
            var destTree = names?.Elements.GetDictionary("/Dests");
            if (destTree != null)
                return ResolveNameTree(destTree, name);

            return null;
        }

        /// <summary>
        /// Walks a PDF name tree to find the destination array for the given name.
        /// </summary>
        private static PdfArray? ResolveNameTree(PdfDictionary node, string name)
        {
            // Leaf node: flat /Names array [key val key val ...]
            var namesArr = node.Elements.GetArray("/Names");
            if (namesArr != null)
            {
                for (int i = 0; i + 1 < namesArr.Elements.Count; i += 2)
                {
                    var key = namesArr.Elements[i];
                    string keyStr = key is PdfString ks ? ks.Value : key?.ToString() ?? "";
                    if (keyStr == name)
                    {
                        PdfItem? val = DerefItem(namesArr.Elements[i + 1]);
                        if (val is PdfArray va) return va;
                        if (val is PdfDictionary vd) return vd.Elements.GetArray("/D");
                    }
                }
            }

            // Intermediate node: recurse into /Kids
            var kids = node.Elements.GetArray("/Kids");
            if (kids != null)
            {
                for (int i = 0; i < kids.Elements.Count; i++)
                {
                    PdfItem? kid = DerefItem(kids.Elements[i]);
                    if (kid is PdfDictionary kd)
                    {
                        var result = ResolveNameTree(kd, name);
                        if (result != null) return result;
                    }
                }
            }

            return null;
        }

        // ============================================================
        // Tool selection
        // ============================================================

        // Maps an editing tool to its mouse cursor. Shared by SetTool and by the
        // per-page overlay creation so freshly rendered tiles get the right cursor.
        private static Cursor CursorForTool(EditTool tool) => tool switch
        {
            EditTool.Text => Cursors.IBeam,
            EditTool.Highlight => Cursors.Cross,
            EditTool.Draw => Cursors.Pen,
            EditTool.Signature => Cursors.Pen,
            EditTool.Image => Cursors.Hand,
            EditTool.Crop => Cursors.Cross,
            _ => Cursors.Arrow
        };

        private void SetTool(EditTool tool)
        {
            // Continuous view now supports annotation tools inline via per-page overlays.
            CommitActiveTextBox();
            ClearTextSelection();
            _currentTool = tool;

            var map = new (Button btn, EditTool t)[]
            {
                (_toolSelectBtn, EditTool.Select),
                (_toolTextBtn, EditTool.Text),
                (_toolHighlightBtn, EditTool.Highlight),
                (_toolDrawBtn, EditTool.Draw),
                (_toolSignatureBtn, EditTool.Signature),
                (_toolImageBtn, EditTool.Image),
                (_toolCropBtn, EditTool.Crop)
            };
            foreach (var (btn, t) in map)
            {
                if (t == tool)
                {
                    btn.SetResourceReference(Control.BackgroundProperty, "AccentDim");
                    btn.SetResourceReference(Control.ForegroundProperty, "Accent");
                }
                else
                {
                    btn.Background = Brushes.Transparent;
                    btn.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
                }
            }

            // Apply the tool cursor to every page surface, not just the primary page.
            // In Grid / Two-Page / Continuous modes the secondary tiles are separate
            // overlay canvases tracked in _continuousCanvases; without this they keep
            // the default arrow cursor while only page 1 (_annotationCanvas) updates.
            var toolCursor = CursorForTool(tool);
            _annotationCanvas.Cursor = toolCursor;
            foreach (var overlay in _continuousCanvases.Values)
                overlay.Cursor = toolCursor;

            // Show/hide draw settings bar
            if (tool == EditTool.Draw || tool == EditTool.Highlight)
                ShowDrawSettings(tool);
            else
                HideDrawSettings();

            // Show/hide text tool settings bar
            if (tool == EditTool.Text)
                ShowTextSettings();
            else
                HideTextSettings();

            // Hide signature popup when switching away
            if (tool != EditTool.Signature)
            {
                HideSignaturePopup();
                _pendingSignature = null;
            }

            // Dismiss crop confirm bar when switching away from Crop
            if (tool != EditTool.Crop)
                HideCropConfirmBar();
        }

        private void SidebarToggle_Click(object sender, RoutedEventArgs e)
        {
            _sidebarCollapsed = !_sidebarCollapsed;
            if (_sidebarCollapsed)
            {
                // Save current width before collapsing so expand restores it.
                if (_sidebarCol.ActualWidth > 24)
                {
                    if (_sidebarShowingOutlines)
                        _savedOutlinesWidth = Math.Min(_sidebarCol.ActualWidth, SidebarMaxOutlines);
                    else
                        _savedPagesWidth = Math.Min(_sidebarCol.ActualWidth, SidebarMaxPages);
                }
                _sidebarBorder.Visibility = Visibility.Collapsed;
                _sidebarCol.Width = new GridLength(24);
                _sidebarCol.MinWidth = 24;
                _sidebarToggleBtn.Content = "\uE76C"; // ChevronRight (Segoe MDL2)
                _sidebarToggleBtn.ToolTip = Loc("Str_TT_ExpandSidebar");
            }
            else
            {
                _sidebarBorder.Visibility = Visibility.Visible;
                double restore = _sidebarShowingOutlines ? _savedOutlinesWidth : _savedPagesWidth;
                _sidebarCol.Width = new GridLength(restore);
                _sidebarCol.MinWidth = 24;
                _sidebarToggleBtn.Content = "\uE76B"; // ChevronLeft (Segoe MDL2)
                _sidebarToggleBtn.ToolTip = Loc("Str_TT_CollapseSidebar");
            }
            if (PageList.SelectedIndex >= 0)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () => RefreshPageView(PageList.SelectedIndex));
        }

        // ============================================================
        // Sidebar outline/bookmark panel
        // ============================================================

        private void SidebarPagesTab_Click(object sender, RoutedEventArgs e) => SwitchSidebarToPagesTab();
        private void SidebarOutlinesTab_Click(object sender, RoutedEventArgs e) => SwitchSidebarToOutlinesTab();

        private const double SidebarMaxPages   = 260;
        private const double SidebarMaxOutlines = 480;

        private void SwitchSidebarToPagesTab()
        {
            _sidebarShowingOutlines = false;
            PageList.Visibility = Visibility.Visible;
            OutlineScrollViewer.Visibility = Visibility.Collapsed;
            PageControlsRow.Visibility = Visibility.Visible;
            SidebarPagesTab.Foreground = (Brush)FindResource("Accent");
            SidebarOutlinesTab.Foreground = (Brush)FindResource("TextSecondary");
            // Save current outlines width before snapping back to pages.
            if (!_sidebarCollapsed && _sidebarCol.ActualWidth > 0)
                _savedOutlinesWidth = Math.Min(_sidebarCol.ActualWidth, SidebarMaxOutlines);

            SidebarSplitter.IsEnabled = false;
            _sidebarCol.MaxWidth = SidebarMaxPages;
            if (!_sidebarCollapsed)
            {
                double target = _savedPagesWidth;
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Render,
                    (Action)(() => _sidebarCol.Width = new GridLength(target)));
            }
        }

        private void SwitchSidebarToOutlinesTab()
        {
            // Save current pages width, then restore (or auto-fit) the outlines width.
            if (!_sidebarCollapsed && _sidebarCol.ActualWidth > 0)
                _savedPagesWidth = Math.Min(_sidebarCol.ActualWidth, SidebarMaxPages);

            _sidebarShowingOutlines = true;
            PageList.Visibility = Visibility.Collapsed;
            OutlineScrollViewer.Visibility = Visibility.Visible;
            PageControlsRow.Visibility = Visibility.Collapsed;
            SidebarPagesTab.Foreground = (Brush)FindResource("TextSecondary");
            SidebarOutlinesTab.Foreground = (Brush)FindResource("Accent");
            SidebarSplitter.IsEnabled = true;
            _sidebarCol.MaxWidth = SidebarMaxOutlines;
            if (!_sidebarCollapsed)
            {
                if (!_outlinesFitted)
                    Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Render,
                        (Action)AutoFitOutlineWidth);
                else
                {
                    double target = _savedOutlinesWidth;
                    Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Render,
                        (Action)(() => _sidebarCol.Width = new GridLength(target)));
                }
            }
        }

        /// <summary>
        /// Sizes the sidebar to fit the widest outline item by measuring each item's
        /// text width via FormattedText plus its indentation depth.
        /// </summary>
        private void AutoFitOutlineWidth()
        {
            if (_sidebarCollapsed) return;

            var typeface = new Typeface(
                OutlineTree.FontFamily, OutlineTree.FontStyle,
                OutlineTree.FontWeight, OutlineTree.FontStretch);
            double em  = OutlineTree.FontSize;
            double max = 0;

            void Walk(ItemCollection items, int depth)
            {
                foreach (TreeViewItem node in items)
                {
                    var ft = new System.Windows.Media.FormattedText(
                        node.Header?.ToString() ?? string.Empty,
                        System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight, typeface, em, Brushes.White,
                        /*pixelsPerDip*/ 1.0);
                    // 19 px indent per level + 19 px toggle + text + 12 px item padding
                    double w = depth * 19.0 + 19.0 + ft.Width + 12.0;
                    if (w > max) max = w;
                    if (node.Items.Count > 0)
                        Walk(node.Items, depth + 1);
                }
            }

            Walk(OutlineTree.Items, 0);

            // TreeView outer padding (8 px) + sidebar margins + scrollbar gutter (~36 px)
            double target = Math.Max(160.0, Math.Min(max + 44.0, SidebarMaxOutlines));
            _savedOutlinesWidth = target;
            _outlinesFitted     = true;
            _sidebarCol.Width   = new GridLength(target);
        }

        private void OutlineTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is TreeViewItem item && item.Tag is int pageIdx && pageIdx >= 0 && _doc is not null)
            {
                if (pageIdx < _doc.PageCount)
                    PageList.SelectedIndex = pageIdx;
            }
        }

        private void LoadOutlines()
        {
            _outlinesFitted = false;   // triggers auto-fit on next tab switch
            OutlineTree.Items.Clear();
            try
            {
                var outlines = _doc?.Outlines;
                if (outlines is null || outlines.Count == 0)
                {
                    SidebarOutlinesTab.IsEnabled = false;
                    return;
                }
                SidebarOutlinesTab.IsEnabled = true;
                AddOutlineItems(OutlineTree.Items, outlines);
            }
            catch
            {
                // Malformed outline — show a placeholder and don't crash
                SidebarOutlinesTab.IsEnabled = false;
            }
        }

        private void AddOutlineItems(ItemCollection target, PdfSharpCore.Pdf.PdfOutlineCollection outlines)
        {
            foreach (PdfSharpCore.Pdf.PdfOutline outline in outlines)
            {
                int pageIdx = GetOutlinePageIndex(outline);
                var item = new TreeViewItem
                {
                    Header = string.IsNullOrEmpty(outline.Title) ? "(untitled)" : outline.Title,
                    IsExpanded = true,
                    Tag = pageIdx,
                    ToolTip = pageIdx >= 0 ? $"Page {pageIdx + 1}" : null,
                    Style = (Style)FindResource("OutlineItemStyle")
                };
                if (outline.Outlines is not null && outline.Outlines.Count > 0)
                    AddOutlineItems(item.Items, outline.Outlines);
                target.Add(item);
            }
        }

        private int GetOutlinePageIndex(PdfSharpCore.Pdf.PdfOutline outline)
        {
            if (outline.DestinationPage is PdfSharpCore.Pdf.PdfPage destPage)
            {
                for (int i = 0; i < _doc!.PageCount; i++)
                    if (ReferenceEquals(_doc.Pages[i], destPage)) return i;
            }
            return -1;
        }

        private void ToolSelect_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Select);
        private void ToolText_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Text);
        private void ToolHighlight_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Highlight);
        private void ToolDraw_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Draw);
        private void ToolImage_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Image);
        private void ToolCrop_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Crop);
        private void ToolSignature_Click(object sender, RoutedEventArgs e)
        {
            if (_signaturePopup is not null)
            {
                HideSignaturePopup();
                if (_currentTool == EditTool.Signature && _pendingSignature is null)
                    SetTool(EditTool.Select);
                return;
            }
            SetTool(EditTool.Signature);
            ShowSignaturePopup();
        }

        // ============================================================
        // Crop tool
        // ============================================================

        // ── Crop coordinate helpers ──────────────────────────────────────────
        //
        // The rendered canvas already incorporates the user-applied rotation stored
        // in _pageRotations.  These helpers invert / apply the same transforms that
        // the link-overlay code uses (lines ~1925-1957), so canvas↔PDF coords are
        // consistent with how Docnet drew the bitmap.
        //
        //  rot=0:   canvas_x = native_x * cW/pW,  canvas_y = (pH - native_y) * cH/pH
        //  rot=90:  canvas_x = native_y * cW/pH,  canvas_y = native_x * cH/pW
        //  rot=180: canvas_x = (pW - nx) * cW/pW, canvas_y = (pH - ny) * cH/pH
        //  rot=270: canvas_x = (pH - ny) * cW/pH, canvas_y = (pW - nx) * cH/pW
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Convert a canvas-space <see cref="Rect"/> to PDF CropBox coordinates
        /// (bottom-left origin, points) with rotation awareness.
        /// </summary>
        private static (double x1, double y1, double x2, double y2) CanvasToPdfRect(
            Rect cr, double pdfW, double pdfH, double canvasW, double canvasH, int rot)
        {
            double cx = cr.X, cy = cr.Y, cw = cr.Width, ch = cr.Height;
            return rot switch
            {
                90  => (cy      * pdfW / canvasH,
                        cx      * pdfH / canvasW,
                       (cy + ch) * pdfW / canvasH,
                       (cx + cw) * pdfH / canvasW),

                180 => (pdfW - (cx + cw) * pdfW / canvasW,
                        pdfH - (cy + ch) * pdfH / canvasH,
                        pdfW -  cx       * pdfW / canvasW,
                        pdfH -  cy       * pdfH / canvasH),

                270 => (pdfW - (cy + ch) * pdfW / canvasH,
                        pdfH - (cx + cw) * pdfH / canvasW,
                        pdfW -  cy       * pdfW / canvasH,
                        pdfH -  cx       * pdfH / canvasW),

                _   => (cx       * pdfW / canvasW,           // 0°
                        pdfH - (cy + ch) * pdfH / canvasH,
                       (cx + cw) * pdfW / canvasW,
                        pdfH -  cy       * pdfH / canvasH),
            };
        }

        /// <summary>
        /// Inverse of <see cref="CanvasToPdfRect"/> — map PDF CropBox coords back to a canvas-space
        /// <see cref="Rect"/>.
        /// </summary>
        private static Rect PdfToCanvasRect(
            double x1, double y1, double x2, double y2,
            double pdfW, double pdfH, double canvasW, double canvasH, int rot)
        {
            double cx, cy, cw, ch;
            switch (rot)
            {
                case 90:
                    cx = y1 * canvasW / pdfH;
                    cy = x1 * canvasH / pdfW;
                    cw = (y2 - y1) * canvasW / pdfH;
                    ch = (x2 - x1) * canvasH / pdfW;
                    break;
                case 180:
                    cx = (pdfW - x2) * canvasW / pdfW;
                    cy = (pdfH - y2) * canvasH / pdfH;
                    cw = (x2 - x1)   * canvasW / pdfW;
                    ch = (y2 - y1)   * canvasH / pdfH;
                    break;
                case 270:
                    cx = (pdfH - y2) * canvasW / pdfH;
                    cy = (pdfW - x2) * canvasH / pdfW;
                    cw = (y2 - y1)   * canvasW / pdfH;
                    ch = (x2 - x1)   * canvasH / pdfW;
                    break;
                default: // 0°
                    cx = x1 * canvasW / pdfW;
                    cy = (pdfH - y2) * canvasH / pdfH;
                    cw = (x2 - x1)  * canvasW / pdfW;
                    ch = (y2 - y1)  * canvasH / pdfH;
                    break;
            }
            return new Rect(Math.Max(0, cx), Math.Max(0, cy),
                            Math.Max(10, cw), Math.Max(10, ch));
        }

        /// <summary>
        /// Push current <see cref="_cropCanvasRect"/> → PDF coords into the x1/y1/x2/y2 TextBoxes.
        /// No-ops when the TextBoxes haven't been created yet (confirm bar not showing).
        /// </summary>
        private void SyncCropBoxInputs()
        {
            if (_cropX1Box is null || _doc is null) return;
            int pi = PageList.SelectedIndex;
            if (pi < 0 || !_renderDims.TryGetValue(pi, out var dims)) return;
            _pageRotations.TryGetValue(pi, out int rot);
            var page = _doc.Pages[pi];
            double pdfW = page.Width.Point, pdfH = page.Height.Point;

            var (x1, y1, x2, y2) = CanvasToPdfRect(_cropCanvasRect, pdfW, pdfH, dims.w, dims.h, rot);
            x1 = Math.Max(0,    x1); y1 = Math.Max(0,    y1);
            x2 = Math.Min(pdfW, x2); y2 = Math.Min(pdfH, y2);

            _updatingCropInputs = true;
            _cropX1Box.Text  = $"{x1:F1}";
            _cropY1Box!.Text = $"{y1:F1}";
            _cropX2Box!.Text = $"{x2:F1}";
            _cropY2Box!.Text = $"{y2:F1}";
            _updatingCropInputs = false;
        }

        /// <summary>
        /// Read x1/y1/x2/y2 TextBoxes → update <see cref="_cropCanvasRect"/> and visuals.
        /// Called on Enter-key or LostFocus inside each TextBox.
        /// </summary>
        private void CommitCropBoxInput()
        {
            if (_updatingCropInputs || _cropX1Box is null || _doc is null) return;
            int pi = PageList.SelectedIndex;
            if (pi < 0 || !_renderDims.TryGetValue(pi, out var dims)) return;
            if (!double.TryParse(_cropX1Box.Text,  out double x1)) return;
            if (!double.TryParse(_cropY1Box!.Text, out double y1)) return;
            if (!double.TryParse(_cropX2Box!.Text, out double x2)) return;
            if (!double.TryParse(_cropY2Box!.Text, out double y2)) return;

            _pageRotations.TryGetValue(pi, out int rot);
            var page = _doc.Pages[pi];
            double pdfW = page.Width.Point, pdfH = page.Height.Point;

            x1 = Math.Max(0,       Math.Min(pdfW - 1, x1));
            y1 = Math.Max(0,       Math.Min(pdfH - 1, y1));
            x2 = Math.Max(x1 + 1,  Math.Min(pdfW,     x2));
            y2 = Math.Max(y1 + 1,  Math.Min(pdfH,     y2));

            _cropCanvasRect = PdfToCanvasRect(x1, y1, x2, y2, pdfW, pdfH, dims.w, dims.h, rot);
            UpdateCropRectVisuals();
        }

        /// <summary>
        /// Parse a page-range string like "1-3,5,7-9" (1-based) into a zero-based index array.
        /// Returns <c>null</c> on parse error or if no valid pages are produced.
        /// </summary>
        private static int[]? ParsePageRange(string input, int pageCount)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var result = new System.Collections.Generic.HashSet<int>();
            foreach (var part in input.Split([','], StringSplitOptions.RemoveEmptyEntries))
            {
                var seg = part.Trim();
                if (seg.Contains('-'))
                {
                    var halves = seg.Split('-');
                    if (halves.Length == 2 &&
                        int.TryParse(halves[0].Trim(), out int lo) &&
                        int.TryParse(halves[1].Trim(), out int hi))
                    {
                        for (int p = lo; p <= hi; p++)
                            if (p >= 1 && p <= pageCount) result.Add(p - 1);
                    }
                    else return null;
                }
                else if (int.TryParse(seg, out int pg))
                {
                    if (pg >= 1 && pg <= pageCount) result.Add(pg - 1);
                }
                else return null;
            }
            return result.Count == 0 ? null : [.. result.OrderBy(x => x)];
        }

        // ─────────────────────────────────────────────────────────────────────

        private void ShowCropConfirmBar()
        {
            // Save the preview rect — HideCropConfirmBar removes it, but we need it to persist.
            var savedRect = _cropPreviewRect;
            _cropPreviewRect = null;
            HideCropConfirmBar();
            _cropPreviewRect = savedRect;
            // Remove fill once confirmed — keep only the outline.
            if (_cropPreviewRect is not null)
                _cropPreviewRect.Fill = Brushes.Transparent;
            if (_doc is null) return;

            int currentPage = _cropPageIndex >= 0 ? _cropPageIndex : PageList.SelectedIndex;
            bool multiPage  = _doc.PageCount > 1;

            var bar = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(8, 6, 8, 6)
            };
            bar.SetResourceReference(Border.BackgroundProperty,  "BgModal");
            bar.SetResourceReference(Border.BorderBrushProperty, "Accent");

            var outer = new StackPanel { Orientation = Orientation.Vertical };

            // ── Row 1: CropBox coordinate inputs ────────────────────────────────
            var inputRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 0, 0, 5)
            };

            var headerLbl = new TextBlock
            {
                Text              = "CropBox (pts):",
                FontFamily        = new FontFamily("Segoe UI"),
                FontSize          = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 6, 0)
            };
            headerLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            inputRow.Children.Add(headerLbl);

            // Helper: adds a label + TextBox pair to inputRow and returns the TextBox
            TextBox AddCoordField(string lbl)
            {
                var fieldLbl = new TextBlock
                {
                    Text              = lbl,
                    FontFamily        = new FontFamily("Segoe UI"),
                    FontSize          = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(0, 0, 2, 0)
                };
                fieldLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                inputRow.Children.Add(fieldLbl);
                var tb = new TextBox
                {
                    Width             = 52,
                    Height            = 22,
                    FontFamily        = new FontFamily("Segoe UI"),
                    FontSize          = 11,
                    BorderThickness   = new Thickness(1),
                    Padding           = new Thickness(3, 1, 3, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(0, 0, 6, 0)
                };
                tb.SetResourceReference(TextBox.BackgroundProperty,  "BgPanel");
                tb.SetResourceReference(TextBox.ForegroundProperty,  "TextPrimary");
                tb.SetResourceReference(TextBox.BorderBrushProperty, "BorderDim");
                tb.KeyDown   += (_, e) => { if (e.Key == Key.Enter) { CommitCropBoxInput(); e.Handled = true; } };
                tb.LostFocus += (_, _) => CommitCropBoxInput();
                inputRow.Children.Add(tb);
                return tb;
            }

            _cropX1Box = AddCoordField("x1:");
            _cropY1Box = AddCoordField("y1:");
            _cropX2Box = AddCoordField("x2:");
            _cropY2Box = AddCoordField("y2:");

            outer.Children.Add(inputRow);

            // ── Row 2: Apply / remove / cancel buttons ───────────────────────────
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };

            // Base button style - only non-theme values; theme colors set via
            // SetResourceReference on each instance so they update on theme switches.
            var btnStyle = new Style(typeof(Button));
            btnStyle.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(1)));
            btnStyle.Setters.Add(new Setter(Button.PaddingProperty,   new Thickness(8, 3, 8, 3)));
            btnStyle.Setters.Add(new Setter(Button.MarginProperty,    new Thickness(0, 0, 5, 0)));
            btnStyle.Setters.Add(new Setter(Button.CursorProperty,    Cursors.Hand));
            btnStyle.Setters.Add(new Setter(Button.FontFamilyProperty, new FontFamily("Segoe UI")));
            btnStyle.Setters.Add(new Setter(Button.FontSizeProperty,   12.0));

            // Wire up DynamicResource-equivalent bindings on a crop button.
            void StyleAccentBtn(Button b)
            {
                b.SetResourceReference(Button.BackgroundProperty,  "AccentDim");
                b.SetResourceReference(Button.ForegroundProperty,  "Accent");
                b.SetResourceReference(Button.BorderBrushProperty, "Accent");
            }

            // "This Page" apply
            var thisPageBtn = new Button { Content = "This Page", Style = btnStyle,
                ToolTip = "Crop this page (Enter)" };
            StyleAccentBtn(thisPageBtn);
            thisPageBtn.Click += (_, _) => ApplyCrop([currentPage]);
            btnRow.Children.Add(thisPageBtn);

            // Range input + "Range" apply button
            _cropRangeBox = new TextBox
            {
                Width             = 68,
                Height            = 22,
                FontFamily        = new FontFamily("Segoe UI"),
                FontSize          = 11,
                BorderThickness   = new Thickness(1),
                Padding           = new Thickness(3, 1, 3, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 3, 0),
                ToolTip           = "Page range, e.g. 1-3,5"
            };
            _cropRangeBox.SetResourceReference(TextBox.BackgroundProperty,  "BgPanel");
            _cropRangeBox.SetResourceReference(TextBox.ForegroundProperty,  "TextPrimary");
            _cropRangeBox.SetResourceReference(TextBox.BorderBrushProperty, "BorderDim");
            var rangeApplyBtn = new Button { Content = "Range", Style = btnStyle,
                ToolTip = "Crop pages in the specified range" };
            StyleAccentBtn(rangeApplyBtn);
            rangeApplyBtn.Click += (_, _) =>
            {
                int pc = _doc?.PageCount ?? 0;
                var pages = ParsePageRange(_cropRangeBox?.Text ?? "", pc);
                if (pages is null) { SetStatus(Loc("Str_InvalidRange")); return; }
                ApplyCrop(pages);
            };
            btnRow.Children.Add(_cropRangeBox);
            btnRow.Children.Add(rangeApplyBtn);

            if (multiPage)
            {
                var allPagesBtn = new Button { Content = "All Pages", Style = btnStyle };
                StyleAccentBtn(allPagesBtn);
                allPagesBtn.Click += (_, _) => ApplyCrop([.. Enumerable.Range(0, _doc!.PageCount)]);
                btnRow.Children.Add(allPagesBtn);
            }

            // Visual divider before destructive buttons
            var divider = new Border
            {
                Width             = 1,
                Margin            = new Thickness(2, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Stretch
            };
            divider.SetResourceReference(Border.BackgroundProperty, "BorderDim");
            btnRow.Children.Add(divider);

            // Remove Crop — only shown if current page already has a CropBox
            bool hasCropBox = _doc.Pages[currentPage].Elements.ContainsKey("/CropBox");
            if (hasCropBox)
            {
                var removeBtn = new Button { Content = "Remove Crop", Style = btnStyle,
                    ToolTip = "Remove CropBox (and TrimBox) from this page" };
                removeBtn.SetResourceReference(Button.BackgroundProperty,  "AccentDim");
                removeBtn.SetResourceReference(Button.ForegroundProperty,  "DangerRed");
                removeBtn.SetResourceReference(Button.BorderBrushProperty, "DangerRed");
                removeBtn.Click += (_, _) => RemoveCropBox([currentPage]);
                btnRow.Children.Add(removeBtn);

                if (multiPage)
                {
                    var removeAllBtn = new Button { Content = "Remove All", Style = btnStyle,
                        ToolTip = "Remove CropBox from all pages" };
                    removeAllBtn.SetResourceReference(Button.BackgroundProperty,  "AccentDim");
                    removeAllBtn.SetResourceReference(Button.ForegroundProperty,  "DangerRed");
                    removeAllBtn.SetResourceReference(Button.BorderBrushProperty, "DangerRed");
                    removeAllBtn.Click += (_, _) => RemoveCropBox([.. Enumerable.Range(0, _doc!.PageCount)]);
                    btnRow.Children.Add(removeAllBtn);
                }
            }

            var cancelBtn = new Button { Content = "Cancel", Style = btnStyle, ToolTip = "Cancel (Escape)", Background = Brushes.Transparent };
            cancelBtn.SetResourceReference(Button.ForegroundProperty,  "TextSecondary");
            cancelBtn.SetResourceReference(Button.BorderBrushProperty, "TextSecondary");
            cancelBtn.Click += (_, _) => HideCropConfirmBar();
            btnRow.Children.Add(cancelBtn);

            outer.Children.Add(btnRow);
            bar.Child = outer;
            bar.HorizontalAlignment = HorizontalAlignment.Left;
            bar.VerticalAlignment   = VerticalAlignment.Top;
            bar.Cursor              = Cursors.SizeAll;
            // *** Place bar in the OUTER (unscaled) grid so it renders at native
            // screen size regardless of the current zoom level. ***
            Panel.SetZIndex(bar, 100);

            // ── Drag-to-move ─────────────────────────────────────────────────────
            bar.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is Button || e.OriginalSource is TextBox) return;
                _cropBarDragging   = true;
                _cropBarDragOffset = e.GetPosition(PagePreviewPanel.Parent as UIElement);
                _cropBarDragOffset = new Point(
                    _cropBarDragOffset.X - bar.Margin.Left,
                    _cropBarDragOffset.Y - bar.Margin.Top);
                bar.CaptureMouse();
                e.Handled = true;
            };
            bar.MouseMove += (s, e) =>
            {
                if (!_cropBarDragging) return;
                var pos = e.GetPosition(PagePreviewPanel.Parent as UIElement);
                bar.Margin = new Thickness(
                    Math.Max(0, pos.X - _cropBarDragOffset.X),
                    Math.Max(0, pos.Y - _cropBarDragOffset.Y), 0, 0);
                e.Handled = true;
            };
            bar.MouseLeftButtonUp += (s, e) =>
            {
                if (!_cropBarDragging) return;
                _cropBarDragging = false;
                bar.ReleaseMouseCapture();
                e.Handled = true;
            };
            // ─────────────────────────────────────────────────────────────────────

            _cropConfirmBar = bar;

            var outerGrid = PagePreviewPanel.Parent as Panel ?? (Panel)_annotationCanvas;
            outerGrid.Children.Add(bar);
            AddCropHandles();
            RepositionCropConfirmBar();
            SyncCropBoxInputs();
        }

        private void HideCropConfirmBar()
        {
            if (_cropConfirmBar is not null)
            {
                // Remove from whichever panel it was added to (outer grid or canvas fallback)
                (_annotationCanvas.Parent as Panel)?.Children.Remove(_cropConfirmBar);
                _annotationCanvas.Children.Remove(_cropConfirmBar);   // no-op if not there
                (PagePreviewPanel.Parent as Panel)?.Children.Remove(_cropConfirmBar);
                _cropConfirmBar = null;
            }
            if (_cropPreviewRectBorder is not null)
            {
                (_cropPreviewRectBorder.Parent as Panel)?.Children.Remove(_cropPreviewRectBorder);
                _annotationCanvas.Children.Remove(_cropPreviewRectBorder);
                _cropPreviewRectBorder = null;
            }
            if (_cropPreviewRect is not null)
            {
                (_cropPreviewRect.Parent as Panel)?.Children.Remove(_cropPreviewRect);
                _annotationCanvas.Children.Remove(_cropPreviewRect);
                _cropPreviewRect = null;
            }
            RemoveCropHandles();
            _cropX1Box = _cropY1Box = _cropX2Box = _cropY2Box = null;
            _cropRangeBox = null;
        }

        private void AddCropHandles()
        {
            RemoveCropHandles();
            const double hSize = 24;
            var tags    = new[] { "NW", "NE", "SE", "SW" };
            var cursors = new[] { Cursors.SizeNWSE, Cursors.SizeNESW, Cursors.SizeNWSE, Cursors.SizeNESW };
            // Handles live in the OUTER unscaled panel (same as the confirm bar) so they render
            // at a fixed screen size regardless of canvas zoom level.
            var outerGrid = PagePreviewPanel.Parent as Panel ?? (Panel)_annotationCanvas;

            for (int i = 0; i < 4; i++)
            {
                var tag = tags[i];
                var h = new Rectangle
                {
                    Width  = hSize, Height = hSize,
                    Fill   = Brushes.Transparent,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    StrokeThickness = 1.5,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                        { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 3, Opacity = 0.6 },
                    Tag    = tag,
                    Cursor = cursors[i],
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment   = VerticalAlignment.Top,
                };
                Panel.SetZIndex(h, 101);
                // Attach drag directly on the handle so clicks don't need to reach _annotationCanvas.
                h.MouseLeftButtonDown += (_, e) =>
                {
                    _activeCropHandleTag  = tag;
                    // Measure and capture against the active surface (per-page overlay in
                    // Continuous view) so the drag delta matches the crop rect's coordinate space.
                    _cropHandleDragStart  = e.GetPosition(_activeCanvas);
                    _cropRectAtHandleDrag = _cropCanvasRect;
                    _activeCanvas.CaptureMouse();
                    e.Handled = true;
                };
                _cropHandles.Add(h);
                outerGrid.Children.Add(h);
            }
            PositionCropHandles();
        }

        private void RemoveCropHandles()
        {
            var outerGrid = PagePreviewPanel.Parent as Panel ?? (Panel)_annotationCanvas;
            foreach (var h in _cropHandles)
            {
                outerGrid.Children.Remove(h);
                _annotationCanvas.Children.Remove(h); // belt-and-suspenders in case it ended up in canvas
            }
            _cropHandles.Clear();
            _activeCropHandleTag = null;
            RemoveCropBrackets();   // no-op — list is always empty now, kept for safety
        }

        private void RemoveCropBrackets()
        {
            foreach (var b in _cropBrackets) _annotationCanvas.Children.Remove(b);
            _cropBrackets.Clear();
        }

        private void PositionCropHandles()
        {
            if (_cropHandles.Count < 4) return;
            const double hSize = 24;
            var outerGrid = PagePreviewPanel.Parent as UIElement ?? _annotationCanvas;
            // Translate canvas-space corners to outer-panel screen space (same as RepositionCropConfirmBar).
            var canvasCorners = new Point[]
            {
                new(_cropCanvasRect.X,      _cropCanvasRect.Y),
                new(_cropCanvasRect.Right,   _cropCanvasRect.Y),
                new(_cropCanvasRect.Right,   _cropCanvasRect.Bottom),
                new(_cropCanvasRect.X,       _cropCanvasRect.Bottom),
            };
            var offsets = new (double dx, double dy)[]
            {
                (0,       0      ),   // NW: top-left at top-left corner
                (-hSize,  0      ),   // NE: top-right at top-right corner
                (-hSize, -hSize  ),   // SE: bottom-right at bottom-right corner
                (0,      -hSize  ),   // SW: bottom-left at bottom-left corner
            };
            for (int i = 0; i < 4; i++)
            {
                Point screen = _activeCanvas.TranslatePoint(canvasCorners[i], outerGrid);
                _cropHandles[i].Margin = new Thickness(
                    screen.X + offsets[i].dx,
                    screen.Y + offsets[i].dy,
                    0, 0);
            }
        }

        private void UpdateCropRectVisuals()
        {
            if (_cropPreviewRect is null) return;
            var r = _cropCanvasRect;
            Canvas.SetLeft(_cropPreviewRect, r.X); Canvas.SetTop(_cropPreviewRect, r.Y);
            _cropPreviewRect.Width = r.Width;       _cropPreviewRect.Height = r.Height;
            PositionCropHandles();
            RepositionCropConfirmBar();
            SyncCropBoxInputs();
        }

        private void RepositionCropConfirmBar()
        {
            if (_cropConfirmBar is null) return;

            // The confirm bar lives in the OUTER (unscaled) panel, so we must
            // translate canvas-space coordinates to that panel's coordinate space.
            var outerGrid = PagePreviewPanel.Parent as UIElement ?? _annotationCanvas;
            Point topLeft     = _activeCanvas.TranslatePoint(
                new Point(_cropCanvasRect.X, _cropCanvasRect.Y), outerGrid);
            Point bottomLeft  = _activeCanvas.TranslatePoint(
                new Point(_cropCanvasRect.X, _cropCanvasRect.Bottom), outerGrid);

            const double barHeight = 78;
            double barLeft     = Math.Max(4, topLeft.X);
            double barTopBelow = bottomLeft.Y + 8;
            double barTopAbove = topLeft.Y - barHeight - 8;

            double parentH = (outerGrid as FrameworkElement)?.ActualHeight
                             ?? _annotationCanvas.ActualHeight;
            double barTop = barTopBelow + barHeight < parentH
                ? barTopBelow : Math.Max(4, barTopAbove);

            _cropConfirmBar.Margin = new Thickness(barLeft, barTop, 0, 0);
        }

        private void ApplyCrop(int[] pageIndices)
        {
            if (_doc is null || _currentFile is null) { SetStatus(Loc("Str_CropNoDoc")); return; }
            int currentPage = _cropPageIndex >= 0 ? _cropPageIndex : PageList.SelectedIndex;
            if (currentPage < 0) { SetStatus(Loc("Str_CropNoPage")); return; }
            if (!_renderDims.TryGetValue(currentPage, out var refDims))
            { SetStatus(Loc("Str_CropNoDims")); return; }

            try
            {
                PushDocUndo();

                // Convert canvas rect to PDF CropBox coords using the rotation-aware helper.
                // This is the correct inversion of how Docnet renders the rotated bitmap.
                _pageRotations.TryGetValue(currentPage, out int rot);
                var refPage = _doc.Pages[currentPage];
                double refPdfW = refPage.Width.Point;
                double refPdfH = refPage.Height.Point;

                var (rx1, ry1, rx2, ry2) = CanvasToPdfRect(
                    _cropCanvasRect, refPdfW, refPdfH, refDims.w, refDims.h, rot);

                foreach (int pi in pageIndices)
                {
                    if (pi < 0 || pi >= _doc.PageCount) continue;
                    var page  = _doc.Pages[pi];
                    double pW = page.Width.Point;
                    double pH = page.Height.Point;

                    // Scale proportionally when "All Pages" spans pages of different sizes
                    double x1 = rx1 * pW / refPdfW;
                    double y1 = ry1 * pH / refPdfH;
                    double x2 = rx2 * pW / refPdfW;
                    double y2 = ry2 * pH / refPdfH;

                    // Clamp to media box and ensure minimum 1-pt size
                    x1 = Math.Max(0, x1);  y1 = Math.Max(0, y1);
                    x2 = Math.Min(pW, x2); y2 = Math.Min(pH, y2);
                    if (x2 - x1 < 1) x2 = x1 + 1;
                    if (y2 - y1 < 1) y2 = y1 + 1;

                    // Write CropBox directly into the page dictionary (more reliable across
                    // PdfSharpCore versions than the CropBox property setter).
                    var cropArr = new PdfSharpCore.Pdf.PdfArray();
                    cropArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(x1));
                    cropArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(y1));
                    cropArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(x2));
                    cropArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(y2));
                    page.Elements["/CropBox"] = cropArr;

                    // Mirror to TrimBox (PDF spec: TrimBox ⊆ CropBox ⊆ MediaBox)
                    var trimArr = new PdfSharpCore.Pdf.PdfArray();
                    trimArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(x1));
                    trimArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(y1));
                    trimArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(x2));
                    trimArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(y2));
                    page.Elements["/TrimBox"] = trimArr;
                }

                HideCropConfirmBar();
                SetTool(EditTool.Select);
                SaveTempAndReload(keepAnnotations: true);
                SetStatus(string.Format(Loc("Str_Cropped"), pageIndices.Length));
            }
            catch (Exception ex)
            {
                SetStatus(string.Format(Loc("Str_CropFailed"), ex.Message));
            }
        }

        private void RemoveCropBox(int[] pageIndices)
        {
            if (_doc is null || _currentFile is null) return;
            try
            {
                PushDocUndo();
                foreach (int pi in pageIndices)
                {
                    if (pi < 0 || pi >= _doc.PageCount) continue;
                    _doc.Pages[pi].Elements.Remove("/CropBox");
                    _doc.Pages[pi].Elements.Remove("/TrimBox");
                }
                HideCropConfirmBar();
                SetTool(EditTool.Select);
                SaveTempAndReload(keepAnnotations: true);
                SetStatus(string.Format(Loc("Str_RemovedCrop"), pageIndices.Length));
            }
            catch (Exception ex)
            {
                SetStatus(string.Format(Loc("Str_RemoveCropFailed"), ex.Message));
            }
        }

        // ============================================================
        // Draw/Highlight settings bar
        // ============================================================

        private static readonly Color[] SwatchColors =
        [
            Colors.Red, Colors.SaddleBrown, Colors.Orange, Colors.Gold,
            Colors.LimeGreen, Colors.DodgerBlue, Colors.MediumPurple,
            Colors.DeepPink, Colors.White, Colors.Black
        ];

        // Frozen cached brushes for hot-path UI construction
        private static readonly SolidColorBrush _swatchDimBorder   = Freeze(new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)));
        private static readonly SolidColorBrush _drawBarBackground  = Freeze(new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)));
        private static readonly SolidColorBrush _thumbBorderBrush   = Freeze(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)));

        private static T Freeze<T>(T freezable) where T : System.Windows.Freezable
        {
            freezable.Freeze();
            return freezable;
        }

        private void ShowDrawSettings(EditTool tool)
        {
            if (_drawSettingsBar is not null)
            {
                var previewGrid = PagePreviewPanel.Parent as Grid;
                previewGrid?.Children.Remove(_drawSettingsBar);
                _drawSettingsBar = null;
            }

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };

            // Color label
            var colorLbl = new TextBlock
            {
                Text = "Color:",
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            };
            colorLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            panel.Children.Add(colorLbl);

            // Color swatches
            var activeColor = tool == EditTool.Draw ? _drawColor : Color.FromRgb(_highlightColor.R, _highlightColor.G, _highlightColor.B);
            foreach (var color in SwatchColors)
            {
                bool isActive = color == activeColor;
                var swatch = new Border
                {
                    Width = 18, Height = 18,
                    Background = Freeze(new SolidColorBrush(color)),
                    BorderThickness = new Thickness(isActive ? 2 : 1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Tag = color
                };
                if (isActive)
                    swatch.SetResourceReference(Border.BorderBrushProperty, "Accent");
                else
                    swatch.BorderBrush = _swatchDimBorder;
                swatch.MouseLeftButtonDown += (s, e) =>
                {
                    var c = (Color)((Border)s!).Tag;
                    if (tool == EditTool.Draw)
                        _drawColor = Color.FromArgb(_drawOpacity, c.R, c.G, c.B);
                    else
                        _highlightColor = Color.FromArgb(_highlightColor.A, c.R, c.G, c.B);
                    ShowDrawSettings(tool); // refresh selection
                };
                panel.Children.Add(swatch);
            }

            // Separator
            var sep1 = new Rectangle { Width = 1, Margin = new Thickness(8, 2, 8, 2) };
            sep1.SetResourceReference(Rectangle.FillProperty, "BorderDim");
            panel.Children.Add(sep1);

            // Size slider (draw only)
            if (tool == EditTool.Draw)
            {
                var sizeLbl = new TextBlock
                {
                    Text = "Size:",
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                };
                sizeLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                panel.Children.Add(sizeLbl);

                var sizeSlider = new Slider
                {
                    Minimum = 1, Maximum = 20, Value = _drawWidth,
                    Width = 80, VerticalAlignment = VerticalAlignment.Center,
                    TickFrequency = 1, IsSnapToTickEnabled = true
                };
                sizeSlider.ValueChanged += (s, e) => _drawWidth = e.NewValue;
                panel.Children.Add(sizeSlider);

                var sizeLabel = new TextBlock
                {
                    Text = $"{_drawWidth:F0}px",
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0)
                };
                sizeLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                sizeSlider.ValueChanged += (s, e) => sizeLabel.Text = $"{e.NewValue:F0}px";
                panel.Children.Add(sizeLabel);

                var sep2 = new Rectangle { Width = 1, Margin = new Thickness(8, 2, 8, 2) };
                sep2.SetResourceReference(Rectangle.FillProperty, "BorderDim");
                panel.Children.Add(sep2);
            }

            // Opacity slider
            var opacityLbl = new TextBlock
            {
                Text = "Opacity:",
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            };
            opacityLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            panel.Children.Add(opacityLbl);

            byte currentOpacity = tool == EditTool.Draw ? _drawOpacity : _highlightColor.A;
            var opacitySlider = new Slider
            {
                Minimum = 10, Maximum = 255, Value = currentOpacity,
                Width = 80, VerticalAlignment = VerticalAlignment.Center
            };
            var opacityLabel = new TextBlock
            {
                Text = $"{(int)(currentOpacity / 255.0 * 100)}%",
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0)
            };
            opacityLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            opacitySlider.ValueChanged += (s, e) =>
            {
                byte a = (byte)e.NewValue;
                opacityLabel.Text = $"{(int)(a / 255.0 * 100)}%";
                if (tool == EditTool.Draw)
                {
                    _drawOpacity = a;
                    _drawColor = Color.FromArgb(a, _drawColor.R, _drawColor.G, _drawColor.B);
                }
                else
                {
                    _highlightColor = Color.FromArgb(a, _highlightColor.R, _highlightColor.G, _highlightColor.B);
                }
            };
            panel.Children.Add(opacitySlider);
            panel.Children.Add(opacityLabel);

            _drawSettingsBar = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Child = panel,
                Margin = new Thickness(0, 0, 8, 0)
            };
            _drawSettingsBar.SetResourceReference(Border.BackgroundProperty,  "BgPanel");
            _drawSettingsBar.SetResourceReference(Border.BorderBrushProperty, "BorderDim");

            var previewArea = PagePreviewPanel.Parent as Grid;
            if (previewArea is not null)
            {
                Panel.SetZIndex(_drawSettingsBar, 100);
                previewArea.Children.Add(_drawSettingsBar);
            }
        }

        private void HideDrawSettings()
        {
            if (_drawSettingsBar is not null)
            {
                var previewGrid = PagePreviewPanel.Parent as Grid;
                previewGrid?.Children.Remove(_drawSettingsBar);
                _drawSettingsBar = null;
            }
        }

        // ============================================================
        // Text tool settings bar
        // ============================================================

        private static readonly double[] TextFontSizes = [8, 10, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 64, 72];

        private void ApplyTextStyleToActiveBox()
        {
            if (_activeTextBox is null || _activeTextBox.Tag is TextEditContext) return;
            _activeTextBox.Foreground = new SolidColorBrush(_textColor);
            int pg = _activeTextBox.Tag is int tp ? tp : PageList.SelectedIndex;
            double fontCanvas = _textFontSize;
            if (_doc is not null && pg >= 0 && _renderDims.TryGetValue(pg, out var rd) && rd.h > 0)
            {
                double sy = _doc.Pages[pg].Height.Point / rd.h;
                if (sy > 0) fontCanvas = _textFontSize / sy;
            }
            _activeTextBox.FontSize = fontCanvas;
        }

        private void ShowTextSettings()
        {
            HideTextSettings();

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };

            // Font size label
            var sizeLbl = new TextBlock
            {
                Text = "Size:",
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            };
            sizeLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            panel.Children.Add(sizeLbl);

            // Font size dropdown
            var sizeBox = new ComboBox
            {
                Width = 64, Height = 24,
                Style = (Style)FindResource("DarkComboBox"),
                IsEditable = true,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            foreach (var size in TextFontSizes)
                sizeBox.Items.Add(size.ToString("0"));
            sizeBox.Text = _textFontSize.ToString("0");
            sizeBox.SelectionChanged += (_, _) =>
            {
                if (sizeBox.SelectedItem is string s && double.TryParse(s, out double v) && v > 0)
                {
                    _textFontSize = v;
                    ApplyTextStyleToActiveBox();
                }
            };
            sizeBox.LostFocus += (_, _) =>
            {
                if (double.TryParse(sizeBox.Text, out double v) && v > 0)
                {
                    _textFontSize = v;
                    ApplyTextStyleToActiveBox();
                }
            };
            panel.Children.Add(sizeBox);

            // Separator
            var sep = new Rectangle { Width = 1, Margin = new Thickness(8, 2, 8, 2) };
            sep.SetResourceReference(Rectangle.FillProperty, "BorderDim");
            panel.Children.Add(sep);

            // Color label
            var colorLbl = new TextBlock
            {
                Text = "Color:",
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            };
            colorLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            panel.Children.Add(colorLbl);

            // Color swatches (reuse same palette as draw tool)
            foreach (var color in SwatchColors)
            {
                var c = color;
                bool isActive = c.R == _textColor.R && c.G == _textColor.G && c.B == _textColor.B;
                var swatch = new Border
                {
                    Width = 18, Height = 18,
                    Background = new SolidColorBrush(c),
                    BorderThickness = new Thickness(isActive ? 2 : 1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(1),
                    Cursor = Cursors.Hand
                };
                if (isActive)
                    swatch.SetResourceReference(Border.BorderBrushProperty, "Accent");
                else
                    swatch.BorderBrush = _swatchDimBorder;
                swatch.MouseLeftButtonDown += (_, _) => { _textColor = c; ApplyTextStyleToActiveBox(); ShowTextSettings(); };
                panel.Children.Add(swatch);
            }

            _textSettingsBar = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Child = panel,
                Margin = new Thickness(0, 0, 8, 0)
            };
            _textSettingsBar.SetResourceReference(Border.BackgroundProperty,  "BgPanel");
            _textSettingsBar.SetResourceReference(Border.BorderBrushProperty, "BorderDim");

            var previewArea = PagePreviewPanel.Parent as Grid;
            if (previewArea is not null)
            {
                Panel.SetZIndex(_textSettingsBar, 100);
                previewArea.Children.Add(_textSettingsBar);
            }
        }

        private void HideTextSettings()
        {
            if (_textSettingsBar is not null)
            {
                (PagePreviewPanel.Parent as Grid)?.Children.Remove(_textSettingsBar);
                _textSettingsBar = null;
            }
        }

        // ============================================================
        // Signatures
        // ============================================================

        private void LoadSignatures() => _signatureStore.Load();

        private void PersistSignatures() => _signatureStore.Persist();

        private void ShowSignaturePopup()
        {
            // NOTE: this popup is rebuilt on every open. All event handlers here are lambdas
            // on the popup's own child elements — no external source subscriptions, so no leak.
            // If SignatureStore.Signatures ever becomes ObservableCollection and this popup
            // subscribes to CollectionChanged, use CollectionChangedEventManager instead of +=.
            HideSignaturePopup();

            var stack = new StackPanel { Margin = new Thickness(4) };

            // Title
            stack.Children.Add(new TextBlock
            {
                Text = "Signatures",
                Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(4, 2, 4, 6)
            });

            // Saved signatures
            if (_signatureStore.Signatures.Count > 0)
            {
                var scroll = new ScrollViewer
                {
                    MaxHeight = 260,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                var listPanel = new StackPanel();

                foreach (var sig in _signatureStore.Signatures)
                {
                    var sigCopy = sig; // capture for lambda
                    var item = new Border
                    {
                        Background = Brushes.White,
                        BorderBrush = _swatchDimBorder,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        Margin = new Thickness(4, 2, 4, 2),
                        Padding = new Thickness(4),
                        Cursor = Cursors.Hand,
                        Height = 60,
                        Width = 220
                    };

                    // Render mini signature preview
                    if (sigCopy.ImageData is not null)
                    {
                        try
                        {
                            var imgBytes = Convert.FromBase64String(sigCopy.ImageData);
                            var bmpImg = new System.Windows.Media.Imaging.BitmapImage();
                            bmpImg.BeginInit();
                            bmpImg.StreamSource = new System.IO.MemoryStream(imgBytes);
                            bmpImg.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            bmpImg.EndInit();
                            item.Child = new System.Windows.Controls.Image
                            {
                                Source = bmpImg,
                                Width = 210, Height = 50,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                IsHitTestVisible = false
                            };
                        }
                        catch { item.Child = new TextBlock { Text = "(image)", IsHitTestVisible = false }; }
                    }
                    else
                    {
                        var canvas = new Canvas
                        {
                            Width = 210, Height = 50,
                            Background = Brushes.Transparent,
                            IsHitTestVisible = false
                        };
                        RenderSignaturePreview(canvas, sigCopy, 210, 50);
                        item.Child = canvas;
                    }

                    item.MouseLeftButtonDown += (s, e) =>
                    {
                        _pendingSignature = sigCopy;
                        HideSignaturePopup();
                        _annotationCanvas.Cursor = Cursors.Cross;
                        SetStatus("Click on the page to place your signature");
                    };
                    item.MouseEnter += (s, e) =>
                        ((Border)s!).BorderBrush = (SolidColorBrush)FindResource("Accent");
                    item.MouseLeave += (s, e) =>
                        ((Border)s!).BorderBrush = _swatchDimBorder;

                    // Wrap in grid with delete button
                    var itemGrid = new Grid();
                    itemGrid.Children.Add(item);

                    var delBtn = new Button
                    {
                        Content = "\ue711",
                        FontSize = 10,
                        Width = 18, Height = 18,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 0, 2, 0),
                        Background = new SolidColorBrush(Color.FromArgb(200, 30, 30, 30)),
                        Foreground = (SolidColorBrush)FindResource("DangerRed"),
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        Padding = new Thickness(0),
                        Style = (Style)FindResource("ToolbarButton")
                    };
                    delBtn.Click += (s, e) =>
                    {
                        _signatureStore.Remove(sigCopy);
                        PersistSignatures();
                        ShowSignaturePopup(); // refresh
                    };
                    itemGrid.Children.Add(delBtn);
                    listPanel.Children.Add(itemGrid);
                }
                scroll.Content = listPanel;
                stack.Children.Add(scroll);
            }
            else
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "No saved signatures",
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(4, 4, 4, 8),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }

            // Separator
            stack.Children.Add(new Rectangle
            {
                Height = 1,
                Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(4, 4, 4, 4)
            });

            // Create Signature button
            var createBtn = new Button
            {
                Content = "Create Signature",
                Style = (Style)FindResource("DarkButton"),
                Background = (SolidColorBrush)FindResource("AccentDim"),
                Foreground = (SolidColorBrush)FindResource("Accent"),
                BorderBrush = (SolidColorBrush)FindResource("AccentDim"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            createBtn.Click += (s, e) =>
            {
                HideSignaturePopup();
                OpenSignatureCreator();
            };
            stack.Children.Add(createBtn);

            // Import image button
            var importBtn = new Button
            {
                Content = "Import Image",
                Style = (Style)FindResource("DarkButton"),
                Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x3a, 0x2e)),
                Foreground = (SolidColorBrush)FindResource("Accent"),
                BorderBrush = (SolidColorBrush)FindResource("AccentDim"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(4, 2, 4, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            importBtn.Click += (s, e) =>
            {
                HideSignaturePopup();
                ImportImageSignature();
            };
            stack.Children.Add(importBtn);

            _signaturePopup = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                Child = stack,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 80, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, BlurRadius = 12, Opacity = 0.5, ShadowDepth = 4
                }
            };

            var previewGrid = PagePreviewPanel.Parent as Grid;
            if (previewGrid is not null)
            {
                Panel.SetZIndex(_signaturePopup, 200);
                previewGrid.Children.Add(_signaturePopup);
            }
        }

        private void HideSignaturePopup()
        {
            if (_signaturePopup is not null)
            {
                var previewGrid = PagePreviewPanel.Parent as Grid;
                previewGrid?.Children.Remove(_signaturePopup);
                _signaturePopup = null;
            }
        }

        private void RenderSignaturePreview(Canvas canvas, SavedSignature sig, double targetW, double targetH)
        {
            double scaleX = targetW / sig.CanvasWidth;
            double scaleY = targetH / sig.CanvasHeight;
            double scale = Math.Min(scaleX, scaleY) * 0.9;

            double offsetX = (targetW - sig.CanvasWidth * scale) / 2;
            double offsetY = (targetH - sig.CanvasHeight * scale) / 2;

            foreach (var stroke in sig.Strokes)
            {
                if (stroke.Count < 2) continue;
                var poly = new Polyline
                {
                    Stroke = Brushes.Black,
                    StrokeThickness = 1.5,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                foreach (var pt in stroke)
                    poly.Points.Add(new Point(pt.X * scale + offsetX, pt.Y * scale + offsetY));
                canvas.Children.Add(poly);
            }
        }

        private void OpenSignatureCreator()
        {
            var win = new Window
            {
                Title = "Create Signature",
                Width = 460, Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent
            };

            // Outer chrome
            var outerChrome = new Border
            {
                Background      = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0x22, 0x54, 0x3d)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6)
            };
            var rootStack = new StackPanel();

            // Title bar
            var titleBar = new Border
            {
                Background   = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)),
                Padding      = new Thickness(14, 8, 8, 8),
                CornerRadius = new CornerRadius(5, 5, 0, 0)
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleText = new TextBlock
            {
                Text       = "Create Signature",
                Foreground = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)),
                FontWeight = FontWeights.SemiBold,
                FontSize   = 13,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleText, 0);
            var closeWinBtn = new Button
            {
                Content         = "",
                FontFamily      = new FontFamily("Segoe MDL2 Assets"),
                FontSize        = 10,
                Width           = 28, Height = 28,
                Background      = System.Windows.Media.Brushes.Transparent,
                Foreground      = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                BorderThickness = new Thickness(0),
                Cursor          = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeWinBtn.MouseEnter += (_, _2) => closeWinBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
            closeWinBtn.MouseLeave += (_, _2) => closeWinBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            closeWinBtn.Click += (_, _2) => win.Close();
            Grid.SetColumn(closeWinBtn, 1);
            titleGrid.Children.Add(titleText);
            titleGrid.Children.Add(closeWinBtn);
            titleBar.Child = titleGrid;
            rootStack.Children.Add(titleBar);

            var contentArea = new StackPanel();

            // Drawing canvas
            var canvasBorder = new Border
            {
                Background = Brushes.White,
                Margin = new Thickness(12, 12, 12, 4),
                CornerRadius = new CornerRadius(4),
                Height = 170
            };
            var drawCanvas = new Canvas
            {
                Background = Brushes.White,
                ClipToBounds = true,
                Cursor = Cursors.Pen
            };
            canvasBorder.Child = drawCanvas;

            // Placeholder text
            var placeholder = new TextBlock
            {
                Text = "Draw your signature here",
                Foreground = new SolidColorBrush(Color.FromRgb(0xbb, 0xbb, 0xbb)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14, FontStyle = FontStyles.Italic,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            drawCanvas.Children.Add(placeholder);

            // Drawing state
            var strokes = new List<List<Point>>();
            List<Point>? currentStroke = null;
            Polyline? currentPoly = null;

            drawCanvas.MouseLeftButtonDown += (s, e) =>
            {
                if (placeholder.Visibility == Visibility.Visible)
                    placeholder.Visibility = Visibility.Collapsed;
                currentStroke = [];
                var pos = e.GetPosition(drawCanvas);
                currentStroke.Add(pos);
                currentPoly = new Polyline
                {
                    Stroke = Brushes.Black,
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                currentPoly.Points.Add(pos);
                drawCanvas.Children.Add(currentPoly);
                drawCanvas.CaptureMouse();
            };

            drawCanvas.MouseMove += (s, e) =>
            {
                if (currentStroke is null || currentPoly is null) return;
                var pos = e.GetPosition(drawCanvas);
                pos.X = Math.Max(0, Math.Min(drawCanvas.ActualWidth, pos.X));
                pos.Y = Math.Max(0, Math.Min(drawCanvas.ActualHeight, pos.Y));
                currentStroke.Add(pos);
                currentPoly.Points.Add(pos);
            };

            drawCanvas.MouseLeftButtonUp += (s, e) =>
            {
                if (currentStroke is not null && currentStroke.Count > 1)
                    strokes.Add(currentStroke);
                else if (currentPoly is not null)
                    drawCanvas.Children.Remove(currentPoly);
                currentStroke = null;
                currentPoly = null;
                drawCanvas.ReleaseMouseCapture();
            };

            contentArea.Children.Add(canvasBorder);

            // Buttons
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 4, 12, 12)
            };

            var clearBtn = new Button
            {
                Content = "Clear",
                Style = (Style)FindResource("DarkButton"),
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas")
            };
            clearBtn.Click += (s, e) =>
            {
                strokes.Clear();
                drawCanvas.Children.Clear();
                placeholder.Visibility = Visibility.Visible;
                drawCanvas.Children.Add(placeholder);
            };

            var saveBtn = new Button
            {
                Content = "Save Signature",
                Style = (Style)FindResource("DarkButton"),
                Padding = new Thickness(16, 6, 16, 6),
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x54, 0x3d)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold
            };
            saveBtn.Click += (s, e) =>
            {
                if (strokes.Count == 0)
                {
                    KillerDialog.Show(this, "Draw a signature first.", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                double cw = drawCanvas.ActualWidth > 0 ? drawCanvas.ActualWidth : 400;
                double ch = drawCanvas.ActualHeight > 0 ? drawCanvas.ActualHeight : 150;

                var saved = new SavedSignature
                {
                    CanvasWidth = cw,
                    CanvasHeight = ch,
                    Name = $"Signature {_signatureStore.Signatures.Count + 1}"
                };
                foreach (var stroke in strokes)
                {
                    var sPts = stroke.Select(p => new SerializablePoint { X = p.X, Y = p.Y }).ToList();
                    saved.Strokes.Add(sPts);
                }
                _signatureStore.Add(saved);
                PersistSignatures();

                // Auto-select the new signature for placement
                _pendingSignature = saved;
                _annotationCanvas.Cursor = Cursors.Cross;
                SetStatus("Signature saved - click on the page to place it");

                win.Close();
            };

            btnPanel.Children.Add(clearBtn);
            btnPanel.Children.Add(saveBtn);
            contentArea.Children.Add(btnPanel);

            rootStack.Children.Add(contentArea);
            outerChrome.Child = rootStack;
            win.Content = outerChrome;
            win.ShowDialog();
        }

        private void ImportImageSignature()
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
                Title = "Import Signature Image"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage(new Uri(dlg.FileName));
                byte[] pngBytes;
                using (var ms = new System.IO.MemoryStream())
                {
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    encoder.Save(ms);
                    pngBytes = ms.ToArray();
                }

                var saved = new SavedSignature
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName),
                    CanvasWidth = bmp.PixelWidth,
                    CanvasHeight = bmp.PixelHeight,
                    ImageData = Convert.ToBase64String(pngBytes)
                };
                _signatureStore.Add(saved);
                PersistSignatures();

                _pendingSignature = saved;
                _annotationCanvas.Cursor = Cursors.Cross;
                SetStatus("Image loaded - click on the page to place it");
                ShowSignaturePopup(); // refresh to show the new entry
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Failed to import image:\n{ex.Message}", "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PlaceSignature(Point pos, int pageIdx)
        {
            if (_pendingSignature is null) return;

            var sig = _pendingSignature;
            double scale = 0.5;

            var annot = new SignatureAnnotation
            {
                PageIndex = pageIdx,
                Position = pos,
                Scale = scale,
                SourceWidth = sig.CanvasWidth,
                SourceHeight = sig.CanvasHeight,
                ImageData = sig.ImageData
            };

            // Drawn signature — convert serializable points to WPF points
            if (sig.ImageData is null)
            {
                foreach (var stroke in sig.Strokes)
                    annot.Strokes.Add([..stroke.Select(p => new Point(p.X, p.Y))]);
            }

            AddAnnotation(annot);
            RenderAllAnnotations(pageIdx);

            // Auto-switch to Select and select the placed signature so the user
            // can immediately reposition or resize without an extra click.
            SetTool(EditTool.Select);
            double sigW = sig.CanvasWidth * scale;
            double sigH = sig.CanvasHeight * scale;
            SelectAnnotation(annot, new Rect(pos.X, pos.Y, sigW, sigH));
            SetStatus("Signature placed — drag to reposition, use the corner handle to resize");
        }

        private void PlaceImageFromDialog(Point pos, int pageIdx)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Insert Image",
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff;*.tif|All files|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var imgBytes = File.ReadAllBytes(dlg.FileName);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(imgBytes);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();

                double srcW = bmp.PixelWidth > 0 ? bmp.PixelWidth : 400;
                double srcH = bmp.PixelHeight > 0 ? bmp.PixelHeight : 300;

                // Default the placed image to ~50% of the page's longest side (in render-dim
                // units) so it is a usable size regardless of page dimensions, never upscaling
                // beyond the source's native resolution.
                double pageMax = _renderDims.TryGetValue(pageIdx, out var rdImg)
                    ? Math.Max(rdImg.w, rdImg.h) : 2048.0;
                double MaxCanvasDim = pageMax * 0.5;
                double scale = Math.Min(1.0, Math.Min(MaxCanvasDim / srcW, MaxCanvasDim / srcH));

                var imgAnnot = new ImageAnnotation
                {
                    PageIndex = pageIdx,
                    Position = pos,
                    Scale = scale,
                    SourceWidth = srcW,
                    SourceHeight = srcH,
                    ImageData = Convert.ToBase64String(imgBytes)
                };

                // Switch to Select FIRST so placement renders last and nothing wipes the image
                // (calling SetTool between render and select was what made the image vanish).
                SetTool(EditTool.Select);
                AddAnnotation(imgAnnot);
                RenderAllAnnotations(pageIdx);
                double w = srcW * scale;
                double h = srcH * scale;
                SelectAnnotation(imgAnnot, new Rect(pos.X, pos.Y, w, h));
                SetStatus("Image placed - drag to reposition, use the corner handle to resize");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Could not load image:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ============================================================
        // Canvas interaction
        // ============================================================

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_doc is null) return;
            if (sender is Canvas srcCanvas) _activeCanvas = srcCanvas;
            // Don't intercept clicks on an active text editing box
            if (_activeTextBox is not null && e.OriginalSource is DependencyObject src &&
                IsDescendantOf(src, _activeTextBox))
                return;
            // Don't intercept clicks on the crop confirm bar (canvas uses Preview events which
            // tunnel before child Button clicks fire — we must not swallow them here).
            if (_cropConfirmBar is not null && e.OriginalSource is DependencyObject cropSrc &&
                IsDescendantOf(cropSrc, _cropConfirmBar))
                return;
            // Don't intercept clicks on form field overlay controls (TextBox, CheckBox, etc.)
            // — WPF must handle those natively so focus, toggling, and text entry work.
            if (e.OriginalSource is DependencyObject formSrc && IsFormFieldElement(formSrc))
                return;
            // Check if click lands inside a PDF link overlay.
            // We do an explicit bounds check rather than relying on WPF hit-testing through
            // nested transparent canvases, which is unreliable.
            if (_linkOverlays.Count > 0)
            {
                var clickPos = e.GetPosition(_activeCanvas);
                foreach (var lo in _linkOverlays)
                {
                    double lx = Canvas.GetLeft(lo);
                    double ly = Canvas.GetTop(lo);
                    if (clickPos.X >= lx && clickPos.X <= lx + lo.Width &&
                        clickPos.Y >= ly && clickPos.Y <= ly + lo.Height)
                    {
                        var lTarget = lo.Tag is LinkAnnotInfo lai ? lai.Target : lo.Tag;
                        if (lTarget is int tp)
                            PageList.SelectedIndex = tp;
                        else if (lTarget is string u)
                            try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
                        e.Handled = true;
                        return;
                    }
                }
            }
            var pos = e.GetPosition(_activeCanvas);
            int pageIdx = _activeCanvas.Tag is int tagPage ? tagPage : PageList.SelectedIndex;
            if (pageIdx < 0) return;

            // Crop corner handles live in the outer panel and have direct MouseLeftButtonDown
            // handlers attached in AddCropHandles() — no detection needed here.

            // Check if click is on any of the four corner resize handles (signature or image)
            if (_resizeHandles.Count > 0 && _selectedAnnotation is PlacedAnnotation rsa)
            {
                foreach (var hd in _resizeHandles)
                {
                    double hx = Canvas.GetLeft(hd), hy = Canvas.GetTop(hd);
                    if (pos.X >= hx && pos.X <= hx + hd.Width &&
                        pos.Y >= hy && pos.Y <= hy + hd.Height)
                    {
                        _isResizingSig = true;
                        _resizeSigStart = pos;
                        _resizeSigStartScale = rsa.Scale;
                        _resizeSigAnnot = rsa;
                        _resizeCorner = hd.Tag as string ?? "SE";
                        // Anchor on the opposite corner so it stays put while the dragged corner moves.
                        double w0 = rsa.SourceWidth * rsa.Scale, h0 = rsa.SourceHeight * rsa.Scale;
                        double ax = rsa.Position.X, ay = rsa.Position.Y;
                        _resizeAnchor = _resizeCorner switch
                        {
                            "NW" => new Point(ax + w0, ay + h0),
                            "NE" => new Point(ax,      ay + h0),
                            "SW" => new Point(ax + w0, ay),
                            _    => new Point(ax,      ay)   // SE
                        };
                        _activeCanvas.CaptureMouse();
                        e.Handled = true;
                        return;
                    }
                }
            }

            switch (_currentTool)
            {
                case EditTool.Select:
                    if (e.ClickCount == 2)
                    {
                        ClearSelection();
                        ClearTextSelection();
                        EditTextAtPosition(pos, pageIdx);
                        e.Handled = true;
                    }
                    else
                    {
                        // Single click: check if hitting a PlacedAnnotation first — select and drag
                        bool hitPlaced = false;
                        if (_annotations.TryGetValue(pageIdx, out var pageAnnotsList))
                        {
                            for (int i = pageAnnotsList.Count - 1; i >= 0; i--)
                            {
                                if (IsDraggable(pageAnnotsList[i]) &&
                                    HitTestAnnotation(pageAnnotsList[i], pos, out Rect paBounds))
                                {
                                    var pa = pageAnnotsList[i];
                                    ClearSelection();
                                    RenderAllAnnotations(pageIdx);
                                    SelectAnnotation(pa, paBounds);
                                    _isDraggingAnnot = true;
                                    _dragAnnotStart = pos;
                                    _dragAnnotOrigPos = AnnotGetPos(pa);
                                    _dragAnnot = pa;
                                    _activeCanvas.CaptureMouse();
                                    e.Handled = true;
                                    hitPlaced = true;
                                    break;
                                }
                            }
                        }
                        if (!hitPlaced)
                        {
                            ClearSelection();
                            ClearTextSelection();
                            _isSelecting = true;
                            _selectStart = pos;
                            _selectRect = new Rectangle
                            {
                                Fill = new SolidColorBrush(Color.FromArgb(40, 74, 130, 255)),
                                Stroke = new SolidColorBrush(Color.FromArgb(120, 74, 130, 255)),
                                StrokeThickness = 1,
                                Width = 0, Height = 0,
                                IsHitTestVisible = false
                            };
                            Canvas.SetLeft(_selectRect, pos.X);
                            Canvas.SetTop(_selectRect, pos.Y);
                            _activeCanvas.Children.Add(_selectRect);
                            _activeCanvas.CaptureMouse();
                            e.Handled = true;
                        }
                    }
                    break;

                case EditTool.Text:
                    CommitActiveTextBox();
                    PlaceTextBox(pos, pageIdx);
                    e.Handled = true;
                    break;

                case EditTool.Highlight:
                    ClearSelection();
                    _isDrawing = true;
                    _drawStart = pos;
                    var rect = new Rectangle
                    {
                        Fill = new SolidColorBrush(_highlightColor),
                        Width = 0, Height = 0
                    };
                    Canvas.SetLeft(rect, pos.X);
                    Canvas.SetTop(rect, pos.Y);
                    _activeCanvas.Children.Add(rect);
                    _activePreview = rect;
                    _activeCanvas.CaptureMouse();
                    break;

                case EditTool.Draw:
                    ClearSelection();
                    _isDrawing = true;
                    _activeInk = new InkAnnotation { PageIndex = pageIdx, StrokeWidth = _drawWidth };
                    _activeInk.SetColor(_drawColor);
                    _activeInk.Points.Add(pos);
                    var poly = new Polyline
                    {
                        Stroke = new SolidColorBrush(_drawColor),
                        StrokeThickness = _drawWidth,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                    poly.Points.Add(pos);
                    _activeCanvas.Children.Add(poly);
                    _activePreview = poly;
                    _activeCanvas.CaptureMouse();
                    break;

                case EditTool.Signature:
                    if (_pendingSignature is not null)
                    {
                        PlaceSignature(pos, pageIdx);
                        e.Handled = true;
                    }
                    else
                    {
                        ShowSignaturePopup();
                    }
                    break;

                case EditTool.Image:
                    PlaceImageFromDialog(pos, pageIdx);
                    e.Handled = true;
                    break;

                case EditTool.Crop:
                    ClearSelection();
                    HideCropConfirmBar();
                    _isDrawing = true;
                    _drawStart = pos;
                    _cropPageIndex = _activeCanvas.Tag is int cpi ? cpi : (_viewMode == ViewMode.Grid ? 0 : PageList.SelectedIndex);
                    _cropPreviewRect = new Rectangle
                    {
                        Stroke          = Brushes.White,
                        StrokeThickness = 1.5,
                        StrokeDashArray = [5, 3],
                        Fill            = AccentBrush(55),
                        Width = 0, Height = 0,
                        IsHitTestVisible = false,
                        Effect = new System.Windows.Media.Effects.DropShadowEffect
                            { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 3, Opacity = 0.7 },
                    };
                    Canvas.SetLeft(_cropPreviewRect, pos.X);
                    Canvas.SetTop(_cropPreviewRect, pos.Y);
                    Panel.SetZIndex(_cropPreviewRect, 1); // handles sit at ZIndex 10
                    _activeCanvas.Children.Add(_cropPreviewRect);
                    _activePreview = _cropPreviewRect;
                    _activeCanvas.CaptureMouse();
                    break;
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            // Don't interfere with mouse interaction inside form field overlays.
            if (e.OriginalSource is DependencyObject moveSrc && IsFormFieldElement(moveSrc))
                return;

            var pos = e.GetPosition(_activeCanvas);
            pos.X = Math.Max(0, Math.Min(_activeCanvas.ActualWidth, pos.X));
            pos.Y = Math.Max(0, Math.Min(_activeCanvas.ActualHeight, pos.Y));

            // Signature resize drag
            if (_isResizingSig && _resizeSigAnnot is not null)
            {
                // Uniform-scale resize from the dragged corner; the opposite corner (_resizeAnchor)
                // stays fixed. Aspect is preserved by taking whichever axis demands the larger scale.
                double desiredW = Math.Abs(pos.X - _resizeAnchor.X);
                double desiredH = Math.Abs(pos.Y - _resizeAnchor.Y);
                double sw = Math.Max(1.0, _resizeSigAnnot.SourceWidth);
                double sh = Math.Max(1.0, _resizeSigAnnot.SourceHeight);
                double newScale = Math.Max(0.05, Math.Max(desiredW / sw, desiredH / sh));
                _resizeSigAnnot.Scale = newScale;

                double newW = _resizeSigAnnot.SourceWidth * newScale;
                double newH = _resizeSigAnnot.SourceHeight * newScale;
                // Reposition the top-left so the anchor corner is preserved.
                double nx = (_resizeCorner is "NW" or "SW") ? _resizeAnchor.X - newW : _resizeAnchor.X;
                double ny = (_resizeCorner is "NW" or "NE") ? _resizeAnchor.Y - newH : _resizeAnchor.Y;
                _resizeSigAnnot.Position = new Point(nx, ny);

                if (_selectionBorder is not null)
                {
                    _selectionBorder.Width  = newW + 8;
                    _selectionBorder.Height = newH + 8;
                    Canvas.SetLeft(_selectionBorder, nx - 4);
                    Canvas.SetTop(_selectionBorder, ny - 4);
                }
                LayoutResizeHandles(nx, ny, newW, newH);

                // Re-render annotations to show updated size
                RenderAllAnnotations(_resizeSigAnnot.PageIndex);
                // Restore selection visuals (RenderAllAnnotations clears canvas children including our overlays)
                _activeCanvas.Children.Add(_selectionBorder!);
                foreach (var hd in _resizeHandles) _activeCanvas.Children.Add(hd);
                return;
            }

            // Annotation drag-to-move
            if (_isDraggingAnnot && _dragAnnot is not null)
            {
                double dx = pos.X - _dragAnnotStart.X;
                double dy = pos.Y - _dragAnnotStart.Y;
                AnnotSetPos(_dragAnnot, new Point(_dragAnnotOrigPos.X + dx, _dragAnnotOrigPos.Y + dy));
                var db = AnnotBounds(_dragAnnot);
                if (_selectionBorder is not null)
                {
                    Canvas.SetLeft(_selectionBorder, db.X - 4);
                    Canvas.SetTop(_selectionBorder, db.Y - 4);
                }
                if (_dragAnnot is PlacedAnnotation)
                    LayoutResizeHandles(db.X, db.Y, db.Width, db.Height);
                RenderAllAnnotations(_dragAnnot.PageIndex);
                _activeCanvas.Children.Add(_selectionBorder!);
                foreach (var hd in _resizeHandles) _activeCanvas.Children.Add(hd);
                return;
            }

            // Text selection drag
            if (_isSelecting && _selectRect is not null)
            {
                Canvas.SetLeft(_selectRect, Math.Min(pos.X, _selectStart.X));
                Canvas.SetTop(_selectRect, Math.Min(pos.Y, _selectStart.Y));
                _selectRect.Width = Math.Abs(pos.X - _selectStart.X);
                _selectRect.Height = Math.Abs(pos.Y - _selectStart.Y);
                return;
            }

            // Crop corner handle drag — must be before the _isDrawing guard since handle drag
            // runs with _isDrawing = false and _activePreview = null.
            if (_activeCropHandleTag is not null && _cropPreviewRect is not null)
            {
                double dx = pos.X - _cropHandleDragStart.X;
                double dy = pos.Y - _cropHandleDragStart.Y;
                var r = _cropRectAtHandleDrag;
                double newX = r.X, newY = r.Y, newW = r.Width, newH = r.Height;
                switch (_activeCropHandleTag)
                {
                    case "NW":
                        newX = Math.Min(r.Right - 10, r.X + dx);
                        newY = Math.Min(r.Bottom - 10, r.Y + dy);
                        newW = r.Right - newX;
                        newH = r.Bottom - newY;
                        break;
                    case "NE":
                        newY = Math.Min(r.Bottom - 10, r.Y + dy);
                        newW = Math.Max(10, r.Width + dx);
                        newH = r.Bottom - newY;
                        break;
                    case "SE":
                        newW = Math.Max(10, r.Width + dx);
                        newH = Math.Max(10, r.Height + dy);
                        break;
                    case "SW":
                        newX = Math.Min(r.Right - 10, r.X + dx);
                        newW = r.Right - newX;
                        newH = Math.Max(10, r.Height + dy);
                        break;
                }
                _cropCanvasRect = new Rect(newX, newY, newW, newH);
                UpdateCropRectVisuals();
                return;
            }

            if (!_isDrawing || _activePreview is null) return;

            switch (_currentTool)
            {
                case EditTool.Highlight when _activePreview is Rectangle rect:
                    Canvas.SetLeft(rect, Math.Min(pos.X, _drawStart.X));
                    Canvas.SetTop(rect, Math.Min(pos.Y, _drawStart.Y));
                    rect.Width = Math.Abs(pos.X - _drawStart.X);
                    rect.Height = Math.Abs(pos.Y - _drawStart.Y);
                    break;

                case EditTool.Draw when _activePreview is Polyline poly && _activeInk is not null:
                    _activeInk.Points.Add(pos);
                    poly.Points.Add(pos);
                    break;

                case EditTool.Crop when _activePreview is Rectangle crect:
                    Canvas.SetLeft(crect, Math.Min(pos.X, _drawStart.X));
                    Canvas.SetTop(crect, Math.Min(pos.Y, _drawStart.Y));
                    crect.Width = Math.Abs(pos.X - _drawStart.X);
                    crect.Height = Math.Abs(pos.Y - _drawStart.Y);
                    break;
            }
        }

        // Draggable annotations (placed image/signature and typewriter text) expose a top-left
        // Position; these helpers read/write it generically so one drag path serves both.
        private static bool IsDraggable(PageAnnotation a) => a is PlacedAnnotation or TextAnnotation;
        private static Point AnnotGetPos(PageAnnotation a) => a switch
        {
            PlacedAnnotation p => p.Position,
            TextAnnotation t   => t.Position,
            _                  => default
        };
        private static void AnnotSetPos(PageAnnotation a, Point pos)
        {
            switch (a)
            {
                case PlacedAnnotation p: p.Position = pos; break;
                case TextAnnotation t:   t.Position = pos; break;
            }
        }
        private Rect AnnotBounds(PageAnnotation a)
        {
            HitTestAnnotation(a, new Point(double.MinValue, double.MinValue), out Rect b);
            return b;
        }

        // Returns the page index + canvas under the mouse across every per-page overlay
        // (grid / two-page / continuous tiles) and the primary page canvas. Used to drop a placed
        // annotation onto a different page than the one it started on.
        private (int page, Canvas canvas)? PageCanvasUnderPointer(MouseEventArgs e)
        {
            foreach (var kv in _continuousCanvases)
            {
                var c = kv.Value;
                if (c.ActualWidth <= 0 || c.ActualHeight <= 0) continue;
                var p = e.GetPosition(c);
                if (p.X >= 0 && p.X <= c.ActualWidth && p.Y >= 0 && p.Y <= c.ActualHeight)
                    return (kv.Key, c);
            }
            if (_annotationCanvas.ActualWidth > 0 && _annotationCanvas.ActualHeight > 0)
            {
                var pp = e.GetPosition(_annotationCanvas);
                if (pp.X >= 0 && pp.X <= _annotationCanvas.ActualWidth &&
                    pp.Y >= 0 && pp.Y <= _annotationCanvas.ActualHeight)
                    return (_viewMode == ViewMode.Grid ? 0 : PageList.SelectedIndex, _annotationCanvas);
            }
            return null;
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Don't process release events that originate inside the crop confirm bar.
            if (_cropConfirmBar is not null && e.OriginalSource is DependencyObject cropSrc &&
                IsDescendantOf(cropSrc, _cropConfirmBar))
                return;

            int pageIdx = PageList.SelectedIndex;

            // Finish crop handle drag
            if (_activeCropHandleTag is not null)
            {
                _activeCropHandleTag = null;
                _activeCanvas.ReleaseMouseCapture();
                return;
            }

            // Finish annotation drag-to-move
            if (_isDraggingAnnot)
            {
                _isDraggingAnnot = false;
                _activeCanvas.ReleaseMouseCapture();
                if (_dragAnnot is not null)
                {
                    var da = _dragAnnot;
                    _dragAnnot = null;
                    int oldPage = da.PageIndex;
                    // Released over a different page? Move it there (position was updated live during drag).
                    var drop = PageCanvasUnderPointer(e);
                    if (drop is { } d && d.page != oldPage && _doc is not null
                        && d.page >= 0 && d.page < _doc.PageCount)
                    {
                        var pt = e.GetPosition(d.canvas);
                        AnnotSetPos(da, new Point(pt.X - (_dragAnnotStart.X - _dragAnnotOrigPos.X),
                                                  pt.Y - (_dragAnnotStart.Y - _dragAnnotOrigPos.Y)));
                        if (_annotations.TryGetValue(oldPage, out var oldList)) oldList.Remove(da);
                        da.PageIndex = d.page;
                        if (!_annotations.TryGetValue(d.page, out var newList)) { newList = []; _annotations[d.page] = newList; }
                        newList.Add(da);
                        ClearSelection();
                        RenderAllAnnotations(oldPage);
                        RenderAllAnnotations(d.page);
                        SelectAnnotation(da, AnnotBounds(da));
                        MarkDirty();
                        return;
                    }
                    RenderAllAnnotations(da.PageIndex);
                    SelectAnnotation(da, AnnotBounds(da));
                    MarkDirty();
                }
                return;
            }

            // Finish signature resize
            if (_isResizingSig)
            {
                _isResizingSig = false;
                _activeCanvas.ReleaseMouseCapture();
                if (_resizeSigAnnot is not null)
                {
                    // Final re-render and re-select to reposition handle cleanly
                    var sa = _resizeSigAnnot;
                    _resizeSigAnnot = null;
                    RenderAllAnnotations(sa.PageIndex);
                    double newW = sa.SourceWidth * sa.Scale;
                    double newH = sa.SourceHeight * sa.Scale;
                    SelectAnnotation(sa, new Rect(sa.Position.X, sa.Position.Y, newW, newH));
                    MarkDirty();
                }
                return;
            }

            // Handle text selection release
            if (_isSelecting)
            {
                _isSelecting = false;
                _activeCanvas.ReleaseMouseCapture();
                var pos = e.GetPosition(_activeCanvas);
                double dragW = Math.Abs(pos.X - _selectStart.X);
                double dragH = Math.Abs(pos.Y - _selectStart.Y);

                if (dragW < 5 && dragH < 5)
                {
                    // Tiny drag = single click -> try annotation selection
                    ClearTextSelection();
                    if (pageIdx >= 0 && _annotations.ContainsKey(pageIdx))
                    {
                        for (int i = _annotations[pageIdx].Count - 1; i >= 0; i--)
                        {
                            if (HitTestAnnotation(_annotations[pageIdx][i], _selectStart, out Rect bounds))
                            {
                                SelectAnnotation(_annotations[pageIdx][i], bounds);
                                break;
                            }
                        }
                    }
                }
                else
                {
                    // Real drag -> extract text from rectangle
                    var selectBounds = new Rect(
                        Math.Min(pos.X, _selectStart.X), Math.Min(pos.Y, _selectStart.Y),
                        dragW, dragH);
                    ExtractTextFromRegion(pageIdx, selectBounds);
                }
                return;
            }

            if (!_isDrawing) return;
            _isDrawing = false;
            _activeCanvas.ReleaseMouseCapture();

            switch (_currentTool)
            {
                case EditTool.Highlight when _activePreview is Rectangle rect:
                    if (rect.Width > 3 && rect.Height > 3)
                    {
                        var ha = new HighlightAnnotation
                        {
                            PageIndex = pageIdx,
                            Bounds = new Rect(Canvas.GetLeft(rect), Canvas.GetTop(rect), rect.Width, rect.Height)
                        };
                        ha.SetColor(_highlightColor);
                        AddAnnotation(ha);
                    }
                    else
                    {
                        _activeCanvas.Children.Remove(rect);
                    }
                    break;

                case EditTool.Draw when _activeInk is not null:
                    if (_activeInk.Points.Count > 2)
                    {
                        AddAnnotation(_activeInk);
                    }
                    else
                    {
                        _activeCanvas.Children.Remove(_activePreview);
                    }
                    _activeInk = null;
                    break;

                case EditTool.Crop when _activePreview is Rectangle cr:
                    _activeCanvas.ReleaseMouseCapture(); // MUST release before showing handles
                    if (cr.Width > 10 && cr.Height > 10)
                    {
                        _cropCanvasRect = new Rect(Canvas.GetLeft(cr), Canvas.GetTop(cr), cr.Width, cr.Height);
                        _activePreview = null;
                        if (_cropPreviewRect is not null)
                            Panel.SetZIndex(_cropPreviewRect, 1); // below handles (ZIndex 10)
                        ShowCropConfirmBar();
                        return;
                    }
                    else
                    {
                        _activeCanvas.Children.Remove(cr);
                        _cropPreviewRect = null;
                    }
                    break;
            }
            _activePreview = null;
        }

        // ============================================================
        // Selection
        // ============================================================

        private bool HitTestAnnotation(PageAnnotation annot, Point pos, out Rect bounds)
        {
            switch (annot)
            {
                case HighlightAnnotation ha:
                    bounds = ha.Bounds;
                    return bounds.Contains(pos);

                case TextAnnotation ta:
                    var ft = new FormattedText(ta.Content,
                        System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), ta.FontSize, Brushes.Black,
                        VisualTreeHelper.GetDpi(_activeCanvas).PixelsPerDip);
                    bounds = new Rect(ta.Position.X, ta.Position.Y, ft.Width + 8, ft.Height + 8);
                    return bounds.Contains(pos);

                case InkAnnotation ia when ia.Points.Count > 0:
                    bool near = ia.Points.Any(p =>
                        Math.Sqrt((p.X - pos.X) * (p.X - pos.X) + (p.Y - pos.Y) * (p.Y - pos.Y)) < 15);
                    if (near)
                    {
                        double minX = ia.Points.Min(p => p.X);
                        double minY = ia.Points.Min(p => p.Y);
                        double maxX = ia.Points.Max(p => p.X);
                        double maxY = ia.Points.Max(p => p.Y);
                        bounds = new Rect(minX, minY, Math.Max(maxX - minX, 4), Math.Max(maxY - minY, 4));
                        return true;
                    }
                    bounds = Rect.Empty;
                    return false;

                case TextEditAnnotation tea:
                    bounds = tea.OriginalBounds;
                    return bounds.Contains(pos);

                case SignatureAnnotation sa:
                    double sigW = sa.SourceWidth * sa.Scale;
                    double sigH = sa.SourceHeight * sa.Scale;
                    bounds = new Rect(sa.Position.X, sa.Position.Y, sigW, sigH);
                    return bounds.Contains(pos);

                case ImageAnnotation ia:
                    double iaW = ia.SourceWidth * ia.Scale;
                    double iaH = ia.SourceHeight * ia.Scale;
                    bounds = new Rect(ia.Position.X, ia.Position.Y, iaW, iaH);
                    return bounds.Contains(pos);

                default:
                    bounds = Rect.Empty;
                    return false;
            }
        }

        // Resolve the active theme's "SelectionAccent" color: a per-theme color picked to stay
        // readable on the white PDF page (Accent is white in several themes, and AccentBorder is a
        // pale cream that washes out on white). Falls back to brand green.
        private Color AccentColor()
            => TryFindResource("SelectionAccent") is SolidColorBrush b ? b.Color : Color.FromRgb(30, 165, 76);
        private SolidColorBrush AccentBrush(byte alpha = 255)
        {
            var c = AccentColor();
            return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        }

        // Recolor any live selection / crop visuals to the current theme's SelectionAccent.
        // Their brushes are plain (not resource references), so a theme swap won't update them
        // until reselected unless we repaint them here.
        private void RefreshSelectionAccent()
        {
            if (_selectionBorder is not null)
            {
                _selectionBorder.BorderBrush = AccentBrush();
                _selectionBorder.Background  = AccentBrush(40);
            }
            foreach (var hd in _resizeHandles)
                hd.Fill = AccentBrush();
            if (_cropPreviewRect is not null)
                _cropPreviewRect.Fill = AccentBrush(55);
        }

        // Positions the four corner handles around an annotation's bounds (top-left x,y and size w,h).
        private void LayoutResizeHandles(double x, double y, double w, double h)
        {
            foreach (var hd in _resizeHandles)
            {
                double hs = hd.Width;
                (double cx, double cy) = (hd.Tag as string) switch
                {
                    "NW" => (x,     y),
                    "NE" => (x + w, y),
                    "SW" => (x,     y + h),
                    _    => (x + w, y + h)   // SE
                };
                Canvas.SetLeft(hd, cx - hs / 2);
                Canvas.SetTop(hd, cy - hs / 2);
            }
        }

        private void SelectAnnotation(PageAnnotation annot, Rect bounds)
        {
            _selectedAnnotation = annot;
            // Continuous-view overlays are scaled down by their LayoutTransform, which would
            // shrink the selection outline and resize handle to near-invisibility. Compensate
            // so they render at the same on-screen size as single-page view.
            double inv = 1.0;
            if (_activeCanvas.LayoutTransform is ScaleTransform _selScale && _selScale.ScaleX > 0.0001)
                inv = 1.0 / _selScale.ScaleX;
            _selectionBorder = new Border
            {
                BorderBrush = AccentBrush(),
                BorderThickness = new Thickness(2 * inv),
                Background = AccentBrush(40),
                Width = bounds.Width + 8,
                Height = bounds.Height + 8,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(_selectionBorder, bounds.X - 4);
            Canvas.SetTop(_selectionBorder, bounds.Y - 4);
            _activeCanvas.Children.Add(_selectionBorder);

            // Add four corner resize handles for placed annotations (signature, image).
            if (annot is PlacedAnnotation)
            {
                double hSize = 14 * inv;
                _resizeHandles.Clear();
                foreach (string tag in new[] { "NW", "NE", "SE", "SW" })
                {
                    var hd = new Rectangle
                    {
                        Width = hSize, Height = hSize,
                        Fill = AccentBrush(),
                        Stroke = Brushes.White, StrokeThickness = 1 * inv,
                        Cursor = (tag is "NW" or "SE") ? Cursors.SizeNWSE : Cursors.SizeNESW,
                        IsHitTestVisible = true,
                        Tag = tag
                    };
                    _resizeHandles.Add(hd);
                    _activeCanvas.Children.Add(hd);
                }
                LayoutResizeHandles(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                string label = annot is SignatureAnnotation ? "Signature" : "Image";
                SetStatus($"{label} selected - drag any corner to resize, Delete to remove");
            }
            else
            {
                SetStatus($"Selected {annot.GetType().Name.Replace("Annotation", "").ToLower()} annotation - press Delete to remove");
            }
        }

        private static bool IsDescendantOf(DependencyObject child, DependencyObject parent)
        {
            var current = child;
            while (current != null)
            {
                if (current == parent) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        /// <summary>
        /// Returns true if <paramref name="element"/> is inside a form field overlay control
        /// (tagged with <see cref="FormOverlayTag"/>). Used to let WPF handle mouse events
        /// for TextBox, CheckBox, RadioButton, and ComboBox controls natively.
        /// </summary>
        private static bool IsFormFieldElement(DependencyObject element)
        {
            var current = element;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.Tag as string == FormOverlayTag)
                    return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private void ClearSelection()
        {
            if (_selectionBorder is not null)
            {
                _activeCanvas.Children.Remove(_selectionBorder);
                _selectionBorder = null;
            }
            foreach (var hd in _resizeHandles)
                _activeCanvas.Children.Remove(hd);
            _resizeHandles.Clear();
            _isResizingSig = false;
            _resizeSigAnnot = null;
            _isDraggingAnnot = false;
            _dragAnnot = null;
            _selectedAnnotation = null;
        }

        private void DeleteSelected()
        {
            if (_selectedAnnotation is null) return;
            int pageIdx = _selectedAnnotation.PageIndex;
            if (_annotations.ContainsKey(pageIdx))
                _annotations[pageIdx].Remove(_selectedAnnotation);
            ClearSelection();
            RenderAllAnnotations(pageIdx);
            SetStatus("Deleted selected annotation");
        }

        private void SelectAllText()
        {
            if (_currentFile is null) return;
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;

            try
            {
                using var pigDoc = PdfPigDoc.Open(_currentFile);
                if (pageIdx >= pigDoc.NumberOfPages) return;
                var page = pigDoc.GetPage(pageIdx + 1);
                _selectedText = WordsToText(page.GetWords());
                if (string.IsNullOrWhiteSpace(_selectedText))
                {
                    SetStatus("No text found on this page");
                    return;
                }
                Clipboard.SetText(_selectedText);
                // Visual feedback: highlight entire canvas
                ClearTextSelection();
                _selectRect = new Rectangle
                {
                    Fill = new SolidColorBrush(Color.FromArgb(30, 74, 130, 255)),
                    Stroke = new SolidColorBrush(Color.FromArgb(80, 74, 130, 255)),
                    StrokeThickness = 1,
                    Width = _annotationCanvas.Width,
                    Height = _annotationCanvas.Height,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(_selectRect, 0);
                Canvas.SetTop(_selectRect, 0);
                _annotationCanvas.Children.Add(_selectRect);
                SetStatus($"Selected all text - copied to clipboard");
            }
            catch (Exception ex)
            {
                SetStatus($"Select all error: {ex.Message}");
            }
        }

        private void CopySelectedText()
        {
            if (!string.IsNullOrEmpty(_selectedText))
            {
                Clipboard.SetText(_selectedText);
                SetStatus($"Copied to clipboard");
            }
            else
            {
                SetStatus("No text selected - drag to select text");
            }
        }

        private void ClearTextSelection()
        {
            if (_selectRect is not null)
            {
                _activeCanvas.Children.Remove(_selectRect);
                _selectRect = null;
            }
            _selectedText = null;
        }

        private void ExtractTextFromRegion(int pageIdx, Rect canvasBounds)
        {
            if (_currentFile is null || pageIdx < 0) return;
            if (!_renderDims.ContainsKey(pageIdx)) return;

            try
            {
                var (renderW, renderH) = _renderDims[pageIdx];

                using var pigDoc = PdfPigDoc.Open(_currentFile);
                if (pageIdx >= pigDoc.NumberOfPages) return;
                var page = pigDoc.GetPage(pageIdx + 1); // PdfPig is 1-based

                double pdfW = page.Width;
                double pdfH = page.Height;
                double sx = pdfW / renderW;
                double sy = pdfH / renderH;

                // Convert canvas rect to PDF coordinates (flip Y - PDF origin is bottom-left)
                double pdfLeft = canvasBounds.Left * sx;
                double pdfRight = canvasBounds.Right * sx;
                double pdfTop = pdfH - (canvasBounds.Top * sy);
                double pdfBottom = pdfH - (canvasBounds.Bottom * sy);
                // pdfTop > pdfBottom because of Y flip
                double pdfMinY = Math.Min(pdfTop, pdfBottom);
                double pdfMaxY = Math.Max(pdfTop, pdfBottom);

                var words = page.GetWords()
                    .Where(w =>
                    {
                        var bb = w.BoundingBox;
                        double cx = (bb.Left + bb.Right) / 2;
                        double cy = (bb.Bottom + bb.Top) / 2;
                        return cx >= pdfLeft && cx <= pdfRight && cy >= pdfMinY && cy <= pdfMaxY;
                    })
                    .ToList();

                if (words.Count == 0)
                {
                    SetStatus("No text found in selection");
                    ClearTextSelection();
                    return;
                }

                _selectedText = WordsToText(words);

                Clipboard.SetText(_selectedText);
                int wordCount = words.Count;
                SetStatus($"Copied {wordCount} word(s) to clipboard");
            }
            catch (Exception ex)
            {
                SetStatus($"Text extraction error: {ex.Message}");
                ClearTextSelection();
            }
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

        private void ToggleSearchBar()
        {
            if (_searchBar is not null && _searchBar.Visibility == Visibility.Visible)
            {
                CloseSearchBar();
                return;
            }
            ShowSearchBar();
        }

        private void ShowSearchBar()
        {
            if (_searchBar is null)
            {
                // Build search bar programmatically and inject into the preview area grid
                _searchBox = new TextBox
                {
                    Width = 260,
                    Height = 28,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 13,
                    Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a)),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0)),
                    BorderBrush = (SolidColorBrush)FindResource("Accent"), SelectionBrush = AccentBrush(),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 2, 6, 2),
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                _searchBox.KeyDown += SearchBox_KeyDown;
                _searchBox.TextChanged += SearchBox_TextChanged;

                _searchStatus = new TextBlock
                {
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };

                var closeBtn = new Button
                {
                    Content = "\ue711",  // MDL2 Cancel glyph \u2014 matches ToolbarButton font
                    Margin = new Thickness(4, 0, 0, 0),
                    Style = (Style)FindResource("ToolbarButton"),
                    ToolTip = "Close search (Esc)"
                };
                closeBtn.Click += (s, e) => CloseSearchBar();

                var searchIcon = new TextBlock
                {
                    Text = "",  // Segoe MDL2 Search / magnifying glass
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    IsHitTestVisible = false
                };

                var panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(8)
                };
                panel.Children.Add(searchIcon);
                panel.Children.Add(_searchBox);
                panel.Children.Add(_searchStatus);
                panel.Children.Add(closeBtn);

                _searchBar = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)),
                    BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    CornerRadius = new CornerRadius(0, 0, 4, 4),
                    Padding = new Thickness(4),
                    Child = panel,
                    Margin = new Thickness(0, 0, 16, 0)
                };

                // Add to the preview area grid (parent of ScrollViewer)
                var previewGrid = PagePreviewPanel.Parent as Grid;
                if (previewGrid is not null)
                {
                    Panel.SetZIndex(_searchBar, 100);
                    previewGrid.Children.Add(_searchBar);
                }
            }

            _searchBar.Visibility = Visibility.Visible;
            _searchBox!.Text = "";
            if (_searchStatus != null) _searchStatus.Text = "Enter = next  Shift+Enter = prev";
            _searchBox.Focus();
            Keyboard.Focus(_searchBox);
        }

        private void CloseSearchBar()
        {
            if (_searchBar is not null)
                _searchBar.Visibility = Visibility.Collapsed;
            ClearSearchHighlights();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseSearchBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                    SearchPrevResult();
                else
                    SearchNextResult();
                e.Handled = true;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = _searchBox?.Text ?? "";
            if (text.Length >= 2)
                RunSearch(text);
            else
            {
                ClearSearchHighlights();
                _allSearchRects.Clear();
                _searchResultPages.Clear();
                _searchPageCursor = -1;
            }
        }

        private readonly SearchService _searchService = new();

        private void RunSearch(string query)
        {
            ClearSearchHighlights();
            _allSearchRects.Clear();
            _searchResultPages.Clear();
            _searchPageCursor = -1;

            if (string.IsNullOrWhiteSpace(query) || _currentFile is null)
            {
                if (_searchStatus != null) _searchStatus.Text = "";
                return;
            }

            try
            {
                var sr = _searchService.Search(_currentFile, query);

                foreach (var kvp in sr.PageRects)
                    _allSearchRects[kvp.Key] = kvp.Value;
                _searchResultPages.AddRange(sr.ResultPages);

                if (_searchResultPages.Count == 0)
                {
                    if (_searchStatus != null) _searchStatus.Text = "No matches";
                    return;
                }

                int startPage = PageList.SelectedIndex;
                _searchPageCursor = _searchResultPages.FindIndex(p => p >= startPage);
                if (_searchPageCursor < 0) _searchPageCursor = 0;

                if (_searchStatus != null)
                    _searchStatus.Text = sr.TotalHits == 1
                        ? $"1 match ({_searchResultPages.Count} page)"
                        : $"{sr.TotalHits} matches ({_searchResultPages.Count} page{(_searchResultPages.Count != 1 ? "s" : "")})";

                int targetPage = _searchResultPages[_searchPageCursor];
                if (PageList.SelectedIndex != targetPage)
                    PageList.SelectedIndex = targetPage;
                else
                    HighlightSearchResultsOnCurrentPage();
            }
            catch
            {
                if (_searchStatus != null) _searchStatus.Text = "Search error";
            }
        }

        private void HighlightSearchResultsOnCurrentPage()
        {
            ClearSearchHighlights();
            int curPage = PageList.SelectedIndex;
            if (!_allSearchRects.ContainsKey(curPage)) return;
            if (!_renderDims.ContainsKey(curPage)) return;

            var (renderW, renderH) = _renderDims[curPage];

            try
            {
                using var pigDoc = PdfPigDoc.Open(_currentFile!);
                var page = pigDoc.GetPage(curPage + 1);
                double pdfW = page.Width;
                double pdfH = page.Height;
                double sx = renderW / pdfW;
                double sy = renderH / pdfH;

                foreach (var (left, bottom, right, top) in _allSearchRects[curPage])
                    AddSearchHighlight(left, bottom, right, top, sx, sy, renderH);
            }
            catch { }
        }

        private void SearchNextResult()
        {
            if (_searchResultPages.Count == 0) return;
            _searchPageCursor = (_searchPageCursor + 1) % _searchResultPages.Count;
            int targetPage = _searchResultPages[_searchPageCursor];
            if (PageList.SelectedIndex != targetPage)
                PageList.SelectedIndex = targetPage;
            else
                HighlightSearchResultsOnCurrentPage();
        }

        private void SearchPrevResult()
        {
            if (_searchResultPages.Count == 0) return;
            _searchPageCursor = (_searchPageCursor - 1 + _searchResultPages.Count) % _searchResultPages.Count;
            int targetPage = _searchResultPages[_searchPageCursor];
            if (PageList.SelectedIndex != targetPage)
                PageList.SelectedIndex = targetPage;
            else
                HighlightSearchResultsOnCurrentPage();
        }

        private void AddSearchHighlight(double left, double bottom, double right, double top,
            double sx, double sy, double renderH)
        {
            double cx = left  * sx;
            double cy = renderH - (top * sy);
            double cw = (right - left) * sx;
            double ch = (top - bottom) * sy;
            var rect = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(80, 255, 165, 0)),
                Stroke = new SolidColorBrush(Color.FromArgb(160, 255, 165, 0)),
                StrokeThickness = 1,
                Width = Math.Max(cw, 4),
                Height = Math.Max(ch, 4),
                IsHitTestVisible = false,
                Tag = "SearchHighlight"
            };
            Canvas.SetLeft(rect, cx);
            Canvas.SetTop(rect, cy);
            _annotationCanvas.Children.Add(rect);
        }

        private void ClearSearchHighlights()
        {
            var toRemove = _annotationCanvas.Children.OfType<Rectangle>()
                .Where(r => r.Tag is string s && s == "SearchHighlight").ToList();
            foreach (var r in toRemove)
                _annotationCanvas.Children.Remove(r);
            if (_searchStatus is not null)
                _searchStatus.Text = "";
        }

        // ============================================================
        // Inline text editing (double-click)
        // ============================================================

        private void EditTextAtPosition(Point canvasPos, int pageIdx)
        {
            if (_currentFile is null || !_renderDims.ContainsKey(pageIdx)) return;

            // Commit any existing edit first
            if (_activeTextBox is not null)
            {
                CommitActiveTextBox();
                return;
            }

            // Re-edit an already-committed TextEditAnnotation without re-reading the PDF.
            // Without this check, a second double-click would read the original file, produce
            // a duplicate whiteout+text layer, and cause the "overlapping quasi-duplicates" bug.
            if (_annotations.TryGetValue(pageIdx, out var existingPage))
            {
                var existingEdit = existingPage.OfType<TextEditAnnotation>()
                    .FirstOrDefault(a => a.OriginalBounds.Contains(canvasPos));
                if (existingEdit is not null)
                {
                    var reb = existingEdit.OriginalBounds;
                    var retb = new TextBox
                    {
                        Text = existingEdit.NewContent,
                        Background = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)),
                        Foreground = Brushes.Black,
                        BorderBrush = (SolidColorBrush)FindResource("Accent"), SelectionBrush = AccentBrush(),
                        BorderThickness = new Thickness(2),
                        FontFamily = new FontFamily(existingEdit.FontName),
                        FontSize = Math.Max(existingEdit.FontSize, 10),
                        MinWidth = Math.Max(reb.Width + 20, 100),
                        Height = Math.Max(reb.Height + 12, 24),
                        Padding = new Thickness(2, 0, 2, 0),
                        VerticalContentAlignment = VerticalAlignment.Center,
                        AcceptsReturn = false,
                        Tag = new TextEditContext
                        {
                            PageIndex = pageIdx,
                            OriginalText = existingEdit.OriginalContent,
                            CanvasBounds = reb,
                            Position = existingEdit.Position,
                            FontSize = existingEdit.FontSize,
                            FontName = existingEdit.FontName,
                            ExistingAnnotation = existingEdit
                        }
                    };
                    Canvas.SetLeft(retb, reb.X);
                    Canvas.SetTop(retb, reb.Y);
                    _activeCanvas.Children.Add(retb);
                    _activeTextBox = retb;
                    var rewo = new Rectangle
                    {
                        Fill = Brushes.White,
                        Width = reb.Width + 4,
                        Height = reb.Height + 4,
                        IsHitTestVisible = false,
                        Tag = "EditWhiteout"
                    };
                    Canvas.SetLeft(rewo, reb.X - 2);
                    Canvas.SetTop(rewo, reb.Y - 2);
                    _activeCanvas.Children.Insert(_activeCanvas.Children.IndexOf(retb), rewo);
                    retb.KeyDown += EditTextBox_KeyDown;
                    retb.Loaded += (s, ev) => { retb.Focus(); Keyboard.Focus(retb); retb.SelectAll(); retb.LostFocus += EditTextBox_LostFocus; };
                    SetStatus("Re-editing text — Enter to save, Escape to cancel");
                    return;
                }
            }

            // Re-edit a user-placed text annotation: lift it into an editable box
            // pre-filled with its content, size (shown in points), and color.
            if (_annotations.TryGetValue(pageIdx, out var placedPage))
            {
                var placed = placedPage.OfType<TextAnnotation>()
                    .LastOrDefault(a => HitTestAnnotation(a, canvasPos, out _));
                if (placed is not null)
                {
                    var pcol = placed.GetColor();
                    _textColor = pcol;
                    double syp = 1.0;
                    if (_doc is not null && _renderDims.TryGetValue(pageIdx, out var prd) && prd.h > 0)
                        syp = _doc.Pages[pageIdx].Height.Point / prd.h;
                    _textFontSize = Math.Max(1, Math.Round(placed.FontSize * syp));

                    _reeditOriginal = placed;
                    placedPage.Remove(placed);
                    RenderAllAnnotations(pageIdx);

                    var ptb = new TextBox
                    {
                        Text = placed.Content,
                        Background = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
                        Foreground = new SolidColorBrush(pcol),
                        BorderBrush = (SolidColorBrush)FindResource("Accent"), SelectionBrush = AccentBrush(),
                        BorderThickness = new Thickness(1),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = placed.FontSize,
                        MinWidth = 120,
                        MinHeight = 24,
                        Padding = new Thickness(2),
                        AcceptsReturn = true,
                        Tag = pageIdx
                    };
                    Canvas.SetLeft(ptb, placed.Position.X);
                    Canvas.SetTop(ptb, placed.Position.Y);
                    _activeCanvas.Children.Add(ptb);
                    _activeTextBox = ptb;
                    ptb.KeyDown += TextBox_KeyDown;
                    ptb.Loaded += (s, ev) => { ptb.Focus(); Keyboard.Focus(ptb); ptb.SelectAll(); ptb.LostFocus += TextBox_LostFocus; };
                    ShowTextSettings();
                    SetStatus("Editing text — change size/color above, Enter to save");
                    return;
                }
            }

            try
            {
                var (renderW, renderH) = _renderDims[pageIdx];

                using var pigDoc = PdfPigDoc.Open(_currentFile);
                if (pageIdx >= pigDoc.NumberOfPages) return;
                var page = pigDoc.GetPage(pageIdx + 1);

                double pdfW = page.Width;
                double pdfH = page.Height;
                double sxInv = (double)renderW / pdfW; // pdf->canvas
                double syInv = (double)renderH / pdfH;

                // Convert all words to canvas coordinates upfront
                var canvasWords = page.GetWords().Select(w =>
                {
                    double cx = w.BoundingBox.Left * sxInv;
                    double cy = renderH - (w.BoundingBox.Top * syInv);
                    double cw = (w.BoundingBox.Right - w.BoundingBox.Left) * sxInv;
                    double ch = (w.BoundingBox.Top - w.BoundingBox.Bottom) * syInv;
                    return new { Word = w, Rect = new Rect(cx, cy, cw, ch) };
                }).ToList();

                if (canvasWords.Count == 0) { SetStatus("No selectable text — this page may be a scanned image"); return; }

                // Find words on the same line as the click (Y overlap with tolerance)
                var clickY = canvasPos.Y;
                var lineWords = canvasWords
                    .Where(cw => clickY >= cw.Rect.Top - 3 && clickY <= cw.Rect.Bottom + 3)
                    .OrderBy(cw => cw.Rect.Left)  // strictly left-to-right
                    .ToList();

                if (lineWords.Count == 0)
                {
                    // Try nearest line within 20px
                    var nearest = canvasWords
                        .OrderBy(cw => Math.Abs((cw.Rect.Top + cw.Rect.Bottom) / 2 - clickY))
                        .First();
                    double nearMidY = (nearest.Rect.Top + nearest.Rect.Bottom) / 2;
                    lineWords = [..canvasWords
                        .Where(cw => Math.Abs((cw.Rect.Top + cw.Rect.Bottom) / 2 - nearMidY) < 5)
                        .OrderBy(cw => cw.Rect.Left)];
                }

                if (lineWords.Count == 0)
                {
                    SetStatus("No text line found at this position");
                    return;
                }

                // Compute bounding box in canvas space
                double cLeft = lineWords.Min(w => w.Rect.Left);
                double cTop = lineWords.Min(w => w.Rect.Top);
                double cRight = lineWords.Max(w => w.Rect.Right);
                double cBottom = lineWords.Max(w => w.Rect.Bottom);
                double cWidth = cRight - cLeft;
                double cHeight = cBottom - cTop;

                string lineText = string.Join(" ", lineWords.Select(w => w.Word.Text));

                // Get actual font info from PdfPig letter data
                double canvasFontSize = cHeight * 0.75; // fallback
                string fontName = "Segoe UI"; // fallback
                var firstWord = lineWords.First().Word;
                try
                {
                    if (firstWord.Letters.Count > 0)
                    {
                        var letter = firstWord.Letters[0];
                        double pdfFontPts = letter.FontSize;
                        canvasFontSize = pdfFontPts * syInv;

                        // Try to get font name from letter
                        string? rawFont = null;
                        try { rawFont = letter.FontName; } catch { }
                        if (string.IsNullOrEmpty(rawFont))
                        {
                            // Some PdfPig versions use different property paths
                            try { rawFont = firstWord.FontName; } catch { }
                        }
                        if (!string.IsNullOrEmpty(rawFont))
                        {
                            string fontStr = rawFont!;
                            // Strip PDF subset prefix (e.g. "ABCDEF+FontName" -> "FontName")
                            if (fontStr.Contains('+'))
                                fontStr = fontStr[(fontStr.IndexOf('+') + 1)..];
                            // Clean common suffixes
                            fontStr = fontStr.Replace(",Bold", "").Replace(",Italic", "")
                                             .Replace("-Bold", "").Replace("-Italic", "")
                                             .Replace("-Roman", "").Replace("-Regular", "");
                            if (!string.IsNullOrWhiteSpace(fontStr))
                                fontName = fontStr;
                        }
                    }
                }
                catch { /* use fallbacks */ }

                // Show editable TextBox over the line
                var tb = new TextBox
                {
                    Text = lineText,
                    Background = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)),
                    Foreground = Brushes.Black,
                    BorderBrush = (SolidColorBrush)FindResource("Accent"), SelectionBrush = AccentBrush(),
                    BorderThickness = new Thickness(2),
                    FontFamily = new FontFamily(fontName),
                    FontSize = Math.Max(canvasFontSize, 10),
                    MinWidth = Math.Max(cWidth + 20, 100),
                    Height = Math.Max(cHeight + 12, 24),
                    Padding = new Thickness(2, 0, 2, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    AcceptsReturn = false,
                    Tag = new TextEditContext
                    {
                        PageIndex = pageIdx,
                        OriginalText = lineText,
                        CanvasBounds = new Rect(cLeft, cTop, cWidth, cHeight),
                        Position = new Point(cLeft, cTop),
                        FontSize = Math.Max(canvasFontSize, 10),
                        FontName = fontName
                    }
                };
                Canvas.SetLeft(tb, cLeft);
                Canvas.SetTop(tb, cTop);
                _activeCanvas.Children.Add(tb);
                _activeTextBox = tb;

                // Show white-out behind the edit box so original text is hidden
                var whiteout = new Rectangle
                {
                    Fill = Brushes.White,
                    Width = cWidth + 4,
                    Height = cHeight + 4,
                    IsHitTestVisible = false,
                    Tag = "EditWhiteout"
                };
                Canvas.SetLeft(whiteout, cLeft - 2);
                Canvas.SetTop(whiteout, cTop - 2);
                int tbIdx = _activeCanvas.Children.IndexOf(tb);
                _activeCanvas.Children.Insert(tbIdx, whiteout);

                tb.KeyDown += EditTextBox_KeyDown;
                tb.Loaded += (s, ev) =>
                {
                    tb.Focus();
                    Keyboard.Focus(tb);
                    tb.SelectAll();
                    tb.LostFocus += EditTextBox_LostFocus;
                };

                SetStatus("Editing text - Enter to save, Escape to cancel");
            }
            catch (Exception ex)
            {
                SetStatus($"Text edit error: {ex.Message}");
            }
        }

        /// <summary>Context data attached to an inline text edit TextBox via Tag.</summary>
        private class TextEditContext
        {
            public int PageIndex { get; set; }
            public string OriginalText { get; set; } = "";
            public Rect CanvasBounds { get; set; }
            public Point Position { get; set; }
            public double FontSize { get; set; }
            public string FontName { get; set; } = "Segoe UI";
            /// <summary>Non-null when re-editing an already-committed annotation; update in place instead of adding a new one.</summary>
            public TextEditAnnotation? ExistingAnnotation { get; set; }
        }

        private void EditTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CancelTextEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                CommitTextEdit();
                e.Handled = true;
            }
        }

        private void EditTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_activeTextBox is not null && _activeTextBox.Tag is TextEditContext)
            {
                Dispatcher.BeginInvoke(new Action(CommitTextEdit),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void CancelTextEdit()
        {
            if (_activeTextBox is null) return;
            var tb = _activeTextBox;
            _activeTextBox = null;
            _activeCanvas.Children.Remove(tb);
            // Remove the whiteout rectangle
            var whiteout = _activeCanvas.Children.OfType<Rectangle>()
                .FirstOrDefault(r => r.Tag is string s && s == "EditWhiteout");
            if (whiteout is not null)
                _activeCanvas.Children.Remove(whiteout);
            SetStatus("Text edit cancelled");
        }

        private void CommitTextEdit()
        {
            if (_activeTextBox is null || _activeTextBox.Tag is not TextEditContext ctx) return;
            var tb = _activeTextBox;
            _activeTextBox = null;
            string newText = tb.Text.Trim();
            _activeCanvas.Children.Remove(tb);

            // Remove the whiteout rectangle
            var whiteout = _activeCanvas.Children.OfType<Rectangle>()
                .FirstOrDefault(r => r.Tag is string s && s == "EditWhiteout");
            if (whiteout is not null)
                _activeCanvas.Children.Remove(whiteout);

            if (string.IsNullOrEmpty(newText) || newText == ctx.OriginalText)
            {
                SetStatus(newText == ctx.OriginalText ? "No changes made" : "Text edit cancelled (empty)");
                return;
            }

            if (ctx.ExistingAnnotation is not null)
            {
                // Update the existing annotation in place — avoids duplicate whiteout layers
                ctx.ExistingAnnotation.NewContent = newText;
            }
            else
            {
                var edit = new TextEditAnnotation
                {
                    PageIndex = ctx.PageIndex,
                    OriginalBounds = ctx.CanvasBounds,
                    Position = ctx.Position,
                    NewContent = newText,
                    OriginalContent = ctx.OriginalText,
                    FontSize = ctx.FontSize,
                    FontName = ctx.FontName
                };
                AddAnnotation(edit);
            }
            RenderAllAnnotations(ctx.PageIndex);
            SetStatus($"Text edited: \"{ctx.OriginalText}\" -> \"{newText}\"");
        }

        // ============================================================
        // Text box handling
        // ============================================================

        private void PlaceTextBox(Point pos, int pageIdx)
        {
            // _textFontSize is a point size; convert to the page's canvas (render-dim) units so
            // it renders and exports as real points. DrawAnnotationsOnDocument multiplies by
            // sy = page.Height.Point / renderH, so dividing by sy here makes "14" export as 14pt.
            double fontCanvas = _textFontSize;
            if (_doc is not null && _renderDims.TryGetValue(pageIdx, out var rdims) && rdims.h > 0)
            {
                double sy = _doc.Pages[pageIdx].Height.Point / rdims.h;
                if (sy > 0) fontCanvas = _textFontSize / sy;
            }
            var tb = new TextBox
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
                Foreground = new SolidColorBrush(_textColor),
                BorderBrush = (SolidColorBrush)FindResource("Accent"), SelectionBrush = AccentBrush(),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = fontCanvas,
                MinWidth = 120,
                MinHeight = 24,
                Padding = new Thickness(2),
                AcceptsReturn = true,
                Tag = pageIdx
            };
            Canvas.SetLeft(tb, pos.X);
            Canvas.SetTop(tb, pos.Y);
            _activeCanvas.Children.Add(tb);
            _activeTextBox = tb;
            tb.KeyDown += TextBox_KeyDown;
            // Defer focus until the TextBox is actually rendered
            tb.Loaded += (s, e) =>
            {
                tb.Focus();
                Keyboard.Focus(tb);
                tb.LostFocus += TextBox_LostFocus;
            };
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (_activeTextBox is not null)
                {
                    _activeCanvas.Children.Remove(_activeTextBox);
                    _activeTextBox = null;
                }
                if (_reeditOriginal is not null)
                {
                    int rp = _reeditOriginal.PageIndex;
                    if (!_annotations.TryGetValue(rp, out var rlist)) { rlist = []; _annotations[rp] = rlist; }
                    rlist.Add(_reeditOriginal);
                    _reeditOriginal = null;
                    RenderAllAnnotations(rp);
                }
                if (_currentTool != EditTool.Text) HideTextSettings();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                CommitActiveTextBox();
                e.Handled = true;
            }
        }

        private void TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // Only commit if the TextBox actually has content
            if (_activeTextBox is not null && !string.IsNullOrWhiteSpace(_activeTextBox.Text))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Keep the edit box open if focus moved into the size/color bar so the
                    // user can restyle (the Size ComboBox takes focus; color swatches do not).
                    if (_textSettingsBar is not null && Keyboard.FocusedElement is DependencyObject fe
                        && IsDescendantOf(fe, _textSettingsBar))
                        return;
                    CommitActiveTextBox();
                }),
                System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void CommitActiveTextBox()
        {
            if (_activeTextBox is null) return;
            // If it's an inline text edit, use the dedicated commit path
            if (_activeTextBox.Tag is TextEditContext)
            {
                CommitTextEdit();
                return;
            }
            var tb = _activeTextBox;
            _activeTextBox = null;
            _reeditOriginal = null;   // committing replaces any annotation being re-edited

            string content = tb.Text.Trim();
            int pageIdx = tb.Tag is int idx ? idx : PageList.SelectedIndex;
            double x = Canvas.GetLeft(tb);
            double y = Canvas.GetTop(tb);

            _activeCanvas.Children.Remove(tb);

            if (!string.IsNullOrEmpty(content))
            {
                var ta = new TextAnnotation
                {
                    PageIndex = pageIdx,
                    Position = new Point(x, y),
                    Content = content,
                    FontSize = tb.FontSize
                };
                ta.SetColor(tb.Foreground is SolidColorBrush scb ? scb.Color : Colors.Black);
                AddAnnotation(ta);
                RenderTextAnnotation(ta);
            }
            if (_currentTool != EditTool.Text) HideTextSettings();
        }

        // ============================================================
        // Keyboard shortcuts
        // ============================================================

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            // Don't intercept keys when typing in any TextBox (typewriter tool or form field)
            if (e.OriginalSource is TextBox) return;
            if (_activeTextBox is not null && _activeTextBox.IsFocused) return;

            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CopySelectedText();
                e.Handled = true;
            }
            else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SelectAllText();
                e.Handled = true;
            }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ToggleSearchBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && _currentTool == EditTool.Crop && _cropConfirmBar is not null)
            {
                ApplyCrop([PageList.SelectedIndex]);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _currentTool == EditTool.Crop && _cropConfirmBar is not null)
            {
                HideCropConfirmBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && ShortcutOverlay.Visibility == Visibility.Visible)
            {
                ShortcutOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _searchBar is not null && _searchBar.Visibility == Visibility.Visible)
            {
                CloseSearchBar();
                e.Handled = true;
            }
            else if (e.Key == Key.OemQuestion && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ShortcutOverlay.Visibility = ShortcutOverlay.Visibility == Visibility.Visible
                    ? Visibility.Collapsed : Visibility.Visible;
                e.Handled = true;
            }
            else if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Print_Click(this, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && _selectedAnnotation is not null)
            {
                DeleteSelected();
                e.Handled = true;
            }
            else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Undo_Click(this, e);
                e.Handled = true;
            }
            else if (e.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                SaveAs_Click(this, e);
                e.Handled = true;
            }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SaveInPlace();
                e.Handled = true;
            }
            else if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CloseFile();
                e.Handled = true;
            }
            else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Open_Click(this, e);
                e.Handled = true;
            }
            else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
            {
                NewDocument();
                e.Handled = true;
            }
            else if ((e.Key == Key.Left || e.Key == Key.Up) && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (_doc is not null && PageList.SelectedIndex > 0)
                {
                    PageList.SelectedIndex--;
                    e.Handled = true;
                }
            }
            else if ((e.Key == Key.Right || e.Key == Key.Down) && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (_doc is not null && PageList.SelectedIndex < _doc.PageCount - 1)
                {
                    PageList.SelectedIndex++;
                    e.Handled = true;
                }
            }
            else if ((e.Key == Key.OemPlus || e.Key == Key.Add) && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (_viewMode == ViewMode.Grid) GridZoomStep(false); else SetZoom(_zoomLevel + ZoomStep);
                e.Handled = true;
            }
            else if ((e.Key == Key.OemMinus || e.Key == Key.Subtract) && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (_viewMode == ViewMode.Grid) GridZoomStep(true); else SetZoom(_zoomLevel - ZoomStep);
                e.Handled = true;
            }
            else if (e.Key == Key.D0 && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SetZoom(1.0);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                // No overlay active — ESC exits the app
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Space && !_spaceHeld)
            {
                _spaceHeld = true;
                PagePreviewPanel.Cursor = Cursors.Hand;
                e.Handled = true;
            }
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            base.OnPreviewKeyUp(e);
            if (e.Key == Key.Space && _spaceHeld)
            {
                _spaceHeld = false;
                if (!_isPanning)
                    PagePreviewPanel.Cursor = Cursors.Arrow;
                e.Handled = true;
            }
        }

        // ============================================================
        // Annotation management
        // ============================================================

        private void AddAnnotation(PageAnnotation annotation)
        {
            if (!_annotations.ContainsKey(annotation.PageIndex))
                _annotations[annotation.PageIndex] = [];
            _annotations[annotation.PageIndex].Add(annotation);
            _undoStack.Push(new UndoEntry(UndoKind.Annotation, annotation.PageIndex, WasDirty: _isDirty));
            MarkDirty();
        }

        /// <summary>
        /// Saves the current in-memory document bytes onto the undo stack so that
        /// document-level operations (crop, delete page, merge, reorder) can be undone.
        /// Must be called BEFORE modifying _doc.
        /// </summary>
        private void PushDocUndo()
        {
            if (_doc is null) return;
            using var ms = new System.IO.MemoryStream();
            _doc.Save(ms);
            _undoStack.Push(new UndoEntry(UndoKind.Document, DocBytes: ms.ToArray(), WasDirty: _isDirty));
        }

        private void RenderTextAnnotation(TextAnnotation ta)
        {
            var tb = new TextBlock
            {
                Text = ta.Content,
                Foreground = new SolidColorBrush(ta.GetColor()),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = ta.FontSize,
                Padding = new Thickness(2)
            };
            Canvas.SetLeft(tb, ta.Position.X);
            Canvas.SetTop(tb, ta.Position.Y);
            _activeCanvas.Children.Add(tb);
        }

        private void RenderAllAnnotations(int pageIndex)
        {
            // Resolve this page's annotation surface from the unified per-page overlay map, which
            // every multi-page view populates; fall back to the single-page canvas. View-mode
            // independent on purpose so the tools behave identically in all four modes.
            _activeCanvas = _continuousCanvases.TryGetValue(pageIndex, out var pageCanvas)
                ? pageCanvas : _annotationCanvas;
            _activeCanvas.Children.Clear();

            if (_annotations.TryGetValue(pageIndex, out var annotList))
            foreach (var annot in annotList)
            {
                switch (annot)
                {
                    case TextAnnotation ta:
                        RenderTextAnnotation(ta);
                        break;
                    case HighlightAnnotation ha:
                        var rect = new Rectangle
                        {
                            Fill = new SolidColorBrush(ha.GetColor()),
                            Width = ha.Bounds.Width,
                            Height = ha.Bounds.Height
                        };
                        Canvas.SetLeft(rect, ha.Bounds.X);
                        Canvas.SetTop(rect, ha.Bounds.Y);
                        _activeCanvas.Children.Add(rect);
                        break;
                    case InkAnnotation ia:
                        if (ia.Points.Count < 2) continue;
                        var poly = new Polyline
                        {
                            Stroke = new SolidColorBrush(ia.GetColor()),
                            StrokeThickness = ia.StrokeWidth,
                            StrokeLineJoin = PenLineJoin.Round,
                            StrokeStartLineCap = PenLineCap.Round,
                            StrokeEndLineCap = PenLineCap.Round
                        };
                        foreach (var pt in ia.Points) poly.Points.Add(pt);
                        _activeCanvas.Children.Add(poly);
                        break;
                    case TextEditAnnotation tea:
                        // White-out original text
                        var wo = new Rectangle
                        {
                            Fill = Brushes.White,
                            Width = tea.OriginalBounds.Width + 4,
                            Height = tea.OriginalBounds.Height + 4,
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(wo, tea.OriginalBounds.X - 2);
                        Canvas.SetTop(wo, tea.OriginalBounds.Y - 2);
                        _activeCanvas.Children.Add(wo);
                        // Draw replacement text
                        var etb = new TextBlock
                        {
                            Text = tea.NewContent,
                            Foreground = Brushes.Black,
                            FontFamily = new FontFamily(tea.FontName),
                            FontSize = tea.FontSize,
                            Padding = new Thickness(0)
                        };
                        Canvas.SetLeft(etb, tea.Position.X);
                        Canvas.SetTop(etb, tea.Position.Y);
                        _activeCanvas.Children.Add(etb);
                        break;

                    case SignatureAnnotation sa:
                        if (sa.ImageData is not null)
                        {
                            // Image-based signature
                            try
                            {
                                var imgBytes = Convert.FromBase64String(sa.ImageData);
                                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                                bmp.BeginInit();
                                bmp.StreamSource = new System.IO.MemoryStream(imgBytes);
                                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                                bmp.EndInit();
                                var imgCtrl = new System.Windows.Controls.Image
                                {
                                    Source = bmp,
                                    Width = sa.SourceWidth * sa.Scale,
                                    Height = sa.SourceHeight * sa.Scale,
                                    Stretch = System.Windows.Media.Stretch.Uniform,
                                    IsHitTestVisible = false
                                };
                                Canvas.SetLeft(imgCtrl, sa.Position.X);
                                Canvas.SetTop(imgCtrl, sa.Position.Y);
                                _activeCanvas.Children.Add(imgCtrl);
                            }
                            catch { /* skip broken image */ }
                        }
                        else
                        {
                            foreach (var stroke in sa.Strokes)
                            {
                                if (stroke.Count < 2) continue;
                                var sigPoly = new Polyline
                                {
                                    Stroke = Brushes.Black,
                                    StrokeThickness = 2 * sa.Scale,
                                    StrokeLineJoin = PenLineJoin.Round,
                                    StrokeStartLineCap = PenLineCap.Round,
                                    StrokeEndLineCap = PenLineCap.Round
                                };
                                foreach (var pt in stroke)
                                    sigPoly.Points.Add(new Point(
                                        sa.Position.X + pt.X * sa.Scale,
                                        sa.Position.Y + pt.Y * sa.Scale));
                                _activeCanvas.Children.Add(sigPoly);
                            }
                        }
                        break;

                    case ImageAnnotation ia:
                        try
                        {
                            var iaBytes = Convert.FromBase64String(ia.ImageData);
                            var iaBmp = new System.Windows.Media.Imaging.BitmapImage();
                            iaBmp.BeginInit();
                            iaBmp.StreamSource = new System.IO.MemoryStream(iaBytes);
                            iaBmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            iaBmp.EndInit();
                            var iaCtrl = new System.Windows.Controls.Image
                            {
                                Source = iaBmp,
                                Width = ia.SourceWidth * ia.Scale,
                                Height = ia.SourceHeight * ia.Scale,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                IsHitTestVisible = false
                            };
                            Canvas.SetLeft(iaCtrl, ia.Position.X);
                            Canvas.SetTop(iaCtrl, ia.Position.Y);
                            _activeCanvas.Children.Add(iaCtrl);
                        }
                        catch { /* skip broken image */ }
                        break;
                }
            }

            // Re-add form field overlays — RenderAllAnnotations clears the canvas so they must be restored.
            if (_renderDims.TryGetValue(pageIndex, out var dims))
                RenderFormFields(pageIndex, dims.w, dims.h);
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (_undoStack.Count == 0)
            {
                SetStatus("Nothing to undo");
                return;
            }

            var entry = _undoStack.Pop();

            if (entry.Kind == UndoKind.Annotation)
            {
                int pageIdx = entry.PageIdx;
                if (_annotations.ContainsKey(pageIdx) && _annotations[pageIdx].Count > 0)
                    _annotations[pageIdx].RemoveAt(_annotations[pageIdx].Count - 1);
                ClearSelection();
                RenderAllAnnotations(pageIdx);
                MarkDirty(entry.WasDirty);
                SetStatus("Undid last annotation");
            }
            else // Document snapshot
            {
                if (entry.DocBytes is null) return;
                int selectedIdx = PageList.SelectedIndex;
                var tempPath = App.MakeTempFile("undo");
                System.IO.File.WriteAllBytes(tempPath, entry.DocBytes);
                _doc?.Close();
                // PdfSharpCore can write a snapshot whose xref offset points at the xref table,
                // producing "Unexpected token 'xref'" on reopen. Repair via Import (preserves
                // rotations) then PDFium, mirroring the save/reload path, instead of crashing.
                try
                {
                    _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
                }
                catch (Exception undoOpenEx) when (IsXRefException(undoOpenEx))
                {
                    var fixedPath = App.MakeTempFile("undofixed");
                    if (!TryImportRepairToPath(tempPath, fixedPath)
                        && !TryPdfiumSaveWithZeroRotations(tempPath, fixedPath))
                        throw;
                    tempPath = fixedPath;
                    _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
                }
                _currentFile = tempPath;
                _annotations.Clear();
                _renderDims.Clear();
                ClearSelection();
                MarkDirty(entry.WasDirty);
                RefreshPageList();
                if (selectedIdx >= 0 && selectedIdx < PageList.Items.Count)
                    PageList.SelectedIndex = selectedIdx;
                else if (PageList.Items.Count > 0)
                    PageList.SelectedIndex = 0;
                // Re-render the current view so the main page(s) reflect the restored document.
                // RefreshPageList only updates the sidebar, and re-selecting the same page does not
                // fire SelectionChanged, so grid/two-page tiles would otherwise stay stale.
                int reIdx = PageList.SelectedIndex;
                if (_viewMode == ViewMode.Continuous)
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                        (Action)(() => SetupContinuousView(reIdx)));
                else
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
                    {
                        RenderPage(_viewMode == ViewMode.Grid ? 0 : reIdx);
                        ReapplyGridOrFit();
                    }));
                SetStatus("Undid document change");
            }
        }

        private void ClearAnnotations_Click(object sender, RoutedEventArgs e)
        {
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;
            if (_annotations.ContainsKey(pageIdx) && _annotations[pageIdx].Count > 0)
            {
                _annotations[pageIdx].Clear();
                MarkDirty();
            }
            ClearSelection();
            _annotationCanvas.Children.Clear();
            SetStatus("Cleared annotations on this page");
        }

        // ============================================================
        // Dirty / unsaved-change tracking
        // ============================================================

        private void MarkDirty(bool dirty = true)
        {
            _isDirty = dirty;
            if (_saveAsBtnRef != null)
            {
                _saveAsBtnRef.Foreground = dirty
                    ? new SolidColorBrush(Color.FromRgb(0xff, 0xa5, 0x00)) // orange = unsaved
                    : (SolidColorBrush)FindResource("Accent");
            }
        }

        // ============================================================
        // Close file (Ctrl+W) — returns to drop-zone state
        // ============================================================

        private void CloseFile()
        {
            if (_doc is null) return;
            if (_isDirty)
            {
                var res = KillerDialog.Show(this,
                    Loc("Str_Dlg_UnsavedClose"),
                    "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
            }
            _doc.Close();
            _doc = null;
            _currentFile = null;
            _activeTextBox = null;   // cancel any in-progress typewriter edit before canvas clear
            _annotations.Clear();
            _undoStack.Clear();
            _renderDims.Clear();
            _formTextValues.Clear();
            _formCheckValues.Clear();
            _formRadioValues.Clear();
            _allSearchRects.Clear();
            _searchResultPages.Clear();
            _searchPageCursor = -1;
            _thumbCts?.Cancel();
            PageList.ItemsSource = null;
            if (FindName("PageImage") is System.Windows.Controls.Image img) img.Source = null;
            _annotationCanvas.Children.Clear();
            FileNameLabel.Text = "";
            DropZone.Visibility = Visibility.Visible;
            PagePreviewPanel.Visibility = Visibility.Collapsed;
            CloseSearchBar();
            HideDrawSettings();
            HideTextSettings();
            HideSignaturePopup();
            SetTool(EditTool.Select);
            if (_closeFileBtnRef != null) _closeFileBtnRef.IsEnabled = false;
            _pageJumpBox.IsEnabled = false;
            _continuousRenderCts?.Cancel();
            _continuousPanel.Children.Clear();
            _continuousTops.Clear();
            _pageJumpBox.Text = "";
            _pageTotalLabel.Text = "/ –";
            OutlineTree.Items.Clear();
            SidebarOutlinesTab.IsEnabled = false;
            if (_sidebarShowingOutlines) SwitchSidebarToPagesTab();
            MarkDirty(false);
            SetStatus("Ready");
        }

        private void CloseFile_Click(object sender, RoutedEventArgs e) => CloseFile();

        // ============================================================
        // File toolbar handlers
        // ============================================================

        private void New_Click(object sender, RoutedEventArgs e) => NewDocument();

        private void NewDocument()
        {
            if (_isDirty)
            {
                var res = KillerDialog.Show(this,
                    "You have unsaved changes. Discard them and create a new document?",
                    "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
            }

            try
            {
                var newDoc = new PdfDocument();
                newDoc.AddPage(); // one blank A4 page

                var tempPath = App.MakeTempFile("new");
                newDoc.Save(tempPath);
                newDoc.Close();

                _doc?.Close();
                _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
                FinishOpenFile("Untitled.pdf", tempPath);
                SetStatus("New blank document");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Could not create new document:\n{ex.Message}",
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "PDF files|*.pdf", Title = "Open PDF" };
            if (dlg.ShowDialog(this) == true) OpenFile(dlg.FileName);
        }

        private void Merge_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            var doc = _doc;
            var dlg = new OpenFileDialog { Filter = "PDF files|*.pdf", Title = "Select PDF to merge", Multiselect = true };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                foreach (var file in dlg.FileNames)
                {
                    int pageOffset = doc.PageCount;

                    // Open twice: Import mode for AddPage, ReadOnly for catalog access.
                    using var srcRead = PdfReader.Open(file, PdfDocumentOpenMode.ReadOnly);
                    var namedDestMap = BuildNamedDestMap(srcRead);

                    using var src = PdfReader.Open(file, PdfDocumentOpenMode.Import);
                    for (int i = 0; i < src.PageCount; i++)
                        doc.AddPage(src.Pages[i]);

                    // Rewrite named-destination links in the newly added pages so they
                    // resolve correctly after the catalog is not imported.
                    if (namedDestMap.Count > 0)
                        RewriteNamedDestLinks(doc, pageOffset, namedDestMap);
                }
                SaveTempAndReload();
                SetStatus($"Merged {dlg.FileNames.Length} file(s) - {_doc?.PageCount} total pages");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Merge failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Builds a map of named destination string → 0-based page index from a source document's
        /// /Dests dictionary and /Names /Dests name tree.
        /// </summary>
        private Dictionary<string, int> BuildNamedDestMap(PdfDocument src)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                var catalog = src.Internals.Catalog;

                // Legacy flat /Dests dictionary
                var destsDict = catalog.Elements.GetDictionary("/Dests");
                if (destsDict != null)
                {
                    foreach (var key in destsDict.Elements.Keys)
                    {
                        PdfItem? val = DerefItem(destsDict.Elements[key] ?? new PdfInteger(-1));
                        int? idx = ResolveDestPageIndexInDoc(src, val);
                        if (idx.HasValue) map[key.TrimStart('/')] = idx.Value;
                    }
                }

                // Modern /Names /Dests name tree
                var namesDict = catalog.Elements.GetDictionary("/Names");
                var destTree  = namesDict?.Elements.GetDictionary("/Dests");
                if (destTree != null)
                    WalkNameTree(src, destTree, map);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"BuildNamedDestMap: {ex}"); }
            return map;
        }

        private void WalkNameTree(PdfDocument src, PdfDictionary node, Dictionary<string, int> map)
        {
            var namesArr = node.Elements.GetArray("/Names");
            if (namesArr != null)
            {
                for (int i = 0; i + 1 < namesArr.Elements.Count; i += 2)
                {
                    var keyItem = namesArr.Elements[i];
                    string key  = keyItem is PdfString ks ? ks.Value : keyItem?.ToString()?.TrimStart('/') ?? "";
                    if (string.IsNullOrEmpty(key)) continue;
                    PdfItem? val = DerefItem(namesArr.Elements[i + 1]);
                    int? idx = ResolveDestPageIndexInDoc(src, val);
                    if (idx.HasValue) map[key] = idx.Value;
                }
            }

            var kids = node.Elements.GetArray("/Kids");
            if (kids != null)
            {
                for (int i = 0; i < kids.Elements.Count; i++)
                {
                    if (DerefItem(kids.Elements[i]) is PdfDictionary kid)
                        WalkNameTree(src, kid, map);
                }
            }
        }

        /// <summary>
        /// Resolves a destination value (PdfArray or PdfDictionary with /D) to a page index
        /// within the given source document by matching the page object number.
        /// </summary>
        private static int? ResolveDestPageIndexInDoc(PdfDocument src, PdfItem? val)
        {
            PdfArray? arr = val as PdfArray;
            if (arr is null && val is PdfDictionary vd)
                arr = vd.Elements.GetArray("/D");
            if (arr is null || arr.Elements.Count == 0) return null;

            var first = arr.Elements[0];
            int objNum = GetObjectNumber(first);
            if (objNum > 0)
            {
                for (int i = 0; i < src.PageCount; i++)
                {
                    var pgRef = src.Pages[i].Reference;
                    if (pgRef != null && pgRef.ObjectNumber == objNum) return i;
                }
            }
            else if (first is PdfInteger pi && pi.Value >= 0 && pi.Value < src.PageCount)
            {
                return pi.Value;
            }
            return null;
        }

        /// <summary>
        /// Walks all link annotations in pages [pageOffset, doc.PageCount) and rewrites any
        /// named-destination /D values to explicit [pageRef /Fit] arrays using the merged
        /// document's page references. This is needed because PdfSharpCore's import does not
        /// copy the source document's /Names /Dests catalog entries.
        /// </summary>
        private static void RewriteNamedDestLinks(PdfDocument doc, int pageOffset,
            Dictionary<string, int> namedDestMap)
        {
            for (int pi = pageOffset; pi < doc.PageCount; pi++)
            {
                try
                {
                    var page    = doc.Pages[pi];
                    var annotsArr = page.Elements.GetArray("/Annots");
                    if (annotsArr is null) continue;

                    for (int ai = 0; ai < annotsArr.Elements.Count; ai++)
                    {
                        PdfItem? elem = annotsArr.Elements[ai];
                        PdfDictionary? ann = elem as PdfDictionary
                            ?? (DerefItemStatic(elem) as PdfDictionary);
                        if (ann is null) continue;

                        var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                        if (!subtype.Contains("Link")) continue;

                        // Check /A /D (GoTo action)
                        var actionDict = ann.Elements.GetDictionary("/A");
                        if (actionDict != null)
                        {
                            var s = actionDict.Elements["/S"]?.ToString() ?? "";
                            if (s.Contains("GoTo"))
                            {
                                var destItem = actionDict.Elements["/D"];
                                string? name = ExtractDestName(destItem);
                                if (name != null && namedDestMap.TryGetValue(name, out int srcIdx))
                                {
                                    int targetIdx = pageOffset + srcIdx;
                                    if (targetIdx < doc.PageCount)
                                        actionDict.Elements["/D"] = MakeExplicitDest(doc, targetIdx);
                                }
                            }
                        }
                        else
                        {
                            // Bare /Dest on annotation
                            var destItem = ann.Elements["/Dest"];
                            string? name = ExtractDestName(destItem);
                            if (name != null && namedDestMap.TryGetValue(name, out int srcIdx))
                            {
                                int targetIdx = pageOffset + srcIdx;
                                if (targetIdx < doc.PageCount)
                                    ann.Elements["/Dest"] = MakeExplicitDest(doc, targetIdx);
                            }
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RewriteNamedDestLinks p{pi}: {ex}"); }
            }
        }

        private static string? ExtractDestName(PdfItem? item)
        {
            if (item is null) return null;
            if (item is PdfString ps) return ps.Value;
            if (item is PdfName   pn) return pn.Value.TrimStart('/');
            return null;
        }

        private static PdfArray MakeExplicitDest(PdfDocument doc, int pageIndex)
        {
            var arr = new PdfArray(doc);
            arr.Elements.Add(doc.Pages[pageIndex].Reference);
            arr.Elements.Add(new PdfName("/Fit"));
            return arr;
        }

        // Static version of DerefItem for use in static helpers.
        private static PdfItem DerefItemStatic(PdfItem item)
        {
            var valueProp = item.GetType().GetProperty("Value",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (valueProp?.GetValue(item) is PdfObject resolved) return resolved;
            return item;
        }

        private void Split_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            var currentFile = _currentFile;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) { KillerDialog.Show(this, "Select pages to extract."); return; }
            var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", Title = "Save extracted pages as",
                                           CheckFileExists = false, CheckPathExists = true };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var indices = new List<int>();
                foreach (PageThumbnailVm vm in selected) indices.Add(vm.PageIndex);
                using var importDoc = PdfReader.Open(currentFile, PdfDocumentOpenMode.Import);
                var newDoc = new PdfDocument();
                foreach (var idx in indices.OrderBy(i => i))
                    newDoc.AddPage(importDoc.Pages[idx]);
                newDoc.Save(dlg.FileName);
                SetStatus(string.Format(Loc("Str_Extracted"), indices.Count, System.IO.Path.GetFileName(dlg.FileName)));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Split failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            var doc = _doc;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) { KillerDialog.Show(this, "Select pages to delete."); return; }
            var result = KillerDialog.Show(this, $"Delete {selected.Count} {(selected.Count == 1 ? "page" : "pages")}?", "KillerPDF",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            try
            {
                var indices = new List<int>();
                foreach (PageThumbnailVm vm in selected) indices.Add(vm.PageIndex);
                foreach (var idx in indices.OrderByDescending(i => i))
                    doc.Pages.RemoveAt(idx);
                SaveTempAndReload();
                SetStatus(string.Format(Loc("Str_Deleted"), indices.Count, _doc?.PageCount));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Delete failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InsertBlankPage_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            var doc = _doc;
            int insertAfter = PageList.SelectedIndex >= 0 ? PageList.SelectedIndex : doc.PageCount - 1;
            try
            {
                var blank = new PdfPage { Width = XUnit.FromPoint(595), Height = XUnit.FromPoint(842) };
                doc.Pages.Insert(insertAfter + 1, blank);
                SaveTempAndReload();
                PageList.SelectedIndex = insertAfter + 1;
                SetStatus($"Inserted blank page at position {insertAfter + 2}");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Insert failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || PageList.SelectedIndex <= 0) return;
            var doc = _doc;
            int idx = PageList.SelectedIndex;
            var page = doc.Pages[idx];
            doc.Pages.RemoveAt(idx);
            doc.Pages.Insert(idx - 1, page);
            SaveTempAndReload();
            PageList.SelectedIndex = idx - 1;
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || PageList.SelectedIndex < 0 || PageList.SelectedIndex >= _doc.PageCount - 1) return;
            var doc = _doc;
            int idx = PageList.SelectedIndex;
            var page = doc.Pages[idx];
            doc.Pages.RemoveAt(idx);
            doc.Pages.Insert(idx + 1, page);
            SaveTempAndReload();
            PageList.SelectedIndex = idx + 1;
        }

        private void SaveInPlace()
        {
            if (_doc is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            // Save back to the user's real file. After a page edit (crop/rotate) _currentFile is a
            // temp working copy, so the real path is kept in _originalFile. If there is no real path
            // (e.g. a repaired temp-backed open), fall back to Save As.
            if (string.IsNullOrEmpty(_originalFile)) { SaveAs_Click(this, new RoutedEventArgs()); return; }
            CommitActiveTextBox();
            string saveTarget = _originalFile!;
            try
            {
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
                WriteFormValuesToDocument();
                // Always strip link annotation borders regardless of user annotation count
                // so mailto/URI links don't appear as strikethrough lines in other viewers.
                StripLinkAnnotationBorders(_doc);

                if (hasAnnotations)
                {
                    // Save a clean copy of the doc (without burned annotations), burn
                    // annotations into the real file, then restore the in-memory doc
                    // from the clean copy so future saves don't double-burn.
                    var tempClean = App.MakeTempFile("clean");
                    _doc.Save(tempClean);
                    DrawAnnotationsOnDocument();
                    _doc.Save(saveTarget);
                    _doc.Close();
                    _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                    _currentFile = tempClean;
                }
                else
                {
                    _doc.Save(saveTarget);
                }

                MarkDirty(false);
                SetStatus($"Saved - {System.IO.Path.GetFileName(saveTarget)}");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Save failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            // No real path yet (repaired temp-backed open) -> go straight to Save As.
            if (string.IsNullOrEmpty(_originalFile)) { SaveAs_Click(sender, e); return; }
            var name = System.IO.Path.GetFileName(_originalFile);
            var choice = KillerDialog.Show(this, $"Overwrite {name}?", "Save",
                                           MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Yes)      SaveInPlace();
            else if (choice == MessageBoxResult.No)  SaveAs_Click(sender, e);
            // Cancel or closed: do nothing.
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            CommitActiveTextBox();
            var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", Title = "Save PDF as",
                                           CheckFileExists = false, CheckPathExists = true };
            string? seed = _originalFile ?? _currentFile;
            if (!string.IsNullOrEmpty(seed))
            {
                dlg.FileName = System.IO.Path.GetFileName(seed);
                var seedDir = System.IO.Path.GetDirectoryName(_originalFile ?? "");
                if (!string.IsNullOrEmpty(seedDir) && System.IO.Directory.Exists(seedDir))
                    dlg.InitialDirectory = seedDir;
            }
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
                WriteFormValuesToDocument();
                // Always strip link annotation borders regardless of user annotation count.
                StripLinkAnnotationBorders(_doc);

                if (hasAnnotations)
                {
                    var tempClean = App.MakeTempFile("clean");
                    _doc.Save(tempClean);
                    DrawAnnotationsOnDocument();
                    _doc.Save(dlg.FileName);
                    _doc.Close();
                    _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                    _currentFile = tempClean;
                    _originalFile = dlg.FileName;
                    FileNameLabel.Text = System.IO.Path.GetFileName(dlg.FileName);
                    MarkDirty(false);
                    SetStatus($"Saved with annotations to {System.IO.Path.GetFileName(dlg.FileName)}");
                }
                else
                {
                    _doc.Save(dlg.FileName);
                    _originalFile = dlg.FileName;
                    FileNameLabel.Text = System.IO.Path.GetFileName(dlg.FileName);
                    MarkDirty(false);
                    SetStatus($"Saved to {System.IO.Path.GetFileName(dlg.FileName)}");
                }
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Save failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void SaveFlattened_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            CommitActiveTextBox();
            var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", Title = "Save Flattened PDF",
                                           CheckFileExists = false, CheckPathExists = true };
            if (dlg.ShowDialog(this) != true) return;

            // Burn any pending annotations into a temp source for rasterization
            // (must happen on UI thread before we go async)
            string sourcePath;
            bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
            if (hasAnnotations)
            {
                var tempClean  = App.MakeTempFile("clean");
                var tempBurned = App.MakeTempFile("burned");
                _doc.Save(tempClean);
                DrawAnnotationsOnDocument();
                _doc.Save(tempBurned);
                _doc.Close();
                _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                _currentFile = tempClean;
                sourcePath = tempBurned;
            }
            else
            {
                var temp = App.MakeTempFile("src");
                _doc.Save(temp);
                sourcePath = temp;
            }

            int pageCount = _doc.PageCount;

            // Snapshot per-page dimensions (CropBox-aware) before going off-thread
            var pageDims = new (double widthPt, double heightPt)[pageCount];
            for (int i = 0; i < pageCount; i++)
            {
                var p = _doc.Pages[i];
                pageDims[i] = (p.Width.Point, p.Height.Point);
            }

            // Show a progress overlay so the user knows we're working
            var overlay = ShowFlattenProgress(pageCount);
            string outputPath = dlg.FileName;

            try
            {
                // Rasterize on a background thread — keeps the UI responsive
                await Task.Run(() =>
                {
                    // Rasterize pages across CPU cores. Docnet/PDFium is not thread-safe, so the
                    // pdfium render is serialized behind a lock; the PNG encode (GDI+) runs in
                    // parallel. Pages are assembled into the PDF afterwards, in order.
                    var pngPages = new byte[pageCount][];
                    var docGate  = new object();
                    int done     = 0;
                    var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) };
                    Parallel.For(0, pageCount, po, i =>
                    {
                        // Per-page pixel dimensions at 150 DPI, sized to the CropBox
                        int pw = Math.Max(1, (int)(pageDims[i].widthPt  * 150 / 72.0));
                        int ph = Math.Max(1, (int)(pageDims[i].heightPt * 150 / 72.0));
                        // PageDimensions requires dimOne <= dimTwo (short-edge, long-edge)
                        int dimMin = Math.Min(pw, ph);
                        int dimMax = Math.Max(pw, ph);

                        byte[] bgra; int rw, rh;
                        lock (docGate)
                        {
                            using var pageDocReader = DocLib.Instance.GetDocReader(sourcePath, new PageDimensions(dimMin, dimMax));
                            using var pr = pageDocReader.GetPageReader(i);
                            bgra = pr.GetImage();
                            rw   = pr.GetPageWidth();
                            rh   = pr.GetPageHeight();
                        }
                        // Encode BGRA to PNG (GDI+) outside the lock so it parallelizes.
                        pngPages[i] = RenderToPng(bgra, rw, rh);

                        int n = System.Threading.Interlocked.Increment(ref done);
                        Dispatcher.BeginInvoke(new Action(() => UpdateFlattenProgress(overlay, n, pageCount)));
                    });

                    // Assemble the output PDF in page order (PdfSharp is single-threaded).
                    var outDoc = new PdfDocument();
                    try
                    {
                        for (int i = 0; i < pageCount; i++)
                        {
                            var newPage = outDoc.AddPage();
                            newPage.Width  = XUnit.FromPoint(pageDims[i].widthPt);
                            newPage.Height = XUnit.FromPoint(pageDims[i].heightPt);
                            using var xi  = XImage.FromStream(() => new MemoryStream(pngPages[i]));
                            using var gfx = XGraphics.FromPdfPage(newPage);
                            gfx.DrawImage(xi, 0, 0, newPage.Width.Point, newPage.Height.Point);
                        }
                        outDoc.Save(outputPath);
                    }
                    finally
                    {
                        outDoc.Dispose();
                    }
                });

                MarkDirty(false);
                SetStatus($"Flattened PDF saved to {System.IO.Path.GetFileName(outputPath)}");
            }
            catch (Exception ex)
            {
                try { KillerDialog.Show(this, $"Flatten failed:\n{ex.GetType().Name}: {ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error); }
                catch { /* dialog failed; overlay still removed in finally */ }
            }
            finally
            {
                try { HideFlattenProgress(overlay); } catch { /* ensure overlay never leaks */ }
            }
        }

        // ---- flatten progress overlay helpers ----

        private Border ShowFlattenProgress(int pageCount, string verb = "Flattening")
        {
            var progressText = new TextBlock
            {
                Text       = $"{verb} page 0 of {pageCount}...",
                Foreground = Brushes.White,
                FontSize   = 14,
                Tag        = verb   // stored so UpdateFlattenProgress can read it
            };
            var panel = new StackPanel
            {
                Orientation         = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            panel.Children.Add(progressText);

            var overlay = new Border
            {
                Background        = new SolidColorBrush(Color.FromArgb(200, 0x1a, 0x1a, 0x1a)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment   = VerticalAlignment.Stretch,
                Child             = panel,
                Tag               = "FlattenOverlay"
            };
            Panel.SetZIndex(overlay, 999);

            // Attach to the root grid
            if (Content is Grid rootGrid)
                rootGrid.Children.Add(overlay);

            return overlay;
        }

        private static void UpdateFlattenProgress(Border overlay, int current, int total)
        {
            if (overlay.Child is StackPanel panel)
                foreach (var child in panel.Children)
                    if (child is TextBlock tb && tb.Tag is string verb)
                        tb.Text = $"{verb} page {current} of {total}...";
        }

        private void HideFlattenProgress(Border overlay)
        {
            if (Content is Grid rootGrid)
                rootGrid.Children.Remove(overlay);
        }

        /// <summary>
        /// Encodes raw BGRA pixel data from pdfium to PNG without touching the UI thread.
        /// GDI+ Format32bppArgb is BGRA in memory — matches pdfium output exactly.
        /// </summary>
        private static byte[] RenderToPng(byte[] bgra, int width, int height)
        {
            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                using var bmp = new System.Drawing.Bitmap(
                    width, height, width * 4,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb,
                    pin.AddrOfPinnedObject());
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }
            finally { pin.Free(); }
        }

        private async void Print_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            CommitActiveTextBox();

            // Burn pending annotations into a temp copy on the UI thread before going off-thread
            bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
            string printPath;
            string? tempFlattened = null;
            if (hasAnnotations)
            {
                var tempClean = App.MakeTempFile("clean");
                _doc.Save(tempClean);
                DrawAnnotationsOnDocument();
                printPath = App.MakeTempFile("print");
                _doc.Save(printPath);
                tempFlattened = printPath;
                _doc.Close();
                _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                _currentFile = tempClean;
            }
            else
            {
                printPath = _currentFile;
            }

            int pageCount = _doc.PageCount;

            // Rasterize every page, then hand them to our own preview window. WPF's OS
            // PrintDialog cannot show a preview ("This app doesn't support print preview"),
            // so KillerPDF renders the preview and drives printing itself.
            var overlay = ShowFlattenProgress(pageCount, "Preparing");
            bool overlayHidden = false;
            byte[][]? pngPages = null;
            int[]?    rasterW  = null;
            int[]?    rasterH  = null;

            try
            {
                // Rasterize all pages on background thread — keeps UI responsive
                await Task.Run(() =>
                {
                    pngPages = new byte[pageCount][];
                    rasterW  = new int[pageCount];
                    rasterH  = new int[pageCount];
                    using var docReader = DocLib.Instance.GetDocReader(printPath, new PageDimensions(1536, 1536));
                    for (int i = 0; i < pageCount; i++)
                    {
                        using var pr = docReader.GetPageReader(i);
                        int w = pr.GetPageWidth();
                        int h = pr.GetPageHeight();
                        pngPages[i] = RenderToPng(pr.GetImage(), w, h);
                        rasterW[i]  = w;
                        rasterH[i]  = h;
                        int captured = i;
                        Dispatcher.Invoke(() => UpdateFlattenProgress(overlay, captured + 1, pageCount));
                    }
                });

                // Decode to frozen BitmapSources on the UI thread for the preview window.
                var sources = new BitmapSource[pageCount];
                for (int i = 0; i < pageCount; i++)
                {
                    using var ms = new MemoryStream(pngPages![i]);
                    var src = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    src.Freeze();
                    sources[i] = src;
                }

                HideFlattenProgress(overlay);
                overlayHidden = true;

                var preview = new PrintPreviewWindow(this, sources, rasterW!, rasterH!);
                if (preview.ShowDialog() == true)
                    SetStatus(string.Format(Loc("Str_Printed"), preview.PrintedPageCount));
            }
            catch (Exception ex)
            {
                try { KillerDialog.Show(this, $"Print failed:\n{ex.GetType().Name}: {ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error); }
                catch { }
            }
            finally
            {
                if (!overlayHidden) try { HideFlattenProgress(overlay); } catch { }
                if (tempFlattened != null) try { System.IO.File.Delete(tempFlattened); } catch { }
            }
        }

        // ============================================================
        // Save annotations to PDF
        // ============================================================

        private void DrawAnnotationsOnDocument()
        {
            if (_doc is null) return;

            // Strip link annotation borders so they don't render as colored rectangles
            // (e.g. strikethrough-like lines) in other PDF viewers.
            StripLinkAnnotationBorders(_doc);

            foreach (var kvp in _annotations)
            {
                int pageIdx = kvp.Key;
                var annots = kvp.Value;
                if (annots.Count == 0 || pageIdx >= _doc.PageCount) continue;
                if (!_renderDims.ContainsKey(pageIdx)) continue;

                var page = _doc.Pages[pageIdx];
                var (renderW, renderH) = _renderDims[pageIdx];
                double sx = page.Width.Point / renderW;
                double sy = page.Height.Point / renderH;

                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

                foreach (var annot in annots)
                {
                    switch (annot)
                    {
                        case TextAnnotation ta:
                            var font = new XFont("Segoe UI", ta.FontSize * sy);
                            var lines = ta.Content.Split('\n');
                            double lineH = ta.FontSize * sy * 1.2;
                            double ty = ta.Position.Y * sy + ta.FontSize * sy;
                            var taColor = ta.GetColor();
                            var taBrush = new XSolidBrush(XColor.FromArgb(taColor.A, taColor.R, taColor.G, taColor.B));
                            foreach (var line in lines)
                            {
                                if (!string.IsNullOrEmpty(line))
                                    gfx.DrawString(line, font, taBrush, ta.Position.X * sx, ty);
                                ty += lineH;
                            }
                            break;

                        case HighlightAnnotation ha:
                            var hc = ha.GetColor();
                            var hBrush = new XSolidBrush(XColor.FromArgb(hc.A, hc.R, hc.G, hc.B));
                            gfx.DrawRectangle(hBrush,
                                ha.Bounds.X * sx, ha.Bounds.Y * sy,
                                ha.Bounds.Width * sx, ha.Bounds.Height * sy);
                            break;

                        case InkAnnotation ia:
                            if (ia.Points.Count < 2) break;
                            var ic = ia.GetColor();
                            var pen = new XPen(XColor.FromArgb(ic.A, ic.R, ic.G, ic.B), ia.StrokeWidth * sx)
                            {
                                LineJoin = XLineJoin.Round,
                                LineCap = XLineCap.Round
                            };
                            for (int i = 0; i < ia.Points.Count - 1; i++)
                            {
                                gfx.DrawLine(pen,
                                    ia.Points[i].X * sx, ia.Points[i].Y * sy,
                                    ia.Points[i + 1].X * sx, ia.Points[i + 1].Y * sy);
                            }
                            break;

                        case TextEditAnnotation tea:
                            // White-out original text area
                            var whiteRect = new XSolidBrush(XColors.White);
                            gfx.DrawRectangle(whiteRect,
                                (tea.OriginalBounds.X - 2) * sx, (tea.OriginalBounds.Y - 2) * sy,
                                (tea.OriginalBounds.Width + 4) * sx, (tea.OriginalBounds.Height + 4) * sy);
                            // Draw replacement text
                            var editFont = new XFont(tea.FontName, tea.FontSize * sy);
                            double ety = tea.Position.Y * sy + tea.FontSize * sy;
                            gfx.DrawString(tea.NewContent, editFont, XBrushes.Black, tea.Position.X * sx, ety);
                            break;

                        case SignatureAnnotation sa:
                            if (sa.ImageData is not null)
                            {
                                try
                                {
                                    var imgBytes = Convert.FromBase64String(sa.ImageData);
                                    var xImg = XImage.FromStream(() => new System.IO.MemoryStream(imgBytes));
                                    double imgX = sa.Position.X * sx;
                                    double imgY = sa.Position.Y * sy;
                                    double imgW = sa.SourceWidth * sa.Scale * sx;
                                    double imgH = sa.SourceHeight * sa.Scale * sy;
                                    gfx.DrawImage(xImg, imgX, imgY, imgW, imgH);
                                }
                                catch { /* skip broken image */ }
                            }
                            else
                            {
                                var sigPen = new XPen(XColors.Black, 2 * sa.Scale * sx)
                                {
                                    LineJoin = XLineJoin.Round,
                                    LineCap = XLineCap.Round
                                };
                                foreach (var stroke in sa.Strokes)
                                {
                                    for (int i = 0; i < stroke.Count - 1; i++)
                                    {
                                        double x1 = (sa.Position.X + stroke[i].X * sa.Scale) * sx;
                                        double y1 = (sa.Position.Y + stroke[i].Y * sa.Scale) * sy;
                                        double x2 = (sa.Position.X + stroke[i + 1].X * sa.Scale) * sx;
                                        double y2 = (sa.Position.Y + stroke[i + 1].Y * sa.Scale) * sy;
                                        gfx.DrawLine(sigPen, x1, y1, x2, y2);
                                    }
                                }
                            }
                            break;

                        case ImageAnnotation ia:
                            try
                            {
                                var iaBytes = Convert.FromBase64String(ia.ImageData);
                                var xia = XImage.FromStream(() => new System.IO.MemoryStream(iaBytes));
                                double iaX = ia.Position.X * sx;
                                double iaY = ia.Position.Y * sy;
                                double iaW = ia.SourceWidth * ia.Scale * sx;
                                double iaH = ia.SourceHeight * ia.Scale * sy;
                                gfx.DrawImage(xia, iaX, iaY, iaW, iaH);
                            }
                            catch { /* skip broken image */ }
                            break;
                    }
                }
            }
        }

        // ============================================================
        // Bitmap rotation helper
        // ============================================================

        /// <summary>
        /// Rotates a raw BGRA (4 bytes/pixel) bitmap clockwise by degrees.
        /// Used because Docnet's FPDF_RenderPageBitmapWithMatrix uses a pure-scaling
        /// matrix, so PDFium renders the page in its MediaBox orientation (no rotation).
        /// We strip /Rotate from the temp file so content is never clipped, then rotate
        /// the pixel buffer here to match the intended visual orientation.
        /// </summary>
        internal static (byte[] bytes, int w, int h) RotateBitmapStatic(byte[] src, int w, int h, int degrees)
            => RotateBitmap(src, w, h, degrees);

        private static (byte[] bytes, int w, int h) RotateBitmap(byte[] src, int w, int h, int degrees)
        {
            degrees = ((degrees % 360) + 360) % 360;
            if (degrees == 0) return (src, w, h);
            int newW = (degrees == 90 || degrees == 270) ? h : w;
            int newH = (degrees == 90 || degrees == 270) ? w : h;
            byte[] dst = new byte[newW * newH * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int srcIdx = (y * w + x) * 4;
                    int dstX, dstY;
                    switch (degrees)
                    {
                        case 90:  dstX = h - 1 - y; dstY = x;         break; // CW
                        case 180: dstX = w - 1 - x; dstY = h - 1 - y; break;
                        default:  dstX = y;          dstY = w - 1 - x; break; // 270 CW
                    }
                    int dstIdx = (dstY * newW + dstX) * 4;
                    dst[dstIdx]     = src[srcIdx];
                    dst[dstIdx + 1] = src[srcIdx + 1];
                    dst[dstIdx + 2] = src[srcIdx + 2];
                    dst[dstIdx + 3] = src[srcIdx + 3];
                }
            }
            return (dst, newW, newH);
        }

        // ============================================================
        // Temp save/reload
        // ============================================================

        private void SaveTempAndReload(bool keepAnnotations = false)
        {
            if (_doc is null || _currentFile is null) return;
            // Overlay annotations are unsaved, still-editable user work. Callers that don't change
            // page identity (crop) pass keepAnnotations:true so annotations on other pages survive
            // the reload and stay selectable/movable; they are re-rendered after the doc reopens.
            if (!keepAnnotations) _annotations.Clear();
            _renderDims.Clear();
            ClearSelection();
            MarkDirty();
            var doc = _doc;
            int selectedIdx = PageList.SelectedIndex;

            // Capture page rotations, then strip them from the document before saving.
            // Docnet uses FPDF_GetPageWidth/Height (MediaBox, no rotation) to size the bitmap,
            // then renders with PDFium's page CTM which *does* include /Rotate.  For 90°/270°
            // the rendered landscape content overflows the portrait-sized bitmap and gets clipped.
            // Stripping /Rotate to 0 before saving means Docnet renders clean unrotated content
            // that fits the bitmap; RotateBitmap is applied in each render path instead.
            _pageRotations.Clear();
            for (int i = 0; i < doc.PageCount; i++)
            {
                int rot = ((doc.Pages[i].Rotate % 360) + 360) % 360;
                _pageRotations[i] = rot;
                doc.Pages[i].Rotate = 0;
            }

            var tempPath = App.MakeTempFile("temp");
            try
            {
                doc.Save(tempPath);
                doc.Close();
            }
            catch (Exception saveEx) when (IsXRefException(saveEx))
            {
                // PdfSharpCore fails to re-save encrypted PDFs (e.g. owner-restricted RC4 files)
                // because it encounters cross-reference tokens while serialising dirty objects.
                // Primary fallback: use PDFium (already initialised for the page preview) to
                // load the source, strip all /Rotate values, remove encryption, and save.
                // Secondary fallback: PdfSharpCore Import mode (works on some non-encrypted xref
                // issues but fails on encrypted files; kept as a last resort).
                doc.Close();
                _doc = null;
                if (!TryPdfiumSaveWithZeroRotations(_currentFile!, tempPath) &&
                    !TryImportRepairToPath(_currentFile!, tempPath, stripRotations: true))
                    throw; // re-throw original if both fallbacks fail
            }
            // PdfSharpCore sometimes saves a file where one object's xref offset points at the
            // xref table itself (object N offset = xref table position). When PdfSharp then tries
            // to re-open that file in Modify mode it seeks to the xref table, reads the keyword
            // "xref" as a token in an object context, and throws "Unexpected token 'xref'".
            // Fix: catch the reopen failure, pipe the saved file through PDFium (which has
            // robust error recovery and will rewrite a correct xref), then retry the open.
            try
            {
                _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
            }
            catch (Exception openEx) when (IsXRefException(openEx))
            {
                var fixedPath = App.MakeTempFile("fixed");
                if (!TryPdfiumSaveWithZeroRotations(tempPath, fixedPath))
                    throw; // PDFium also failed — re-throw original reopen error
                tempPath = fixedPath;
                _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
            }
            _currentFile = tempPath;

            // Restore rotations in the reopened in-memory doc so saves, form fields,
            // and all other operations see the correct rotation values.
            foreach (var kv in _pageRotations)
                _doc.Pages[kv.Key].Rotate = kv.Value;

            RefreshPageList();
            if (selectedIdx >= 0 && selectedIdx < PageList.Items.Count)
                PageList.SelectedIndex = selectedIdx;
            else if (PageList.Items.Count > 0)
                PageList.SelectedIndex = 0;

            // In Continuous view the strip caches one rendered slot per page. After a
            // page-modifying reload (e.g. crop) it must be rebuilt so the main view reflects the
            // new pages; the slot-sizing in RenderContinuousPages makes cropped pages fit cleanly.
            if (_viewMode == ViewMode.Continuous)
            {
                int contIdx = PageList.SelectedIndex;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() => SetupContinuousView(contIdx)));
                return;
            }

            // Refit synchronously so the first rendered frame uses the correct zoom.
            PagePreviewPanel.ScrollToHorizontalOffset(0);
            ReapplyGridOrFit();

            // Deferred refit after layout settles for accurate ActualWidth.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
            {
                PagePreviewPanel.ScrollToHorizontalOffset(0);
                ReapplyGridOrFit();
            }));
        }

        // ============================================================
        // Zoom
        // ============================================================

        private void PagePreview_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                if (_viewMode == ViewMode.Grid) { GridZoomStep(e.Delta < 0); return; }

                // Capture cursor position and scroll offsets BEFORE zoom changes so we can
                // compute the new offsets that keep the point under the cursor stationary.
                Point cursorInViewport = e.GetPosition(PagePreviewPanel);
                double oldZoom = _zoomLevel;
                double oldHOff = PagePreviewPanel.HorizontalOffset;
                double oldVOff = PagePreviewPanel.VerticalOffset;

                SetZoom(e.Delta > 0 ? _zoomLevel + ZoomStep : _zoomLevel - ZoomStep);

                // After layout settles, reposition the scroll so the cursor point stays fixed.
                // Formula: newOffset = (oldOffset + cursorPos) * (newZoom / oldZoom) - cursorPos
                double ratio   = _zoomLevel / oldZoom;
                double newHOff = (oldHOff + cursorInViewport.X) * ratio - cursorInViewport.X;
                double newVOff = (oldVOff + cursorInViewport.Y) * ratio - cursorInViewport.Y;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
                {
                    PagePreviewPanel.ScrollToHorizontalOffset(Math.Max(0, newHOff));
                    PagePreviewPanel.ScrollToVerticalOffset(Math.Max(0, newVOff));
                }));
                return;
            }

            // Regular scroll: let the ScrollViewer handle it normally.
            // At scroll boundaries, fall through to page navigation so the user
            // can reach adjacent pages without touching the sidebar.
            if (PagePreviewPanel.ScrollableHeight <= 0)
            {
                // No scrollable content — navigate pages directly.
                e.Handled = true;
                NavigatePageByWheel(e.Delta);
                return;
            }

            bool atTop    = PagePreviewPanel.VerticalOffset <= 0;
            bool atBottom = PagePreviewPanel.VerticalOffset >= PagePreviewPanel.ScrollableHeight - 1;
            // In Continuous view the whole document is one scroll; don't hop pages at the
            // boundary - just let it stop at the top/bottom.
            if (_viewMode != ViewMode.Continuous && ((atTop && e.Delta > 0) || (atBottom && e.Delta < 0)))
            {
                e.Handled = true;
                NavigatePageByWheel(e.Delta);
            }
            // Otherwise let the ScrollViewer scroll naturally.
        }

        private void NavigatePageByWheel(int delta)
        {
            if (_doc is null) return;
            int cur = PageList.SelectedIndex;
            if (delta > 0 && cur > 0)
                PageList.SelectedIndex = cur - 1;
            else if (delta < 0 && cur < _doc.PageCount - 1)
                PageList.SelectedIndex = cur + 1;
        }

        private void ApplyZoom()
        {
            if (_pageContentGrid.LayoutTransform is ScaleTransform st)
            {
                st.ScaleX = _zoomLevel;
                st.ScaleY = _zoomLevel;
            }
            SyncZoomBox();   // keep the toolbar box in step (FitToWidth/FitToPage don't call SetZoom)
            // Recalculate how many pages fit after zoom changes.
            // Use RefreshPageView so link overlays are re-added after RenderAdditionalPages
            // calls ClearSecondaryPages (which wipes them).
            int applyIdx = PageList.SelectedIndex;
            if (applyIdx >= 0)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () => RefreshPageView(applyIdx));

            // If the user has zoomed in far enough that the current bitmap would be
            // upscaled by more than 20%, queue a deferred re-render at higher resolution.
            // The timer debounces rapid Ctrl+scroll so we re-render only once per gesture.
            if (applyIdx >= 0 && _zoomLevel > _lastRenderZoom * 1.20 && _doc is not null)
            {
                if (_rerenderTimer is null)
                {
                    _rerenderTimer = new System.Windows.Threading.DispatcherTimer
                        { Interval = TimeSpan.FromMilliseconds(400) };
                    _rerenderTimer.Tick += (_, _) =>
                    {
                        _rerenderTimer!.Stop();
                        if (_doc is not null && PageList.SelectedIndex >= 0)
                            RenderPage(PageList.SelectedIndex);
                    };
                }
                _rerenderTimer.Stop();
                _rerenderTimer.Start();
            }
        }

        private void ResetZoom() => SetZoom(1.0);

        // Grid zoom snaps to "fit N pages across the viewport", so zooming steps through clean
        // columns (1, 2, 3, ... per row) instead of arbitrary percentages. N rises as you zoom out
        // and keeps going for larger documents until the page size hits the zoom floor.
        private double GridZoomForN(int n)
        {
            if (n < 1) n = 1;
            double rdW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 1583;
            double vw  = PagePreviewPanel.ActualWidth;   // SAME width + slot the layout uses
            if (vw <= 0 || rdW <= 0) return _zoomLevel;
            // RenderAdditionalPages lays out pages in slots of (rdW + 12) within (ActualWidth - 24);
            // invert that so "fit n" produces exactly n columns with no gap.
            return (vw - 24.0) / (n * (rdW + 12.0));
        }

        private void GridZoomStep(bool zoomOut)
        {
            double rdW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 1583;
            double vw  = PagePreviewPanel.ActualWidth;
            if (vw <= 0 || rdW <= 0) { SetZoom(zoomOut ? _zoomLevel - ZoomStep : _zoomLevel + ZoomStep); return; }
            // Current columns, computed the SAME way RenderAdditionalPages computes pagesPerRow.
            int curN = Math.Max(1, (int)Math.Floor((vw - 24.0) / (_zoomLevel * (rdW + 12.0))));
            int newN = Math.Max(1, zoomOut ? curN + 1 : curN - 1);
            // If the column count is already at the limit the clamped zoom is unchanged, so
            // skip the re-render entirely - otherwise every Ctrl+Scroll reloads all tiles
            // without changing anything.
            double target = Math.Max(ZoomMin, Math.Min(ZoomMax, GridZoomForN(newN)));
            if (Math.Abs(target - _zoomLevel) < 1e-4) return;
            SetZoom(target);   // already clamped to [ZoomMin, ZoomMax]
        }

        /// <summary>
        /// Central zoom-change entry point for buttons, keyboard shortcuts, and the dropdown.
        /// Clamps to [ZoomMin, ZoomMax], applies the scale, syncs the combo box, and updates
        /// the status bar. Does NOT apply a fit mode — call FitToWidth / FitToPage for that.
        /// </summary>
        // The internal _zoomLevel scales each page's layout box. In Continuous mode that box is
        // the page's natural DIP width, so _zoomLevel already reads as true zoom (1.0 = 100%).
        // In Single/Two-Page/Grid the box is the render-dimension bitmap (~2x natural width), so
        // the raw _zoomLevel reads about half the real size. DisplayZoomFactor converts to true
        // zoom for everything shown to (or typed by) the user; the internal value is unchanged.
        private double DisplayZoomFactor()
        {
            if (_viewMode == ViewMode.Continuous || _doc is null) return 1.0;
            int idx = _viewMode == ViewMode.Grid ? 0 : Math.Max(0, PageList.SelectedIndex);
            if (idx < 0 || idx >= _doc.PageCount) return 1.0;
            if (!_renderDims.TryGetValue(idx, out var d) || d.w <= 0) return 1.0;
            double wpt = _doc.Pages[idx].Width.Point, hpt = _doc.Pages[idx].Height.Point;
            if (_pageRotations.TryGetValue(idx, out int r) && (r == 90 || r == 270)) wpt = hpt;
            double naturalW = wpt * 96.0 / 72.0;
            if (naturalW <= 0) return 1.0;
            return d.w / naturalW;
        }
        private double DisplayZoomPct() => _zoomLevel * DisplayZoomFactor() * 100.0;

        private void SetZoom(double level)
        {
            _fitMode   = FitMode.None;
            _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, level));
            ApplyZoom();
            SyncZoomBox();
            if (_doc != null && PageList.SelectedIndex >= 0)
                SetStatus($"Page {PageList.SelectedIndex + 1} of {_doc.PageCount} - {DisplayZoomPct():F0}%");
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)  { if (_viewMode == ViewMode.Grid) GridZoomStep(false); else SetZoom(_zoomLevel + ZoomStep); }
        private void ZoomOut_Click(object sender, RoutedEventArgs e) { if (_viewMode == ViewMode.Grid) GridZoomStep(true);  else SetZoom(_zoomLevel - ZoomStep); }

        private void SyncZoomBox()
        {
            if (_zoomBox is null) return;
            _zoomBox.SelectionChanged -= ZoomBox_SelectionChanged;

            // When a fit mode is active, show the "Fit Width"/"Fit Page" entry rather than a raw
            // percentage so the box matches the status bar.
            string? fitTag = _fitMode == FitMode.Width ? "fitwidth"
                           : _fitMode == FitMode.Page  ? "fitpage"
                           : null;
            if (fitTag != null)
            {
                foreach (ComboBoxItem item in _zoomBox.Items)
                {
                    if (item.Tag?.ToString() == fitTag)
                    {
                        _zoomBox.SelectedItem = item;
                        _zoomBox.SelectionChanged += ZoomBox_SelectionChanged;
                        return;
                    }
                }
            }

            string target = $"{DisplayZoomPct():F0}%";
            foreach (ComboBoxItem item in _zoomBox.Items)
            {
                if (item.Content?.ToString() == target)
                {
                    _zoomBox.SelectedItem = item;
                    _zoomBox.SelectionChanged += ZoomBox_SelectionChanged;
                    return;
                }
            }
            // No preset match - clear dropdown selection and show free-form percentage
            _zoomBox.SelectedItem = null;
            _zoomBox.Text = target;
            _zoomBox.SelectionChanged += ZoomBox_SelectionChanged;
        }

        private void ZoomBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_zoomBox?.SelectedItem is not ComboBoxItem item) return;
            string? tag = item.Tag?.ToString();
            if (tag is null) return;

            if (tag == "fitwidth") { FitToWidth(); return; }
            if (tag == "fitpage")  { FitToPage();  return; }

            if (double.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double z))
            {
                _fitMode = FitMode.None;
                // Preset tags are true zoom (1.0 = 100%); convert to the internal render-dim scale.
                double zf = DisplayZoomFactor(); if (zf <= 0) zf = 1.0;
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, z / zf));
                ApplyZoom();
                if (PageList.SelectedIndex >= 0 && _doc != null)
                    SetStatus($"Page {PageList.SelectedIndex + 1} of {_doc.PageCount} - {DisplayZoomPct():F0}%");
            }
        }

        private void FitToWidth()
        {
            double viewW = PagePreviewPanel.ActualWidth - 40;
            if (viewW <= 0) return;

            // Continuous mode: pages are laid out at _continuousPageW (natural DIPs width)
            // and scaled by the ScaleTransform on PageContentGrid. PageImage is hidden, so
            // we cannot use its Source as a guard; use _continuousPageW directly instead.
            if (_viewMode == ViewMode.Continuous)
            {
                if (_continuousPageW <= 0) return;
                _fitMode   = FitMode.Width;
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, viewW / _continuousPageW));
                ApplyZoom();
                int ci = PageList.SelectedIndex;
                if (ci >= 0 && _doc != null)
                    SetStatus(string.Format(Loc("Str_FitWidth"), ci + 1, _doc.PageCount, $"{DisplayZoomPct():F0}"));
                return;
            }

            if (PageImage.Source is null) return;
            // Use _renderDims rather than PageImage.ActualWidth - the latter can be stale
            // (reporting the previous page's layout size) if WPF layout hasn't fully settled.
            // _renderDims is set synchronously inside RenderPage so it always matches the
            // current page. dipW is zoom-stable: scaledMax scales with zoom while RenderPage
            // divides by zoomFactor, so the two cancel out. Use dipW directly.
            int idx = PageList.SelectedIndex;
            double dipW = (idx >= 0 && _renderDims.TryGetValue(idx, out var dimsW))
                ? dimsW.w
                : (PageImage.ActualWidth > 0 ? PageImage.ActualWidth : 1);
            if (dipW <= 0) return;
            // Two Page mode shows two pages side by side — each page gets roughly half
            // the viewport width (minus a small gap between pages).
            double slotW = _viewMode == ViewMode.TwoPage ? (viewW - 12) / 2 : viewW;
            _fitMode = FitMode.Width;
            _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, slotW / dipW));
            ApplyZoom();
            if (idx >= 0 && _doc != null)
                SetStatus(string.Format(Loc("Str_FitWidth"), idx + 1, _doc.PageCount, $"{DisplayZoomPct():F0}"));
        }

        private void FitToPage()
        {
            double viewW = PagePreviewPanel.ActualWidth  - 40;
            double viewH = PagePreviewPanel.ActualHeight - 40;
            if (viewW <= 0 || viewH <= 0) return;

            // Continuous mode: derive the current page's natural height from its PDF aspect
            // ratio and _continuousPageW, then fit both axes.
            if (_viewMode == ViewMode.Continuous)
            {
                if (_continuousPageW <= 0 || _doc is null) return;
                int ci = PageList.SelectedIndex;
                if (ci < 0) return;
                var pdfPage = _doc.Pages[ci];
                double ratio = Math.Max(0.1, pdfPage.Height.Point / Math.Max(1.0, pdfPage.Width.Point));
                double dipH  = _continuousPageW * ratio;
                _fitMode   = FitMode.Page;
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax,
                    Math.Min(viewW / _continuousPageW, viewH / dipH)));
                ApplyZoom();
                SetStatus(string.Format(Loc("Str_FitPage"), ci + 1, _doc.PageCount, $"{DisplayZoomPct():F0}"));
                return;
            }

            if (PageImage.Source is null) return;
            int idx = PageList.SelectedIndex;
            // dipW/dipH are zoom-stable (see FitToWidth comment). Use them directly.
            double dipW = (idx >= 0 && _renderDims.TryGetValue(idx, out var dimsP))
                ? dimsP.w
                : (PageImage.ActualWidth > 0 ? PageImage.ActualWidth : 1);
            double dipH2 = (idx >= 0 && _renderDims.TryGetValue(idx, out var dimsP2))
                ? dimsP2.h
                : (PageImage.ActualHeight > 0 ? PageImage.ActualHeight : 1);
            if (dipW <= 0 || dipH2 <= 0) return;
            double slotW2 = _viewMode == ViewMode.TwoPage ? (viewW - 12) / 2 : viewW;
            _fitMode = FitMode.Page;
            _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax,
                Math.Min(slotW2 / dipW, viewH / dipH2)));
            ApplyZoom();
            SetStatus(string.Format(Loc("Str_FitPage"), idx + 1, _doc!.PageCount, $"{DisplayZoomPct():F0}"));
        }

        // Re-fit the main view after a reload. Grid keeps its column-fit (FitToWidth alone would
        // yank it out into a single-page Fit Width view); other modes honor the fit mode.
        private void ReapplyGridOrFit()
        {
            if (_viewMode == ViewMode.Grid)
            {
                double rdW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 1583;
                double vw  = PagePreviewPanel.ActualWidth;
                if (vw > 0 && rdW > 0)
                {
                    int curN = Math.Max(1, (int)Math.Round((vw - 24.0) / (Math.Max(0.01, _zoomLevel) * (rdW + 12.0))));
                    SetZoom(GridZoomForN(curN));
                }
                else ApplyZoom();
                return;
            }
            if (_fitMode == FitMode.Page) FitToPage();
            else FitToWidth();
        }

        private void PagePreviewPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_cropPreviewRect is not null || _cropConfirmBar is not null) return;
            // Grid keeps its zoom on resize. Re-zooming here rebuilds the tiles, which can toggle a
            // scrollbar, change the viewport size again, and feed back into this handler - an
            // infinite layout loop that freezes the app. The grid already fits on open and on
            // mode switch, so do nothing here for grid.
            if (_viewMode == ViewMode.Grid) return;
            if (_fitMode == FitMode.Width) FitToWidth();
            else if (_fitMode == FitMode.Page) FitToPage();
        }

        private void PagePreviewPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            bool spaceDown = Keyboard.IsKeyDown(Key.Space);
            if (e.ChangedButton == MouseButton.Middle ||
                (e.ChangedButton == MouseButton.Left && spaceDown))
            {
                _isPanning  = true;
                _panStart   = e.GetPosition(PagePreviewPanel);
                _panScrollH = PagePreviewPanel.HorizontalOffset;
                _panScrollV = PagePreviewPanel.VerticalOffset;
                PagePreviewPanel.CaptureMouse();
                PagePreviewPanel.Cursor = Cursors.SizeAll;
                e.Handled = true;
            }
            // Crop: allow starting the selection OUTSIDE the page. On-page clicks are handled by
            // the page overlay; here we catch clicks in the margins, route them to the nearest page
            // overlay, and clamp the start point to the page edge so the rect stays on the page.
            else if (e.ChangedButton == MouseButton.Left && !spaceDown
                     && _currentTool == EditTool.Crop && _doc is not null)
            {
                // Resolve the page surface for an off-page crop start in any view mode. On-page
                // clicks are left to the page canvas/overlay; we only handle margin clicks here.
                Canvas? target = null;
                if (_viewMode == ViewMode.Continuous)
                {
                    if (!(e.OriginalSource is DependencyObject osc && IsWithinPageOverlay(osc)))
                    {
                        int pg = NearestContinuousPage(e.GetPosition(_continuousPanel).Y);
                        if (pg >= 0) _continuousCanvases.TryGetValue(pg, out target);
                    }
                }
                else
                {
                    // Single / Two-Page / Grid. An on-page click is handled by that page's own
                    // surface: the primary page uses _annotationCanvas, secondary/grid tiles use
                    // their per-page overlay. Only a genuine margin click (on neither) is routed
                    // here, and we fall back to the primary page for it.
                    bool onPrimary = e.OriginalSource is DependencyObject oss && IsDescendantOf(oss, _annotationCanvas);
                    bool onTile    = e.OriginalSource is DependencyObject ost && IsWithinPageOverlay(ost);
                    if (!onPrimary && !onTile)
                        target = _annotationCanvas;
                }
                if (target is not null && target.Width > 0 && target.Height > 0)
                {
                    _activeCanvas = target;
                    var p = e.GetPosition(target);
                    p.X = Math.Max(0, Math.Min(target.Width, p.X));
                    p.Y = Math.Max(0, Math.Min(target.Height, p.Y));
                    StartCropDraw(p);
                    e.Handled = true;
                }
            }
        }

        // Begin a crop selection on the active overlay at pos (render-dim coords).
        private void StartCropDraw(Point pos)
        {
            _cropPageIndex = _activeCanvas.Tag is int cpi ? cpi : (_viewMode == ViewMode.Grid ? 0 : PageList.SelectedIndex);
            ClearSelection();
            HideCropConfirmBar();
            _isDrawing = true;
            _drawStart = pos;
            _cropPreviewRect = new Rectangle
            {
                Stroke          = Brushes.White,
                StrokeThickness = 1.5,
                StrokeDashArray = [5, 3],
                Fill            = AccentBrush(55),
                Width = 0, Height = 0,
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                    { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 3, Opacity = 0.7 },
            };
            Canvas.SetLeft(_cropPreviewRect, pos.X);
            Canvas.SetTop(_cropPreviewRect, pos.Y);
            Panel.SetZIndex(_cropPreviewRect, 1);
            _activeCanvas.Children.Add(_cropPreviewRect);
            _activePreview = _cropPreviewRect;
            _activeCanvas.CaptureMouse();
        }

        private bool IsWithinPageOverlay(DependencyObject node)
        {
            var cur = node;
            while (cur != null)
            {
                if (cur is Canvas c && _continuousCanvases.ContainsValue(c)) return true;
                cur = VisualTreeHelper.GetParent(cur);
            }
            return false;
        }

        private int NearestContinuousPage(double yInPanel)
        {
            int best = -1; double bestDist = double.MaxValue;
            for (int i = 0; i < _continuousTops.Count && i < _continuousPanel.Children.Count; i++)
            {
                double top = _continuousTops[i];
                double h = ((FrameworkElement)_continuousPanel.Children[i]).Height;
                if (double.IsNaN(h)) h = 0;
                double bottom = top + h;
                double dist = yInPanel < top ? top - yInPanel : (yInPanel > bottom ? yInPanel - bottom : 0);
                if (dist < bestDist) { bestDist = dist; best = i; }
            }
            return best;
        }

        private void PagePreviewPanel_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPanning) return;
            var pos = e.GetPosition(PagePreviewPanel);
            PagePreviewPanel.ScrollToHorizontalOffset(_panScrollH - (pos.X - _panStart.X));
            PagePreviewPanel.ScrollToVerticalOffset  (_panScrollV - (pos.Y - _panStart.Y));
            e.Handled = true;
        }

        private void PagePreviewPanel_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isPanning) return;
            if (e.ChangedButton != MouseButton.Middle && e.ChangedButton != MouseButton.Left) return;
            _isPanning = false;
            PagePreviewPanel.ReleaseMouseCapture();
            PagePreviewPanel.Cursor = _spaceHeld ? Cursors.Hand : Cursors.Arrow;
            e.Handled = true;
        }

        // ============================================================
        // Drag/drop: file open
        // ============================================================

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
                if (files.Length > 0 && files[0].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    OpenFile(files[0]);
            }
        }

        private void DropZone_Click(object sender, MouseButtonEventArgs e) => Open_Click(sender, e);

        // ============================================================
        // Drag/drop: page reorder
        // ============================================================

        private void PageList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
            _dragStartPoint = e.GetPosition(null);

        private void PageList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var diff = _dragStartPoint - e.GetPosition(null);
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (PageList.SelectedIndex >= 0)
                    DragDrop.DoDragDrop(PageList, PageList.SelectedIndex, DragDropEffects.Move);
            }
        }

        private void PageList_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(int)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void PageList_Drop(object sender, DragEventArgs e)
        {
            if (_doc is null || !e.Data.GetDataPresent(typeof(int))) return;
            var doc = _doc;
            int fromIdx = (int)e.Data.GetData(typeof(int))!;
            var pos = e.GetPosition(PageList);
            int toIdx = PageList.Items.Count - 1;
            for (int i = 0; i < PageList.Items.Count; i++)
            {
                if (PageList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item)
                {
                    var itemPos = item.TranslatePoint(new Point(0, item.ActualHeight / 2), PageList);
                    if (pos.Y < itemPos.Y) { toIdx = i; break; }
                }
            }
            if (fromIdx == toIdx) return;
            var page = doc.Pages[fromIdx];
            doc.Pages.RemoveAt(fromIdx);
            if (toIdx > fromIdx) toIdx--;
            doc.Pages.Insert(toIdx, page);
            SaveTempAndReload();
            PageList.SelectedIndex = toIdx;
        }

        // ============================================================
        // Page selection handler
        // ============================================================

        // Lazy accessor — resolves PageList's internal ScrollViewer on first use.
        private ScrollViewer? _sidebarSv;
        private ScrollViewer? SidebarScrollViewer
            => _sidebarSv ??= FindDescendant<ScrollViewer>(PageList);

        private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            int n = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T hit) return hit;
                var result = FindDescendant<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private void PageList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            SidebarScrollViewer?.ScrollToVerticalOffset(
                SidebarScrollViewer.VerticalOffset - e.Delta / 3.0);
            e.Handled = true;
        }

        private void PageJumpBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || _doc is null) return;
            e.Handled = true;
            if (int.TryParse(_pageJumpBox.Text, out int pg))
            {
                int idx = Math.Max(0, Math.Min(_doc.PageCount - 1, pg - 1));
                PageList.SelectedIndex = idx;
            }
            else
            {
                // Restore current page number if input was invalid
                _pageJumpBox.Text = (PageList.SelectedIndex + 1).ToString();
            }
            Keyboard.ClearFocus();
        }

        private void PageJumpBox_GotFocus(object sender, RoutedEventArgs e)
        {
            _pageJumpBox.SelectAll();
        }

        private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PageList.SelectedIndex >= 0)
            {
                CommitActiveTextBox();
                ClearSelection();
                ClearTextSelection();
                if (_viewMode == ViewMode.Continuous)
                {
                    _pageJumpBox.Text = (PageList.SelectedIndex + 1).ToString();
                    ScrollContinuousToPage(PageList.SelectedIndex);
                    return;
                }
                if (_viewMode == ViewMode.Grid)
                {
                    // Grid is a stable overview: selecting a page highlights it but must NOT
                    // re-anchor the grid. It still needs an initial render (open / first display)
                    // when no tiles exist yet; later selections only update the highlight.
                    _pageJumpBox.Text = (PageList.SelectedIndex + 1).ToString();
                    if (_pageContentPanel.Children.Count <= 1)
                    {
                        PagePreviewPanel.ScrollToTop();
                        PagePreviewPanel.ScrollToHorizontalOffset(0);
                        RenderPage(0);   // grid primary is always page 0
                        // Default the grid to a clean 3-columns-across fit. Deferred to Loaded so the
                        // viewport width is valid (it can still be 0 mid-open, which would fall back
                        // to a carried-over zoom and show a single large page).
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                            (Action)(() => SetZoom(GridZoomForN(Math.Min(_doc?.PageCount ?? 1, 3)))));
                    }
                    return;
                }
                PagePreviewPanel.ScrollToTop();
                PagePreviewPanel.ScrollToHorizontalOffset(0);
                RenderPage(PageList.SelectedIndex);
                ApplyZoom();
                // Update page jump box
                _pageJumpBox.Text = (PageList.SelectedIndex + 1).ToString();
                // Re-highlight search results on this page if a search is active
                if (_searchBar is not null && _searchBar.Visibility == Visibility.Visible
                    && _allSearchRects.Count > 0)
                    HighlightSearchResultsOnCurrentPage();
            }
        }

        private void ShortcutHelp_Click(object sender, RoutedEventArgs e)
        {
            ShortcutOverlay.Visibility = ShortcutOverlay.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ShortcutOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Click on the dim backdrop closes the overlay.
            ShortcutOverlay.Visibility = Visibility.Collapsed;
        }

        private void ShortcutOverlayCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Stop the click from bubbling up to the backdrop handler.
            e.Handled = true;
        }

        private void ShortcutOverlayClose_Click(object sender, RoutedEventArgs e)
        {
            ShortcutOverlay.Visibility = Visibility.Collapsed;
        }

        // ── About overlay ───────────────────────────────────────────────

        private void ShowAboutOverlay()
        {
            // Populate dynamic values (SHA256 is slow; run on background thread)
            var version = System.Reflection.Assembly.GetExecutingAssembly()
                               .GetName().Version?.ToString(3) ?? "?";
            var (sigValid, sigSubject, sigThumbprint) = App.GetExeSignerInfo();

            AboutPublisherBlock.Text   = sigValid ? sigSubject : "(not signed or chain failed)";
            AboutThumbprintBlock.Text  = string.IsNullOrEmpty(sigThumbprint) ? "(none)" : sigThumbprint;
            AboutSha256Block.Text      = Loc("Str_About_Computing");

            // Logo block
            AboutLogoBlock.Inlines.Clear();
            var logoHl = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("KillerPDF"))
            {
                Foreground      = (System.Windows.Media.Brush)FindResource("Accent"),
                TextDecorations = null
            };
            logoHl.Click += (_, _) =>
                Process.Start(new ProcessStartInfo("https://pdf.killertools.net") { UseShellExecute = true });
            AboutLogoBlock.Inlines.Add(logoHl);

            // Tagline block
            AboutTaglineBlock.Inlines.Clear();
            AboutTaglineBlock.Inlines.Add(new System.Windows.Documents.Run("A fast, free PDF toolkit for Windows. Part of ")
            {
                Foreground = (System.Windows.Media.Brush)FindResource("TextDim")
            });
            var ktHl = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Killer Tools"))
            {
                Foreground      = (System.Windows.Media.Brush)FindResource("Accent"),
                TextDecorations = null
            };
            ktHl.Click += (_, _) =>
                Process.Start(new ProcessStartInfo("https://killertools.net") { UseShellExecute = true });
            AboutTaglineBlock.Inlines.Add(ktHl);
            AboutTaglineBlock.Inlines.Add(new System.Windows.Documents.Run(".")
            {
                Foreground = (System.Windows.Media.Brush)FindResource("TextDim")
            });

            // Version block (clickable - opens GitHub release)
            AboutVersionBlock.Inlines.Clear();
            var verHl = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run($"v{version}"))
            {
                Foreground      = (System.Windows.Media.Brush)FindResource("Accent"),
                TextDecorations = null
            };
            verHl.Click += (_, _) =>
                Process.Start(new ProcessStartInfo(
                    $"https://github.com/SteveTheKiller/KillerPDF/releases/tag/v{version}")
                { UseShellExecute = true });
            AboutVersionBlock.Inlines.Add(verHl);

            AboutOverlay.Visibility = Visibility.Visible;

            // Compute SHA256 off the UI thread
            System.Threading.Tasks.Task.Run(() =>
            {
                var sha256 = App.GetExeSha256();
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    (Action)(() => AboutSha256Block.Text = sha256));
            });
        }

        private void AboutOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            AboutOverlay.Visibility = Visibility.Collapsed;
        }

        private void AboutOverlayCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        private void AboutOverlayClose_Click(object sender, RoutedEventArgs e)
        {
            AboutOverlay.Visibility = Visibility.Collapsed;
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        // ============================================================
        // View Mode
        // ============================================================

        private void SetViewMode(ViewMode mode)
        {
            if (_viewMode == mode) return;
            _viewMode = mode;
            App.SetSetting("ViewMode", mode.ToString());

            bool isContinuous = mode == ViewMode.Continuous;
            _pageContentPanel.Visibility = isContinuous ? Visibility.Collapsed : Visibility.Visible;
            _continuousPanel.Visibility  = isContinuous ? Visibility.Visible   : Visibility.Collapsed;

            if (!isContinuous)
            {
                _continuousRenderCts?.Cancel();
                _continuousPanel.Children.Clear();
                _continuousTops.Clear();
                _continuousCanvases.Clear();
            }

            if (_doc is null) return;
            int idx = PageList.SelectedIndex;
            if (mode == ViewMode.Continuous)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () => SetupContinuousView(idx));
            }
            else
            {
                _secondaryRenderCts?.Cancel();
                ClearSecondaryPages();
                _pageContentPanel.Width = double.NaN;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                {
                    RenderPage(mode == ViewMode.Grid ? 0 : idx);
                    // Grid: apply a clean column-fit zoom (continuous's zoom is far too large for a
                    // grid, and a non-column zoom leaves a gap). SetZoom -> ApplyZoom defers the
                    // single tile render, so return here instead of calling RefreshPageView again
                    // (a second render would duplicate tiles).
                    if (mode == ViewMode.Grid)
                    {
                        SetZoom(GridZoomForN(Math.Min(_doc!.PageCount, 3)));
                        return;
                    }
                    // Switching into Single or Two-Page fits the whole page so it isn't left at an
                    // awkward carried-over zoom from another mode.
                    if      (mode == ViewMode.Single || mode == ViewMode.TwoPage) FitToPage();
                    else if (_fitMode == FitMode.Width) FitToWidth();
                    else if (_fitMode == FitMode.Page)  FitToPage();
                    else                                ApplyZoom();
                    RefreshPageView(idx);
                });
            }
        }

        private void ScrollContinuousToPage(int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= _continuousTops.Count) return;
            double target = _continuousTops[pageIndex] * _zoomLevel;
            PagePreviewPanel.ScrollToVerticalOffset(target);
        }

        private void PagePreviewPanel_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_viewMode != ViewMode.Continuous || _continuousTops.Count == 0) return;

            double viewportCenter = (PagePreviewPanel.VerticalOffset + PagePreviewPanel.ViewportHeight * 0.5)
                                    / Math.Max(0.01, _zoomLevel);
            int nearest = 0;
            double minDist = double.MaxValue;
            for (int i = 0; i < _continuousTops.Count; i++)
            {
                if (i >= _continuousPanel.Children.Count) break;
                var slot = (FrameworkElement)_continuousPanel.Children[i];
                double center = _continuousTops[i] + slot.Height * 0.5;
                double dist   = Math.Abs(center - viewportCenter);
                if (dist < minDist) { minDist = dist; nearest = i; }
            }

            if (PageList.SelectedIndex != nearest)
            {
                _pageJumpBox.Text = (nearest + 1).ToString();
                // Update sidebar thumbnail without triggering a full page render
                PageList.SelectionChanged -= PageList_SelectionChanged;
                PageList.SelectedIndex = nearest;
                PageList.SelectionChanged += PageList_SelectionChanged;
            }
        }

        private void SetupContinuousView(int initialPage)
        {
            if (_doc is null) return;
            _continuousRenderCts?.Cancel();
            _continuousPanel.Children.Clear();
            _continuousTops.Clear();
            _continuousCanvases.Clear();

            // Use the PDF's natural page width in WPF DIPs (96 DIP/inch, 72 pt/inch).
            // This is zoom-independent, which is critical: FitToWidth computes
            //   zoom = viewportW / _continuousPageW
            // and if _continuousPageW were derived from the current zoom level the two
            // would cancel and FitToWidth would always return approximately the old zoom.
            var refPage = _doc.Pages[0];
            _continuousPageW = Math.Max(200.0, refPage.Width.Point * (96.0 / 72.0));

            double y = 0;
            for (int i = 0; i < _doc.PageCount; i++)
            {
                _continuousTops.Add(y);
                var pdfPage = _doc.Pages[i];
                double pw = pdfPage.Width.Point, ph = pdfPage.Height.Point;
                if (_pageRotations.TryGetValue(i, out int prot) && (prot == 90 || prot == 270))
                    (pw, ph) = (ph, pw);
                double ratio = Math.Max(0.1, ph / Math.Max(1, pw));
                double slotH = _continuousPageW * ratio;

                // Canonical render-dim space (matches single-page RenderPage: longest side -> 2048)
                // so annotation coordinates are identical in both view modes.
                double maxDim = Math.Max(pw, ph);
                int rdW = Math.Max(1, (int)Math.Round(2048.0 * pw / maxDim));
                int rdH = Math.Max(1, (int)Math.Round(2048.0 * ph / maxDim));
                _renderDims[i] = (rdW, rdH);

                // Per-page annotation overlay: sized in render-dim space, scaled to the slot.
                double slotScale = _continuousPageW / rdW;
                var overlay = new Canvas
                {
                    Width           = rdW,
                    Height          = rdH,
                    Background       = Brushes.Transparent,
                    ClipToBounds     = true,
                    Tag              = i,
                    LayoutTransform  = new System.Windows.Media.ScaleTransform(slotScale, slotScale)
                };
                overlay.PreviewMouseLeftButtonDown += Canvas_MouseLeftButtonDown;
                overlay.MouseMove                  += Canvas_MouseMove;
                overlay.PreviewMouseLeftButtonUp   += Canvas_MouseLeftButtonUp;
                _continuousCanvases[i] = overlay;

                var pageImg = new Image { Stretch = Stretch.None, Width = _continuousPageW, Height = slotH };
                RenderOptions.SetBitmapScalingMode(pageImg, BitmapScalingMode.HighQuality);

                var slotGrid = new Grid();
                slotGrid.Children.Add(pageImg);
                slotGrid.Children.Add(overlay);

                var placeholder = new Border
                {
                    Width      = _continuousPageW,
                    Height     = slotH,
                    Margin     = new Thickness(0, 0, 0, 12),
                    Background = Application.Current.TryFindResource("Background") as SolidColorBrush
                                 ?? new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                    Tag = i,
                    Child = slotGrid
                };
                int capturedI = i;
                placeholder.PreviewMouseLeftButtonDown += (_, _) => PageList.SelectedIndex = capturedI;
                _continuousPanel.Children.Add(placeholder);
                y += slotH + 12;
            }

            // Re-apply fit mode now that _continuousPageW is known; default to fit-page (one whole
            // page in view) unless the user explicitly chose fit-width.
            if (_fitMode == FitMode.Width) FitToWidth(); else FitToPage();

            _continuousScrollTarget = initialPage;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                () => ScrollContinuousToPage(initialPage));

            _ = RenderContinuousPages();
        }

        private async System.Threading.Tasks.Task RenderContinuousPages()
        {
            if (_doc is null || _currentFile is null) return;
            _continuousRenderCts?.Cancel();
            _continuousRenderCts = new System.Threading.CancellationTokenSource();
            var cts = _continuousRenderCts;

            string currentFile = _currentFile;
            int pageCount      = _doc.PageCount;
            double targetW     = _continuousPageW;
            int renderW        = Math.Max(800, Math.Min(2048, (int)(targetW * 2)));

            // Capture per-page rotations on the UI thread before going async
            var rotations = new Dictionary<int, int>(_pageRotations);

            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var docReader = DocLib.Instance.GetDocReader(
                        currentFile, new PageDimensions(renderW, renderW * 2));

                    for (int i = 0; i < pageCount; i++)
                    {
                        if (cts.IsCancellationRequested) return;
                        using var pr = docReader.GetPageReader(i);
                        int w = pr.GetPageWidth();
                        int h = pr.GetPageHeight();
                        var raw = pr.GetImage();
                        if (w <= 0 || h <= 0 || raw is null) continue;
                        if (rotations.TryGetValue(i, out int rot) && rot != 0)
                            (raw, w, h) = RotateBitmap(raw, w, h, rot);

                        int fi = i, fw = w, fh = h;
                        byte[] bytes = raw;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (cts.IsCancellationRequested || _viewMode != ViewMode.Continuous) return;
                            if (fi >= _continuousPanel.Children.Count) return;

                            var slot = (Border)_continuousPanel.Children[fi];
                            double dipW = slot.Width;
                            double dipH = dipW * fh / fw;
                            double dpiX = 96.0 * fw / dipW;
                            double dpiY = 96.0 * fh / dipH;

                            var bmp = new WriteableBitmap(fw, fh, dpiX, dpiY, PixelFormats.Bgra32, null);
                            bmp.WritePixels(new Int32Rect(0, 0, fw, fh), bytes, fw * 4, 0);
                            bmp.Freeze();

                            if (slot.Child is Grid slotGrid && slotGrid.Children.Count > 0
                                && slotGrid.Children[0] is Image pageImg)
                            {
                                pageImg.Source  = bmp;
                                pageImg.Width   = dipW;
                                pageImg.Height  = dipH;
                                slot.Background = Brushes.White;

                                // Size the slot and overlay from the ACTUAL rendered page so a
                                // cropped page (which renders shorter than its MediaBox estimate)
                                // fills its slot with no white bars. Mirrors single-page view.
                                slot.Height = dipH;
                                double maxF = Math.Max(fw, fh);
                                int rdW = Math.Max(1, (int)Math.Round(2048.0 * fw / maxF));
                                int rdH = Math.Max(1, (int)Math.Round(2048.0 * fh / maxF));
                                _renderDims[fi] = (rdW, rdH);
                                if (slotGrid.Children.Count > 1 && slotGrid.Children[1] is Canvas ov)
                                {
                                    ov.Width  = rdW;
                                    ov.Height = rdH;
                                    ov.LayoutTransform =
                                        new System.Windows.Media.ScaleTransform(dipW / rdW, dipW / rdW);
                                }

                                // Slot heights are now exact; recompute scroll offsets from them.
                                double yy = 0;
                                for (int k = 0; k < _continuousPanel.Children.Count && k < _continuousTops.Count; k++)
                                {
                                    _continuousTops[k] = yy;
                                    double hk = ((FrameworkElement)_continuousPanel.Children[k]).Height;
                                    if (double.IsNaN(hk)) hk = 0;
                                    yy += hk + 12;
                                }

                                // Pages render in order, so when the target page is reached every
                                // page above it has its final height; re-scroll so a crop lands you
                                // back on the same page instead of drifting to the next one.
                                if (_continuousScrollTarget >= 0 && fi >= _continuousScrollTarget)
                                {
                                    int tgt = _continuousScrollTarget;
                                    _continuousScrollTarget = -1;
                                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                                        (Action)(() => ScrollContinuousToPage(tgt)));
                                }

                                RenderAllAnnotations(fi);
                            }
                        });
                    }
                }
                catch { /* render cancelled or doc closed */ }
            }, cts.Token);
        }
    }

    // ============================================================
    // Themed dialog — replaces MessageBox for dark-UI consistency
    // ============================================================
    internal static class KillerDialog
    {
        // Pulls the current theme brush at call time so dialogs respect light/dark/HC themes.
        private static SolidColorBrush R(string key)
            => (SolidColorBrush)Application.Current.Resources[key];

#pragma warning disable IDE0060 // image intentionally kept for API parity with MessageBox; not yet rendered
        public static MessageBoxResult Show(
            Window? owner,
            string message,
            string title = "KillerPDF",
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage image = MessageBoxImage.None)
#pragma warning restore IDE0060
        {
            var result = MessageBoxResult.OK;

            var win = new Window
            {
                Title = title,
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                WindowStartupLocation = owner != null
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize
            };

            var outerBorder = new Border
            {
                Background      = R("BgModal"),
                BorderBrush     = R("Accent"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6)
            };

            var root = new StackPanel();

            // Title bar
            var titleBar = new Border
            {
                Background   = R("BgPanel"),
                Padding      = new Thickness(16, 10, 16, 10),
                CornerRadius = new CornerRadius(5, 5, 0, 0)
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            titleBar.Child = new TextBlock
            {
                Text       = title,
                Foreground = R("Accent"),
                FontWeight = FontWeights.SemiBold,
                FontSize   = 13,
                FontFamily = new System.Windows.Media.FontFamily("Consolas")
            };
            root.Children.Add(titleBar);

            // Message
            var msgBorder = new Border
            {
                Padding = new Thickness(20, 16, 20, 8),
                Child = new TextBlock
                {
                    Text         = message,
                    Foreground   = R("TextPrimary"),
                    FontSize     = 13,
                    TextWrapping = TextWrapping.Wrap
                }
            };
            root.Children.Add(msgBorder);

            // Buttons
            var btnPanel = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            // Build a minimal ControlTemplate so Background binds correctly and
            // WPF's default blue hover chrome can't override our colors.
            static ControlTemplate MakeBtnTemplate()
            {
                var bf = new FrameworkElementFactory(typeof(Border));
                bf.SetBinding(Border.BackgroundProperty,
                    new System.Windows.Data.Binding("Background")
                    { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
                bf.SetBinding(Border.BorderBrushProperty,
                    new System.Windows.Data.Binding("BorderBrush")
                    { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
                bf.SetBinding(Border.BorderThicknessProperty,
                    new System.Windows.Data.Binding("BorderThickness")
                    { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
                bf.SetBinding(Border.PaddingProperty,
                    new System.Windows.Data.Binding("Padding")
                    { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
                bf.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
                var cp = new FrameworkElementFactory(typeof(ContentPresenter));
                cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                bf.AppendChild(cp);
                return new ControlTemplate(typeof(Button)) { VisualTree = bf };
            }

            Button MakeBtn(string label, MessageBoxResult res, bool accent = false)
            {
                var bgNorm = accent ? R("AccentDim") : R("BgPanel");
                var bgHov  = accent ? R("AccentDim") : R("BgHover");
                var btn = new Button
                {
                    Content         = label,
                    Padding         = new Thickness(18, 6, 18, 6),
                    Margin          = new Thickness(8, 0, 0, 0),
                    Background      = bgNorm,
                    Foreground      = accent ? R("Accent") : R("TextPrimary"),
                    BorderBrush     = accent ? R("Accent") : R("BorderDim"),
                    BorderThickness = new Thickness(1),
                    Cursor          = Cursors.Hand,
                    FontSize        = 12,
                    Template        = MakeBtnTemplate()
                };
                btn.Click      += (_, _2) => { result = res; win.Close(); };
                btn.MouseEnter += (_, _2) => btn.Background = bgHov;
                btn.MouseLeave += (_, _2) => btn.Background = bgNorm;
                return btn;
            }

            switch (buttons)
            {
                case MessageBoxButton.OK:
                    btnPanel.Children.Add(MakeBtn("OK", MessageBoxResult.OK, accent: true));
                    break;
                case MessageBoxButton.OKCancel:
                    btnPanel.Children.Add(MakeBtn("OK",     MessageBoxResult.OK,     accent: true));
                    btnPanel.Children.Add(MakeBtn("Cancel", MessageBoxResult.Cancel));
                    break;
                case MessageBoxButton.YesNo:
                    btnPanel.Children.Add(MakeBtn("Yes", MessageBoxResult.Yes, accent: true));
                    btnPanel.Children.Add(MakeBtn("No",  MessageBoxResult.No));
                    break;
                case MessageBoxButton.YesNoCancel:
                    btnPanel.Children.Add(MakeBtn("Yes",    MessageBoxResult.Yes,    accent: true));
                    btnPanel.Children.Add(MakeBtn("No",     MessageBoxResult.No));
                    btnPanel.Children.Add(MakeBtn("Cancel", MessageBoxResult.Cancel));
                    break;
            }

            root.Children.Add(new Border
            {
                Padding = new Thickness(16, 8, 16, 16),
                Child   = btnPanel
            });

            outerBorder.Child = root;

            win.Content = outerBorder;
            win.ShowDialog();
            return result;
        }
    }
}
