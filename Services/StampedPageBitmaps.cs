using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerPDF.Services;

/// <summary>
/// #478: which page bitmaps were drawn from the viewer's stamped copy of the document, and the overlay stamps that
/// follow them. A bitmap keeps its mark wherever it goes, the render cache included, so the overlay hides its stamps
/// exactly over the bitmaps that already carry them.
/// </summary>
internal static class StampedPageBitmaps
{
    private static readonly ConditionalWeakTable<BitmapSource, object> Stamped = new();

    /// <summary>True when <paramref name="bitmap"/> was drawn with the stamps in its page content.</summary>
    internal static bool IsStamped(ImageSource? bitmap) => bitmap is BitmapSource source && Stamped.TryGetValue(source, out _);

    /// <summary>Marks <paramref name="bitmap"/>, drawn for <paramref name="page"/> from <paramref name="request"/>, when
    /// that page rendered from the stamped copy.</summary>
    internal static void Note(PdfBackgroundRenderCache.Request? request, int page, BitmapSource bitmap)
    {
        if (request?.StampsInPage(page) == true) Stamped.AddOrUpdate(bitmap, page);
    }

    /// <summary>
    /// Shows the overlay's <paramref name="stamps"/> over <paramref name="page"/> unless its bitmap already carries
    /// them. Each place a page bitmap is put in place calls this in the same step, so the stamps never show twice and
    /// never go missing between the two.
    /// </summary>
    internal static void ShowStamps(Image? page, IEnumerable<UIElement> stamps)
    {
        var visibility = IsStamped(page?.Source) ? Visibility.Hidden : Visibility.Visible;
        foreach (var element in stamps) element.Visibility = visibility;
    }
}
