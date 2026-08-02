using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using KillerPDF.Services;

namespace KillerPDF.Features
{
    /// <summary>
    /// The four OCR operations that work on the open document: page to clipboard, region to
    /// clipboard, searchable PDF, and extract-all-text. Moved out of Ocr.cs (MainWindow) in the
    /// KillerUI refactor.
    ///
    /// Holds no controls. Talks to the window only through <see cref="IOcrHost"/>, so the whole
    /// of this file is testable against a stub host. The pure language/download helpers live in
    /// Services/OcrLanguages.cs; the OCR menu, region arming, and model downloads stay in the
    /// shell half (Shell/Ocr.cs).
    /// </summary>
    internal sealed class OcrController
    {
        // Longest-side pixel budget for the OCR render. ~300 DPI on a Letter page, which is the sweet
        // spot for Tesseract: high enough for small body text, not so high it wastes time/memory.
        private const int OcrRenderMax = 2600;

        private readonly IOcrHost _host;

        internal OcrController(IOcrHost host) => _host = host;

        // Right-click "OCR Page" action: rasterize the page, recognize text off the UI thread, and drop
        // the result on the clipboard. Render + OCR are both slow, so they run inside Task.Run behind the
        // busy overlay; everything touching the clipboard/UI happens back on the UI thread.
        internal async void OcrPageToClipboard(int pageIdx)
        {
            if (!_host.HasDocument) { KillerDialog.Show(_host.Window, _host.Loc("Str_Msg_OpenFirst")); return; }
            if (pageIdx < 0 || pageIdx >= _host.PageCount) return;
            if (!await _host.EnsureOcrModelsReadyAsync()) return;

            // Capture everything off the live UI state before going async.
            string file = _host.CurrentFile!;
            int rot = _host.RotationFor(pageIdx);
            string lang = _host.OcrLanguageString;

            var ct = _host.BeginOp("OCR operation", "Running OCR...");
            try
            {
                OcrResult result = await Task.Run(() =>
                {
                    using var docReader = DocLib.Instance.GetDocReader(file, new PageDimensions(OcrRenderMax, OcrRenderMax));
                    using var pageReader = docReader.GetPageReader(pageIdx);

                    int w = pageReader.GetPageWidth();
                    int h = pageReader.GetPageHeight();
                    byte[] bgra = pageReader.GetImage();

                    // Temp file has /Rotate stripped, so rotate the pixel buffer to the page's visual orientation.
                    if (rot != 0) (bgra, w, h) = BitmapHelpers.RotateBitmap(bgra, w, h, rot);

                    using var ocr = new OcrService(language: lang);   // engine is not thread-safe: one per operation
                    return ocr.RecognizeBgra(bgra, w, h);
                });

                _host.HideBusy();
                // Cooperative cancel: a single page can't be interrupted mid-recognition, so we just discard
                // the result if the user cancelled. No exceptions are thrown for cancellation anywhere.
                if (ct.IsCancellationRequested) { _host.SetStatus(_host.Loc("Str_St_OcrCancelled")); return; }

                string text = result.Text.Trim();
                if (text.Length == 0)
                {
                    _host.SetStatus($"OCR: no text found on page {pageIdx + 1}");
                    return;
                }

                Clipboard.SetText(text);
                _host.SetStatus($"OCR: copied {text.Length} chars from page {pageIdx + 1} ({result.MeanConfidence:P0} confidence)");
            }
            catch (Exception ex)
            {
                _host.HideBusy();
                KillerDialog.Show(_host.Window, $"OCR failed:\n{ex.Message}", "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _host.EndOp();
            }
        }

        // OCR Region: armed by the shell's menu item (Select-tool box-drag); Annotations' drag handler
        // lands here with the page index and the canvas-space rect. Works on scans with no text layer.
        internal async void OcrRegion(int pageIdx, Rect canvasBounds)
        {
            if (!_host.HasDocument) return;
            if (pageIdx < 0 || pageIdx >= _host.PageCount) return;
            if (!_host.TryGetRenderDims(pageIdx, out int renderW, out int renderH) || renderW <= 0 || renderH <= 0) return;
            if (canvasBounds.Width < 4 || canvasBounds.Height < 4) { _host.SetStatus(_host.Loc("Str_St_OcrRegionTooSmall")); return; }
            if (!await _host.EnsureOcrModelsReadyAsync()) return;

            string file = _host.CurrentFile!;
            int rot = _host.RotationFor(pageIdx);
            string lang = _host.OcrLanguageString;
            Rect cb = canvasBounds;

            var ct = _host.BeginOp("OCR region", "Recognizing region...");
            try
            {
                OcrResult result = await Task.Run(() =>
                {
                    using var docReader = DocLib.Instance.GetDocReader(file, new PageDimensions(OcrRenderMax, OcrRenderMax));
                    using var pageReader = docReader.GetPageReader(pageIdx);
                    int w = pageReader.GetPageWidth();
                    int h = pageReader.GetPageHeight();
                    byte[] bgra = pageReader.GetImage();
                    if (rot != 0) (bgra, w, h) = BitmapHelpers.RotateBitmap(bgra, w, h, rot);

                    double sx = (double)w / renderW, sy = (double)h / renderH;
                    byte[] crop = CropBgra(bgra, w, h,
                        (int)Math.Round(cb.Left * sx), (int)Math.Round(cb.Top * sy),
                        (int)Math.Round(cb.Width * sx), (int)Math.Round(cb.Height * sy),
                        out int cw, out int chh);

                    using var ocr = new OcrService(language: lang);
                    return ocr.RecognizeBgra(crop, cw, chh);
                });

                _host.HideBusy();
                if (ct.IsCancellationRequested) { _host.SetStatus(_host.Loc("Str_St_OcrCancelled")); return; }

                string text = result.Text.Trim();
                if (text.Length == 0) { _host.SetStatus(_host.Loc("Str_St_OcrNoText")); return; }
                Clipboard.SetText(text);
                _host.SetStatus($"OCR: copied {text.Length} chars from the region ({result.MeanConfidence:P0} confidence)");
            }
            catch (Exception ex)
            {
                _host.HideBusy();
                KillerDialog.Show(_host.Window, $"OCR failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _host.EndOp();
            }
        }

        private static byte[] CropBgra(byte[] src, int srcW, int srcH, int x, int y, int cw, int ch, out int outW, out int outH)
        {
            x = Math.Max(0, Math.Min(x, srcW - 1));
            y = Math.Max(0, Math.Min(y, srcH - 1));
            outW = Math.Max(1, Math.Min(cw, srcW - x));
            outH = Math.Max(1, Math.Min(ch, srcH - y));
            var dst = new byte[outW * outH * 4];
            for (int row = 0; row < outH; row++)
                Array.Copy(src, ((y + row) * srcW + x) * 4, dst, row * outW * 4, outW * 4);
            return dst;
        }

        // ============================================================
        // Make Searchable PDF - OCR every page and write an invisible text
        // layer aligned to the image, so the existing PdfPig search and text
        // selection start working on scans.
        // ============================================================

        internal async void MakeSearchablePdf()
        {
            if (!_host.HasDocument) { KillerDialog.Show(_host.Window, _host.Loc("Str_Ocr_NoDoc")); return; }
            if (!await _host.EnsureOcrModelsReadyAsync()) return;
            _host.CommitActiveTextBox();

            var dlg = new KillerPDF.Controls.FileDialog(KillerPDF.Controls.FileDialogMode.Save)
            {
                Filter = "PDF files|*.pdf",
                Title = "Save Searchable PDF",
                FileName = SuggestSearchableName(),
                CheckFileExists = false,
                CheckPathExists = true
            };
            if (dlg.ShowDialog(_host.Window) != true) return;
            string outPath = dlg.FileName;

            // Snapshot the current document to a temp; we render and re-open from this so the live doc
            // is never touched. (Unburned overlay annotations are not included in v1.)
            string src = App.MakeTempFile("ocrsrc");
            try { _host.SaveDocumentTo(src); }
            catch (Exception ex)
            {
                KillerDialog.Show(_host.Window, $"Could not prepare the document:\n{ex.Message}", "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var ct = _host.BeginOp("OCR operation", "Making searchable PDF...");
            void report(int i, int n) => _host.Window.Dispatcher.Invoke(() =>
                _host.SetBusyMessage($"Making searchable PDF... page {i + 1} of {n}  (Esc to cancel)"));
            string lang = _host.OcrLanguageString;

            try
            {
                var (pages, words) = await Task.Run(() => BuildSearchablePdf(src, outPath, report, ct, lang));
                _host.HideBusy();
                if (ct.IsCancellationRequested) { _host.SetStatus(_host.Loc("Str_St_SearchablePdfCancelled")); return; }
                _host.SetStatus($"Searchable PDF saved: {pages} pages, {words} words recognized");
                KillerDialog.Show(_host.Window,
                    $"Saved searchable PDF:\n{outPath}\n\n{pages} pages processed, {words} words recognized.",
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _host.HideBusy();
                KillerDialog.Show(_host.Window, $"Searchable PDF failed:\n{ex.Message}", "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _host.EndOp();
            }
        }

        // Suggest "<original name>-searchable.pdf" for the save dialog.
        private string SuggestSearchableName()
        {
            string baseName = Path.GetFileNameWithoutExtension(_host.OriginalFile ?? _host.CurrentFile ?? "document");
            return baseName + "-searchable.pdf";
        }

        // Renders each page, OCRs it, and appends an invisible (alpha 0) text layer positioned over the
        // recognized words. The text is real content-stream text, so PdfPig extracts it for search/select;
        // alpha 0 keeps it from showing or printing. Runs entirely off the UI thread. Also the core of the
        // CLI's --ocr command (CliRunner).
        internal static (int pages, int words) BuildSearchablePdf(string src, string outPath, Action<int, int> report, CancellationToken ct, string language)
        {
            // Cache one XFont per integer point size so a page of words doesn't allocate thousands of fonts.
            var fontCache = new Dictionary<int, XFont>();
            XFont FontFor(double heightPt)
            {
                int key = Math.Max(4, (int)Math.Round(heightPt));
                if (!fontCache.TryGetValue(key, out var f))
                {
                    try { f = new XFont("Arial", key, XFontStyle.Regular); }
                    catch { f = new XFont("Segoe UI", key, XFontStyle.Regular); }
                    fontCache[key] = f;
                }
                return f;
            }

            int totalWords = 0;
            var invisible = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));

            using var docReader = DocLib.Instance.GetDocReader(src, new PageDimensions(OcrRenderMax, OcrRenderMax));
            using var ocr = new OcrService(language: language);   // one engine reused across the whole document (single-threaded here)

            var outDoc = PdfReader.Open(src, PdfDocumentOpenMode.Modify);
            int pages = outDoc.PageCount;
            for (int i = 0; i < pages; i++)
            {
                // Cooperative cancel: bail before the next page; the caller sees the cancelled token and the
                // file is never saved (outDoc.Save is past the loop), so no partial output is written.
                if (ct.IsCancellationRequested) return (i, totalWords);
                report(i, pages);

                using var pr = docReader.GetPageReader(i);
                int w = pr.GetPageWidth();
                int h = pr.GetPageHeight();
                byte[] bgra = pr.GetImage();
                if (bgra is null || bgra.Length == 0 || w <= 0 || h <= 0) continue;

                OcrResult result = ocr.RecognizeBgra(bgra, w, h);
                if (result.Words.Count == 0) continue;

                var page = outDoc.Pages[i];
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

                // OCR boxes are top-left pixel space; XGraphics is top-left point space. Same convention,
                // so mapping is a straight scale (mirrors DrawAnnotationsOnDocument).
                double sx = page.Width.Point / w;
                double sy = page.Height.Point / h;

                foreach (var word in result.Words)
                {
                    double bx = word.Left * sx;
                    double by = word.Top * sy;
                    double bh = Math.Max(1, (word.Bottom - word.Top) * sy);
                    try
                    {
                        // (bx, by) is the top-left of the text by default (Near/Near alignment).
                        gfx.DrawString(word.Text, FontFor(bh), invisible, bx, by);
                        totalWords++;
                    }
                    catch { /* a single word that won't lay out should not abort the page */ }
                }
            }

            outDoc.Save(outPath);
            outDoc.Close();
            return (pages, totalWords);
        }

        // ============================================================
        // Extract All Text - OCR every page and save the plain text to a .txt or .md file.
        // ============================================================

        internal async void ExtractAllText()
        {
            if (!_host.HasDocument) { KillerDialog.Show(_host.Window, _host.Loc("Str_Ocr_NoDoc")); return; }
            if (!await _host.EnsureOcrModelsReadyAsync()) return;
            _host.CommitActiveTextBox();

            var dlg = new KillerPDF.Controls.FileDialog(KillerPDF.Controls.FileDialogMode.Save)
            {
                Filter = "Text file|*.txt|Markdown|*.md",
                Title = "Extract All Text",
                FileName = Path.GetFileNameWithoutExtension(_host.OriginalFile ?? _host.CurrentFile ?? "document") + ".txt",
                CheckFileExists = false,
                CheckPathExists = true
            };
            if (dlg.ShowDialog(_host.Window) != true) return;
            string outPath = dlg.FileName;
            bool markdown = Path.GetExtension(outPath).Equals(".md", StringComparison.OrdinalIgnoreCase);

            string src = App.MakeTempFile("ocrtxt");
            int pageCount;
            try { _host.SaveDocumentTo(src); pageCount = _host.PageCount; }
            catch (Exception ex)
            {
                KillerDialog.Show(_host.Window, $"Could not prepare the document:\n{ex.Message}", "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var ct = _host.BeginOp("OCR operation", "Extracting text...");
            void report(int i, int n) => _host.Window.Dispatcher.Invoke(() =>
                _host.SetBusyMessage($"Extracting text... page {i + 1} of {n}  (Esc to cancel)"));
            string lang = _host.OcrLanguageString;

            try
            {
                int pages = await Task.Run(() => ExtractText(src, pageCount, outPath, markdown, report, ct, lang));
                _host.HideBusy();
                if (ct.IsCancellationRequested) { _host.SetStatus(_host.Loc("Str_St_TextExtractCancelled")); return; }
                _host.SetStatus($"Text extracted from {pages} pages -> {Path.GetFileName(outPath)}");
            }
            catch (Exception ex)
            {
                _host.HideBusy();
                KillerDialog.Show(_host.Window, $"Text extraction failed:\n{ex.Message}", "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _host.EndOp();
            }
        }

        // OCR each page and concatenate the text into one file. Markdown gets a "## Page N" heading per
        // page; plain text uses a simple divider. Cancellable - nothing is written if cancelled.
        private static int ExtractText(string src, int pageCount, string outPath, bool markdown,
            Action<int, int> report, CancellationToken ct, string language)
        {
            string nl = Environment.NewLine;
            var sb = new StringBuilder();
            using var docReader = DocLib.Instance.GetDocReader(src, new PageDimensions(OcrRenderMax, OcrRenderMax));
            using var ocr = new OcrService(language: language);

            for (int i = 0; i < pageCount; i++)
            {
                // Cooperative cancel: stop and write nothing if the user cancelled (caller checks the token).
                if (ct.IsCancellationRequested) return 0;
                report(i, pageCount);

                using var pr = docReader.GetPageReader(i);
                int w = pr.GetPageWidth();
                int h = pr.GetPageHeight();
                byte[] bgra = pr.GetImage();
                string text = (bgra is null || bgra.Length == 0 || w <= 0 || h <= 0)
                    ? string.Empty
                    : ocr.RecognizeBgra(bgra, w, h).Text.TrimEnd();
                // Normalize Tesseract's LF line breaks to the platform's so .txt opens cleanly everywhere.
                text = text.Replace("\r\n", "\n").Replace("\n", nl);

                if (markdown)
                    sb.Append("## Page ").Append(i + 1).Append(nl).Append(nl).Append(text).Append(nl).Append(nl);
                else
                    sb.Append("----- Page ").Append(i + 1).Append(" -----").Append(nl).Append(text).Append(nl).Append(nl);
            }

            if (ct.IsCancellationRequested) return 0;
            File.WriteAllText(outPath, sb.ToString());
            return pageCount;
        }
    }
}
