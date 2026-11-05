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

internal static class NativeOutlineSmoke
{
    internal static int Run(Window owner, string appPath, string appXaml, string folder)
    {
        Application? app = Application.Current;
        string stage = "starting";
        string? failedAt = null;
        Exception? failure = null;
        var events = new List<object>();
        var hashes = new Dictionary<string, string>();
        var sources = new Dictionary<string, byte[]>();
        var modals = new HashSet<Window>();
        nint hwnd = 0;
        bool closed = false, preserved = false;
        int assertions = 0;
        var overall = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(25) };
        DispatcherUnhandledExceptionEventHandler dispatcherFailure = (_, e) => { Fail(e.Exception); e.Handled = true; };
        timer.Tick += (_, _) =>
        {
            if (overall.Elapsed > TimeSpan.FromSeconds(140)) Fail(new TimeoutException("Overall native outline deadline expired."));
            if (app is null) return;
            foreach (Window window in app.Windows.Cast<Window>().Where(w => w != owner && w.IsVisible).ToArray())
            {
                if (modals.Add(window))
                    events.Add(new { stage, unexpectedWindow = window.GetType().FullName, window.Title,
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
            hashes["driver"] = Hash(File.ReadAllBytes(typeof(NativeOutlineSmoke).Assembly.Location));
            Write("binaries.json", new { hashes, appPath, appXaml });
            Assembly engine = Assembly.Load("KillerPdf.Engine");
            hashes["engine"] = SelectedAssemblyIdentity.HashLoaded(engine, appPath);
            Write("binaries.json", new { hashes, engine.FullName, engine.Location, engine.ManifestModule.ModuleVersionId });
            Stage("vm-guard");
            if (!new[] { "SN01", "SN02" }.Contains(Environment.MachineName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Native outline acceptance requires an exclusively owned SN01 or SN02.");
            if (app is null) throw new InvalidOperationException("Isolated application absent.");
            app.DispatcherUnhandledException += dispatcherFailure;
            timer.Start();
            Stage("fixtures-and-detector-controls");
            var builderType = engine.GetType("KillerPdf.Engine.Authoring.PdfDocumentBuilder", true)!;
            var constructor = builderType.GetConstructors().Single();
            dynamic builder = constructor.Invoke(constructor.GetParameters().Select(p => p.DefaultValue).ToArray());
            string pdf = Path.Combine(folder, "blank-editable-control.pdf"), epub = Path.Combine(folder, "native-two-chapters.epub");
            sources.Add(pdf, (byte[])builder.AddBlankPage(600d, 750d).Build());
            sources.Add(epub, EpubFixture());
            foreach (var input in sources) File.WriteAllBytes(input.Key, input.Value);
            foreach (string color in new[] { "red", "blue", "blank" })
            {
                BitmapSource control = Solid(color);
                if (Detect(control, "red") != (color == "red") || Detect(control, "blue") != (color == "blue")
                    || Detect(control, "blank") != (color == "blank"))
                    throw new InvalidOperationException("Independent color detector presence/absence control failed.");
            }
            events.Add(new { stage, controls = "Independent red, blue and blank buffers each accept only their own detector." });
            owner.WindowState = WindowState.Normal;
            owner.Width = Math.Min(1200, SystemParameters.WorkArea.Width - 48);
            owner.Height = Math.Min(850, SystemParameters.WorkArea.Height - 48);
            if (!owner.IsVisible) owner.Show();
            owner.Activate(); hwnd = new WindowInteropHelper(owner).Handle;
            if (!SetForegroundWindow(hwnd)) throw new InvalidOperationException("Cannot foreground owned acceptance window.");

            Open(pdf); Ready(pdf, 1, 1, "blank", "pdf-control");
            Stage("editable-pdf-outline-control");
            Button tab = Named<Button>(owner, "SidebarOutlinesTab");
            if (!tab.IsVisible || !tab.IsEnabled) throw new InvalidOperationException("Editable PDF outline positive control unavailable.");
            ShowOutlines();
            Wait(() => Named<TreeView>(owner, "OutlineTree").IsVisible);
            TreeView tree = Named<TreeView>(owner, "OutlineTree");
            string addLabel = Resource("Str_Ctx_BmAdd");
            if (tree.Items.Count != 1 || tree.Items[0] is not TreeViewItem ghost
                || !Descendants<TextBlock>(ghost).Any(t => t.IsVisible && t.Text == addLabel))
                throw new InvalidOperationException("Editable blank PDF must expose exactly its visible Add bookmark control.");
            Capture(Bounds(owner), "pdf-outline-control-window");
            assertions++;

            Open(epub); Ready(epub, 2, 1, "red", "native-first");
            Stage("native-outline-enabled");
            tab = Named<Button>(owner, "SidebarOutlinesTab");
            Capture(Bounds(owner), "native-ready-before-outline-window");
            Product(tab.IsEnabled, "Rendered native EPUB has a disabled outline tab.");
            if (!tab.IsVisible) throw new InvalidOperationException("Enabled native outline tab is not visible.");
            ShowOutlines();
            Wait(() => Named<TreeView>(owner, "OutlineTree").IsVisible);
            CheckNativeTree("native-outlines");
            Stage("collapse-all");
            tree = Named<TreeView>(owner, "OutlineTree");
            TreeViewItem group = (TreeViewItem)tree.Items[0];
            Button expand = Named<Button>(owner, "OutlineExpandCollapseAllButton");
            if (!expand.IsVisible || !expand.IsEnabled) throw new InvalidOperationException("Public expansion control unavailable.");
            if (!group.IsExpanded) Invoke(expand);
            Wait(() => group.IsExpanded);
            Invoke(expand); Wait(() => !group.IsExpanded);
            Product(!((TreeViewItem)group.Items[1]).IsVisible, "Collapse all leaves a chapter visible.");
            Capture(Bounds(owner), "native-collapsed-window");
            Invoke(expand); Wait(() => group.IsExpanded && ((TreeViewItem)group.Items[1]).IsVisible);
            CheckNativeTree("native-expanded");
            Stage("select-second-chapter");
            TreeViewItem second = (TreeViewItem)group.Items[1];
            var peer = UIElementAutomationPeer.CreatePeerForElement(second) ?? new TreeViewItemAutomationPeer(second);
            Require<ISelectionItemProvider>(peer.GetPattern(PatternInterface.SelectionItem)).Select();
            Ready(epub, 2, 2, "blue", "native-second");
            Product(second.IsSelected, "Second chapter selection was lost.");

            Open(pdf); Ready(pdf, 1, 1, "blank", "pdf-return");
            Stage("pdf-return-outline-isolation");
            ShowOutlines();
            Wait(() => Named<TreeView>(owner, "OutlineTree").IsVisible);
            tree = Named<TreeView>(owner, "OutlineTree");
            Product(tree.Items.Count == 1 && tree.Items[0] is TreeViewItem pdfGhost
                && Descendants<TextBlock>(pdfGhost).Any(t => t.IsVisible && t.Text == addLabel)
                && !Descendants<TextBlock>(tree).Any(t => t.Text is "Contents" or "First chapter" or "Second chapter"),
                "Native outline data leaked into the blank PDF.");
            Open(epub);
            // Existing tabs may restore page two. Verify the loaded footer first, then select the public first row.
            Ready(epub, 2, null, null, "native-return-ready");
            Stage("native-return-outline-isolation");
            tab = Named<Button>(owner, "SidebarOutlinesTab");
            Product(tab.IsEnabled, "Native outline tab disabled after returning from PDF.");
            ShowOutlines(); Wait(() => Named<TreeView>(owner, "OutlineTree").IsVisible);
            CheckNativeTree("native-return");
            tree = Named<TreeView>(owner, "OutlineTree"); group = (TreeViewItem)tree.Items[0];
            if (!group.IsExpanded) Invoke(Named<Button>(owner, "OutlineExpandCollapseAllButton"));
            Wait(() => group.IsExpanded && ((TreeViewItem)group.Items[0]).IsVisible);
            var firstPeer = UIElementAutomationPeer.CreatePeerForElement((TreeViewItem)group.Items[0])
                ?? new TreeViewItemAutomationPeer((TreeViewItem)group.Items[0]);
            Require<ISelectionItemProvider>(firstPeer.GetPattern(PatternInterface.SelectionItem)).Select();
            Ready(epub, 2, 1, "red", "native-return-first");
            Stage("complete");
        }
        catch (Exception error)
        {
            Fail(error);
            if (hwnd != 0 && IsWindow(hwnd) && owner.IsVisible)
            {
                try
                {
                    owner.UpdateLayout();
                    Capture(Bounds(owner), "failure-window");
                    ScrollViewer viewport = Named<ScrollViewer>(owner, "PagePreviewPanel");
                    var presenter = Descendants<ScrollContentPresenter>(viewport).SingleOrDefault(p => p.IsVisible);
                    if (presenter is not null) Capture(Rect.Intersect(Bounds(owner), Bounds(presenter)), "failure-viewport");
                }
                catch (Exception captureError)
                {
                    events.Add(new { diagnostic = "failure-capture-error", error = captureError.ToString() });
                    Fail(new InvalidOperationException("Failure capture could not certify the owned surface.", captureError));
                }
            }
        }
        finally
        {
            timer.Stop();
            StageWithoutFailure("teardown", preserveStage: true);
            try
            {
                preserved = sources.All(s => File.Exists(s.Key) && File.ReadAllBytes(s.Key).AsSpan().SequenceEqual(s.Value));
                if (!preserved) Fail(new InvalidOperationException("An owned source changed or disappeared."));
                foreach (Window modal in modals.Where(w => w.IsVisible).ToArray()) modal.Close();
                if (hwnd != 0 && IsWindow(hwnd)) owner.Close();
                var closeDeadline = Stopwatch.StartNew();
                while (hwnd != 0 && IsWindow(hwnd) && closeDeadline.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Pump();
                    Thread.Sleep(25);
                }
                closed = hwnd == 0 || !IsWindow(hwnd);
                if (!closed) Fail(new InvalidOperationException("Owned acceptance window did not close."));
            }
            catch (Exception error) { Fail(new InvalidOperationException("Acceptance teardown failed.", error)); }
            if (app is not null) app.DispatcherUnhandledException -= dispatcherFailure;
            Write("result.json", new { passed = failure is null && preserved && closed,
                verdict = failure is null ? "GREEN_NATIVE_OUTLINES" : failure is OutlineAssertionException ? "RED_NATIVE_OUTLINES" : "SETUP_FAILURE",
                stage, failedAt, failure = failure?.ToString(), assertions, preserved, closed,
                machine = Environment.MachineName, processId = Environment.ProcessId, ownerHandle = hwnd.ToInt64(), hashes, events,
                inputs = sources.Select(s => new { path = s.Key, length = s.Value.Length, sha256 = Hash(s.Value) }),
                scope = "Owned native WPF window screen pixels, public external-open, footer radio, sidebar Invoke and tree SelectionItem providers. No private model injection, native pointer input or screen-reader claim." });
        }
        return failure is null && preserved && closed ? 0 : 1;

        void CheckNativeTree(string name)
        {
            Stage(name);
            TreeView outline = Named<TreeView>(owner, "OutlineTree");
            Product(outline.Items.Count == 1 && outline.Items[0] is TreeViewItem { Header: "Contents" }, "Native root label or read-only row count differs.");
            TreeViewItem root = (TreeViewItem)outline.Items[0];
            Product(root.Items.Count == 2 && root.Items[0] is TreeViewItem { Header: "First chapter" }
                && root.Items[1] is TreeViewItem { Header: "Second chapter" }, "Native chapter labels or hierarchy differ.");
            Product(!Descendants<TextBox>(outline).Any(t => t.IsVisible)
                && !Descendants<TextBlock>(outline).Any(t => t.IsVisible && t.Text == Resource("Str_Ctx_BmAdd")), "Native outline exposes PDF add/edit controls.");
            if (root.IsExpanded)
            {
                Wait(() => ((TreeViewItem)root.Items[0]).IsVisible && ((TreeViewItem)root.Items[1]).IsVisible && Opaque(outline));
                Product(Bounds(owner).Contains(Bounds((TreeViewItem)root.Items[1])), "Native chapter lies outside the owned window.");
            }
            Capture(Bounds(owner), name + "-window");
        }
        void ShowOutlines()
        {
            // An already active rail button opens its position menu. Select Pages first so Invoke opens Outlines.
            if (Named<TreeView>(owner, "OutlineTree").IsVisible)
            {
                Invoke(Named<Button>(owner, "SidebarPagesTab"));
                Wait(() => !Named<TreeView>(owner, "OutlineTree").IsVisible);
            }
            Invoke(Named<Button>(owner, "SidebarOutlinesTab"));
        }
        void Open(string path)
        {
            Stage("open-" + Path.GetFileName(path));
            MethodInfo open = owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMethodException("MainWindow.OpenFromExternal");
            try { open.Invoke(owner, [path]); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
        }
        void Ready(string path, int count, int? pageNumber, string? color, string name)
        {
            Stage(name + "-readiness");
            Wait(() => owner.IsVisible && owner.IsEnabled && Named<RadioButton>(owner, "ViewSingleRadio") is { IsVisible: true, IsEnabled: true });
            RadioButton single = Named<RadioButton>(owner, "ViewSingleRadio");
            Require<ISelectionItemProvider>(new RadioButtonAutomationPeer(single).GetPattern(PatternInterface.SelectionItem)).Select();
            Image? page = null;
            Rect last = Rect.Empty, lastWindow = Rect.Empty, lastViewport = Rect.Empty;
            ImageSource? previous = null;
            int stable = 0;
            Wait(() =>
            {
                owner.UpdateLayout();
                TextBlock status = Named<TextBlock>(owner, "StatusText");
                string prefix = pageNumber is int number ? string.Format(CultureInfo.CurrentCulture, Resource("Str_PageOf"), number, count) : "";
                ListBox list = Named<ListBox>(owner, "PageList");
                if (Named<TextBlock>(owner, "FileNameLabel").Text != Path.GetFileName(path) || list.Items.Count != count
                    || (pageNumber is int p && list.SelectedIndex != p - 1) || !status.IsVisible
                    || (pageNumber is not null && !status.Text.StartsWith(prefix, StringComparison.Ordinal))
                    || (pageNumber is null && !Enumerable.Range(1, count).Any(n => status.Text.StartsWith(
                        string.Format(CultureInfo.CurrentCulture, Resource("Str_PageOf"), n, count), StringComparison.Ordinal)))
                    || single.IsChecked != true) return false;
                ScrollViewer viewport = Named<ScrollViewer>(owner, "PagePreviewPanel");
                var presenters = Descendants<ScrollContentPresenter>(viewport).Where(v => v.IsVisible).ToArray();
                var images = Descendants<Image>(viewport).Where(i => i.IsVisible && i.Source is BitmapSource && i.ActualWidth > 40 && i.ActualHeight > 40).ToArray();
                if (presenters.Length != 1 || images.Length != 1 || !Opaque(images[0])) { stable = 0; return false; }
                page = images[0];
                Rect bounds = Bounds(page), window = Bounds(owner), clip = Bounds(presenters[0]);
                if (!window.Contains(bounds) || !clip.Contains(bounds)) { stable = 0; return false; }
                stable = bounds == last && window == lastWindow && clip == lastViewport && ReferenceEquals(previous, page.Source) ? stable + 1 : 0;
                last = bounds; lastWindow = window; lastViewport = clip; previous = page.Source;
                return stable >= 7;
            });
            Stage(name + "-screen-pixels");
            BitmapSource pixels = Capture(last, name + "-page");
            Capture(lastViewport, name + "-viewport");
            bool red = Detect(pixels, "red"), blue = Detect(pixels, "blue"), blank = Detect(pixels, "blank");
            events.Add(new { stage, red, blue, blank, footer = Named<TextBlock>(owner, "StatusText").Text, bounds = last.ToString(), opaque = Opaque(page!) });
            if (color is not null && (red != (color == "red") || blue != (color == "blue") || blank != (color == "blank")))
                throw new InvalidOperationException("Owned screen color readiness control failed: " + name);
            assertions++;
        }
        void Wait(Func<bool> ready)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                Pump();
                if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                if (app?.Dispatcher.HasShutdownStarted == true || watch.Elapsed > TimeSpan.FromSeconds(20))
                    throw new TimeoutException("Readiness or locator deadline expired at " + stage);
                if (ready()) return;
                Thread.Sleep(25);
            }
        }
        void Product(bool condition, string message) { assertions++; if (!condition) throw new OutlineAssertionException(message); }
        void Stage(string value) { stage = value; File.WriteAllText(Path.Combine(folder, "stage.txt"), value); }
        void StageWithoutFailure(string value, bool preserveStage) { events.Add(new { stage = value, prior = stage }); if (!preserveStage) Stage(value); }
        void Fail(Exception error)
        {
            if (failure is null) { failedAt = stage; failure = error; }
            else if (failure is OutlineAssertionException && error is not OutlineAssertionException)
            { events.Add(new { originalProductFailure = failure.ToString() }); failure = error; }
            File.WriteAllText(Path.Combine(folder, "failure.txt"), failure.ToString());
        }
        void Write(string name, object value) => File.WriteAllText(Path.Combine(folder, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        string Resource(string key) => app?.TryFindResource(key) as string ?? throw new InvalidOperationException("Required locale resource absent: " + key);
        BitmapSource Capture(Rect bounds, string name)
        {
            BitmapSource image = ScreenCapture(owner, bounds);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            string path = Path.Combine(folder, name + ".png"); using (var stream = File.Create(path)) encoder.Save(stream);
            events.Add(new { capture = name, bounds = bounds.ToString(), sha256 = Hash(File.ReadAllBytes(path)), image.PixelWidth, image.PixelHeight });
            return image;
        }
    }

    private sealed class OutlineAssertionException(string message) : Exception(message);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static T Require<T>(object? value) where T : class => value as T ?? throw new InvalidOperationException("Public provider absent: " + typeof(T).Name);
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => (root as FrameworkElement)?.FindName(name) as T
        ?? Descendants<T>(root).SingleOrDefault(e => e.Name == name)
        ?? throw new InvalidOperationException("Required public control absent: " + name);
    private static void Invoke(Button button)
    {
        if (!button.IsVisible || !button.IsEnabled) throw new InvalidOperationException("Public button unavailable: " + button.Name);
        Require<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }
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
        { pixels[i] = color == "red" ? (byte)0 : (byte)255; pixels[i + 1] = color == "blank" ? (byte)255 : (byte)0; pixels[i + 2] = color == "blue" ? (byte)0 : (byte)255; pixels[i + 3] = 255; }
        return BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 256);
    }
    private static bool Detect(BitmapSource image, string color)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth, height = converted.PixelHeight, stride = width * 4, hit = 0, total = 0;
        byte[] pixels = new byte[stride * height]; converted.CopyPixels(pixels, stride, 0);
        for (int y = height / 4; y < height * 3 / 4; y++)
            for (int x = width / 4; x < width * 3 / 4; x++)
            {
                int i = y * stride + x * 4; byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2]; total++;
                if (color == "red" ? r > 200 && g < 55 && b < 55 : color == "blue" ? b > 200 && g < 55 && r < 55 : r > 225 && g > 225 && b > 225) hit++;
            }
        return total > 0 && (double)hit / total > .95;
    }
    private static byte[] EpubFixture()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"EPUB/book.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            Add("EPUB/book.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">urn:test:native-outlines</dc:identifier><dc:title>Owned native outlines</dc:title><dc:language>en</dc:language><meta property=\"dcterms:modified\">2000-01-01T00:00:00Z</meta></metadata><manifest><item id=\"first\" href=\"first.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"second\" href=\"second.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/></manifest><spine><itemref idref=\"first\"/><itemref idref=\"second\"/></spine></package>");
            Add("EPUB/first.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>First chapter</title></head><body style=\"background-color:#ff0000\"><p>First chapter</p></body></html>");
            Add("EPUB/second.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Second chapter</title></head><body style=\"background-color:#0000ff\"><p>Second chapter</p></body></html>");
            Add("EPUB/nav.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><head><title>Contents</title></head><body><nav epub:type=\"toc\"><ol><li><span>Contents</span><ol><li><a href=\"first.xhtml\">First chapter</a></li><li><a href=\"second.xhtml\">Second chapter</a></li></ol></li></ol></nav></body></html>");
            void Add(string path, string text, CompressionLevel compression = CompressionLevel.Optimal)
            { var entry = zip.CreateEntry(path, compression); entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); using var stream = entry.Open(); stream.Write(Encoding.UTF8.GetBytes(text)); }
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
