using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using KillerPDF.Services;

namespace KillerPDF
{
    public partial class MainWindow
    {
        private void RotatePages_Click(int delta)
        {
            if (_doc is null) return;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) return;
            try
            {
                UndoEntry? documentUndo = CaptureDocumentUndo();
                var indices = new List<int>();
                foreach (PageThumbnailVm vm in selected) indices.Add(vm.PageIndex);
                // #169: rotation must not destroy the overlay annotations - the reload's default
                // keepAnnotations:false cleared them all, losing committed unsaved work on the
                // second rotation after placing it. Remap each rotated page's annotations through
                // the turn (render dims are still the pre-turn frame here; the reload clears them)
                // and keep everything through the reload. A page with no cached render dims keeps
                // its annotations unmapped - recoverable beats deleted.
                foreach (var idx in indices)
                    if (_annotations.TryGetValue(idx, out var anns) && _renderDims.TryGetValue(idx, out var dims))
                        Services.AnnotationRotate.Remap(anns, delta, dims.w, dims.h);
                int restoreIdx = PageList.SelectedIndex;
                SaveTempAndReload(
                    keepAnnotations: true,
                    remapRotations: rotations =>
                        PdfEngineIntegration.RemapRotationsAfterPageTurns(
                            rotations, indices, delta),
                    documentUndo: documentUndo);
                PageList.SelectedIndex = Math.Min(restoreIdx, PageList.Items.Count - 1);
                // After a rotation the page aspect ratio changes; always fit-to-page so the
                // full rotated page is visible regardless of the previous zoom level.
                FitToPage();
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() => FitToPage()));
                SetStatus(string.Format(Loc("Str_Rotated"), indices.Count));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_RotateFailed"), ex.Message), Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Split_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            var currentFile = _currentFile;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) { KillerDialog.Show(this, Loc("Str_Dlg_SelectExtract")); return; }
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Save)
                          { Filter = Loc("Str_Filter_Pdf") + "|*.pdf", Title = Loc("Str_Dlg_SaveExtractedAs"),
                            CheckFileExists = false, CheckPathExists = true };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var indices = new List<int>();
                foreach (PageThumbnailVm vm in selected) indices.Add(vm.PageIndex);
                int[] ordered = [.. indices.OrderBy(index => index)];
                PdfEngineIntegration.ExtractPages(
                    currentFile, dlg.FileName, ordered, _pageRotations);
                SetStatus(string.Format(Loc("Str_Extracted"), indices.Count, System.IO.Path.GetFileName(dlg.FileName)));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_SplitFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) { KillerDialog.Show(this, Loc("Str_Dlg_SelectDelete")); return; }
            var result = KillerDialog.Show(this, selected.Count == 1 ? Loc("Str_Dlg_DeletePage1") : string.Format(Loc("Str_Dlg_DeletePagesN"), selected.Count), "KillerPDF",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            try
            {
                var indices = new List<int>();
                foreach (PageThumbnailVm vm in selected) indices.Add(vm.PageIndex);
                SaveTempAndReload(
                    finalizeSavedFile: path => PdfEngineIntegration.RemovePages(path, indices),
                    remapRotations: rotations =>
                        PdfEngineIntegration.RemapRotationsAfterPageRemoval(rotations, indices));
                SetStatus(string.Format(Loc("Str_Deleted"), indices.Count, _doc?.PageCount));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_DeleteFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InsertBlankPage_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            int insertAfter = PageList.SelectedIndex >= 0
                ? PageList.SelectedIndex : _doc.PageCount - 1;
            int insertIndex = insertAfter + 1;
            try
            {
                SaveTempAndReload(
                    finalizeSavedFile: path =>
                        PdfEngineIntegration.InsertBlankPage(path, insertIndex, 595, 842),
                    remapRotations: rotations =>
                        PdfEngineIntegration.RemapRotationsAfterPageInsertion(
                            rotations, insertIndex));
                PageList.SelectedIndex = insertIndex;
                SetStatus(string.Format(Loc("Str_St_InsertedBlank"), insertAfter + 2));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_InsertFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Appends a blank A4 page to the END of the document. Used by the page-agnostic context menu
        // (sidebar empty area / outside the page), where there's no specific page to insert relative to.
        private void AddBlankPageAtEnd()
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            try
            {
                int insertIndex = _doc.PageCount;
                SaveTempAndReload(
                    finalizeSavedFile: path =>
                        PdfEngineIntegration.InsertBlankPage(path, insertIndex, 595, 842),
                    remapRotations: rotations =>
                        PdfEngineIntegration.RemapRotationsAfterPageInsertion(
                            rotations, insertIndex));
                if (PageList.Items.Count > 0) PageList.SelectedIndex = PageList.Items.Count - 1;
                SetStatus(string.Format(Loc("Str_St_AddedBlank"), _doc?.PageCount));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_AddPageFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || PageList.SelectedIndex <= 0) return;
            int idx = PageList.SelectedIndex;
            SaveTempAndReload(
                finalizeSavedFile: path => PdfEngineIntegration.MovePage(path, idx, idx - 1),
                remapRotations: rotations =>
                    PdfEngineIntegration.RemapRotationsAfterPageMove(rotations, idx, idx - 1));
            PageList.SelectedIndex = idx - 1;
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || PageList.SelectedIndex < 0 || PageList.SelectedIndex >= _doc.PageCount - 1) return;
            int idx = PageList.SelectedIndex;
            SaveTempAndReload(
                finalizeSavedFile: path => PdfEngineIntegration.MovePage(path, idx, idx + 1),
                remapRotations: rotations =>
                    PdfEngineIntegration.RemapRotationsAfterPageMove(rotations, idx, idx + 1));
            PageList.SelectedIndex = idx + 1;
        }

        // Cancels the previous thumbnail background load when the file changes.
        // The FOCUSED pane's thumbnail loader token. The panes keep their own (PdfViewer.ThumbCts):
        // one window-wide token had each pane canceling the other's decode.
        private System.Threading.CancellationTokenSource? _thumbCts
        {
            get => ActiveViewer.ThumbCts;
            set => ActiveViewer.ThumbCts = value;
        }

        /// <summary>Re-seat the newly focused pane's thumbnails instead of rebuilding them.
        ///
        /// Not folded into RefreshPageList as a general cache: every other caller is calling it
        /// BECAUSE the pages changed, and a cache keyed on the file path would make those no-op and
        /// leave stale thumbnails on screen. Only a focus switch knows nothing changed.
        ///
        /// Falls back to a full refresh whenever the cache cannot be proven to match.</summary>
        internal void RestorePageListForActivePane()
        {
            var cached = ActiveViewer.ThumbCache;
            int preservedPage = ActiveViewer.CurrentPageIndex;

            bool usable = cached != null
                       && _doc != null
                       && _currentFile != null
                       && ActiveViewer.ThumbCacheComplete
                       && cached.Length == _doc.PageCount
                       && string.Equals(ActiveViewer.ThumbCacheFile, _currentFile,
                                        System.StringComparison.OrdinalIgnoreCase);

            if (!usable) { RefreshPageList(); return; }

            // No cancel here. The panes own their thumbnail lists and their loader tokens
            // separately, so the other pane's decode is writing into ITS array and should be left
            // to finish - canceling it was what left a pane showing page labels with no pictures
            // after any focus change.
            _sidebarPages.Show(cached);
            ActiveViewer.SyncPageListSelection(preservedPage);
        }

        internal void RefreshPageList()
        {
            var thumbnailOwner = ActiveViewer;
            // Cancel any in-flight thumbnail load for the previous file.
            _thumbCts?.Cancel();
            // Hold the token locally rather than reading it back off _thumbCts. That property
            // forwards to the ACTIVE TAB (PdfViewer.ThumbCts), and both sides are guarded on
            // _active, so with no tab open the write is dropped and the read comes back null.
            // A killerpdf: launch reaches here in exactly that state: it goes to
            // OpenFromExternal, the one startup path that never calls EnsureInitialSession.
            var freshCts = new System.Threading.CancellationTokenSource();
            _thumbCts = freshCts;
            var ct = freshCts.Token;

            if (_doc is null || _currentFile is null)
            {
                _sidebarPages.Show(null);
                return;
            }

            int    pageCount = EnsureEngineDocumentSession().Pages.Count;
            string filePath  = _currentFile;
            int preservedPage = ActiveViewer.CurrentPageIndex;

            // Snapshot rotations on the UI thread before going to background.
            var rotSnap = new Dictionary<int, int>(_pageRotations);

            // Carry forward any existing thumbnails so the list never flashes blank
            // during reload (e.g. after a rotation).  New thumbnails replace them as
            // the background loader finishes each page.
            var oldItems = PageList.ItemsSource is PageThumbnailVm[] oi ? oi : null;

            var items = new PageThumbnailVm[pageCount];
            for (int i = 0; i < pageCount; i++)
            {
                rotSnap.TryGetValue(i, out int rot);
                items[i] = new PageThumbnailVm(i, filePath, rot);
                // Seed with stale thumbnail - better than blank while reloading
                if (oldItems != null && i < oldItems.Length)
                {
                    var prev = oldItems[i].Thumbnail;
                    if (prev != null) items[i].SetThumbnailDirect(prev);
                }
            }
            _sidebarPages.Show(items);
            ActiveViewer.SyncPageListSelection(preservedPage);

            // Hand the array to the pane it belongs to, so focusing away and back can re-seat it
            // rather than decode the document again. RestorePageListForActivePane is the only reader.
            ActiveViewer.ThumbCache     = items;
            ActiveViewer.ThumbCacheFile = filePath;
            ActiveViewer.ThumbCacheComplete = false;

            // Load thumbnails sequentially on a background thread via a single doc reader.
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var renderSession = PdfPageRenderSession.OpenEngineFirst(filePath, 128, 256);
                    for (int i = 0; i < pageCount; i++)
                    {
                        if (ct.IsCancellationRequested) return;
                        try
                        {
                            PdfRenderedPage page = renderSession.RenderPage(i);
                            int tw = page.Width;
                            int th = page.Height;
                            byte[] raw = page.Pixels;
                            if (tw <= 0 || th <= 0 || raw == null || raw.Length < tw * th * 4)
                                continue;
                            rotSnap.TryGetValue(i, out int rot);
                            if (rot != 0)
                                (raw, tw, th) = BitmapHelpers.RotateBitmap(raw, tw, th, rot);
                            var src = PageThumbnailVm.BuildThumbFromRaw(raw, tw, th);
                            if (src != null && !ct.IsCancellationRequested)
                                items[i].SetThumbnail(src);
                        }
                        catch { /* skip failed thumbnail; item shows label-only */ }
                    }
                    if (!ct.IsCancellationRequested)
                        thumbnailOwner.Dispatcher.Invoke(() =>
                            thumbnailOwner.MarkThumbnailCacheComplete(items));
                }
                catch { /* docReader open failed; all items remain label-only */ }
            }, ct);
        }
    }
}
