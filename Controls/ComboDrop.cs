#nullable enable
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KillerFamily.Layout
{
    // The dark combo's open list joins its box flush. OpensUp puts the list above the box, for a combo
    // at the bottom of its window; the template reads it for the placement, the shadow room and the
    // 1px overlap. SquareTop and SquareBottom take the theme's corner radius and square the corners
    // on the side where the box and the list meet, so the list comes straight off the box.
    public static class ComboDrop
    {
        public static readonly DependencyProperty OpensUpProperty = DependencyProperty.RegisterAttached(
            "OpensUp", typeof(bool), typeof(ComboDrop), new PropertyMetadata(false));

        public static bool GetOpensUp(DependencyObject target) => (bool)target.GetValue(OpensUpProperty);
        public static void SetOpensUp(DependencyObject target, bool value) => target.SetValue(OpensUpProperty, value);

        public static readonly IValueConverter SquareTop = new Square(top: true);
        public static readonly IValueConverter SquareBottom = new Square(top: false);

        private sealed class Square : IValueConverter
        {
            private readonly bool _top;
            internal Square(bool top) => _top = top;

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                var r = value is CornerRadius radius ? radius : new CornerRadius();
                return _top ? new CornerRadius(0, 0, r.BottomRight, r.BottomLeft) : new CornerRadius(r.TopLeft, r.TopRight, 0, 0);
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
                => Binding.DoNothing;
        }
    }
}
