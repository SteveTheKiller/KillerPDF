#nullable enable
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace KillerFamily.Layout
{
    /// <summary>
    /// Centers a radio ring or checkbox square on its label's capital letters, for whatever font
    /// the label is drawn in. Bind the indicator's Margin to the row's FontFamily and FontSize;
    /// ConverterParameter is the margin it would have with no correction ("0,0,8,0").
    /// </summary>
    /// <remarks>
    /// Centering the indicator in the row puts it on the middle of the text's LINE BOX, and where
    /// the capitals sit inside that box is a property of the font. Killer Sans UI sits low (the
    /// ring has to come down a pixel at 12px); Microsoft Sans Serif, 98SE's face, sits in the
    /// middle. A fixed pixel nudge is right for one face at one size and wrong for the other,
    /// which is how this kept drifting from one fix to the next. The offset here comes from a
    /// capital H laid out exactly as the label is, through the same font fallback, so it holds for
    /// every theme, every language fallback and every app size. Reading the family's own metrics
    /// is not enough: for a fallback list such as "Killer Sans UI, Segoe UI" WPF reports the
    /// metrics of Segoe UI while the label draws in Killer Sans UI.
    /// </remarks>
    public sealed class CapCenterMargin : IMultiValueConverter
    {
        public static readonly CapCenterMargin Instance = new();
        /// <summary>The reverse: moves a LABEL so its capitals sit on the line box's middle, where a plainly centered indicator is.</summary>
        public static readonly CapCenterMargin Lift = new() { _sign = -1 };
        private double _sign = 1;
        private static readonly System.Collections.Generic.Dictionary<string, double> Cache = new();

        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            Thickness margin = ParseMargin(parameter as string);
            if (values.Length < 2 || values[0] is not FontFamily family || values[1] is not double size)
                return margin;

            double raw = Offset(family, size);
            // A lifted label snaps to whole pixels, so lift it the whole way: rounding down left the capitals a pixel low.
            // The measured offset carries float noise: Killer Sans UI at 12px reads 1.0002, and a bare Ceiling made that 2 and lifted every label a pixel too high.
            double shift = _sign < 0 ? -Math.Ceiling(raw - 0.05) : raw;
            return new Thickness(margin.Left, margin.Top + shift, margin.Right, margin.Bottom - shift);
        }

        /// <summary>How far below the line box's middle the middle of the capitals sits, in px.</summary>
        public static double Offset(FontFamily family, double size)
        {
            string key = (family.BaseUri?.ToString() ?? "") + "|" + family.Source + "|" + size.ToString("R", CultureInfo.InvariantCulture);
            lock (Cache)
                if (Cache.TryGetValue(key, out double cached)) return cached;
            double shift = 0;
            try
            {
                var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var text = new FormattedText("H", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, size, Brushes.Black, 1.0);
                Rect ink = text.BuildGeometry(new Point(0, 0)).Bounds;
                if (!ink.IsEmpty) shift = (ink.Top + ink.Bottom) / 2 - text.Height / 2;
                if (double.IsNaN(shift) || double.IsInfinity(shift)) shift = 0;
            }
            catch { shift = 0; }   // an unreadable font keeps the plain margin rather than failing layout
            lock (Cache) Cache[key] = shift;
            return shift;
        }

        private static Thickness ParseMargin(string? text)
        {
            if (string.IsNullOrEmpty(text)) return new Thickness(0, 0, 8, 0);
            try { return (Thickness)new ThicknessConverter().ConvertFromInvariantString(text!)!; }
            catch { return new Thickness(0, 0, 8, 0); }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
