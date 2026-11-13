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
using Microsoft.Win32;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    public partial class MainWindow
    {
        // A page's annotation space before a turn, for pages drawn since the last reload and for
        // pages that were not. Called while the reload remaps, before the new rotations apply.
        private (int Width, int Height) AnnotationSpaceBeforeTurn(int pageIndex)
        {
            var (visualWidth, visualHeight) = EnsureEngineDocumentSession().VisualPageSize(pageIndex, _pageRotations);
            return PdfEngineBurn.AnnotationSpace(_renderDims, pageIndex, visualWidth, visualHeight);
        }

        // page: the page a menu named; it turns that page, or the whole selection when the page is part of it.
        private void RotatePages_Click(int delta, int? page = null)
        {
            if (_doc is null) return;
            if (!EnsureEditable(continueAfterUnlock: false)) return;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0 && page is null) return;
            try
            {
                UndoEntry? documentUndo = CaptureDocumentUndo();
                var indices = new List<int>();
                if (page is { } clicked)
                    indices.AddRange(Services.PageMenuTarget.Pages(clicked, [.. selected.Cast<PageThumbnailVm>().Select(vm => vm.PageIndex)]));
                else
                    foreach (PageThumbnailVm vm in selected) indices.Add(vm.PageIndex);
                // #169: rotation must not destroy the overlay annotations - the reload's default
                // keepAnnotations:false cleared them all, losing committed unsaved work on the
                // second rotation after placing it. Remap each rotated page's annotations through
                // the turn (render dims are still the pre-turn frame here; the reload clears them)
                // and keep everything through the reload. A page not drawn since the last reload has
                // no cached render dims, so it uses the same canonical annotation space the burn uses.
                int restoreIdx = PageList.SelectedIndex;
                SaveTempAndReload(
                    keepAnnotations: true,
                    remapAnnotations: annotations =>
                    {
                        foreach (var idx in indices)
                            if (annotations.TryGetValue(idx, out var anns))
                            {
                                var (w, h) = AnnotationSpaceBeforeTurn(idx);
                                Services.AnnotationRotate.Remap(anns, delta, w, h);
                            }
                    },
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
            if (!EnsureMayWrite(PdfOutput.Printable)) return;
            var sourceCurrent = CaptureDocumentOrigin();
            int[]? ordered = PromptPageOperationRange("Str_Lbl_Extract", _doc.PageCount);
            if (ordered is null || !sourceCurrent()) return;
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Save)
                          { Filter = Loc("Str_Filter_Pdf") + "|*.pdf", Title = Loc("Str_Dlg_SaveExtractedAs"),
                            CheckFileExists = false, CheckPathExists = true, RequireFilterExtension = true };
            if (dlg.ShowDialog(this) != true || !sourceCurrent()) return;
            string? snapshot = null;
            try
            {
                CommitActiveTextBox();
                var rotations = SnapshotPageRotations();
                SavedCopy extractCopy = WriteSavedCopyToTemp(throwaway: true);
                snapshot = extractCopy.Path;
                PdfEngineIntegration.ExtractPages(
                    snapshot, dlg.FileName, ordered, rotations);
                SetStatus(extractCopy.Note(
                    string.Format(Loc("Str_Extracted"), ordered.Length, System.IO.Path.GetFileName(dlg.FileName)),
                    Loc("Str_Save_PicturesSkipped")));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_SplitFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (snapshot is not null)
                    try { System.IO.File.Delete(snapshot); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e) => DeletePages(null);

        /// <summary>Delete Pages, prefilled with one page (a page's own menu) or else the sidebar selection.</summary>
        private void DeletePages(int? page)
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            if (!EnsureEditable(continueAfterUnlock: false)) return;
            var sourceCurrent = CaptureDocumentOrigin();
            string initial = page is int one && one >= 0 && one < _doc.PageCount
                ? (one + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : InitialPageRange(_doc.PageCount);
            // One step: the dialog shows which pages go and says so on its button, so no Yes/No box follows it.
            var dialog = new DeletePagesDialog(this, _doc.PageCount, initial,
                page => page < PageList.Items.Count ? PageList.Items[page] as PageThumbnailVm : null, GestureFor("Ctrl:Z"));
            if (dialog.ShowDialog() != true || !sourceCurrent()) return;
            int[] selected = [.. dialog.Pages];
            // The dialog refuses an empty range and one that leaves no page; the document can change behind it.
            if (selected.Length == 0 || selected.Length >= _doc.PageCount) return;
            try
            {
                var indices = selected.ToList();
                var removed = indices.ToHashSet();
                int originalPage = Math.Clamp(_currentPage, 0, _doc.PageCount - 1);
                int survivingPage = originalPage;
                while (survivingPage < _doc.PageCount && removed.Contains(survivingPage)) survivingPage++;
                if (survivingPage >= _doc.PageCount)
                {
                    survivingPage = originalPage - 1;
                    while (survivingPage >= 0 && removed.Contains(survivingPage)) survivingPage--;
                }
                int selectedAfterRemoval = Math.Max(0, survivingPage - indices.Count(index => index < survivingPage));
                UndoEntry? documentUndo = CaptureDocumentUndo();
                var annotationBackup = _annotations.ToDictionary(
                    pair => pair.Key, pair => pair.Value);
                try
                {
                    PageAnnotationInsertion.RemovePages(_annotations, indices);
                    SaveTempAndReload(
                        keepAnnotations: true,
                        finalizeSavedFile: path => PdfEngineIntegration.RemovePages(path, indices),
                        selectedPageAfterReload: selectedAfterRemoval,
                        remapRedactionMarks: true,
                        remapRotations: rotations =>
                            PdfEngineIntegration.RemapRotationsAfterPageRemoval(rotations, indices),
                        documentUndo: documentUndo);
                }
                catch
                {
                    _annotations.Clear();
                    foreach (var pair in annotationBackup)
                    {
                        foreach (PageAnnotation annotation in pair.Value)
                            annotation.PageIndex = pair.Key;
                        _annotations[pair.Key] = pair.Value;
                    }
                    throw;
                }
                SetStatus(string.Format(Loc("Str_Deleted"), indices.Count, _doc?.PageCount));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_DeleteFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // The pages a page operation starts from: the sidebar selection, else the page in view.
        private string InitialPageRange(int pageCount)
        {
            string initial = string.Join(",", PageList.SelectedItems.Cast<PageThumbnailVm>()
                .Select(page => page.PageIndex + 1).Distinct().Order());
            return initial.Length > 0 ? initial
                : (Math.Clamp(_currentPage, 0, Math.Max(0, pageCount - 1)) + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);
        }

        private int[]? PromptPageOperationRange(string titleKey, int pageCount)
        {
            string initial = InitialPageRange(pageCount);
            var dialog = new DocumentValuesDialog(this, Loc(titleKey), Loc("Str_Btn_OK"),
                (string.Format(Loc("Str_Dlg_PageRange"), pageCount), initial));
            if (dialog.ShowDialog() != true) return null;
            List<int>? pages = Features.CliRunner.CliParsePageRange(dialog.Values[0], pageCount, out _);
            if (pages is null)
            {
                KillerDialog.Show(this, Loc("Str_InvalidRange"), "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            return [.. pages];
        }

        private void InsertBlankPage_Click(object sender, RoutedEventArgs e) => InsertBlankPage(null);

        /// <summary>Inserts a blank page after the given page (a page card), or else after the sidebar selection.</summary>
        private void InsertBlankPage(int? after)
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            if (!EnsureEditable(continueAfterUnlock: false)) return;
            int insertAfter = after is int page && page >= 0 && page < _doc.PageCount ? page
                : PageList.SelectedIndex >= 0 ? PageList.SelectedIndex : _doc.PageCount - 1;
            int insertIndex = insertAfter + 1;
            var (pageWidth, pageHeight) = EnsureEngineDocumentSession()
                .VisualPageSize(Math.Max(0, insertAfter), _pageRotations);
            var sizeDialog = new BlankPageDialog(this, pageWidth, pageHeight);
            if (sizeDialog.ShowDialog() != true) return;
            try
            {
                UndoEntry? documentUndo = CaptureDocumentUndo();
                var annotationBackup = _annotations.ToDictionary(
                    pair => pair.Key, pair => pair.Value);
                try
                {
                    PageAnnotationInsertion.Shift(_annotations, insertIndex, 1);
                    SaveTempAndReload(
                        keepAnnotations: true,
                        finalizeSavedFile: path =>
                            PdfEngineIntegration.InsertBlankPage(path, insertIndex,
                                sizeDialog.WidthPoints, sizeDialog.HeightPoints),
                        remapRedactionMarks: true,
                        remapRotations: rotations =>
                            PdfEngineIntegration.RemapRotationsAfterPageInsertion(
                                rotations, insertIndex),
                        documentUndo: documentUndo);
                }
                catch
                {
                    _annotations.Clear();
                    foreach (var pair in annotationBackup)
                    {
                        foreach (PageAnnotation annotation in pair.Value)
                            annotation.PageIndex = pair.Key;
                        _annotations[pair.Key] = pair.Value;
                    }
                    throw;
                }
                PageList.SelectedIndex = insertIndex;
                SetStatus(string.Format(Loc("Str_St_InsertedBlank"), insertAfter + 2));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_InsertFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Appends a blank page to the END of the document. Used by the page-agnostic context menu
        // (sidebar empty area / outside the page), where there's no specific page to insert relative to.
        private void AddBlankPageAtEnd()
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            if (!EnsureEditable()) return;
            int referencePage = Math.Clamp(PageList.SelectedIndex >= 0
                ? PageList.SelectedIndex : _currentPage, 0, _doc.PageCount - 1);
            var (pageWidth, pageHeight) = EnsureEngineDocumentSession()
                .VisualPageSize(referencePage, _pageRotations);
            var sizeDialog = new BlankPageDialog(this, pageWidth, pageHeight);
            if (sizeDialog.ShowDialog() != true) return;
            try
            {
                int insertIndex = _doc.PageCount;
                SaveTempAndReload(
                    keepAnnotations: true,
                    finalizeSavedFile: path =>
                        PdfEngineIntegration.InsertBlankPage(path, insertIndex,
                            sizeDialog.WidthPoints, sizeDialog.HeightPoints),
                    remapRedactionMarks: true,
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
            => MoveSelectedPage(-1);

        private void MoveDown_Click(object sender, RoutedEventArgs e)
            => MoveSelectedPage(1);

        private void MoveSelectedPage(int delta)
        {
            if (_doc is null || PageList.SelectedIndex < 0) return;
            int idx = PageList.SelectedIndex;
            int target = idx + delta;
            if (target < 0 || target >= _doc.PageCount) return;
            if (!EnsureEditable(continueAfterUnlock: false)) return;
            try
            {
                IReadOnlyList<int> order = PdfEngineIntegration.PageOrderAfterMove(
                    _doc.PageCount, [idx], delta > 0 ? target + 1 : target);
                SaveTempAndReload(
                    keepAnnotations: true,
                    finalizeSavedFile: path => PdfEngineIntegration.MovePage(path, idx, target),
                    remapAnnotations: annotations => PageAnnotationInsertion.Reorder(annotations, order),
                    remapRedactionMarks: true,
                    remapRotations: rotations =>
                        PdfEngineIntegration.RemapRotationsAfterPageMove(rotations, idx, target),
                    selectedPageAfterReload: target);
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_Pages_Failed"), ex.Message),
                    Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Page operations that renumber pages without changing their content keep the unsaved
        // overlay annotations. The undo entry is captured before the remap so Undo returns the
        // annotations to their original pages, and a failed rewrite puts them back.
        private void ReloadWithRemappedAnnotations(
            Action<Dictionary<int, List<PageAnnotation>>> remapAnnotations,
            Action<UndoEntry?> reload)
        {
            if (_doc is null || _currentFile is null) return;
            UndoEntry? documentUndo = ActiveViewer.CaptureSerializedDocumentUndoExt(_currentFile);
            var annotationBackup = _annotations.ToDictionary(
                pair => pair.Key, pair => pair.Value);
            try
            {
                remapAnnotations(_annotations);
                reload(documentUndo);
            }
            catch
            {
                _annotations.Clear();
                foreach (var pair in annotationBackup)
                {
                    foreach (PageAnnotation annotation in pair.Value)
                        annotation.PageIndex = pair.Key;
                    _annotations[pair.Key] = pair.Value;
                }
                throw;
            }
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
            var thumbnailOwner = ActiveViewer;
            var proof = thumbnailOwner.DocumentProofView?.CaptureProof();
            var cached = thumbnailOwner.ThumbCache;
            int preservedPage = thumbnailOwner.CurrentPageIndex;

            // A book has no PdfDocument; its pages are counted from the book, and a book still being
            // paginated is still growing, so its list is rebuilt.
            var nativeBook = thumbnailOwner.NativeDocumentRef;
            int? pageCount = nativeBook is null ? _doc?.PageCount
                : nativeBook.IsPaginating ? null : nativeBook.ShownPageCount;
            bool usable = cached != null
                       && pageCount != null
                       && _currentFile != null
                       && thumbnailOwner.ThumbCacheComplete
                       && cached.Length == pageCount
                       && cached.All(item => item.ProofStamp == proof?.Stamp
                           && item.WorkingCmyk == KillerPDF.Services.WorkingCmykProfiles.Generation)
                       && string.Equals(thumbnailOwner.ThumbCacheFile, _currentFile,
                                        System.StringComparison.OrdinalIgnoreCase);

            if (!usable) { RefreshPageList(); return; }

            // No cancel here. The panes own their thumbnail lists and their loader tokens
            // separately, so the other pane's decode is writing into ITS array and should be left
            // to finish - canceling it was what left a pane showing page labels with no pictures
            // after any focus change.
            _sidebarPages.Show(cached);
            ApplyPageThumbnailLayout();
            ActiveViewer.SyncPageListSelection(preservedPage);
        }

        internal void RefreshPageList()
        {
            var thumbnailOwner = ActiveViewer;
            var thumbnailProof = thumbnailOwner.DocumentProofView?.CaptureProof();
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

            var nativeRevision = thumbnailOwner.NativeDocumentRef?.CaptureReadRevision();
            if ((_doc is null && nativeRevision is null) || _currentFile is null)
            {
                _sidebarPages.Show(null);
                return;
            }

            // A book still being paginated lists the pages the views show; more are appended as they are taken in.
            var nativeBook = thumbnailOwner.NativeDocumentRef;
            int    pageCount = nativeBook?.ShownPageCount ?? EnsureEngineDocumentSession().Pages.Count;
            string filePath  = _currentFile;
            OpenedProtection protection = _protection;
            int preservedPage = ActiveViewer.CurrentPageIndex;

            // Snapshot rotations on the UI thread before going to background.
            var rotSnap = new Dictionary<int, int>(_pageRotations);

            // Carry forward any existing thumbnails so the list never flashes blank
            // during reload (e.g. after a rotation).  New thumbnails replace them as
            // the background loader finishes each page.
            // They come from this tab's own list. The list on screen can belong to the tab just left, whose pages are
            // another document's. The file path is not compared: a page edit reloads from a fresh temporary path.
            var oldItems = thumbnailOwner.ThumbCache;

            var items = new PageThumbnailVm[pageCount];
            for (int i = 0; i < pageCount; i++)
            {
                rotSnap.TryGetValue(i, out int rot);
                items[i] = nativeRevision is not null ? new PageThumbnailVm(i, nativeRevision, rot, thumbnailProof)
                    : new PageThumbnailVm(i, filePath, protection, rot, thumbnailProof);
            }
            // Seed with stale thumbnail - better than blank while reloading
            if (nativeRevision is null)
                PageThumbnailVm.CarryPictures(oldItems, items, thumbnailProof?.Stamp, KillerPDF.Services.WorkingCmykProfiles.Generation);
            // Draw thumbnails only for the rows the list shows, newest request first, and keep a bounded number:
            // drawing and holding one for every page took about 805 MiB at 10,000 pages and 8 GiB at 100,000.
            // The rows ask through their Thumbnail binding, which the list reads only for rows it realizes.
            var renderer = new ThumbnailSessionOwner<PdfPageRenderSession>();
            var window = new ThumbnailWindow<System.Windows.Media.Imaging.BitmapSource>(pageCount, (i, token) =>
            {
                if (ct.IsCancellationRequested) return null;
                rotSnap.TryGetValue(i, out int rot);
                if (nativeRevision is not null)
                    return PageThumbnailVm.BuildNativeThumb(nativeRevision, i, rot, 128, 256, ct, thumbnailProof?.Settings);
                return renderer.Use(() => PdfPageRenderSession.OpenEngineFirst(filePath, 128, 256, protection), renderSession =>
                {
                    PdfRenderedPage page = renderSession.RenderPreview(i, ct, thumbnailProof?.Settings);
                    int tw = page.Width;
                    int th = page.Height;
                    byte[] raw = page.Pixels;
                    if (tw <= 0 || th <= 0 || raw == null || raw.Length < tw * th * 4) return null;
                    if (rot != 0)
                        (raw, tw, th) = BitmapHelpers.RotateBitmap(raw, tw, th, rot);
                    return PageThumbnailVm.BuildThumbFromRaw(raw, tw, th);
                }, ct);
            }, (i, image) =>
            {
                if (ct.IsCancellationRequested) return;
                if (image is null) items[i].ReleaseThumbnail();
                else items[i].SetThumbnail(image);
            }, drained: () =>
            {
                // The renderer holds decoded page images; a scanned PDF kept them while the tab stayed open.
                renderer.Release();
            });
            ct.Register(() =>
            {
                window.Dispose();
                renderer.Release();
                // Rows not drawn yet would ask a cancelled window, so a tab that comes back rebuilds its list
                // (carrying the pictures already drawn) instead of re-seating these rows.
                thumbnailOwner.MarkThumbnailCacheIncomplete(items);
            });
            foreach (var item in items) item.RequestThumbnail = window.Request;
            _sidebarPages.Show(items, provisional: nativeBook?.IsPaginating == true);
            ApplyPageThumbnailLayout();
            ActiveViewer.SyncPageListSelection(preservedPage);

            // Hand the array to the pane it belongs to, so focusing away and back can re-seat it
            // rather than decode the document again. RestorePageListForActivePane is the only reader.
            ActiveViewer.ThumbCache     = items;
            ActiveViewer.ThumbCacheFile = filePath;
            ActiveViewer.ThumbCacheComplete = false;

            // Every row draws itself when shown, so the list is complete as it stands and a pane that is focused
            // again reuses it.
            thumbnailOwner.MarkThumbnailCacheComplete(items);
        }
    }
}
