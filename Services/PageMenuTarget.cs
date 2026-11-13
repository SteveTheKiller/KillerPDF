namespace KillerPDF.Services;

// Which pages a page-menu action takes. Free of WPF and of MainWindow so the rule can be checked without a window.
internal static class PageMenuTarget
{
    /// <summary>The right-clicked page, or the whole selection when that page is part of it.</summary>
    internal static int[] Pages(int clicked, IReadOnlyList<int> selected) => selected.Contains(clicked) ? [.. selected] : [clicked];
}
