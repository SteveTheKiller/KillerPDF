using System.Globalization;

namespace KillerPdf.Engine.Documents;

/// <summary>Evaluates caller-selected XFA form events inside the restricted FormCalc subset.</summary>
public static class PdfXfaEventEngine
{
    private const int MaximumEvents = 10_000;

    /// <summary>Evaluates matching events in template order without mutating form data.</summary>
    public static IReadOnlyList<PdfXfaEventResult> Evaluate(
        PdfXfaInfo info, PdfFormDataSet data, string activity)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(data);
        if (string.IsNullOrWhiteSpace(activity))
            throw new ArgumentException("An XFA event activity is required.", nameof(activity));
        PdfXfaTemplateBehavior[] events = [.. PdfXfaTemplate.Read(info).Behaviors.Where(
            behavior => behavior.Kind == PdfXfaTemplateBehaviorKind.Event
                && string.Equals(behavior.Activity, activity, StringComparison.OrdinalIgnoreCase))];
        if (events.Length > MaximumEvents)
            throw new InvalidOperationException(
                $"An XFA template cannot contain more than {MaximumEvents} matching events.");

        Dictionary<string, double> variables = Variables(data);
        return Array.AsReadOnly(events.Select(formEvent => EvaluateOne(formEvent, variables)).ToArray());
    }

    private static PdfXfaEventResult EvaluateOne(
        PdfXfaTemplateBehavior formEvent, IReadOnlyDictionary<string, double> variables)
    {
        if (!IsFormCalc(formEvent.ScriptContentType))
            return Result(formEvent, PdfXfaEventStatus.UnsupportedLanguage, null,
                "The event is not declared as FormCalc.");
        if (string.IsNullOrWhiteSpace(formEvent.Script))
            return Result(formEvent, PdfXfaEventStatus.MissingExpression, null,
                "The FormCalc event has no expression.");
        try
        {
            return Result(formEvent, PdfXfaEventStatus.Evaluated,
                PdfXfaFormCalc.Evaluate(formEvent.Script, variables), null);
        }
        catch (Exception exception) when (exception is ArgumentException
            or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return Result(formEvent, PdfXfaEventStatus.Failed, null, exception.Message);
        }
    }

    private static Dictionary<string, double> Variables(PdfFormDataSet data)
    {
        var variables = new Dictionary<string, double>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (PdfFormDataField field in data.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name) || !names.Add(field.Name))
                throw new ArgumentException(
                    "XFA event data requires nonempty, unique field names.", nameof(data));
            if (field.Values.Count == 1 && double.TryParse(field.Values[0],
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && double.IsFinite(value))
            {
                variables[field.Name] = value;
                variables["$record." + field.Name] = value;
            }
        }
        return variables;
    }

    private static bool IsFormCalc(string? contentType) => contentType is not null
        && (contentType.Equals("application/x-formcalc", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("application/x-formcalc;", StringComparison.OrdinalIgnoreCase));

    private static PdfXfaEventResult Result(PdfXfaTemplateBehavior formEvent,
        PdfXfaEventStatus status, double? value, string? failure) => new(
            formEvent.FieldPath, formEvent.Activity!, status, value, failure);
}

/// <summary>The outcome of one restricted XFA form event.</summary>
public sealed record PdfXfaEventResult(
    string FieldPath, string Activity, PdfXfaEventStatus Status, double? Value, string? Failure);

/// <summary>The outcome category for a restricted XFA form event.</summary>
public enum PdfXfaEventStatus
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
