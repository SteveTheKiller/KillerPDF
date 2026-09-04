using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;

namespace KillerPdf.Engine.Diagnostics;

/// <summary>A reviewed, unambiguous document-language accessibility correction.</summary>
public sealed record PdfAccessibilityLanguageRepair(
    string Language, PdfAccessibilityReport Before, bool WillChange);

/// <summary>The saved document and accessibility reports surrounding one repair.</summary>
public sealed record PdfAccessibilityRepairResult(
    ReadOnlyMemory<byte> Document,
    PdfAccessibilityReport Before,
    PdfAccessibilityReport After);

/// <summary>Previews and applies accessibility corrections that require no semantic inference.</summary>
public static class PdfAccessibilityRepair
{
    /// <summary>Previews adding a supplied primary language when the document has none.</summary>
    public static PdfAccessibilityLanguageRepair PreviewDocumentLanguage(
        PdfDocument document, string language)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        _ = new PdfIncrementalPageEditor(document).SetDocumentLanguage(language);
        PdfAccessibilityReport before = PdfAccessibilityInspector.Inspect(document);
        bool missing = before.Findings.Any(finding =>
            finding.Code == PdfAccessibilityFindingCode.MissingDocumentLanguage);
        return new PdfAccessibilityLanguageRepair(language, before, missing);
    }

    /// <summary>Applies a previewed missing-language correction and verifies the saved result.</summary>
    public static PdfAccessibilityRepairResult ApplyDocumentLanguage(
        PdfDocument document, PdfAccessibilityLanguageRepair repair)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(repair);
        PdfAccessibilityReport before = PdfAccessibilityInspector.Inspect(document);
        bool missing = before.Findings.Any(finding =>
            finding.Code == PdfAccessibilityFindingCode.MissingDocumentLanguage);
        if (!repair.WillChange || !missing)
            throw new InvalidOperationException(
                "The document does not have the previewed missing-language finding.");
        byte[] saved = new PdfIncrementalPageEditor(document)
            .SetDocumentLanguage(repair.Language)
            .Build();
        PdfAccessibilityReport after = PdfAccessibilityInspector.Inspect(
            PdfDocument.Open(saved));
        if (after.Findings.Any(finding =>
                finding.Code == PdfAccessibilityFindingCode.MissingDocumentLanguage))
            throw new InvalidOperationException(
                "The saved document still has no primary language.");
        return new PdfAccessibilityRepairResult(saved, before, after);
    }
}
