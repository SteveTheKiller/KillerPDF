using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace KillerPdf.Engine.Documents;

/// <summary>Maps one imported record field to one PDF form field.</summary>
public sealed record PdfDataMergeFieldMapping(
    string SourceField, string TargetField, string? DefaultValue = null,
    PdfDataMergeValueKind ValueKind = PdfDataMergeValueKind.Text,
    string? Format = null, string? CultureName = null,
    string? IncludeWhenField = null, string? IncludeWhenValue = null,
    PdfDataMergeTargetKind TargetKind = PdfDataMergeTargetKind.FormField);

/// <summary>Maps one imported record field to a fixed image placement.</summary>
public sealed record PdfDataMergeImageMapping(
    string SourceField, int PageIndex, double X, double Y, double Width, double Height,
    string? DefaultValue = null, string? IncludeWhenField = null,
    string? IncludeWhenValue = null);

/// <summary>A reusable data-merge mapping that contains no source records.</summary>
public sealed class PdfDataMergeProfile
{
    /// <summary>Creates a validated reusable mapping profile.</summary>
    public PdfDataMergeProfile(string name, IEnumerable<PdfDataMergeFieldMapping> mappings,
        string outputFileNameTemplate,
        PdfMissingMergeValueBehavior missingValueBehavior = PdfMissingMergeValueBehavior.Error,
        IEnumerable<PdfDataMergeImageMapping>? imageMappings = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A data-merge profile name is required.", nameof(name));
        ArgumentNullException.ThrowIfNull(mappings);
        PdfDataMergeFieldMapping[] selected = mappings.ToArray();
        PdfDataMergeImageMapping[] selectedImages = imageMappings?.ToArray() ?? [];
        if (selected.Length == 0 && selectedImages.Length == 0)
            throw new ArgumentException("A data-merge profile requires at least one mapping.",
                nameof(mappings));
        if (selected.Any(mapping => string.IsNullOrWhiteSpace(mapping.SourceField)
            || string.IsNullOrWhiteSpace(mapping.TargetField)))
            throw new ArgumentException("Data-merge field names cannot be empty.", nameof(mappings));
        if (selected.Any(mapping => !Enum.IsDefined(mapping.ValueKind)))
            throw new ArgumentException("A data-merge value kind is invalid.", nameof(mappings));
        if (selected.Any(mapping => !Enum.IsDefined(mapping.TargetKind)))
            throw new ArgumentException("A data-merge target kind is invalid.", nameof(mappings));
        if (selected.Any(mapping => mapping.ValueKind == PdfDataMergeValueKind.Text
            && mapping.Format is not null))
            throw new ArgumentException("Text mappings cannot have a format string.", nameof(mappings));
        if (selected.Any(mapping => string.IsNullOrWhiteSpace(mapping.IncludeWhenField)
                != string.IsNullOrWhiteSpace(mapping.IncludeWhenValue)))
            throw new ArgumentException(
                "Conditional mappings require both a field and a value.", nameof(mappings));
        foreach (PdfDataMergeFieldMapping mapping in selected)
            if (mapping.CultureName is not null)
                _ = CultureInfo.GetCultureInfo(mapping.CultureName);
        if (selected.Select(mapping => mapping.TargetField)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length)
            throw new ArgumentException("Data-merge target fields must be unique.", nameof(mappings));
        if (selectedImages.Any(mapping => string.IsNullOrWhiteSpace(mapping.SourceField)))
            throw new ArgumentException("Data-merge image source fields cannot be empty.",
                nameof(imageMappings));
        if (selectedImages.Any(mapping => mapping.PageIndex < 0
            || !double.IsFinite(mapping.X) || mapping.X < 0
            || !double.IsFinite(mapping.Y) || mapping.Y < 0
            || !double.IsFinite(mapping.Width) || mapping.Width <= 0
            || !double.IsFinite(mapping.Height) || mapping.Height <= 0))
            throw new ArgumentException("Data-merge image placements must have valid geometry.",
                nameof(imageMappings));
        if (selectedImages.Any(mapping => string.IsNullOrWhiteSpace(mapping.IncludeWhenField)
                != string.IsNullOrWhiteSpace(mapping.IncludeWhenValue)))
            throw new ArgumentException(
                "Conditional image mappings require both a field and a value.",
                nameof(imageMappings));
        if (string.IsNullOrWhiteSpace(outputFileNameTemplate))
            throw new ArgumentException("An output filename template is required.",
                nameof(outputFileNameTemplate));
        if (!Enum.IsDefined(missingValueBehavior))
            throw new ArgumentOutOfRangeException(nameof(missingValueBehavior));
        Name = name;
        Mappings = Array.AsReadOnly(selected);
        ImageMappings = Array.AsReadOnly(selectedImages);
        OutputFileNameTemplate = outputFileNameTemplate;
        MissingValueBehavior = missingValueBehavior;
    }

    /// <summary>Gets the profile name.</summary>
    public string Name { get; }
    /// <summary>Gets the field mappings.</summary>
    public IReadOnlyList<PdfDataMergeFieldMapping> Mappings { get; }
    /// <summary>Gets the fixed image placements populated by record values.</summary>
    public IReadOnlyList<PdfDataMergeImageMapping> ImageMappings { get; }
    /// <summary>Gets the output filename template.</summary>
    public string OutputFileNameTemplate { get; }
    /// <summary>Gets the missing-value policy.</summary>
    public PdfMissingMergeValueBehavior MissingValueBehavior { get; }

    /// <summary>Maps one record into form data and an output filename.</summary>
    public PdfDataMergeMappedRecord Map(IReadOnlyDictionary<string, string?> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var fields = new List<PdfFormDataField>(Mappings.Count);
        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        var images = new List<PdfDataMergeMappedImage>(ImageMappings.Count);
        foreach (PdfDataMergeFieldMapping mapping in Mappings)
        {
            if (mapping.IncludeWhenField is not null
                && (!record.TryGetValue(mapping.IncludeWhenField, out string? conditionValue)
                    || !string.Equals(conditionValue, mapping.IncludeWhenValue,
                        StringComparison.Ordinal)))
                continue;
            string value = record.TryGetValue(mapping.SourceField, out string? supplied)
                && supplied is not null
                ? supplied
                : mapping.DefaultValue ?? PdfDataMerge.Expand(
                    "{{" + mapping.SourceField + "}}", record, MissingValueBehavior);
            string formatted = FormatValue(mapping, value);
            if (mapping.TargetKind == PdfDataMergeTargetKind.FormField)
                fields.Add(new PdfFormDataField
                {
                    Name = mapping.TargetField,
                    Values = [formatted]
                });
            else
                text.Add("{{" + mapping.TargetField + "}}", formatted);
        }
        foreach (PdfDataMergeImageMapping mapping in ImageMappings)
        {
            if (mapping.IncludeWhenField is not null
                && (!record.TryGetValue(mapping.IncludeWhenField, out string? conditionValue)
                    || !string.Equals(conditionValue, mapping.IncludeWhenValue,
                        StringComparison.Ordinal)))
                continue;
            string value = record.TryGetValue(mapping.SourceField, out string? supplied)
                && supplied is not null
                ? supplied
                : mapping.DefaultValue ?? PdfDataMerge.Expand(
                    "{{" + mapping.SourceField + "}}", record, MissingValueBehavior);
            images.Add(new PdfDataMergeMappedImage(mapping, value));
        }
        string outputFileName = PdfDataMerge.Expand(
            OutputFileNameTemplate, record, MissingValueBehavior);
        if (outputFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("The mapped output filename contains invalid characters.");
        return new PdfDataMergeMappedRecord(
            new PdfFormDataSet { Fields = Array.AsReadOnly(fields.ToArray()) }, outputFileName)
        {
            TextReplacements = text,
            Images = Array.AsReadOnly(images.ToArray())
        };
    }

    private static string FormatValue(PdfDataMergeFieldMapping mapping, string value)
    {
        CultureInfo culture = mapping.CultureName is null
            ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(mapping.CultureName);
        return mapping.ValueKind switch
        {
            PdfDataMergeValueKind.Text => value,
            PdfDataMergeValueKind.Number => decimal.TryParse(value, NumberStyles.Number, culture,
                out decimal number)
                ? number.ToString(mapping.Format, culture)
                : throw new FormatException(
                    $"Field '{mapping.SourceField}' is not a valid number."),
            PdfDataMergeValueKind.Date => DateTimeOffset.TryParse(value, culture,
                DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset date)
                ? date.ToString(mapping.Format, culture)
                : throw new FormatException(
                    $"Field '{mapping.SourceField}' is not a valid date."),
            _ => throw new ArgumentOutOfRangeException(nameof(mapping))
        };
    }

    /// <summary>Serializes the reusable mapping without source record values.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(
        new ProfileFile(1, Name, Mappings.ToArray(), OutputFileNameTemplate, MissingValueBehavior,
            ImageMappings.ToArray()),
        Options(indented));

    /// <summary>Reads a reusable mapping profile.</summary>
    public static PdfDataMergeProfile FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ProfileFile file = JsonSerializer.Deserialize<ProfileFile>(json, Options(false))
            ?? throw new JsonException("The data-merge profile is empty.");
        if (file.Version != 1)
            throw new NotSupportedException(
                $"Data-merge profile version {file.Version} is not supported.");
        return new PdfDataMergeProfile(file.Name, file.Mappings,
            file.OutputFileNameTemplate, file.MissingValueBehavior, file.ImageMappings);
    }

    /// <summary>Creates a macro step containing only this reusable profile configuration.</summary>
    public PdfMacroStep ToMacroStep() => new(PdfMacroOperation.DataMerge,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profile"] = ToJson()
        });

    /// <summary>Reads a reusable profile from a typed data-merge macro step.</summary>
    public static PdfDataMergeProfile FromMacroStep(PdfMacroStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Operation != PdfMacroOperation.DataMerge)
            throw new ArgumentException("The macro step is not a data-merge operation.", nameof(step));
        if (step.Settings is null
            || !step.Settings.TryGetValue("profile", out string? profile)
            || string.IsNullOrWhiteSpace(profile))
            throw new ArgumentException("The data-merge macro step has no profile.", nameof(step));
        return FromJson(profile);
    }

    private static JsonSerializerOptions Options(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = indented
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record ProfileFile(int Version, string Name,
        PdfDataMergeFieldMapping[] Mappings, string OutputFileNameTemplate,
        PdfMissingMergeValueBehavior MissingValueBehavior,
        PdfDataMergeImageMapping[]? ImageMappings);
}

/// <summary>One mapped form-data record and its generated output filename.</summary>
public sealed record PdfDataMergeMappedRecord(PdfFormDataSet FormData, string OutputFileName)
{
    /// <summary>Gets exact Latin-1 page-text placeholders and their replacement values.</summary>
    public IReadOnlyDictionary<string, string> TextReplacements { get; init; } =
        new Dictionary<string, string>();
    /// <summary>Gets record-specific image references and their saved placements.</summary>
    public IReadOnlyList<PdfDataMergeMappedImage> Images { get; init; } = [];
}

/// <summary>One record-specific image reference and its saved placement.</summary>
public sealed record PdfDataMergeMappedImage(
    PdfDataMergeImageMapping Mapping, string SourceValue);

/// <summary>The PDF destination receiving a mapped value.</summary>
public enum PdfDataMergeTargetKind
{
    /// <summary>An AcroForm field name.</summary>
    FormField,
    /// <summary>A double-braced placeholder in a Latin-1 page text operand.</summary>
    TextPlaceholder
}

/// <summary>The value conversion applied by a reusable field mapping.</summary>
public enum PdfDataMergeValueKind
{
    /// <summary>Preserve the source text.</summary>
    Text,
    /// <summary>Parse and format a decimal number.</summary>
    Number,
    /// <summary>Parse and format a date with an optional time and offset.</summary>
    Date
}
