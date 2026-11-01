using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

internal static class RenderRecoverySmoke
{
    internal static int Run(Window owner, string appPath, string folder)
    {
        if (Environment.GetCommandLineArgs().Any(argument => argument is "--verify-two-page-fit-after-mode-switch" or "--verify-two-page-zoom-after-mode-switch"))
            return VerifyTwoPageModeSwitch(owner, appPath, folder);
        if (Environment.GetCommandLineArgs().Any(argument => argument is "--verify-dirty-open-viewport" or "--verify-cached-fit-zoom-pending"
            || argument.StartsWith("--verify-cross-mode-", StringComparison.Ordinal) || argument == "--verify-zoom-close-writer"))
            return VerifyDirtyOpenViewport(owner, appPath, folder);
        if (Environment.GetCommandLineArgs().Contains("--verify-japanese-selection", StringComparer.Ordinal))
            return JapaneseSelectionSmoke.Run(owner, appPath, folder);
        if (Environment.GetCommandLineArgs().Contains("--verify-undo-storage-failure", StringComparer.Ordinal))
            return UndoFailureSmoke.Run(owner, appPath, folder);
        if (Environment.GetCommandLineArgs().Contains("--verify-signature-keyboard", StringComparer.Ordinal))
            return SignatureKeyboardSmoke.Run(owner, appPath, folder);
        if (Environment.GetCommandLineArgs().Contains("--verify-multiwindow-recovery", StringComparer.Ordinal))
            return VerifyMultiwindowRecovery(owner, appPath, folder);
        bool pendingOnly = Environment.GetCommandLineArgs().Contains("--verify-pending-text-recovery", StringComparer.Ordinal);
        if (!pendingOnly) VerifyIncompleteRecovery(owner, folder);
        foreach (bool existing in new[] { false, true })
        {
            string mode = existing ? "existing-page-text" : "new-annotation";
            string source = Path.Combine(folder, mode + ".pdf");
            byte[] fixture = Fixture();
            File.WriteAllBytes(source, fixture);
            Open(owner, source);
            var viewer = (FrameworkElement)owner.FindName("Viewer");
            Canvas page = Children(viewer).OfType<Canvas>().First(canvas => Equals(canvas.Tag, 0) && canvas.Width > 0);
            ((Button)owner.FindName("ToolTextBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            string entry = existing ? "BeginTextEditAtPosition" : "PlaceTextAtPosition";
            viewer.GetType().GetMethod(entry, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(viewer, [new Point(page.Width * .18, page.Height * (existing ? .15 : .5)), 0]);
            Wait(() => Children(viewer).OfType<TextBox>().Any(box => box.AcceptsReturn && Equals(box.Tag, 0)), "page editor");
            TextBox editor = Children(viewer).OfType<TextBox>().Single(box => box.AcceptsReturn && Equals(box.Tag, 0));
            if (existing && !editor.Text.Contains("ORIGINAL BODY TEXT", StringComparison.Ordinal))
                throw new InvalidOperationException("The real page-text editor did not contain the source line.");
            if (!existing) editor.Width = page.Width * .75;
            editor.Focus();
            editor.SelectAll();
            string expected = existing ? "RECOVERED EXISTING TEXT" : "RECOVERED PENDING ANNOTATION";
            editor.SelectedText = expected;
            if (editor.Text != expected) throw new InvalidOperationException("The live editor did not accept the pending edit.");

            string? manifest = (string?)owner.GetType()
                .GetMethod("PrepareRenderFailureRecovery", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, null);
            if (manifest is null) throw new InvalidOperationException(mode + ": recovery skipped the clean tab with pending text.");
            using JsonDocument recovery = JsonDocument.Parse(File.ReadAllBytes(manifest));
            string recovered = recovery.RootElement.EnumerateArray()
                .Single(item => item.GetProperty("OriginalPath").GetString() == source)
                .GetProperty("RecoveredPath").GetString()!;
            string actual = Extract(File.ReadAllBytes(recovered));
            if (!actual.Contains(expected, StringComparison.Ordinal)
                || existing && actual.Contains("ORIGINAL BODY TEXT", StringComparison.Ordinal))
                throw new InvalidOperationException(mode + ": saved recovery omitted or duplicated pending text: " + actual);
            if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(fixture))
                throw new InvalidOperationException("Recovery changed the original file.");
            CloseWithoutSaving(owner);
            Open(owner, recovered);
            try
            {
                Wait(() => string.Join(" ", ReadDocument(owner).Split((char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries)).Contains(expected, StringComparison.Ordinal),
                    "reopened recovery text provider");
            }
            catch (TimeoutException error)
            {
                throw new InvalidOperationException("Reopened recovery provider text: " + ReadDocument(owner), error);
            }
            CloseWithoutSaving(owner);
            Console.WriteLine("PASS pending " + mode + ", recovery output, original preservation and reopened provider text.");
        }
        string result = (pendingOnly ? "PASS " : "PASS filesystem-denied save retains working bytes and edit state with an incomplete manifest; ")
            + "pending new annotation and existing page-text recovery through live editors, public recovery entry, saved PDF text and reopened UIA provider. Offscreen WPF only; no native crash or desktop input. App SHA256="
            + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
        File.WriteAllText(Path.Combine(folder, "result.txt"), result);
        return 0;
    }

    private static int VerifyTwoPageModeSwitch(Window owner, string appPath, string folder)
    {
        bool fit = Environment.GetCommandLineArgs().Contains("--verify-two-page-fit-after-mode-switch", StringComparer.Ordinal);
        string source = Path.Combine(folder, "mixed-page-mode-switch.pdf");
        byte[] original = MixedPageFixture();
        File.WriteAllBytes(source, original);
        var viewer = (FrameworkElement)owner.FindName("Viewer");
        var pages = (ListBox)owner.FindName("PageList");
        var jump = (TextBox)owner.FindName("PageJumpBox");
        var zoom = (ComboBox)owner.FindName("ZoomBox");
        ScrollViewer panel = Children(viewer).OfType<ScrollViewer>().Single(scroll => scroll.Name == "PagePreviewPanel");
        const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object state = viewer.GetType().GetProperty("State", members)!.GetValue(viewer)!;
        PropertyInfo primary = viewer.GetType().GetProperty("PrimaryRenderTask", members)!;
        string stage = "open Continuous";
        double? rightWidth = null, rightHeight = null, spreadWidth = null, spreadHeight = null;
        try
        {
            owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Instance | BindingFlags.Public)!.Invoke(owner, [source]);
            Wait(() => pages.Items.Count == 5, "mode-switch fixture opened");
            SelectMode("Continuous");
            pages.SelectedIndex = 1;
            Wait(() => jump.Text == "2", "Continuous right page selected");
            SettleSelectedRaster();
            stage = "switch to TwoPage";
            SelectMode("TwoPage");
            pages.SelectedIndex = 1;
            Wait(() => jump.Text == "2", "TwoPage right page remains selected");
            SettleSelectedRaster();
            stage = "public " + (fit ? "FitPage" : "200%") + " preset";
            zoom.SelectedItem = zoom.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, fit ? "fitpage" : "2.0"));
            SettleSelectedRaster();
            Canvas right = SelectedCanvas(1);
            Rect rightBounds = right.TransformToAncestor(panel).TransformBounds(new Rect(right.RenderSize));
            rightWidth = rightBounds.Width; rightHeight = rightBounds.Height;
            Canvas left = SelectedCanvas(0);
            Rect spread = left.TransformToAncestor(panel).TransformBounds(new Rect(left.RenderSize));
            spread.Union(rightBounds);
            spreadWidth = spread.Width; spreadHeight = spread.Height;
            Console.WriteLine("CONTROL mode-switch " + (fit ? "FitPage" : "200%") + ": right=" + rightWidth + "x" + rightHeight
                + ", spread=" + spreadWidth + "x" + spreadHeight + ", viewport=" + panel.ViewportWidth + "x" + panel.ViewportHeight
                + ", label=" + zoom.Text);
            if (fit)
            {
                if (!Equals((zoom.SelectedItem as ComboBoxItem)?.Tag, "fitpage")
                    || spread.Width > panel.ViewportWidth + 2 || spread.Height > panel.ViewportHeight + 2)
                    throw new InvalidOperationException("FitPage after the mode switch does not fit the actual spread inside its public viewport.");
            }
            else if (zoom.Text != "200%" || Math.Abs(rightBounds.Width - 800) > 2)
                throw new InvalidOperationException("The mode-switch 200% preset rendered the 300pt right page at width " + rightBounds.Width + " DIP instead of 800 DIP.");
            if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(original))
                throw new InvalidOperationException("Mode-switch zoom changed the source PDF bytes.");
            stage = "passed";
            return 0;
        }
        finally
        {
            File.WriteAllText(Path.Combine(folder, "mode-switch-zoom-result.json"), JsonSerializer.Serialize(new
            {
                Stage = stage, Mode = fit ? "FitPage" : "200%", ActualZoom = zoom.Text,
                ActualSelectedTag = (zoom.SelectedItem as ComboBoxItem)?.Tag?.ToString(), Page = jump.Text,
                RightWidth = rightWidth, RightHeight = rightHeight, SpreadWidth = spreadWidth, SpreadHeight = spreadHeight,
                panel.ViewportWidth, panel.ViewportHeight, PanelActualWidth = panel.ActualWidth, PanelActualHeight = panel.ActualHeight,
                ExpectedRightWidth = fit ? (double?)null : 800, ToleranceDip = 2,
                SourceUnchanged = File.ReadAllBytes(source).AsSpan().SequenceEqual(original),
                AppSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)))
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        Canvas SelectedCanvas(int page) => Children(viewer).OfType<Canvas>().Single(canvas => canvas.IsVisible
            && Equals(canvas.Tag, page) && canvas.RenderSize.Width > 0 && canvas.Parent is Grid tile
            && tile.Children.OfType<Image>().Any(image => image.Source is System.Windows.Media.Imaging.BitmapSource));

        void SelectMode(string mode)
        {
            var radio = (RadioButton)owner.FindName("View" + mode + "Radio");
            Wait(() => radio.IsEnabled && panel.IsVisible && panel.ActualWidth > 0, "mode-switch public controls ready");
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(radio).GetPattern(PatternInterface.SelectionItem)!).Select();
            Wait(() => radio.IsChecked == true && state.GetType().GetField("Mode")!.GetValue(state)?.ToString() == mode
                && state.GetType().GetField("Pending")!.GetValue(state) is null && panel.IsVisible && panel.Opacity > .99,
                "actual " + mode + " mode and completed fade");
        }

        void SettleSelectedRaster()
        {
            Task render = (Task)primary.GetValue(viewer)!;
            Wait(() => render.IsCompleted, "mode-switch primary render complete");
            render.GetAwaiter().GetResult();
            Wait(() => Children(viewer).OfType<Canvas>().Any(canvas => canvas.IsVisible && Equals(canvas.Tag, 1)
                && canvas.RenderSize.Width > 0 && canvas.Parent is Grid tile
                && tile.Children.OfType<Image>().Any(image => image.Source is System.Windows.Media.Imaging.BitmapSource)),
                "mode-switch actual right page image");
            double lastWidth = -1, lastHeight = -1;
            var stable = Stopwatch.StartNew();
            Wait(() =>
            {
                if (!panel.IsMeasureValid || !panel.IsArrangeValid || Math.Abs(panel.ActualWidth - lastWidth) > .01
                    || Math.Abs(panel.ActualHeight - lastHeight) > .01)
                {
                    lastWidth = panel.ActualWidth; lastHeight = panel.ActualHeight; stable.Restart(); return false;
                }
                return stable.Elapsed >= TimeSpan.FromMilliseconds(150);
            }, "mode-switch public viewport stable");
            owner.UpdateLayout(); viewer.UpdateLayout(); Pump();
        }
    }

    private static int VerifyDirtyOpenViewport(Window owner, string appPath, string folder)
    {
        string scenario = Environment.GetCommandLineArgs().First(argument => argument is "--verify-dirty-open-viewport" or "--verify-cached-fit-zoom-pending"
            || argument.StartsWith("--verify-cross-mode-", StringComparison.Ordinal) || argument == "--verify-zoom-close-writer");
        bool crossMode = scenario != "--verify-dirty-open-viewport";
        bool reverse = scenario == "--verify-cross-mode-zoom-reverse";
        bool twoPageFit = scenario == "--verify-cross-mode-fit-page-twopage-pending";
        bool twoPage = scenario == "--verify-cross-mode-zoom-twopage" || twoPageFit;
        bool comparisonFit = scenario == "--verify-cross-mode-zoom-compare-fit";
        bool grid = scenario == "--verify-cross-mode-grid";
        bool clamp = scenario == "--verify-cross-mode-zoom-clamp";
        bool closeWindow = scenario == "--verify-zoom-close-writer";
        bool reassert = scenario == "--verify-cross-mode-zoom-reassert";
        bool navigate = scenario == "--verify-cross-mode-zoom-navigate";
        bool wheel = scenario == "--verify-cross-mode-zoom-wheel";
        bool button = scenario == "--verify-cross-mode-zoom-button";
        bool sequence = scenario == "--verify-cross-mode-zoom-sequence";
        bool cachedFit = scenario == "--verify-cached-fit-zoom-pending";
        bool fitThenZoom = scenario == "--verify-cross-mode-fit-zoom-pending" || cachedFit;
        bool zoomThenFit = scenario == "--verify-cross-mode-zoom-fit-pending";
        string? pendingFit = scenario == "--verify-cross-mode-fit-page-pending" || twoPageFit ? "fitpage"
            : scenario == "--verify-cross-mode-fit-width-pending" || fitThenZoom || zoomThenFit ? "fitwidth" : null;
        string? expectedFitTag = fitThenZoom ? null : pendingFit;
        bool measuredControl = button || sequence || pendingFit is not null;
        bool timedCommand = !cachedFit && (reassert || navigate || wheel || measuredControl || comparisonFit);
        string? fit = scenario == "--verify-cross-mode-fit-page" ? "fitpage"
            : scenario == "--verify-cross-mode-fit-width" ? "fitwidth" : null;
        string initialMode = reverse || !crossMode ? "Single" : "Continuous";
        string reopenedMode = reverse ? "Continuous" : twoPage ? "TwoPage" : grid ? "Grid" : "Single";
        string expectedZoom = clamp ? "38%" : wheel ? "220%" : "200%";
        string source = Path.Combine(folder, "remembered-viewport.pdf");
        string other = Path.Combine(folder, "different-view.pdf");
        byte[] original = twoPage ? MixedPageFixture() : PageDeletionSmoke.Fixture();
        File.WriteAllBytes(source, original);
        if (crossMode) File.WriteAllBytes(other, original);
        var pages = (ListBox)owner.FindName("PageList");
        var zoom = (ComboBox)owner.FindName("ZoomBox");
        var jump = (TextBox)owner.FindName("PageJumpBox");
        var viewer = (FrameworkElement)owner.FindName("Viewer");
        int expectedPage = twoPage ? 2 : fit is not null || grid || clamp ? 1 : 4;
        string stage = "initial open";
        bool timingTriggerScheduled = false, timingTriggerFired = false;
        bool pendingAtTimingFire = false;
        bool? primaryCompletedBeforeTimingCommand = null, primaryCompletedAfterTimingCommand = null;
        long? generationBeforeTimingCommand = null, generationAfterTimingCommand = null;
        double? actualCanvasScreenDipWidth = null;
        string? settledButtonControlZoom = null;
        double? settledButtonControlWidth = null;
        double? expectedCanvasScreenDipWidth = fit is null && !grid && !clamp
            ? (twoPage ? 800.0 : 200.0 * 96 / 72 * (wheel ? 2.2 : 2)) : null;
        SelectionChangedEventHandler? timingHandler = null;
        System.ComponentModel.DependencyPropertyDescriptor? cachedImageDescriptor = null;
        EventHandler? cachedImageHandler = null;
        Image? cachedImage = null;
        bool cachedPageWarmed = false, cachedImageNullAtCommand = false, cachedPendingAfterFit = false;
        long? cachedGenerationBeforeNavigation = null;
        Window? comparisonWindow = null;
        FrameworkElement? comparisonSourceViewer = null;
        double? comparisonSourceOwnerWidth = null, comparisonSourceOwnerHeight = null;
        object? originalConvertedFitGeometry = null, sourceFitGeometry = null, targetFitGeometry = null, targetRepeatedFitGeometry = null;
        object? comparisonInvocationConditions = null;
        bool comparisonCriticalSeamReached = false;
        string? originalGeometryFailureZoom = null;
        double? sourcePanelWidth = null, sourcePanelHeight = null;
        bool? equalSourceTargetPanel = null;
        try
        {
            OpenDocument();
            Wait(() => pages.Items.Count == 5, "five-page viewport fixture");
            SelectMode(initialMode);
            pages.SelectedIndex = expectedPage - 1;
            Wait(() => jump.Text == expectedPage.ToString(), "public target-page selection");
            Wait(() => Children(viewer).OfType<Canvas>().Any(canvas => Equals(canvas.Tag, expectedPage - 1) && canvas.Width > 0),
                "target page rendered before setting zoom");
            SettleRender("before manual preset");
            ComboBoxItem preset = zoom.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, fit ?? (clamp ? "0.5" : "2.0")));
            zoom.SelectedItem = preset;
            Wait(() => fit is not null ? Equals((zoom.SelectedItem as ComboBoxItem)?.Tag, fit)
                : zoom.Text == (clamp ? "50%" : "200%"), "public zoom preset");
            if (clamp)
            {
                var zoomOut = (Button)owner.FindName("ZoomOutBtn");
                zoomOut.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                zoomOut.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Wait(() => zoom.Text == "20%", "public low-zoom clamp fixture");
            }
            SettleRender("after manual preset, before close");
            if (fit is null && zoom.Text != (clamp ? "20%" : "200%"))
                throw new InvalidOperationException("The manual zoom preset did not survive render readiness.");
            Console.WriteLine("CONTROL public viewport before close: page=" + jump.Text + ", zoom=" + zoom.Text);
            stage = "clean document close";
            if (closeWindow)
            {
                owner.GetType().Assembly.GetType("KillerPDF.App", true)!
                    .GetMethod("SetSetting", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
                    .Invoke(null, ["RememberChoiceLocked", "1"]);
                Type windowType = owner.GetType();
                owner.Close(); Pump();
                owner = (Window)Activator.CreateInstance(windowType)!;
                owner.Show(); Pump();
                pages = (ListBox)owner.FindName("PageList");
                zoom = (ComboBox)owner.FindName("ZoomBox");
                jump = (TextBox)owner.FindName("PageJumpBox");
                viewer = (FrameworkElement)owner.FindName("Viewer");
                Console.WriteLine("CONTROL source closed through the owning window settings writer.");
            }
            else
            {
                CloseWithoutSaving(owner);
                Wait(() => pages.Items.Count == 0, "clean viewport document closed");
            }
            if (crossMode)
            {
                stage = "global mode change through a second document";
                OpenDocument(other);
                SelectMode(reopenedMode);
                SettleRender("second document in " + reopenedMode + " mode");
                if (twoPageFit)
                {
                    pages.SelectedIndex = 1;
                    Wait(() => jump.Text == "2", "original converted TwoPage source right-page selection");
                    SettleRender("original converted TwoPage source right-page selection");
                    Wait(() => Children(viewer).OfType<Canvas>().Any(canvas => canvas.IsVisible && Equals(canvas.Tag, 1)
                        && canvas.Parent is Grid tile && tile.Children.OfType<Image>()
                            .Any(image => image.Source is System.Windows.Media.Imaging.BitmapSource)), "original converted source right image");
                    zoom.SelectedItem = zoom.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, "2.0"));
                    Wait(() => zoom.Text == "200%", "original converted source200%");
                    SettleRender("original converted source before FitPage");
                    zoom.SelectedItem = zoom.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, "fitpage"));
                    SettleRender("original converted source FitPage");
                    originalConvertedFitGeometry = ReadPublicFitGeometry(Children(viewer).OfType<Canvas>().Single(canvas => canvas.IsVisible
                        && Equals(canvas.Tag, 1) && canvas.RenderSize.Width > 0));
                    CloseWithoutSaving(owner);
                    Wait(() => pages.Items.Count == 0, "TwoPage source closed before same-mode reopen");
                    OpenDocument(other);
                    SelectMode("TwoPage", request: false);
                    SettleRender("TwoPage source reopened directly in global TwoPage mode");
                }
                if (measuredControl)
                {
                    if (twoPage)
                    {
                        pages.SelectedIndex = 1;
                        Wait(() => jump.Text == "2", "settled TwoPage control selects the right page");
                        SettleRender("settled TwoPage control right-page selection");
                        Wait(() => Children(viewer).OfType<Canvas>().Any(canvas => canvas.IsVisible && Equals(canvas.Tag, 1)
                            && canvas.Parent is Grid tile && tile.Children.OfType<Image>()
                                .Any(image => image.Source is System.Windows.Media.Imaging.BitmapSource)),
                            "settled TwoPage control actual right image");
                    }
                    zoom.SelectedItem = zoom.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, "2.0"));
                    Wait(() => zoom.Text == "200%", "settled Single button control at200%");
                    SettleRender("settled Single button control beforeclick");
                    ExecuteMeasuredCommand();
                    SettleRender("settled Single button control afterclick");
                    Canvas? controlCanvas = null;
                    Wait(() => (controlCanvas = Children(viewer).OfType<Canvas>().FirstOrDefault(canvas => canvas.IsVisible
                        && Equals(canvas.Tag, pages.SelectedIndex) && canvas.RenderSize.Width > 0
                        && canvas.Parent is Grid tile && tile.Children.OfType<Image>()
                            .Any(image => image.Source is System.Windows.Media.Imaging.BitmapSource))) != null,
                        "settled Single button control raster");
                    owner.UpdateLayout(); viewer.UpdateLayout(); Pump();
                    settledButtonControlZoom = zoom.Text;
                    settledButtonControlWidth = controlCanvas!.TransformToAncestor(viewer)
                        .TransformBounds(new Rect(controlCanvas.RenderSize)).Width;
                    if (settledButtonControlZoom == "200%" || !double.IsFinite(settledButtonControlWidth.Value)
                        || settledButtonControlWidth <= 0)
                        throw new InvalidOperationException("The settled Single Zoom In control did not establish a distinct public result.");
                    expectedZoom = settledButtonControlZoom;
                    expectedCanvasScreenDipWidth = settledButtonControlWidth;
                    if (twoPageFit)
                    {
                        sourceFitGeometry = ReadPublicFitGeometry(controlCanvas);
                        ScrollViewer panel = Children(viewer).OfType<ScrollViewer>().Single(scroll => scroll.Name == "PagePreviewPanel");
                        sourcePanelWidth = panel.ActualWidth; sourcePanelHeight = panel.ActualHeight;
                    }
                    Console.WriteLine("CONTROL settled Single Zoom In: zoom=" + settledButtonControlZoom
                        + ", width=" + settledButtonControlWidth + " DIP");
                }
                CloseWithoutSaving(owner);
                Wait(() => pages.Items.Count == 0, "second document closed");
                if (comparisonFit) PrepareComparisonSource();
            }
            stage = "public reopen";
            if (timedCommand)
            {
                timingHandler = (_, _) =>
                {
                    if (timingTriggerScheduled) return;
                    const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    object? session = viewer.GetType().GetProperty("ActiveSessionExt", members)!.GetValue(viewer);
                    object state = viewer.GetType().GetProperty("State", members)!.GetValue(viewer)!;
                    if (ReadPendingTrueZoom(session) is not double pending
                        || !double.IsFinite(pending) || pending <= 0
                        || state.GetType().GetField("Mode")!.GetValue(state)?.ToString() != reopenedMode) return;
                    timingTriggerScheduled = true;
                    owner.Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
                    {
                        timingTriggerFired = true;
                        object? currentSession = viewer.GetType().GetProperty("ActiveSessionExt", members)!.GetValue(viewer);
                        pendingAtTimingFire = ReadPendingTrueZoom(currentSession) is double requested
                            && double.IsFinite(requested) && requested > 0;
                        PropertyInfo primary = viewer.GetType().GetProperty("PrimaryRenderTask", members)!;
                        FieldInfo generation = viewer.GetType().GetField("_primaryRenderGeneration", members)!;
                        primaryCompletedBeforeTimingCommand = ((Task)primary.GetValue(viewer)!).IsCompleted;
                        generationBeforeTimingCommand = (long)generation.GetValue(viewer)!;
                        Console.WriteLine("CONTROL pending restore public command: " + (reassert ? "reassert Single" : wheel ? "zoom wheel +120"
                            : comparisonFit ? "comparison module source FitPage" : pendingFit ?? (sequence ? "Zoom Out, Out, In" : button ? "Zoom In button" : "navigate page3")));
                        if (reassert)
                            ((RadioButton)owner.FindName("ViewSingleRadio")).RaiseEvent(new RoutedEventArgs(RadioButton.CheckedEvent));
                        else if (wheel)
                            zoom.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
                                { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = zoom });
                        else if (comparisonFit)
                        {
                            object sourceState = comparisonSourceViewer!.GetType().GetProperty("State", members)!.GetValue(comparisonSourceViewer)!;
                            object targetState = viewer.GetType().GetProperty("State", members)!.GetValue(viewer)!;
                            string? sourceFit = sourceState.GetType().GetField("Fit")!.GetValue(sourceState)?.ToString();
                            int currentPage = (int)targetState.GetType().GetField("CurrentPage")!.GetValue(targetState)!;
                            var dimensions = (System.Collections.IDictionary)(viewer.GetType().GetProperty("_renderDims", members)?.GetValue(viewer)
                                ?? viewer.GetType().GetField("_renderDims", members)!.GetValue(viewer))!;
                            bool dimsPresent = dimensions.Contains(currentPage);
                            bool imageNull = ((Image)targetState.GetType().GetField("PageImage")!.GetValue(targetState)!).Source is null;
                            double? pendingValue = ReadPendingTrueZoom(currentSession);
                            string? pendingFitValue = currentSession?.GetType().GetField("PendingFitRestore", members)?.GetValue(currentSession)?.ToString();
                            bool primaryComplete = ((Task)primary.GetValue(viewer)!).IsCompleted;
                            comparisonCriticalSeamReached = sourceFit == "Page" && !dimsPresent;
                            comparisonInvocationConditions = new { SourceFit = sourceFit, TargetPage = currentPage,
                                TargetDimsPresent = dimsPresent, TargetImageNull = imageNull, PendingValue = pendingValue,
                                PendingFit = pendingFitValue, PrimaryComplete = primaryComplete };
                            viewer.GetType().GetMethod("ApplyComparisonZoomExt", members)!.Invoke(viewer, [comparisonSourceViewer!]);
                        }
                        else if (measuredControl)
                            ExecuteMeasuredCommand();
                        else
                        {
                            expectedPage = 3;
                            pages.SelectedIndex = 2;
                        }
                        primaryCompletedAfterTimingCommand = ((Task)primary.GetValue(viewer)!).IsCompleted;
                        generationAfterTimingCommand = (long)generation.GetValue(viewer)!;
                        Console.WriteLine("CONTROL timing readiness: pending=" + pendingAtTimingFire
                            + ", primary completed=" + primaryCompletedBeforeTimingCommand + "->" + primaryCompletedAfterTimingCommand
                            + ", generation=" + generationBeforeTimingCommand + "->" + generationAfterTimingCommand);
                    }));
                };
                pages.SelectionChanged += timingHandler;
            }
            OpenDocument();
            if (timedCommand && (!timingTriggerScheduled || !timingTriggerFired))
                throw new InvalidOperationException("The pending true-zoom restore timing precondition was not reached.");
            if (timedCommand && !pendingAtTimingFire)
                throw new InvalidOperationException("The timing control missed its target: saved zoom was already consumed before the public command.");
            if ((measuredControl || comparisonFit) && !cachedFit && primaryCompletedBeforeTimingCommand != false)
                throw new InvalidOperationException("The Zoom In timing control missed its target: the initial primary was already complete.");
            if (comparisonFit && !comparisonCriticalSeamReached)
                throw new InvalidOperationException("Comparison invocation missed source FitPage with absent target dimensions.");
            Wait(() => pages.Items.Count == 5 && jump.Text.Length > 0, "viewport document reopened");
            SelectMode(reopenedMode, request: false);
            SettleRender("after public reopen");
            if (cachedFit)
            {
                pages.SelectedIndex = 2;
                Wait(() => jump.Text == "3", "warm cached page3 selected");
                SettleRender("warm cached page3");
                Wait(() => Children(viewer).OfType<Canvas>().Any(canvas => canvas.IsVisible && Equals(canvas.Tag, 2)
                    && canvas.Parent is Grid tile && tile.Children.OfType<Image>().Any(image => image.Source is System.Windows.Media.Imaging.BitmapSource)),
                    "warm page3 actual image");
                cachedPageWarmed = true;
                pages.SelectedIndex = 3;
                Wait(() => jump.Text == "4", "return to page4 before cached navigation");
                SettleRender("return to page4 after warming");
                Canvas displayed = Children(viewer).OfType<Canvas>().Single(canvas => canvas.IsVisible && Equals(canvas.Tag, 3) && canvas.Width > 0);
                cachedImage = ((Grid)displayed.Parent).Children.OfType<Image>().Single();
                if (cachedImage.Source is null) throw new InvalidOperationException("The cached navigation control did not start with a presented page4 image.");
                const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo generation = viewer.GetType().GetField("_primaryRenderGeneration", members)!;
                PropertyInfo primary = viewer.GetType().GetProperty("PrimaryRenderTask", members)!;
                cachedImageDescriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
                cachedImageHandler = (_, _) =>
                {
                    if (timingTriggerFired || cachedImage.Source != null) return;
                    object state = viewer.GetType().GetProperty("State", members)!.GetValue(viewer)!;
                    if (pages.SelectedIndex != 2 || state.GetType().GetField("Mode")!.GetValue(state)?.ToString() != "Single") return;
                    timingTriggerScheduled = timingTriggerFired = true;
                    cachedImageNullAtCommand = true;
                    generationBeforeTimingCommand = (long)generation.GetValue(viewer)!;
                    primaryCompletedBeforeTimingCommand = ((Task)primary.GetValue(viewer)!).IsCompleted;
                    zoom.SelectedItem = zoom.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, "fitwidth"));
                    object? session = viewer.GetType().GetProperty("ActiveSessionExt", members)!.GetValue(viewer);
                    cachedPendingAfterFit = ReadPendingTrueZoom(session) is double requested && double.IsFinite(requested) && requested > 0;
                    ((Button)owner.FindName("ZoomInBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    generationAfterTimingCommand = (long)generation.GetValue(viewer)!;
                    primaryCompletedAfterTimingCommand = ((Task)primary.GetValue(viewer)!).IsCompleted;
                    Console.WriteLine("CONTROL cached-image command: imageNull=" + cachedImageNullAtCommand
                        + ", pendingAfterFit=" + cachedPendingAfterFit + ", generation=" + cachedGenerationBeforeNavigation + "->" + generationAfterTimingCommand);
                };
                cachedImageDescriptor.AddValueChanged(cachedImage, cachedImageHandler);
                cachedGenerationBeforeNavigation = (long)generation.GetValue(viewer)!;
                expectedPage = 3;
                pages.SelectedIndex = 2;
                SettleRender("cached page3 after synchronous Fit Width and Zoom In");
                if (!cachedPageWarmed || !timingTriggerFired || !cachedImageNullAtCommand || !cachedPendingAfterFit
                    || generationAfterTimingCommand <= cachedGenerationBeforeNavigation)
                    throw new InvalidOperationException("The cached-image timing control missed its required warm-page, null-image, positive-fit-request or changed-generation target.");
            }
            if (twoPage)
            {
                Wait(() => Children(viewer).OfType<Canvas>().Any(canvas => Equals(canvas.Tag, 1) && canvas.Width > 0
                    && canvas.Parent is Grid tile && tile.Children.OfType<Image>()
                        .Any(image => image.Source is System.Windows.Media.Imaging.BitmapSource)),
                    "mixed-size selected right page raster");
                SettleRender("selected right page presented");
            }
            if (grid)
            {
                Wait(() => Enumerable.Range(0, 4).All(index => Children(viewer).OfType<Canvas>()
                    .Any(canvas => Equals(canvas.Tag, index) && canvas.Width > 0)), "three-column grid tiles");
                Canvas Tile(int index) => Children(viewer).OfType<Canvas>().Single(canvas => Equals(canvas.Tag, index) && canvas.Width > 0);
                Point Position(int index) => Tile(index).TranslatePoint(new Point(), viewer);
                Point a = Position(0), b = Position(1), c = Position(2), d = Position(3);
                if (!(a.X < b.X && b.X < c.X && Math.Abs(a.Y - b.Y) < 2 && Math.Abs(a.Y - c.Y) < 2 && d.Y > a.Y + 10))
                    throw new InvalidOperationException("Reopen did not retain Grid's three-column layout precedence: "
                        + $"{a} {b} {c} {d}, viewer {viewer.ActualWidth}x{viewer.ActualHeight}, tile {Tile(0).Width}.");
            }
            if (expectedCanvasScreenDipWidth.HasValue)
            {
                Canvas? selectedCanvas = null;
                Wait(() => (selectedCanvas = Children(viewer).OfType<Canvas>().FirstOrDefault(canvas => canvas.IsVisible
                    && Equals(canvas.Tag, expectedPage - 1) && canvas.RenderSize.Width > 0
                    && canvas.Parent is Grid tile && tile.Children.OfType<Image>()
                        .Any(image => image.Source is System.Windows.Media.Imaging.BitmapSource))) != null,
                    "selected page public raster and layout");
                owner.UpdateLayout(); viewer.UpdateLayout(); Pump();
                actualCanvasScreenDipWidth = selectedCanvas!.TransformToAncestor(viewer)
                    .TransformBounds(new Rect(selectedCanvas.RenderSize)).Width;
                if (twoPageFit)
                {
                    targetFitGeometry = ReadPublicFitGeometry(selectedCanvas);
                    ScrollViewer panel = Children(viewer).OfType<ScrollViewer>().Single(scroll => scroll.Name == "PagePreviewPanel");
                    equalSourceTargetPanel = Math.Abs(panel.ActualWidth - sourcePanelWidth!.Value) < .01
                        && Math.Abs(panel.ActualHeight - sourcePanelHeight!.Value) < .01;
                    if (equalSourceTargetPanel != true)
                    {
                        stage = "TwoPage viewport measurement precondition mismatch";
                        throw new InvalidOperationException("The settled TwoPage source and target viewport sizes differ; page-scale mismatch cannot be attributed to pending fit.");
                    }
                }
                Console.WriteLine("CONTROL selected page rendered width: " + actualCanvasScreenDipWidth
                    + " DIP, expected " + expectedCanvasScreenDipWidth + " DIP");
                if (!double.IsFinite(actualCanvasScreenDipWidth.Value)
                    || Math.Abs(actualCanvasScreenDipWidth.Value - expectedCanvasScreenDipWidth.Value) > 2)
                {
                    originalGeometryFailureZoom = zoom.Text;
                    if (twoPageFit)
                    {
                        zoom.SelectedItem = zoom.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, "2.0"));
                        Wait(() => zoom.Text == "200%", "TwoPage diagnostic reset before settled FitPage repeat");
                        SettleRender("TwoPage diagnostic target settled before FitPage repeat");
                        zoom.SelectedItem = zoom.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, "fitpage"));
                        SettleRender("TwoPage diagnostic target settled FitPage repeat");
                        Canvas repeated = Children(viewer).OfType<Canvas>().Single(canvas => canvas.IsVisible
                            && Equals(canvas.Tag, 1) && canvas.RenderSize.Width > 0);
                        targetRepeatedFitGeometry = ReadPublicFitGeometry(repeated);
                    }
                    throw new InvalidOperationException("Document reopen rendered the wrong public page scale: expected width "
                        + expectedCanvasScreenDipWidth + " DIP, got " + actualCanvasScreenDipWidth + " DIP while zoom label was " + originalGeometryFailureZoom + ".");
                }
            }
            Console.WriteLine("CONTROL public viewport after reopen: page=" + jump.Text + ", zoom=" + zoom.Text);
            bool scaleMatches = grid || (fit is not null ? Equals((zoom.SelectedItem as ComboBoxItem)?.Tag, fit)
                : zoom.Text == expectedZoom && (expectedFitTag is null || Equals((zoom.SelectedItem as ComboBoxItem)?.Tag, expectedFitTag)));
            if (pages.SelectedIndex != expectedPage - 1 || jump.Text != expectedPage.ToString() || !scaleMatches)
                throw new InvalidOperationException("Document reopen lost its remembered viewport: expected page " + expectedPage + " at " + (fit ?? expectedZoom) + ", got page "
                    + jump.Text + " at " + zoom.Text + ".");
            if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(original))
                throw new InvalidOperationException("Viewport restore changed the source PDF bytes.");
            if (crossMode && !File.ReadAllBytes(other).AsSpan().SequenceEqual(original))
                throw new InvalidOperationException("Cross-mode viewport restore changed its second source PDF.");
            stage = "passed";
            Console.WriteLine("PASS " + scenario + ": public reopen preserves page and "
                + (grid ? "three-column Grid precedence" : fit ?? (clamp ? "existing internal minimum (requested 20%, restored 38%)" : expectedZoom))
                + " with unchanged source bytes.");
            return 0;
        }
        finally
        {
            if (timingHandler != null) pages.SelectionChanged -= timingHandler;
            if (cachedImageDescriptor != null && cachedImage != null && cachedImageHandler != null)
                cachedImageDescriptor.RemoveValueChanged(cachedImage, cachedImageHandler);
            if (comparisonWindow != null)
            {
                ((Button)comparisonWindow.FindName("CloseFileBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                comparisonWindow.Close(); Pump();
            }
            File.WriteAllText(Path.Combine(folder, "viewport-restore-result.json"), JsonSerializer.Serialize(new
            {
                Stage = stage, ActualPage = jump.Text, ActualZoom = originalGeometryFailureZoom ?? zoom.Text,
                Scenario = scenario, CrossMode = crossMode, InitialMode = initialMode, ReopenedMode = reopenedMode,
                ExpectedPage = expectedPage, ExpectedZoom = grid ? "Grid3Columns" : fit ?? expectedZoom,
                RequestedZoom = comparisonFit ? expectedZoom : measuredControl ? settledButtonControlZoom : clamp ? "20%" : wheel ? "220%" : "200%", ClampLimited = clamp, ZoomRestorationAsserted = !grid,
                SettledButtonControlZoom = settledButtonControlZoom, SettledButtonControlWidth = settledButtonControlWidth,
                ExpectedSelectedZoomTag = expectedFitTag, ActualSelectedZoomTag = (zoom.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                ComparisonSeam = comparisonFit ? "ApplyComparisonZoomExt module API with real separate owning window; no physical Compare UI gesture" : null,
                ComparisonSourceOwnerWidth = comparisonSourceOwnerWidth, ComparisonSourceOwnerHeight = comparisonSourceOwnerHeight,
                ComparisonInvocationConditions = comparisonInvocationConditions, ComparisonCriticalSeamReached = comparisonCriticalSeamReached,
                OriginalConvertedFitGeometry = originalConvertedFitGeometry,
                EqualSourceTargetPanel = equalSourceTargetPanel,
                SourceFitGeometry = sourceFitGeometry, TargetFitGeometry = targetFitGeometry, TargetRepeatedFitGeometry = targetRepeatedFitGeometry,
                TimingTriggerScheduled = timingTriggerScheduled, TimingTriggerFired = timingTriggerFired,
                PendingAtTimingFire = pendingAtTimingFire,
                CachedPageWarmed = cachedPageWarmed, CachedImageNullAtCommand = cachedImageNullAtCommand,
                CachedPendingAfterFit = cachedPendingAfterFit, CachedGenerationBeforeNavigation = cachedGenerationBeforeNavigation,
                PrimaryCompletedBeforeTimingCommand = primaryCompletedBeforeTimingCommand,
                PrimaryCompletedAfterTimingCommand = primaryCompletedAfterTimingCommand,
                GenerationBeforeTimingCommand = generationBeforeTimingCommand,
                GenerationAfterTimingCommand = generationAfterTimingCommand,
                ActualCanvasScreenDipWidth = actualCanvasScreenDipWidth,
                ExpectedCanvasScreenDipWidth = expectedCanvasScreenDipWidth,
                SourceUnchanged = File.ReadAllBytes(source).AsSpan().SequenceEqual(original),
                OtherSourceUnchanged = !crossMode || File.ReadAllBytes(other).AsSpan().SequenceEqual(original),
                AppSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)))
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        object ReadPublicFitGeometry(Canvas canvas)
        {
            ScrollViewer panel = Children(viewer).OfType<ScrollViewer>().Single(scroll => scroll.Name == "PagePreviewPanel");
            Rect bounds = canvas.TransformToAncestor(viewer).TransformBounds(new Rect(canvas.RenderSize));
            object geometry = new { PageIndex = pages.SelectedIndex, Zoom = zoom.Text, CanvasWidth = bounds.Width,
                CanvasHeight = bounds.Height, PanelWidth = panel.ActualWidth, PanelHeight = panel.ActualHeight, panel.ViewportHeight };
            return geometry;
        }

        void PrepareComparisonSource()
        {
            Window targetOwner = owner;
            ListBox targetPages = pages;
            ComboBox targetZoom = zoom;
            TextBox targetJump = jump;
            FrameworkElement targetViewer = viewer;
            comparisonWindow = (Window)Activator.CreateInstance(owner.GetType())!;
            comparisonWindow.Width = targetOwner.ActualWidth;
            comparisonWindow.Height = targetOwner.ActualHeight;
            comparisonWindow.Show(); Pump();
            owner = comparisonWindow;
            pages = (ListBox)owner.FindName("PageList"); zoom = (ComboBox)owner.FindName("ZoomBox");
            jump = (TextBox)owner.FindName("PageJumpBox"); viewer = (FrameworkElement)owner.FindName("Viewer");
            try
            {
                OpenDocument(other);
                SelectMode("Single");
                pages.SelectedIndex = 3;
                Wait(() => jump.Text == "4", "comparison source page4");
                SettleRender("comparison source actual page4 before fit");
                zoom.SelectedItem = zoom.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, "fitpage"));
                SettleRender("comparison source settled FitPage");
                Wait(() => Equals((zoom.SelectedItem as ComboBoxItem)?.Tag, "fitpage"), "comparison source public FitPage marker");
                Canvas canvas = Children(viewer).OfType<Canvas>().Single(candidate => candidate.IsVisible
                    && Equals(candidate.Tag, 3) && candidate.RenderSize.Width > 0);
                expectedCanvasScreenDipWidth = canvas.TransformToAncestor(viewer).TransformBounds(new Rect(canvas.RenderSize)).Width;
                expectedZoom = zoom.Text;
                expectedFitTag = "fitpage";
                comparisonSourceViewer = viewer;
                comparisonSourceOwnerWidth = owner.ActualWidth; comparisonSourceOwnerHeight = owner.ActualHeight;
                if (comparisonSourceOwnerWidth != targetOwner.ActualWidth || comparisonSourceOwnerHeight != targetOwner.ActualHeight)
                    throw new InvalidOperationException("Comparison source and target owning window sizes differ.");
                Console.WriteLine("CONTROL real comparison source FitPage: zoom=" + expectedZoom
                    + ", width=" + expectedCanvasScreenDipWidth + " DIP; module API integration only.");
            }
            finally
            {
                owner = targetOwner; pages = targetPages; zoom = targetZoom; jump = targetJump; viewer = targetViewer;
            }
        }

        static double? ReadPendingTrueZoom(object? session)
        {
            if (session is null) return null;
            const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            return (session.GetType().GetProperty("PendingTrueZoomRestore", members)?.GetValue(session)
                ?? session.GetType().GetField("PendingTrueZoomRestore", members)?.GetValue(session)) is double value ? value : null;
        }

        void ExecuteMeasuredCommand()
        {
            if (pendingFit is not null)
            {
                if (zoomThenFit)
                    ((Button)owner.FindName("ZoomInBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                zoom.SelectedItem = zoom.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, pendingFit));
                if (fitThenZoom)
                    ((Button)owner.FindName("ZoomInBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else
            {
                if (sequence)
                    for (int index = 0; index < 2; index++)
                        ((Button)owner.FindName("ZoomOutBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((Button)owner.FindName("ZoomInBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        }

        void OpenDocument(string? path = null)
        {
            owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [path ?? source]);
            Wait(() => pages.Items.Count == 5 && pages.SelectedIndex >= 0
                && Children(viewer).OfType<Canvas>().Any(canvas => Equals(canvas.Tag, pages.SelectedIndex) && canvas.Width > 0),
                "current public viewport page rendered");
            Pump();
        }

        void SelectMode(string mode, bool request = true)
        {
            var radio = (RadioButton)owner.FindName("View" + mode + "Radio");
            if (request)
                ((ISelectionItemProvider)new RadioButtonAutomationPeer(radio).GetPattern(PatternInterface.SelectionItem)!).Select();
            Type appType = owner.GetType().Assembly.GetType("KillerPDF.App", true)!;
            MethodInfo setting = appType.GetMethod("GetSetting", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            object viewState = viewer.GetType().GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(viewer)!;
            Wait(() => radio.IsChecked == true
                && (Equals(setting.Invoke(null, ["ViewMode"]), mode)
                    || mode == "Continuous" && setting.Invoke(null, ["ViewMode"]) is null)
                && viewState.GetType().GetField("Mode")!.GetValue(viewState)?.ToString() == mode
                && viewState.GetType().GetField("Pending")!.GetValue(viewState) is null
                && Children(viewer).OfType<ScrollViewer>().Any(scroll => scroll.Name == "PagePreviewPanel"
                    && scroll.IsVisible && scroll.Opacity > .99), mode + " applied and viewport fade completed");
        }

        void SettleRender(string phase)
        {
            const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo render = viewer.GetType().GetProperty("PrimaryRenderTask", members)!;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var task = (Task)render.GetValue(viewer)!;
                Wait(() => task.IsCompleted, "primary raster completion: " + phase);
                task.GetAwaiter().GetResult();
                Pump(); owner.UpdateLayout(); viewer.UpdateLayout(); Pump();
                if (!ReferenceEquals(task, render.GetValue(viewer))) continue;
                Wait(() => Children(viewer).OfType<Image>().Any(image => image.Source is System.Windows.Media.Imaging.BitmapSource),
                    "public raster presentation: " + phase);
                if (twoPageFit)
                {
                    ScrollViewer panel = Children(viewer).OfType<ScrollViewer>().Single(scroll => scroll.Name == "PagePreviewPanel");
                    double lastWidth = -1, lastHeight = -1;
                    var stable = Stopwatch.StartNew();
                    Wait(() =>
                    {
                        if (!panel.IsMeasureValid || !panel.IsArrangeValid || Math.Abs(panel.ActualWidth - lastWidth) > .01
                            || Math.Abs(panel.ActualHeight - lastHeight) > .01)
                        {
                            lastWidth = panel.ActualWidth; lastHeight = panel.ActualHeight; stable.Restart();
                            return false;
                        }
                        return stable.Elapsed >= TimeSpan.FromMilliseconds(150);
                    }, "public TwoPage viewport dimensions stable: " + phase);
                }
                Console.WriteLine("CONTROL raster ready " + phase + ": page=" + jump.Text + ", zoom=" + zoom.Text);
                return;
            }
            throw new InvalidOperationException("The primary raster changed repeatedly during viewport readiness.");
        }
    }

    private static byte[] MixedPageFixture()
    {
        List<string> objects = ["<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [4 0 R 6 0 R 8 0 R 10 0 R 12 0 R] /Count 5 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"];
        for (int page = 0; page < 5; page++)
        {
            string content = $"0.2 0.5 0.8 rg 0 0 200 200 re f 0 g BT /F1 20 Tf 20 100 Td (PAGE_{page + 1}) Tj ET\n";
            string size = page == 1 ? "300 400" : "200 200";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {size}] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + page * 2} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}endstream");
        }
        var pdf = new StringBuilder("%PDF-1.7\n");
        List<int> offsets = [0];
        for (int index = 0; index < objects.Count; index++) { offsets.Add(pdf.Length); pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static int VerifyMultiwindowRecovery(Window owner, string appPath, string folder)
    {
        const BindingFlags members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        Type windowType = owner.GetType();
        Type appType = windowType.Assembly.GetType("KillerPDF.App", true)!;
        appType.GetMethod("SetSetting", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(null, ["RememberChoiceLocked", "1"]);
        byte[] fixture = Fixture();
        string clean = Path.Combine(folder, "clean-main.pdf");
        string primary = Path.Combine(folder, "pending-main.pdf");
        string secondPane = Path.Combine(folder, "pending-main-b.pdf");
        string detached = Path.Combine(folder, "pending-detached.pdf");
        foreach (string path in new[] { clean, primary, secondPane, detached }) File.WriteAllBytes(path, fixture);
        Open(owner, clean);
        Open(owner, detached);
        var primaryViewer = (FrameworkElement)owner.FindName("Viewer");
        object session = primaryViewer.GetType().GetProperty("ActiveSessionExt", members)!.GetValue(primaryViewer)!;
        windowType.GetMethod("TearOutTab", members)!.Invoke(owner, [primaryViewer, session]);
        Window secondary = Application.Current.Windows.Cast<Window>()
            .Single(window => window != owner && window.GetType() == windowType);
        try { return CaptureAndVerify(); }
        finally
        {
            var discard = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
            discard.Tick += (_, _) =>
            {
                foreach (Window dialog in Application.Current.Windows.Cast<Window>()
                    .Where(window => window.GetType() != windowType && window.IsVisible).ToArray())
                {
                    string label = (string)Application.Current.FindResource("Str_Btn_DontSave");
                    Button? button = Children(dialog).OfType<Button>().FirstOrDefault(candidate => Equals(candidate.Content, label));
                    button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            };
            discard.Start();
            try { secondary.Close(); Pump(); }
            finally { discard.Stop(); }
        }

        int CaptureAndVerify()
        {
            // Startup-smoke suppresses startup document restoration, including the tear-out payload.
            // Load its source through the same external-open entry used for the primary fixture.
            Open(secondary, detached);
            Open(owner, primary);
            ((Button)owner.FindName("SplitPaneRailBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var paneB = (FrameworkElement)owner.FindName("ViewerB");
            Wait(() => paneB.IsVisible && paneB.ActualWidth >= 320, "recovery pane B shown");
            Activate(paneB);
            Open(owner, secondPane, "ViewerB");
            if (((TextBlock)owner.FindName("FileNameLabel")).Text != Path.GetFileName(secondPane))
                throw new InvalidOperationException("The pane B fixture did not open in the owning pane.");
            Activate(primaryViewer);
            Pending(owner, "MAIN PENDING");
            Activate(paneB);
            Pending(owner, "PANE PENDING", "ViewerB");
            Pending(secondary, "TORN PENDING");
            var editStates = new Dictionary<string, string>();
            foreach ((FrameworkElement pane, string marker) in new[]
            {
                (primaryViewer, "MAIN PENDING"), (paneB, "PANE PENDING"),
                ((FrameworkElement)secondary.FindName("Viewer"), "TORN PENDING")
            })
            {
                bool live = Children(pane).OfType<TextBox>()
                    .Any(box => box.AcceptsReturn && Equals(box.Tag, 0) && box.Text == marker);
                if (!live)
                {
                    // Normal focus changes commit text into an unsaved annotation. The public
                    // reading provider includes those annotations, so verify it was kept.
                    FrameworkElement surface = Children(pane).OfType<FrameworkElement>()
                        .Single(element => element.GetType().Name == "DocumentSurface");
                    var provider = UIElementAutomationPeer.CreatePeerForElement(surface)?.GetPattern(PatternInterface.Text) as ITextProvider
                        ?? throw new InvalidOperationException("The committed fixture has no public reading provider.");
                    Wait(() => provider.DocumentRange.GetText(-1).Contains(marker, StringComparison.Ordinal),
                        "committed unsaved marker remains readable: " + marker);
                }
                editStates[marker] = live ? "live editor" : "committed unsaved annotation";
                Console.WriteLine("CONTROL recovery fixture " + marker + ": " + editStates[marker]);
            }
            File.WriteAllText(Path.Combine(folder, "recovery-fixture-state.json"),
                JsonSerializer.Serialize(editStates, new JsonSerializerOptions { WriteIndented = true }));
            foreach (string original in new[] { primary, secondPane, detached })
                if (!File.ReadAllBytes(original).AsSpan().SequenceEqual(fixture))
                    throw new InvalidOperationException("The fixture saved its edits to the original before recovery.");

            string? manifest = (string?)windowType.GetMethod("PrepareRenderFailureRecovery", members)!.Invoke(owner, null);
            if (manifest is null) throw new InvalidOperationException("Recovery did not create a manifest for all three unsaved documents.");
            using JsonDocument recovery = JsonDocument.Parse(File.ReadAllBytes(manifest));
            JsonElement[] records = recovery.RootElement.EnumerateArray().ToArray();
            foreach ((string original, string expected) in new[]
            {
                (primary, "MAIN PENDING"), (secondPane, "PANE PENDING"), (detached, "TORN PENDING")
            })
            {
                JsonElement[] matches = records.Where(record => record.GetProperty("OriginalPath").GetString() == original).ToArray();
                if (matches.Length != 1)
                    throw new InvalidOperationException($"All-window recovery expected one entry for {Path.GetFileName(original)}, found {matches.Length}.");
                string recovered = matches[0].GetProperty("RecoveredPath").GetString()!;
                string actual = Extract(File.ReadAllBytes(recovered));
                if (!actual.Contains(expected, StringComparison.Ordinal))
                    throw new InvalidOperationException("All-window recovery omitted unsaved editor content in " + Path.GetFileName(original) + ": " + actual);
                if (!File.ReadAllBytes(original).AsSpan().SequenceEqual(fixture))
                    throw new InvalidOperationException("All-window recovery changed " + Path.GetFileName(original));
            }
            MethodInfo getSetting = appType.GetMethod("GetSetting", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            string[] restore = ((string?)getSetting.Invoke(null, ["OpenTabs"]) ?? string.Empty).Split('|');
            if (!restore.Contains(clean) || restore.Contains(primary) || restore.Contains(secondPane) || restore.Contains(detached))
                throw new InvalidOperationException("Recovery did not preserve only clean originals for normal tab restore.");
            string result = "PASS one recovery call preserved three unsaved edits across main panes A/B and a torn-out window, original files and clean-tab restore settings. App SHA256="
                + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
            File.WriteAllText(Path.Combine(folder, "result.txt"), result);
            Console.WriteLine(result);
            return 0;
        }

        static void Activate(FrameworkElement pane)
        {
            pane.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Pump();
        }

        static void Pending(Window window, string text, string viewerName = "Viewer")
        {
            var viewer = (FrameworkElement)window.FindName(viewerName);
            Canvas page = Children(viewer).OfType<Canvas>().First(canvas => Equals(canvas.Tag, 0) && canvas.Width > 0);
            ((Button)window.FindName("ToolTextBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            viewer.GetType().GetMethod("PlaceTextAtPosition", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(viewer, [new Point(page.Width * .2, page.Height * .5), 0]);
            Wait(() => Children(viewer).OfType<TextBox>().Any(box => box.AcceptsReturn && Equals(box.Tag, 0)), "multiwindow live editor");
            TextBox editor = Children(viewer).OfType<TextBox>().Single(box => box.AcceptsReturn && Equals(box.Tag, 0));
            editor.Width = page.Width * .75;
            editor.Text = text;
            Pump();
            if (editor.Text != text) throw new InvalidOperationException("The live editor did not retain its independent marker.");
            var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                editor.FlowDirection, new Typeface(editor.FontFamily, editor.FontStyle, editor.FontWeight, editor.FontStretch),
                editor.FontSize, editor.Foreground, VisualTreeHelper.GetDpi(editor).PixelsPerDip);
            if (formatted.WidthIncludingTrailingWhitespace > editor.ActualWidth - editor.Padding.Left - editor.Padding.Right - 2)
                throw new InvalidOperationException("The recovery marker does not fit on one editor line.");
            Rect pageBounds = page.TransformToAncestor(viewer).TransformBounds(new Rect(page.RenderSize));
            Rect editorBounds = editor.TransformToAncestor(viewer).TransformBounds(new Rect(editor.RenderSize));
            if (editorBounds.IsEmpty || !pageBounds.Contains(editorBounds))
                throw new InvalidOperationException("The live editor lies outside its rendered page.");
        }
    }

    private static void VerifyIncompleteRecovery(Window owner, string folder)
    {
        string source = Path.Combine(folder, "incomplete-source.pdf");
        byte[] fixture = Fixture();
        File.WriteAllBytes(source, fixture);
        Open(owner, source);
        var viewer = (FrameworkElement)owner.FindName("Viewer");
        Canvas page = Children(viewer).OfType<Canvas>().First(canvas => Equals(canvas.Tag, 0) && canvas.Width > 0);
        ((Button)owner.FindName("ToolTextBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        viewer.GetType().GetMethod("PlaceTextAtPosition", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewer, [new Point(page.Width * .2, page.Height * .5), 0]);
        Wait(() => Children(viewer).OfType<TextBox>().Any(box => box.AcceptsReturn && Equals(box.Tag, 0)), "failure fixture editor");
        TextBox editor = Children(viewer).OfType<TextBox>().Single(box => box.AcceptsReturn && Equals(box.Tag, 0));
        editor.Text = "COMMITTED BEFORE FAILED RECOVERY";
        ((Button)owner.FindName("ToolSelectBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((Button)owner.FindName("ToolTextBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        viewer.GetType().GetMethod("PlaceTextAtPosition", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewer, [new Point(page.Width * .2, page.Height * .7), 0]);
        editor = Children(viewer).OfType<TextBox>().Single(box => box.AcceptsReturn && Equals(box.Tag, 0));
        editor.Text = "PENDING BEFORE FAILED RECOVERY";

        // Read the isolated app's owned directory only to set up a real filesystem failure.
        // The observable operation remains the same public recovery call used by the app.
        string temp = (string)owner.GetType().Assembly.GetType("KillerPDF.App", true)!
            .GetProperty("TempDir", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var directory = new DirectoryInfo(temp);
        // The app's session folder sits either in a startup-smoke folder under the system temp
        // folder, or under this run's own output folder when the host isolated the data root there.
        bool inStartupSmoke = directory.Parent?.Parent?.Name.StartsWith("KillerPDF-startup-smoke-", StringComparison.Ordinal) == true
            && Path.GetFullPath(temp).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase);
        bool inRunFolder = Path.GetFullPath(temp).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!directory.Name.StartsWith("session-", StringComparison.Ordinal)
            || directory.Parent?.Name != "Temp"
            || !(inStartupSmoke || inRunFolder))
            throw new InvalidOperationException("The failure fixture is outside the isolated smoke directory.");
        // Only the access list changes: rewriting the whole descriptor would also write the audit
        // list, which needs a privilege a standard user does not hold, and an unmodified copy
        // persists nothing, so the rule is removed from the same object afterwards.
        DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Access);
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
        security.AddAccessRule(deny);
        string? manifest;
        try
        {
            directory.SetAccessControl(security);
            manifest = (string?)owner.GetType().GetMethod("PrepareRenderFailureRecovery", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(owner, null);
        }
        finally
        {
            security.RemoveAccessRuleSpecific(deny);
            directory.SetAccessControl(security);
        }
        if (manifest is null) throw new InvalidOperationException("Failed save discarded the document instead of reporting incomplete recovery.");
        using JsonDocument recovery = JsonDocument.Parse(File.ReadAllBytes(manifest));
        JsonElement record = recovery.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("OriginalPath").GetString() == source);
        if (!record.TryGetProperty("RecoveryError", out JsonElement error) || string.IsNullOrWhiteSpace(error.GetString()))
            throw new InvalidOperationException("Failed save was not recorded as incomplete recovery.");
        string working = record.GetProperty("RecoveredPath").GetString()!;
        string state = record.GetProperty("StatePath").GetString()!;
        if (!Extract(File.ReadAllBytes(working)).Contains("ORIGINAL BODY TEXT", StringComparison.Ordinal))
            throw new InvalidOperationException("Failed recovery did not retain readable working PDF bytes.");
        string savedState = File.ReadAllText(state);
        if (!savedState.Contains("COMMITTED BEFORE FAILED RECOVERY", StringComparison.Ordinal)
            || !savedState.Contains("PENDING BEFORE FAILED RECOVERY", StringComparison.Ordinal))
            throw new InvalidOperationException("Failed recovery discarded available annotation or pending editor state.");
        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(fixture))
            throw new InvalidOperationException("Failed recovery modified the original document.");
        CloseWithoutSaving(owner);
        Console.WriteLine("PASS denied full save retained working PDF, annotations, pending editor state and incomplete manifest.");
    }

    private static string ReadDocument(Window owner)
    {
        FrameworkElement? surface = Children((DependencyObject)owner.FindName("Viewer"))
            .OfType<FrameworkElement>().FirstOrDefault(element => element.GetType().Name == "DocumentSurface");
        if (surface is null) return string.Empty;
        return (UIElementAutomationPeer.CreatePeerForElement(surface)?.GetPattern(PatternInterface.Text) as ITextProvider)
            ?.DocumentRange.GetText(-1) ?? string.Empty;
    }

    private static void Open(Window owner, string path, string viewerName = "Viewer")
    {
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [path]);
        Wait(() => Children((DependencyObject)owner.FindName(viewerName)).OfType<Canvas>()
            .Any(canvas => Equals(canvas.Tag, 0) && canvas.Width > 0), "page render");
        Pump();
    }

    private static void CloseWithoutSaving(Window owner)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            foreach (Window message in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                string discard = (string)Application.Current.FindResource("Str_Btn_DontSave");
                Button? button = Children(message).OfType<Button>().FirstOrDefault(candidate => Equals(candidate.Content, discard));
                if (button is not null) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else message.Close();
            }
        };
        timer.Start();
        try { ((Button)owner.FindName("CloseFileBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
        finally { timer.Stop(); }
    }

    private static string Extract(byte[] bytes)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
            .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(bytes)])!;
        object reader = Activator.CreateInstance(engine.GetType("KillerPdf.Engine.Documents.PdfPageContentReader", true)!, [document])!;
        object content = reader.GetType().GetMethod("Read")!.Invoke(reader, [0, CancellationToken.None])!;
        return (string)content.GetType().GetProperty("Text")!.GetValue(content)!;
    }

    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (DependencyObject child in Children(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Wait(Func<bool> condition, string stage)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(stage);
            Pump();
            Thread.Sleep(10);
        }
    }

    private static byte[] Fixture()
    {
        string stream = "BT /F1 14 Tf 40 250 Td (ORIGINAL BODY TEXT) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Length " + stream.Length + " >>\nstream\n" + stream + "\nendstream"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int> { 0 };
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
