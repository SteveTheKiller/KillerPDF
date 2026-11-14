using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class WatchedFolderNavigationSmoke
{
    internal static void Run(Application app, Window owner, string folder)
    {
        Directory.CreateDirectory(folder);
        Pump();
        if (Descendants<ListBox>(owner).Where(list => list.Name == "PageList").Any(list => list.Items.Count != 0))
            throw new InvalidOperationException("The navigation check requires no open PDF.");
        Button rail = owner.FindName("SidebarDocumentToolsTab") as Button
            ?? throw new InvalidOperationException("The real Document tools rail button is missing.");
        RequireUsable(rail);
        rail.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Button[] Rows() => [.. Descendants<Button>(owner).Where(button => button.IsVisible
            && AutomationProperties.GetAutomationId(button) is { } id && (id.StartsWith("Str_DocumentTools_", StringComparison.Ordinal)
                || id is "Str_CertCatalog_Title" or "Str_Guided_Title" or "Str_CM_Title" or "Str_NativePdf_Convert"))];
        Wait(() => Rows().Length > 0);
        Button[] tools = Rows();
        if (tools.Length != 2 || AutomationProperties.GetAutomationId(tools[0]) != "Str_CertCatalog_Title"
            || AutomationProperties.GetAutomationId(tools[1]) != "Str_DocumentTools_Macros")
            throw new InvalidOperationException("The no-document Document tools panel must offer only the certificate catalog and Macros.");
        Capture((FrameworkElement)owner.FindName("SidebarBorder"), Path.Combine(folder, "document-tools-no-document.png"));
        Button macros = tools[1];
        RequireUsable(macros);
        VerifyDialogs(app, owner, macros, folder);
        if (Descendants<ListBox>(owner).Where(list => list.Name == "PageList").Any(list => list.Items.Count != 0))
            throw new InvalidOperationException("Opening Automatic folders unexpectedly opened a PDF.");
        File.WriteAllLines(Path.Combine(folder, "results.txt"),
        [
            "PASS the Document tools rail button opens its panel with no PDF open",
            "PASS no-document Document tools contains only the certificate catalog and Macros",
            "PASS rendered Macros and Automatic folders buttons open the real owned dialogs",
            "PASS both dialogs close through their rendered buttons without opening a PDF",
            "Evidence uses WPF routed control events, not native keyboard or mouse input"
        ]);
    }

    private static void VerifyDialogs(Application app, Window owner, Button macros, string folder)
    {
        Window? macroDialog = null, watchedDialog = null;
        Exception? failure = null;
        bool completed = false;
        int stage = 0;
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(20))
                    throw new TimeoutException("The rendered Macros to Automatic folders route did not complete.");
                if (stage == 0)
                {
                    macroDialog = app.Windows.Cast<Window>().FirstOrDefault(window =>
                        window.GetType().FullName == "KillerPDF.MacroDialog" && window.IsLoaded && window.IsVisible);
                    if (macroDialog is null) return;
                    if (!ReferenceEquals(macroDialog.Owner, owner))
                        throw new InvalidOperationException("Macros did not belong to the main window.");
                    Button folders = Descendants<Button>(macroDialog).Single(button =>
                        AutomationProperties.GetAutomationId(button) == "OpenWatchedFolders");
                    RequireUsable(folders);
                    if (!Equals(folders.Content, app.FindResource("Str_Watch_Title")))
                        throw new InvalidOperationException("The Automatic folders entry is not localized.");
                    Capture(macroDialog, Path.Combine(folder, "macros-dialog.png"));
                    stage = 1;
                    owner.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { folders.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                        catch (Exception error)
                        {
                            failure = error;
                            timer.Stop();
                            macroDialog.Close();
                        }
                    }), DispatcherPriority.Background);
                }
                else if (stage == 1)
                {
                    watchedDialog = app.Windows.Cast<Window>().FirstOrDefault(window =>
                        AutomationProperties.GetAutomationId(window) == "WatchedFolderDialog"
                        && window.IsLoaded && window.IsVisible);
                    if (watchedDialog is null) return;
                    if (!ReferenceEquals(watchedDialog.Owner, macroDialog))
                        throw new InvalidOperationException("Automatic folders did not belong to Macros.");
                    Button close = Descendants<Button>(watchedDialog).Single(button =>
                        AutomationProperties.GetAutomationId(button) == "WatchClose");
                    RequireUsable(close);
                    Capture(watchedDialog, Path.Combine(folder, "automatic-folders-dialog.png"));
                    stage = 2;
                    close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else if (stage == 2)
                {
                    if (watchedDialog!.IsVisible) return;
                    Button cancel = Descendants<Button>(macroDialog!).Single(button => button.IsCancel);
                    RequireUsable(cancel);
                    stage = 3;
                    cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else if (!macroDialog!.IsVisible)
                {
                    completed = true;
                    timer.Stop();
                }
            }
            catch (Exception error)
            {
                failure = error;
                timer.Stop();
                foreach (Window window in app.Windows.Cast<Window>().Where(window =>
                    window.GetType().FullName is "KillerPDF.MacroDialog" or "KillerPDF.WatchedFolderDialog").ToArray())
                    window.Close();
            }
        };
        timer.Start();
        try
        {
            macros.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(() => completed || failure is not null);
            if (failure is not null) throw failure;
        }
        finally
        {
            timer.Stop();
            watchedDialog?.Close();
            macroDialog?.Close();
        }
    }

    private static void RequireUsable(FrameworkElement control)
    {
        control.UpdateLayout();
        if (!control.IsVisible || !control.IsEnabled || control.ActualWidth <= 0 || control.ActualHeight <= 0)
            throw new InvalidOperationException("The rendered navigation control is unavailable: " + control.GetType().Name);
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Wait(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Pump();
            if (clock.Elapsed > TimeSpan.FromSeconds(25)) throw new TimeoutException("Navigation UI did not become ready.");
            Thread.Sleep(10);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T current) yield return current;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (T nested in Descendants<T>(VisualTreeHelper.GetChild(root, index))) yield return nested;
    }

    private static void Capture(FrameworkElement control, string path)
    {
        control.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(control.ActualWidth),
            (int)Math.Ceiling(control.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(control);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        png.Save(file);
    }
}
