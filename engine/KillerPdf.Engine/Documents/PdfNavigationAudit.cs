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
    LinkUnresolvedDestination
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
        return Array.AsReadOnly(findings.ToArray());
    }

    private static IEnumerable<PdfBookmarkInfo> Flatten(IEnumerable<PdfBookmarkInfo> roots)
    {
        foreach (PdfBookmarkInfo item in roots)
        {
            yield return item;
            foreach (PdfBookmarkInfo child in Flatten(item.Children)) yield return child;
        }
    }
}
