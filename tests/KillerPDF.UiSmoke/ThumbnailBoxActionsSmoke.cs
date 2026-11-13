using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using static DocumentStateSmoke;

internal static class ThumbnailBoxActionsSmoke
{
    internal static void Run(Window owner, string folder, string source, byte[] original, int[] selected)
    {
        var pages = (ListBox)owner.FindName("PageList");
        string extracted = Path.Combine(folder, "box-selected-pages.pdf");
        string selectedLabels = string.Join('|', selected.Select(page => "PAGE_" + (page + 1)));
        string allLabels = string.Join('|', Enumerable.Range(1, 80).Select(page => "PAGE_" + page));
        string survivingLabels = string.Join('|', Enumerable.Range(0, 80).Except(selected).Select(page => "PAGE_" + (page + 1)));
        Command(extract: true);
        if (PageDeletionSmoke.Read(extracted) != (selected.Length, selectedLabels)
            || pages.Items.Count != 80 || Dirty(owner) || ((Button)owner.FindName("UndoBtn")).IsEnabled
            || !File.ReadAllBytes(source).AsSpan().SequenceEqual(original)
            || !pages.SelectedItems.Cast<object>().Select(pages.Items.IndexOf).Order().SequenceEqual(selected))
            throw new InvalidOperationException("Extract did not preserve the drag-selected range, source, or clean history.");
        Console.WriteLine("CONTROL box Extract: strict output contains exactly the drag-selected labels in source order; source and selection unchanged.");
        Command(extract: false);
        Wait(() => pages.Items.Count == 80 - selected.Length && Dirty(owner), "Delete of drag-selected pages");
        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(original))
            throw new InvalidOperationException("Box-selected Delete changed source bytes before Save.");
        Save(owner);
        if (PageDeletionSmoke.Read(source) != (80 - selected.Length, survivingLabels) || Dirty(owner))
            throw new InvalidOperationException("Saved Delete did not retain exactly the unselected pages.");
        Click(owner, "UndoBtn");
        Wait(() => pages.Items.Count == 80 && Dirty(owner), "Undo of box-selected Delete");
        Save(owner);
        if (PageDeletionSmoke.Read(source) != (80, allLabels) || Dirty(owner))
            throw new InvalidOperationException("Undo did not restore all original page identities in order.");
        Click(owner, "RedoBtn");
        Wait(() => pages.Items.Count == 80 - selected.Length && Dirty(owner), "Redo of box-selected Delete");
        Save(owner);
        if (PageDeletionSmoke.Read(source) != (80 - selected.Length, survivingLabels) || Dirty(owner)
            || PageDeletionSmoke.Read(extracted) != (selected.Length, selectedLabels))
            throw new InvalidOperationException("Redo changed the expected survivor order or extracted output.");
        Console.WriteLine("PASS thumbnail box actions: the offscreen drag-selected set drives exact Extract and Delete output with strict Save, Undo, and Redo page-order checks.");

        void Command(bool extract)
        {
            int ranges = 0, pickers = 0;
            var seen = new HashSet<Window>();
            Exception? failure = null;
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
                {
                    if (!seen.Add(dialog)) continue;
                    try
                    {
                        Button accept;
                        if (dialog.GetType().Name == (extract ? "DocumentValuesDialog" : "DeletePagesDialog"))
                        {
                            ranges++;
                            if (Descendants<TextBox>(dialog).Single().Text != string.Join(',', selected.Select(page => page + 1)))
                                throw new InvalidOperationException("The action range did not prefill the drag-selected set.");
                            accept = Descendants<Button>(dialog).Single(button => button.IsDefault);
                        }
                        else if (extract && dialog.GetType().Name == "FileDialog")
                        {
                            pickers++;
                            ((TextBox)dialog.FindName("FileNameBox")).Text = extracted;
                            accept = (Button)dialog.FindName("AcceptButton");
                        }
                        else throw new InvalidOperationException("Unexpected dialog during box-selected page action.");
                        owner.Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() => accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                    }
                    catch (Exception error) { failure = error; dialog.Close(); }
                }
            };
            timer.Start();
            try { Click(owner, extract ? "ExtractPagesBtn" : "DeletePagesBtn"); }
            finally { timer.Stop(); }
            if (failure is not null) throw new InvalidOperationException("Box-selected page-action dialog failed.", failure);
            if (ranges != 1 || pickers != (extract ? 1 : 0))
                throw new InvalidOperationException("The box-selected command did not use exactly one range dialog and expected picker.");
        }
    }
}
