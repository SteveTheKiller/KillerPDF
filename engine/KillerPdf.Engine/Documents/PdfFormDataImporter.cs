using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using KillerPdf.Engine.Authoring;
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
        Dictionary<string, IReadOnlyList<PdfFormWidgetInfo>> widgets = Widgets(document);
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
            if (!widgets.TryGetValue(field.Name, out IReadOnlyList<PdfFormWidgetInfo>? fieldWidgets))
                status = PdfFormDataMatchStatus.Unmatched;
            else
            {
                PdfFormWidgetInfo widget = fieldWidgets[0];
                kind = widget.FieldKind;
                flags = widget.Flags;
                status = (flags & 1) != 0 ? PdfFormDataMatchStatus.ReadOnly
                    : kind == PdfFormFieldKind.Signature || kind == PdfFormFieldKind.Unknown
                        || (kind == PdfFormFieldKind.Button && (flags & (1L << 16)) != 0)
                        ? PdfFormDataMatchStatus.Incompatible
                        : ValuesAreValid(field, fieldWidgets)
                            ? PdfFormDataMatchStatus.Matched
                            : PdfFormDataMatchStatus.InvalidValue;
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
    public static byte[] Apply(PdfDocument document, PdfFormDataSet data) =>
        Apply(document, data, PdfFormDataImportOutputMode.Editable);

    /// <summary>Applies matched values and optionally flattens the updated form.</summary>
    public static byte[] Apply(PdfDocument document, PdfFormDataSet data,
        PdfFormDataImportOutputMode outputMode)
    {
        if (!Enum.IsDefined(outputMode))
            throw new ArgumentOutOfRangeException(nameof(outputMode));
        IReadOnlyList<PdfFormDataMatch> preview = Preview(document, data);
        Dictionary<string, IReadOnlyList<PdfFormWidgetInfo>> widgets = Widgets(document);
        var editor = new PdfIncrementalPageEditor(document);
        bool fieldsChanged = false;
        for (int index = 0; index < data.Fields.Count; index++)
        {
            if (preview[index].Status != PdfFormDataMatchStatus.Matched) continue;
            PdfFormDataField field = data.Fields[index];
            PdfFormWidgetInfo widget = widgets[field.Name][0];
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
            fieldsChanged = true;
        }
        PdfDocument updatedDocument = fieldsChanged
            ? PdfDocument.Open(editor.Build()) : document;
        bool annotationsChanged = data.Annotations.Count > 0;
        if (annotationsChanged)
            updatedDocument = PdfDocument.Open(ApplyAnnotations(updatedDocument, data.Annotations));
        if (!fieldsChanged && !annotationsChanged)
            throw new InvalidOperationException(
                "The form data has no applicable field values or annotations.");
        byte[] updated = updatedDocument.Source.ToArray();
        return outputMode == PdfFormDataImportOutputMode.Flattened
            ? PdfFormFlattener.Flatten(updatedDocument) : updated;
    }

    private static byte[] ApplyAnnotations(PdfDocument document,
        IReadOnlyList<PdfFormDataAnnotation> annotations)
    {
        var editor = new PdfIncrementalAnnotationEditor(document);
        foreach (PdfFormDataAnnotation annotation in annotations)
        {
            if (annotation.Rectangle.Count != 4)
                throw new ArgumentException(
                    "Imported annotation rectangles require four coordinates.", nameof(annotations));
            double x = annotation.Rectangle[0];
            double y = annotation.Rectangle[1];
            double width = annotation.Rectangle[2] - x;
            double height = annotation.Rectangle[3] - y;
            PdfRgbColor? color = ParseColor(annotation.Color);
            PdfAnnotationMetadata metadata = Metadata(annotation);
            string subtype = annotation.Subtype.ToLowerInvariant();
            if (subtype == "highlight")
                editor.AddHighlight(annotation.PageIndex, x, y, width, height,
                    annotation.Contents, color, annotation.Opacity ?? 0.35,
                    metadata, annotation.Name, annotation.ReplyToName);
            else if (subtype == "underline")
                editor.AddUnderline(annotation.PageIndex, x, y, width, height,
                    annotation.Contents, color, annotation.Opacity ?? 1,
                    metadata, annotation.Name, annotation.ReplyToName);
            else if (subtype is "strikeout" or "strike-out")
                editor.AddStrikeOut(annotation.PageIndex, x, y, width, height,
                    annotation.Contents, color, annotation.Opacity ?? 1,
                    metadata, annotation.Name, annotation.ReplyToName);
            else if (subtype == "squiggly")
                editor.AddSquiggly(annotation.PageIndex, x, y, width, height,
                    annotation.Contents, color, annotation.Opacity ?? 1,
                    metadata, annotation.Name, annotation.ReplyToName);
            else if (subtype == "text")
                editor.AddTextNote(annotation.PageIndex, x, y,
                    annotation.Contents ?? string.Empty, color,
                    size: Math.Max(width, height), annotationMetadata: metadata,
                    name: annotation.Name, inReplyTo: annotation.ReplyToName);
            else
                throw new NotSupportedException(
                    $"Importing {annotation.Subtype} annotations is not supported.");
        }
        return editor.Build();
    }

    private static PdfAnnotationMetadata Metadata(PdfFormDataAnnotation annotation) => new()
    {
        Author = annotation.Author,
        Subject = annotation.Subject,
        CreationDate = ParseDate(annotation.CreationDate),
        ModificationDate = ParseDate(annotation.ModifiedDate)
    };

    private static DateTimeOffset? ParseDate(string? value)
    {
        if (value is null) return null;
        string normalized = value.StartsWith("D:", StringComparison.Ordinal)
            ? value[2..] : value;
        if (DateTimeOffset.TryParseExact(normalized,
                ["yyyyMMddHHmmss'Z'", "yyyy-MM-dd'T'HH:mm:ssK"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
            return parsed;
        throw new ArgumentException($"Annotation date '{value}' is invalid.");
    }

    private static PdfRgbColor? ParseColor(string? value)
    {
        if (value is null) return null;
        if (value.Length != 7 || value[0] != '#'
            || !byte.TryParse(value.AsSpan(1, 2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out byte red)
            || !byte.TryParse(value.AsSpan(3, 2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out byte green)
            || !byte.TryParse(value.AsSpan(5, 2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out byte blue))
            throw new ArgumentException($"Annotation color '{value}' is invalid.");
        return new PdfRgbColor(red / 255d, green / 255d, blue / 255d);
    }

    private static bool ValuesAreValid(PdfFormDataField field,
        IReadOnlyList<PdfFormWidgetInfo> widgets)
    {
        PdfFormWidgetInfo widget = widgets[0];
        string first = field.Values.FirstOrDefault() ?? string.Empty;
        if (widget.FieldKind == PdfFormFieldKind.Text)
            return field.Values.Count <= 1
                && (widget.MaximumLength == 0 || first.Length <= widget.MaximumLength);
        if (widget.FieldKind == PdfFormFieldKind.Choice)
        {
            bool combo = (widget.Flags & (1L << 17)) != 0;
            bool editable = (widget.Flags & (1L << 18)) != 0;
            bool multiSelect = (widget.Flags & (1L << 21)) != 0;
            if (combo && multiSelect) return false;
            if (!multiSelect && field.Values.Count != 1) return false;
            var exportValues = new HashSet<string>(
                widget.Options.Select(option => option.ExportValue), StringComparer.Ordinal);
            return field.Values.All(value => exportValues.Contains(value) || combo && editable);
        }
        if (widget.FieldKind != PdfFormFieldKind.Button || field.Values.Count > 1)
            return false;
        string normalized = first.TrimStart('/');
        if ((widget.Flags & (1L << 15)) != 0)
            return normalized.Length == 0 || normalized == "Off"
                || widgets.Any(item => string.Equals(item.OnValue.TrimStart('/'), normalized,
                    StringComparison.Ordinal));
        return normalized.Length == 0 || normalized == "Off" || normalized == "0"
            || normalized.Equals("false", StringComparison.OrdinalIgnoreCase)
            || normalized == "1" || normalized.Equals("true", StringComparison.OrdinalIgnoreCase)
            || widgets.Any(item => string.Equals(item.OnValue.TrimStart('/'), normalized,
                StringComparison.Ordinal));
    }

    private static Dictionary<string, IReadOnlyList<PdfFormWidgetInfo>> Widgets(PdfDocument document)
    {
        int pageCount = PdfDocumentInformation.Read(document).PageCount;
        var collected = new Dictionary<string, List<PdfFormWidgetInfo>>(StringComparer.Ordinal);
        for (int page = 0; page < pageCount; page++)
            foreach (PdfFormWidgetInfo widget in PdfFormWidgetReader.ReadPage(document, page))
            {
                if (!collected.TryGetValue(widget.FieldName, out List<PdfFormWidgetInfo>? matches))
                    collected.Add(widget.FieldName, matches = []);
                matches.Add(widget);
            }
        return collected.ToDictionary(item => item.Key,
            item => (IReadOnlyList<PdfFormWidgetInfo>)item.Value.AsReadOnly(),
            StringComparer.Ordinal);
    }
}

/// <summary>Controls whether imported form values remain editable.</summary>
public enum PdfFormDataImportOutputMode
{
    /// <summary>Retain form fields and their imported values.</summary>
    Editable,
    /// <summary>Paint field appearances into page content and remove the fields.</summary>
    Flattened
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
    Incompatible,
    /// <summary>The supplied value cannot be applied to the destination field.</summary>
    InvalidValue
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
