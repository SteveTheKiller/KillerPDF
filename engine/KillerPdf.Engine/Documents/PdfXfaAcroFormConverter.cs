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
        TextControls.Concat(["checkButton", "choiceList", "imageEdit", "signature"]),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Preserves source pages, removes XFA, and authors editable text widgets.</summary>
    public static byte[] Convert(PdfDocument document) =>
        Convert(document, PdfXfaConversionMode.Editable);

    /// <summary>Converts XFA fields to editable widgets or flattened page content.</summary>
    public static byte[] Convert(PdfDocument document, PdfXfaConversionMode mode)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        PdfXfaInfo info = PdfXfaReader.Read(document)
            ?? throw new InvalidOperationException("The document has no XFA form.");
        if (!info.IsPacketArray)
            throw new NotSupportedException("Combined XDP conversion is not supported.");
        PdfXfaTemplateInfo template = PdfXfaTemplate.Read(info);
        Dictionary<string, PdfXfaTemplateField> fields = template.Fields.ToDictionary(
            field => field.Path, StringComparer.Ordinal);
        PdfXfaTemplateField? unsupported = fields.Values.FirstOrDefault(field =>
            field.ControlType is null || !ConvertibleControls.Contains(field.ControlType));
        if (unsupported is not null)
            throw new NotSupportedException(
                $"XFA control '{unsupported.ControlType ?? "unknown"}' cannot be converted.");
        bool hasImages = fields.Values.Any(field =>
            field.ControlType!.Equals("imageEdit", StringComparison.OrdinalIgnoreCase));
        if (hasImages && mode != PdfXfaConversionMode.Flattened)
            throw new NotSupportedException(
                "XFA image fields can be preserved only in flattened output.");
        if (hasImages && info.FormType == PdfXfaFormType.Dynamic)
            throw new NotSupportedException(
                "Dynamic XFA image-field conversion is not supported.");
        Dictionary<string, PdfXfaImageValue> images = hasImages
            ? PdfXfaImages.Read(info).ToDictionary(image => image.FieldPath, StringComparer.Ordinal)
            : [];
        PdfFormDataSet data = PdfXfaDatasets.Read(info);
        Dictionary<string, string> values = data.Fields.ToDictionary(
            field => field.Name,
            field => field.Values.Count == 0 ? string.Empty : field.Values[0],
            StringComparer.Ordinal);

        var editor = new PdfIncrementalPageEditor(document).RemoveXfa();
        if (info.FormType == PdfXfaFormType.Dynamic)
        {
            PdfPageContent firstPage = new PdfPageContentReader(document).Read(0);
            PdfXfaFlowLayoutPlan flow = PdfXfaFlowLayout.Plan(
                info, data, firstPage.Width, firstPage.Height, 0);
            while (editor.PageCount < flow.PageCount)
                editor.AddBlankPage(firstPage.Width, firstPage.Height);
            PdfDocument expanded = PdfDocument.Open(editor.Build());
            editor = new PdfIncrementalPageEditor(expanded);
            foreach (PdfXfaFlowFieldPlacement placement in flow.Placements)
            {
                PdfXfaTemplateField field = fields[placement.FieldPath];
                string name = placement.FieldPath + "[" + placement.OccurrenceIndex + "]";
                AddField(editor, field, name, placement.PageIndex, placement.X,
                    firstPage.Height - placement.Y - placement.Height,
                    placement.Width, placement.Height, placement.Value);
            }
            return Finish(editor, mode);
        }

        PdfXfaStaticLayoutPlan layout = PdfXfaStaticLayout.Plan(info);
        if (layout.UnsupportedFlowedFieldPaths.Count != 0)
            throw new NotSupportedException("Flowed XFA fields require a dynamic form declaration.");
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
            if (field.ControlType!.Equals("imageEdit", StringComparison.OrdinalIgnoreCase))
            {
                AddImage(editor, field, images, placement.PageIndex, page.Width, page.Height,
                    placement.X, bottom, placement.Width, placement.Height);
                continue;
            }
            AddField(editor, field, placement.FieldPath, placement.PageIndex,
                placement.X, bottom, placement.Width, placement.Height, value);
        }
        return Finish(editor, mode);
    }

    private static void AddImage(PdfIncrementalPageEditor editor, PdfXfaTemplateField field,
        IReadOnlyDictionary<string, PdfXfaImageValue> images, int pageIndex,
        double pageWidth, double pageHeight, double x, double bottom, double width, double height)
    {
        if (!images.TryGetValue(field.Path, out PdfXfaImageValue? image) || image.Data.IsEmpty)
            throw new NotSupportedException(
                $"XFA image field '{field.Path}' has no embedded image value.");
        if (image.IsExternal)
            throw new NotSupportedException(
                $"XFA image field '{field.Path}' requires an external resource.");
        if (!string.Equals(image.ContentType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                $"XFA image field '{field.Path}' does not contain a supported JPEG image.");
        var content = new PdfContentStreamBuilder()
            .DrawImage(PdfImage.FromJpeg(image.Data), x, bottom, width, height);
        editor.AppendPageContent(pageIndex, pageWidth, pageHeight, content);
    }

    private static byte[] Finish(PdfIncrementalPageEditor editor, PdfXfaConversionMode mode)
    {
        byte[] converted = editor.Build();
        return mode == PdfXfaConversionMode.Flattened
            ? PdfFormFlattener.Flatten(PdfDocument.Open(converted)) : converted;
    }

    private static void AddField(PdfIncrementalPageEditor editor,
        PdfXfaTemplateField field, string name, int pageIndex,
        double x, double bottom, double width, double height, string? value)
    {
        var metadata = new PdfFormFieldMetadata
        {
            Tooltip = field.Description,
            MappingName = field.Path
        };
        double fontSize = field.Appearance.FontSize ?? 12;
        PdfTextFieldAlignment alignment = field.Appearance.Alignment ?? PdfTextFieldAlignment.Left;
        var style = new PdfFormFieldAppearanceStyle
        {
            BackgroundColor = field.Appearance.BackgroundColor ?? new PdfRgbColor(1, 1, 1),
            BorderColor = field.Appearance.BorderColor ?? new PdfRgbColor(0, 0, 0),
            TextColor = field.Appearance.TextColor ?? new PdfRgbColor(0, 0, 0)
        };
        if (TextControls.Contains(field.ControlType!))
            editor.AddTextField(pageIndex, name, x, bottom, width, height,
                value ?? string.Empty, fontSize,
                options: new PdfTextFieldOptions { Alignment = alignment },
                fieldMetadata: metadata, appearanceStyle: style);
        else if (field.ControlType!.Equals("checkButton", StringComparison.OrdinalIgnoreCase))
            editor.AddCheckBox(pageIndex, name, x, bottom, width, height,
                Checked(value), fieldMetadata: metadata, appearanceStyle: style);
        else if (field.ControlType.Equals("choiceList", StringComparison.OrdinalIgnoreCase))
        {
            if (field.ChoiceOptions.Count == 0)
                throw new NotSupportedException($"XFA choice field '{field.Path}' has no options.");
            editor.AddComboBoxOptions(pageIndex, name, x, bottom, width, height,
                field.ChoiceOptions.Select(option => new PdfChoiceOption(
                    option.ExportValue, option.DisplayValue)), value,
                fontSize: fontSize, fieldMetadata: metadata,
                choiceOptions: new PdfChoiceFieldOptions
                {
                    Alignment = alignment,
                    AppearanceStyle = style
                });
        }
        else
        {
            if (!string.IsNullOrEmpty(value))
                throw new NotSupportedException(
                    $"Signed XFA field '{field.Path}' cannot be converted safely.");
            editor.AddSignatureField(pageIndex, name, x, bottom, width, height,
                fieldMetadata: metadata, fontSize: fontSize,
                appearanceStyle: style, appearanceAlignment: alignment);
        }
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

/// <summary>The standard-PDF output produced by XFA conversion.</summary>
public enum PdfXfaConversionMode
{
    /// <summary>Keep converted fields editable as AcroForm widgets.</summary>
    Editable,
    /// <summary>Paint widget appearances into page content and remove the fields.</summary>
    Flattened
}
