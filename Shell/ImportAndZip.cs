using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    // Import images as a PDF, and compress the current PDF to a .zip. Kept in its own partial-class
    // file rather than the MainWindow monolith. User-facing strings go through Loc() (keys live in
    // Strings/*.xaml) so the feature is fully localized.
    public partial class MainWindow
    {
        // ----- Import images as a single PDF -------------------------------------------------

        private void ImportImages_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Open)
            {
                Title       = Loc("Str_Menu_Import"),
                Filter      = $"{Loc("Str_Filter_Images")}|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|{Loc("Str_Filter_AllFiles")}|*.*",
                Multiselect = true,
                ShowImagePreview = true
            };
            if (dlg.ShowDialog(this) != true) return;

            // Open in its own tab using the same flow as New, so the import is treated as UNSAVED work:
            // dirty (orange Save icon), Save routes to Save As (no real file yet), and Close warns.
            var target = BeginTabLoad(out var prev, out bool createdNew);
            try
            {
                string tempPath = BuildPdfFromImages(dlg.FileNames);
                _doc = PdfWorkingDocument.Open(tempPath, null);
                FinishOpenFile("Imported.pdf", tempPath);
                _originalFile = null;   // no saved location yet -> Save becomes Save As
                MarkDirty(true);        // unsaved -> orange icon + close warns
                SetStatus(string.Format(Loc("Str_Status_Imported"), dlg.FileNames.Length));
                CaptureSessionState(_active!);
                SetTool(_currentTool);
                RebuildTabStrip();
            }
            catch (Exception ex)
            {
                AbortTabLoad(target, prev, createdNew);
                KillerDialog.Show(this, Loc("Str_Err_ImportFailed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Builds a PDF where each page is exactly one source image. Multi-frame TIFF/GIF expand to one
        // page per frame. Page size matches the image's physical size (pixels at its own DPI, 96 if the
        // image declares none). Returns a temp PDF path; the caller opens it and the user can Save As.
        private static string BuildPdfFromImages(string[] imagePaths)
        {
            string outPath = App.MakeTempFile("imported");
            AtomicFileOutput.Write(outPath, PdfEngineIntegration.MergeFiles(imagePaths));
            return outPath;
        }

        // IsPdfPath lives in Services/PdfImport.cs (KillerUI refactor).

        // ----- Drag/drop of folders, archives, and multiple files ----------------------------

        private static bool IsOpenablePath(string path) => DropImport.SupportsFile(path);

        // Entry point for any file/folder/archive drop. Expands dropped folders (recursively) and .zip
        // archives, then opens the collected PDFs/images - asking merge-vs-separate when there's >1.
        private async void OnPathsDropped(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            if (_busyCts is not null) { SetStatus(Loc("Str_Drop_Busy")); return; }
            var owner = AsyncOwner();
            var ct = BeginCancellableOp(Loc("Str_Menu_Import"));
            var busy = ShowBusyOverlay(Loc("Str_Menu_Import"));
            DropImportBatch? batch = null;
            try
            {
                // Render the cue and process pending input before discovery or extraction starts.
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                string directory = App.MakeImportDirectory();
                batch = await Task.Run(() => DropImport.Prepare(paths, directory, cancellation: ct), ct);
            }
            catch (OperationCanceledException) { SetStatus(Loc("Str_St_Canceled")); }
            catch (DropImportException ex)
            {
                string message = ex.Failure is DropImportFailure.Link or DropImportFailure.UnsafePath
                    ? Loc("Str_Drop_Unsafe")
                    : string.Format(Loc("Str_Drop_Limits"), 10_000, 64, 1, 2);
                KillerDialog.Show(this, message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_ImportFailed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { HideBusyOverlay(busy); EndCancellableOp(ct); }
            if (batch is null) return;
            bool handedToMerge = false;
            try
            {
                if (!IsLoaded || ct.IsCancellationRequested) return;
                if (owner.Session is not null)
                {
                    if (!ReclaimAsyncOwner(owner)) return;
                }
                else if (!ReferenceEquals(ActiveViewer, owner.Viewer) || owner.Viewer.ActiveSessionExt is not null) return;
                var found = batch.Files.ToList();
                if (found.Count == 0) { SetStatus(Loc("Str_Drop_NothingOpenable")); return; }
                const int MaxDropFiles = 50;
                if (found.Count > MaxDropFiles)
                {
                    if (KillerDialog.Show(this, string.Format(Loc("Str_Drop_TooMany"), found.Count, MaxDropFiles),
                        "KillerPDF", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                    found = found.GetRange(0, MaxDropFiles);
                }
                if (!batch.Expanded && found.Count == 1) { OpenDropped(found[0].Path); return; }
                // Several dropped PDFs open the Merge files dialog (#495) directly: put them in order and merge, open
                // them separately, or cancel. A drop with pictures in it keeps the plain question, because the dialog
                // lists only PDFs.
                if (found.All(file => PdfImport.IsPdfPath(file.Path)))
                {
                    var dlg = new MergeFilesDialog(this, found.Select(file => file.Path), offerOpenSeparately: true,
                        offerPages: true);
                    if (dlg.ShowDialog() != true || dlg.Paths.Count == 0) return;
                    var kept = DropImport.InDialogOrder(dlg.Paths, found);
                    if (dlg.OpenSeparately) { await OpenSeparately(kept, batch); return; }
                    handedToMerge = true;
                    OpenMerged([.. kept.Select(file => file.Path)], batch, pages: dlg.PageOrder);
                    return;
                }
                int choice = KillerDialog.ShowChoices(this,
                    string.Format(Loc("Str_Drop_Prompt"), found.Count),
                    [Loc("Str_Drop_Merge"), Loc("Str_Drop_Separate"), Loc("Str_Stamp_Cancel")], accentIndex: 0);
                if (choice == 0)
                {
                    handedToMerge = true;
                    OpenMerged(found.Select(file => file.Path).ToList(), batch);
                }
                else if (choice == 1) await OpenSeparately(found, batch);
            }
            finally { if (!handedToMerge) batch.Dispose(); }
        }

        private void OpenDropped(string path)
        {
            if (PdfImport.IsPdfPath(path)) OpenInNewTab(path);
            else OpenImagesAsImportedTab([path], Path.GetFileName(path));
        }

        private async Task OpenSeparately(List<DropImportFile> found, DropImportBatch batch)
        {
            if (found.Count > 30)
            {
                var ok = KillerDialog.Show(this, string.Format(Loc("Str_Drop_ManyTabs"), found.Count),
                    "KillerPDF", MessageBoxButton.OKCancel);
                if (ok != MessageBoxResult.OK) return;
            }
            foreach (var file in found)
            {
                if (_windowResourcesReleased) return;
                if (PdfImport.IsPdfPath(file.Path))
                {
                    if (file.Temporary) App.RememberSuggestedName(file.Path, file.SuggestedName);
                    var opened = ActiveViewer.OpenInNewTabExt(file.Path);
                    // The batch holds its inputs until this exact open and any repair handoff
                    // finish. Only an adopted document retains its extracted input afterward.
                    bool adopted = opened.Completion is not null && await opened.Completion;
                    if (_windowResourcesReleased) return;
                    if (file.Temporary)
                    {
                        if (adopted) batch.KeepFiles([file.Path]);
                        else batch.DiscardFile(file.Path);
                    }
                }
                else OpenImagesAsImportedTab([file.Path], file.SuggestedName);
            }
        }

        private async void OpenMerged(List<string> found, DropImportBatch batch, int skippedPictures = 0,
            IReadOnlyList<MergePage>? pages = null)
        {
            var target = BeginTabLoad(out var prev, out bool createdNew);
            target.LoadPending = true;   // a second open while this builds must not land in this tab
            var owner = AsyncOwner();
            var busy = ShowBusyOverlay(Loc("Str_Drop_Merging"));
            var ct = BeginCancellableOp(Loc("Str_Op_Merge"));   // Esc cancels; the busy overlay keeps the window draggable
            string? completedPath = null;
            bool adopted = false;
            try
            {
                // Build off the UI thread so the window stays responsive (and movable) while it works.
                (string? tempPath, int leftOut) = await Task.Run(() => BuildCombinedPdf(found, ct, pages));
                completedPath = tempPath;
                // A merge that finishes while the cancel prompt is open honors the answer.
                if (_busyCancelPrompt is { } prompt) await prompt;
                target.LoadPending = false;
                if (!ReclaimAsyncOwner(owner))
                {
                    // Its tab was closed meanwhile.
                    HideBusyOverlay(busy);
                    if (tempPath != null) try { File.Delete(tempPath); } catch (IOException) { }
                    return;
                }
                // The tab to fall back to may have been closed while this ran.
                if (prev != null && !ActiveViewer.SessionsRef.Contains(prev))
                    prev = ActiveViewer.SessionsRef.FirstOrDefault(session => !ReferenceEquals(session, target));

                if (ct.IsCancellationRequested || tempPath == null)
                {
                    HideBusyOverlay(busy);
                    AbortTabLoad(target, prev, createdNew);
                    SetStatus(Loc("Str_Drop_MergeCanceled"));
                    return;
                }

                _doc = PdfWorkingDocument.Open(tempPath, null);
                FinishOpenFile("Combined.pdf", tempPath);
                adopted = true;
                _originalFile = null;   // unsaved -> Save routes to Save As
                MarkDirty(true);
                string merged = string.Format(Loc("Str_Status_Merged"), found.Count - leftOut);
                if (leftOut > 0) merged += " " + string.Format(Loc("Str_Drop_SkippedFiles"), leftOut);
                SetStatus(Services.PicturesSkippedNote.Append(merged, skippedPictures, Loc("Str_Save_PicturesSkipped")));
                CaptureSessionState(_active!);
                SetTool(_currentTool);
                RebuildTabStrip();
                HideBusyOverlay(busy);
            }
            catch (OperationCanceledException)
            {
                HideBusyOverlay(busy);
                if (!ReclaimAsyncOwner(owner)) return;
                if (prev != null && !owner.Viewer.SessionsRef.Contains(prev))
                    prev = owner.Viewer.SessionsRef.FirstOrDefault(session => !ReferenceEquals(session, target));
                AbortTabLoad(target, prev, createdNew);
                SetStatus(Loc("Str_Drop_MergeCanceled"));
            }
            catch (Exception ex)
            {
                HideBusyOverlay(busy);
                if (!ReclaimAsyncOwner(owner)) return;
                if (prev != null && !owner.Viewer.SessionsRef.Contains(prev))
                    prev = owner.Viewer.SessionsRef.FirstOrDefault(session => !ReferenceEquals(session, target));
                AbortTabLoad(target, prev, createdNew);
                KillerDialog.Show(this, Loc("Str_Err_ImportFailed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                target.LoadPending = false;
                EndCancellableOp(ct);
                if (!adopted && completedPath is not null) try { File.Delete(completedPath); } catch (IOException) { }
                batch.Dispose();
            }
        }

        // Opens the given image(s) as a single unsaved imported-PDF tab (same flow as Import Images).
        private void OpenImagesAsImportedTab(string[] images, string displayName)
        {
            var target = BeginTabLoad(out var prev, out bool createdNew);
            try
            {
                string tempPath = BuildPdfFromImages(images);
                _doc = PdfWorkingDocument.Open(tempPath, null);
                FinishOpenFile(displayName, tempPath);
                _originalFile = null;
                MarkDirty(true);
                CaptureSessionState(_active!);
                SetTool(_currentTool);
                RebuildTabStrip();
            }
            catch (Exception ex)
            {
                AbortTabLoad(target, prev, createdNew);
                KillerDialog.Show(this, Loc("Str_Err_ImportFailed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Builds one PDF from a mix of PDFs (pages imported in order) and images (one page each).
        // Unreadable / encrypted entries are skipped rather than aborting the whole merge, and how many
        // were left out comes back with the file so the status can say so.
        // A page order from the Merge dialog's page view (#495) is laid over what the merge actually took, so a file
        // it left out moves nobody else's pages.
        private static (string? Path, int LeftOut) BuildCombinedPdf(List<string> files, CancellationToken ct,
            IReadOnlyList<MergePage>? pages = null)
        {
            ct.ThrowIfCancellationRequested();
            byte[] bytes = PdfEngineIntegration.MergeReadableFiles(files, out IReadOnlyList<string> leftOut,
                out var merged, ct);
            ct.ThrowIfCancellationRequested();
            string outPath = App.MakeTempFile("combined");
            AtomicFileOutput.Write(outPath, bytes, ct);
            if (pages is not null)
            {
                try
                {
                    var plan = MergePageArrangement.Plan(pages, merged)
                        ?? throw new InvalidOperationException(Loc("Str_MergeFiles_NoPagesLeft"));
                    ct.ThrowIfCancellationRequested();
                    MergePageArrangement.Apply(outPath, plan);
                }
                catch
                {
                    try { File.Delete(outPath); } catch (IOException) { }
                    throw;
                }
            }
            return (outPath, leftOut.Count);
        }

        // ----- Compress the current PDF to a .zip --------------------------------------------

        private void CompressToZip_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }

            // The zip wraps the PDF on disk, so make sure what's on screen is saved first.
            if (_isDirty || string.IsNullOrEmpty(_originalFile) || !File.Exists(_originalFile))
            {
                var ask = KillerDialog.Show(this, Loc("Str_Dlg_SaveBeforeZip"),
                    "KillerPDF", MessageBoxButton.OKCancel);
                if (ask != MessageBoxResult.OK) return;
                SaveInPlace();
                if (_isDirty || string.IsNullOrEmpty(_originalFile) || !File.Exists(_originalFile))
                    return;   // save was canceled or failed
            }

            string sourcePdf = _originalFile!;
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Save)
            {
                Filter   = $"{Loc("Str_Filter_Zip")}|*.zip",
                Title    = Loc("Str_Menu_CompressZip"),
                FileName = Path.GetFileNameWithoutExtension(sourcePdf) + ".zip"
            };
            var srcDir = Path.GetDirectoryName(sourcePdf);
            if (!string.IsNullOrEmpty(srcDir) && Directory.Exists(srcDir)) dlg.InitialDirectory = srcDir;
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                PdfZipExport.Write(sourcePdf, dlg.FileName);

                long before = new FileInfo(sourcePdf).Length;
                long after  = new FileInfo(dlg.FileName).Length;
                SetStatus(string.Format(Loc("Str_Status_Zipped"),
                    Path.GetFileName(dlg.FileName), FormatSize(after), FormatSize(before)));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_ZipFailed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string FormatSize(long bytes) => Services.ByteSizeText.Format(bytes);
    }
}
