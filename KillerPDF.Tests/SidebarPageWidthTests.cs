using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// The Pages panel stops widening when its thumbnails reach their 200 px cap. Two-page thumbnails put two side by
// side, so that point is about twice as wide, and the panel must be allowed to get there.
public sealed class SidebarPageWidthTests
{
    [Fact]
    public void OneThumbnailStopsWhereTheThumbnailIsFull()
        => Assert.Equal(234, SidebarPageWidth.Max(paired: false));

    [Fact]
    public void TwoPageThumbnailsGetRoomForTwoFullThumbnails()
    {
        double single = SidebarPageWidth.Max(paired: false);
        double paired = SidebarPageWidth.Max(paired: true);
        // Two thumbnails' worth of width, with the panel's own margins and scroll bar counted once.
        Assert.Equal(2 * SidebarPageWidth.ThumbnailCap + (single - SidebarPageWidth.ThumbnailCap), paired);
        Assert.True(paired >= 2 * single - 40, "about twice the single-thumbnail limit");
    }
}
