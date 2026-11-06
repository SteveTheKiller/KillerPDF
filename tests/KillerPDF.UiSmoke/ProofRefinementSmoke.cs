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

internal static class ProofRefinementSmoke
{
    internal static int Run(Window owner, string appPath, string folder, bool nativePresenter = false)
    {
        if (Environment.MachineName is not ("SN01" or "SN02"))
            throw new InvalidOperationException("Proof refinement requires an owned VM.");
        Directory.CreateDirectory(folder);
        SelectedAssemblyIdentity.HashLoaded(typeof(PdfDocumentBuilder).Assembly, appPath);
        var paint = new StringBuilder("1 1 1 rg 0 0 600 1200 re f 0 0 0 rg ");
        for (int x = 0; x < 600; x += 20)
            paint.Append(x.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" 0 10 1200 re f ");
        byte[] original = new PdfDocumentBuilder().AddPage(600, 1200, Encoding.ASCII.GetBytes(paint.ToString())).Build();
        string path = Path.Combine(folder, "proof-stripes.pdf");
        File.WriteAllBytes(path, original);
        string stage = "configure";
        Exception? failure = null;
        var events = new List<object>();
        var submissions = new List<SubmittedStripeColors>();
        var modals = new List<string>();
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object viewer = owner.GetType().GetProperty("ActiveViewer", instance)!.GetValue(owner)!;
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
            var target = new PdfDisplayTarget("proof-region-output", "Synthetic SDR display")
            {
                ColorState = new(nativePresenter ? PdfDisplayColorMode.AdvancedColorSdr : PdfDisplayColorMode.ConventionalSdr,
                    15, 1, 0, 0, nativePresenter ? 16u : 8u, null, null),
                StandardProfile = new(PdfDisplayProfileStatus.NoProfile, 0, null, null, null)
            };
            var display = new PdfDisplayDiscoverySnapshot(92, PdfDisplayDiscoveryStatus.Available,
                "proof-region-desktop", new(0, 0, 1920, 1080), null, null)
                { TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget, [target]) };
            viewer.GetType().GetProperty("DisplayCapture")!.SetValue(viewer,
                (Func<nint, PdfDisplayDiscoverySnapshot>)(_ => display));
            if (nativePresenter)
                viewer.GetType().GetProperty("FramePresenterFactory")!.SetValue(viewer,
                    (Func<nint, IPdfFramePresenter>)(_ => new StripePresenter(submission => submissions.Add(submission))));
            timer.Start();
            stage = "open";
            owner.GetType().GetMethod("OpenFromExternal")!.Invoke(owner, [path]);
            Wait(() => ((TextBlock)owner.FindName("FileNameLabel")).Text == Path.GetFileName(path));
            var single = (RadioButton)owner.FindName("ViewSingleRadio");
            Wait(() => single.IsVisible && single.IsEnabled);
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(single).GetPattern(PatternInterface.SelectionItem)!).Select();
            Wait(() => PrimaryReady() && (nativePresenter
                ? submissions.Any(item => item.Stamp.RegionId == 1) : BaseImage() is not null));
            stage = "normal-control";
            var normal = nativePresenter ? submissions.Last(item => item.Stamp.RegionId == 1).Normal
                : StripeColors((BitmapSource)BaseImage()!.Source, proof: false);
            var absent = nativePresenter ? submissions.Last(item => item.Stamp.RegionId == 1).Proof
                : StripeColors((BitmapSource)BaseImage()!.Source, proof: true);
            events.Add(new { stage, normal, absent });
            RequireStripeColors(normal);
            if (absent.Paper != 0 || absent.Ink != 0)
                throw new InvalidOperationException("Normal stripes passed the proof-color absence control.");
            if (nativePresenter)
            {
                Wait(() => submissions.Any(item => item.Stamp.RegionId == 1));
                var initial = submissions.Last(item => item.Stamp.RegionId == 1);
                RequireStripeColors(initial.Normal);
                if (initial.Proof.Paper != 0 || initial.Proof.Ink != 0)
                    throw new InvalidOperationException("Normal primary presenter frame contains proof colors.");
            }
            stage = "proof-base";
            var view = (PdfDocumentProofView)viewer.GetType().GetProperty("DocumentProofView")!.GetValue(viewer)!;
            view.Document.Proof = new PdfDocumentProofSettings(enabled: true,
                printerProfile: PdfColorProfile.Load(NativeReadingSmoke.ProofPrinterFixture()),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulatePaper = true, SimulateBlack = true });
            int previousSubmissionCount = submissions.Count;
            viewer.GetType().GetMethod("RenderActiveSessionExt", instance)!.Invoke(viewer, null);
            Wait(() => PrimaryReady() && (nativePresenter
                ? submissions.Count > previousSubmissionCount : BaseImage() is not null));
            var proofBase = nativePresenter ? submissions.Last(item => item.Stamp.RegionId == 1).Proof
                : StripeColors((BitmapSource)BaseImage()!.Source, proof: true);
            events.Add(new { stage, proofBase });
            RequireStripeColors(proofBase);
            if (nativePresenter)
                RequireStripeColors(submissions.Last(item => item.Stamp.RegionId == 1).Proof);
            stage = "proof-region-setup";
            var zoom = (ComboBox)owner.FindName("ZoomBox");
            Wait(() => zoom.IsVisible && zoom.IsEnabled);
            zoom.IsDropDownOpen = true;
            var zoom200 = zoom.Items.OfType<ComboBoxItem>().Single(item => item.Tag as string == "2.0");
            Wait(() => zoom200.IsVisible && zoom200.IsEnabled);
            zoom.SelectedItem = zoom200;
            zoom.IsDropDownOpen = false;
            if (nativePresenter)
            {
                Wait(() => PrimaryReady() && submissions.Any(item => item.Stamp.RegionId == 2)
                    && (Task)viewer.GetType().GetProperty("VisibleRefinementTask", instance)!.GetValue(viewer)!
                        is { IsCompletedSuccessfully: true });
                stage = "proof-presenter-region";
                var primary = submissions.Last(item => item.Stamp.RegionId == 1);
                var region = submissions.Last(item => item.Stamp.RegionId == 2);
                events.Add(new { stage, primary, region, recordingBoundaryOnly = true });
                RequireStripeColors(region.Proof);
                if (region.Stamp.ProofGeneration <= 0 || region.Stamp.ProofGeneration != primary.Stamp.ProofGeneration
                    || region.Stamp.DocumentId != primary.Stamp.DocumentId
                    || region.Stamp.PageIndex != primary.Stamp.PageIndex)
                    throw new InvalidOperationException("Detail presenter frame does not retain the primary proof identity.");
            }
            else
            {
                Wait(() => PrimaryReady() && DetailImage() is not null
                    && (Task)viewer.GetType().GetProperty("VisibleRefinementTask", instance)!.GetValue(viewer)! is { IsCompletedSuccessfully: true });
                stage = "proof-region";
                Image detail = DetailImage()!;
                var proofDetail = StripeColors((BitmapSource)detail.Source, proof: true);
                var normalDetail = StripeColors((BitmapSource)detail.Source, proof: false);
                events.Add(new { stage, proofDetail, normalDetail, detail.ActualWidth, detail.ActualHeight });
                RequireStripeColors(proofDetail);
                var scroll = (ScrollViewer)((FrameworkElement)viewer).FindName("PagePreviewPanel");
                var clip = Descendants<ScrollContentPresenter>(scroll).Single(element => element.IsVisible);
                Rect bounds = Rect.Intersect(Bounds(detail), Bounds(clip));
                Wait(() => StripeColors(NativeReadingSmoke.Capture(owner, bounds), proof: true) is var colors
                    && colors.Paper > colors.Pixels / 5 && colors.Ink > colors.Pixels / 5);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(NativeReadingSmoke.Capture(owner, bounds)));
                using (var output = File.Create(Path.Combine(folder, "proof-region-screen.png"))) encoder.Save(output);
                events.Add(new { stage, visibleProofColors = true, sourcePreserved = true });
            }
        }
        catch (Exception error)
        {
            failure = error;
            try
            {
                var zoom = owner.FindName("ZoomBox") as ComboBox;
                var viewport = ((FrameworkElement)viewer).FindName("PagePreviewPanel") as ScrollViewer;
                events.Add(new { stage, diagnostic = "refinement-readiness", zoom = zoom?.Text,
                    selectedZoom = (zoom?.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                    primary = (viewer.GetType().GetProperty("PrimaryRenderTask", instance)!.GetValue(viewer) as Task)?.Status.ToString(),
                    refinement = (viewer.GetType().GetProperty("VisibleRefinementTask", instance)!.GetValue(viewer) as Task)?.Status.ToString(),
                    refineError = viewer.GetType().GetProperty("LastVisibleRefinementError", instance)!.GetValue(viewer)?.ToString(),
                    viewport = viewport is null ? null : new { viewport.ViewportWidth, viewport.ViewportHeight,
                        viewport.ExtentWidth, viewport.ExtentHeight, viewport.HorizontalOffset, viewport.VerticalOffset },
                    images = Descendants<Image>((DependencyObject)viewer).Where(image => image.Source is BitmapSource)
                        .Select(image => new { image.IsVisible, image.ActualWidth, image.ActualHeight,
                            z = Panel.GetZIndex(image), image.ToolTip, pixelsWide = ((BitmapSource)image.Source).PixelWidth,
                            pixelsHigh = ((BitmapSource)image.Source).PixelHeight }).ToArray() });
            }
            catch (Exception diagnostic) { events.Add(new { stage, diagnostic = diagnostic.ToString() }); }
        }
        finally
        {
            timer.Stop();
            bool preserved = File.ReadAllBytes(path).SequenceEqual(original);
            bool passed = failure is null && modals.Count == 0 && preserved;
            File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new
            {
                passed, stage, preserved, machine = Environment.MachineName, error = failure?.ToString(), modals, events, submissions,
                appHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)))
            }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        }
        return failure is null && modals.Count == 0 && File.ReadAllBytes(path).SequenceEqual(original) ? 0 : 1;

        bool PrimaryReady() => (Task)viewer.GetType().GetProperty("PrimaryRenderTask", instance)!.GetValue(viewer)!
            is { IsCompletedSuccessfully: true };
        Image? BaseImage() => Descendants<Image>((DependencyObject)viewer).FirstOrDefault(image => image.IsVisible
            && image.Source is BitmapSource { PixelWidth: > 100, PixelHeight: > 100 }
            && image.Parent is Grid grid && ReferenceEquals(grid.Children[0], image));
        Image? DetailImage() => Descendants<Image>((DependencyObject)viewer).FirstOrDefault(image => image.IsVisible
            && image.Source is BitmapSource && Panel.GetZIndex(image) == -1);
        void Wait(Func<bool> ready)
        {
            var wait = Stopwatch.StartNew();
            while (true)
            {
                owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                if (modals.Count > 0) throw new InvalidOperationException("Unexpected modal at " + stage);
                if (ready()) return;
                if (wait.Elapsed > TimeSpan.FromSeconds(30) || deadline.Elapsed > TimeSpan.FromSeconds(90))
                    throw new TimeoutException("Proof refinement deadline expired at " + stage);
                Thread.Sleep(25);
            }
        }
    }

    private sealed record SubmittedStripeColors(PdfPresentationStamp Stamp, int Width, int Height,
        (int Paper, int Ink, int Pixels) Normal, (int Paper, int Ink, int Pixels) Proof);

    private sealed class StripePresenter(Action<SubmittedStripeColors> record) : IPdfFramePresenter
    {
        private PdfPresentationState? _state;
        public PdfPresentationStatus Status => new(PdfPresentationMode.LinearFloat,
            PdfMonitorTransformOwner.Compositor, _state?.Stamp, "RecordingBoundaryOnly", null);
        public void UpdateState(PdfPresentationState state) => _state = state;
        public ValueTask<PdfFrameSubmissionResult> SubmitAsync(PdfFrameSubmission submission,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (submission.Stamp != _state?.Stamp)
                return ValueTask.FromResult(new PdfFrameSubmissionResult(PdfFrameSubmissionOutcome.Stale, Status));
            byte[] pixels = submission.Frame.ToSrgbBgra32(cancellationToken);
            var bitmap = BitmapSource.Create(submission.Frame.Width, submission.Frame.Height, 96, 96,
                PixelFormats.Bgra32, null, pixels, submission.Frame.Width * 4);
            record(new(submission.Stamp, submission.Frame.Width, submission.Frame.Height,
                StripeColors(bitmap, false), StripeColors(bitmap, true)));
            return ValueTask.FromResult(new PdfFrameSubmissionResult(
                submission.Stamp.RegionId == 1 ? PdfFrameSubmissionOutcome.Submitted : PdfFrameSubmissionOutcome.Unavailable, Status));
        }
        public void Dispose() { }
    }

    internal static void RequireStripeColors((int Paper, int Ink, int Pixels) colors)
    {
        if (colors.Paper <= colors.Pixels / 5 || colors.Ink <= colors.Pixels / 5)
            throw new InvalidOperationException("The page region does not retain the expected paper and ink colors.");
    }
    internal static (int Paper, int Ink, int Pixels) StripeColors(BitmapSource source, bool proof)
    {
        var image = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] bytes = new byte[checked(image.PixelWidth * image.PixelHeight * 4)];
        image.CopyPixels(bytes, image.PixelWidth * 4, 0);
        int[] paper = proof ? [236, 251, 219] : [255, 255, 255];
        int[] ink = proof ? [91, 97, 84] : [0, 0, 0];
        int paperCount = 0, inkCount = 0;
        for (int offset = 0; offset < bytes.Length; offset += 4)
        {
            if (Math.Abs(bytes[offset] - paper[0]) <= 2 && Math.Abs(bytes[offset + 1] - paper[1]) <= 2
                && Math.Abs(bytes[offset + 2] - paper[2]) <= 2) paperCount++;
            if (Math.Abs(bytes[offset] - ink[0]) <= 2 && Math.Abs(bytes[offset + 1] - ink[1]) <= 2
                && Math.Abs(bytes[offset + 2] - ink[2]) <= 2) inkCount++;
        }
        return (paperCount, inkCount, bytes.Length / 4);
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
