using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
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
            int pageIdx = PageList.SelectedIndex;
            // Render the preview from a copy with this page's annotations baked in, so the preview matches
            // what Apply will produce (otherwise annotations are invisible in the Transform window). Kept at a
            // modest resolution (the preview only shows at ~600px) so the live scale/rotate compose stays
            // fast; Apply re-renders at full resolution independently.
            var src = RenderPageBitmap(pageIdx, 1100, BurnPageAnnotationsToTemp(pageIdx));
            if (src is null) { SetStatus(Loc("Str_Tf_NoRender")); return; }

            // First-use warning that a transform rasterizes the page; persists the opt-out.
            if (App.GetSetting("RotateWarnAck") != "1")
            {
                var (res, dontWarn) = KillerDialog.ShowWithCheckbox(this,
                    Loc("Str_Tf_Warn"),
                    Loc("Str_Tf_DontWarn"), Loc("Str_Tf_Suffix"), MessageBoxButton.OKCancel);
                if (res != MessageBoxResult.OK) return;
                if (dontWarn) App.SetSetting("RotateWarnAck", "1");
            }

            var page = _doc.Pages[pageIdx];
            var (pwpt, phpt) = EffectivePageSize(page);   // CropBox-aware, so the readout matches the visible page
            var win = new TransformWindow(this, src, pwpt, phpt);
            win.ShowDialog();
            if (win.Applied && (Math.Abs(win.Angle) > 0.01 || Math.Abs(win.Scale - 1.0) > 0.001 || win.FlipH || win.FlipV))
                ApplyPageTransform(pageIdx, win.Angle, win.Scale, win.FixedPage, win.FlipH, win.FlipV);
        }

        // The page's visible size in points: the CropBox if one is set (so a cropped page reports its real,
        // smaller size), otherwise the full MediaBox. PdfPage.Width/Height return the MediaBox only.
        private static (double wpt, double hpt) EffectivePageSize(PdfPage page)
        {
            double wpt = page.Width.Point, hpt = page.Height.Point;
            if (page.Elements.GetArray("/CropBox") is { Elements.Count: 4 } cb)
            {
                double x1 = cb.Elements.GetReal(0), y1 = cb.Elements.GetReal(1);
                double x2 = cb.Elements.GetReal(2), y2 = cb.Elements.GetReal(3);
                double cw = Math.Abs(x2 - x1), ch = Math.Abs(y2 - y1);
                if (cw > 1 && ch > 1) { wpt = cw; hpt = ch; }
            }
            return (wpt, hpt);
        }

        // Rasterizes one page with the chosen rotate + scale and swaps it in for the original (undoable).
        private void ApplyPageTransform(int pageIdx, double angleDeg, double scale, bool fixedPage, bool flipH, bool flipV)
        {
            if (_doc is null || _currentFile is null) return;
            if (pageIdx < 0 || pageIdx >= _doc.PageCount) return;

            try
            {
                // Snapshot for undo BEFORE touching the document, so one Ctrl+Z reverts the transform.
                PushDocUndo();

                // If the page carries annotations, bake just that page's annotations into the PDF so they
                // rotate/scale with the page (it is being rasterized anyway, and the user was warned). The
                // helper is non-destructive (restores _doc); we then drop the now-baked annotations.
                string? burned = BurnPageAnnotationsToTemp(pageIdx);
                if (burned != null && _annotations.TryGetValue(pageIdx, out var pageAnns))
                    pageAnns.Clear();   // now part of the page image

                var src = RenderPageBitmap(pageIdx, 2200, burned);
                if (src is null) { SetStatus(Loc("Str_Tf_NoRender")); return; }

                var composed = ComposeTransform(src, angleDeg, scale, fixedPage, flipH, flipV);
                byte[] png = EncodePng(composed);

                var oldPage = _doc.Pages[pageIdx];
                var (epw, eph) = EffectivePageSize(oldPage);   // honor CropBox so a cropped page keeps its size
                // #167: the bitmap is in VISUAL orientation - RenderPageBitmap applies the page's
                // in-app rotation - but MediaBox/CropBox are always unrotated (the working file has
                // /Rotate stripped into _pageRotations). On a quarter-turned page the two disagreed,
                // so sx and sy came out different: the transformed page was squeezed back to portrait
                // and stretched vertically. Swap the point dimensions to match what was rendered.
                int visRot = _pageRotations.TryGetValue(pageIdx, out int vr) ? ((vr % 360) + 360) % 360 : 0;
                if (visRot == 90 || visRot == 270) (epw, eph) = (eph, epw);
                double sx = epw / src.PixelWidth;
                double sy = eph / src.PixelHeight;
                double newWpt = composed.PixelWidth * sx;
                double newHpt = composed.PixelHeight * sy;

                // Build a one-page PDF holding the transformed image (the proven image-page pattern).
                string tmp = App.MakeTempFile("xfpage");
                using (var one = new PdfDocument())
                {
                    var np = one.AddPage();
                    np.Width  = XUnit.FromPoint(newWpt);
                    np.Height = XUnit.FromPoint(newHpt);
                    using (var xi = XImage.FromStream(() => new MemoryStream(png)))
                    using (var gfx = XGraphics.FromPdfPage(np))
                        gfx.DrawImage(xi, 0, 0, np.Width.Point, np.Height.Point);
                    one.Save(tmp);
                }

                // Import that page and swap it in for the original (mirrors DuplicatePage's index dance).
                using (var srcDoc = PdfReader.Open(tmp, PdfDocumentOpenMode.Import))
                {
                    var imported = _doc.AddPage(srcDoc.Pages[0]);
                    _doc.Pages.RemoveAt(_doc.PageCount - 1);
                    _doc.Pages.Insert(pageIdx, imported);
                    _doc.Pages.RemoveAt(pageIdx + 1);
                }

                SaveTempAndReload(keepAnnotations: true);
                SetStatus(string.Format(Loc("Str_Tf_Done"), pageIdx + 1));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_Tf_Failed"), ex.Message), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
                DrawAnnotationsOnDocument(pageIdx);
                _doc.Save(tempBurned);
            }
            catch { burnOk = false; }
            _doc.Close();
            try
            {
                _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
            }
            catch (Exception xrefEx) when (PdfImport.IsXRefException(xrefEx))
            {
                var fixedPath = App.MakeTempFile("xffixed");
                if (!PdfImport.TryImportRepairToPath(tempClean, fixedPath)
                    && !PdfiumInterop.TryPdfiumSaveWithZeroRotations(tempClean, fixedPath))
                    throw;
                tempClean = fixedPath;
                _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
            }
            _currentFile = tempClean;
            return burnOk ? tempBurned : null;
        }

        // Renders a page to a white-backed bitmap (transparent page backgrounds show white, not the dark
        // canvas), applying any in-app rotation so the preview matches the live view.
        private BitmapSource? RenderPageBitmap(int pageIdx, int maxPx, string? sourceOverride = null)
        {
            if (_doc is null || _currentFile is null) return null;
            if (pageIdx < 0 || pageIdx >= _doc.PageCount) return null;
            try
            {
                string srcPath = sourceOverride ?? _currentFile;
                using var docReader = DocLib.Instance.GetDocReader(srcPath, new PageDimensions(maxPx, maxPx));
                using var pr = docReader.GetPageReader(pageIdx);
                int w = pr.GetPageWidth();
                int h = pr.GetPageHeight();
                // #141: WithAnnotations - Transform rasterizes the page and REPLACES it, so
                // without this the file's own markup would be dropped by transforming a page.
                byte[] bgra = pr.GetImage(PdfRender.WithAnnotations);
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
        private static BitmapSource ScaleCompose(BitmapSource src, double scale, bool fixedPage)
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

        // Rotates a bitmap by angleDeg about its centre into a canvas grown to the rotated bounding box, with
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

        private static byte[] EncodePng(BitmapSource bmp)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
    }
}
