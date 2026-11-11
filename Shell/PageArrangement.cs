using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    // One-click page arrangement (#436): reverse the page order, turn every page upright, and
    // straighten tilted scans. Each acts on the selected pages when two or more are selected,
    // otherwise on the whole document, and each is a single undo step.
    public partial class MainWindow
    {
        private int[] PagesForDocumentCommand()
        {
            int[] selected = [.. PageList.SelectedItems.Cast<PageThumbnailVm>()
                .Select(page => page.PageIndex).Distinct().OrderBy(index => index)];
            return selected.Length >= 2 ? selected : [.. Enumerable.Range(0, PageList.Items.Count)];
        }

        // ---- Reverse Page Order ----------------------------------------------------------------

        private void ReversePageOrder()
        {
            if (_doc is null) { SetStatus(Loc("Str_Msg_OpenFirst")); return; }
            if (!EnsureEditable(continueAfterUnlock: false)) return;
            CommitActiveTextBox();
            int[] pages = PagesForDocumentCommand();
            if (pages.Length < 2) return;
            try
            {
                IReadOnlyList<int> order = PdfEngineIntegration.PageOrderAfterReverse(PageList.Items.Count, pages);
                int selected = Math.Max(0, PageList.SelectedIndex);
                ReloadWithRemappedAnnotations(
                    annotations => PageAnnotationInsertion.Reorder(annotations, order),
                    documentUndo => SaveTempAndReload(
                        keepAnnotations: true,
                        finalizeSavedFile: path => PdfEngineIntegration.ReorderPages(path, order),
                        remapRedactionMarks: true,
                        remapRotations: rotations => PdfEngineIntegration.RemapRotationsAfterReorder(rotations, order),
                        selectedPageAfterReload: Math.Max(0, order.ToList().IndexOf(selected)),
                        documentUndo: documentUndo));
                SetStatus(string.Format(Loc("Str_Pages_Reversed"), pages.Length));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_Pages_Failed"), ex.Message), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---- Fix Page Orientation ------------------------------------------------------------

        // Pages with text are judged by the direction their text runs. Scans without a text layer
        // are read with OCR in the two directions their lines allow, and the reading that makes
        // sense wins. The fix is a page rotation, so nothing on the page is redrawn.
        private async void FixPageOrientation()
        {
            if (_doc is null || _currentFile is null) { SetStatus(Loc("Str_Msg_OpenFirst")); return; }
            if (!EnsureEditable(continueAfterUnlock: false)) return;
            CommitActiveTextBox();
            int[] pages = PagesForDocumentCommand();
            string label = Loc("Str_Pages_FixOrientation");
            var sourceViewer = ActiveViewer;
            var sourceSession = sourceViewer.ActiveSessionExt;
            var sourceDocument = _doc;
            bool IsSourceCurrent() => ReferenceEquals(ActiveViewer, sourceViewer)
                && ReferenceEquals(sourceViewer.ActiveSessionExt, sourceSession)
                && ReferenceEquals(_doc, sourceDocument);
            var turns = new Dictionary<int, int>();
            int unread = 0;
            Border? busy = null;
            CancellationToken ct = default;
            try
            {
                ct = BeginCancellableOp(label);
                busy = ShowBusyOverlay(label);
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (!IsSourceCurrent() || ct.IsCancellationRequested) return;

                string file = _currentFile;
                Dictionary<int, int?> fromText = await Task.Run(
                    () => PageArrangementService.UprightFromText(file, pages, ct), ct);
                if (!IsSourceCurrent() || ct.IsCancellationRequested) return;
                var scans = new List<int>();
                foreach (int page in pages)
                {
                    if (!fromText.TryGetValue(page, out int? upright)) { scans.Add(page); continue; }
                    if (upright is not int target) { unread++; continue; }
                    int turn = PageGeometry.TurnBetween(CurrentPageRotation(page), target);
                    if (turn != 0) turns[page] = turn;
                }

                if (scans.Count > 0)
                {
                    // The model download prompt must not sit under the busy overlay.
                    HideBusyOverlay(busy);
                    busy = null;
                    bool modelsReady = await EnsureOcrModelsReadyAsync();
                    if (!IsSourceCurrent() || ct.IsCancellationRequested) return;
                    if (!modelsReady)
                        unread += scans.Count;
                    else
                    {
                        busy = ShowBusyOverlay(label);
                        using var ocr = new OcrService(language: CurrentOcrLanguageString());
                        for (int i = 0; i < scans.Count; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            SetBusyMessage(busy, $"{label}: {i + 1}/{scans.Count}");
                            await Dispatcher.Yield(DispatcherPriority.Background);
                            if (!IsSourceCurrent() || ct.IsCancellationRequested) return;
                            var bitmap = RenderPageBitmap(scans[i], 1600);
                            if (bitmap is null) { unread++; continue; }
                            int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
                            var bgra = new byte[width * height * 4];
                            bitmap.CopyPixels(bgra, width * 4, 0);
                            int? turn = await Task.Run(
                                () => PageArrangementService.ScanUprightTurn(ocr, bgra, width, height, ct), ct);
                            if (!IsSourceCurrent() || ct.IsCancellationRequested) return;
                            if (turn is null) unread++;
                            else if (turn != 0) turns[scans[i]] = turn.Value;
                        }
                    }
                }

                string skipped = unread > 0 ? " " + string.Format(Loc("Str_Pages_Unread"), unread) : "";
                if (turns.Count == 0)
                {
                    SetStatus(Loc("Str_Pages_OrientNone") + skipped);
                    return;
                }

                UndoEntry? documentUndo = CaptureDocumentUndo();
                int restore = PageList.SelectedIndex;
                // The turn is applied to the reload's copy of the annotations, which is published only
                // after the rotated file reopens, so a failed write leaves pending annotations untouched.
                SaveTempAndReload(
                    keepAnnotations: true,
                    remapAnnotations: annotations =>
                    {
                        foreach (var (page, turn) in turns)
                            if (annotations.TryGetValue(page, out var pageAnnotations))
                            {
                                var (w, h) = AnnotationSpaceBeforeTurn(page);
                                AnnotationRotate.Remap(pageAnnotations, turn, w, h);
                            }
                    },
                    remapRotations: rotations =>
                    {
                        foreach (var group in turns.GroupBy(pair => pair.Value))
                            PdfEngineIntegration.RemapRotationsAfterPageTurns(
                                rotations, [.. group.Select(pair => pair.Key)], group.Key);
                    },
                    documentUndo: documentUndo);
                PageList.SelectedIndex = Math.Min(restore, PageList.Items.Count - 1);
                FitToPage();
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
                {
                    if (ReferenceEquals(ActiveViewer, sourceViewer)
                        && ReferenceEquals(sourceViewer.ActiveSessionExt, sourceSession)) FitToPage();
                }));
                SetStatus(string.Format(Loc("Str_Pages_Oriented"), turns.Count) + skipped);
            }
            catch (OperationCanceledException)
            {
                SetStatus(Loc("Str_St_Canceled"));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_Pages_Failed"), ex.Message), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (busy is not null) HideBusyOverlay(busy);
                EndCancellableOp(ct);
            }
        }

        // The rotation the page shows now. Native /Rotate stays on the pages of a freshly opened
        // document until SaveTempAndReload captures it, so a page missing from the map is read from
        // the file rather than as 0: the text's upright rotation is absolute, and a page that
        // already reads upright must not be turned again.
        private int CurrentPageRotation(int page)
        {
            if (_pageRotations.TryGetValue(page, out int rotation)) return rotation;
            PdfEngineDocumentSession session = EnsureEngineDocumentSession();
            return page >= 0 && page < session.PageCount ? session.Pages[page].Rotation : 0;
        }

        // ---- Straighten Scanned Pages --------------------------------------------------------

        // Measures each scan's tilt from its lines of text and turns the page image back level,
        // keeping the page size. Only scanned pages without a text layer are changed: redrawing
        // a scan drops an OCR layer, so scans should be straightened before OCR.
        private async void StraightenScannedPages()
        {
            if (_doc is null || _currentFile is null) { SetStatus(Loc("Str_Msg_OpenFirst")); return; }
            if (!EnsureEditable(continueAfterUnlock: false)) return;
            CommitActiveTextBox();
            int[] pages = PagesForDocumentCommand();
            string label = Loc("Str_Pages_Straighten");
            var isSourceCurrent = CaptureDocumentOrigin();
            Border? busy = null;
            CancellationToken ct = default;
            try
            {
                ct = BeginCancellableOp(label);
                busy = ShowBusyOverlay(label);
                await Dispatcher.Yield(DispatcherPriority.Background);

                if (!isSourceCurrent() || ct.IsCancellationRequested) return;
                string file = _currentFile;
                var (scans, withText) = await Task.Run(
                    () => PageArrangementService.ScansWithoutText(file, pages, ct), ct);
                if (!isSourceCurrent() || ct.IsCancellationRequested) return;
                string skipped = withText > 0 ? " " + string.Format(Loc("Str_Pages_TextLayerSkipped"), withText) : "";

                var tilts = new Dictionary<int, double>();
                for (int i = 0; i < scans.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    SetBusyMessage(busy, $"{label}: {i + 1}/{scans.Count}");
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    if (!isSourceCurrent() || ct.IsCancellationRequested) return;
                    var preview = RenderPageBitmap(scans[i], 1600);
                    if (preview is null) continue;
                    int width = preview.PixelWidth, height = preview.PixelHeight;
                    var bgra = new byte[width * height * 4];
                    preview.CopyPixels(bgra, width * 4, 0);
                    double tilt = await Task.Run(() => PageArrangementService.MeasureTilt(bgra, width, height), ct);
                    if (!isSourceCurrent() || ct.IsCancellationRequested) return;
                    if (Math.Abs(tilt) >= PageArrangementService.MinimumTilt) tilts[scans[i]] = tilt;
                }
                if (tilts.Count == 0)
                {
                    SetStatus(Loc("Str_Pages_StraightenNone") + skipped);
                    return;
                }

                UndoEntry? documentUndo = CaptureDocumentUndo();
                PdfEngineDocumentSession engineSession = EnsureEngineDocumentSession();
                var replacements = new Dictionary<int, string>();
                var bakedAnnotationPages = new List<int>();
                var unburnedByPage = new Dictionary<int, IReadOnlySet<int>>();
                int skippedPictures = 0;
                int done = 0;
                foreach (var (page, tilt) in tilts.OrderBy(pair => pair.Key))
                {
                    ct.ThrowIfCancellationRequested();
                    SetBusyMessage(busy, $"{label}: {++done}/{tilts.Count}");
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    if (!isSourceCurrent() || ct.IsCancellationRequested) return;

                    // Annotations drawn on the page turn with it, the way the Transform tool
                    // bakes them, so they stay where they were placed on the scan.
                    PageBake bake = BurnPageAnnotationsToTemp(page);
                    string? burned = bake.Path;
                    isSourceCurrent = CaptureDocumentOrigin();
                    if (burned is not null)
                    {
                        bakedAnnotationPages.Add(page);
                        skippedPictures += bake.Skipped;
                        if (bake.Unburned.Count > 0) unburnedByPage[page] = bake.Unburned;
                    }
                    var (pageWidth, pageHeight) = engineSession.VisualPageSize(page, _pageRotations);
                    int budget = Math.Clamp((int)Math.Ceiling(Math.Max(pageWidth, pageHeight) * 300 / 72.0), 1000, 6000);
                    var source = RenderPageBitmap(page, budget, burned)
                        ?? throw new InvalidOperationException(Loc("Str_Tf_NoRender"));
                    string tmp = App.MakeTempFile("straighten");
                    File.WriteAllBytes(tmp, PdfEngineIntegration.CreateRasterDocument([
                        PageArrangementService.LevelPage(source, tilt, pageWidth, pageHeight)]));
                    replacements[page] = tmp;
                }

                int[] changed = [.. replacements.Keys.OrderBy(index => index)];
                int restore = Math.Max(0, PageList.SelectedIndex);
                SaveTempAndReload(
                    keepAnnotations: true,
                    remapAnnotations: annotations =>
                        PdfEngineBurn.RemapAfterBake(annotations, bakedAnnotationPages, unburnedByPage),
                    finalizeSavedFile: path =>
                    {
                        if (changed.Length == engineSession.PageCount)
                            PdfEngineIntegration.ReplaceAllPagesAndCompact(path,
                                [.. changed.Select(page => replacements[page])]);
                        else
                            PdfEngineIntegration.ReplacePagesAndCompact(path, replacements);
                    },
                    remapRotations: rotations =>
                        PdfEngineIntegration.RemapRotationsAfterPageReplacements(rotations, changed),
                    selectedPageAfterReload: restore,
                    documentUndo: documentUndo);
                SetStatus(Services.PicturesSkippedNote.Append(
                    string.Format(Loc("Str_Pages_Straightened"), changed.Length) + skipped,
                    skippedPictures, Loc("Str_Save_PicturesSkipped")));
            }
            catch (OperationCanceledException)
            {
                SetStatus(Loc("Str_St_Canceled"));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_Pages_Failed"), ex.Message), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (busy is not null) HideBusyOverlay(busy);
                EndCancellableOp(ct);
            }
        }
    }
}
