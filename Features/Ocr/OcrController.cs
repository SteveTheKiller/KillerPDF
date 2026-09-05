using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using KillerPdf.Engine.Documents;
using Microsoft.Win32;
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

        internal void OcrPageToClipboard(int pageIdx) => OcrPagesToClipboard([pageIdx]);

        // Rasterize the selected pages, recognize them in document order off the UI thread, and copy
        // their combined text. The same OCR engine and document reader are reused for the whole batch.
        internal async void OcrPagesToClipboard(IReadOnlyList<int> pageIndices)
        {
            if (!_host.HasDocument) { KillerDialog.Show(_host.Window, _host.Loc("Str_Msg_OpenFirst")); return; }
            int[] pages = [.. pageIndices.Distinct().Where(page => page >= 0 && page < _host.PageCount).OrderBy(page => page)];
            if (pages.Length == 0) return;
            if (!await _host.EnsureOcrModelsReadyAsync()) return;

            // Capture everything off the live UI state before going async.
            string file = _host.CurrentFile!;
            int[] rotations = [.. pages.Select(_host.RotationFor)];
            string lang = _host.OcrLanguageString;
            bool formAware = _host.FormAwareOcr;

            var ct = _host.BeginOp(_host.Loc("Str_Op_Ocr"), _host.Loc("Str_Busy_Ocr"));
            try
            {
                List<PdfOcrResult> results = await Task.Run(() =>
                {
                    using var renderSession = PdfPageRenderSession.OpenEngineFirst(
                        file, OcrRenderMax, OcrRenderMax);
                    using var ocr = new OcrService(language: lang);   // engine is not thread-safe: one per operation
                    var recognized = new List<PdfOcrResult>(pages.Length);
                    for (int i = 0; i < pages.Length; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        int progress = i;
                        _host.Window.Dispatcher.Invoke(() => _host.SetBusyMessage(
                            $"{_host.Loc("Str_Busy_Ocr")} {progress + 1}/{pages.Length}"));
                        PdfRenderedPage page = renderSession.RenderBasePage(pages[i]);
                        int w = page.Width;
                        int h = page.Height;
                        byte[] bgra = page.Pixels;
                        int rot = rotations[i];
                        if (rot != 0) (bgra, w, h) = BitmapHelpers.RotateBitmap(bgra, w, h, rot);
                        recognized.Add(!formAware
                            ? ocr.RecognizeBgra(bgra, w, h)
                            : FormAwareOcr.Recognize(ocr, bgra, w, h,
                                ReadFormHints(file, pages[i]), rot));
                    }
                    return recognized;
                });

                _host.HideBusy();
                // Cooperative cancel: a single page can't be interrupted mid-recognition, so we just discard
                // the result if the user canceled. No exceptions are thrown for cancellation anywhere.
                if (ct.IsCancellationRequested) { _host.SetStatus(_host.Loc("Str_St_OcrCanceled")); return; }

                string text = string.Join(Environment.NewLine + Environment.NewLine,
                    results.Select(result => result.Text.Trim()).Where(pageText => pageText.Length > 0));
                if (text.Length == 0)
                {
                    _host.SetStatus(pages.Length == 1
                        ? string.Format(_host.Loc("Str_St_OcrNoTextPage"), pages[0] + 1)
                        : _host.Loc("Str_St_OcrNoText"));
                    return;
                }

                Clipboard.SetText(text);
                double confidence = results.Count == 0 ? 0 : results.Average(result => result.MeanConfidence);
                _host.SetStatus(pages.Length == 1
                    ? string.Format(_host.Loc("Str_St_OcrCopiedPage"), text.Length, pages[0] + 1, confidence.ToString("P0"))
                    : string.Format(_host.Loc("Str_St_OcrCopiedPages"), text.Length, pages.Length, confidence.ToString("P0")));
            }
            catch (Exception ex)
            {
                _host.HideBusy();
                KillerDialog.Show(_host.Window, _host.Loc("Str_Err_OcrFailed") + "\n" + ex.Message, "KillerPDF",
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

            var ct = _host.BeginOp(_host.Loc("Str_Op_OcrRegion"), _host.Loc("Str_Busy_Region"));
            try
            {
                PdfOcrResult result = await Task.Run(() =>
                {
                    using var renderSession = PdfPageRenderSession.OpenEngineFirst(
                        file, OcrRenderMax, OcrRenderMax);
                    PdfRenderedPage page = renderSession.RenderBasePage(pageIdx);
                    int w = page.Width;
                    int h = page.Height;
                    byte[] bgra = page.Pixels;
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
                if (ct.IsCancellationRequested) { _host.SetStatus(_host.Loc("Str_St_OcrCanceled")); return; }

                string text = result.Text.Trim();
                if (text.Length == 0) { _host.SetStatus(_host.Loc("Str_St_OcrNoText")); return; }
                Clipboard.SetText(text);
                _host.SetStatus(string.Format(_host.Loc("Str_St_OcrCopiedRegion"), text.Length, result.MeanConfidence.ToString("P0")));
            }
            catch (Exception ex)
            {
                _host.HideBusy();
                KillerDialog.Show(_host.Window, _host.Loc("Str_Err_OcrFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
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
        // layer aligned to the image, so the existing engine search and text
        // selection start working on scans.
        // ============================================================

        internal async void MakeSearchablePdf()
        {
            if (!_host.HasDocument) { KillerDialog.Show(_host.Window, _host.Loc("Str_Ocr_NoDoc")); return; }
            if (!await _host.EnsureOcrModelsReadyAsync()) return;
            _host.CommitActiveTextBox();

            var dlg = new KillerPDF.Controls.FileDialog(KillerPDF.Controls.FileDialogMode.Save)
            {
                Filter = _host.Loc("Str_Filter_Pdf") + "|*.pdf",
                Title = _host.Loc("Str_Ocr_SaveSearchable"),
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
                KillerDialog.Show(_host.Window, _host.Loc("Str_Err_PrepareDoc") + "\n" + ex.Message, "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var ct = _host.BeginOp(_host.Loc("Str_Op_Ocr"), _host.Loc("Str_Busy_Searchable"));
            void report(int i, int n) => _host.Window.Dispatcher.Invoke(() =>
                _host.SetBusyMessage(string.Format(_host.Loc("Str_Busy_SearchablePage"), i + 1, n)));
            string lang = _host.OcrLanguageString;
            bool formAware = _host.FormAwareOcr;

            try
            {
                var (pages, words) = await Task.Run(() => BuildSearchablePdf(
                    src, outPath, report, lang, formAware, ct));
                _host.HideBusy();
                if (ct.IsCancellationRequested) { _host.SetStatus(_host.Loc("Str_St_SearchablePdfCanceled")); return; }
                _host.SetStatus(string.Format(_host.Loc("Str_St_SearchableSaved"), pages, words));
                KillerDialog.Show(_host.Window,
                    string.Format(_host.Loc("Str_Dlg_SearchableSaved"), outPath, pages, words),
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _host.HideBusy();
                KillerDialog.Show(_host.Window, _host.Loc("Str_Err_SearchableFailed") + "\n" + ex.Message, "KillerPDF",
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

        // Renders each page, OCRs it, and appends an invisible text layer positioned over the
        // recognized words. The text is real content-stream text, so engine extracts it for search/select;
        // invisible text rendering keeps it from showing or printing. Runs entirely off the UI thread. Also the core of the
        // CLI's --ocr command (CliRunner).
        internal static (int pages, int words) BuildSearchablePdf(string src, string outPath,
            Action<int, int> report, string language, bool formAware,
            CancellationToken ct)
        {
            using var renderSession = PdfPageRenderSession.OpenEngineFirst(
                src, OcrRenderMax, OcrRenderMax);
            using var ocr = new OcrService(language: language);   // one engine reused across the whole document (single-threaded here)
            int pages = PdfEngineIntegration.ReadPageInformation(src).Count;
            IReadOnlyList<IReadOnlyList<KillerPdf.Engine.Documents.PdfFormWidgetInfo>> formPages =
                formAware ? ReadAllFormHints(src, pages) :
                [
                    .. Enumerable.Range(0, pages).Select(_ =>
                        (IReadOnlyList<KillerPdf.Engine.Documents.PdfFormWidgetInfo>)
                        [])
                ];
            var layers = new List<PdfEngineIntegration.SearchablePage>(pages);
            for (int i = 0; i < pages; i++)
            {
                // Cooperative cancel: bail before the next page; the caller sees the canceled token and the
                // destination write happens after the loop, so no partial output is written.
                if (ct.IsCancellationRequested) return (i, 0);
                report(i, pages);

                PdfRenderedPage page = renderSession.RenderBasePage(i);
                int w = page.Width;
                int h = page.Height;
                byte[] bgra = page.Pixels;
                if (bgra is null || bgra.Length == 0 || w <= 0 || h <= 0)
                {
                    layers.Add(new PdfEngineIntegration.SearchablePage(
                        Math.Max(1, w), Math.Max(1, h), []));
                    continue;
                }

                PdfOcrResult result = formAware
                    ? FormAwareOcr.Recognize(ocr, bgra, w, h, formPages[i])
                    : ocr.RecognizeBgra(bgra, w, h);
                layers.Add(new PdfEngineIntegration.SearchablePage(w, h,
                    [.. result.Words.Select(word => new PdfEngineIntegration.SearchableWord(
                        word.Text, word.Left, word.Top, word.Right, word.Bottom))]));
            }
            if (ct.IsCancellationRequested) return (pages, 0);
            int totalWords = PdfEngineIntegration.AddSearchableTextLayers(
                src, outPath, layers);
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
                Filter = _host.Loc("Str_Filter_Text") + "|*.txt|Markdown|*.md",
                Title = _host.Loc("Str_Ocr_ExtractAllText"),
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
                KillerDialog.Show(_host.Window, _host.Loc("Str_Err_PrepareDoc") + "\n" + ex.Message, "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var ct = _host.BeginOp(_host.Loc("Str_Op_Ocr"), _host.Loc("Str_Busy_Extracting"));
            void report(int i, int n) => _host.Window.Dispatcher.Invoke(() =>
                _host.SetBusyMessage(string.Format(_host.Loc("Str_Busy_ExtractingPage"), i + 1, n)));
            string lang = _host.OcrLanguageString;
            bool formAware = _host.FormAwareOcr;

            try
            {
                int pages = await Task.Run(() => ExtractText(
                    src, pageCount, outPath, markdown, report, lang, formAware, ct));
                _host.HideBusy();
                if (ct.IsCancellationRequested) { _host.SetStatus(_host.Loc("Str_St_TextExtractCanceled")); return; }
                _host.SetStatus(string.Format(_host.Loc("Str_St_TextExtracted"), pages, Path.GetFileName(outPath)));
            }
            catch (Exception ex)
            {
                _host.HideBusy();
                KillerDialog.Show(_host.Window, _host.Loc("Str_Err_ExtractFailed") + "\n" + ex.Message, "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _host.EndOp();
            }
        }

        // OCR each page and concatenate the text into one file. Markdown gets a "## Page N" heading per
        // page; plain text uses a simple divider. Cancellable - nothing is written if canceled.
        private static int ExtractText(string src, int pageCount, string outPath, bool markdown,
            Action<int, int> report, string language, bool formAware,
            CancellationToken ct)
        {
            string nl = Environment.NewLine;
            var sb = new StringBuilder();
            using var renderSession = PdfPageRenderSession.OpenEngineFirst(
                src, OcrRenderMax, OcrRenderMax);
            using var ocr = new OcrService(language: language);
            IReadOnlyList<IReadOnlyList<KillerPdf.Engine.Documents.PdfFormWidgetInfo>> formPages =
                formAware ? ReadAllFormHints(src, pageCount) :
                [
                    .. Enumerable.Range(0, pageCount).Select(_ =>
                        (IReadOnlyList<KillerPdf.Engine.Documents.PdfFormWidgetInfo>)
                        [])
                ];

            for (int i = 0; i < pageCount; i++)
            {
                // Cooperative cancel: stop and write nothing if the user canceled (caller checks the token).
                if (ct.IsCancellationRequested) return 0;
                report(i, pageCount);

                PdfRenderedPage page = renderSession.RenderBasePage(i);
                int w = page.Width;
                int h = page.Height;
                byte[] bgra = page.Pixels;
                string text = (bgra is null || bgra.Length == 0 || w <= 0 || h <= 0)
                    ? string.Empty
                    : (formAware
                        ? FormAwareOcr.Recognize(ocr, bgra, w, h, formPages[i])
                        : ocr.RecognizeBgra(bgra, w, h)).Text.TrimEnd();
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

        private static IReadOnlyList<KillerPdf.Engine.Documents.PdfFormWidgetInfo> ReadFormHints(
            string path, int pageIndex)
        {
            try { return PdfEngineIntegration.ReadPageFormWidgets(path, pageIndex); }
            catch { return []; }
        }

        private static IReadOnlyList<IReadOnlyList<KillerPdf.Engine.Documents.PdfFormWidgetInfo>> ReadAllFormHints(
            string path, int pageCount)
        {
            try { return PdfEngineIntegration.ReadAllPageFormWidgets(path); }
            catch
            {
                return
                [
                    .. Enumerable.Range(0, pageCount).Select(_ =>
                        (IReadOnlyList<KillerPdf.Engine.Documents.PdfFormWidgetInfo>)
                        [])
                ];
            }
        }
    }
}
