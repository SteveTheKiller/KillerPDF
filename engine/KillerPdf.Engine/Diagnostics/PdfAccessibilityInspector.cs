using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Diagnostics;

/// <summary>Stable accessibility checks for document-level tagged-PDF requirements.</summary>
public static class PdfAccessibilityInspector
{
    /// <summary>Checks the document language and tagged-PDF catalog declarations.</summary>
    public static PdfAccessibilityReport Inspect(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsDecrypted)
            throw new InvalidOperationException("Authenticate the document before accessibility inspection.");
        PdfDictionary catalog = PdfPageTree.Read(document).Catalog;
        var findings = new List<PdfAccessibilityFinding>();
        if (!catalog.ContainsKey(Name("Lang")))
            findings.Add(Finding(PdfAccessibilityFindingCode.MissingDocumentLanguage,
                "The document catalog has no primary language."));
        if (!catalog.ContainsKey(Name("StructTreeRoot")))
            findings.Add(Finding(PdfAccessibilityFindingCode.MissingStructureTree,
                "The document catalog has no structure tree."));
        if (!catalog.TryGetValue(Name("MarkInfo"), out PdfObject? markInfoValue)
            || Resolve(document, markInfoValue) is not PdfDictionary markInfo
            || !markInfo.TryGetValue(Name("Marked"), out PdfObject? markedValue)
            || Resolve(document, markedValue) is not PdfBoolean { Value: true })
            findings.Add(Finding(PdfAccessibilityFindingCode.DocumentNotMarked,
                "The document catalog does not declare marked content."));
        return new PdfAccessibilityReport(findings);
    }

    private static PdfAccessibilityFinding Finding(PdfAccessibilityFindingCode code, string message) =>
        new(code, PdfDiagnosticSeverity.Error, message);

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("An accessibility metadata reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}

/// <summary>Stable document-level accessibility finding categories.</summary>
public enum PdfAccessibilityFindingCode
{
    /// <summary>The catalog has no primary natural language.</summary>
    MissingDocumentLanguage,
    /// <summary>The catalog has no tagged structure tree.</summary>
    MissingStructureTree,
    /// <summary>The catalog does not declare marked content.</summary>
    DocumentNotMarked
}

/// <summary>One accessibility finding with a stable code and severity.</summary>
public sealed record PdfAccessibilityFinding(
    PdfAccessibilityFindingCode Code, PdfDiagnosticSeverity Severity, string Message);

/// <summary>Accessibility findings produced for one document.</summary>
public sealed class PdfAccessibilityReport
{
    internal PdfAccessibilityReport(IEnumerable<PdfAccessibilityFinding> findings) =>
        Findings = Array.AsReadOnly(findings.ToArray());

    /// <summary>Gets findings in deterministic check order.</summary>
    public IReadOnlyList<PdfAccessibilityFinding> Findings { get; }
    /// <summary>Gets whether the implemented accessibility checks passed.</summary>
    public bool PassesImplementedChecks =>
        !Findings.Any(finding => finding.Severity == PdfDiagnosticSeverity.Error);
}
