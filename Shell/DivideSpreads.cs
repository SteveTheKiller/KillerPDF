using System.Windows;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    // Divide Spreads (#382): split scanned two-page spreads at a line the user places. Each
    // divided page is followed by its second half, and the whole change is one undo step.
    public partial class MainWindow
    {
        private void DivideSpreads()
        {
            if (_doc is null || _currentFile is null) { SetStatus(Loc("Str_Msg_OpenFirst")); return; }
            if (!EnsureEditable(continueAfterUnlock: false)) return;
            CommitActiveTextBox();
            int current = Math.Clamp(Math.Max(0, PageList.SelectedIndex), 0, Math.Max(0, PageList.Items.Count - 1));
            int[] selected = [.. PageList.SelectedItems.Cast<PageThumbnailVm>()
                .Select(page => page.PageIndex).Distinct().OrderBy(index => index)];
            var dialog = new SpreadDivideDialog(this, RenderPageBitmap(current, 1200), selected.Length,
                PageList.Items.Count);
            dialog.ShowDialog();
            if (!dialog.Confirmed) return;

            int[] pages = dialog.Scope switch
            {
                SpreadDivideDialog.DivideScope.AllPages => [.. Enumerable.Range(0, PageList.Items.Count)],
                SpreadDivideDialog.DivideScope.SelectedPages => selected,
                _ => [current]
            };
            if (pages.Length == 0) return;

            // Markup the user placed on a divided page is drawn into it first, so each half keeps
            // whatever sits on it. Ask before that makes it permanent.
            var marked = pages.Where(page => _annotations.TryGetValue(page, out var list) && list.Count > 0).ToHashSet();
            if (marked.Count > 0 && KillerDialog.Show(this, Loc("Str_Divide_MarkupWarn"), "KillerPDF",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            PdfSpreadDivisionRequest[] requests = [.. pages.Select(page =>
                new PdfSpreadDivisionRequest(page, dialog.Direction, dialog.Position))];
            Dictionary<int, int> rotations = SnapshotPageRotations();
            var burn = marked.ToDictionary(page => page, page => _annotations[page]);
            var dims = new Dictionary<int, (int w, int h)>(_renderDims);
            int[] descending = [.. pages.OrderByDescending(page => page)];
            int skippedPictures = 0;
            try
            {
                ReloadWithRemappedAnnotations(
                    annotations =>
                    {
                        foreach (int page in descending)
                        {
                            annotations.Remove(page);
                            PageAnnotationInsertion.Shift(annotations, page + 1, 1);
                        }
                    },
                    documentUndo => SaveTempAndReload(
                        keepAnnotations: true,
                        remapRedactionMarks: true,
                        finalizeSavedFile: path =>
                        {
                            if (burn.Count > 0)
                            {
                                PdfEngineBurn.Burn(path, burn, dims, rotations: rotations);
                                skippedPictures = PdfEngineBurn.SkippedPictures;
                            }
                            PdfEngineIntegration.ApplyPageRotations(path, rotations);
                            PdfEngineIntegration.DivideSpreads(path, requests);
                        },
                        remapRotations: map =>
                        {
                            foreach (int page in descending)
                                PdfEngineIntegration.RemapRotationsAfterPageDuplication(map, page);
                        },
                        selectedPageAfterReload: pages[0],
                        documentUndo: documentUndo));
                SetStatus(Services.PicturesSkippedNote.Append(
                    string.Format(Loc("Str_Divide_Done"), pages.Length, pages.Length * 2),
                    skippedPictures, Loc("Str_Save_PicturesSkipped")));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_Divide_Failed"), ex.Message), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
