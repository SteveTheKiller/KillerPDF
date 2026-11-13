using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

internal static class SessionEndingSmoke
{
    internal static int Run(Window first, Assembly assembly, string appPath, string folder,
        string scenario = "--verify-session-ending")
    {
        bool saveCase = scenario.EndsWith("-save", StringComparison.Ordinal);
        var second = (Window)Activator.CreateInstance(first.GetType())!;
        string source = Path.Combine(folder, "unsaved-session.pdf");
        byte[] sourceBytes = SignedCopySmoke.Fixture(measurement: false, pageWidth: 300);
        File.WriteAllBytes(source, sourceBytes);
        try
        {
            second.Show();
            first.GetType().GetMethod("OpenFromExternal", BindingFlags.Instance | BindingFlags.Public)!
                .Invoke(second, [source]);
            Pump();
            RotateDocument(second);
            var save = (Button)second.FindName("SaveAsBtn");
            bool Dirty() => save.Foreground is SolidColorBrush brush && brush.Color == Color.FromRgb(0xE0, 0x73, 0);
            if (!Dirty()) throw new InvalidOperationException("The real Rotate command did not leave an unsaved document.");
            Console.WriteLine("SessionEnding: real Rotate leaves the secondary window unsaved.");

            int prompts = 0, events = 0;
            Exception? failure = null;
            var seen = new HashSet<Window>();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
            var elapsed = Stopwatch.StartNew();
            timer.Tick += (_, _) =>
            {
                try
                {
                    if (elapsed.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Session-ending prompt did not finish.");
                    foreach (Window dialog in Application.Current.Windows.Cast<Window>()
                        .Where(window => window != first && window != second && window.IsVisible).ToArray())
                    {
                        if (!seen.Add(dialog)) continue;
                        Button choice = Children(dialog).OfType<Button>().Single(button =>
                            Equals(button.Content, Application.Current.FindResource(saveCase ? "Str_Btn_Save" : "Str_Btn_CancelDlg")));
                        prompts++;
                        choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                }
                catch (Exception error)
                {
                    failure = error;
                    timer.Stop();
                    foreach (Window dialog in Application.Current.Windows.Cast<Window>()
                        .Where(window => window != first && window != second).ToArray()) dialog.Close();
                }
            };
            Application.Current.SessionEnding += (_, _) => events++;
            // WPF constructs these arguments internally when Windows delivers the request.
            var ending = (SessionEndingCancelEventArgs)Activator.CreateInstance(typeof(SessionEndingCancelEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                [saveCase ? ReasonSessionEnding.Shutdown : ReasonSessionEnding.Logoff], null)!;
            timer.Start();
            try { Application.Current.GetType().GetMethod("SimulateSessionEnding")!.Invoke(Application.Current, [ending]); }
            finally { timer.Stop(); }
            if (failure is not null) throw failure;
            if (events != 1 || prompts != 1 || ending.Cancel == saveCase
                || !first.IsVisible || !second.IsVisible || Dirty() == saveCase)
                throw new InvalidOperationException($"Session-ending protection failed: events={events}, prompts={prompts}, cancel={ending.Cancel}, windows={first.IsVisible},{second.IsVisible}, unsaved={Dirty()}.");
            if (saveCase)
            {
                Assembly engine = Assembly.Load("KillerPdf.Engine");
                object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
                    .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!
                    .Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(source))])!;
                try
                {
                    object page = ((System.Collections.IEnumerable)engine.GetType("KillerPdf.Engine.Documents.PdfPageInformation", true)!
                        .GetMethod("Read")!.Invoke(null, [document])!).Cast<object>().Single();
                    if ((int)page.GetType().GetProperty("Rotation")!.GetValue(page)! != 90)
                        throw new InvalidOperationException("The saved PDF did not retain the approved 90-degree rotation.");
                }
                finally { (document as IDisposable)?.Dispose(); }
            }
            else if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(sourceBytes))
                throw new InvalidOperationException("Canceling session ending changed the source PDF.");
            string result = (saveCase
                ? "PASS Save during session ending writes the real 90-degree rotation, clears unsaved state and permits shutdown. App SHA256="
                : "PASS session ending prompts for the secondary window, Cancel requests cancellation, both windows and unsaved edits survive, and source bytes are unchanged. App SHA256=")
                + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
            File.WriteAllText(Path.Combine(folder, "result.txt"), result);
            Console.WriteLine(result);
            return 0;
        }
        finally
        {
            var cleanup = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
            cleanup.Tick += (_, _) =>
            {
                foreach (Window dialog in Application.Current.Windows.Cast<Window>()
                    .Where(window => window != first && window != second && window.IsVisible).ToArray())
                    Children(dialog).OfType<Button>().FirstOrDefault(button =>
                        Equals(button.Content, Application.Current.FindResource("Str_Btn_DontSave")))?
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            cleanup.Start();
            try
            {
                second.Close();
                var elapsed = Stopwatch.StartNew();
                while (second.IsVisible && elapsed.Elapsed < TimeSpan.FromSeconds(10)) { Pump(); Thread.Sleep(15); }
                if (second.IsVisible) throw new TimeoutException("The owned secondary test window did not close.");
            }
            finally { cleanup.Stop(); }
        }
    }

    internal static void RotateDocument(Window window)
    {
        ((Button)window.FindName("SidebarPagesTab")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        var pages = (ListBox)window.FindName("PageList");
        pages.SelectedIndex = 0;
        pages.ScrollIntoView(pages.Items[0]);
        window.UpdateLayout();
        var thumbnail = (ListBoxItem)pages.ItemContainerGenerator.ContainerFromIndex(0);
        pages.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
            { RoutedEvent = UIElement.MouseRightButtonUpEvent, Source = thumbnail });
        Pump();
        ContextMenu menu = PresentationSource.CurrentSources.Cast<PresentationSource>()
            .Select(item => item.RootVisual).OfType<DependencyObject>().SelectMany(Children)
            .OfType<ContextMenu>().SingleOrDefault(item => item.IsOpen)
            ?? throw new InvalidOperationException("The thumbnail right-click did not expose an open context menu.");
        string rotateName = (string)Application.Current.FindResource("Str_Ctx_RotateCWShort");
        Button rotate = Children(menu).OfType<Button>().SingleOrDefault(button =>
            System.Windows.Automation.AutomationProperties.GetName(button) == rotateName)
            ?? throw new InvalidOperationException("Rotate card button is missing. Actual="
                + string.Join("|", Children(menu).OfType<Button>().Select(button => System.Windows.Automation.AutomationProperties.GetName(button))));
        rotate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        menu.IsOpen = false;
        Pump();
    }

    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (DependencyObject child in Children(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
