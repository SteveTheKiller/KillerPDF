using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class TwoPageContinuousSmoke
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly (double Width, double Height, string Color, byte R, byte G, byte B)[] Pages =
    [
        (300, 320, "1 0 0", 255, 0, 0), (300, 200, "0 1 0", 0, 255, 0),
        (300, 240, "0 0 1", 0, 0, 255), (300, 360, "1 0 1", 255, 0, 255),
        (300, 180, "1 0.5 0", 255, 128, 0)
    ];

    internal static int Run(Window owner, string appPath, string folder)
    {
        string stage = "fixture";
        Exception? failure = null;
        var events = new List<object>();
        var modals = new List<object>();
        var inputs = new Dictionary<string, byte[]>();
        var elapsed = Stopwatch.StartNew();
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(25) };
        DispatcherUnhandledExceptionEventHandler dispatcherFailure = (_, error) =>
        {
            failure ??= error.Exception;
            error.Handled = true;
        };
        timer.Tick += (_, _) =>
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(150))
                failure ??= new TimeoutException("Overall deadline expired at " + stage);
            foreach (Window window in Application.Current.Windows.Cast<Window>().Where(w => w != owner && w.IsVisible).ToArray())
            {
                modals.Add(new { stage, type = window.GetType().FullName, window.Title,
                    text = string.Join("\n", Descendants<TextBlock>(window).Select(t => t.Text)) });
                try { window.Close(); } catch (Exception error) { failure ??= error; }
            }
        };
        Application.Current.DispatcherUnhandledException += dispatcherFailure;
        try
        {
            byte[] fixture = Fixture(engine);
            string source = Path.Combine(folder, "five-page-markers.pdf");
            inputs.Add(source, fixture);
            File.WriteAllBytes(source, fixture);
            owner.Activate();
            timer.Start();
            Invoke(owner, "OpenFromExternal", source);
            Wait(() => Named<TextBlock>(owner, "FileNameLabel")?.Text == Path.GetFileName(source)
                && Named<ListBox>(owner, "PageList")?.Items.Count == 5);
            FrameworkElement viewer = Descendants<FrameworkElement>(owner)
                .Single(v => v.GetType().FullName == "KillerPDF.Controls.PdfViewer" && v.IsVisible);
            ScrollViewer viewport = Named<ScrollViewer>(viewer, "PagePreviewPanel")
                ?? throw new MissingMemberException("PagePreviewPanel");

            CheckOriginalModes("before");
            SelectMode("ViewTwoPageContinuousRadio");
            FitWidth();
            Border[] slots = Slots();
            AssertPairs(slots, book: false);
            CaptureMarkers("paired-first-row", [0, 1]);
            stage = "scroll-next-row";
            ScrollTo(slots[2]);
            CaptureMarkers("paired-next-row", [2, 3]);
            stage = "scroll-last-page";
            ScrollTo(slots[4]);
            CaptureMarkers("paired-last-page", [4]);

            stage = "book-on";
            ToggleBookFromKeyboard(owner, viewport);
            FitWidth();
            slots = Slots();
            AssertPairs(slots, book: true);
            viewport.ScrollToTop(); Stable();
            CaptureMarkers("book-cover", [0]);
            ScrollTo(slots[1]);
            CaptureMarkers("book-next-row", [1, 2]);
            stage = "book-off";
            ToggleBookFromKeyboard(owner, viewport);
            FitWidth();
            slots = Slots();
            AssertPairs(slots, book: false);

            stage = "zoom-two-hundred";
            SelectZoom("2.0");
            Stable();
            Require(viewport.ScrollableWidth > 20 && viewport.ScrollableHeight > 20,
                "200% must expose horizontal and vertical scrolling.");
            Require(viewport.ComputedHorizontalScrollBarVisibility == Visibility.Visible
                && viewport.ComputedVerticalScrollBarVisibility == Visibility.Visible, "Both scrollbars must be visible at 200%.");
            viewport.ScrollToHorizontalOffset(Math.Min(80, viewport.ScrollableWidth));
            viewport.ScrollToVerticalOffset(Math.Min(120, viewport.ScrollableHeight));
            Stable();
            Require(viewport.HorizontalOffset > 10 && viewport.VerticalOffset > 10, "Both scroll offsets must move at 200%.");
            events.Add(new { stage, viewport.HorizontalOffset, viewport.VerticalOffset,
                viewport.ScrollableWidth, viewport.ScrollableHeight });

            stage = "tab-open-control";
            string second = Path.Combine(folder, "tab-control.pdf");
            inputs.Add(second, fixture);
            File.WriteAllBytes(second, fixture);
            Invoke(owner, "OpenFromExternal", second);
            Wait(() => Named<TextBlock>(owner, "FileNameLabel")?.Text == Path.GetFileName(second));
            stage = "tab-control-single";
            SelectMode("ViewSingleRadio");
            stage = "tab-restore-source";
            TextBlock sourceTab = Descendants<TextBlock>(owner).Single(label => label.IsVisible
                && label.Name == "tabLbl" && label.Text == Path.GetFileNameWithoutExtension(source));
            ClickTab(owner, sourceTab);
            events.Add(new { stage, titleAfterClick = Named<TextBlock>(owner, "FileNameLabel")?.Text,
                single = Named<RadioButton>(owner, "ViewSingleRadio")?.IsChecked,
                paired = Named<RadioButton>(owner, "ViewTwoPageContinuousRadio")?.IsChecked,
                visibleTabs = Descendants<TextBlock>(owner).Where(label => label.IsVisible && label.Name == "tabLbl")
                    .Select(label => label.Text).ToArray() });
            Wait(() => Named<TextBlock>(owner, "FileNameLabel")?.Text == Path.GetFileName(source)
                && Named<RadioButton>(owner, "ViewTwoPageContinuousRadio")?.IsChecked == true);
            Stable();
            AssertPairs(Slots(), book: false);
            Require(viewport.ScrollableWidth > 20, "Restoring the tab must preserve its zoomed facing layout.");
            FitWidth();
            CheckOriginalModes("after");
            Require(Named<ListBox>(owner, "PageList")?.Items.Count == 5,
                "Mode changes must preserve all five pages.");

            void CheckOriginalModes(string arm)
            {
                stage = arm + "-single";
                SelectMode("ViewSingleRadio");
                SelectZoom("fitpage");
                Stable();
                Panel pagePanel = Named<Panel>(viewer, "PageContentPanel")!;
                Wait(() => pagePanel.IsVisible && Descendants<Image>(pagePanel).Any(i => i.IsVisible && i.Source is BitmapSource));
                Image singleImage = Descendants<Image>(pagePanel).First(i => i.IsVisible && i.Source is BitmapSource);
                Rect singleBounds = Bounds(singleImage);
                BitmapSource singleCapture = Capture(owner, singleBounds);
                Rect blankBounds = new(singleBounds.Left + singleBounds.Width * .35, singleBounds.Top + singleBounds.Height * .03,
                    singleBounds.Width * .3, singleBounds.Height * .1);
                BitmapSource singleBlank = Capture(owner, blankBounds);
                Require(Enumerable.Range(0, 5).Any(i => ColorFraction(singleCapture, i) > .1)
                    && Enumerable.Range(0, 5).All(i => ColorFraction(singleBlank, i) < .01),
                    "Single must show a visible fixture marker with a blank absence control.");
                Save(singleCapture, Path.Combine(folder, arm + "-single-page.png"));
                Save(singleBlank, Path.Combine(folder, arm + "-single-blank.png"));
                stage = arm + "-continuous";
                SelectMode("ViewContinuousRadio");
                FitWidth();
                Border[] singleSlots = Slots();
                Rect[] bounds = singleSlots.Select(Bounds).ToArray();
                Require(bounds.Skip(1).All(b => Math.Abs(b.Left - bounds[0].Left) < 2), "Continuous pages must share one column.");
                for (int i = 1; i < bounds.Length; i++)
                    Require(bounds[i].Top >= bounds[i - 1].Bottom, "Continuous pages must stack without overlap.");
                viewport.ScrollToTop(); Stable();
                CaptureMarkers(arm + "-continuous", [0]);
                events.Add(new { stage, pages = bounds.Select(b => b.ToString()).ToArray() });
            }

            void SelectMode(string name)
            {
                Wait(() => Named<RadioButton>(owner, name) is { IsVisible: true, IsEnabled: true });
                RadioButton radio = Named<RadioButton>(owner, name)!;
                var peer = UIElementAutomationPeer.CreatePeerForElement(radio) ?? new RadioButtonAutomationPeer(radio);
                ((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)!).Select();
                Wait(() => radio.IsChecked == true);
                Stable();
            }

            void SelectZoom(string tag)
            {
                Wait(() => Named<ComboBox>(owner, "ZoomBox") is { IsVisible: true, IsEnabled: true });
                ComboBox zoom = Named<ComboBox>(owner, "ZoomBox")!;
                ComboBoxItem choice = zoom.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, tag));
                zoom.SelectedItem = choice;
                Wait(() => ReferenceEquals(zoom.SelectedItem, choice));
                Stable();
            }

            void FitWidth()
            {
                // Leave the current selection so Fit Width always raises the real control event.
                SelectZoom("1.0");
                SelectZoom("fitwidth");
                Stable();
                Require(viewport.ScrollableWidth <= 1, "Fit Width must not leave horizontal overflow.");
            }

            Border[] Slots()
            {
                Panel panel = Named<Panel>(viewer, "ContinuousPanel") ?? throw new MissingMemberException("ContinuousPanel");
                Wait(() => panel.IsVisible && panel.Children.Count == 5);
                Border[] result = panel.Children.Cast<Border>().OrderBy(b => (int)b.Tag).ToArray();
                Require(result.Select(s => (int)s.Tag).SequenceEqual(Enumerable.Range(0, 5)), "Every page needs its own visible slot.");
                return result;
            }

            void AssertPairs(Border[] pages, bool book)
            {
                Stable();
                Rect[] b = pages.Select(Bounds).ToArray();
                int first = book ? 1 : 0;
                if (book)
                {
                    Rect panel = Bounds(Named<Panel>(viewer, "ContinuousPanel")!);
                    Require(Math.Abs((b[0].Left + b[0].Right) / 2 - (panel.Left + panel.Right) / 2) < 2,
                        "Book cover must be centered alone.");
                    Require(b[1].Top >= b[0].Bottom, "Book's next spread must start below its cover.");
                }
                for (int i = first; i + 1 < b.Length; i += 2)
                {
                    Require(Math.Abs(b[i].Top - b[i + 1].Top) < 2, "Facing pages must share the row top.");
                    Require(b[i + 1].Left > b[i].Right, "Facing pages must have horizontal separation.");
                    if (i + 2 < b.Length)
                        Require(b[i + 2].Top >= Math.Max(b[i].Bottom, b[i + 1].Bottom), "Next row must clear the taller page.");
                }
                events.Add(new { stage, book, pages = b.Select(r => r.ToString()).ToArray() });
            }

            void ScrollTo(Border page)
            {
                Rect relative = page.TransformToAncestor(viewport).TransformBounds(new Rect(page.RenderSize));
                viewport.ScrollToVerticalOffset(viewport.VerticalOffset + relative.Top);
                Stable();
                Require(viewport.VerticalOffset > 10, "Scrolling must change the real viewport offset.");
            }

            void CaptureMarkers(string label, int[] pageIndices)
            {
                stage = label + "-pixels";
                Border[] pages = Slots();
                foreach (int index in pageIndices)
                {
                    Image image = Descendants<Image>(pages[index]).First();
                    Wait(() => image.Source is BitmapSource && image.IsVisible);
                    Stable();
                    Rect page = Bounds(pages[index]);
                    Rect clip = Bounds(Descendants<ScrollContentPresenter>(viewport).Single(p => p.IsVisible));
                    Rect marker = new(page.Left + page.Width * .35, page.Top + page.Height * .23, page.Width * .3, page.Height * .1);
                    Rect blank = new(page.Left + page.Width * .35, page.Top + page.Height * .03, page.Width * .3, page.Height * .1);
                    Require(clip.Contains(marker) && clip.Contains(blank), "Marker and blank control must be visible within the viewport.");
                    BitmapSource target = Capture(owner, marker), absence = Capture(owner, blank);
                    Save(target, Path.Combine(folder, label + "-page-" + index + "-marker.png"));
                    Save(absence, Path.Combine(folder, label + "-page-" + index + "-blank.png"));
                    double positive = ColorFraction(target, index), negative = ColorFraction(absence, index);
                    Require(positive > .85 && negative < .01, "Rendered marker presence or independent blank absence control failed for page " + index);
                    Require(Enumerable.Range(0, 5).Where(i => i != index).All(i => ColorFraction(target, i) < .01),
                        "A page marker must be distinguishable from its neighbors.");
                    events.Add(new { stage, index, positive, negative, page = page.ToString(), clip = clip.ToString() });
                }
                Rect visible = Bounds(Descendants<ScrollContentPresenter>(viewport).Single(p => p.IsVisible));
                Save(Capture(owner, visible), Path.Combine(folder, label + "-viewport.png"));
            }

            void Stable()
            {
                string previous = "";
                int stable = 0;
                Wait(() =>
                {
                    owner.UpdateLayout();
                    string state = Bounds(owner) + "|" + Bounds(viewport) + "|" + viewport.ExtentWidth.ToString(CultureInfo.InvariantCulture)
                        + "|" + viewport.ExtentHeight.ToString(CultureInfo.InvariantCulture) + "|" + viewport.HorizontalOffset + "|" + viewport.VerticalOffset;
                    bool opaque = true;
                    for (DependencyObject? visual = Named<Panel>(viewer, "ContinuousPanel"); visual is Visual; visual = VisualTreeHelper.GetParent(visual))
                        if (visual is UIElement { Opacity: < .999 }) opaque = false;
                    stable = state == previous && viewport.Opacity > .999 && opaque ? stable + 1 : 0;
                    previous = state;
                    return stable >= 6;
                });
            }

            void Wait(Func<bool> ready)
            {
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                    Require(modals.Count == 0, "Unexpected isolated-host modal at " + stage);
                    if (ready()) return;
                    if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Readiness deadline expired at " + stage);
                    Thread.Sleep(25);
                }
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            timer.Stop();
            Application.Current.DispatcherUnhandledException -= dispatcherFailure;
            bool preserved = inputs.Count > 0 && inputs.All(p => File.Exists(p.Key) && File.ReadAllBytes(p.Key).AsSpan().SequenceEqual(p.Value));
            if (!preserved) failure ??= new InvalidOperationException("Synthetic source bytes changed.");
            File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new
            {
                passed = failure is null && modals.Count == 0 && preserved, stage, error = failure?.ToString(),
                preserved, machine = Environment.MachineName, processId = Environment.ProcessId,
                ownedWindow = new WindowInteropHelper(owner).Handle.ToInt64(), elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                events, modals, inputs = inputs.Select(p => new { p.Key, sha256 = Hash(p.Value) }).ToArray(),
                binaries = new[]
                {
                    new { path = appPath, sha256 = Hash(File.ReadAllBytes(appPath)) },
                    new { path = "selected:KillerPdf.Engine", sha256 = SelectedAssemblyIdentity.HashLoaded(engine, appPath) },
                    new { path = typeof(TwoPageContinuousSmoke).Assembly.Location,
                        sha256 = Hash(File.ReadAllBytes(typeof(TwoPageContinuousSmoke).Assembly.Location)) }
                }
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failure is null && modals.Count == 0 ? 0 : 1;
    }

    private static byte[] Fixture(Assembly engine)
    {
        Type type = engine.GetType("KillerPdf.Engine.Authoring.PdfDocumentBuilder", true)!;
        ConstructorInfo constructor = type.GetConstructors().Single();
        object builder = constructor.Invoke(constructor.GetParameters().Select(p => p.DefaultValue).ToArray());
        foreach (var page in Pages)
        {
            string content = FormattableString.Invariant($"q 1 1 1 rg 0 0 {page.Width} {page.Height} re f {page.Color} rg {page.Width * .2} {page.Height * .2} {page.Width * .6} {page.Height * .6} re f Q\n");
            type.GetMethod("AddPage", [typeof(double), typeof(double), typeof(ReadOnlyMemory<byte>)])!
                .Invoke(builder, [page.Width, page.Height, new ReadOnlyMemory<byte>(Encoding.ASCII.GetBytes(content))]);
        }
        return (byte[])type.GetMethod("Build", Type.EmptyTypes)!.Invoke(builder, null)!;
    }

    private static object? Invoke(object target, string name, params object[] args)
    {
        try { return target.GetType().GetMethod(name, Members)!.Invoke(target, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static T? Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Descendants<T>(root).SingleOrDefault(e => e.Name == name);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T item) yield return item;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static Rect Bounds(FrameworkElement element) => new(element.PointToScreen(new Point()),
        element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path); encoder.Save(output);
    }
    private static double ColorFraction(BitmapSource image, int index)
    {
        byte[] pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        var color = Pages[index];
        int matched = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (Math.Abs(pixels[i] - color.B) <= 35 && Math.Abs(pixels[i + 1] - color.G) <= 35
                && Math.Abs(pixels[i + 2] - color.R) <= 35) matched++;
        return (double)matched / (image.PixelWidth * image.PixelHeight);
    }

    private static BitmapSource Capture(Window owner, Rect bounds)
    {
        int left = (int)Math.Ceiling(bounds.Left), top = (int)Math.Ceiling(bounds.Top);
        int width = (int)Math.Floor(bounds.Right) - left, height = (int)Math.Floor(bounds.Bottom) - top;
        if (width < 8 || height < 8 || width > 4096 || height > 4096) throw new InvalidOperationException("Invalid owned capture size.");
        IntPtr hwnd = new WindowInteropHelper(owner).Handle;
        Require(Bounds(owner).Contains(bounds), "Capture must remain within the owned window.");
        foreach (var point in new[] { new NativePoint(left + 1, top + 1), new(left + width - 2, top + 1),
            new(left + 1, top + height - 2), new(left + width - 2, top + height - 2), new(left + width / 2, top + height / 2) })
            Require(GetAncestor(WindowFromPoint(point), 2) == hwnd, "Capture rectangle is obscured by another window.");
        IntPtr screen = IntPtr.Zero, dc = IntPtr.Zero, bitmap = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            screen = GetDC(IntPtr.Zero); if (screen == IntPtr.Zero) throw new Win32Exception();
            dc = CreateCompatibleDC(screen); if (dc == IntPtr.Zero) throw new Win32Exception();
            bitmap = CreateCompatibleBitmap(screen, width, height); if (bitmap == IntPtr.Zero) throw new Win32Exception();
            old = SelectObject(dc, bitmap); if (old == IntPtr.Zero || old == new IntPtr(-1)) throw new Win32Exception();
            if (!BitBlt(dc, 0, 0, width, height, screen, left, top, 0x00CC0020)) throw new Win32Exception();
            IntPtr restored = SelectObject(dc, old);
            if (restored == IntPtr.Zero || restored == new IntPtr(-1)) throw new Win32Exception();
            old = IntPtr.Zero;
            byte[] pixels = new byte[checked(width * height * 4)];
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref info, 0) != height) throw new Win32Exception();
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        }
        finally
        {
            if (old != IntPtr.Zero && old != new IntPtr(-1)) SelectObject(dc, old);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (dc != IntPtr.Zero) DeleteDC(dc);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }
    private static void ToggleBookFromKeyboard(Window owner, ScrollViewer viewport)
    {
        Require(new[] { "SN01", "SN02" }.Contains(Environment.MachineName, StringComparer.OrdinalIgnoreCase),
            "Physical keyboard acceptance requires an owned UI VM.");
        nint root = new WindowInteropHelper(owner).Handle;
        Require(GetWindowThreadProcessId(root, out uint process) != 0 && process == Environment.ProcessId,
            "Keyboard endpoint must belong to this fixture.");
        Require(new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x42 }.All(key => (GetAsyncKeyState(key) & 0x8000) == 0),
            "Keyboard acceptance requires released modifiers and B.");
        owner.Activate();
        SetForegroundWindow(root);
        Keyboard.Focus(viewport);
        Require(ReferenceEquals(Keyboard.FocusedElement, viewport), "Reading pane did not receive keyboard focus.");
        Require(GetForegroundWindow() == root && GetFocus() == root, "Owned keyboard endpoint is not focused.");
        var down = new NativeInput { Type = 1, Data = new InputData { Key = new KeyData { Virtual = 0x42 } } };
        var up = new NativeInput { Type = 1, Data = new InputData { Key = new KeyData { Virtual = 0x42, Flags = 2 } } };
        try { Require(SendInput(2, [down, up], Marshal.SizeOf<NativeInput>()) == 2, "Book shortcut input failed."); }
        finally { SendInput(1, [up], Marshal.SizeOf<NativeInput>()); }
        owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static void ClickTab(Window owner, TextBlock label)
    {
        nint root = new WindowInteropHelper(owner).Handle;
        owner.Activate(); SetForegroundWindow(root);
        Require(GetWindowThreadProcessId(root, out uint process) != 0 && process == Environment.ProcessId
            && GetForegroundWindow() == root, "Tab endpoint must be owned and foreground.");
        Rect bounds = Bounds(label);
        var point = new Point(Math.Round(bounds.Left + bounds.Width / 2), Math.Round(bounds.Top + bounds.Height / 2));
        Require(SystemParameters.WorkArea.Contains(point) && bounds.Contains(point)
            && GetAncestor(WindowFromPoint(new NativePoint((int)point.X, (int)point.Y)), 2) == root
            && ReferenceEquals(owner.InputHitTest(owner.PointFromScreen(point)), label),
            "Tab click must hit the visible owned tab label.");
        Require(SetCursorPos((int)point.X, (int)point.Y), "Tab pointer move failed.");
        var down = new NativeInput { Data = new InputData { Mouse = new MouseData { Flags = 2 } } };
        var up = new NativeInput { Data = new InputData { Mouse = new MouseData { Flags = 4 } } };
        try
        {
            Require(SendInput(1, [down], Marshal.SizeOf<NativeInput>()) == 1, "Tab press failed.");
            owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Require(SendInput(1, [up], Marshal.SizeOf<NativeInput>()) == 1, "Tab release failed.");
        }
        finally { SendInput(1, [up], Marshal.SizeOf<NativeInput>()); }
        owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    { [FieldOffset(0)] public KeyData Key; [FieldOffset(0)] public MouseData Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyData { public ushort Virtual, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important, Colors; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint operation);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] pixels, ref BitmapInfo info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
