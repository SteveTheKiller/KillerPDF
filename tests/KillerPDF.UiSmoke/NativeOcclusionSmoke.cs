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
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Rendering;

// #534: with the native presenter up, the find bar and the page badge must cut holes in the native page window, or the
// swap chain paints over them. Hit testing respects window regions, so WindowFromPoint at a bar's center lands on the
// main window when the hole is there and on the native child when it is not. Each check has a control before and after.
internal static class NativeOcclusionSmoke
{
    internal static int Run(Window owner, string appPath, string folder)
    {
        if (Environment.MachineName is not ("SN01" or "SN02"))
            throw new InvalidOperationException("Native occlusion acceptance requires an owned VM.");
        if (NativePresenterGate.Disabled(owner)) return NativePresenterGate.Skip(owner, folder, "verify-native-occlusion");
        Directory.CreateDirectory(folder);
        byte[] original = new PdfDocumentBuilder().AddPage(800, 800, Encoding.ASCII.GetBytes("1 1 1 rg 0 0 800 800 re f"))
            .AddPage(800, 800, Encoding.ASCII.GetBytes("1 1 1 rg 0 0 800 800 re f")).Build();
        string path = Path.Combine(folder, "square.pdf");
        File.WriteAllBytes(path, original);
        string stage = "configure";
        Exception? failure = null;
        var checks = new List<object>();
        var presenters = new List<(nint Window, IPdfFramePresenter Presenter)>();
        var unexpected = new List<string>();
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
        nint owned = new WindowInteropHelper(owner).Handle;
        try
        {
            object viewer = owner.FindName("Viewer")!;
            var target = new PdfDisplayTarget("occlusion-test", "Synthetic observed mode")
            { ColorState = new(PdfDisplayColorMode.AdvancedColorSdr, 15, 1, 0, 0, 16, null, null) };
            var display = new PdfDisplayDiscoverySnapshot(80, PdfDisplayDiscoveryStatus.Available,
                "occlusion-desktop", new(0, 0, 1920, 1080), null, null)
            { TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget, [target]) };
            viewer.GetType().GetProperty("DisplayCapture", BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(viewer, (Func<nint, PdfDisplayDiscoverySnapshot>)(_ => display));
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
            Wait(() => single.IsVisible && single.IsEnabled);
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(single).GetPattern(PatternInterface.SelectionItem)).Select();
            var zoom = (ComboBox)owner.FindName("ZoomBox");
            Wait(() => zoom.IsVisible && zoom.IsEnabled);
            // Fit width on a square page fills the viewport, so every floating bar sits over the page.
            zoom.SelectedItem = zoom.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "fitwidth");
            SetForegroundWindow(owned);
            SendMessage(owned, 0x7E, 0, 0);

            stage = "native-submission";
            Wait(() => presenters.Any(item => item.Presenter.Status is
                { Mode: PdfPresentationMode.LinearFloat, LastAcceptedStamp: { PageIndex: 0 } }));
            Settle();
            checks.Add(new { stage, presenters[0].Presenter.Status });

            stage = "find-bar";
            Type shell = owner.GetType();
            FieldInfo barField = shell.GetField("_searchBar", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MainWindow._searchBar is missing; update the occlusion smoke.");
            MethodInfo toggle = shell.GetMethod("ToggleSearchBar", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MainWindow.ToggleSearchBar is missing; update the occlusion smoke.");
            toggle.Invoke(owner, null);
            Wait(() => barField.GetValue(owner) is Border { IsVisible: true, ActualWidth: > 50 });
            var bar = (Border)barField.GetValue(owner)!;
            PointI barPoint = Center(bar);
            Wait(() => Occluded(barPoint));
            checks.Add(new { stage, open = Describe(barPoint) });
            toggle.Invoke(owner, null);
            Wait(() => !bar.IsVisible);
            Wait(() => Native(barPoint));
            checks.Add(new { stage = "find-bar-closed", closed = Describe(barPoint) });

            stage = "page-badge";
            var badge = (FrameworkElement)((FrameworkElement)viewer).FindName("PageBadge");
            MethodInfo show = viewer.GetType().GetMethod("ShowPageBadgeText", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("PdfViewer.ShowPageBadgeText is missing; update the occlusion smoke.");
            show.Invoke(viewer, ["1 / 2"]);
            Wait(() => badge.IsVisible && badge.ActualWidth > 10);
            PointI badgePoint = Center(badge);
            // The badge holds for 900 ms before it fades, so the hole has to show up inside that window.
            var held = Stopwatch.StartNew();
            while (!Occluded(badgePoint))
            {
                if (held.ElapsedMilliseconds > 800)
                    throw new InvalidOperationException("Page badge is not cut out: " + Describe(badgePoint));
                Pump(); Thread.Sleep(10);
            }
            checks.Add(new { stage, shown = Describe(badgePoint), afterMs = held.ElapsedMilliseconds });
            Wait(() => !badge.IsVisible);
            Wait(() => Native(badgePoint));
            checks.Add(new { stage = "page-badge-hidden", hidden = Describe(badgePoint) });
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
                app = Hash(File.ReadAllBytes(appPath)), driver = Hash(File.ReadAllBytes(typeof(NativeOcclusionSmoke).Assembly.Location)),
                engine = SelectedAssemblyIdentity.HashLoaded(typeof(PdfRenderedFrame).Assembly, appPath)
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failure is null ? 0 : 1;

        // A hole hands the point back to the main window itself; without one it lands on a native child window.
        bool Occluded(PointI point) => WindowFromPoint(point) == owned;
        bool Native(PointI point) { nint hit = WindowFromPoint(point); return hit != owned && IsChild(owned, hit); }
        string Describe(PointI point)
        {
            nint hit = WindowFromPoint(point);
            return $"({point.X},{point.Y}) hit 0x{hit:X} owner 0x{owned:X} child {IsChild(owned, hit)}";
        }
        PointI Center(FrameworkElement element)
        {
            Point center = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
            return new((int)Math.Round(center.X), (int)Math.Round(center.Y));
        }
        void Wait(Func<bool> ready)
        {
            var watch = Stopwatch.StartNew();
            while (!ready())
            {
                if (failure is not null) throw failure;
                if (watch.ElapsedMilliseconds > 15000) throw new TimeoutException("Readiness at " + stage
                    + "; presenter statuses: " + JsonSerializer.Serialize(presenters.Select(item => item.Presenter.Status))
                    + "; status text: " + (owner.FindName("StatusText") as TextBlock)?.Text);
                Pump(); Thread.Sleep(10);
            }
        }
        void Settle() { var watch = Stopwatch.StartNew(); Wait(() => watch.ElapsedMilliseconds >= 700); }
        void SettleClosing()
        { var watch = Stopwatch.StartNew(); while (owner.IsVisible && watch.ElapsedMilliseconds < 3000) { Pump(); Thread.Sleep(10); } }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Pump()
    { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return child; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct PointI(int X, int Y);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(PointI point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
