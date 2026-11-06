using System.Diagnostics;
using System.IO;
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
    internal static int Run(Window owner, string appPath, string folder, bool continuous = false, bool pairedContinuous = false)
    {
        if (Environment.MachineName is not ("SN01" or "SN02"))
            throw new InvalidOperationException("Two-page proof checks require an owned VM.");
        SelectedAssemblyIdentity.HashLoaded(typeof(PdfDocumentBuilder).Assembly, appPath);
        Directory.CreateDirectory(folder);
        var paint = new StringBuilder("1 1 1 rg 0 0 600 750 re f 0 0 0 rg ");
        for (int x = 0; x < 600; x += 20)
            paint.Append(x.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" 0 10 750 re f ");
        byte[] content = Encoding.ASCII.GetBytes(paint.ToString());
        byte[] original = new PdfDocumentBuilder().AddPage(600, 750, content).AddPage(600, 750, content).Build();
        string path = Path.Combine(folder, "two-page-proof.pdf");
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
                modals.Add(modal.Title);
                modal.Close();
            }
        };
        try
        {
            var target = new PdfDisplayTarget("two-page-proof-output", "Synthetic SDR display")
            {
                ColorState = new(PdfDisplayColorMode.ConventionalSdr, 15, 1, 0, 0, 8, null, null),
                StandardProfile = new(PdfDisplayProfileStatus.NoProfile, 0, null, null, null)
            };
            var display = new PdfDisplayDiscoverySnapshot(93, PdfDisplayDiscoveryStatus.Available,
                "two-page-proof-desktop", new(0, 0, 1920, 1080), null, null)
                { TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget, [target]) };
            viewer.GetType().GetProperty("DisplayCapture")!.SetValue(viewer,
                (Func<nint, PdfDisplayDiscoverySnapshot>)(_ => display));
            timer.Start();
            stage = "open";
            owner.GetType().GetMethod("OpenFromExternal")!.Invoke(owner, [path]);
            Wait(() => ((TextBlock)owner.FindName("FileNameLabel")).Text == Path.GetFileName(path));
            var radio = (RadioButton)owner.FindName(pairedContinuous ? "ViewTwoPageContinuousRadio"
                : continuous ? "ViewContinuousRadio" : "ViewTwoPageRadio");
            Wait(() => radio.IsVisible && radio.IsEnabled);
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(radio).GetPattern(PatternInterface.SelectionItem)!).Select();
            Wait(() => PrimaryReady() && Pages().Length == 2);
            stage = "normal-two-page-control";
            CheckPages(false);
            var view = (PdfDocumentProofView)viewer.GetType().GetProperty("DocumentProofView")!.GetValue(viewer)!;
            view.Document.Proof = new PdfDocumentProofSettings(enabled: true,
                printerProfile: PdfColorProfile.Load(NativeReadingSmoke.ProofPrinterFixture()),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulatePaper = true, SimulateBlack = true });
            stage = "proof-two-page";
            viewer.GetType().GetMethod("RenderActiveSessionExt", members)!.Invoke(viewer, null);
            Wait(() => PrimaryReady() && Pages().Length == 2);
            CheckPages(true);
            CheckScreens(true);
            stage = "proof-off-two-page";
            view.Document.Proof = new PdfDocumentProofSettings();
            viewer.GetType().GetMethod("RenderActiveSessionExt", members)!.Invoke(viewer, null);
            Wait(() => PrimaryReady() && Pages().Length == 2);
            CheckPages(false);
            CheckScreens(false);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            timer.Stop();
            bool preserved = File.ReadAllBytes(path).SequenceEqual(original);
            File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new
            {
                passed = failure is null && modals.Count == 0 && preserved, stage, preserved,
                mode = pairedContinuous ? "TwoPageContinuous" : continuous ? "Continuous" : "TwoPage",
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
                selected = ProofRefinementSmoke.StripeColors((BitmapSource)item.Image.Source, proof),
                other = ProofRefinementSmoke.StripeColors((BitmapSource)item.Image.Source, !proof) }).ToArray();
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
                Wait(() => ProofRefinementSmoke.StripeColors(NativeReadingSmoke.Capture(owner, bounds), proof) is var colors
                    && colors.Paper > colors.Pixels / 5 && colors.Ink > colors.Pixels / 5);
                var capture = NativeReadingSmoke.Capture(owner, bounds);
                var selected = ProofRefinementSmoke.StripeColors(capture, proof);
                var other = ProofRefinementSmoke.StripeColors(capture, !proof);
                if (other.Paper != 0 || other.Ink != 0)
                    throw new InvalidOperationException("A visible facing page retains the other proof state's colors.");
                events.Add(new { stage, page.Page, screen = true, selected, other });
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(capture));
                using var output = File.Create(Path.Combine(folder, stage + "-page-" + page.Page + ".png"));
                encoder.Save(output);
            }
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
