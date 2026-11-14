using System.Windows.Media.Imaging;

namespace KillerPDF.Services;

/// <summary>Builds the thumbnail window a page list's rows draw through and ties it to the list's cancellation.</summary>
internal static class PageListThumbnails
{
    /// <summary>Makes the window that draws <paramref name="items"/>, points every row at it, and lets go of it when
    /// <paramref name="ct"/> is cancelled. <paramref name="markIncomplete"/> is told the rows can no longer draw, so
    /// the tab that owns them rebuilds its list when it is shown again.</summary>
    internal static ThumbnailWindow<BitmapSource> Wire<TSession>(PageThumbnailVm[] items,
        ThumbnailSessionOwner<TSession> renderer, Func<int, CancellationToken, BitmapSource?> draw,
        Action<PageThumbnailVm[]> markIncomplete, CancellationToken ct) where TSession : class, IDisposable
    {
        var window = new ThumbnailWindow<BitmapSource>(items.Length, (i, token) =>
        {
            if (ct.IsCancellationRequested) return null;
            return draw(i, ct);
        }, (i, image) =>
        {
            if (ct.IsCancellationRequested) return;
            if (image is null) items[i].ReleaseThumbnail();
            else items[i].SetThumbnail(image);
        }, drained: () =>
        {
            // The renderer holds decoded page images; a scanned PDF kept them while the tab stayed open.
            renderer.Release();
        });
        foreach (var item in items) item.RequestThumbnail = window.Request;
        LetGoOnCancel(ct, window, renderer, markIncomplete, items);
        return window;
    }

    // Registered from its own method so its closure is not the one the window's draw and publish callbacks share.
    // The window outlives the list in WPF's view cache through the rows, and must not lead from there to the owner.
    private static void LetGoOnCancel<TSession>(CancellationToken ct, ThumbnailWindow<BitmapSource> window,
        ThumbnailSessionOwner<TSession> renderer, Action<PageThumbnailVm[]> markIncomplete, PageThumbnailVm[] items)
        where TSession : class, IDisposable
    {
        ct.Register(() =>
        {
            window.Dispose();
            renderer.Release();
            // A cancelled window draws nothing more, so the rows stop pointing at it.
            foreach (var item in items) item.RequestThumbnail = null;
            // Rows not drawn yet would ask a cancelled window, so a tab that comes back rebuilds its list
            // (carrying the pictures already drawn) instead of re-seating these rows.
            markIncomplete(items);
        });
    }
}
