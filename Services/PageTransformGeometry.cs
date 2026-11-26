using System;
using System.Collections.Generic;
using System.IO;
using KillerPdf.Engine.Editing;

namespace KillerPDF.Services
{
    // The Transform tool's settings as one PDF matrix, for the vector path that keeps a page's text. The steps run
    // in the order the raster path draws them (PageTransformRaster.ComposeTransform): the page's in-app quarter turn, then
    // the mirror, then the scale, then the free turn into a page grown to fit it. Each step works in screen points
    // with y running down, like the bitmaps do, and the result maps the unrotated crop box onto the new page.
    internal static class PageTransformGeometry
    {
        private readonly record struct Affine(double A, double B, double C, double D, double E, double F)
        {
            // This step first, then next.
            public Affine Then(Affine next) => new(
                A * next.A + B * next.C, A * next.B + B * next.D,
                C * next.A + D * next.C, C * next.B + D * next.D,
                E * next.A + F * next.C + next.E, E * next.B + F * next.D + next.F);
        }

        internal static PdfPageTransform For(double cropLeft, double cropBottom, double cropWidth, double cropHeight,
            int rotation, double angleDeg, double scale, bool fixedPage, bool flipH, bool flipV)
        {
            double w = cropWidth, h = cropHeight;
            // PDF points to screen points: the crop box's top left is the origin.
            var m = new Affine(1, 0, 0, -1, -cropLeft, cropBottom + cropHeight);

            switch (((rotation % 360) + 360) % 360)
            {
                case 90: m = m.Then(new Affine(0, 1, -1, 0, h, 0)); (w, h) = (h, w); break;
                case 180: m = m.Then(new Affine(-1, 0, 0, -1, w, h)); break;
                case 270: m = m.Then(new Affine(0, -1, 1, 0, 0, w)); (w, h) = (h, w); break;
            }

            if (flipH) m = m.Then(new Affine(-1, 0, 0, 1, w, 0));
            if (flipV) m = m.Then(new Affine(1, 0, 0, -1, 0, h));

            if (Math.Abs(scale - 1.0) >= 0.001)
            {
                if (fixedPage)
                    m = m.Then(new Affine(scale, 0, 0, scale, (w - w * scale) / 2, (h - h * scale) / 2));
                else
                {
                    m = m.Then(new Affine(scale, 0, 0, scale, 0, 0));
                    (w, h) = (w * scale, h * scale);
                }
            }

            if (Math.Abs(angleDeg) >= 0.001)
            {
                double rad = angleDeg * Math.PI / 180.0;
                double cos = Math.Cos(rad), sin = Math.Sin(rad);
                double nw = w * Math.Abs(cos) + h * Math.Abs(sin);
                double nh = w * Math.Abs(sin) + h * Math.Abs(cos);
                // WPF's RotateTransform: a positive angle turns clockwise on a y-down screen.
                m = m.Then(new Affine(1, 0, 0, 1, -w / 2, -h / 2))
                     .Then(new Affine(cos, sin, -sin, cos, 0, 0))
                     .Then(new Affine(1, 0, 0, 1, nw / 2, nh / 2));
                (w, h) = (nw, nh);
            }

            // Screen points back to PDF points on the new page.
            m = m.Then(new Affine(1, 0, 0, -1, 0, h));
            return new PdfPageTransform(m.A, m.B, m.C, m.D, m.E, m.F, w, h);
        }

        // Transforms the pages of a working file in place, each from its own crop box and in-app rotation. The
        // working file carries no /Rotate, so the rotation comes from the viewer's map; the caller sets those
        // pages' rotation to 0 afterwards, since the turn is now in the page.
        internal static void TransformPages(string path, IReadOnlyCollection<int> pages,
            IReadOnlyDictionary<int, int> rotations, double angleDeg, double scale, bool fixedPage, bool flipH, bool flipV)
        {
            var document = KillerPdf.Engine.Documents.PdfDocument.Open(File.ReadAllBytes(path));
            var boxes = KillerPdf.Engine.Documents.PdfPageBoxInformation.Read(document);
            var transforms = new Dictionary<int, PdfPageTransform>();
            foreach (int page in pages)
            {
                var crop = boxes[page].CropBox;
                transforms[page] = For(crop.Left, crop.Bottom, crop.Width, crop.Height,
                    rotations.TryGetValue(page, out int rotation) ? rotation : 0,
                    angleDeg, scale, fixedPage, flipH, flipV);
            }
            AtomicFileOutput.Write(path, PdfPageTransformer.Transform(document, transforms));
        }
    }
}
