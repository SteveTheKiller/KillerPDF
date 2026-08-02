using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using KillerPDF.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace KillerPDF.Controls
{
    // Moved from Shell/Crop.cs; the namespace and class line are the only changes. Window members
    // spelled bare here resolve through PdfViewer.Bridge.cs.
    public partial class PdfViewer
    {
        // ============================================================
        // Crop tool
        // ============================================================

        // Crop coordinate helpers
        //
        // The rendered canvas already incorporates the user-applied rotation stored
        // in _pageRotations.  These helpers invert / apply the same transforms that
        // the link-overlay code uses (lines ~1925-1957), so canvas<->PDF coords are
        // consistent with how Docnet drew the bitmap.
        //
        //  rot=0:   canvas_x = native_x * cW/pW,  canvas_y = (pH - native_y) * cH/pH
        //  rot=90:  canvas_x = native_y * cW/pH,  canvas_y = native_x * cH/pW
        //  rot=180: canvas_x = (pW - nx) * cW/pW, canvas_y = (pH - ny) * cH/pH
        //  rot=270: canvas_x = (pH - ny) * cW/pH, canvas_y = (pW - nx) * cH/pW

        /// <summary>
        /// Convert a canvas-space <see cref="Rect"/> to PDF CropBox coordinates
        /// (bottom-left origin, points) with rotation awareness.
        /// </summary>
        private static (double x1, double y1, double x2, double y2) CanvasToPdfRect(
            Rect cr, double pdfW, double pdfH, double canvasW, double canvasH, int rot)
        {
            double cx = cr.X, cy = cr.Y, cw = cr.Width, ch = cr.Height;
            return rot switch
            {
                90  => (cy      * pdfW / canvasH,
                        cx      * pdfH / canvasW,
                       (cy + ch) * pdfW / canvasH,
                       (cx + cw) * pdfH / canvasW),

                180 => (pdfW - (cx + cw) * pdfW / canvasW,
                        pdfH - (cy + ch) * pdfH / canvasH,
                        pdfW -  cx       * pdfW / canvasW,
                        pdfH -  cy       * pdfH / canvasH),

                270 => (pdfW - (cy + ch) * pdfW / canvasH,
                        pdfH - (cx + cw) * pdfH / canvasW,
                        pdfW -  cy       * pdfW / canvasH,
                        pdfH -  cx       * pdfH / canvasW),

                _   => (cx       * pdfW / canvasW,           // 0 deg
                        pdfH - (cy + ch) * pdfH / canvasH,
                       (cx + cw) * pdfW / canvasW,
                        pdfH -  cy       * pdfH / canvasH),
            };
        }

        /// <summary>
        /// Inverse of <see cref="CanvasToPdfRect"/> - map PDF CropBox coords back to a canvas-space
        /// <see cref="Rect"/>.
        /// </summary>
        private static Rect PdfToCanvasRect(
            double x1, double y1, double x2, double y2,
            double pdfW, double pdfH, double canvasW, double canvasH, int rot)
        {
            double cx, cy, cw, ch;
            switch (rot)
            {
                case 90:
                    cx = y1 * canvasW / pdfH;
                    cy = x1 * canvasH / pdfW;
                    cw = (y2 - y1) * canvasW / pdfH;
                    ch = (x2 - x1) * canvasH / pdfW;
                    break;
                case 180:
                    cx = (pdfW - x2) * canvasW / pdfW;
                    cy = (pdfH - y2) * canvasH / pdfH;
                    cw = (x2 - x1)   * canvasW / pdfW;
                    ch = (y2 - y1)   * canvasH / pdfH;
                    break;
                case 270:
                    cx = (pdfH - y2) * canvasW / pdfH;
                    cy = (pdfW - x2) * canvasH / pdfW;
                    cw = (y2 - y1)   * canvasW / pdfH;
                    ch = (x2 - x1)   * canvasH / pdfW;
                    break;
                default: // 0 deg
                    cx = x1 * canvasW / pdfW;
                    cy = (pdfH - y2) * canvasH / pdfH;
                    cw = (x2 - x1)  * canvasW / pdfW;
                    ch = (y2 - y1)  * canvasH / pdfH;
                    break;
            }
            return new Rect(Math.Max(0, cx), Math.Max(0, cy),
                            Math.Max(10, cw), Math.Max(10, ch));
        }

        // The displayed page's point dimensions (width across the canvas, height down it). For a 90/270
        // rotation the rendered bitmap is turned, so the point dims are swapped relative to the raw page.
        private (double dispW, double dispH, double sx, double sy) CropDisplayDims(int pi, (double w, double h) dims)
        {
            _pageRotations.TryGetValue(pi, out int rot);
            var page = _doc!.Pages[pi];
            double pdfW = page.Width.Point, pdfH = page.Height.Point;
            bool swap = rot == 90 || rot == 270;
            double dispW = swap ? pdfH : pdfW;
            double dispH = swap ? pdfW : pdfH;
            return (dispW, dispH, dispW / dims.w, dispH / dims.h);   // sx,sy: page-points per canvas-unit
        }

        // points -> the active display unit (relative to the page dimension for "%").
        private double FromPoints(double pts, double pageDim) => _cropUnit switch
        {
            "in" => pts / 72.0,
            "%"  => pageDim > 0 ? pts / pageDim * 100.0 : 0,
            _    => pts,
        };

        // active display unit -> points.
        private double ToPoints(double val, double pageDim) => _cropUnit switch
        {
            "in" => val * 72.0,
            "%"  => val / 100.0 * pageDim,
            _    => val,
        };

        /// <summary>
        /// Push <see cref="_cropCanvasRect"/> into the X/Y/W/H boxes as a top-left origin rectangle
        /// (GIMP style) in the active unit. No-ops when the bar isn't showing.
        /// </summary>
        private void SyncCropBoxInputs()
        {
            if (_cropXBox is null || _doc is null) return;
            int pi = PageList.SelectedIndex;
            if (pi < 0 || !_renderDims.TryGetValue(pi, out var dims)) return;
            var (dispW, dispH, sx, sy) = CropDisplayDims(pi, dims);

            var r = _cropCanvasRect;
            double xPt = Math.Max(0, r.X) * sx;
            double yPt = Math.Max(0, r.Y) * sy;
            double wPt = r.Width  * sx;
            double hPt = r.Height * sy;
            xPt = Math.Min(xPt, dispW); yPt = Math.Min(yPt, dispH);
            wPt = Math.Min(wPt, dispW - xPt); hPt = Math.Min(hPt, dispH - yPt);

            string fmt = _cropUnit == "in" ? "F2" : "F1";
            _updatingCropInputs = true;
            _cropXBox.Text  = FromPoints(xPt, dispW).ToString(fmt);
            _cropYBox!.Text = FromPoints(yPt, dispH).ToString(fmt);
            _cropWBox!.Text = FromPoints(wPt, dispW).ToString(fmt);
            _cropHBox!.Text = FromPoints(hPt, dispH).ToString(fmt);
            _updatingCropInputs = false;
        }

        /// <summary>
        /// Read the X/Y/W/H boxes (top-left origin, active unit) -> update <see cref="_cropCanvasRect"/>.
        /// Called on Enter or LostFocus inside a box.
        /// </summary>
        private void CommitCropBoxInput()
        {
            if (_updatingCropInputs || _cropXBox is null || _doc is null) return;
            int pi = PageList.SelectedIndex;
            if (pi < 0 || !_renderDims.TryGetValue(pi, out var dims)) return;
            if (!double.TryParse(_cropXBox.Text,  out double x)) return;
            if (!double.TryParse(_cropYBox!.Text, out double y)) return;
            if (!double.TryParse(_cropWBox!.Text, out double w)) return;
            if (!double.TryParse(_cropHBox!.Text, out double h)) return;

            var (dispW, dispH, sx, sy) = CropDisplayDims(pi, dims);
            double xPt = ToPoints(x, dispW), yPt = ToPoints(y, dispH);
            double wPt = ToPoints(w, dispW), hPt = ToPoints(h, dispH);

            xPt = Math.Max(0, Math.Min(dispW - 1, xPt));
            yPt = Math.Max(0, Math.Min(dispH - 1, yPt));
            wPt = Math.Max(1, Math.Min(dispW - xPt, wPt));
            hPt = Math.Max(1, Math.Min(dispH - yPt, hPt));

            _cropCanvasRect = new Rect(xPt / sx, yPt / sy, Math.Max(10, wPt / sx), Math.Max(10, hPt / sy));
            UpdateCropRectVisuals();
        }

        /// <summary>
        /// Parse a page-range string like "1-3,5,7-9" (1-based) into a zero-based index array.
        /// Returns <c>null</c> on parse error or if no valid pages are produced.
        /// </summary>
        private static int[]? ParsePageRange(string input, int pageCount)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var result = new System.Collections.Generic.HashSet<int>();
            foreach (var part in input.Split([','], StringSplitOptions.RemoveEmptyEntries))
            {
                var seg = part.Trim();
                if (seg.Contains('-'))
                {
                    var halves = seg.Split('-');
                    if (halves.Length == 2 &&
                        int.TryParse(halves[0].Trim(), out int lo) &&
                        int.TryParse(halves[1].Trim(), out int hi))
                    {
                        for (int p = lo; p <= hi; p++)
                            if (p >= 1 && p <= pageCount) result.Add(p - 1);
                    }
                    else return null;
                }
                else if (int.TryParse(seg, out int pg))
                {
                    if (pg >= 1 && pg <= pageCount) result.Add(pg - 1);
                }
                else return null;
            }
            return result.Count == 0 ? null : [.. result.OrderBy(x => x)];
        }

        // Entering the Crop tool drops a default crop box (inset from the page edges) and shows the bar
        // straight away, so the box is visible without the user having to draw one first.
        private void ShowDefaultCropBox()
        {
            if (_doc is null) return;
            int pi = PageList.SelectedIndex;
            if (pi < 0) return;
            var canvas = VisibleCanvasForPage(pi) ?? CanvasForPage(pi);
            if (canvas is null || canvas.Width <= 0 || canvas.Height <= 0) return;

            _cropPageIndex = pi;
            _activeCanvas  = canvas;
            _gestureCanvas = canvas;
            _gesturePage   = pi;

            double w = canvas.Width, h = canvas.Height;
            double mx = w * 0.08, my = h * 0.08;
            _cropCanvasRect = new Rect(mx, my, Math.Max(10, w - 2 * mx), Math.Max(10, h - 2 * my));

            _cropPreviewRect = new Rectangle
            {
                Stroke = Brushes.White, StrokeThickness = 1.5, StrokeDashArray = [5, 3],
                Fill = AccentBrush(55), Width = _cropCanvasRect.Width, Height = _cropCanvasRect.Height,
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 3, Opacity = 0.7 }
            };
            Canvas.SetLeft(_cropPreviewRect, _cropCanvasRect.X);
            Canvas.SetTop(_cropPreviewRect, _cropCanvasRect.Y);
            Panel.SetZIndex(_cropPreviewRect, 1);
            canvas.Children.Add(_cropPreviewRect);

            ShowCropConfirmBar();
        }

        // Rebuilds the crop bar (and its box) from the current rect so a language switch picks up the new
        // locale - the bar is built once with Loc() snapshots and would otherwise stay in the old language.
        // No-op if the bar isn't showing.
        private void RebuildCropBarForLocale()
        {
            if (_cropConfirmBar is null) return;
            var rect = _cropCanvasRect;
            var canvas = _activeCanvas;
            HideCropConfirmBar();
            if (canvas is null || canvas.Width <= 0 || rect.Width <= 1 || rect.Height <= 1) return;
            _cropCanvasRect = rect;
            _cropPreviewRect = new Rectangle
            {
                Stroke = Brushes.White, StrokeThickness = 1.5, StrokeDashArray = [5, 3],
                Fill = Brushes.Transparent, Width = rect.Width, Height = rect.Height,
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 3, Opacity = 0.7 }
            };
            Canvas.SetLeft(_cropPreviewRect, rect.X);
            Canvas.SetTop(_cropPreviewRect, rect.Y);
            Panel.SetZIndex(_cropPreviewRect, 1);
            canvas.Children.Add(_cropPreviewRect);
            ShowCropConfirmBar();
        }

        private void ShowCropConfirmBar()
        {
            if (_doc is null) return;
            if (_cropPreviewRect is not null)
            {
                // Committed box: outline only, but darker + a touch thicker so it reads on light scans.
                _cropPreviewRect.Fill = Brushes.Transparent;
                _cropPreviewRect.Stroke = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
                _cropPreviewRect.StrokeThickness = 2;
            }

            // Bar already up: do NOT rebuild it (that flickers it and wipes the Pages/All inputs). Just
            // refresh the corner handles and the X/Y/W/H fields so the values track the box.
            if (_cropConfirmBar is not null)
            {
                AddCropHandles();
                SyncCropBoxInputs();
                return;
            }

            int currentPage = _cropPageIndex >= 0 ? _cropPageIndex : PageList.SelectedIndex;

            // Build the bar exactly like the other annotate bars: a drag grip first, then the controls,
            // wrapped by the shared BuildBarHost and placed with PlaceAnnotationBar so it attaches to the top
            // and slides left/right like Draw/Text/Highlight - no bespoke host, grain, drag, or positioning.
            _annotBarDragInners.Clear();
            var outer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 8, 2), Background = Brushes.Transparent };
            var grip = MakeBarGrip();
            outer.Children.Add(grip);
            static Button CropBtn(Button b) { b.Padding = new Thickness(10, 4, 10, 4); b.Margin = new Thickness(0, 0, 5, 0); return b; }
            Brush Res(string key) => (Brush)FindResource(key);

            // label + themed field. Enter applies the crop; LostFocus just updates the rect from the values.
            TextBox AddField(string lbl, double width)
            {
                outer.Children.Add(new TextBlock
                {
                    Text = lbl, FontFamily = UiKit.UiFont, FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0),
                    Foreground = Res("MutedTextBrush")
                });
                var tb = new TextBox
                {
                    Width = width, Height = 22, FontFamily = UiKit.UiFont, FontSize = 11,
                    BorderThickness = new Thickness(1), Padding = new Thickness(3, 1, 3, 1),
                    VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0), Style = (Style)FindResource("FormFieldTextBox")
                };
                tb.SetResourceReference(TextBox.BackgroundProperty,  "PaneBrush");
                tb.SetResourceReference(TextBox.ForegroundProperty,  "TextBrush");
                tb.SetResourceReference(TextBox.BorderBrushProperty, "CardBorderBrush");
                tb.KeyDown   += (_, e) => { if (e.Key == Key.Enter) { CommitCropBoxInput(); ApplyCrop([currentPage]); e.Handled = true; } };
                tb.LostFocus += (_, _) => CommitCropBoxInput();
                outer.Children.Add(tb);
                return tb;
            }

            // Group labels (GIMP-style "Position" / "Size") so the single-letter fields read clearly.
            void GroupLabel(string t, double leftPad) => outer.Children.Add(new TextBlock
            {
                Text = t, FontFamily = UiKit.UiFont, FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = Res("MutedTextBrush"), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(leftPad, 0, 6, 0)
            });

            GroupLabel(Loc("Str_Crop_Position"), 0);
            _cropXBox = AddField("X", 50);
            _cropYBox = AddField("Y", 50);
            GroupLabel(Loc("Str_Crop_Size"), 6);   // padding after the Y box, before the size group
            _cropWBox = AddField("W", 50);
            _cropHBox = AddField("H", 50);

            // Unit picker (pt / in / %); re-formats the fields on change.
            var unitCombo = new ComboBox
            {
                Width = 54, Height = 22, Margin = new Thickness(0, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("DarkComboBox")
            };
            foreach (var u in new[] { "pt", "in", "%" }) unitCombo.Items.Add(u);
            unitCombo.SelectedItem = _cropUnit;
            unitCombo.SelectionChanged += (_, _) => { _cropUnit = unitCombo.SelectedItem as string ?? "pt"; SyncCropBoxInputs(); };
            outer.Children.Add(unitCombo);

            // Divider before the action buttons.
            var divider = new Border { Width = 1, Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Stretch };
            divider.SetResourceReference(Border.BackgroundProperty, "CardBorderBrush");
            outer.Children.Add(divider);

            // Pages range + "All" checkbox, then a single Crop button on the far right. Crop logic:
            // All checked -> every page; else a typed range like "1-3,5"; else just the current page.
            outer.Children.Add(new TextBlock
            {
                Text = Loc("Str_Crop_Pages"), FontFamily = UiKit.UiFont, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0),
                Foreground = Res("MutedTextBrush")
            });
            _cropRangeBox = new TextBox
            {
                Width = 64, Height = 22, FontFamily = UiKit.UiFont, FontSize = 11,
                BorderThickness = new Thickness(1), Padding = new Thickness(3, 1, 3, 1),
                VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0), ToolTip = Loc("Str_Crop_RangeTip"),
                Style = (Style)FindResource("FormFieldTextBox")
            };
            _cropRangeBox.SetResourceReference(TextBox.BackgroundProperty,  "PaneBrush");
            _cropRangeBox.SetResourceReference(TextBox.ForegroundProperty,  "TextBrush");
            _cropRangeBox.SetResourceReference(TextBox.BorderBrushProperty, "CardBorderBrush");
            outer.Children.Add(_cropRangeBox);

            // "All" checkbox - the same compact look as the annotate-bar toggles (no WPF CheckBox chrome).
            bool cropAll = false;
            var allTick = new TextBlock { Text = "✓", Foreground = Brushes.White, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            var allBox = new Border { Width = 15, Height = 15, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), BorderBrush = _swatchDimBorder, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0), Child = allTick };
            var allRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 10, 0), ToolTip = Loc("Str_Crop_AllTip") };
            allRow.Children.Add(allBox);
            allRow.Children.Add(new TextBlock { Text = Loc("Str_Crop_All"), FontFamily = UiKit.UiFont, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("MutedTextBrush") });
            allRow.MouseLeftButtonDown += (_, _) =>
            {
                cropAll = !cropAll;
                allTick.Visibility = cropAll ? Visibility.Visible : Visibility.Collapsed;
                if (cropAll) allBox.SetResourceReference(Border.BackgroundProperty, "SelectionAccent");
                else { allBox.Background = Brushes.Transparent; allBox.BorderBrush = _swatchDimBorder; }
            };
            outer.Children.Add(allRow);

            // Single Crop button on the right.
            var cropBtn = CropBtn(UiKit.Make(Loc("Str_Crop_Apply"), true));
            cropBtn.ToolTip = Loc("Str_TT_CropThisPage");
            cropBtn.Click += (_, _) =>
            {
                int pc = _doc?.PageCount ?? 0;
                int[]? pages;
                if (cropAll) pages = [.. Enumerable.Range(0, pc)];
                else if (!string.IsNullOrWhiteSpace(_cropRangeBox?.Text))
                {
                    pages = ParsePageRange(_cropRangeBox!.Text, pc);
                    if (pages is null) { SetStatus(Loc("Str_InvalidRange")); return; }
                }
                else pages = [currentPage];
                ApplyCrop(pages);
            };
            outer.Children.Add(cropBtn);

            // Wrap the controls in the shared bar host + frame and place it like the other annotate bars
            // (top, right-anchored, slidable via the grip; the X position persists across tools).
            var bar = new Border
            {
                BorderThickness     = new Thickness(1, 0, 1, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Top,
                CornerRadius        = new CornerRadius(0, 0, 4, 4),
                Padding             = new Thickness(4),
                Effect              = AnnotBarShadow(),
                Child               = BuildBarHost(outer)
            };
            bar.SetResourceReference(Border.BackgroundProperty,  "BgFlyout");
            bar.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
            _cropConfirmBar = bar;

            var previewArea = PagePreviewPanel.Parent as Grid;
            if (previewArea is not null)
            {
                Panel.SetZIndex(bar, 100);
                previewArea.Children.Add(bar);
                PlaceAnnotationBar(bar, grip, fadeIn: false);
            }
            _annotBarTool = EditTool.Crop;   // so re-clicking the Crop tool minimizes this bar like the others
            _annotBarMinimized = false;
            AddCropHandles();
            SyncCropBoxInputs();
        }

        private void HideCropConfirmBar()
        {
            if (_cropConfirmBar is not null)
            {
                // Remove from whichever panel it was added to (outer grid or canvas fallback)
                (_annotationCanvas.Parent as Panel)?.Children.Remove(_cropConfirmBar);
                _annotationCanvas.Children.Remove(_cropConfirmBar);   // no-op if not there
                (PagePreviewPanel.Parent as Panel)?.Children.Remove(_cropConfirmBar);
                _cropConfirmBar = null;
            }
            if (_cropPreviewRectBorder is not null)
            {
                (_cropPreviewRectBorder.Parent as Panel)?.Children.Remove(_cropPreviewRectBorder);
                _annotationCanvas.Children.Remove(_cropPreviewRectBorder);
                _cropPreviewRectBorder = null;
            }
            if (_cropPreviewRect is not null)
            {
                (_cropPreviewRect.Parent as Panel)?.Children.Remove(_cropPreviewRect);
                _annotationCanvas.Children.Remove(_cropPreviewRect);
                _cropPreviewRect = null;
            }
            RemoveCropHandles();
            _cropXBox = _cropYBox = _cropWBox = _cropHBox = null;
            _cropRangeBox = null;
            if (_annotBarTool == EditTool.Crop) _annotBarTool = null;   // release the shared annotate-bar slot
        }

        private void AddCropHandles()
        {
            RemoveCropHandles();
            const double hSize = 24;
            var tags    = new[] { "NW", "NE", "SE", "SW" };
            var cursors = new[] { Cursors.SizeNWSE, Cursors.SizeNESW, Cursors.SizeNWSE, Cursors.SizeNESW };
            // Handles live in the OUTER unscaled panel (same as the confirm bar) so they render
            // at a fixed screen size regardless of canvas zoom level.
            var outerGrid = PagePreviewPanel.Parent as Panel ?? (Panel)_annotationCanvas;

            for (int i = 0; i < 4; i++)
            {
                var tag = tags[i];
                var h = new Rectangle
                {
                    Width  = hSize, Height = hSize,
                    Fill   = Brushes.Transparent,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    StrokeThickness = 1.5,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                        { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 3, Opacity = 0.6 },
                    Tag    = tag,
                    Cursor = cursors[i],
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment   = VerticalAlignment.Top,
                };
                Panel.SetZIndex(h, 101);
                // Attach drag directly on the handle so clicks don't need to reach _annotationCanvas.
                h.MouseLeftButtonDown += (_, e) =>
                {
                    _activeCropHandleTag  = tag;
                    // Measure and capture against the active surface (per-page overlay in
                    // Continuous view) so the drag delta matches the crop rect's coordinate space.
                    _cropHandleDragStart  = e.GetPosition(_activeCanvas);
                    _cropRectAtHandleDrag = _cropCanvasRect;
                    _activeCanvas.CaptureMouse();
                    e.Handled = true;
                };
                _cropHandles.Add(h);
                outerGrid.Children.Add(h);
            }
            PositionCropHandles();
        }

        private void RemoveCropHandles()
        {
            var outerGrid = PagePreviewPanel.Parent as Panel ?? (Panel)_annotationCanvas;
            foreach (var h in _cropHandles)
            {
                outerGrid.Children.Remove(h);
                _annotationCanvas.Children.Remove(h); // belt-and-suspenders in case it ended up in canvas
            }
            _cropHandles.Clear();
            _activeCropHandleTag = null;
            RemoveCropBrackets();   // no-op - list is always empty now, kept for safety
        }

        private void RemoveCropBrackets()
        {
            foreach (var b in _cropBrackets) _annotationCanvas.Children.Remove(b);
            _cropBrackets.Clear();
        }

        private void PositionCropHandles()
        {
            if (_cropHandles.Count < 4) return;
            const double hSize = 24;
            var outerGrid = PagePreviewPanel.Parent as UIElement ?? _annotationCanvas;
            // Translate canvas-space corners to outer-panel screen space (same as RepositionCropConfirmBar).
            var canvasCorners = new Point[]
            {
                new(_cropCanvasRect.X,      _cropCanvasRect.Y),
                new(_cropCanvasRect.Right,   _cropCanvasRect.Y),
                new(_cropCanvasRect.Right,   _cropCanvasRect.Bottom),
                new(_cropCanvasRect.X,       _cropCanvasRect.Bottom),
            };
            var offsets = new (double dx, double dy)[]
            {
                (0,       0      ),   // NW: top-left at top-left corner
                (-hSize,  0      ),   // NE: top-right at top-right corner
                (-hSize, -hSize  ),   // SE: bottom-right at bottom-right corner
                (0,      -hSize  ),   // SW: bottom-left at bottom-left corner
            };
            for (int i = 0; i < 4; i++)
            {
                Point screen = _activeCanvas.TranslatePoint(canvasCorners[i], outerGrid);
                _cropHandles[i].Margin = new Thickness(
                    screen.X + offsets[i].dx,
                    screen.Y + offsets[i].dy,
                    0, 0);
            }
        }

        private void UpdateCropRectVisuals()
        {
            if (_cropPreviewRect is null) return;
            var r = _cropCanvasRect;
            Canvas.SetLeft(_cropPreviewRect, r.X); Canvas.SetTop(_cropPreviewRect, r.Y);
            _cropPreviewRect.Width = r.Width;       _cropPreviewRect.Height = r.Height;
            PositionCropHandles();
            SyncCropBoxInputs();
        }

        private void ApplyCrop(int[] pageIndices)
        {
            if (_doc is null || _currentFile is null) { SetStatus(Loc("Str_CropNoDoc")); return; }
            int currentPage = _cropPageIndex >= 0 ? _cropPageIndex : PageList.SelectedIndex;
            if (currentPage < 0) { SetStatus(Loc("Str_CropNoPage")); return; }
            if (!_renderDims.TryGetValue(currentPage, out var refDims))
            { SetStatus(Loc("Str_CropNoDims")); return; }

            try
            {
                PushDocUndo();

                // Convert canvas rect to PDF CropBox coords using the rotation-aware helper.
                // This is the correct inversion of how Docnet renders the rotated bitmap.
                _pageRotations.TryGetValue(currentPage, out int rot);
                var refPage = _doc.Pages[currentPage];
                double refPdfW = refPage.Width.Point;
                double refPdfH = refPage.Height.Point;

                var (rx1, ry1, rx2, ry2) = CanvasToPdfRect(
                    _cropCanvasRect, refPdfW, refPdfH, refDims.w, refDims.h, rot);

                foreach (int pi in pageIndices)
                {
                    if (pi < 0 || pi >= _doc.PageCount) continue;
                    var page  = _doc.Pages[pi];
                    double pW = page.Width.Point;
                    double pH = page.Height.Point;

                    // Scale proportionally when "All Pages" spans pages of different sizes
                    double x1 = rx1 * pW / refPdfW;
                    double y1 = ry1 * pH / refPdfH;
                    double x2 = rx2 * pW / refPdfW;
                    double y2 = ry2 * pH / refPdfH;

                    // Clamp to media box and ensure minimum 1-pt size
                    x1 = Math.Max(0, x1);  y1 = Math.Max(0, y1);
                    x2 = Math.Min(pW, x2); y2 = Math.Min(pH, y2);
                    if (x2 - x1 < 1) x2 = x1 + 1;
                    if (y2 - y1 < 1) y2 = y1 + 1;

                    // Write CropBox directly into the page dictionary (more reliable across
                    // PdfSharpCore versions than the CropBox property setter).
                    var cropArr = new PdfSharpCore.Pdf.PdfArray();
                    cropArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(x1));
                    cropArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(y1));
                    cropArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(x2));
                    cropArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(y2));
                    page.Elements["/CropBox"] = cropArr;

                    // Mirror to TrimBox (PDF spec: TrimBox within CropBox within MediaBox)
                    var trimArr = new PdfSharpCore.Pdf.PdfArray();
                    trimArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(x1));
                    trimArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(y1));
                    trimArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(x2));
                    trimArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(y2));
                    page.Elements["/TrimBox"] = trimArr;
                }

                HideCropConfirmBar();
                SetTool(EditTool.Select);
                SaveTempAndReload(keepAnnotations: true, preserveZoom: true);
                SetStatus(string.Format(Loc("Str_Cropped"), pageIndices.Length));
            }
            catch (Exception ex)
            {
                SetStatus(string.Format(Loc("Str_CropFailed"), ex.Message));
            }
        }

        private void RemoveCropBox(int[] pageIndices)
        {
            if (_doc is null || _currentFile is null) return;
            try
            {
                PushDocUndo();
                foreach (int pi in pageIndices)
                {
                    if (pi < 0 || pi >= _doc.PageCount) continue;
                    _doc.Pages[pi].Elements.Remove("/CropBox");
                    _doc.Pages[pi].Elements.Remove("/TrimBox");
                }
                HideCropConfirmBar();
                SetTool(EditTool.Select);
                SaveTempAndReload(keepAnnotations: true);
                SetStatus(string.Format(Loc("Str_RemovedCrop"), pageIndices.Length));
            }
            catch (Exception ex)
            {
                SetStatus(string.Format(Loc("Str_RemoveCropFailed"), ex.Message));
            }
        }
    }
}
