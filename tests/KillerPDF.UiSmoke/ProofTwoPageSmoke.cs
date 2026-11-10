using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Rendering;

internal static class ProofTwoPageSmoke
{
    internal static int Run(Window owner, string appPath, string folder, bool continuous = false,
        bool pairedContinuous = false, bool compositorOwned = false, bool nativeEpub = false, bool grid = false,
        bool unavailableProof = false, bool unavailableMonitor = false, bool deviceMonitor = false,
        bool refreshMonitor = false)
    {
        if (Environment.MachineName is not ("SN01" or "SN02"))
            throw new InvalidOperationException("Two-page proof checks require an owned VM.");
        SelectedAssemblyIdentity.HashLoaded(typeof(PdfDocumentBuilder).Assembly, appPath);
        Directory.CreateDirectory(folder);
        var paint = new StringBuilder("1 1 1 rg 0 0 600 750 re f 0 0 0 rg ");
        for (int x = 0; x < 600; x += 20)
            paint.Append(x.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" 0 10 750 re f ");
        byte[] content = Encoding.ASCII.GetBytes(paint.ToString());
        byte[] original = nativeEpub ? EpubFixture()
            : new PdfDocumentBuilder().AddPage(600, 750, content).AddPage(600, 750, content).Build();
        string path = Path.Combine(folder, nativeEpub ? "native-stripes.epub" : "two-page-proof.pdf");
        File.WriteAllBytes(path, original);
        const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object viewer = owner.GetType().GetProperty("ActiveViewer", members)!.GetValue(owner)!;
        string stage = "configure";
        Exception? failure = null;
        var events = new List<object>();
        var modals = new List<string>();
        var deadline = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            foreach (Window modal in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                modals.Add(modal.Title + ": " + string.Join(" ", Descendants<TextBlock>(modal).Select(text => text.Text)));
                modal.Close();
            }
        };
        try
        {
            var target = new PdfDisplayTarget("two-page-proof-output", "Synthetic SDR display")
            {
                ColorState = new(compositorOwned ? PdfDisplayColorMode.AdvancedColorSdr : PdfDisplayColorMode.ConventionalSdr,
                    15, 1, 0, 0, compositorOwned ? 16u : 8u, null, null),
                StandardProfile = new(unavailableMonitor || deviceMonitor ? PdfDisplayProfileStatus.Named : PdfDisplayProfileStatus.NoProfile,
                    0, unavailableMonitor ? "unreadable-monitor.icc" : deviceMonitor ? "synthetic-monitor.icc" : null, null, null)
            };
            var display = new PdfDisplayDiscoverySnapshot(93, PdfDisplayDiscoveryStatus.Available,
                "two-page-proof-desktop", new(0, 0, 1920, 1080), null, null)
                { TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget, [target]) };
            viewer.GetType().GetProperty("DisplayCapture")!.SetValue(viewer,
                (Func<nint, PdfDisplayDiscoverySnapshot>)(_ => display));
            if (unavailableMonitor)
                viewer.GetType().GetProperty("DisplayProfileReader")!.SetValue(viewer,
                    (Func<string, CancellationToken, PdfColorProfile>)((_, _) => throw new IOException("Synthetic monitor profile read failure.")));
            if (deviceMonitor)
                viewer.GetType().GetProperty("DisplayProfileReader")!.SetValue(viewer,
                    (Func<string, CancellationToken, PdfColorProfile>)((_, _) => PdfColorProfile.Load(SdrMonitorSmoke.MonitorBytes())));
            events.Add(new { stage, observedPolicy = PdfDisplayPresentationPolicy.Resolve(display).TransformOwner.ToString(),
                syntheticDisplayObservation = true });
            timer.Start();
            stage = "open";
            owner.GetType().GetMethod("OpenFromExternal")!.Invoke(owner, [path]);
            Wait(() => ((TextBlock)owner.FindName("FileNameLabel")).Text == Path.GetFileName(path));
            if (deviceMonitor && !nativeEpub)
            {
                stage = "monitor-pdf-single-control";
                var single = (RadioButton)owner.FindName("ViewSingleRadio");
                ((ISelectionItemProvider)new RadioButtonAutomationPeer(single).GetPattern(PatternInterface.SelectionItem)!).Select();
                Wait(() => PrimaryReady() && Pages().Length == 1);
                var colors = ProofRefinementSmoke.StripeColors((BitmapSource)Pages()[0].Image.Source, false, true);
                events.Add(new { stage, colors });
                ProofRefinementSmoke.RequireStripeColors(colors);
                CheckScreens(false);
            }
            if (unavailableMonitor)
            {
                stage = "monitor-single-control";
                var single = (RadioButton)owner.FindName("ViewSingleRadio");
                ((ISelectionItemProvider)new RadioButtonAutomationPeer(single).GetPattern(PatternInterface.SelectionItem)!).Select();
                Wait(() => PrimaryReady() && Pages().Length == 1);
                ProofRefinementSmoke.RequireStripeColors(ProofRefinementSmoke.StripeColors((BitmapSource)Pages()[0].Image.Source, false));
                CheckMonitorWarnings();
                CheckScreens(false);
            }
            stage = "open-mode";
            var radio = (RadioButton)owner.FindName(grid ? "ViewGridRadio" : pairedContinuous ? "ViewTwoPageContinuousRadio"
                : continuous ? "ViewContinuousRadio" : "ViewTwoPageRadio");
            Wait(() => radio.IsVisible && radio.IsEnabled);
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(radio).GetPattern(PatternInterface.SelectionItem)!).Select();
            Wait(() => PrimaryReady() && Pages().Length == 2);
            if (nativeEpub)
            {
                object snapshot = viewer.GetType().GetMethod("CaptureCurrentSearchSnapshot", members)!.Invoke(viewer, null)!;
                if (snapshot.GetType().GetProperty("NativeRevision", members)!.GetValue(snapshot) is null)
                    throw new InvalidOperationException("The EPUB did not retain its native reading session.");
                events.Add(new { stage, capturedNative = true });
            }
            stage = "normal-two-page-control";
            CheckPages(false);
            if (unavailableMonitor)
            {
                CheckScreens(false);
                CheckMonitorWarnings();
            }
            var view = (PdfDocumentProofView)viewer.GetType().GetProperty("DocumentProofView")!.GetValue(viewer)!;
            view.Document.Proof = new PdfDocumentProofSettings(enabled: true,
                printerProfile: unavailableProof ? null : PdfColorProfile.Load(NativeReadingSmoke.ProofPrinterFixture()),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulatePaper = true, SimulateBlack = true });
            stage = "proof-two-page";
            viewer.GetType().GetMethod("RenderActiveSessionExt", members)!.Invoke(viewer, null);
            Wait(() => PrimaryReady() && Pages().Length == 2);
            CheckPages(!unavailableProof);
            CheckScreens(!unavailableProof);
            if (unavailableMonitor) CheckMonitorWarnings();
            if (unavailableProof)
            {
                if (!view.Document.Proof.Enabled || view.Document.Proof.PrinterProfile is not null)
                    throw new InvalidOperationException("Unavailable proof changed the requested document settings.");
                CheckWarnings(true);
            }
            stage = "proof-off-two-page";
            view.Document.Proof = new PdfDocumentProofSettings();
            viewer.GetType().GetMethod("RenderActiveSessionExt", members)!.Invoke(viewer, null);
            Wait(() => PrimaryReady() && Pages().Length == 2);
            CheckPages(false);
            CheckScreens(false);
            if (unavailableMonitor) CheckMonitorWarnings();
            if (unavailableProof) CheckWarnings(false);
            if (refreshMonitor)
            {
                RefreshMonitor(false);
                RefreshMonitor(true);

                void RefreshMonitor(bool available)
                {
                    stage = available ? "monitor-refresh-device" : "monitor-refresh-unavailable";
                    var previous = Pages().Select(page => page.Image.Source).ToArray();
                    var viewport = (ScrollViewer)((FrameworkElement)viewer).FindName("PagePreviewPanel");
                    double beforeHorizontal = viewport.HorizontalOffset, beforeVertical = viewport.VerticalOffset;
                    deviceMonitor = available;
                    viewer.GetType().GetProperty("DisplayProfileReader")!.SetValue(viewer,
                        (Func<string, CancellationToken, PdfColorProfile>)((_, _) => available
                            ? PdfColorProfile.Load(SdrMonitorSmoke.MonitorBytes())
                            : throw new IOException("Synthetic monitor profile read failure after notification.")));
                    display = display with { Generation = display.Generation + 1 };
                    SdrMonitorSmoke.NotifyDisplayChanged(owner);
                    Wait(() => PrimaryReady() && Pages().Length == 2
                        && Pages().All(page => !previous.Any(source => ReferenceEquals(source, page.Image.Source))));
                    if (continuous && (Math.Abs(viewport.HorizontalOffset - beforeHorizontal) > 1
                        || Math.Abs(viewport.VerticalOffset - beforeVertical) > 1))
                        throw new InvalidOperationException("Monitor refresh changed the Continuous scroll position.");
                    if (radio.IsChecked != true || !ReferenceEquals(view,
                        viewer.GetType().GetProperty("DocumentProofView")!.GetValue(viewer)))
                        throw new InvalidOperationException("Monitor refresh changed the view mode or document proof owner.");
                    CheckPages(false);
                    CheckScreens(false);
                    if (!available) CheckMonitorWarnings();
                    else if (Pages().Any(page => page.Image.ToolTip is not null))
                        throw new InvalidOperationException("Recovered monitor conversion retains a stale diagnostic.");
                    events.Add(new { stage, display.Generation, available, allSourcesReplaced = true,
                        beforeHorizontal, beforeVertical, viewModeAndProofOwnerPreserved = true });
                }
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            timer.Stop();
            bool preserved = File.ReadAllBytes(path).SequenceEqual(original);
            File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new
            {
                passed = failure is null && modals.Count == 0 && preserved, stage, preserved,
                mode = grid ? "Grid" : pairedContinuous ? "TwoPageContinuous" : continuous ? "Continuous" : "TwoPage",
                format = nativeEpub ? "EPUB" : "PDF", deviceMonitor, refreshMonitor,
                error = failure?.ToString(), modals, events,
                appHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)))
            }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        }
        return failure is null && modals.Count == 0 && File.ReadAllBytes(path).SequenceEqual(original) ? 0 : 1;

        bool PrimaryReady() => (Task)viewer.GetType().GetProperty("PrimaryRenderTask", members)!.GetValue(viewer)!
            is { IsCompletedSuccessfully: true };
        (int Page, Image Image)[] Pages() => Descendants<Image>((DependencyObject)viewer)
            .Where(image => image.IsVisible && image.Source is BitmapSource { PixelWidth: > 100, PixelHeight: > 100 }
                && image.Parent is Grid grid && grid.Children.Count > 1 && ReferenceEquals(grid.Children[0], image)
                && grid.Children[1] is Canvas { Tag: int })
            .Select(image => ((int)((Canvas)((Grid)image.Parent).Children[1]).Tag, image)).OrderBy(item => item.Item1).ToArray();
        void CheckPages(bool proof)
        {
            var pages = Pages();
            var colors = pages.Select(item => new { item.Page,
                selected = ProofRefinementSmoke.StripeColors((BitmapSource)item.Image.Source, proof, deviceMonitor),
                other = ProofRefinementSmoke.StripeColors((BitmapSource)item.Image.Source, !proof, deviceMonitor) }).ToArray();
            events.Add(new { stage, colors });
            if (!pages.Select(item => item.Page).SequenceEqual(new[] { 0, 1 }))
                throw new InvalidOperationException("The facing pages are not both present.");
            foreach (var page in colors)
            {
                ProofRefinementSmoke.RequireStripeColors(page.selected);
                if (page.other.Paper != 0 || page.other.Ink != 0)
                    throw new InvalidOperationException("A facing page retained colors from the other proof state.");
            }
        }
        void CheckWarnings(bool expected)
        {
            var warnings = Pages().Select(page => new { page.Page, warning = page.Image.ToolTip?.ToString() }).ToArray();
            events.Add(new { stage, warnings, requestedProofRetained = expected });
            // Since e7435d5 a page whose requested printer profile is unavailable names the profile, not the proof.
            string notice = (string)owner.FindResource("Str_CM_ProfileUnavailable");
            if (warnings.Any(page => (page.warning?.Contains(notice, StringComparison.Ordinal) == true) != expected))
                throw new InvalidOperationException("A native page has an incorrect unavailable-proof diagnostic.");
        }
        void CheckMonitorWarnings()
        {
            var warnings = Pages().Select(page => new { page.Page, warning = page.Image.ToolTip?.ToString() }).ToArray();
            events.Add(new { stage, monitorWarnings = warnings });
            // Since ca651d4 the page shows the localized display fallback notice instead of the operation code.
            string notice = (string)owner.FindResource("Str_CM_DisplayFallback");
            if (warnings.Any(page => page.warning?.Contains(notice, StringComparison.Ordinal) != true))
                throw new InvalidOperationException("A native page lost its unavailable-monitor-profile diagnostic.");
        }
        void CheckScreens(bool proof)
        {
            var viewport = (ScrollViewer)((FrameworkElement)viewer).FindName("PagePreviewPanel");
            var clip = Descendants<ScrollContentPresenter>(viewport).Single(element => element.IsVisible);
            foreach (var page in Pages())
            {
                if (continuous)
                {
                    page.Image.BringIntoView();
                    owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                }
                Rect bounds = Rect.Intersect(Bounds(page.Image), Bounds(clip));
                Wait(() => ProofRefinementSmoke.StripeColors(NativeReadingSmoke.Capture(owner, bounds), proof, deviceMonitor) is var colors
                    && colors.Paper > colors.Pixels / 5 && colors.Ink > colors.Pixels / 5);
                if (deviceMonitor)
                {
                    int pendingSamples = 0;
                    var readiness = Stopwatch.StartNew();
                    Wait(() =>
                    {
                        bool ready = CheckDevicePatches(page, NativeReadingSmoke.Capture(owner, bounds), bounds, proof, assert: false);
                        if (!ready) pendingSamples++;
                        return ready;
                    });
                    events.Add(new { stage, page.Page, exactPatchReady = true, pendingSamples,
                        readinessMilliseconds = readiness.ElapsedMilliseconds });
                }
                var capture = NativeReadingSmoke.Capture(owner, bounds);
                var selected = ProofRefinementSmoke.StripeColors(capture, proof, deviceMonitor);
                var other = ProofRefinementSmoke.StripeColors(capture, !proof, deviceMonitor);
                var source = (BitmapSource)page.Image.Source;
                events.Add(new { stage, page.Page, screen = true, selected, other,
                    imageBounds = Bounds(page.Image).ToString(), capturedBounds = bounds.ToString(),
                    page.Image.ActualWidth, page.Image.ActualHeight, source.Width, source.Height,
                    source.PixelWidth, source.PixelHeight, source.DpiX, source.DpiY });
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(capture));
                using var output = File.Create(Path.Combine(folder, stage + "-page-" + page.Page + ".png"));
                encoder.Save(output);
                if (other.Paper != 0 || (other.Ink != 0 && (!deviceMonitor || proof)))
                    throw new InvalidOperationException("A visible facing page retains the other proof state's colors.");
                if (deviceMonitor) CheckDevicePatches(page, capture, bounds, proof);
            }
        }
        bool CheckDevicePatches((int Page, Image Image) page, BitmapSource capture, Rect capturedBounds, bool proof,
            bool assert = true)
        {
            Rect full = Bounds(page.Image);
            foreach (var patch in new[] { (PageX: nativeEpub ? 262 : 265, Paper: false),
                (PageX: nativeEpub ? 274 : 275, Paper: true) })
            {
                // Centers of adjacent solid stripes in each independent page fixture.
                int x = (int)Math.Round(full.Left + full.Width * patch.PageX / (nativeEpub ? 800.0 : 600.0))
                    - (int)Math.Ceiling(capturedBounds.Left) - 1;
                int y = (int)Math.Round(full.Top + full.Height * (nativeEpub ? 456 / 1000.0 : 375 / 750.0))
                    - (int)Math.Ceiling(capturedBounds.Top) - 1;
                if (x < 0 || y < 0 || x + 3 > capture.PixelWidth || y + 3 > capture.PixelHeight)
                    throw new InvalidOperationException("The independent device-color patch is outside the visible page.");
                var colors = ProofRefinementSmoke.StripeColors(new CroppedBitmap(capture, new Int32Rect(x, y, 3, 3)),
                    proof, deviceMonitor: true);
                if (assert) events.Add(new { stage, page.Page, flatDevicePatch = true, patch.Paper, x, y, colors });
                if (patch.Paper ? colors.Paper != 9 || colors.Ink != 0 : colors.Ink != 9 || colors.Paper != 0)
                {
                    if (assert) throw new InvalidOperationException("A solid device-color patch differs from its independent endpoint.");
                    return false;
                }
            }
            return true;
        }
        void Wait(Func<bool> ready)
        {
            var wait = Stopwatch.StartNew();
            while (true)
            {
                owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                if (modals.Count > 0) throw new InvalidOperationException("Unexpected modal at " + stage);
                if (ready()) return;
                if (wait.Elapsed > TimeSpan.FromSeconds(30) || deadline.Elapsed > TimeSpan.FromSeconds(90))
                    throw new TimeoutException("Two-page proof deadline expired at " + stage);
                Thread.Sleep(25);
            }
        }
    }
    private static byte[] EpubFixture()
    {
        const int width = 720, height = 880;
        byte[] pixels = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            byte shade = x % 24 < 12 ? (byte)0 : (byte)255;
            int offset = (y * width + x) * 3;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = shade;
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Rgb24, null, pixels, width * 3)));
        using var png = new MemoryStream();
        encoder.Save(png);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            Add("EPUB/book.opf", """
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:proof-stripes</dc:identifier><dc:title>Native proof stripes</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2000-01-01T00:00:00Z</meta></metadata><manifest><item id="one" href="one.xhtml" media-type="application/xhtml+xml"/><item id="two" href="two.xhtml" media-type="application/xhtml+xml"/><item id="stripes" href="stripes.png" media-type="image/png"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest><spine><itemref idref="one"/><itemref idref="two"/></spine></package>
                """);
            foreach (string chapter in new[] { "one", "two" })
                Add("EPUB/" + chapter + ".xhtml", """
                    <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Stripes</title></head><body><p><img src="stripes.png" alt=""/></p></body></html>
                    """);
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="one.xhtml">One</a></li><li><a href="two.xhtml">Two</a></li></ol></nav></body></html>
                """);
            Write("EPUB/stripes.png", png.ToArray());
            void Add(string entry, string text) => Write(entry, Encoding.UTF8.GetBytes(text));
            void Write(string path, byte[] bytes)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        }
        return output.ToArray();
    }

    private static Rect Bounds(FrameworkElement element) => new(element.PointToScreen(default),
        element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
}
