using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharpCore.Drawing;
using KillerPDF.Services;

namespace KillerPDF
{
    public partial class MainWindow
    {
        // The document's current stamp configuration (one spec drives page numbers and/or a watermark).
        // Reopening the Stamp tool edits this; Apply rebuilds _stamps from it. Stamps live on their own
        // layer, painted BELOW annotations in RenderAllAnnotations.
        private StampSpec? _docStampSpec;
        private readonly Dictionary<int, List<StampInstance>> _stamps = [];
        // Per-page rendered stamp bounds (render-dim/canvas space) so a double-click can hit-test a stamp and
        // reopen the editor. Repopulated every RenderStamps; the stamp visuals themselves stay non-hit-testable.
        private readonly Dictionary<int, List<Rect>> _stampHitRects = [];

        private void ToolStamp_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { SetStatus(Loc("Str_Tf_NoRender")); return; }
            OpenStampTool();
        }

        // Opens the Stamp window seeded with the current spec (so it edits the existing stamps).
        private void OpenStampTool()
        {
            if (_doc is null) return;
            int pageIdx = PageList.SelectedIndex < 0 ? 0 : PageList.SelectedIndex;
            var src = RenderPageBitmap(pageIdx, 1100, BurnPageAnnotationsToTemp(pageIdx));
            if (src is null) { SetStatus(Loc("Str_Tf_NoRender")); return; }

            var page = _doc.Pages[pageIdx];
            var (pwpt, phpt) = EffectivePageSize(page);
            var win = new StampWindow(this, src, pwpt, phpt, _doc.PageCount, pageIdx, _docStampSpec,
                idx =>   // page-render callback for the preview stepper
                {
                    var s = RenderPageBitmap(idx, 1100, BurnPageAnnotationsToTemp(idx));
                    var (w, h) = EffectivePageSize(_doc!.Pages[idx]);
                    return (s, w, h);
                });
            win.ShowDialog();
            if (win.Applied) ApplyStampSpec(win.Result);
        }

        private void ApplyStampSpec(StampSpec spec)
        {
            _docStampSpec = (spec.NumbersEnabled || spec.WmEnabled) ? spec : null;
            UpdateStampIndicator();
            RebuildStamps();
            RerenderAllVisiblePages();
            MarkDirty();
            int pages = 0;
            foreach (var kv in _stamps) if (kv.Value.Count > 0) pages++;
            SetStatus(string.Format(Loc("Str_Stamp_Applied"), pages));
        }

        // Regenerates the per-page stamp instances from the active spec.
        private void RebuildStamps()
        {
            _stamps.Clear();
            if (_docStampSpec is null || _doc is null) return;
            int n = _doc.PageCount;

            if (_docStampSpec.NumbersEnabled)
                foreach (int p in PdfBurn.StampPageRange(_docStampSpec.NumRange, n))
                    AddStamp(p, StampKind.PageNumber);

            if (_docStampSpec.WmEnabled)
                foreach (int p in PdfBurn.StampPageRange(_docStampSpec.WmRange, n))
                    AddStamp(p, StampKind.Watermark);
        }

        // Keeps the Stamp toolbar button showing a persistent "hovered" (gray) background while the document
        // has active stamps, as a subtle indicator. Cleared when there are no stamps.
        private void UpdateStampIndicator()
        {
            if (ToolStampBtn is null) return;
            if (_docStampSpec is not null)
                ToolStampBtn.SetResourceReference(Control.BackgroundProperty, "RowHoverBrush");
            else
                ToolStampBtn.ClearValue(Control.BackgroundProperty);
        }

        private void AddStamp(int page, StampKind kind)
        {
            if (!_stamps.TryGetValue(page, out var list)) { list = []; _stamps[page] = list; }
            list.Add(new StampInstance { PageIndex = page, Kind = kind, Spec = _docStampSpec! });
        }

        private void RerenderAllVisiblePages()
        {
            if (_doc is null) return;
            // Re-render every currently-mapped page (the primary tile plus all multi-page tiles), so stamps
            // show on every visible page in Grid / Two-Page / Continuous, not just the selected one.
            foreach (int p in new List<int>(_pages.Keys)) RenderAllAnnotations(p);
        }

        // Painted by RenderAllAnnotations onto the page's annotation canvas, BEFORE the annotations, so
        // stamps sit visually beneath them. Coordinates are the same 2048-based render-dim space the page
        // numbers used originally, so placement matches the rest of the annotation layer.
        private void RenderStamps(int pageIndex)
        {
            _stampHitRects[pageIndex] = [];   // reset; repopulated below as stamps render
            if (_docStampSpec is null || _doc is null) return;
            if (!_stamps.TryGetValue(pageIndex, out var list) || list.Count == 0) return;

            var (rdW, rdH, _, phpt) = StampRenderDims(pageIndex);
            if (rdW <= 0 || rdH <= 0) return;
            double mx = rdW * 0.05, my = rdH * 0.04;
            var spec = _docStampSpec;

            // First page that carries a number, so numbering starts at StartNumber there.
            int firstNumPage = -1;
            if (spec.NumbersEnabled)
                foreach (int p in PdfBurn.StampPageRange(spec.NumRange, _doc.PageCount)) { firstNumPage = p; break; }

            foreach (var st in list)
            {
                if (st.Kind == StampKind.Watermark) RenderWatermark(spec, pageIndex, rdW, rdH, phpt, mx, my);
                else RenderPageNumber(spec, pageIndex, firstNumPage, rdW, rdH, phpt, mx, my);
            }
        }

        private void RenderPageNumber(StampSpec spec, int pageIndex, int firstNumPage, double rdW, double rdH, double phpt, double mx, double my)
        {
            double fontCanvas = spec.NumFontPt * rdH / Math.Max(1, phpt);
            int number = spec.StartNumber + Math.Max(0, pageIndex - Math.Max(0, firstNumPage));
            string text = (string.IsNullOrEmpty(spec.Format) ? "{n}" : spec.Format)
                .Replace("{n}", number.ToString())
                .Replace("{N}", (_doc?.PageCount ?? 1).ToString());
            if (text.Length == 0) return;

            var tb = new TextBlock { Text = text, FontFamily = UiKit.UiFont, FontSize = Math.Max(1, fontCanvas), Foreground = new SolidColorBrush(spec.NumColor), IsHitTestVisible = false };
            var sz = MeasureEl(tb);
            int posH = spec.NumPosH;
            double x, y;
            if (posH < 0)   // custom position (center as a fraction of the page)
            {
                double cx = spec.NumCustomX;
                if (spec.NumMirror && (pageIndex % 2 == 1)) cx = 1 - cx;   // mirror flips the x-fraction
                x = cx * rdW - sz.Width / 2;
                y = spec.NumCustomY * rdH - sz.Height / 2;
            }
            else
            {
                // Mirror: on alternating pages flip left<->right so the number sits on the outer edge of a spread.
                if (spec.NumMirror && posH != 1 && (pageIndex % 2 == 1)) posH = 2 - posH;
                x = posH == 0 ? mx : posH == 2 ? rdW - sz.Width - mx : (rdW - sz.Width) / 2;
                y = spec.NumPosV == 0 ? my : spec.NumPosV == 1 ? (rdH - sz.Height) / 2 : rdH - sz.Height - my;
            }
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            _activeCanvas.Children.Add(tb);
            if (_stampHitRects.TryGetValue(pageIndex, out var rects)) rects.Add(new Rect(x, y, sz.Width, sz.Height));
        }

        private void RenderWatermark(StampSpec spec, int pageIndex, double rdW, double rdH, double phpt, double mx, double my)
        {
            FrameworkElement el;
            double w, h;
            if (spec.WmIsImage && !string.IsNullOrEmpty(spec.WmImagePath) && System.IO.File.Exists(spec.WmImagePath))
            {
                BitmapImage? bmp = LoadImageFile(spec.WmImagePath!);
                if (bmp is null) return;
                w = rdW * 0.5 * spec.WmScale;
                h = w * bmp.PixelHeight / Math.Max(1, bmp.PixelWidth);
                el = new Image { Source = bmp, Width = w, Height = h, Opacity = spec.WmOpacity, Stretch = Stretch.Fill, IsHitTestVisible = false };
            }
            else
            {
                if (string.IsNullOrEmpty(spec.WmText)) return;
                double fontCanvas = spec.WmFontPt * rdH / Math.Max(1, phpt);
                var tb = new TextBlock { Text = spec.WmText, FontFamily = new FontFamily(string.IsNullOrWhiteSpace(spec.WmFont) ? "Segoe UI" : spec.WmFont), FontWeight = FontWeights.Bold, FontSize = Math.Max(1, fontCanvas), Foreground = new SolidColorBrush(spec.WmColor), Opacity = spec.WmOpacity, IsHitTestVisible = false };
                var sz = MeasureEl(tb);
                w = sz.Width; h = sz.Height;
                el = tb;
            }

            double x, y;
            if (spec.WmPosH < 0)   // custom position (center as a fraction of the page)
            {
                x = spec.WmCustomX * rdW - w / 2;
                y = spec.WmCustomY * rdH - h / 2;
            }
            else
            {
                x = spec.WmPosH == 0 ? mx : spec.WmPosH == 2 ? rdW - w - mx : (rdW - w) / 2;
                y = spec.WmPosV == 0 ? my : spec.WmPosV == 1 ? (rdH - h) / 2 : rdH - h - my;
            }
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = new RotateTransform(-spec.WmAngle);
            Canvas.SetLeft(el, x);
            Canvas.SetTop(el, y);
            _activeCanvas.Children.Add(el);
            if (_stampHitRects.TryGetValue(pageIndex, out var rects)) rects.Add(new Rect(x, y, w, h));
        }

        // True if a point (render-dim/canvas space) falls on a rendered stamp on this page - used to reopen
        // the Stamp Pages editor on double-click.
        private bool StampHitTest(int pageIndex, Point pos)
        {
            if (_stampHitRects.TryGetValue(pageIndex, out var rects))
                foreach (var r in rects) if (r.Contains(pos)) return true;
            return false;
        }

        // (rdW, rdH) in the 2048-based render-dim space; phpt/pwpt the page size in points (rotation-aware).
        private (double rdW, double rdH, double pwpt, double phpt) StampRenderDims(int pageIndex)
        {
            if (_doc is null) return (0, 0, 0, 0);
            double pw = _doc.Pages[pageIndex].Width.Point;
            double ph = _doc.Pages[pageIndex].Height.Point;
            if (_pageRotations.TryGetValue(pageIndex, out int rot) && (rot == 90 || rot == 270)) (pw, ph) = (ph, pw);
            double maxDim = Math.Max(1, Math.Max(pw, ph));
            return (2048.0 * pw / maxDim, 2048.0 * ph / maxDim, pw, ph);
        }

        private static Size MeasureEl(FrameworkElement el)
        {
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return el.DesiredSize;
        }

        private static BitmapImage? LoadImageFile(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        // StampPageRange (shared with the burned output) lives in Services/PdfBurn.cs.

        // ---- Export: burn the stamp layer into the PDF (below annotations) on save/flatten ----

        // Draws the active stamps into the doc via XGraphics, in PDF-point space. Called BEFORE
        // DrawAnnotationsOnDocument at each save site so stamps sit beneath annotations.
        private void DrawStampsOnDocument(int? onlyPage = null)
            => PdfBurn.DrawStampsIntoDoc(_doc, _docStampSpec, onlyPage, _pageRotations);

        // True when the document carries stamps that must be burned on save. The save sites used to
        // gate the whole burn block on the ANNOTATION count alone, so a document whose only markup
        // was stamps (page numbers / watermark on a fresh doc) saved without them (#147).
        private bool HasActiveStamps => _docStampSpec is { } s && (s.NumbersEnabled || s.WmEnabled);

        // DrawStampsIntoDoc and its DrawNumberPdf / DrawWatermarkPdf / LoadStampImage workers
        // live in Services/PdfBurn.cs with the annotation burn core.
    }
}
