using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.Documents;

/// <summary>Builds deterministic table-of-contents entries from document bookmarks.</summary>
public static class PdfTableOfContentsPlanner
{
    /// <summary>Plans entries in bookmark order through the requested hierarchy depth.</summary>
    public static PdfTableOfContentsPlan Plan(PdfDocument document, int maximumDepth = 6)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (maximumDepth is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        IReadOnlyList<string> pageLabels = PdfPageLabelReader.Read(document);
        var entries = new List<PdfTableOfContentsEntry>();
        Append(PdfBookmarkReader.Read(document), 0);
        return new PdfTableOfContentsPlan(maximumDepth,
            Array.AsReadOnly(entries.ToArray()));

        void Append(IReadOnlyList<PdfBookmarkInfo> bookmarks, int depth)
        {
            if (depth >= maximumDepth) return;
            foreach (PdfBookmarkInfo bookmark in bookmarks)
            {
                int? pageIndex = bookmark.DestinationPageIndex;
                entries.Add(new PdfTableOfContentsEntry
                {
                    Title = bookmark.Title,
                    Depth = depth,
                    PageIndex = pageIndex,
                    PageLabel = pageIndex.HasValue ? pageLabels[pageIndex.Value] : null,
                    NamedDestination = bookmark.NamedDestination,
                    Destination = bookmark.Destination,
                    Style = bookmark.Style,
                    Color = bookmark.Color,
                    SourceObjectNumber = bookmark.ObjectNumber
                });
                Append(bookmark.Children, depth + 1);
            }
        }
    }
}

/// <summary>A depth-limited table-of-contents plan.</summary>
public sealed record PdfTableOfContentsPlan(
    int MaximumDepth, IReadOnlyList<PdfTableOfContentsEntry> Entries)
{
    /// <summary>Gets the number of entries without a resolved local page.</summary>
    public int UnresolvedCount => Entries.Count(entry => !entry.PageIndex.HasValue);
}

/// <summary>One planned table-of-contents row and link target.</summary>
public sealed record PdfTableOfContentsEntry
{
    /// <summary>Gets the displayed entry title.</summary>
    public required string Title { get; init; }
    /// <summary>Gets the zero-based hierarchy depth.</summary>
    public int Depth { get; init; }
    /// <summary>Gets the resolved zero-based page index.</summary>
    public int? PageIndex { get; init; }
    /// <summary>Gets the source document's displayed page label.</summary>
    public string? PageLabel { get; init; }
    /// <summary>Gets the named destination when one was used.</summary>
    public string? NamedDestination { get; init; }
    /// <summary>Gets the resolved destination view.</summary>
    public PdfDestination? Destination { get; init; }
    /// <summary>Gets the source bookmark's title style.</summary>
    public PdfBookmarkStyle Style { get; init; }
    /// <summary>Gets the source bookmark's title color.</summary>
    public PdfRgbColor? Color { get; init; }
    /// <summary>Gets the source bookmark object number.</summary>
    public int SourceObjectNumber { get; init; }
}
