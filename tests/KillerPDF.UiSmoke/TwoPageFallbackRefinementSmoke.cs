using System.Diagnostics;
using System.Globalization;
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
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerPdf.Engine.Rendering;

internal static class TwoPageFallbackRefinementSmoke
{
    private const int PairCount = 64;
    private const int WhiteMinimum = 180, BlackMaximum = 75;
    private const double RequiredFraction = .95;

    public static int Run(Window owner, string appPath, string folder)
    {
        if (Environment.MachineName is not ("SN01" or "SN02"))
            throw new InvalidOperationException("Refinement acceptance requires an exclusively owned VM.");
        Directory.CreateDirectory(folder);
        byte[] original = Fixture();
        if (Hash(original) != "D5349CF41C345372CD2B28383DFACB1C78E97DA26182C2C56CE7E35C17299BFD")
            throw new InvalidOperationException("Two-page fixture differs from its frozen bytes.");
        string path = Path.Combine(folder, "two-page-vertical-stripes.pdf");
        File.WriteAllBytes(path, original);
        string stage = "configure-boundaries";
        Exception? failure = null;
        var checks = new List<object>();
        var modals = new List<string>();
        var presenters = new List<(nint Handle, IPdfFramePresenter Presenter)>();
        nint owned = new WindowInteropHelper(owner).Handle;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            foreach (Window modal in Application.Current.Windows.Cast<Window>()
                .Where(window => window != owner && window.IsVisible).ToArray())
            { modals.Add(modal.GetType().FullName ?? modal.GetType().Name); modal.Close(); }
        };
        DispatcherUnhandledExceptionEventHandler onError = (_, args) => { failure ??= args.Exception; args.Handled = true; };
        Application.Current.DispatcherUnhandledException += onError;
        timer.Start();
        try
        {
            var viewer = (DependencyObject)(owner.FindName("Viewer") ?? throw new InvalidOperationException("Primary viewer missing."));
            var target = new PdfDisplayTarget("two-page-refinement-output", "Synthetic observed display")
            {
                ColorState = new(PdfDisplayColorMode.ConventionalSdr, 15, 1, 0, 0, 8, null, null),
                StandardProfile = new(PdfDisplayProfileStatus.NoProfile, 0, null, null, null)
            };
            var display = new PdfDisplayDiscoverySnapshot(81, PdfDisplayDiscoveryStatus.Available,
                "refinement-desktop", new(0, 0, 1920, 1080), null, null)
            { TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget, [target]) };
            viewer.GetType().GetProperty("DisplayCapture", BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(viewer, (Func<nint, PdfDisplayDiscoverySnapshot>)(_ => display));
            Type presenterType = owner.GetType().Assembly.GetType("KillerPDF.Presentation.WindowsPdfFramePresenter", true)!;
            viewer.GetType().GetProperty("FramePresenterFactory", BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(viewer, (Func<nint, IPdfFramePresenter>)(handle =>
                {
                    var presenter = (IPdfFramePresenter)Activator.CreateInstance(presenterType, handle, 512L << 20)!;
                    presenters.Add((handle, presenter));
                    return presenter;
                }));
            stage = "public-open";
            owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Instance | BindingFlags.Public,
                null, [typeof(string)], null)!.Invoke(owner, [path]);
            Wait(() => owner.FindName("FileNameLabel") is TextBlock label && label.Text == Path.GetFileName(path));
            var single = (RadioButton)owner.FindName("ViewSingleRadio");
            var twoPage = (RadioButton)owner.FindName("ViewTwoPageRadio");
            Wait(() => single.IsVisible && single.IsEnabled);
            SelectMode(twoPage);
            var invert = (Button)owner.FindName("DocInvertBtn");
            var zoom = (ComboBox)owner.FindName("ZoomBox");
            var viewport = Descendants(viewer).OfType<ScrollViewer>().Single(scroll => scroll.Name == "PagePreviewPanel");
            Wait(() => twoPage.IsChecked == true && invert.IsVisible && invert.IsEnabled && zoom.IsVisible && zoom.IsEnabled);
            SetInversion(true);
            SetGeometry();
            stage = "fresh-two-page-controls";
            if (presenters.Count != 0) throw new InvalidOperationException("Fresh compatibility control already attached a native presenter.");
            CheckSpread(true);

            stage = "native-attachment";
            SelectMode(single);
            SetInversion(false);
            display = display with { Generation = 82, TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget,
                [target with { ColorState = new(PdfDisplayColorMode.AdvancedColorSdr, 15, 1, 0, 0, 16, null, null) }]) };
            SendMessage(owned, 0x7E, 0, 0);
            Wait(() => presenters.Any(item => item.Presenter.Status is
                { Mode: PdfPresentationMode.LinearFloat, LastAcceptedStamp: { PageIndex: 0, RegionId: 1 } }));
            SetGeometry();
            Image image = BaseImage();
            var native = CaptureProbe(image, "native-initial");
            var nativeSamples = Detect(native.Bitmap, native.Points, false);
            int nativeDarkSamples = nativeSamples.Pixels.Count(sample => sample.Take(3).All(channel => channel < WhiteMinimum));
            if (nativeDarkSamples == 0)
                throw new InvalidOperationException("Native stripe content is absent from the owned screen capture.");
            // The top owned paper point is outside the stripe band and changes from inverted black to white.
            Point paper = image.PointFromScreen(new Point(native.Left + native.Bitmap.PixelWidth / 2.0,
                viewport.PointToScreen(new Point(0, 25)).Y));
            double pdfY = 2400 * (1 - paper.Y / image.ActualHeight);
            if (paper.Y <= 0 || paper.Y >= image.ActualHeight || pdfY is >= 1800 and <= 2100)
                throw new InvalidOperationException("Native white-paper control is not outside the stripe band; promote geometry setup.");
            Point whiteScreen = image.PointToScreen(paper);
            var white = Capture((int)Math.Round(whiteScreen.X) - 2, (int)Math.Round(whiteScreen.Y) - 2, 5, 5, "native-white-paper");
            byte[] pixel = Pixel(white, 2, 2);
            if (pixel.Take(3).Any(channel => channel < WhiteMinimum))
                throw new InvalidOperationException("Native initial content is not visible white paper: " + string.Join(",", pixel));
            checks.Add(new { stage, pdfY, pixel, nativeDarkSamples,
                statuses = presenters.Select(item => item.Presenter.Status).ToArray(), native.Geometry });
            nint retainedHandle = presenters.First(item => item.Presenter.Status is
                { Mode: PdfPresentationMode.LinearFloat, LastAcceptedStamp: { PageIndex: 0, RegionId: 1 } }).Handle;

            stage = "retained-host-two-page-inverted-refinement";
            SelectMode(twoPage);
            SetInversion(true);
            SetGeometry();
            if (!IsWindow(retainedHandle))
                throw new InvalidOperationException("The attached native host was destroyed before retained-host acceptance.");
            checks.Add(new { stage, retainedHandle = retainedHandle.ToInt64(),
                statuses = presenters.Select(item => item.Presenter.Status).ToArray() });
            CheckSpread(false);
            stage = "return-single-native-recovery";
            var beforeRecovery = presenters.Select(item => item.Presenter.Status.LastAcceptedStamp).ToArray();
            SelectMode(single);
            SetInversion(false);
            SetGeometry();
            Wait(() => presenters.Any(item => item.Presenter.Status is
                { Mode: PdfPresentationMode.LinearFloat, LastAcceptedStamp: { PageIndex: 0, RegionId: 1 } stamp }
                && !beforeRecovery.Contains(stamp)));
            image = BaseImage();
            var recovered = CaptureProbe(image, "recovered-native");
            var recoveredSamples = Detect(recovered.Bitmap, recovered.Points, false);
            if (!recoveredSamples.Pixels.Any(pixel => pixel.Take(3).All(channel => channel < WhiteMinimum)))
                throw new InvalidOperationException("Recovered native stripe content is absent.");
            Point recoveredPaper = image.PointFromScreen(new Point(recovered.Left + recovered.Bitmap.PixelWidth / 2.0,
                viewport.PointToScreen(new Point(0, 25)).Y));
            double recoveredPdfY = 2400 * (1 - recoveredPaper.Y / image.ActualHeight);
            if (recoveredPaper.Y <= 0 || recoveredPaper.Y >= image.ActualHeight || recoveredPdfY is >= 1800 and <= 2100)
                throw new InvalidOperationException("Recovered white-paper control has invalid geometry.");
            Point recoveredWhiteScreen = image.PointToScreen(recoveredPaper);
            var recoveredWhite = Capture((int)Math.Round(recoveredWhiteScreen.X) - 2,
                (int)Math.Round(recoveredWhiteScreen.Y) - 2, 5, 5, "recovered-white-paper");
            byte[] recoveredPixel = Pixel(recoveredWhite, 2, 2);
            if (recoveredPixel.Take(3).Any(channel => channel < WhiteMinimum))
                throw new InvalidOperationException("Recovered native paper is not visibly white.");
            checks.Add(new { stage, recoveredPdfY, recoveredPixel, recoveredSamples });
            checks.Add(new { stage, statuses = presenters.Select(item => item.Presenter.Status).ToArray(), recovered.Geometry });
        }
        catch (Exception error) { failure ??= error; }
        finally
        {
            Application.Current.DispatcherUnhandledException -= onError;
            try { owner.Close(); var watch = Stopwatch.StartNew(); while (owner.IsVisible && watch.ElapsedMilliseconds < 3000) Pump(); }
            catch (Exception error) { failure ??= error; }
            timer.Stop();
            bool preserved = File.ReadAllBytes(path).AsSpan().SequenceEqual(original);
            bool handlesClosed = presenters.All(item => !IsWindow(item.Handle));
            if (!preserved || owner.IsVisible || !handlesClosed || modals.Count != 0)
                failure ??= new InvalidOperationException("Original, owned window, native handle or modal cleanup failed.");
            File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new
            {
                passed = failure is null, stage, error = failure?.ToString(), checks, preserved,
                windowClosed = !owner.IsVisible, handlesClosed, modals, machine = Environment.MachineName,
                fixtureSha256 = Hash(original), oracle = new { PairCount, WhiteMinimum, BlackMaximum, RequiredFraction },
                appSha256 = Hash(File.ReadAllBytes(appPath)), driverSha256 = Hash(File.ReadAllBytes(typeof(TwoPageFallbackRefinementSmoke).Assembly.Location)),
                engine = SelectedAssemblyIdentity.HashLoaded(typeof(PdfRenderedFrame).Assembly, appPath),
                captures = Directory.GetFiles(folder, "*.png").Select(file => new { name = Path.GetFileName(file), sha256 = Hash(File.ReadAllBytes(file)) }).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failure is null ? 0 : 1;

        void Wait(Func<bool> ready)
        {
            var watch = Stopwatch.StartNew();
            while (!ready())
            {
                if (failure is not null) throw failure;
                if (watch.ElapsedMilliseconds > 20000) throw new TimeoutException("Readiness at " + stage);
                Pump(); Thread.Sleep(10);
            }
        }
        void SelectMode(RadioButton radio)
        {
            Wait(() => radio.IsVisible && radio.IsEnabled);
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(radio).GetPattern(PatternInterface.SelectionItem)).Select();
            Wait(() => radio.IsChecked == true);
            Settle();
        }
        void CheckSpread(bool requireNegative)
        {
            var viewport = Descendants((DependencyObject)owner.FindName("Viewer")).OfType<ScrollViewer>()
                .Single(item => item.Name == "PagePreviewPanel");
            Wait(() => Images().Count() == 2);
            for (int page = 0; page < 2; page++)
            {
                Image image = Images().ElementAt(page);
                Point middle = image.PointToScreen(new Point(image.ActualWidth / 2, 0));
                Point scrollOrigin = viewport.PointToScreen(default);
                double delta = (middle.X - scrollOrigin.X) / VisualTreeHelper.GetDpi(viewport).DpiScaleX
                    - viewport.ViewportWidth / 2;
                viewport.ScrollToHorizontalOffset(Math.Clamp(viewport.HorizontalOffset + delta, 0, viewport.ScrollableWidth));
                viewport.ScrollToVerticalOffset(600);
                Settle();
                SetGeometry();
                image = Images().ElementAt(page);
                string name = (requireNegative ? "fresh" : "retained") + "-page-" + page;
                var probe = CaptureProbe(image, name);
                var detected = Detect(probe.Bitmap, probe.Points, true);
                var clock = Stopwatch.StartNew();
                while (!detected.Passed && clock.ElapsedMilliseconds < 20000)
                {
                    Pump(); Thread.Sleep(100);
                    probe = probe with { Bitmap = Capture(probe.Left, probe.Top,
                        probe.Bitmap.PixelWidth, probe.Bitmap.PixelHeight, name) };
                    detected = Detect(probe.Bitmap, probe.Points, true);
                }
                checks.Add(new { stage, page, detected, probe.Geometry, image.Opacity,
                    parentOpacity = ((Grid)image.Parent).Opacity, viewportOpacity = viewport.Opacity,
                    details = ((Grid)image.Parent).Children.OfType<Image>().Where(child => child != image && child.Source is BitmapSource)
                        .Select(child => new { child.Margin, child.Width, child.Height, child.ActualWidth, child.ActualHeight,
                            child.UseLayoutRounding, child.SnapsToDevicePixels,
                            expectedStart = image.PointToScreen(new Point(child.Margin.Left, child.Margin.Top)),
                            actualStart = child.PointToScreen(default),
                            actualEnd = child.PointToScreen(new Point(child.ActualWidth, child.ActualHeight)) }).ToArray() });
                if (!detected.Passed)
                    throw new InvalidOperationException("Facing page failed frozen refinement oracle: " + page);
                if (!requireNegative) continue;
                BitmapSource source = (BitmapSource)image.Source;
                var visual = new DrawingVisual();
                RenderOptions.SetBitmapScalingMode(visual, RenderOptions.GetBitmapScalingMode(image));
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawRectangle(Brushes.Magenta, null,
                        new Rect(0, 0, probe.Bitmap.PixelWidth, probe.Bitmap.PixelHeight));
                    drawing.DrawImage(source, new Rect(probe.Geometry.Left - probe.Left, probe.Geometry.Top - probe.Top,
                        probe.Geometry.Width, probe.Geometry.Height));
                }
                var baseOnly = new RenderTargetBitmap(probe.Bitmap.PixelWidth, probe.Bitmap.PixelHeight,
                    96, 96, PixelFormats.Pbgra32);
                baseOnly.Render(visual);
                Save(baseOnly, name + "-base-only-negative");
                var negative = Detect(baseOnly, probe.Points, true);
                checks.Add(new { stage, page, negative, sourceHash = PixelHash(source),
                    scaling = RenderOptions.GetBitmapScalingMode(image).ToString() });
                if (negative.Passed)
                    throw new InvalidOperationException("Facing page base-only control passes; promote detector design: " + page);
            }
        }
        void SetInversion(bool on)
        {
            var button = (Button)owner.FindName("DocInvertBtn");
            Wait(() => button.IsVisible && button.IsEnabled);
            if ((button.Tag?.ToString() == "on") != on)
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            Wait(() => (button.Tag?.ToString() == "on") == on);
            Settle();
        }
        void SetGeometry()
        {
            var combo = (ComboBox)owner.FindName("ZoomBox");
            Wait(() => combo.IsVisible && combo.IsEnabled);
            combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "2.0");
            Settle();
            var scroll = Descendants((DependencyObject)owner.FindName("Viewer")).OfType<ScrollViewer>()
                .Single(item => item.Name == "PagePreviewPanel");
            scroll.ScrollToVerticalOffset(600);
            Wait(() => Math.Abs(scroll.VerticalOffset - 600) <= 1);
            Settle();
            bool spread = ((RadioButton)owner.FindName("ViewTwoPageRadio")).IsChecked == true;
            Wait(() => Images().Count() == (spread ? 2 : 1));
            foreach (Image current in Images())
            {
            Point near = current.PointToScreen(default), far = current.PointToScreen(new Point(current.ActualWidth, current.ActualHeight));
            var dpi = VisualTreeHelper.GetDpi(current);
            checks.Add(new { stage, geometry = "two-hundred-percent", current.ActualWidth, current.ActualHeight,
                drawnWidth = (far.X - near.X) / dpi.DpiScaleX, drawnHeight = (far.Y - near.Y) / dpi.DpiScaleY,
                dpi.DpiScaleX, dpi.DpiScaleY, scroll.HorizontalOffset, scroll.VerticalOffset,
                selectedZoom = (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), combo.Text,
                contentBounds = VisualTreeHelper.GetContentBounds(current),
                sourceWidth = current.Source.Width, sourceHeight = current.Source.Height,
                sourceDpiX = ((BitmapSource)current.Source).DpiX, sourcePixels = ((BitmapSource)current.Source).PixelWidth,
                current.Stretch, current.HorizontalAlignment, current.VerticalAlignment });
            if (!double.IsFinite(far.X - near.X) || !double.IsFinite(far.Y - near.Y)
                || !double.IsFinite(scroll.VerticalOffset)
                || Math.Abs((far.X - near.X) / dpi.DpiScaleX - 3200) > 4
                || Math.Abs((far.Y - near.Y) / dpi.DpiScaleY - 6400) > 4
                || Math.Abs(scroll.VerticalOffset - 600) > 1)
                throw new InvalidOperationException("Actual physical page geometry is not true 200 percent at offset 600.");
            }
        }
        void Settle()
        {
            string? previous = null;
            var stable = Stopwatch.StartNew();
            Wait(() =>
            {
                string now = string.Join(";", Images().Select(image =>
                {
                    Point p = image.PointToScreen(default), q = image.PointToScreen(new Point(image.ActualWidth, image.ActualHeight));
                    return FormattableString.Invariant($"{p.X:F2},{p.Y:F2},{q.X:F2},{q.Y:F2},{image.Source?.GetHashCode()},{image.IsVisible}");
                }));
                if (now != previous) { previous = now; stable.Restart(); }
                return stable.ElapsedMilliseconds >= 1800;
            });
        }
        IEnumerable<Image> Images()
        {
            var roots = new List<DependencyObject> { (DependencyObject)owner.FindName("Viewer") };
            roots.AddRange(PresentationSource.CurrentSources.OfType<HwndSource>().Where(source => IsChild(owned, source.Handle))
                .Select(source => source.RootVisual).OfType<DependencyObject>());
            return roots.SelectMany(Descendants).OfType<Image>().Distinct().Where(image => image.Source is BitmapSource
                && image.ActualWidth > 500 && image.ActualHeight > 1000 && image.Parent is Grid grid
                && grid.IsVisible && grid.Children.Count > 1 && ReferenceEquals(grid.Children[0], image))
                .OrderBy(image => image.PointToScreen(default).X).ToArray();
        }
        Image BaseImage()
        {
            Wait(() => Images().Count() == 1);
            return Images().Single();
        }
        Probe CaptureProbe(Image image, string name)
        {
            var scroll = Descendants((DependencyObject)owner.FindName("Viewer")).OfType<ScrollViewer>()
                .Single(item => item.Name == "PagePreviewPanel");
            Rect clip = new(scroll.PointToScreen(default), scroll.PointToScreen(new Point(scroll.ViewportWidth, scroll.ViewportHeight)));
            Point origin = ExactScreenPoint(image, default), far = ExactScreenPoint(image, new Point(image.ActualWidth, image.ActualHeight));
            var geometry = new Rect(origin, far);
            Rect visible = Rect.Intersect(clip, geometry);
            if (visible.IsEmpty || visible.Width < 400 || visible.Height < 7)
                throw new InvalidOperationException("Page has insufficient visible space for the frozen oracle.");
            Point center = image.PointFromScreen(new Point(visible.Left + visible.Width / 2, visible.Top + visible.Height / 2));
            double paperY = 2400 * (1 - center.Y / image.ActualHeight);
            if (paperY < 1805 || paperY > 2095) throw new InvalidOperationException("Viewport center is outside the stripe band; promote geometry setup.");
            int start = (int)Math.Floor(center.X / image.ActualWidth * 1200 / 2) * 2 - PairCount;
            if (start < 0 || start + PairCount * 2 >= 1200) throw new InvalidOperationException("Insufficient consecutive stripe pairs.");
            var screenPoints = Enumerable.Range(0, PairCount * 2).Select(index => ExactScreenPoint(image,
                new Point(image.ActualWidth * (start + index + .5) / 1200, center.Y))).ToArray();
            int left = (int)Math.Floor(screenPoints.First().X) - 3, top = (int)Math.Round(screenPoints.First().Y) - 3;
            int width = (int)Math.Ceiling(screenPoints.Last().X) - left + 4;
            if (!clip.Contains(new Rect(left, top, width, 7))) throw new InvalidOperationException("Stripe ROI leaves the owned viewport.");
            var bitmap = Capture(left, top, width, 7, name);
            var points = screenPoints.Select(point => new Point(Math.Floor(point.X) - left, Math.Floor(point.Y) - top)).ToArray();
            checks.Add(new { stage, name, startPaperX = start, paperY, left, top, width,
                scroll.HorizontalOffset, scroll.VerticalOffset, points });
            return new(bitmap, points, geometry, left, top);
        }
        BitmapSource Capture(int left, int top, int width, int height, string name)
        {
            if (!SetForegroundWindow(owned) && GetForegroundWindow() != owned)
                throw new InvalidOperationException("Could not foreground the owned editor.");
            Wait(() => GetForegroundWindow() == owned);
            foreach (var point in new[] { new PointI(left, top), new(left + width - 1, top),
                new(left, top + height - 1), new(left + width - 1, top + height - 1), new(left + width / 2, top + height / 2) })
                if (GetAncestor(WindowFromPoint(point), 2) != owned) throw new InvalidOperationException("Capture ROI is obscured.");
            nint screen = GetDC(0), memory = 0, bitmap = 0, previous = 0;
            try
            {
                memory = CreateCompatibleDC(screen); bitmap = CreateCompatibleBitmap(screen, width, height); previous = SelectObject(memory, bitmap);
                if (screen == 0 || memory == 0 || bitmap == 0 || previous == 0 || previous == -1
                    || !BitBlt(memory, 0, 0, width, height, screen, left, top, 0x00CC0020)) throw new System.ComponentModel.Win32Exception();
                BitmapSource captured = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                captured.Freeze(); Save(captured, name); return captured;
            }
            finally
            {
                if (previous != 0 && previous != -1 && memory != 0) SelectObject(memory, previous);
                if (bitmap != 0) DeleteObject(bitmap); if (memory != 0) DeleteDC(memory); if (screen != 0) ReleaseDC(0, screen);
            }
        }
        Point ExactScreenPoint(Visual visual, Point point)
        {
            if (PresentationSource.FromVisual(visual) is not HwndSource { RootVisual: { } root, CompositionTarget: { } target } source
                || (source.Handle != owned && !IsChild(owned, source.Handle)))
                throw new InvalidOperationException("Exact screen mapping requires an owned connected visual.");
            Point local = ReferenceEquals(visual, root) ? point : visual.TransformToAncestor(root).Transform(point);
            if (VisualTreeHelper.GetTransform(root) is { } transform) local = transform.Transform(local);
            local += VisualTreeHelper.GetOffset(root);
            Point device = target.TransformToDevice.Transform(local);
            var clientOrigin = new NativePoint();
            if (!ClientToScreen(source.Handle, ref clientOrigin)) throw new System.ComponentModel.Win32Exception();
            return new Point(clientOrigin.X + device.X, clientOrigin.Y + device.Y);
        }
        void Save(BitmapSource source, string name)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            using var file = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(file);
        }
    }

    private sealed record Probe(BitmapSource Bitmap, Point[] Points, Rect Geometry, int Left, int Top);
    private sealed record Detection(bool Passed, int GoodPairs, int TotalPairs, byte[][] Pixels);
    private static Detection Detect(BitmapSource bitmap, Point[] points, bool inverted)
    {
        byte[][] pixels = points.Select(point => Pixel(bitmap, (int)point.X, (int)point.Y)).ToArray();
        int good = 0;
        for (int pair = 0; pair < PairCount; pair++)
        {
            byte[] white = pixels[pair * 2 + (inverted ? 0 : 1)], black = pixels[pair * 2 + (inverted ? 1 : 0)];
            if (white.Take(3).All(channel => channel >= WhiteMinimum) && black.Take(3).All(channel => channel <= BlackMaximum)) good++;
        }
        return new(good >= Math.Ceiling(PairCount * RequiredFraction), good, PairCount, pixels);
    }
    private static byte[] Pixel(BitmapSource source, int x, int y)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0); return pixel;
    }
    private static string PixelHash(BitmapSource source)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return Hash(pixels);
    }
    private static byte[] Fixture()
    {
        var paint = new StringBuilder("1 1 1 rg 0 0 1200 2400 re f 0 0 0 rg ");
        for (int x = 0; x < 1200; x += 2) paint.Append(x.ToString(CultureInfo.InvariantCulture)).Append(" 1800 1 300 re f ");
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1200 2400] /Resources << >> /Contents 4 0 R >>",
            $"<< /Length {paint.Length} >>\nstream\n{paint}endstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1200 2400] /Resources << >> /Contents 4 0 R >>"];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Pump()
    { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct PointI(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(PointI point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
