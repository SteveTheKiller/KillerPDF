using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerPDF.Controls
{
    // Border rounds its own paint but does not clip child layers to those corners.
    public sealed class PickerBorder : Border
    {
        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == CornerRadiusProperty) InvalidateArrange();
        }

        protected override Geometry GetLayoutClip(Size layoutSlotSize)
        {
            double w = RenderSize.Width, h = RenderSize.Height;
            double limit = Math.Min(w, h) / 2;
            double tl = Math.Min(CornerRadius.TopLeft, limit), tr = Math.Min(CornerRadius.TopRight, limit);
            double br = Math.Min(CornerRadius.BottomRight, limit), bl = Math.Min(CornerRadius.BottomLeft, limit);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(tl, 0), true, true);
                context.LineTo(new Point(w - tr, 0), true, false);
                Corner(context, new Point(w, tr), tr);
                context.LineTo(new Point(w, h - br), true, false);
                Corner(context, new Point(w - br, h), br);
                context.LineTo(new Point(bl, h), true, false);
                Corner(context, new Point(0, h - bl), bl);
                context.LineTo(new Point(0, tl), true, false);
                Corner(context, new Point(tl, 0), tl);
            }
            geometry.Freeze();
            return geometry;
        }

        private static void Corner(StreamGeometryContext context, Point end, double radius)
        {
            if (radius == 0) context.LineTo(end, true, false);
            else context.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
        }
    }
}
