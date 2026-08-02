using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using KillerPDF.Services;

namespace KillerPDF
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
                Multiselect = true
            };
            if (dlg.ShowDialog(this) != true) return;

            // Open in its own tab using the same flow as New, so the import is treated as UNSAVED work:
            // dirty (orange Save icon), Save routes to Save As (no real file yet), and Close warns.
            var target = BeginTabLoad(out var prev, out bool createdNew);
            try
            {
                string tempPath = BuildPdfFromImages(dlg.FileNames);
                _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
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
            using var pdf = new PdfDocument();
            foreach (var path in imagePaths) PdfImport.AddImagePagesFromFile(pdf, path);

            if (pdf.PageCount == 0)
                throw new InvalidOperationException(
                    Application.Current.TryFindResource("Str_Err_NoImages") as string ?? "No images could be read.");

            string outPath = Path.Combine(Path.GetTempPath(),
                $"Imported-{DateTime.Now:yyyyMMdd-HHmmss-fff}.pdf");
            pdf.Save(outPath);
            return outPath;
        }

        // AddImagePagesFromFile and IsPdfPath live in Services/PdfImport.cs (KillerUI refactor).

        // ----- Drag/drop of folders, archives, and multiple files ----------------------------

        private static readonly string[] DropImageExt = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"];
        private static bool IsImagePath(string p)    => DropImageExt.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        private static bool IsOpenablePath(string p) => PdfImport.IsPdfPath(p) || IsImagePath(p);

        // Entry point for any file/folder/archive drop. Expands dropped folders (recursively) and .zip
        // archives, then opens the collected PDFs/images - asking merge-vs-separate when there's >1.
        private void OnPathsDropped(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;

            var found    = new List<string>();
            var tempDirs = new List<string>();   // extracted-zip temp dirs we may need to clean up
            bool expanded = false;               // a folder or archive was opened
            try
            {
                foreach (var p in paths)
                {
                    if (Directory.Exists(p)) { expanded = true; CollectOpenable(p, found); }
                    else if (p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        var dir = ExtractZipToTemp(p);
                        if (dir != null) { expanded = true; tempDirs.Add(dir); CollectOpenable(dir, found); }
                    }
                    else if (IsOpenablePath(p)) found.Add(p);
                }
            }
            catch (Exception ex)
            {
                CleanupDirs(tempDirs);
                KillerDialog.Show(this, Loc("Str_Err_ImportFailed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (found.Count == 0)
            {
                CleanupDirs(tempDirs);
                SetStatus(Loc("Str_Drop_NothingOpenable"));
                return;
            }

            // Guard against a folder/archive holding a huge number of files: opening or merging them all
            // would exhaust memory. Cap to a sane maximum, opening only the first N (name-sorted) after asking.
            const int MaxDropFiles = 50;
            if (found.Count > MaxDropFiles)
            {
                var proceed = KillerDialog.Show(this,
                    string.Format(Loc("Str_Drop_TooMany"), found.Count, MaxDropFiles),
                    "KillerPDF", MessageBoxButton.OKCancel);
                if (proceed != MessageBoxResult.OK) { CleanupDirs(tempDirs); return; }
                found = found.GetRange(0, MaxDropFiles);
            }

            // A single dropped file (no folder/zip): open it directly, as before. Temp from a single-file
            // zip is kept for the session since the opened doc references it.
            if (!expanded && found.Count == 1) { OpenDropped(found[0]); return; }

            int choice = KillerDialog.ShowChoices(this,
                string.Format(Loc("Str_Drop_Prompt"), found.Count),
                [Loc("Str_Drop_Merge"), Loc("Str_Drop_Separate"), Loc("Str_Stamp_Cancel")],
                accentIndex: 0);

            if (choice == 0)
            {
                OpenMerged(found, tempDirs);   // async: builds on a background thread, then owns temp cleanup
            }
            else if (choice == 1)
            {
                OpenSeparately(found);     // opened docs may reference extracted files - keep temp for the session
            }
            else
            {
                CleanupDirs(tempDirs);     // cancelled
            }
        }

        private void OpenDropped(string path)
        {
            if (PdfImport.IsPdfPath(path)) OpenInNewTab(path);
            else OpenImagesAsImportedTab([path], Path.GetFileName(path));
        }

        private void OpenSeparately(List<string> found)
        {
            if (found.Count > 30)
            {
                var ok = KillerDialog.Show(this, string.Format(Loc("Str_Drop_ManyTabs"), found.Count),
                    "KillerPDF", MessageBoxButton.OKCancel);
                if (ok != MessageBoxResult.OK) return;
            }
            foreach (var f in found)
            {
                if (PdfImport.IsPdfPath(f)) OpenInNewTab(f);
                else OpenImagesAsImportedTab([f], Path.GetFileName(f));
            }
        }

        private async void OpenMerged(List<string> found, List<string> tempDirs)
        {
            var target = BeginTabLoad(out var prev, out bool createdNew);
            var busy = ShowBusyOverlay(Loc("Str_Drop_Merging"));
            var ct = BeginCancellableOp("merge");   // Esc cancels; the busy overlay keeps the window draggable
            try
            {
                // Build off the UI thread so the window stays responsive (and movable) while it works.
                string? tempPath = await Task.Run(() => BuildCombinedPdf(found, ct));

                if (ct.IsCancellationRequested || tempPath == null)
                {
                    HideBusyOverlay(busy);
                    AbortTabLoad(target, prev, createdNew);
                    SetStatus(Loc("Str_Drop_MergeCancelled"));
                    return;
                }

                _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
                FinishOpenFile("Combined.pdf", tempPath);
                _originalFile = null;   // unsaved -> Save routes to Save As
                MarkDirty(true);
                SetStatus(string.Format(Loc("Str_Status_Merged"), found.Count));
                CaptureSessionState(_active!);
                SetTool(_currentTool);
                RebuildTabStrip();
                HideBusyOverlay(busy);
            }
            catch (Exception ex)
            {
                HideBusyOverlay(busy);
                AbortTabLoad(target, prev, createdNew);
                KillerDialog.Show(this, Loc("Str_Err_ImportFailed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EndCancellableOp();
                CleanupDirs(tempDirs);
            }
        }

        // Opens the given image(s) as a single unsaved imported-PDF tab (same flow as Import Images).
        private void OpenImagesAsImportedTab(string[] images, string displayName)
        {
            var target = BeginTabLoad(out var prev, out bool createdNew);
            try
            {
                string tempPath = BuildPdfFromImages(images);
                _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
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
        // Unreadable / encrypted entries are skipped rather than aborting the whole merge.
        private static string? BuildCombinedPdf(List<string> files, CancellationToken ct)
        {
            using var outPdf = new PdfDocument();
            foreach (var f in files)
            {
                if (ct.IsCancellationRequested) return null;
                if (PdfImport.IsPdfPath(f))
                {
                    try
                    {
                        using var src = PdfReader.Open(f, PdfDocumentOpenMode.Import);
                        for (int i = 0; i < src.PageCount; i++) outPdf.AddPage(src.Pages[i]);
                    }
                    catch { /* skip an unreadable/encrypted PDF */ }
                }
                else
                {
                    try { PdfImport.AddImagePagesFromFile(outPdf, f); } catch { /* skip an unreadable image */ }
                }
            }

            if (ct.IsCancellationRequested) return null;
            if (outPdf.PageCount == 0)
                throw new InvalidOperationException(
                    Application.Current.TryFindResource("Str_Err_NoImages") as string ?? "Nothing could be read.");

            string outPath = Path.Combine(Path.GetTempPath(),
                $"Combined-{DateTime.Now:yyyyMMdd-HHmmss-fff}.pdf");
            outPdf.Save(outPath);
            return outPath;
        }

        // Recursively gathers the PDFs and images under a folder, in a stable name order.
        private static void CollectOpenable(string dir, List<string> found)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories); }
            catch { return; }
            foreach (var f in files.Where(IsOpenablePath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                found.Add(f);
        }

        private static string? ExtractZipToTemp(string zipPath)
        {
            string dir = Path.Combine(Path.GetTempPath(), "KillerPDF-zip-" + Guid.NewGuid().ToString("N")[..8]);
            try { Directory.CreateDirectory(dir); ZipFile.ExtractToDirectory(zipPath, dir); return dir; }
            catch { try { Directory.Delete(dir, true); } catch { } return null; }
        }

        private static void CleanupDirs(List<string> dirs)
        {
            foreach (var d in dirs) try { Directory.Delete(d, true); } catch { }
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
                    return;   // save was cancelled or failed
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
                if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
                using (var zip = ZipFile.Open(dlg.FileName, ZipArchiveMode.Create))
                    zip.CreateEntryFromFile(sourcePdf, Path.GetFileName(sourcePdf), CompressionLevel.Optimal);

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

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
            if (bytes >= 1024)        return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} B";
        }
    }
}
