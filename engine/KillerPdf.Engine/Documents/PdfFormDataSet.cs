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
}

/// <summary>One named field and its scalar or multi-select values.</summary>
public sealed record PdfFormDataField
{
    /// <summary>Gets the fully qualified field name.</summary>
    public required string Name { get; init; }
    /// <summary>Gets the field values in source order.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];
}
