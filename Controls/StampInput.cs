using System;
using System.Globalization;

namespace KillerPDF.Controls
{
    /// <summary>
    /// How the Stamp window reads what the user typed: font sizes parsed in the current culture and
    /// held to the slider's range, and the watermark font kept when it is not installed here.
    /// Pure, so the rules can be checked without a window.
    /// </summary>
    internal static class StampInput
    {
        public const double NumFontMin = 6, NumFontMax = 96;
        public const double WmFontMin = 12, WmFontMax = 200;

        /// <summary>A plain decimal number in the given culture. A thousands separator is not
        /// accepted, so "1,5" under en-US is rejected rather than read as 15.</summary>
        public static bool TryParseNumber(string? text, CultureInfo culture, out double value)
            => double.TryParse(text?.Trim(), NumberStyles.Float, culture, out value);

        /// <summary>A typed size, positive and held to [min, max]; the fallback when it cannot be read.</summary>
        public static double ReadSize(string? text, CultureInfo culture, double fallback, double min, double max)
            => TryParseNumber(text, culture, out double d) && d > 0 ? Math.Max(min, Math.Min(max, d)) : fallback;

        /// <summary>The font to store: the one chosen, else the one the stamp already had.</summary>
        public static string FontToKeep(string? selected, string saved)
            => !string.IsNullOrEmpty(selected) ? selected : !string.IsNullOrEmpty(saved) ? saved : "Segoe UI";
    }
}

