using System.IO;
using System.Windows;
using KillerPDF.Services;
using PdfViewer = KillerPDF.Controls.PdfViewer;

namespace KillerPDF.Shell
{
    // Insert Pages (issue 529), the page card's tile: pages from another open tab, all or a range, go in before or
    // after a page of this document. With no other tab open the tile is the merge it replaced, and the dialog's files
    // on disk row runs that merge at the chosen place, so drops, folders and repairs work as before.
    //
    // The source's pages are its saved copy as it stands, markup, form values and page turns included, which only the
    // window can write: the document fields describe the front tab of the focused pane. So the source tab is brought
    // forward for the copy, as closing a dirty background tab does, and both panes go back to what they showed.
    public partial class MainWindow
    {
        private async Task InsertPagesAt(int page)
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            if (!EnsureEditable()) return;
            PdfViewer home = ActiveViewer;
            PdfViewer.DocumentSession? homeTab = home.ActiveSessionExt;
            var sources = new List<(PdfViewer Viewer, PdfViewer.DocumentSession Tab, InsertPagesSource Shown)>();
            foreach (PdfViewer viewer in IsSplit ? new[] { Viewer, ViewerB } : new[] { home })
                foreach (PdfViewer.DocumentSession tab in viewer.PageSourceTabsExt())
                    if (!ReferenceEquals(tab, homeTab)) sources.Add((viewer, tab, Describe(tab)));
            if (sources.Count == 0) { await MergeAtIndex(page + 1); return; }

            var dialog = new InsertPagesDialog(this, [.. sources.Select(source => source.Shown)], _doc.PageCount, page);
            if (dialog.ShowDialog() != true) return;
            if (dialog.FromDisk) { await MergeAtIndex(dialog.InsertAt); return; }
            var (sourceViewer, sourceTab, shown) = sources[dialog.SourceIndex];
            var isTargetCurrent = CaptureDocumentOrigin();
            string? pagesFile = null;
            try
            {
                pagesFile = await CopyPagesFromTab(home, homeTab, sourceViewer, sourceTab, shown.Title, dialog);
                if (pagesFile is null) return;
                if (_doc is null || !isTargetCurrent())
                {
                    SetStatus(Loc("Str_Merge_TargetChanged"));
                    return;
                }
                IReadOnlyList<KillerPdf.Engine.Documents.PdfPageInformation> pages =
                    PdfEngineIntegration.ReadPageInformation(pagesFile, null);
                var import = new PdfEngineIntegration.ImportedDocument(pagesFile,
                    [.. pages.Select(info => info.Rotation)], sourceTab.OriginalFile ?? shown.Title);
                InsertImportedDocuments([import], dialog.InsertAt);
                SetStatus(string.Format(Loc("Str_InsertPages_Done"), pages.Count, shown.Title));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_MergeFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (pagesFile is not null)
                    try { File.Delete(pagesFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        // The chosen pages of the source tab in a new temp PDF, or null when the tab could not give them (still
        // opening, its protection said no, or the range is past its end). Extract Pages makes its file the same way.
        private async Task<string?> CopyPagesFromTab(PdfViewer home, PdfViewer.DocumentSession? homeTab,
            PdfViewer sourceViewer, PdfViewer.DocumentSession sourceTab, string title, InsertPagesDialog choice)
        {
            PdfViewer.DocumentSession? sourceWasShowing = sourceViewer.ActiveSessionExt;
            string? copyPath = null;
            string? output = null;
            bool done = false;
            // A reason given while the source tab shows, kept so switching back to the home tab can't wipe it.
            string? reason = null;
            try
            {
                FocusPane(sourceViewer);
                if (!ReferenceEquals(sourceViewer.ActiveSessionExt, sourceTab)) sourceViewer.SwitchToTabExt(sourceTab);
                // A tab that had not loaded opens now; a big or repaired file finishes in the background.
                if (!ReferenceEquals(ActiveViewer, sourceViewer) || !ReferenceEquals(sourceViewer.ActiveSessionExt, sourceTab) || _doc is null)
                {
                    reason = string.Format(Loc("Str_InsertPages_StillOpening"), title);
                    return null;
                }
                string statusBefore = StatusText.Text;
                if (!EnsureMayWrite(PdfOutput.Printable))
                {
                    // Canceling the owner password says so in the status bar; a refusal dialog leaves it as it was.
                    if (StatusText.Text != statusBefore) reason = StatusText.Text;
                    return null;
                }
                if (InsertPagesPlan.SourcePages(choice.AllPages, choice.FirstPage, choice.LastPage, _doc.PageCount) is not { } pages)
                {
                    KillerDialog.Show(this, Loc("Str_InvalidRange"), "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;
                }
                CommitActiveTextBox();
                Dictionary<int, int> rotations = SnapshotPageRotations();
                copyPath = WriteSavedCopyToTemp(throwaway: true).Path;
                string copy = copyPath;
                string file = output = App.MakeTempFile("insertpages");
                await RunBusyAsync(() =>
                {
                    PdfEngineIntegration.ExtractPages(copy, file, pages, rotations);
                    return true;
                });
                done = true;
                return output;
            }
            finally
            {
                if (copyPath is not null)
                    try { File.Delete(copyPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                if (!done && output is not null)
                    try { File.Delete(output); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                if (sourceViewer.ContainsSessionExt(sourceWasShowing) && !ReferenceEquals(sourceViewer.ActiveSessionExt, sourceWasShowing))
                {
                    FocusPane(sourceViewer);
                    sourceViewer.SwitchToTabExt(sourceWasShowing);
                }
                FocusPane(home);
                if (home.ContainsSessionExt(homeTab) && !ReferenceEquals(home.ActiveSessionExt, homeTab))
                    home.SwitchToTabExt(homeTab);
                if (reason is not null)
                {
                    SetStatus(reason);
                    // The tab switch can post its own page status; the reason goes back on top once that has run.
                    _ = Dispatcher.BeginInvoke(() => { if (StatusText.Text != reason) SetStatus(reason); },
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
            }
        }

        private static InsertPagesSource Describe(PdfViewer.DocumentSession tab)
        {
            string? path = tab.OriginalFile ?? tab.DeferredPath;
            int? count = tab.Doc?.PageCount;
            if (count is null && tab.DeferredPath is { } deferred)
            {
                // A tab not opened yet is counted from its file; a protected one waits until it opens.
                try { count = PdfEngineIntegration.ReadPageInformation(deferred, null).Count; }
                catch (Exception) { count = null; }
            }
            return new InsertPagesSource(tab.Title, path, count);
        }
    }
}
