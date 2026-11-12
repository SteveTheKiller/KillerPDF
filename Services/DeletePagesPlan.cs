namespace KillerPDF.Services;

/// <summary>
/// What the one-step Delete Pages dialog will do with the range typed into it: the pages that go (zero-based, sorted,
/// each once), the same pages grouped into runs of neighbours for the thumbnail strip (one tile per run, however long),
/// how many pages stay, and whether at least one does. Pages outside the document are dropped.
/// </summary>
internal sealed class DeletePagesPlan
{
    private DeletePagesPlan(IReadOnlyList<int> pages, IReadOnlyList<(int First, int Last)> groups, int pageCount)
    {
        Pages = pages;
        Groups = groups;
        PageCount = pageCount;
    }

    internal IReadOnlyList<int> Pages { get; }
    internal IReadOnlyList<(int First, int Last)> Groups { get; }
    internal int PageCount { get; }
    internal int Remaining => PageCount - Pages.Count;
    internal bool HasPages => Pages.Count > 0;
    internal bool KeepsAPage => Remaining > 0;

    internal static DeletePagesPlan From(IEnumerable<int> pages, int pageCount)
    {
        int[] sorted = [.. pages.Where(page => page >= 0 && page < pageCount).Distinct().Order()];
        var groups = new List<(int First, int Last)>();
        foreach (int page in sorted)
        {
            if (groups.Count > 0 && groups[^1].Last == page - 1) groups[^1] = (groups[^1].First, page);
            else groups.Add((page, page));
        }
        return new DeletePagesPlan(sorted, groups, pageCount);
    }
}
