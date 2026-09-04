using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

/// <summary>A stable navigation problem category.</summary>
public enum PdfNavigationFindingCode
{
    /// <summary>A bookmark has no supported destination.</summary>
    BookmarkMissingDestination,
    /// <summary>A bookmark destination does not resolve to a local page.</summary>
    BookmarkUnresolvedDestination,
    /// <summary>A link uses an invalid or unsupported URI scheme.</summary>
    LinkUnsafeUri,
    /// <summary>A link destination does not resolve to a local page.</summary>
    LinkUnresolvedDestination,
    /// <summary>The document contains a JavaScript action that was not executed.</summary>
    DocumentJavaScript,
    /// <summary>The document contains an unsafe launch action that was not executed.</summary>
    UnsafeLaunchAction,
    /// <summary>An action /Next chain contains a reference cycle.</summary>
    CircularActionChain
}

/// <summary>A safe repair that can be offered for a navigation problem.</summary>
public enum PdfNavigationRepairKind
{
    /// <summary>Remove the broken navigation item.</summary>
    Remove,
    /// <summary>Choose a valid destination before applying a repair.</summary>
    ChooseDestination
}

/// <summary>A problem found while auditing document navigation.</summary>
public sealed record PdfNavigationFinding(PdfNavigationFindingCode Code, string Kind,
    int? SourcePageIndex, string Source, string Message,
    PdfNavigationRepairKind SuggestedRepair, int? SourceObjectNumber = null);

/// <summary>Validates resolved bookmark and link targets without executing document actions.</summary>
public static class PdfNavigationAudit
{
    /// <summary>Removes links whose URI schemes are unsafe or invalid.</summary>
    public static byte[] RemoveUnsafeLinks(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return RemoveLinks(document, link => link.Uri is not null
            && (!Uri.TryCreate(link.Uri, UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not ("http" or "https" or "mailto")));
    }

    /// <summary>Removes links whose local or named destination cannot be resolved.</summary>
    public static byte[] RemoveUnresolvedLinks(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return RemoveLinks(document, link => link.Uri is null
            && link.DestinationPageIndex is null);
    }

    private static byte[] RemoveLinks(PdfDocument document, Func<PdfLinkInfo, bool> remove)
    {
        var editor = new PdfIncrementalAnnotationEditor(document);
        bool changed = false;
        int pageCount = PdfPageTree.Read(document).Pages.Count;
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            int[] indexes = [.. PdfLinkReader.ReadPage(document, pageIndex)
                .Where(remove).Select(link => link.AnnotationIndex).OrderByDescending(index => index)];
            foreach (int annotationIndex in indexes)
            {
                editor.RemoveAnnotationAt(pageIndex, annotationIndex);
                changed = true;
            }
        }
        return changed ? editor.Build() : document.Source.ToArray();
    }

    /// <summary>Exports navigation findings as stable machine-readable JSON.</summary>
    public static string ExportJson(PdfDocument document, bool indented = true) =>
        JsonSerializer.Serialize(Inspect(document), new JsonSerializerOptions
        {
            WriteIndented = indented,
            Converters = { new JsonStringEnumConverter() }
        });

    /// <summary>Exports a readable navigation audit without executing document actions.</summary>
    public static string ExportText(PdfDocument document)
    {
        IReadOnlyList<PdfNavigationFinding> findings = Inspect(document);
        var output = new StringBuilder();
        output.Append("Navigation findings: ").AppendLine(findings.Count.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        foreach (PdfNavigationFinding finding in findings)
        {
            output.Append("  ").Append(finding.Code).Append(": ").Append(finding.Kind);
            if (finding.SourcePageIndex.HasValue)
                output.Append(", page ").Append((finding.SourcePageIndex.Value + 1).ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            if (finding.SourceObjectNumber.HasValue)
                output.Append(", object ").Append(finding.SourceObjectNumber.Value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            output.Append(", source \"").Append(OneLine(finding.Source)).AppendLine("\"");
            output.Append("    ").AppendLine(OneLine(finding.Message));
            output.Append("    Suggested repair: ").AppendLine(finding.SuggestedRepair.ToString());
        }
        return output.ToString().TrimEnd();
    }

    /// <summary>Reports bookmarks and links whose local or named targets cannot be resolved.</summary>
    public static IReadOnlyList<PdfNavigationFinding> Inspect(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        int pageCount = PdfPageTree.Read(document).Pages.Count;
        var findings = new List<PdfNavigationFinding>();
        foreach (PdfBookmarkInfo bookmark in Flatten(PdfBookmarkReader.Read(document)))
        {
            if (bookmark.DestinationPageIndex is int page && page >= 0 && page < pageCount) continue;
            if (bookmark.DestinationPageIndex is null && bookmark.NamedDestination is null
                && bookmark.Destination is null) findings.Add(new(
                    PdfNavigationFindingCode.BookmarkMissingDestination,
                    "Bookmark", null, bookmark.Title,
                    "The bookmark has no supported local destination.",
                    PdfNavigationRepairKind.ChooseDestination, bookmark.ObjectNumber));
            else if (bookmark.DestinationPageIndex is null) findings.Add(new(
                PdfNavigationFindingCode.BookmarkUnresolvedDestination,
                "Bookmark", null, bookmark.Title,
                "The bookmark destination cannot be resolved to a local page.",
                PdfNavigationRepairKind.ChooseDestination, bookmark.ObjectNumber));
        }
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            foreach (PdfLinkInfo link in PdfLinkReader.ReadPage(document, pageIndex))
            {
                if (link.Uri is not null)
                {
                    if (!Uri.TryCreate(link.Uri, UriKind.Absolute, out Uri? uri)
                        || uri.Scheme is not ("http" or "https" or "mailto"))
                        findings.Add(new(PdfNavigationFindingCode.LinkUnsafeUri,
                            "Link", pageIndex, link.Uri,
                            "The link uses an invalid or unsupported URI scheme.",
                            PdfNavigationRepairKind.Remove, link.ObjectNumber));
                    continue;
                }
                if (link.DestinationPageIndex is null)
                    findings.Add(new(PdfNavigationFindingCode.LinkUnresolvedDestination,
                        "Link", pageIndex, link.NamedDestination ?? string.Empty,
                        "The link destination cannot be resolved to a local page.",
                        PdfNavigationRepairKind.ChooseDestination, link.ObjectNumber));
            }
        }
        InspectUnsafeActions(document, PdfPageTree.Read(document).Catalog, findings);
        return Array.AsReadOnly(findings.ToArray());
    }

    private static void InspectUnsafeActions(PdfDocument document, PdfObject root,
        ICollection<PdfNavigationFinding> findings)
    {
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        var active = new HashSet<(int ObjectNumber, int Generation)>();
        int visitedValues = 0;
        Walk(root, null, 0, false);

        void Walk(PdfObject value, int? sourceObjectNumber, int depth,
            bool actionChain)
        {
            if (depth >= 256 || ++visitedValues > 1_000_000)
                throw new InvalidOperationException(
                    "The navigation action graph exceeds the inspection limit.");
            if (value is PdfIndirectReference reference)
            {
                var identity = (reference.ObjectNumber, reference.Generation);
                if (active.Contains(identity))
                {
                    if (actionChain)
                        findings.Add(new(PdfNavigationFindingCode.CircularActionChain,
                            "Action", null, "Next",
                            "The document contains a circular action chain that was not executed.",
                            PdfNavigationRepairKind.Remove, reference.ObjectNumber));
                    return;
                }
                if (!visited.Add(identity)) return;
                active.Add(identity);
                Walk(document.Resolve(reference), reference.ObjectNumber,
                    depth + 1, actionChain);
                active.Remove(identity);
                return;
            }
            if (value is PdfArray array)
            {
                foreach (PdfObject item in array)
                    Walk(item, sourceObjectNumber, depth + 1, actionChain);
                return;
            }
            if (value is not PdfDictionary dictionary) return;
            bool isAction = false;
            if (dictionary.TryGetValue(new PdfName("S"u8), out PdfObject? actionValue)
                && Resolve(actionValue) is PdfName action)
            {
                isAction = true;
                string actionName = action.ValueAsLatin1();
                if (actionName == "JavaScript")
                    findings.Add(new(PdfNavigationFindingCode.DocumentJavaScript,
                        "Action", null, "JavaScript",
                        "The document contains JavaScript that was not executed.",
                        PdfNavigationRepairKind.Remove, sourceObjectNumber));
                else if (actionName == "Launch")
                    findings.Add(new(PdfNavigationFindingCode.UnsafeLaunchAction,
                        "Action", null, "Launch",
                        "The document contains an unsafe launch action that was not executed.",
                        PdfNavigationRepairKind.Remove, sourceObjectNumber));
            }
            foreach ((PdfName key, PdfObject child) in dictionary)
                Walk(child, sourceObjectNumber, depth + 1,
                    isAction && key.ValueAsLatin1() == "Next");
        }

        PdfObject Resolve(PdfObject value)
        {
            var local = new HashSet<(int, int)>();
            while (value is PdfIndirectReference reference)
            {
                if (!local.Add((reference.ObjectNumber, reference.Generation))) return value;
                value = document.Resolve(reference);
            }
            return value;
        }
    }

    private static IEnumerable<PdfBookmarkInfo> Flatten(IEnumerable<PdfBookmarkInfo> roots)
    {
        foreach (PdfBookmarkInfo item in roots)
        {
            yield return item;
            foreach (PdfBookmarkInfo child in Flatten(item.Children)) yield return child;
        }
    }

    private static string OneLine(string value) =>
        value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
