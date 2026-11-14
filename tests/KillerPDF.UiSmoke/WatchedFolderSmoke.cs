using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

internal static class WatchedFolderSmoke
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    internal static int Run(string appPath, string appXaml, string folder, bool hostOnly = false, bool clearDataOnly = false, bool navigationOnly = false)
    {
        if (!Environment.GetCommandLineArgs().Contains("--startup-smoke"))
            throw new InvalidOperationException("Startup smoke isolation is required.");
        if (Directory.Exists(folder)) throw new IOException("Use a fresh evidence directory.");
        Directory.CreateDirectory(folder);
        if (clearDataOnly)
        {
            string temp = Path.Combine(folder, "temp");
            Directory.CreateDirectory(temp);
            Environment.SetEnvironmentVariable("TEMP", temp);
            Environment.SetEnvironmentVariable("TMP", temp);
        }
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(Path.GetDirectoryName(appPath)!, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        Assembly assembly = Assembly.LoadFrom(appPath);
        Type dialogType = assembly.GetType("KillerPDF.WatchedFolderDialog", true)!;
        Type serviceType = assembly.GetType("KillerPDF.Services.WatchedFolderService", true)!;
        typeof(Application).GetField("_resourceAssembly", Members)!.SetValue(null, assembly);
        assembly.GetType("KillerPDF.Services.AppDataPaths", true)!
            .GetProperty("StartupSmokeRoot", Members)!.SetValue(null, Path.Combine(folder, "data"));
        Type appType = assembly.GetType("KillerPDF.App", true)!;
        var hostType = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("WatchedFolderSmokeHost"),
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
        Window? owner = null, dialog = null;
        IDisposable? service = null;
        var report = new List<string>();
        try
        {
            // App startup order: the kit theme hooks (settings, KillerPDF's own keys) before the services.
            assembly.GetType("KillerPDF.Services.AppTheme", true)!.GetMethod("Configure",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null);
            foreach (string name in new[] { "ThemeManager", "LocaleManager", "FilmGrain" })
                assembly.GetType("KillerPDF.Services." + name, true)!.GetMethod("Initialize", Members)!.Invoke(null, null);
            owner = (Window)Activator.CreateInstance(assembly.GetType("KillerPDF.Shell.MainWindow", true)!)!;
            HideFromDesktop(owner); owner.Show();
            if (navigationOnly)
            {
                WatchedFolderNavigationSmoke.Run(app, owner, Path.Combine(folder, "navigation"));
                return 0;
            }
            if (clearDataOnly)
            {
                WatchedFolderClearDataSmoke.Run(assembly, Path.Combine(folder, "clear-data"));
                return 0;
            }
            if (hostOnly)
            {
                WatchedFolderHostSmoke.Run(assembly, Path.Combine(folder, "host-dispatcher"));
                return 0;
            }
            string input = Path.Combine(folder, "input"), output = Path.Combine(folder, "output");
            Directory.CreateDirectory(input); Directory.CreateDirectory(output);
            byte[] pdf = Fixture();
            string inputFile = Path.Combine(input, "invoice.pdf");
            File.WriteAllBytes(inputFile, pdf);
            string originalHash = Convert.ToHexString(SHA256.HashData(pdf));
            Type macroType = Assembly.Load("KillerPdf.Engine").GetType("KillerPdf.Engine.Documents.PdfMacro", true)!;
            object macro = macroType.GetMethod("FromJson")!.Invoke(null,
                ["{\"version\":1,\"name\":\"Compress\",\"steps\":[{\"operation\":\"optimize\"},{\"operation\":\"save\"}]}"])!;
            object alternative = macroType.GetMethod("FromJson")!.Invoke(null,
                ["{\"version\":1,\"name\":\"Save copy\",\"steps\":[{\"operation\":\"save\"}]}"])!;
            Array macros = Array.CreateInstance(macroType, 2); macros.SetValue(macro, 0); macros.SetValue(alternative, 1);
            service = (IDisposable)Activator.CreateInstance(serviceType,
                [Path.Combine(folder, "queue"), null, null, null])!;
            dialog = (Window)Activator.CreateInstance(dialogType, Members, null, [owner, service, macros, 1d], null)!;
            HideFromDesktop(dialog); dialog.Show(); Pump();
            Find<TextBox>(dialog, "WatchInput").Text = folder;
            BrowseFolder(app, dialog, "WatchInput", input, accept: true, folder);
            BrowseFolder(app, dialog, "WatchInput", output, accept: false, folder);
            Find<TextBox>(dialog, "WatchOutput").Text = folder;
            BrowseFolder(app, dialog, "WatchOutput", output, accept: true, folder);
            report.Add("PASS real folder picker accepts input/output and Cancel preserves the prior input");
            Click(Find<Button>(dialog, "WatchStart")); Pump();
            if (SnapshotPaused(serviceType, service)) throw new InvalidOperationException("Start did not enable processing.");
            ReconcileUntil(serviceType, service, () => Directory.GetFiles(output, "*.pdf").Length != 0);
            string[] outputs = Directory.GetFiles(output, "*.pdf");
            if (outputs.Length != 1) throw new InvalidOperationException("The configured task did not produce exactly one PDF.");
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(inputFile))) != originalHash)
                throw new InvalidOperationException("The original PDF changed.");
            Type documentType = macroType.Assembly.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!;
            object saved = documentType.GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!
                .Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(outputs[0]))])!;
            (saved as IDisposable)?.Dispose();
            Click(Find<Button>(dialog, "WatchPause")); Pump();
            if (!SnapshotPaused(serviceType, service)) throw new InvalidOperationException("Pause was not applied.");
            report.Add("PASS configure, start, valid output, source preservation and pause");
            VerifyChangedSetupCannotResume(dialog, serviceType, service, folder);
            report.Add("PASS changing paused folders, filter, macro or pause-on-error prevents Resume, including a stale click; restoring the saved fields enables Resume");

            string collisionInput = Path.Combine(input, "collision.pdf"), collisionOutput = Path.Combine(output, "collision.pdf");
            byte[] collisionPdf = Fixture("Invoice 0042");
            byte[] occupied = Encoding.ASCII.GetBytes("Existing output must be preserved.");
            File.WriteAllBytes(collisionInput, collisionPdf);
            File.WriteAllBytes(collisionOutput, occupied);
            Click(Find<Button>(dialog, "WatchResume"));
            ReconcileUntil(serviceType, service, () => HasJob(serviceType, service, "Failed"));
            Wait(() => Find<Button>(dialog, "WatchRetry").IsEnabled);
            Click(Find<Button>(dialog, "WatchPause"));
            if (!File.ReadAllBytes(collisionOutput).AsSpan().SequenceEqual(occupied))
                throw new InvalidOperationException("An existing output was overwritten.");
            Wait(() => Find<ListBox>(dialog, "WatchResults").Items.Cast<object>().Any(item =>
                item.ToString()?.Contains((string)app.FindResource("Str_Watch_OutputExists"), StringComparison.Ordinal) == true));
            Find<ListBox>(dialog, "WatchResults").BringIntoView(); Pump();
            Capture(dialog, Path.Combine(folder, "collision-paused.png"));
            string preservedOutput = Path.Combine(folder, "preserved-existing-output.pdf");
            File.Move(collisionOutput, preservedOutput);
            Click(Find<Button>(dialog, "WatchRetry"));
            Click(Find<Button>(dialog, "WatchResume"));
            ReconcileUntil(serviceType, service, () => File.Exists(collisionOutput));
            Click(Find<Button>(dialog, "WatchPause"));
            if (!File.ReadAllBytes(collisionInput).AsSpan().SequenceEqual(collisionPdf)
                || !File.ReadAllBytes(preservedOutput).AsSpan().SequenceEqual(occupied))
                throw new InvalidOperationException("Retry changed a source or the preserved existing output.");
            report.Add("PASS output collision is visible, preserves existing bytes, and retries after the collision is resolved");
            dialog.Close(); dialog = null;
            service.Dispose();
            service = (IDisposable)Activator.CreateInstance(serviceType, [Path.Combine(folder, "queue"), null, null, null])!;
            if (!SnapshotPaused(serviceType, service)) throw new InvalidOperationException("Pause was not retained after reopening.");
            report.Add("PASS pause is retained after reopening the queue");
            object hosted = appType.GetMethod("GetFolderAutomation", Members)!.Invoke(null, null)!;
            string hostedInput = Path.Combine(folder, "host-input"), hostedOutput = Path.Combine(folder, "host-output");
            Directory.CreateDirectory(hostedInput); Directory.CreateDirectory(hostedOutput);
            dialog = (Window)Activator.CreateInstance(dialogType, Members, null, [owner, hosted, macros, 1d], null)!;
            HideFromDesktop(dialog); dialog.Show(); Pump();
            Find<TextBox>(dialog, "WatchInput").Text = hostedInput;
            Find<TextBox>(dialog, "WatchOutput").Text = hostedOutput;
            Click(Find<Button>(dialog, "WatchStart"));
            if (SnapshotPaused(serviceType, hosted)) throw new InvalidOperationException("The app-hosted worker did not start.");
            Click(Find<Button>(dialog, "WatchClose")); dialog = null;
            owner.Hide();
            string hostedSource = Path.Combine(hostedInput, "automatic.pdf");
            File.WriteAllBytes(hostedSource, pdf);
            string hostedResult = Path.Combine(hostedOutput, "automatic.pdf");
            Wait(() => File.Exists(hostedResult));
            if (owner.IsVisible || !File.ReadAllBytes(hostedSource).AsSpan().SequenceEqual(pdf))
                throw new InvalidOperationException("Background processing changed the source or required the window to be visible.");
            object hostedSaved = documentType.GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!
                .Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(hostedResult))])!;
            (hostedSaved as IDisposable)?.Dispose();
            owner.Show();
            dialog = (Window)Activator.CreateInstance(dialogType, Members, null, [owner, hosted, macros, 1d], null)!;
            HideFromDesktop(dialog); dialog.Show(); Pump();
            Wait(() => Find<ListBox>(dialog, "WatchResults").Items.Cast<object>().Any(item =>
                item.ToString()?.Contains("automatic.pdf", StringComparison.Ordinal) == true));
            Find<ListBox>(dialog, "WatchResults").BringIntoView(); Pump();
            Capture(dialog, Path.Combine(folder, "app-hosted-completed.png"));
            string lockedSource = Path.Combine(hostedInput, "locked-copy.pdf");
            string lockedOutput = Path.Combine(hostedOutput, "locked-copy.pdf");
            File.WriteAllBytes(lockedSource, Fixture("Locked copy"));
            using (var held = new FileStream(lockedSource, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Wait(() => Find<TextBlock>(dialog, "WatchError").Text == (string)app.FindResource("Str_Watch_SourceChanged"));
                if (File.Exists(lockedOutput)) throw new InvalidOperationException("A locked input was processed before the copy completed.");
            }
            Wait(() => File.Exists(lockedOutput));
            Wait(() => string.IsNullOrEmpty(Find<TextBlock>(dialog, "WatchError").Text));
            report.Add("PASS a locked copy waits, processes automatically after release, and clears the recovered error");
            Click(Find<Button>(dialog, "WatchPause"));
            if (!SnapshotPaused(serviceType, hosted)) throw new InvalidOperationException("The app-hosted worker did not pause.");
            report.Add("PASS app background loop processes a newly added PDF while the main window is hidden, with no manual reconciliation");
            dialog.Close(); dialog = null;
            VerifyLayouts(assembly, dialogType, owner, service, macros, folder, report);
            WatchedFolderHostSmoke.Run(assembly, Path.Combine(folder, "host-dispatcher"));
            report.Add("App SHA256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath))));
            report.Add("UI actions use routed control events; native keyboard input was not tested.");
            File.WriteAllLines(Path.Combine(folder, "result.txt"), report);
            Console.WriteLine(string.Join(Environment.NewLine, report));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllLines(Path.Combine(folder, "partial-results.txt"), report);
            File.WriteAllText(Path.Combine(folder, "failure.txt"), error.ToString());
            throw;
        }
        finally
        {
            dialog?.Close(); service?.Dispose();
            owner?.Hide(); app.Shutdown();
        }
    }

    private static bool SnapshotPaused(Type type, object service)
    {
        object snapshot = type.GetMethod("GetSnapshot")!.Invoke(service, null)!;
        return (bool)snapshot.GetType().GetProperty("IsPaused")!.GetValue(snapshot)!;
    }
    private static void VerifyChangedSetupCannotResume(Window dialog, Type serviceType, object service, string folder)
    {
        TextBox input = Find<TextBox>(dialog, "WatchInput"), output = Find<TextBox>(dialog, "WatchOutput");
        TextBox filter = Find<TextBox>(dialog, "WatchFilter");
        ComboBox macro = Find<ComboBox>(dialog, "WatchMacro");
        CheckBox pauseOnError = Find<CheckBox>(dialog, "WatchPauseOnError");
        Button resume = Find<Button>(dialog, "WatchResume");
        string savedInput = input.Text, savedOutput = output.Text, savedFilter = filter.Text;
        int savedMacro = macro.SelectedIndex;
        bool? savedPause = pauseOnError.IsChecked;
        void Restore()
        {
            input.Text = savedInput; output.Text = savedOutput; filter.Text = savedFilter;
            macro.SelectedIndex = savedMacro; pauseOnError.IsChecked = savedPause;
        }
        void Check(string name, Action change)
        {
            Wait(() => resume.IsEnabled);
            change();
            bool offeredResume = resume.IsEnabled;
            Click(resume);
            if (!SnapshotPaused(serviceType, service))
                throw new InvalidOperationException("Resume ran the saved setup while the displayed " + name + " had unsaved changes.");
            if (offeredResume || resume.IsEnabled)
                throw new InvalidOperationException("Resume remained available after changing the displayed " + name + ".");
            Restore();
            Wait(() => resume.IsEnabled);
        }
        try
        {
            Check("input folder", () => input.Text = Path.Combine(folder, "changed-input"));
            Check("output folder", () => output.Text = Path.Combine(folder, "changed-output"));
            Check("file filter", () => filter.Text = "invoice*.pdf");
            Check("macro", () => macro.SelectedIndex = 1);
            Check("pause-on-error option", () => pauseOnError.IsChecked = savedPause != true);
            input.Text = savedInput.ToUpperInvariant(); output.Text = savedOutput.ToUpperInvariant();
            Pump();
            if (!resume.IsEnabled) throw new InvalidOperationException("Changing only folder letter case disabled Resume for the same Windows folders.");
        }
        finally { Restore(); }
        Wait(() => resume.IsEnabled);
    }
    private static bool HasJob(Type type, object service, string status)
    {
        object snapshot = type.GetMethod("GetSnapshot")!.Invoke(service, null)!;
        var jobs = (System.Collections.IEnumerable)snapshot.GetType().GetProperty("Jobs")!.GetValue(snapshot)!;
        return jobs.Cast<object>().Any(job => job.GetType().GetProperty("Status")!.GetValue(job)!.ToString() == status);
    }
    private static void ReconcileUntil(Type type, object service, Func<bool> condition)
    {
        MethodInfo reconcile = type.GetMethod("ReconcileAsync")!;
        Task processing = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 20 && !condition(); attempt++)
            {
                await (Task)reconcile.Invoke(service, [CancellationToken.None])!;
                await Task.Delay(500);
            }
            if (!condition()) throw new TimeoutException("Watched-folder reconciliation did not reach its expected result.");
        });
        Wait(() => processing.IsCompleted); processing.GetAwaiter().GetResult();
    }
    private static void BrowseFolder(Application app, Window dialog, string fieldId, string target, bool accept, string evidence)
    {
        TextBox field = Find<TextBox>(dialog, fieldId);
        string previous = field.Text;
        bool answered = false, navigated = false;
        Exception? failure = null;
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(20))
                    throw new TimeoutException(navigated ? "The folder picker did not list the folder." : "The folder picker did not open.");
                Window? picker = app.Windows.Cast<Window>().FirstOrDefault(window => window.GetType().FullName == "KillerPDF.Controls.FileDialog" && window.IsVisible);
                if (picker is null || !picker.IsLoaded) return;
                TextBox path = (TextBox)picker.FindName("PathBox");
                if (!navigated)
                {
                    path.Text = target;
                    path.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(picker), Environment.TickCount, Key.Enter)
                        { RoutedEvent = Keyboard.KeyDownEvent });
                    navigated = true;
                    return;
                }
                // The folder is listed off the UI thread, and OK does nothing until it has been.
                Type pickerType = picker.GetType();
                if ((bool)pickerType.GetProperty("IsDirectoryListingBusy")!.GetValue(picker)!
                    || !(bool)pickerType.GetProperty("IsDirectoryListingAvailable")!.GetValue(picker)!) return;
                picker.UpdateLayout();
                Capture(picker, Path.Combine(evidence, fieldId + (accept ? "-picker-accept.png" : "-picker-cancel.png")));
                Button button = accept ? (Button)picker.FindName("AcceptButton")
                    : Descendants<Button>(picker).Single(item => Equals(item.Content, app.FindResource("Str_Btn_Cancel")));
                answered = true; timer.Stop(); Click(button);
            }
            catch (Exception error)
            {
                failure = error; timer.Stop();
                foreach (Window window in app.Windows.Cast<Window>().Where(window => window.GetType().FullName == "KillerPDF.Controls.FileDialog").ToArray()) window.Close();
            }
        };
        timer.Start();
        try
        {
            Button browse = Find<Button>(dialog, fieldId + "Browse");
            browse.BringIntoView(); Pump(); Click(browse);
            if (failure is not null) throw failure;
            if (!answered) throw new InvalidOperationException("Browse skipped the folder picker.");
            if (!string.Equals(field.Text, accept ? target : previous, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Folder picker " + (accept ? "accept" : "cancel") + " returned an unexpected path.");
        }
        finally { timer.Stop(); }
    }

    private static void VerifyLayouts(Assembly assembly, Type dialogType, Window owner, object service,
        Array macros, string folder, List<string> report)
    {
        Type themeType = assembly.GetType("KillerPDF.Services.Theme", true)!;
        Type localeType = assembly.GetType("KillerPDF.Services.Locale", true)!;
        MethodInfo applyTheme = assembly.GetType("KillerPDF.Services.ThemeManager", true)!.GetMethod("Apply", Members)!;
        MethodInfo applyLocale = assembly.GetType("KillerPDF.Services.LocaleManager", true)!.GetMethod("Apply", Members)!;
        var failures = new List<string>();
        int passed = 0;
        void Layout(string name, double scale, int width, int height)
        {
            Window? dialog = null;
            try
            {
                dialog = (Window)Activator.CreateInstance(dialogType, Members, null, [owner, service, macros, scale], null)!;
                HideFromDesktop(dialog); dialog.Show();
                dialog.Width = width; dialog.Height = height;
                Wait(() => dialog.IsLoaded && dialog.Opacity >= .99);
                dialog.UpdateLayout(); Pump();
                ScrollViewer scroll = Descendants<ScrollViewer>(dialog).Single(view => view.Content is StackPanel);
                if (scroll.ViewportHeight < 24) throw new InvalidOperationException($"Scrollable settings viewport is only {scroll.ViewportHeight:F2} logical pixels high.");
                scroll.ScrollToTop(); Pump();
                Capture(dialog, Path.Combine(folder, name + "-top.png"));
                // Close is in the footer row; Pause, Resume and Retry are icons on the file results, inside the scroll.
                AssertControl(dialog, Find<Button>(dialog, "WatchClose"));
                foreach (string id in new[] { "WatchInput", "WatchInputBrowse", "WatchOutput", "WatchOutputBrowse",
                    "WatchFilter", "WatchMacro", "WatchPauseOnError", "WatchStart", "WatchPause", "WatchResume", "WatchRetry" })
                {
                    Control control = Find<Control>(dialog, id);
                    control.BringIntoView(); Pump();
                    AssertControl(dialog, control);
                }
                ListBox results = Find<ListBox>(dialog, "WatchResults");
                results.BringIntoView(); Pump();
                if (!results.IsVisible || results.ActualWidth <= 0 || results.ActualHeight <= 0)
                    throw new InvalidOperationException("Results are not reachable through the settings scroll area.");
                scroll.ScrollToBottom(); Pump();
                Capture(dialog, Path.Combine(folder, name + "-bottom.png"));
                File.WriteAllText(Path.Combine(folder, name + ".txt"), $"PASS {width}x{height}, scale {scale:F1}; settings viewport {scroll.ViewportWidth:F2}x{scroll.ViewportHeight:F2}; control names, tab eligibility, bounds and unwrapped label widths.");
                passed++;
            }
            catch (Exception error)
            {
                failures.Add(name + ": " + error.Message);
                File.WriteAllText(Path.Combine(folder, name + ".txt"), error.ToString());
                if (dialog is not null)
                    try { Capture(dialog, Path.Combine(folder, name + "-failure.png")); } catch { }
            }
            finally { dialog?.Close(); Pump(); }
        }
        try
        {
            applyLocale.Invoke(null, [Enum.Parse(localeType, "EnUS")]);
            foreach (string theme in Enum.GetNames(themeType))
            {
                applyTheme.Invoke(null, [Enum.Parse(themeType, theme)]);
                foreach (double scale in new[] { 1d, 1.5 })
                    Layout($"theme-{theme}-{(int)(scale * 100)}", scale, 820, 760);
            }
            applyTheme.Invoke(null, [Enum.Parse(themeType, "Dark")]);
            foreach (string locale in Enum.GetNames(localeType))
            {
                applyLocale.Invoke(null, [Enum.Parse(localeType, locale)]);
                foreach (double scale in new[] { 1d, 1.5 })
                    Layout($"locale-{locale}-{(int)(scale * 100)}", scale, 820, 760);
            }
            foreach (string locale in new[] { "EnUS", "De" })
            {
                applyLocale.Invoke(null, [Enum.Parse(localeType, locale)]);
                foreach (double scale in new[] { 1d, 1.5 })
                    Layout($"minimum-{locale}-{(int)(scale * 100)}", scale, 480, 380);
            }
        }
        finally
        {
            applyLocale.Invoke(null, [Enum.Parse(localeType, "EnUS")]);
            applyTheme.Invoke(null, [Enum.Parse(themeType, "Dark")]);
        }
        report.Add($"Layout cases passed: {passed}; failed: {failures.Count}; {Enum.GetNames(themeType).Length} themes and {Enum.GetNames(localeType).Length} locales at 100% and 150%, plus English/German minimum-size cases.");
        if (failures.Count > 0)
        {
            File.WriteAllLines(Path.Combine(folder, "layout-failures.txt"), failures);
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        }
    }

    private static void AssertControl(Window window, Control control)
    {
        string name = AutomationProperties.GetName(control);
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("Str_", StringComparison.Ordinal))
            throw new InvalidOperationException(AutomationProperties.GetAutomationId(control) + " has no localized accessible name.");
        if (!control.IsTabStop) throw new InvalidOperationException(AutomationProperties.GetAutomationId(control) + " is not eligible for keyboard tab navigation.");
        AssertVisible(window, control);
        foreach (TextBlock text in Descendants<TextBlock>(control).Where(text => text.IsVisible && !string.IsNullOrEmpty(text.Text)))
        {
            AssertVisible(window, text);
            if (text.TextWrapping != TextWrapping.NoWrap || text.TextTrimming != TextTrimming.None) continue;
            var formatted = new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentUICulture,
                text.FlowDirection, new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                text.FontSize, Brushes.Black, null, TextOptions.GetTextFormattingMode(text),
                VisualTreeHelper.GetDpi(text).PixelsPerDip);
            if (formatted.WidthIncludingTrailingWhitespace > text.ActualWidth + 1)
                throw new InvalidOperationException($"{AutomationProperties.GetAutomationId(control)} label is clipped: text needs {formatted.WidthIncludingTrailingWhitespace:F2}, available {text.ActualWidth:F2} logical pixels.");
        }
    }

    private static void AssertVisible(Window window, FrameworkElement element)
    {
        Rect bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
        var available = new Rect(0, 0, window.ActualWidth, window.ActualHeight);
        available.Inflate(.5, .5);
        if (!element.IsVisible || bounds.Width <= 0 || bounds.Height <= 0 || !available.Contains(bounds))
            throw new InvalidOperationException($"{AutomationProperties.GetAutomationId(element)} ({element.GetType().Name}) is outside the window: {bounds}; available {available}.");
        for (DependencyObject? ancestor = VisualTreeHelper.GetParent(element); ancestor is FrameworkElement parent && parent != window;
            ancestor = VisualTreeHelper.GetParent(parent))
        {
            Geometry? geometry = VisualTreeHelper.GetClip(parent);
            if (geometry is null && !parent.ClipToBounds) continue;
            Rect clip = parent.TransformToAncestor(window).TransformBounds(geometry?.Bounds ?? new Rect(parent.RenderSize));
            clip.Inflate(.75, .75);
            if (!clip.Contains(bounds))
                throw new InvalidOperationException($"{AutomationProperties.GetAutomationId(element)} ({element.GetType().Name}) is clipped by {parent.GetType().Name}: {bounds}; clip {clip}.");
        }
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); png.Save(file);
    }
    private static void HideFromDesktop(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000; window.Top = -10000; window.ShowInTaskbar = false; window.ShowActivated = false;
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Wait(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { Pump(); if (clock.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException(); Thread.Sleep(10); }
    }
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        Descendants<T>(root).Single(item => AutomationProperties.GetAutomationId(item) == id);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T item) yield return item;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static byte[] Fixture(string label = "Invoice 0012")
    {
        string content = $"BT /F1 12 Tf 20 100 Td ({label}) Tj ET\n";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {content.Length} >>\nstream\n{content}endstream"];
        var pdf = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int> { 0 };
        for (int index = 0; index < objects.Length; index++) { offsets.Add(pdf.Length); pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); }
        int xref = pdf.Length; pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
