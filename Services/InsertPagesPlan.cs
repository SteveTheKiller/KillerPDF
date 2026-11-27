namespace KillerPDF.Services;

/// <summary>
/// The window-free numbers of Insert Pages: where the pages go in the open document and which pages of the source
/// tab they are. Page numbers here are the one-based numbers the dialog shows; positions and pages are zero-based.
/// </summary>
internal static class InsertPagesPlan
{
    /// <summary>The zero-based position the pages go in at, before or after a page, or null for a page the document lacks.</summary>
    internal static int? Position(int page, bool before, int pageCount)
        => page < 1 || page > pageCount ? null : before ? page - 1 : page;

    /// <summary>
    /// A typed range, either way round, as first and last page. With the source's page count unknown (a tab that has
    /// not loaded, or one whose page tree needs its password) only the start is checked; the end is checked by
    /// <see cref="SourcePages"/> once the tab is open.
    /// </summary>
    internal static (int First, int Last)? Span(int from, int to, int? pageCount)
    {
        if (from > to) (from, to) = (to, from);
        return from < 1 || pageCount is int count && to > count ? null : (from, to);
    }

    /// <summary>The zero-based source pages, checked against the opened source's real page count, or null when they do not fit.</summary>
    internal static IReadOnlyList<int>? SourcePages(bool all, int first, int last, int pageCount)
    {
        if (pageCount < 1) return null;
        if (all) return [.. Enumerable.Range(0, pageCount)];
        return Span(first, last, pageCount) is var (from, to) ? [.. Enumerable.Range(from - 1, to - from + 1)] : null;
    }
}
