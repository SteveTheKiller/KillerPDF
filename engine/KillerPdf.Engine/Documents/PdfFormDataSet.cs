namespace KillerPdf.Engine.Documents;

/// <summary>Portable field values read from or written to FDF-compatible interchange.</summary>
public sealed record PdfFormDataSet
{
    /// <summary>Gets the optional source PDF reference.</summary>
    public string? SourcePdfPath { get; init; }
    /// <summary>Gets the optional embedded source PDF bytes.</summary>
    public ReadOnlyMemory<byte>? EmbeddedSourcePdf { get; init; }
    /// <summary>Gets the ordered form field values.</summary>
    public IReadOnlyList<PdfFormDataField> Fields { get; init; } = [];
    /// <summary>Gets the ordered annotations carried by the interchange file.</summary>
    public IReadOnlyList<PdfFormDataAnnotation> Annotations { get; init; } = [];
    /// <summary>Gets whether the source contains document JavaScript that was not executed.</summary>
    public bool ContainsJavaScript { get; init; }
    /// <summary>Gets whether the source carries an FDF signature dictionary.</summary>
    public bool ContainsSignature { get; init; }
    /// <summary>Gets whether the source carries incremental PDF differences.</summary>
    public bool ContainsIncrementalDifferences { get; init; }

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

    /// <summary>Returns annotations from selected zero-based pages in source order.</summary>
    public PdfFormDataSet SelectAnnotationPages(IEnumerable<int> pageIndexes)
    {
        ArgumentNullException.ThrowIfNull(pageIndexes);
        int[] requested = pageIndexes.ToArray();
        if (requested.Any(page => page < 0)
            || requested.Distinct().Count() != requested.Length)
            throw new ArgumentException(
                "Selected annotation pages must be nonnegative and unique.", nameof(pageIndexes));
        var selected = requested.ToHashSet();
        return this with
        {
            Annotations = Array.AsReadOnly(Annotations
                .Where(annotation => selected.Contains(annotation.PageIndex)).ToArray())
        };
    }
}

/// <summary>One portable annotation carried by FDF-compatible interchange.</summary>
public sealed record PdfFormDataAnnotation
{
    /// <summary>Gets the XFDF annotation element name.</summary>
    public required string Subtype { get; init; }
    /// <summary>Gets the zero-based page index.</summary>
    public required int PageIndex { get; init; }
    /// <summary>Gets the annotation rectangle as left, bottom, right, and top coordinates.</summary>
    public required IReadOnlyList<double> Rectangle { get; init; }
    /// <summary>Gets the optional stable annotation identifier.</summary>
    public string? Name { get; init; }
    /// <summary>Gets the optional review text.</summary>
    public string? Contents { get; init; }
    /// <summary>Gets the optional author.</summary>
    public string? Author { get; init; }
    /// <summary>Gets the optional subject.</summary>
    public string? Subject { get; init; }
    /// <summary>Gets the optional RGB color as a hexadecimal value.</summary>
    public string? Color { get; init; }
    /// <summary>Gets the optional opacity from zero through one.</summary>
    public double? Opacity { get; init; }
    /// <summary>Gets the optional creation date in its source representation.</summary>
    public string? CreationDate { get; init; }
    /// <summary>Gets the optional modification date in its source representation.</summary>
    public string? ModifiedDate { get; init; }
    /// <summary>Gets the optional identifier of the annotation this annotation replies to.</summary>
    public string? ReplyToName { get; init; }
}

/// <summary>One named field and its scalar or multi-select values.</summary>
public sealed record PdfFormDataField
{
    /// <summary>Gets the fully qualified field name.</summary>
    public required string Name { get; init; }
    /// <summary>Gets the field values in source order.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];
}
