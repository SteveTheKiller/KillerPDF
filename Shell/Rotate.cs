using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Services;

namespace KillerPDF
{
    public partial class MainWindow
    {
        // ============================================================
        // Transform tool (rotate + scale; draggable corner handles + aspect-unlock next).
        // The toolbar button opens a modal TransformWindow that renders the page on its own canvas (so the
        // main view mode is irrelevant). Apply rasterizes at full resolution into an expanded white page
        // (no cropped corners) and swaps the page in, with undo.
        // ============================================================

        private void ToolRotate_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { SetStatus(Loc("Str_Msg_OpenFirst")); return; }
            OpenTransformWindow();
        }

        private void OpenTransformWindow()
        {
            if (_doc is null) return;
            // Transform burns the live annotation layer into its preview and final page image. A text
            // box that still has keyboard focus has not entered that layer yet, so opening Transform
            // directly after typing used to preview (and apply) the page without the new text.
            CommitActiveTextBox();
            int pageIdx = PageList.SelectedIndex;
            int[] pageIndices = [.. PageList.SelectedItems.Cast<PageThumbnailVm>()
                .Select(page => page.PageIndex).Distinct().OrderBy(index => index)];
            if (pageIndices.Length == 0) pageIndices = [pageIdx];
            // Build a modest preview for every selected page. Transform settings remain shared, while
            // the window can flip through the actual targets before applying the batch.
            PdfEngineDocumentSession previewSession = EnsureEngineDocumentSession();
            var previews = new List<TransformWindow.PagePreview>();
            foreach (int selectedPage in pageIndices)
            {
                var src = RenderPageBitmap(selectedPage, 1100, BurnPageAnnotationsToTemp(selectedPage));
                if (src is null) { SetStatus(Loc("Str_Tf_NoRender")); return; }
                var (pwpt, phpt) = previewSession.VisualPageSize(selectedPage, _pageRotations);
                previews.Add(new TransformWindow.PagePreview(src, pwpt, phpt, selectedPage + 1));
            }

            // First-use warning that a transform rasterizes the page; persists the opt-out.
            if (App.GetSetting("RotateWarnAck") != "1")
            {
                var (res, dontWarn) = KillerDialog.ShowWithCheckbox(this,
                    Loc("Str_Tf_Warn"),
                    Loc("Str_Tf_DontWarn"), Loc("Str_Tf_Suffix"), MessageBoxButton.OKCancel);
                if (res != MessageBoxResult.OK) return;
                if (dontWarn) App.SetSetting("RotateWarnAck", "1");
            }

            var win = new TransformWindow(this, previews);
            win.ShowDialog();
            if (win.Applied && (Math.Abs(win.Angle) > 0.01 || Math.Abs(win.Scale - 1.0) > 0.001 ||
                win.FlipH || win.FlipV || !PerspectiveWarp.IsIdentity(win.PerspectiveCorners) ||
                !TransformWindow.LevelsIdentity(win.LevelBlack, win.LevelWhite, win.LevelGamma) ||
                win.ColorMode != PageColorMode.Color || win.OutputDpi > 0 ||
                win.UseJpegCompression))
                ApplyPageTransforms(pageIndices, win.Angle, win.Scale, win.FixedPage, win.FlipH, win.FlipV,
                    win.PerspectiveCorners, win.LevelBlack, win.LevelWhite, win.LevelGamma,
                    win.ColorMode, win.BlackWhiteThreshold, win.OutputDpi,
                    win.UseJpegCompression, win.JpegQuality);
        }

        // Rasterizes every selected page with one transform setup and swaps the batch in as one undo step.
        private async void ApplyPageTransforms(IReadOnlyList<int> pageIndices, double angleDeg,
            double scale, bool fixedPage, bool flipH, bool flipV, Point[] perspectiveCorners,
            int levelBlack = 0, int levelWhite = 255, double levelGamma = 1.0,
            PageColorMode colorMode = PageColorMode.Color,
            int blackWhiteThreshold = 160, int outputDpi = 0,
            bool useJpegCompression = false, int jpegQuality = 85)
        {
            if (_doc is null || _currentFile is null) return;
            PdfEngineDocumentSession engineSession = EnsureEngineDocumentSession();
            int[] pages = [.. pageIndices.Distinct().OrderBy(index => index)];
            if (pages.Length == 0 || pages.Any(index => index < 0 || index >= engineSession.PageCount)) return;

            Border? busy = null;
            try
            {
                var ct = BeginCancellableOp(Loc("Str_Tf_Suffix"));
                busy = ShowBusyOverlay($"{Loc("Str_Tf_Suffix")}: 1/{pages.Length}");
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Background);

                // This operation adjusts overlay annotations before SaveTempAndReload, so retain
                // the complete pre-transform state for the central history push.
                UndoEntry? documentUndo = CaptureDocumentUndo();

                // If the page carries annotations, bake just that page's annotations into the PDF so they
                // rotate/scale with the page (it is being rasterized anyway, and the user was warned). The
                // helper is non-destructive (restores _doc); we then drop the now-baked annotations.
                var replacements = new Dictionary<int, string>();
                var bakedAnnotationPages = new List<int>();
                for (int i = 0; i < pages.Length; i++)
                {
                    if (ct.IsCancellationRequested)
                    {
                        SetStatus(Loc("Str_St_Canceled"));
                        return;
                    }
                    int pageIdx = pages[i];
                    SetBusyMessage(busy, $"{Loc("Str_Tf_Suffix")}: {i + 1}/{pages.Length}");
                    await System.Windows.Threading.Dispatcher.Yield(
                        System.Windows.Threading.DispatcherPriority.Background);

                    string? burned = BurnPageAnnotationsToTemp(pageIdx);
                    if (burned != null) bakedAnnotationPages.Add(pageIdx);
                    var (epw, eph) = engineSession.VisualPageSize(pageIdx, _pageRotations);
                    int renderBudget = outputDpi > 0
                        ? Math.Max(1, (int)Math.Ceiling(Math.Max(epw, eph) * outputDpi / 72.0))
                        : 2200;
                    var src = RenderPageBitmap(pageIdx, renderBudget, burned) ?? throw new InvalidOperationException(Loc("Str_Tf_NoRender"));
                    var perspective = PerspectiveWarp.IsIdentity(perspectiveCorners)
                        ? src : PerspectiveWarp.Apply(src, perspectiveCorners);
                    var composed = ComposeTransform(perspective, angleDeg, scale, fixedPage, flipH, flipV);
                    composed = TransformWindow.ApplyLevels(composed, levelBlack, levelWhite, levelGamma);
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
                        ? EncodeJpeg(composed, jpegQuality,
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
                    return;
                }
                foreach (int pageIdx in bakedAnnotationPages)
                    if (_annotations.TryGetValue(pageIdx, out var pageAnns)) pageAnns.Clear();

                SaveTempAndReload(
                    keepAnnotations: true,
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
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
                {
                    PageList.SelectedItems.Clear();
                    foreach (int pageIdx in pages)
                        if (pageIdx >= 0 && pageIdx < PageList.Items.Count)
                            PageList.SelectedItems.Add(PageList.Items[pageIdx]);
                }));
                SetStatus(pages.Length == 1
                    ? string.Format(Loc("Str_Tf_Done"), pages[0] + 1)
                    : string.Format(Loc("Str_Tf_DoneBatch"), pages.Length));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_Tf_Failed"), ex.Message), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (busy is not null) HideBusyOverlay(busy);
                EndCancellableOp();
            }
        }

        private static byte[] EncodeJpeg(
            BitmapSource source, int quality, bool grayscale)
        {
            BitmapSource encodedSource = grayscale
                ? new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0)
                : source;
            var encoder = new JpegBitmapEncoder
            {
                QualityLevel = Math.Clamp(quality, 1, 100)
            };
            encoder.Frames.Add(BitmapFrame.Create(encodedSource));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        // Saves the document with ONE page's annotations burned in, to a temp PDF, and returns its path
        // (null if the page has no annotations - the caller then renders the normal source). Non-destructive:
        // _doc is restored to its pre-burn state by reopening from a clean snapshot, mirroring the proven
        // Save-Flattened pattern, so this is safe for the preview as well as Apply.
        private string? BurnPageAnnotationsToTemp(int pageIdx)
        {
            if (_doc is null) return null;
            if (!(_annotations.TryGetValue(pageIdx, out var pa) && pa.Count > 0)) return null;

            var tempClean  = App.MakeTempFile("xfclean");
            var tempBurned = App.MakeTempFile("xfburn");
            _doc.Save(tempClean);
            // #142: a failed burn (one bad annotation) must not crash the tool that asked for the
            // preview. The clean snapshot is already on disk, so on failure fall through to the
            // restore below and render without the annotation layer instead.
            bool burnOk = true;
            try
            {
                System.IO.File.Copy(tempClean, tempBurned, true);
                PdfEngineBurn.Burn(tempBurned, _annotations, _renderDims,
                    null, pageIdx, _pageRotations);
            }
            catch { burnOk = false; }
            _doc.Close();
            try
            {
                _doc = PdfWorkingDocument.Open(tempClean);
            }
            catch (Exception xrefEx) when (PdfImport.IsXRefException(xrefEx))
            {
                var fixedPath = App.MakeTempFile("xffixed");
                if (!PdfImport.TryImportRepairToPath(tempClean, fixedPath)
                    && !PdfEngineIntegration.TryCreateZeroRotationCopy(tempClean, fixedPath))
                    throw;
                tempClean = fixedPath;
                _doc = PdfWorkingDocument.Open(tempClean);
            }
            _currentFile = tempClean;
            return burnOk ? tempBurned : null;
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
                using var renderSession = PdfPageRenderSession.OpenEngineFirst(srcPath, maxPx, maxPx);
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

        // Scale (per page-size mode) then rotate. Used by both the window preview and full-resolution Apply.
        internal static BitmapSource ComposeTransform(BitmapSource src, double angleDeg, double scale, bool fixedPage, bool flipH, bool flipV)
        {
            var s = ApplyFlip(src, flipH, flipV);
            var scaled = Math.Abs(scale - 1.0) < 0.001 ? s : ScaleCompose(s, scale, fixedPage);
            return Math.Abs(angleDeg) < 0.001 ? scaled : RotateExpand(scaled, angleDeg);
        }

        private static BitmapSource ApplyFlip(BitmapSource src, bool flipH, bool flipV)
        {
            if (!flipH && !flipV) return src;
            var tb = new TransformedBitmap(src, new ScaleTransform(flipH ? -1 : 1, flipV ? -1 : 1));
            tb.Freeze();
            return tb;
        }

        // fixedPage=true: keep the canvas size, shrink the content with white margins. false: resize the page
        // (fewer pixels at the same points-per-pixel = a physically smaller page).
        private static RenderTargetBitmap ScaleCompose(BitmapSource src, double scale, bool fixedPage)
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            int sw = Math.Max(1, (int)Math.Round(w * scale));
            int sh = Math.Max(1, (int)Math.Round(h * scale));

            var dv = new DrawingVisual();
            if (fixedPage)
            {
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                    dc.DrawImage(src, new Rect((w - sw) / 2.0, (h - sh) / 2.0, sw, sh));
                }
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            else
            {
                using (var dc = dv.RenderOpen())
                    dc.DrawImage(src, new Rect(0, 0, sw, sh));
                var rtb = new RenderTargetBitmap(sw, sh, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
        }

        // Rotates a bitmap by angleDeg about its center into a canvas grown to the rotated bounding box, with
        // the new corners filled white.
        internal static BitmapSource RotateExpand(BitmapSource src, double angleDeg)
        {
            double w = src.PixelWidth, h = src.PixelHeight;
            double rad = angleDeg * Math.PI / 180.0;
            double cos = Math.Abs(Math.Cos(rad));
            double sin = Math.Abs(Math.Sin(rad));
            int nw = (int)Math.Ceiling(w * cos + h * sin);
            int nh = (int)Math.Ceiling(w * sin + h * cos);

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, nw, nh));
                dc.PushTransform(new TranslateTransform(nw / 2.0, nh / 2.0));
                dc.PushTransform(new RotateTransform(angleDeg));
                dc.DrawImage(src, new Rect(-w / 2.0, -h / 2.0, w, h));
                dc.Pop();
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(nw, nh, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

    }
}
