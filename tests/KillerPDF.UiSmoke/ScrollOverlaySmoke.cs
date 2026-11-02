using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

internal static class ScrollOverlaySmoke
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static;

    internal static int Run(string appPath, string appXaml, string folder, bool toolbar = false)
    {
        if (!Environment.GetCommandLineArgs().Contains("--startup-smoke"))
            throw new InvalidOperationException("Startup smoke isolation is required.");
        if (Directory.Exists(folder)) throw new IOException("Use a fresh evidence directory.");
        Directory.CreateDirectory(folder);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(Path.GetDirectoryName(appPath)!, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        Assembly assembly = Assembly.LoadFrom(appPath);
        typeof(Application).GetField("_resourceAssembly", Members)!.SetValue(null, assembly);
        assembly.GetType("KillerPDF.Services.AppDataPaths", true)!
            .GetProperty("StartupSmokeRoot", Members)!.SetValue(null, Path.Combine(folder, "data"));
        Type appType = assembly.GetType("KillerPDF.App", true)!;
        var hostType = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ScrollSmokeHost"),
            AssemblyBuilderAccess.Run).DefineDynamicModule("Host")
            .DefineType("IsolatedApplication", TypeAttributes.Public, appType);
        hostType.DefineDefaultConstructor(MethodAttributes.Public);
        MethodBuilder startup = hostType.DefineMethod("OnStartup",
            MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(void), [typeof(StartupEventArgs)]);
        startup.GetILGenerator().Emit(OpCodes.Ret);
        hostType.DefineMethodOverride(startup, appType.GetMethod("OnStartup", Members)!);
        var app = (Application)Activator.CreateInstance(hostType.CreateType()!)!;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        XDocument source = XDocument.Load(appXaml);
        XElement resources = source.Descendants().First(element => element.Name.LocalName == "ResourceDictionary");
        foreach (XAttribute declaration in source.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            resources.SetAttributeValue(declaration.Name, declaration.Value);
        string xaml = resources.ToString();
        // Whole attribute values: "clr-namespace:KillerPDF" is also the start of KillerPDF.Controls.
        foreach (string name in new[] { "KillerPDF", "KillerPDF.Controls", "KillerFamily.Layout" })
            xaml = xaml.Replace("\"clr-namespace:" + name + "\"", "\"clr-namespace:" + name + ";assembly=KillerPDF\"", StringComparison.Ordinal);
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml,
            new System.Windows.Markup.ParserContext { BaseUri = new Uri("pack://application:,,,/KillerPDF;component/") });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var report = new List<string>();
        var failures = new List<string>();
        Window? window = null;
        try
        {
            // App startup order: the kit theme hooks (settings, KillerPDF's own keys) before the services.
            assembly.GetType("KillerPDF.Services.AppTheme", true)!.GetMethod("Configure",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null);
            foreach (string service in new[] { "ThemeManager", "LocaleManager", "FilmGrain" })
                assembly.GetType("KillerPDF.Services." + service, true)!
                    .GetMethod("Initialize", Members)!.Invoke(null, null);
            window = (Window)Activator.CreateInstance(assembly.GetType("KillerPDF.Shell.MainWindow", true)!)!;
            var root = (FrameworkElement)window.Content;
            if (toolbar) return VerifyToolbar(window, root, folder);
            var overlay = (FrameworkElement)window.FindName("ShortcutsOverlay");
            overlay.Visibility = Visibility.Visible;
            var card = (FrameworkElement)window.FindName("ShortcutCardGrid");
            var scroll = Descendants<ScrollViewer>(card).Single();
            var modernClose = Descendants<Button>(card).First(b => b.Width == 28 || b.HorizontalAlignment == HorizontalAlignment.Right);
            Type themeType = assembly.GetType("KillerPDF.Services.Theme", true)!;
            MethodInfo apply = assembly.GetType("KillerPDF.Services.ThemeManager", true)!.GetMethod("Apply", Members)!;
            foreach (string theme in new[] { "Dark", "Light", "SE98" })
            {
                apply.Invoke(null, [Enum.Parse(themeType, theme)]);
                foreach (double height in new[] { 480d, 900d })
                {
                    Settle(root, 1000, height);
                    var bar = Descendants<ScrollBar>(scroll).First(b => b.Orientation == Orientation.Vertical);
                    Rect edge = Bounds(bar, card);
                    report.Add($"{theme} height={height} card={card.ActualWidth}x{card.ActualHeight} bar={edge} overflow={scroll.ScrollableHeight}");
                    Check(Math.Abs(card.ActualWidth - 1 - edge.Right) <= 1, "Scrollbar must touch the card's inner right edge.");
                    if (modernClose.Visibility == Visibility.Visible)
                    {
                        Rect overlap = Rect.Intersect(edge, Bounds(modernClose, card));
                        Check(overlap.IsEmpty || overlap.Width <= 0 || overlap.Height <= 0, "Close and scrollbar must not overlap.");
                    }
                    foreach (string state in new[] { "top", "middle", "bottom" })
                    {
                        scroll.ScrollToVerticalOffset(state == "top" ? 0 : state == "middle" ? scroll.ScrollableHeight / 2 : scroll.ScrollableHeight);
                        Settle(root, 1000, height);
                        var cues = Descendants<Border>(scroll).Where(b => b.Background is LinearGradientBrush g
                            && g.GradientStops.Any(s => s.Color == Color.FromArgb(0x50, 0, 0, 0))).ToArray();
                        Check(cues.Length == 2, "Both overflow cues must exist.");
                        foreach (var cue in cues)
                        {
                            bool expected = cue.VerticalAlignment == VerticalAlignment.Top
                                ? scroll.VerticalOffset > .5 : scroll.ScrollableHeight - scroll.VerticalOffset > .5;
                            Check((cue.Visibility == Visibility.Visible) == expected, "Cue must follow remaining content.");
                            Check(cue.Opacity == (theme == "SE98" ? 0 : 1), "Cue must follow theme opacity.");
                        }
                        Render(card, Path.Combine(folder, $"{theme}-{height}-{state}.png"));
                    }
                }
                card.MaxHeight = double.PositiveInfinity;
                scroll.ScrollToTop();
                Settle(root, 1000, 2200);
                Check(scroll.ScrollableHeight == 0, "A fitting list must not overflow.");
                Check(Descendants<Border>(scroll).Where(b => b.Background is LinearGradientBrush g
                    && g.GradientStops.Any(s => s.Color == Color.FromArgb(0x50, 0, 0, 0)))
                    .All(b => b.Visibility == Visibility.Collapsed), "Fitting content must have no edge cues.");
                card.MaxHeight = 620;
                ((Button)window.FindName("KsViewKeyboardBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Settle(root, 1000, 480);
                Check(((FrameworkElement)window.FindName("ShortcutKeyboardHost")).Visibility == Visibility.Visible,
                    "The keyboard view must remain available.");
                Render(card, Path.Combine(folder, theme + "-keyboard.png"));
                ((Button)window.FindName("KsViewListBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                report.Add(theme + " fitting content and keyboard view checked");
            }
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally
        {
            if (toolbar && window is not null)
                window.GetType().GetField("_fadingOut", Members)!.SetValue(window, true);
            window?.Close();
            app.Shutdown();
        }
        report.AddRange(failures.Select(f => "FAIL " + f));
        report.Add(failures.Count == 0 ? "result=ok" : "result=error");
        File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
        return failures.Count == 0 ? 0 : 1;

        void Check(bool success, string message) { if (!success) failures.Add(message); }
    }

    private static int VerifyToolbar(Window window, FrameworkElement root, string folder)
    {
        Type type = window.GetType();
        void Call(string name) => type.GetMethod(name, Members)!.Invoke(window, null);
        var left = (FrameworkElement)window.FindName("LeftBar");
        var right = (FrameworkElement)window.FindName("RightContainer");
        var grid = (FrameworkElement)window.FindName("ToolbarGrid");
        var units = ((System.Collections.IEnumerable)type.GetField("_toolbarUnits", Members)!.GetValue(window)!)
            .Cast<object>().Select(unit => (
                Id: (string)unit.GetType().GetProperty("Id")!.GetValue(unit)!,
                Bar: (FrameworkElement)unit.GetType().GetProperty("Bar")!.GetValue(unit)!)).ToArray();
        var report = new List<string>();
        int failures = 0;
        var outlineFixture = new TreeView
        {
            Style = (Style)window.FindResource("OutlineTreeStyle"),
            ItemContainerStyle = (Style)window.FindResource("OutlineItemStyle")
        };
        for (int index = 0; index < 24; index++)
            outlineFixture.Items.Add(new TreeViewItem
            {
                Header = "A long outline title describing rendering and document architecture " + index,
                Style = (Style)window.FindResource("OutlineItemStyle")
            });
        var outlineScroll = new ScrollViewer { Content = outlineFixture };
        type.GetMethod("ApplyTreeStripLayout", Members)!.Invoke(null, [outlineFixture, outlineScroll, true]);
        Settle(outlineScroll, 600, 160);
        outlineScroll.ScrollToRightEnd();
        Settle(outlineScroll, 600, 160);
        report.Add($"Outline strip extent={outlineScroll.ExtentWidth:F1} viewport={outlineScroll.ViewportWidth:F1} offset={outlineScroll.HorizontalOffset:F1}");
        if (outlineScroll.ScrollableWidth <= 0 || outlineScroll.HorizontalOffset <= 0
            || ScrollViewer.GetHorizontalScrollBarVisibility(outlineFixture) != ScrollBarVisibility.Disabled)
        { report.Add("FAIL outline overflow must be owned by the visible outer scrollbar"); failures++; }
        var outlineRow = (TreeViewItem)outlineFixture.Items[0];
        if (outlineRow.ActualWidth < 300)
        { report.Add("FAIL long outline titles need readable column width"); failures++; }
        Render(outlineScroll, Path.Combine(folder, "Outline-strip.png"));
        foreach (string mode in new[] { "Beside", "None", "Under" })
        {
            // The kit toolbar (Shell/ToolbarStyle.cs) names them LabelMode and SetLabelMode.
            var modeType = type.GetNestedType("LabelMode", Members)!;
            type.GetMethod("SetLabelMode", Members)!.Invoke(window, [Enum.Parse(modeType, mode)]);
            foreach (double width in new[] { 1000d, 1320d, 1600d })
            {
                Settle(root, width, 800);
                Call("InvalidateToolbarReflow"); Call("ReflowToolbar");
                Settle(root, width, 800);
                report.Add($"{mode} width={width} gap={right.TranslatePoint(new Point(), grid).X - left.ActualWidth:F1}");
                Render(grid, Path.Combine(folder, $"{mode}-{width}.png"));
                foreach (var unit in units.Where(unit => unit.Bar.Visibility == Visibility.Collapsed))
                {
                    unit.Bar.Visibility = Visibility.Visible;
                    Call("SyncToolbarSeparators"); Call("MeasureToolbarBars");
                    if (left.DesiredSize.Width + right.DesiredSize.Width <= grid.ActualWidth)
                    {
                        report.Add($"FAIL {mode} width={width}: {unit.Id} fits but is in overflow");
                        failures++;
                    }
                    unit.Bar.Visibility = Visibility.Collapsed;
                    Call("SyncToolbarSeparators"); Call("MeasureToolbarBars");
                }
            }
        }
        var footer = (FrameworkElement)window.FindName("FooterBorder");
        var display = (Panel)window.FindName("FooterDisplayControls");
        var fullScreen = (Button)window.FindName("FullScreenRailBtn");
        var invert = (Button)window.FindName("DocInvertBtn");
        var lastMode = (FrameworkElement)window.FindName("ViewGridRadio");
        var zoom = (FrameworkElement)window.FindName("ZoomOutBtn");
        if (fullScreen.Parent != display || invert.Parent != display
            || Bounds(lastMode, footer).Right > Bounds(display, footer).Left
            || Bounds(display, footer).Right > Bounds(zoom, footer).Left)
        {
            report.Add("FAIL footer display controls must sit between view modes and zoom");
            failures++;
        }
        Render(footer, Path.Combine(folder, "footer.png"));
        var dialogOwner = new Window { Resources = window.Resources, ShowInTaskbar = false };
        _ = new System.Windows.Interop.WindowInteropHelper(dialogOwner).EnsureHandle();
        Type transformType = type.Assembly.GetType("KillerPDF.TransformWindow", true)!;
        Type previewType = transformType.GetNestedType("PagePreview", Members)!;
        var pixels = Enumerable.Repeat((byte)255, 100 * 140 * 4).ToArray();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 64;
            pixels[i + 1] = 96;
            pixels[i + 2] = 160;
        }
        var bitmap = BitmapSource.Create(100, 140, 96, 96, PixelFormats.Bgra32, null, pixels, 400);
        bitmap.Freeze();
        var previews = Array.CreateInstance(previewType, 1);
        previews.SetValue(Activator.CreateInstance(previewType, bitmap, 500d, 700d, 1, null), 0);
        foreach (string mode in new[] { "ColorCorrection" })
        {
            var dialog = (Window)Activator.CreateInstance(transformType, dialogOwner, previews,
                Enum.Parse(type.Assembly.GetType("KillerPDF.TransformWindowMode", true)!, mode))!;
            try
            {
                var content = (FrameworkElement)dialog.Content;
                dialog.Content = null;
                Settle(content, 980, 720);
                foreach (var header in Descendants<Border>(content).Where(border =>
                    border.Visibility == Visibility.Visible && border.ActualHeight > 0
                    && border.Child is StackPanel row && row.Children.OfType<TextBlock>().Any(text => text.Text == "▸")))
                    header.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                        System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                        { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                Settle(content, 980, 720);
                var apply = Descendants<Button>(content).Single(button => button.IsDefault);
                var level = (Slider)transformType.GetField(mode == "ColorCorrection" ? "_lvlWhite" : "_rotSlider", Members)!.GetValue(dialog)!;
                double edge = Bounds(apply, content).Right;
                double controlEdge = Bounds(level, content).Right;
                var optionsScroll = Descendants<ScrollViewer>(content).Single(scroll => scroll.Parent is DockPanel);
                var verticalBar = (ScrollBar)optionsScroll.Template.FindName("PART_VerticalScrollBar", optionsScroll);
                var sidebar = (Border)((DockPanel)optionsScroll.Parent).Parent;
                double barInset = Bounds(sidebar, content).Right - Bounds(verticalBar, content).Right;
                if (Math.Abs(barInset) > 1)
                { report.Add($"FAIL sidebar scrollbar is inset {barInset:F1} from its edge"); failures++; }
                report.Add($"{mode} applyRight={edge:F1} controlRight={controlEdge:F1}");
                if (controlEdge > edge + 1) { report.Add("FAIL controls extend beyond action buttons"); failures++; }
                Render(content, Path.Combine(folder, mode + ".png"));
                if (mode == "ColorCorrection")
                {
                    var preview = (Image)transformType.GetField("_preview", Members)!.GetValue(dialog)!;
                    var zoomMethod = transformType.GetMethod("ZoomPreview", Members)!;
                    var center = new Point(preview.ActualWidth / 2, preview.ActualHeight / 2);
                    zoomMethod.Invoke(dialog, [480, center]);
                    Matrix zoomed = ((MatrixTransform)preview.RenderTransform).Matrix;
                    if (zoomed.M11 <= 1 || (zoomed.Transform(center) - center).Length > 1)
                    { report.Add("FAIL preview zoom must preserve the pointer anchor"); failures++; }
                    transformType.GetMethod("UpdatePreview", Members)!.Invoke(dialog, null);
                    if (((MatrixTransform)preview.RenderTransform).Matrix.M11 != zoomed.M11)
                    { report.Add("FAIL correction refresh reset preview zoom"); failures++; }
                    Render(content, Path.Combine(folder, "ColorCorrection-zoom.png"));
                    zoomMethod.Invoke(dialog, [-4800, center]);
                    if (!((MatrixTransform)preview.RenderTransform).Matrix.IsIdentity)
                    { report.Add("FAIL zoom must stop at the fitted preview"); failures++; }
                    report.Add("ColorCorrection pointer anchor, refresh persistence, and minimum zoom checked");
                    var sliders = ((IEnumerable<Slider>)transformType.GetField("_colorAdjustmentSliders", Members)!
                        .GetValue(dialog)!).ToArray();
                    var reset = ((Panel)sliders[0].Parent).Children.OfType<Button>().Single();
                    sliders[0].Value = 0.037;
                    object exposureSettings = transformType.GetProperty("ColorAdjustments", Members)!.GetValue(dialog)!;
                    double exposure = (double)exposureSettings.GetType().GetProperty("Exposure")!.GetValue(exposureSettings)!;
                    if (Math.Abs(exposure - 0.037) > 0.000001 || sliders[0].IsSnapToTickEnabled)
                    { report.Add("FAIL exposure must preserve continuous slider values"); failures++; }
                    reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    double[] values = [-1, 0, 70, -60];
                    for (int i = 0; i < sliders.Length; i++)
                    {
                        sliders[i].Value = values[i];
                        transformType.GetMethod("UpdatePreview", Members)!.Invoke(dialog, null);
                        var adjusted = (BitmapSource)preview.Source;
                        var adjustedPixels = new byte[pixels.Length];
                        adjusted.CopyPixels(adjustedPixels, 400, 0);
                        if (adjustedPixels.SequenceEqual(pixels))
                        { report.Add("FAIL color slider did not change preview: " + i); failures++; }
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(adjusted));
                        using (var stream = File.Create(Path.Combine(folder, "ColorAdjustment-" + i + ".png")))
                            encoder.Save(stream);
                        reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        transformType.GetMethod("UpdatePreview", Members)!.Invoke(dialog, null);
                        if (!ReferenceEquals(preview.Source, bitmap))
                        { report.Add("FAIL color reset must restore source preview"); failures++; }
                    }
                    report.Add("Exposure, saturation, warmth, tint preview changes and reset checked");
                    foreach (string field in new[] { "_autoExposure", "_autoWhiteBalance" })
                    {
                        var check = (CheckBox)transformType.GetField(field, Members)!.GetValue(dialog)!;
                        check.IsChecked = true;
                        bool disabled = field == "_autoExposure" ? !sliders[0].IsEnabled
                            : !sliders[2].IsEnabled && !sliders[3].IsEnabled;
                        if (!disabled) { report.Add("FAIL auto mode must disable its manual sliders"); failures++; }
                        transformType.GetMethod("UpdatePreview", Members)!.Invoke(dialog, null);
                        var autoPixels = new byte[pixels.Length];
                        ((BitmapSource)preview.Source).CopyPixels(autoPixels, 400, 0);
                        if (autoPixels.SequenceEqual(pixels))
                        { report.Add("FAIL auto mode did not adjust the preview"); failures++; }
                        reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (check.IsChecked == true || sliders.Any(slider => !slider.IsEnabled))
                        { report.Add("FAIL reset must clear auto mode and enable manual controls"); failures++; }
                    }
                    report.Add("Automatic exposure and white balance preview and reset checked");
                }
            }
            finally { dialog.Close(); }
        }
        string sourcePath = Path.Combine(folder, "source.pdf");
        byte[] sourceBytes = (byte[])typeof(Program).GetMethod("NewPdf", Members)!.Invoke(null, null)!;
        File.WriteAllBytes(sourcePath, sourceBytes);
        object paneA = window.FindName("Viewer"), paneB = window.FindName("ViewerB");
        Type paneType = paneA.GetType();
        paneType.GetMethod("OpenInNewTabExt", Members)!.Invoke(paneA, [sourcePath]);
        MethodInfo focus = type.GetMethod("FocusPane", Members)!;
        focus.Invoke(window, [paneB]);
        var pagesButton = (Button)window.FindName("SidebarPagesTab");
        if (pagesButton.IsEnabled || pagesButton.Visibility != Visibility.Collapsed)
        { report.Add("FAIL empty pane panel buttons must be hidden"); failures++; }
        focus.Invoke(window, [paneA]);
        if (!pagesButton.IsEnabled || pagesButton.Visibility != Visibility.Visible)
        { report.Add("FAIL document pane must restore panel buttons"); failures++; }
        if ((bool)type.GetField("_sidebarCollapsed", Members)!.GetValue(window)!)
            pagesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        // This window is never shown, so the menus these clicks open cannot appear here.
        // SidebarActionsSmoke checks them in a shown window.
        pagesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if ((bool)type.GetField("_sidebarCollapsed", Members)!.GetValue(window)!)
        { report.Add("FAIL selected panel click must keep sidebar open"); failures++; }
        var rightClick = new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right)
            { RoutedEvent = System.Windows.Input.Mouse.MouseUpEvent };
        pagesButton.RaiseEvent(rightClick);
        if (!rightClick.Handled || (bool)type.GetField("_sidebarCollapsed", Members)!.GetValue(window)!)
        { report.Add("FAIL rail right-click must be handled and keep the sidebar open"); failures++; }
        type.GetField("_isSplit", Members)!.SetValue(window, true);
        string copyPath = Path.Combine(folder, "redacted-copy.pdf");
        type.GetMethod("SaveCopyAndOpen", Members)!.Invoke(window, [copyPath]);
        if (!ReferenceEquals(type.GetProperty("ActiveViewer", Members)!.GetValue(window), paneB)
            || !File.Exists(copyPath) || !File.ReadAllBytes(sourcePath).SequenceEqual(sourceBytes))
        { report.Add("FAIL saved copy must open in empty second pane and preserve source"); failures++; }
        string nextCopy = Path.Combine(folder, "another-copy.pdf");
        type.GetMethod("SaveCopyAndOpen", Members)!.Invoke(window, [nextCopy]);
        var sessions = (System.Collections.ICollection)paneType.GetProperty("SessionsRef", Members)!.GetValue(paneB)!;
        if (sessions.Count != 2 || !ReferenceEquals(type.GetProperty("ActiveViewer", Members)!.GetValue(window), paneB))
        { report.Add("FAIL occupied second pane must use a new tab"); failures++; }
        report.Add("Pane availability transitions, rail right-click, and saved-copy routing checked");
        var panelField = type.GetField("_sidebarPanel", Members)!;
        object previousPanel = panelField.GetValue(window)!;
        var expectedMenus = new Dictionary<string, string[]>
        {
            ["Attachments"] = ["Str_Attachment_Add", "Str_DocumentTools_EditAttachments"],
            ["Comments"] = ["Str_Comments_Add", "Str_DocumentTools_Comments", "Str_Measure_Distance", "Str_Measure_Perimeter",
                "Str_Measure_Area", "Str_Measure_Angle", "Str_Measure_TT_Scale", "Str_DocumentTools_Measurements"],
            ["Layers"] = ["Str_DocumentTools_EditLayers"],
            ["Signatures"] = ["Str_Lbl_DigitalSig", "Str_DocumentTools_Signatures", "Str_CertCatalog_Title"]
        };
        foreach (var (panelName, keys) in expectedMenus)
        {
            panelField.SetValue(window, Enum.Parse(panelField.FieldType, panelName));
            var menu = new ContextMenu();
            type.GetMethod("FillSidebarPanelMenu", Members)!.Invoke(window, [menu, null, null]);
            string[] headers = menu.Items.OfType<MenuItem>().Select(item => item.Header.ToString()!).ToArray();
            string[] expected = keys.Select(key => (string)window.FindResource(key)).ToArray();
            if (!headers.SequenceEqual(expected))
            { report.Add("FAIL context menu contains unrelated actions: " + panelName); failures++; }
        }
        panelField.SetValue(window, previousPanel);
        report.Add("Attachments, Comments, Layers, and Signatures menus checked");
        var renderPage = type.GetMethod("RenderPageBitmap", Members)!;
        static double Brightness(BitmapSource source)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(bytes, converted.PixelWidth * 4, 0);
            double sum = 0;
            for (int i = 0; i < bytes.Length; i += 4) sum += bytes[i] + bytes[i + 1] + bytes[i + 2];
            return sum / (bytes.Length / 4 * 3);
        }
        double originalBrightness = Brightness((BitmapSource)renderPage.Invoke(window, [0, 100, null])!);
        Type settingsType = type.Assembly.GetType("KillerPDF.Services.ColorCorrectionSettings", true)!;
        object settings = Activator.CreateInstance(settingsType, -1d, 1d, 0d, 0d)!;
        type.GetMethod("ApplyPageTransforms", Members)!.Invoke(window,
            [new[] { 0 }, 0d, 1d, false, false, false,
                new Point[] { new(0, 0), new(1, 0), new(1, 1), new(0, 1) }, 0, 255, 1d,
                Enum.Parse(type.Assembly.GetType("KillerPDF.Services.PageColorMode", true)!, "Color"),
                160, 72, false, 85, settings]);
        var frame = new DispatcherFrame();
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if ((bool)type.GetProperty("_isDirty", Members)!.GetValue(window)! || DateTime.UtcNow >= deadline)
                frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        string correctedPath = Path.Combine(folder, "color-corrected.pdf");
        type.GetMethod("SaveCopyAndOpen", Members)!.Invoke(window, [correctedPath]);
        double savedBrightness = Brightness((BitmapSource)renderPage.Invoke(window, [0, 100, null])!);
        if (savedBrightness >= originalBrightness - 10)
        { report.Add("FAIL exposure must survive Apply, saving, and reopening the PDF"); failures++; }
        report.Add($"Exposure PDF round trip brightness: {originalBrightness:F1} to {savedBrightness:F1}");
        report.Add($"failures={failures}");
        File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
        return failures == 0 ? 0 : 1;
    }

    private static void Settle(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        root.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        root.UpdateLayout();
    }

    private static Rect Bounds(FrameworkElement element, Visual relative)
        => element.TransformToAncestor(relative).TransformBounds(new Rect(element.RenderSize));

    private static void Render(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(element), null, new Rect(element.RenderSize));
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
