using System.Globalization;
using System.Text;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Documents;

/// <summary>The rule that decides where a document is divided into separate files.</summary>
public enum PdfSplitMode
{
    /// <summary>Every part holds a fixed number of pages.</summary>
    PageCount,
    /// <summary>The document is divided into a fixed number of parts of near equal length.</summary>
    PartCount,
    /// <summary>Each supplied page range becomes one part.</summary>
    PageRanges,
    /// <summary>Each bookmark at the selected level starts a new part.</summary>
    BookmarkLevel,
    /// <summary>Parts grow until adding another page would exceed a byte budget.</summary>
    MaximumBytes
}

/// <summary>One inclusive zero-based page range.</summary>
public sealed record PdfPageRange(int FirstPageIndex, int LastPageIndex);

/// <summary>The settings that control how a document is divided.</summary>
public sealed record PdfSplitOptions
{
    /// <summary>Gets the rule that decides where the document is divided.</summary>
    public PdfSplitMode Mode { get; init; } = PdfSplitMode.PageCount;
    /// <summary>Gets the page count used by the page count rule.</summary>
    public int PagesPerPart { get; init; } = 1;
    /// <summary>Gets the number of parts used by the part count rule.</summary>
    public int PartCount { get; init; } = 2;
    /// <summary>Gets the one-based outline depth used by the bookmark rule.</summary>
    public int BookmarkLevel { get; init; } = 1;
    /// <summary>Gets the byte budget used by the maximum size rule.</summary>
    public long MaximumBytes { get; init; } = 5 * 1024 * 1024;
    /// <summary>Gets the inclusive page ranges used by the page range rule.</summary>
    public IReadOnlyList<PdfPageRange> Ranges { get; init; } = [];
    /// <summary>
    /// Gets the part name template. The placeholders are {name} for the base name,
    /// {index} for the padded part number, {first} and {last} for one-based page
    /// numbers, and {title} for the bookmark title that started the part.
    /// </summary>
    public string NameTemplate { get; init; } = "{name}-{index}";
    /// <summary>
    /// Gets whether each part drops the objects the removed pages left behind.
    /// Parts are much smaller with this on, which is why it is the default.
    /// </summary>
    public bool CompactParts { get; init; } = true;
}

/// <summary>One planned output part.</summary>
public sealed record PdfSplitPart
{
    /// <summary>Gets the part name without a file extension.</summary>
    public required string Name { get; init; }
    /// <summary>Gets the zero-based source pages the part keeps, in order.</summary>
    public required IReadOnlyList<int> SourcePageIndices { get; init; }
    /// <summary>Gets the bookmark title that started the part, or null when none did.</summary>
    public string? Title { get; init; }
}

/// <summary>One built output part.</summary>
public sealed record PdfSplitOutput
{
    /// <summary>Gets the part name without a file extension.</summary>
    public required string Name { get; init; }
    /// <summary>Gets the zero-based source pages the part keeps, in order.</summary>
    public required IReadOnlyList<int> SourcePageIndices { get; init; }
    /// <summary>Gets the built part.</summary>
    public required ReadOnlyMemory<byte> Document { get; init; }
}

/// <summary>Divides one document into separate files by page count, ranges, bookmarks, or size.</summary>
public static class PdfDocumentSplitter
{
    private const int MaximumParts = 4096;

    /// <summary>Plans the output parts without building them.</summary>
    public static PdfSplitPlan Plan(PdfDocument document, PdfSplitOptions options,
        string baseName = "document")
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        if (options.Mode == PdfSplitMode.MaximumBytes)
            throw new ArgumentException(
                "The maximum size rule is resolved while parts are built. Call Split instead.",
                nameof(options));
        int pageCount = PdfPageInformation.Read(document).Count;
        if (pageCount == 0)
            throw new InvalidOperationException("The document has no pages to divide.");
        List<(List<int> Pages, string? Title)> groups = options.Mode switch
        {
            PdfSplitMode.PageCount => ByPageCount(pageCount, options.PagesPerPart),
            PdfSplitMode.PartCount => ByPartCount(pageCount, options.PartCount),
            PdfSplitMode.PageRanges => ByRanges(pageCount, options.Ranges),
            PdfSplitMode.BookmarkLevel => ByBookmarks(document, pageCount, options.BookmarkLevel),
            _ => throw new ArgumentOutOfRangeException(nameof(options), "The split rule is not defined.")
        };
        return Build(groups, options.NameTemplate, baseName);
    }

    /// <summary>Plans and builds every output part.</summary>
    public static IReadOnlyList<PdfSplitOutput> Split(PdfDocument document,
        PdfSplitOptions options, string baseName = "document",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        if (options.Mode == PdfSplitMode.MaximumBytes)
            return BySize(document, options, baseName, cancellationToken);
        PdfSplitPlan plan = Plan(document, options, baseName);
        var outputs = new List<PdfSplitOutput>(plan.Parts.Count);
        foreach (PdfSplitPart part in plan.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outputs.Add(new PdfSplitOutput
            {
                Name = part.Name,
                SourcePageIndices = part.SourcePageIndices,
                Document = Extract(document, part.SourcePageIndices, options.CompactParts)
            });
        }
        return Array.AsReadOnly(outputs.ToArray());
    }

    /// <summary>Builds one document holding only the selected source pages, in order.</summary>
    public static byte[] Extract(PdfDocument document, IReadOnlyList<int> sourcePageIndices,
        bool compact = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sourcePageIndices);
        int pageCount = PdfPageInformation.Read(document).Count;
        var kept = new HashSet<int>();
        foreach (int index in sourcePageIndices)
        {
            if (index < 0 || index >= pageCount)
                throw new ArgumentOutOfRangeException(nameof(sourcePageIndices),
                    "A selected page is outside the document.");
            if (!kept.Add(index))
                throw new ArgumentException(
                    "A page cannot be selected twice in one part.", nameof(sourcePageIndices));
        }
        if (kept.Count == 0)
            throw new ArgumentException(
                "At least one page is required.", nameof(sourcePageIndices));
        var editor = new PdfIncrementalPageEditor(document);
        for (int index = pageCount - 1; index >= 0; index--)
            if (!kept.Contains(index)) editor.RemovePage(index);
        byte[] built = editor.Build();
        return compact ? Compact(built) : built;
    }

    // Removing pages leaves their content, fonts, and images in the file. Pruning what the
    // trailer can no longer reach, and the resources the kept pages no longer name, is what
    // makes a part smaller than the document it came from. A document that cannot be rewritten
    // is returned unchanged rather than failing the split.
    private static byte[] Compact(byte[] built)
    {
        PdfDocument part;
        try
        {
            part = PdfDocument.Open(built);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or NotSupportedException or ArgumentException or FormatException)
        {
            return built;
        }
        // Object streams and cross-reference streams need PDF 1.5. An older document keeps
        // its original structure so the rewrite cannot fail on its declared version.
        bool modern = part.Header.Version.CompareTo(new PdfVersion(1, 5)) >= 0;
        foreach (bool pruneResources in (bool[])[true, false])
        {
            try
            {
                return PdfOptimizer.CreatePlan(part, new PdfOptimizationOptions
                {
                    PruneUnreachableObjects = true,
                    PruneUnusedPageResources = pruneResources,
                    PackObjects = modern,
                    CompressStructure = modern
                }).Apply().Data.ToArray();
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or NotSupportedException or ArgumentException or FormatException)
            {
                // Fall through to the safer option set, then to the unchanged part.
            }
        }
        return built;
    }

    private static IReadOnlyList<PdfSplitOutput> BySize(PdfDocument document,
        PdfSplitOptions options, string baseName, CancellationToken cancellationToken)
    {
        if (options.MaximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options),
                "The maximum part size must be greater than zero.");
        int pageCount = PdfPageInformation.Read(document).Count;
        if (pageCount == 0)
            throw new InvalidOperationException("The document has no pages to divide.");
        var groups = new List<(List<int> Pages, string? Title)>();
        var current = new List<int>();
        for (int index = 0; index < pageCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current.Add(index);
            // A single page that already exceeds the budget still becomes its own part.
            if (current.Count == 1
                || Extract(document, current, options.CompactParts).LongLength
                    <= options.MaximumBytes) continue;
            current.RemoveAt(current.Count - 1);
            groups.Add(([.. current], null));
            Guard(groups.Count);
            current = [index];
        }
        if (current.Count > 0) groups.Add(([.. current], null));
        PdfSplitPlan plan = Build(groups, options.NameTemplate, baseName);
        var outputs = new List<PdfSplitOutput>(plan.Parts.Count);
        foreach (PdfSplitPart part in plan.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outputs.Add(new PdfSplitOutput
            {
                Name = part.Name,
                SourcePageIndices = part.SourcePageIndices,
                Document = Extract(document, part.SourcePageIndices, options.CompactParts)
            });
        }
        return Array.AsReadOnly(outputs.ToArray());
    }

    private static List<(List<int> Pages, string? Title)> ByPageCount(int pageCount, int pagesPerPart)
    {
        if (pagesPerPart <= 0)
            throw new ArgumentOutOfRangeException(nameof(pagesPerPart),
                "A part must hold at least one page.");
        var groups = new List<(List<int>, string?)>();
        for (int start = 0; start < pageCount; start += pagesPerPart)
        {
            var pages = new List<int>();
            for (int index = start; index < Math.Min(start + pagesPerPart, pageCount); index++)
                pages.Add(index);
            groups.Add((pages, null));
        }
        Guard(groups.Count);
        return groups;
    }

    private static List<(List<int> Pages, string? Title)> ByPartCount(int pageCount, int partCount)
    {
        if (partCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(partCount),
                "A document must be divided into at least one part.");
        if (partCount > pageCount)
            throw new ArgumentOutOfRangeException(nameof(partCount),
                "A document cannot be divided into more parts than it has pages.");
        Guard(partCount);
        var groups = new List<(List<int>, string?)>(partCount);
        int baseSize = pageCount / partCount;
        int remainder = pageCount % partCount;
        int next = 0;
        for (int part = 0; part < partCount; part++)
        {
            int size = baseSize + (part < remainder ? 1 : 0);
            var pages = new List<int>(size);
            for (int index = 0; index < size; index++) pages.Add(next++);
            groups.Add((pages, null));
        }
        return groups;
    }

    private static List<(List<int> Pages, string? Title)> ByRanges(
        int pageCount, IReadOnlyList<PdfPageRange> ranges)
    {
        if (ranges.Count == 0)
            throw new ArgumentException("At least one page range is required.", nameof(ranges));
        Guard(ranges.Count);
        var groups = new List<(List<int>, string?)>(ranges.Count);
        foreach (PdfPageRange range in ranges)
        {
            ArgumentNullException.ThrowIfNull(range);
            if (range.FirstPageIndex < 0 || range.LastPageIndex >= pageCount
                || range.FirstPageIndex > range.LastPageIndex)
                throw new ArgumentOutOfRangeException(nameof(ranges),
                    "A page range is outside the document or ends before it starts.");
            var pages = new List<int>(range.LastPageIndex - range.FirstPageIndex + 1);
            for (int index = range.FirstPageIndex; index <= range.LastPageIndex; index++)
                pages.Add(index);
            groups.Add((pages, null));
        }
        return groups;
    }

    private static List<(List<int> Pages, string? Title)> ByBookmarks(
        PdfDocument document, int pageCount, int level)
    {
        if (level <= 0)
            throw new ArgumentOutOfRangeException(nameof(level),
                "A bookmark level starts at one.");
        var starts = new List<(int Page, string Title)>();
        Walk(PdfBookmarkReader.Read(document), 1);
        if (starts.Count == 0)
            throw new InvalidOperationException(
                $"The document has no bookmarks at level {level} with a page destination.");
        starts.Sort((left, right) => left.Page.CompareTo(right.Page));
        var unique = new List<(int Page, string Title)>();
        foreach ((int page, string title) in starts)
            if (unique.Count == 0 || unique[^1].Page != page) unique.Add((page, title));
        Guard(unique.Count + 1);

        var groups = new List<(List<int>, string?)>();
        if (unique[0].Page > 0) groups.Add((Pages(0, unique[0].Page - 1), null));
        for (int index = 0; index < unique.Count; index++)
        {
            int last = index + 1 < unique.Count ? unique[index + 1].Page - 1 : pageCount - 1;
            groups.Add((Pages(unique[index].Page, last), unique[index].Title));
        }
        return groups;

        List<int> Pages(int first, int last)
        {
            var pages = new List<int>(Math.Max(0, last - first + 1));
            for (int index = first; index <= last; index++) pages.Add(index);
            return pages;
        }

        void Walk(IReadOnlyList<PdfBookmarkInfo> bookmarks, int depth)
        {
            foreach (PdfBookmarkInfo bookmark in bookmarks)
            {
                if (depth == level && bookmark.DestinationPageIndex is int page
                    && page >= 0 && page < pageCount) starts.Add((page, bookmark.Title));
                if (depth < level) Walk(bookmark.Children, depth + 1);
            }
        }
    }

    private static void Guard(int partCount)
    {
        if (partCount > MaximumParts)
            throw new InvalidOperationException(
                $"A document cannot be divided into more than {MaximumParts} parts.");
    }

    private static PdfSplitPlan Build(List<(List<int> Pages, string? Title)> groups,
        string template, string baseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        int width = groups.Count.ToString(CultureInfo.InvariantCulture).Length;
        var parts = new List<PdfSplitPart>(groups.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < groups.Count; index++)
        {
            (List<int> pages, string? title) = groups[index];
            if (pages.Count == 0) continue;
            string name = template
                .Replace("{name}", baseName, StringComparison.Ordinal)
                .Replace("{index}", (index + 1).ToString(
                    CultureInfo.InvariantCulture).PadLeft(width, '0'), StringComparison.Ordinal)
                .Replace("{first}", (pages[0] + 1).ToString(
                    CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{last}", (pages[^1] + 1).ToString(
                    CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{title}", Sanitize(title ?? string.Empty), StringComparison.Ordinal);
            name = Sanitize(name);
            if (name.Length == 0) name = baseName + "-" + (index + 1).ToString(
                CultureInfo.InvariantCulture).PadLeft(width, '0');
            string unique = name;
            for (int suffix = 2; !used.Add(unique); suffix++)
                unique = name + "-" + suffix.ToString(CultureInfo.InvariantCulture);
            parts.Add(new PdfSplitPart
            {
                Name = unique,
                SourcePageIndices = Array.AsReadOnly(pages.ToArray()),
                Title = title
            });
        }
        if (parts.Count == 0)
            throw new InvalidOperationException("The split rule produced no parts.");
        return new PdfSplitPlan { Parts = Array.AsReadOnly(parts.ToArray()) };
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                || char.IsControl(character)) builder.Append(' ');
            else builder.Append(character);
        }
        string collapsed = string.Join(' ', builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Trim(' ', '.');
    }
}

/// <summary>The planned division of one document into separate files.</summary>
public sealed record PdfSplitPlan
{
    /// <summary>Gets the planned parts in source page order.</summary>
    public required IReadOnlyList<PdfSplitPart> Parts { get; init; }
}
