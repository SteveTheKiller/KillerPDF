using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerPDF.Services
{
    // The Transform tool's raster steps, shared by the window preview and the raster Apply: mirror, then scale
    // (per page-size mode), then rotate into a page grown to fit. PageTransformGeometry is the same order as a PDF
    // matrix for the vector Apply.
    internal static class PageTransformRaster
    {
        internal static BitmapSource ComposeTransform(BitmapSource src, double angleDeg, double scale, bool fixedPage, bool flipH, bool flipV)
        {
            var s = ApplyFlip(src, flipH, flipV);
            var scaled = Math.Abs(scale - 1.0) < 0.001 ? s : ScaleCompose(s, scale, fixedPage);
            return Math.Abs(angleDeg) < 0.001 ? scaled : RotateExpand(scaled, angleDeg);
        }

        internal static BitmapSource ApplyFlip(BitmapSource src, bool flipH, bool flipV)
        {
            if (!flipH && !flipV) return src;
            var tb = new TransformedBitmap(src, new ScaleTransform(flipH ? -1 : 1, flipV ? -1 : 1));
            tb.Freeze();
            return tb;
        }

        // fixedPage=true: keep the canvas size, shrink the content with white margins. false: resize the page
        // (fewer pixels at the same points-per-pixel = a physically smaller page).
        private static RenderTargetBitmap ScaleCompose(BitmapSource src, double scale, bool fixedPage)
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            int sw = Math.Max(1, (int)Math.Round(w * scale));
            int sh = Math.Max(1, (int)Math.Round(h * scale));

            var dv = new DrawingVisual();
            if (fixedPage)
            {
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                    dc.DrawImage(src, new Rect((w - sw) / 2.0, (h - sh) / 2.0, sw, sh));
                }
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            else
            {
                using (var dc = dv.RenderOpen())
                    dc.DrawImage(src, new Rect(0, 0, sw, sh));
                var rtb = new RenderTargetBitmap(sw, sh, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
        }

        // Rotates a bitmap by angleDeg about its center into a canvas grown to the rotated bounding box, with
        // the new corners filled white.
        internal static BitmapSource RotateExpand(BitmapSource src, double angleDeg)
        {
            double w = src.PixelWidth, h = src.PixelHeight;
            double rad = angleDeg * Math.PI / 180.0;
            double cos = Math.Abs(Math.Cos(rad));
            double sin = Math.Abs(Math.Sin(rad));
            int nw = (int)Math.Ceiling(w * cos + h * sin);
            int nh = (int)Math.Ceiling(w * sin + h * cos);

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, nw, nh));
                dc.PushTransform(new TranslateTransform(nw / 2.0, nh / 2.0));
                dc.PushTransform(new RotateTransform(angleDeg));
                dc.DrawImage(src, new Rect(-w / 2.0, -h / 2.0, w, h));
                dc.Pop();
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(nw, nh, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
    }
}
