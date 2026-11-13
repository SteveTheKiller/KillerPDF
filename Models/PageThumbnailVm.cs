using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using KillerPDF.Services;
using KillerPdf.Engine.ColorManagement;

namespace KillerPDF
{
    /// <summary>
    /// ViewModel for a single page thumbnail in the sidebar PageList.
    /// Thumbnail is loaded lazily on a background thread; the UI binds to
    /// the <see cref="Thumbnail"/> property and updates via PropertyChanged.
    /// </summary>
    internal sealed class PageThumbnailVm(int pageIndex, string filePath, Services.OpenedProtection? protection,
        int rotation = 0, PdfDocumentProofViewSnapshot? proof = null) : INotifyPropertyChanged
    {
        private BitmapSource? _thumb;

        public int    PageIndex { get; } = pageIndex;
        public string Label     => string.Format(
            Application.Current?.TryFindResource("Str_PageLabel") as string ?? "Page {0}", PageIndex + 1);
        /// <summary>The page list item's accessible name, which UI Automation takes from the item.</summary>
        public override string ToString() => Label;


        private readonly string _filePath = filePath;
        private readonly Services.OpenedProtection? _protection = protection;   // the tab's, so its own file reads under its key
        private readonly int    _rotation = ((rotation % 360) + 360) % 360; // degrees: 0, 90, 180, 270
        private readonly PdfCapturedReadRevision? _nativeRevision;
        private readonly PdfDocumentProofViewSnapshot? _proof = proof;
        internal PdfDocumentProofViewStamp? ProofStamp => _proof?.Stamp;
        // The working CMYK choice this thumbnail is drawn with; a new choice redraws it.
        internal long WorkingCmyk { get; } = Services.WorkingCmykProfiles.Generation;

        internal PageThumbnailVm(int pageIndex, PdfCapturedReadRevision revision, int rotation = 0,
            PdfDocumentProofViewSnapshot? proof = null)
            : this(pageIndex, string.Empty, null, rotation, proof) => _nativeRevision = revision;

        /// <summary>The list reads this only for rows it shows, so a missing or stale picture asks the list's
        /// thumbnail window to draw it.</summary>
        public BitmapSource? Thumbnail
        {
            get
            {
                if (_thumb is null || _stale) RequestThumbnail?.Invoke(PageIndex);
                return _thumb;
            }
        }

        /// <summary>Asks for this row's picture; set by the page list that owns the row.</summary>
        internal Action<int>? RequestThumbnail { get; set; }
        private bool _stale;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Seed the thumbnail before ItemsSource is set - no dispatch needed because
        /// no binding exists yet. Used to carry old thumbnails across a RefreshPageList
        /// call so the list never flashes blank.
        /// </summary>
        internal void SetThumbnailDirect(BitmapSource src)
        {
            _thumb = src;
            _stale = true;   // shown until this row's own picture is drawn
        }

        /// <summary>Seeds a rebuilt page list with the pictures the list it replaces already shows, so it never
        /// flashes blank while each row draws its own. Rows drawn with another proof or working CMYK choice are skipped.</summary>
        internal static void CarryPictures(PageThumbnailVm[]? previous, PageThumbnailVm[] rows,
            PdfDocumentProofViewStamp? proof, long workingCmyk)
        {
            if (previous is null) return;
            for (int i = 0; i < rows.Length && i < previous.Length; i++)
            {
                if (previous[i].ProofStamp != proof || previous[i].WorkingCmyk != workingCmyk) continue;
                // The picture as it stands. Reading Thumbnail would ask the previous list's window to draw the row.
                var prev = previous[i]._thumb;
                if (prev != null) rows[i].SetThumbnailDirect(prev);
            }
        }

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
                _stale = false;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            }));
        }

        /// <summary>Lets go of the picture of a row the list has not asked for lately. A row still on screen asks
        /// for it again when the list reads it.</summary>
        internal void ReleaseThumbnail()
        {
            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_thumb is null) return;
                _thumb = null;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            }));
        }

        internal static BitmapSource? BuildNativeThumb(PdfCapturedReadRevision revision, int pageIndex,
            int rotation, int maximumWidth, int maximumHeight, CancellationToken cancellationToken,
            PdfDocumentProofSettings? proof = null, KillerPDF.Presentation.PdfSdrDisplayConverter? monitor = null)
        {
            var frame = NativeFrameRendering.Render(revision, pageIndex, maximumWidth, maximumHeight, cancellationToken, proof);
            int width = frame.Width, height = frame.Height;
            byte[] pixels = KillerPDF.Presentation.PdfViewingFrameConversion.DisplayPixels(frame, monitor, cancellationToken).Pixels;
            if (rotation != 0)
                (pixels, width, height) = BitmapHelpers.RotateBitmap(pixels, width, height, rotation);
            cancellationToken.ThrowIfCancellationRequested();
            return EncodeToBitmapSource(pixels, width, height);
        }

        /// <summary>One PDF page's thumbnail pixels, rotated, or null when the page drew nothing.</summary>
        internal static (byte[] Pixels, int Width, int Height)? RenderPdfThumbPixels(PdfPageRenderSession renderSession,
            int pageIndex, int rotation, CancellationToken cancellationToken, PdfDocumentProofSettings? proof = null,
            KillerPDF.Presentation.PdfSdrDisplayConverter? monitor = null)
        {
            int tw, th;
            byte[] raw;
            if (monitor is null)
            {
                PdfRenderedPage page = renderSession.RenderPreview(pageIndex, cancellationToken, proof);
                (tw, th, raw) = (page.Width, page.Height, page.Pixels);
            }
            else
            {
                // The page view's own conversion, so a thumbnail on a profiled monitor matches the page.
                var viewing = KillerPDF.Presentation.PdfViewingFrameConversion.Render(
                    requested => renderSession.RenderPreviewFrame(pageIndex, cancellationToken, requested),
                    () => renderSession.RenderPreview(pageIndex, cancellationToken), proof, monitor, cancellationToken);
                (tw, th, raw) = (viewing.Width, viewing.Height, viewing.Pixels);
            }
            if (tw <= 0 || th <= 0 || raw == null || raw.Length < tw * th * 4) return null;
            if (rotation != 0)
                (raw, tw, th) = BitmapHelpers.RotateBitmap(raw, tw, th, rotation);
            return (raw, tw, th);
        }

        internal static BitmapSource? BuildThumb(string filePath, Services.OpenedProtection? protection, int pageIndex,
            int rotation = 0, PdfDocumentProofSettings? proof = null)
        {
            try
            {
                // Render thumbnails at a higher resolution than they're usually shown so the page list
                // stays crisp when the sidebar is dragged wider (thumbnails scale to the sidebar width).
                // 288px covers the ~240px the page can show at the widest sidebar; raise for sharper,
                // lower to save memory (each loaded thumbnail is kept in RAM).
                using var renderSession =
                    PdfPageRenderSession.OpenEngineFirst(filePath, 288, 576, protection);
                PdfRenderedPage page = renderSession.RenderPreview(pageIndex, proof: proof);
                int tw = page.Width;
                int th = page.Height;
                byte[] raw = page.Pixels;
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
        private static BitmapFrame? EncodeToBitmapSource(byte[] bgra, int width, int height)
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
