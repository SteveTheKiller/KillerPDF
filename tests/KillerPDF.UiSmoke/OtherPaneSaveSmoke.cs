using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using static DocumentStateSmoke;

// One file open in both panes is two independent copies. Saving one with Ctrl+S, not only through
// the Save button, must first warn that the other pane's unsaved edits to the same file will be
// overwritten, and Cancel must leave the file on disk untouched.
internal static class OtherPaneSaveSmoke
{
    internal static int Run(Window owner, string folder)
    {
        try { return RunCore(owner, folder); }
        finally
        {
            var cleanup = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            cleanup.Tick += (_, _) =>
            {
                foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
                {
                    Button? discard = Descendants<Button>(dialog).FirstOrDefault(button =>
                        Equals(button.Content, owner.FindResource("Str_Btn_DontSave")) || Equals(button.Content, owner.FindResource("Str_Btn_Quit")));
                    if (discard is not null) discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.Close();
                }
            };
            cleanup.Start();
            try { if (owner.IsVisible) owner.Close(); Wait(() => !owner.IsVisible, "owned two-pane cleanup"); }
            catch (InvalidOperationException) { }
            finally { cleanup.Stop(); }
        }
    }

    private static int RunCore(Window owner, string folder)
    {
        Rect work = SystemParameters.WorkArea;
        owner.Left = work.Left + 8; owner.Top = work.Top + 8;
        owner.Width = Math.Min(1200, work.Width - 16); owner.Height = Math.Min(760, work.Height - 16);
        owner.Opacity = 1; owner.Activate();
        ShortcutHintsSmoke.Focus(owner);
        string source = Path.Combine(folder, "two-pane-source.pdf");
        File.WriteAllBytes(source, TableExportSmoke.Fixture());
        byte[] original = File.ReadAllBytes(source);
        MethodInfo open = owner.GetType().GetMethod("OpenFromExternal", [typeof(string)])!;
        MethodInfo rotate = owner.GetType().GetMethod("RotatePages_Click", BindingFlags.NonPublic | BindingFlags.Instance)!;

        open.Invoke(owner, [source]);
        Wait(() => Descendants<Canvas>(owner).Any(IsPageCanvas), "first pane rendered");
        Click(owner, "SplitPaneRailBtn");
        var paneB = (FrameworkElement)owner.FindName("ViewerB");
        var paneA = (FrameworkElement)owner.FindName("Viewer");
        Wait(() => paneB.IsVisible && paneB.ActualWidth >= 320, "second pane shown");
        Activate(paneB);
        open.Invoke(owner, [source]);
        Wait(() => Descendants<Canvas>(paneB).Any(IsPageCanvas), "second pane rendered the same file");
        RotateFirstPage(owner, rotate);
        Wait(() => Dirty(owner), "second pane has unsaved edits");
        Activate(paneA);
        RotateFirstPage(owner, rotate);
        Wait(() => Dirty(owner), "first pane has unsaved edits");

        string name = Path.GetFileName(source);
        string warningStart = ((string)owner.FindResource("Str_Dlg_SaveOtherPaneDirty")).Split('{')[0];
        var dialogs = new List<string>();
        var seen = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                if (!seen.Add(dialog)) continue;
                dialogs.Add(string.Join(" ", Descendants<TextBlock>(dialog).Select(block => block.Text)));
                Button? cancel = Descendants<Button>(dialog).FirstOrDefault(button =>
                    Equals(button.Content, owner.FindResource("Str_Btn_Cancel")) || Equals(button.Content, owner.FindResource("Str_Btn_CancelDlg")));
                if (cancel is not null) cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else dialog.Close();
            }
        };
        timer.Start();
        try
        {
            ShortcutHintsSmoke.Focus(owner);
            ShortcutHintsSmoke.Key(owner, 0x53, 0x11);   // Ctrl+S in the first pane
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (dialogs.Count == 0 && watch.Elapsed < TimeSpan.FromSeconds(5)) { Pump(); Thread.Sleep(10); }
        }
        finally { timer.Stop(); }
        Pump();
        Console.WriteLine("CONTROL Ctrl+S with the same file dirty in the other pane: dialogs=" + string.Join(" | ", dialogs));
        if (!dialogs.Any(text => text.Contains(warningStart.Trim(), StringComparison.Ordinal)))
            throw new InvalidOperationException("Ctrl+S saved over the other pane's unsaved edits without warning.");
        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(original))
            throw new InvalidOperationException("Canceling the other-pane warning still changed the file.");
        if (!Dirty(owner)) throw new InvalidOperationException("Canceling the other-pane warning dropped the first pane's edits.");
        Console.WriteLine("PASS other-pane save: Ctrl+S warns about the other pane's unsaved copy, and Cancel leaves the file and both edits alone.");
        return 0;
    }

    private static void Activate(FrameworkElement pane)
    {
        pane.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Pump();
    }

    private static void RotateFirstPage(Window owner, MethodInfo rotate)
    {
        var pages = (ListBox)owner.FindName("PageList");
        pages.SelectedIndex = 0;
        Pump();
        rotate.Invoke(owner, [90, null]);
        Pump();
    }
}
