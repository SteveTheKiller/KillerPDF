using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace KillerPDF.Services;

/// <summary>
/// A line of text centred on its line box sits low: the box keeps room for accents above the capitals and
/// for descenders below, and the two differ. This is how far to lift the line so its capitals sit at the
/// middle instead, the way text in a form box is read as centred.
/// </summary>
internal static class TextCapCentering
{
    internal static double Lift(FontFamily family, double fontSize)
    {
        if (!double.IsFinite(fontSize) || fontSize <= 0) return 0;
        double capHeight = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).CapsHeight;
        double capMiddle = family.Baseline - capHeight / 2;
        double lift = (capMiddle - family.LineSpacing / 2) * fontSize;
        return double.IsFinite(lift) ? lift : 0;
    }

    /// <summary>Padding for a single-line text box whose content is centred vertically, moving the line up
    /// by the lift without changing the content height, so nothing is clipped. The form-field TextBox
    /// applies its padding twice (the template's content host margin and its own text view), so half
    /// the lift is given. Binds FontSize and FontFamily.</summary>
    internal sealed class PaddingConverter(double left, double right) : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] is not double size || values[1] is not FontFamily family)
                return new Thickness(left, 0, right, 0);
            double lift = Lift(family, size) / 2;
            return new Thickness(left, -lift, right, lift);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
