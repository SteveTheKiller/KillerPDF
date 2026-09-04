using System.Globalization;
using System.Text;
using System.Text.Json;

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
    /// <summary>Exports a readable summary of selected plates and page presence.</summary>
    public string ToText()
    {
        var output = new StringBuilder();
        output.Append("Separation preview: plates ").Append(Plates.Count)
            .Append(", pages ").AppendLine(Pages.Count.ToString(CultureInfo.InvariantCulture));
        foreach (PdfSeparationPreviewPlate plate in Plates)
        {
            output.Append("  ").Append(plate.IsProcess ? "Process" : "Spot")
                .Append(" plate ").Append(plate.Name).Append(": pages ")
                .AppendLine(PageList(plate.PageIndexes));
        }
        foreach (PdfSeparationPreviewPage page in Pages)
        {
            output.Append("  Page ")
                .Append((page.PageIndex + 1).ToString(CultureInfo.InvariantCulture))
                .Append(": ")
                .AppendLine(page.PlateNames.Count == 0
                    ? "no selected plates" : string.Join(", ", page.PlateNames));
        }
        return output.ToString().TrimEnd();
    }

    /// <summary>Exports the selected plates and per-page presence without document content.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(new
    {
        Version = 1,
        Plates,
        Pages
    }, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented
    });

    private static string PageList(IEnumerable<int> pageIndexes) => string.Join(", ",
        pageIndexes.Select(index => (index + 1).ToString(CultureInfo.InvariantCulture)));

    /// <summary>Creates a preview selection from the document's declared process and spot plates.</summary>
    public static PdfSeparationPreview Create(
        PdfDocument document, IEnumerable<string> plateNames,
        IEnumerable<int>? pageIndexes = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(plateNames);
        int pageCount = PdfPageTree.Read(document).Pages.Count;
        int[] selectedPages = pageIndexes?.ToArray()
            ?? Enumerable.Range(0, pageCount).ToArray();
        if (selectedPages.Length == 0 || selectedPages.Any(index => index < 0 || index >= pageCount)
            || selectedPages.Distinct().Count() != selectedPages.Length)
            throw new ArgumentException(
                "Separation preview page indexes must be in range and unique.",
                nameof(pageIndexes));
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
                colorant.Name, colorant.IsProcess,
                Array.AsReadOnly(selectedPages
                    .Where(colorant.PageIndexes.Contains).ToArray())))];
        PdfSeparationPreviewPage[] pages = [.. selectedPages
            .Select(pageIndex => new PdfSeparationPreviewPage(pageIndex,
                Array.AsReadOnly(plates
                    .Where(plate => plate.PageIndexes.Contains(pageIndex))
                    .Select(plate => plate.Name).ToArray())))];
        return new PdfSeparationPreview(
            Array.AsReadOnly(plates), Array.AsReadOnly(pages));
    }
}
