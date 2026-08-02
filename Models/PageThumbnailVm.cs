using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;

namespace KillerPDF
{
    /// <summary>
    /// ViewModel for a single page thumbnail in the sidebar PageList.
    /// Thumbnail is loaded lazily on a background thread; the UI binds to
    /// the <see cref="Thumbnail"/> property and updates via PropertyChanged.
    /// </summary>
    internal sealed class PageThumbnailVm(int pageIndex, string filePath, int rotation = 0) : INotifyPropertyChanged
    {
        // Limit concurrent pdfium doc-reader opens to avoid contention
        private static readonly SemaphoreSlim _loadSem = new(2, 2);

        private BitmapSource? _thumb;
        private bool         _loadRequested;

        public int    PageIndex { get; } = pageIndex;
        public string Label     => string.Format(
            Application.Current?.TryFindResource("Str_PageLabel") as string ?? "Page {0}", PageIndex + 1);

        private readonly string _filePath = filePath;
        private readonly int    _rotation = ((rotation % 360) + 360) % 360; // degrees: 0, 90, 180, 270

        public BitmapSource? Thumbnail => _thumb;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Called when the ListBox item becomes visible (via binding getter trigger).</summary>
        public void RequestLoad()
        {
            if (_loadRequested) return;
            _loadRequested = true;
            System.Threading.Tasks.Task.Run(LoadAsync);
        }

        /// <summary>
        /// Seed the thumbnail before ItemsSource is set - no dispatch needed because
        /// no binding exists yet. Used to carry old thumbnails across a RefreshPageList
        /// call so the list never flashes blank.
        /// </summary>
        internal void SetThumbnailDirect(BitmapSource src) => _thumb = src;

        /// <summary>Called by RefreshPageList's bulk background loader.</summary>
        internal void SetThumbnail(BitmapSource src)
        {
            // A background load can finish after the app has begun shutting down (or between
            // tab switches), when Application.Current is briefly null. The UI is going away in
            // that case, so just drop the update instead of throwing.
            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                _thumb = src;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            }));
        }

        private async System.Threading.Tasks.Task LoadAsync()
        {
            await _loadSem.WaitAsync().ConfigureAwait(false);
            try
            {
                var src = BuildThumb(_filePath, PageIndex, _rotation);
                if (src != null) SetThumbnail(src);
            }
            catch { /* thumbnail not critical */ }
            finally { _loadSem.Release(); }
        }

        internal static BitmapSource? BuildThumb(string filePath, int pageIndex, int rotation = 0)
        {
            try
            {
                // Render thumbnails at a higher resolution than they're usually shown so the page list
                // stays crisp when the sidebar is dragged wider (thumbnails scale to the sidebar width).
                // 288px covers the ~240px the page can show at the widest sidebar; raise for sharper,
                // lower to save memory (each loaded thumbnail is kept in RAM).
                using var docReader = DocLib.Instance.GetDocReader(filePath, new PageDimensions(288, 576));
                using var pr = docReader.GetPageReader(pageIndex);
                int tw  = pr.GetPageWidth();
                int th  = pr.GetPageHeight();
                var raw = pr.GetImage(KillerPDF.Services.PdfRender.WithAnnotations);   // #141
                if (tw <= 0 || th <= 0 || raw == null || raw.Length < tw * th * 4)
                    return null;
                // Apply in-memory rotation (temp file stores /Rotate=0; _pageRotations holds true angle)
                if (rotation != 0)
                    (raw, tw, th) = Services.BitmapHelpers.RotateBitmap(raw, tw, th, rotation);
                return EncodeToBitmapSource(raw, tw, th);
            }
            catch { return null; }
        }

        /// <summary>
        /// Encode already-decoded BGRA pixels (with rotation applied) to a frozen BitmapFrame.
        /// Called by the RefreshPageList bulk loader which manages its own doc reader.
        /// </summary>
        internal static BitmapSource? BuildThumbFromRaw(byte[] bgra, int width, int height)
            => EncodeToBitmapSource(bgra, width, height);

        /// <summary>
        /// Encode raw BGRA (pdfium) → PNG → frozen BitmapFrame entirely on the calling thread.
        /// GDI+ Format32bppArgb is BGRA in memory, matching pdfium output exactly.
        /// </summary>
        private static BitmapSource? EncodeToBitmapSource(byte[] bgra, int width, int height)
        {
            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                using var bmp = new System.Drawing.Bitmap(
                    width, height, width * 4,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb,
                    pin.AddrOfPinnedObject());
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                var src = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                src.Freeze();
                return src;
            }
            finally { pin.Free(); }
        }
    }
}
