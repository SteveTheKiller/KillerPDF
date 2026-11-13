using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
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

internal static class FlattenScaleSmoke
{
    internal static int Run(Window owner, string appPath, string appXaml, string folder)
    {
        if (!new[] { "SN01", "SN02" }.Contains(Environment.MachineName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Visible dialog acceptance requires an owned UI VM.");
        Application app = Application.Current ?? throw new InvalidOperationException("Isolated application absent.");
        nint ownerHandle = new WindowInteropHelper(owner).Handle, dialogHandle = 0;
        string stage = "starting";
        string? failedAt = null;
        Window? dialog = null;
        Exception? failure = null;
        int testCount = 0;
        var events = new List<object>();
        var hashes = new Dictionary<string, string>();
        DispatcherUnhandledExceptionEventHandler onFailure = (_, e) => { Fail(e.Exception); e.Handled = true; };
        app.DispatcherUnhandledException += onFailure;
        try
        {
            Stage("binary-identities");
            hashes["app"] = Hash(appPath); hashes["appXaml"] = Hash(appXaml);
            hashes["driver"] = Hash(typeof(FlattenScaleSmoke).Assembly.Location);
            hashes["engine"] = SelectedAssemblyIdentity.HashLoaded(Assembly.Load("KillerPdf.Engine"), appPath);
            File.WriteAllText(Path.Combine(folder, "binaries.json"), JsonSerializer.Serialize(hashes));
            Stage("show-real-dialog");
            Type type = owner.GetType().Assembly.GetType("KillerPDF.FlattenOptionsDialog", true)!;
            dialog = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [owner, 720d, 720d, ((int Width, int Height)?)(500, 500)], CultureInfo.CurrentCulture)!;
            dialog.ShowInTaskbar = false;
            dialog.Show(); dialog.Activate();
            dialogHandle = new WindowInteropHelper(dialog).Handle;
            SetForegroundWindow(dialogHandle);
            Stable();
            CheckBox keep = Descendants(dialog).OfType<CheckBox>().Single(c => c.Content is TextBlock text && Equals(text.Text, app.TryFindResource("Str_Output_KeepResolution")));
            TextBox dpi = Descendants(dialog).OfType<TextBox>().Single();
            Panel body = keep.Parent as Panel ?? throw new InvalidOperationException("Keep Resolution parent absent.");
            TextBlock label = body.Children[body.Children.IndexOf(keep) + 1] as TextBlock
                ?? throw new InvalidOperationException("Output dimension label absent after Keep Resolution.");
            Check("keep-resolution", "500", "500", "x1", true);
            Stage("turn-off-keep-resolution");
            new CheckBoxAutomationPeer(keep).GetPattern(PatternInterface.Toggle).Require<IToggleProvider>().Toggle();
            Wait(() => keep.IsChecked == false && dpi.IsEnabled);
            SetDpi("50"); Check("dpi-50", "500", "500", "x1", false);
            SetDpi("100"); Check("dpi-100", "1000", "1000", "x2", false);
            SetDpi("25"); Check("dpi-25", "250", "250", "x" + 0.5.ToString("0.0", CultureInfo.CurrentCulture), false);

            void SetDpi(string value)
            {
                Stage("set-dpi-" + value);
                new TextBoxAutomationPeer(dpi).GetPattern(PatternInterface.Value).Require<IValueProvider>().SetValue(value);
                Stable();
            }
            void Check(string name, string width, string height, string scale, bool checkedExpected)
            {
                Stage(name); Stable();
                string expected = string.Format(CultureInfo.CurrentCulture,
                    app.TryFindResource("Str_OutputPixels") as string ?? throw new InvalidOperationException("OutputPixels locale absent."), width, height) + "  |  " + scale;
                Rect labelBounds = Bounds(label), dialogBounds = Bounds(dialog);
                Rect clip = dialogBounds;
                for (DependencyObject? parent = VisualTreeHelper.GetParent(label); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                    if (parent is FrameworkElement element && (element.ClipToBounds || element is ScrollContentPresenter)) clip.Intersect(Bounds(element));
                bool visible = label.IsVisible && label.ActualWidth > 0 && label.ActualHeight > 0
                    && dialogBounds.Contains(labelBounds) && clip.Contains(labelBounds) && SystemParameters.WorkArea.Contains(labelBounds);
                bool correct = label.Text == expected && keep.IsChecked == checkedExpected && dpi.IsEnabled == !checkedExpected;
                events.Add(new { stage, expected, actual = label.Text, visible, correct, keep.IsChecked, dpi.IsEnabled,
                    labelBounds, dialogBounds, clip, dpi = dpi.Text, culture = CultureInfo.CurrentCulture.Name });
                testCount++;
                if (!visible || !correct) Fail(new LabelAssertionException("Visible Flatten output label or resolution control state differs at " + name));
                Capture(labelBounds, name + "-label"); Capture(dialogBounds, name + "-dialog");
                if (failure is not null) throw failure;
            }
        }
        catch (Exception error) { Fail(error); }
        finally
        {
            if (failure is not null) File.WriteAllText(Path.Combine(folder, "failure.txt"), failure.ToString());
            try { if (dialog is not null && IsWindow(dialogHandle)) dialog.Close(); }
            catch (Exception error) { Fail(error); }
            app.DispatcherUnhandledException -= onFailure;
            bool closed = dialogHandle != 0 && !IsWindow(dialogHandle);
            if (!closed) Fail(new InvalidOperationException("Owned Flatten dialog did not close."));
            File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new
            {
                passed = failure is null && testCount == 4, verdict = failure is null ? "GREEN_FLATTEN_SCALE" : failure is LabelAssertionException ? "RED_FLATTEN_SCALE" : "SETUP_FAILURE",
                testCount, stage, failedAt, failure = failure?.ToString(), machine = Environment.MachineName, processId = Environment.ProcessId,
                ownerHandle = ownerHandle.ToInt64(), dialogHandle = dialogHandle.ToInt64(), closed, hashes, events,
                scope = "Actual FlattenOptionsDialog with invented 720-point square page and 500-pixel square image. Public Keep Resolution toggle and DPI editor events. Captures require separate visual review; no text-pixel detector claim. No PDF opened or modified."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failure is null && testCount == 4 ? 0 : 1;

        void Stage(string value) { stage = value; File.WriteAllText(Path.Combine(folder, "stage.txt"), value); }
        void Fail(Exception error) { if (failure is null) failedAt = stage; failure ??= error; File.WriteAllText(Path.Combine(folder, "failure.txt"), failure.ToString()); }
        void Wait(Func<bool> ready)
        {
            var clock = Stopwatch.StartNew();
            while (!ready())
            {
                if (failure is not null) throw failure;
                if (app.Dispatcher.HasShutdownStarted || clock.ElapsedMilliseconds > 10000) throw new TimeoutException("Dialog readiness failed at " + stage);
                Pump(); Thread.Sleep(15);
            }
        }
        void Stable()
        {
            string last = ""; int repeats = 0;
            Wait(() => { dialog!.UpdateLayout(); string next = Bounds(dialog).ToString() + string.Join("|", Descendants(dialog).OfType<TextBlock>().Select(t => t.Text)); repeats = last == next ? repeats + 1 : 0; last = next; return repeats >= 8; });
        }
        void Capture(Rect bounds, string name)
        {
            int left = (int)Math.Ceiling(bounds.Left), top = (int)Math.Ceiling(bounds.Top);
            int width = (int)Math.Floor(bounds.Right) - left, height = (int)Math.Floor(bounds.Bottom) - top;
            if (width <= 0 || height <= 0 || !SystemParameters.WorkArea.Contains(bounds)) throw new InvalidOperationException("Invalid owned capture bounds.");
            foreach (NativePoint point in new[] { new NativePoint(left + 1, top + 1), new(left + width - 2, top + height - 2), new(left + width / 2, top + height / 2) })
                if (GetAncestor(WindowFromPoint(point), 2) != dialogHandle) throw new InvalidOperationException("Flatten capture is obscured by another window.");
            nint screen = GetDC(0), memory = 0, bitmap = 0, previous = 0;
            try
            {
                memory = CreateCompatibleDC(screen); bitmap = CreateCompatibleBitmap(screen, width, height); previous = SelectObject(memory, bitmap);
                if (screen == 0 || memory == 0 || bitmap == 0 || previous == 0 || previous == -1
                    || !BitBlt(memory, 0, 0, width, height, screen, left, top, 0x00CC0020)) throw new System.ComponentModel.Win32Exception();
                BitmapSource image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                string path = Path.Combine(folder, name + ".png"); using (var file = File.Create(path)) encoder.Save(file);
                events.Add(new { capture = name, bounds, sha256 = Hash(path), image.PixelWidth, image.PixelHeight });
            }
            finally { if (previous != 0 && previous != -1 && memory != 0) SelectObject(memory, previous); if (bitmap != 0) DeleteObject(bitmap); if (memory != 0) DeleteDC(memory); if (screen != 0) ReleaseDC(0, screen); }
        }
    }
    private sealed class LabelAssertionException(string message) : Exception(message);
    private static T Require<T>(this object? value) where T : class => value as T ?? throw new InvalidOperationException("Public automation pattern absent: " + typeof(T).Name);
    private static Rect Bounds(FrameworkElement e) => new(e.PointToScreen(new Point()), e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight)));
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dc, int x, int y, int width, int height, nint source, int sx, int sy, uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
