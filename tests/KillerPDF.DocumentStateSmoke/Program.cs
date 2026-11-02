using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;

// This process owns every window, PDF, and setting it creates. Reflection is limited to
// startup isolation, service initialization, and constructing WPF drag-event data;
// workflow actions use actual controls and routed events.
internal static partial class Program
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static;
    private static Application App = null!;
    private static Window Owner = null!;
    private static string Folder = "";
    private static Exception? Failure;
    private static System.Threading.Timer? Deadline;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 5 || args[4] != "--startup-smoke")
                throw new ArgumentException("APP_DLL APP_XAML NEW_OUTPUT_DIRECTORY CASE --startup-smoke");
            string appPath = Path.GetFullPath(args[0]);
            Folder = Path.GetFullPath(args[2]);
            if (Directory.Exists(Folder)) throw new IOException("Use a fresh evidence folder.");
            Directory.CreateDirectory(Folder);
            string temporary = Path.Combine(Folder, "temp");
            Directory.CreateDirectory(temporary);
            Environment.SetEnvironmentVariable("TEMP", temporary);
            Environment.SetEnvironmentVariable("TMP", temporary);
            TimeSpan processLimit = TimeSpan.FromSeconds(args[3] == "print-cancel-native" ? 120 : 45);
            Deadline = new System.Threading.Timer(_ =>
            {
                Console.Error.WriteLine("FAIL isolated process deadline");
                Environment.Exit(1);
            }, null, processLimit, Timeout.InfiniteTimeSpan);
            Console.WriteLine("Initializing isolated host");
            Bootstrap(appPath, Path.GetFullPath(args[1]), processLimit);
            string loadedEngine = typeof(PdfDocument).Assembly.Location;
            string EngineHash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            string expectedEngineHash = EngineHash(Path.Combine(Path.GetDirectoryName(appPath)!, "KillerPdf.Engine.dll"));
            if (EngineHash(loadedEngine) != expectedEngineHash)
                throw new InvalidOperationException("The runner must use the engine from the tested application build.");
            File.WriteAllText(Path.Combine(Folder, "app-identity.txt"),
                "app=" + appPath + "\nappSha256=" + EngineHash(appPath) + "\nengineSha256=" + expectedEngineHash);
            Console.WriteLine("Host ready");
            if (args[3] == "redaction") Redaction();
            else if (args[3] == "macro-source") MacroSource();
            else if (args[3] == "macro-batch") MacroSource(batch: true);
            else if (args[3] == "macro-rotation") MacroRotation();
            else if (args[3] == "merge-rotation") MacroRotation(merge: true);
            else if (args[3] == "extract-form") ExtractForm();
            else if (args[3] == "extract-rotation") ExtractForm(rotate: true);
            else if (args[3] == "print-rotation") PrintRotation();
            else if (args[3] == "print-switch") PrintSwitch();
            else if (args[3] == "print-cancel-before-prepare") PrintCancelBeforePrepare();
            else if (args[3] == "print-cancel-preparing") PrintCancelBeforePrepare(duringPreparation: true);
            else if (args[3] == "print-cancel-native") PrintCancelBeforePrepare(nativePrompt: true);
            else if (args[3] == "merge-failure-pane") MergeFailurePane();
            else if (args[3] == "merge-close-success") MergeFailurePane(closeOwner: true);
            else if (args[3] == "archive-first-save") ArchiveFirstSave();
            else if (args[3] == "archive-first-close") ArchiveFirstSave(closeUntouched: true);
            else if (args[3] == "archive-responsive") ArchiveFirstSave(responsive: true);
            else if (args[3] == "archive-durable-save") ArchiveFirstSave(durableSave: true);
            else if (args[3] == "archive-cap-cancel") ArchiveAbandon("cap");
            else if (args[3] == "archive-tabs-cancel") ArchiveAbandon("tabs");
            else if (args[3] == "archive-unsafe") ArchiveAbandon("unsafe");
            else if (args[3] == "archive-mixed") ArchiveMixed();
            else if (args[3] == "archive-async-cancel") ArchiveAsyncCancel();
            else if (args[3] == "archive-async-empty") ArchiveAsyncCancel(empty: true, cleanup: true);
            else if (args[3] == "archive-async-owner-closed") ArchiveAsyncCancel(closeOwner: true, cleanup: true);
            else if (args[3] == "archive-async-previous-closed") ArchiveAsyncCancel(closePrevious: true, cleanup: true);
            else if (args[3] == "archive-async-cleanup") ArchiveAsyncCancel(cleanup: true);
            else if (args[3] == "archive-async-working-copy-failure") ArchiveAsyncCancel(workingCopyFailure: true, cleanup: true);
            else if (args[3] == "archive-async-empty-pdf") ArchiveAsyncCancel(noPages: true, cleanup: true);
            else if (args[3] == "archive-async-repair-failure") ArchiveAsyncCancel(noPages: true, repair: true, cleanup: true);
            else if (args[3] == "archive-window-closed") ArchiveClosedWindow();
            else if (args[3] == "archive-next-prompt-release") ArchiveClosedWindow(releaseAtPrompt: true);
            else if (args[3] == "merge-cancel") MergeCancellation();
            else if (args[3] == "radio-accessibility") RadioAccessibility();
            else if (args[3] == "radio-name-boundary") RadioAccessibility(slashOption: true);
            else if (args[3] == "keyboard-pane-owner") KeyboardPaneOwner();
            else if (args[3] == "reading-caret") ReadingCaret();
            else if (args[3] == "reading-keyboard") ReadingCaret(keyboard: true);
            else if (args[3] == "reading-terminal-home") ReadingCaret(terminalHome: true);
            else if (args[3] == "reading-scroll") ReadingScroll();
            else if (args[3] == "reading-scroll-spread") ReadingScroll(spread: true);
            else if (args[3] == "reading-scroll-wide") ReadingScroll(wide: true);
            else throw new ArgumentException("Unknown case.");
            Console.WriteLine("PASS " + args[3]);
            File.WriteAllText(Path.Combine(Folder, "result.txt"), "PASS " + args[3]);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + ex);
            if (Directory.Exists(Folder)) File.WriteAllText(Path.Combine(Folder, "result.txt"), "FAIL " + ex);
            Environment.Exit(1);
        }
        return 1;
    }

    private static void Bootstrap(string appPath, string appXaml, TimeSpan processLimit)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(Path.GetDirectoryName(appPath)!, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        Assembly assembly = Assembly.LoadFrom(appPath);
        typeof(Application).GetField("_resourceAssembly", Members)!.SetValue(null, assembly);
        assembly.GetType("KillerPDF.Services.AppDataPaths", true)!
            .GetProperty("StartupSmokeRoot", Members)!.SetValue(null, Path.Combine(Folder, "data"));
        Type appType = assembly.GetType("KillerPDF.App", true)!;
        string smokeRoot = (string)appType.GetProperty("SmokeDataRoot", Members)!.GetValue(null)!;
        File.WriteAllText(Path.Combine(Folder, "owned-smoke-root.txt"), smokeRoot);
        Console.WriteLine("Owned app temporary root: " + smokeRoot);
        var host = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("DocumentStateHost"),
            AssemblyBuilderAccess.Run).DefineDynamicModule("Host")
            .DefineType("IsolatedApplication", TypeAttributes.Public, appType);
        host.DefineDefaultConstructor(MethodAttributes.Public);
        var startup = host.DefineMethod("OnStartup", MethodAttributes.Family | MethodAttributes.Virtual
            | MethodAttributes.HideBySig, typeof(void), [typeof(StartupEventArgs)]);
        startup.GetILGenerator().Emit(OpCodes.Ret);
        host.DefineMethodOverride(startup, appType.GetMethod("OnStartup", Members)!);
        App = (Application)Activator.CreateInstance(host.CreateType()!)!;
        App.DispatcherUnhandledException += (_, e) => { Console.Error.WriteLine(e.Exception); Environment.Exit(1); };
        var deadline = new DispatcherTimer { Interval = processLimit - TimeSpan.FromSeconds(5) };
        deadline.Tick += (_, _) =>
        {
            foreach (Window window in App.Windows)
                Console.Error.WriteLine(window.GetType().Name + ": " + window.Title + " "
                    + string.Join(" | ", Descendants<TextBlock>(window).Select(t => t.Text)));
            Console.Error.WriteLine("FAIL isolated host deadline");
            Environment.Exit(1);
        };
        deadline.Start();
        App.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        XDocument source = XDocument.Load(appXaml);
        XElement resources = source.Descendants().First(e => e.Name.LocalName == "ResourceDictionary");
        foreach (var declaration in source.Root!.Attributes().Where(a => a.IsNamespaceDeclaration))
            resources.SetAttributeValue(declaration.Name, declaration.Value);
        string xaml = resources.ToString();
        string assemblyName = assembly.GetName().Name!;
        // Whole attribute values: "clr-namespace:KillerPDF" is also the start of KillerPDF.Controls.
        foreach (string name in new[] { "KillerPDF", "KillerPDF.Controls", "KillerFamily.Layout" })
            xaml = xaml.Replace("\"clr-namespace:" + name + "\"", "\"clr-namespace:" + name
                + ";assembly=" + assemblyName + "\"", StringComparison.Ordinal);
        App.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml,
            new System.Windows.Markup.ParserContext { BaseUri = new Uri($"pack://application:,,,/{assemblyName};component/") });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        // App startup order: the kit theme hooks (settings, KillerPDF's own keys) before the services.
        assembly.GetType("KillerPDF.Services.AppTheme", true)!.GetMethod("Configure",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null);
        foreach (string service in new[] { "ThemeManager", "LocaleManager", "FilmGrain" })
            assembly.GetType("KillerPDF.Services." + service, true)!
                .GetMethod("Initialize", Members)!.Invoke(null, null);
        Owner = (Window)Activator.CreateInstance(assembly.GetType("KillerPDF.Shell.MainWindow", true)!)!;
        Owner.WindowStartupLocation = WindowStartupLocation.Manual;
        Owner.Left = -10000; Owner.Top = -10000; Owner.ShowInTaskbar = false;
        Owner.ShowActivated = false; Owner.Width = 1200; Owner.Height = 800;
        Owner.Show();
        Console.WriteLine("Window shown");
        Pump(300);
    }

    private static void ReadingScroll(bool spread = false, bool wide = false)
    {
        string input = Path.Combine(Folder, "tall-reading.pdf");
        var stream = new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .MoveText(20, 2940).ShowLatin1Text("Line 00" + (wide ? " " + new string('W', 45) + "Z" : ""));
        for (int line = 1; line < 40; line++)
            stream.MoveText(0, -45).ShowLatin1Text("Line " + line.ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
        stream.EndText();
        var document = new PdfDocumentBuilder();
        if (spread)
            for (int page = 0; page < 3; page++) document.AddBlankPage(600, 3000);
        File.WriteAllBytes(input, document.AddPage(600, 3000, stream).Build());
        Open(input);
        var viewer = (FrameworkElement)Owner.FindName("Viewer");
        var surface = (FrameworkElement)viewer.FindName("DocSurfacePad");
        Owner.Activate();
        if (!surface.Focus()) throw new InvalidOperationException("Reading surface could not receive focus.");
        void Press(Key key)
        {
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Owner),
                Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            surface.RaiseEvent(input);
            if (!input.Handled) { input.RoutedEvent = Keyboard.KeyDownEvent; surface.RaiseEvent(input); }
        }
        Press(Key.F6);
        Pump(600);
        var zoom = (ComboBox)Owner.FindName("ZoomBox");
        zoom.SelectedItem = zoom.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "2.0");
        Pump(600);
        var viewport = (ScrollViewer)viewer.FindName("PagePreviewPanel");
        var ready = System.Diagnostics.Stopwatch.StartNew();
        while (viewport.ScrollableHeight < viewport.ViewportHeight && ready.Elapsed < TimeSpan.FromSeconds(8)) Pump(100);
        File.WriteAllText(Path.Combine(Folder, "viewport-setup.txt"),
            $"Extent={viewport.ExtentHeight}; viewport={viewport.ViewportHeight}; scrollable={viewport.ScrollableHeight}; zoom={zoom.Text}; surface={surface.ActualHeight}");
        viewport.ScrollToTop();
        Pump(100);
        if (viewport.ScrollableHeight < viewport.ViewportHeight)
            throw new InvalidOperationException("The owned page did not exceed the reading viewport.");
        surface.Focus();
        if (spread)
        {
            Press(Key.F7);
            Pump(600);
            zoom.SelectedItem = zoom.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "2.0");
            Pump(600);
            viewport.ScrollToTop();
            Pump(100);
            surface.Focus();
            var spreadPeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(surface);
            var spreadText = (System.Windows.Automation.Provider.ITextProvider)spreadPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Text);
            var target = spreadText.DocumentRange.FindText("Line 35", false, false)!;
            var heading = spreadText.DocumentRange.FindText("Line 00", false, false)!;
            target.Select();
            target.ScrollIntoView(true);
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (target.GetBoundingRectangles().Length == 0 && heading.GetBoundingRectangles().Length == 0
                && wait.Elapsed < TimeSpan.FromSeconds(8)) Pump(100);
            Pump(300);
            File.WriteAllText(Path.Combine(Folder, "spread-result-state.txt"), $"offset={viewport.VerticalOffset}; target={target.GetBoundingRectangles().Length}; heading={heading.GetBoundingRectangles().Length}; extent={viewport.ExtentHeight}; zoom={zoom.Text}");
            if (viewport.VerticalOffset <= 0 || target.GetBoundingRectangles().Length == 0)
                throw new InvalidOperationException("The cold spread dropped its right-page reading scroll.");
            string visibleText = string.Join("\n", spreadText.GetVisibleRanges().Select(range => range.GetText(-1)));
            File.WriteAllText(Path.Combine(Folder, "spread-visible-text.txt"), visibleText);
            if (!visibleText.Contains("Line 35", StringComparison.Ordinal))
                throw new InvalidOperationException("The cold spread clipped the requested reading line at the horizontal edge.");
            File.WriteAllText(Path.Combine(Folder, "reading-spread-scroll.txt"), "Right-page Line 35 visible; vertical offset=" + viewport.VerticalOffset);
            return;
        }
        if (wide)
        {
            var widePeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(surface);
            var wideText = (System.Windows.Automation.Provider.ITextProvider)widePeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Text);
            var terminal = wideText.DocumentRange.FindText("Z", false, false)!;
            terminal.Select();
            terminal.ScrollIntoView(true);
            Pump(600);
            File.WriteAllText(Path.Combine(Folder, "wide-visible-text.txt"),
                string.Join("\n", wideText.GetVisibleRanges().Select(range => range.GetText(-1))));
            if (terminal.GetBoundingRectangles().Length == 0)
                throw new InvalidOperationException("Reading scroll lost the requested caret in an oversized zoomed line.");
            File.WriteAllText(Path.Combine(Folder, "reading-wide-scroll.txt"), "Terminal Z visible; horizontal offset=" + viewport.HorizontalOffset);
            return;
        }
        for (int line = 0; line < 35; line++) Press(Key.Down);
        Pump(600);
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(surface);
        var text = (System.Windows.Automation.Provider.ITextProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Text);
        var selected = text.GetSelection().Single();
        selected.ExpandToEnclosingUnit(System.Windows.Automation.Text.TextUnit.Line);
        if (selected.GetText(-1).TrimEnd('\n') != "Line 35")
            throw new InvalidOperationException("Reading navigation did not reach the expected line.");
        if (viewport.VerticalOffset <= 0 || selected.GetBoundingRectangles().Length == 0)
            throw new InvalidOperationException("Reading movement left its caret outside the viewport.");
        viewport.ScrollToTop();
        Pump(100);
        selected.ScrollIntoView(true);
        Pump(300);
        if (viewport.VerticalOffset <= 0 || selected.GetBoundingRectangles().Length == 0)
            throw new InvalidOperationException("UIA ScrollIntoView did not reveal the reading position.");
        File.WriteAllText(Path.Combine(Folder, "reading-scroll.txt"), "Line 35 visible; vertical offset=" + viewport.VerticalOffset);
    }

    private static void ReadingCaret(bool keyboard = false, bool terminalHome = false)
    {
        string input = Path.Combine(Folder, "reading.pdf");
        var stream = new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .MoveText(20, 420).ShowLatin1Text("Reading order check")
            .MoveText(0, -25).ShowLatin1Text("First, the cedar tree stands beside the path.")
            .MoveText(0, -25).ShowLatin1Text("Second, the blue bicycle rests near the gate.").EndText();
        var secondPage = new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .MoveText(20, 420).ShowLatin1Text("Page two check").EndText();
        File.WriteAllBytes(input, new PdfDocumentBuilder().AddPage(300, 500, stream).AddPage(300, 500, secondPage).Build());
        Open(input);
        var viewer = (FrameworkElement)Owner.FindName("Viewer");
        var surface = (FrameworkElement)viewer.FindName("DocSurfacePad");
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(surface);
        var text = (System.Windows.Automation.Provider.ITextProvider)
            peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Text);
        if (keyboard || terminalHome)
        {
            Owner.Activate();
            if (!surface.Focus()) throw new InvalidOperationException("Reading surface could not receive keyboard focus.");
            void Press(Key key)
            {
                var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Owner),
                    Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                surface.RaiseEvent(input);
                if (!input.Handled)
                {
                    input.RoutedEvent = Keyboard.KeyDownEvent;
                    surface.RaiseEvent(input);
                }
            }
            if (terminalHome)
            {
                foreach (var (key, expectedText) in new[] { (Key.Home, "Page two check"),
                             (Key.Up, "Second, the blue"), (Key.PageUp, "Reading order check") })
                {
                    var end = text.DocumentRange.Clone();
                    end.MoveEndpointByRange(System.Windows.Automation.Text.TextPatternRangeEndpoint.Start, end,
                        System.Windows.Automation.Text.TextPatternRangeEndpoint.End);
                    end.Select();
                    Press(key);
                    var expected = text.DocumentRange.FindText(expectedText, false, false)!;
                    if (text.GetSelection().Single().CompareEndpoints(System.Windows.Automation.Text.TextPatternRangeEndpoint.Start,
                        expected, System.Windows.Automation.Text.TextPatternRangeEndpoint.Start) != 0)
                        throw new InvalidOperationException(key + " left the caret at the final insertion point.");
                }
                return;
            }
            foreach (string expected in new[] { "First, the cedar tree stands beside the path.",
                         "Second, the blue bicycle rests near the gate." })
            {
                Press(Key.Down);
                var line = text.GetSelection().Single();
                line.ExpandToEnclosingUnit(System.Windows.Automation.Text.TextUnit.Line);
                if (line.GetText(-1).TrimEnd('\n') != expected)
                    throw new InvalidOperationException("Down did not move the actual reading caret to: " + expected);
            }
            Press(Key.Home);
            Press(Key.Right);
            var character = text.GetSelection().Single();
            character.ExpandToEnclosingUnit(System.Windows.Automation.Text.TextUnit.Character);
            if (character.GetText(-1) != "e") throw new InvalidOperationException("Character movement lost its logical offset.");
            foreach (var (key, expected) in new[] { (Key.PageDown, "Page two check"), (Key.PageUp, "Reading order check") })
            {
                Press(key);
                Pump(300);
                var line = text.GetSelection().Single();
                line.ExpandToEnclosingUnit(System.Windows.Automation.Text.TextUnit.Line);
                if (line.GetText(-1).TrimEnd('\n') != expected)
                    throw new InvalidOperationException("Page movement lost its reading caret: " + expected);
            }
            return;
        }
        foreach (bool afterWord in new[] { false, true })
        {
            var caret = text.DocumentRange.FindText("cedar", false, false)
                ?? throw new InvalidOperationException("Reading fixture text is unavailable.");
            caret.MoveEndpointByRange(afterWord ? System.Windows.Automation.Text.TextPatternRangeEndpoint.Start
                : System.Windows.Automation.Text.TextPatternRangeEndpoint.End, caret,
                afterWord ? System.Windows.Automation.Text.TextPatternRangeEndpoint.End
                    : System.Windows.Automation.Text.TextPatternRangeEndpoint.Start);
            caret.Select();
            var actual = text.GetSelection().Single();
            if (actual.CompareEndpoints(System.Windows.Automation.Text.TextPatternRangeEndpoint.Start, caret,
                System.Windows.Automation.Text.TextPatternRangeEndpoint.Start) != 0 || actual.GetText(-1) != "")
                throw new InvalidOperationException("The actual viewer lost its collapsed reading caret.");
        }
        string other = Path.Combine(Folder, "other.pdf");
        File.WriteAllBytes(other, new PdfDocumentBuilder().AddBlankPage(300, 500).Build());
        Open(other);
        if (text.GetSelection().Single().CompareEndpoints(System.Windows.Automation.Text.TextPatternRangeEndpoint.Start,
            text.DocumentRange, System.Windows.Automation.Text.TextPatternRangeEndpoint.Start) != 0)
            throw new InvalidOperationException("Reading caret leaked into another document.");
    }

    private static void KeyboardPaneOwner()
    {
        string left = Path.Combine(Folder, "left.pdf"), right = Path.Combine(Folder, "right.pdf");
        File.WriteAllBytes(left, new PdfDocumentBuilder().AddBlankPage(300, 500).Build());
        File.WriteAllBytes(right, new PdfDocumentBuilder().AddBlankPage(300, 500).Build());
        Open(left);
        Click((Button)Owner.FindName("SplitPaneRailBtn"));
        Pump(350);
        var a = (FrameworkElement)Owner.FindName("Viewer");
        var b = (FrameworkElement)Owner.FindName("ViewerB");
        void FocusPane(FrameworkElement viewer) => viewer.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        FocusPane(b);
        Open(right);
        FocusPane(a);
        var first = (FrameworkElement)a.FindName("DocSurfacePad");
        var second = (FrameworkElement)b.FindName("DocSurfacePad");
        Owner.Activate();
        if (!first.Focus()) throw new InvalidOperationException("Could not focus the first reading surface.");
        int steps = 0;
        while (!ReferenceEquals(Keyboard.FocusedElement, second) && steps++ < 200)
        {
            if (Keyboard.FocusedElement is not UIElement current
                || !current.MoveFocus(new(FocusNavigationDirection.Next))) break;
        }
        if (!ReferenceEquals(Keyboard.FocusedElement, second))
            throw new InvalidOperationException("Tab traversal did not enter the second reading surface.");
        second.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Owner),
            Environment.TickCount, Key.F6) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Pump(400);
        var leftPages = (FrameworkElement)a.FindName("ContinuousPanel");
        var rightPages = (FrameworkElement)b.FindName("ContinuousPanel");
        if (leftPages.Visibility != Visibility.Visible || rightPages.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("F6 after Tab into pane B changed the wrong document pane.");
        File.WriteAllText(Path.Combine(Folder, "focus-route.txt"), "Tab steps=" + steps + "; F6 changed pane B only.");
    }

    private static void RadioAccessibility(bool slashOption = false)
    {
        string input = Path.Combine(Folder, "radio-options.pdf");
        File.WriteAllBytes(input, new PdfDocumentBuilder().AddBlankPage(300, 500)
            .AddRadioGroup("choice", [new(0, 30, 300, 22, 22, slashOption ? "/Alpha" : "Alpha"),
                new(0, 30, 250, 22, 22, slashOption ? "Alpha" : "Beta")],
                selectedValue: "Alpha", fieldMetadata: new() { Tooltip = "Choose Alpha or Beta" }).Build());
        Open(input);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (Descendants<FrameworkElement>(Owner).Count(element => element.GetType().Name == "FormToggleSurface") < 2
            && watch.Elapsed < TimeSpan.FromSeconds(8)) Pump(100);
        var radios = Descendants<FrameworkElement>(Owner)
            .Where(element => element.GetType().Name == "FormToggleSurface")
            .Select(System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement)
            .Where(peer => peer?.GetAutomationControlType() == System.Windows.Automation.Peers.AutomationControlType.RadioButton)
            .ToArray();
        if (radios.Length != 2) throw new InvalidOperationException("Expected two rendered radio widgets; found " + radios.Length);
        string[] names = radios.Select(peer => peer!.GetName()).ToArray();
        File.WriteAllLines(Path.Combine(Folder, "radio-names.txt"), names);
        string[] expected = slashOption
            ? ["Choose Alpha or Beta: /Alpha", "Choose Alpha or Beta: Alpha"]
            : ["Choose Alpha or Beta: Alpha", "Choose Alpha or Beta: Beta"];
        if (names.Distinct(StringComparer.Ordinal).Count() != 2 || expected.Any(name => !names.Contains(name)))
            throw new InvalidOperationException("Radio widgets do not identify their individual options: " + string.Join(" | ", names));
        var selections = radios.Select(peer => (System.Windows.Automation.Provider.ISelectionItemProvider)
            peer!.GetPattern(System.Windows.Automation.Peers.PatternInterface.SelectionItem)).ToArray();
        int beta = Array.IndexOf(names, expected[1]);
        selections[beta].Select();
        if (!selections[beta].IsSelected || selections.Where((_, index) => index != beta).Any(item => item.IsSelected))
            throw new InvalidOperationException("Selecting Beta did not preserve radio exclusivity.");
    }

    private static void MergeFailurePane(bool closeOwner = false)
    {
        string left = Path.Combine(Folder, "left.pdf");
        string right = Path.Combine(Folder, "right.pdf");
        File.WriteAllBytes(left, new PdfDocumentBuilder().AddBlankPage(300, 500)
            .AddTextField(0, "customer", 20, 200, 150, 25, "LEFT").Build());
        File.WriteAllBytes(right, new PdfDocumentBuilder().AddBlankPage(300, 500)
            .AddTextField(0, "customer", 20, 200, 150, 25, "RIGHT").Build());
        Open(left);
        Click((Button)Owner.FindName("SplitPaneRailBtn"));
        Pump(350);
        var leftViewer = (FrameworkElement)Owner.FindName("Viewer");
        var rightViewer = (FrameworkElement)Owner.FindName("ViewerB");
        void Focus(FrameworkElement viewer) => viewer.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Focus(rightViewer);
        Open(right);
        if (closeOwner)
        {
            Focus(leftViewer);
            Descendants<TextBox>(leftViewer).Single(input => input.Text == "LEFT").Text = "LEFT-EDIT";
            Focus(rightViewer);
        }
        else
        {
            Descendants<TextBox>(rightViewer).Single(input => input.Text == "RIGHT").Text = "RIGHT-EDIT";
            Focus(leftViewer);
        }
        string[] inputs = [Path.Combine(Folder, "merge-one.pdf"), Path.Combine(Folder, "merge-two.pdf")];
        foreach (string path in inputs)
            if (closeOwner) File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(300, 500).Build());
            else File.WriteAllText(path, "%PDF-2.0\nUnreadable owned merge fixture\n");
        int stage = 0;
        bool switchedDuringWorker = false;
        bool finished = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            Window? dialog = App.Windows.Cast<Window>().LastOrDefault(window => !ReferenceEquals(window, Owner) && window.IsVisible);
            if (dialog is null) return;
            try
            {
                if (stage == 0)
                {
                    Button? merge = Descendants<Button>(dialog).FirstOrDefault(button =>
                        Text(button) == (string)Owner.FindResource("Str_Drop_Merge"));
                    if (merge is null) return;
                    stage = 1;
                    dialog.Closed += (_, _) => Owner.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                    {
                        switchedDuringWorker = Descendants<TextBlock>(Owner).Any(text => text.IsVisible
                            && text.Text == (string)Owner.FindResource("Str_Drop_Merging"));
                        if (closeOwner) Click((Button)Owner.FindName("SplitPaneRailBtn"));
                        else Focus(rightViewer);
                    }));
                    Click(merge);
                    return;
                }
                if (!Descendants<Button>(dialog).Any(button => button.IsVisible && button.IsEnabled && Text(button).Trim() == "OK")) return;
                if (closeOwner) throw new InvalidOperationException("The merge failed when successful completion was required: "
                    + string.Join(" ", Descendants<TextBlock>(dialog).Select(text => text.Text)));
                string[] values = Descendants<TextBox>(rightViewer).Select(input => input.Text).ToArray();
                File.WriteAllLines(Path.Combine(Folder, "right-fields-after-merge-failure.txt"), values);
                if (!switchedDuringWorker) throw new InvalidOperationException("The pane switch did not occur while the merge was pending.");
                if (!values.Contains("RIGHT-EDIT"))
                    throw new InvalidOperationException("A failed merge replaced the other pane's pending form state.");
            }
            catch (Exception error) { Failure = error; }
            finished = true;
            timer.Stop();
            dialog.Close();
        };
        timer.Start();
        var data = new DataObject(DataFormats.FileDrop, inputs);
        ScrollViewer dropTarget = Descendants<ScrollViewer>(closeOwner ? rightViewer : leftViewer).First(view => view.AllowDrop);
        ConstructorInfo constructor = typeof(DragEventArgs).GetConstructors(Members)
            .Single(item => item.GetParameters().Length == 5);
        object Argument(ParameterInfo parameter)
        {
            if (typeof(IDataObject).IsAssignableFrom(parameter.ParameterType)) return data;
            if (parameter.ParameterType == typeof(int)) return 0;
            if (parameter.ParameterType == typeof(DragDropKeyStates)) return DragDropKeyStates.None;
            if (parameter.ParameterType == typeof(DependencyObject)) return dropTarget;
            if (parameter.ParameterType == typeof(Point)) return new Point(10, 10);
            if (parameter.ParameterType == typeof(DragDropEffects)) return DragDropEffects.Copy;
            throw new InvalidOperationException("Unsupported drag-event argument: " + parameter.ParameterType);
        }
        var drop = (DragEventArgs)constructor.Invoke(constructor.GetParameters().Select(Argument).ToArray());
        drop.RoutedEvent = DragDrop.DropEvent;
        dropTarget.RaiseEvent(drop);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!finished && watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            Pump(80);
            if (!closeOwner || !switchedDuringWorker || Descendants<TextBlock>(Owner).Any(text => text.IsVisible
                && text.Text == (string)Owner.FindResource("Str_Drop_Merging"))) continue;
            if (((TextBlock)Owner.FindName("FileNameLabel")).Text != "Combined.pdf") continue;
            Pump(600);
            string name = ((TextBlock)Owner.FindName("FileNameLabel")).Text;
            File.WriteAllText(Path.Combine(Folder, "completed-merge-pane.txt"),
                $"Document={name}\nSecondPaneVisible={rightViewer.IsVisible}\n");
            if (name != "Combined.pdf") throw new InvalidOperationException("The expected merged document did not become active.");
            if (!rightViewer.IsVisible) throw new InvalidOperationException("The completed merge activated a collapsed pane.");
            if (!Descendants<TextBox>(leftViewer).Any(input => input.Text == "LEFT-EDIT"))
                throw new InvalidOperationException("The completed merge lost the visible pane's pending form edit.");
            finished = true;
        }
        timer.Stop();
        if (Failure is not null) throw Failure;
        if (!finished) throw new TimeoutException("The owned merge did not reach the expected completion state.");
    }

    private static void Redaction()
    {
        string macros = Path.Combine(Folder, "data", "macros");
        Directory.CreateDirectory(macros);
        File.WriteAllText(Path.Combine(macros, "redact.json"), new PdfMacro("Redact test",
            [new PdfMacroStep(PdfMacroOperation.Redact), new PdfMacroStep(PdfMacroOperation.Save)]).ToJson());
        string left = Fixture("left", "PUBLIC", 20, 100, 10, 80, 120, 120);
        string right = Fixture("right", "SECRET", 150, 300, 140, 280, 250, 320);
        Open(left);
        Console.WriteLine("Left fixture opened");
        Click((Button)Owner.FindName("SplitPaneRailBtn"));
        Pump(350);
        var rightViewer = (FrameworkElement)Owner.FindName("ViewerB");
        rightViewer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount,
            MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Open(right);
        Console.WriteLine("Right fixture opened");
        RunMacro(Path.Combine(Folder, "result.pdf"));
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(Path.Combine(Folder, "result.pdf")));
        string text = string.Join(" ", new PdfPageContentReader(document).Read(0).GetWords().Select(w => w.Text));
        Console.WriteLine("Extracted macro output: " + text);
        if (text.Contains("SECRET", StringComparison.Ordinal))
            throw new InvalidOperationException("BF-F001: right-pane SECRET remains extractable after Redact macro.");
        if (!text.Contains("KEPT-RIGHT", StringComparison.Ordinal))
            throw new InvalidOperationException("The macro must preserve right-document content outside its redaction mark.");
    }

    private static void MacroSource(bool batch = false)
    {
        Open(Fixture("source", "SOURCE", 20, 100, 10, 80, 120, 120));
        Exception? assertion = null;
        bool sourceChosen = false;
        bool batchPicker = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            Window? dialog = App.Windows.Cast<Window>().LastOrDefault(w => !ReferenceEquals(w, Owner) && w.IsVisible);
            if (dialog is null) return;
            if (batch && sourceChosen && dialog.GetType().Name == "FileDialog")
            { timer.Stop(); batchPicker = true; dialog.Close(); return; }
            if (sourceChosen || dialog.GetType().Name != "MacroDialog") return;
            sourceChosen = true;
            timer.Stop();
            var options = Descendants<RadioButton>(dialog).ToArray();
            if (options.Length != 2 || options.Count(o => o.IsChecked == true) != 1
                || !options.Any(o => Text(o).Contains("current", StringComparison.OrdinalIgnoreCase) && o.IsChecked == true))
                assertion = new InvalidOperationException("Macro source choice must be in the macro window, defaulting to the current document.");
            if (assertion is null)
            {
                if (batch) options.Single(o => o.IsChecked != true).IsChecked = true;
                Capture(dialog, batch ? "macro-batch.png" : "macro-current.png");
                if (batch)
                { timer.Start(); QueueClick(Descendants<Button>(dialog).Single(b => b.IsDefault)); return; }
            }
            dialog.Close();
        };
        timer.Start();
        OpenMacroWindow();
        if (assertion is not null) throw assertion;
        if (batch && !batchPicker) throw new InvalidOperationException("Batch selection did not open the file picker directly.");
    }

    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(Folder, name));
        encoder.Save(file);
    }

    private static void MacroRotation(bool merge = false)
    {
        string macros = Path.Combine(Folder, "data", "macros");
        Directory.CreateDirectory(macros);
        PdfMacro macro = merge
            ? new PdfMacro("Merge test", [new PdfDataMergeProfile("Names", [new("Name", "Name")],
                "{{Name}}.pdf").ToMacroStep(), new PdfMacroStep(PdfMacroOperation.Save)])
            : new PdfMacro("Save test", [new PdfMacroStep(PdfMacroOperation.Save)]);
        File.WriteAllText(Path.Combine(macros, "save.json"), macro.ToJson());
        string path = Path.Combine(Folder, "rotate.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(300, 500)
            .AddTextField(0, "Name", 20, 200, 150, 25, "Original").Build());
        Open(path);
        Rotate();
        string output = Path.Combine(Folder, merge ? "record.pdf" : "result.pdf");
        string? records = merge ? Path.Combine(Folder, "records.csv") : null;
        if (records is not null) File.WriteAllText(records, "Name\nrecord\n");
        RunMacro(output, redact: false, mergeData: records);
        int rotation = PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(output)))[0].Rotation;
        if (rotation != 90) throw new InvalidOperationException("BF-F003: macro output rotation was " + rotation + ", expected 90.");
        if (merge && PdfFormWidgetReader.ReadPage(PdfDocument.Open(File.ReadAllBytes(output)), 0)
                .Single(w => w.FieldName == "Name").Value != "record")
            throw new InvalidOperationException("The data-merge macro did not apply the selected record.");
    }

    private static void Rotate()
    {
        var pages = (ListBox)Owner.FindName("PageList");
        pages.SelectedIndex = 0;
        var thumbnail = (ListBoxItem)pages.ItemContainerGenerator.ContainerFromIndex(0);
        thumbnail.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
            { RoutedEvent = Mouse.MouseUpEvent });
        Pump(80);
        string label = (string)Owner.FindResource("Str_Ctx_RotateCWShort");
        MenuItem clockwise = Menus().SelectMany(m => Items(m.Items)).First(m => Header(m) == label);
        clockwise.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        foreach (ContextMenu menu in Menus()) menu.IsOpen = false;
        Pump(250);
    }

    private static void ExtractForm(bool rotate = false)
    {
        string path = Path.Combine(Folder, "form.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(300, 500)
            .AddTextField(0, "customer", 20, 200, 150, 25, "Original").Build());
        Open(path);
        if (rotate) Rotate();
        var input = Descendants<TextBox>((DependencyObject)Owner.FindName("Viewer")).Single(t => t.Text == "Original");
        input.Text = "Updated";
        ((ListBox)Owner.FindName("PageList")).SelectedIndex = 0;
        string output = Path.Combine(Folder, "extracted.pdf");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        bool rangeAccepted = false;
        timer.Tick += (_, _) =>
        {
            // Extract Pages asks which pages first, prefilled with the selected one.
            Window? range = App.Windows.Cast<Window>().FirstOrDefault(w => w.GetType().Name == "DocumentValuesDialog");
            if (range is not null && !rangeAccepted)
            {
                rangeAccepted = true;
                QueueClick(Descendants<Button>(range).Single(button => button.IsDefault));
                return;
            }
            Window? picker = App.Windows.Cast<Window>().FirstOrDefault(w => w.GetType().Name == "FileDialog");
            if (picker is null) return;
            timer.Stop();
            ((TextBox)picker.FindName("FileNameBox")).Text = output;
            QueueClick((Button)picker.FindName("AcceptButton"));
        };
        timer.Start();
        Click((Button)Owner.FindName("ExtractPagesBtn"));
        string value = PdfFormWidgetReader.ReadPage(PdfDocument.Open(File.ReadAllBytes(output)), 0)
            .Single(w => w.FieldName == "customer").Value;
        if (value != "Updated") throw new InvalidOperationException("BF-F004: extraction returned " + value + ", expected Updated.");
        if (rotate && PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(output)))[0].Rotation != 90)
            throw new InvalidOperationException("Extraction must apply the pending 90-degree rotation exactly once.");
    }

    private static void PrintCancelBeforePrepare(bool duringPreparation = false, bool nativePrompt = false)
    {
        string path = Path.Combine(Folder, "cancel-print.pdf");
        int count = duringPreparation ? 8 : 1;
        var document = new PdfDocumentBuilder();
        for (int index = 0; index < count; index++) document.AddBlankPage(300, 500);
        File.WriteAllBytes(path, document.Build());
        Open(path);
        bool started = false, closeRequested = false, closed = false;
        int updatesAfterClose = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            Window? preview = App.Windows.Cast<Window>().FirstOrDefault(w => w.GetType().Name == "PrintPreviewWindow");
            if (preview is null) return;
            Button? print = Descendants<Button>(preview).FirstOrDefault(button => button.IsDefault && button.IsEnabled);
            if (print is null) return;
            timer.Stop();
            ComboBox queues = Descendants<ComboBox>(preview)
                .Single(combo => combo.Items.Contains("Microsoft Print to PDF"));
            queues.SelectedItem = "Microsoft Print to PDF";
            preview.Closing += (_, _) => { closeRequested = true; Console.WriteLine("Print preview close requested"); };
            preview.Closed += (_, _) => { closed = true; Console.WriteLine("Print preview closed at assigned cancellation point"); };
            if (!duringPreparation && !nativePrompt)
                preview.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(preview.Close));
            started = true;
            Click(print);
            TextBlock status = Descendants<TextBlock>(preview).Single(text =>
                text.Text == (string)preview.FindResource("Str_Print_PreparingToPrint"));
            string firstPage = string.Format((string)preview.FindResource("Str_Print_Preparing"), 1, count);
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
                .AddValueChanged(status, (_, _) =>
                {
                    Console.WriteLine("Print progress: " + status.Text);
                    if (closeRequested) updatesAfterClose++;
                    else if (duringPreparation && status.Text == firstPage) preview.Close();
                });
        };
        timer.Start();
        Click((Button)Owner.FindName("PrintBtn"));
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (!closed && wait.Elapsed < TimeSpan.FromSeconds(20)) Pump(100);
        timer.Stop();
        if (!started || !closed) throw new TimeoutException("The owned print cancellation did not execute.");
        Pump(1500);
        if (updatesAfterClose != 0)
            throw new InvalidOperationException("Print preparation continued after the preview close request.");
        if (App.Windows.Cast<Window>().Any(window => window != Owner))
            throw new InvalidOperationException("Closing Print Preview left a print or error dialog open.");
    }

    private static void PrintRotation()
    {
        string path = Path.Combine(Folder, "print.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(300, 500).Build());
        Open(path);
        Rotate();
        Exception? assertion = null;
        bool rendered = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            Window? preview = App.Windows.Cast<Window>().FirstOrDefault(w => w.GetType().Name == "PrintPreviewWindow");
            if (preview is null) return;
            var image = Descendants<Image>(preview).FirstOrDefault(i => i.Source is System.Windows.Media.Imaging.BitmapSource b && b.PixelWidth > 100);
            if (image?.Source is not System.Windows.Media.Imaging.BitmapSource bitmap) return;
            rendered = true; timer.Stop();
            Console.WriteLine("Print preview raster: " + bitmap.PixelWidth + "x" + bitmap.PixelHeight);
            if (bitmap.PixelWidth <= bitmap.PixelHeight)
                assertion = new InvalidOperationException("BF-F002: rotated page prints in its original portrait orientation.");
            preview.Close();
        };
        timer.Start();
        Click((Button)Owner.FindName("PrintBtn"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!rendered && watch.Elapsed < TimeSpan.FromSeconds(25)) Pump(100);
        if (!rendered) throw new TimeoutException("Print preview raster did not load.");
        if (assertion is not null) throw assertion;
    }

    private static void PrintSwitch()
    {
        string original = Path.Combine(Folder, "source.pdf");
        var builder = new PdfDocumentBuilder().AddBlankPage(300, 500);
        for (int i = 0; i < 350; i++)
            builder.AddTextField(0, "field" + i, 20, 100, 150, 25, "Original" + i);
        File.WriteAllBytes(original, builder.Build());
        string other = Path.Combine(Folder, "other.pdf");
        File.WriteAllBytes(other, new PdfDocumentBuilder().AddBlankPage(500, 300)
            .AddBlankPage(500, 300).AddBlankPage(500, 300).Build());
        Open(original);
        // The 350 field editors appear once the page has rendered, which can trail the open.
        TextBox? field = null;
        var appear = System.Diagnostics.Stopwatch.StartNew();
        while ((field = Descendants<TextBox>((DependencyObject)Owner.FindName("Viewer"))
                   .FirstOrDefault(t => t.Text.StartsWith("Original", StringComparison.Ordinal))) is null
               && appear.Elapsed < TimeSpan.FromSeconds(20)) Pump(100);
        if (field is null) throw new TimeoutException("The form fields did not appear after opening.");
        field.Text = "Updated";
        bool switched = false;
        bool previewShown = false;
        bool cancellationReported = false;
        var status = (TextBlock)Owner.FindName("StatusText");
        string canceled = (string)Owner.FindResource("Str_St_Canceled");
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
            .AddValueChanged(status, (_, _) => { if (status.Text == canceled) cancellationReported = true; });
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            Window? preview = App.Windows.Cast<Window>().FirstOrDefault(w => w.GetType().Name == "PrintPreviewWindow");
            if (preview is null) return;
            previewShown = true; timer.Stop(); preview.Close();
        };
        timer.Start();
        Click((Button)Owner.FindName("PrintBtn"));
        Owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { Open(other); switched = true; }));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!previewShown && !cancellationReported && watch.Elapsed < TimeSpan.FromSeconds(12)) Pump(100);
        timer.Stop();
        if (!switched) throw new InvalidOperationException("The timed document switch did not execute.");
        if (previewShown) throw new InvalidOperationException("BF-F006: print preview opened after the source document changed during preparation.");
        if (!cancellationReported) throw new TimeoutException("Print preparation did not report cancellation after the document switch.");
    }

    private static string Fixture(string name, string text, double x, double y,
        double left, double bottom, double right, double top)
    {
        string path = Path.Combine(Folder, name + ".pdf");
        var stream = new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .MoveText(x, y).ShowLatin1Text(text).EndText().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .MoveText(20, 450).ShowLatin1Text("KEPT-" + name.ToUpperInvariant()).EndText();
        var quad = new PdfTextQuad(new(left, top), new(right, top), new(left, bottom), new(right, bottom));
        File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(300, 500, stream)
            .AddRedactionMark(0, [quad]).Build());
        return path;
    }

    private static void Open(string path)
    {
        Owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(Owner, [path]);
        Pump(500);
    }

    private static void RunMacro(string destination, bool redact = true, string? mergeData = null)
    {
        int stage = 0;
        string lastDialog = "";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            try
            {
                Window? dialog = App.Windows.Cast<Window>().LastOrDefault(w => !ReferenceEquals(w, Owner) && w.IsVisible);
                if (dialog is null) return;
                string type = dialog.GetType().Name;
                if (lastDialog != type + stage)
                { Console.WriteLine("Macro dialog: " + type + ", stage " + stage); lastDialog = type + stage; }
                if (type == "MacroDialog" && stage == 0)
                {
                    stage = Descendants<RadioButton>(dialog).Any(b => b.Content is string text && text.Contains("current", StringComparison.OrdinalIgnoreCase)) ? 2 : 1;
                    QueueClick(Descendants<Button>(dialog).Single(b => b.IsDefault));
                }
                else if (type == "DocumentCommandDialog" && stage == 1)
                {
                    stage++;
                    QueueClick(Descendants<Button>(dialog).First(b => Text(b).Contains("current", StringComparison.OrdinalIgnoreCase)));
                }
                else if (type == "FileDialog" && stage == 2)
                {
                    stage++;
                    ((TextBox)dialog.FindName("FileNameBox")).Text = mergeData ?? destination;
                    QueueClick((Button)dialog.FindName("AcceptButton"));
                }
                else if (mergeData is not null && type == "FileDialog" && stage == 3
                    && dialog.Title.Contains((string)Owner.FindResource("Str_DataMerge_SelectOutput"), StringComparison.Ordinal))
                {
                    stage++;
                    ((TextBox)dialog.FindName("FileNameBox")).Text = destination;
                    QueueClick((Button)dialog.FindName("AcceptButton"));
                }
                else if (mergeData is not null && type == "ImportReviewDialog" && stage == 4)
                { stage++; QueueClick(Descendants<Button>(dialog).Single(b => b.IsDefault)); }
                else if (stage == 3 && Descendants<Button>(dialog).Any(b => Text(b) == "Yes"))
                { stage++; QueueClick(Descendants<Button>(dialog).First(b => Text(b) == "Yes")); }
                else if (redact && stage == 4 && Descendants<TextBox>(dialog).Any(t => t.AcceptsReturn))
                { stage++; QueueClick(Descendants<Button>(dialog).First(b => Text(b) == "OK")); }
                else if (stage >= (redact ? 5 : 4) && type == "DocumentReportDialog")
                { timer.Stop(); dialog.Close(); }
            }
            catch (Exception ex) { Console.Error.WriteLine("Macro stage " + stage + ": " + ex); Failure = ex; timer.Stop(); foreach (Window w in App.Windows.Cast<Window>().ToArray()) if (!ReferenceEquals(w, Owner)) w.Close(); }
        };
        timer.Start();
        OpenMacroWindow();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(destination) && Failure is null && watch.Elapsed < TimeSpan.FromSeconds(25)) Pump(100);
        timer.Stop();
        if (Failure is not null) throw Failure;
        if (!File.Exists(destination)) throw new TimeoutException("Macro output missing; dialog stage " + stage);
    }

    private static void OpenMacroWindow()
    {
        Click((Button)Owner.FindName("SaveMenuBtn"));
        MenuItem macros = Menus().SelectMany(m => Items(m.Items)).Single(m => Header(m).Contains("Macros", StringComparison.OrdinalIgnoreCase));
        macros.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static string Header(MenuItem item) => item.Header?.ToString() ?? "";
    private static string Text(ContentControl b) => b.Content is string s ? s : string.Join("", Descendants<TextBlock>(b).Select(t => t.Text));
    private static IEnumerable<MenuItem> Items(ItemCollection items)
    {
        foreach (MenuItem item in items.OfType<MenuItem>())
        { yield return item; foreach (var nested in Items(item.Items)) yield return nested; }
    }
    private static IEnumerable<ContextMenu> Menus() => PresentationSource.CurrentSources.Cast<PresentationSource>()
        .Where(s => s.RootVisual is not null).SelectMany(s => Descendants<ContextMenu>(s.RootVisual));
    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void QueueClick(Button b) => b.Dispatcher.BeginInvoke(new Action(() => Click(b)));
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { DependencyObject child = VisualTreeHelper.GetChild(parent, i); if (child is T t) yield return t; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
}
