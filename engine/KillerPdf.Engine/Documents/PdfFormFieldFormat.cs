using System.Globalization;
using System.Text;

namespace KillerPdf.Engine.Documents;

/// <summary>The digit grouping and decimal separator pair used by AcroForm number formats.</summary>
public enum PdfFormSeparatorStyle
{
    /// <summary>Comma grouping with a point decimal separator, such as 1,234.56.</summary>
    CommaGroupPointDecimal = 0,
    /// <summary>No grouping with a point decimal separator, such as 1234.56.</summary>
    PointDecimalOnly = 1,
    /// <summary>Point grouping with a comma decimal separator, such as 1.234,56.</summary>
    PointGroupCommaDecimal = 2,
    /// <summary>No grouping with a comma decimal separator, such as 1234,56.</summary>
    CommaDecimalOnly = 3,
    /// <summary>Apostrophe grouping with a point decimal separator, such as 1'234.56.</summary>
    ApostropheGroupPointDecimal = 4
}

/// <summary>The presentation applied to negative AcroForm number values.</summary>
public enum PdfFormNegativeStyle
{
    /// <summary>A leading minus sign in the normal text color.</summary>
    MinusBlack = 0,
    /// <summary>A leading minus sign in red.</summary>
    MinusRed = 1,
    /// <summary>Surrounding parentheses in the normal text color.</summary>
    ParenthesesBlack = 2,
    /// <summary>Surrounding parentheses in red.</summary>
    ParenthesesRed = 3
}

/// <summary>The fixed pattern applied by an AcroForm special format.</summary>
public enum PdfFormSpecialFormat
{
    /// <summary>A five digit postal code.</summary>
    ZipCode = 0,
    /// <summary>A five digit postal code with a four digit extension.</summary>
    ZipCodePlusFour = 1,
    /// <summary>A North American telephone number.</summary>
    PhoneNumber = 2,
    /// <summary>A social security number.</summary>
    SocialSecurityNumber = 3
}

/// <summary>The settings applied by an AcroForm number or currency format.</summary>
public sealed record PdfFormNumberFormatOptions
{
    /// <summary>Gets the number of digits kept after the decimal separator.</summary>
    public int DecimalPlaces { get; init; } = 2;
    /// <summary>Gets the digit grouping and decimal separator pair.</summary>
    public PdfFormSeparatorStyle SeparatorStyle { get; init; } = PdfFormSeparatorStyle.CommaGroupPointDecimal;
    /// <summary>Gets the presentation applied to negative values.</summary>
    public PdfFormNegativeStyle NegativeStyle { get; init; } = PdfFormNegativeStyle.MinusBlack;
    /// <summary>Gets the currency symbol, or an empty string when no symbol is shown.</summary>
    public string CurrencySymbol { get; init; } = string.Empty;
    /// <summary>Gets whether the currency symbol precedes the digits.</summary>
    public bool PrependCurrency { get; init; } = true;
}

/// <summary>Formats and parses AcroForm field values without executing document scripts.</summary>
public static class PdfFormFieldFormat
{
    private const int MaximumDecimalPlaces = 15;
    private const int MaximumPictureLength = 256;

    private static readonly string[] DatePictures =
    [
        "m/d", "m/d/yy", "m/d/yyyy", "mm/dd/yy", "mm/dd/yyyy", "mm/yy", "mm/yyyy",
        "d-mmm", "d-mmm-yy", "d-mmm-yyyy", "dd-mmm-yy", "dd-mmm-yyyy",
        "yy-mm-dd", "yyyy-mm-dd", "mmm-yy", "mmm-yyyy", "mmmm-yy", "mmmm-yyyy",
        "mmm d, yyyy", "mmmm d, yyyy",
        "m/d/yy h:MM tt", "m/d/yyyy h:MM tt", "m/d/yy HH:MM", "m/d/yyyy HH:MM"
    ];

    private static readonly string[] TimePictures =
    [
        "HH:MM", "h:MM tt", "HH:MM:ss", "h:MM:ss tt"
    ];

    /// <summary>Returns the date picture selected by a built-in AcroForm format index.</summary>
    public static string DatePicture(int index) => index >= 0 && index < DatePictures.Length
        ? DatePictures[index]
        : throw new ArgumentOutOfRangeException(nameof(index),
            "The built-in date format index is not defined.");

    /// <summary>Returns the time picture selected by a built-in AcroForm format index.</summary>
    public static string TimePicture(int index) => index >= 0 && index < TimePictures.Length
        ? TimePictures[index]
        : throw new ArgumentOutOfRangeException(nameof(index),
            "The built-in time format index is not defined.");

    /// <summary>Returns whether a negative value is shown in red by a negative style.</summary>
    public static bool UsesRedNegative(PdfFormNegativeStyle style) =>
        style is PdfFormNegativeStyle.MinusRed or PdfFormNegativeStyle.ParenthesesRed;

    /// <summary>Formats a number using AcroForm separator, negative, and currency settings.</summary>
    public static string Number(double value, PdfFormNumberFormatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.DecimalPlaces < 0 || options.DecimalPlaces > MaximumDecimalPlaces)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"A number format cannot keep more than {MaximumDecimalPlaces} decimal places.");
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value),
                "A number format requires a finite value.");
        (char group, char point) = Separators(options.SeparatorStyle);
        bool negative = Math.Round(Math.Abs(value), options.DecimalPlaces) > 0 && value < 0;
        string digits = Digits(Math.Abs(value), options.DecimalPlaces, group, point);
        var builder = new StringBuilder();
        if (negative && options.NegativeStyle
            is PdfFormNegativeStyle.ParenthesesBlack or PdfFormNegativeStyle.ParenthesesRed)
            builder.Append('(');
        else if (negative) builder.Append('-');
        if (options.PrependCurrency) builder.Append(options.CurrencySymbol);
        builder.Append(digits);
        if (!options.PrependCurrency) builder.Append(options.CurrencySymbol);
        if (negative && options.NegativeStyle
            is PdfFormNegativeStyle.ParenthesesBlack or PdfFormNegativeStyle.ParenthesesRed)
            builder.Append(')');
        return builder.ToString();
    }

    /// <summary>Formats a fraction as an AcroForm percentage with a trailing percent sign.</summary>
    public static string Percent(double value, int decimalPlaces, PdfFormSeparatorStyle style)
    {
        if (decimalPlaces < 0 || decimalPlaces > MaximumDecimalPlaces)
            throw new ArgumentOutOfRangeException(nameof(decimalPlaces),
                $"A percent format cannot keep more than {MaximumDecimalPlaces} decimal places.");
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value),
                "A percent format requires a finite value.");
        double scaled = value * 100;
        if (!double.IsFinite(scaled))
            throw new ArgumentOutOfRangeException(nameof(value),
                "The scaled percent value is not finite.");
        (char group, char point) = Separators(style);
        bool negative = Math.Round(Math.Abs(scaled), decimalPlaces) > 0 && scaled < 0;
        return (negative ? "-" : string.Empty)
            + Digits(Math.Abs(scaled), decimalPlaces, group, point) + "%";
    }

    /// <summary>Reads a number written with an AcroForm separator style.</summary>
    public static bool TryParseNumber(string? text, PdfFormSeparatorStyle style, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        (char group, char point) = Separators(style);
        var builder = new StringBuilder(text.Length);
        bool parenthesized = text.Contains('(') && text.Contains(')');
        bool negative = parenthesized || text.Contains('-');
        bool seenPoint = false;
        bool seenDigit = false;
        foreach (char character in text)
        {
            if (character == group) continue;
            if (character == point)
            {
                if (seenPoint) return false;
                seenPoint = true;
                builder.Append('.');
            }
            else if (char.IsAsciiDigit(character))
            {
                seenDigit = true;
                builder.Append(character);
            }
        }
        if (!seenDigit || !double.TryParse(builder.ToString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed))
            return false;
        value = negative ? -parsed : parsed;
        return true;
    }

    /// <summary>Formats a date or time using an AcroForm picture string.</summary>
    public static string Date(DateTime value, string picture, CultureInfo? culture = null)
    {
        string format = NetFormat(picture);
        return value.ToString(format, culture ?? CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a date or time written with an AcroForm picture string.</summary>
    public static bool TryParseDate(string? text, string picture, out DateTime value,
        CultureInfo? culture = null)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string format = NetFormat(picture);
        CultureInfo effective = culture ?? CultureInfo.InvariantCulture;
        if (DateTime.TryParseExact(text.Trim(), format, effective,
            DateTimeStyles.AllowWhiteSpaces, out value)) return true;
        return DateTime.TryParse(text.Trim(), effective,
            DateTimeStyles.AllowWhiteSpaces, out value);
    }

    /// <summary>Applies an AcroForm special format to the digits found in a value.</summary>
    public static string Special(string? text, PdfFormSpecialFormat format)
    {
        string digits = string.Concat((text ?? string.Empty).Where(char.IsAsciiDigit));
        return format switch
        {
            PdfFormSpecialFormat.ZipCode => digits.Length == 5 ? digits : text ?? string.Empty,
            PdfFormSpecialFormat.ZipCodePlusFour => digits.Length == 9
                ? digits[..5] + "-" + digits[5..]
                : digits.Length == 5 ? digits : text ?? string.Empty,
            PdfFormSpecialFormat.SocialSecurityNumber => digits.Length == 9
                ? digits[..3] + "-" + digits[3..5] + "-" + digits[5..]
                : text ?? string.Empty,
            PdfFormSpecialFormat.PhoneNumber => Phone(digits, text ?? string.Empty),
            _ => throw new ArgumentOutOfRangeException(nameof(format),
                "The special format is not defined.")
        };
    }

    /// <summary>Reports whether a value satisfies an AcroForm special format.</summary>
    public static bool IsSpecialValid(string? text, PdfFormSpecialFormat format)
    {
        string digits = string.Concat((text ?? string.Empty).Where(char.IsAsciiDigit));
        if (string.IsNullOrWhiteSpace(text)) return true;
        return format switch
        {
            PdfFormSpecialFormat.ZipCode => digits.Length == 5,
            PdfFormSpecialFormat.ZipCodePlusFour => digits.Length is 5 or 9,
            PdfFormSpecialFormat.SocialSecurityNumber => digits.Length == 9,
            PdfFormSpecialFormat.PhoneNumber => digits.Length is 7 or 10 or 11,
            _ => throw new ArgumentOutOfRangeException(nameof(format),
                "The special format is not defined.")
        };
    }

    private static string Phone(string digits, string original) => digits.Length switch
    {
        7 => digits[..3] + "-" + digits[3..],
        10 => "(" + digits[..3] + ") " + digits[3..6] + "-" + digits[6..],
        11 when digits[0] == '1' =>
            "1 (" + digits[1..4] + ") " + digits[4..7] + "-" + digits[7..],
        _ => original
    };

    private static (char Group, char Point) Separators(PdfFormSeparatorStyle style) => style switch
    {
        PdfFormSeparatorStyle.CommaGroupPointDecimal => (',', '.'),
        PdfFormSeparatorStyle.PointDecimalOnly => ('\0', '.'),
        PdfFormSeparatorStyle.PointGroupCommaDecimal => ('.', ','),
        PdfFormSeparatorStyle.CommaDecimalOnly => ('\0', ','),
        PdfFormSeparatorStyle.ApostropheGroupPointDecimal => ('\'', '.'),
        _ => throw new ArgumentOutOfRangeException(nameof(style),
            "The separator style is not defined.")
    };

    private static string Digits(double magnitude, int decimalPlaces, char group, char point)
    {
        string rendered = Math.Round(magnitude, decimalPlaces, MidpointRounding.AwayFromZero)
            .ToString("F" + decimalPlaces.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture);
        int split = rendered.IndexOf('.');
        string whole = split < 0 ? rendered : rendered[..split];
        string fraction = split < 0 ? string.Empty : rendered[(split + 1)..];
        if (group != '\0' && whole.Length > 3)
        {
            var grouped = new StringBuilder(whole.Length + (whole.Length / 3));
            int lead = whole.Length % 3;
            if (lead > 0) grouped.Append(whole, 0, lead);
            for (int index = lead; index < whole.Length; index += 3)
            {
                if (grouped.Length > 0) grouped.Append(group);
                grouped.Append(whole, index, 3);
            }
            whole = grouped.ToString();
        }
        return decimalPlaces == 0 ? whole : whole + point + fraction;
    }

    private static string NetFormat(string picture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(picture);
        if (picture.Length > MaximumPictureLength)
            throw new ArgumentException(
                $"A date picture cannot exceed {MaximumPictureLength} characters.", nameof(picture));
        var builder = new StringBuilder(picture.Length * 2);
        for (int index = 0; index < picture.Length;)
        {
            char character = picture[index];
            if (character == '\\')
            {
                if (index + 1 >= picture.Length)
                    throw new ArgumentException(
                        "A date picture cannot end with an escape character.", nameof(picture));
                builder.Append('\\').Append(picture[index + 1]);
                index += 2;
                continue;
            }
            int run = 1;
            while (index + run < picture.Length && picture[index + run] == character) run++;
            builder.Append(Token(character, run, picture));
            index += run;
        }
        return builder.ToString();
    }

    private static string Token(char character, int run, string picture) => character switch
    {
        'y' => run >= 4 ? "yyyy" : "yy",
        'm' => run switch { 1 => "%M", 2 => "MM", 3 => "MMM", _ => "MMMM" },
        'd' => run switch { 1 => "%d", 2 => "dd", 3 => "ddd", _ => "dddd" },
        'H' => run >= 2 ? "HH" : "%H",
        'h' => run >= 2 ? "hh" : "%h",
        'M' => run >= 2 ? "mm" : "%m",
        's' => run >= 2 ? "ss" : "%s",
        't' => run >= 2 ? "tt" : "%t",
        _ => Literal(character, run, picture)
    };

    private static string Literal(char character, int run, string picture)
    {
        if (char.IsAsciiLetter(character))
            throw new ArgumentException(
                $"The date picture character '{character}' is not supported.", nameof(picture));
        var builder = new StringBuilder(run * 2);
        for (int index = 0; index < run; index++) builder.Append('\\').Append(character);
        return builder.ToString();
    }
}
