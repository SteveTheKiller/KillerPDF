using System.IO;

namespace KillerPDF.Services;

/// <summary>Prepares current form values on a disposable print copy.</summary>
internal static class PdfPrintSource
{
    internal static PdfEngineIntegration.FormEdits Snapshot(PdfEngineIntegration.FormEdits edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        return new PdfEngineIntegration.FormEdits(
            new Dictionary<string, string>(edits.TextValues),
            new Dictionary<string, string>(edits.ChoiceValues),
            edits.MultiChoiceValues.ToDictionary(item => item.Key,
                item => (IReadOnlyList<string>)[.. item.Value]),
            new Dictionary<string, bool>(edits.CheckBoxValues),
            new Dictionary<string, string>(edits.RadioValues),
            new Dictionary<string, double>(edits.TextFontSizes),
            new Dictionary<string, FormTextStyle>(edits.TextStyles ?? new Dictionary<string, FormTextStyle>()));
    }

    internal static bool HasValues(PdfEngineIntegration.FormEdits edits) =>
        edits.TextValues.Count > 0 || edits.ChoiceValues.Count > 0
        || edits.MultiChoiceValues.Count > 0 || edits.CheckBoxValues.Count > 0
        || edits.RadioValues.Count > 0 || edits.TextFontSizes.Count > 0
        || edits.TextStyles is { Count: > 0 };

    internal static void Create(string sourcePath, string printPath, PdfEngineIntegration.FormEdits edits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(printPath);
        ArgumentNullException.ThrowIfNull(edits);
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(printPath),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Printing requires a separate PDF copy.", nameof(printPath));

        File.Copy(sourcePath, printPath, true);
        PdfEngineIntegration.ApplyFormValues(printPath, edits);
    }
}
