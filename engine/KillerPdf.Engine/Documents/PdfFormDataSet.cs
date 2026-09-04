namespace KillerPdf.Engine.Documents;

/// <summary>Portable field values read from or written to FDF-compatible interchange.</summary>
public sealed record PdfFormDataSet
{
    /// <summary>Gets the optional source PDF reference.</summary>
    public string? SourcePdfPath { get; init; }
    /// <summary>Gets the ordered form field values.</summary>
    public IReadOnlyList<PdfFormDataField> Fields { get; init; } = [];
    /// <summary>Gets whether the source contains document JavaScript that was not executed.</summary>
    public bool ContainsJavaScript { get; init; }

    /// <summary>Returns an ordered subset of fields for selective import or export.</summary>
    public PdfFormDataSet SelectFields(IEnumerable<string> fieldNames)
    {
        ArgumentNullException.ThrowIfNull(fieldNames);
        string[] requested = fieldNames.ToArray();
        if (requested.Any(string.IsNullOrWhiteSpace)
            || requested.Distinct(StringComparer.Ordinal).Count() != requested.Length)
            throw new ArgumentException(
                "Selected field names must be nonempty and unique.", nameof(fieldNames));
        Dictionary<string, PdfFormDataField> available = Fields.ToDictionary(
            field => field.Name, StringComparer.Ordinal);
        var selected = new List<PdfFormDataField>(requested.Length);
        foreach (string name in requested)
            selected.Add(available.TryGetValue(name, out PdfFormDataField? field)
                ? field : throw new KeyNotFoundException(
                    $"The form data has no field named '{name}'."));
        return this with { Fields = Array.AsReadOnly(selected.ToArray()) };
    }
}

/// <summary>One named field and its scalar or multi-select values.</summary>
public sealed record PdfFormDataField
{
    /// <summary>Gets the fully qualified field name.</summary>
    public required string Name { get; init; }
    /// <summary>Gets the field values in source order.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];
}
