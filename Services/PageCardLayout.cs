namespace KillerPDF.Services;

/// <summary>
/// The page card's saved tile layout. The card had two customizable grids (#496), the selection and place tiles over
/// the page tools, each saved under its own key; it is one grid now (Steve 2026-11-26), saved under the tools' key.
/// </summary>
internal static class PageCardLayout
{
    /// <summary>
    /// Folds a layout saved for the old top grid into the one grid's: the top grid's tiles first, as they sat above the
    /// tools, then the tools grid's, and both hidden sets. A tools grid on its defaults (null or empty) names none of its
    /// tiles, so they take their default places after the top grid's.
    /// </summary>
    internal static string Merge(string top, string? page)
    {
        static (string[] Shown, string[] Hidden) Parse(string? saved)
        {
            if (string.IsNullOrEmpty(saved)) return ([], []);
            if (saved.StartsWith("v1:", StringComparison.Ordinal))
                return ([], saved[3..].Split('|', StringSplitOptions.RemoveEmptyEntries));
            if (!saved.StartsWith("v2:", StringComparison.Ordinal)) return ([], []);
            string[] parts = saved[3..].Split('/');
            return (parts[0].Split('|', StringSplitOptions.RemoveEmptyEntries),
                    parts.Length > 1 ? parts[1].Split('|', StringSplitOptions.RemoveEmptyEntries) : []);
        }
        var (topShown, topHidden) = Parse(top);
        var (pageShown, pageHidden) = Parse(page);
        return "v2:" + string.Join("|", topShown.Concat(pageShown).Distinct())
             + "/" + string.Join("|", topHidden.Concat(pageHidden).Distinct());
    }
}
