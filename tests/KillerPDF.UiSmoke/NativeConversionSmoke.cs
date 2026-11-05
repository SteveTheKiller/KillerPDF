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
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;

internal static class NativeConversionSmoke
{
    public static int Run(Window owner, string appPath, string appXaml, string folder, bool djvu = false, bool customPaper = false)
    {
        string stage = "vm-guard";
        string? failedAt = null;
        Exception? failure = null;
        bool admitted = false, closed = false, preserved = false, modalBusy = false;
        bool cancelOptions = false, optionsExpected = false, pickerExpected = false;
        bool optionsHandled = false, pickerHandled = false;
        int assertions = 0;
        var events = new List<object>();
        var hashes = new Dictionary<string, string>();
        var handled = new HashSet<Window>();
        var unexpected = new HashSet<Window>();
        string native = Path.Combine(folder, djvu ? "native-source.djvu" : "native-source.epub");
        string control = Path.Combine(folder, "ordinary-control.pdf");
        string output = Path.Combine(folder, "explicit-converted.pdf");
        byte[] original = [];
        nint hwnd = 0;
        Application? app = Application.Current;
        var overall = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(40) };
        DispatcherUnhandledExceptionEventHandler dispatcherFailure = (_, e) => { Fail(e.Exception); e.Handled = true; };
        timer.Tick += (_, _) =>
        {
            if (overall.Elapsed > TimeSpan.FromSeconds(140)) Fail(new TimeoutException("Native conversion overall deadline expired."));
            if (app is null || modalBusy) return;
            foreach (Window window in app.Windows.Cast<Window>().Where(w => w != owner && w.IsVisible).ToArray())
            {
                if (failure is not null)
                {
                    try { window.Close(); } catch (Exception error) { Fail(error); }
                    continue;
                }
                if (handled.Contains(window)) continue;
                modalBusy = true;
                try
                {
                    if (optionsExpected && window.Owner == owner && window.GetType().Name == "NativePdfConversionDialog")
                    {
                        window.UpdateLayout();
                        ComboBox paper = Named<ComboBox>(window, "NativeConversionPaper");
                        Button action = Named<Button>(window, cancelOptions ? "NativeConversionCancel" : "NativeConversionStart");
                        if (!paper.IsVisible || !action.IsVisible || !action.IsEnabled) continue;
                        Product(paper.SelectedIndex == 0 && paper.Items.Count == 4, "EPUB conversion must default to A4 and expose four paper choices.");
                        Product(Number("NativeConversionWidth") == 210 && Number("NativeConversionHeight") == 297
                            && Number("NativeConversionMargin") == 15, "EPUB conversion defaults must be 210 x 297 mm with 15 mm margins.");
                        if (customPaper && !cancelOptions)
                        {
                            SelectPaper(1, "Letter");
                            Product(Number("NativeConversionWidth") == 215.9 && Number("NativeConversionHeight") == 279.4,
                                "Letter must populate 215.9 x 279.4 mm dimensions.");
                            SelectPaper(2, "Legal");
                            Product(Number("NativeConversionWidth") == 215.9 && Number("NativeConversionHeight") == 355.6,
                                "Legal must populate 215.9 x 355.6 mm dimensions.");
                            SetNumber("NativeConversionWidth", 127);
                            SetNumber("NativeConversionHeight", 177.8);
                            SetNumber("NativeConversionMargin", 10);
                            Product(paper.SelectedIndex == 3 && Number("NativeConversionWidth") == 127
                                && Number("NativeConversionHeight") == 177.8 && Number("NativeConversionMargin") == 10,
                                "Edited dimensions must select Custom and retain 127 x 177.8 mm paper with 10 mm margins.");
                            events.Add(new { stage, customPaper, paperIndex = paper.SelectedIndex,
                                widthMm = Number("NativeConversionWidth"), heightMm = Number("NativeConversionHeight"),
                                marginMm = Number("NativeConversionMargin") });
                        }
                        handled.Add(window); optionsHandled = true; optionsExpected = false;
                        events.Add(new { stage, expectedModal = window.GetType().FullName, cancelOptions });
                        owner.Dispatcher.BeginInvoke(new Action(() => Invoke(action)));
                        double Number(string name) => double.Parse(Named<TextBox>(window, name).Text, CultureInfo.CurrentCulture);
                        void SetNumber(string name, double value)
                        {
                            TextBox field = Named<TextBox>(window, name);
                            if (!field.IsVisible || !field.IsEnabled) throw new InvalidOperationException("Public dimension field unavailable: " + name);
                            Require<IValueProvider>(new TextBoxAutomationPeer(field).GetPattern(PatternInterface.Value))
                                .SetValue(value.ToString(CultureInfo.CurrentCulture));
                        }
                        void SelectPaper(int index, string label)
                        {
                            if (!paper.IsEnabled) throw new InvalidOperationException("Public paper selector unavailable.");
                            var peer = UIElementAutomationPeer.CreatePeerForElement(paper) ?? new ComboBoxAutomationPeer(paper);
                            var dropdown = Require<IExpandCollapseProvider>(peer.GetPattern(PatternInterface.ExpandCollapse));
                            dropdown.Expand();
                            Wait(() => paper.ItemContainerGenerator.ContainerFromIndex(index) is ComboBoxItem { IsVisible: true, IsEnabled: true });
                            var itemPeer = (peer.GetChildren() ?? []).SingleOrDefault(item => item.GetName() == label
                                && item.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider)
                                ?? throw new InvalidOperationException("Public paper item selection provider absent: " + label);
                            Require<ISelectionItemProvider>(itemPeer.GetPattern(PatternInterface.SelectionItem)).Select();
                            dropdown.Collapse();
                            Product(paper.SelectedIndex == index, "Public paper selection must choose " + label + ".");
                            events.Add(new { stage, paperIndex = paper.SelectedIndex, paper = label,
                                widthMm = Number("NativeConversionWidth"), heightMm = Number("NativeConversionHeight") });
                        }
                        continue;
                    }
                    if (pickerExpected && window.Owner == owner && window.GetType().FullName == "KillerPDF.Controls.FileDialog")
                    {
                        window.UpdateLayout();
                        TextBox name = Named<TextBox>(window, "FileNameBox");
                        Button accept = Named<Button>(window, "AcceptButton");
                        if (!name.IsVisible || !name.IsEnabled || !accept.IsVisible) continue;
                        Require<IValueProvider>(new TextBoxAutomationPeer(name).GetPattern(PatternInterface.Value)).SetValue(output);
                        if (!accept.IsEnabled) continue;
                        handled.Add(window); pickerHandled = true; pickerExpected = false;
                        events.Add(new { stage, expectedModal = window.GetType().FullName, output });
                        owner.Dispatcher.BeginInvoke(new Action(() => Invoke(accept))); continue;
                    }
                    if (unexpected.Add(window)) events.Add(new { stage, unexpectedModal = window.GetType().FullName, window.Title });
                    throw new InvalidOperationException("Unexpected modal at " + stage + ": " + window.GetType().FullName);
                }
                catch (Exception error) { Fail(error); try { window.Close(); } catch (Exception closeError) { Fail(closeError); } }
                finally { modalBusy = false; }
            }
        };
        Directory.CreateDirectory(folder);
        try
        {
            // Admission precedes every visible operation and every input operation, including cleanup.
            if (!new[] { "SN01", "SN02" }.Contains(Environment.MachineName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Native conversion acceptance requires an exclusively owned SN01 or SN02.");
            admitted = true;
            if (app is null) throw new InvalidOperationException("Isolated WPF application absent.");
            if (File.Exists(native) || File.Exists(control) || File.Exists(output)) throw new InvalidOperationException("Use a fresh owned fixture folder.");
            app.DispatcherUnhandledException += dispatcherFailure;
            timer.Start();
            Stage("identities-and-owned-fixtures");
            hashes["app"] = Hash(File.ReadAllBytes(appPath));
            hashes["appXaml"] = Hash(File.ReadAllBytes(appXaml));
            hashes["driver"] = Hash(File.ReadAllBytes(typeof(NativeConversionSmoke).Assembly.Location));
            hashes["engine"] = SelectedAssemblyIdentity.HashLoaded(typeof(PdfDocument).Assembly, appPath);
            hashes["loadedApp"] = SelectedAssemblyIdentity.HashLoaded(owner.GetType().Assembly, appPath);
            original = djvu ? DjvuFixture() : EpubFixture();
            hashes["nativeOriginal"] = Hash(original);
            File.WriteAllBytes(native, original);
            byte[] ordinary = new PdfDocumentBuilder().AddBlankPage(600, 750).Build();
            hashes["ordinaryControl"] = Hash(ordinary); File.WriteAllBytes(control, ordinary);
            Write("binaries.json", new { hashes, appPath, appXaml });
            owner.WindowState = WindowState.Normal;
            owner.Width = Math.Min(1200, SystemParameters.WorkArea.Width - 48);
            owner.Height = Math.Min(850, SystemParameters.WorkArea.Height - 48);
            if (!owner.IsVisible) owner.Show();
            owner.Activate(); hwnd = new WindowInteropHelper(owner).Handle;
            if (!SetForegroundWindow(hwnd)) throw new InvalidOperationException("Cannot foreground owned window.");

            Open(control); Ready(control);
            Stage("ordinary-pdf-negative-command-control");
            Menu(false); Escape();
            Open(native); Ready(native);
            Stage("native-positive-convert-command");
            MenuItem command = Menu(true);
            if (!djvu)
            {
                Stage("cancel-native-conversion-options");
                cancelOptions = true; optionsExpected = true;
                QueueInvoke(command);
                Wait(() => optionsHandled && owner.IsEnabled && NoVisibleModal());
                Product(!File.Exists(output) && File.ReadAllBytes(native).SequenceEqual(original), "Cancel must preserve the native source and publish no PDF.");
                Ready(native);
                optionsHandled = false; cancelOptions = false;
                command = Menu(true);
            }
            Stage("explicit-native-conversion");
            optionsExpected = !djvu; pickerExpected = true;
            QueueInvoke(command);
            Wait(() => pickerHandled && (djvu || optionsHandled) && File.Exists(output) && owner.IsEnabled && NoVisibleModal());
            Ready(output);
            Stage("published-independent-pdf");
            byte[] converted = File.ReadAllBytes(output); hashes["converted"] = Hash(converted);
            Product(converted.AsSpan().StartsWith("%PDF-"u8) && !converted.SequenceEqual(original), "Conversion must publish independent PDF bytes.");
            var pdf = PdfDocument.Open(converted);
            var pages = PdfPageInformation.Read(pdf);
            Product(pages.Count == 1, "The owned fixture must produce one PDF page.");
            var reader = new PdfPageContentReader(pdf);
            string text = string.Concat(reader.Read(0).Letters.Select(letter => letter.Value));
            if (djvu)
            {
                Product(Math.Abs(pages[0].PhysicalWidth - 1.5) < .000001 && Math.Abs(pages[0].PhysicalHeight - 1) < .000001,
                    "DjVu must preserve its 144 DPI physical page size.");
                var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(3, 2));
                Product(!rendered.Diagnostics.Any() && rendered.Pixels.ToArray().Where((_, i) => i % 4 == 0)
                    .SequenceEqual(new byte[] { 255, 0, 0, 255, 255, 255 }), "DjVu PDF must preserve the literal asymmetric bitonal fixture.");
                Product(text.Length == 0, "The text-free DjVu fixture must remain text-free.");
            }
            else if (customPaper)
            {
                Product(Math.Abs(pages[0].PhysicalWidth - 360) < .002
                    && Math.Abs(pages[0].PhysicalHeight - 504) < .002, "Custom EPUB conversion must use 360 x 504 point paper.");
                Product(text == "ALPHA BRAVO", "Custom EPUB conversion must preserve the exact selectable ASCII content.");
                Product(reader.Read(0).Letters.All(letter => letter.BoundingBox.Left >= 28.3455 - .002
                    && letter.BoundingBox.Right <= 331.6545 + .002 && letter.BoundingBox.Bottom >= 28.3455 - .002
                    && letter.BoundingBox.Top <= 475.6545 + .002), "EPUB text must respect the selected 10 mm margins.");
            }
            else
            {
                Product(Math.Abs(pages[0].PhysicalWidth - 595.2755905511812) < .001
                    && Math.Abs(pages[0].PhysicalHeight - 841.8897637795276) < .001, "Default EPUB conversion must use A4 paper.");
                Product(text == "ALPHA BRAVO", "EPUB conversion must preserve the exact selectable ASCII content.");
                Product(reader.Read(0).Letters.All(letter => letter.BoundingBox.Left >= 42.518 - .001
                    && letter.BoundingBox.Right <= 552.757 + .001 && letter.BoundingBox.Bottom >= 42.518 - .001
                    && letter.BoundingBox.Top <= 799.372 + .001), "EPUB text must respect the default 15 mm margins.");
            }
            events.Add(new { stage, pages = pages.Count, text, width = pages[0].PhysicalWidth, height = pages[0].PhysicalHeight });
            Menu(false); Escape();
            Stage("original-native-tab-preserved");
            SwitchExistingTab(native); Ready(native); Menu(true); Escape();
            Product(File.ReadAllBytes(native).SequenceEqual(original), "Explicit conversion must leave original native bytes unchanged.");
            Stage("converted-tab-preserved");
            SwitchExistingTab(output); Ready(output); Menu(false); Escape();
            Stage("complete");
        }
        catch (Exception error) { Fail(error); }
        finally
        {
            timer.Stop();
            if (app is not null) app.DispatcherUnhandledException -= dispatcherFailure;
            if (admitted)
            {
                try { Send([Key(0x10, 2), Key(0x11, 2), Key(0x12, 2), Key(0x44, 2)]); } catch (Exception error) { Fail(error); }
                foreach (Window window in app?.Windows.Cast<Window>().Where(w => w != owner).ToArray() ?? [])
                    try { window.Close(); } catch (Exception error) { Fail(error); }
                try
                {
                    owner.Close();
                    var closeDeadline = Stopwatch.StartNew();
                    while (owner.IsVisible && closeDeadline.Elapsed < TimeSpan.FromSeconds(10))
                    {
                        Pump();
                        Thread.Sleep(25);
                    }
                    closed = !owner.IsVisible && (hwnd == 0 || !IsWindow(hwnd));
                    if (!closed) Fail(new InvalidOperationException("The owned acceptance window did not close."));
                }
                catch (Exception error) { Fail(error); }
            }
            preserved = original.Length > 0 && File.Exists(native) && File.ReadAllBytes(native).SequenceEqual(original);
            Write("result.json", new { passed = failure is null && closed && preserved, stage, failedAt, failure = failure?.ToString(),
                assertions, admitted, closed, preserved, unexpectedModals = unexpected.Count, hashes, events,
                elapsedSeconds = overall.Elapsed.TotalSeconds, visualScope = "Visible WPF page and viewport only; no screen pixel acceptance claim." });
        }
        return failure is null && closed && preserved ? 0 : 1;

        void SwitchExistingTab(string path)
        {
            // Ctrl+Tab cycles existing documents without invoking the file-open path.
            string filename = Path.GetFileName(path);
            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (Named<TextBlock>(owner, "FileNameLabel").Text == filename) return;
                Shortcut(0x09, false);
                var until = Stopwatch.StartNew();
                while (until.Elapsed < TimeSpan.FromMilliseconds(200)) { Pump(); Thread.Sleep(20); }
            }
            Product(false, "Expected original or converted independent tab is absent: " + filename);
        }
        bool NoVisibleModal() => !app!.Windows.Cast<Window>().Any(w => w != owner && w.IsVisible);
        void Open(string path)
        {
            Stage("open-" + Path.GetFileName(path));
            MethodInfo open = owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMethodException("MainWindow.OpenFromExternal");
            try { open.Invoke(owner, [path]); }
            catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
        }
        void Ready(string path)
        {
            Stage("ready-" + Path.GetFileName(path));
            Wait(() => owner.IsEnabled && Named<TextBlock>(owner, "FileNameLabel").Text == Path.GetFileName(path)
                && Named<ListBox>(owner, "PageList").Items.Count == 1);
            RadioButton single = Named<RadioButton>(owner, "ViewSingleRadio");
            Require<ISelectionItemProvider>(new RadioButtonAutomationPeer(single).GetPattern(PatternInterface.SelectionItem)).Select();
            int stable = 0; Rect previous = Rect.Empty;
            Wait(() =>
            {
                owner.UpdateLayout();
                ScrollViewer viewport = Named<ScrollViewer>(owner, "PagePreviewPanel");
                Image[] images = Descendants<Image>(viewport).Where(i => i.IsVisible && i.Source is BitmapSource
                    && i.ActualWidth > 40 && i.ActualHeight > 40).ToArray();
                if (images.Length != 1 || !viewport.IsVisible) return false;
                Rect bounds = Bounds(images[0]);
                stable = bounds == previous ? stable + 1 : 0; previous = bounds;
                return stable >= 7 && Bounds(owner).IntersectsWith(bounds) && Bounds(viewport).IntersectsWith(bounds);
            });
            events.Add(new { stage, visiblePage = previous.ToString(), filename = Named<TextBlock>(owner, "FileNameLabel").Text });
            assertions++;
        }
        MenuItem Menu(bool expectNative)
        {
            Shortcut(0x44, true);
            Wait(() => Menus().Any());
            MenuItem? conversion = Menus().SingleOrDefault(item => Equals(item.Header, "Convert to PDF..."));
            Product(expectNative ? conversion is { IsVisible: true, IsEnabled: true } : conversion is null,
                expectNative ? "Native document must expose an enabled visible Convert to PDF... command." : "Ordinary PDF must not expose the native conversion command.");
            return conversion!;
        }
        IEnumerable<MenuItem> Menus() => PresentationSource.CurrentSources.Cast<PresentationSource>().OfType<HwndSource>()
            .Where(source => source.RootVisual is not null && GetAncestor(source.Handle, 3) == hwnd)
            .SelectMany(source => Descendants<MenuItem>((DependencyObject)source.RootVisual)).Where(item => item.IsVisible);
        void QueueInvoke(MenuItem item)
        {
            var peer = new MenuItemAutomationPeer(item);
            var provider = Require<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
            owner.Dispatcher.BeginInvoke(new Action(provider.Invoke));
        }
        void Escape() { CheckInput(); Send([Key(0x1B), Key(0x1B, 2)]); Pump(); }
        void Shortcut(ushort key, bool shift)
        {
            CheckInput();
            Send(shift ? [Key(0x11), Key(0x10), Key(key), Key(key, 2), Key(0x10, 2), Key(0x11, 2)]
                : [Key(0x11), Key(key), Key(key, 2), Key(0x11, 2)]);
        }
        void CheckInput()
        {
            if (!admitted || !owner.IsVisible || GetAncestor(GetForegroundWindow(), 2) != hwnd)
                throw new InvalidOperationException("Owned foreground input guard failed.");
        }
        void Wait(Func<bool> predicate)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                Pump(); if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                if (overall.Elapsed > TimeSpan.FromSeconds(140) || watch.Elapsed > TimeSpan.FromSeconds(20))
                    throw new TimeoutException("Readiness deadline expired at " + stage);
                if (predicate()) return; Thread.Sleep(25);
            }
        }
        void Product(bool condition, string message) { assertions++; if (!condition) throw new ProductAssertionException(message); }
        void Stage(string value) { stage = value; File.WriteAllText(Path.Combine(folder, "stage.txt"), value); }
        void Fail(Exception error) { if (failure is null) { failedAt = stage; failure = error; } File.WriteAllText(Path.Combine(folder, "failure.txt"), failure.ToString()); }
        void Write(string name, object value) => File.WriteAllText(Path.Combine(folder, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static byte[] EpubFixture()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"EPUB/book.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            Add("EPUB/book.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">urn:test:epub-conversion</dc:identifier><dc:title>Conversion</dc:title><dc:language>en</dc:language></metadata><manifest><item id=\"chapter\" href=\"text.xhtml\" media-type=\"application/xhtml+xml\"/></manifest><spine><itemref idref=\"chapter\"/></spine></package>");
            Add("EPUB/text.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Conversion</title></head><body><p>ALPHA BRAVO</p></body></html>");
            void Add(string path, string text, CompressionLevel level = CompressionLevel.Optimal)
            { var entry = zip.CreateEntry(path, level); entry.LastWriteTime = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero); using var stream = entry.Open(); stream.Write(Encoding.UTF8.GetBytes(text)); }
        }
        return buffer.ToArray();
    }
    private static byte[] DjvuFixture() => [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVU"u8,
        .. Chunk("INFO", [0, 3, 0, 2, 26, 0, 144, 0, 22, 1]),
        .. Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 2, 0x23, 0xe1, 0x80, 0x08, 0, 0x80])])];
    private static byte[] Chunk(string id, byte[] payload) => [.. Encoding.ASCII.GetBytes(id),
        (byte)(payload.Length >> 24), (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length,
        .. payload, .. (payload.Length % 2 == 1 ? new byte[1] : [])];
    private sealed class ProductAssertionException(string message) : Exception(message);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static T Require<T>(object? value) where T : class => value as T ?? throw new InvalidOperationException("Public provider absent: " + typeof(T).Name);
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => (root as FrameworkElement)?.FindName(name) as T
        ?? Descendants<T>(root).SingleOrDefault(element => element.Name == name) ?? throw new InvalidOperationException("Public control absent: " + name);
    private static void Invoke(Button button)
    {
        if (!button.IsVisible || !button.IsEnabled) throw new InvalidOperationException("Public button unavailable: " + button.Name);
        Require<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }
    private static Rect Bounds(FrameworkElement element) => new(element.PointToScreen(new Point()), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T self) yield return self;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (T item in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct INPUT { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public KEYBDINPUT Keyboard; }
    private static INPUT Key(ushort key, uint flags = 0) => new() { Type = 1, Keyboard = new() { VirtualKey = key, Flags = flags } };
    private static void Send(INPUT[] inputs) { if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length) throw new System.ComponentModel.Win32Exception(); }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
}
