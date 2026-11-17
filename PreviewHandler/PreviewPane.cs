using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using KillerPDF.Services;

namespace KillerPDF.PreviewHandler;

/// <summary>
/// The preview itself: a small toolbar over the pages in one scrolling column, hosted as a
/// child window of the shell's preview pane. Pages near the view render on one background
/// thread at the pane's device resolution, and pages far away drop their pixels so long
/// documents stay light.
/// </summary>
internal sealed partial class PreviewPane : IDisposable
{
    private enum ZoomMode { FitWidth, FitPage, Custom }

    private sealed class PageSlot(int index, double width, double height)
    {
        internal int Index { get; } = index;
        internal double WidthPoints { get; } = width;
        internal double HeightPoints { get; } = height;
        internal Border Frame { get; } = new()
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, PageGap),
            SnapsToDevicePixels = true
        };
        internal Image Picture { get; } = new() { Stretch = Stretch.Fill };
        internal Border Notice { get; } = new() { Visibility = Visibility.Collapsed, Background = SystemColors.InfoBrush,
            Padding = new Thickness(8), VerticalAlignment = VerticalAlignment.Bottom };
        internal Button? Retry { get; set; }
        internal int RequestedWidth { get; set; }
    }

    private sealed record RenderRequest(PageSlot Slot, int Width, int Height, int Generation);

    private const double PageGap = 12;
    private const double Edge = 12;
    private const double MinimumZoom = 0.1;
    private const double MaximumZoom = 8;
    private const long MaximumPagePixels = 40_000_000;
    private static readonly TimeSpan PageRenderDeadline = TimeSpan.FromSeconds(15);

    private readonly HwndSource _source;
    private readonly byte[] _data;
    private readonly string _name;
    private readonly ScrollViewer _scroller = new()
    {
        // Always shown, so fitting the width never makes the scroll bar come and go.
        VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        CanContentScroll = false,
        Focusable = true
    };
    private readonly StackPanel _column = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(Edge) };
    private readonly TextBlock _zoomLabel = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 44, TextAlignment = TextAlignment.Center };
    private readonly List<PageSlot> _slots = [];
    private readonly List<Button> _buttons = [];
    private readonly CancellationTokenSource _closing = new();
    private readonly Channel<RenderRequest> _requests = Channel.CreateUnbounded<RenderRequest>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly DispatcherTimer _refresh;
    private readonly Dispatcher _dispatcher;
    private PdfPageRenderer? _renderer;
    private ZoomMode _mode = ZoomMode.FitWidth;
    private double _zoom = 1;
    private int _generation;
    private bool _disposed;
    private Brush _foreground = Brushes.Black;

    internal IntPtr Handle => _source.Handle;

    internal PreviewPane(IntPtr parent, NativeRect bounds, byte[] data, string name,
        uint? background, uint? text)
    {
        _data = data;
        _name = name;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Color backColor = background is uint back ? FromColorRef(back) : SystemColors.WindowColor;
        Color textColor = text is uint fore ? FromColorRef(fore) : SystemColors.WindowTextColor;
        var foreground = new SolidColorBrush(textColor);
        _foreground = foreground;
        var subtle = new SolidColorBrush(Color.FromArgb(0x26, textColor.R, textColor.G, textColor.B));

        var root = new Grid { Background = new SolidColorBrush(backColor) };
        // Thin scroll bars in the pane's own colors, so a dark Explorer does not get light system ones.
        root.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), ScrollBarStyle(textColor));
        TextElement.SetForeground(root, foreground);
        TextElement.SetFontFamily(root, new FontFamily("Segoe UI"));
        TextElement.SetFontSize(root, 12);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        ControlTemplate template = ButtonTemplate(subtle);
        var toolbar = new DockPanel { Margin = new Thickness(6, 4, 6, 4), LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(MakeButton(template, null, PreviewStrings.Get(PreviewStrings.Key.FitWidth), null, () => SetMode(ZoomMode.FitWidth)));
        left.Children.Add(MakeButton(template, null, PreviewStrings.Get(PreviewStrings.Key.FitPage), null, () => SetMode(ZoomMode.FitPage)));
        left.Children.Add(MakeButton(template, 0xE71F, null, PreviewStrings.Get(PreviewStrings.Key.ZoomOut), () => Step(1 / 1.25)));
        left.Children.Add(_zoomLabel);
        left.Children.Add(MakeButton(template, 0xE8A3, null, PreviewStrings.Get(PreviewStrings.Key.ZoomIn), () => Step(1.25)));
        DockPanel.SetDock(left, Dock.Left);
        toolbar.Children.Add(left);
        Button open = MakeButton(template, 0xE8A7, PreviewStrings.Get(PreviewStrings.Key.Open), null, OpenInKillerPdf);
        DockPanel.SetDock(open, Dock.Right);
        toolbar.Children.Add(open);
        var bar = new Border { Child = toolbar, BorderBrush = subtle, BorderThickness = new Thickness(0, 0, 0, 1) };
        root.Children.Add(bar);

        _scroller.Content = _column;
        Grid.SetRow(_scroller, 1);
        root.Children.Add(_scroller);

        _refresh = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(40) };
        _refresh.Tick += (_, _) => { _refresh.Stop(); Refresh(); };
        _scroller.ScrollChanged += (_, e) =>
        {
            if (e.ViewportWidthChange != 0 && _mode != ZoomMode.Custom) ApplyZoom();
            else ScheduleRefresh();
        };
        _scroller.SizeChanged += (_, _) => { if (_mode != ZoomMode.Custom) ApplyZoom(); else ScheduleRefresh(); };
        _scroller.PreviewMouseWheel += OnMouseWheel;

        var parameters = new HwndSourceParameters("KillerPDF Preview")
        {
            ParentWindow = parent,
            WindowStyle = WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            PositionX = bounds.Left,
            PositionY = bounds.Top,
            Width = Math.Max(1, bounds.Right - bounds.Left),
            Height = Math.Max(1, bounds.Bottom - bounds.Top)
        };
        _source = new HwndSource(parameters) { RootVisual = root, SizeToContent = SizeToContent.Manual };

        if (!Open(out string? problem))
        {
            open.IsEnabled = _data.Length > 0;
            foreach (Button button in _buttons.Where(button => button != open)) button.IsEnabled = false;
            root.Children.Remove(_scroller);
            var message = new TextBlock
            {
                Text = problem,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(24)
            };
            Grid.SetRow(message, 1);
            root.Children.Add(message);
            return;
        }
        _ = Task.Run(RenderLoop);
        _dispatcher.BeginInvoke(DispatcherPriority.Loaded, ApplyZoom);
    }

    private bool Open(out string? problem)
    {
        problem = PreviewStrings.Get(PreviewStrings.Key.Failed);
        if (_data.Length == 0) return false;
        try
        {
            using var stream = new MemoryStream(_data, writable: false);
            PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(stream);
            if (!document.CanReadPageContent)
            {
                // Owner-password files open with the empty user password, as in the app.
                try
                {
                    stream.Position = 0;
                    document = PdfDocument.OpenWithCompatibilityRecovery(stream, string.Empty);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
            }
            if (!document.CanReadPageContent)
            {
                problem = PreviewStrings.Get(PreviewStrings.Key.Protected);
                return false;
            }
            IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(document);
            if (pages.Count == 0) return false;
            _renderer = new PdfPageRenderer(document, InstalledPdfFontResolver.Instance);
            foreach (PdfPageInformation page in pages)
            {
                bool quarterTurn = page.Rotation is 90 or 270;
                var slot = new PageSlot(_slots.Count,
                    Math.Max(1, quarterTurn ? page.Height : page.Width),
                    Math.Max(1, quarterTurn ? page.Width : page.Height));
                InitializePageNotice(slot);
                _slots.Add(slot);
                _column.Children.Add(slot.Frame);
            }
            problem = null;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    // ── Zoom ─────────────────────────────────────────────────────────────────────────────

    private void SetMode(ZoomMode mode)
    {
        _mode = mode;
        ApplyZoom();
    }

    private void Step(double factor)
    {
        _mode = ZoomMode.Custom;
        _zoom = Math.Clamp(_zoom * factor, MinimumZoom, MaximumZoom);
        ApplyZoom();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || _slots.Count == 0) return;
        Step(e.Delta > 0 ? 1.1 : 1 / 1.1);
        e.Handled = true;
    }

    private void ApplyZoom()
    {
        if (_disposed || _slots.Count == 0) return;
        double viewportWidth = _scroller.ViewportWidth > 0 ? _scroller.ViewportWidth : _scroller.ActualWidth;
        double viewportHeight = _scroller.ViewportHeight > 0 ? _scroller.ViewportHeight : _scroller.ActualHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0) return;
        double widest = _slots.Max(slot => slot.WidthPoints) * 96 / 72;
        double tallest = _slots.Max(slot => slot.HeightPoints) * 96 / 72;
        double fitWidth = (viewportWidth - 2 * Edge - 2) / widest;
        if (_mode == ZoomMode.FitWidth) _zoom = fitWidth;
        else if (_mode == ZoomMode.FitPage) _zoom = Math.Min(fitWidth, (viewportHeight - 2 * Edge - 2) / tallest);
        _zoom = Math.Clamp(_zoom, MinimumZoom, MaximumZoom);

        double fraction = _scroller.ExtentHeight > 0 ? _scroller.VerticalOffset / _scroller.ExtentHeight : 0;
        foreach (PageSlot slot in _slots)
        {
            slot.Frame.Width = slot.WidthPoints * 96 / 72 * _zoom + 2;
            slot.Frame.Height = slot.HeightPoints * 96 / 72 * _zoom + 2;
            slot.RequestedWidth = 0;
        }
        _zoomLabel.Text = (_zoom * 100).ToString("0", System.Globalization.CultureInfo.CurrentCulture) + "%";
        Interlocked.Increment(ref _generation);
        _column.UpdateLayout();
        _scroller.ScrollToVerticalOffset(fraction * _scroller.ExtentHeight);
        ScheduleRefresh();
    }

    // ── Rendering ────────────────────────────────────────────────────────────────────────

    private void ScheduleRefresh()
    {
        if (_disposed) return;
        _refresh.Stop();
        _refresh.Start();
    }

    // Queues the pages within a screen of the view, and lets pages far from it drop their pixels.
    private void Refresh()
    {
        if (_disposed || _slots.Count == 0) return;
        double scale = _source.CompositionTarget?.TransformToDevice.M11 ?? 1;
        double viewport = Math.Max(1, _scroller.ViewportHeight);
        int generation = Volatile.Read(ref _generation);
        foreach (PageSlot slot in _slots)
        {
            double top;
            try { top = slot.Frame.TransformToAncestor(_scroller).Transform(new Point(0, 0)).Y; }
            catch (InvalidOperationException) { continue; }
            double bottom = top + slot.Frame.ActualHeight;
            if (bottom < -viewport * 4 || top > viewport * 5)
            {
                slot.Picture.Source = null;
                slot.RequestedWidth = 0;
                continue;
            }
            if (bottom < -viewport || top > viewport * 2) continue;
            int width = Math.Max(1, (int)Math.Round((slot.Frame.Width - 2) * scale));
            int height = Math.Max(1, (int)Math.Round((slot.Frame.Height - 2) * scale));
            if ((long)width * height > MaximumPagePixels)
            {
                double shrink = Math.Sqrt((double)MaximumPagePixels / ((long)width * height));
                width = Math.Max(1, (int)(width * shrink));
                height = Math.Max(1, (int)(height * shrink));
            }
            if (slot.RequestedWidth == width) continue;
            slot.RequestedWidth = width;
            _requests.Writer.TryWrite(new RenderRequest(slot, width, height, generation));
        }
    }

    private void InitializePageNotice(PageSlot slot)
    {
        var layer = new Grid();
        layer.Children.Add(slot.Picture);
        var content = new StackPanel();
        var message = new TextBlock { Text = PreviewStrings.Get(PreviewStrings.Key.Incomplete),
            TextWrapping = TextWrapping.Wrap, Foreground = SystemColors.InfoTextBrush };
        System.Windows.Automation.AutomationProperties.SetName(message, message.Text);
        System.Windows.Automation.AutomationProperties.SetLiveSetting(message, System.Windows.Automation.AutomationLiveSetting.Polite);
        content.Children.Add(message);
        var actions = new WrapPanel();
        var template = ButtonTemplate(new SolidColorBrush(Color.FromArgb(0x26, 0, 0, 0)));
        Button retry = MakeButton(template, null, PreviewStrings.Get(PreviewStrings.Key.Retry), null, () =>
        {
            if (_disposed) return;
            slot.Retry!.IsEnabled = false;
            slot.RequestedWidth = 0;
            ScheduleRefresh();
        });
        retry.Tag = "retry";
        retry.Foreground = SystemColors.InfoTextBrush;
        slot.Retry = retry;
        actions.Children.Add(retry);
        Button open = MakeButton(template, null, PreviewStrings.Get(PreviewStrings.Key.Open), null, OpenInKillerPdf);
        open.Foreground = SystemColors.InfoTextBrush;
        actions.Children.Add(open);
        content.Children.Add(actions);
        slot.Notice.Child = content;
        layer.Children.Add(slot.Notice);
        slot.Frame.Child = layer;
    }

    private async Task RenderLoop()
    {
        CancellationToken token = _closing.Token;
        try
        {
            while (await _requests.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (_requests.Reader.TryRead(out RenderRequest? request))
                {
                    if (request.Generation != Volatile.Read(ref _generation)) continue;
                    PdfPageRenderer? renderer = _renderer;
                    if (renderer is null) return;
                    BitmapSource? bitmap = null;
                    bool incomplete = false;
                    try
                    {
                        // Explorer's preview shows untagged CMYK through the same chosen working profile.
                        var options = KillerPDF.Services.WorkingCmykProfiles.Apply(new PdfRenderOptions(request.Width, request.Height)
                        {
                            CacheResult = false,
                            MaximumParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4)
                        });
                        // A page that takes longer than the deadline is shown as incomplete, so one
                        // hostile page cannot keep Explorer's or Outlook's preview host busy forever.
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                        deadline.CancelAfter(PageRenderDeadline);
                        PdfRenderedPage page = renderer.Render(request.Slot.Index, options, deadline.Token);
                        incomplete = PdfRenderCompleteness.Classify(page.Diagnostics).Any(item => item.BlocksPublication);
                        byte[] pixels = MemoryMarshal.TryGetArray(page.Pixels, out ArraySegment<byte> segment)
                            && segment.Offset == 0 && segment.Array is not null
                            ? segment.Array : page.Pixels.ToArray();
                        bitmap = BitmapSource.Create(page.Width, page.Height, 96, 96, PixelFormats.Bgra32, null,
                            pixels, page.Width * 4);
                        bitmap.Freeze();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Includes the page deadline: the page shows its incomplete notice and Retry.
                        bitmap = null;
                        incomplete = true;
                    }
                    RenderRequest done = request;
                    BitmapSource? result = bitmap;
                    bool showNotice = incomplete;
                    _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    {
                        if (_disposed || done.Generation != _generation) return;
                        done.Slot.Picture.Source = result;
                        done.Slot.Notice.Visibility = showNotice ? Visibility.Visible : Visibility.Collapsed;
                        done.Slot.Retry!.IsEnabled = true;
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ── Open in KillerPDF ────────────────────────────────────────────────────────────────

    // The shell hands over a stream, not a path, so the app opens a read-only copy of it.
    private void OpenInKillerPdf()
    {
        try
        {
            // LocalLow, because the low-integrity preview cannot write to %TEMP%.
            string root = Path.Combine(DesktopLaunch.LowWritableFolder(), "KillerPDF Preview");
            Directory.CreateDirectory(root);
            RemoveOldCopies(root);
            string folder = Path.Combine(root, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            string name = string.Concat(_name.Split(Path.GetInvalidFileNameChars()));
            if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
            string copy = Path.Combine(folder, name);
            File.WriteAllBytes(copy, _data);
            File.SetAttributes(copy, FileAttributes.ReadOnly);
            string directory = Path.GetDirectoryName(typeof(PreviewPane).Assembly.Location) ?? AppContext.BaseDirectory;
            DesktopLaunch.Start(Path.Combine(directory, "KillerPDF.App.exe"), copy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Nothing to show in the pane; opening the file from Explorer still works.
        }
    }

    private static void RemoveOldCopies(string root)
    {
        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (Directory.GetCreationTimeUtc(folder) > DateTime.UtcNow.AddDays(-2)) continue;
                foreach (string file in Directory.EnumerateFiles(folder)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // ── Window ───────────────────────────────────────────────────────────────────────────

    internal void Reparent(IntPtr parent, NativeRect bounds)
    {
        if (_disposed) return;
        SetParent(_source.Handle, parent);
        Resize(bounds);
    }

    internal void Resize(NativeRect bounds)
    {
        if (_disposed) return;
        SetWindowPos(_source.Handle, IntPtr.Zero, bounds.Left, bounds.Top,
            Math.Max(1, bounds.Right - bounds.Left), Math.Max(1, bounds.Bottom - bounds.Top), SwpNoZOrder | SwpNoActivate);
    }

    internal void FocusFirst()
    {
        if (_disposed) return;
        SetFocusNative(_source.Handle);
        (_buttons.FirstOrDefault(button => button.IsEnabled) as UIElement ?? _scroller).Focus();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _closing.Cancel();
        _requests.Writer.TryComplete();
        _refresh.Stop();
        _renderer = null;
        foreach (PageSlot slot in _slots) slot.Picture.Source = null;
        _source.Dispose();
        _closing.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private Button MakeButton(ControlTemplate template, int? glyph, string? text, string? tip, Action click)
    {
        var icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        object content;
        if (glyph is int code && text is not null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = ((char)code).ToString(), FontFamily = icons, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            content = row;
        }
        else if (glyph is int only)
        {
            content = new TextBlock { Text = ((char)only).ToString(), FontFamily = icons };
        }
        else
        {
            content = text ?? string.Empty;
        }
        var button = new Button
        {
            Content = content,
            Template = template,
            ToolTip = tip,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 2, 0),
            Cursor = Cursors.Hand,
            Foreground = _foreground
        };
        System.Windows.Automation.AutomationProperties.SetName(button, tip ?? text ?? string.Empty);
        button.Click += (_, _) => click();
        _buttons.Add(button);
        return button;
    }

    private static ControlTemplate ButtonTemplate(SolidColorBrush hover)
    {
        string color = hover.Color.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string xaml =
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
            "<Border x:Name='Back' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='Transparent' CornerRadius='4' Padding='{TemplateBinding Padding}'>" +
            "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' RecognizesAccessKey='False'/></Border>" +
            "<ControlTemplate.Triggers>" +
            "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Back' Property='Background' Value='" + color + "'/></Trigger>" +
            "<Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Back' Property='Background' Value='" + color + "'/></Trigger>" +
            "<Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate>";
        return (ControlTemplate)XamlReader.Parse(xaml);
    }

    private static Style ScrollBarStyle(Color text)
    {
        string thumb = Color.FromArgb(0x60, text.R, text.G, text.B).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string hover = Color.FromArgb(0xA0, text.R, text.G, text.B).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string Track(bool vertical) =>
            "<ControlTemplate TargetType='ScrollBar'><Track x:Name='PART_Track' IsDirectionReversed='" + (vertical ? "True" : "False") + "'>" +
            "<Track.DecreaseRepeatButton><RepeatButton Command='" + (vertical ? "ScrollBar.PageUpCommand" : "ScrollBar.PageLeftCommand") +
            "' Focusable='False' IsTabStop='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>" +
            "<Track.IncreaseRepeatButton><RepeatButton Command='" + (vertical ? "ScrollBar.PageDownCommand" : "ScrollBar.PageRightCommand") +
            "' Focusable='False' IsTabStop='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>" +
            "<Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'><Border x:Name='Grip' Background='" + thumb + "' CornerRadius='3' Margin='2'/>" +
            "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Grip' Property='Background' Value='" + hover + "'/></Trigger>" +
            "<Trigger Property='IsDragging' Value='True'><Setter TargetName='Grip' Property='Background' Value='" + hover + "'/></Trigger></ControlTemplate.Triggers>" +
            "</ControlTemplate></Thumb.Template></Thumb></Track.Thumb></Track></ControlTemplate>";
        string xaml =
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollBar'>" +
            "<Setter Property='Background' Value='Transparent'/><Setter Property='Width' Value='10'/><Setter Property='MinWidth' Value='10'/>" +
            "<Setter Property='Template'><Setter.Value>" + Track(true) + "</Setter.Value></Setter>" +
            "<Style.Triggers><Trigger Property='Orientation' Value='Horizontal'>" +
            "<Setter Property='Width' Value='Auto'/><Setter Property='MinWidth' Value='0'/><Setter Property='Height' Value='10'/><Setter Property='MinHeight' Value='10'/>" +
            "<Setter Property='Template'><Setter.Value>" + Track(false) + "</Setter.Value></Setter></Trigger></Style.Triggers></Style>";
        return (Style)XamlReader.Parse(xaml);
    }

    private static Color FromColorRef(uint value) =>
        Color.FromRgb((byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));

    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetParent(IntPtr child, IntPtr parent);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "SetFocus")]
    private static partial IntPtr SetFocusNative(IntPtr window);
}
