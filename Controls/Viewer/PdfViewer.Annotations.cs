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
    // Annotation rendering - draws the per-page annotation overlays (text, cover, highlight, ink,
    // signature, image). Moved from Shell/Annotations.cs; the namespace and class line are the only
    // changes. Window members spelled bare here resolve through PdfViewer.Bridge.cs.
    public partial class PdfViewer
    {
        private static double MeasureTextBoxHeight(string text, double width, double fontSize)
        {
            double inner = Math.Max(1, width - 4);   // minus left+right padding (2 + 2)
            var ft = new FormattedText(
                string.IsNullOrEmpty(text) ? " " : text,
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), Math.Max(1, fontSize), Brushes.Black, 1.0)
            { MaxTextWidth = inner };
            return Math.Ceiling(ft.Height) + 4;      // plus top + bottom padding
        }

        private void RenderTextAnnotation(TextAnnotation ta)
        {
            // A fixed W x H box: optional fill background, text wrapped to the width and clipped to the
            // height (so a free-form-resized box behaves like an image/crop frame).
            FontFamily famsel;
            try { famsel = new FontFamily(string.IsNullOrEmpty(ta.FontName) ? "Segoe UI" : ta.FontName); }
            catch { famsel = UiKit.UiFont; }
            var tb = new TextBlock
            {
                Text = ta.Content,
                Foreground = new SolidColorBrush(ta.GetColor()),
                FontFamily = famsel,
                FontWeight = ta.Bold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = ta.Italic ? FontStyles.Italic : FontStyles.Normal,
                TextDecorations = BuildDecorations(ta.Underline, ta.Strike),
                FontSize = ta.FontSize,
                Padding = new Thickness(2),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top
            };
            // Crisp glyphs: pixel-snapped layout + grayscale AA (ClearType can't subpixel on the
            // transparent overlay, and the default left the placed text looking aliased).
            TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(tb, TextRenderingMode.Grayscale);
            var box = new Border
            {
                Width = Math.Max(1, ta.Width),
                Height = Math.Max(1, ta.Height),
                Background = ta.HasFill ? new SolidColorBrush(ta.GetFill()) : Brushes.Transparent,
                ClipToBounds = true,
                IsHitTestVisible = false,
                Child = tb
            };
            Canvas.SetLeft(box, ta.Position.X);
            Canvas.SetTop(box, ta.Position.Y);
            _activeCanvas.Children.Add(box);
        }

        // HighlightEraseGeometry (shared by on-screen rendering and PDF export) lives in
        // Services/PdfBurn.cs with the rest of the burn-to-document core.

        internal void RenderAllAnnotations(int pageIndex)
        {
            // Resolve this page's annotation surface from the unified per-page overlay map, which
            // every multi-page view populates; fall back to the single-page canvas. View-mode
            // independent on purpose so the tools behave identically in all four modes.
            _activeCanvas = CanvasForPage(pageIndex);
            _activeCanvas.Children.Clear();

            RenderStamps(pageIndex);   // stamp layer (page numbers / watermark) sits beneath annotations

            if (_annotations.TryGetValue(pageIndex, out var annotList))
            foreach (var annot in annotList)
            {
                switch (annot)
                {
                    case TextAnnotation ta:
                        RenderTextAnnotation(ta);
                        break;
                    case CoverAnnotation cov:
                        var covRect = new Rectangle
                        {
                            Fill = new SolidColorBrush(cov.GetColor()),
                            Width = cov.Bounds.Width, Height = cov.Bounds.Height
                        };
                        // While being typed into, dash-outline the cover so it's visible behind the live
                        // text box. Otherwise just the opaque fill - its outline only appears on selection
                        // (drawn as selection chrome), so a deselected cover stays clean. Screen-only; the
                        // flattened/saved PDF draws just the fill.
                        if (ReferenceEquals(cov, _pendingCover))
                        {
                            covRect.Stroke = DarkerAccentBrush();
                            covRect.StrokeThickness = 1;
                            covRect.StrokeDashArray = [4, 3];
                        }
                        Canvas.SetLeft(covRect, cov.Bounds.X);
                        Canvas.SetTop(covRect, cov.Bounds.Y);
                        _activeCanvas.Children.Add(covRect);
                        break;
                    case HighlightAnnotation ha:
                        if (PdfBurn.HighlightEraseGeometry(ha) is { } hgeo)
                        {
                            // Carved highlight: rectangle minus the eraser strokes, one anti-aliased fill.
                            _activeCanvas.Children.Add(new System.Windows.Shapes.Path
                            { Fill = new SolidColorBrush(ha.GetColor()), Data = hgeo, IsHitTestVisible = false });
                        }
                        else
                        {
                            var hr = ha.DrawRect();
                            var rect = new Rectangle
                            {
                                Fill = new SolidColorBrush(ha.GetColor()),
                                Width = hr.Width,
                                Height = hr.Height
                            };
                            Canvas.SetLeft(rect, hr.X);
                            Canvas.SetTop(rect, hr.Y);
                            _activeCanvas.Children.Add(rect);
                        }
                        break;
                    case InkAnnotation ia:
                        if (ia.Points.Count < 2) continue;
                        if (ia.HasFill)
                        {
                            // Filled shape (#127 Phase 3): closed outline - one Polygon, filled + stroked.
                            var pg = new Polygon
                            {
                                Stroke = new SolidColorBrush(ia.GetColor()),
                                StrokeThickness = ia.StrokeWidth,
                                StrokeLineJoin = PenLineJoin.Round,
                                Fill = new SolidColorBrush(ia.GetFillColor())
                            };
                            foreach (var pt in ia.Points) pg.Points.Add(pt);
                            _activeCanvas.Children.Add(pg);
                            break;
                        }
                        var poly = new Polyline
                        {
                            Stroke = new SolidColorBrush(ia.GetColor()),
                            StrokeThickness = ia.StrokeWidth,
                            StrokeLineJoin = PenLineJoin.Round,
                            StrokeStartLineCap = PenLineCap.Round,
                            StrokeEndLineCap = PenLineCap.Round
                        };
                        foreach (var pt in ia.Points) poly.Points.Add(pt);
                        _activeCanvas.Children.Add(poly);
                        break;
                    case SignatureAnnotation sa:
                        if (sa.ImageData is not null)
                        {
                            // Image-based signature (decoded once, then cached on the annotation)
                            var bmp = GetAnnotationBitmap(sa, sa.ImageData);
                            if (bmp != null)
                            {
                                var imgCtrl = new System.Windows.Controls.Image
                                {
                                    Source = bmp,
                                    Width = sa.SourceWidth * sa.Scale,
                                    Height = sa.SourceHeight * sa.Scale,
                                    Stretch = System.Windows.Media.Stretch.Uniform,
                                    IsHitTestVisible = false
                                };
                                Canvas.SetLeft(imgCtrl, sa.Position.X);
                                Canvas.SetTop(imgCtrl, sa.Position.Y);
                                _activeCanvas.Children.Add(imgCtrl);
                            }
                        }
                        else
                        {
                            foreach (var stroke in sa.Strokes)
                            {
                                if (stroke.Count < 2) continue;
                                var sigPoly = new Polyline
                                {
                                    Stroke = Brushes.Black,
                                    StrokeThickness = sa.StrokeWidth * sa.Scale,
                                    StrokeLineJoin = PenLineJoin.Round,
                                    StrokeStartLineCap = PenLineCap.Round,
                                    StrokeEndLineCap = PenLineCap.Round
                                };
                                foreach (var pt in stroke)
                                    sigPoly.Points.Add(new Point(
                                        sa.Position.X + pt.X * sa.Scale,
                                        sa.Position.Y + pt.Y * sa.Scale));
                                _activeCanvas.Children.Add(sigPoly);
                            }
                        }
                        break;

                    case ImageAnnotation ia:
                        var iaBmp = GetAnnotationBitmap(ia, ia.ImageData);
                        if (iaBmp != null)
                        {
                            var iaCtrl = new System.Windows.Controls.Image
                            {
                                Source = iaBmp,
                                Width = ia.SourceWidth * ia.Scale,
                                Height = ia.SourceHeight * ia.Scale,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                IsHitTestVisible = false
                            };
                            Canvas.SetLeft(iaCtrl, ia.Position.X);
                            Canvas.SetTop(iaCtrl, ia.Position.Y);
                            _activeCanvas.Children.Add(iaCtrl);
                        }
                        break;
                }
            }

            // Re-add form field overlays - RenderAllAnnotations clears the canvas so they must be restored.
            if (_renderDims.TryGetValue(pageIndex, out var dims))
                RenderFormFields(pageIndex, dims.w, dims.h);

            // Search highlights live on this same canvas and were wiped by the clear above; repaint
            // them last so they sit on top and survive every re-render and continuous scroll.
            ApplySearchHighlights(pageIndex, _activeCanvas);
            // Flowing text selection quads (#127) live here too - same deal, repaint last.
            ApplyTextSelectionQuads(pageIndex, _activeCanvas);
        }

        // Decode a placed annotation's Base64 image once and cache the frozen result on the annotation,
        // so repeated renders (e.g. every mousemove of a resize-drag) reuse it instead of re-decoding.
        private static System.Windows.Media.Imaging.BitmapSource? GetAnnotationBitmap(PlacedAnnotation a, string? data)
        {
            if (a.CachedBitmap != null) return a.CachedBitmap;
            if (string.IsNullOrEmpty(data)) return null;
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new System.IO.MemoryStream(Convert.FromBase64String(data));
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                a.CachedBitmap = bmp;
                return bmp;
            }
            catch { return null; }
        }

        internal void ClearSelection()
        {
            if (_selectionBorder is not null)
            {
                (_selectionBorder.Parent as Canvas)?.Children.Remove(_selectionBorder);
                _selectionBorder = null;
            }
            foreach (var hd in _resizeHandles)
                (hd.Parent as Canvas)?.Children.Remove(hd);
            _resizeHandles.Clear();
            if (_pairedCoverOutline is not null)
            {
                (_pairedCoverOutline.Parent as Canvas)?.Children.Remove(_pairedCoverOutline);
                _pairedCoverOutline = null;
            }
            ClearMultiSelection();
            _isResizingSig = false;
            _resizeSigAnnot = null;
            _resizeTextAnnot = null;
            _resizeHlAnnot = null;
            _resizeInkAnnot = null;
            _resizeInkOrigPoints = null;
            _isDraggingAnnot = false;
            _dragAnnot = null;
            _dragGroupOrig.Clear();
            _selectedAnnotation = null;
            // If the text bar was opened for a now-cleared text-box selection (not because the Text tool
            // is active), close it again.
            if (_currentTool != EditTool.Text && _annotBarTool == EditTool.Text)
                HideTextSettings();
            // Likewise the draw bar: if it was opened to edit a selected highlight / line / ink
            // annotation (the active tool isn't a draw-family tool), close it when the selection clears.
            if (_currentTool is not (EditTool.Draw or EditTool.Highlight or EditTool.Strikethrough or EditTool.Underline)
                && _annotBarTool is EditTool.Draw or EditTool.Highlight or EditTool.Strikethrough or EditTool.Underline)
                HideDrawSettings();
        }

        // ---- Shift+click multi-selection (Select tool) -------------------------------------------

        /// <summary>Removes every shift-selection outline and empties the multi-selection set.</summary>
        private void ClearMultiSelection()
        {
            foreach (var o in _selectionOutlines)
                (o.Parent as Canvas)?.Children.Remove(o);
            _selectionOutlines.Clear();
            _selectedSet.Clear();
        }

        /// <summary>The overlay canvas that hosts a given page's annotations. Reads the unified page
        /// map (primary included); falls back to the primary canvas only for a page with no tile.</summary>
        private Canvas CanvasForPage(int pageIndex)
            => _pages.TryGetValue(pageIndex, out var c) ? c : _annotationCanvas;

        /// <summary>The overlay for a page that is actually on screen right now, or null when the page
        /// has no live tile (so callers don't paint a page's content onto the wrong canvas). The
        /// unified map includes the primary, so there's no primary special case here anymore.</summary>
        private Canvas? VisibleCanvasForPage(int pageIndex)
            => _pages.TryGetValue(pageIndex, out var c) ? c : null;

        /// <summary>Every page overlay currently in the visual tree (primary + per-page tiles).</summary>
        private IEnumerable<Canvas> AllPageCanvases()
        {
            yield return _annotationCanvas;
            foreach (var c in _pages.Values)
                if (!ReferenceEquals(c, _annotationCanvas)) yield return c;
        }

        /// <summary>Total annotations currently selected (primary + shift-selected set).</summary>
        private int SelectionCount()
            => _selectedSet.Count
             + (_selectedAnnotation is not null && !_selectedSet.Contains(_selectedAnnotation) ? 1 : 0);

        /// <summary>Draws a selection outline for a shift-selected annotation on its page canvas.</summary>
        private void AddSelectionOutline(Rect bounds, Canvas canvas)
        {
            // Match SelectAnnotation: counter a continuous/grid overlay's LayoutTransform so the
            // outline keeps a constant on-screen thickness regardless of the tile scale.
            double inv = 1.0;
            if (canvas.LayoutTransform is ScaleTransform st && st.ScaleX > 0.0001)
                inv = 1.0 / st.ScaleX;
            var outline = new Border
            {
                BorderBrush     = AccentBrush(),
                BorderThickness = new Thickness(2 * inv),
                Background      = AccentBrush(40),
                Width           = bounds.Width + 8,
                Height          = bounds.Height + 8,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(outline, bounds.X - 4);
            Canvas.SetTop(outline, bounds.Y - 4);
            canvas.Children.Add(outline);
            _selectionOutlines.Add(outline);
        }

        /// <summary>
        /// Shift+click handler: toggle an annotation in or out of the multi-selection. The first
        /// shift+click folds any existing single selection into the set so nothing is lost.
        /// </summary>
        private void ToggleMultiSelect(PageAnnotation annot, Rect bounds, Canvas canvas)
        {
            // Fold an existing single (primary) selection into the set so it stays selected.
            if (_selectedAnnotation is not null && !_selectedSet.Contains(_selectedAnnotation))
            {
                var prim = _selectedAnnotation;
                // Drop the single-selection chrome (border + resize handles) but keep the annotation.
                if (_selectionBorder is not null)
                {
                    (_selectionBorder.Parent as Canvas)?.Children.Remove(_selectionBorder);
                    _selectionBorder = null;
                }
                foreach (var hd in _resizeHandles)
                    (hd.Parent as Canvas)?.Children.Remove(hd);
                _resizeHandles.Clear();
                _selectedAnnotation = null;
                _selectedSet.Add(prim);
                AddSelectionOutline(AnnotBounds(prim), CanvasForPage(prim.PageIndex));
            }

            if (_selectedSet.Remove(annot))
            {
                // Was selected -> rebuild the outlines without it.
                foreach (var o in _selectionOutlines)
                    (o.Parent as Canvas)?.Children.Remove(o);
                _selectionOutlines.Clear();
                foreach (var a in _selectedSet)
                    AddSelectionOutline(AnnotBounds(a), CanvasForPage(a.PageIndex));
            }
            else
            {
                _selectedSet.Add(annot);
                AddSelectionOutline(bounds, canvas);
            }

            int n = SelectionCount();
            SetStatus(n == 0 ? "Selection cleared"
                             : $"{n} annotations selected - press Delete to remove");
        }

        private void DeleteSelected()
        {
            // Gather the primary selection plus any shift-selected annotations, de-duplicated.
            var toDelete = new List<PageAnnotation>();
            if (_selectedAnnotation is not null) toDelete.Add(_selectedAnnotation);
            foreach (var a in _selectedSet)
                if (!toDelete.Contains(a)) toDelete.Add(a);
            if (toDelete.Count == 0) return;
            PushPagesSnapshotUndo(toDelete.Select(a => a.PageIndex));

            var pages = new HashSet<int>();
            foreach (var a in toDelete)
                if (_annotations.TryGetValue(a.PageIndex, out var list) && list.Remove(a))
                    pages.Add(a.PageIndex);

            // If a paired replacement text was deleted, unpair its cover so it stops rendering dashed and
            // becomes a plain solid box (the original "two fields" hint is gone, just the cover remains).
            foreach (var a in toDelete)
                if (a is TextAnnotation t && t.PairId.Length > 0 && _annotations.TryGetValue(t.PageIndex, out var pl))
                    foreach (var cov in pl.OfType<CoverAnnotation>())
                        if (cov.PairId == t.PairId) cov.PairId = "";

            ClearSelection();
            foreach (var p in pages) RenderAllAnnotations(p);
            SetStatus(toDelete.Count == 1
                ? "Deleted selected annotation"
                : $"Deleted {toDelete.Count} annotations");
        }

        private bool HitTestAnnotation(PageAnnotation annot, Point pos, out Rect bounds)
        {
            switch (annot)
            {
                case CoverAnnotation cov:
                    // A text cover gets a forgiving grab margin (and sits below its text, which is checked
                    // first), so you can click the colored background to select/move it without hunting.
                    bounds = cov.Bounds;
                    var coverHit = bounds; coverHit.Inflate(6, 6);
                    return coverHit.Contains(pos);

                case HighlightAnnotation ha:
                    bounds = ha.Bounds;
                    return bounds.Contains(pos);

                case TextAnnotation ta:
                    bounds = new Rect(ta.Position.X, ta.Position.Y,
                                      Math.Max(8, ta.Width), Math.Max(8, ta.Height));
                    // Forgiving grab area: clicking anywhere in the box - plus a small margin around it,
                    // since a one-line box is a thin band - selects and drags the text, so you don't have
                    // to land on a glyph. The selection loop still checks topmost-first, so any annotation
                    // stacked above the text takes the click before this does (no conflict).
                    var textHit = bounds; textHit.Inflate(8, 10);
                    return textHit.Contains(pos);

                case InkAnnotation ia when ia.Points.Count > 0:
                    bool near = ia.Points.Any(p =>
                        Math.Sqrt((p.X - pos.X) * (p.X - pos.X) + (p.Y - pos.Y) * (p.Y - pos.Y)) < 15);
                    // Also hit anywhere along each segment, so a straight line is grabbable along its whole
                    // length (not just its two endpoints) and freehand strokes select between sample points.
                    for (int si = 0; si < ia.Points.Count - 1 && !near; si++)
                        if (DistPointToSegment(pos, ia.Points[si], ia.Points[si + 1]) < 10) near = true;
                    if (near)
                    {
                        double minX = ia.Points.Min(p => p.X);
                        double minY = ia.Points.Min(p => p.Y);
                        double maxX = ia.Points.Max(p => p.X);
                        double maxY = ia.Points.Max(p => p.Y);
                        bounds = new Rect(minX, minY, Math.Max(maxX - minX, 4), Math.Max(maxY - minY, 4));
                        return true;
                    }
                    bounds = Rect.Empty;
                    return false;

                case SignatureAnnotation sa:
                    double sigW = sa.SourceWidth * sa.Scale;
                    double sigH = sa.SourceHeight * sa.Scale;
                    bounds = new Rect(sa.Position.X, sa.Position.Y, sigW, sigH);
                    return bounds.Contains(pos);

                case ImageAnnotation ia:
                    double iaW = ia.SourceWidth * ia.Scale;
                    double iaH = ia.SourceHeight * ia.Scale;
                    bounds = new Rect(ia.Position.X, ia.Position.Y, iaW, iaH);
                    return bounds.Contains(pos);

                default:
                    bounds = Rect.Empty;
                    return false;
            }
        }

        // Recolor any live selection / crop visuals to the current theme's SelectionAccent.
        // Their brushes are plain (not resource references), so a theme swap won't update them
        // until reselected unless we repaint them here.
        private void RefreshSelectionAccent()
        {
            if (_selectionBorder is not null)
            {
                _selectionBorder.BorderBrush = AccentBrush();
                _selectionBorder.Background  = AccentBrush(40);
            }
            foreach (var hd in _resizeHandles)
                hd.Fill = AccentBrush();
            if (_cropPreviewRect is not null)
                _cropPreviewRect.Fill = AccentBrush(55);
        }

        // Positions the four corner handles around an annotation's bounds (top-left x,y and size w,h).
        private void LayoutResizeHandles(double x, double y, double w, double h)
        {
            foreach (var hd in _resizeHandles)
            {
                double hs = hd.Width;
                (double cx, double cy) = (hd.Tag as string) switch
                {
                    "NW" => (x,     y),
                    "NE" => (x + w, y),
                    "SW" => (x,     y + h),
                    _    => (x + w, y + h)   // SE
                };
                Canvas.SetLeft(hd, cx - hs / 2);
                Canvas.SetTop(hd, cy - hs / 2);
            }
        }

        private void SelectAnnotation(PageAnnotation annot, Rect bounds)
        {
            _selectedAnnotation = annot;
            // Recovery: if an annotation's corners drifted off-page (placed before the on-page guard),
            // pull it back on selection so its handles become reachable again. Only acts when it's
            // actually off-page, and re-derives bounds for the selection visuals below.
            if (IsDraggable(annot))
            {
                var onPage = ClampAnnotPos(annot);
                if (onPage != AnnotGetPos(annot))
                {
                    AnnotSetPos(annot, onPage);
                    RenderAllAnnotations(annot.PageIndex);
                    bounds = AnnotBounds(annot);
                    MarkDirty();
                }
            }
            // Continuous-view overlays are scaled down by their LayoutTransform, which would
            // shrink the selection outline and resize handle to near-invisibility. Compensate
            // so they render at the same on-screen size as single-page view.
            double inv = 1.0;
            if (_activeCanvas.LayoutTransform is ScaleTransform _selScale && _selScale.ScaleX > 0.0001)
                inv = 1.0 / _selScale.ScaleX;
            // A cover gets a darker accent so its handles/border stand apart from the text box over it.
            bool isCover = annot is CoverAnnotation;
            var selBrush = isCover ? DarkerAccentBrush() : AccentBrush();
            _selectionBorder = new Border
            {
                BorderBrush = selBrush,
                BorderThickness = new Thickness(2 * inv),
                Background = isCover ? DarkerAccentBrush(40) : AccentBrush(40),
                Width = bounds.Width + 8,
                Height = bounds.Height + 8,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(_selectionBorder, bounds.X - 4);
            Canvas.SetTop(_selectionBorder, bounds.Y - 4);
            _activeCanvas.Children.Add(_selectionBorder);

            // Selecting EITHER paired box dash-outlines its partner so both boxes stay visible: clicking
            // the cover keeps the text box shown, and vice versa. Cleared in ClearSelection.
            PageAnnotation? partner = null;
            if (annot.PairId.Length > 0 && _annotations.TryGetValue(annot.PageIndex, out var ppl))
                partner = annot is CoverAnnotation
                    ? ppl.OfType<TextAnnotation>().FirstOrDefault(t => t.PairId == annot.PairId)
                    : ppl.OfType<CoverAnnotation>().FirstOrDefault(c => c.PairId == annot.PairId);
            if (partner is not null)
            {
                var pb = AnnotBounds(partner);
                _pairedCoverOutline = new Rectangle
                {
                    Width = pb.Width + 4, Height = pb.Height + 4,
                    Stroke = DarkerAccentBrush(), StrokeThickness = 1.5 * inv,
                    StrokeDashArray = [4, 3],
                    Fill = Brushes.Transparent, IsHitTestVisible = false
                };
                Canvas.SetLeft(_pairedCoverOutline, pb.X - 2);
                Canvas.SetTop(_pairedCoverOutline, pb.Y - 2);
                _activeCanvas.Children.Add(_pairedCoverOutline);
            }

            // Add four corner resize handles for resizable annotations (signature, image, text box,
            // highlight/strikethrough/underline, and ink).
            if (annot is PlacedAnnotation or TextAnnotation or HighlightAnnotation or InkAnnotation)
            {
                double hSize = 14 * inv;
                _resizeHandles.Clear();
                foreach (string tag in new[] { "NW", "NE", "SE", "SW" })
                {
                    var hd = new Rectangle
                    {
                        Width = hSize, Height = hSize,
                        Fill = selBrush,
                        Stroke = Brushes.White, StrokeThickness = 1 * inv,
                        Cursor = (tag is "NW" or "SE") ? Cursors.SizeNWSE : Cursors.SizeNESW,
                        IsHitTestVisible = true,
                        Tag = tag
                    };
                    _resizeHandles.Add(hd);
                    _activeCanvas.Children.Add(hd);
                }
                LayoutResizeHandles(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                string label = annot switch
                {
                    SignatureAnnotation => "Signature",
                    ImageAnnotation     => "Image",
                    TextAnnotation      => "Text box",
                    CoverAnnotation     => "Text cover",
                    HighlightAnnotation { Style: HighlightStyle.Strikethrough } => "Strikethrough",
                    HighlightAnnotation { Style: HighlightStyle.Underline }     => "Underline",
                    HighlightAnnotation => "Highlight",
                    InkAnnotation       => "Drawing",
                    _                   => "Item"
                };
                string how = annot is TextAnnotation
                    ? "drag a side corner to set width, double-click to edit, Delete to remove"
                    : "drag any corner to resize, Delete to remove";
                SetStatus($"{label} selected - {how}");
            }
            else
            {
                SetStatus($"Selected {annot.GetType().Name.Replace("Annotation", "").ToLower()} annotation - press Delete to remove");
            }

            // Selecting a text box opens the text bar (synced to that box) so its color, fill and size
            // can be changed without re-typing. The bar's swatches/sliders then apply to the selection.
            if (annot is TextAnnotation tsel)
            {
                var col = tsel.GetColor();
                _textColor = col;
                _textOpacity = col.A;
                _textFillColor = tsel.GetFill();
                double sy = 1.0;
                if (_doc is not null && _renderDims.TryGetValue(tsel.PageIndex, out var rd) && rd.h > 0)
                    sy = _doc.Pages[tsel.PageIndex].Height.Point / rd.h;
                _textFontSize = Math.Max(1, Math.Round(tsel.FontSize * sy));
                ShowTextSettings();
            }
            // Selecting a highlight / strikethrough / underline opens the draw bar synced to it, so its
            // color and opacity can be edited in place. The annotation's style picks the matching tool.
            else if (annot is HighlightAnnotation hsel)
            {
                if (hsel.Style == HighlightStyle.Fill) _highlightColor = hsel.GetColor();
                else                                   _lineAnnotColor = hsel.GetColor();
                ShowDrawSettings(hsel.Style switch
                {
                    HighlightStyle.Strikethrough => EditTool.Strikethrough,
                    HighlightStyle.Underline     => EditTool.Underline,
                    _                            => EditTool.Highlight
                });
            }
            // Selecting a freehand stroke opens the draw bar synced to it (color, opacity and width).
            else if (annot is InkAnnotation isel)
            {
                _drawColor   = isel.GetColor();
                _drawOpacity = isel.GetColor().A;
                _drawWidth   = isel.StrokeWidth;
                ShowDrawSettings(EditTool.Draw);
            }
        }

        private static bool IsDraggable(PageAnnotation a) => a is PlacedAnnotation or TextAnnotation or HighlightAnnotation or InkAnnotation;

        // Shortest distance from point p to the segment a-b. Used to hit-test straight lines and ink
        // strokes along their length, not just at their sample points.
        private static double DistPointToSegment(Point p, Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-9) { dx = p.X - a.X; dy = p.Y - a.Y; return Math.Sqrt(dx * dx + dy * dy); }
            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
            t = Math.Max(0, Math.Min(1, t));
            double qx = a.X + t * dx, qy = a.Y + t * dy;
            double ex = p.X - qx, ey = p.Y - qy;
            return Math.Sqrt(ex * ex + ey * ey);
        }
        private static Point AnnotGetPos(PageAnnotation a) => a switch
        {
            PlacedAnnotation p => p.Position,
            TextAnnotation t   => t.Position,
            HighlightAnnotation h => h.Bounds.Location,
            // Ink has no single origin; use the stroke's bounding-box top-left.
            InkAnnotation ink when ink.Points.Count > 0
                => new Point(ink.Points.Min(p => p.X), ink.Points.Min(p => p.Y)),
            _                  => default
        };
        private static void AnnotSetPos(PageAnnotation a, Point pos)
        {
            switch (a)
            {
                case PlacedAnnotation p: p.Position = pos; break;
                case TextAnnotation t:   t.Position = pos; break;
                case HighlightAnnotation h:
                {
                    double hdx = pos.X - h.Bounds.X, hdy = pos.Y - h.Bounds.Y;
                    h.Bounds = new Rect(pos, h.Bounds.Size);
                    if (h.Erases is { } hes)
                        foreach (var e in hes)
                            for (int i = 0; i < e.Points.Count; i++)
                                e.Points[i] = new Point(e.Points[i].X + hdx, e.Points[i].Y + hdy);
                    break;
                }
                case InkAnnotation ink when ink.Points.Count > 0:
                    // Move the whole stroke by the delta from its current bounding-box origin.
                    double ox = ink.Points.Min(p => p.X), oy = ink.Points.Min(p => p.Y);
                    double dx = pos.X - ox, dy = pos.Y - oy;
                    for (int i = 0; i < ink.Points.Count; i++)
                        ink.Points[i] = new Point(ink.Points[i].X + dx, ink.Points[i].Y + dy);
                    break;
            }
        }
        private Rect AnnotBounds(PageAnnotation a)
        {
            // Ink isn't a simple rect in HitTestAnnotation (it's a proximity test), so derive its bounds
            // from the stroke points; everything else reuses HitTestAnnotation's out-bounds via a far probe.
            if (a is InkAnnotation ia)
            {
                if (ia.Points.Count == 0) return Rect.Empty;
                double minX = ia.Points.Min(p => p.X), minY = ia.Points.Min(p => p.Y);
                double maxX = ia.Points.Max(p => p.X), maxY = ia.Points.Max(p => p.Y);
                return new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
            }
            HitTestAnnotation(a, new Point(double.MinValue, double.MinValue), out Rect b);
            return b;
        }

        // Draws the selection marquee on the top-most MarqueeLayer (above every page) so a drag can span
        // pages. Maps the two corners from the start page's canvas into the layer's coordinate space.
        internal void UpdateMarquee(Point startInStartCanvas, Point currentInStartCanvas)
        {
            var gc = _gestureCanvas ?? _activeCanvas;
            if (_selectRect is null || gc is null) return;
            var t = gc.TransformToVisual(MarqueeLayer);
            Point a = t.Transform(startInStartCanvas), b = t.Transform(currentInStartCanvas);
            Canvas.SetLeft(_selectRect, Math.Min(a.X, b.X));
            Canvas.SetTop(_selectRect, Math.Min(a.Y, b.Y));
            _selectRect.Width = Math.Abs(a.X - b.X);
            _selectRect.Height = Math.Abs(a.Y - b.Y);
        }

        // Maps a rectangle from one visual's coordinate space into another's, for cross-page hit-testing.
        private static Rect MapRect(UIElement from, System.Windows.Media.Visual to, Rect r)
        {
            var t = from.TransformToVisual(to);
            return new Rect(t.Transform(new Point(r.Left, r.Top)), t.Transform(new Point(r.Right, r.Bottom)));
        }

        // Constrains a rectangle (annotation-canvas coordinates) to the page so its corners can't land
        // off the page, where resize handles become unreachable. The page occupies (0,0)-(w,h) in the
        // same coordinate space as annotations, taken from _renderDims. A box bigger than the page is
        // pinned to the top-left rather than shrunk.
        private Rect ClampRectToPage(int pageIdx, Rect r)
        {
            if (!_renderDims.TryGetValue(pageIdx, out var d)) return r;
            double pw = d.w, ph = d.h;
            double x = Math.Max(0, Math.Min(r.X, pw - r.Width));
            double y = Math.Max(0, Math.Min(r.Y, ph - r.Height));
            return new Rect(x, y, r.Width, r.Height);
        }

        // Clamps an annotation's position so its whole bounding box stays on its page. Returns the
        // top-left to feed back through AnnotSetPos (which knows how to move each annotation type).
        private Point ClampAnnotPos(PageAnnotation a)
        {
            var b = AnnotBounds(a);
            return ClampRectToPage(a.PageIndex, b).Location;
        }

        // Clamps a point to the page rectangle. Used during resize so a dragged corner can't leave the
        // page (with the opposite corner already on-page, that keeps the whole box on-page).
        private Point ClampPointToPage(int pageIdx, Point p)
            => _renderDims.TryGetValue(pageIdx, out var d)
                ? new Point(Math.Max(0, Math.Min(p.X, d.w)), Math.Max(0, Math.Min(p.Y, d.h)))
                : p;

        // Returns the page index + canvas under the mouse across every per-page overlay
        // (grid / two-page / continuous tiles) and the primary page canvas. Used to drop a placed
        // annotation onto a different page than the one it started on.
        private (int page, Canvas canvas)? PageCanvasUnderPointer(MouseEventArgs e)
        {
            foreach (var kv in _continuousCanvases)
            {
                var c = kv.Value;
                if (c.ActualWidth <= 0 || c.ActualHeight <= 0) continue;
                var p = e.GetPosition(c);
                if (p.X >= 0 && p.X <= c.ActualWidth && p.Y >= 0 && p.Y <= c.ActualHeight)
                    return (kv.Key, c);
            }
            if (_annotationCanvas.ActualWidth > 0 && _annotationCanvas.ActualHeight > 0)
            {
                var pp = e.GetPosition(_annotationCanvas);
                if (pp.X >= 0 && pp.X <= _annotationCanvas.ActualWidth &&
                    pp.Y >= 0 && pp.Y <= _annotationCanvas.ActualHeight)
                    return (_viewMode == ViewMode.Grid ? 0 : PageList.SelectedIndex, _annotationCanvas);
            }
            return null;
        }

        internal void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_doc is null) return;
            if (sender is Canvas srcCanvas) _activeCanvas = srcCanvas;
            // A click on a live text-edit corner handle starts a free-form resize. Checked FIRST, before
            // the "click inside the editing box" guard below: that guard tests OriginalSource, which is
            // unreliable across the nested transparent canvases, so it can otherwise swallow a corner
            // click right after placement. This is a reliable position-based hit test.
            if (_textEditHandles.Count > 0 && _tehBox is not null)
            {
                var hpos = e.GetPosition(_activeCanvas);
                string? corner = TextEditHandleAt(hpos);
                if (corner is not null)
                {
                    _tehCorner = corner;
                    double bx = Canvas.GetLeft(_tehBox), by = Canvas.GetTop(_tehBox);
                    double bw = _tehBox.ActualWidth  > 0 ? _tehBox.ActualWidth  : _tehBox.Width;
                    double bh = _tehBox.ActualHeight > 0 ? _tehBox.ActualHeight : Math.Max(_tehBox.MinHeight, 24);
                    _tehAnchor = _tehCorner switch
                    {
                        "NW" => new Point(bx + bw, by + bh),
                        "NE" => new Point(bx,      by + bh),
                        "SW" => new Point(bx + bw, by),
                        _    => new Point(bx,      by)   // SE
                    };
                    _draggingTextEditHandle = true;
                    _gestureCanvas = _activeCanvas;
                    _gesturePage   = _activeCanvas.Tag is int tp ? tp : PageList.SelectedIndex;
                    _activeCanvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }
            }
            // Don't intercept clicks on an active text editing box
            if (_activeTextBox is not null && e.OriginalSource is DependencyObject src &&
                IsDescendantOf(src, _activeTextBox))
                return;
            // Don't intercept clicks on the crop confirm bar (canvas uses Preview events which
            // tunnel before child Button clicks fire - we must not swallow them here).
            if (_cropConfirmBar is not null && e.OriginalSource is DependencyObject cropSrc &&
                IsDescendantOf(cropSrc, _cropConfirmBar))
                return;
            // Don't intercept clicks on form field overlay controls (TextBox, CheckBox, etc.)
            // - WPF must handle those natively so focus, toggling, and text entry work.
            if (e.OriginalSource is DependencyObject formSrc && IsFormFieldElement(formSrc))
                return;
            // Check if click lands inside a PDF link overlay.
            // We do an explicit bounds check rather than relying on WPF hit-testing through
            // nested transparent canvases, which is unreliable. Links are only followed with the
            // Select tool (so drawing/typing over a link region edits instead of navigating), and
            // never when the click is on an annotation - annotations stay selectable over a link
            // (those orphan-scan PDFs embed a page-spanning site link that otherwise eats every click).
            if (_currentTool == EditTool.Select && _linkOverlays.Count > 0)
            {
                var clickPos = e.GetPosition(_activeCanvas);
                int linkPage = _activeCanvas.Tag is int ltp ? ltp : PageList.SelectedIndex;
                bool onAnnot = _annotations.TryGetValue(linkPage, out var lal)
                               && lal.Any(a => HitTestAnnotation(a, clickPos, out _));
                foreach (var lo in onAnnot ? Enumerable.Empty<Canvas>() : _linkOverlays)
                {
                    double lx = Canvas.GetLeft(lo);
                    double ly = Canvas.GetTop(lo);
                    if (clickPos.X >= lx && clickPos.X <= lx + lo.Width &&
                        clickPos.Y >= ly && clickPos.Y <= ly + lo.Height)
                    {
                        var lTarget = lo.Tag is LinkAnnotInfo lai ? lai.Target : lo.Tag;
                        FollowLinkTarget(lTarget);
                        e.Handled = true;
                        return;
                    }
                }
            }
            // Tiled views (continuous / grid / two-page): resolve link clicks by a position bounds-check
            // against the page's stored rects. A per-link overlay can't be used here - it swallows the click
            // but its own handler never fires - so no overlay exists; _continuousLinks is the source of truth.
            // (Single-page view is handled by the _linkOverlays check above.)
            if (_currentTool == EditTool.Select && _viewMode != ViewMode.Single)
            {
                var cpos = e.GetPosition(_activeCanvas);
                int cpage = _activeCanvas.Tag is int cltp ? cltp : PageList.SelectedIndex;
                bool cOnAnnot = _annotations.TryGetValue(cpage, out var clal)
                                && clal.Any(a => HitTestAnnotation(a, cpos, out _));
                if (!cOnAnnot && _continuousLinks.TryGetValue(cpage, out var clinks))
                {
                    const double pad = LinkHitPad;   // shared with hover + the single-page overlay so all views match
                    foreach (var lnk in clinks)
                    {
                        if (cpos.X >= lnk.Cx - pad && cpos.X <= lnk.Cx + lnk.Cw + pad &&
                            cpos.Y >= lnk.Cy - pad && cpos.Y <= lnk.Cy + lnk.Ch + pad)
                        {
                            FollowLinkTarget(lnk.Tag);
                            e.Handled = true;
                            return;
                        }
                    }
                }
            }
            var pos = e.GetPosition(_activeCanvas);
            int pageIdx = _activeCanvas.Tag is int tagPage ? tagPage : PageList.SelectedIndex;
            if (pageIdx < 0) return;
            // Pin the surface/page this gesture started on so async re-renders (grid tile streaming)
            // can't redirect the in-progress draw/select to another page. See _gestureCanvas.
            _gestureCanvas = _activeCanvas;
            _gesturePage   = pageIdx;

            // Crop corner handles live in the outer panel and have direct MouseLeftButtonDown
            // handlers attached in AddCropHandles() - no detection needed here.

            // Check if click is on any of the four corner resize handles (signature, image, or text box)
            if (_resizeHandles.Count > 0 && _selectedAnnotation is not null)
            {
                foreach (var hd in _resizeHandles)
                {
                    double hx = Canvas.GetLeft(hd), hy = Canvas.GetTop(hd);
                    if (pos.X >= hx && pos.X <= hx + hd.Width &&
                        pos.Y >= hy && pos.Y <= hy + hd.Height)
                    {
                        _resizeCorner = hd.Tag as string ?? "SE";
                        // Anchor on the opposite corner so it stays put while the dragged corner moves.
                        if (_selectedAnnotation is PlacedAnnotation rsa)
                        {
                            _isResizingSig = true;
                            _resizeSigStart = pos;
                            _resizeSigStartScale = rsa.Scale;
                            _resizeSigAnnot = rsa;
                            double w0 = rsa.SourceWidth * rsa.Scale, h0 = rsa.SourceHeight * rsa.Scale;
                            double ax = rsa.Position.X, ay = rsa.Position.Y;
                            _resizeAnchor = _resizeCorner switch
                            {
                                "NW" => new Point(ax + w0, ay + h0),
                                "NE" => new Point(ax,      ay + h0),
                                "SW" => new Point(ax + w0, ay),
                                _    => new Point(ax,      ay)   // SE
                            };
                            _activeCanvas.CaptureMouse();
                            e.Handled = true;
                            return;
                        }
                        if (_selectedAnnotation is TextAnnotation rta)
                        {
                            _isResizingSig = true;          // shared "resize in progress" flag + capture
                            _resizeTextAnnot = rta;
                            double ax = rta.Position.X, ay = rta.Position.Y;
                            double w0 = rta.Width, h0 = rta.Height;
                            _resizeAnchor = _resizeCorner switch
                            {
                                "NW" => new Point(ax + w0, ay + h0),
                                "NE" => new Point(ax,      ay + h0),
                                "SW" => new Point(ax + w0, ay),
                                _    => new Point(ax,      ay)   // SE
                            };
                            _activeCanvas.CaptureMouse();
                            e.Handled = true;
                            return;
                        }
                        if (_selectedAnnotation is HighlightAnnotation rha)
                        {
                            _isResizingSig = true;          // shared "resize in progress" flag + capture
                            _resizeHlAnnot = rha;
                            double ax = rha.Bounds.X, ay = rha.Bounds.Y;
                            double w0 = rha.Bounds.Width, h0 = rha.Bounds.Height;
                            _resizeAnchor = _resizeCorner switch
                            {
                                "NW" => new Point(ax + w0, ay + h0),
                                "NE" => new Point(ax,      ay + h0),
                                "SW" => new Point(ax + w0, ay),
                                _    => new Point(ax,      ay)   // SE
                            };
                            _activeCanvas.CaptureMouse();
                            e.Handled = true;
                            return;
                        }
                        if (_selectedAnnotation is InkAnnotation rink && rink.Points.Count > 0)
                        {
                            _isResizingSig = true;          // shared "resize in progress" flag + capture
                            _resizeInkAnnot = rink;
                            _resizeInkOrigPoints = [.. rink.Points];
                            double minX = rink.Points.Min(p => p.X), minY = rink.Points.Min(p => p.Y);
                            double maxX = rink.Points.Max(p => p.X), maxY = rink.Points.Max(p => p.Y);
                            _resizeInkOrigBounds = new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
                            _resizeAnchor = _resizeCorner switch
                            {
                                "NW" => new Point(maxX, maxY),
                                "NE" => new Point(minX, maxY),
                                "SW" => new Point(maxX, minY),
                                _    => new Point(minX, minY)   // SE
                            };
                            _activeCanvas.CaptureMouse();
                            e.Handled = true;
                            return;
                        }
                    }
                }
            }

            switch (_currentTool)
            {
                case EditTool.Select:
                    if (e.ClickCount == 2)
                    {
                        // Double-clicking a stamp (page number / watermark) reopens the Stamp Pages editor.
                        if (StampHitTest(pageIdx, pos)) { OpenStampTool(); e.Handled = true; return; }
                        ClearSelection();
                        ClearTextSelection();
                        EditTextAtPosition(pos, pageIdx);
                        e.Handled = true;
                    }
                    else
                    {
                        // Shift+click builds a multi-selection instead of replacing it.
                        bool shiftSel = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                        // Flowing text selection (#127): when the press lands ON text, the text run
                        // owns the DRAG even if an unselected annotation (e.g. a paragraph-covering
                        // highlight) sits underneath - a plain CLICK still selects that annotation,
                        // resolved on mouse-up via _txtSelClickAnnot. An already-selected annotation
                        // keeps drag priority so click-then-drag moves it. Empty page, Shift, and
                        // OCR region capture all keep the classic marquee below.
                        bool selectedUnderPress = _selectedAnnotation is not null
                            && _selectedAnnotation.PageIndex == pageIdx
                            && HitTestAnnotation(_selectedAnnotation, pos, out _);
                        if (!_ocrRegionMode && !shiftSel && !selectedUnderPress
                            && TryBeginTextSelection(pageIdx, pos))
                        {
                            ClearSelection();
                            if (_annotations.TryGetValue(pageIdx, out var underPress))
                                for (int i = underPress.Count - 1; i >= 0; i--)
                                    if (IsDraggable(underPress[i]) && HitTestAnnotation(underPress[i], pos, out Rect ub))
                                    { _txtSelClickAnnot = underPress[i]; _txtSelClickAnnotBounds = ub; break; }
                            if (_viewMode == ViewMode.Grid) PageList.SelectedIndex = pageIdx;
                            _activeCanvas.CaptureMouse();
                            e.Handled = true;
                            break;
                        }
                        // Single click: check if hitting a PlacedAnnotation first - select and drag
                        bool hitPlaced = false;
                        if (_annotations.TryGetValue(pageIdx, out var pageAnnotsList))
                        {
                            for (int i = pageAnnotsList.Count - 1; i >= 0; i--)
                            {
                                if (IsDraggable(pageAnnotsList[i]) &&
                                    HitTestAnnotation(pageAnnotsList[i], pos, out Rect paBounds))
                                {
                                    var pa = pageAnnotsList[i];
                                    if (shiftSel)
                                    {
                                        // Toggle in/out of the multi-selection; no drag while shifting.
                                        ToggleMultiSelect(pa, paBounds, _gestureCanvas ?? _activeCanvas);
                                        e.Handled = true;
                                        hitPlaced = true;
                                        break;
                                    }
                                    // A grouped annotation selects + drags its whole group together;
                                    // otherwise just this one. _dragGroupOrig holds the companions' start
                                    // positions so the group translates rigidly during the drag.
                                    _dragGroupOrig.Clear();
                                    if (pa.GroupId.Length > 0)
                                    {
                                        SelectGroup(pa);
                                        foreach (var m in _selectedSet) _dragGroupOrig.Add((m, AnnotGetPos(m)));
                                    }
                                    else
                                    {
                                        ClearSelection();
                                        RenderAllAnnotations(pageIdx);
                                        SelectAnnotation(pa, paBounds);
                                    }
                                    _isDraggingAnnot = true;
                                    _dragAnnotStart = pos;
                                    _dragAnnotOrigPos = AnnotGetPos(pa);
                                    _dragAnnot = pa;
                                    _activeCanvas.CaptureMouse();
                                    e.Handled = true;
                                    hitPlaced = true;
                                    break;
                                }
                            }
                        }
                        if (!hitPlaced)
                        {
                            // Keep the existing multi-selection when shift is held (a click on a
                            // non-draggable annotation is added on mouse-up); only a plain click clears.
                            if (!shiftSel) ClearSelection();
                            ClearTextSelection();
                            if (_viewMode == ViewMode.Grid && !shiftSel) PageList.SelectedIndex = pageIdx;  // grid: click selects the page
                            _isSelecting = true;
                            _selectStart = pos;
                            _selectRect = new Rectangle
                            {
                                Fill = AccentBrush(40),
                                Stroke = AccentBrush(150),
                                StrokeThickness = 1,
                                Width = 0, Height = 0,
                                IsHitTestVisible = false
                            };
                            MarqueeLayer.Children.Add(_selectRect);
                            UpdateMarquee(pos, pos);
                            _activeCanvas.CaptureMouse();
                            e.Handled = true;
                        }
                    }
                    break;

                case EditTool.Text:
                    // A click inside the box that's already being edited must NOT commit-and-replace it
                    // (that makes the box appear to jump to the cursor). In Grid view a per-tile overlay
                    // can sit above the TextBox, so the OriginalSource guard near the top of this method
                    // misses the hit; a bounds check on the box's own canvas catches it reliably.
                    if (ClickInsideActiveTextBox(pos))
                    {
                        e.Handled = true;
                        break;
                    }
                    CommitActiveTextBox();
                    PlaceTextBox(pos, pageIdx);
                    e.Handled = true;
                    break;

                case EditTool.Highlight:
                case EditTool.Strikethrough:
                case EditTool.Underline:
                    ClearSelection();
                    // #127 Phase 2: on text these tools FLOW along the character runs exactly like
                    // text selection; the release turns the selected lines into per-line annotations.
                    // The rectangle path below survives ONLY for the highlight eraser. A press on a
                    // page with no text under it hints instead of silently laying down a rect,
                    // pointing at the Shapes tool (digit 4) as the deliberate rectangle path.
                    if (!(_currentTool == EditTool.Highlight && _highlightErase))
                    {
                        ClearTextSelection();
                        if (TryBeginTextSelection(pageIdx, pos))
                        {
                            _txtSelCommitTool = _currentTool;
                            _activeCanvas.CaptureMouse();
                        }
                        else
                        {
                            SetStatus(Loc("Str_St_NoTextUseShapes"));
                        }
                        e.Handled = true;
                        break;
                    }
                    _isDrawing = true;
                    _drawStart = pos;
                    var previewFill = _currentTool == EditTool.Highlight
                        ? _highlightColor
                        : Color.FromArgb(70, _lineAnnotColor.R, _lineAnnotColor.G, _lineAnnotColor.B);
                    var rect = new Rectangle { Width = 0, Height = 0 };
                    if (_currentTool == EditTool.Highlight && _highlightErase)
                    {
                        // Eraser: red marching-ants box so it reads as "delete inside" and stays visible on
                        // any page color (the old faint white dashes vanished on light scans). The dash
                        // offset animates so the ants "walk".
                        rect.Fill = new SolidColorBrush(Color.FromArgb(36, 255, 60, 60));
                        rect.Stroke = new SolidColorBrush(Color.FromArgb(235, 220, 40, 40));
                        rect.StrokeThickness = 2;
                        rect.StrokeDashArray = [4, 3];
                        rect.BeginAnimation(Shape.StrokeDashOffsetProperty,
                            new DoubleAnimation(0, 7, new Duration(TimeSpan.FromSeconds(0.5)))
                            { RepeatBehavior = RepeatBehavior.Forever });
                    }
                    else rect.Fill = new SolidColorBrush(previewFill);
                    Canvas.SetLeft(rect, pos.X);
                    Canvas.SetTop(rect, pos.Y);
                    _activeCanvas.Children.Add(rect);
                    _activePreview = rect;
                    _activeCanvas.CaptureMouse();
                    break;

                case EditTool.Shape:
                    // #127 Phase 3: rectangle/ellipse drag or polygon vertex click - see Shapes.cs.
                    ShapeToolMouseDown(pageIdx, pos, e);
                    break;

                case EditTool.Draw:
                    ClearSelection();
                    _isDrawing = true;
                    _activeInk = new InkAnnotation { PageIndex = pageIdx, StrokeWidth = _drawWidth };
                    _activeInk.SetColor(_drawColor);
                    _activeInk.Points.Add(pos);
                    var poly = new Polyline
                    {
                        // Eraser brush shows a translucent grey stroke so it reads as erasing, not inking.
                        Stroke = _drawErase ? new SolidColorBrush(Color.FromArgb(120, 200, 200, 200))
                                            : new SolidColorBrush(_drawColor),
                        StrokeThickness = _drawWidth,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                    poly.Points.Add(pos);
                    _activeCanvas.Children.Add(poly);
                    _activePreview = poly;
                    _activeCanvas.CaptureMouse();
                    break;

                case EditTool.Line:
                    // A straight line is a 2-point ink stroke - reuses ink rendering/export/selection and
                    // gets round end caps (and antialiased edges) for free. The 2nd point tracks the drag.
                    ClearSelection();
                    _isDrawing = true;
                    _drawStart = pos;
                    _activeInk = new InkAnnotation { PageIndex = pageIdx, StrokeWidth = _drawWidth };
                    _activeInk.SetColor(_drawColor);
                    _activeInk.Points.Add(pos);
                    _activeInk.Points.Add(pos);
                    var lpoly = new Polyline
                    {
                        Stroke = new SolidColorBrush(_drawColor),
                        StrokeThickness = _drawWidth,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                    lpoly.Points.Add(pos);
                    lpoly.Points.Add(pos);
                    _activeCanvas.Children.Add(lpoly);
                    _activePreview = lpoly;
                    _activeCanvas.CaptureMouse();
                    break;

                case EditTool.Signature:
                    if (_pendingSignature is not null)
                    {
                        PlaceSignature(pos, pageIdx);
                        e.Handled = true;
                    }
                    else
                    {
                        ShowSignaturePopup();
                    }
                    break;

                case EditTool.Image:
                    PlaceImageFromDialog(pos, pageIdx);
                    e.Handled = true;
                    break;

                case EditTool.Crop:
                    ClearSelection();
                    _isDrawing = true;
                    _drawStart = pos;
                    _cropPageIndex = _activeCanvas.Tag is int cpi ? cpi : (_viewMode == ViewMode.Grid ? 0 : PageList.SelectedIndex);
                    // Draw the NEW box as a separate rect (above the existing one). The current box, its
                    // handles, and the bar all stay put until this draw is committed on mouse-up - so a
                    // mouse-down never makes the box or the bar vanish.
                    var cropDrawRect = new Rectangle
                    {
                        Stroke          = Brushes.White,
                        StrokeThickness = 1.5,
                        StrokeDashArray = [5, 3],
                        Fill            = AccentBrush(55),
                        Width = 0, Height = 0,
                        IsHitTestVisible = false,
                        Effect = new System.Windows.Media.Effects.DropShadowEffect
                            { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 3, Opacity = 0.7 },
                    };
                    Canvas.SetLeft(cropDrawRect, pos.X);
                    Canvas.SetTop(cropDrawRect, pos.Y);
                    Panel.SetZIndex(cropDrawRect, 2); // above the existing box
                    _activeCanvas.Children.Add(cropDrawRect);
                    _activePreview = cropDrawRect;
                    _activeCanvas.CaptureMouse();
                    break;
            }
        }

        // Ink/eraser brush cursor preview: a circle the size of the brush, shown while hovering with the
        // Draw tool so the brush footprint is visible before a stroke. Ink = color-tinted ring; eraser =
        // grey dashed ring. Lives on the page overlay under the cursor; removed on tool change / stroke /
        // when the pointer is no longer hovering the Draw tool.
        private System.Windows.Shapes.Ellipse? _brushPreview;

        private void ShowBrushPreview(Canvas canvas, Point center)
        {
            double d = Math.Max(2, _drawWidth);   // brush diameter in canvas (render-dim) units
            _brushPreview ??= new System.Windows.Shapes.Ellipse { IsHitTestVisible = false };
            Panel.SetZIndex(_brushPreview, 9000);
            _brushPreview.StrokeThickness = 1;
            _brushPreview.Width = d;
            _brushPreview.Height = d;
            if (_drawErase)
            {
                _brushPreview.Stroke = new SolidColorBrush(Color.FromArgb(220, 150, 150, 150));
                _brushPreview.StrokeDashArray = [3, 2];
                _brushPreview.Fill = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
            }
            else
            {
                _brushPreview.Stroke = new SolidColorBrush(_drawColor);
                _brushPreview.StrokeDashArray = null;
                _brushPreview.Fill = new SolidColorBrush(Color.FromArgb(40, _drawColor.R, _drawColor.G, _drawColor.B));
            }
            if (!ReferenceEquals(_brushPreview.Parent, canvas))
            {
                (_brushPreview.Parent as Canvas)?.Children.Remove(_brushPreview);
                canvas.Children.Add(_brushPreview);
            }
            Canvas.SetLeft(_brushPreview, center.X - d / 2);
            Canvas.SetTop(_brushPreview, center.Y - d / 2);
        }

        private void HideBrushPreview()
        {
            if (_brushPreview is not null)
                (_brushPreview.Parent as Canvas)?.Children.Remove(_brushPreview);
        }

        // The pointer left a page surface: drop the brush cursor so it doesn't hang frozen at the page
        // edge (MouseMove stops firing off-canvas, so it can't clear itself there).
        internal void Canvas_MouseLeave(object sender, MouseEventArgs e)
        {
            HideBrushPreview();
            ShowLinkHoverStatus(null);   // restore the status bar when the pointer leaves the page
        }

        internal void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            // Don't interfere with mouse interaction inside form field overlays.
            if (e.OriginalSource is DependencyObject moveSrc && IsFormFieldElement(moveSrc))
                return;

            // Shapes tool (#127 Phase 3): rubber-band the in-progress polygon (no button held).
            if (_currentTool == EditTool.Shape && _shapePolyPoints.Count > 0)
            {
                UpdateShapePolyRubber(e);
                return;
            }

            // Tiled views: hand cursor + the link's target in the status bar while hovering (links have no
            // clickable overlay here - see Canvas_MouseLeftButtonDown - so hover is resolved by hit-testing
            // the stored rects, same 20px pad as the click).
            if (_currentTool == EditTool.Select && _viewMode != ViewMode.Single && sender is Canvas linkHoverCv)
            {
                int hpage = linkHoverCv.Tag is int htp ? htp : -1;
                var hpos = e.GetPosition(linkHoverCv);
                string? hoverTarget = null;
                if (hpage >= 0 && _continuousLinks.TryGetValue(hpage, out var hlinks))
                    foreach (var l in hlinks)
                        if (hpos.X >= l.Cx - LinkHitPad && hpos.X <= l.Cx + l.Cw + LinkHitPad &&
                            hpos.Y >= l.Cy - LinkHitPad && hpos.Y <= l.Cy + l.Ch + LinkHitPad)
                        { hoverTarget = l.Tip; break; }
                linkHoverCv.Cursor = hoverTarget != null ? System.Windows.Input.Cursors.Hand : null;
                ShowLinkHoverStatus(hoverTarget);
            }

            // Brush cursor: with the Draw tool active and no button down, show a circle the size of the
            // brush (ink or eraser) at the pointer so it's obvious where the next stroke will land.
            if (sender is Canvas hoverCv)
            {
                if (_currentTool == EditTool.Draw && !_isDrawing && e.LeftButton != MouseButtonState.Pressed)
                    ShowBrushPreview(hoverCv, e.GetPosition(hoverCv));
                else
                    HideBrushPreview();
            }

            // Safety: a drag/resize is in progress but the left button is no longer down - the mouse-up was
            // lost (typically because the app was in the background when the user released). Finish the
            // gesture now so the annotation stops following the cursor and the canvas frees its capture.
            if (e.LeftButton != MouseButtonState.Pressed && (_isDraggingAnnot || _isResizingSig))
            {
                FinishStuckGesture();
                return;
            }

            // Resolve the pointer against the surface the gesture started on, not _activeCanvas,
            // which RenderAllAnnotations (and async grid tile streaming) can re-point mid-gesture.
            var gc = _gestureCanvas ?? _activeCanvas;
            var pos = e.GetPosition(gc);
            pos.X = Math.Max(0, Math.Min(gc.ActualWidth, pos.X));
            pos.Y = Math.Max(0, Math.Min(gc.ActualHeight, pos.Y));

            // Live text-edit box resize (mid-edit corner handles): free-form, opposite corner held fixed.
            if (_draggingTextEditHandle && _tehBox is not null)
            {
                double newW = Math.Max(40, Math.Abs(pos.X - _tehAnchor.X));
                double newH = Math.Max(24, Math.Abs(pos.Y - _tehAnchor.Y));
                double nx = (_tehCorner is "NW" or "SW") ? _tehAnchor.X - newW : _tehAnchor.X;
                double ny = (_tehCorner is "NW" or "NE") ? _tehAnchor.Y - newH : _tehAnchor.Y;
                Canvas.SetLeft(_tehBox, nx);
                Canvas.SetTop(_tehBox, ny);
                _tehBox.Width  = newW;
                _tehBox.Height = newH;
                LayoutTextEditHandles();
                return;
            }

            // Text box resize drag: width follows the dragged corner; height auto-fits the wrapped text.
            if (_isResizingSig && _resizeTextAnnot is not null)
            {
                var rta = _resizeTextAnnot;
                // Free-form: the dragged corner sets both width and height (the opposite corner is fixed),
                // exactly like resizing an image or crop rectangle. Text wraps to the width and is clipped
                // to the height. The corner is clamped to the page so it can't leave it.
                var cp = ClampPointToPage(rta.PageIndex, pos);
                double newW = Math.Max(40, Math.Abs(cp.X - _resizeAnchor.X));
                double newH = Math.Max(20, Math.Abs(cp.Y - _resizeAnchor.Y));
                double nx = (_resizeCorner is "NW" or "SW") ? _resizeAnchor.X - newW : _resizeAnchor.X;
                double ny = (_resizeCorner is "NW" or "NE") ? _resizeAnchor.Y - newH : _resizeAnchor.Y;
                rta.Width = newW;
                rta.Height = newH;
                rta.Position = new Point(nx, ny);
                if (_selectionBorder is not null)
                {
                    _selectionBorder.Width  = newW + 8;
                    _selectionBorder.Height = newH + 8;
                    Canvas.SetLeft(_selectionBorder, nx - 4);
                    Canvas.SetTop(_selectionBorder, ny - 4);
                }
                LayoutResizeHandles(nx, ny, newW, newH);
                RenderAllAnnotations(rta.PageIndex);
                ReattachSelectionVisuals();   // re-add border + handles + the paired cover outline (stays put during resize/move)
                return;
            }

            // Signature resize drag
            if (_isResizingSig && _resizeSigAnnot is not null)
            {
                // Uniform-scale resize from the dragged corner; the opposite corner (_resizeAnchor)
                // stays fixed. Aspect is preserved by taking whichever axis demands the larger scale.
                double desiredW = Math.Abs(pos.X - _resizeAnchor.X);
                double desiredH = Math.Abs(pos.Y - _resizeAnchor.Y);
                double sw = Math.Max(1.0, _resizeSigAnnot.SourceWidth);
                double sh = Math.Max(1.0, _resizeSigAnnot.SourceHeight);
                double newScale = Math.Max(0.05, Math.Max(desiredW / sw, desiredH / sh));
                _resizeSigAnnot.Scale = newScale;

                double newW = _resizeSigAnnot.SourceWidth * newScale;
                double newH = _resizeSigAnnot.SourceHeight * newScale;
                // Reposition the top-left so the anchor corner is preserved.
                double nx = (_resizeCorner is "NW" or "SW") ? _resizeAnchor.X - newW : _resizeAnchor.X;
                double ny = (_resizeCorner is "NW" or "NE") ? _resizeAnchor.Y - newH : _resizeAnchor.Y;
                _resizeSigAnnot.Position = new Point(nx, ny);

                if (_selectionBorder is not null)
                {
                    _selectionBorder.Width  = newW + 8;
                    _selectionBorder.Height = newH + 8;
                    Canvas.SetLeft(_selectionBorder, nx - 4);
                    Canvas.SetTop(_selectionBorder, ny - 4);
                }
                LayoutResizeHandles(nx, ny, newW, newH);

                // Re-render annotations to show updated size
                RenderAllAnnotations(_resizeSigAnnot.PageIndex);
                // Restore selection visuals (RenderAllAnnotations clears canvas children including our overlays)
                ReattachSelectionVisuals();   // re-add border + handles + the paired cover outline (stays put during resize/move)
                return;
            }

            // Highlight / strikethrough / underline resize drag (modifies the Bounds rectangle).
            if (_isResizingSig && _resizeHlAnnot is not null)
            {
                var cp = ClampPointToPage(_resizeHlAnnot.PageIndex, pos);   // keep the dragged corner on-page
                double newW = Math.Max(4, Math.Abs(cp.X - _resizeAnchor.X));
                double newH = Math.Max(4, Math.Abs(cp.Y - _resizeAnchor.Y));
                double nx = (_resizeCorner is "NW" or "SW") ? _resizeAnchor.X - newW : _resizeAnchor.X;
                double ny = (_resizeCorner is "NW" or "NE") ? _resizeAnchor.Y - newH : _resizeAnchor.Y;
                _resizeHlAnnot.Bounds = new Rect(nx, ny, newW, newH);
                _resizeHlAnnot.Erases = null;   // resizing remakes a solid highlight; old carve no longer fits
                if (_selectionBorder is not null)
                {
                    _selectionBorder.Width  = newW + 8;
                    _selectionBorder.Height = newH + 8;
                    Canvas.SetLeft(_selectionBorder, nx - 4);
                    Canvas.SetTop(_selectionBorder, ny - 4);
                }
                LayoutResizeHandles(nx, ny, newW, newH);
                RenderAllAnnotations(_resizeHlAnnot.PageIndex);
                ReattachSelectionVisuals();   // re-add border + handles + the paired cover outline (stays put during resize/move)
                return;
            }

            // Ink resize drag: scale every stroke point about the fixed anchor corner.
            if (_isResizingSig && _resizeInkAnnot is not null && _resizeInkOrigPoints is not null)
            {
                var cp = ClampPointToPage(_resizeInkAnnot.PageIndex, pos);   // keep the dragged corner on-page
                double newW = Math.Max(4, Math.Abs(cp.X - _resizeAnchor.X));
                double newH = Math.Max(4, Math.Abs(cp.Y - _resizeAnchor.Y));
                double sx = newW / _resizeInkOrigBounds.Width;
                double sy = newH / _resizeInkOrigBounds.Height;
                for (int i = 0; i < _resizeInkOrigPoints.Count && i < _resizeInkAnnot.Points.Count; i++)
                {
                    var p = _resizeInkOrigPoints[i];
                    _resizeInkAnnot.Points[i] = new Point(
                        _resizeAnchor.X + (p.X - _resizeAnchor.X) * sx,
                        _resizeAnchor.Y + (p.Y - _resizeAnchor.Y) * sy);
                }
                var ib = AnnotBounds(_resizeInkAnnot);
                if (_selectionBorder is not null)
                {
                    _selectionBorder.Width  = ib.Width + 8;
                    _selectionBorder.Height = ib.Height + 8;
                    Canvas.SetLeft(_selectionBorder, ib.X - 4);
                    Canvas.SetTop(_selectionBorder, ib.Y - 4);
                }
                LayoutResizeHandles(ib.X, ib.Y, ib.Width, ib.Height);
                RenderAllAnnotations(_resizeInkAnnot.PageIndex);
                ReattachSelectionVisuals();   // re-add border + handles + the paired cover outline (stays put during resize/move)
                return;
            }

            // Annotation drag-to-move
            if (_isDraggingAnnot && _dragAnnot is not null)
            {
                double dx = pos.X - _dragAnnotStart.X;
                double dy = pos.Y - _dragAnnotStart.Y;
                if (_dragGroupOrig.Count > 0)
                {
                    // Rigid group move: clamp ONE shared delta against the group's combined bounds so the
                    // whole group stops at the page edge together - clamping members individually would let
                    // some stop while others slide on, distorting the layout. Applies the same delta to all.
                    double minLeft = _dragAnnotOrigPos.X, minTop = _dragAnnotOrigPos.Y;
                    double maxRight = _dragAnnotOrigPos.X + AnnotBounds(_dragAnnot).Width;
                    double maxBottom = _dragAnnotOrigPos.Y + AnnotBounds(_dragAnnot).Height;
                    foreach (var (m, orig) in _dragGroupOrig)
                    {
                        var sz = AnnotBounds(m).Size;
                        minLeft = Math.Min(minLeft, orig.X);
                        minTop = Math.Min(minTop, orig.Y);
                        maxRight = Math.Max(maxRight, orig.X + sz.Width);
                        maxBottom = Math.Max(maxBottom, orig.Y + sz.Height);
                    }
                    if (_renderDims.TryGetValue(_dragAnnot.PageIndex, out var rdg))
                    {
                        // Math.Max(lo, ...) wins when the group is larger than the page, pinning it to the
                        // top-left edge instead of letting it drift partly off.
                        dx = Math.Max(-minLeft, Math.Min(rdg.w - maxRight, dx));
                        dy = Math.Max(-minTop, Math.Min(rdg.h - maxBottom, dy));
                    }
                    AnnotSetPos(_dragAnnot, new Point(_dragAnnotOrigPos.X + dx, _dragAnnotOrigPos.Y + dy));
                    foreach (var (m, orig) in _dragGroupOrig)
                        AnnotSetPos(m, new Point(orig.X + dx, orig.Y + dy));
                }
                else
                {
                    AnnotSetPos(_dragAnnot, new Point(_dragAnnotOrigPos.X + dx, _dragAnnotOrigPos.Y + dy));
                    AnnotSetPos(_dragAnnot, ClampAnnotPos(_dragAnnot));   // keep the whole annotation on-page
                }
                var db = AnnotBounds(_dragAnnot);
                if (_selectionBorder is not null)
                {
                    Canvas.SetLeft(_selectionBorder, db.X - 4);
                    Canvas.SetTop(_selectionBorder, db.Y - 4);
                }
                if (_dragAnnot is PlacedAnnotation or TextAnnotation or HighlightAnnotation or InkAnnotation)
                    LayoutResizeHandles(db.X, db.Y, db.Width, db.Height);
                RenderAllAnnotations(_dragAnnot.PageIndex);
                ReattachSelectionVisuals();   // re-add border + handles + the paired cover outline (stays put during resize/move)
                if (_dragGroupOrig.Count > 0) ReattachMultiOutlines();   // keep group outlines live during the drag
                return;
            }

            // Flowing text selection drag (#127): move the focus caret and repaint the quads.
            if (_txtSelActive)
            {
                UpdateTextSelectionDrag(e);
                return;
            }

            // Text selection drag
            if (_isSelecting && _selectRect is not null)
            {
                // Raw (un-clamped) pointer so the marquee can extend past the start page onto the others;
                // pos above is clamped to the page, which is right for dragging annotations but not for this.
                UpdateMarquee(_selectStart, e.GetPosition(gc));
                return;
            }

            // Crop corner handle drag - must be before the _isDrawing guard since handle drag
            // runs with _isDrawing = false and _activePreview = null.
            if (_activeCropHandleTag is not null && _cropPreviewRect is not null)
            {
                double dx = pos.X - _cropHandleDragStart.X;
                double dy = pos.Y - _cropHandleDragStart.Y;
                var r = _cropRectAtHandleDrag;
                double newX = r.X, newY = r.Y, newW = r.Width, newH = r.Height;
                switch (_activeCropHandleTag)
                {
                    case "NW":
                        newX = Math.Min(r.Right - 10, r.X + dx);
                        newY = Math.Min(r.Bottom - 10, r.Y + dy);
                        newW = r.Right - newX;
                        newH = r.Bottom - newY;
                        break;
                    case "NE":
                        newY = Math.Min(r.Bottom - 10, r.Y + dy);
                        newW = Math.Max(10, r.Width + dx);
                        newH = r.Bottom - newY;
                        break;
                    case "SE":
                        newW = Math.Max(10, r.Width + dx);
                        newH = Math.Max(10, r.Height + dy);
                        break;
                    case "SW":
                        newX = Math.Min(r.Right - 10, r.X + dx);
                        newW = r.Right - newX;
                        newH = Math.Max(10, r.Height + dy);
                        break;
                }
                _cropCanvasRect = new Rect(newX, newY, newW, newH);
                UpdateCropRectVisuals();
                return;
            }

            if (!_isDrawing || _activePreview is null) return;

            switch (_currentTool)
            {
                case EditTool.Shape when _activePreview is Rectangle:
                case EditTool.Highlight when _activePreview is Rectangle:
                case EditTool.Strikethrough when _activePreview is Rectangle:
                case EditTool.Underline when _activePreview is Rectangle:
                    var rect = (Rectangle)_activePreview;
                    Canvas.SetLeft(rect, Math.Min(pos.X, _drawStart.X));
                    Canvas.SetTop(rect, Math.Min(pos.Y, _drawStart.Y));
                    rect.Width = Math.Abs(pos.X - _drawStart.X);
                    rect.Height = Math.Abs(pos.Y - _drawStart.Y);
                    break;

                case EditTool.Shape when _activePreview is Ellipse sell:
                    Canvas.SetLeft(sell, Math.Min(pos.X, _drawStart.X));
                    Canvas.SetTop(sell, Math.Min(pos.Y, _drawStart.Y));
                    sell.Width = Math.Abs(pos.X - _drawStart.X);
                    sell.Height = Math.Abs(pos.Y - _drawStart.Y);
                    break;

                case EditTool.Draw when _activePreview is Polyline poly && _activeInk is not null:
                    _activeInk.Points.Add(pos);
                    poly.Points.Add(pos);
                    break;

                case EditTool.Line when _activePreview is Polyline lp && _activeInk is not null && _activeInk.Points.Count >= 2:
                    // Straight line: the second point follows the cursor (endpoint), no points added.
                    // "Level" snaps to the nearest axis - horizontal when the drag is wider than tall,
                    // vertical otherwise - so both level horizontal and vertical lines just work.
                    Point lend;
                    if (_lineLevel)
                    {
                        var sp = _activeInk.Points[0];
                        lend = Math.Abs(pos.X - sp.X) >= Math.Abs(pos.Y - sp.Y)
                            ? new Point(pos.X, sp.Y)    // horizontal
                            : new Point(sp.X, pos.Y);   // vertical
                    }
                    else lend = pos;
                    _activeInk.Points[1] = lend;
                    lp.Points[1] = lend;
                    break;

                case EditTool.Crop when _activePreview is Rectangle crect:
                    Canvas.SetLeft(crect, Math.Min(pos.X, _drawStart.X));
                    Canvas.SetTop(crect, Math.Min(pos.Y, _drawStart.Y));
                    crect.Width = Math.Abs(pos.X - _drawStart.X);
                    crect.Height = Math.Abs(pos.Y - _drawStart.Y);
                    // Live-update the bar's X/Y/W/H as the box is dragged out.
                    _cropCanvasRect = new Rect(Canvas.GetLeft(crect), Canvas.GetTop(crect), crect.Width, crect.Height);
                    SyncCropBoxInputs();
                    break;
            }
        }

        internal void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Don't process release events that originate inside the crop confirm bar.
            if (_cropConfirmBar is not null && e.OriginalSource is DependencyObject cropSrc &&
                IsDescendantOf(cropSrc, _cropConfirmBar))
                return;

            // The gesture may have re-pointed _activeCanvas via RenderAllAnnotations since mouse-down
            // (in Grid view, async tile streaming does this), so restore it to the surface the
            // gesture started on. This makes page resolution, capture release, and preview cleanup
            // below all act on the correct page - the root cause of highlights landing on the wrong
            // page and select/delete failing in Grid view.
            if (_gestureCanvas is not null) _activeCanvas = _gestureCanvas;

            // Use the page the gesture started on (continuous/grid have per-page canvases tagged
            // with their index), matching the mouse-down handler. Falls back to the selected page
            // for the single-page canvas. Prevents committing an annotation to the wrong page.
            int pageIdx = _gesturePage >= 0
                ? _gesturePage
                : (_activeCanvas?.Tag is int tagPage ? tagPage : PageList.SelectedIndex);

            // Finish a live text-edit box resize and hand focus back so typing continues.
            if (_draggingTextEditHandle)
            {
                _draggingTextEditHandle = false;
                _activeCanvas?.ReleaseMouseCapture();
                _tehBox?.Focus();
                e.Handled = true;
                return;
            }

            // Finish crop handle drag
            if (_activeCropHandleTag is not null)
            {
                _activeCropHandleTag = null;
                _activeCanvas?.ReleaseMouseCapture();
                return;
            }

            // Finish annotation drag-to-move
            if (_isDraggingAnnot)
            {
                _isDraggingAnnot = false;
                _activeCanvas?.ReleaseMouseCapture();
                if (_dragAnnot is not null)
                {
                    var da = _dragAnnot;
                    _dragAnnot = null;
                    int oldPage = da.PageIndex;
                    // Released over a different page? Move it there (position was updated live during drag).
                    var drop = PageCanvasUnderPointer(e);
                    if (drop is { } d && d.page != oldPage && _doc is not null
                        && d.page >= 0 && d.page < _doc.PageCount)
                    {
                        var pt = e.GetPosition(d.canvas);
                        AnnotSetPos(da, new Point(pt.X - (_dragAnnotStart.X - _dragAnnotOrigPos.X),
                                                  pt.Y - (_dragAnnotStart.Y - _dragAnnotOrigPos.Y)));
                        if (_annotations.TryGetValue(oldPage, out var oldList)) oldList.Remove(da);
                        da.PageIndex = d.page;
                        if (!_annotations.TryGetValue(d.page, out var newList)) { newList = []; _annotations[d.page] = newList; }
                        newList.Add(da);
                        ClearSelection();
                        RenderAllAnnotations(oldPage);
                        RenderAllAnnotations(d.page);
                        SelectAnnotation(da, AnnotBounds(da));
                        MarkDirty();
                        return;
                    }
                    RenderAllAnnotations(da.PageIndex);
                    // Keep the whole group selected after a group move (not just the dragged member).
                    if (da.GroupId.Length > 0) SelectGroup(da);
                    else SelectAnnotation(da, AnnotBounds(da));
                    MarkDirty();
                }
                _dragGroupOrig.Clear();
                return;
            }

            // Finish text box resize
            if (_isResizingSig && _resizeTextAnnot is not null)
            {
                _isResizingSig = false;
                _activeCanvas?.ReleaseMouseCapture();
                var rta = _resizeTextAnnot;
                _resizeTextAnnot = null;
                RenderAllAnnotations(rta.PageIndex);
                SelectAnnotation(rta, new Rect(rta.Position.X, rta.Position.Y, rta.Width, rta.Height));
                MarkDirty();
                return;
            }

            // Finish highlight / strikethrough / underline resize
            if (_isResizingSig && _resizeHlAnnot is not null)
            {
                _isResizingSig = false;
                _activeCanvas?.ReleaseMouseCapture();
                var rha = _resizeHlAnnot;
                _resizeHlAnnot = null;
                RenderAllAnnotations(rha.PageIndex);
                SelectAnnotation(rha, rha.Bounds);
                MarkDirty();
                return;
            }

            // Finish ink resize
            if (_isResizingSig && _resizeInkAnnot is not null)
            {
                _isResizingSig = false;
                _activeCanvas?.ReleaseMouseCapture();
                var rink = _resizeInkAnnot;
                _resizeInkAnnot = null;
                _resizeInkOrigPoints = null;
                RenderAllAnnotations(rink.PageIndex);
                SelectAnnotation(rink, AnnotBounds(rink));
                MarkDirty();
                return;
            }

            // Finish signature resize
            if (_isResizingSig)
            {
                _isResizingSig = false;
                _activeCanvas?.ReleaseMouseCapture();
                if (_resizeSigAnnot is not null)
                {
                    // Final re-render and re-select to reposition handle cleanly
                    var sa = _resizeSigAnnot;
                    _resizeSigAnnot = null;
                    RenderAllAnnotations(sa.PageIndex);
                    double newW = sa.SourceWidth * sa.Scale;
                    double newH = sa.SourceHeight * sa.Scale;
                    SelectAnnotation(sa, new Rect(sa.Position.X, sa.Position.Y, newW, newH));
                    MarkDirty();
                }
                return;
            }

            // Flowing text selection release (#127): commit the run, copy it, keep the quads on screen.
            if (_txtSelActive)
            {
                (_gestureCanvas ?? _activeCanvas)?.ReleaseMouseCapture();
                FinishTextSelection();
                e.Handled = true;
                return;
            }

            // Handle text selection release
            if (_isSelecting)
            {
                _isSelecting = false;
                var gc = _gestureCanvas ?? _activeCanvas;   // the canvas the marquee was drawn against
                gc?.ReleaseMouseCapture();
                // Drop the shaded marquee rectangle now that the drag is over - the selection it made is
                // kept, but the box itself shouldn't linger on the page.
                if (_selectRect is not null)
                {
                    (_selectRect.Parent as Canvas)?.Children.Remove(_selectRect);
                    _selectRect = null;
                }
                var pos = e.GetPosition(gc);
                double dragW = Math.Abs(pos.X - _selectStart.X);
                double dragH = Math.Abs(pos.Y - _selectStart.Y);

                if (_ocrRegionMode)
                {
                    _ocrRegionMode = false;
                    if (dragW >= 4 && dragH >= 4)
                        OcrRegion(pageIdx, new Rect(Math.Min(pos.X, _selectStart.X), Math.Min(pos.Y, _selectStart.Y), dragW, dragH));
                    else SetStatus(Loc("Str_St_OcrRegionCancelled"));
                    return;
                }

                if (dragW < 5 && dragH < 5)
                {
                    // Tiny drag = single click -> try annotation selection
                    ClearTextSelection();
                    bool shiftSel = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                    if (pageIdx >= 0 && _annotations.ContainsKey(pageIdx))
                    {
                        for (int i = _annotations[pageIdx].Count - 1; i >= 0; i--)
                        {
                            if (HitTestAnnotation(_annotations[pageIdx][i], _selectStart, out Rect bounds))
                            {
                                if (shiftSel)
                                    // Add/remove this annotation from the multi-selection.
                                    ToggleMultiSelect(_annotations[pageIdx][i], bounds,
                                                      _gestureCanvas ?? CanvasForPage(pageIdx));
                                else
                                    SelectAnnotation(_annotations[pageIdx][i], bounds);
                                break;
                            }
                        }
                    }
                }
                else
                {
                    var startRect = new Rect(
                        Math.Min(pos.X, _selectStart.X), Math.Min(pos.Y, _selectStart.Y),
                        dragW, dragH);
                    // Box-select across EVERY visible page: map the marquee (in the start page's coords) into
                    // each page's own coords and collect the annotations it covers. Falls back to region text
                    // extraction on the start page only when nothing is caught anywhere.
                    var hits = new List<(PageAnnotation a, Rect b, Canvas cv)>();
                    foreach (int p in new List<int>(_pages.Keys))
                    {
                        if (!_annotations.TryGetValue(p, out var apg) || apg.Count == 0) continue;
                        var pc = _pages[p];
                        Rect inP = MapRect(gc!, pc, startRect);
                        foreach (var a in apg)
                        {
                            if (!IsDraggable(a)) continue;
                            var ab = AnnotBounds(a);
                            if (!ab.IsEmpty && inP.IntersectsWith(ab)) hits.Add((a, ab, pc));
                        }
                    }
                    if (hits.Count > 0)
                    {
                        ClearSelection();
                        foreach (var (a, b, cv) in hits) ToggleMultiSelect(a, b, cv);
                        SetStatus($"Selected {hits.Count} annotation{(hits.Count == 1 ? "" : "s")}");
                    }
                    else
                    {
                        ExtractTextFromRegion(pageIdx, startRect);
                    }
                }
                return;
            }

            if (!_isDrawing) return;
            _isDrawing = false;
            _activeCanvas?.ReleaseMouseCapture();

            switch (_currentTool)
            {
                case EditTool.Highlight when _activePreview is Rectangle:
                case EditTool.Strikethrough when _activePreview is Rectangle:
                case EditTool.Underline when _activePreview is Rectangle:
                    {
                        var rect = (Rectangle)_activePreview;
                        // Eraser mode (Highlight tool): the box deletes annotations inside it instead of
                        // laying down a highlight.
                        if (_currentTool == EditTool.Highlight && _highlightErase)
                        {
                            if (rect.Width > 3 && rect.Height > 3)
                            {
                                var region = new Rect(Canvas.GetLeft(rect), Canvas.GetTop(rect), rect.Width, rect.Height);
                                _activeCanvas?.Children.Remove(rect);
                                // Partial erase: cut strokes inside the box and subtract it from highlights.
                                ErasePartial(pageIdx, p => region.Contains(p), region, 4.0);
                            }
                            else _activeCanvas?.Children.Remove(rect);
                        }
                        else if (rect.Width > 3 && rect.Height > 3)
                        {
                            var ha = new HighlightAnnotation
                            {
                                PageIndex = pageIdx,
                                Bounds = new Rect(Canvas.GetLeft(rect), Canvas.GetTop(rect), rect.Width, rect.Height),
                                Style = _currentTool == EditTool.Strikethrough ? HighlightStyle.Strikethrough
                                      : _currentTool == EditTool.Underline   ? HighlightStyle.Underline
                                      : HighlightStyle.Fill
                            };
                            ha.SetColor(_currentTool == EditTool.Highlight ? _highlightColor : _lineAnnotColor);
                            AddAnnotation(ha);
                            _activeCanvas?.Children.Remove(rect);
                            RenderAllAnnotations(pageIdx);
                        }
                        else
                        {
                            _activeCanvas?.Children.Remove(rect);
                        }
                    }
                    break;

                case EditTool.Shape when _activePreview is Shape:
                    // #127 Phase 3: rectangle/ellipse drag commit - see Shapes.cs.
                    CommitShapeDrag(pageIdx);
                    break;

                case EditTool.Draw when _activeInk is not null:
                    if (_drawErase)
                    {
                        // Brush eraser: partial-erase only what the brush passes over (within its radius).
                        double radius = _drawWidth / 2.0;
                        var pts = _activeInk.Points;
                        _activeCanvas?.Children.Remove(_activePreview);
                        bool Covered(Point p)
                        {
                            if (pts.Count == 1)
                                return (p - pts[0]).Length <= radius;
                            for (int i = 0; i < pts.Count - 1; i++)
                                if (DistPointToSegment(p, pts[i], pts[i + 1]) <= radius) return true;
                            return false;
                        }
                        ErasePartial(pageIdx, Covered, null, Math.Max(1.0, radius / 2.0), pts, radius);
                    }
                    else if (_activeInk.Points.Count > 2)
                    {
                        // Commit, drop the preview, and render the stroke from _annotations - the same three
                        // steps the Highlight and Line cases do. Relying on the preview to "stay" only ever
                        // worked in single-page mode; continuous re-renders the overlay from _annotations,
                        // so a committed stroke that was never rendered there just disappears.
                        AddAnnotation(_activeInk);
                        _activeCanvas?.Children.Remove(_activePreview);
                        RenderAllAnnotations(pageIdx);
                    }
                    else
                    {
                        _activeCanvas?.Children.Remove(_activePreview);
                    }
                    _activeInk = null;
                    break;

                case EditTool.Line when _activeInk is not null:
                    // Commit only a line with real length; a click without a drag is discarded.
                    if (_activeInk.Points.Count >= 2 &&
                        (Math.Abs(_activeInk.Points[1].X - _activeInk.Points[0].X) > 3 ||
                         Math.Abs(_activeInk.Points[1].Y - _activeInk.Points[0].Y) > 3))
                    {
                        AddAnnotation(_activeInk);
                        _activeCanvas?.Children.Remove(_activePreview);
                        RenderAllAnnotations(pageIdx);
                    }
                    else
                    {
                        _activeCanvas?.Children.Remove(_activePreview);
                    }
                    _activeInk = null;
                    break;

                case EditTool.Crop when _activePreview is Rectangle cr:
                    _activeCanvas?.ReleaseMouseCapture(); // MUST release before showing handles
                    if (cr.Width > 10 && cr.Height > 10)
                    {
                        // Commit the new box: drop the previous one and promote the drawn rect.
                        if (_cropPreviewRect is not null && !ReferenceEquals(_cropPreviewRect, cr))
                            (_cropPreviewRect.Parent as Panel)?.Children.Remove(_cropPreviewRect);
                        _cropPreviewRect = cr;
                        _cropCanvasRect = new Rect(Canvas.GetLeft(cr), Canvas.GetTop(cr), cr.Width, cr.Height);
                        _activePreview = null;
                        Panel.SetZIndex(_cropPreviewRect, 1); // below handles (ZIndex 10)
                        ShowCropConfirmBar();
                        return;
                    }
                    else
                    {
                        // A click without a real drag: discard the tiny drawn rect, keep the existing box,
                        // and restore the fields to it.
                        _activeCanvas?.Children.Remove(cr);
                        _activePreview = null;
                        if (_cropPreviewRect is not null)
                        {
                            _cropCanvasRect = new Rect(Canvas.GetLeft(_cropPreviewRect), Canvas.GetTop(_cropPreviewRect),
                                                       _cropPreviewRect.Width, _cropPreviewRect.Height);
                            SyncCropBoxInputs();
                        }
                    }
                    break;
            }
            _activePreview = null;
        }

        private static List<Point> DensifyPolyline(List<Point> pts, double maxSpacing)
        {
            var dense = new List<Point>();
            if (pts.Count == 0) return dense;
            dense.Add(pts[0]);
            for (int i = 1; i < pts.Count; i++)
            {
                Point a = pts[i - 1], b = pts[i];
                double d = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                int steps = Math.Max(1, (int)Math.Ceiling(d / Math.Max(0.5, maxSpacing)));
                for (int s = 1; s <= steps; s++)
                    dense.Add(new Point(a.X + (b.X - a.X) * s / steps, a.Y + (b.Y - a.Y) * s / steps));
            }
            return dense;
        }

        // The parts of rect a NOT covered by rect b (rectangle subtraction), as up to four strips. Returns
        // [a] when they don't overlap, and an empty list when b fully covers a.
        private static List<Rect> SubtractRect(Rect a, Rect b)
        {
            var res = new List<Rect>();
            var inter = Rect.Intersect(a, b);
            if (inter.IsEmpty) { res.Add(a); return res; }
            if (inter.Top > a.Top)       res.Add(new Rect(a.Left, a.Top, a.Width, inter.Top - a.Top));
            if (inter.Bottom < a.Bottom) res.Add(new Rect(a.Left, inter.Bottom, a.Width, a.Bottom - inter.Bottom));
            if (inter.Left > a.Left)     res.Add(new Rect(a.Left, inter.Top, inter.Left - a.Left, inter.Height));
            if (inter.Right < a.Right)   res.Add(new Rect(inter.Right, inter.Top, a.Right - inter.Right, inter.Height));
            return res;
        }

        // True partial eraser: remove only the parts of annotations under the eraser, not whole annotations.
        // Ink/line strokes are split at the covered points; highlight rectangles are subtracted (rect eraser
        // only - rectRegion). Text, images, signatures and covers can't be vector-erased, so they're left
        // as-is. One undoable action. 'covered' tests a single canvas point; densifySpacing controls how
        // finely strokes are resampled before testing.
        private void ErasePartial(int pageIdx, Func<Point, bool> covered, Rect? rectRegion, double densifySpacing,
                                  List<Point>? brushPath = null, double brushRadius = 0)
        {
            if (!_annotations.TryGetValue(pageIdx, out var list)) return;

            // Both erasers partial-erase VECTOR annotations only: ink strokes (split at covered points) and
            // highlights (the brush carves its circular footprint; the rect eraser subtracts its box). Text,
            // images, and signatures aren't vector regions an eraser can carve, so they're left untouched
            // (use the Select tool + Delete for those).
            bool brush = rectRegion is null;

            var result = new List<PageAnnotation>();
            bool changed = false;

            foreach (var a in list)
            {
                switch (a)
                {
                    case CoverAnnotation:
                        result.Add(a);   // paired opaque cover - leave intact
                        break;

                    case InkAnnotation ink when ink.Points.Count >= 1:
                    {
                        var dense = DensifyPolyline(ink.Points, densifySpacing);
                        if (!dense.Any(covered)) { result.Add(ink); break; }
                        changed = true;
                        var runs = new List<List<Point>>();
                        List<Point>? cur = null;
                        foreach (var p in dense)
                        {
                            if (covered(p)) { if (cur is { Count: >= 2 }) runs.Add(cur); cur = null; }
                            else (cur ??= []).Add(p);
                        }
                        if (cur is { Count: >= 2 }) runs.Add(cur);
                        foreach (var run in runs)
                            if (CloneAnnotation(ink) is InkAnnotation seg) { seg.Points = run; result.Add(seg); }
                        break;
                    }

                    case HighlightAnnotation h when rectRegion is Rect er:
                    {
                        var pieces = SubtractRect(h.Bounds, er);
                        if (pieces.Count == 1 && pieces[0] == h.Bounds) { result.Add(h); break; }
                        changed = true;
                        foreach (var pc in pieces)
                            if (pc.Width >= 1 && pc.Height >= 1 && CloneAnnotation(h) is HighlightAnnotation nh)
                            { nh.Bounds = pc; result.Add(nh); }
                        break;
                    }

                    case HighlightAnnotation h when brush:
                    {
                        if (h.Style != HighlightStyle.Fill || brushPath is not { Count: > 0 }) { result.Add(h); break; }
                        // Record the eraser stroke on the highlight (skip ones the stroke doesn't reach). The
                        // stroke is widened to the brush radius and subtracted at render/export time, giving a
                        // smooth anti-aliased hole instead of a blocky grid.
                        var hit = h.Bounds; hit.Inflate(brushRadius, brushRadius);
                        if (!brushPath.Any(p => hit.Contains(p))) { result.Add(h); break; }
                        changed = true;
                        if (CloneAnnotation(h) is HighlightAnnotation carved)
                        {
                            carved.Erases ??= [];
                            carved.Erases.Add(new HighlightErase { Points = [..brushPath], Radius = brushRadius });
                            result.Add(carved);
                        }
                        break;
                    }

                    default:
                        result.Add(a);   // text / image / signature: not vector-erasable, left for Select+Delete
                        break;
                }
            }
            if (!changed) return;
            PushPageSnapshotUndo(pageIdx);
            _annotations[pageIdx] = result;
            ClearSelection();
            RenderAllAnnotations(pageIdx);
            MarkDirty();
            SetStatus(Loc("Str_St_Erased"));
        }

        // Ctrl+A: multi-select every annotation on the pages currently on screen (single selected page,
        // or all continuous/grid tiles), so the user can see where everything is and grab stacked items.
        // Returns false when there are no annotations to select (caller falls back to text select).
        private bool SelectAllAnnotations()
        {
            List<int> pages = _pages.Count > 0
                ? [.. _pages.Keys]
                : [PageList.SelectedIndex];
            ClearSelection();
            int n = 0;
            foreach (int p in pages)
            {
                if (p < 0 || !_annotations.TryGetValue(p, out var list)) continue;
                var cv = CanvasForPage(p);
                foreach (var a in list)
                {
                    if (!IsDraggable(a)) continue;
                    var b = AnnotBounds(a);
                    if (b.IsEmpty) continue;
                    ToggleMultiSelect(a, b, cv);
                    n++;
                }
            }
            if (n > 0) SetStatus($"Selected {n} annotation{(n == 1 ? "" : "s")}");
            return n > 0;
        }

        // Select an annotation and, if it's grouped, every other member of its group - so the whole group
        // shows selected and can move together. The lead is the primary selection; the rest get outlines.
        private void SelectGroup(PageAnnotation lead)
        {
            int pg = lead.PageIndex;
            ClearSelection();
            RenderAllAnnotations(pg);
            _activeCanvas = CanvasForPage(pg);
            SelectAnnotation(lead, AnnotBounds(lead));
            // Inside a group the paired cover is itself a selected member with its own outline, so the
            // dashed "paired" hint SelectAnnotation may have drawn is redundant - and it wouldn't track the
            // group during a drag, leaving a stray dashed box behind. Drop it.
            if (_pairedCoverOutline is not null)
            {
                (_pairedCoverOutline.Parent as Canvas)?.Children.Remove(_pairedCoverOutline);
                _pairedCoverOutline = null;
            }
            if (lead.GroupId.Length > 0 && _annotations.TryGetValue(pg, out var list))
                foreach (var m in list)
                    if (!ReferenceEquals(m, lead) && m.GroupId == lead.GroupId)
                    {
                        _selectedSet.Add(m);
                        AddSelectionOutline(AnnotBounds(m), _activeCanvas);
                    }
        }

        // Rebuild the multi-selection outlines at the set members' current bounds. Used while dragging a
        // group, since RenderAllAnnotations wipes the canvas (and the outlines) on every tick.
        private void ReattachMultiOutlines()
        {
            foreach (var o in _selectionOutlines) (o.Parent as Canvas)?.Children.Remove(o);
            _selectionOutlines.Clear();
            foreach (var a in _selectedSet)
                AddSelectionOutline(AnnotBounds(a), CanvasForPage(a.PageIndex));
        }

        // Group every currently-selected annotation under a fresh group id (they move together afterward).
        // Grouping is a layer above pairing: a text/cover pair's partner is pulled in automatically so the
        // pair always groups as a unit, and ungrouping later leaves the underlying pairing untouched.
        private void GroupSelected()
        {
            var sel = new List<PageAnnotation>();
            if (_selectedAnnotation is not null) sel.Add(_selectedAnnotation);
            foreach (var a in _selectedSet) if (!sel.Contains(a)) sel.Add(a);
            foreach (var a in sel.ToList())
                if (PairPartner(a) is { } p && !sel.Contains(p)) sel.Add(p);
            if (sel.Count < 2) return;
            PushPagesSnapshotUndo(sel.Select(a => a.PageIndex));
            string gid = Guid.NewGuid().ToString("N");
            foreach (var a in sel) a.GroupId = gid;
            MarkDirty();
            SetStatus($"Grouped {sel.Count} annotations - they now move together");
        }

        // Break a group: clear the shared group id on every member.
        private void UngroupAnnotation(PageAnnotation a)
        {
            if (a.GroupId.Length == 0) return;
            PushPageSnapshotUndo(a.PageIndex);
            string gid = a.GroupId;
            if (_annotations.TryGetValue(a.PageIndex, out var list))
                foreach (var m in list) if (m.GroupId == gid) m.GroupId = "";
            MarkDirty();
            SetStatus(Loc("Str_St_Ungrouped"));
        }

        // Drop a single annotation out of its group, leaving the rest grouped. If that leaves only one
        // member, the group is meaningless, so dissolve it too. The removed item is left selected alone.
        private void RemoveFromGroup(PageAnnotation a)
        {
            if (a.GroupId.Length == 0) return;
            PushPageSnapshotUndo(a.PageIndex);
            string gid = a.GroupId;
            a.GroupId = "";
            if (_annotations.TryGetValue(a.PageIndex, out var list))
            {
                var remaining = list.Where(x => x.GroupId == gid).ToList();
                if (remaining.Count == 1) remaining[0].GroupId = "";
            }
            MarkDirty();
            ClearSelection();
            RenderAllAnnotations(a.PageIndex);
            _activeCanvas = CanvasForPage(a.PageIndex);
            SelectAnnotation(a, AnnotBounds(a));
            SetStatus(Loc("Str_St_RemovedFromGroup"));
        }

        // The selected annotation (primary or multi-select) that belongs to a text/cover pair, if any.
        private PageAnnotation? SelectedPaired()
        {
            if (_selectedAnnotation is not null && _selectedAnnotation.PairId.Length > 0) return _selectedAnnotation;
            foreach (var a in _selectedSet) if (a.PairId.Length > 0) return a;
            return null;
        }

        // Break text/cover pairing for every pair represented in the current selection - so it works for a
        // single paired item, both halves selected together, or a group containing one or more pairs. Each
        // affected cover stops rendering dashed and no longer shows when its former partner is selected.
        private void UnpairSelected()
        {
            var sel = new List<PageAnnotation>();
            if (_selectedAnnotation is not null) sel.Add(_selectedAnnotation);
            foreach (var a in _selectedSet) if (!sel.Contains(a)) sel.Add(a);
            var pids = sel.Where(a => a.PairId.Length > 0).Select(a => a.PairId).Distinct().ToHashSet();
            if (pids.Count == 0) return;
            PushPagesSnapshotUndo(sel.Select(a => a.PageIndex));

            var pages = new HashSet<int>();
            foreach (var kv in _annotations)
                foreach (var x in kv.Value)
                    if (pids.Contains(x.PairId)) { x.PairId = ""; pages.Add(kv.Key); }

            if (_pairedCoverOutline is not null)
            {
                (_pairedCoverOutline.Parent as Canvas)?.Children.Remove(_pairedCoverOutline);
                _pairedCoverOutline = null;
            }
            foreach (var p in pages) RenderAllAnnotations(p);
            ReattachSelectionVisuals();   // keep the current selection chrome after the repaint
            MarkDirty();
            SetStatus(pids.Count == 1
                ? "Unpaired - the text and cover are now separate"
                : $"Unpaired {pids.Count} text/cover pairs");
        }

        // RenderAllAnnotations rebuilds the page canvas from scratch, so the selection outline and any
        // resize handles (which live on that same canvas) must be re-added after a repaint.
        private void ReattachSelectionVisuals()
        {
            if (_selectionBorder is not null) _activeCanvas.Children.Add(_selectionBorder);
            if (_pairedCoverOutline is not null) _activeCanvas.Children.Add(_pairedCoverOutline);   // stays put during resize
            foreach (var hd in _resizeHandles) _activeCanvas.Children.Add(hd);
        }

        internal static bool IsDescendantOf(DependencyObject child, DependencyObject parent)
        {
            var current = child;
            while (current != null)
            {
                if (current == parent) return true;
                current = GetAnyParent(current);
            }
            return false;
        }

        // VisualTreeHelper.GetParent throws on non-Visuals (e.g. a Run / Hyperlink inside a TextBlock,
        // which is what the footer and About text are made of). Walk the logical tree for those and the
        // visual tree otherwise, so a click on inline text never crashes the outside-click dismissers.
        private static DependencyObject? GetAnyParent(DependencyObject d)
        {
            if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                return VisualTreeHelper.GetParent(d);
            return LogicalTreeHelper.GetParent(d);
        }

        /// <summary>
        /// Returns true if <paramref name="element"/> is inside a form field overlay control
        /// (tagged with <see cref="FormOverlayTag"/>). Used to let WPF handle mouse events
        /// for TextBox, CheckBox, RadioButton, and ComboBox controls natively.
        /// </summary>
        private static bool IsFormFieldElement(DependencyObject element)
        {
            var current = element;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.Tag as string == FormOverlayTag)
                    return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        // Abort an in-progress drag or resize and release the canvas's mouse capture. Used when the gesture
        // can't be finished normally - the mouse-up was lost because the app was backgrounded mid-drag, or
        // the window deactivated. Leaves the annotation selected at its last position so it stays usable.
        private void FinishStuckGesture()
        {
            PageAnnotation? keep = _dragAnnot;
            keep ??= _resizeTextAnnot;
            keep ??= _resizeHlAnnot;
            keep ??= _resizeInkAnnot;
            keep ??= _resizeSigAnnot;

            _isDraggingAnnot = false;
            _isResizingSig = false;
            _dragAnnot = null;
            _resizeTextAnnot = null;
            _resizeHlAnnot = null;
            _resizeInkAnnot = null;
            _resizeSigAnnot = null;
            _resizeInkOrigPoints = null;
            _dragGroupOrig.Clear();

            // Release whatever element grabbed the mouse for this gesture (and the active canvas, in case).
            if (Mouse.Captured is FrameworkElement cap) cap.ReleaseMouseCapture();
            _activeCanvas?.ReleaseMouseCapture();

            if (keep is not null)
            {
                RenderAllAnnotations(keep.PageIndex);
                SelectAnnotation(keep, AnnotBounds(keep));
                MarkDirty();
            }
        }

        private void AddAnnotation(PageAnnotation annotation)
        {
            if (!_annotations.ContainsKey(annotation.PageIndex))
                _annotations[annotation.PageIndex] = [];
            _annotations[annotation.PageIndex].Add(annotation);
            PushUndo(new UndoEntry(UndoKind.Annotation, annotation.PageIndex, WasDirty: _isDirty, Annot: annotation));
            MarkDirty();
        }

        /// <summary>
        /// Saves the current in-memory document bytes onto the undo stack so that
        /// document-level operations (crop, delete page, merge, reorder) can be undone.
        /// Must be called BEFORE modifying _doc.
        /// </summary>
        private void PushDocUndo()
        {
            if (_doc is null) return;
            using var ms = new System.IO.MemoryStream();
            _doc.Save(ms);
            PushUndo(new UndoEntry(UndoKind.Document, DocBytes: ms.ToArray(), WasDirty: _isDirty));
        }

        // Snapshot one page's annotations (deep clones) onto the undo stack so an in-place edit - erase,
        // group/ungroup, layer reorder, unpair, delete - reverts with a single Ctrl+Z. Call BEFORE mutating.
        private void PushPageSnapshotUndo(int pageIdx) => PushPagesSnapshotUndo([pageIdx]);

        // Multi-page variant (e.g. deleting a selection that spans pages).
        private void PushPagesSnapshotUndo(IEnumerable<int> pages)
        {
            var snap = new Dictionary<int, List<PageAnnotation>>();
            foreach (int p in pages.Distinct())
                if (_annotations.TryGetValue(p, out var list))
                    snap[p] = [.. list.Select(CloneAnnotation).Where(c => c is not null).Cast<PageAnnotation>()];
            if (snap.Count > 0)
                PushUndo(new UndoEntry(UndoKind.PageSnapshot, WasDirty: _isDirty, AnnotSnapshot: snap));
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            // Commit any in-progress text box first so its annotation is on the undo stack and the
            // order is deterministic. Otherwise the text box commits asynchronously (LostFocus fires
            // when a re-render clears the canvas), which races the undo and makes a press appear to
            // do nothing - the cause of "second Ctrl+Z did nothing" after placing several texts.
            if (_activeTextBox is not null)
                CommitActiveTextBox();

            if (_undoStack.Count == 0)
            {
                SetStatus(Loc("Str_St_NothingToUndo"));
                return;
            }

            var entry = _undoStack.Pop();
            // Capture what this undo is about to overwrite, so Ctrl+Y can bring it back.
            var inverse = CaptureInverse(entry);
            if (ApplyHistoryEntry(entry) && inverse is { } inv)
                _redoStack.Push(inv);
        }

        /// <summary>Redo (Ctrl+Y / Ctrl+Shift+Z): re-applies the last undone action. Any new edit
        /// clears the redo stack (see PushUndo), so redo only chains directly after undos.</summary>
        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTextBox is not null)
                CommitActiveTextBox();
            if (_redoStack.Count == 0)
            {
                SetStatus(Loc("Str_St_NothingToRedo"));
                return;
            }
            var entry = _redoStack.Pop();
            var inverse = CaptureInverse(entry);
            // Straight onto the undo stack - NOT PushUndo, which would clear the redo chain.
            if (ApplyHistoryEntry(entry) && inverse is { } inv)
                _undoStack.Push(inv);
            SetStatus(Loc("Str_St_Redone"));
        }

        /// <summary>The state that applying <paramref name="entry"/> will overwrite, captured as a
        /// history entry of its own: a document byte snapshot for document-level entries, a cloned
        /// page-annotation snapshot for every annotation-level kind. Applying the returned entry
        /// restores exactly what was there, which is what makes undo and redo symmetric.</summary>
        private UndoEntry? CaptureInverse(UndoEntry entry)
        {
            try
            {
                if (entry.Kind == UndoKind.Document)
                {
                    if (_doc is null) return null;
                    using var ms = new System.IO.MemoryStream();
                    _doc.Save(ms);
                    return new UndoEntry(UndoKind.Document, DocBytes: ms.ToArray(), WasDirty: _isDirty);
                }
                int[] pages = entry.Kind switch
                {
                    UndoKind.Annotation or UndoKind.AnnotationGroup => [entry.PageIdx],
                    UndoKind.StampBatch => entry.Pages ?? [],
                    _ => entry.AnnotSnapshot is null ? [] : [.. entry.AnnotSnapshot.Keys],
                };
                var snap = new Dictionary<int, List<PageAnnotation>>();
                foreach (int p in pages.Distinct())
                    snap[p] = _annotations.TryGetValue(p, out var list)
                        ? [.. list.Select(CloneAnnotation).Where(c => c is not null).Cast<PageAnnotation>()]
                        : [];
                return new UndoEntry(UndoKind.PageSnapshot, WasDirty: _isDirty, AnnotSnapshot: snap);
            }
            catch { return null; }   // a failed snapshot must never block the undo itself
        }

        // Applies a history entry - the shared engine behind undo AND redo. Returns false when the
        // entry cannot be applied (missing document bytes) so the caller skips pushing its inverse.
        private bool ApplyHistoryEntry(UndoEntry entry)
        {
            if (entry.Kind == UndoKind.Annotation)
            {
                int pageIdx = entry.PageIdx;
                if (_annotations.TryGetValue(pageIdx, out var pageList) && pageList.Count > 0)
                {
                    // Remove the exact annotation this entry recorded (not just the last one in the
                    // list), so undo stays correct even when annotations were re-edited or reordered.
                    if (entry.Annot is not null)
                        pageList.Remove(entry.Annot);
                    else
                        pageList.RemoveAt(pageList.Count - 1);
                }
                ClearSelection();
                RenderAllAnnotations(pageIdx);
                MarkDirty(entry.WasDirty);
                SetStatus(Loc("Str_St_UndidAnnotation"));
            }
            else if (entry.Kind == UndoKind.AnnotationGroup && entry.AnnotGroup is not null)
            {
                // A grouped edit (text cover + replacement text). Remove the exact annotations recorded,
                // so one Ctrl+Z cancels the whole edit.
                int pageIdx = entry.PageIdx;
                if (_annotations.TryGetValue(pageIdx, out var pageList))
                    foreach (var a in entry.AnnotGroup) pageList.Remove(a);
                ClearSelection();
                RenderAllAnnotations(pageIdx);
                MarkDirty(entry.WasDirty);
                SetStatus(Loc("Str_St_UndidTextEdit"));
            }
            else if (entry.Kind == UndoKind.StampBatch && entry.Pages is not null)
            {
                // Page-number stamping adds one annotation per page as a single action; remove the
                // last annotation from each stamped page in one undo.
                foreach (int p in entry.Pages)
                    if (_annotations.TryGetValue(p, out var list) && list.Count > 0)
                        list.RemoveAt(list.Count - 1);
                ClearSelection();
                foreach (int p in entry.Pages)
                    if (_pages.ContainsKey(p))   // authoritative map: covers the primary tile in Grid/Two-Page too
                        RenderAllAnnotations(p);
                MarkDirty(entry.WasDirty);
                SetStatus(Loc("Str_St_RemovedStamps"));
            }
            else if (entry.Kind == UndoKind.ClearAnnotations && entry.AnnotSnapshot is not null)
            {
                // Restore every page's annotations that the clear removed, in one undo.
                foreach (var kv in entry.AnnotSnapshot)
                    _annotations[kv.Key] = [.. kv.Value];
                ClearSelection();
                RenderAnnotationsOnAllVisiblePages();
                MarkDirty(entry.WasDirty);
                SetStatus(Loc("Str_St_RestoredAnnotations"));
            }
            else if (entry.Kind == UndoKind.PageSnapshot && entry.AnnotSnapshot is not null)
            {
                // Restore the affected page(s) to their pre-edit state - reverts erase, group/ungroup,
                // layer reorder, unpair or delete in one Ctrl+Z. The snapshot holds deep clones.
                foreach (var kv in entry.AnnotSnapshot)
                    _annotations[kv.Key] = [.. kv.Value];
                ClearSelection();
                RenderAnnotationsOnAllVisiblePages();
                MarkDirty(entry.WasDirty);
                SetStatus(Loc("Str_St_Undone"));
            }
            else // Document snapshot
            {
                if (entry.DocBytes is null) return false;
                int selectedIdx = PageList.SelectedIndex;
                var tempPath = App.MakeTempFile("undo");
                System.IO.File.WriteAllBytes(tempPath, entry.DocBytes);
                _doc?.Close();
                // PdfSharpCore can write a snapshot whose xref offset points at the xref table,
                // producing "Unexpected token 'xref'" on reopen. Repair via Import (preserves
                // rotations) then PDFium, mirroring the save/reload path, instead of crashing.
                try
                {
                    _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
                }
                catch (Exception undoOpenEx) when (PdfImport.IsXRefException(undoOpenEx))
                {
                    var fixedPath = App.MakeTempFile("undofixed");
                    if (!PdfImport.TryImportRepairToPath(tempPath, fixedPath)
                        && !PdfiumInterop.TryPdfiumSaveWithZeroRotations(tempPath, fixedPath))
                        throw;
                    tempPath = fixedPath;
                    _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
                }
                _currentFile = tempPath;
                _annotations.Clear();
                _renderDims.Clear();
                ClearSelection();
                MarkDirty(entry.WasDirty);
                RefreshPageList();
                LoadOutlines();   // #133: bookmark edits ride this undo path, and page-level undos can change the outline too
                if (selectedIdx >= 0 && selectedIdx < PageList.Items.Count)
                    PageList.SelectedIndex = selectedIdx;
                else if (PageList.Items.Count > 0)
                    PageList.SelectedIndex = 0;
                // Re-render the current view so the main page(s) reflect the restored document.
                // RefreshPageList only updates the sidebar, and re-selecting the same page does not
                // fire SelectionChanged, so grid/two-page tiles would otherwise stay stale.
                int reIdx = PageList.SelectedIndex;
                if (_viewMode == ViewMode.Continuous)
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                        (Action)(() => SetupContinuousView(reIdx)));
                else
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
                    {
                        RenderPage(_viewMode == ViewMode.Grid ? 0 : reIdx);
                        ReapplyGridOrFit();
                    }));
                SetStatus(Loc("Str_St_UndidDocChange"));
            }
            return true;
        }

        // Every NEW user action pushes history through here: fresh edits invalidate the redo chain
        // (the branched future can no longer be replayed safely).
        private void PushUndo(UndoEntry entry)
        {
            _undoStack.Push(entry);
            _redoStack.Clear();
        }

        // Re-renders annotations on every page that currently has a visible surface: each overlay
        // tracked by a multi-page view, plus the single-page canvas. RenderAllAnnotations re-adds
        // form fields, so forms survive the refresh.
        private void RenderAnnotationsOnAllVisiblePages()
        {
            if (_continuousCanvases.Count > 0)
                foreach (var p in _continuousCanvases.Keys.ToList())
                    RenderAllAnnotations(p);
            else if (PageList.SelectedIndex >= 0)
                RenderAllAnnotations(PageList.SelectedIndex);
        }

        // Context-menu "Clear Page Annotations": removes annotations on the current page only.
        private void ClearAnnotations_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTextBox is not null) CommitActiveTextBox();
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;
            if (_annotations.TryGetValue(pageIdx, out var list) && list.Count > 0)
            {
                // Snapshot this page so the clear is a single undo, then drop the annotations.
                var snap = new Dictionary<int, List<PageAnnotation>> { [pageIdx] = [.. list] };
                PushUndo(new UndoEntry(UndoKind.ClearAnnotations, WasDirty: _isDirty, AnnotSnapshot: snap));
                _annotations.Remove(pageIdx);
                MarkDirty();
            }
            ClearSelection();
            // Redraw the page on whichever surface it lives on (overlay in multi-page views, the
            // single canvas otherwise) so the cleared page actually updates in continuous/grid mode.
            RenderAllAnnotations(pageIdx);
            SetStatus(Loc("Str_St_ClearedPageAnnotations"));
        }

        // Toolbar "Clear All Annotations": removes annotations across the whole document in one undo.
        private void ClearAllAnnotations_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) return;
            if (_activeTextBox is not null) CommitActiveTextBox();

            int total = _annotations.Values.Sum(l => l.Count);
            if (total == 0) { SetStatus(Loc("Str_St_NoAnnotationsToClear")); return; }

            // Snapshot every page so a single Ctrl+Z restores the whole document's annotations.
            var snapshot = new Dictionary<int, List<PageAnnotation>>();
            foreach (var kv in _annotations)
                if (kv.Value.Count > 0) snapshot[kv.Key] = [.. kv.Value];
            PushUndo(new UndoEntry(UndoKind.ClearAnnotations, WasDirty: _isDirty, AnnotSnapshot: snapshot));

            _annotations.Clear();
            ClearSelection();
            RenderAnnotationsOnAllVisiblePages();
            MarkDirty();
            SetStatus($"Cleared all annotations ({total})");
        }

        // onlyPage: when set, burns just that one page's annotations (used by Transform, which rasterizes a
        // single page and wants its annotations baked in). Default null burns every page, as before.
        private void DrawAnnotationsOnDocument(int? onlyPage = null)
            => PdfBurn.DrawAnnotationsIntoDoc(_doc, _annotations, _renderDims, onlyPage, _pageRotations);

        // The burn core (DrawAnnotationsIntoDoc) lives in Services/PdfBurn.cs.
    }
}
