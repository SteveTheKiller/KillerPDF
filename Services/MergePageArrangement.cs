namespace KillerPDF.Services;

/// <summary>One page in the Merge dialog's page view: a page of a listed file, or <see cref="WholeFile"/> for a file
/// whose pages could not be read there (a protected or damaged file), which stands for all of its pages.</summary>
internal readonly record struct MergePage(string Path, int Page)
{
    internal const int WholeFile = -1;
}

/// <summary>Which pages of the combined file go and the order of the rest, in the terms
/// <see cref="PdfEngineIntegration.RemovePages"/> and <see cref="PdfEngineIntegration.ReorderPages"/> take.</summary>
internal sealed record MergePagePlan(IReadOnlyList<int> Removed, IReadOnlyList<int>? Order);

/// <summary>
/// The Merge dialog's page order (#495): every page of the listed files, which can be moved and removed one by one.
/// Adding a file appends its pages; removing a file drops them; moving a file in the file list regroups the pages by
/// the new file order and keeps each file's own pages in the order they were put in.
/// </summary>
internal sealed class MergePageArrangement
{
    private readonly List<MergePage> _pages = [];
    // Each file's page count as it was added, so a removed last page still reads as a change.
    private readonly Dictionary<string, int?> _counts = new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<MergePage> Pages => _pages;

    /// <summary>The pages of <paramref name="files"/> in file order. A null count is a file whose pages could not
    /// be read, which gets one whole-file entry.</summary>
    internal static MergePageArrangement For(IEnumerable<(string Path, int? PageCount)> files)
    {
        var arrangement = new MergePageArrangement();
        foreach (var (path, count) in files) arrangement.AddFile(path, count);
        return arrangement;
    }

    internal void AddFile(string path, int? pageCount)
    {
        if (_counts.ContainsKey(path)) return;
        _counts[path] = pageCount;
        if (pageCount is not int count) { _pages.Add(new MergePage(path, MergePage.WholeFile)); return; }
        for (int page = 0; page < count; page++) _pages.Add(new MergePage(path, page));
    }

    /// <summary>Gives a file listed without a count its <paramref name="pageCount"/> pages, in the place its
    /// whole-file entry holds. A file that is not listed, or already has a count, stays as it is.</summary>
    internal void ExpandFile(string path, int pageCount)
    {
        if (!_counts.TryGetValue(path, out int? known) || known is not null) return;
        _counts[path] = pageCount;
        int at = _pages.FindIndex(page => SamePath(page.Path, path) && page.Page == MergePage.WholeFile);
        if (at < 0) return;
        string listed = _pages[at].Path;
        _pages.RemoveAt(at);
        _pages.InsertRange(at, Enumerable.Range(0, pageCount).Select(page => new MergePage(listed, page)));
    }

    /// <summary>True when the file's page count is known.</summary>
    internal bool HasCount(string path) => _counts.TryGetValue(path, out int? count) && count is not null;

    internal void RemoveFile(string path)
    {
        _counts.Remove(path);
        _pages.RemoveAll(page => SamePath(page.Path, path));
    }

    /// <summary>True when <paramref name="path"/> is listed, even with every page of it removed.</summary>
    internal bool HasFile(string path) => _counts.ContainsKey(path);

    /// <summary>Regroups the pages by file in <paramref name="files"/>' order, each file's pages keeping theirs.</summary>
    internal void FollowFileOrder(IReadOnlyList<string> files)
    {
        int Rank(MergePage page)
        {
            for (int index = 0; index < files.Count; index++) if (SamePath(files[index], page.Path)) return index;
            return files.Count;
        }
        MergePage[] regrouped = [.. _pages.Select((page, index) => (page, index))
            .OrderBy(entry => Rank(entry.page)).ThenBy(entry => entry.index).Select(entry => entry.page)];
        _pages.Clear();
        _pages.AddRange(regrouped);
    }

    /// <summary>Where <paramref name="pages"/> sit now, in page order. A whole-file entry whose file has opened up since
    /// stands for all of that file's pages, and pages no longer listed are left out. A drag carries pages, not places,
    /// because a page count arriving during it shifts the places.</summary>
    internal int[] IndicesOf(IEnumerable<MergePage> pages)
    {
        var found = new SortedSet<int>();
        foreach (MergePage wanted in pages)
        {
            for (int index = 0; index < _pages.Count; index++)
            {
                MergePage page = _pages[index];
                if (!SamePath(page.Path, wanted.Path)) continue;
                if (page.Page == wanted.Page || (wanted.Page == MergePage.WholeFile && HasCount(wanted.Path)))
                    found.Add(index);
            }
        }
        return [.. found];
    }

    /// <summary>Removes the pages at <paramref name="indices"/>.</summary>
    internal void RemoveAt(IEnumerable<int> indices)
    {
        foreach (int index in indices.Distinct().Where(i => i >= 0 && i < _pages.Count).OrderByDescending(i => i))
            _pages.RemoveAt(index);
    }

    /// <summary>Moves the pages at <paramref name="indices"/>, kept in their order, so they sit before the page now at
    /// <paramref name="before"/> (the end for <see cref="Pages"/>.Count or more). Returns where the first one landed.</summary>
    internal int MoveTo(IReadOnlyCollection<int> indices, int before)
    {
        int[] moving = [.. indices.Distinct().Where(i => i >= 0 && i < _pages.Count).Order()];
        if (moving.Length == 0) return -1;
        before = Math.Clamp(before, 0, _pages.Count);
        int landing = before - moving.Count(index => index < before);
        MergePage[] block = [.. moving.Select(index => _pages[index])];
        foreach (int index in moving.Reverse()) _pages.RemoveAt(index);
        _pages.InsertRange(landing, block);
        return landing;
    }

    /// <summary>Moves the pages at <paramref name="indices"/> one place back (-1) or forward (+1) as a block,
    /// stopping at either end. Returns where the first one landed.</summary>
    internal int Step(IReadOnlyCollection<int> indices, int delta)
    {
        int[] moving = [.. indices.Distinct().Where(i => i >= 0 && i < _pages.Count).Order()];
        if (moving.Length == 0) return -1;
        if (delta < 0) return moving[0] == 0 ? 0 : MoveTo(moving, moving[0] - 1);
        int last = moving[^1];
        // Forward past the page after the last one; already at the end stays put.
        return last >= _pages.Count - 1 ? MoveTo(moving, _pages.Count) : MoveTo(moving, last + 2);
    }

    /// <summary>True when the pages are just every page of <paramref name="files"/> in file order, so the merge
    /// needs no page plan.</summary>
    internal bool IsPlainOrder(IReadOnlyList<string> files) =>
        _pages.SequenceEqual(For(files.Select(path => (path, CountOf(path)))).Pages);

    private int? CountOf(string path) => _counts.TryGetValue(path, out int? count) ? count : 0;

    /// <summary>
    /// Turns <paramref name="pages"/> into a plan for the combined file. <paramref name="merged"/> lists the files
    /// the merge actually took, in merge order, with the pages each one put in, so a file it left out moves nothing.
    /// Pages of files it did not take are skipped. Null when nothing would be left.
    /// </summary>
    internal static MergePagePlan? Plan(IReadOnlyList<MergePage> pages, IReadOnlyList<(string Path, int PageCount)> merged)
    {
        // The dialog lists full paths, and a drop hands the merge the paths it was given, so both are matched in full.
        var offsets = new Dictionary<string, (int Start, int Count)>(StringComparer.OrdinalIgnoreCase);
        int total = 0;
        foreach (var (path, count) in merged)
        {
            offsets[Full(path)] = (total, count);
            total += count;
        }
        var wanted = new List<int>();
        foreach (MergePage page in pages)
        {
            if (!offsets.TryGetValue(Full(page.Path), out var span)) continue;
            if (page.Page == MergePage.WholeFile)
                wanted.AddRange(Enumerable.Range(span.Start, span.Count));
            else if (page.Page >= 0 && page.Page < span.Count)
                wanted.Add(span.Start + page.Page);
        }
        wanted = [.. wanted.Distinct()];
        if (wanted.Count == 0) return null;
        var kept = new HashSet<int>(wanted);
        int[] removed = [.. Enumerable.Range(0, total).Where(index => !kept.Contains(index))];
        // After the removal the kept pages close up in their old order; the order says where each one goes.
        int[] survivors = [.. Enumerable.Range(0, total).Where(kept.Contains)];
        var position = new Dictionary<int, int>();
        for (int index = 0; index < survivors.Length; index++) position[survivors[index]] = index;
        int[] order = [.. wanted.Select(index => position[index])];
        bool identity = order.Select((value, index) => value == index).All(same => same);
        return new MergePagePlan(removed, identity ? null : order);
    }

    /// <summary>Applies <paramref name="plan"/> to the combined file at <paramref name="path"/>.</summary>
    internal static void Apply(string path, MergePagePlan plan)
    {
        if (plan.Removed.Count > 0) PdfEngineIntegration.RemovePages(path, plan.Removed);
        if (plan.Order is { } order) PdfEngineIntegration.ReorderPages(path, order);
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Full(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { return path; }
    }
}
