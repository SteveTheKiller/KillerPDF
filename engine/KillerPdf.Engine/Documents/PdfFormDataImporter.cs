using System.Text.Json;
using System.Text.Json.Serialization;
using KillerPdf.Engine.Editing;

namespace KillerPdf.Engine.Documents;

/// <summary>Matches portable form data to AcroForm widgets before applying changes.</summary>
public static class PdfFormDataImporter
{
    /// <summary>Reports how every supplied field maps to the destination document.</summary>
    public static IReadOnlyList<PdfFormDataMatch> Preview(PdfDocument document, PdfFormDataSet data)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(data);
        Dictionary<string, PdfFormWidgetInfo> widgets = Widgets(document);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<PdfFormDataMatch>();
        foreach (PdfFormDataField field in data.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
                throw new ArgumentException("A form-data field name cannot be empty.", nameof(data));
            if (!names.Add(field.Name))
                throw new ArgumentException($"The form data contains duplicate field '{field.Name}'.", nameof(data));
            PdfFormDataMatchStatus status;
            PdfFormFieldKind kind = PdfFormFieldKind.Unknown;
            long flags = 0;
            if (!widgets.TryGetValue(field.Name, out PdfFormWidgetInfo? widget))
                status = PdfFormDataMatchStatus.Unmatched;
            else
            {
                kind = widget.FieldKind;
                flags = widget.Flags;
                status = (flags & 1) != 0 ? PdfFormDataMatchStatus.ReadOnly
                    : kind == PdfFormFieldKind.Signature || kind == PdfFormFieldKind.Unknown
                        || (kind == PdfFormFieldKind.Button && (flags & (1L << 16)) != 0)
                        ? PdfFormDataMatchStatus.Incompatible : PdfFormDataMatchStatus.Matched;
            }
            matches.Add(new PdfFormDataMatch
            {
                FieldName = field.Name,
                Status = status,
                FieldKind = kind,
                IsRequired = (flags & 2) != 0,
                IsNoExport = (flags & 4) != 0
            });
        }
        return Array.AsReadOnly(matches.ToArray());
    }

    /// <summary>Creates a concise, value-free report for a planned form-data import.</summary>
    public static PdfFormDataImportReport CreateReport(PdfDocument document, PdfFormDataSet data)
    {
        IReadOnlyList<PdfFormDataMatch> matches = Preview(document, data);
        return new PdfFormDataImportReport(
            matches.Count,
            matches.Count(match => match.Status == PdfFormDataMatchStatus.Matched),
            matches.Count(match => match.Status != PdfFormDataMatchStatus.Matched),
            matches.Count(match => match.IsRequired),
            matches.Count(match => match.IsNoExport),
            matches);
    }

    /// <summary>Applies matched values in one byte-preserving incremental revision.</summary>
    public static byte[] Apply(PdfDocument document, PdfFormDataSet data)
    {
        IReadOnlyList<PdfFormDataMatch> preview = Preview(document, data);
        Dictionary<string, PdfFormWidgetInfo> widgets = Widgets(document);
        var editor = new PdfIncrementalPageEditor(document);
        bool changed = false;
        for (int index = 0; index < data.Fields.Count; index++)
        {
            if (preview[index].Status != PdfFormDataMatchStatus.Matched) continue;
            PdfFormDataField field = data.Fields[index];
            PdfFormWidgetInfo widget = widgets[field.Name];
            string first = field.Values.FirstOrDefault() ?? string.Empty;
            switch (widget.FieldKind)
            {
                case PdfFormFieldKind.Text:
                    editor.SetTextFieldValue(field.Name, first);
                    break;
                case PdfFormFieldKind.Choice:
                    editor.SetChoiceFieldValues(field.Name, field.Values);
                    break;
                case PdfFormFieldKind.Button when (widget.Flags & (1L << 15)) != 0:
                    editor.SetRadioButtonValue(field.Name,
                        first.Length == 0 || first is "/Off" or "Off" ? null : first.TrimStart('/'));
                    break;
                case PdfFormFieldKind.Button:
                    string on = widget.OnValue.TrimStart('/');
                    editor.SetCheckBoxValue(field.Name, first.TrimStart('/').Equals(on,
                        StringComparison.Ordinal) || first.Equals("true", StringComparison.OrdinalIgnoreCase)
                        || first == "1");
                    break;
                default:
                    continue;
            }
            changed = true;
        }
        if (!changed) throw new InvalidOperationException("The form data has no applicable field values.");
        return editor.Build();
    }

    private static Dictionary<string, PdfFormWidgetInfo> Widgets(PdfDocument document)
    {
        int pageCount = PdfDocumentInformation.Read(document).PageCount;
        var result = new Dictionary<string, PdfFormWidgetInfo>(StringComparer.Ordinal);
        for (int page = 0; page < pageCount; page++)
            foreach (PdfFormWidgetInfo widget in PdfFormWidgetReader.ReadPage(document, page))
                result.TryAdd(widget.FieldName, widget);
        return result;
    }
}

/// <summary>The preview status for one imported form-data field.</summary>
public enum PdfFormDataMatchStatus
{
    /// <summary>The field can be applied.</summary>
    Matched,
    /// <summary>No destination field has this name.</summary>
    Unmatched,
    /// <summary>The destination field is read-only.</summary>
    ReadOnly,
    /// <summary>The destination field type cannot accept imported values.</summary>
    Incompatible
}

/// <summary>Describes one previewed form-data match.</summary>
public sealed record PdfFormDataMatch
{
    /// <summary>Gets the supplied field name.</summary>
    public required string FieldName { get; init; }
    /// <summary>Gets the match status.</summary>
    public PdfFormDataMatchStatus Status { get; init; }
    /// <summary>Gets the destination field kind when found.</summary>
    public PdfFormFieldKind FieldKind { get; init; }
    /// <summary>Gets whether the destination field is required.</summary>
    public bool IsRequired { get; init; }
    /// <summary>Gets whether the destination field is excluded from export.</summary>
    public bool IsNoExport { get; init; }
}

/// <summary>A value-free summary of a planned FDF or XFDF import.</summary>
public sealed record PdfFormDataImportReport(
    int TotalFieldCount,
    int ApplicableFieldCount,
    int BlockedFieldCount,
    int RequiredFieldCount,
    int NoExportFieldCount,
    IReadOnlyList<PdfFormDataMatch> Fields)
{
    /// <summary>Serializes the report without exposing imported field values.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(this,
        new JsonSerializerOptions
        {
            WriteIndented = indented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        });
}
