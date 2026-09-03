namespace KillerPdf.Engine.Documents;

/// <summary>A problem found while auditing document navigation.</summary>
public sealed record PdfNavigationFinding(string Kind, int? SourcePageIndex, string Source,
    string Message);

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
                && bookmark.Destination is null) findings.Add(new("Bookmark", null, bookmark.Title,
                    "The bookmark has no supported local destination."));
            else if (bookmark.DestinationPageIndex is null) findings.Add(new("Bookmark", null,
                bookmark.Title, "The bookmark destination cannot be resolved to a local page."));
        }
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            foreach (PdfLinkInfo link in PdfLinkReader.ReadPage(document, pageIndex))
            {
                if (link.Uri is not null)
                {
                    if (!Uri.TryCreate(link.Uri, UriKind.Absolute, out Uri? uri)
                        || uri.Scheme is not ("http" or "https" or "mailto"))
                        findings.Add(new("Link", pageIndex, link.Uri,
                            "The link uses an invalid or unsupported URI scheme."));
                    continue;
                }
                if (link.DestinationPageIndex is null)
                    findings.Add(new("Link", pageIndex, link.NamedDestination ?? string.Empty,
                        "The link destination cannot be resolved to a local page."));
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
