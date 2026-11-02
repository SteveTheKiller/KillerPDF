using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using WpfApplication = System.Windows.Application;
using AutomationElement = FlaUI.Core.AutomationElements.AutomationElement;

internal static class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr desktop);

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            if (args.Length == 2 && args[0] is "--verify-thumbnail-fixture" or "--verify-orientation-fixture")
            {
                string enginePath = Path.GetFullPath(args[1]);
                Assembly? ResolveFixtureAssembly(AssemblyLoadContext context, AssemblyName name)
                {
                    string dependency = Path.Combine(Path.GetDirectoryName(enginePath)!, name.Name + ".dll");
                    return File.Exists(dependency) ? context.LoadFromAssemblyPath(dependency) : null;
                }
                AssemblyLoadContext.Default.Resolving += ResolveFixtureAssembly;
                try
                {
                    Assembly engine = AssemblyLoadContext.Default.LoadFromAssemblyPath(enginePath);
                    if (args[0] == "--verify-orientation-fixture") return OrientationFailureSmoke.VerifyFixture(engine);
                    Type documentType = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", throwOnError: true)!;
                    object document = documentType.GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!
                        .Invoke(null, [new ReadOnlyMemory<byte>(ThumbnailBoxGutterSmoke.LongFixture())])!;
                    var pages = (System.Collections.IEnumerable)engine.GetType("KillerPdf.Engine.Documents.PdfPageInformation", true)!
                        .GetMethod("Read", [documentType])!.Invoke(null, [document])!;
                    int count = pages.Cast<object>().Count();
                    if (count != 80) throw new InvalidOperationException("The thumbnail fixture did not strictly reopen with eighty pages.");
                    Console.WriteLine("PASS thumbnail fixture: strict public engine reopened eighty pages without creating a Window.");
                    return 0;
                }
                finally { AssemblyLoadContext.Default.Resolving -= ResolveFixtureAssembly; }
            }
            if (args.Length == 1 && args[0] == "--verify-print-color-probe")
                return PrintVisibilitySmoke.VerifyColorProbe();
            if (args.Length == 2 && args[0] == "--request-process-close")
            {
                using var target = Process.GetProcessById(int.Parse(args[1]));
                Console.WriteLine("Native close target HWND=" + target.MainWindowHandle);
                if (target.MainWindowHandle == IntPtr.Zero)
                    throw new InvalidOperationException("The fixture process has no eligible native main window.");
                if (!target.CloseMainWindow()) throw new InvalidOperationException("The prepared native window refused CloseMainWindow.");
                Console.WriteLine("PASS native updater close request");
                return 0;
            }
            if (args.Length == 6 && args[0].StartsWith("--verify-page-navigation-", StringComparison.Ordinal)
                && args[5] == "--session-isolation")
                return TableExportSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]),
                    windowSession: true, windowSessionCase: args[0], updateRestartState: Path.GetFullPath(args[4]));
            if (args.Length == 6 && args[0] is "--verify-update-restart-stage" or "--verify-update-restart-consume" or "--verify-update-restart-early-exit"
                && args[5] == "--session-isolation")
                return TableExportSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]),
                    windowSession: true, windowSessionCase: args[0], updateRestartState: Path.GetFullPath(args[4]));
            if (args.Length == 5 && (args[0].StartsWith("--verify-validate-profile-case-", StringComparison.Ordinal)
                || args[0].StartsWith("--verify-delete-nearest-case-", StringComparison.Ordinal)
                || args[0].StartsWith("--verify-page-straighten-", StringComparison.Ordinal)
                || args[0].StartsWith("--verify-file-insertion-", StringComparison.Ordinal)
                || args[0] == "--verify-picker-navigation-recovery"
                || args[0] is "--verify-page-reorder-failure" or "--verify-page-transform-form-failure"
                || args[0] == "--verify-page-navigation"
                || args[0] is "--verify-watch-job-failure" or "--verify-watch-unsupported-step"
                || args[0] is "--verify-page-delete-failure" or "--verify-page-transform-failure" or "--verify-page-transform-cancel" or "--verify-page-transform-save-control" or "--verify-page-transform-success" or "--verify-range-extract-pending" or "--verify-page-straighten-failure")
                && args[4] == "--session-isolation")
                return TableExportSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]),
                    windowSession: true, windowSessionCase: args[0]);
            if (args.Length == 5 && args[0] is "--verify-window-session" or "--verify-window-session-secondary-first"
                    or "--verify-window-session-manual-close" or "--verify-window-session-forget" or "--verify-window-session-close-all"
                    or "--verify-session-ending" or "--verify-session-ending-save" or "--verify-session-ending-controls" or "--verify-watch-readiness" or "--verify-paragraph-refusal" or"--verify-search-prerequisites" or "--verify-search-certificates" or "--verify-search-scan" or "--verify-search-office" or "--verify-shortcut-hints"
                    or "--verify-update-window-state" or "--verify-print-visibility" or "--verify-export-loss-mixed" or "--verify-export-loss-continue" or "--verify-export-lossless"
                    or "--verify-save-history" or "--verify-active-editor-close" or "--verify-annotation-history" or "--verify-page-edit-failure" or "--verify-window-collection" or "--verify-layout-pending" or "--verify-merge-pending" or "--verify-layouts-pending" or "--verify-layer-isolation" or "--verify-validate-profile" or "--verify-form-format"
                    or "--verify-form-history" or "--verify-stamp-isolation" or "--verify-failed-save-history" or "--verify-stamp-history" or "--verify-stamp-tabs" or "--verify-stamp-panes" or "--verify-stamp-tearout" or "--verify-stamp-saveall" or "--verify-active-editor-close-b" or "--verify-form-status" or "--verify-form-total" or "--verify-delete-nearest" or "--verify-export-loss-review"
                && args[4] == "--session-isolation")
                return TableExportSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), windowSession: true, windowSessionCase: args[0]);
            if (args.Length == 5 && args[0] == "--verify-window-lifecycle" && args[4] == "--startup-smoke")
                return TableExportSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), windowLifecycle: true);
            if (args.Length == 5 && args[0] == "--verify-viewer-parity" && args[4] == "--startup-smoke")
                return ViewerParitySmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
            if (args.Length == 5 && args[0] == "--verify-certificates" && args[4] == "--startup-smoke")
                return CertificateCatalogSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
            if (args.Length == 5 && args[0] == "--verify-toolbar-availability" && args[4] == "--startup-smoke")
                return TableExportSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), toolbarAvailability: true);
            if (args.Length == 5 && (args[0] is "--verify-render-recovery" or "--verify-pending-text-recovery" or "--verify-multiwindow-recovery" or "--verify-signature-keyboard" or "--verify-undo-storage-failure" or "--verify-japanese-selection" or "--verify-dirty-open-viewport" or "--verify-cross-mode-zoom"
                or "--verify-cross-mode-zoom-reverse" or "--verify-cross-mode-zoom-twopage" or "--verify-cross-mode-fit-page"
                or "--verify-cross-mode-fit-width" or "--verify-cross-mode-grid" or "--verify-zoom-close-writer"
                or "--verify-cross-mode-zoom-clamp" or "--verify-cross-mode-zoom-reassert"
                or "--verify-cross-mode-zoom-navigate" or "--verify-cross-mode-zoom-wheel"
                or "--verify-cross-mode-zoom-button" or "--verify-cross-mode-zoom-sequence"
                or "--verify-cross-mode-fit-page-pending" or "--verify-cross-mode-fit-width-pending"
                or "--verify-cross-mode-fit-zoom-pending" or "--verify-cross-mode-zoom-fit-pending"
                or "--verify-cached-fit-zoom-pending" or "--verify-cross-mode-zoom-compare-fit"
                or "--verify-cross-mode-fit-page-twopage-pending" or "--verify-two-page-fit-after-mode-switch"
                or "--verify-two-page-zoom-after-mode-switch") && args[4] == "--startup-smoke")
                return TableExportSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), renderRecovery: true);
            if (args.Length == 5 && args[0] == "--verify-reference-repair-notice" && args[4] == "--startup-smoke")
                return TableExportSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), referenceRepairNotice: true);
            if (args.Length == 5 && args[0] == "--verify-reduction-repair-notice" && args[4] == "--startup-smoke")
                return TableExportSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), reductionRepairNotice: true);
            if (args.Length == 5 && args[0] == "--verify-signed-copy" && args[4] == "--startup-smoke")
                return TableExportSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), signedCopy: true);
            if (args.Length == 5 && args[0] == "--verify-guided-tasks" && args[4] == "--startup-smoke")
                return TableExportSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), guidedTasks: true);
            if (args.Length == 5 && args[0] == "--verify-form-field-editing" && args[4] == "--startup-smoke")
                return TableExportSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), formFieldEditing: true);
            if (args.Length == 5 && args[0] == "--verify-accessibility-review" && args[4] == "--startup-smoke")
                return TableExportSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), accessibilityReview: true);
            if (args.Length == 5 && args[0] == "--verify-paragraph-edit" && args[4] == "--startup-smoke")
                return TableExportSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), paragraphEdit: true);
            if (args.Length == 5 && args[0] == "--verify-sidebar-actions" && args[4] == "--startup-smoke")
                return TableExportSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), sidebarActions: true);
            if (args.Length == 5 && args[0] == "--verify-dialog-text" && args[4] == "--startup-smoke")
                return TableExportSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), dialogText: true);
            if (args.Length == 5 && args[0] == "--verify-grain-themes" && args[4] == "--startup-smoke")
                return TableExportSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), grainThemes: true);
            if (args.Length == 5 && args[0] == "--verify-tray" && args[4] == "--startup-smoke")
                return TraySmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
            if (args.Length == 5 && args[0] == "--verify-tray-session" && args[4] == "--startup-smoke")
                return TraySmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), sessionOnly: true);
            if (args.Length == 5 && args[0] == "--verify-watched-folder" && args[4] == "--startup-smoke")
                return WatchedFolderSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
            if (args.Length == 5 && args[0] == "--verify-watched-host" && args[4] == "--startup-smoke")
                return WatchedFolderSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), hostOnly: true);
            if (args.Length == 5 && args[0] == "--verify-watched-clear" && args[4] == "--startup-smoke")
                return WatchedFolderSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), clearDataOnly: true);
            if (args.Length == 5 && args[0] == "--verify-watched-navigation" && args[4] == "--startup-smoke")
                return WatchedFolderSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), navigationOnly: true);
            if (args.Length == 5 && args[0] == "--verify-table-export" && args[4] == "--startup-smoke")
                return TableExportSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
            if (args.Length == 5 && args[0] == "--verify-office-export" && args[4] == "--startup-smoke")
                return TableExportSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), officeExport: true);
            if (args.Length == 5 && args[0] == "--verify-toolbar" && args[4] == "--startup-smoke")
                return ScrollOverlaySmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), toolbar: true);
            if (args.Length == 5 && args[0] == "--verify-scroll-overlays" && args[4] == "--startup-smoke")
                return ScrollOverlaySmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
            if (args.Length == 5 && args[0] == "--verify-presets" && args[4] == "--startup-smoke")
                return PresetSelectionSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
            if (args.Length == 4 && args[0] == "--host")
                return Host(Path.GetFullPath(args[1]), args[2], Path.GetFullPath(args[3]), false);
            if (args.Length == 3 && args[0] == "--verify-fixture")
                return Host(Path.GetFullPath(args[1]), "Dark", Path.GetFullPath(args[2]), true);
            if (args.Length != 3 || args[0] != "--interactive")
                throw new ArgumentException("Usage: KillerPDF.UiSmoke --interactive APP_DLL OUTPUT_DIRECTORY (unlocked Windows desktop required), or --verify-fixture APP_DLL OUTPUT_DIRECTORY.");
            RequireDesktop();
            string assembly = Path.GetFullPath(args[1]);
            string output = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(output);
            using var buildLock = new Mutex(false, @"Local\KillerFamilyBuildVerification");
            if (!buildLock.WaitOne(0)) throw new InvalidOperationException("A family build is active. Run UI smoke after it completes.");
            try
            {
                foreach (string theme in new[] { "Dark", "Light", "SE98" })
                    RunTheme(assembly, theme, output);
            }
            finally { buildLock.ReleaseMutex(); }
            Console.WriteLine("PASS interactive stamp acceptance, cancellation, and working-document save/reopen in Dark, Light, and SE98.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + error);
            return 1;
        }
    }

    private static void RequireDesktop()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive ||
            Process.GetCurrentProcess().SessionId == 0)
            throw new InvalidOperationException("An interactive Windows session is required; hosted CI does not run this suite.");
        IntPtr desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == IntPtr.Zero)
            throw new InvalidOperationException("The input desktop is unavailable. Unlock the session before running smoke tests.");
        CloseDesktop(desktop);
        if (Process.GetProcessesByName("KillerPDF").Length != 0 ||
            Process.GetProcessesByName("KillerPDF.App").Length != 0)
            throw new InvalidOperationException("Close the existing PDF session before running smoke tests; no user app is terminated.");
    }

    private static void RunTheme(string assembly, string theme, string output)
    {
        string folder = Path.Combine(output, theme + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string arg in new[] { "--host", assembly, theme, folder }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated dialog host.");
        try
        {
            using var automation = new UIA3Automation
            {
                ConnectionTimeout = TimeSpan.FromSeconds(2),
                TransactionTimeout = TimeSpan.FromSeconds(3)
            };
            AutomationElement Find(string id)
            {
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(15))
                {
                    if (process.HasExited) throw new InvalidOperationException("Dialog host exited: " + ReadFailure(folder));
                    var element = automation.GetDesktop().FindFirstDescendant(
                        automation.ConditionFactory.ByProcessId(process.Id).And(automation.ConditionFactory.ByAutomationId(id)));
                    if (element is not null) return element;
                    Thread.Sleep(100);
                }
                var tree = new List<string> { "pid=" + process.Id + "; client-apartment=" + Thread.CurrentThread.GetApartmentState() };
                foreach (var root in automation.GetDesktop().FindAllChildren(automation.ConditionFactory.ByProcessId(process.Id)))
                {
                    tree.Add($"root {root.ControlType}; pid={root.Properties.ProcessId.ValueOrDefault}; id={root.AutomationId}; name={root.Name}; class={root.ClassName}");
                    foreach (var child in root.FindAllDescendants().Take(200))
                        tree.Add($"  {child.ControlType}; pid={child.Properties.ProcessId.ValueOrDefault}; id={child.AutomationId}; name={child.Name}; class={child.ClassName}");
                }
                File.WriteAllLines(Path.Combine(folder, "uia-timeout-" + id + ".txt"), tree);
                throw new TimeoutException("UI Automation element unavailable: " + id + ". " + ReadFailure(folder));
            }
            Find("ShowStamp").AsButton().Invoke();
            Find("StampDialog");
            Pick("StampPageNumberColor", "#123456", "PickerOK");
            Pick("StampWatermarkColor", "#ABCDEF", "PickerOK");
            // Cancel must preserve the previously accepted page-number color.
            Pick("StampPageNumberColor", "#654321", "PickerCancel");
            Find("StampApply").AsButton().Invoke();
            var watch = Stopwatch.StartNew();
            while (Find("SmokeStatus").Name != "applied:#FF123456:#FFABCDEF")
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(10))
                    throw new InvalidOperationException("Stamp colors were not preserved: " + Find("SmokeStatus").Name);
                Thread.Sleep(100);
            }
            Find("SaveReopen").AsButton().Invoke();
            watch.Restart();
            while (Find("SmokeStatus").Name != "saved:1")
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(10))
                    throw new InvalidOperationException("Save/reopen did not finish: " + Find("SmokeStatus").Name);
                Thread.Sleep(100);
            }
            Console.WriteLine("PASS " + theme + "; app sha256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))) + "; evidence=" + folder);
            void Pick(string button, string value, string close)
            {
                Find(button).AsButton().Invoke();
                var hex = Find("PickerHex").AsTextBox();
                hex.Focus();
                hex.Text = value;
                var action = Find(close).AsButton();
                action.Focus(); // The real field commits hex edits on LostFocus.
                action.Invoke();
                // Modal windows close through an animation. Await the next enabled parent.
                var wait = Stopwatch.StartNew();
                while (!Find("StampApply").IsEnabled)
                {
                    if (wait.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Picker did not close.");
                    Thread.Sleep(100);
                }
            }
        }
        finally
        {
            // Terminate only the child host created by this case, including any modal windows.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000)) throw new TimeoutException("Owned dialog host did not exit: " + process.Id);
            }
        }
    }

    private static string ReadFailure(string folder) =>
        File.Exists(Path.Combine(folder, "host-error.txt")) ? File.ReadAllText(Path.Combine(folder, "host-error.txt")) : "See retained host evidence.";

    private static int Host(string appPath, string theme, string folder, bool fixtureOnly)
    {
        Directory.CreateDirectory(folder);
        // App settings resolve to this synthetic portable launcher instead of the user's registry.
        string launcher = Path.Combine(folder, "fixture-launcher.exe");
        File.WriteAllBytes(launcher, []);
        Environment.SetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH", launcher);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string candidate = Path.Combine(Path.GetDirectoryName(appPath)!, name.Name + ".dll");
            return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
        };
        var assembly = Assembly.LoadFrom(appPath);
        typeof(WpfApplication).GetField("_resourceAssembly", Static)!.SetValue(null, assembly);
        var app = new WpfApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            File.WriteAllText(Path.Combine(folder, "host-error.txt"), e.Exception.ToString());
            e.Handled = true;
            app.Shutdown(1);
        };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/Dark.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Controls/Controls.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Strings/en-US.xaml") });
        Type manager = assembly.GetType("KillerPDF.Services.ThemeManager", true)!;
        Type themeType = assembly.GetType("KillerPDF.Services.Theme", true)!;
        object themeValue = Enum.Parse(themeType, theme);
        manager.GetField("_current", Static)!.SetValue(null, themeValue);
        manager.GetMethod("LoadDict", Static)!.Invoke(null, [themeValue, null]);

        string source = Path.Combine(folder, "one-page.pdf");
        File.WriteAllBytes(source, NewPdf());
        Type workingType = assembly.GetType("KillerPDF.Services.PdfWorkingDocument", true)!;
        object working = workingType.GetMethod("Open", Static)!.Invoke(null, [source, null, false])!;
        // Both active lines validate through the engine, but only development retains ParsedDocument.
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        Type documentType = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!;
        object document = documentType.GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!
            .Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(source))])!;
        Type rendererType = engine.GetType("KillerPdf.Engine.Rendering.PdfPageRenderer", true)!;
        object renderer = rendererType.GetConstructors().Single(c => c.GetParameters().Length == 2).Invoke([document, null]);
        Type optionsType = engine.GetType("KillerPdf.Engine.Rendering.PdfRenderOptions", true)!;
        object options = optionsType.GetConstructors().Single().Invoke([612, 792, false, true, true, 512L * 1024 * 1024]);
        object page = rendererType.GetMethod("Render")!.Invoke(renderer, [0, options, CancellationToken.None])!;
        Type pageType = page.GetType();
        var pixels = (ReadOnlyMemory<byte>)pageType.GetProperty("Pixels")!.GetValue(page)!;
        var bitmap = BitmapSource.Create(612, 792, 96, 96, PixelFormats.Bgra32, null, pixels.ToArray(), 612 * 4);
        bitmap.Freeze();

        void SaveReopen()
        {
            string saved = Path.Combine(folder, "reopened.pdf");
            workingType.GetMethod("Save", Instance, [typeof(string)])!.Invoke(working, [saved]);
            using var reopened = (IDisposable)workingType.GetMethod("Open", Static)!.Invoke(null, [saved, null, false])!;
            if ((int)workingType.GetProperty("PageCount", Instance)!.GetValue(reopened)! != 1 ||
                !File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(saved)))
                throw new InvalidOperationException("The synthetic working document changed during save/reopen.");
            File.WriteAllText(Path.Combine(folder, "fixture-result.txt"), "PASS: real working-document save/reopen; one synthetic page; byte-identical source.");
        }
        if (fixtureOnly)
        {
            try { SaveReopen(); Console.WriteLine("PASS synthetic PDF parses, renders, saves, and reopens through app services."); return 0; }
            finally { ((IDisposable)working).Dispose(); app.Shutdown(); }
        }
        string UiString(string key) => app.TryFindResource(key) as string
            ?? throw new InvalidOperationException("Missing host resource: " + key);
        var status = new TextBlock { Text = UiString("Str_Ready"), Margin = new Thickness(12) };
        AutomationProperties.SetAutomationId(status, "SmokeStatus");
        AutomationProperties.SetName(status, "ready");
        var panel = new StackPanel();
        var owner = new System.Windows.Window { Title = "KillerPDF", Width = 500, Height = 180, Content = panel };
        AutomationProperties.SetAutomationId(owner, "SmokeHost");
        var show = new System.Windows.Controls.Button { Content = UiString("Str_Lbl_Stamp"), Margin = new Thickness(12) };
        AutomationProperties.SetAutomationId(show, "ShowStamp");
        show.Click += (_, _) =>
        {
            Type specType = assembly.GetType("KillerPDF.StampSpec", true)!;
            object spec = Activator.CreateInstance(specType)!;
            specType.GetField("NumbersEnabled", Instance)!.SetValue(spec, true);
            specType.GetField("WmEnabled", Instance)!.SetValue(spec, true);
            Type stampType = assembly.GetType("KillerPDF.StampWindow", true)!;
            var stamp = (System.Windows.Window)Activator.CreateInstance(stampType, [owner, bitmap, 612d, 792d, 1, 0, spec, null])!;
            stamp.ContentRendered += (_, _) =>
            {
                var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(stamp);
                File.WriteAllText(Path.Combine(folder, "stamp-managed.txt"),
                    "pid=" + Environment.ProcessId + "; hwnd=" + new System.Windows.Interop.WindowInteropHelper(stamp).Handle +
                    "; attached-id=" + AutomationProperties.GetAutomationId(stamp) + "; peer-id=" + peer?.GetAutomationId() +
                    "; peer-class=" + peer?.GetClassName() + "; title=" + stamp.Title);
                Snapshot(stamp, Path.Combine(folder, "stamp-initial.png"));
            };
            stamp.ShowDialog();
            object result = stampType.GetProperty("Result", Instance)!.GetValue(stamp)!;
            if ((bool)stampType.GetProperty("Applied", Instance)!.GetValue(stamp)!)
            {
                status.Text = UiString("Str_Btn_OK");
                AutomationProperties.SetName(status, "applied:" + specType.GetField("NumColor", Instance)!.GetValue(result) +
                    ":" + specType.GetField("WmColor", Instance)!.GetValue(result));
            }
            else
            {
                status.Text = UiString("Str_Btn_Cancel");
                AutomationProperties.SetName(status, "canceled");
            }
        };
        var save = new System.Windows.Controls.Button { Content = UiString("Str_Lbl_Save"), Margin = new Thickness(12) };
        AutomationProperties.SetAutomationId(save, "SaveReopen");
        save.Click += (_, _) =>
        {
            SaveReopen(); status.Text = UiString("Str_Lbl_Save");
            AutomationProperties.SetName(status, "saved:1");
        };
        panel.Children.Add(show); panel.Children.Add(save); panel.Children.Add(status);
        owner.Closed += (_, _) => app.Shutdown();
        // Capture only rendered WPF content. No desktop, user windows, or private files are captured.
        app.Activated += (_, _) =>
        {
            foreach (System.Windows.Window window in app.Windows)
                if (AutomationProperties.GetAutomationId(window) == "ColorPickerDialog")
                    window.Dispatcher.BeginInvoke(() => Snapshot(window, Path.Combine(folder, "picker.png")));
        };
        try { app.Run(owner); return 0; }
        finally { ((IDisposable)working).Dispose(); }
    }

    private static void Snapshot(System.Windows.Window window, string path)
    {
        if (window.ActualWidth <= 0 || window.ActualHeight <= 0) return;
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static byte[] NewPdf()
    {
        string[] objects = [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>"
        ];
        var source = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string value in objects)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(source.ToString()));
            source.Append($"{offsets.Count} 0 obj\n{value}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(source.ToString());
        source.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (int offset in offsets) source.Append($"{offset:D10} 00000 n \n");
        source.Append($"trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(source.ToString());
    }
}
