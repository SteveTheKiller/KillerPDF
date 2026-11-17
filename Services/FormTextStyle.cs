using System.Windows.Media;

namespace KillerPDF.Services;

/// <summary>
/// The look given to a text field's value, kept per field name like its font size: a font family (null keeps
/// the field's own), bold, italic, a text color as "#RRGGBB" (null keeps the field's own), underline and
/// strikethrough.
/// </summary>
internal sealed record FormTextStyle(string? FontFamily = null, bool Bold = false, bool Italic = false,
    string? TextColor = null, bool Underline = false, bool StrikeThrough = false)
{
    /// <summary>The family saved when a field is made bold or italic without choosing one: the face a
    /// form's standard Helvetica stands for.</summary>
    internal const string DefaultFamily = "Arial";

    /// <summary>Whether saving embeds a font for this style.</summary>
    internal bool HasFont => FontFamily is not null || Bold || Italic;

    internal Color? MediaColor
    {
        get
        {
            if (TextColor is null) return null;
            try { return (Color)ColorConverter.ConvertFromString(TextColor); }
            catch (FormatException) { return null; }
        }
    }

    internal static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
