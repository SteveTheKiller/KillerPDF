using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using static DocumentStateSmoke;

internal static class PageNavigationTransitionsSmoke
{
    internal static int Run(Window owner, string folder)
    {
        owner.WindowState = WindowState.Normal;
        owner.Left = 32; owner.Top = 32; owner.Width = 950; owner.Height = 700;
        owner.Opacity = 1; owner.ShowActivated = true; owner.Activate();
        string path = Path.Combine(folder, "navigation-transition.pdf");
        byte[] original = PageDeletionSmoke.Fixture(2); File.WriteAllBytes(path, original);
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [path]);
        var pages = (ListBox)owner.FindName("PageList");
        Wait(() => pages.Items.Count == 2 && pages.SelectedIndex == 0 && !Dirty(owner), "clean two-page source");
        ParagraphEditSmoke.RunSearchAction(owner, "Str_NavigationButtons_Show", "Alt+Shift+N");
        Visible(true, "enabled two-page document");
        int prompts = 0;
        var seen = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                if (seen.Contains(dialog)) continue;
                if (dialog.GetType().Name != "DeletePagesDialog") throw new InvalidOperationException("Unexpected dialog during the synthetic navigation transition.");
                if (Descendants<TextBox>(dialog).Single().Text != "1") throw new InvalidOperationException("Delete did not prefill the first page.");
                seen.Add(dialog); prompts++;
                Button apply = Descendants<Button>(dialog).Single(button => button.IsDefault);
                owner.Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() => apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            }
        };
        timer.Start();
        try { Click(owner, "DeletePagesBtn"); }
        finally { timer.Stop(); }
        Wait(() => pages.Items.Count == 1 && Dirty(owner), "two-to-one-page deletion");
        if (prompts != 1) throw new InvalidOperationException("Expected one Delete dialog.");
        Visible(false, "one surviving page after Delete");
        Click(owner, "UndoBtn");
        Wait(() => pages.Items.Count == 2 && !Dirty(owner), "Delete Undo restores the clean two-page source");
        Visible(true, "two pages after Undo");
        Click(owner, "RedoBtn");
        Wait(() => pages.Items.Count == 1 && Dirty(owner), "Delete Redo restores one page");
        Visible(false, "one page after Redo");
        Click(owner, "UndoBtn");
        Wait(() => pages.Items.Count == 2 && !Dirty(owner), "final Undo restores original edit state");
        Visible(true, "two pages before Close");
        ShortcutHintsSmoke.Key(owner, 0x57, 0x11);
        Wait(() => pages.Items.Count == 0, "public Close document");
        Visible(false, "no document after Close with preference still enabled");
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(original)) throw new InvalidOperationException("Navigation transition changed the original source bytes.");
        Console.WriteLine("PASS navigation transitions: Delete, Undo, Redo and Close update optional arrows across two/one/zero pages without changing the source.");
        return 0;

        void Visible(bool expected, string stage)
        {
            Wait(() => ((Button)owner.FindName("PreviousPageBtn")).IsVisible == expected
                && ((Button)owner.FindName("NextPageBtn")).IsVisible == expected, stage);
            Console.WriteLine("CONTROL navigation visibility " + expected + ": " + stage);
        }
    }
}
