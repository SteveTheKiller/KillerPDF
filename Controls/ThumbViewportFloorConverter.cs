using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace KillerPDF
{
    // Keeps the document scrollbar reactive (thumb sized to the visible proportion) while
    // guaranteeing it never shrinks below a grabbable floor.
    //
    // WPF's Track sizes the thumb AND the repeat buttons from the raw proportional value
    // (trackLen * viewport / (range + viewport)). Thumb.MinHeight does NOT feed that math - it
    // only stretches the thumb's render, so on a long document the thumb overflows its tiny
    // proportional slot and the increase RepeatButton paints over the overflow (the "8px"
    // scrollbar). Enforcing the minimum here, by raising the ViewportSize the Track sees, makes
    // the Track size the thumb and the buttons from the same floored value - no overflow, no
    // overlap, still proportional whenever there is room.
    //
    // Bindings (in order): ViewportSize, Maximum, Minimum, ActualWidth, ActualHeight, Orientation.
    // ConverterParameter: the floor in pixels (default 64).
    public sealed class ThumbViewportFloorConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            double vp = AsDouble(values, 0);
            // Anything unexpected -> hand back the real ViewportSize so behaviour is unchanged.
            if (double.IsNaN(vp) || vp <= 0) return vp;

            double max     = AsDouble(values, 1);
            double min     = AsDouble(values, 2);
            double width   = AsDouble(values, 3);
            double height  = AsDouble(values, 4);
            bool vertical  = values.Length <= 5 || values[5] is not Orientation o
                                 || o == Orientation.Vertical;

            double trackLen = vertical ? height : width;
            double range    = max - min;
            if (double.IsNaN(trackLen) || trackLen <= 0 || range <= 0) return vp;

            double floor = 64;
            if (parameter != null &&
                double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p) && p > 0)
                floor = p;

            // Never let the floor eat the whole track; leave room for the thumb to travel.
            floor = Math.Min(floor, trackLen * 0.5);
            if (trackLen <= floor) return vp;

            // ViewportSize that yields a thumb exactly = floor; take the larger of it and the real VP.
            double vpForFloor = floor * range / (trackLen - floor);
            return Math.Max(vp, vpForFloor);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static double AsDouble(object[] values, int i)
        {
            if (values == null || i >= values.Length) return double.NaN;
            object v = values[i];
            if (v == null || v == DependencyProperty.UnsetValue) return double.NaN;
            try { return System.Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return double.NaN; }
        }
    }
}
