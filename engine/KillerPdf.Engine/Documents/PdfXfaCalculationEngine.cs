using System.Globalization;

namespace KillerPdf.Engine.Documents;

/// <summary>Evaluates the safe FormCalc subset against portable XFA dataset values.</summary>
public static class PdfXfaCalculationEngine
{
    private const int MaximumCalculations = 10_000;

    /// <summary>Evaluates field calculations in template order without executing JavaScript.</summary>
    public static IReadOnlyList<PdfXfaCalculationResult> Evaluate(
        PdfXfaInfo info, PdfFormDataSet data)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(data);
        PdfXfaTemplateInfo template = PdfXfaTemplate.Read(info);
        PdfXfaTemplateBehavior[] calculations = [.. template.Behaviors.Where(
            behavior => behavior.Kind == PdfXfaTemplateBehaviorKind.Calculate)];
        if (calculations.Length > MaximumCalculations)
            throw new InvalidOperationException(
                $"An XFA template cannot contain more than {MaximumCalculations} calculations.");

        var variables = new Dictionary<string, double>(StringComparer.Ordinal);
        var dataNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (PdfFormDataField field in data.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name) || !dataNames.Add(field.Name))
                throw new ArgumentException(
                    "XFA calculation data requires nonempty, unique field names.", nameof(data));
            if (field.Values.Count != 1 || !double.TryParse(field.Values[0],
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || !double.IsFinite(value)) continue;
            variables[field.Name] = value;
            variables["$record." + field.Name] = value;
        }

        var results = new List<PdfXfaCalculationResult>(calculations.Length);
        foreach (PdfXfaTemplateBehavior calculation in calculations)
        {
            PdfXfaCalculationResult result;
            if (!IsFormCalc(calculation.ScriptContentType))
                result = Result(calculation, PdfXfaCalculationStatus.UnsupportedLanguage,
                    null, "The calculation is not declared as FormCalc.");
            else if (string.IsNullOrWhiteSpace(calculation.Script))
                result = Result(calculation, PdfXfaCalculationStatus.MissingExpression,
                    null, "The FormCalc calculation has no expression.");
            else
            {
                try
                {
                    double value = PdfXfaFormCalc.Evaluate(calculation.Script, variables);
                    result = Result(calculation, PdfXfaCalculationStatus.Evaluated, value, null);
                    variables[calculation.FieldPath] = value;
                    variables["$record." + calculation.FieldPath] = value;
                }
                catch (Exception exception) when (exception is ArgumentException
                    or FormatException or InvalidOperationException or KeyNotFoundException)
                {
                    result = Result(calculation, PdfXfaCalculationStatus.Failed,
                        null, exception.Message);
                }
            }
            results.Add(result);
        }
        return Array.AsReadOnly(results.ToArray());
    }

    private static bool IsFormCalc(string? contentType) => contentType is not null
        && (contentType.Equals("application/x-formcalc", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("application/x-formcalc;", StringComparison.OrdinalIgnoreCase));

    private static PdfXfaCalculationResult Result(PdfXfaTemplateBehavior behavior,
        PdfXfaCalculationStatus status, double? value, string? failure) => new(
            behavior.FieldPath, status, value, failure);
}

/// <summary>The outcome of one safe XFA calculation attempt.</summary>
public sealed record PdfXfaCalculationResult(
    string FieldPath,
    PdfXfaCalculationStatus Status,
    double? Value,
    string? Failure);

/// <summary>The outcome category for a safe XFA calculation attempt.</summary>
public enum PdfXfaCalculationStatus
{
    /// <summary>The FormCalc expression evaluated successfully.</summary>
    Evaluated,
    /// <summary>The script language is absent or unsupported.</summary>
    UnsupportedLanguage,
    /// <summary>The FormCalc script has no expression.</summary>
    MissingExpression,
    /// <summary>The expression could not be evaluated by the safe subset.</summary>
    Failed
}
