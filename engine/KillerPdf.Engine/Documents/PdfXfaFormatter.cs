using System.Globalization;

namespace KillerPdf.Engine.Documents;

/// <summary>Applies a bounded subset of XFA picture clauses without executing form scripts.</summary>
public static class PdfXfaFormatter
{
    private const int MaximumFormats = 10_000;

    /// <summary>Formats dataset values for template fields in template order.</summary>
    public static IReadOnlyList<PdfXfaFormatResult> Format(
        PdfXfaInfo info, PdfFormDataSet data)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(data);
        PdfXfaTemplateBehavior[] formats = [.. PdfXfaTemplate.Read(info).Behaviors.Where(
            behavior => behavior.Kind == PdfXfaTemplateBehaviorKind.Format)];
        if (formats.Length > MaximumFormats)
            throw new InvalidOperationException(
                $"An XFA template cannot contain more than {MaximumFormats} formats.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (PdfFormDataField field in data.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name) || field.Values.Count != 1
                || !values.TryAdd(field.Name, field.Values[0]))
                throw new ArgumentException(
                    "XFA formatting data requires nonempty, unique, single-value fields.", nameof(data));
        }

        return Array.AsReadOnly(formats.Select(format => FormatOne(format, values)).ToArray());
    }

    private static PdfXfaFormatResult FormatOne(PdfXfaTemplateBehavior format,
        IReadOnlyDictionary<string, string> values)
    {
        string name = format.FieldPath;
        if (!values.TryGetValue(name, out string? source))
            return Result(format, PdfXfaFormatStatus.MissingValue, null,
                "The dataset has no exact value for the formatted field.");
        if (string.IsNullOrWhiteSpace(format.Picture))
            return Result(format, PdfXfaFormatStatus.MissingPicture, null,
                "The format has no picture clause.");
        string picture = format.Picture.Trim();
        if (!picture.StartsWith("num{", StringComparison.OrdinalIgnoreCase)
            || picture[^1] != '}')
            return Result(format, PdfXfaFormatStatus.UnsupportedPicture, null,
                "Only numeric XFA picture clauses are supported.");
        string mask = picture[4..^1];
        int decimalIndex = mask.LastIndexOf('.');
        string integerMask = decimalIndex < 0 ? mask : mask[..decimalIndex];
        string fractionMask = decimalIndex < 0 ? string.Empty : mask[(decimalIndex + 1)..];
        if (mask.Length == 0 || mask.Length > 128
            || integerMask.Any(character => character is not ('z' or 'Z' or '9' or ','))
            || fractionMask.Any(character => character is not ('z' or 'Z' or '9'))
            || integerMask.Count(character => character == '9') == 0)
            return Result(format, PdfXfaFormatStatus.UnsupportedPicture, null,
                "The numeric XFA picture clause uses unsupported symbols.");
        if (!decimal.TryParse(source, NumberStyles.Float, CultureInfo.InvariantCulture,
                out decimal number))
            return Result(format, PdfXfaFormatStatus.InvalidValue, null,
                "The field value is not a finite invariant number.");

        int requiredDecimals = fractionMask.Count(character => character == '9');
        int optionalDecimals = fractionMask.Length - requiredDecimals;
        string numericFormat = (integerMask.Contains(',') ? "#,##0" : "0")
            + (fractionMask.Length == 0 ? string.Empty
                : "." + new string('0', requiredDecimals) + new string('#', optionalDecimals));
        return Result(format, PdfXfaFormatStatus.Formatted,
            number.ToString(numericFormat, CultureInfo.InvariantCulture), null);
    }

    private static PdfXfaFormatResult Result(PdfXfaTemplateBehavior behavior,
        PdfXfaFormatStatus status, string? value, string? failure) =>
        new(behavior.FieldPath, status, value, failure);
}

/// <summary>The outcome of one safe XFA formatting attempt.</summary>
public sealed record PdfXfaFormatResult(
    string FieldPath, PdfXfaFormatStatus Status, string? Value, string? Failure);

/// <summary>The outcome category for a safe XFA formatting attempt.</summary>
public enum PdfXfaFormatStatus
{
    /// <summary>The value was formatted.</summary>
    Formatted,
    /// <summary>The dataset does not contain the field value.</summary>
    MissingValue,
    /// <summary>The format has no picture clause.</summary>
    MissingPicture,
    /// <summary>The picture clause is outside the supported subset.</summary>
    UnsupportedPicture,
    /// <summary>The source value cannot be formatted by the picture clause.</summary>
    InvalidValue
}
