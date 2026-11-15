using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    // The page list's thumbnails show each page as it looks, unsaved edits included: the pane draws a page's edits
    // over its render, and redraws them shortly after the edits on a page change.
    public partial class PdfViewer
    {
        private DispatcherTimer? _thumbnailOverlayTimer;
        private readonly HashSet<int> _thumbnailOverlayPages = [];

        /// <summary>The page's unsaved edits drawn over its thumbnail render; the render itself when it has none.</summary>
        internal BitmapSource ThumbnailOverlayFor(int page, BitmapSource render)
        {
            if (!_annotations.TryGetValue(page, out var items) || items.Count == 0) return render;
            if (!_renderDims.TryGetValue(page, out var dims) || dims.w <= 0 || dims.h <= 0) return render;
            return ThumbnailOverlay.Compose(render, items, new Size(dims.w, dims.h),
                placed => GetAnnotationBitmap(placed, placed switch
                {
                    ImageAnnotation image => image.ImageData,
                    SignatureAnnotation signature => signature.ImageData,
                    _ => null,
                }));
        }

        // Every redraw of a page's edits ends here. A drag redraws on every move, so the thumbnail follows a
        // quarter second after the last one rather than on each.
        private void QueueThumbnailOverlay(int page)
        {
            _thumbnailOverlayPages.Add(page);
            _thumbnailOverlayTimer ??= NewThumbnailOverlayTimer();
            _thumbnailOverlayTimer.Stop();
            _thumbnailOverlayTimer.Start();
        }

        private DispatcherTimer NewThumbnailOverlayTimer()
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var rows = ThumbCache;
                foreach (int page in _thumbnailOverlayPages)
                    if (rows is not null && page >= 0 && page < rows.Length) rows[page].RefreshOverlay();
                _thumbnailOverlayPages.Clear();
            };
            return timer;
        }
    }
}
