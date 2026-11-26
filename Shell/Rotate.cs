using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    public partial class MainWindow
    {
        // ============================================================
        // Transform tool (rotate + scale; draggable corner handles + aspect-unlock next).
        // The toolbar button opens a modal TransformWindow that renders the page on its own canvas (so the
        // main view mode is irrelevant). Turning, scaling and mirroring apply as one PDF matrix around the page's own
        // content, so its text stays text. Perspective, levels, color and output quality rasterize at full resolution
        // into an expanded white page (no cropped corners) and swap the page in. Both are one undo step.
        // ============================================================

        private void ToolRotate_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { SetStatus(Loc("Str_Msg_OpenFirst")); return; }
            OpenTransformWindow(TransformWindowMode.Transform);
        }

        private void ToolColorCorrection_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { SetStatus(Loc("Str_Msg_OpenFirst")); return; }
            OpenTransformWindow(TransformWindowMode.ColorCorrection);
        }

        // page: the page a menu named; the window opens on it, or on the whole selection when it is part of it.
        private void OpenTransformWindow(TransformWindowMode mode = TransformWindowMode.Transform, int? page = null)
        {
            if (_doc is null) return;
            string toolName = Loc(mode == TransformWindowMode.Transform
                ? "Str_Tf_Suffix" : "Str_ColorCorrection");
            // Transform burns the live annotation layer into its preview and final page image. A text
            // box that still has keyboard focus has not entered that layer yet, so opening Transform
            // directly after typing used to preview (and apply) the page without the new text.
            CommitActiveTextBox();
            int pageIdx = page ?? Math.Max(0, PageList.SelectedIndex);
            int[] selectedPages = [.. PageList.SelectedItems.Cast<PageThumbnailVm>()
                .Select(vm => vm.PageIndex).Distinct().OrderBy(index => index)];
            int[] pageIndices = page is { } clicked ? [.. Services.PageMenuTarget.Pages(clicked, selectedPages).OrderBy(index => index)] : selectedPages;
            if (pageIndices.Length == 0) pageIndices = [pageIdx];

            // Any page's preview, rendered when the window flips to it, with that page's annotations burned in.
            TransformWindow.PagePreview? RenderPreview(int page)
            {
                if (_doc is null || page < 0 || page >= _doc.PageCount) return null;
                string? burned;
                try
                {
                    // A temp folder that refuses the copy is reported like a failed Apply instead of escaping the click.
                    burned = BurnPageAnnotationsToTemp(page).Path;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    KillerDialog.Show(this, string.Format(Loc("Str_Tf_Failed"), ex.Message), "KillerPDF",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return null;
                }
                var src = RenderPageBitmap(page, 1100, burned);
                if (src is null) { SetStatus(Loc("Str_Tf_NoRender")); return null; }
                PdfEngineDocumentSession session = EnsureEngineDocumentSession();
                var (pwpt, phpt) = session.PhysicalPageSize(page, _pageRotations);
                return new TransformWindow.PagePreview(src, pwpt, phpt, page + 1, session.PageImagePixels(page, _pageRotations));
            }
            if (RenderPreview(pageIndices[0]) is not { } first) return;

            // Apply changes the page in the window (or the opening selection) and the window stays open, so the
            // next page can be flipped to and changed; each Apply is its own undo step.
            var win = new TransformWindow(this, _doc.PageCount, pageIndices, first, RenderPreview,
                (window, pages) => window.HasChanges
                    ? ApplyPageTransformsAsync(pages, window.Angle, window.Scale, window.FixedPage, window.FlipH, window.FlipV,
                        window.PerspectiveCorners, window.LevelBlack, window.LevelWhite, window.LevelGamma,
                        window.ColorMode, window.BlackWhiteThreshold, window.OutputDpi,
                        window.UseJpegCompression, window.JpegQuality, window.ColorAdjustments, toolName)
                    : Task.FromResult(false), mode);
            win.ShowDialog();
        }

        // Transforms every selected page with one setup and swaps the batch in as one undo step.
        private async Task<bool> ApplyPageTransformsAsync(IReadOnlyList<int> pageIndices, double angleDeg,
            double scale, bool fixedPage, bool flipH, bool flipV, Point[] perspectiveCorners,
            int levelBlack = 0, int levelWhite = 255, double levelGamma = 1.0,
            PageColorMode colorMode = PageColorMode.Color,
            int blackWhiteThreshold = 160, int outputDpi = 0,
            bool useJpegCompression = false, int jpegQuality = 85,
            ColorCorrectionSettings? colorAdjustments = null, string? toolName = null)
        {
            if (_doc is null || _currentFile is null) return false;
            if (!EnsureEditable(continueAfterUnlock: false)) return false;
            PdfEngineDocumentSession engineSession = EnsureEngineDocumentSession();
            int[] pages = [.. pageIndices.Distinct().OrderBy(index => index)];
            if (pages.Length == 0 || pages.Any(index => index < 0 || index >= engineSession.PageCount)) return false;

            var sourceViewer = ActiveViewer;
            var sourceSession = sourceViewer.ActiveSessionExt;
            var sourceDocument = _doc;
            bool IsSourceCurrent() => ReferenceEquals(ActiveViewer, sourceViewer)
                && ReferenceEquals(sourceViewer.ActiveSessionExt, sourceSession)
                && ReferenceEquals(_doc, sourceDocument);
            Border? busy = null;
            CancellationToken ct = default;
            try
            {
                ct = BeginCancellableOp(Loc("Str_Tf_Suffix"));
                busy = ShowBusyOverlay($"{Loc("Str_Tf_Suffix")}: 1/{pages.Length}");
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Background);
                if (!IsSourceCurrent() || ct.IsCancellationRequested) return false;

                // Retain the complete pre-transform state for the central history push.
                UndoEntry? documentUndo = CaptureDocumentUndo();

                // Turning, scaling and mirroring keep the page as PDF content, so its text stays text. Only the
                // perspective, levels, color and output quality settings need the page rendered to a picture.
                if (KeepsVectorContent(perspectiveCorners, levelBlack, levelWhite, levelGamma, colorMode,
                        outputDpi, useJpegCompression, colorAdjustments))
                {
                    int[] annotated = [.. pages.Where(page => _annotations.TryGetValue(page, out var list) && list.Count > 0)];
                    var liveAnnotations = _annotations;
                    var renderDims = _renderDims;
                    var rotations = new Dictionary<int, int>(_pageRotations);
                    int skipped = 0;
                    SaveTempAndReload(
                        keepAnnotations: true,
                        // The app's own marks on these pages are burned into them first, so they turn with the page.
                        remapAnnotations: annotations => PdfEngineBurn.RemapAfterBake(annotations, annotated),
                        finalizeSavedFile: path =>
                        {
                            foreach (int page in annotated)
                            {
                                PdfEngineBurn.Burn(path, liveAnnotations, renderDims, null, page, rotations);
                                skipped += PdfEngineBurn.SkippedPictures;
                            }
                            PageTransformGeometry.TransformPages(path, pages, rotations, angleDeg, scale, fixedPage, flipH, flipV);
                        },
                        remapRotations: rotationMap =>
                            PdfEngineIntegration.RemapRotationsAfterPageReplacements(rotationMap, pages),
                        selectedPageAfterReload: pages[0],
                        documentUndo: documentUndo);
                    ReselectTransformedPages(sourceViewer, sourceSession, pages);
                    SetStatus(Services.PicturesSkippedNote.Append(pages.Length == 1
                        ? string.Format(Loc("Str_Tf_Done"), pages[0] + 1)
                        : string.Format(Loc("Str_Tf_DoneBatch"), pages.Length),
                        skipped, Loc("Str_Save_PicturesSkipped")));
                    return true;
                }

                // The raster path changes the page into a picture, so it says so once.
                if (App.GetSetting("RotateWarnAck") != "1")
                {
                    if (busy is not null) { HideBusyOverlay(busy); busy = null; }
                    var (res, dontWarn) = KillerDialog.ShowWithCheckbox(this,
                        Loc("Str_Tf_Warn"), Loc("Str_Tf_DontWarn"), toolName ?? Loc("Str_Tf_Suffix"), MessageBoxButton.OKCancel);
                    if (res != MessageBoxResult.OK) return false;
                    if (dontWarn) App.SetSetting("RotateWarnAck", "1");
                    if (!IsSourceCurrent() || ct.IsCancellationRequested) return false;
                    busy = ShowBusyOverlay($"{Loc("Str_Tf_Suffix")}: 1/{pages.Length}");
                }

                // If the page carries annotations, bake just that page's annotations into the PDF so they
                // rotate/scale with the page (it is being rasterized anyway, and the user was warned). The
                // helper is non-destructive (restores _doc); baked annotations are removed only
                // when the replacement document succeeds.
                var replacements = new Dictionary<int, string>();
                var bakedAnnotationPages = new List<int>();
                int skippedPictures = 0;
                for (int i = 0; i < pages.Length; i++)
                {
                    if (ct.IsCancellationRequested)
                    {
                        SetStatus(Loc("Str_St_Canceled"));
                        return false;
                    }
                    int pageIdx = pages[i];
                    SetBusyMessage(busy, $"{Loc("Str_Tf_Suffix")}: {i + 1}/{pages.Length}");
                    await System.Windows.Threading.Dispatcher.Yield(
                        System.Windows.Threading.DispatcherPriority.Background);
                    if (!IsSourceCurrent() || ct.IsCancellationRequested) return false;

                    PageBake bake = BurnPageAnnotationsToTemp(pageIdx);
                    string? burned = bake.Path;
                    sourceDocument = _doc;
                    if (burned != null)
                    {
                        bakedAnnotationPages.Add(pageIdx);
                        skippedPictures += bake.Skipped;
                    }
                    var (epw, eph) = engineSession.PhysicalPageSize(pageIdx, _pageRotations);
                    int renderBudget = outputDpi > 0
                        ? Math.Max(1, checked((int)Math.Ceiling(Math.Max(epw, eph) * outputDpi / 72.0)))
                        : 2200;
                    var src = RenderPageBitmap(pageIdx, renderBudget, burned) ?? throw new InvalidOperationException(Loc("Str_Tf_NoRender"));
                    var perspective = PerspectiveWarp.IsIdentity(perspectiveCorners)
                        ? src : PerspectiveWarp.Apply(src, perspectiveCorners);
                    var composed = PageTransformRaster.ComposeTransform(perspective, angleDeg, scale, fixedPage, flipH, flipV);
                    composed = TransformWindow.ApplyLevels(composed, levelBlack, levelWhite, levelGamma);
                    composed = ColorCorrection.Apply(composed, colorAdjustments ?? new());
                    composed = PageQualityConverter.ApplyColorMode(
                        composed, colorMode, blackWhiteThreshold);
                    double sx = epw / src.PixelWidth;
                    double sy = eph / src.PixelHeight;
                    double newWpt = composed.PixelWidth * sx;
                    double newHpt = composed.PixelHeight * sy;

                    string tmp = App.MakeTempFile("xfpage");
                    byte[] pixels = new byte[composed.PixelWidth * composed.PixelHeight * 4];
                    composed.CopyPixels(pixels, composed.PixelWidth * 4, 0);
                    ReadOnlyMemory<byte> jpeg = useJpegCompression
                        ? BitmapHelpers.EncodeJpeg(composed, jpegQuality,
                            colorMode == PageColorMode.Grayscale) : default;
                    File.WriteAllBytes(tmp, PdfEngineIntegration.CreateRasterDocument([
                        new PdfEngineIntegration.RasterPage(composed.PixelWidth,
                            composed.PixelHeight, newWpt, newHpt, pixels, jpeg,
                            Bitonal: colorMode == PageColorMode.BlackAndWhite,
                            Grayscale: colorMode == PageColorMode.Grayscale)]));
                    replacements[pageIdx] = tmp;
                }
                if (ct.IsCancellationRequested)
                {
                    SetStatus(Loc("Str_St_Canceled"));
                    return false;
                }
                SaveTempAndReload(
                    keepAnnotations: true,
                    // Every baked page loses its records: they are in the page, or the burn left them out and said so.
                    remapAnnotations: annotations =>
                        PdfEngineBurn.RemapAfterBake(annotations, bakedAnnotationPages),
                    finalizeSavedFile: path =>
                    {
                        if (pages.Length == engineSession.PageCount)
                            PdfEngineIntegration.ReplaceAllPagesAndCompact(path,
                                [.. pages.Select(page => replacements[page])]);
                        else
                            PdfEngineIntegration.ReplacePagesAndCompact(path, replacements);
                    },
                    remapRotations: rotations =>
                        PdfEngineIntegration.RemapRotationsAfterPageReplacements(
                            rotations, pages),
                    selectedPageAfterReload: pages[0],
                    documentUndo: documentUndo);
                ReselectTransformedPages(sourceViewer, sourceSession, pages);
                SetStatus(Services.PicturesSkippedNote.Append(pages.Length == 1
                    ? string.Format(Loc("Str_Tf_Done"), pages[0] + 1)
                    : string.Format(Loc("Str_Tf_DoneBatch"), pages.Length),
                    skippedPictures, Loc("Str_Save_PicturesSkipped")));
                return true;
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_Tf_Failed"), ex.Message), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (busy is not null) HideBusyOverlay(busy);
                EndCancellableOp(ct);
            }
            return false;
        }

        // The settings a PDF matrix can carry. Anything else changes pixels and needs the raster path.
        internal static bool KeepsVectorContent(Point[] perspectiveCorners, int levelBlack, int levelWhite,
            double levelGamma, PageColorMode colorMode, int outputDpi, bool useJpegCompression,
            ColorCorrectionSettings? colorAdjustments) =>
            PerspectiveWarp.IsIdentity(perspectiveCorners)
            && TransformWindow.LevelsIdentity(levelBlack, levelWhite, levelGamma)
            && colorMode == PageColorMode.Color
            && (colorAdjustments?.IsIdentity ?? true)
            && outputDpi <= 0 && !useJpegCompression;

        private void ReselectTransformedPages(object sourceViewer, object? sourceSession, int[] pages) =>
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
            {
                if (!ReferenceEquals(ActiveViewer, sourceViewer)
                    || !ReferenceEquals(ActiveViewer.ActiveSessionExt, sourceSession)) return;
                PageList.SelectedItems.Clear();
                foreach (int pageIdx in pages)
                    if (pageIdx >= 0 && pageIdx < PageList.Items.Count)
                        PageList.SelectedItems.Add(PageList.Items[pageIdx]);
            }));

        // Saves the document with ONE page's annotations burned in, to a temp PDF, and returns its path
        // (null if the page has no annotations - the caller then renders the normal source). Non-destructive:
        // _doc is restored to its pre-burn state by reopening from a clean snapshot, mirroring the proven
        // Save-Flattened pattern, so this is safe for the preview as well as Apply.
        // The result carries how many pictures the burn left out, so the caller can say so.
        private readonly record struct PageBake(string? Path, int Skipped);

        private PageBake BurnPageAnnotationsToTemp(int pageIdx)
        {
            if (_doc is null) return default;
            if (!(_annotations.TryGetValue(pageIdx, out var pa) && pa.Count > 0)) return default;

            var tempClean  = App.MakeTempFile("xfclean");
            var tempBurned = App.MakeTempFile("xfburn");
            _doc.Save(tempClean);
            // #142: a failed burn (one bad annotation) must not crash the tool that asked for the
            // preview. The clean snapshot is already on disk, so on failure fall through to the
            // restore below and render without the annotation layer instead.
            bool burnOk = true;
            int skipped = 0;
            try
            {
                System.IO.File.Copy(tempClean, tempBurned, true);
                PdfEngineBurn.Burn(tempBurned, _annotations, _renderDims,
                    null, pageIdx, _pageRotations);
                skipped = PdfEngineBurn.SkippedPictures;
            }
            catch { burnOk = false; }
            _doc.Close();
            try
            {
                _doc = PdfWorkingDocument.Open(tempClean, null);
            }
            catch (Exception xrefEx) when (PdfImport.IsXRefException(xrefEx))
            {
                var fixedPath = App.MakeTempFile("xffixed");
                if (!PdfImport.TryImportRepairToPath(tempClean, fixedPath)
                    && !PdfEngineIntegration.TryCreateZeroRotationCopy(tempClean, fixedPath))
                    throw;
                tempClean = fixedPath;
                _doc = PdfWorkingDocument.Open(tempClean, null);
            }
            _currentFile = tempClean;
            return burnOk ? new PageBake(tempBurned, skipped) : default;
        }

        // Renders a page to a white-backed bitmap (transparent page backgrounds show white, not the dark
        // canvas), applying any in-app rotation so the preview matches the live view.
        private RenderTargetBitmap? RenderPageBitmap(int pageIdx, int maxPx, string? sourceOverride = null)
        {
            if (_doc is null || _currentFile is null) return null;
            if (pageIdx < 0 || pageIdx >= _doc.PageCount) return null;
            try
            {
                string srcPath = sourceOverride ?? _currentFile;
                using var renderSession = PdfPageRenderSession.OpenEngineFirst(srcPath, maxPx, maxPx,
                    sourceOverride is null ? _protection : null);
                PdfRenderedPage page = renderSession.RenderPage(pageIdx);
                int w = page.Width;
                int h = page.Height;
                // #141: WithAnnotations - Transform rasterizes the page and REPLACES it, so
                // without this the file's own markup would be dropped by transforming a page.
                byte[] bgra = page.Pixels;
                if (_pageRotations.TryGetValue(pageIdx, out int prot) && prot != 0)
                    (bgra, w, h) = BitmapHelpers.RotateBitmap(bgra, w, h, prot);
                if (bgra == null || bgra.Length == 0 || w <= 0 || h <= 0) return null;

                var raw = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                    dc.DrawImage(raw, new Rect(0, 0, w, h));
                }
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            catch { return null; }
        }

    }
}
