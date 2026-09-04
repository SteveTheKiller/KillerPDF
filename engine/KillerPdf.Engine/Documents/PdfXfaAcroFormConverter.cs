using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.Documents;

/// <summary>Converts positioned static XFA controls into editable AcroForm fields.</summary>
public static class PdfXfaAcroFormConverter
{
    private static readonly HashSet<string> TextControls = new(StringComparer.OrdinalIgnoreCase)
    {
        "dateTimeEdit", "defaultUi", "numericEdit", "passwordEdit", "textEdit"
    };
    private static readonly HashSet<string> ConvertibleControls = new(
        TextControls.Concat(["checkButton", "choiceList", "signature"]), StringComparer.OrdinalIgnoreCase);

    /// <summary>Preserves source pages, removes XFA, and authors editable text widgets.</summary>
    public static byte[] Convert(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfXfaInfo info = PdfXfaReader.Read(document)
            ?? throw new InvalidOperationException("The document has no XFA form.");
        if (!info.IsPacketArray)
            throw new NotSupportedException("Combined XDP conversion is not supported.");
        PdfXfaStaticLayoutPlan layout = PdfXfaStaticLayout.Plan(info);
        if (layout.UnsupportedFlowedFieldPaths.Count != 0)
            throw new NotSupportedException("Flowed XFA fields require the dynamic layout engine.");
        PdfXfaTemplateInfo template = PdfXfaTemplate.Read(info);
        Dictionary<string, PdfXfaTemplateField> fields = template.Fields.ToDictionary(
            field => field.Path, StringComparer.Ordinal);
        PdfXfaTemplateField? unsupported = fields.Values.FirstOrDefault(field =>
            field.ControlType is null || !ConvertibleControls.Contains(field.ControlType));
        if (unsupported is not null)
            throw new NotSupportedException(
                $"XFA control '{unsupported.ControlType ?? "unknown"}' cannot be converted to a text field.");
        Dictionary<string, string> values = PdfXfaDatasets.Read(info).Fields.ToDictionary(
            field => field.Name,
            field => field.Values.Count == 0 ? string.Empty : field.Values[0],
            StringComparer.Ordinal);

        var editor = new PdfIncrementalPageEditor(document).RemoveXfa();
        var pageSizes = new Dictionary<int, PdfPageContent>();
        foreach (PdfXfaFieldPlacement placement in layout.Placements)
        {
            if (placement.PageIndex >= editor.PageCount)
                throw new InvalidOperationException("An XFA field targets a page outside the document.");
            PdfXfaTemplateField field = fields[placement.FieldPath];
            string dataName = BindingName(field.Binding) ?? placement.FieldPath;
            values.TryGetValue(dataName, out string? value);
            if (!pageSizes.TryGetValue(placement.PageIndex, out PdfPageContent? page))
            {
                page = new PdfPageContentReader(document).Read(placement.PageIndex);
                pageSizes.Add(placement.PageIndex, page);
            }
            double bottom = page.Height - placement.Y - placement.Height;
            if (bottom < 0 || placement.X + placement.Width > page.Width)
                throw new InvalidOperationException(
                    $"XFA field '{placement.FieldPath}' lies outside its page.");
            if (TextControls.Contains(field.ControlType!))
                editor.AddTextField(placement.PageIndex, placement.FieldPath,
                    placement.X, bottom, placement.Width, placement.Height, value ?? string.Empty);
            else if (field.ControlType!.Equals("checkButton", StringComparison.OrdinalIgnoreCase))
                editor.AddCheckBox(placement.PageIndex, placement.FieldPath,
                    placement.X, bottom, placement.Width, placement.Height, Checked(value));
            else if (field.ControlType.Equals("choiceList", StringComparison.OrdinalIgnoreCase))
            {
                if (field.ChoiceOptions.Count == 0)
                    throw new NotSupportedException(
                        $"XFA choice field '{placement.FieldPath}' has no options.");
                editor.AddComboBoxOptions(placement.PageIndex, placement.FieldPath,
                    placement.X, bottom, placement.Width, placement.Height,
                    field.ChoiceOptions.Select(option => new PdfChoiceOption(
                        option.ExportValue, option.DisplayValue)), value);
            }
            else
            {
                if (!string.IsNullOrEmpty(value))
                    throw new NotSupportedException(
                        $"Signed XFA field '{placement.FieldPath}' cannot be converted safely.");
                editor.AddSignatureField(placement.PageIndex, placement.FieldPath,
                    placement.X, bottom, placement.Width, placement.Height);
            }
        }
        return editor.Build();
    }

    private static string? BindingName(string? binding)
    {
        if (string.IsNullOrWhiteSpace(binding) || binding == "none") return null;
        const string record = "$record.";
        return binding.StartsWith(record, StringComparison.Ordinal)
            ? binding[record.Length..] : binding;
    }

    private static bool Checked(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "0" or "false" or "off" or "no" => false,
        "1" or "true" or "on" or "yes" => true,
        _ => throw new InvalidOperationException(
            "An XFA check-button value is not a recognized boolean state.")
    };
}
