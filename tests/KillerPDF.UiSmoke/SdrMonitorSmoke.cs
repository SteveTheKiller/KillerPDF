using System.Buffers.Binary;
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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Rendering;

internal static class SdrMonitorSmoke
{
    internal static int Run(Window owner, string appPath, string folder)
    {
        if (Environment.MachineName is not ("SN01" or "SN02"))
            throw new InvalidOperationException("Monitor UI acceptance requires an exclusively owned VM.");
        Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "monitor-red.pdf");
        byte[] original = RedPdf();
        File.WriteAllBytes(source, original);
        string largeSource = Path.Combine(folder, "monitor-large-red.pdf");
        byte[] largeOriginal = RedPdf(1200);
        Exception? failure = null;
        string stage = "observation-boundary";
        int profileReads = 0;
        byte[]? actual = null, visible = null;
        var modals = new List<string>();
        var checks = new List<object>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            foreach (Window window in Application.Current.Windows.Cast<Window>().Where(w => w != owner && w.IsVisible).ToArray())
            {
                modals.Add(window.GetType().Name);
                window.Close();
            }
        };
        DispatcherUnhandledExceptionEventHandler onFailure = (_, args) => { failure ??= args.Exception; args.Handled = true; };
        Application.Current.DispatcherUnhandledException += onFailure;
        timer.Start();
        try
        {
            object viewer = owner.FindName("Viewer") ?? throw new InvalidOperationException("Primary viewer missing.");
            var profile = PdfColorProfile.Load(MonitorBytes());
            var target = new PdfDisplayTarget("test-output", "Synthetic monitor")
            {
                ColorState = new(PdfDisplayColorMode.ConventionalSdr, 15, 1, 0, 0, 8, null, null),
                StandardProfile = new(PdfDisplayProfileStatus.Named, 0, "synthetic-monitor.icc", null, null)
            };
            var display = new PdfDisplayDiscoverySnapshot(31, PdfDisplayDiscoveryStatus.Available,
                "synthetic-desktop", new(0, 0, 1920, 1080), null, null)
            { TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget, [target]) };
            viewer.GetType().GetProperty("DisplayCapture", BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(viewer, (Func<nint, PdfDisplayDiscoverySnapshot>)(_ => display));
            viewer.GetType().GetProperty("DisplayProfileReader", BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(viewer, (Func<string, CancellationToken, PdfColorProfile>)((name, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (name != "synthetic-monitor.icc") throw new InvalidOperationException("Wrong observed profile.");
                    Interlocked.Increment(ref profileReads);
                    return profile;
                }));
            stage = "public-open";
            owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Instance | BindingFlags.Public,
                null, [typeof(string)], null)!.Invoke(owner, [source]);
            Wait(() => owner.FindName("FileNameLabel") is TextBlock label && label.Text == Path.GetFileName(source)
                && owner.FindName("ViewSingleRadio") is RadioButton { IsVisible: true, IsEnabled: true });
            var radio = (RadioButton)owner.FindName("ViewSingleRadio");
            stage = "single-mode";
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(radio).GetPattern(PatternInterface.SelectionItem)).Select();
            var pages = Descendants((DependencyObject)viewer).OfType<Panel>().Single(panel => panel.Name == "PageContentPanel");
            Wait(() => radio.IsChecked == true && pages.IsVisible && Descendants(pages).OfType<Image>()
                .Any(image => image.IsVisible && image.Source is BitmapSource));
            var image = Descendants(pages).OfType<Image>().First(image => image.IsVisible && image.Source is BitmapSource);
            BitmapSource? previous = null;
            var stable = Stopwatch.StartNew();
            Wait(() =>
            {
                var current = image.Source as BitmapSource;
                if (!ReferenceEquals(previous, current)) { previous = current; stable.Restart(); }
                return current is not null && stable.ElapsedMilliseconds >= 700 && image.Opacity >= .99
                    && image.IsVisible && image.ActualWidth > 20 && image.ActualHeight > 20;
            });
            stage = "monitor-device-pixels";
            actual = Pixel((BitmapSource)image.Source);
            Save((BitmapSource)image.Source, "source.png");
            var rendered = new RenderTargetBitmap((int)Math.Ceiling(image.ActualWidth),
                (int)Math.Ceiling(image.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            rendered.Render(image);
            visible = Pixel(rendered);
            Save(rendered, "rendered.png");
            if (!actual.SequenceEqual(new byte[] { 4, 57, 111, 255 }))
                throw new InvalidOperationException("Observed SDR profile was not applied. Actual BGRA: " + string.Join(",", actual));
            if (profileReads == 0 || !visible.Zip(new byte[] { 4, 57, 111, 255 }, (a, b) => Math.Abs(a - b) <= 2).All(equal => equal))
                throw new InvalidOperationException("Rendered monitor colors or profile-boundary invocation did not match.");
            checks.Add(new { stage, actual, visible, profileReads });

            stage = "changed-monitor-profile";
            int previousReads = profileReads;
            profile = PdfColorProfile.Load(GammaMonitorBytes());
            display = display with { Generation = 32 };
            ReturnToSingle();
            CheckPixels([30, 120, 168, 255], "changed-profile");
            if (profileReads <= previousReads)
                throw new InvalidOperationException("Changed display generation did not reread the associated profile.");
            checks.Add(new { stage, actual, visible, profileReads, profile.ContentSha256 });

            stage = "composed-monitor-refinement";
            var pageGrid = (Grid)image.Parent;
            Wait(() => pageGrid.Children.OfType<Image>().Any(detail => !ReferenceEquals(detail, image)
                && detail.IsVisible && detail.Source is BitmapSource && detail.ActualWidth > 1 && detail.ActualHeight > 1));
            var visibleDetail = pageGrid.Children.OfType<Image>().Single(detail => !ReferenceEquals(detail, image)
                && detail.IsVisible && detail.Source is BitmapSource);
            Point detailPoint = visibleDetail.TransformToAncestor(pageGrid).Transform(
                new Point(visibleDetail.ActualWidth / 2, visibleDetail.ActualHeight / 2));
            var composed = new RenderTargetBitmap((int)Math.Ceiling(pageGrid.ActualWidth),
                (int)Math.Ceiling(pageGrid.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            composed.Render(pageGrid);
            byte[] composedPixel = new byte[4];
            composed.CopyPixels(new Int32Rect((int)detailPoint.X, (int)detailPoint.Y, 1, 1), composedPixel, 4, 0);
            Save(composed, "composed-refinement.png");
            if (!composedPixel.Zip(new byte[] { 30, 120, 168, 255 },
                    (a, b) => Math.Abs(a - b) <= 2).All(equal => equal))
                throw new InvalidOperationException("Visible detail bypassed monitor conversion. Composed BGRA: "
                    + string.Join(",", composedPixel));
            checks.Add(new { stage, composedPixel, detailPoint.X, detailPoint.Y, profileReads });

            stage = "missing-monitor-profile";
            previousReads = profileReads;
            display = display with { Generation = 33, TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget,
                [target with { StandardProfile = new(PdfDisplayProfileStatus.NoProfile, 0, null, null, null) }]) };
            ReturnToSingle();
            CheckPixels([0, 0, 255, 255], "missing-profile");
            // Windows treats an unprofiled SDR monitor as sRGB, so pages take the plain sRGB path with no monitor
            // conversion and no fallback notice.
            if (profileReads != previousReads || !string.IsNullOrWhiteSpace(image.ToolTip?.ToString()))
                throw new InvalidOperationException("A monitor without a profile must show sRGB without a fallback warning and without reading a profile.");
            checks.Add(new { stage, actual, visible, profileReads, warning = image.ToolTip?.ToString() });

            stage = "continuous-monitor-profile";
            display = display with { Generation = 34, TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget, [target]) };
            var continuousRadio = (RadioButton)owner.FindName("ViewContinuousRadio");
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(continuousRadio).GetPattern(PatternInterface.SelectionItem)).Select();
            var continuousPanel = Descendants((DependencyObject)viewer).OfType<Panel>()
                .Single(panel => panel.Name == "ContinuousPanel");
            Wait(() => continuousRadio.IsChecked == true && continuousPanel.IsVisible && !pages.IsVisible
                && continuousPanel.Opacity >= .99 && Descendants(continuousPanel).OfType<Image>()
                    .Any(candidate => candidate.IsVisible && candidate.Source is BitmapSource && candidate.ActualWidth > 20));
            var continuousImage = Descendants(continuousPanel).OfType<Image>()
                .First(candidate => candidate.IsVisible && candidate.Source is BitmapSource);
            var continuousSettled = Stopwatch.StartNew();
            Wait(() => continuousSettled.ElapsedMilliseconds >= 700);
            actual = Pixel((BitmapSource)continuousImage.Source);
            var continuousCapture = new RenderTargetBitmap((int)Math.Ceiling(continuousImage.ActualWidth),
                (int)Math.Ceiling(continuousImage.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            continuousCapture.Render(continuousImage);
            visible = Pixel(continuousCapture);
            Save(continuousCapture, "continuous-rendered.png");
            if (!actual.SequenceEqual(new byte[] { 30, 120, 168, 255 })
                || !visible.Zip(new byte[] { 30, 120, 168, 255 }, (a, b) => Math.Abs(a - b) <= 2).All(equal => equal))
                throw new InvalidOperationException("Continuous viewing bypassed monitor conversion. BGRA: " + string.Join(",", actual));
            checks.Add(new { stage, actual, visible, profileReads });

            stage = "continuous-large-open";
            File.WriteAllBytes(largeSource, largeOriginal);
            owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Instance | BindingFlags.Public,
                null, [typeof(string)], null)!.Invoke(owner, [largeSource]);
            Wait(() => owner.FindName("FileNameLabel") is TextBlock largeLabel
                && largeLabel.Text == Path.GetFileName(largeSource));
            stage = "continuous-large-base";
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(continuousRadio).GetPattern(PatternInterface.SelectionItem)).Select();
            Wait(() => continuousPanel.IsVisible && continuousRadio.IsChecked == true
                && continuousPanel.Opacity >= .99 && Descendants(continuousPanel).OfType<Image>()
                    .Any(candidate => candidate.Source is BitmapSource && candidate.ActualWidth > 1000));
            stage = "continuous-large-zoom";
            var zoom = (ComboBox)owner.FindName("ZoomBox");
            Wait(() => zoom.IsVisible && zoom.IsEnabled);
            zoom.SelectedItem = zoom.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "2.0");
            var zoomSettled = Stopwatch.StartNew();
            Wait(() => zoomSettled.ElapsedMilliseconds >= 700);
            var largeImage = Descendants(continuousPanel).OfType<Image>()
                .First(candidate => candidate.IsVisible && candidate.Source is BitmapSource && candidate.ActualWidth > 1000);
            var largeGrid = (Grid)largeImage.Parent;
            stage = "continuous-refined-profile";
            Wait(() => largeGrid.Children.OfType<Image>().Any(detail => !ReferenceEquals(detail, largeImage)
                && detail.IsVisible && detail.Source is BitmapSource && detail.ActualWidth > 1 && detail.ActualHeight > 1));
            var largeDetail = largeGrid.Children.OfType<Image>().Single(detail => !ReferenceEquals(detail, largeImage)
                && detail.IsVisible && detail.Source is BitmapSource);
            Point largePoint = largeDetail.TransformToAncestor(largeGrid).Transform(
                new Point(largeDetail.ActualWidth / 2, largeDetail.ActualHeight / 2));
            var largeCapture = new RenderTargetBitmap((int)Math.Ceiling(largeGrid.ActualWidth),
                (int)Math.Ceiling(largeGrid.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            largeCapture.Render(largeGrid);
            byte[] largePixel = new byte[4];
            largeCapture.CopyPixels(new Int32Rect((int)largePoint.X, (int)largePoint.Y, 1, 1), largePixel, 4, 0);
            Save(largeCapture, "continuous-refinement.png");
            if (!largePixel.Zip(new byte[] { 30, 120, 168, 255 }, (a, b) => Math.Abs(a - b) <= 2).All(equal => equal))
                throw new InvalidOperationException("Continuous detail bypassed monitor conversion. BGRA: " + string.Join(",", largePixel));
            checks.Add(new { stage, largePixel, largePoint.X, largePoint.Y, profileReads });

            stage = "continuous-live-profile-change";
            var viewport = Descendants((DependencyObject)viewer).OfType<ScrollViewer>()
                .Single(scroll => scroll.Name == "PagePreviewPanel");
            viewport.ScrollToHorizontalOffset(200);
            viewport.ScrollToVerticalOffset(300);
            Wait(() => Math.Abs(viewport.HorizontalOffset - 200) <= 1 && Math.Abs(viewport.VerticalOffset - 300) <= 1);
            double beforeHorizontal = viewport.HorizontalOffset, beforeVertical = viewport.VerticalOffset;
            var oldLargeSource = largeImage.Source;
            profile = PdfColorProfile.Load(MonitorBytes());
            display = display with { Generation = 35 };
            SendMessage(new WindowInteropHelper(owner).Handle, 0x7E, 0, 0);
            var liveSettled = Stopwatch.StartNew();
            Image? refreshedImage = null;
            // The re-render lands when the queue gets to it, so wait for the new page picture instead of a fixed time.
            Wait(() => liveSettled.ElapsedMilliseconds >= 1200 && (refreshedImage = Descendants(continuousPanel).OfType<Image>()
                .FirstOrDefault(candidate => candidate.IsVisible && candidate.Source is BitmapSource && candidate.ActualWidth > 1000
                    && !ReferenceEquals(candidate.Source, oldLargeSource))) is not null);
            actual = Pixel((BitmapSource)refreshedImage!.Source);
            if (ReferenceEquals(oldLargeSource, refreshedImage.Source)
                || !actual.SequenceEqual(new byte[] { 4, 57, 111, 255 }))
                throw new InvalidOperationException("Loaded Continuous pages did not refresh after a display notification. BGRA: "
                    + string.Join(",", actual));
            if (Math.Abs(viewport.HorizontalOffset - beforeHorizontal) > 1
                || Math.Abs(viewport.VerticalOffset - beforeVertical) > 1
                || (zoom.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "2.0")
                throw new InvalidOperationException("Display refresh changed Continuous zoom or scroll position.");
            var liveGrid = (Grid)refreshedImage.Parent;
            Point livePoint = viewport.TransformToDescendant(liveGrid).Transform(
                new Point(viewport.ViewportWidth / 2, viewport.ViewportHeight / 2));
            var liveCapture = new RenderTargetBitmap((int)Math.Ceiling(liveGrid.ActualWidth),
                (int)Math.Ceiling(liveGrid.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            liveCapture.Render(liveGrid);
            visible = new byte[4];
            liveCapture.CopyPixels(new Int32Rect((int)livePoint.X, (int)livePoint.Y, 1, 1), visible, 4, 0);
            Save(liveCapture, "continuous-live-composed.png");
            if (!visible.Zip(new byte[] { 4, 57, 111, 255 }, (a, b) => Math.Abs(a - b) <= 2).All(equal => equal))
                throw new InvalidOperationException("Composed Continuous view retained the old profile after notification.");
            checks.Add(new { stage, actual, visible, beforeHorizontal, beforeVertical, profileReads });

            void ReturnToSingle()
            {
                var continuous = (RadioButton)owner.FindName("ViewContinuousRadio");
                ((ISelectionItemProvider)new RadioButtonAutomationPeer(continuous).GetPattern(PatternInterface.SelectionItem)).Select();
                Wait(() => continuous.IsChecked == true && !pages.IsVisible
                    && Descendants((DependencyObject)viewer).OfType<Panel>().Any(panel => panel.Name == "ContinuousPanel" && panel.IsVisible));
                var before = image.Source;
                ((ISelectionItemProvider)new RadioButtonAutomationPeer(radio).GetPattern(PatternInterface.SelectionItem)).Select();
                Wait(() => radio.IsChecked == true && pages.IsVisible && image.IsVisible && image.Source is BitmapSource
                    && !ReferenceEquals(before, image.Source));
                var settled = Stopwatch.StartNew();
                Wait(() => settled.ElapsedMilliseconds >= 700);
            }

            void CheckPixels(byte[] expected, string name)
            {
                actual = Pixel((BitmapSource)image.Source);
                var capture = new RenderTargetBitmap((int)Math.Ceiling(image.ActualWidth),
                    (int)Math.Ceiling(image.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                capture.Render(image);
                visible = Pixel(capture);
                Save((BitmapSource)image.Source, name + "-source.png");
                Save(capture, name + "-rendered.png");
                if (!actual.SequenceEqual(expected)
                    || !visible.Zip(expected, (a, b) => Math.Abs(a - b) <= 2).All(equal => equal))
                    throw new InvalidOperationException("Profile transition pixels differ at " + stage + ": " + string.Join(",", actual));
            }
        }
        catch (Exception error) { failure ??= error; }
        finally
        {
            timer.Stop();
            Application.Current.DispatcherUnhandledException -= onFailure;
            try
            {
                owner.Close();
                var closing = Stopwatch.StartNew();
                while (owner.IsVisible && closing.ElapsedMilliseconds < 3000)
                {
                    owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    Thread.Sleep(10);
                }
            }
            catch (Exception error) { failure ??= error; }
            bool preserved = File.ReadAllBytes(source).AsSpan().SequenceEqual(original)
                && (!File.Exists(largeSource) || File.ReadAllBytes(largeSource).AsSpan().SequenceEqual(largeOriginal));
            if (!preserved || owner.IsVisible || modals.Count != 0)
                failure ??= new InvalidOperationException("Original, window teardown or unexpected-modal check failed.");
            File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new
            {
                passed = failure is null, stage, error = failure?.ToString(), actual, visible, profileReads, preserved,
                checks,
                windowClosed = !owner.IsVisible, modals, machine = Environment.MachineName,
                app = Hash(File.ReadAllBytes(appPath)), driver = Hash(File.ReadAllBytes(typeof(SdrMonitorSmoke).Assembly.Location)),
                engine = SelectedAssemblyIdentity.HashLoaded(typeof(PdfRenderedFrame).Assembly, appPath)
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failure is null ? 0 : 1;

        void Wait(Func<bool> predicate)
        {
            var watch = Stopwatch.StartNew();
            while (!predicate())
            {
                if (failure is not null) throw failure;
                if (watch.ElapsedMilliseconds > 15000) throw new TimeoutException("Readiness at " + stage
                    + "; images: " + string.Join("; ", Descendants(owner).OfType<Image>()
                        .Where(candidate => candidate.IsVisible && candidate.Source is BitmapSource)
                        .Select(candidate => $"{candidate.ActualWidth}x{candidate.ActualHeight}/{((BitmapSource)candidate.Source).PixelWidth}x{((BitmapSource)candidate.Source).PixelHeight}")));
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(10);
            }
        }
        void Save(BitmapSource bitmap, string name)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(folder, name));
            encoder.Save(output);
        }
    }

    private static byte[] Pixel(BitmapSource source)
    {
        BitmapSource bitmap = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32
            ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static void NotifyDisplayChanged(Window owner) =>
        SendMessage(new WindowInteropHelper(owner).Handle, 0x7E, 0, 0);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    private static byte[] RedPdf(int edge = 72)
    {
        string paint = $"1 0 0 rg 0 0 {edge} {edge} re f\n";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {edge} {edge}] /Resources << >> /Contents 4 0 R >>",
            $"<< /Length {paint.Length} >>\nstream\n{paint}endstream"];
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int> { 0 };
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = text.Length;
        text.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) text.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + " 00000 n \n");
        text.Append($"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    internal static byte[] MonitorBytes()
    {
        var bytes = new byte[300];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 300);
        bytes[8] = 4;
        "mntr"u8.CopyTo(bytes.AsSpan(12)); "RGB "u8.CopyTo(bytes.AsSpan(16));
        "XYZ "u8.CopyTo(bytes.AsSpan(20)); "acsp"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(128), 6);
        string[] tags = ["rTRC", "gTRC", "bTRC", "rXYZ", "gXYZ", "bXYZ"];
        for (int i = 0; i < 6; i++)
        {
            int entry = 132 + i * 12, offset = i < 3 ? 204 : 216 + (i - 3) * 20;
            Encoding.ASCII.GetBytes(tags[i]).CopyTo(bytes, entry);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 4), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 8), i < 3 ? 12u : 20u);
            if (i < 3) "curv"u8.CopyTo(bytes.AsSpan(offset));
            else { "XYZ "u8.CopyTo(bytes.AsSpan(offset)); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset + 8 + (i - 3) * 4), 65536); }
        }
        return bytes;
    }

    private static byte[] GammaMonitorBytes()
    {
        byte[] bytes = new byte[316];
        MonitorBytes().CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 316);
        for (int channel = 0; channel < 3; channel++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(136 + channel * 12), 300);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(140 + channel * 12), 14);
        }
        "curv"u8.CopyTo(bytes.AsSpan(300));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(308), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(312), 512);
        return bytes;
    }
}
