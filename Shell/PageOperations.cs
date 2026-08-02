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
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using KillerPDF.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

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
                var indices = new List<int>();
                foreach (PageThumbnailVm vm in selected) indices.Add(vm.PageIndex);
                foreach (var idx in indices)
                    _doc.Pages[idx].Rotate = ((_doc.Pages[idx].Rotate + delta) % 360 + 360) % 360;
                int restoreIdx = PageList.SelectedIndex;
                SaveTempAndReload();
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
                          { Filter = "PDF files|*.pdf", Title = "Save extracted pages as",
                            CheckFileExists = false, CheckPathExists = true };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var indices = new List<int>();
                foreach (PageThumbnailVm vm in selected) indices.Add(vm.PageIndex);
                using var importDoc = PdfReader.Open(currentFile, PdfDocumentOpenMode.Import);
                var newDoc = new PdfDocument();
                foreach (var idx in indices.OrderBy(i => i))
                    newDoc.AddPage(importDoc.Pages[idx]);
                newDoc.Save(dlg.FileName);
                SetStatus(string.Format(Loc("Str_Extracted"), indices.Count, System.IO.Path.GetFileName(dlg.FileName)));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Split failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            var doc = _doc;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) { KillerDialog.Show(this, Loc("Str_Dlg_SelectDelete")); return; }
            var result = KillerDialog.Show(this, $"Delete {selected.Count} {(selected.Count == 1 ? "page" : "pages")}?", "KillerPDF",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            try
            {
                var indices = new List<int>();
                foreach (PageThumbnailVm vm in selected) indices.Add(vm.PageIndex);
                foreach (var idx in indices.OrderByDescending(i => i))
                    doc.Pages.RemoveAt(idx);
                SaveTempAndReload();
                SetStatus(string.Format(Loc("Str_Deleted"), indices.Count, _doc?.PageCount));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Delete failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InsertBlankPage_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            var doc = _doc;
            int insertAfter = PageList.SelectedIndex >= 0 ? PageList.SelectedIndex : doc.PageCount - 1;
            try
            {
                var blank = new PdfPage { Width = XUnit.FromPoint(595), Height = XUnit.FromPoint(842) };
                doc.Pages.Insert(insertAfter + 1, blank);
                SaveTempAndReload();
                PageList.SelectedIndex = insertAfter + 1;
                SetStatus($"Inserted blank page at position {insertAfter + 2}");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Insert failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Appends a blank A4 page to the END of the document. Used by the page-agnostic context menu
        // (sidebar empty area / outside the page), where there's no specific page to insert relative to.
        private void AddBlankPageAtEnd()
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            var doc = _doc;
            try
            {
                doc.Pages.Add(new PdfPage { Width = XUnit.FromPoint(595), Height = XUnit.FromPoint(842) });
                SaveTempAndReload();
                if (PageList.Items.Count > 0) PageList.SelectedIndex = PageList.Items.Count - 1;
                SetStatus($"Added blank page (now {_doc?.PageCount} pages)");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Add page failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || PageList.SelectedIndex <= 0) return;
            var doc = _doc;
            int idx = PageList.SelectedIndex;
            var page = doc.Pages[idx];
            doc.Pages.RemoveAt(idx);
            doc.Pages.Insert(idx - 1, page);
            SaveTempAndReload();
            PageList.SelectedIndex = idx - 1;
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || PageList.SelectedIndex < 0 || PageList.SelectedIndex >= _doc.PageCount - 1) return;
            var doc = _doc;
            int idx = PageList.SelectedIndex;
            var page = doc.Pages[idx];
            doc.Pages.RemoveAt(idx);
            doc.Pages.Insert(idx + 1, page);
            SaveTempAndReload();
            PageList.SelectedIndex = idx + 1;
        }

        // Cancels the previous thumbnail background load when the file changes.
        // The FOCUSED pane's thumbnail loader token. The panes keep their own (PdfViewer.ThumbCts):
        // one window-wide token had each pane cancelling the other's decode.
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

            bool usable = cached != null
                       && _doc != null
                       && _currentFile != null
                       && cached.Length == _doc.PageCount
                       && string.Equals(ActiveViewer.ThumbCacheFile, _currentFile,
                                        System.StringComparison.OrdinalIgnoreCase);

            if (!usable) { RefreshPageList(); return; }

            // No cancel here. The panes own their thumbnail lists and their loader tokens
            // separately, so the other pane's decode is writing into ITS array and should be left
            // to finish - cancelling it was what left a pane showing page labels with no pictures
            // after any focus change.
            if (!ReferenceEquals(PageList.ItemsSource, cached)) PageList.ItemsSource = cached;
            ActiveViewer.SyncPageListSelection();
        }

        internal void RefreshPageList()
        {
            // Cancel any in-flight thumbnail load for the previous file.
            _thumbCts?.Cancel();
            _thumbCts = new System.Threading.CancellationTokenSource();
            var ct = _thumbCts.Token;

            if (_doc is null || _currentFile is null)
            {
                PageList.ItemsSource = null;
                return;
            }

            int    pageCount = _doc.PageCount;
            string filePath  = _currentFile;

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
            PageList.ItemsSource = items;

            // Hand the array to the pane it belongs to, so focusing away and back can re-seat it
            // rather than decode the document again. RestorePageListForActivePane is the only reader.
            ActiveViewer.ThumbCache     = items;
            ActiveViewer.ThumbCacheFile = filePath;

            // Load thumbnails sequentially on a background thread via a single doc reader.
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var docReader = DocLib.Instance.GetDocReader(filePath, new PageDimensions(128, 256));
                    for (int i = 0; i < pageCount; i++)
                    {
                        if (ct.IsCancellationRequested) return;
                        try
                        {
                            using var pr  = docReader.GetPageReader(i);
                            int tw  = pr.GetPageWidth();
                            int th  = pr.GetPageHeight();
                            var raw = pr.GetImage(Services.PdfRender.WithAnnotations);   // #141
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
                }
                catch { /* docReader open failed; all items remain label-only */ }
            }, ct);
        }
    }
}
