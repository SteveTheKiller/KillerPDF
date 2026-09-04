namespace KillerPdf.Engine.Documents;

/// <summary>A process or spot plate selected for a separation preview.</summary>
public sealed record PdfSeparationPreviewPlate(
    string Name, bool IsProcess, IReadOnlyList<int> PageIndexes);

/// <summary>The selected plates present on one page.</summary>
public sealed record PdfSeparationPreviewPage(
    int PageIndex, IReadOnlyList<string> PlateNames);

/// <summary>A validated, deterministic plate selection for rendering separation previews.</summary>
public sealed record PdfSeparationPreview(
    IReadOnlyList<PdfSeparationPreviewPlate> Plates,
    IReadOnlyList<PdfSeparationPreviewPage> Pages)
{
    /// <summary>Creates a preview selection from the document's declared process and spot plates.</summary>
    public static PdfSeparationPreview Create(
        PdfDocument document, IEnumerable<string> plateNames)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(plateNames);
        PdfSeparationColorant[] available =
            [.. PdfSeparationInspection.Inspect(document).Colorants];
        var requested = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? name in plateNames)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException(
                    "Separation plate names cannot be empty.", nameof(plateNames));
            if (!requested.Add(name))
                throw new ArgumentException(
                    $"The separation plate '{name}' was selected more than once.",
                    nameof(plateNames));
        }

        string[] unknown = [.. requested
            .Where(name => available.All(colorant => colorant.Name != name))
            .Order(StringComparer.Ordinal)];
        if (unknown.Length > 0)
            throw new ArgumentException(
                $"Unknown separation plate: {string.Join(", ", unknown)}.",
                nameof(plateNames));

        PdfSeparationPreviewPlate[] plates = [.. available
            .Where(colorant => requested.Contains(colorant.Name))
            .Select(colorant => new PdfSeparationPreviewPlate(
                colorant.Name, colorant.IsProcess, colorant.PageIndexes))];
        int pageCount = PdfPageTree.Read(document).Pages.Count;
        PdfSeparationPreviewPage[] pages = [.. Enumerable.Range(0, pageCount)
            .Select(pageIndex => new PdfSeparationPreviewPage(pageIndex,
                Array.AsReadOnly(plates
                    .Where(plate => plate.PageIndexes.Contains(pageIndex))
                    .Select(plate => plate.Name).ToArray())))];
        return new PdfSeparationPreview(
            Array.AsReadOnly(plates), Array.AsReadOnly(pages));
    }
}
