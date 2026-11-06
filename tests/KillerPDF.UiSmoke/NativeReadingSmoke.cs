using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

internal static class NativeReadingSmoke
{
    internal static int Run(Window owner, string appPath, string folder, bool continuous = false, bool grid = false, bool gridSwitch = false, bool epub = false, bool search = false)
    {
        string stage = "fixture";
        var events = new List<object>();
        var modals = new List<object>();
        var sources = new Dictionary<string, byte[]>();
        Exception? failure = null;
        bool control = false, native = false;
        bool switched = false;
        ImageSource? controlSource = null;
        Rect controlBounds = Rect.Empty;
        string selectedRadioName = "";
        ImageSource? previous = null;
        int stable = 0;
        int viewportStable = 0;
        Rect observedWindow = Rect.Empty, observedViewport = Rect.Empty;
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        if (search) SelectedAssemblyIdentity.HashLoaded(engine, appPath);
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(25) };
        var seen = new HashSet<Window>();
        var deadline = Stopwatch.StartNew();
        DispatcherUnhandledExceptionEventHandler dispatcherFailure = (_, error) =>
        {
            failure ??= error.Exception;
            error.Handled = true;
        };
        if (gridSwitch) Application.Current.DispatcherUnhandledException += dispatcherFailure;
        timer.Tick += (_, _) =>
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(100))
                failure ??= new TimeoutException("Overall native reading deadline expired at " + stage);
            foreach (Window window in Application.Current.Windows.Cast<Window>()
                .Where(window => window != owner && window.IsVisible).ToArray())
            {
                if (seen.Add(window)) modals.Add(new { stage, type = window.GetType().FullName, window.Title,
                    text = string.Join("\n", Descendants<TextBlock>(window).Select(block => block.Text)) });
                // Application.Windows belongs to this isolated host, never another process.
                try { window.Close(); }
                catch (Exception error) { failure ??= error; }
            }
        };
        try
        {
            Type builderType = engine.GetType("KillerPdf.Engine.Authoring.PdfDocumentBuilder", true)!;
            ConstructorInfo constructor = builderType.GetConstructors().Single();
            dynamic builder = constructor.Invoke(constructor.GetParameters().Select(p => p.DefaultValue).ToArray());
            sources.Add(Path.Combine(folder, "blank-control.pdf"), (byte[])builder.AddBlankPage(epub ? 600d : 300d, epub ? 750d : 200d).Build());
            byte[] nativeBytes = epub ? EpubFixture(search) : continuous || grid ? ContinuousFixture() : DjvuFixture();
            if (!epub && (continuous || grid) && (nativeBytes.Length != 100 || Hash(nativeBytes) != "A6112B847B6731E478086F5A14528540B155D8A871AF2F39BE362F7BF682C609"))
                throw new InvalidOperationException("Frozen Continuous fixture hash mismatch.");
            sources.Add(Path.Combine(folder, epub ? "native-letter.epub" : continuous || grid ? "rectangle-300x200-300dpi.djvu" : "native-upright.djvu"), nativeBytes);
            foreach (var source in sources) File.WriteAllBytes(source.Key, source.Value);
            owner.WindowState = WindowState.Normal;
            owner.Width = Math.Min(1000, SystemParameters.WorkArea.Width - 64);
            owner.Height = Math.Min(800, SystemParameters.WorkArea.Height - 64);
            owner.Activate();
            timer.Start();
            var steps = sources.Select(source => (Source: source, Grid: grid, Open: true)).ToList();
            if (gridSwitch)
                steps = [(sources.First(), true, true), (sources.Last(), false, true), (sources.Last(), true, false)];
            foreach (var step in steps)
            {
                var source = step.Source;
                bool isNative = source.Key.EndsWith(".djvu", StringComparison.Ordinal) || source.Key.EndsWith(".epub", StringComparison.Ordinal);
                stage = isNative ? "native-open" : "control-open";
                if (step.Open)
                {
                    MethodInfo open = owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)
                        ?? throw new MissingMethodException("MainWindow.OpenFromExternal");
                    try { open.Invoke(owner, [source.Key]); }
                    catch (TargetInvocationException error) when (error.InnerException is not null)
                    { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
                }
                else previous = controlSource;
                stage = isNative ? "native-readiness" : "control-readiness";
                string radioName = step.Grid ? "ViewGridRadio" : continuous ? "ViewContinuousRadio" : "ViewSingleRadio";
                selectedRadioName = radioName;
                Wait(() => owner.IsLoaded && owner.IsVisible && owner.IsEnabled
                    && Named<RadioButton>(owner, radioName) is { IsVisible: true, IsEnabled: true });
                var radio = Named<RadioButton>(owner, radioName)!;
                var peer = UIElementAutomationPeer.CreatePeerForElement(radio) ?? new RadioButtonAutomationPeer(radio);
                ((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)!).Select();
                if (epub && step.Grid)
                {
                    stage = isNative ? "native-grid-zoom" : "control-grid-zoom";
                    Wait(() => Named<ComboBox>(owner, "ZoomBox") is { IsVisible: true, IsEnabled: true });
                    var zoom = Named<ComboBox>(owner, "ZoomBox")!;
                    zoom.IsDropDownOpen = true;
                    var choice = zoom.Items.OfType<ComboBoxItem>().Single(item => item.Tag as string == "0.5");
                    Wait(() => choice.IsVisible && choice.IsEnabled);
                    zoom.SelectedItem = choice;
                    zoom.IsDropDownOpen = false;
                    Wait(() => ReferenceEquals(zoom.SelectedItem, choice) && !zoom.IsDropDownOpen);
                }
                Image? page = null;
                Rect last = Rect.Empty;
                Rect lastWindow = Rect.Empty, lastViewport = Rect.Empty;
                ImageSource? stableSource = null;
                stable = 0;
                viewportStable = 0;
                observedWindow = Rect.Empty; observedViewport = Rect.Empty;
                Wait(() =>
                {
                    owner.UpdateLayout();
                    if (Named<TextBlock>(owner, "FileNameLabel") is not { IsVisible: true } label
                        || label.Text != Path.GetFileName(source.Key)
                        || Named<ListBox>(owner, "PageList") is not { IsVisible: true, IsEnabled: true } list
                        || list.Items.Count != 1) return false;
                    var viewport = Descendants<ScrollViewer>(owner).SingleOrDefault(v => v.Name == "PagePreviewPanel" && v.IsVisible);
                    if (viewport is null || !viewport.IsEnabled || viewport.Opacity < .999) { stable = 0; return false; }
                    DependencyObject imageRoot = viewport;
                    if (continuous || step.Grid)
                    {
                        Panel? panel = step.Grid ? Named<Panel>(viewport, "PageContentPanel") : Named<Panel>(viewport, "ContinuousPanel");
                        if (radio.IsChecked != true || panel is not { IsVisible: true, IsEnabled: true }
                            || step.Grid && panel.GetType().FullName != "KillerPDF.Controls.SafeWrapPanel")
                        { stable = 0; return false; }
                        imageRoot = panel;
                        if (step.Grid)
                        {
                            if (panel.Children.Count != 1 || panel.Children[0] is not Border { IsVisible: true } tile)
                            { stable = 0; return false; }
                            imageRoot = tile;
                        }
                        var visiblePresenter = Descendants<ScrollContentPresenter>(viewport).SingleOrDefault(element => element.IsVisible);
                        if (visiblePresenter is null || !Opaque(panel)) { viewportStable = 0; return false; }
                        Rect currentWindow = ScreenBounds(owner), currentViewport = ScreenBounds(visiblePresenter);
                        if (currentViewport.Width < 40 || currentViewport.Height < 40 || !currentWindow.Contains(currentViewport))
                        { viewportStable = 0; return false; }
                        viewportStable = observedWindow == currentWindow && observedViewport == currentViewport ? viewportStable + 1 : 0;
                        observedWindow = currentWindow; observedViewport = currentViewport;
                        if (viewportStable < 5) return false;
                        stage = isNative ? "native-render-readiness" : "control-render-readiness";
                    }
                    Image[] pages = Descendants<Image>(imageRoot).Where(i => i.IsVisible && i.IsEnabled
                        && i.Source is BitmapSource && i.ActualWidth > 20 && i.ActualHeight > 20).ToArray();
                    if (pages.Length != 1 || ReferenceEquals(pages[0].Source, previous)) return false;
                    page = pages[0];
                    for (DependencyObject? visual = page; visual is Visual; visual = VisualTreeHelper.GetParent(visual))
                        if (visual is UIElement { Opacity: < .999 }) { stable = 0; return false; }
                    Rect bounds = ScreenBounds(page);
                    ScrollContentPresenter? presenter = Descendants<ScrollContentPresenter>(viewport)
                        .SingleOrDefault(element => element.IsVisible);
                    if (presenter is null) return false;
                    Rect visibleViewport = ScreenBounds(presenter), windowBounds = ScreenBounds(owner);
                    if (!visibleViewport.Contains(bounds) || !windowBounds.Contains(bounds)) return false;
                    Rect work = new(owner.PointToScreen(new Point(SystemParameters.WorkArea.Left - owner.Left,
                        SystemParameters.WorkArea.Top - owner.Top)), new Size(SystemParameters.WorkArea.Width * VisualTreeHelper.GetDpi(owner).DpiScaleX,
                        SystemParameters.WorkArea.Height * VisualTreeHelper.GetDpi(owner).DpiScaleY));
                    if (!work.Contains(bounds) || Math.Abs(bounds.Width / bounds.Height - (epub ? .8 : 1.5)) > .03) return false;
                    stable = bounds == last && lastWindow == windowBounds && lastViewport == visibleViewport
                        && ReferenceEquals(stableSource, page.Source) ? stable + 1 : 0;
                    last = bounds; lastWindow = windowBounds; lastViewport = visibleViewport; stableSource = page.Source;
                    return stable >= 5;
                });
                if (continuous || grid)
                    events.Add(new { stage, diagnostic = "ready-visible-state", stable, viewportStable,
                        mode = radioName, transition = !step.Open,
                        filename = Named<TextBlock>(owner, "FileNameLabel")?.Text,
                        pages = Named<ListBox>(owner, "PageList")?.Items.Count,
                        radio.IsChecked, viewport = observedViewport.ToString(), window = observedWindow.ToString(),
                        oldSource = ReferenceEquals(page!.Source, previous), previousSource = SourceIdentity(previous),
                        currentSource = SourceIdentity(page.Source), opaqueAncestry = Opaque(page) });
                previous = page!.Source;
                if (!isNative) { controlSource = page.Source; controlBounds = last; }
                stage = isNative ? "native-pixels" : "control-pixels";
                string nativePrefix = gridSwitch && isNative && !step.Grid ? "native-single" : "native";
                var ancestry = new List<object>();
                for (DependencyObject? visual = page; visual is Visual; visual = VisualTreeHelper.GetParent(visual))
                    if (visual is FrameworkElement element)
                        ancestry.Add(new { type = element.GetType().FullName, element.Name, element.Opacity,
                            element.IsVisible, element.ActualWidth, element.ActualHeight,
                            bounds = ScreenBounds(element).ToString(), effect = element.Effect?.GetType().FullName });
                var sourceBitmap = (BitmapSource)page.Source;
                var sourceEncoder = new PngBitmapEncoder();
                sourceEncoder.Frames.Add(BitmapFrame.Create(sourceBitmap));
                using (var sourceOutput = File.Create(Path.Combine(folder, isNative ? nativePrefix + "-source-diagnostic.png" : "control-source-diagnostic.png")))
                    sourceEncoder.Save(sourceOutput);
                events.Add(new { stage, diagnostic = "source-and-ancestry", sourceBitmap.PixelWidth,
                    sourceBitmap.PixelHeight, format = sourceBitmap.Format.ToString(), sourceBitmap.DpiX,
                    sourceBitmap.DpiY, tooltip = page.ToolTip?.ToString(), ancestry });
                var capture = Capture(owner, last);
                string path = Path.Combine(folder, isNative ? nativePrefix + "-page.png" : "blank-control-page.png");
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(capture));
                using (var output = File.Create(path)) encoder.Save(output);
                double[] upper = Patch(capture, .125, .125), upperRight = Patch(capture, .875, .125), lower = Patch(capture, .875, .875);
                bool whiteUpper = upper.All(value => value >= 224);
                bool whiteLower = lower.All(value => value >= 224);
                bool blackUpperRight = upperRight.All(value => value <= 32);
                int darkPixels = epub ? DarkPixels(capture) : 0;
                bool detector = epub ? whiteLower && darkPixels > 100 && darkPixels < capture.PixelWidth * capture.PixelHeight / 4
                    : whiteUpper && blackUpperRight && whiteLower;
                events.Add(new { stage, filename = Path.GetFileName(source.Key), pages = 1, bounds = last.ToString(), upper, upperRight, lower, darkPixels, detector });
                if (isNative ? !detector : !whiteUpper || !whiteLower || !upperRight.All(value => value >= 224) || detector)
                    throw new InvalidOperationException(isNative ? "Native white/black screen detector failed." : "Blank PDF absence control failed.");
                if (isNative) native = true; else control = true;
                if (isNative && search)
                {
                    stage = "native-search-capture";
                    const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    object viewer = owner.GetType().GetProperty("ActiveViewer", instance)!.GetValue(owner)!;
                    object snapshot = viewer.GetType().GetMethod("CaptureCurrentSearchSnapshot", instance)!.Invoke(viewer, null)!;
                    object? revision = snapshot.GetType().GetProperty("NativeRevision", instance)!.GetValue(snapshot);
                    if (revision is null) throw new InvalidOperationException("Viewer search did not capture its native revision.");
                    Type service = owner.GetType().Assembly.GetType("KillerPDF.Services.SearchService", true)!;
                    MethodInfo find = service.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                        .Single(method => method.Name == "Search" && method.GetParameters().Length == 5
                            && method.GetParameters()[0].ParameterType == snapshot.GetType());
                    object result = find.Invoke(null, [snapshot, "AA", true, false, CancellationToken.None])!;
                    int hits = (int)result.GetType().GetProperty("TotalHits")!.GetValue(result)!;
                    if (hits != 1) throw new InvalidOperationException("Native viewer search expected one original-text match.");
                    events.Add(new { stage, hits, capturedNative = true });
                    stage = "native-search-bar";
                    owner.GetType().GetMethod("ToggleSearchBar", instance)!.Invoke(owner, null);
                    var queryBox = (TextBox)owner.GetType().GetField("_searchBox", instance)!.GetValue(owner)!;
                    Wait(() => queryBox.IsVisible && queryBox.IsEnabled);
                    queryBox.Text = "AA";
                    Wait(() => ((TextBlock?)owner.GetType().GetField("_searchStatus", instance)!.GetValue(owner))?.Text == "1 / 1");
                    stage = "native-search-highlights";
                    Wait(() => Descendants<System.Windows.Shapes.Rectangle>(owner)
                        .Any(rectangle => rectangle.Tag as string == "SearchHighlight" && rectangle.IsVisible));
                    events.Add(new { stage, query = queryBox.Text, counter = "1 / 1", visibleHighlight = true });
                }
                if (gridSwitch && isNative && step.Grid)
                {
                    stage = "native-grid-geometry";
                    events.Add(new { stage, controlBounds = controlBounds.ToString(), nativeBounds = last.ToString() });
                    if (Math.Abs(last.Width - controlBounds.Width) > 2 || Math.Abs(last.Height - controlBounds.Height) > 2)
                        throw new InvalidOperationException("Native Grid geometry differs from the PDF Grid control.");
                    switched = true;
                }
            }

            void Wait(Func<bool> ready)
            {
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                    if (modals.Count > 0) throw new InvalidOperationException("Unexpected isolated-host modal during " + stage);
                    if (ready()) return;
                    if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Readiness deadline expired at " + stage);
                    Thread.Sleep(25);
                }
            }
        }
        catch (Exception error)
        {
            failure = error;
            if (continuous || grid)
            {
                try
                {
                    owner.UpdateLayout();
                    var viewport = Descendants<ScrollViewer>(owner).SingleOrDefault(v => v.Name == "PagePreviewPanel" && v.IsVisible);
                    Panel? panel = viewport is null ? null : grid ? Named<Panel>(viewport, "PageContentPanel") : Named<Panel>(viewport, "ContinuousPanel");
                    events.Add(new { stage, diagnostic = "failure-visible-state", stable, viewportStable,
                        observedWindow = observedWindow.ToString(), observedViewport = observedViewport.ToString(),
                        filename = Named<TextBlock>(owner, "FileNameLabel")?.Text,
                        filenameVisible = Named<TextBlock>(owner, "FileNameLabel")?.IsVisible,
                        pageCount = Named<ListBox>(owner, "PageList")?.Items.Count,
                        pageListVisible = Named<ListBox>(owner, "PageList")?.IsVisible,
                        radioChecked = Named<RadioButton>(owner, selectedRadioName)?.IsChecked,
                        radioVisible = Named<RadioButton>(owner, selectedRadioName)?.IsVisible,
                        viewportVisible = viewport?.IsVisible, panelVisible = panel?.IsVisible,
                        panelType = panel?.GetType().FullName, panelName = panel?.Name, panelChildren = panel?.Children.Count,
                        viewportBounds = viewport is null ? null : ScreenBounds(viewport).ToString(),
                        windowBounds = ScreenBounds(owner).ToString(),
                        previousSource = SourceIdentity(previous),
                        images = (panel is null ? Enumerable.Empty<Image>() : Descendants<Image>(panel)).Select(image => new
                        {
                            image.IsVisible, image.IsEnabled, image.Opacity, image.ActualWidth, image.ActualHeight,
                            bounds = ScreenBounds(image).ToString(), source = SourceIdentity(image.Source),
                            oldSource = ReferenceEquals(image.Source, previous), opaqueAncestry = Opaque(image)
                        }).ToArray(),
                        viewportOpaqueAncestry = viewport is not null && Opaque(viewport)
                    });
                    if (viewport is not null)
                    {
                        var presenter = Descendants<ScrollContentPresenter>(viewport).SingleOrDefault(element => element.IsVisible);
                        if (presenter is not null)
                        {
                            Rect bounds = Rect.Intersect(ScreenBounds(presenter), ScreenBounds(owner));
                            SaveCapture(owner, bounds, Path.Combine(folder, "failure-viewport.png"));
                            foreach (Image image in panel is null ? Enumerable.Empty<Image>() : Descendants<Image>(panel))
                            {
                                Rect pageBounds = Rect.Intersect(ScreenBounds(image), bounds);
                                if (image.IsVisible && pageBounds.Width >= 40 && pageBounds.Height >= 40)
                                    SaveCapture(owner, pageBounds, Path.Combine(folder, "failure-page.png"));
                            }
                        }
                    }
                }
                catch (Exception diagnosticError) { events.Add(new { stage, diagnostic = "failure-capture-error", error = diagnosticError.ToString() }); }
            }
        }
        finally
        {
            timer.Stop();
            if (gridSwitch) Application.Current.DispatcherUnhandledException -= dispatcherFailure;
            bool preserved = sources.All(source => File.Exists(source.Key) && File.ReadAllBytes(source.Key).AsSpan().SequenceEqual(source.Value));
            bool passed = control && native && preserved && failure is null && modals.Count == 0 && (!gridSwitch || switched);
            File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new
            {
                passed, stage, control, native, preserved, continuous, grid, gridSwitch, switched, epub, machine = Environment.MachineName,
                failureKind = failure is null ? null : stage == "fixture" ? "fixture"
                    : stage == "native-search-capture" ? "native-search-capture"
                    : grid && failure is TimeoutException && modals.Count == 0 ? "grid-readiness-review-required"
                    : continuous && stage == "native-render-readiness" && modals.Count == 0 ? "native-render-readiness-review-required"
                    : modals.Count > 0 ? "unexpected-modal" : stage.EndsWith("readiness", StringComparison.Ordinal)
                    ? "readiness-or-locator" : stage.EndsWith("open", StringComparison.Ordinal) ? "open" : "rendered-pixels",
                error = failure?.ToString(), modals, events,
                binaries = new[] { appPath, engine.Location, typeof(NativeReadingSmoke).Assembly.Location }
                    .Select(path => new { path, sha256 = string.IsNullOrEmpty(path) ? null : Hash(File.ReadAllBytes(path)) }).ToArray(),
                loadedEngine = new { engine.FullName, engine.ManifestModule.ModuleVersionId, engine.Location },
                inputs = sources.Select(source => new { path = source.Key, length = source.Value.Length, sha256 = Hash(source.Value) }).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return control && native && failure is null && modals.Count == 0 && (!gridSwitch || switched)
            && sources.All(source => File.ReadAllBytes(source.Key).AsSpan().SequenceEqual(source.Value)) ? 0 : 1;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static object? SourceIdentity(ImageSource? source) => source is null ? null : new
    {
        type = source.GetType().FullName, identity = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(source),
        width = (source as BitmapSource)?.PixelWidth, height = (source as BitmapSource)?.PixelHeight
    };
    private static bool Opaque(DependencyObject element)
    {
        for (DependencyObject? visual = element; visual is Visual; visual = VisualTreeHelper.GetParent(visual))
            if (visual is UIElement { Opacity: < .999 }) return false;
        return true;
    }
    private static void SaveCapture(Window owner, Rect bounds, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Capture(owner, bounds)));
        using var output = File.Create(path); encoder.Save(output);
    }
    private static T? Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Descendants<T>(root).SingleOrDefault(element => element.Name == name);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static Rect ScreenBounds(FrameworkElement element)
    {
        Point a = element.PointToScreen(new Point()), b = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return new Rect(a, b);
    }
    private static double[] Patch(BitmapSource image, double x, double y)
    {
        int px = (int)(image.PixelWidth * x), py = (int)(image.PixelHeight * y);
        var patch = new Int32Rect(px - 2, py - 2, 5, 5);
        byte[] bytes = new byte[100]; image.CopyPixels(patch, bytes, 20, 0);
        return Enumerable.Range(0, 3).Select(channel => Enumerable.Range(0, 25).Average(i => bytes[i * 4 + channel])).ToArray();
    }
    private static byte[] DjvuFixture() => [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVU"u8,
        .. Chunk("INFO", [0, 3, 0, 2, 26, 0, 0x2c, 1, 22, 1]),
        .. Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 2, 0x23, 0xe1, 0x80, 0x08, 0, 0x80])])];
    private static byte[] EpubFixture(bool search = false)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            Add("EPUB/book.opf", """
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:native-screen</dc:identifier><dc:title>Native letter</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2000-01-01T00:00:00Z</meta></metadata><manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest><spine><itemref idref="chapter"/></spine></package>
                """);
            Add("EPUB/text.xhtml", search ? """
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Native letter</title></head><body><p style="font-size:160px">AA</p></body></html>
                """ : """
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Native letter</title></head><body><p style="font-size:160px">A</p></body></html>
                """);
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="text.xhtml">Letter</a></li></ol></nav></body></html>
                """);
            void Add(string path, string text, CompressionLevel compression = CompressionLevel.Optimal)
            {
                var entry = zip.CreateEntry(path, compression);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(System.Text.Encoding.UTF8.GetBytes(text));
            }
        }
        return output.ToArray();
    }
    private static int DarkPixels(BitmapSource image)
    {
        int stride = checked(image.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * image.PixelHeight)];
        image.CopyPixels(pixels, stride, 0);
        int count = 0;
        for (int y = 8; y < image.PixelHeight - 8; y++)
            for (int x = 8; x < image.PixelWidth - 8; x++)
            {
                int offset = y * stride + x * 4;
                if (pixels[offset] < 64 && pixels[offset + 1] < 64 && pixels[offset + 2] < 64) count++;
            }
        return count;
    }
    private static byte[] ContinuousFixture() => Convert.FromBase64String(
        "QVQmVEZPUk0AAABYREpWVUlORk8AAAAKASwAyBoALAEWAVNtbXIAAAA5TU1SAAEsAMgyBhkA3/////////////////////////////////LctDf////////////////gAgAgAA==");
    private static byte[] Chunk(string id, byte[] payload) => [.. System.Text.Encoding.ASCII.GetBytes(id),
        (byte)(payload.Length >> 24), (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length,
        .. payload, .. (payload.Length % 2 == 1 ? new byte[1] : [])];

    private static BitmapSource Capture(Window owner, Rect bounds)
    {
        int left = (int)Math.Ceiling(bounds.Left), top = (int)Math.Ceiling(bounds.Top);
        int width = (int)Math.Floor(bounds.Right) - left, height = (int)Math.Floor(bounds.Bottom) - top;
        if (width < 40 || height < 40 || width > 4096 || height > 4096) throw new InvalidOperationException("Invalid owned capture size.");
        IntPtr hwnd = new WindowInteropHelper(owner).Handle;
        foreach (var point in new[] { new NativePoint(left + 1, top + 1), new(left + width - 2, top + 1),
            new(left + 1, top + height - 2), new(left + width - 2, top + height - 2), new(left + width / 2, top + height / 2) })
            if (GetAncestor(WindowFromPoint(point), 2) != hwnd) throw new InvalidOperationException("Capture rectangle is obscured by another window.");
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
