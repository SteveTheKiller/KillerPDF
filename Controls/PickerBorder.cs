using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerPDF.Controls
{
    // Clip content to the inner stroke edge without clipping the border's own paint.
    public sealed class PickerBorder : Border
    {
        private UIElement? _clippedChild;
        private Geometry? _originalClip;
        private Geometry? _appliedClip;

        protected override Size ArrangeOverride(Size finalSize)
        {
            Size result = base.ArrangeOverride(finalSize);
            UIElement? child = Child;
            if (_clippedChild != child)
            {
                if (_clippedChild != null && _clippedChild.Clip == _appliedClip)
                    _clippedChild.SetCurrentValue(ClipProperty, _originalClip);
                _clippedChild = child;
                _originalClip = child?.Clip;
            }
            if (child == null) return result;
            if (child.Clip != _appliedClip) _originalClip = child.Clip;

            // Snap the inset to whole device pixels at the scale this border really draws at. An
            // app-size zoom is a scale transform above the border, and it turns layout rounding
            // off, so a 1 unit stroke at 110% put the clip edge 1.1 pixels in: the part-covered row
            // let the border's own background show as a line where a tab joins the page.
            Thickness stroke = BorderThickness;
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            double scaleX = dpi.DpiScaleX, scaleY = dpi.DpiScaleY;
            if (PresentationSource.FromVisual(this)?.RootVisual is Visual root && root != this && IsDescendantOf(root)
                && TransformToAncestor(root) is MatrixTransform zoom)
            {
                scaleX *= Math.Abs(zoom.Matrix.M11);
                scaleY *= Math.Abs(zoom.Matrix.M22);
            }
            if (UseLayoutRounding || scaleX != dpi.DpiScaleX || scaleY != dpi.DpiScaleY)
            {
                stroke = new Thickness(Round(stroke.Left, scaleX), Round(stroke.Top, scaleY),
                    Round(stroke.Right, scaleX), Round(stroke.Bottom, scaleY));
            }
            Vector offset = VisualTreeHelper.GetOffset(child);
            var bounds = new Rect(stroke.Left - offset.X, stroke.Top - offset.Y,
                Math.Max(0, finalSize.Width - stroke.Left - stroke.Right),
                Math.Max(0, finalSize.Height - stroke.Top - stroke.Bottom));
            CornerRadius radius = CornerRadius;
            Geometry clip = RoundedGeometry(bounds,
                new Size(Math.Max(0, radius.TopLeft - stroke.Left / 2), Math.Max(0, radius.TopLeft - stroke.Top / 2)),
                new Size(Math.Max(0, radius.TopRight - stroke.Right / 2), Math.Max(0, radius.TopRight - stroke.Top / 2)),
                new Size(Math.Max(0, radius.BottomRight - stroke.Right / 2), Math.Max(0, radius.BottomRight - stroke.Bottom / 2)),
                new Size(Math.Max(0, radius.BottomLeft - stroke.Left / 2), Math.Max(0, radius.BottomLeft - stroke.Bottom / 2)));
            if (_originalClip != null)
                clip = new CombinedGeometry(GeometryCombineMode.Intersect, _originalClip, clip);
            clip.Freeze();
            _appliedClip = clip;
            child.SetCurrentValue(ClipProperty, clip);
            return result;
        }

        private static double Round(double value, double scale) => Math.Round(value * scale) / scale;

        internal static StreamGeometry RoundedGeometry(Rect bounds, Size tl, Size tr, Size br, Size bl)
        {
            double x = bounds.X, y = bounds.Y, w = bounds.Width, h = bounds.Height;
            Fit(ref tl, ref tr, w, true);
            Fit(ref bl, ref br, w, true);
            Fit(ref tl, ref bl, h, false);
            Fit(ref tr, ref br, h, false);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(x + tl.Width, y), true, true);
                context.LineTo(new Point(x + w - tr.Width, y), true, false);
                Corner(context, new Point(x + w, y + tr.Height), tr);
                context.LineTo(new Point(x + w, y + h - br.Height), true, false);
                Corner(context, new Point(x + w - br.Width, y + h), br);
                context.LineTo(new Point(x + bl.Width, y + h), true, false);
                Corner(context, new Point(x, y + h - bl.Height), bl);
                context.LineTo(new Point(x, y + tl.Height), true, false);
                Corner(context, new Point(x + tl.Width, y), tl);
            }
            return geometry;
        }

        private static void Fit(ref Size first, ref Size second, double length, bool horizontal)
        {
            double a = horizontal ? first.Width : first.Height;
            double b = horizontal ? second.Width : second.Height;
            if (a + b <= length) return;
            double ratio = length / (a + b);
            if (horizontal) { first.Width = a * ratio; second.Width = b * ratio; }
            else { first.Height = a * ratio; second.Height = b * ratio; }
        }

        private static void Corner(StreamGeometryContext context, Point end, Size radius)
        {
            if (radius.Width == 0 || radius.Height == 0) context.LineTo(end, true, false);
            else context.ArcTo(end, radius, 0, false, SweepDirection.Clockwise, true, false);
        }
    }
}
