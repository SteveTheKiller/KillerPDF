using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerPDF.Services
{
    // ============================================================
    // Raw-bitmap helpers - pure functions over BGRA pixel buffers,
    // no window state. Formerly a MainWindow partial (KillerUI
    // refactor); shared by the render paths, thumbnails, page
    // export, OCR and the CLI.
    // ============================================================
    internal static class BitmapHelpers
    {
        /// <summary>
        /// Rotates a raw BGRA (4 bytes/pixel) bitmap clockwise by degrees.
        /// Used because Docnet's FPDF_RenderPageBitmapWithMatrix uses a pure-scaling
        /// matrix, so PDFium renders the page in its MediaBox orientation (no rotation).
        /// We strip /Rotate from the temp file so content is never clipped, then rotate
        /// the pixel buffer here to match the intended visual orientation.
        /// </summary>
        internal static (byte[] bytes, int w, int h) RotateBitmap(byte[] src, int w, int h, int degrees)
        {
            degrees = ((degrees % 360) + 360) % 360;
            if (degrees == 0) return (src, w, h);
            int newW = (degrees == 90 || degrees == 270) ? h : w;
            int newH = (degrees == 90 || degrees == 270) ? w : h;
            byte[] dst = new byte[newW * newH * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int srcIdx = (y * w + x) * 4;
                    int dstX, dstY;
                    switch (degrees)
                    {
                        case 90: dstX = h - 1 - y; dstY = x; break; // CW
                        case 180: dstX = w - 1 - x; dstY = h - 1 - y; break;
                        default: dstX = y; dstY = w - 1 - x; break; // 270 CW
                    }
                    int dstIdx = (dstY * newW + dstX) * 4;
                    dst[dstIdx] = src[srcIdx];
                    dst[dstIdx + 1] = src[srcIdx + 1];
                    dst[dstIdx + 2] = src[srcIdx + 2];
                    dst[dstIdx + 3] = src[srcIdx + 3];
                }
            }
            return (dst, newW, newH);
        }

        // ============================================================
        // Document color inversion (#135, "dark mode")
        // ============================================================

        // True = the document pane renders with inverted colors (dark-mode reading). DISPLAY
        // ONLY: saves, prints, exports, OCR, thumbnails, and tool previews all keep the
        // document's true colors. Loaded from the "DocInvert" setting at startup; toggled from
        // the Settings panel, which flushes the render caches (the state is baked into pixels).
        internal static bool DocInvert;

        // True = night mode inverts pictures along with everything else (the pre-carve-out
        // behavior, now opt-in from the moon button's right-click menu; default off). Loaded
        // from the "DocInvertImages" setting at startup.
        internal static bool DocInvertImages;

        /// <summary>In-place inversion for the display dark mode, applied at the Viewport render
        /// sites BEFORE the pixel-buffer rotation (via InvertBgraInPlaceExcept, which carves the
        /// image regions back out). PDF pages usually paint NO background - the "paper" is
        /// transparent pixels compositing over the white page slot - so a plain RGB flip left
        /// the page white and merely faded the ink. Composite over white and invert in one
        /// step: out = a*(255-c)/255 with alpha forced opaque. White (or unpainted) paper
        /// becomes black, dark ink becomes light, and opaque images get a true negative.</summary>
        internal static void InvertBgraInPlace(byte[] bgra)
        {
            for (int i = 0; i + 3 < bgra.Length; i += 4)
            {
                int a = bgra[i + 3];
                bgra[i]     = (byte)(a * (255 - bgra[i])     / 255);
                bgra[i + 1] = (byte)(a * (255 - bgra[i + 1]) / 255);
                bgra[i + 2] = (byte)(a * (255 - bgra[i + 2]) / 255);
                bgra[i + 3] = 255;
            }
        }

        /// <summary>An image's bounding box as FRACTIONS of the unrotated page (top-left origin),
        /// so one cached set serves every render resolution. Produced by PdfImages.GetFracRects.</summary>
        internal readonly record struct FracRect(double L, double T, double R, double B);

        /// <summary>
        /// #135 follow-up: dark mode that does NOT invert pictures. Inverts the whole page with
        /// the operator above, then applies the SAME operator once more over the image regions.
        /// That second pass is exact, not approximate: for an already-inverted opaque pixel,
        /// out = 255 - (a*(255-c)/255) = (a*c + (255-a)*255)/255 - the ORIGINAL pixel composited
        /// over white, which is precisely what the image looked like on the normal white page.
        /// Overlapping image boxes are merged per scanline so no pixel gets the operator twice.
        /// </summary>
        internal static void InvertBgraInPlaceExcept(byte[] bgra, int width, int height, FracRect[] keep)
        {
            InvertBgraInPlace(bgra);
            if (keep is null || keep.Length == 0 || width <= 0 || height <= 0) return;

            // Fractions -> pixel boxes, clamped. Floor/ceiling so a box never leaves a 1px
            // inverted sliver of the image at its edge.
            var px = new List<(int x0, int y0, int x1, int y1)>(keep.Length);
            foreach (var r in keep)
            {
                int x0 = Math.Max(0, (int)Math.Floor(r.L * width));
                int x1 = Math.Min(width, (int)Math.Ceiling(r.R * width));
                int y0 = Math.Max(0, (int)Math.Floor(r.T * height));
                int y1 = Math.Min(height, (int)Math.Ceiling(r.B * height));
                if (x1 > x0 && y1 > y0) px.Add((x0, y0, x1, y1));
            }
            if (px.Count == 0) return;

            var spans = new List<(int x0, int x1)>(px.Count);
            for (int y = 0; y < height; y++)
            {
                spans.Clear();
                foreach (var b in px)
                    if (y >= b.y0 && y < b.y1) spans.Add((b.x0, b.x1));
                if (spans.Count == 0) continue;
                spans.Sort((a, b) => a.x0.CompareTo(b.x0));

                int row = y * width * 4;
                int curStart = spans[0].x0, curEnd = spans[0].x1;
                for (int s = 1; s <= spans.Count; s++)
                {
                    if (s < spans.Count && spans[s].x0 <= curEnd)
                    {
                        if (spans[s].x1 > curEnd) curEnd = spans[s].x1;
                        continue;
                    }
                    for (int x = curStart; x < curEnd; x++)
                    {
                        int i = row + x * 4;
                        int a = bgra[i + 3];
                        bgra[i]     = (byte)(a * (255 - bgra[i])     / 255);
                        bgra[i + 1] = (byte)(a * (255 - bgra[i + 1]) / 255);
                        bgra[i + 2] = (byte)(a * (255 - bgra[i + 2]) / 255);
                        bgra[i + 3] = 255;
                    }
                    if (s < spans.Count) { curStart = spans[s].x0; curEnd = spans[s].x1; }
                }
            }
        }

        /// <summary>
        /// Encodes raw BGRA pixel data from pdfium to PNG without touching the UI thread.
        /// GDI+ Format32bppArgb is BGRA in memory - matches pdfium output exactly.
        /// </summary>
        internal static byte[] RenderToPng(byte[] bgra, int width, int height)
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
                return ms.ToArray();
            }
            finally { pin.Free(); }
        }

        // Builds a frozen bitmap sized so its baked DPI displays it at (dipW x dipH) DIPs. Shared by the
        // tile and the render cache so a cached tile bitmap reuses the exact same geometry.
        internal static BitmapSource BuildScaledBitmap(int w, int h, byte[] rawBytes, int dipW, int dipH)
        {
            var wb = new WriteableBitmap(w, h, 96.0 * w / Math.Max(1, dipW), 96.0 * h / Math.Max(1, dipH), PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, w, h), rawBytes, w * 4, 0);
            wb.Freeze();
            return wb;
        }

        /// <summary>
        /// Encodes raw BGRA pixel data to JPEG (quality 90) via WPF's encoder. Born as the CLI's
        /// CliEncodeJpeg (no JPEG encoder existed before --to-image); homed here beside RenderToPng
        /// in the KillerUI refactor, shared by the CLI and the GUI image export.
        /// </summary>
        internal static byte[] EncodeJpeg(byte[] bgra, int width, int height)
        {
            var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
    }
}
