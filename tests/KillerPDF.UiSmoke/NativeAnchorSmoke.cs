using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
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
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class NativeAnchorSmoke
{
    internal static int Run(Window owner, string appPath, string appXaml, string folder)
    {
        Application? app = Application.Current;
        string stage = "starting";
        string? failedAt = null;
        Exception? failure = null;
        var events = new List<object>();
        var hashes = new Dictionary<string, string>();
        var modals = new HashSet<Window>();
        byte[] original = [];
        string path = Path.Combine(folder, "native-anchor.epub");
        nint hwnd = 0;
        bool preserved = false, closed = false;
        int assertions = 0;
        var overall = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(25) };
        DispatcherUnhandledExceptionEventHandler dispatcherFailure = (_, e) => { Fail(e.Exception); e.Handled = true; };
        timer.Tick += (_, _) =>
        {
            if (overall.Elapsed > TimeSpan.FromSeconds(140)) Fail(new TimeoutException("Overall native anchor deadline expired."));
            if (app is null) return;
            foreach (Window window in app.Windows.Cast<Window>().Where(w => w != owner && w.IsVisible).ToArray())
            {
                if (modals.Add(window)) events.Add(new { stage, unexpectedWindow = window.GetType().FullName, window.Title,
                    text = string.Join("\n", Descendants<TextBlock>(window).Select(t => t.Text)) });
                Fail(new InvalidOperationException("Unexpected owned modal at " + stage));
                try { window.Close(); } catch (Exception error) { Fail(error); }
            }
        };
        Directory.CreateDirectory(folder);
        try
        {
            Stage("binary-identities");
            hashes["app"] = Hash(File.ReadAllBytes(appPath));
            hashes["appXaml"] = Hash(File.ReadAllBytes(appXaml));
            hashes["driver"] = Hash(File.ReadAllBytes(typeof(NativeAnchorSmoke).Assembly.Location));
            Write("binaries.json", new { hashes, appPath, appXaml });
            Assembly engine = Assembly.Load("KillerPdf.Engine");
            hashes["engine"] = SelectedAssemblyIdentity.HashLoaded(engine, appPath);
            Write("binaries.json", new { hashes, engine.FullName, engine.Location, engine.ManifestModule.ModuleVersionId });
            Stage("vm-guard");
            if (!new[] { "SN01", "SN02" }.Contains(Environment.MachineName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Native anchor acceptance requires an exclusively owned SN01 or SN02.");
            if (app is null) throw new InvalidOperationException("Isolated application absent.");
            app.DispatcherUnhandledException += dispatcherFailure;
            timer.Start();
            Stage("fixture-and-detector-controls");
            original = EpubFixture();
            File.WriteAllBytes(path, original);
            foreach (string color in new[] { "blue", "black", "white" })
                if (Blue(Solid(color)) != (color == "blue"))
                    throw new InvalidOperationException("Blue detector independent presence/absence control failed: " + color);
            events.Add(new { stage, controls = "Solid blue accepted, solid black and white rejected." });
            owner.WindowState = WindowState.Normal;
            owner.Width = Math.Min(1200, SystemParameters.WorkArea.Width - 48);
            owner.Height = Math.Min(850, SystemParameters.WorkArea.Height - 48);
            if (!owner.IsVisible) owner.Show();
            owner.Activate();
            hwnd = new WindowInteropHelper(owner).Handle;
            if (!SetForegroundWindow(hwnd)) throw new InvalidOperationException("Cannot foreground owned acceptance window.");
            Stage("external-open");
            MethodInfo open = owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMethodException("MainWindow.OpenFromExternal");
            try { open.Invoke(owner, [path]); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
            Stage("single-view-and-fit-width");
            Wait(() => Named<RadioButton>(owner, "ViewSingleRadio") is { IsVisible: true, IsEnabled: true }
                && Named<ComboBox>(owner, "ZoomBox") is { IsVisible: true, IsEnabled: true });
            RadioButton single = Named<RadioButton>(owner, "ViewSingleRadio");
            Stage("single-view-provider");
            Require<ISelectionItemProvider>(new RadioButtonAutomationPeer(single).GetPattern(PatternInterface.SelectionItem)).Select();
            Observe("loaded-before-zoom", out _, out bool wholePageBlue, requireFitWidth: false);
            if (!wholePageBlue) throw new InvalidOperationException("Loaded native page does not show the fixture's blue target before zoom.");
            ComboBox zoom = Named<ComboBox>(owner, "ZoomBox");
            ComboBoxItem fit = zoom.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, "fitwidth"));
            var zoomPeer = UIElementAutomationPeer.CreatePeerForElement(zoom) ?? new ComboBoxAutomationPeer(zoom);
            var dropdown = Require<IExpandCollapseProvider>(zoomPeer.GetPattern(PatternInterface.ExpandCollapse));
            dropdown.Expand();
            Wait(() => fit.IsVisible && fit.IsEnabled);
            Stage("fit-width-item-provider");
            var peers = zoomPeer.GetChildren() ?? [];
            events.Add(new { stage, items = peers.Select(peer => new { name = peer.GetName(), type = peer.GetType().FullName,
                selection = peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider }).ToArray() });
            var fitPeer = peers.SingleOrDefault(peer => peer.GetName() == fit.Content?.ToString()
                && peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider)
                ?? throw new InvalidOperationException("Fit Width item public selection peer absent.");
            Require<ISelectionItemProvider>(fitPeer.GetPattern(PatternInterface.SelectionItem)).Select();
            dropdown.Collapse();
            Wait(() => single.IsChecked == true && ReferenceEquals(zoom.SelectedItem, fit));
            Observe("fit-width-before-outline", out _, out bool fitWidthBlue);
            if (fitWidthBlue) throw new InvalidOperationException("Fit Width must place the blue target below the visible viewport.");
            Stage("native-outline-readiness");
            Button outlineTab = Named<Button>(owner, "SidebarOutlinesTab");
            Product(outlineTab.IsEnabled, "Changing the native document view or zoom disables its outline sidebar.");
            if (!outlineTab.IsVisible) throw new InvalidOperationException("Enabled native outline tab is not visible.");
            TreeView tree = Named<TreeView>(owner, "OutlineTree");
            if (!tree.IsVisible) Invoke(Named<Button>(owner, "SidebarOutlinesTab"));
            Wait(() => tree.IsVisible && tree.IsEnabled && Opaque(tree));
            var rows = tree.Items.OfType<TreeViewItem>().ToArray();
            if (tree.Items.Count != 2 || rows.Length != 2 || !Equals(rows[0].Header, "Start") || !Equals(rows[1].Header, "Anchor"))
                throw new InvalidOperationException("Fixture native TOC must expose exactly Start and Anchor.");
            if (rows.Any(r => r.Items.Count != 0) || Descendants<TextBox>(tree).Any(t => t.IsVisible)
                || Descendants<TextBlock>(tree).Any(t => t.IsVisible && t.Text == Resource("Str_Ctx_BmAdd")))
                throw new InvalidOperationException("Native TOC contains unexpected hierarchy or editable PDF ghost.");
            Wait(() => rows.All(r => r.IsVisible && r.IsEnabled && Bounds(owner).Contains(Bounds(r))));
            Observe("initial", out double initialScroll, out bool initialBlue);
            if (initialScroll > .5 || initialBlue)
                throw new InvalidOperationException("Fixture initial viewport control requires scroll zero and no blue anchor.");
            Stage("select-anchor");
            Select(rows[1]);
            Observe("anchor", out double anchorScroll, out bool anchorBlue);
            Product(rows[1].IsSelected, "Anchor selection was lost.");
            Product(anchorScroll > initialScroll + 1 && anchorBlue,
                "Selecting the within-page Anchor must keep page one, scroll forward and reveal blue in the viewport.");
            Stage("select-start");
            Select(rows[0]);
            Observe("start", out double startScroll, out bool startBlue);
            Product(rows[0].IsSelected && startScroll < anchorScroll - 1 && !startBlue,
                "Selecting Start must keep page one, scroll back above the anchor and hide blue.");
            Stage("complete");
        }
        catch (Exception error)
        {
            Fail(error);
            if (owner.IsVisible && hwnd != 0)
                try { Capture(Bounds(owner), "failure-window"); }
                catch (Exception captureError) { Fail(new InvalidOperationException("Failure capture could not certify ownership.", captureError)); }
        }
        finally
        {
            timer.Stop();
            events.Add(new { stage = "teardown", prior = stage });
            try
            {
                preserved = original.Length > 0 && File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(original);
                if (original.Length > 0 && !preserved) Fail(new InvalidOperationException("Owned source changed or disappeared."));
                foreach (Window modal in modals.Where(w => w.IsVisible).ToArray()) modal.Close();
                if (hwnd != 0 && IsWindow(hwnd)) owner.Close();
                var closeDeadline = Stopwatch.StartNew();
                while (hwnd != 0 && IsWindow(hwnd) && closeDeadline.Elapsed < TimeSpan.FromSeconds(10)) { Pump(); Thread.Sleep(25); }
                closed = hwnd == 0 || !IsWindow(hwnd);
                if (!closed) Fail(new InvalidOperationException("Owned acceptance window did not close."));
            }
            catch (Exception error) { Fail(new InvalidOperationException("Acceptance teardown failed.", error)); }
            if (app is not null) app.DispatcherUnhandledException -= dispatcherFailure;
            Write("result.json", new { passed = failure is null && preserved && closed,
                verdict = failure is null ? "GREEN_NATIVE_ANCHOR" : failure is AnchorAssertionException ? "RED_NATIVE_ANCHOR" : "SETUP_FAILURE",
                stage, failedAt, failure = failure?.ToString(), assertions, preserved, closed, machine = Environment.MachineName,
                processId = Environment.ProcessId, ownerHandle = hwnd.ToInt64(), hashes, events,
                input = new { path, length = original.Length, sha256 = Hash(original) },
                scope = "Public external-open, Single and Fit Width selection providers, native outline selection providers, actual owned viewport screen pixels and offsets." });
        }
        return failure is null && preserved && closed ? 0 : 1;

        void Observe(string name, out double offset, out bool blue, bool requireFitWidth = true)
        {
            Stage(name + "-readiness");
            ScrollViewer viewport = Named<ScrollViewer>(owner, "PagePreviewPanel");
            Rect lastClip = Rect.Empty, lastPage = Rect.Empty, lastWindow = Rect.Empty;
            ImageSource? lastSource = null;
            double lastOffset = double.NaN;
            int stable = 0;
            var age = Stopwatch.StartNew();
            Wait(() =>
            {
                owner.UpdateLayout();
                ListBox pages = Named<ListBox>(owner, "PageList");
                TextBlock status = Named<TextBlock>(owner, "StatusText");
                string expected = string.Format(CultureInfo.CurrentCulture, Resource("Str_PageOf"), 1, 1);
                if (Named<TextBlock>(owner, "FileNameLabel").Text != Path.GetFileName(path) || pages.Items.Count != 1
                    || pages.SelectedIndex != 0 || !status.IsVisible || !status.Text.StartsWith(expected, StringComparison.Ordinal)
                    || Named<RadioButton>(owner, "ViewSingleRadio").IsChecked != true
                    || (requireFitWidth && Named<ComboBox>(owner, "ZoomBox").SelectedItem is not ComboBoxItem { Tag: "fitwidth" })) return false;
                var presenters = Descendants<ScrollContentPresenter>(viewport).Where(p => p.IsVisible).ToArray();
                var images = Descendants<Image>(viewport).Where(i => i.IsVisible && i.Source is BitmapSource && i.ActualWidth > 40 && i.ActualHeight > 40).ToArray();
                if (!viewport.IsVisible || presenters.Length != 1 || images.Length != 1 || !Opaque(images[0])) { stable = 0; age.Restart(); return false; }
                Rect clip = Rect.Intersect(Bounds(owner), Bounds(presenters[0]));
                Rect page = Bounds(images[0]), window = Bounds(owner);
                if (clip.IsEmpty || clip.Width < 40 || clip.Height < 40 || !clip.IntersectsWith(page)) return false;
                bool same = clip == lastClip && page == lastPage && window == lastWindow
                    && ReferenceEquals(lastSource, images[0].Source) && viewport.VerticalOffset == lastOffset;
                stable = same ? stable + 1 : 0;
                if (!same) age.Restart();
                lastClip = clip; lastPage = page; lastWindow = window; lastSource = images[0].Source; lastOffset = viewport.VerticalOffset;
                return stable >= 12 && age.Elapsed >= TimeSpan.FromMilliseconds(600);
            });
            Stage(name + "-screen-pixels");
            blue = Blue(Capture(lastClip, name + "-viewport"));
            offset = viewport.VerticalOffset;
            Capture(Bounds(owner), name + "-window");
            events.Add(new { stage, blue, offset, viewport.ScrollableHeight, viewport.ViewportHeight,
                clip = lastClip.ToString(), page = lastPage.ToString(), footer = Named<TextBlock>(owner, "StatusText").Text });
            assertions++;
        }
        void Wait(Func<bool> ready)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                Pump();
                if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                if (overall.Elapsed > TimeSpan.FromSeconds(140) || app?.Dispatcher.HasShutdownStarted == true || watch.Elapsed > TimeSpan.FromSeconds(20))
                    throw new TimeoutException("Readiness or locator deadline expired at " + stage);
                if (ready()) return;
                Thread.Sleep(25);
            }
        }
        void Product(bool condition, string message) { assertions++; if (!condition) throw new AnchorAssertionException(message); }
        void Stage(string value) { stage = value; File.WriteAllText(Path.Combine(folder, "stage.txt"), value); }
        void Fail(Exception error)
        {
            if (failure is null) { failedAt = stage; failure = error; }
            else if (failure is AnchorAssertionException && error is not AnchorAssertionException)
            { events.Add(new { originalProductFailure = failure.ToString() }); failure = error; }
            File.WriteAllText(Path.Combine(folder, "failure.txt"), failure.ToString());
        }
        void Write(string name, object value) => File.WriteAllText(Path.Combine(folder, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        string Resource(string key) => app?.TryFindResource(key) as string ?? throw new InvalidOperationException("Required locale resource absent: " + key);
        BitmapSource Capture(Rect bounds, string name)
        {
            BitmapSource image = ScreenCapture(owner, bounds);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            string capturePath = Path.Combine(folder, name + ".png");
            using (var stream = File.Create(capturePath)) encoder.Save(stream);
            events.Add(new { capture = name, bounds = bounds.ToString(), sha256 = Hash(File.ReadAllBytes(capturePath)), image.PixelWidth, image.PixelHeight });
            return image;
        }
    }

    private sealed class AnchorAssertionException(string message) : Exception(message);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static T Require<T>(object? value) where T : class => value as T ?? throw new InvalidOperationException("Public provider absent: " + typeof(T).Name);
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => (root as FrameworkElement)?.FindName(name) as T
        ?? Descendants<T>(root).SingleOrDefault(e => e.Name == name) ?? throw new InvalidOperationException("Required public control absent: " + name);
    private static void Invoke(Button button) => Require<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static void Select(TreeViewItem row) => Require<ISelectionItemProvider>((UIElementAutomationPeer.CreatePeerForElement(row)
        ?? new TreeViewItemAutomationPeer(row)).GetPattern(PatternInterface.SelectionItem)).Select();
    private static bool Opaque(DependencyObject element)
    {
        for (DependencyObject? current = element; current is Visual; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Opacity: < .999 }) return false;
        return true;
    }
    private static Rect Bounds(FrameworkElement element) => new(element.PointToScreen(new Point()), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static BitmapSource Solid(string color)
    {
        byte[] pixels = new byte[64 * 64 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = color is "blue" or "white" ? (byte)255 : (byte)0; pixels[i + 1] = pixels[i + 2] = color == "white" ? (byte)255 : (byte)0; pixels[i + 3] = 255; }
        return BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 256);
    }
    private static bool Blue(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4, hits = 0;
        byte[] pixels = new byte[stride * converted.PixelHeight]; converted.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i] > 170 && pixels[i + 1] < 85 && pixels[i + 2] < 85) hits++;
        return hits >= 40;
    }
    private static byte[] EpubFixture()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"EPUB/book.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            Add("EPUB/book.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">urn:test:native-anchor</dc:identifier><dc:title>Native anchor</dc:title><dc:language>en</dc:language><meta property=\"dcterms:modified\">2000-01-01T00:00:00Z</meta></metadata><manifest><item id=\"chapter\" href=\"chapter.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/></manifest><spine><itemref idref=\"chapter\"/></spine></package>");
            Add("EPUB/chapter.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Anchor chapter</title></head><body><p style=\"line-height:48px\">"
                + string.Join("<br/>", Enumerable.Range(1, 14).Select(i => "Preceding line " + i.ToString(CultureInfo.InvariantCulture)))
                + "</p><p id=\"target\" style=\"font-size:64px;color:#0000ff;line-height:80px\">AAA</p></body></html>");
            Add("EPUB/nav.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><head><title>Contents</title></head><body><nav epub:type=\"toc\"><ol><li><a href=\"chapter.xhtml\">Start</a></li><li><a href=\"chapter.xhtml#target\">Anchor</a></li></ol></nav></body></html>");
            void Add(string entryPath, string content, CompressionLevel compression = CompressionLevel.Optimal)
            { var entry = zip.CreateEntry(entryPath, compression); entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); using var stream = entry.Open(); stream.Write(Encoding.UTF8.GetBytes(content)); }
        }
        return output.ToArray();
    }
    private static BitmapSource ScreenCapture(Window owner, Rect bounds)
    {
        int left = (int)Math.Ceiling(bounds.Left), top = (int)Math.Ceiling(bounds.Top);
        int width = (int)Math.Floor(bounds.Right) - left, height = (int)Math.Floor(bounds.Bottom) - top;
        if (width < 40 || height < 40 || width > 4096 || height > 4096) throw new InvalidOperationException("Invalid owned screen capture size.");
        nint hwnd = new WindowInteropHelper(owner).Handle;
        int inset = bounds == Bounds(owner) ? 16 : 2;
        foreach (NativePoint point in new[] { new NativePoint(left + inset, top + inset), new(left + width - inset - 1, top + inset), new(left + inset, top + height - inset - 1), new(left + width - inset - 1, top + height - inset - 1), new(left + width / 2, top + height / 2) })
            if (GetAncestor(WindowFromPoint(point), 2) != hwnd) throw new InvalidOperationException("Owned capture is obscured or offscreen.");
        nint screen = 0, dc = 0, bitmap = 0, old = 0;
        try
        {
            screen = GetDC(0); dc = CreateCompatibleDC(screen); bitmap = CreateCompatibleBitmap(screen, width, height); old = SelectObject(dc, bitmap);
            if (screen == 0 || dc == 0 || bitmap == 0 || old == 0 || old == -1 || !BitBlt(dc, 0, 0, width, height, screen, left, top, 0x00CC0020)) throw new System.ComponentModel.Win32Exception();
            SelectObject(dc, old); old = 0;
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            byte[] pixels = new byte[width * height * 4];
            if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref info, 0) != height) throw new System.ComponentModel.Win32Exception();
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        }
        finally { if (old != 0 && old != -1) SelectObject(dc, old); if (bitmap != 0) DeleteObject(bitmap); if (dc != 0) DeleteDC(dc); if (screen != 0) ReleaseDC(0, screen); }
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important, Colors; }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint count, byte[] pixels, ref BitmapInfo info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
