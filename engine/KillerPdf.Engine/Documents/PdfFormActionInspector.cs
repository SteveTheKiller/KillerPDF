using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

/// <summary>The review classification assigned to an AcroForm action.</summary>
public enum PdfFormActionSafety
{
    /// <summary>The action has local, non-executing semantics.</summary>
    Supported,
    /// <summary>The action should be reviewed before it is retained or used.</summary>
    RequiresReview,
    /// <summary>The action can submit data or execute active content and was not run.</summary>
    Unsafe
}

/// <summary>An AcroForm action found without executing active content.</summary>
public sealed record PdfFormActionInfo(string FieldName, int? SourceObjectNumber,
    string Trigger, string ActionType, PdfFormActionSafety Safety, string? Target);

/// <summary>Inspects field and widget actions without executing them.</summary>
public static partial class PdfFormActionInspector
{
    private static readonly PdfFormActionCompactJsonContext CompactJson = new(JsonOptions(false));
    private static readonly PdfFormActionIndentedJsonContext IndentedJson = new(JsonOptions(true));
    private static readonly PdfName AcroFormName = Name("AcroForm");
    private static readonly PdfName FieldsName = Name("Fields");
    private static readonly PdfName KidsName = Name("Kids");
    private static readonly PdfName PartialName = Name("T");
    private static readonly PdfName ActionName = Name("A");
    private static readonly PdfName AdditionalActionsName = Name("AA");
    private static readonly PdfName ActionTypeName = Name("S");
    private static readonly PdfName NextName = Name("Next");
    private static readonly PdfName UriName = Name("URI");

    /// <summary>Exports the inspection result as stable machine-readable JSON.</summary>
    public static string ExportJson(PdfDocument document, bool indented = true)
    {
        PdfFormActionInfo[] actions = [.. Inspect(document)];
        return JsonSerializer.Serialize(actions, indented
            ? IndentedJson.PdfFormActionInfoArray
            : CompactJson.PdfFormActionInfoArray);
    }

    private static JsonSerializerOptions JsonOptions(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented
        };
        options.Converters.Add(new JsonStringEnumConverter<PdfFormActionSafety>());
        return options;
    }

    /// <summary>Reports field and widget actions without executing active content.</summary>
    public static IReadOnlyList<PdfFormActionInfo> Inspect(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfDictionary catalog = PdfPageTree.Read(document).Catalog;
        if (!catalog.TryGetValue(AcroFormName, out PdfObject? formValue)) return [];
        PdfDictionary form = Resolve(document, formValue, "The catalog /AcroForm value")
            as PdfDictionary ?? throw new InvalidOperationException(
                "The catalog /AcroForm value is not a dictionary.");
        if (!form.TryGetValue(FieldsName, out PdfObject? fieldsValue)) return [];
        PdfArray fields = Resolve(document, fieldsValue, "The AcroForm /Fields value")
            as PdfArray ?? throw new InvalidOperationException(
                "The AcroForm /Fields value is not an array.");
        var result = new List<PdfFormActionInfo>();
        var visitedFields = new HashSet<(int, int)>();
        foreach (PdfObject field in fields) VisitField(field, string.Empty, 0);
        return Array.AsReadOnly(result.ToArray());

        void VisitField(PdfObject value, string inheritedName, int depth)
        {
            if (depth >= 256)
                throw new InvalidOperationException("The AcroForm field tree is too deeply nested.");
            int? sourceObjectNumber = null;
            if (value is PdfIndirectReference reference)
            {
                if (!visitedFields.Add((reference.ObjectNumber, reference.Generation)))
                    throw new InvalidOperationException(
                        "The AcroForm field tree contains a cycle or reused field.");
                sourceObjectNumber = reference.ObjectNumber;
            }
            PdfDictionary field = Resolve(document, value, "An AcroForm field") as PdfDictionary
                ?? throw new InvalidOperationException("An AcroForm field is not a dictionary.");
            string fieldName = inheritedName;
            if (field.TryGetValue(PartialName, out PdfObject? partialValue))
            {
                PdfString partial = Resolve(document, partialValue, "An AcroForm /T value")
                    as PdfString ?? throw new InvalidOperationException(
                        "An AcroForm /T value is not a string.");
                string local = PdfUnicodeEncoding.DecodeTextString(
                    partial.Bytes.Span, "An AcroForm /T value");
                if (local.Length > 0)
                    fieldName = fieldName.Length == 0 ? local : $"{fieldName}.{local}";
            }
            InspectActions(field, fieldName, sourceObjectNumber);
            if (!field.TryGetValue(KidsName, out PdfObject? kidsValue)) return;
            PdfArray kids = Resolve(document, kidsValue, "An AcroForm /Kids value") as PdfArray
                ?? throw new InvalidOperationException("An AcroForm /Kids value is not an array.");
            foreach (PdfObject kid in kids) VisitField(kid, fieldName, depth + 1);
        }

        void InspectActions(PdfDictionary owner, string fieldName, int? sourceObjectNumber)
        {
            if (owner.TryGetValue(ActionName, out PdfObject? action))
                VisitAction(action, fieldName, sourceObjectNumber, "A", 0,
                    new HashSet<(int, int)>());
            if (!owner.TryGetValue(AdditionalActionsName, out PdfObject? additionalValue)) return;
            PdfDictionary additional = Resolve(document, additionalValue,
                "An AcroForm /AA value") as PdfDictionary
                ?? throw new InvalidOperationException("An AcroForm /AA value is not a dictionary.");
            foreach ((PdfName trigger, PdfObject triggerAction) in additional)
                VisitAction(triggerAction, fieldName, sourceObjectNumber,
                    trigger.ValueAsLatin1(), 0, new HashSet<(int, int)>());
        }

        void VisitAction(PdfObject value, string fieldName, int? ownerObjectNumber,
            string trigger, int depth, HashSet<(int, int)> active)
        {
            if (depth >= 256)
                throw new InvalidOperationException("An AcroForm action chain is too deeply nested.");
            int? actionObjectNumber = ownerObjectNumber;
            (int, int)? identity = null;
            if (value is PdfIndirectReference reference)
            {
                identity = (reference.ObjectNumber, reference.Generation);
                actionObjectNumber = reference.ObjectNumber;
                if (!active.Add(identity.Value))
                {
                    result.Add(new(fieldName, actionObjectNumber, trigger, "Circular",
                        PdfFormActionSafety.Unsafe, null));
                    return;
                }
            }
            PdfObject resolved = Resolve(document, value, "An AcroForm action");
            if (resolved is PdfArray actions)
            {
                foreach (PdfObject action in actions)
                    VisitAction(action, fieldName, ownerObjectNumber, trigger, depth + 1, active);
            }
            else if (resolved is PdfDictionary action)
            {
                string type = action.TryGetValue(ActionTypeName, out PdfObject? typeValue)
                    && Resolve(document, typeValue, "An action /S value") is PdfName typeName
                    ? typeName.ValueAsLatin1() : "Unknown";
                string? target = type == "URI" && action.TryGetValue(UriName, out PdfObject? uriValue)
                    && Resolve(document, uriValue, "A URI action target") is PdfString uri
                    ? PdfUnicodeEncoding.DecodeTextString(uri.Bytes.Span, "A URI action target")
                    : null;
                result.Add(new(fieldName, actionObjectNumber, trigger, type,
                    Safety(type, target), target));
                if (action.TryGetValue(NextName, out PdfObject? next))
                    VisitAction(next, fieldName, actionObjectNumber, "Next", depth + 1, active);
            }
            else throw new InvalidOperationException("An AcroForm action is not a dictionary or array.");
            if (identity.HasValue) active.Remove(identity.Value);
        }
    }

    private static PdfFormActionSafety Safety(string type, string? target) => type switch
    {
        "GoTo" or "ResetForm" => PdfFormActionSafety.Supported,
        "JavaScript" or "Launch" or "SubmitForm" or "ImportData" =>
            PdfFormActionSafety.Unsafe,
        "URI" when Uri.TryCreate(target, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https" or "mailto" => PdfFormActionSafety.RequiresReview,
        _ => PdfFormActionSafety.RequiresReview
    };

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

    [JsonSerializable(typeof(PdfFormActionInfo[]))]
    private sealed partial class PdfFormActionCompactJsonContext : JsonSerializerContext;

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(PdfFormActionInfo[]))]
    private sealed partial class PdfFormActionIndentedJsonContext : JsonSerializerContext;
}
