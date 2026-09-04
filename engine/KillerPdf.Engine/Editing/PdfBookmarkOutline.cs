using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;

namespace KillerPdf.Engine.Editing;

/// <summary>An immutable flat bookmark outline for bulk reordering and nesting.</summary>
public sealed class PdfBookmarkOutline
{
    /// <summary>Creates a validated flat outline.</summary>
    public PdfBookmarkOutline(IEnumerable<PdfBookmarkOutlineItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        PdfBookmarkOutlineItem[] selected = items.ToArray();
        Validate(selected);
        Items = Array.AsReadOnly(selected);
    }

    /// <summary>Gets the outline items in depth-first document order.</summary>
    public IReadOnlyList<PdfBookmarkOutlineItem> Items { get; }

    /// <summary>Reads the current bookmark tree into a flat editable outline.</summary>
    public static PdfBookmarkOutline Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var items = new List<PdfBookmarkOutlineItem>();
        Append(PdfBookmarkReader.Read(document), 0);
        return new PdfBookmarkOutline(items);

        void Append(IEnumerable<PdfBookmarkInfo> bookmarks, int level)
        {
            foreach (PdfBookmarkInfo bookmark in bookmarks)
            {
                items.Add(new PdfBookmarkOutlineItem
                {
                    SourceObjectNumber = bookmark.ObjectNumber,
                    Title = bookmark.Title,
                    Level = level,
                    PageIndex = bookmark.DestinationPageIndex,
                    NamedDestination = bookmark.NamedDestination,
                    Destination = bookmark.Destination,
                    IsOpen = bookmark.IsOpen,
                    Style = bookmark.Style,
                    Color = bookmark.Color
                });
                Append(bookmark.Children, level + 1);
            }
        }
    }

    /// <summary>Moves one bookmark subtree to a new flat position and hierarchy level.</summary>
    public PdfBookmarkOutline MoveSubtree(
        int sourceObjectNumber, int targetIndex, int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        PdfBookmarkOutlineItem[] source = Items.ToArray();
        int start = Index(source, sourceObjectNumber);
        int end = start + 1;
        while (end < source.Length && source[end].Level > source[start].Level) end++;
        PdfBookmarkOutlineItem[] subtree = source[start..end];
        var remaining = source.Take(start).Concat(source.Skip(end)).ToList();
        if (targetIndex < 0 || targetIndex > remaining.Count)
            throw new ArgumentOutOfRangeException(nameof(targetIndex));
        int delta = level - subtree[0].Level;
        remaining.InsertRange(targetIndex, subtree.Select(item =>
            item with { Level = item.Level + delta }));
        return new PdfBookmarkOutline(remaining);
    }

    /// <summary>Duplicates one complete bookmark subtree immediately after itself.</summary>
    public PdfBookmarkOutline DuplicateSubtree(int sourceObjectNumber)
    {
        PdfBookmarkOutlineItem[] source = Items.ToArray();
        int start = Index(source, sourceObjectNumber);
        int end = start + 1;
        while (end < source.Length && source[end].Level > source[start].Level) end++;
        var changed = source.ToList();
        changed.InsertRange(end, source[start..end].Select(item => item with
        {
            SourceObjectNumber = null
        }));
        return new PdfBookmarkOutline(changed);
    }

    /// <summary>Renames one source bookmark in the editable outline.</summary>
    public PdfBookmarkOutline Rename(int sourceObjectNumber, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("A bookmark title is required.", nameof(title));
        PdfBookmarkOutlineItem[] changed = Items.ToArray();
        int index = Index(changed, sourceObjectNumber);
        changed[index] = changed[index] with { Title = title };
        return new PdfBookmarkOutline(changed);
    }

    /// <summary>Replaces the document outline with this supported bookmark model.</summary>
    public byte[] Apply(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var editor = new PdfIncrementalPageEditor(document).ClearBookmarks();
        foreach (PdfBookmarkOutlineItem item in Items)
        {
            var options = new PdfBookmarkOptions
            {
                IsOpen = item.IsOpen,
                Style = item.Style,
                Color = item.Color,
                Destination = item.Destination ?? PdfDestination.FitPage()
            };
            if (item.PageIndex is int pageIndex)
                editor.AddBookmark(item.Title, pageIndex, item.Level, options);
            else if (!string.IsNullOrWhiteSpace(item.NamedDestination))
                editor.AddNamedDestinationBookmark(
                    item.Title, item.NamedDestination, item.Level, options);
            else
                throw new InvalidOperationException(
                    $"Bookmark '{item.Title}' has no supported destination.");
        }
        return editor.Build();
    }

    private static int Index(
        IReadOnlyList<PdfBookmarkOutlineItem> items, int sourceObjectNumber)
    {
        if (sourceObjectNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceObjectNumber));
        int[] matches = [.. items.Select((item, index) => (item, index))
            .Where(pair => pair.item.SourceObjectNumber == sourceObjectNumber)
            .Select(pair => pair.index)];
        return matches.Length == 1 ? matches[0]
            : throw new KeyNotFoundException(
                $"Bookmark {sourceObjectNumber} was not found exactly once.");
    }

    private static void Validate(IReadOnlyList<PdfBookmarkOutlineItem> items)
    {
        if (items.Count > 0 && items[0].Level != 0)
            throw new ArgumentException("The first bookmark must be at level zero.", nameof(items));
        var sourceIds = new HashSet<int>();
        for (int index = 0; index < items.Count; index++)
        {
            PdfBookmarkOutlineItem item = items[index]
                ?? throw new ArgumentException("A bookmark outline item cannot be null.", nameof(items));
            if (string.IsNullOrWhiteSpace(item.Title))
                throw new ArgumentException("A bookmark title cannot be empty.", nameof(items));
            if (item.Level < 0 || index > 0 && item.Level > items[index - 1].Level + 1)
                throw new ArgumentException("A bookmark level cannot skip its parent level.", nameof(items));
            if (item.SourceObjectNumber is int id && (id <= 0 || !sourceIds.Add(id)))
                throw new ArgumentException("Source bookmark identifiers must be positive and unique.", nameof(items));
        }
    }
}

/// <summary>One bookmark in a flat depth-first editable outline.</summary>
public sealed record PdfBookmarkOutlineItem
{
    /// <summary>Gets the original bookmark object number, or null for a new copy.</summary>
    public int? SourceObjectNumber { get; init; }
    /// <summary>Gets the bookmark title.</summary>
    public required string Title { get; init; }
    /// <summary>Gets the zero-based hierarchy level.</summary>
    public int Level { get; init; }
    /// <summary>Gets the resolved local target page.</summary>
    public int? PageIndex { get; init; }
    /// <summary>Gets the named destination target.</summary>
    public string? NamedDestination { get; init; }
    /// <summary>Gets the destination view.</summary>
    public PdfDestination? Destination { get; init; }
    /// <summary>Gets whether children are initially expanded.</summary>
    public bool IsOpen { get; init; } = true;
    /// <summary>Gets the title emphasis.</summary>
    public PdfBookmarkStyle Style { get; init; }
    /// <summary>Gets the optional title color.</summary>
    public PdfRgbColor? Color { get; init; }
}
