using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        private const double ZoomMin = 0.25;
        private const double ZoomMax = 5.0;
        private const double ZoomStep = 0.15;

        // Editing
        private EditTool _currentTool = EditTool.Select;
        private readonly Dictionary<int, List<PageAnnotation>> _annotations = new();
        private readonly Dictionary<int, (int w, int h)> _renderDims = new();
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

        // Text selection
        private bool _isSelecting;
        private Point _selectStart;
        private Rectangle? _selectRect;
        private string? _selectedText;

        // Search
        private Border? _searchBar;
        private TextBox? _searchBox;
        private TextBlock? _searchStatus;
        private readonly List<Rect> _searchHighlights = new();

        // Signatures
        private List<SavedSignature> _savedSignatures = new();
        private SavedSignature? _pendingSignature;
        private Border? _signaturePopup;
        private static readonly string SignatureDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string SignatureFile = System.IO.Path.Combine(SignatureDir, "signatures.json");

        // Manual element refs (XAML codegen doesn't resolve these)
        private Canvas _annotationCanvas = null!;
        private Grid _pageContentGrid = null!;
        private Button _toolSelectBtn = null!;
        private Button _toolTextBtn = null!;
        private Button _toolHighlightBtn = null!;
        private Button _toolDrawBtn = null!;
        private Button _toolSignatureBtn = null!;

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
            LoadSignatures();
            BuildContextMenu();
            SetTool(EditTool.Select);
        }

        // ============================================================
        // Window chrome
        // ============================================================

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) MaximizeBtn_Click(sender, e);
            else DragMove();
        }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void MaximizeBtn_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        // ============================================================
        // Context menu
        // ============================================================

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
            menu.Items.Add(MakeMenuItem("Delete Selected", (s, e) => DeleteSelected(), "Delete"));
            menu.Items.Add(MakeMenuItem("Undo Last", (s, e) => Undo_Click(s!, e), "Ctrl+Z"));
            menu.Items.Add(MakeMenuItem("Clear Page Annotations", (s, e) => ClearAnnotations_Click(s!, e)));

            _annotationCanvas.ContextMenu = menu;
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
                FileNameLabel.Text = System.IO.Path.GetFileName(path);
                _annotations.Clear();
                _renderDims.Clear();
                ClearSelection();
                RefreshPageList();
                DropZone.Visibility = Visibility.Collapsed;
                PagePreviewPanel.Visibility = Visibility.Visible;
                if (_doc.PageCount > 0) PageList.SelectedIndex = 0;
                SetStatus($"Opened {System.IO.Path.GetFileName(path)} - {_doc.PageCount} page(s)");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open PDF:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
                        Text = $"Page {i + 1}",
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
                            Text = $"Page {i + 1}",
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
                    PageList.Items.Add($"Page {i + 1}");
            }
        }

        private void RenderPage(int pageIndex)
        {
            if (_currentFile is null || _doc is null) return;
            try
            {
                using var docReader = DocLib.Instance.GetDocReader(_currentFile, new PageDimensions(1536, 1536));
                using var pageReader = docReader.GetPageReader(pageIndex);

                int width = pageReader.GetPageWidth();
                int height = pageReader.GetPageHeight();
                var rawBytes = pageReader.GetImage();

                if (width <= 0 || height <= 0 || rawBytes == null || rawBytes.Length == 0)
                {
                    PageImage.Source = null;
                    SetStatus($"Page {pageIndex + 1} - could not render");
                    return;
                }

                _renderDims[pageIndex] = (width, height);

                var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                bitmap.WritePixels(new Int32Rect(0, 0, width, height), rawBytes, width * 4, 0);

                PageImage.Source = bitmap;
                _annotationCanvas.Width = width;
                _annotationCanvas.Height = height;
                ClearSelection();
                RenderAllAnnotations(pageIndex);
                SetStatus($"Page {pageIndex + 1} of {_doc.PageCount}");
            }
            catch (Exception ex)
            {
                PageImage.Source = null;
                SetStatus($"Render error: {ex.Message}");
            }
        }

        private void SetStatus(string text) => StatusText.Text = text;

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
                (_toolSignatureBtn, EditTool.Signature)
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
                _ => Cursors.Arrow
            };

            // Show/hide draw settings bar
            if (tool == EditTool.Draw || tool == EditTool.Highlight)
                ShowDrawSettings(tool);
            else
                HideDrawSettings();

            // Hide signature popup when switching away
            if (tool != EditTool.Signature)
            {
                HideSignaturePopup();
                _pendingSignature = null;
            }
        }

        private void ToolSelect_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Select);
        private void ToolText_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Text);
        private void ToolHighlight_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Highlight);
        private void ToolDraw_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Draw);
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
        // Draw/Highlight settings bar
        // ============================================================

        private static readonly Color[] SwatchColors = new[]
        {
            Colors.Red, Colors.SaddleBrown, Colors.Orange, Colors.Gold,
            Colors.LimeGreen, Colors.DodgerBlue, Colors.MediumPurple,
            Colors.DeepPink, Colors.White, Colors.Black
        };

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
                Text = "Color:",
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
                    Text = "Size:",
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
                Text = "Opacity:",
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
        // Signatures
        // ============================================================

        private void LoadSignatures()
        {
            try
            {
                if (File.Exists(SignatureFile))
                {
                    var json = File.ReadAllText(SignatureFile);
                    _savedSignatures = JsonSerializer.Deserialize<List<SavedSignature>>(json) ?? new();
                }
            }
            catch { _savedSignatures = new(); }
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
                Text = "Signatures",
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
                    var canvas = new Canvas
                    {
                        Width = 210, Height = 50,
                        Background = Brushes.Transparent,
                        IsHitTestVisible = false
                    };
                    RenderSignaturePreview(canvas, sigCopy, 210, 50);
                    item.Child = canvas;

                    item.MouseLeftButtonDown += (s, e) =>
                    {
                        _pendingSignature = sigCopy;
                        HideSignaturePopup();
                        _annotationCanvas.Cursor = Cursors.Cross;
                        SetStatus("Click on the page to place your signature");
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
                        Content = "\u2715",
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
                Background = (SolidColorBrush)FindResource("AccentGreenDim"),
                Foreground = (SolidColorBrush)FindResource("AccentGreen"),
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                Cursor = Cursors.Hand,
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
                Width = 460, Height = 260,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a))
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Drawing canvas
            var canvasBorder = new Border
            {
                Background = Brushes.White,
                Margin = new Thickness(12, 12, 12, 4),
                CornerRadius = new CornerRadius(4)
            };
            var drawCanvas = new Canvas
            {
                Background = Brushes.White,
                ClipToBounds = true,
                Cursor = Cursors.Pen
            };
            canvasBorder.Child = drawCanvas;
            Grid.SetRow(canvasBorder, 0);
            grid.Children.Add(canvasBorder);

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
                currentStroke = new List<Point>();
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
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0)),
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Segoe UI"),
                Cursor = Cursors.Hand
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
                Padding = new Thickness(16, 6, 16, 6),
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x54, 0x3d)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)),
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand
            };
            saveBtn.Click += (s, e) =>
            {
                if (strokes.Count == 0)
                {
                    MessageBox.Show("Draw a signature first.", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                double cw = drawCanvas.ActualWidth > 0 ? drawCanvas.ActualWidth : 400;
                double ch = drawCanvas.ActualHeight > 0 ? drawCanvas.ActualHeight : 150;

                var saved = new SavedSignature
                {
                    CanvasWidth = cw,
                    CanvasHeight = ch,
                    Name = $"Signature {_savedSignatures.Count + 1}"
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
                SetStatus("Signature saved - click on the page to place it");

                win.Close();
            };

            btnPanel.Children.Add(clearBtn);
            btnPanel.Children.Add(saveBtn);
            Grid.SetRow(btnPanel, 1);
            grid.Children.Add(btnPanel);

            win.Content = grid;
            win.ShowDialog();
        }

        private void PlaceSignature(Point pos, int pageIdx)
        {
            if (_pendingSignature is null) return;

            var sig = _pendingSignature;
            double scale = 0.5;

            // Convert serializable points to WPF points
            var wpfStrokes = new List<List<Point>>();
            foreach (var stroke in sig.Strokes)
            {
                var pts = stroke.Select(p => new Point(p.X, p.Y)).ToList();
                wpfStrokes.Add(pts);
            }

            var annot = new SignatureAnnotation
            {
                PageIndex = pageIdx,
                Position = pos,
                Scale = scale,
                Strokes = wpfStrokes,
                SourceWidth = sig.CanvasWidth,
                SourceHeight = sig.CanvasHeight
            };

            AddAnnotation(annot);
            RenderAllAnnotations(pageIdx);
            SetStatus("Signature placed - click again to place another, or switch tools");
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
            var pos = e.GetPosition(_annotationCanvas);
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;

            switch (_currentTool)
            {
                case EditTool.Select:
                    ClearSelection();
                    ClearTextSelection();
                    if (e.ClickCount == 2)
                    {
                        // Double-click: edit existing PDF text at this position
                        EditTextAtPosition(pos, pageIdx);
                        e.Handled = true;
                    }
                    else
                    {
                        // Single click: start a drag selection for text
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
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(_annotationCanvas);
            pos.X = Math.Max(0, Math.Min(_annotationCanvas.ActualWidth, pos.X));
            pos.Y = Math.Max(0, Math.Min(_annotationCanvas.ActualHeight, pos.Y));

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
            }
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            int pageIdx = PageList.SelectedIndex;

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
            SetStatus($"Selected {annot.GetType().Name.Replace("Annotation", "").ToLower()} annotation - press Delete to remove");
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
                _selectedText = page.Text;
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
                    SetStatus("No text found in selection");
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
                        lines.Add(new List<UglyToad.PdfPig.Content.Word>());
                        lastY = wy;
                    }
                    lines[^1].Add(w);
                }

                _selectedText = string.Join("\n",
                    lines.Select(line => string.Join(" ", line.Select(w => w.Text))));

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
                    Content = "\u2715",
                    Background = Brushes.Transparent,
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    BorderThickness = new Thickness(0),
                    FontSize = 12,
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(4, 0, 0, 0),
                    Style = (Style)FindResource("ToolbarButton")
                };
                closeBtn.Click += (s, e) => CloseSearchBar();

                var panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(8)
                };
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
                RunSearch(_searchBox?.Text ?? "");
                e.Handled = true;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = _searchBox?.Text ?? "";
            if (text.Length >= 2)
                RunSearch(text);
            else
                ClearSearchHighlights();
        }

        private void RunSearch(string query)
        {
            ClearSearchHighlights();
            if (string.IsNullOrWhiteSpace(query) || _currentFile is null) return;
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;
            if (!_renderDims.ContainsKey(pageIdx)) return;

            try
            {
                var (renderW, renderH) = _renderDims[pageIdx];

                using var pigDoc = PdfPigDoc.Open(_currentFile);
                if (pageIdx >= pigDoc.NumberOfPages) return;
                var page = pigDoc.GetPage(pageIdx + 1);

                double pdfW = page.Width;
                double pdfH = page.Height;
                double sx = renderW / pdfW;
                double sy = renderH / pdfH;

                var words = page.GetWords().ToList();
                var lowerQuery = query.ToLowerInvariant();
                int hitCount = 0;

                // Search each word and multi-word sequences
                for (int i = 0; i < words.Count; i++)
                {
                    // Check single word contains
                    if (words[i].Text.ToLowerInvariant().Contains(lowerQuery))
                    {
                        var bb = words[i].BoundingBox;
                        AddSearchHighlight(bb, sx, sy, renderH, pdfH);
                        hitCount++;
                        continue;
                    }

                    // Check multi-word match (e.g. searching "Steve Riley")
                    string combined = words[i].Text;
                    for (int j = i + 1; j < words.Count && combined.Length < lowerQuery.Length + 20; j++)
                    {
                        combined += " " + words[j].Text;
                        if (combined.ToLowerInvariant().Contains(lowerQuery))
                        {
                            // Highlight from word[i] to word[j]
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
                            double cx = minX * sx;
                            double cy = renderH - (maxY * sy);
                            double cw = (maxX - minX) * sx;
                            double ch = (maxY - minY) * sy;
                            var highlightRect = new Rectangle
                            {
                                Fill = new SolidColorBrush(Color.FromArgb(80, 255, 165, 0)),
                                Stroke = new SolidColorBrush(Color.FromArgb(160, 255, 165, 0)),
                                StrokeThickness = 1,
                                Width = Math.Max(cw, 4),
                                Height = Math.Max(ch, 4),
                                IsHitTestVisible = false,
                                Tag = "SearchHighlight"
                            };
                            Canvas.SetLeft(highlightRect, cx);
                            Canvas.SetTop(highlightRect, cy);
                            _annotationCanvas.Children.Add(highlightRect);
                            hitCount++;
                            break;
                        }
                    }
                }

                if (_searchStatus is not null)
                    _searchStatus.Text = hitCount > 0 ? $"{hitCount} found" : "No matches";
            }
            catch
            {
                if (_searchStatus is not null)
                    _searchStatus.Text = "Search error";
            }
        }

        private void AddSearchHighlight(UglyToad.PdfPig.Core.PdfRectangle bb, double sx, double sy, double renderH, double pdfH)
        {
            double cx = bb.Left * sx;
            double cy = renderH - (bb.Top * sy);
            double cw = (bb.Right - bb.Left) * sx;
            double ch = (bb.Top - bb.Bottom) * sy;
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

                if (canvasWords.Count == 0) { SetStatus("No text found at this position"); return; }

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
                    lineWords = canvasWords
                        .Where(cw => Math.Abs((cw.Rect.Top + cw.Rect.Bottom) / 2 - nearMidY) < 5)
                        .OrderBy(cw => cw.Rect.Left)
                        .ToList();
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
                            // Strip PDF subset prefix (e.g. "ABCDEF+FontName" -> "FontName")
                            if (rawFont.Contains('+'))
                                rawFont = rawFont.Substring(rawFont.IndexOf('+') + 1);
                            // Clean common suffixes
                            rawFont = rawFont.Replace(",Bold", "").Replace(",Italic", "")
                                             .Replace("-Bold", "").Replace("-Italic", "")
                                             .Replace("-Roman", "").Replace("-Regular", "");
                            if (!string.IsNullOrWhiteSpace(rawFont))
                                fontName = rawFont;
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
            SetStatus("Text edit cancelled");
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
                SetStatus(newText == ctx.OriginalText ? "No changes made" : "Text edit cancelled (empty)");
                return;
            }

            // Create a TextEditAnnotation
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
            RenderAllAnnotations(ctx.PageIndex);
            SetStatus($"Text edited: \"{ctx.OriginalText}\" -> \"{newText}\"");
        }

        // ============================================================
        // Text box handling
        // ============================================================

        private void PlaceTextBox(Point pos, int pageIdx)
        {
            var tb = new TextBox
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
                Foreground = Brushes.Black,
                BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
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
            else if (e.Key == Key.Escape && _searchBar is not null && _searchBar.Visibility == Visibility.Visible)
            {
                CloseSearchBar();
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
        }

        // ============================================================
        // Annotation management
        // ============================================================

        private void AddAnnotation(PageAnnotation annotation)
        {
            if (!_annotations.ContainsKey(annotation.PageIndex))
                _annotations[annotation.PageIndex] = new List<PageAnnotation>();
            _annotations[annotation.PageIndex].Add(annotation);
        }

        private void RenderTextAnnotation(TextAnnotation ta)
        {
            var tb = new TextBlock
            {
                Text = ta.Content,
                Foreground = Brushes.Black,
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
                        break;
                }
            }
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;
            if (!_annotations.ContainsKey(pageIdx) || _annotations[pageIdx].Count == 0)
            {
                SetStatus("Nothing to undo");
                return;
            }
            _annotations[pageIdx].RemoveAt(_annotations[pageIdx].Count - 1);
            ClearSelection();
            RenderAllAnnotations(pageIdx);
            SetStatus("Undid last annotation");
        }

        private void ClearAnnotations_Click(object sender, RoutedEventArgs e)
        {
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;
            if (_annotations.ContainsKey(pageIdx)) _annotations[pageIdx].Clear();
            ClearSelection();
            _annotationCanvas.Children.Clear();
            SetStatus("Cleared annotations on this page");
        }

        // ============================================================
        // File toolbar handlers
        // ============================================================

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "PDF files|*.pdf", Title = "Open PDF" };
            if (dlg.ShowDialog() == true) OpenFile(dlg.FileName);
        }

        private void Merge_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { MessageBox.Show("Open a PDF first.", "KillerPDF"); return; }
            var doc = _doc;
            var dlg = new OpenFileDialog { Filter = "PDF files|*.pdf", Title = "Select PDF to merge", Multiselect = true };
            if (dlg.ShowDialog() != true) return;
            try
            {
                foreach (var file in dlg.FileNames)
                {
                    using var src = PdfReader.Open(file, PdfDocumentOpenMode.Import);
                    for (int i = 0; i < src.PageCount; i++)
                        doc.AddPage(src.Pages[i]);
                }
                SaveTempAndReload();
                SetStatus($"Merged {dlg.FileNames.Length} file(s) - {_doc?.PageCount} total pages");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Merge failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Split_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { MessageBox.Show("Open a PDF first.", "KillerPDF"); return; }
            var currentFile = _currentFile;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) { MessageBox.Show("Select pages to extract.", "KillerPDF"); return; }
            var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", Title = "Save extracted pages as" };
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
                SetStatus($"Extracted {indices.Count} page(s) to {System.IO.Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Split failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { MessageBox.Show("Open a PDF first.", "KillerPDF"); return; }
            var doc = _doc;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) { MessageBox.Show("Select pages to delete.", "KillerPDF"); return; }
            var result = MessageBox.Show($"Delete {selected.Count} page(s)?", "KillerPDF",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            try
            {
                var indices = new List<int>();
                foreach (var item in selected) indices.Add(PageList.Items.IndexOf(item));
                foreach (var idx in indices.OrderByDescending(i => i))
                    doc.Pages.RemoveAt(idx);
                SaveTempAndReload();
                SetStatus($"Deleted {indices.Count} page(s) - {_doc?.PageCount} remaining");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Delete failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
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

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { MessageBox.Show("Open a PDF first.", "KillerPDF"); return; }
            CommitActiveTextBox();
            var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", Title = "Save PDF as" };
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
                    SetStatus($"Saved with annotations to {System.IO.Path.GetFileName(dlg.FileName)}");
                }
                else
                {
                    _doc.Save(dlg.FileName);
                    SetStatus($"Saved to {System.IO.Path.GetFileName(dlg.FileName)}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Save failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Print_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { MessageBox.Show("Open a PDF first.", "KillerPDF"); return; }
            CommitActiveTextBox();
            try
            {
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
                string printPath;

                if (hasAnnotations)
                {
                    // Save a temp copy with annotations flattened for printing
                    var tempClean = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        $"killerpdf_clean_{Guid.NewGuid():N}.pdf");
                    _doc.Save(tempClean);
                    DrawAnnotationsOnDocument();
                    printPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        $"killerpdf_print_{Guid.NewGuid():N}.pdf");
                    _doc.Save(printPath);
                    // Restore clean state
                    _doc.Close();
                    _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                    _currentFile = tempClean;
                }
                else
                {
                    printPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        $"killerpdf_print_{Guid.NewGuid():N}.pdf");
                    _doc.Save(printPath);
                }

                Process.Start(new ProcessStartInfo(printPath) { Verb = "print", UseShellExecute = true });
                SetStatus("Sent to printer");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Print failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
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
                            foreach (var line in lines)
                            {
                                if (!string.IsNullOrEmpty(line))
                                    gfx.DrawString(line, font, XBrushes.Black, ta.Position.X * sx, ty);
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
                            var pen = new XPen(XColor.FromArgb(ic.A, ic.R, ic.G, ic.B), ia.StrokeWidth * sx);
                            pen.LineJoin = XLineJoin.Round;
                            pen.LineCap = XLineCap.Round;
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
                            var sigPen = new XPen(XColors.Black, 2 * sa.Scale * sx);
                            sigPen.LineJoin = XLineJoin.Round;
                            sigPen.LineCap = XLineCap.Round;
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
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            e.Handled = true;
            _zoomLevel = e.Delta > 0
                ? Math.Min(ZoomMax, _zoomLevel + ZoomStep)
                : Math.Max(ZoomMin, _zoomLevel - ZoomStep);
            ApplyZoom();
            SetStatus($"Page {PageList.SelectedIndex + 1} of {_doc?.PageCount} - {_zoomLevel * 100:F0}%");
        }

        private void ApplyZoom()
        {
            if (_pageContentGrid.LayoutTransform is ScaleTransform st)
            {
                st.ScaleX = _zoomLevel;
                st.ScaleY = _zoomLevel;
            }
        }

        private void ResetZoom()
        {
            _zoomLevel = 1.0;
            ApplyZoom();
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

        private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PageList.SelectedIndex >= 0)
            {
                CommitActiveTextBox();
                ClearSelection();
                ClearTextSelection();
                RenderPage(PageList.SelectedIndex);
                ApplyZoom();
                // Re-run search on new page if search bar is open
                if (_searchBar is not null && _searchBar.Visibility == Visibility.Visible
                    && _searchBox is not null && _searchBox.Text.Length >= 2)
                    RunSearch(_searchBox.Text);
            }
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }
}
