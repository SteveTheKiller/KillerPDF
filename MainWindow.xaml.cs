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
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace KillerPDF
{
    public partial class MainWindow : Window
    {
        private PdfDocument? _doc;
        private string? _currentFile;
        private Point _dragStartPoint;

        // Zoom
        private double _zoomLevel = 1.0;
        private double _lastRenderZoom = 1.0;
        private const double ZoomMin = 0.25;
        private const double ZoomMax = 5.0;
        private const double ZoomStep = 0.15;
        private enum FitMode { None, Width, Page }
        private FitMode _fitMode = FitMode.None;
        private System.Windows.Threading.DispatcherTimer? _rerenderTimer;
        private System.Threading.CancellationTokenSource? _secondaryRenderCts;
        private bool _gridViewEnabled = true;
        private readonly System.Windows.Controls.Primitives.ToggleButton _gridViewToggle = null!;

        // Editing
        private EditTool _currentTool = EditTool.Select;
        private readonly Dictionary<int, List<PageAnnotation>> _annotations = [];
        private readonly Dictionary<int, (int w, int h)> _renderDims = [];

        // Undo stack — each entry is either an annotation removal or a full document snapshot.
        private enum UndoKind { Annotation, Document }
        private readonly record struct UndoEntry(UndoKind Kind, int PageIdx = -1, byte[]? DocBytes = null);
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
        private double _textFontSize = 14;
        private Color _textColor = Colors.Black;
        private Border? _textSettingsBar;

        // Signature / image resize
        private bool _isResizingSig;
        private Point _resizeSigStart;
        private double _resizeSigStartScale;
        private PlacedAnnotation? _resizeSigAnnot;
        private Rectangle? _resizeHandle;

        // Placed annotation drag-to-move
        private bool _isDraggingAnnot;
        private Point _dragAnnotStart;

        // Middle-mouse pan
        private bool _isPanning;
        private Point _panStart;
        private double _panScrollH;
        private double _panScrollV;
        private Point _dragAnnotOrigPos;
        private PlacedAnnotation? _dragAnnot;

        // Crop tool
        private Rect _cropCanvasRect;
        private Rectangle? _cropPreviewRect;
        private Border? _cropConfirmBar;
        private readonly Button _toolCropBtn = null!;
        private readonly List<Rectangle> _cropHandles = [];
        private string? _activeCropHandleTag; // "NW" | "NE" | "SE" | "SW"
        private Point _cropHandleDragStart;
        private Rect _cropRectAtHandleDrag;

        // PDF link overlays (rendered on top of the annotation canvas)
        private readonly List<Canvas> _linkOverlays = [];

        // Sidebar + multi-page view
        private bool _sidebarCollapsed;
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
        private List<SavedSignature> _savedSignatures = [];
        private SavedSignature? _pendingSignature;
        private Border? _signaturePopup;
        private static readonly string SignatureDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string SignatureFile = System.IO.Path.Combine(SignatureDir, "signatures.json");

        // Manual element refs (XAML codegen doesn't resolve these)
        private readonly Canvas _annotationCanvas = null!;
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
            _gridViewToggle = (System.Windows.Controls.Primitives.ToggleButton)FindName("GridViewToggle")!;
            LoadSignatures();
            BuildContextMenu();
            SetTool(EditTool.Select);
            ApplyGrainTexture();
            SourceInitialized += MainWindow_SourceInitialized;

            // Open a file passed via command-line / file association (e.g. double-clicking a .pdf)
            // Also show the portable badge when running outside the install location.
            Loaded += (_, _) =>
            {
                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1 && System.IO.File.Exists(args[1]))
                    OpenFile(args[1]);

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
        }

        private const int WM_GETMINMAXINFO = 0x0024;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
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

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 2;

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
                "要將 KillerPDF 安裝到你的使用者設定檔嗎？\n\n" +
                "• 建立開始功能表捷徑\n" +
                "• 加入 .pdf 檔案的「開啟方式」清單\n" +
                "• 顯示在新增/移除程式中",
                "安裝 KillerPDF", MessageBoxButton.OKCancel);
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
                    "你有尚未儲存的變更。要不儲存就關閉 KillerPDF 嗎？",
                    "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            base.OnClosing(e);
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

        private void BuildContextMenu()
        {
            var menu = new ContextMenu();

            menu.Items.Add(MakeMenuItem("複製文字", (s, e) => CopySelectedText(), "Ctrl+C"));
            menu.Items.Add(MakeMenuItem("列印", (s, e) => Print_Click(s!, e), "Ctrl+P"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("選取工具", (s, e) => SetTool(EditTool.Select)));
            menu.Items.Add(MakeMenuItem("文字工具", (s, e) => SetTool(EditTool.Text)));
            menu.Items.Add(MakeMenuItem("螢光標示工具", (s, e) => SetTool(EditTool.Highlight)));
            menu.Items.Add(MakeMenuItem("繪圖工具", (s, e) => SetTool(EditTool.Draw)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("刪除選取項目", (s, e) => DeleteSelected(), "Delete"));
            menu.Items.Add(MakeMenuItem("復原上一個動作", (s, e) => Undo_Click(s!, e), "Ctrl+Z"));
            menu.Items.Add(MakeMenuItem("清除此頁註記", (s, e) => ClearAnnotations_Click(s!, e)));

            _annotationCanvas.ContextMenu = menu;
        }

        private void PageList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_doc is null) return;
            var menu = new ContextMenu();
            menu.Items.Add(MakeMenuItem("在後方插入空白頁", (s, ev) => InsertBlankPage_Click(s!, ev)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("順時針旋轉",  (s, ev) => RotatePages_Click(90)));
            menu.Items.Add(MakeMenuItem("逆時針旋轉", (s, ev) => RotatePages_Click(-90)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("頁面上移",   (s, ev) => MoveUp_Click(s!, ev)));
            menu.Items.Add(MakeMenuItem("頁面下移", (s, ev) => MoveDown_Click(s!, ev)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("擷取頁面", (s, ev) => Split_Click(s!, ev)));
            menu.Items.Add(MakeMenuItem("刪除頁面", (s, ev) => Delete_Click(s!, ev)));
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
                foreach (var item in selected) indices.Add(PageList.Items.IndexOf(item));
                foreach (var idx in indices)
                    _doc.Pages[idx].Rotate = ((_doc.Pages[idx].Rotate + delta) % 360 + 360) % 360;
                int restoreIdx = PageList.SelectedIndex;
                SaveTempAndReload();
                PageList.SelectedIndex = Math.Min(restoreIdx, PageList.Items.Count - 1);
                SetStatus($"已旋轉 {indices.Count} 頁");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"旋轉失敗：\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
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
            try
            {
                if (_doc is not null) { _doc.Close(); _doc = null; }
                _doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
                _currentFile = path;
                FinishOpenFile(path, path);
            }
            catch (Exception ex) when (IsOwnerPasswordException(ex))
            {
                // PDF has owner/permissions restrictions but no open password —
                // open read-only so the user can still view and print it.
                try
                {
                    if (_doc is not null) { _doc.Close(); _doc = null; }
                    _doc = PdfReader.Open(path, PdfDocumentOpenMode.ReadOnly);
                    _currentFile = path;
                    FinishOpenFile(path, path);
                    SetStatus($"已開啟 {System.IO.Path.GetFileName(path)} (唯讀 - 擁有者限制) - 共 {_doc.PageCount} 頁");
                }
                catch (Exception ex2)
                {
                    KillerDialog.Show(this, $"無法開啟 PDF：\n{ex2.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex) when (IsPasswordException(ex))
            {
                string? pw = PromptForPassword(path);
                if (pw is null) return;
                try
                {
                    if (_doc is not null) { _doc.Close(); _doc = null; }
                    _doc = PdfReader.Open(path, pw, PdfDocumentOpenMode.Modify);
                    // Save a decrypted temp copy so Docnet can render without needing the password
                    var tempDec = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"killerpdf_dec_{Guid.NewGuid():N}.pdf");
                    _doc.Save(tempDec);
                    _doc.Close();
                    _doc = PdfReader.Open(tempDec, PdfDocumentOpenMode.Modify);
                    _currentFile = tempDec;
                    FinishOpenFile(path, tempDec);
                }
                catch (Exception ex2)
                {
                    KillerDialog.Show(this, $"無法開啟 PDF：\n{ex2.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"無法開啟 PDF：\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static bool IsOwnerPasswordException(Exception ex) =>
            ex.Message.IndexOf("owner", StringComparison.OrdinalIgnoreCase) >= 0 &&
            ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0;

        private void FinishOpenFile(string displayPath, string workingPath)
        {
            _currentFile = workingPath;
            FileNameLabel.Text = System.IO.Path.GetFileName(displayPath);
            _annotations.Clear();
            _undoStack.Clear();
            _renderDims.Clear();
            _allSearchRects.Clear();
            _searchResultPages.Clear();
            _searchPageCursor = -1;
            ClearSecondaryPages();
            ClearSelection();
            RefreshPageList();
            DropZone.Visibility = Visibility.Collapsed;
            PagePreviewPanel.Visibility = Visibility.Visible;
            if (_closeFileBtnRef != null) _closeFileBtnRef.IsEnabled = true;
            _pageJumpBox.IsEnabled = true;
            _gridViewToggle.IsEnabled = true;
            _pageTotalLabel.Text = $"/ {_doc!.PageCount}";
            MarkDirty(false);
            if (_doc!.PageCount > 0)
            {
                PageList.SelectedIndex = 0;
                // Auto-fit to width once the first page has rendered and layout has settled.
                // DispatcherPriority.Background is lower than Loaded, so this fires after
                // all pending RenderPage / RefreshPageView callbacks have completed.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    (Action)FitToWidth);
            }
            SetStatus($"已開啟 {System.IO.Path.GetFileName(displayPath)} - 共 {_doc.PageCount} 頁");
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
                Title = "需要密碼",
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
                Text = $"「{System.IO.Path.GetFileName(filename)}」受到密碼保護。",
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
            var pwBox = new PasswordBox { Margin = new Thickness(0, 0, 0, 14) };
            sp.Children.Add(pwBox);
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = "開啟", Width = 76, Margin = new Thickness(0, 0, 8, 0) };
            var cancelBtn = new Button { Content = "取消", Width = 76 };
            okBtn.Click += (s, ev) => { result = pwBox.Password; win.DialogResult = true; };
            cancelBtn.Click += (s, ev) => { win.DialogResult = false; };
            pwBox.KeyDown += (s, ev) => { if (ev.Key == Key.Enter) { result = pwBox.Password; win.DialogResult = true; } };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);
            sp.Children.Add(btnRow);
            win.Content = sp;
            return win.ShowDialog() == true ? result : null;
        }

        private void RefreshPageList()
        {
            PageList.Items.Clear();
            if (_doc is null || _currentFile is null) return;

            try
            {
                using var docReader = DocLib.Instance.GetDocReader(_currentFile, new PageDimensions(256, 256));
                for (int i = 0; i < _doc.PageCount; i++)
                {
                    BitmapSource? thumb = null;
                    try
                    {
                        using var pr = docReader.GetPageReader(i);
                        int tw = pr.GetPageWidth();
                        int th = pr.GetPageHeight();
                        var raw = pr.GetImage();
                        if (tw > 0 && th > 0 && raw != null && raw.Length > 0)
                        {
                            var wb = new WriteableBitmap(tw, th, 96, 96, PixelFormats.Bgra32, null);
                            wb.WritePixels(new Int32Rect(0, 0, tw, th), raw, tw * 4, 0);
                            wb.Freeze();
                            thumb = wb;
                        }
                    }
                    catch { /* thumbnail failed, show text fallback */ }

                    var img = new Image
                    {
                        Source = thumb,
                        Width = 140,
                        Height = thumb is not null ? 140.0 * thumb.PixelHeight / thumb.PixelWidth : 100,
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(0, 0, 0, 2)
                    };

                    var label = new TextBlock
                    {
                        Text = $"第 {i + 1} 頁",
                        Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 10,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };

                    var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                    if (thumb is not null)
                    {
                        var border = new Border
                        {
                            Background = Brushes.White,
                            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                            BorderThickness = new Thickness(1),
                            Child = img
                        };
                        panel.Children.Add(border);
                    }
                    else
                    {
                        panel.Children.Add(new TextBlock
                        {
                            Text = $"第 {i + 1} 頁",
                            Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                            FontFamily = new FontFamily("Consolas"),
                            FontSize = 13,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(0, 20, 0, 20)
                        });
                    }
                    panel.Children.Add(label);
                    PageList.Items.Add(panel);
                }
            }
            catch
            {
                // Fallback to plain text list
                for (int i = 0; i < _doc.PageCount; i++)
                    PageList.Items.Add($"第 {i + 1} 頁");
            }
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

                if (width <= 0 || height <= 0 || rawBytes == null || rawBytes.Length == 0)
                {
                    PageImage.Source = null;
                    SetStatus($"第 {pageIndex + 1} 頁無法轉譯");
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
                ClearSelection();
                ClearSecondaryPages();
                RenderAllAnnotations(pageIndex);
                SetStatus($"第 {pageIndex + 1} / {_doc.PageCount} 頁");
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
                SetStatus($"轉譯錯誤：{ex.Message}");
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
            int pagesPerRow = Math.Max(1, (int)(availablePreZoom / pageSlotW));
            double panelW = pagesPerRow * pageSlotW;
            if (panelW > 0) _pageContentPanel.Width = panelW;

            // Cancel any previously running secondary render.
            _secondaryRenderCts?.Cancel();
            _secondaryRenderCts = new System.Threading.CancellationTokenSource();
            var cts = _secondaryRenderCts;

            // Secondary pages: fixed 1536 px cap regardless of DPI/zoom.
            const int SecondaryMax = 1536;
            const int MaxSecondaryPages = 25;
            int limit = Math.Min(_doc.PageCount, primaryPageIdx + 1 + MaxSecondaryPages);
            if (limit <= primaryPageIdx + 1) return;

            string currentFile = _currentFile;

            // Render pixels on a background thread — this is the slow Docnet work.
            List<(int pi, int w, int h, byte[] rawBytes)> pages;
            try
            {
                pages = await System.Threading.Tasks.Task.Run(() =>
                {
                    var results = new List<(int, int, int, byte[])>();
                    using var docReader = DocLib.Instance.GetDocReader(currentFile, new PageDimensions(SecondaryMax, SecondaryMax));
                    for (int i = primaryPageIdx + 1; i < limit; i++)
                    {
                        if (cts.IsCancellationRequested) break;
                        using var pageReader = docReader.GetPageReader(i);
                        int w = pageReader.GetPageWidth();
                        int h = pageReader.GetPageHeight();
                        var rawBytes = pageReader.GetImage();
                        if (w > 0 && h > 0 && rawBytes is not null)
                            results.Add((i, w, h, rawBytes));
                    }
                    return results;
                }, cts.Token);
            }
            catch { return; }

            // Bail if the render was cancelled or the document was closed.
            // Do NOT check PageList.SelectedIndex here — the CTS cancellation already
            // handles stale renders when the user navigates, and checking SelectedIndex
            // causes false bails when rapid zoom dispatches settle on the correct page.
            if (cts.IsCancellationRequested || _doc is null || !_gridViewEnabled)
                return;

            // Create WPF elements back on the UI thread.
            // Match the primary page's DIP width so all pages appear the same size in the grid.
            double primaryDipW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 595;

            foreach (var (pi, w, h, rawBytes) in pages)
            {
                if (cts.IsCancellationRequested) break;

                // Scale secondary DIP size to match primary canvas width.
                // Bitmap DPI is set so the lower-res pixels fill exactly primaryDipW DIPs.
                int pageDipW = (int)Math.Round(primaryDipW);
                int pageDipH = (int)Math.Round(primaryDipW * h / w);
                double bitmapDpiX = 96.0 * w / pageDipW;
                double bitmapDpiY = 96.0 * h / pageDipH;

                // Do NOT overwrite _renderDims if the page was already rendered as
                // primary — its annotation coordinate mapping must stay intact.
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
                    Cursor = Cursors.Hand,
                    ToolTip = $"第 {pi + 1} 頁 - 點一下前往"
                };
                int capturedPi = pi;
                overlay.PreviewMouseLeftButtonDown += (_, _) => PageList.SelectedIndex = capturedPi;

                var pageGrid = new Grid();
                pageGrid.Children.Add(img);
                pageGrid.Children.Add(overlay);
                AddSecondaryPageLinks(pi, pageGrid, pageDipW, pageDipH);

                _pageContentPanel.Children.Add(new Border
                {
                    Background = Brushes.White,
                    Margin = new Thickness(0, 0, 12, 12),
                    Child = pageGrid
                });
            }
        }

        private void SetStatus(string text) => StatusText.Text = text;

        /// <summary>
        /// Re-renders secondary pages and then link overlays for the current page.
        /// Must be called via Dispatcher so layout is settled before RenderAdditionalPages
        /// reads ActualWidth. All zoom-change and sidebar-toggle dispatch sites use this
        /// instead of a bare RenderAdditionalPages call so link overlays are never left
        /// cleared without being re-added.
        /// </summary>
        private void RefreshPageView(int pageIndex)
        {
            if (_gridViewEnabled)
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

        private void GridViewToggle_Click(object sender, RoutedEventArgs e)
        {
            _gridViewEnabled = _gridViewToggle.IsChecked == true;
            int idx = PageList.SelectedIndex;
            if (idx < 0) return;
            if (_gridViewEnabled)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () => RefreshPageView(idx));
            }
            else
            {
                _secondaryRenderCts?.Cancel();
                ClearSecondaryPages();
                if (_pageContentPanel is not null)
                    _pageContentPanel.Width = double.NaN;
            }
        }

        // ============================================================
        // PDF Link Annotation Overlays
        // ============================================================

        private readonly record struct LinkInfo(double Cx, double Cy, double Cw, double Ch, object Tag, string Tip);

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
                    string tip = targetPage.HasValue ? $"前往第 {targetPage.Value + 1} 頁" : uri!;
                    links.Add(new LinkInfo(cx, cy, cw, ch, tag, tip));
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
                var overlay = new Canvas
                {
                    Width            = lnk.Cw,
                    Height           = lnk.Ch,
                    Background       = Brushes.Transparent,
                    Cursor           = Cursors.Hand,
                    ToolTip          = lnk.Tip,
                    Tag              = lnk.Tag,
                    IsHitTestVisible = true,
                };
                Canvas.SetLeft(overlay, lnk.Cx);
                Canvas.SetTop(overlay, lnk.Cy);

                _annotationCanvas.Children.Add(overlay);
                _linkOverlays.Add(overlay);
            }

            if (links.Count > 0)
                SetStatus($"第 {pageIndex + 1} / {_doc.PageCount} 頁  ({links.Count} 個連結)");
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
        ///
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

        private void SetTool(EditTool tool)
        {
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
            var green = (SolidColorBrush)FindResource("AccentGreen");
            var greenDim = (SolidColorBrush)FindResource("AccentGreenDim");
            var text = (SolidColorBrush)FindResource("TextPrimary");

            foreach (var (btn, t) in map)
            {
                btn.Background = t == tool ? greenDim : Brushes.Transparent;
                btn.Foreground = t == tool ? green : text;
            }

            _annotationCanvas.Cursor = tool switch
            {
                EditTool.Text => Cursors.IBeam,
                EditTool.Highlight => Cursors.Cross,
                EditTool.Draw => Cursors.Pen,
                EditTool.Signature => Cursors.Hand,
                EditTool.Image => Cursors.Hand,
                EditTool.Crop => Cursors.Cross,
                _ => Cursors.Arrow
            };

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
                _sidebarBorder.Visibility = Visibility.Collapsed;
                _sidebarCol.Width = new GridLength(24);
                _sidebarCol.MinWidth = 24;
                _sidebarToggleBtn.Content = "\uE76C"; // ChevronRight (Segoe MDL2)
                _sidebarToggleBtn.ToolTip = "展開側邊欄";
            }
            else
            {
                _sidebarBorder.Visibility = Visibility.Visible;
                _sidebarCol.Width = new GridLength(180);
                _sidebarCol.MinWidth = 24;
                _sidebarToggleBtn.Content = "\uE76B"; // ChevronLeft (Segoe MDL2)
                _sidebarToggleBtn.ToolTip = "收合側邊欄";
            }
            if (PageList.SelectedIndex >= 0)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () => RefreshPageView(PageList.SelectedIndex));
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

        private void ShowCropConfirmBar()
        {
            HideCropConfirmBar();
            if (_doc is null) return;

            int currentPage = PageList.SelectedIndex;
            bool multiPage = _doc.PageCount > 1;

            var bar = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 30, 30, 30)),
                BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6)
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            var label = new TextBlock
            {
                Text = "套用裁切到：",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            panel.Children.Add(label);

            var btnStyle = new Style(typeof(Button));
            btnStyle.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromArgb(40, 74, 222, 128))));
            btnStyle.Setters.Add(new Setter(Button.ForegroundProperty, (SolidColorBrush)FindResource("AccentGreen")));
            btnStyle.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(1)));
            btnStyle.Setters.Add(new Setter(Button.BorderBrushProperty, (SolidColorBrush)FindResource("AccentGreen")));
            btnStyle.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(10, 4, 10, 4)));
            btnStyle.Setters.Add(new Setter(Button.MarginProperty, new Thickness(0, 0, 6, 0)));
            btnStyle.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
            btnStyle.Setters.Add(new Setter(Button.FontFamilyProperty, new FontFamily("Segoe UI")));
            btnStyle.Setters.Add(new Setter(Button.FontSizeProperty, 12.0));

            var thisPageBtn = new Button { Content = "此頁", Style = btnStyle, ToolTip = "裁切此頁 (Enter)" };
            thisPageBtn.Click += (_, _) => ApplyCrop([currentPage]);
            panel.Children.Add(thisPageBtn);

            if (multiPage)
            {
                var allPagesBtn = new Button { Content = "全部頁面", Style = btnStyle };
                allPagesBtn.Click += (_, _) => ApplyCrop([..Enumerable.Range(0, _doc.PageCount)]);
                panel.Children.Add(allPagesBtn);
            }

            // "Remove Crop" — only shown if current page already has a CropBox
            bool hasCropBox = _doc.Pages[currentPage].Elements.ContainsKey("/CropBox");
            if (hasCropBox)
            {
                var dimBtnStyle = new Style(typeof(Button), btnStyle);
                dimBtnStyle.Setters.Add(new Setter(Button.ForegroundProperty,
                    new SolidColorBrush(Color.FromRgb(0xff, 0x80, 0x80))));
                dimBtnStyle.Setters.Add(new Setter(Button.BorderBrushProperty,
                    new SolidColorBrush(Color.FromRgb(0xff, 0x80, 0x80))));

                var removeBtn = new Button { Content = "移除裁切", Style = dimBtnStyle,
                    ToolTip = multiPage ? "移除此頁的 CropBox" : "移除現有 CropBox" };
                removeBtn.Click += (_, _) => RemoveCropBox([currentPage]);
                panel.Children.Add(removeBtn);

                if (multiPage)
                {
                    var removeAllBtn = new Button { Content = "全部移除", Style = dimBtnStyle,
                        ToolTip = "移除所有頁面的 CropBox" };
                    removeAllBtn.Click += (_, _) => RemoveCropBox([..Enumerable.Range(0, _doc.PageCount)]);
                    panel.Children.Add(removeAllBtn);
                }
            }

            var cancelBtn = new Button
            {
                Content = "取消",
                Style = btnStyle,
                ToolTip = "取消 (Escape)",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                BorderBrush = (SolidColorBrush)FindResource("TextSecondary"),
                Background = Brushes.Transparent
            };
            cancelBtn.Click += (_, _) => HideCropConfirmBar();
            panel.Children.Add(cancelBtn);

            bar.Child = panel;
            _cropConfirmBar = bar;

            _annotationCanvas.Children.Add(bar);
            AddCropHandles();
            RepositionCropConfirmBar();
        }

        private void HideCropConfirmBar()
        {
            if (_cropConfirmBar is not null)
            {
                _annotationCanvas.Children.Remove(_cropConfirmBar);
                _cropConfirmBar = null;
            }
            if (_cropPreviewRect is not null)
            {
                _annotationCanvas.Children.Remove(_cropPreviewRect);
                _cropPreviewRect = null;
            }
            RemoveCropHandles();
        }

        private void AddCropHandles()
        {
            RemoveCropHandles();
            const double hSize = 8;
            var tags    = new[] { "NW", "NE", "SE", "SW" };
            var cursors = new[] { Cursors.SizeNWSE, Cursors.SizeNESW, Cursors.SizeNWSE, Cursors.SizeNESW };
            var green   = (SolidColorBrush)FindResource("AccentGreen");

            for (int i = 0; i < 4; i++)
            {
                var h = new Rectangle
                {
                    Width = hSize, Height = hSize,
                    Fill = green,
                    Stroke = Brushes.White,
                    StrokeThickness = 1,
                    Tag = tags[i],
                    Cursor = cursors[i],
                };
                _cropHandles.Add(h);
                _annotationCanvas.Children.Add(h);
            }
            PositionCropHandles();
        }

        private void RemoveCropHandles()
        {
            foreach (var h in _cropHandles)
                _annotationCanvas.Children.Remove(h);
            _cropHandles.Clear();
            _activeCropHandleTag = null;
        }

        private void PositionCropHandles()
        {
            if (_cropHandles.Count < 4) return;
            const double hSize = 8;
            var corners = new (double x, double y)[]
            {
                (_cropCanvasRect.X - hSize / 2,       _cropCanvasRect.Y - hSize / 2),
                (_cropCanvasRect.Right - hSize / 2,   _cropCanvasRect.Y - hSize / 2),
                (_cropCanvasRect.Right - hSize / 2,   _cropCanvasRect.Bottom - hSize / 2),
                (_cropCanvasRect.X - hSize / 2,       _cropCanvasRect.Bottom - hSize / 2),
            };
            for (int i = 0; i < 4; i++)
            {
                Canvas.SetLeft(_cropHandles[i], corners[i].x);
                Canvas.SetTop(_cropHandles[i],  corners[i].y);
            }
        }

        private void UpdateCropRectVisuals()
        {
            if (_cropPreviewRect is null) return;
            Canvas.SetLeft(_cropPreviewRect, _cropCanvasRect.X);
            Canvas.SetTop(_cropPreviewRect,  _cropCanvasRect.Y);
            _cropPreviewRect.Width  = _cropCanvasRect.Width;
            _cropPreviewRect.Height = _cropCanvasRect.Height;
            PositionCropHandles();
            RepositionCropConfirmBar();
        }

        private void RepositionCropConfirmBar()
        {
            if (_cropConfirmBar is null) return;
            const double barHeight = 38;
            double barLeft     = Math.Max(4, _cropCanvasRect.X);
            double barTopBelow = _cropCanvasRect.Y + _cropCanvasRect.Height + 8;
            double barTopAbove = _cropCanvasRect.Y - barHeight - 8;
            double barTop = barTopBelow + barHeight < _annotationCanvas.ActualHeight
                ? barTopBelow : Math.Max(4, barTopAbove);
            Canvas.SetLeft(_cropConfirmBar, barLeft);
            Canvas.SetTop(_cropConfirmBar,  barTop);
        }

        private void ApplyCrop(int[] pageIndices)
        {
            if (_doc is null || _currentFile is null) { SetStatus("裁切：尚未開啟文件"); return; }
            int currentPage = PageList.SelectedIndex;
            if (currentPage < 0) { SetStatus("裁切：尚未選取頁面"); return; }
            if (!_renderDims.ContainsKey(currentPage)) { SetStatus("裁切：頁面尺寸尚不可用"); return; }

            try
            {
                PushDocUndo();

                var (renderW, renderH) = _renderDims[currentPage];
                var cr = _cropCanvasRect;

                foreach (int pi in pageIndices)
                {
                    if (pi < 0 || pi >= _doc.PageCount) continue;
                    var page = _doc.Pages[pi];
                    double pdfW = page.Width.Point;
                    double pdfH = page.Height.Point;

                    // Convert canvas rect (top-left origin) to PDF rect (bottom-left origin, points)
                    double x1 = cr.X * pdfW / renderW;
                    double y1 = pdfH - (cr.Y + cr.Height) * pdfH / renderH;
                    double x2 = (cr.X + cr.Width) * pdfW / renderW;
                    double y2 = pdfH - cr.Y * pdfH / renderH;

                    // Clamp to media box
                    x1 = Math.Max(0, x1); y1 = Math.Max(0, y1);
                    x2 = Math.Min(pdfW, x2); y2 = Math.Min(pdfH, y2);

                    // Write CropBox directly into the page dictionary — more reliable than the
                    // CropBox property setter across PdfSharpCore versions.
                    var arr = new PdfSharpCore.Pdf.PdfArray();
                    arr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(x1));
                    arr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(y1));
                    arr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(x2));
                    arr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(y2));
                    page.Elements["/CropBox"] = arr;
                }

                HideCropConfirmBar();
                SetTool(EditTool.Select);
                SaveTempAndReload();
                SetStatus($"已裁切 {pageIndices.Length} 頁");
            }
            catch (Exception ex)
            {
                SetStatus($"裁切失敗：{ex.Message}");
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
                }
                HideCropConfirmBar();
                SetTool(EditTool.Select);
                SaveTempAndReload();
                SetStatus($"已從 {pageIndices.Length} 頁移除 CropBox");
            }
            catch (Exception ex)
            {
                SetStatus($"移除裁切失敗：{ex.Message}");
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
            panel.Children.Add(new TextBlock
            {
                Text = "顏色：",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });

            // Color swatches
            var activeColor = tool == EditTool.Draw ? _drawColor : Color.FromRgb(_highlightColor.R, _highlightColor.G, _highlightColor.B);
            foreach (var color in SwatchColors)
            {
                var swatch = new Border
                {
                    Width = 18, Height = 18,
                    Background = new SolidColorBrush(color),
                    BorderBrush = color == activeColor
                        ? (SolidColorBrush)FindResource("AccentGreen")
                        : new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                    BorderThickness = new Thickness(color == activeColor ? 2 : 1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Tag = color
                };
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
            panel.Children.Add(new Rectangle
            {
                Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(8, 2, 8, 2)
            });

            // Size slider (draw only)
            if (tool == EditTool.Draw)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "大小：",
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                });
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
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0)
                };
                sizeSlider.ValueChanged += (s, e) => sizeLabel.Text = $"{e.NewValue:F0}px";
                panel.Children.Add(sizeLabel);

                // Separator
                panel.Children.Add(new Rectangle
                {
                    Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                    Margin = new Thickness(8, 2, 8, 2)
                });
            }

            // Opacity slider
            panel.Children.Add(new TextBlock
            {
                Text = "透明度：",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });
            byte currentOpacity = tool == EditTool.Draw ? _drawOpacity : _highlightColor.A;
            var opacitySlider = new Slider
            {
                Minimum = 10, Maximum = 255, Value = currentOpacity,
                Width = 80, VerticalAlignment = VerticalAlignment.Center
            };
            var opacityLabel = new TextBlock
            {
                Text = $"{(int)(currentOpacity / 255.0 * 100)}%",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0)
            };
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
                Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)),
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Child = panel,
                Margin = new Thickness(8, 0, 0, 0)
            };

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

        private void ShowTextSettings()
        {
            HideTextSettings();

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };

            // Font size label
            panel.Children.Add(new TextBlock
            {
                Text = "大小：",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });

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
                    _textFontSize = v;
            };
            sizeBox.LostFocus += (_, _) =>
            {
                if (double.TryParse(sizeBox.Text, out double v) && v > 0)
                    _textFontSize = v;
            };
            panel.Children.Add(sizeBox);

            // Separator
            panel.Children.Add(new Rectangle
            {
                Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(8, 2, 8, 2)
            });

            // Color label
            panel.Children.Add(new TextBlock
            {
                Text = "顏色：",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });

            // Color swatches (reuse same palette as draw tool)
            foreach (var color in SwatchColors)
            {
                var c = color;
                var swatch = new Border
                {
                    Width = 18, Height = 18,
                    Background = new SolidColorBrush(c),
                    BorderBrush = (c.R == _textColor.R && c.G == _textColor.G && c.B == _textColor.B)
                        ? (SolidColorBrush)FindResource("AccentGreen")
                        : new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                    BorderThickness = new Thickness(
                        (c.R == _textColor.R && c.G == _textColor.G && c.B == _textColor.B) ? 2 : 1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(1),
                    Cursor = Cursors.Hand
                };
                swatch.MouseLeftButtonDown += (_, _) => { _textColor = c; ShowTextSettings(); };
                panel.Children.Add(swatch);
            }

            _textSettingsBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)),
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Child = panel,
                Margin = new Thickness(8, 0, 0, 0)
            };

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

        private void LoadSignatures()
        {
            try
            {
                if (File.Exists(SignatureFile))
                {
                    var json = File.ReadAllText(SignatureFile);
                    _savedSignatures = JsonSerializer.Deserialize<List<SavedSignature>>(json) ?? [];
                }
            }
            catch { _savedSignatures = []; }
        }

        private void PersistSignatures()
        {
            try
            {
                Directory.CreateDirectory(SignatureDir);
                var json = JsonSerializer.Serialize(_savedSignatures, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SignatureFile, json);
            }
            catch { /* best effort */ }
        }

        private void ShowSignaturePopup()
        {
            HideSignaturePopup();

            var stack = new StackPanel { Margin = new Thickness(4) };

            // Title
            stack.Children.Add(new TextBlock
            {
                Text = "簽名",
                Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(4, 2, 4, 6)
            });

            // Saved signatures
            if (_savedSignatures.Count > 0)
            {
                var scroll = new ScrollViewer
                {
                    MaxHeight = 260,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                var listPanel = new StackPanel();

                foreach (var sig in _savedSignatures)
                {
                    var sigCopy = sig; // capture for lambda
                    var item = new Border
                    {
                        Background = Brushes.White,
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
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
                        catch { item.Child = new TextBlock { Text = "(圖片)", IsHitTestVisible = false }; }
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
                        SetStatus("請在頁面上點一下以放置簽名");
                    };
                    item.MouseEnter += (s, e) =>
                        ((Border)s!).BorderBrush = (SolidColorBrush)FindResource("AccentGreen");
                    item.MouseLeave += (s, e) =>
                        ((Border)s!).BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));

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
                        _savedSignatures.Remove(sigCopy);
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
                    Text = "沒有已儲存的簽名",
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
                Content = "建立簽名",
                Style = (Style)FindResource("DarkButton"),
                Background = (SolidColorBrush)FindResource("AccentGreenDim"),
                Foreground = (SolidColorBrush)FindResource("AccentGreen"),
                BorderBrush = (SolidColorBrush)FindResource("AccentGreenDim"),
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
                Content = "匯入圖片",
                Style = (Style)FindResource("DarkButton"),
                Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x3a, 0x2e)),
                Foreground = (SolidColorBrush)FindResource("AccentGreen"),
                BorderBrush = (SolidColorBrush)FindResource("AccentGreenDim"),
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
                Title = "建立簽名",
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
                Text       = "建立簽名",
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
                Text = "在這裡手寫簽名",
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
                Content = "清除",
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
                Content = "儲存簽名",
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
                    KillerDialog.Show(this, "請先畫出簽名。", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                double cw = drawCanvas.ActualWidth > 0 ? drawCanvas.ActualWidth : 400;
                double ch = drawCanvas.ActualHeight > 0 ? drawCanvas.ActualHeight : 150;

                var saved = new SavedSignature
                {
                    CanvasWidth = cw,
                    CanvasHeight = ch,
                    Name = $"簽名 {_savedSignatures.Count + 1}"
                };
                foreach (var stroke in strokes)
                {
                    var sPts = stroke.Select(p => new SerializablePoint { X = p.X, Y = p.Y }).ToList();
                    saved.Strokes.Add(sPts);
                }
                _savedSignatures.Add(saved);
                PersistSignatures();

                // Auto-select the new signature for placement
                _pendingSignature = saved;
                _annotationCanvas.Cursor = Cursors.Cross;
                SetStatus("簽名已儲存 - 請在頁面上點一下以放置");

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
                Filter = "圖片檔案|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有檔案|*.*",
                Title = "匯入簽名圖片"
            };
            if (dlg.ShowDialog() != true) return;

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
                _savedSignatures.Add(saved);
                PersistSignatures();

                _pendingSignature = saved;
                _annotationCanvas.Cursor = Cursors.Cross;
                SetStatus("圖片已載入 - 請在頁面上點一下以放置");
                ShowSignaturePopup(); // refresh to show the new entry
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"匯入圖片失敗：\n{ex.Message}", "KillerPDF",
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
            SetStatus("簽名已放置 - 拖曳可移動，使用角落控制點可調整大小");
        }

        private void PlaceImageFromDialog(Point pos, int pageIdx)
        {
            var dlg = new OpenFileDialog
            {
                Title = "插入圖片",
                Filter = "圖片檔案|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff;*.tif|所有檔案|*.*"
            };
            if (dlg.ShowDialog() != true) return;

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

                // Default scale: fit within 250 canvas pixels on the longest axis
                const double MaxCanvasDim = 250;
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

                AddAnnotation(imgAnnot);
                RenderAllAnnotations(pageIdx);
                double w = srcW * scale;
                double h = srcH * scale;
                SelectAnnotation(imgAnnot, new Rect(pos.X, pos.Y, w, h));
                SetStatus("圖片已放置 - 拖曳角落控制點可調整大小，切換到選取工具可移動/刪除");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"無法載入圖片：\n{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ============================================================
        // Canvas interaction
        // ============================================================

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_doc is null) return;
            // Don't intercept clicks on an active text editing box
            if (_activeTextBox is not null && e.OriginalSource is DependencyObject src &&
                IsDescendantOf(src, _activeTextBox))
                return;
            // Don't intercept clicks on the crop confirm bar (canvas uses Preview events which
            // tunnel before child Button clicks fire — we must not swallow them here).
            if (_cropConfirmBar is not null && e.OriginalSource is DependencyObject cropSrc &&
                IsDescendantOf(cropSrc, _cropConfirmBar))
                return;
            // Check if click lands inside a PDF link overlay.
            // We do an explicit bounds check rather than relying on WPF hit-testing through
            // nested transparent canvases, which is unreliable.
            if (_linkOverlays.Count > 0)
            {
                var clickPos = e.GetPosition(_annotationCanvas);
                foreach (var lo in _linkOverlays)
                {
                    double lx = Canvas.GetLeft(lo);
                    double ly = Canvas.GetTop(lo);
                    if (clickPos.X >= lx && clickPos.X <= lx + lo.Width &&
                        clickPos.Y >= ly && clickPos.Y <= ly + lo.Height)
                    {
                        if (lo.Tag is int tp)
                            PageList.SelectedIndex = tp;
                        else if (lo.Tag is string u)
                            try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
                        e.Handled = true;
                        return;
                    }
                }
            }
            var pos = e.GetPosition(_annotationCanvas);
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;

            // Crop corner handle — must be checked before the tool switch so the normal
            // Crop mousedown path (which calls HideCropConfirmBar) doesn't remove handles first.
            if (_cropHandles.Count > 0 && e.OriginalSource is Rectangle cropHandleRect &&
                _cropHandles.Contains(cropHandleRect))
            {
                _activeCropHandleTag = (string)cropHandleRect.Tag;
                _cropHandleDragStart = pos;
                _cropRectAtHandleDrag = _cropCanvasRect;
                _annotationCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            // Check if click is on the resize handle (signature or image annotation)
            if (_resizeHandle is not null && _selectedAnnotation is PlacedAnnotation rsa)
            {
                double hx = Canvas.GetLeft(_resizeHandle);
                double hy = Canvas.GetTop(_resizeHandle);
                if (pos.X >= hx && pos.X <= hx + _resizeHandle.Width &&
                    pos.Y >= hy && pos.Y <= hy + _resizeHandle.Height)
                {
                    _isResizingSig = true;
                    _resizeSigStart = pos;
                    _resizeSigStartScale = rsa.Scale;
                    _resizeSigAnnot = rsa;
                    _annotationCanvas.CaptureMouse();
                    e.Handled = true;
                    return;
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
                                if (pageAnnotsList[i] is PlacedAnnotation pa &&
                                    HitTestAnnotation(pa, pos, out Rect paBounds))
                                {
                                    ClearSelection();
                                    RenderAllAnnotations(pageIdx);
                                    SelectAnnotation(pa, paBounds);
                                    _isDraggingAnnot = true;
                                    _dragAnnotStart = pos;
                                    _dragAnnotOrigPos = pa.Position;
                                    _dragAnnot = pa;
                                    _annotationCanvas.CaptureMouse();
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
                            _annotationCanvas.Children.Add(_selectRect);
                            _annotationCanvas.CaptureMouse();
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
                    _annotationCanvas.Children.Add(rect);
                    _activePreview = rect;
                    _annotationCanvas.CaptureMouse();
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
                    _annotationCanvas.Children.Add(poly);
                    _activePreview = poly;
                    _annotationCanvas.CaptureMouse();
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
                    _cropPreviewRect = new Rectangle
                    {
                        Stroke = (SolidColorBrush)FindResource("AccentGreen"),
                        StrokeThickness = 2,
                        StrokeDashArray = [5, 3],
                        Fill = new SolidColorBrush(Color.FromArgb(20, 74, 222, 128)),
                        Width = 0, Height = 0,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(_cropPreviewRect, pos.X);
                    Canvas.SetTop(_cropPreviewRect, pos.Y);
                    _annotationCanvas.Children.Add(_cropPreviewRect);
                    _activePreview = _cropPreviewRect;
                    _annotationCanvas.CaptureMouse();
                    break;
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(_annotationCanvas);
            pos.X = Math.Max(0, Math.Min(_annotationCanvas.ActualWidth, pos.X));
            pos.Y = Math.Max(0, Math.Min(_annotationCanvas.ActualHeight, pos.Y));

            // Signature resize drag
            if (_isResizingSig && _resizeSigAnnot is not null)
            {
                double dx = pos.X - _resizeSigStart.X;
                double dy = pos.Y - _resizeSigStart.Y;
                double delta = (Math.Abs(dx) > Math.Abs(dy) ? dx : dy);
                double newScale = Math.Max(0.05, _resizeSigStartScale + delta / _resizeSigAnnot.SourceWidth);
                _resizeSigAnnot.Scale = newScale;

                // Update selection border and handle position live
                double newW = _resizeSigAnnot.SourceWidth * newScale;
                double newH = _resizeSigAnnot.SourceHeight * newScale;
                if (_selectionBorder is not null)
                {
                    _selectionBorder.Width  = newW + 8;
                    _selectionBorder.Height = newH + 8;
                }
                if (_resizeHandle is not null)
                {
                    double hx = _resizeSigAnnot.Position.X + newW - 4 - _resizeHandle.Width / 2;
                    double hy = _resizeSigAnnot.Position.Y + newH - 4 - _resizeHandle.Height / 2;
                    Canvas.SetLeft(_resizeHandle, hx);
                    Canvas.SetTop(_resizeHandle, hy);
                }

                // Re-render annotations to show updated size
                RenderAllAnnotations(_resizeSigAnnot.PageIndex);
                // Restore selection visuals (RenderAllAnnotations clears canvas children including our overlays)
                _annotationCanvas.Children.Add(_selectionBorder!);
                _annotationCanvas.Children.Add(_resizeHandle!);
                return;
            }

            // Annotation drag-to-move
            if (_isDraggingAnnot && _dragAnnot is not null)
            {
                double dx = pos.X - _dragAnnotStart.X;
                double dy = pos.Y - _dragAnnotStart.Y;
                _dragAnnot.Position = new Point(_dragAnnotOrigPos.X + dx, _dragAnnotOrigPos.Y + dy);
                double w = _dragAnnot.SourceWidth * _dragAnnot.Scale;
                double h = _dragAnnot.SourceHeight * _dragAnnot.Scale;
                if (_selectionBorder is not null)
                {
                    Canvas.SetLeft(_selectionBorder, _dragAnnot.Position.X - 4);
                    Canvas.SetTop(_selectionBorder, _dragAnnot.Position.Y - 4);
                }
                if (_resizeHandle is not null)
                {
                    Canvas.SetLeft(_resizeHandle, _dragAnnot.Position.X + w - 4 - _resizeHandle.Width / 2);
                    Canvas.SetTop(_resizeHandle, _dragAnnot.Position.Y + h - 4 - _resizeHandle.Height / 2);
                }
                RenderAllAnnotations(_dragAnnot.PageIndex);
                _annotationCanvas.Children.Add(_selectionBorder!);
                _annotationCanvas.Children.Add(_resizeHandle!);
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

            // Crop corner handle drag — resize the crop rect live.
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
            }
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
                _annotationCanvas.ReleaseMouseCapture();
                return;
            }

            // Finish annotation drag-to-move
            if (_isDraggingAnnot)
            {
                _isDraggingAnnot = false;
                _annotationCanvas.ReleaseMouseCapture();
                if (_dragAnnot is not null)
                {
                    var da = _dragAnnot;
                    _dragAnnot = null;
                    RenderAllAnnotations(da.PageIndex);
                    double w = da.SourceWidth * da.Scale;
                    double h = da.SourceHeight * da.Scale;
                    SelectAnnotation(da, new Rect(da.Position.X, da.Position.Y, w, h));
                    MarkDirty();
                }
                return;
            }

            // Finish signature resize
            if (_isResizingSig)
            {
                _isResizingSig = false;
                _annotationCanvas.ReleaseMouseCapture();
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
                _annotationCanvas.ReleaseMouseCapture();
                var pos = e.GetPosition(_annotationCanvas);
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
            _annotationCanvas.ReleaseMouseCapture();

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
                        _annotationCanvas.Children.Remove(rect);
                    }
                    break;

                case EditTool.Draw when _activeInk is not null:
                    if (_activeInk.Points.Count > 2)
                    {
                        AddAnnotation(_activeInk);
                    }
                    else
                    {
                        _annotationCanvas.Children.Remove(_activePreview);
                    }
                    _activeInk = null;
                    break;

                case EditTool.Crop when _activePreview is Rectangle cr:
                    if (cr.Width > 10 && cr.Height > 10)
                    {
                        _cropCanvasRect = new Rect(Canvas.GetLeft(cr), Canvas.GetTop(cr), cr.Width, cr.Height);
                        _activePreview = null; // keep the preview rect visible; don't null it
                        ShowCropConfirmBar();
                        return;
                    }
                    else
                    {
                        _annotationCanvas.Children.Remove(cr);
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
                        VisualTreeHelper.GetDpi(_annotationCanvas).PixelsPerDip);
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

        private void SelectAnnotation(PageAnnotation annot, Rect bounds)
        {
            _selectedAnnotation = annot;
            _selectionBorder = new Border
            {
                BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Color.FromArgb(20, 74, 222, 128)),
                Width = bounds.Width + 8,
                Height = bounds.Height + 8,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(_selectionBorder, bounds.X - 4);
            Canvas.SetTop(_selectionBorder, bounds.Y - 4);
            _annotationCanvas.Children.Add(_selectionBorder);

            // Add resize handle for placed annotations (signature, image) — bottom-right corner
            if (annot is PlacedAnnotation)
            {
                const double hSize = 10;
                _resizeHandle = new Rectangle
                {
                    Width = hSize, Height = hSize,
                    Fill = (SolidColorBrush)FindResource("AccentGreen"),
                    Stroke = Brushes.White, StrokeThickness = 1,
                    Cursor = Cursors.SizeNWSE,
                    IsHitTestVisible = true
                };
                Canvas.SetLeft(_resizeHandle, bounds.X + bounds.Width - 4 - hSize / 2);
                Canvas.SetTop(_resizeHandle, bounds.Y + bounds.Height - 4 - hSize / 2);
                _annotationCanvas.Children.Add(_resizeHandle);
                string label = annot is SignatureAnnotation ? "簽名" : "圖片";
                SetStatus($"已選取{label} - 拖曳角落控制點可調整大小，按 Delete 可移除");
            }
            else
            {
                SetStatus($"已選取註記 - 按 Delete 可移除");
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

        private void ClearSelection()
        {
            if (_selectionBorder is not null)
            {
                _annotationCanvas.Children.Remove(_selectionBorder);
                _selectionBorder = null;
            }
            if (_resizeHandle is not null)
            {
                _annotationCanvas.Children.Remove(_resizeHandle);
                _resizeHandle = null;
            }
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
            SetStatus("已刪除選取註記");
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
                _selectedText = page.Text;
                if (string.IsNullOrWhiteSpace(_selectedText))
                {
                    SetStatus("此頁找不到文字");
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
                SetStatus("已選取所有文字並複製到剪貼簿");
            }
            catch (Exception ex)
            {
                SetStatus($"全選文字錯誤：{ex.Message}");
            }
        }

        private void CopySelectedText()
        {
            if (!string.IsNullOrEmpty(_selectedText))
            {
                Clipboard.SetText(_selectedText);
                SetStatus("已複製到剪貼簿");
            }
            else
            {
                SetStatus("尚未選取文字 - 請拖曳選取文字");
            }
        }

        private void ClearTextSelection()
        {
            if (_selectRect is not null)
            {
                _annotationCanvas.Children.Remove(_selectRect);
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
                    .OrderByDescending(w => w.BoundingBox.Top)
                    .ThenBy(w => w.BoundingBox.Left)
                    .ToList();

                if (words.Count == 0)
                {
                    SetStatus("選取範圍內找不到文字");
                    ClearTextSelection();
                    return;
                }

                // Group into lines by Y proximity
                var lines = new List<List<UglyToad.PdfPig.Content.Word>>();
                double lastY = double.MaxValue;
                foreach (var w in words)
                {
                    double wy = w.BoundingBox.Top;
                    if (Math.Abs(wy - lastY) > 3)
                    {
                        lines.Add([]);
                        lastY = wy;
                    }
                    lines[^1].Add(w);
                }

                _selectedText = string.Join("\n",
                    lines.Select(line => string.Join(" ", line.Select(w => w.Text))));

                Clipboard.SetText(_selectedText);
                int wordCount = words.Count;
                SetStatus($"已複製 {wordCount} 個字到剪貼簿");
            }
            catch (Exception ex)
            {
                SetStatus($"文字擷取錯誤：{ex.Message}");
                ClearTextSelection();
            }
        }

        // ============================================================
        // Search (Ctrl+F)
        // ============================================================

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
                    BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
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
                    ToolTip = "關閉搜尋 (Esc)"
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
            if (_searchStatus != null) _searchStatus.Text = "Enter = 下一個  Shift+Enter = 上一個";
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
                string lowerQuery = query.ToLowerInvariant();
                int totalHits = 0;

                using var pigDoc = PdfPigDoc.Open(_currentFile);
                for (int pi = 0; pi < pigDoc.NumberOfPages; pi++)
                {
                    var page = pigDoc.GetPage(pi + 1);
                    var hits = FindMatchesOnPage(page, lowerQuery);
                    if (hits.Count > 0)
                    {
                        _allSearchRects[pi] = hits;
                        _searchResultPages.Add(pi);
                        totalHits += hits.Count;
                    }
                }

                if (_searchResultPages.Count == 0)
                {
                    if (_searchStatus != null) _searchStatus.Text = "沒有符合項目";
                    return;
                }

                // Start from current page or the first page with results
                int startPage = PageList.SelectedIndex;
                _searchPageCursor = _searchResultPages.FindIndex(p => p >= startPage);
                if (_searchPageCursor < 0) _searchPageCursor = 0;

                if (_searchStatus != null)
                    _searchStatus.Text = totalHits == 1
                        ? $"1 個符合項目 ({_searchResultPages.Count} 頁)"
                        : $"{totalHits} 個符合項目 ({_searchResultPages.Count} 頁)";

                int targetPage = _searchResultPages[_searchPageCursor];
                if (PageList.SelectedIndex != targetPage)
                    PageList.SelectedIndex = targetPage;  // triggers SelectionChanged -> HighlightSearchResultsOnCurrentPage
                else
                    HighlightSearchResultsOnCurrentPage();
            }
            catch
            {
                if (_searchStatus != null) _searchStatus.Text = "搜尋錯誤";
            }
        }

        private static List<(double left, double bottom, double right, double top)> FindMatchesOnPage(
            UglyToad.PdfPig.Content.Page page, string lowerQuery)
        {
            var result = new List<(double left, double bottom, double right, double top)>();
            var words = page.GetWords().ToList();

            for (int i = 0; i < words.Count; i++)
            {
                if (words[i].Text.ToLowerInvariant().Contains(lowerQuery))
                {
                    var bb = words[i].BoundingBox;
                    result.Add((bb.Left, bb.Bottom, bb.Right, bb.Top));
                    continue;
                }

                // Multi-word match
                string combined = words[i].Text;
                for (int j = i + 1; j < words.Count && combined.Length < lowerQuery.Length + 20; j++)
                {
                    combined += " " + words[j].Text;
                    if (combined.ToLowerInvariant().Contains(lowerQuery))
                    {
                        double minX = double.MaxValue, minY = double.MaxValue;
                        double maxX = double.MinValue, maxY = double.MinValue;
                        for (int k = i; k <= j; k++)
                        {
                            var wbb = words[k].BoundingBox;
                            minX = Math.Min(minX, wbb.Left);
                            minY = Math.Min(minY, wbb.Bottom);
                            maxX = Math.Max(maxX, wbb.Right);
                            maxY = Math.Max(maxY, wbb.Top);
                        }
                        result.Add((minX, minY, maxX, maxY));
                        break;
                    }
                }
            }
            return result;
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
                        BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
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
                    _annotationCanvas.Children.Add(retb);
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
                    _annotationCanvas.Children.Insert(_annotationCanvas.Children.IndexOf(retb), rewo);
                    retb.KeyDown += EditTextBox_KeyDown;
                    retb.Loaded += (s, ev) => { retb.Focus(); Keyboard.Focus(retb); retb.SelectAll(); retb.LostFocus += EditTextBox_LostFocus; };
                    SetStatus("正在重新編輯文字 - Enter 儲存，Escape 取消");
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

                if (canvasWords.Count == 0) { SetStatus("沒有可選取的文字 - 此頁可能是掃描影像"); return; }

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
                    SetStatus("此位置找不到文字行");
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
                    BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
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
                _annotationCanvas.Children.Add(tb);
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
                int tbIdx = _annotationCanvas.Children.IndexOf(tb);
                _annotationCanvas.Children.Insert(tbIdx, whiteout);

                tb.KeyDown += EditTextBox_KeyDown;
                tb.Loaded += (s, ev) =>
                {
                    tb.Focus();
                    Keyboard.Focus(tb);
                    tb.SelectAll();
                    tb.LostFocus += EditTextBox_LostFocus;
                };

                SetStatus("正在編輯文字 - Enter 儲存，Escape 取消");
            }
            catch (Exception ex)
            {
                SetStatus($"文字編輯錯誤：{ex.Message}");
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
            _annotationCanvas.Children.Remove(tb);
            // Remove the whiteout rectangle
            var whiteout = _annotationCanvas.Children.OfType<Rectangle>()
                .FirstOrDefault(r => r.Tag is string s && s == "EditWhiteout");
            if (whiteout is not null)
                _annotationCanvas.Children.Remove(whiteout);
            SetStatus("已取消文字編輯");
        }

        private void CommitTextEdit()
        {
            if (_activeTextBox is null || _activeTextBox.Tag is not TextEditContext ctx) return;
            var tb = _activeTextBox;
            _activeTextBox = null;
            string newText = tb.Text.Trim();
            _annotationCanvas.Children.Remove(tb);

            // Remove the whiteout rectangle
            var whiteout = _annotationCanvas.Children.OfType<Rectangle>()
                .FirstOrDefault(r => r.Tag is string s && s == "EditWhiteout");
            if (whiteout is not null)
                _annotationCanvas.Children.Remove(whiteout);

            if (string.IsNullOrEmpty(newText) || newText == ctx.OriginalText)
            {
                SetStatus(newText == ctx.OriginalText ? "沒有變更" : "已取消文字編輯 (空白)");
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
            SetStatus($"文字已編輯：「{ctx.OriginalText}」->「{newText}」");
        }

        // ============================================================
        // Text box handling
        // ============================================================

        private void PlaceTextBox(Point pos, int pageIdx)
        {
            var tb = new TextBox
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
                Foreground = new SolidColorBrush(_textColor),
                BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = _textFontSize,
                MinWidth = 120,
                MinHeight = 24,
                Padding = new Thickness(2),
                AcceptsReturn = true,
                Tag = pageIdx
            };
            Canvas.SetLeft(tb, pos.X);
            Canvas.SetTop(tb, pos.Y);
            _annotationCanvas.Children.Add(tb);
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
                    _annotationCanvas.Children.Remove(_activeTextBox);
                    _activeTextBox = null;
                }
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
                Dispatcher.BeginInvoke(new Action(CommitActiveTextBox),
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

            string content = tb.Text.Trim();
            int pageIdx = tb.Tag is int idx ? idx : PageList.SelectedIndex;
            double x = Canvas.GetLeft(tb);
            double y = Canvas.GetTop(tb);

            _annotationCanvas.Children.Remove(tb);

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
        }

        // ============================================================
        // Keyboard shortcuts
        // ============================================================

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            // Don't intercept keys when typing in a TextBox
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
        }

        // ============================================================
        // Annotation management
        // ============================================================

        private void AddAnnotation(PageAnnotation annotation)
        {
            if (!_annotations.ContainsKey(annotation.PageIndex))
                _annotations[annotation.PageIndex] = [];
            _annotations[annotation.PageIndex].Add(annotation);
            _undoStack.Push(new UndoEntry(UndoKind.Annotation, annotation.PageIndex));
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
            _undoStack.Push(new UndoEntry(UndoKind.Document, DocBytes: ms.ToArray()));
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
            _annotationCanvas.Children.Add(tb);
        }

        private void RenderAllAnnotations(int pageIndex)
        {
            _annotationCanvas.Children.Clear();
            if (!_annotations.ContainsKey(pageIndex)) return;

            foreach (var annot in _annotations[pageIndex])
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
                        _annotationCanvas.Children.Add(rect);
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
                        _annotationCanvas.Children.Add(poly);
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
                        _annotationCanvas.Children.Add(wo);
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
                        _annotationCanvas.Children.Add(etb);
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
                                _annotationCanvas.Children.Add(imgCtrl);
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
                                _annotationCanvas.Children.Add(sigPoly);
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
                            _annotationCanvas.Children.Add(iaCtrl);
                        }
                        catch { /* skip broken image */ }
                        break;
                }
            }
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (_undoStack.Count == 0)
            {
                SetStatus("沒有可復原的動作");
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
                SetStatus("已復原上一個註記");
            }
            else // Document snapshot
            {
                if (entry.DocBytes is null) return;
                int selectedIdx = PageList.SelectedIndex;
                var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    $"killerpdf_undo_{Guid.NewGuid():N}.pdf");
                System.IO.File.WriteAllBytes(tempPath, entry.DocBytes);
                _doc?.Close();
                _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
                _currentFile = tempPath;
                _annotations.Clear();
                _renderDims.Clear();
                ClearSelection();
                MarkDirty();
                RefreshPageList();
                if (selectedIdx >= 0 && selectedIdx < PageList.Items.Count)
                    PageList.SelectedIndex = selectedIdx;
                else if (PageList.Items.Count > 0)
                    PageList.SelectedIndex = 0;
                SetStatus("已復原文件變更");
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
            SetStatus("已清除此頁註記");
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
                    : (SolidColorBrush)FindResource("AccentGreen");
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
                    "你有尚未儲存的變更。要不儲存就關閉此檔案嗎？",
                    "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
            }
            _doc.Close();
            _doc = null;
            _currentFile = null;
            _annotations.Clear();
            _undoStack.Clear();
            _renderDims.Clear();
            _allSearchRects.Clear();
            _searchResultPages.Clear();
            _searchPageCursor = -1;
            PageList.Items.Clear();
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
            _gridViewToggle.IsEnabled = false;
            _pageJumpBox.Text = "";
            _pageTotalLabel.Text = "/ –";
            MarkDirty(false);
            SetStatus("就緒");
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
                    "你有尚未儲存的變更。要捨棄並建立新文件嗎？",
                    "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
            }

            try
            {
                var newDoc = new PdfDocument();
                newDoc.AddPage(); // one blank A4 page

                var tempPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"KillerPDF_new_{Guid.NewGuid():N}.pdf");
                newDoc.Save(tempPath);
                newDoc.Close();

                _doc?.Close();
                _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
                FinishOpenFile("未命名.pdf", tempPath);
                SetStatus("已建立空白文件");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"無法建立新文件：\n{ex.Message}",
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "PDF 檔案|*.pdf", Title = "開啟 PDF" };
            if (dlg.ShowDialog() == true) OpenFile(dlg.FileName);
        }

        private void Merge_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, "請先開啟 PDF。"); return; }
            var doc = _doc;
            var dlg = new OpenFileDialog { Filter = "PDF 檔案|*.pdf", Title = "選取要合併的 PDF", Multiselect = true };
            if (dlg.ShowDialog() != true) return;
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
                SetStatus($"已合併 {dlg.FileNames.Length} 個檔案 - 共 {_doc?.PageCount} 頁");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"合併失敗：\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
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
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, "請先開啟 PDF。"); return; }
            var currentFile = _currentFile;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) { KillerDialog.Show(this, "請選取要擷取的頁面。"); return; }
            var dlg = new SaveFileDialog { Filter = "PDF 檔案|*.pdf", Title = "將擷取頁面另存為" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var indices = new List<int>();
                foreach (var item in selected) indices.Add(PageList.Items.IndexOf(item));
                using var importDoc = PdfReader.Open(currentFile, PdfDocumentOpenMode.Import);
                var newDoc = new PdfDocument();
                foreach (var idx in indices.OrderBy(i => i))
                    newDoc.AddPage(importDoc.Pages[idx]);
                newDoc.Save(dlg.FileName);
                SetStatus($"已將 {indices.Count} 頁擷取到 {System.IO.Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"擷取失敗：\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, "請先開啟 PDF。"); return; }
            var doc = _doc;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) { KillerDialog.Show(this, "請選取要刪除的頁面。"); return; }
            var result = KillerDialog.Show(this, $"要刪除 {selected.Count} 頁嗎？", "KillerPDF",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            try
            {
                var indices = new List<int>();
                foreach (var item in selected) indices.Add(PageList.Items.IndexOf(item));
                foreach (var idx in indices.OrderByDescending(i => i))
                    doc.Pages.RemoveAt(idx);
                SaveTempAndReload();
                SetStatus($"已刪除 {indices.Count} 頁 - 剩餘 {_doc?.PageCount} 頁");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"刪除失敗：\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InsertBlankPage_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, "請先開啟 PDF。"); return; }
            var doc = _doc;
            int insertAfter = PageList.SelectedIndex >= 0 ? PageList.SelectedIndex : doc.PageCount - 1;
            try
            {
                var blank = new PdfPage { Width = XUnit.FromPoint(595), Height = XUnit.FromPoint(842) };
                doc.Pages.Insert(insertAfter + 1, blank);
                SaveTempAndReload();
                PageList.SelectedIndex = insertAfter + 1;
                SetStatus($"已在第 {insertAfter + 2} 頁位置插入空白頁");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"插入失敗：\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
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
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, "請先開啟 PDF。"); return; }
            CommitActiveTextBox();
            try
            {
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);

                if (hasAnnotations)
                {
                    // Save a clean copy of the doc (without burned annotations), burn
                    // annotations into the on-disk file, then restore the in-memory doc
                    // from the clean copy so future saves don't double-burn.
                    var tempClean = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        $"killerpdf_clean_{Guid.NewGuid():N}.pdf");
                    _doc.Save(tempClean);
                    DrawAnnotationsOnDocument();
                    _doc.Save(_currentFile);
                    _doc.Close();
                    _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                    _currentFile = tempClean;
                }
                else
                {
                    _doc.Save(_currentFile);
                }

                MarkDirty(false);
                SetStatus($"已儲存 - {System.IO.Path.GetFileName(_currentFile)}");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"儲存失敗：\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, "請先開啟 PDF。"); return; }
            CommitActiveTextBox();
            var dlg = new SaveFileDialog { Filter = "PDF 檔案|*.pdf", Title = "將 PDF 另存為" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);

                if (hasAnnotations)
                {
                    var tempClean = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        $"killerpdf_clean_{Guid.NewGuid():N}.pdf");
                    _doc.Save(tempClean);
                    DrawAnnotationsOnDocument();
                    _doc.Save(dlg.FileName);
                    _doc.Close();
                    _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                    _currentFile = tempClean;
                    MarkDirty(false);
                    SetStatus($"已將註記儲存到 {System.IO.Path.GetFileName(dlg.FileName)}");
                }
                else
                {
                    _doc.Save(dlg.FileName);
                    MarkDirty(false);
                    SetStatus($"已儲存到 {System.IO.Path.GetFileName(dlg.FileName)}");
                }
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"儲存失敗：\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveFlattened_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, "請先開啟 PDF。"); return; }
            CommitActiveTextBox();
            var dlg = new SaveFileDialog { Filter = "PDF 檔案|*.pdf", Title = "儲存扁平化 PDF" };
            if (dlg.ShowDialog() != true) return;
            SetStatus("正在扁平化...");
            try
            {
                // Burn any pending annotations into a temp source for rasterization
                string sourcePath;
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
                if (hasAnnotations)
                {
                    var tempClean = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"killerpdf_clean_{Guid.NewGuid():N}.pdf");
                    var tempBurned = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"killerpdf_burned_{Guid.NewGuid():N}.pdf");
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
                    var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"killerpdf_src_{Guid.NewGuid():N}.pdf");
                    _doc.Save(temp);
                    sourcePath = temp;
                }

                int pageCount = _doc.PageCount;

                // Calculate max render dimensions across all pages at 150 DPI
                int maxW = 1, maxH = 1;
                for (int i = 0; i < pageCount; i++)
                {
                    var p = _doc.Pages[i];
                    int pw = (int)(p.Width.Point * 150 / 72.0);
                    int ph = (int)(p.Height.Point * 150 / 72.0);
                    if (pw > maxW) maxW = pw;
                    if (ph > maxH) maxH = ph;
                }

                using var outDoc = new PdfDocument();
                using (var docReader = DocLib.Instance.GetDocReader(sourcePath, new PageDimensions(maxW, maxH)))
                {
                    for (int i = 0; i < pageCount; i++)
                    {
                        using var pr = docReader.GetPageReader(i);
                        var bgra = pr.GetImage();
                        int rw = pr.GetPageWidth();
                        int rh = pr.GetPageHeight();

                        // Encode rendered BGRA pixels to PNG in memory
                        var bmp = new WriteableBitmap(rw, rh, 96, 96, PixelFormats.Bgra32, null);
                        bmp.WritePixels(new Int32Rect(0, 0, rw, rh), bgra, rw * 4, 0);
                        byte[] pngBytes;
                        using (var ms = new MemoryStream())
                        {
                            var enc = new PngBitmapEncoder();
                            enc.Frames.Add(BitmapFrame.Create(bmp));
                            enc.Save(ms);
                            pngBytes = ms.ToArray();
                        }

                        // Add page at original PDF dimensions, fill with rasterized image
                        var origPage = _doc.Pages[i];
                        var newPage = outDoc.AddPage();
                        newPage.Width = origPage.Width;
                        newPage.Height = origPage.Height;
                        using var xi = XImage.FromStream(() => new MemoryStream(pngBytes));
                        using var gfx = XGraphics.FromPdfPage(newPage);
                        gfx.DrawImage(xi, 0, 0, newPage.Width.Point, newPage.Height.Point);
                    }
                }

                outDoc.Save(dlg.FileName);
                MarkDirty(false);
                SetStatus($"扁平化 PDF 已儲存到 {System.IO.Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"扁平化失敗：\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Print_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, "請先開啟 PDF。"); return; }
            CommitActiveTextBox();
            try
            {
                var dlg = new PrintDialog();
                if (dlg.ShowDialog() != true) return;

                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
                string printPath;
                string? tempFlattened = null;

                if (hasAnnotations)
                {
                    var tempClean = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        $"killerpdf_clean_{Guid.NewGuid():N}.pdf");
                    _doc.Save(tempClean);
                    DrawAnnotationsOnDocument();
                    printPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        $"killerpdf_print_{Guid.NewGuid():N}.pdf");
                    _doc.Save(printPath);
                    tempFlattened = printPath;
                    // Restore clean state
                    _doc.Close();
                    _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                    _currentFile = tempClean;
                }
                else
                {
                    printPath = _currentFile;
                }

                var fixedDoc = new System.Windows.Documents.FixedDocument();
                int pageCount = _doc.PageCount;

                using (var docReader = DocLib.Instance.GetDocReader(printPath, new PageDimensions(1536, 1536)))
                {
                    for (int i = 0; i < pageCount; i++)
                    {
                        using var pr = docReader.GetPageReader(i);
                        int w = pr.GetPageWidth();
                        int h = pr.GetPageHeight();
                        var raw = pr.GetImage();

                        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                        bmp.WritePixels(new Int32Rect(0, 0, w, h), raw, w * 4, 0);
                        bmp.Freeze();

                        double scaleX = dlg.PrintableAreaWidth / w;
                        double scaleY = dlg.PrintableAreaHeight / h;
                        double scale = Math.Min(scaleX, scaleY);
                        double pw = w * scale;
                        double ph = h * scale;

                        var fp = new System.Windows.Documents.FixedPage
                        {
                            Width = dlg.PrintableAreaWidth,
                            Height = dlg.PrintableAreaHeight
                        };
                        var img = new System.Windows.Controls.Image { Source = bmp, Width = pw, Height = ph };
                        System.Windows.Documents.FixedPage.SetLeft(img, (dlg.PrintableAreaWidth - pw) / 2);
                        System.Windows.Documents.FixedPage.SetTop(img, (dlg.PrintableAreaHeight - ph) / 2);
                        fp.Children.Add(img);
                        fp.Measure(new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight));
                        fp.Arrange(new Rect(new Point(), new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight)));

                        var pc = new System.Windows.Documents.PageContent();
                        ((System.Windows.Markup.IAddChild)pc).AddChild(fp);
                        fixedDoc.Pages.Add(pc);
                    }
                }

                if (tempFlattened != null)
                    try { System.IO.File.Delete(tempFlattened); } catch { }

                dlg.PrintDocument(fixedDoc.DocumentPaginator, "KillerPDF");
                SetStatus("已送出列印");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"列印失敗：\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ============================================================
        // Save annotations to PDF
        // ============================================================

        private void DrawAnnotationsOnDocument()
        {
            if (_doc is null) return;

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
        // Temp save/reload
        // ============================================================

        private void SaveTempAndReload()
        {
            if (_doc is null || _currentFile is null) return;
            _annotations.Clear();
            _renderDims.Clear();
            ClearSelection();
            MarkDirty();
            var doc = _doc;
            int selectedIdx = PageList.SelectedIndex;
            var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"killerpdf_temp_{Guid.NewGuid():N}.pdf");
            doc.Save(tempPath);
            doc.Close();
            _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
            _currentFile = tempPath;
            RefreshPageList();
            if (selectedIdx >= 0 && selectedIdx < PageList.Items.Count)
                PageList.SelectedIndex = selectedIdx;
            else if (PageList.Items.Count > 0)
                PageList.SelectedIndex = 0;
        }

        // ============================================================
        // Zoom
        // ============================================================

        private void PagePreview_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                _fitMode = FitMode.None;
                _zoomLevel = e.Delta > 0
                    ? Math.Min(ZoomMax, _zoomLevel + ZoomStep)
                    : Math.Max(ZoomMin, _zoomLevel - ZoomStep);
                ApplyZoom();
                SyncZoomBox();
                SetStatus($"第 {PageList.SelectedIndex + 1} / {_doc?.PageCount} 頁 - {_zoomLevel * 100:F0}%");
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
            if ((atTop && e.Delta > 0) || (atBottom && e.Delta < 0))
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

        private void ResetZoom()
        {
            _zoomLevel = 1.0;
            ApplyZoom();
        }

        private void SyncZoomBox()
        {
            if (_zoomBox is null) return;
            string target = $"{_zoomLevel * 100:F0}%";
            _zoomBox.SelectionChanged -= ZoomBox_SelectionChanged;
            foreach (ComboBoxItem item in _zoomBox.Items)
            {
                if (item.Content?.ToString() == target)
                {
                    _zoomBox.SelectedItem = item;
                    _zoomBox.SelectionChanged += ZoomBox_SelectionChanged;
                    return;
                }
            }
            // No preset match — clear dropdown selection and show free-form percentage
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
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, z));
                ApplyZoom();
                if (PageList.SelectedIndex >= 0 && _doc != null)
                    SetStatus($"第 {PageList.SelectedIndex + 1} / {_doc.PageCount} 頁 - {_zoomLevel * 100:F0}%");
            }
        }

        private void FitToWidth()
        {
            if (PageImage.Source is null || PageImage.ActualWidth <= 0) return;
            double viewW = PagePreviewPanel.ActualWidth - 40;
            if (viewW <= 0) return;
            _fitMode = FitMode.Width;
            _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, viewW / PageImage.ActualWidth));
            ApplyZoom();
            if (PageList.SelectedIndex >= 0 && _doc != null)
                SetStatus($"第 {PageList.SelectedIndex + 1} / {_doc.PageCount} 頁 - 符合寬度 ({_zoomLevel * 100:F0}%)");
        }

        private void FitToPage()
        {
            if (PageImage.Source is null || PageImage.ActualWidth <= 0 || PageImage.ActualHeight <= 0) return;
            double viewW = PagePreviewPanel.ActualWidth  - 40;
            double viewH = PagePreviewPanel.ActualHeight - 40;
            if (viewW <= 0 || viewH <= 0) return;
            _fitMode = FitMode.Page;
            _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax,
                Math.Min(viewW / PageImage.ActualWidth, viewH / PageImage.ActualHeight)));
            ApplyZoom();
            if (PageList.SelectedIndex >= 0 && _doc != null)
                SetStatus($"第 {PageList.SelectedIndex + 1} / {_doc.PageCount} 頁 - 符合頁面 ({_zoomLevel * 100:F0}%)");
        }

        private void PagePreviewPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_fitMode == FitMode.Width) FitToWidth();
            else if (_fitMode == FitMode.Page) FitToPage();
        }

        private void PagePreviewPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            _isPanning = true;
            _panStart   = e.GetPosition(PagePreviewPanel);
            _panScrollH = PagePreviewPanel.HorizontalOffset;
            _panScrollV = PagePreviewPanel.VerticalOffset;
            PagePreviewPanel.CaptureMouse();
            PagePreviewPanel.Cursor = Cursors.SizeAll;
            e.Handled = true;
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
            if (e.ChangedButton != MouseButton.Middle || !_isPanning) return;
            _isPanning = false;
            PagePreviewPanel.ReleaseMouseCapture();
            PagePreviewPanel.Cursor = Cursors.Arrow;
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

        private void PageList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // The ListBox's internal ScrollViewer is disabled, so wheel events don't
            // scroll anything. Forward them to the outer SidebarScrollViewer manually.
            SidebarScrollViewer.ScrollToVerticalOffset(
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
                PagePreviewPanel.ScrollToTop();
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

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }

    // ============================================================
    // Themed dialog — replaces MessageBox for dark-UI consistency
    // ============================================================
    internal static class KillerDialog
    {
        private static readonly System.Windows.Media.Color _green     = System.Windows.Media.Color.FromRgb(0x4a, 0xde, 0x80);
        private static readonly System.Windows.Media.Color _dark      = System.Windows.Media.Color.FromRgb(0x1a, 0x1a, 0x1a);
        private static readonly System.Windows.Media.Color _panel     = System.Windows.Media.Color.FromRgb(0x24, 0x24, 0x24);
        private static readonly System.Windows.Media.Color _text      = System.Windows.Media.Color.FromRgb(0xe0, 0xe0, 0xe0);
        private static readonly System.Windows.Media.Color _border    = System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33);
        private static readonly System.Windows.Media.Color _greenDim  = System.Windows.Media.Color.FromRgb(0x22, 0x54, 0x3d);
        private static readonly System.Windows.Media.Color _greenHov  = System.Windows.Media.Color.FromRgb(0x2d, 0x6a, 0x4f);
        private static readonly System.Windows.Media.Color _hover     = System.Windows.Media.Color.FromRgb(0x2e, 0x2e, 0x2e);

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
                Background      = new SolidColorBrush(_dark),
                BorderBrush     = new SolidColorBrush(_greenDim),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6)
            };

            var root = new StackPanel();

            // Title bar
            var titleBar = new Border
            {
                Background   = new SolidColorBrush(_panel),
                Padding      = new Thickness(16, 10, 16, 10),
                CornerRadius = new CornerRadius(5, 5, 0, 0)
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            titleBar.Child = new TextBlock
            {
                Text       = title,
                Foreground = new SolidColorBrush(_green),
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
                    Foreground   = new SolidColorBrush(_text),
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
                var bgNorm = accent ? new SolidColorBrush(_greenDim) : new SolidColorBrush(_panel);
                var bgHov  = accent ? new SolidColorBrush(_greenHov) : new SolidColorBrush(_hover);
                var btn = new Button
                {
                    Content         = label,
                    Padding         = new Thickness(18, 6, 18, 6),
                    Margin          = new Thickness(8, 0, 0, 0),
                    Background      = bgNorm,
                    Foreground      = accent ? new SolidColorBrush(_green) : new SolidColorBrush(_text),
                    BorderBrush     = accent ? new SolidColorBrush(_green) : new SolidColorBrush(_border),
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
                    btnPanel.Children.Add(MakeBtn("確定", MessageBoxResult.OK, accent: true));
                    break;
                case MessageBoxButton.OKCancel:
                    btnPanel.Children.Add(MakeBtn("確定",   MessageBoxResult.OK,     accent: true));
                    btnPanel.Children.Add(MakeBtn("取消",   MessageBoxResult.Cancel));
                    break;
                case MessageBoxButton.YesNo:
                    btnPanel.Children.Add(MakeBtn("是", MessageBoxResult.Yes, accent: true));
                    btnPanel.Children.Add(MakeBtn("否", MessageBoxResult.No));
                    break;
                case MessageBoxButton.YesNoCancel:
                    btnPanel.Children.Add(MakeBtn("是",     MessageBoxResult.Yes,    accent: true));
                    btnPanel.Children.Add(MakeBtn("否",     MessageBoxResult.No));
                    btnPanel.Children.Add(MakeBtn("取消",   MessageBoxResult.Cancel));
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
