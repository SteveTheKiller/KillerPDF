using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

internal static class TraySmoke
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    internal static int Run(string appPath, string appXaml, string folder, bool sessionOnly = false)
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
        var hostType = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TraySmokeHost"),
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
        Window? owner = null;
        try
        {
            // App startup order: the kit theme hooks (settings, KillerPDF's own keys) before the services.
            assembly.GetType("KillerPDF.Services.AppTheme", true)!.GetMethod("Configure",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null);
            foreach (string service in new[] { "ThemeManager", "LocaleManager", "FilmGrain" })
                assembly.GetType("KillerPDF.Services." + service, true)!.GetMethod("Initialize", Members)!.Invoke(null, null);
            if (sessionOnly)
            {
                VerifySessionEnding(assembly, app, report);
                File.WriteAllLines(Path.Combine(folder, "result.txt"), report);
                Console.WriteLine(string.Join(Environment.NewLine, report));
                return 0;
            }
            owner = (Window)Activator.CreateInstance(assembly.GetType("KillerPDF.Shell.MainWindow", true)!)!;
            owner.WindowStartupLocation = WindowStartupLocation.Manual;
            owner.Left = -10000; owner.Top = -10000; owner.ShowInTaskbar = false; owner.ShowActivated = false;
            owner.Show(); Pump();
            OpenAbout(owner);
            CheckBox option = Find<CheckBox>(owner, "AboutCloseToTray");
            if (option.IsChecked == true) throw new InvalidOperationException("Close to tray must default off.");
            Capture(owner, Path.Combine(folder, "about-default.png"));
            option.IsChecked = true;
            option.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            CloseAbout(owner);
            owner.Close(); Pump();
            if (owner.IsVisible || !app.Windows.Cast<Window>().Contains(owner))
                throw new InvalidOperationException("Enabled close did not hide the live window.");
            report.Add("PASS enabled close hides the live window");
            TrayCallback(0x0202);
            Wait(() => owner.IsVisible);
            report.Add("PASS tray click restores the window");
            OpenAbout(owner);
            if (option.IsChecked != true) throw new InvalidOperationException("About did not retain the tray setting.");
            Capture(owner, Path.Combine(folder, "about-enabled.png"));
            CloseAbout(owner);
            owner.Close(); Pump();
            InvokeTrayItem(app, "Str_Tray_Open", "KillerPDF");
            Wait(() => owner.IsVisible);
            report.Add("PASS tray Open restores the window");
            owner.Close(); Pump();
            owner.GetType().GetMethod("RestoreAndActivate", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, null);
            Wait(() => owner.IsVisible);
            report.Add("PASS external-launch activation restores the hidden window");
            OpenAbout(owner);
            option.IsChecked = false;
            option.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            CloseAbout(owner);
            bool closed = false;
            owner.Closed += (_, _) => closed = true;
            owner.Close();
            Wait(() => closed);
            report.Add("PASS disabled close exits the window");
            // Before the dirty tray Exit below: a confirmed Exit from the last window shuts the app
            // down (Shell/CloseToTray.cs), and this check needs it running. The session check ends
            // the app too, so it runs in its own process (--verify-tray-session).
            VerifyRegistrationFailure(assembly, report);

            owner = (Window)Activator.CreateInstance(assembly.GetType("KillerPDF.Shell.MainWindow", true)!)!;
            owner.WindowStartupLocation = WindowStartupLocation.Manual;
            owner.Left = -10000; owner.Top = -10000; owner.ShowInTaskbar = false; owner.ShowActivated = false;
            app.MainWindow = owner;
            owner.Show(); Pump();
            OpenAbout(owner);
            option = Find<CheckBox>(owner, "AboutCloseToTray");
            option.IsChecked = true;
            option.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            CloseAbout(owner);
            string fixture = Path.Combine(folder, "unsaved-source.pdf");
            byte[] original = Fixture();
            File.WriteAllBytes(fixture, original);
            owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [fixture]);
            ListBox pages = Find<ListBox>(owner, "PageList");
            Wait(() => pages.Items.Count == 1);
            pages.SelectedIndex = 0;
            pages.ScrollIntoView(pages.Items[0]);
            owner.UpdateLayout();
            ListBoxItem? page = null;
            Wait(() => (page = pages.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem) is not null);
            page!.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                { RoutedEvent = Mouse.MouseUpEvent });
            InvokeCardButton(app, "Str_Ctx_RotateCWShort");
            owner.Close(); Pump();
            if (owner.IsVisible) throw new InvalidOperationException("Closing dirty work did not hide the window.");
            ChooseExitPrompt(app, owner, folder, "Str_Btn_CancelDlg");
            if (!owner.IsVisible || !app.Windows.Cast<Window>().Contains(owner))
                throw new InvalidOperationException("Canceling tray Exit did not preserve the visible window.");
            report.Add("PASS dirty tray Exit Cancel restores the live window");
            // The kit tray ends a turned-down Exit at the user's next key or click in the window,
            // which a real close (clicking X) always sends first (Shell/CloseToTray.cs).
            owner.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner), 0, Key.A)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            owner.Close(); Pump();
            if (owner.IsVisible) throw new InvalidOperationException("Canceled Exit left close-to-tray bypass enabled.");
            closed = false;
            owner.Closed += (_, _) => closed = true;
            ChooseExitPrompt(app, owner, folder, "Str_Btn_DontSave");
            Wait(() => closed);
            if (!File.ReadAllBytes(fixture).AsSpan().SequenceEqual(original))
                throw new InvalidOperationException("Tray exit changed the source PDF without saving.");
            report.Add("PASS dirty tray Exit Don't Save closes through the fade and preserves source bytes");
            report.Add("App SHA256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath))));
            File.WriteAllLines(Path.Combine(folder, "result.txt"), report);
            Console.WriteLine(string.Join(Environment.NewLine, report));
            return 0;
        }
        finally { app.Shutdown(); }
    }

    private static void OpenAbout(Window owner)
    {
        Find<TextBlock>(owner, "VersionLabel").RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        Wait(() => Find<Grid>(owner, "AboutOverlay") is { IsVisible: true, Opacity: >= 0.99 });
    }

    private static void VerifyRegistrationFailure(Assembly assembly, List<string> report)
    {
        const string appName = "KillerPDFTrayRegistrationSmoke";
        var draft = new TextBox { Text = "Unsaved draft remains available." };
        var window = new Window { Width = 320, Height = 180, Content = draft,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false };
        Type trayType = assembly.GetType("KillerFamily.Shell.CloseToTray", true)!;
        bool enabled = true, registrationSucceeds = false, closed = false;
        object tray = trayType.GetMethod("Attach", Members)!.Invoke(null,
            [window, appName, (Func<string, string>)(key => key), (Func<bool>)(() => enabled), null, null, null,
                (Func<uint, bool>)(message => message != 0 || registrationSucceeds)])!;
        MethodInfo hideInstead = trayType.GetMethod("HideInstead", Members)!;
        window.Closing += (_, args) => hideInstead.Invoke(tray, [args]);
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show(); Pump();
            window.Close(); Pump();
            if (closed || !window.IsVisible || draft.Text != "Unsaved draft remains available.")
                throw new InvalidOperationException("Failed tray icon registration must cancel close and leave the window and its draft visible.");
            report.Add("PASS failed native icon registration cancels close and leaves the window visible");
            registrationSucceeds = true;
            window.Close(); Pump();
            if (closed || window.IsVisible) throw new InvalidOperationException("A recovered icon registration did not hide the live window.");
            registrationSucceeds = false;
            IntPtr callback = FindOwnedWindow(appName + "TrayIcon");
            SendMessage(callback, RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero); Pump();
            if (closed || !window.IsVisible || draft.Text != "Unsaved draft remains available.")
                throw new InvalidOperationException("Failed tray icon re-registration after TaskbarCreated must restore the live window.");
            report.Add("PASS failed native icon re-registration restores the window after TaskbarCreated");
            registrationSucceeds = true;
            window.Close(); Pump();
            if (closed || window.IsVisible) throw new InvalidOperationException("Close could not retry registration after the shell recovered.");
            SendMessage(callback, 0x8051, new IntPtr(1), new IntPtr(0x0202)); Pump();
            if (!window.IsVisible) throw new InvalidOperationException("The recovered tray icon could not restore its window.");
            report.Add("PASS close and tray restore recover after a failed registration");
        }
        finally { enabled = false; window.Close(); ((IDisposable)tray).Dispose(); }
    }

    private static void VerifySessionEnding(Assembly assembly, Application app, List<string> report)
    {
        const string appName = "KillerPDFTraySessionSmoke";
        var window = new Window { Width = 320, Height = 180,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false };
        Type trayType = assembly.GetType("KillerFamily.Shell.CloseToTray", true)!;
        bool enabled = true, closed = false, canceledObserved = false, acceptedObserved = false;
        Exception? failure = null;
        object tray = trayType.GetMethod("Attach", Members)!.Invoke(null,
            [window, appName, (Func<string, string>)(key => key), (Func<bool>)(() => enabled), null, null, null,
                (Func<uint, bool>)(_ => true)])!;
        MethodInfo hideInstead = trayType.GetMethod("HideInstead", Members)!;
        window.Closing += (_, args) => hideInstead.Invoke(tray, [args]);
        window.Closed += (_, _) => closed = true;
        SessionEndingCancelEventHandler cancel = (_, args) => { canceledObserved = true; args.Cancel = true; };
        SessionEndingCancelEventHandler accept = (_, _) => acceptedObserved = true;
        app.SessionEnding += cancel;
        app.MainWindow = window;
        window.Show();
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var ownedWindows = new List<IntPtr>();
                EnumThreadWindows(GetCurrentThreadId(), (handle, _) =>
                {
                    GetWindowThreadProcessId(handle, out uint process);
                    if (process == Environment.ProcessId) ownedWindows.Add(handle);
                    return true;
                }, IntPtr.Zero);
                IntPtr sessionWindow = IntPtr.Zero;
                foreach (IntPtr handle in ownedWindows)
                {
                    SendMessage(handle, 0x0011, IntPtr.Zero, new IntPtr(unchecked((int)0x80000000)));
                    if (canceledObserved) { sessionWindow = handle; break; }
                }
                if (sessionWindow == IntPtr.Zero)
                    throw new InvalidOperationException("Own-process WM_QUERYENDSESSION did not reach WPF SessionEnding.");
                foreach (IntPtr handle in ownedWindows)
                    SendMessage(handle, 0x0016, IntPtr.Zero, new IntPtr(unchecked((int)0x80000000)));
                window.Close(); Pump();
                if (closed || window.IsVisible)
                    throw new InvalidOperationException("Canceled Windows session ending must leave Close to tray enabled.");
                report.Add("PASS own-process canceled session messages preserve close-to-tray behavior");
                SendMessage(FindOwnedWindow(appName + "TrayIcon"), 0x8051, new IntPtr(1), new IntPtr(0x0202));
                if (!window.IsVisible) throw new InvalidOperationException("Canceled session ending left the window inaccessible.");
                app.SessionEnding -= cancel;
                app.SessionEnding += accept;
                SendMessage(sessionWindow, 0x0011, IntPtr.Zero, new IntPtr(unchecked((int)0x80000000)));
                if (!acceptedObserved) throw new InvalidOperationException("The final own-process session query was not observed.");
            }
            catch (Exception error) { failure = error; app.Shutdown(); }
        }));
        try
        {
            // Run creates WPF's session-message window. Every message above stays on this process's UI thread.
            app.Run();
            if (failure is not null) throw failure;
            if (!closed || !acceptedObserved) throw new InvalidOperationException("Accepted session ending did not close the live window.");
            report.Add("PASS accepted own-process session query still performs normal WPF shutdown");
        }
        finally
        {
            app.SessionEnding -= cancel; app.SessionEnding -= accept;
            enabled = false;
            if (!closed) window.Close();
            ((IDisposable)tray).Dispose();
        }
    }

    private static void CloseAbout(Window owner)
    {
        Find<Grid>(owner, "AboutOverlay").RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        Wait(() => !Find<Grid>(owner, "AboutOverlay").IsVisible);
    }

    private static void InvokeTrayItem(Application app, string key, params object[] arguments)
    {
        TrayCallback(0x0205);
        InvokeMenuItem(app, key, arguments);
    }

    private static void InvokeMenuItem(Application app, string key, params object[] arguments)
    {
        string label = string.Format((string)app.FindResource(key), arguments);
        MenuItem? item = null;
        Wait(() => (item = PresentationSource.CurrentSources.Cast<PresentationSource>()
            .Select(source => source.RootVisual).OfType<DependencyObject>()
            .SelectMany(Descendants<MenuItem>).FirstOrDefault(candidate => Equals(candidate.Header, label))) is not null);
        if (ItemsControl.ItemsControlFromItemContainer(item!) is ContextMenu menu) menu.IsOpen = false;
        item!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    // A card button is found by its accessible name; it closes the card itself, then acts.
    private static void InvokeCardButton(Application app, string key)
    {
        string label = (string)app.FindResource(key);
        Button? button = null;
        Wait(() => (button = PresentationSource.CurrentSources.Cast<PresentationSource>()
            .Select(source => source.RootVisual).OfType<DependencyObject>()
            .SelectMany(Descendants<Button>).FirstOrDefault(candidate =>
                System.Windows.Automation.AutomationProperties.GetName(candidate) == label)) is not null);
        button!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void ChooseExitPrompt(Application app, Window owner, string folder, string choice)
    {
        bool answered = false;
        Exception? failure = null;
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Dirty tray Exit did not show its prompt.");
                Window? prompt = app.Windows.Cast<Window>().FirstOrDefault(window => window != owner && window.IsVisible);
                if (prompt is null) return;
                string message = (string)app.FindResource("Str_Dlg_UnsavedExit");
                if (!Descendants<TextBlock>(prompt).Any(block => block.Text.Contains(message, StringComparison.Ordinal)))
                    throw new InvalidOperationException("Tray Exit showed an unexpected dialog.");
                if (!owner.IsVisible) throw new InvalidOperationException("Tray Exit did not restore the window before prompting.");
                Capture(prompt, Path.Combine(folder, choice + ".png"));
                string label = (string)app.FindResource(choice);
                Button button = Descendants<Button>(prompt).Single(candidate => Equals(candidate.Content, label));
                answered = true;
                timer.Stop();
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception error)
            {
                failure = error;
                timer.Stop();
                foreach (Window window in app.Windows.Cast<Window>().Where(window => window != owner).ToArray()) window.Close();
            }
        };
        timer.Start();
        try
        {
            InvokeTrayItem(app, "Str_Tray_Exit");
            if (failure is not null) throw failure;
            if (!answered) throw new InvalidOperationException("Dirty tray Exit skipped the unsaved-work prompt.");
        }
        finally { timer.Stop(); }
    }

    private static void TrayCallback(int notification)
    {
        IntPtr window = FindOwnedWindow("KillerPDFTrayIcon");
        SendMessage(window, 0x8051, new IntPtr(1), new IntPtr(notification));
        Pump();
    }

    private static IntPtr FindOwnedWindow(string title)
    {
        IntPtr window = IntPtr.Zero;
        while ((window = FindWindowEx(IntPtr.Zero, window, null, title)) != IntPtr.Zero)
        {
            GetWindowThreadProcessId(window, out uint process);
            if (process == Environment.ProcessId) break;
        }
        if (window == IntPtr.Zero) throw new InvalidOperationException("Tray callback window is missing.");
        return window;
    }

    private static T Find<T>(Window owner, string name) where T : FrameworkElement =>
        owner.FindName(name) as T ?? Descendants<T>(owner).SingleOrDefault(element => element.Name == name)
            ?? throw new InvalidOperationException("Missing rendered control: " + name);
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Wait(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Pump();
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Tray workflow did not become ready.");
            Thread.Sleep(10);
        }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T item) yield return item;
            foreach (T nested in Descendants<T>(child)) yield return nested;
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

    private static byte[] Fixture()
    {
        const string content = "BT /F1 12 Tf 30 150 Td (Tray save prompt fixture) Tj ET\n";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {content.Length} >>\nstream\n{content}endstream"];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int> { 0 };
        for (int i = 0; i < objects.Length; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string windowName);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumWindowCallback callback, IntPtr parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
