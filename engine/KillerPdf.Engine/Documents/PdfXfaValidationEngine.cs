using System.Globalization;

namespace KillerPdf.Engine.Documents;

/// <summary>Evaluates the safe FormCalc subset for XFA validation rules.</summary>
public static class PdfXfaValidationEngine
{
    private const int MaximumValidations = 10_000;

    /// <summary>Evaluates validation rules in template order without executing JavaScript.</summary>
    public static IReadOnlyList<PdfXfaValidationResult> Evaluate(
        PdfXfaInfo info, PdfFormDataSet data)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(data);
        PdfXfaTemplateBehavior[] validations = [.. PdfXfaTemplate.Read(info).Behaviors.Where(
            behavior => behavior.Kind == PdfXfaTemplateBehaviorKind.Validate)];
        if (validations.Length > MaximumValidations)
            throw new InvalidOperationException(
                $"An XFA template cannot contain more than {MaximumValidations} validations.");

        var variables = new Dictionary<string, double>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (PdfFormDataField field in data.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name) || !names.Add(field.Name))
                throw new ArgumentException(
                    "XFA validation data requires nonempty, unique field names.", nameof(data));
            if (field.Values.Count != 1 || !double.TryParse(field.Values[0],
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || !double.IsFinite(value)) continue;
            variables[field.Name] = value;
            variables["$record." + field.Name] = value;
        }

        return Array.AsReadOnly(validations.Select(EvaluateOne).ToArray());

        PdfXfaValidationResult EvaluateOne(PdfXfaTemplateBehavior validation)
        {
            if (!IsFormCalc(validation.ScriptContentType))
                return Result(validation, PdfXfaValidationStatus.UnsupportedLanguage,
                    "The validation is not declared as FormCalc.");
            if (string.IsNullOrWhiteSpace(validation.Script))
                return Result(validation, PdfXfaValidationStatus.MissingExpression,
                    "The FormCalc validation has no expression.");
            try
            {
                double value = PdfXfaFormCalc.Evaluate(validation.Script, variables);
                return Result(validation, value != 0
                    ? PdfXfaValidationStatus.Passed : PdfXfaValidationStatus.Rejected, null);
            }
            catch (Exception exception) when (exception is ArgumentException
                or FormatException or InvalidOperationException or KeyNotFoundException)
            {
                return Result(validation, PdfXfaValidationStatus.Failed, exception.Message);
            }
        }
    }

    private static bool IsFormCalc(string? contentType) => contentType is not null
        && (contentType.Equals("application/x-formcalc", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("application/x-formcalc;", StringComparison.OrdinalIgnoreCase));

    private static PdfXfaValidationResult Result(PdfXfaTemplateBehavior behavior,
        PdfXfaValidationStatus status, string? failure) =>
        new(behavior.FieldPath, status, failure);
}

/// <summary>The outcome of one safe XFA validation attempt.</summary>
public sealed record PdfXfaValidationResult(
    string FieldPath, PdfXfaValidationStatus Status, string? Failure);

/// <summary>The outcome category for a safe XFA validation attempt.</summary>
public enum PdfXfaValidationStatus
{
    /// <summary>The validation expression accepted the field data.</summary>
    Passed,
    /// <summary>The validation expression rejected the field data.</summary>
    Rejected,
    /// <summary>The script language is absent or unsupported.</summary>
    UnsupportedLanguage,
    /// <summary>The FormCalc script has no expression.</summary>
    MissingExpression,
    /// <summary>The expression could not be evaluated by the safe subset.</summary>
    Failed
}
