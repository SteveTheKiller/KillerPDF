using System.Text.Json;
using System.Text.Json.Serialization;

namespace KillerPdf.Engine.Documents;

/// <summary>Maps one imported record field to one PDF form field.</summary>
public sealed record PdfDataMergeFieldMapping(string SourceField, string TargetField);

/// <summary>A reusable data-merge mapping that contains no source records.</summary>
public sealed class PdfDataMergeProfile
{
    /// <summary>Creates a validated reusable mapping profile.</summary>
    public PdfDataMergeProfile(string name, IEnumerable<PdfDataMergeFieldMapping> mappings,
        string outputFileNameTemplate,
        PdfMissingMergeValueBehavior missingValueBehavior = PdfMissingMergeValueBehavior.Error)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A data-merge profile name is required.", nameof(name));
        ArgumentNullException.ThrowIfNull(mappings);
        PdfDataMergeFieldMapping[] selected = mappings.ToArray();
        if (selected.Length == 0)
            throw new ArgumentException("A data-merge profile requires at least one field mapping.",
                nameof(mappings));
        if (selected.Any(mapping => string.IsNullOrWhiteSpace(mapping.SourceField)
            || string.IsNullOrWhiteSpace(mapping.TargetField)))
            throw new ArgumentException("Data-merge field names cannot be empty.", nameof(mappings));
        if (selected.Select(mapping => mapping.TargetField)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length)
            throw new ArgumentException("Data-merge target fields must be unique.", nameof(mappings));
        if (string.IsNullOrWhiteSpace(outputFileNameTemplate))
            throw new ArgumentException("An output filename template is required.",
                nameof(outputFileNameTemplate));
        if (!Enum.IsDefined(missingValueBehavior))
            throw new ArgumentOutOfRangeException(nameof(missingValueBehavior));
        Name = name;
        Mappings = Array.AsReadOnly(selected);
        OutputFileNameTemplate = outputFileNameTemplate;
        MissingValueBehavior = missingValueBehavior;
    }

    /// <summary>Gets the profile name.</summary>
    public string Name { get; }
    /// <summary>Gets the field mappings.</summary>
    public IReadOnlyList<PdfDataMergeFieldMapping> Mappings { get; }
    /// <summary>Gets the output filename template.</summary>
    public string OutputFileNameTemplate { get; }
    /// <summary>Gets the missing-value policy.</summary>
    public PdfMissingMergeValueBehavior MissingValueBehavior { get; }

    /// <summary>Maps one record into form data and an output filename.</summary>
    public PdfDataMergeMappedRecord Map(IReadOnlyDictionary<string, string?> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var fields = new List<PdfFormDataField>(Mappings.Count);
        foreach (PdfDataMergeFieldMapping mapping in Mappings)
        {
            string value = PdfDataMerge.Expand("{{" + mapping.SourceField + "}}",
                record, MissingValueBehavior);
            fields.Add(new PdfFormDataField { Name = mapping.TargetField, Values = [value] });
        }
        string outputFileName = PdfDataMerge.Expand(
            OutputFileNameTemplate, record, MissingValueBehavior);
        if (outputFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("The mapped output filename contains invalid characters.");
        return new PdfDataMergeMappedRecord(
            new PdfFormDataSet { Fields = Array.AsReadOnly(fields.ToArray()) }, outputFileName);
    }

    /// <summary>Serializes the reusable mapping without source record values.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(
        new ProfileFile(1, Name, Mappings.ToArray(), OutputFileNameTemplate, MissingValueBehavior),
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
            file.OutputFileNameTemplate, file.MissingValueBehavior);
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
        PdfMissingMergeValueBehavior MissingValueBehavior);
}

/// <summary>One mapped form-data record and its generated output filename.</summary>
public sealed record PdfDataMergeMappedRecord(PdfFormDataSet FormData, string OutputFileName);
