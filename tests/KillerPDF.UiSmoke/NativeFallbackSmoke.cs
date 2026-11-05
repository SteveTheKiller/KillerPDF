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
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Rendering;

internal static class NativeFallbackSmoke
{
    internal static int Run(Window owner, string appPath, string folder)
    {
        if (Environment.MachineName is not ("SN01" or "SN02"))
            throw new InvalidOperationException("Native fallback acceptance requires an owned VM.");
        Directory.CreateDirectory(folder);
        byte[] original = new PdfDocumentBuilder().AddPage(300, 600, Paint(300, 600))
            .AddPage(600, 300, Paint(600, 300)).Build();
        string path = Path.Combine(folder, "portrait-landscape.pdf");
        File.WriteAllBytes(path, original);
        string stage = "configure-boundaries";
        Exception? failure = null;
        var checks = new List<object>();
        var presenters = new List<(nint Window, IPdfFramePresenter Presenter)>();
        var unexpected = new List<string>();
        int captureCalls = 0;
        bool closing = false;
        int discardedTestCopies = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            foreach (Window modal in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                var discard = closing ? Descendants(modal).OfType<Button>().FirstOrDefault(button =>
                    button.IsVisible && button.IsEnabled && button.Content?.ToString() ==
                        owner.FindResource("Str_Btn_DontSave").ToString()) : null;
                if (discard is not null)
                {
                    discardedTestCopies++;
                    ((IInvokeProvider)new ButtonAutomationPeer(discard).GetPattern(PatternInterface.Invoke)).Invoke();
                }
                else { unexpected.Add(modal.GetType().Name); modal.Close(); }
            }
        };
        DispatcherUnhandledExceptionEventHandler onError = (_, args) => { failure ??= args.Exception; args.Handled = true; };
        Application.Current.DispatcherUnhandledException += onError;
        timer.Start();
        try
        {
            object viewer = owner.FindName("Viewer")!;
            stage = "public-frame-preflight";
            var preflight = new PdfPageRenderer(KillerPdf.Engine.Documents.PdfDocument.Open(original))
                .RenderFrame(0, new PdfRenderOptions(300, 600));
            checks.Add(new { stage, preflight.Width, preflight.Height, preflight.ColorEncoding });
            stage = "configure-boundaries";
            var target = new PdfDisplayTarget("fallback-test", "Synthetic observed mode")
            { ColorState = new(PdfDisplayColorMode.AdvancedColorSdr, 15, 1, 0, 0, 16, null, null) };
            var display = new PdfDisplayDiscoverySnapshot(70, PdfDisplayDiscoveryStatus.Available,
                "fallback-desktop", new(0, 0, 1920, 1080), null, null)
            { TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget,
                [target with { ColorState = new(PdfDisplayColorMode.ConventionalSdr, 15, 1, 0, 0, 8, null, null),
                    StandardProfile = new(PdfDisplayProfileStatus.NoProfile, 0, null, null, null) }]) };
            viewer.GetType().GetProperty("DisplayCapture", BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(viewer, (Func<nint, PdfDisplayDiscoverySnapshot>)(_ => { captureCalls++; return display; }));
            Type presenterType = owner.GetType().Assembly.GetType("KillerPDF.Presentation.WindowsPdfFramePresenter", true)!;
            viewer.GetType().GetProperty("FramePresenterFactory", BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(viewer, (Func<nint, IPdfFramePresenter>)(window =>
                {
                    var presenter = (IPdfFramePresenter)Activator.CreateInstance(presenterType, window, 512L << 20)!;
                    presenters.Add((window, presenter));
                    return presenter;
                }));
            stage = "public-open";
            owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Instance | BindingFlags.Public,
                null, [typeof(string)], null)!.Invoke(owner, [path]);
            Wait(() => owner.FindName("FileNameLabel") is TextBlock label && label.Text == Path.GetFileName(path));
            var single = (RadioButton)owner.FindName("ViewSingleRadio");
            stage = "single-mode";
            Wait(() => single.IsVisible && single.IsEnabled);
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(single).GetPattern(PatternInterface.SelectionItem)).Select();
            var zoom = (ComboBox)owner.FindName("ZoomBox");
            Wait(() => zoom.IsVisible && zoom.IsEnabled);
            zoom.SelectedItem = zoom.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "fitpage");
            stage = "fresh-compatibility-control";
            var viewport = Descendants((DependencyObject)viewer).OfType<ScrollViewer>().Single(scroll => scroll.Name == "PagePreviewPanel");
            Wait(() => Descendants((DependencyObject)viewer).OfType<Image>().Any(image => image.IsVisible
                && image.Source is BitmapSource && image.ActualHeight > 100 && image.Parent is Grid grid
                && grid.Children.Count > 1 && ReferenceEquals(grid.Children[0], image)));
            zoom.SelectedItem = zoom.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "1.0");
            Settle();
            zoom.SelectedItem = zoom.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "fitpage");
            Settle();
            var freshImage = Descendants((DependencyObject)viewer).OfType<Image>().Single(image => image.IsVisible
                && image.Source is BitmapSource && image.ActualHeight > 100 && image.Parent is Grid grid
                && grid.Children.Count > 1 && ReferenceEquals(grid.Children[0], image));
            if (presenters.Count != 0) throw new InvalidOperationException("Fresh compatibility control already created a native presenter.");
            var fresh = Capture(0, viewport, "fresh-compatibility-portrait", freshImage);
            AssertCorners(fresh, "fresh compatibility portrait");
            bool absentMarksRejected = false;
            try { AssertAnnotationMarks(fresh); }
            catch (InvalidOperationException) { absentMarksRejected = true; }
            if (!absentMarksRejected) throw new InvalidOperationException("Missing-annotation negative control incorrectly passes.");
            checks.Add(new { stage, fresh.PixelWidth, fresh.PixelHeight, absentMarksRejected, presenters = presenters.Count });
            display = display with { Generation = 71, TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget, [target]) };
            SendMessage(new WindowInteropHelper(owner).Handle, 0x7E, 0, 0);
            stage = "actual-native-submission";
            Wait(() => presenters.Any(item => item.Presenter.Status is
                { Mode: PdfPresentationMode.LinearFloat, LastAcceptedStamp: { PageIndex: 0, RegionId: 1 } }));
            var primary = presenters.First(item => item.Presenter.Status.LastAcceptedStamp is { PageIndex: 0, RegionId: 1 });
            checks.Add(new { stage, primary.Presenter.Status });
            stage = "fit-native-portrait";
            zoom.SelectedItem = zoom.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "1.0");
            Settle();
            zoom.SelectedItem = zoom.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "fitpage");
            Settle();
            var native = Capture(primary.Window, viewport, "native-portrait");
            AssertCorners(native, "native portrait");
            if (Math.Abs(native.PixelWidth - fresh.PixelWidth) > 2 || Math.Abs(native.PixelHeight - fresh.PixelHeight) > 2)
                throw new InvalidOperationException("Native and fresh compatibility page geometry differ.");
            checks.Add(new { stage, primary.Presenter.Status, native.PixelWidth, native.PixelHeight });

            stage = "sdr-fallback";
            display = display with { Generation = 72, TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget,
                [target with { ColorState = new(PdfDisplayColorMode.ConventionalSdr, 15, 1, 0, 0, 8, null, null),
                    StandardProfile = new(PdfDisplayProfileStatus.NoProfile, 0, null, null, null) }]) };
            SendMessage(new WindowInteropHelper(owner).Handle, 0x7E, 0, 0);
            Wait(() => OverlayImages().Any(image => image.IsVisible && image.Source is BitmapSource));
            Settle();
            AssertCorners(Capture(primary.Window, viewport, "fallback-portrait"), "same-aspect fallback");
            checks.Add(new { stage });

            stage = "public-next-landscape";
            nint ownedWindow = new WindowInteropHelper(owner).Handle;
            Point center = viewport.PointToScreen(new Point(viewport.ViewportWidth / 2, viewport.ViewportHeight / 2));
            if (GetAncestor(WindowFromPoint(new PointI((int)Math.Round(center.X), (int)Math.Round(center.Y))), 2) != ownedWindow)
                throw new InvalidOperationException("Navigation target is obscured.");
            if (!SetForegroundWindow(ownedWindow)) throw new System.ComponentModel.Win32Exception();
            viewport.Focus(); Keyboard.Focus(viewport);
            Wait(() => GetForegroundWindow() == ownedWindow && viewport.IsKeyboardFocused);
            keybd_event(0x22, 0, 0, 0);
            keybd_event(0x22, 0, 2, 0);
            string secondPage = string.Format((string)owner.FindResource("Str_PageOf"), 2, 2);
            Wait(() => owner.FindName("StatusText") is TextBlock status && status.Text.Contains(secondPage, StringComparison.Ordinal));
            Settle();
            stage = "fallback-landscape-corners";
            var landscape = Capture(primary.Window, viewport, "fallback-landscape");
            AssertCorners(landscape, "landscape fallback");
            if (landscape.PixelWidth < landscape.PixelHeight * 1.9)
                throw new InvalidOperationException("Landscape fallback retained portrait clipping geometry.");
            checks.Add(new { stage, landscape.PixelWidth, landscape.PixelHeight });

            stage = "fallback-return-portrait";
            if (!SetForegroundWindow(ownedWindow)) throw new System.ComponentModel.Win32Exception();
            viewport.Focus(); Keyboard.Focus(viewport);
            Wait(() => GetForegroundWindow() == ownedWindow && viewport.IsKeyboardFocused);
            if (GetForegroundWindow() != ownedWindow || !viewport.IsKeyboardFocused)
                throw new InvalidOperationException("Owned navigation focus changed before recovery.");
            keybd_event(0x21, 0, 0, 0);
            keybd_event(0x21, 0, 2, 0);
            string firstPage = string.Format((string)owner.FindResource("Str_PageOf"), 1, 2);
            Wait(() => owner.FindName("StatusText") is TextBlock status && status.Text.Contains(firstPage, StringComparison.Ordinal));
            Settle();
            AssertCorners(Capture(primary.Window, viewport, "fallback-return-portrait"), "portrait fallback recovery");
            checks.Add(new { stage });

            stage = "return-to-native";
            display = display with { Generation = 73, TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget, [target]) };
            SendMessage(ownedWindow, 0x7E, 0, 0);
            Wait(() => primary.Presenter.Status is { Mode: PdfPresentationMode.LinearFloat,
                LastAcceptedStamp: { DisplayGeneration: 73, PageIndex: 0, RegionId: 1 } });
            Settle();
            AssertCorners(Capture(primary.Window, viewport, "native-return-portrait"), "native recovery");
            checks.Add(new { stage, primary.Presenter.Status });

            stage = "public-clockwise-rotation";
            long beforeRotationGeneration = primary.Presenter.Status.LastAcceptedStamp!.RenderGeneration;
            var beforeTurn = OverlayImages().Select(image => image.Source).OfType<BitmapSource>().ToArray();
            SendControlKey(0x52);
            Wait(() => OverlayImages().Any(image => image.IsVisible && image.Source is BitmapSource bitmap
                && !beforeTurn.Contains(bitmap) && bitmap.PixelWidth > bitmap.PixelHeight * 1.9));
            zoom.SelectedItem = zoom.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "1.0");
            Settle();
            zoom.SelectedItem = zoom.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "fitpage");
            Settle();
            var rotated = Capture(primary.Window, viewport, "public-clockwise-rotation");
            AssertCorners(rotated, "clockwise rotation fallback", clockwise: true);
            if (rotated.PixelWidth < rotated.PixelHeight * 1.9)
                throw new InvalidOperationException("Clockwise rotation retained portrait geometry.");
            checks.Add(new { stage, rotated.PixelWidth, rotated.PixelHeight });

            stage = "public-undo-native-recovery";
            var beforeUndo = OverlayImages().Select(image => image.Source).OfType<BitmapSource>().ToArray();
            SendControlKey(0x5A);
            Wait(() => OverlayImages().Any(image => image.Source is BitmapSource bitmap
                && !beforeUndo.Contains(bitmap) && bitmap.PixelHeight > bitmap.PixelWidth * 1.9));
            Wait(() => presenters.Any(item => item.Presenter.Status is { Mode: PdfPresentationMode.LinearFloat,
                LastAcceptedStamp: { DisplayGeneration: 73, PageIndex: 0, RegionId: 1 } stamp }
                && stamp.RenderGeneration > beforeRotationGeneration));
            Settle();
            var recovered = Capture(presenters.Last(item => IsWindow(item.Window)).Window, viewport, "rotation-undo-native");
            AssertCorners(recovered, "rotation undo native recovery");
            checks.Add(new { stage, statuses = presenters.Select(item => item.Presenter.Status).ToArray() });

            stage = "public-annotation-corners";
            var shape = (Button)owner.FindName("ToolShapeBtn");
            Wait(() => shape.IsVisible && shape.IsEnabled);
            ((IInvokeProvider)new ButtonAutomationPeer(shape).GetPattern(PatternInterface.Invoke)).Invoke();
            Wait(() => Descendants(owner).OfType<Border>().Any(border => border.IsVisible
                && border.Tag is Color { R: 0, G: 0, B: 0 } && border.Width == 18));
            var black = Descendants(owner).OfType<Border>().Single(border => border.IsVisible
                && border.Tag is Color { R: 0, G: 0, B: 0 } && border.Width == 18);
            Point blackPoint = black.PointToScreen(new Point(black.ActualWidth / 2, black.ActualHeight / 2));
            Drag(blackPoint, blackPoint);
            var opacity = Descendants(owner).OfType<Slider>().Single(slider => slider.IsVisible
                && slider.Minimum == 10 && slider.Maximum == 255);
            opacity.Value = 255;
            Settle();
            nint annotationSurface = presenters.Last(item => IsWindow(item.Window)).Window;
            if (!GetWindowRect(annotationSurface, out var paperBounds)) throw new System.ComponentModel.Win32Exception();
            foreach (var centerMark in new[] { new Point(.18, .18), new Point(.82, .18), new Point(.18, .82), new Point(.82, .82) })
            {
                Point start = new(paperBounds.Left + (centerMark.X - .04) * (paperBounds.Right - paperBounds.Left),
                    paperBounds.Top + (centerMark.Y - .04) * (paperBounds.Bottom - paperBounds.Top));
                Point end = new(paperBounds.Left + (centerMark.X + .04) * (paperBounds.Right - paperBounds.Left),
                    paperBounds.Top + (centerMark.Y + .04) * (paperBounds.Bottom - paperBounds.Top));
                Drag(start, end);
            }
            Settle();
            AssertAnnotationMarks(Capture(annotationSurface, viewport, "annotation-native-corners"));
            checks.Add(new { stage });

            stage = "annotation-fallback-corners";
            display = display with { Generation = 74, TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget,
                [target with { ColorState = new(PdfDisplayColorMode.ConventionalSdr, 15, 1, 0, 0, 8, null, null),
                    StandardProfile = new(PdfDisplayProfileStatus.NoProfile, 0, null, null, null) }]) };
            SendMessage(ownedWindow, 0x7E, 0, 0);
            Wait(() => OverlayImages().Any(image => image.IsVisible && image.Source is BitmapSource));
            Settle();
            AssertAnnotationMarks(Capture(annotationSurface, viewport, "annotation-fallback-corners"));
            checks.Add(new { stage });

            stage = "annotation-native-recovery";
            display = display with { Generation = 75, TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget, [target]) };
            SendMessage(ownedWindow, 0x7E, 0, 0);
            Wait(() => presenters.Any(item => item.Presenter.Status is { Mode: PdfPresentationMode.LinearFloat,
                LastAcceptedStamp: { DisplayGeneration: 75, PageIndex: 0, RegionId: 1 } }));
            Settle();
            AssertAnnotationMarks(Capture(annotationSurface, viewport, "annotation-native-recovery"));
            checks.Add(new { stage, statuses = presenters.Select(item => item.Presenter.Status).ToArray() });

            stage = "annotation-clockwise-rotation";
            long annotationGeneration = presenters.Last(item => IsWindow(item.Window)).Presenter.Status.LastAcceptedStamp!.RenderGeneration;
            var beforeAnnotationTurn = OverlayImages().Select(image => image.Source).OfType<BitmapSource>().ToArray();
            SendControlKey(0x52);
            Wait(() => OverlayImages().Any(image => image.IsVisible && image.Source is BitmapSource bitmap
                && !beforeAnnotationTurn.Contains(bitmap) && bitmap.PixelWidth > bitmap.PixelHeight * 1.9));
            Settle();
            var annotationTurn = Capture(annotationSurface, viewport, "annotation-clockwise-rotation");
            AssertCorners(annotationTurn, "annotated clockwise rotation", clockwise: true);
            AssertAnnotationMarks(annotationTurn);
            checks.Add(new { stage, annotationTurn.PixelWidth, annotationTurn.PixelHeight });

            stage = "annotation-rotation-undo";
            var beforeAnnotationUndo = OverlayImages().Select(image => image.Source).OfType<BitmapSource>().ToArray();
            SendControlKey(0x5A);
            Wait(() => OverlayImages().Any(image => image.Source is BitmapSource bitmap && !beforeAnnotationUndo.Contains(bitmap)
                && bitmap.PixelHeight > bitmap.PixelWidth * 1.9));
            Wait(() => presenters.Any(item => item.Presenter.Status is { Mode: PdfPresentationMode.LinearFloat,
                LastAcceptedStamp: { DisplayGeneration: 75, PageIndex: 0, RegionId: 1 } stamp }
                && stamp.RenderGeneration > annotationGeneration));
            Settle();
            var annotationUndo = Capture(annotationSurface, viewport, "annotation-rotation-undo");
            AssertCorners(annotationUndo, "annotated rotation undo");
            AssertAnnotationMarks(annotationUndo);
            checks.Add(new { stage, statuses = presenters.Select(item => item.Presenter.Status).ToArray() });

            void Drag(Point start, Point end)
            {
                SetForegroundWindow(ownedWindow);
                Wait(() => GetForegroundWindow() == ownedWindow);
                foreach (Point point in new[] { start, end })
                    if (GetAncestor(WindowFromPoint(new PointI((int)point.X, (int)point.Y)), 2) != ownedWindow)
                        throw new InvalidOperationException("Owned annotation target is obscured.");
                if (!SetCursorPos((int)Math.Round(start.X), (int)Math.Round(start.Y))) throw new System.ComponentModel.Win32Exception();
                Pump(); Thread.Sleep(30);
                mouse_event(2, 0, 0, 0, 0);
                try
                {
                    for (int step = 1; step <= 12; step++)
                    {
                        if (!SetCursorPos((int)Math.Round(start.X + (end.X - start.X) * step / 12),
                            (int)Math.Round(start.Y + (end.Y - start.Y) * step / 12))) throw new System.ComponentModel.Win32Exception();
                        Pump(); Thread.Sleep(15);
                    }
                }
                finally { mouse_event(4, 0, 0, 0, 0); Pump(); }
            }

            void SendControlKey(byte key)
            {
                Point targetPoint = viewport.PointToScreen(new Point(viewport.ViewportWidth / 2, viewport.ViewportHeight / 2));
                if (GetAncestor(WindowFromPoint(new PointI((int)targetPoint.X, (int)targetPoint.Y)), 2) != ownedWindow)
                    throw new InvalidOperationException("Owned shortcut target is obscured.");
                SetForegroundWindow(ownedWindow);
                viewport.Focus(); Keyboard.Focus(viewport);
                Wait(() => GetForegroundWindow() == ownedWindow && viewport.IsKeyboardFocused);
                keybd_event(0x11, 0, 0, 0);
                try { keybd_event(key, 0, 0, 0); keybd_event(key, 0, 2, 0); }
                finally { keybd_event(0x11, 0, 2, 0); }
            }

            IEnumerable<Image> OverlayImages() => PresentationSource.CurrentSources.OfType<HwndSource>()
                .Where(source => IsChild(new WindowInteropHelper(owner).Handle, source.Handle))
                .Select(source => source.RootVisual).OfType<Grid>().SelectMany(Descendants).OfType<Image>();
        }
        catch (Exception error) { failure ??= error; }
        finally
        {
            Application.Current.DispatcherUnhandledException -= onError;
            closing = true;
            try { owner.Close(); SettleClosing(); } catch (Exception error) { failure ??= error; }
            timer.Stop();
            bool preserved = File.ReadAllBytes(path).AsSpan().SequenceEqual(original);
            if (!preserved || owner.IsVisible || unexpected.Count != 0 || presenters.Any(item => IsWindow(item.Window)))
                failure ??= new InvalidOperationException("Original, window or modal preservation failed.");
            File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new
            {
                passed = failure is null, stage, error = failure?.ToString(), checks, preserved,
                windowClosed = !owner.IsVisible, unexpected, discardedTestCopies,
                nativeHandlesClosed = presenters.All(item => !IsWindow(item.Window)),
                app = Hash(File.ReadAllBytes(appPath)), driver = Hash(File.ReadAllBytes(typeof(NativeFallbackSmoke).Assembly.Location)),
                engine = SelectedAssemblyIdentity.HashLoaded(typeof(PdfRenderedFrame).Assembly, appPath)
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failure is null ? 0 : 1;

        void Wait(Func<bool> ready)
        {
            var watch = Stopwatch.StartNew();
            while (!ready())
            {
                if (failure is not null) throw failure;
                if (watch.ElapsedMilliseconds > 15000) throw new TimeoutException("Readiness at " + stage
                    + "; capture calls: " + captureCalls + "; single: " + (owner.FindName("ViewSingleRadio") as RadioButton)?.IsChecked
                    + "; status text: " + (owner.FindName("StatusText") as TextBlock)?.Text
                    + "; presenter statuses: " + JsonSerializer.Serialize(presenters.Select(item => item.Presenter.Status))
                    + "; image warnings: " + string.Join("; ", Descendants(owner).OfType<Image>()
                        .Where(image => image.Source is BitmapSource).Select(image => image.ToolTip?.ToString() ?? "none"))
                    + "; status: " + string.Join("; ", Descendants(owner).OfType<TextBlock>()
                        .Where(text => text.Text.Contains("render", StringComparison.OrdinalIgnoreCase)
                            || text.Text.Contains("viewport", StringComparison.OrdinalIgnoreCase)
                            || text.Text.Contains("attached", StringComparison.OrdinalIgnoreCase)).Select(text => text.Text)));
                Pump(); Thread.Sleep(10);
            }
        }
        void Settle() { var watch = Stopwatch.StartNew(); Wait(() => watch.ElapsedMilliseconds >= 700); }
        void SettleClosing()
        { var watch = Stopwatch.StartNew(); while (owner.IsVisible && watch.ElapsedMilliseconds < 3000) { Pump(); Thread.Sleep(10); } }
        BitmapSource Capture(nint window, ScrollViewer viewport, string name, FrameworkElement? element = null)
        {
            Rect bounds;
            if (element is not null)
                bounds = new(element.PointToScreen(default), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
            else
            {
                if (!GetWindowRect(window, out var rect)) throw new System.ComponentModel.Win32Exception();
                bounds = new(new Point(rect.Left, rect.Top), new Point(rect.Right, rect.Bottom));
            }
            Rect clip = new(viewport.PointToScreen(default), viewport.PointToScreen(new Point(viewport.ViewportWidth, viewport.ViewportHeight)));
            bounds.Intersect(clip);
            int left = (int)Math.Ceiling(bounds.Left), top = (int)Math.Ceiling(bounds.Top);
            int width = (int)Math.Floor(bounds.Right) - left, height = (int)Math.Floor(bounds.Bottom) - top;
            nint owned = new WindowInteropHelper(owner).Handle;
            if (width < 50 || height < 50) throw new InvalidOperationException("Owned page capture is empty.");
            foreach (PointI point in new[] { new PointI(left + 1, top + 1), new(left + width - 2, top + height - 2), new(left + width / 2, top + height / 2) })
                if (GetAncestor(WindowFromPoint(point), 2) != owned) throw new InvalidOperationException("Owned page is obscured.");
            nint screen = GetDC(0), memory = 0, bitmap = 0, previous = 0;
            try
            {
                memory = CreateCompatibleDC(screen); bitmap = CreateCompatibleBitmap(screen, width, height); previous = SelectObject(memory, bitmap);
                if (screen == 0 || memory == 0 || bitmap == 0 || previous == 0 || previous == -1
                    || !BitBlt(memory, 0, 0, width, height, screen, left, top, 0x00CC0020)) throw new System.ComponentModel.Win32Exception();
                BitmapSource capture = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                capture.Freeze();
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(capture));
                using (var file = File.Create(Path.Combine(folder, name + ".png"))) png.Save(file);
                return capture;
            }
            finally
            {
                if (previous != 0 && previous != -1 && memory != 0) SelectObject(memory, previous);
                if (bitmap != 0) DeleteObject(bitmap); if (memory != 0) DeleteDC(memory); if (screen != 0) ReleaseDC(0, screen);
            }
        }
    }

    private static void AssertCorners(BitmapSource source, string name, bool clockwise = false)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        (double X, double Y, byte[] Expected)[] probes =
        [(0.04, 0.04, [255, 0, 0, 255]), (.96, .04, [255, 0, 255, 255]),
         (.04, .96, [0, 0, 255, 255]), (.96, .96, [0, 255, 0, 255]), (.5, .5, [255, 255, 255, 255])];
        if (clockwise)
            probes = [(0.04, 0.04, [0, 0, 255, 255]), (.96, .04, [255, 0, 0, 255]),
                (.04, .96, [0, 255, 0, 255]), (.96, .96, [255, 0, 255, 255]), (.5, .5, [255, 255, 255, 255])];
        foreach (var probe in probes)
        {
            byte[] actual = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)(bitmap.PixelWidth * probe.X), (int)(bitmap.PixelHeight * probe.Y), 1, 1), actual, 4, 0);
            if (!actual.Zip(probe.Expected, (a, b) => Math.Abs(a - b) <= 12).All(equal => equal))
                throw new InvalidOperationException(name + $" corner {probe.X},{probe.Y}: BGRA" + string.Join(",", actual));
        }
    }
    private static byte[] Paint(int width, int height) => Encoding.ASCII.GetBytes(
        $"1 1 1 rg 0 0 {width} {height} re f 1 0 0 rg 0 0 30 30 re f 0 1 0 rg {width - 30} 0 30 30 re f "
        + $"0 0 1 rg 0 {height - 30} 30 30 re f 1 0 1 rg {width - 30} {height - 30} 30 30 re f");
    private static void AssertAnnotationMarks(BitmapSource source)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        foreach (var probe in new[] { new Point(.18, .18), new Point(.82, .18), new Point(.18, .82), new Point(.82, .82),
            new Point(.30, .18), new Point(.70, .18), new Point(.30, .82), new Point(.70, .82) })
        {
            byte[] pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)(bitmap.PixelWidth * probe.X), (int)(bitmap.PixelHeight * probe.Y), 1, 1), pixel, 4, 0);
            byte expected = probe.X is .18 or .82 ? (byte)0 : (byte)255;
            if (pixel.Take(3).Any(channel => Math.Abs(channel - expected) > 12))
                throw new InvalidOperationException($"Annotation probe {probe}: BGRA" + string.Join(",", pixel));
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Pump()
    { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct PointI(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out RectI rect);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(PointI point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extra);
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
