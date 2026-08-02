using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KillerPDF
{
    public partial class MainWindow
    {
        // ============================================================
        // Shapes tool (#127 Phase 3)
        // ============================================================
        // Three sub-modes (ShapeKind): Rectangle drags out the translucent filled box the old
        // rectangle-highlight gesture used to make (a HighlightAnnotation, so move/resize/erase/
        // flatten/undo all behave exactly as before); Ellipse drags out an outline ellipse; and
        // Polygon places vertices click by click, closing on the first vertex (lit snap target),
        // on double-click, or via Enter - Esc cancels, Backspace removes the last vertex.
        // Ellipse and Polygon commit as CLOSED InkAnnotations (the last point repeats the first),
        // so they ride the existing ink pipeline: render, hit-test, drag, resize, flatten, export.
        // All three draw with the shared draw bar's color/size/opacity (_drawColor/_drawWidth).

        private ShapeKind _shapeKind = ShapeKind.Rectangle;
        private bool _shapeFill = true;   // fill the inside; toggled in the shape bar

        // In-progress polygon state. _shapePolyPoints non-empty = a polygon is being placed.
        private readonly List<Point> _shapePolyPoints = [];
        private Polyline? _shapePolyPreview;    // committed vertices
        private Polyline? _shapePolyRubber;     // last vertex -> cursor, dashed closing preview
        private Ellipse? _shapePolySnapDot;     // lit ring over the first vertex when close enough to close
        private int _shapePolyPage = -1;
        private Canvas? _shapePolyCanvas;

        private const double ShapeSnapPx = 8;   // close the polygon when a click lands this near the start

        /// <summary>Interior color for filled ellipse/polygon shapes: the stroke color at half its
        /// alpha, so the edge still reads against the fill (the filled Box keeps the legacy
        /// full-alpha HighlightAnnotation look instead).</summary>
        private Color ShapeFillColor()
            => Color.FromArgb((byte)Math.Max(20, _drawColor.A / 2), _drawColor.R, _drawColor.G, _drawColor.B);

        /// <summary>Mouse-down entry for the Shapes tool (called from Canvas_MouseLeftButtonDown).</summary>
        private void ShapeToolMouseDown(int pageIdx, Point pos, MouseButtonEventArgs e)
        {
            if (_shapeKind == ShapeKind.Polygon)
            {
                // Double-click closes an in-progress polygon (the first click of the pair already
                // added a vertex at this spot; the commit dedupes trailing repeats).
                if (e.ClickCount == 2 && _shapePolyPoints.Count >= 3) CommitShapePolygon();
                else ShapePolyClick(pageIdx, pos);
                e.Handled = true;
                return;
            }

            // Rectangle / Ellipse: drag out a preview, commit on release (CommitShapeDrag).
            ClearSelection();
            _isDrawing = true;
            _drawStart = pos;
            Shape preview;
            if (_shapeKind == ShapeKind.Rectangle && _shapeFill)
            {
                // The old rectangle-highlight look: translucent fill, no stroke.
                preview = new Rectangle { Fill = new SolidColorBrush(_drawColor) };
            }
            else
            {
                preview = _shapeKind == ShapeKind.Rectangle ? new Rectangle() : new Ellipse();
                preview.Stroke = new SolidColorBrush(_drawColor);
                preview.StrokeThickness = _drawWidth;
                preview.Fill = _shapeKind == ShapeKind.Ellipse && _shapeFill
                    ? new SolidColorBrush(ShapeFillColor())
                    : Brushes.Transparent;
            }
            preview.Width = 0;
            preview.Height = 0;
            Canvas.SetLeft(preview, pos.X);
            Canvas.SetTop(preview, pos.Y);
            _activeCanvas.Children.Add(preview);
            _activePreview = preview;
            _activeCanvas.CaptureMouse();
            e.Handled = true;
        }

        /// <summary>Mouse-up commit for the Rectangle / Ellipse drag (called from
        /// Canvas_MouseLeftButtonUp). The preview element carries the final geometry.</summary>
        private void CommitShapeDrag(int pageIdx)
        {
            if (_activePreview is not Shape shp) return;
            double x = Canvas.GetLeft(shp), y = Canvas.GetTop(shp);
            double w = shp.Width, h = shp.Height;
            _activeCanvas?.Children.Remove(shp);
            if (w <= 3 || h <= 3) return;   // click or a sliver - nothing to keep

            if (shp is Rectangle && _shapeFill)
            {
                // Same annotation the old rectangle-highlight gesture produced.
                var ha = new HighlightAnnotation
                {
                    PageIndex = pageIdx,
                    Bounds = new Rect(x, y, w, h),
                    Style = HighlightStyle.Fill
                };
                ha.SetColor(_drawColor);
                AddAnnotation(ha);
            }
            else if (shp is Rectangle)
            {
                // Outline box: a closed 4-corner ink stroke.
                var ink = new InkAnnotation { PageIndex = pageIdx, StrokeWidth = _drawWidth };
                ink.SetColor(_drawColor);
                ink.Points.Add(new Point(x, y));
                ink.Points.Add(new Point(x + w, y));
                ink.Points.Add(new Point(x + w, y + h));
                ink.Points.Add(new Point(x, y + h));
                ink.Points.Add(new Point(x, y));
                AddAnnotation(ink);
            }
            else
            {
                // Ellipse: a closed 64-segment ink stroke centered in the drag rect.
                var ink = new InkAnnotation { PageIndex = pageIdx, StrokeWidth = _drawWidth };
                ink.SetColor(_drawColor);
                if (_shapeFill) ink.SetFillColor(ShapeFillColor());
                double cx = x + w / 2, cy = y + h / 2, rx = w / 2, ry = h / 2;
                for (int k = 0; k <= 64; k++)
                {
                    double a = k * 2 * Math.PI / 64;
                    ink.Points.Add(new Point(cx + rx * Math.Cos(a), cy + ry * Math.Sin(a)));
                }
                AddAnnotation(ink);
            }
            RenderAllAnnotations(pageIdx);
        }

        /// <summary>One polygon click: start the shape, add a vertex, or close when the click lands
        /// on the start vertex's snap target.</summary>
        private void ShapePolyClick(int pageIdx, Point pos)
        {
            if (_activeCanvas is null) return;

            if (_shapePolyPoints.Count == 0)
            {
                ClearSelection();
                _shapePolyPage = pageIdx;
                _shapePolyCanvas = _activeCanvas;

                _shapePolyPreview = new Polyline
                {
                    Stroke = new SolidColorBrush(_drawColor),
                    StrokeThickness = _drawWidth,
                    StrokeLineJoin = PenLineJoin.Round,
                    IsHitTestVisible = false
                };
                _shapePolyPreview.Points.Add(pos);

                _shapePolyRubber = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromArgb((byte)Math.Max(60, _drawColor.A / 2),
                                                                _drawColor.R, _drawColor.G, _drawColor.B)),
                    StrokeThickness = Math.Max(1, _drawWidth / 2),
                    StrokeDashArray = [4, 3],
                    IsHitTestVisible = false
                };
                _shapePolyRubber.Points.Add(pos);
                _shapePolyRubber.Points.Add(pos);

                // Snap ring over the start vertex - hidden until the polygon can actually close.
                _shapePolySnapDot = new Ellipse
                {
                    Width = 14,
                    Height = 14,
                    StrokeThickness = 2,
                    Fill = Brushes.Transparent,
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed
                };
                _shapePolySnapDot.SetResourceReference(Shape.StrokeProperty, "SelectionAccent");
                Canvas.SetLeft(_shapePolySnapDot, pos.X - 7);
                Canvas.SetTop(_shapePolySnapDot, pos.Y - 7);

                _shapePolyCanvas.Children.Add(_shapePolyPreview);
                _shapePolyCanvas.Children.Add(_shapePolyRubber);
                _shapePolyCanvas.Children.Add(_shapePolySnapDot);
                _shapePolyPoints.Add(pos);
                SetStatus("Click to add points - click the first point or double-click to close, Esc cancels, Backspace removes the last point");
                return;
            }

            if (pageIdx != _shapePolyPage) return;   // the polygon stays on the page it started on

            if (_shapePolyPoints.Count >= 3 && (pos - _shapePolyPoints[0]).Length <= ShapeSnapPx)
            {
                CommitShapePolygon();
                return;
            }

            _shapePolyPoints.Add(pos);
            _shapePolyPreview!.Points.Add(pos);
            _shapePolyRubber!.Points[0] = pos;
        }

        /// <summary>Mouse-move while a polygon is being placed: rubber-band from the last vertex,
        /// and light the snap ring when the cursor is close enough to the start to close.</summary>
        private void UpdateShapePolyRubber(MouseEventArgs e)
        {
            if (_shapePolyCanvas is null || _shapePolyRubber is null) return;
            var p = e.GetPosition(_shapePolyCanvas);
            _shapePolyRubber.Points[_shapePolyRubber.Points.Count - 1] = p;
            if (_shapePolySnapDot is not null)
                _shapePolySnapDot.Visibility =
                    _shapePolyPoints.Count >= 3 && (p - _shapePolyPoints[0]).Length <= ShapeSnapPx
                        ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Commit the in-progress polygon as a closed ink stroke.</summary>
        private void CommitShapePolygon()
        {
            int page = _shapePolyPage;
            var pts = new List<Point>(_shapePolyPoints);
            ResetShapePolyState();

            // Drop trailing points that repeat the last committed vertex (a double-click close adds
            // one at the same spot as the click before it).
            while (pts.Count >= 2 && (pts[pts.Count - 1] - pts[pts.Count - 2]).Length < 2) pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3 || page < 0) return;

            var ink = new InkAnnotation { PageIndex = page, StrokeWidth = _drawWidth };
            ink.SetColor(_drawColor);
            if (_shapeFill) ink.SetFillColor(ShapeFillColor());
            foreach (var p in pts) ink.Points.Add(p);
            ink.Points.Add(pts[0]);   // close
            AddAnnotation(ink);
            RenderAllAnnotations(page);
        }

        /// <summary>Esc: abandon the in-progress polygon. Safe no-op when none is active.</summary>
        private void CancelShapePolygon()
        {
            if (_shapePolyPoints.Count == 0) return;
            ResetShapePolyState();
            SetStatus("Shape cancelled");
        }

        /// <summary>Backspace: remove the last placed vertex; removing the only one cancels.</summary>
        private void ShapePolyBackspace()
        {
            if (_shapePolyPoints.Count == 0) return;
            if (_shapePolyPoints.Count == 1) { CancelShapePolygon(); return; }
            _shapePolyPoints.RemoveAt(_shapePolyPoints.Count - 1);
            _shapePolyPreview!.Points.RemoveAt(_shapePolyPreview.Points.Count - 1);
            _shapePolyRubber!.Points[0] = _shapePolyPoints[_shapePolyPoints.Count - 1];
        }

        private void ResetShapePolyState()
        {
            if (_shapePolyCanvas is not null)
            {
                if (_shapePolyPreview is not null) _shapePolyCanvas.Children.Remove(_shapePolyPreview);
                if (_shapePolyRubber is not null) _shapePolyCanvas.Children.Remove(_shapePolyRubber);
                if (_shapePolySnapDot is not null) _shapePolyCanvas.Children.Remove(_shapePolySnapDot);
            }
            _shapePolyPoints.Clear();
            _shapePolyPreview = null;
            _shapePolyRubber = null;
            _shapePolySnapDot = null;
            _shapePolyPage = -1;
            _shapePolyCanvas = null;
        }
    }
}
