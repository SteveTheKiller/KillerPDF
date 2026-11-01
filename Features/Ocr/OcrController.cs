using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using KillerPdf.Engine.Authoring;
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
        // About 300 dpi on A4. A 600 dpi scan comes down to this, which OCR and the eye both read fine.
        private const int ScanTextRenderMax = 3600;

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
            var isSourceCurrent = _host.CaptureDocumentOrigin();
            if (!await _host.EnsureOcrModelsReadyAsync() || !isSourceCurrent()) return;

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
                        PdfRenderedPage page = renderSession.RenderBasePage(pages[i], ct);
                        int w = page.Width;
                        int h = page.Height;
                        ReadOnlyMemory<byte> bgra = page.Pixels;
                        int rot = rotations[i];
                        if (rot != 0)
                        {
                            PdfOcrBgraImage rotated = PdfOcrImagePreprocessor.RotateBgra(
                                bgra, w, h, rot, ct);
                            bgra = rotated.Pixels;
                            w = rotated.Width;
                            h = rotated.Height;
                        }
                        recognized.Add(!formAware
                            ? ocr.RecognizeBgra(bgra, w, h, cancellationToken: ct)
                            : PdfOcrFormRecognizer.Recognize(ocr.RecognizeBgra,
                                bgra, w, h, ReadFormHints(file, pages[i]), rot, ct));
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _host.HideBusy();
                _host.SetStatus(_host.Loc("Str_St_OcrCanceled"));
            }
            catch (Exception ex)
            {
                _host.HideBusy();
                KillerDialog.Show(_host.Window, _host.Loc("Str_Err_OcrFailed") + "\n" + ex.Message, "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _host.EndOp(ct);
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
            var isSourceCurrent = _host.CaptureDocumentOrigin();
            if (!await _host.EnsureOcrModelsReadyAsync() || !isSourceCurrent()) return;

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
                    PdfRenderedPage page = renderSession.RenderBasePage(pageIdx, ct);
                    int w = page.Width;
                    int h = page.Height;
                    ReadOnlyMemory<byte> bgra = page.Pixels;
                    if (rot != 0)
                    {
                        PdfOcrBgraImage rotated = PdfOcrImagePreprocessor.RotateBgra(
                            bgra, w, h, rot, ct);
                        bgra = rotated.Pixels;
                        w = rotated.Width;
                        h = rotated.Height;
                    }

                    double sx = (double)w / renderW, sy = (double)h / renderH;
                    PdfOcrBgraImage crop = PdfOcrImagePreprocessor.CropBgra(bgra, w, h,
                        (int)Math.Round(cb.Left * sx), (int)Math.Round(cb.Top * sy),
                        (int)Math.Round(cb.Width * sx), (int)Math.Round(cb.Height * sy));

                    using var ocr = new OcrService(language: lang);
                    return ocr.RecognizeBgra(
                        crop.Pixels, crop.Width, crop.Height, cancellationToken: ct);
                });

                _host.HideBusy();
                if (ct.IsCancellationRequested) { _host.SetStatus(_host.Loc("Str_St_OcrCanceled")); return; }

                string text = result.Text.Trim();
                if (text.Length == 0) { _host.SetStatus(_host.Loc("Str_St_OcrNoText")); return; }
                Clipboard.SetText(text);
                _host.SetStatus(string.Format(_host.Loc("Str_St_OcrCopiedRegion"), text.Length, result.MeanConfidence.ToString("P0")));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _host.HideBusy();
                _host.SetStatus(_host.Loc("Str_St_OcrCanceled"));
            }
            catch (Exception ex)
            {
                _host.HideBusy();
                KillerDialog.Show(_host.Window, _host.Loc("Str_Err_OcrFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _host.EndOp(ct);
            }
        }

        // ============================================================
        // Make Searchable PDF - OCR every page and write an invisible text
        // layer aligned to the image, so the existing engine search and text
        // selection start working on scans.
        // ============================================================

        internal async void MakeSearchablePdf()
        {
            if (!_host.HasDocument) { KillerDialog.Show(_host.Window, _host.Loc("Str_Ocr_NoDoc")); return; }
            if (!_host.EnsureDocumentEditable()) return;
            var isSourceCurrent = _host.CaptureDocumentOrigin();
            if (!await _host.EnsureOcrModelsReadyAsync() || !isSourceCurrent()) return;
            _host.CommitActiveTextBox();

            var dlg = new KillerPDF.Controls.FileDialog(KillerPDF.Controls.FileDialogMode.Save)
            {
                Filter = _host.Loc("Str_Filter_Pdf") + "|*.pdf",
                Title = _host.Loc("Str_Ocr_SaveSearchable"),
                FileName = SuggestSearchableName(),
                CheckFileExists = false,
                CheckPathExists = true
            };
            if (dlg.ShowDialog(_host.Window) != true || !isSourceCurrent()) return;
            string outPath = dlg.FileName;

            // Recognize the complete displayed document from an immutable copy, including pending
            // form values, visible markup and page rotations.
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _host.HideBusy();
                _host.SetStatus(_host.Loc("Str_St_SearchablePdfCanceled"));
            }
            catch (Exception ex)
            {
                _host.HideBusy();
                KillerDialog.Show(_host.Window, _host.Loc("Str_Err_SearchableFailed") + "\n" + ex.Message, "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _host.EndOp(ct);
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
            var layers = new List<PdfOcrPixelPage>(pages);
            for (int i = 0; i < pages; i++)
            {
                // Cooperative cancel: bail before the next page; the caller sees the canceled token and the
                // destination write happens after the loop, so no partial output is written.
                if (ct.IsCancellationRequested) return (i, 0);
                report(i, pages);

                PdfRenderedPage page = renderSession.RenderBasePage(i, ct);
                int w = page.Width;
                int h = page.Height;
                byte[] bgra = page.Pixels;
                if (bgra is null || bgra.Length == 0 || w <= 0 || h <= 0)
                {
                    layers.Add(new PdfOcrPixelPage(
                        Math.Max(1, w), Math.Max(1, h), []));
                    continue;
                }

                PdfOcrResult result = formAware
                    ? PdfOcrFormRecognizer.Recognize(ocr.RecognizeBgra,
                        bgra, w, h, formPages[i], cancellationToken: ct)
                    : ocr.RecognizeBgra(bgra, w, h, cancellationToken: ct);
                layers.Add(new PdfOcrPixelPage(w, h, result.Words));
            }
            if (ct.IsCancellationRequested) return (pages, 0);
            int totalWords = PdfEngineIntegration.AddSearchableTextLayers(
                src, outPath, layers);
            return (pages, totalWords);
        }

        // ============================================================
        // Convert Scan to Text - the recognized words of scanned pages become real text.
        // ============================================================

        internal async void ConvertScanToText()
        {
            if (!_host.HasDocument) { KillerDialog.Show(_host.Window, _host.Loc("Str_Ocr_NoDoc")); return; }
            if (!_host.EnsureDocumentEditable()) return;
            var isSourceCurrent = _host.CaptureDocumentOrigin();
            if (!await _host.EnsureOcrModelsReadyAsync() || !isSourceCurrent()) return;
            _host.CommitActiveTextBox();

            var dlg = new KillerPDF.Controls.FileDialog(KillerPDF.Controls.FileDialogMode.Save)
            {
                Filter = _host.Loc("Str_Filter_Pdf") + "|*.pdf",
                Title = _host.Loc("Str_Ocr_SaveScanText"),
                FileName = Path.GetFileNameWithoutExtension(_host.OriginalFile ?? _host.CurrentFile ?? "document") + "-text.pdf",
                CheckFileExists = false,
                CheckPathExists = true
            };
            if (dlg.ShowDialog(_host.Window) != true || !isSourceCurrent()) return;
            string outPath = dlg.FileName;

            string src = App.MakeTempFile("scantext");
            try { _host.SaveDocumentTo(src); }
            catch (Exception ex)
            {
                KillerDialog.Show(_host.Window, _host.Loc("Str_Err_PrepareDoc") + "\n" + ex.Message, "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var ct = _host.BeginOp(_host.Loc("Str_Op_Ocr"), _host.Loc("Str_Busy_ScanText"));
            void report(int i, int n) => _host.Window.Dispatcher.Invoke(() =>
                _host.SetBusyMessage(string.Format(_host.Loc("Str_Busy_ScanTextPage"), i + 1, n)));
            string lang = _host.OcrLanguageString;

            try
            {
                // Every converted word is shown beside its scan before anything is written.
                bool Review(IReadOnlyList<ScanTextReviewWord> words) => _host.Window.Dispatcher.Invoke(() =>
                    new KillerPDF.Controls.ScanTextReviewDialog(_host.Window, words).ShowDialog() == true);
                ScanTextSummary summary = await Task.Run(() => BuildScanTextPdf(src, outPath, report, lang, ct, review: Review));
                _host.HideBusy();
                if (ct.IsCancellationRequested || summary.Canceled) { _host.SetStatus(_host.Loc("Str_St_ScanTextCanceled")); return; }
                if (summary.ScannedPages == 0)
                {
                    KillerDialog.Show(_host.Window, _host.Loc("Str_Dlg_ScanTextNone"), "KillerPDF",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                _host.SetStatus(string.Format(_host.Loc("Str_St_ScanTextSaved"),
                    summary.ScannedPages, summary.ConvertedWords));
                KillerDialog.Show(_host.Window,
                    string.Format(_host.Loc("Str_Dlg_ScanTextSaved"), outPath,
                        summary.ScannedPages, summary.ConvertedWords, summary.KeptWords),
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _host.HideBusy();
                _host.SetStatus(_host.Loc("Str_St_ScanTextCanceled"));
            }
            catch (Exception ex)
            {
                _host.HideBusy();
                KillerDialog.Show(_host.Window, _host.Loc("Str_Err_ScanTextFailed") + "\n" + ex.Message, "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _host.EndOp(ct);
            }
        }

        internal sealed record ScanTextSummary(int ScannedPages, int ConvertedWords, int KeptWords, bool Canceled = false);

        // One word that is about to become text, as scanned and as it will be set, for review
        // before saving. Pixels are gray, one byte each, over the same box of the page.
        internal sealed class ScanTextReviewWord(string id, int pageIndex, PdfScanTextWord word,
            (int Left, int Top, int Right, int Bottom) box, byte[] scan)
        {
            // "page:word", both counted from 1, as --keep-scanned takes them.
            internal string Id { get; } = id;
            internal int PageIndex { get; } = pageIndex;
            internal PdfScanTextWord Word { get; } = word;
            internal (int Left, int Top, int Right, int Bottom) Box { get; } = box;
            internal int Width => Box.Right - Box.Left;
            internal int Height => Box.Bottom - Box.Top;
            internal byte[] Scan { get; } = scan;
            internal byte[] Typeset { get; set; } = [];
            internal bool UseText { get; set; } = true;
        }

        // A face scoring at least this against a page's ink fits it well enough that the page
        // need not be compared with every installed face.
        private const double ConfidentFaceScore = 0.78;

        // Turns the recognized words of every scanned page into real text, set in the installed
        // face that best matches the print and drawn over the page image with those words erased.
        // Words that do not match their ink closely stay in the image under invisible text. Pages
        // that already have visible text or drawings are left as they are. Runs off the UI
        // thread; also the core of the CLI's --scan-to-text command.
        // keepScanned names words ("page:word") to keep as scanned. With review, every page is
        // measured first, the words about to become text are handed over as scanned and as set,
        // and the pages are written once review returns true; false cancels with nothing written.
        internal static ScanTextSummary BuildScanTextPdf(string src, string outPath,
            Action<int, int> report, string language, CancellationToken ct,
            IReadOnlySet<string>? keepScanned = null, Func<IReadOnlyList<ScanTextReviewWord>, bool>? review = null)
        {
            IReadOnlyList<bool> scanned = PdfEngineIntegration.ReadScannedPages(src);
            if (!scanned.Contains(true)) return new ScanTextSummary(0, 0, 0);
            IReadOnlyList<bool> bitonal = PdfEngineIntegration.ReadBitonalPages(src);
            IReadOnlyList<(int Width, int Height)?> scanSizes = PdfEngineIntegration.ReadScanSizes(src);
            using var renderSession = PdfPageRenderSession.OpenEngineFirst(src, OcrRenderMax, OcrRenderMax);
            using var ocr = new OcrService(language: language);
            IReadOnlyList<PdfScanTextFace> faces = PdfEngineIntegration.ScanTextFaces();
            HashSet<string> bundled = [.. PdfScanTextWriter.BundledFaces().Select(item => item.Family)];
            var shortlist = new List<PdfScanTextFace>();
            var pages = new List<PdfScanTextPage>();
            var measured = new List<(int PageIndex, PdfScanTextPageAnalysis Analysis, PdfScanTextFace Face)>();
            var reviewWords = new List<ScanTextReviewWord>();
            for (int i = 0; i < scanned.Count; i++)
            {
                // Cooperative cancel: nothing is written until every page is done.
                if (ct.IsCancellationRequested) return new ScanTextSummary(pages.Count, 0, 0);
                report(i, scanned.Count);
                if (!scanned[i]) continue;

                PdfRenderedPage page = RenderScanPage(renderSession, i, scanSizes[i], ct);
                int w = page.Width, h = page.Height;
                byte[] bgra = page.Pixels;
                if (bgra is null || bgra.Length == 0 || w <= 0 || h <= 0) continue;

                PdfOcrResult result = ocr.RecognizeBgra(bgra, w, h, cancellationToken: ct);
                PdfScanTextPageAnalysis analysis = PdfScanTextAnalyzer.Analyze(bgra, w, h, result.Words);
                // The first page is compared with every face. Later pages choose among the faces
                // that came close on earlier pages and the bundled Times, Arial and Courier
                // equivalents, and are compared with every face again when none of those fits
                // well, since a document can change faces and its first page can be a poor guide.
                IReadOnlyList<(PdfScanTextFace Face, double Score)> ranked =
                    PdfScanTextWriter.RankFaces(analysis, shortlist.Count > 0 ? shortlist : faces, cancellationToken: ct);
                if (shortlist.Count > 0 && (ranked.Count == 0 || ranked[0].Score < ConfidentFaceScore))
                    ranked = PdfScanTextWriter.RankFaces(analysis, faces, cancellationToken: ct);
                foreach (string family in ranked.Take(4).Select(item => item.Face.Family).Concat(ranked.Count > 0 ? bundled : []))
                    if (!shortlist.Any(item => item.Family == family)) shortlist.Add(faces.First(item => item.Family == family));
                PdfScanTextFace face = ranked.Count > 0 ? ranked[0].Face : faces[0];
                PdfScanTextWriter.Verify(analysis, face, cancellationToken: ct);
                if (keepScanned is not null)
                    for (int k = 0; k < analysis.Words.Count; k++)
                        if (keepScanned.Contains($"{i + 1}:{k + 1}")) analysis.Words[k].KeepScanned();
                if (review is not null)
                {
                    reviewWords.AddRange(ReviewWords(bgra, w, h, i, analysis, face));
                    measured.Add((i, analysis, face));
                    continue;
                }
                pages.Add(EraseAndEncode(bgra, w, h, i, analysis, face, bitonal[i], scanSizes[i]));
            }
            if (ct.IsCancellationRequested || pages.Count == 0 && measured.Count == 0)
                return new ScanTextSummary(pages.Count, 0, 0);
            if (review is not null)
            {
                if (reviewWords.Count > 0)
                {
                    AddTypesetPreviews(src, measured, reviewWords, ct);
                    if (!review(reviewWords)) return new ScanTextSummary(measured.Count, 0, 0, Canceled: true);
                    foreach (ScanTextReviewWord word in reviewWords)
                        if (!word.UseText) word.Word.KeepScanned();
                }
                foreach (var (pageIndex, analysis, face) in measured)
                {
                    if (ct.IsCancellationRequested) return new ScanTextSummary(measured.Count, 0, 0);
                    report(pageIndex, scanned.Count);
                    PdfRenderedPage page = RenderScanPage(renderSession, pageIndex, scanSizes[pageIndex], ct);
                    pages.Add(EraseAndEncode(page.Pixels, page.Width, page.Height, pageIndex, analysis, face, bitonal[pageIndex], scanSizes[pageIndex]));
                }
            }
            PdfScanTextResult written = PdfEngineIntegration.WriteScanText(src, outPath, pages);
            return new ScanTextSummary(pages.Count, written.ReplacedWords, written.SearchableWords);
        }

        private static PdfRenderedPage RenderScanPage(PdfPageRenderSession session, int pageIndex,
            (int Width, int Height)? scan, CancellationToken ct) =>
            ScanPixels.RenderSize(scan, OcrRenderMax, ScanTextRenderMax) is { } size
                ? session.RenderBasePage(pageIndex, size.Width, size.Height, ct)
                : session.RenderBasePage(pageIndex, ct);

        private static PdfScanTextPage EraseAndEncode(byte[] bgra, int width, int height, int pageIndex,
            PdfScanTextPageAnalysis analysis, PdfScanTextFace face, bool bitonal, (int Width, int Height)? scan)
        {
            PdfScanTextAnalyzer.Erase(bgra, width, height, analysis.Words
                .Where(word => word.Replaceable && PdfScanTextWriter.CanSet(face, word)));
            PdfScanTextAnalyzer.ReleaseMasks(analysis);
            return new PdfScanTextPage(pageIndex, analysis, EncodeScanBackground(bgra, width, height, bitonal, scan), face);
        }

        // The words of one page that are about to become text, each cut from the scan with a
        // little paper around it.
        private static IEnumerable<ScanTextReviewWord> ReviewWords(byte[] bgra, int width, int height,
            int pageIndex, PdfScanTextPageAnalysis analysis, PdfScanTextFace face)
        {
            for (int k = 0; k < analysis.Words.Count; k++)
            {
                PdfScanTextWord word = analysis.Words[k];
                if (!word.Replaceable || !PdfScanTextWriter.CanSet(face, word)) continue;
                int pad = Math.Max(3, (int)Math.Round(word.XHeight * 0.4));
                var box = (Left: Math.Max(0, word.Left - pad), Top: Math.Max(0, word.Top - pad),
                    Right: Math.Min(width, word.Right + pad), Bottom: Math.Min(height, word.Bottom + pad));
                if (box.Right <= box.Left || box.Bottom <= box.Top) continue;
                yield return new ScanTextReviewWord($"{pageIndex + 1}:{k + 1}", pageIndex, word, box,
                    GrayCrop(bgra, width, box));
            }
        }

        // Sets the reviewed words on blank pages and cuts each one from that rendering, so the
        // review shows exactly the text the converted page will carry.
        private static void AddTypesetPreviews(string src,
            IReadOnlyList<(int PageIndex, PdfScanTextPageAnalysis Analysis, PdfScanTextFace Face)> measured,
            IReadOnlyList<ScanTextReviewWord> words, CancellationToken ct)
        {
            string preview = App.MakeTempFile("scantextreview");
            try
            {
                PdfImage paper = PdfImage.FromGray(1, 1, new byte[] { 255 });
                PdfEngineIntegration.WriteScanText(src, preview,
                    [.. measured.Select(item => new PdfScanTextPage(item.PageIndex, item.Analysis, paper, item.Face))]);
                using var session = PdfPageRenderSession.OpenEngineFirst(preview, OcrRenderMax, OcrRenderMax);
                foreach (var group in words.GroupBy(word => word.PageIndex))
                {
                    ct.ThrowIfCancellationRequested();
                    PdfRenderedPage page = session.RenderBasePage(group.Key, ct);
                    PdfScanTextPageAnalysis analysis = measured.First(item => item.PageIndex == group.Key).Analysis;
                    double sx = page.Width / (double)analysis.PixelWidth, sy = page.Height / (double)analysis.PixelHeight;
                    foreach (ScanTextReviewWord word in group)
                    {
                        var box = (Left: (int)Math.Round(word.Box.Left * sx), Top: (int)Math.Round(word.Box.Top * sy),
                            Right: (int)Math.Round(word.Box.Left * sx) + word.Width, Bottom: (int)Math.Round(word.Box.Top * sy) + word.Height);
                        word.Typeset = box.Right <= page.Width && box.Bottom <= page.Height
                            ? GrayCrop(page.Pixels, page.Width, box) : Enumerable.Repeat((byte)255, word.Width * word.Height).ToArray();
                    }
                }
            }
            finally
            {
                try { if (File.Exists(preview)) File.Delete(preview); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static byte[] GrayCrop(byte[] bgra, int width, (int Left, int Top, int Right, int Bottom) box)
        {
            int w = box.Right - box.Left, h = box.Bottom - box.Top;
            byte[] gray = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = ((box.Top + y) * width + box.Left + x) * 4;
                    gray[y * w + x] = (byte)((bgra[p + 2] * 299 + bgra[p + 1] * 587 + bgra[p] * 114 + 500) / 1000);
                }
            return gray;
        }

        // A black-and-white scan, one bit deep in the source or with next to no middle tones once
        // rendered, stays one bit deep, as JBIG2 when that is smallest; anything else is stored as
        // JPEG, in gray when the page has no color.
        private static PdfImage EncodeScanBackground(byte[] bgra, int width, int height, bool bitonal, (int Width, int Height)? scan)
        {
            long middle = 0;
            bool gray = true;
            for (int i = 0; i < bgra.Length; i += 4)
            {
                int blue = bgra[i], green = bgra[i + 1], red = bgra[i + 2];
                if (green is > 48 and < 208) middle++;
                if (gray && (Math.Abs(red - green) > 12 || Math.Abs(blue - green) > 12)) gray = false;
            }
            if (bitonal || middle * 100 < (long)width * height)
            {
                byte[] samples = new byte[width * height];
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = bgra[i * 4 + 1] < 128 ? (byte)0 : (byte)255;
                return PdfImage.FromBitonal(width, height, samples, allowJbig2: true);
            }
            // Words that stayed scanned are still read from this image, as are stamps and pictures,
            // so it keeps the scan's own resolution. With the rest of the text erased it is mostly
            // paper, which costs next to nothing, and pixels past the scan's would add only size.
            (int storedWidth, int storedHeight) = ScanPixels.StoredSize(width, height, scan);
            byte[] stored = storedWidth == width ? bgra : ScanPixels.Shrink(bgra, width, height, storedWidth, storedHeight);
            return PdfImage.FromJpeg(BitmapHelpers.EncodeJpeg(stored, storedWidth, storedHeight, quality: 85, grayscale: gray));
        }

        // ============================================================
        // Extract All Text - OCR every page and save the plain text to a .txt or .md file.
        // ============================================================

        internal async void ExtractAllText()
        {
            if (!_host.HasDocument) { KillerDialog.Show(_host.Window, _host.Loc("Str_Ocr_NoDoc")); return; }
            var isSourceCurrent = _host.CaptureDocumentOrigin();
            if (!await _host.EnsureOcrModelsReadyAsync() || !isSourceCurrent()) return;
            _host.CommitActiveTextBox();

            var dlg = new KillerPDF.Controls.FileDialog(KillerPDF.Controls.FileDialogMode.Save)
            {
                Filter = _host.Loc("Str_Filter_Text") + "|*.txt|Markdown|*.md",
                Title = _host.Loc("Str_Ocr_ExtractAllText"),
                FileName = Path.GetFileNameWithoutExtension(_host.OriginalFile ?? _host.CurrentFile ?? "document") + ".txt",
                CheckFileExists = false,
                CheckPathExists = true
            };
            if (dlg.ShowDialog(_host.Window) != true || !isSourceCurrent()) return;
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _host.HideBusy();
                _host.SetStatus(_host.Loc("Str_St_TextExtractCanceled"));
            }
            catch (Exception ex)
            {
                _host.HideBusy();
                KillerDialog.Show(_host.Window, _host.Loc("Str_Err_ExtractFailed") + "\n" + ex.Message, "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _host.EndOp(ct);
            }
        }

        // OCR each page and concatenate the text into one file. Markdown gets a "## Page N" heading per
        // page; plain text uses a simple divider. Cancellable - nothing is written if canceled.
        private static int ExtractText(string src, int pageCount, string outPath, bool markdown,
            Action<int, int> report, string language, bool formAware,
            CancellationToken ct)
        {
            var pageTexts = new List<string>(pageCount);
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

                PdfRenderedPage page = renderSession.RenderBasePage(i, ct);
                int w = page.Width;
                int h = page.Height;
                byte[] bgra = page.Pixels;
                string text = (bgra is null || bgra.Length == 0 || w <= 0 || h <= 0)
                    ? string.Empty
                    : (formAware
                        ? PdfOcrFormRecognizer.Recognize(ocr.RecognizeBgra,
                            bgra, w, h, formPages[i], cancellationToken: ct)
                        : ocr.RecognizeBgra(
                            bgra, w, h, cancellationToken: ct)).Text.TrimEnd();
                pageTexts.Add(text);
            }

            if (ct.IsCancellationRequested) return 0;
            File.WriteAllText(outPath, PdfOcrTextExporter.Format(pageTexts,
                markdown ? PdfOcrTextFormat.Markdown : PdfOcrTextFormat.PlainText));
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
