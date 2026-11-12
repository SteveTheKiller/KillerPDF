namespace KillerPDF.Services;

/// <summary>
/// What the one-step Delete Pages dialog will do with the range typed into it: the pages that go (zero-based, sorted,
/// each once), the same pages grouped into runs of neighbours for the thumbnail strip (one tile per run, however long),
/// how many pages stay, and whether at least one does. Pages outside the document are dropped.
/// </summary>
internal enum DeletePagesProblem { None, InvalidRange, KeepOnePage }

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

    /// <summary>Pages in the runs after the first <paramref name="shownGroups"/>, for the strip's "+ N more" tile.</summary>
    internal int PagesAfter(int shownGroups) => Groups.Skip(shownGroups).Sum(group => group.Last - group.First + 1);

    /// <summary>Why the dialog's button is off, if it is: nothing readable to delete, or a range that leaves no page.</summary>
    internal DeletePagesProblem Problem(bool parsed) =>
        !parsed || !HasPages ? DeletePagesProblem.InvalidRange : !KeepsAPage ? DeletePagesProblem.KeepOnePage : DeletePagesProblem.None;

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
