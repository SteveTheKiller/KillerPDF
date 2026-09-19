using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

/// <summary>The field event that runs an AcroForm script.</summary>
public enum PdfFormScriptTrigger
{
    /// <summary>The document recalculation event.</summary>
    Calculate,
    /// <summary>The display formatting event.</summary>
    Format,
    /// <summary>The per keystroke filtering event.</summary>
    Keystroke,
    /// <summary>The value validation event.</summary>
    Validate
}

/// <summary>One AcroForm field script found without executing it.</summary>
public sealed record PdfFormFieldScript(
    string FieldName, PdfFormScriptTrigger Trigger, string Script);

/// <summary>The evaluated state of one AcroForm field.</summary>
public sealed record PdfFormCalculationEntry
{
    /// <summary>Gets the fully qualified field name.</summary>
    public required string FieldName { get; init; }
    /// <summary>Gets the outcome of the field's calculation or format script.</summary>
    public required PdfFormScriptStatus Status { get; init; }
    /// <summary>Gets the field value after any supported calculation.</summary>
    public required string Value { get; init; }
    /// <summary>Gets the value as the supported format script would display it.</summary>
    public required string DisplayValue { get; init; }
    /// <summary>Gets whether every supported validation accepted the value.</summary>
    public required bool IsValid { get; init; }
    /// <summary>Gets whether the value should be drawn in the negative highlight color.</summary>
    public bool IsRed { get; init; }
    /// <summary>Gets the reason a script was not evaluated, or null when none applies.</summary>
    public string? Failure { get; init; }
}

/// <summary>The result of evaluating every supported script in an AcroForm.</summary>
public sealed record PdfFormCalculationReport
{
    /// <summary>Gets the evaluated fields in calculation order followed by the remainder.</summary>
    public required IReadOnlyList<PdfFormCalculationEntry> Fields { get; init; }
    /// <summary>Gets the number of fields whose scripts were fully evaluated.</summary>
    public required int EvaluatedCount { get; init; }
    /// <summary>Gets the number of fields carrying a script outside the safe subset.</summary>
    public required int UnsupportedCount { get; init; }
    /// <summary>Gets the number of fields whose supported script could not be evaluated.</summary>
    public required int FailedCount { get; init; }
    /// <summary>Gets the number of fields rejected by a supported validation.</summary>
    public required int InvalidCount { get; init; }
}

/// <summary>
/// Reads AcroForm field scripts and evaluates the supported built-in subset in
/// calculation order. Scripts outside the subset are reported and never executed.
/// </summary>
public static partial class PdfFormCalculation
{
    private const int MaximumFields = 4096;
    private const int MaximumScriptBytes = 1 << 20;

    private static readonly PdfFormCalculationCompactJsonContext CompactJson = new(JsonOptions(false));
    private static readonly PdfFormCalculationIndentedJsonContext IndentedJson = new(JsonOptions(true));
    private static readonly PdfName AcroFormName = Name("AcroForm");
    private static readonly PdfName FieldsName = Name("Fields");
    private static readonly PdfName CalculationOrderName = Name("CO");
    private static readonly PdfName KidsName = Name("Kids");
    private static readonly PdfName PartialName = Name("T");
    private static readonly PdfName ValueName = Name("V");
    private static readonly PdfName AdditionalActionsName = Name("AA");
    private static readonly PdfName ActionTypeName = Name("S");
    private static readonly PdfName JavaScriptName = Name("JavaScript");
    private static readonly PdfName ScriptName = Name("JS");
    private static readonly PdfName CalculateTriggerName = Name("C");
    private static readonly PdfName FormatTriggerName = Name("F");
    private static readonly PdfName KeystrokeTriggerName = Name("K");
    private static readonly PdfName ValidateTriggerName = Name("V");

    /// <summary>Reads every AcroForm field script without executing any of them.</summary>
    public static IReadOnlyList<PdfFormFieldScript> ReadScripts(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var scripts = new List<PdfFormFieldScript>();
        foreach (FormField field in ReadFields(document))
            foreach ((PdfFormScriptTrigger trigger, string script) in field.Scripts)
                scripts.Add(new(field.Name, trigger, script));
        return Array.AsReadOnly(scripts.ToArray());
    }

    /// <summary>Reads the stored value of every AcroForm field in field tree order.</summary>
    public static IReadOnlyDictionary<string, string> ReadValues(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (FormField field in ReadFields(document)) values[field.Name] = field.Value;
        return values;
    }

    /// <summary>Evaluates supported calculation, format, and validation scripts in order.</summary>
    public static PdfFormCalculationReport Evaluate(PdfDocument document,
        IReadOnlyDictionary<string, string>? values = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        IReadOnlyList<FormField> fields = ReadFields(document);
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (FormField field in fields) current[field.Name] = field.Value;
        if (values is not null)
            foreach ((string name, string value) in values)
            {
                if (!current.ContainsKey(name))
                    throw new KeyNotFoundException($"The form has no field named '{name}'.");
                current[name] = value;
            }

        var entries = new List<PdfFormCalculationEntry>(fields.Count);
        foreach (FormField field in Order(document, fields))
        {
            PdfFormScriptStatus status = PdfFormScriptStatus.Evaluated;
            string? failure = null;
            bool valid = true;
            bool red = false;

            if (field.Scripts.TryGetValue(PdfFormScriptTrigger.Calculate, out string? calculation))
            {
                PdfFormScriptResult result = PdfFormScript.Calculate(calculation, current);
                Merge(result, ref status, ref failure);
                if (result.Status == PdfFormScriptStatus.Evaluated && result.Text is not null)
                    current[field.Name] = result.Text;
            }

            string value = current[field.Name];
            string display = value;
            if (field.Scripts.TryGetValue(PdfFormScriptTrigger.Format, out string? format))
            {
                PdfFormScriptResult result = PdfFormScript.Format(format, value);
                Merge(result, ref status, ref failure);
                if (result.Status == PdfFormScriptStatus.Evaluated)
                {
                    display = result.Text ?? value;
                    red = result.IsRed;
                    valid &= result.IsValid;
                }
            }

            if (field.Scripts.TryGetValue(PdfFormScriptTrigger.Validate, out string? validation))
            {
                PdfFormScriptResult result = PdfFormScript.Validate(validation, value);
                Merge(result, ref status, ref failure);
                if (result.Status == PdfFormScriptStatus.Evaluated) valid &= result.IsValid;
                if (!result.IsValid) failure ??= result.Failure;
            }

            entries.Add(new PdfFormCalculationEntry
            {
                FieldName = field.Name,
                Status = status,
                Value = value,
                DisplayValue = display,
                IsValid = valid,
                IsRed = red,
                Failure = failure
            });
        }

        return new PdfFormCalculationReport
        {
            Fields = Array.AsReadOnly(entries.ToArray()),
            EvaluatedCount = entries.Count(entry => entry.Status == PdfFormScriptStatus.Evaluated),
            UnsupportedCount = entries.Count(entry => entry.Status == PdfFormScriptStatus.Unsupported),
            FailedCount = entries.Count(entry => entry.Status == PdfFormScriptStatus.Failed),
            InvalidCount = entries.Count(entry => !entry.IsValid)
        };
    }

    /// <summary>Exports the evaluation result as stable machine-readable JSON.</summary>
    public static string ExportJson(PdfDocument document, bool indented = true)
    {
        PdfFormCalculationReport report = Evaluate(document);
        return JsonSerializer.Serialize(report, indented
            ? IndentedJson.PdfFormCalculationReport
            : CompactJson.PdfFormCalculationReport);
    }

    private static void Merge(PdfFormScriptResult result,
        ref PdfFormScriptStatus status, ref string? failure)
    {
        if (result.Status == PdfFormScriptStatus.Evaluated) return;
        if (status == PdfFormScriptStatus.Evaluated
            || result.Status == PdfFormScriptStatus.Failed) status = result.Status;
        failure ??= result.Failure;
    }

    private static IEnumerable<FormField> Order(PdfDocument document,
        IReadOnlyList<FormField> fields)
    {
        int[] calculationOrder = CalculationOrder(document);
        if (calculationOrder.Length == 0) return fields;
        Dictionary<int, FormField> byObject = [];
        foreach (FormField field in fields)
            if (field.ObjectNumber > 0) byObject.TryAdd(field.ObjectNumber, field);
        var ordered = new List<FormField>(fields.Count);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (int objectNumber in calculationOrder)
            if (byObject.TryGetValue(objectNumber, out FormField? field) && placed.Add(field.Name))
                ordered.Add(field);
        foreach (FormField field in fields)
            if (placed.Add(field.Name)) ordered.Add(field);
        return ordered;
    }

    private static int[] CalculationOrder(PdfDocument document)
    {
        PdfDictionary catalog = PdfPageTree.Read(document).Catalog;
        if (!catalog.TryGetValue(AcroFormName, out PdfObject? formValue)
            || Resolve(document, formValue, "The catalog /AcroForm value") is not PdfDictionary form
            || !form.TryGetValue(CalculationOrderName, out PdfObject? orderValue)
            || Resolve(document, orderValue, "The AcroForm /CO value") is not PdfArray order) return [];
        var result = new List<int>(order.Count);
        foreach (PdfObject entry in order)
            if (entry is PdfIndirectReference reference) result.Add(reference.ObjectNumber);
        return [.. result];
    }

    private static IReadOnlyList<FormField> ReadFields(PdfDocument document)
    {
        PdfDictionary catalog = PdfPageTree.Read(document).Catalog;
        if (!catalog.TryGetValue(AcroFormName, out PdfObject? formValue)) return [];
        if (Resolve(document, formValue, "The catalog /AcroForm value") is not PdfDictionary form)
            throw new InvalidOperationException("The catalog /AcroForm value is not a dictionary.");
        if (!form.TryGetValue(FieldsName, out PdfObject? fieldsValue)) return [];
        if (Resolve(document, fieldsValue, "The AcroForm /Fields value") is not PdfArray fields)
            throw new InvalidOperationException("The AcroForm /Fields value is not an array.");
        var result = new List<FormField>();
        var visited = new HashSet<(int, int)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (PdfObject field in fields) Visit(field, string.Empty, 0);
        return Array.AsReadOnly(result.ToArray());

        void Visit(PdfObject value, string inherited, int depth)
        {
            if (depth >= 256)
                throw new InvalidOperationException("The AcroForm field tree is too deeply nested.");
            if (result.Count > MaximumFields)
                throw new InvalidOperationException(
                    $"An AcroForm cannot contain more than {MaximumFields} fields.");
            int objectNumber = 0;
            (int, int)? identity = null;
            if (value is PdfIndirectReference reference)
            {
                objectNumber = reference.ObjectNumber;
                identity = (reference.ObjectNumber, reference.Generation);
                if (!visited.Add(identity.Value)) return;
            }
            if (Resolve(document, value, "An AcroForm field") is not PdfDictionary field) return;
            string name = inherited;
            if (field.TryGetValue(PartialName, out PdfObject? partialValue)
                && Resolve(document, partialValue, "An AcroForm field /T value") is PdfString partial)
            {
                string text = PdfUnicodeEncoding.DecodeTextString(
                    partial.Bytes.Span, "An AcroForm field /T value");
                name = name.Length == 0 ? text : name + "." + text;
            }
            if (field.TryGetValue(KidsName, out PdfObject? kidsValue)
                && Resolve(document, kidsValue, "An AcroForm field /Kids value") is PdfArray kids
                && kids.Any(kid => Resolve(document, kid, "An AcroForm field /Kids entry")
                    is PdfDictionary child && child.ContainsKey(PartialName)))
            {
                foreach (PdfObject kid in kids) Visit(kid, name, depth + 1);
                if (identity.HasValue) visited.Remove(identity.Value);
                return;
            }
            if (name.Length > 0 && names.Add(name))
                result.Add(new FormField(name, objectNumber,
                    FieldValue(document, field), Scripts(document, field)));
            if (identity.HasValue) visited.Remove(identity.Value);
        }
    }

    private static string FieldValue(PdfDocument document, PdfDictionary field)
    {
        if (!field.TryGetValue(ValueName, out PdfObject? value)) return string.Empty;
        return Scalar(Resolve(document, value, "An AcroForm field /V value"));

        string Scalar(PdfObject resolved) => resolved switch
        {
            PdfString text => PdfUnicodeEncoding.DecodeTextString(
                text.Bytes.Span, "An AcroForm field /V value"),
            PdfName name => Encoding.ASCII.GetString(name.Bytes.Span),
            PdfInteger integer => integer.Value.ToString(CultureInfo.InvariantCulture),
            PdfReal real => real.Value.ToString("0.############", CultureInfo.InvariantCulture),
            PdfBoolean flag => flag.Value ? "true" : "false",
            PdfArray array when array.Count > 0 => Scalar(
                Resolve(document, array[0], "An AcroForm field /V entry")),
            _ => string.Empty
        };
    }

    private static Dictionary<PdfFormScriptTrigger, string> Scripts(
        PdfDocument document, PdfDictionary field)
    {
        var scripts = new Dictionary<PdfFormScriptTrigger, string>();
        if (!field.TryGetValue(AdditionalActionsName, out PdfObject? actionsValue)
            || Resolve(document, actionsValue, "An AcroForm field /AA value")
                is not PdfDictionary actions) return scripts;
        Add(CalculateTriggerName, PdfFormScriptTrigger.Calculate);
        Add(FormatTriggerName, PdfFormScriptTrigger.Format);
        Add(KeystrokeTriggerName, PdfFormScriptTrigger.Keystroke);
        Add(ValidateTriggerName, PdfFormScriptTrigger.Validate);
        return scripts;

        void Add(PdfName key, PdfFormScriptTrigger trigger)
        {
            if (!actions.TryGetValue(key, out PdfObject? actionValue)
                || Resolve(document, actionValue, "An AcroForm field action")
                    is not PdfDictionary action
                || !action.TryGetValue(ActionTypeName, out PdfObject? typeValue)
                || Resolve(document, typeValue, "An AcroForm action /S value")
                    is not PdfName type
                || !type.Equals(JavaScriptName)
                || !action.TryGetValue(ScriptName, out PdfObject? scriptValue)) return;
            PdfObject script = Resolve(document, scriptValue, "An AcroForm action /JS value");
            string? text = script switch
            {
                PdfString value => PdfUnicodeEncoding.DecodeTextString(
                    value.Bytes.Span, "An AcroForm action /JS value"),
                PdfStream stream => PdfUnicodeEncoding.DecodeTextString(
                    PdfStreamDecoder.Decode(stream, MaximumScriptBytes),
                    "An AcroForm action /JS stream"),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(text)) scripts[trigger] = text;
        }
    }

    private static JsonSerializerOptions JsonOptions(bool indented)
    {
        var options = new JsonSerializerOptions { WriteIndented = indented };
        options.Converters.Add(new JsonStringEnumConverter<PdfFormScriptStatus>());
        return options;
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value, string description)
    {
        var visited = new HashSet<(int, int)>();
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32 || !visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException($"{description} has an invalid reference chain.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private sealed record FormField(string Name, int ObjectNumber, string Value,
        Dictionary<PdfFormScriptTrigger, string> Scripts);

    [JsonSerializable(typeof(PdfFormCalculationReport))]
    private sealed partial class PdfFormCalculationCompactJsonContext : JsonSerializerContext;

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(PdfFormCalculationReport))]
    private sealed partial class PdfFormCalculationIndentedJsonContext : JsonSerializerContext;
}
