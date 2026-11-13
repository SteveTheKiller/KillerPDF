using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class ReplacementSelectionSmoke
{
    private const string Content = "Selection position probe\nSecond line stays here\nThird line stays here\nFourth line stays here";

    internal static int Run(Window owner, string appPath, string appXaml, string folder)
    {
        if (!new[] { "SN01", "SN02" }.Contains(Environment.MachineName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Visible selection acceptance requires an owned UI VM.");
        Application app = Application.Current ?? throw new InvalidOperationException("Isolated application absent.");
        nint root = new WindowInteropHelper(owner).Handle;
        string stage = "starting";
        bool closed = false;
        string? failedAt = null;
        Exception? failure = null;
        var events = new List<object>();
        var hashes = new Dictionary<string, string>();
        string source = Path.Combine(folder, "selection-source.pdf");
        byte[] fixture = BlankPdf();
        owner.Closed += (_, _) => closed = true;
        DispatcherUnhandledExceptionEventHandler onFailure = (_, e) => { RecordFailure(e.Exception); e.Handled = true; };
        app.DispatcherUnhandledException += onFailure;
        Stage("fixture");
        try
        {
            hashes["app"] = Hash(appPath); hashes["appXaml"] = Hash(appXaml);
            hashes["driver"] = Hash(typeof(ReplacementSelectionSmoke).Assembly.Location);
            hashes["engine"] = SelectedAssemblyIdentity.HashLoaded(Assembly.Load("KillerPdf.Engine"), appPath);
            File.WriteAllText(Path.Combine(folder, "binaries.json"), JsonSerializer.Serialize(hashes));
            File.WriteAllBytes(source, fixture);
            owner.Activate();
            SetForegroundWindow(root);
            owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!
                .Invoke(owner, [source]);
            Wait(() => Nodes().OfType<Canvas>().Any(IsPage));
            RadioButton? single = owner.FindName("ViewSingleRadio") as RadioButton;
            if (single is not null) new RadioButtonAutomationPeer(single).GetPattern(PatternInterface.SelectionItem)
                .As<ISelectionItemProvider>().Select();
            Zoom("fitpage"); Stable();
            Canvas page = Page();
            Tool("ToolTextBtn"); Stable();
            Point requested = new(page.ActualWidth * .22, page.ActualHeight - 28);
            Stage("place-near-bottom");
            Click(page.PointToScreen(requested));
            Wait(() => Nodes().OfType<TextBox>().Any(t => t.IsVisible && t.AcceptsReturn && t.Parent is Canvas));
            TextBox editor = Nodes().OfType<TextBox>().Single(t => t.IsVisible && t.AcceptsReturn && t.Parent is Canvas);
            new TextBoxAutomationPeer(editor).GetPattern(PatternInterface.Value).As<IValueProvider>().SetValue(Content);
            Stable();
            events.Add(new { stage, requested, editor = Local(editor), editorScreen = Bounds(editor), geometry = Geometry() });
            Stage("commit-through-select-tool");
            Tool("ToolSelectBtn");
            Wait(() => Marker() is not null); Stable();
            Rect committed = Placement();
            events.Add(new { stage, requested, committed, extendsBelowPage = committed.Bottom > Page().ActualHeight,
                note = "Commit placement is observed independently. A commit clamp is not a selection jump.", geometry = Geometry() });
            SelectCase("fit-page", wheel: false);
            Stage("zoom-two-hundred"); Zoom("2.0"); Stable(); Reveal(); Stable();
            SelectCase("settled-two-hundred", wheel: false);
            SelectCase("immediate-after-wheel", wheel: true);
        }
        catch (Exception error) { RecordFailure(error); }
        finally
        {
            // Retain the original failure before teardown or serialization can fail.
            if (failure is not null) File.WriteAllText(Path.Combine(folder, "failure.txt"), failure.ToString());
            try { Release(); } catch (Exception error) { RecordFailure(error); }
            try { CloseOwned(); } catch (Exception error) { RecordFailure(error); }
            app.DispatcherUnhandledException -= onFailure;
            bool preserved = File.Exists(source) && File.ReadAllBytes(source).AsSpan().SequenceEqual(fixture);
            if (!preserved) RecordFailure(new InvalidOperationException("Synthetic source bytes changed."));
            File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new
            {
                passed = failure is null, verdict = failure is null ? "GREEN_SELECTION_POSITION" : failure is SelectionJumpException ? "RED_SELECTION_POSITION" : "SETUP_FAILURE",
                testCount = events.Count(e => e is SelectionObservation), stage, failedAt, failure = failure?.ToString(),
                machine = Environment.MachineName, processId = Environment.ProcessId, ownedWindow = root.ToInt64(),
                closed, ownedHandleDestroyed = !IsWindow(root), preserved, sourceSha256 = Convert.ToHexString(SHA256.HashData(fixture)), hashes, events,
                scope = "Ordinary multiline Text tool annotation near page bottom. Public editor and toolbar paths. Fit-page, settled 200%, and immediate Ctrl+wheel clicks. Does not establish scanned replacement timing."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failure is null ? 0 : 1;

        void RecordFailure(Exception error)
        {
            if (failure is null) failedAt = stage;
            failure ??= error;
            File.WriteAllText(Path.Combine(folder, "failure.txt"), failure.ToString());
        }
        void Stage(string value) { stage = value; File.WriteAllText(Path.Combine(folder, "stage.txt"), value); }
        IEnumerable<DependencyObject> Nodes() => PresentationSource.CurrentSources.OfType<HwndSource>()
            .Where(s => Owned(s.Handle) && s.RootVisual is not null).SelectMany(s => Descendants(s.RootVisual!)).ToArray();
        bool Owned(nint handle) => handle != 0 && IsWindow(handle)
            && GetWindowThreadProcessId(handle, out uint process) != 0 && process == Environment.ProcessId
            && (GetAncestor(handle, 2) == root || GetWindow(GetAncestor(handle, 2), 4) == root);
        bool IsPage(Canvas c) => c.IsVisible && c.Tag is int i && i == 0 && c.ActualWidth > 100 && c.ActualHeight > 100;
        Canvas Page() => Nodes().OfType<Canvas>().Where(IsPage).OrderByDescending(c => c.ActualWidth * c.ActualHeight).First();
        TextBlock? Marker() => Nodes().OfType<TextBlock>().FirstOrDefault(t => t.IsVisible && t.Text == Content && t.Parent is Border);
        FrameworkElement Frame() => Marker()?.Parent as FrameworkElement ?? throw new InvalidOperationException("Committed annotation frame absent.");
        Rect Placement() => Local(Frame());
        object Geometry()
        {
            Canvas p = Page();
            ScrollViewer v = Nodes().OfType<ScrollViewer>().First(s => s.Name == "PagePreviewPanel");
            return new { canvas = Bounds(p), p.ActualWidth, p.ActualHeight, layout = p.LayoutTransform.Value.ToString(),
                render = p.RenderTransform.Value.ToString(), screenScale = p.PointToScreen(new Point(1, 1)) - p.PointToScreen(new Point()),
                v.HorizontalOffset, v.VerticalOffset, v.ViewportWidth, v.ViewportHeight, v.ExtentWidth, v.ExtentHeight };
        }
        void Wait(Func<bool> check, int timeout = 15000)
        {
            var clock = Stopwatch.StartNew();
            while (!check())
            {
                if (failure is not null && !stage.EndsWith("-close", StringComparison.Ordinal))
                    throw new InvalidOperationException("Dispatcher failed at " + stage, failure);
                if (closed || app.Dispatcher.HasShutdownStarted) throw new InvalidOperationException("Owned host closed at " + stage);
                if (clock.ElapsedMilliseconds > timeout) throw new TimeoutException("Readiness deadline at " + stage);
                Pump(); Thread.Sleep(15);
            }
        }
        void Stable()
        {
            string previous = ""; int repeats = 0;
            Wait(() =>
            {
                owner.UpdateLayout();
                if (!Nodes().OfType<Canvas>().Any(IsPage)) { repeats = 0; return false; }
                string now = JsonSerializer.Serialize(Geometry());
                repeats = now == previous ? repeats + 1 : 0; previous = now;
                return repeats >= 8;
            });
        }
        void Tool(string name)
        {
            Button b = owner.FindName(name) as Button ?? throw new MissingMemberException(name);
            if (!b.IsVisible || !b.IsEnabled) throw new InvalidOperationException("Toolbar unavailable: " + name);
            new ButtonAutomationPeer(b).GetPattern(PatternInterface.Invoke).As<IInvokeProvider>().Invoke(); Pump();
        }
        void Zoom(string tag)
        {
            ComboBox box = owner.FindName("ZoomBox") as ComboBox ?? throw new MissingMemberException("ZoomBox");
            ComboBoxItem item = box.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, tag));
            box.SelectedItem = item; Pump();
        }
        void Reveal()
        {
            FrameworkElement frame = Frame();
            frame.BringIntoView(new Rect(0, 0, Math.Min(100, frame.ActualWidth), Math.Min(30, frame.ActualHeight))); Pump();
        }
        void SelectCase(string label, bool wheel)
        {
            Stage(label); Reveal(); Stable();
            Pause((int)GetDoubleClickTime() + 100);
            FrameworkElement f = Frame();
            Point point = f.PointToScreen(new Point(Math.Min(40, f.ActualWidth / 2), Math.Min(10, f.ActualHeight / 2)));
            Validate(point); SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y)); Pump();
            Rect before = Placement(), screenBefore = Bounds(Frame()); object geometryBefore = Geometry();
            if (wheel)
            {
                // Do not pump or wait between zoom dispatch and the fixed-position press.
                Validate(point);
                try { Send(Key(0x11, 0), MouseEvent(0x800, 120), Key(0x11, 2)); }
                finally { Send(Key(0x11, 2)); }
                Pump();
            }
            if (!Bounds(Frame()).Contains(point)) throw new InvalidOperationException("Zoom moved the fixed pointer outside the annotation; selection setup is inconclusive.");
            Click(point, move: false);
            Rect afterRelease = Placement(); object geometryRelease = Geometry();
            Stable(); Rect after = Placement();
            var observation = new SelectionObservation(label, before, afterRelease, after, screenBefore, Bounds(Frame()),
                geometryBefore, geometryRelease, Geometry(), Near(before, afterRelease) && Near(before, after));
            events.Add(observation);
            if (!observation.Unchanged) RecordFailure(new SelectionJumpException("Plain Select click changed committed canvas-local annotation geometry: " + label));
            Capture(label);
            if (failure is not null) throw failure;
        }
        void Validate(Point point)
        {
            var p = new NativePoint { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) };
            if (!Owned(root) || GetForegroundWindow() != root || !Owned(WindowFromPoint(p))
                || !SystemParameters.WorkArea.Contains(point)) throw new InvalidOperationException("Input endpoint is not visible and owned.");
        }
        void Click(Point point, bool move = true)
        {
            Validate(point);
            if (move) { SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y)); Pump(); }
            Validate(point);
            try { Send(MouseEvent(2)); Pump(); Send(MouseEvent(4)); Pump(); }
            finally { Send(MouseEvent(4)); }
        }
        void Pause(int duration) { var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < duration) { Pump(); Thread.Sleep(15); } }
        void Release() => Send(MouseEvent(4), Key(0x11, 2));
        void CloseOwned()
        {
            if (closed || !IsWindow(root)) return;
            Stage(stage + "-close");
            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(25) };
            timer.Tick += (_, _) =>
            {
                string? discard = app.TryFindResource("Str_Btn_DontSave") as string;
                foreach (Window dialog in app.Windows.Cast<Window>().Where(w => w != owner && w.Owner == owner && w.IsVisible).ToArray())
                {
                    Button? b = Descendants(dialog).OfType<Button>().FirstOrDefault(b => b.IsEnabled && b.IsVisible && Equals(b.Content, discard));
                    if (b is not null && discard is not null) new ButtonAutomationPeer(b).GetPattern(PatternInterface.Invoke).As<IInvokeProvider>().Invoke();
                }
            };
            timer.Start();
            try { owner.Dispatcher.BeginInvoke(new Action(owner.Close)); Wait(() => closed && !IsWindow(root), 5000); }
            finally { timer.Stop(); }
        }
        void Capture(string label)
        {
            Rect bounds = Bounds(owner);
            try
            {
                foreach (Point corner in new[] { bounds.TopLeft + new Vector(24, 24), bounds.BottomRight - new Vector(24, 24) }) Validate(corner);
            }
            catch (InvalidOperationException)
            {
                events.Add(new { capture = label, skipped = "Owned window capture corners are not available; geometry assertions still apply." });
                return;
            }
            nint screen = GetDC(0), memory = 0, bitmap = 0, previous = 0;
            try
            {
                int w = (int)Math.Ceiling(bounds.Width), h = (int)Math.Ceiling(bounds.Height);
                memory = CreateCompatibleDC(screen); bitmap = CreateCompatibleBitmap(screen, w, h); previous = SelectObject(memory, bitmap);
                if (screen == 0 || memory == 0 || bitmap == 0 || previous == 0 || previous == -1
                    || !BitBlt(memory, 0, 0, w, h, screen, (int)bounds.Left, (int)bounds.Top, 0x40CC0020)) throw new System.ComponentModel.Win32Exception();
                BitmapSource image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                string path = Path.Combine(folder, label + ".png"); using (var output = File.Create(path)) png.Save(output);
                events.Add(new { capture = label, sha256 = Hash(path), image.PixelWidth, image.PixelHeight, scope = "Diagnostic owned-window capture; no pixel acceptance claim." });
            }
            finally { if (previous != 0 && previous != -1 && memory != 0) SelectObject(memory, previous); if (bitmap != 0) DeleteObject(bitmap); if (memory != 0) DeleteDC(memory); if (screen != 0) ReleaseDC(0, screen); }
        }
    }

    private sealed record SelectionObservation(string Label, Rect Before, Rect AfterRelease, Rect AfterSettle, Rect ScreenBefore,
        Rect ScreenAfter, object GeometryBefore, object GeometryRelease, object GeometryAfter, bool Unchanged);
    private sealed class SelectionJumpException(string message) : Exception(message);
    private static T As<T>(this object? value) where T : class => value as T ?? throw new InvalidOperationException("Required public automation pattern absent: " + typeof(T).Name);
    private static Rect Local(FrameworkElement e) => new(Canvas.GetLeft(e), Canvas.GetTop(e), e.ActualWidth, e.ActualHeight);
    private static Rect Bounds(FrameworkElement e) => new(e.PointToScreen(new Point()), e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight)));
    private static bool Near(Rect a, Rect b) => Math.Abs(a.Left - b.Left) < .5 && Math.Abs(a.Top - b.Top) < .5 && Math.Abs(a.Width - b.Width) < .5 && Math.Abs(a.Height - b.Height) < .5;
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static byte[] BlankPdf()
    {
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>"];
        var text = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int>();
        foreach (string o in objects) { offsets.Add(Encoding.ASCII.GetByteCount(text.ToString())); text.Append($"{offsets.Count} 0 obj\n{o}\nendobj\n"); }
        int xref = Encoding.ASCII.GetByteCount(text.ToString()); text.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); return Encoding.ASCII.GetBytes(text.ToString());
    }
    private static Input MouseEvent(uint flags, uint data = 0) => new() { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = flags, Data = data } } };
    private static Input Key(ushort key, uint flags) => new() { Type = 1, Data = new InputUnion { Key = new KeyInput { Virtual = key, Flags = flags } } };
    private static void Send(params Input[] input) { if (SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) != input.Length) throw new System.ComponentModel.Win32Exception(); }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyInput Key; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyInput { public ushort Virtual, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dc, int x, int y, int width, int height, nint source, int sx, int sy, uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
