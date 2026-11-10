using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KillerPDF.Tests;

// A rebuilt page list takes over the pictures the previous list shows. Reading them must not ask the previous list's
// thumbnail window to draw anything: after a tab moves to the other pane, the previous rows belong to a tab that is
// still open, and asking would draw every one of its pages in the background.
public sealed class PageListThumbnailCarryTests
{
    [Fact]
    public void CarryingPicturesOverAsksThePreviousListToDrawNothing()
    {
        int asked = 0;
        var previous = Rows(4);
        foreach (var row in previous) row.RequestThumbnail = _ => asked++;
        var drawn = Picture();
        previous[1].SetThumbnailDirect(drawn);   // seeded, so stale until its own picture arrives
        var rows = Rows(4);

        KillerPDF.PageThumbnailVm.CarryPictures(previous, rows, null, Services.WorkingCmykProfiles.Generation);

        Assert.Equal(0, asked);
        Assert.Same(drawn, Shown(rows[1]));
    }

    private static KillerPDF.PageThumbnailVm[] Rows(int count) =>
        [.. Enumerable.Range(0, count).Select(index => new KillerPDF.PageThumbnailVm(index, "carry.pdf", null))];

    // What the list shows for a row, read without letting the row ask for a picture.
    private static BitmapSource? Shown(KillerPDF.PageThumbnailVm row)
    {
        row.RequestThumbnail = null;
        return row.Thumbnail;
    }

    private static BitmapSource Picture()
    {
        var picture = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        picture.Freeze();
        return picture;
    }
}
