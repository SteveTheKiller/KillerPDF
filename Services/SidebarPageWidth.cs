namespace KillerPDF.Services;

/// <summary>
/// How wide the Pages panel may be dragged. It stops where its thumbnails reach their 200 px cap, so widening it
/// further would only add empty space. With two-page thumbnails each of the pair gets half the width, so the limit
/// is two thumbnails plus the panel's margins and scroll bar, about twice the single-column limit.
/// </summary>
internal static class SidebarPageWidth
{
    /// <summary>The widest a page thumbnail is drawn.</summary>
    internal const double ThumbnailCap = 200;

    /// <summary>The panel's own margins and scroll bar around its thumbnails.</summary>
    private const double Chrome = 34;

    internal static double Max(bool paired) => (paired ? 2 : 1) * ThumbnailCap + Chrome;
}
