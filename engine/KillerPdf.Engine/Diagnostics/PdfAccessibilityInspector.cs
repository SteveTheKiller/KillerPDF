using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        PdfPageTree pageTree = PdfPageTree.Read(document);
        PdfDictionary catalog = pageTree.Catalog;
        var findings = new List<PdfAccessibilityFinding>();
        if (!catalog.TryGetValue(Name("Lang"), out PdfObject? languageValue)
            || Resolve(document, languageValue) is not PdfString language
            || string.IsNullOrWhiteSpace(PdfUnicodeEncoding.DecodeTextString(
                language.Bytes.Span, "The document language")))
            findings.Add(Finding(PdfAccessibilityFindingCode.MissingDocumentLanguage,
                "The document catalog has no primary language."));
        if (!catalog.TryGetValue(Name("StructTreeRoot"), out PdfObject? structureRootValue))
            findings.Add(Finding(PdfAccessibilityFindingCode.MissingStructureTree,
                "The document catalog has no structure tree."));
        else
            InspectStructureTree(structureRootValue);
        if (!catalog.TryGetValue(Name("MarkInfo"), out PdfObject? markInfoValue)
            || Resolve(document, markInfoValue) is not PdfDictionary markInfo
            || !markInfo.TryGetValue(Name("Marked"), out PdfObject? markedValue)
            || Resolve(document, markedValue) is not PdfBoolean { Value: true })
            findings.Add(Finding(PdfAccessibilityFindingCode.DocumentNotMarked,
                "The document catalog does not declare marked content."));
        foreach (PdfPageTreeEntry page in pageTree.Pages)
        {
            foreach (PdfFormWidgetInfo widget in PdfFormWidgetReader.ReadPage(document, page.Index))
                if (string.IsNullOrWhiteSpace(widget.Tooltip))
                    findings.Add(Finding(PdfAccessibilityFindingCode.MissingFormFieldDescription,
                        $"Form field {widget.FieldName} has no user-facing description.",
                        page.Index, widget.ObjectNumber == 0 ? null : widget.ObjectNumber));
            foreach (PdfLinkInfo link in PdfLinkReader.ReadPage(document, page.Index))
                if (string.IsNullOrWhiteSpace(link.Description))
                    findings.Add(Finding(PdfAccessibilityFindingCode.MissingLinkDescription,
                        "A link annotation has no user-facing description.",
                        page.Index, link.ObjectNumber));
        }
        return new PdfAccessibilityReport(findings);

        void InspectStructureTree(PdfObject rootValue)
        {
            var visited = new HashSet<(int, int)>();
            var pageIndexes = pageTree.Pages.ToDictionary(
                page => (page.Reference.ObjectNumber, page.Reference.Generation),
                page => page.Index);
            try
            {
                PdfDictionary root = Resolve(document, rootValue) as PdfDictionary
                    ?? throw new InvalidOperationException("The structure-tree root is not a dictionary.");
                if (root.TryGetValue(Name("K"), out PdfObject? kids)) Visit(kids, 0);
            }
            catch (Exception error) when (error is ArgumentException or FormatException
                or InvalidOperationException or NotSupportedException or OverflowException)
            {
                findings.Add(Finding(PdfAccessibilityFindingCode.InvalidStructureTree,
                    error.Message));
            }

            void Visit(PdfObject value, int depth)
            {
                if (depth >= 256)
                    throw new InvalidOperationException("The structure tree exceeds the supported nesting depth.");
                PdfIndirectReference? reference = value as PdfIndirectReference;
                if (reference is not null
                    && !visited.Add((reference.ObjectNumber, reference.Generation))) return;
                value = Resolve(document, value);
                if (value is PdfArray array)
                {
                    foreach (PdfObject item in array) Visit(item, depth + 1);
                    return;
                }
                if (value is not PdfDictionary element) return;
                if (IsName(element, "Type", "StructElem") && IsName(element, "S", "Figure")
                    && !HasNonemptyText(element, "Alt"))
                    findings.Add(Finding(
                        PdfAccessibilityFindingCode.MissingFigureAlternateDescription,
                        "A figure structure element has no alternate description.",
                        PageIndex(element), reference?.ObjectNumber));
                if (IsName(element, "Type", "StructElem") && IsName(element, "S", "Table"))
                {
                    (bool hasHeader, bool hasData, int? pageIndex) = TableSummary(element, depth + 1);
                    if (hasData && !hasHeader)
                        findings.Add(Finding(PdfAccessibilityFindingCode.MissingTableHeader,
                            "A table contains data cells but no header cells.",
                            pageIndex, reference?.ObjectNumber));
                }
                if (element.TryGetValue(Name("K"), out PdfObject? children))
                    Visit(children, depth + 1);
            }

            (bool HasHeader, bool HasData, int? PageIndex) TableSummary(
                PdfDictionary table, int depth)
            {
                var tableVisited = new HashSet<(int, int)>();
                bool hasHeader = false;
                bool hasData = false;
                int? pageIndex = PageIndex(table);
                if (table.TryGetValue(Name("K"), out PdfObject? children))
                    Scan(children, depth);
                return (hasHeader, hasData, pageIndex);

                void Scan(PdfObject value, int childDepth)
                {
                    if (childDepth >= 256)
                        throw new InvalidOperationException(
                            "The structure tree exceeds the supported nesting depth.");
                    if (value is PdfIndirectReference childReference
                        && !tableVisited.Add((childReference.ObjectNumber,
                            childReference.Generation))) return;
                    value = Resolve(document, value);
                    if (value is PdfArray array)
                    {
                        foreach (PdfObject item in array) Scan(item, childDepth + 1);
                        return;
                    }
                    if (value is not PdfDictionary child) return;
                    if (IsName(child, "Type", "StructElem"))
                    {
                        if (IsName(child, "S", "Table")) return;
                        hasHeader |= IsName(child, "S", "TH");
                        hasData |= IsName(child, "S", "TD");
                        pageIndex ??= PageIndex(child);
                    }
                    if (child.TryGetValue(Name("K"), out PdfObject? descendants))
                        Scan(descendants, childDepth + 1);
                }
            }

            int? PageIndex(PdfDictionary element)
            {
                if (!element.TryGetValue(Name("Pg"), out PdfObject? pageValue)
                    || pageValue is not PdfIndirectReference pageReference) return null;
                return pageIndexes.TryGetValue(
                    (pageReference.ObjectNumber, pageReference.Generation), out int pageIndex)
                    ? pageIndex : null;
            }

            bool HasNonemptyText(PdfDictionary element, string key) =>
                element.TryGetValue(Name(key), out PdfObject? value)
                    && Resolve(document, value) is PdfString text
                    && !string.IsNullOrWhiteSpace(PdfUnicodeEncoding.DecodeTextString(
                        text.Bytes.Span, $"A structure element /{key} value"));
        }
    }

    private static PdfAccessibilityFinding Finding(PdfAccessibilityFindingCode code, string message,
        int? pageIndex = null, int? objectNumber = null) =>
        new(code, PdfDiagnosticSeverity.Error, message, pageIndex, objectNumber);

    private static bool IsName(PdfDictionary dictionary, string key, string expected) =>
        dictionary.TryGetValue(Name(key), out PdfObject? value)
            && value is PdfName name && name.ValueAsLatin1() == expected;

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
    DocumentNotMarked,
    /// <summary>The structure tree cannot be inspected safely.</summary>
    InvalidStructureTree,
    /// <summary>A figure structure element has no alternate description.</summary>
    MissingFigureAlternateDescription,
    /// <summary>An interactive form field has no user-facing description.</summary>
    MissingFormFieldDescription,
    /// <summary>A link annotation has no user-facing description.</summary>
    MissingLinkDescription,
    /// <summary>A tagged table has data cells but no header cells.</summary>
    MissingTableHeader
}

/// <summary>One accessibility finding with a stable code and severity.</summary>
public sealed record PdfAccessibilityFinding(
    PdfAccessibilityFindingCode Code, PdfDiagnosticSeverity Severity, string Message,
    int? PageIndex = null, int? ObjectNumber = null);

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

    /// <summary>Formats a readable report with page and object locations.</summary>
    public string ToText()
    {
        var output = new StringBuilder()
            .Append("Accessibility result: ")
            .AppendLine(PassesImplementedChecks ? "Passed" : "Failed");
        if (Findings.Count == 0) return output.AppendLine("No findings.").ToString();
        foreach (PdfAccessibilityFinding finding in Findings)
        {
            output.Append('[').Append(finding.Severity).Append("] ")
                .Append(finding.Code);
            if (finding.PageIndex is int pageIndex)
                output.Append(" | Page ").Append(pageIndex + 1);
            if (finding.ObjectNumber is int objectNumber)
                output.Append(" | Object ").Append(objectNumber);
            output.Append(": ").AppendLine(finding.Message);
        }
        return output.ToString();
    }

    /// <summary>Serializes the report with stable camel-case names and string enums.</summary>
    public string ToJson(bool indented = false)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return JsonSerializer.Serialize(new { PassesImplementedChecks, Findings }, options);
    }
}
