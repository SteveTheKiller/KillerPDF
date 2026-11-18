using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerPDF.Services
{
    /// <summary>
    /// Turns a scanned or photographed signature into ink on a transparent page. Sensitivity
    /// decides how light a pixel can be and still count as ink: lower values keep only the darkest
    /// strokes and read lighter, higher values keep fainter strokes and deepen the ink.
    /// </summary>
    internal static class SignatureInk
    {
        internal const double MinSensitivity = 0;
        internal const double MaxSensitivity = 100;
        internal const double DefaultSensitivity = 50;

        // Width of the luminance ramp between full ink and paper, so stroke edges stay smooth.
        private const double Softness = 0.2;

        /// <summary>Applies the sensitivity to BGRA32 pixels (straight alpha) and returns new pixels.
        /// Paper becomes transparent, ink keeps its hue, and pixels already transparent stay so.</summary>
        internal static byte[] Apply(byte[] bgra, double sensitivity)
        {
            double s = Math.Clamp(sensitivity, MinSensitivity, MaxSensitivity) / MaxSensitivity;
            double cutoff = 0.45 + 0.5 * s;     // lightest luminance that still reads as ink
            double blackPoint = 0.4 * s;        // deepens the ink as sensitivity rises

            var result = new byte[bgra.Length];
            for (int i = 0; i + 3 < bgra.Length; i += 4)
            {
                double b = bgra[i] / 255.0, g = bgra[i + 1] / 255.0, r = bgra[i + 2] / 255.0;
                double luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                double ink = Math.Clamp((cutoff - luminance) / Softness, 0, 1);

                result[i]     = Deepen(b, blackPoint);
                result[i + 1] = Deepen(g, blackPoint);
                result[i + 2] = Deepen(r, blackPoint);
                result[i + 3] = (byte)Math.Round(bgra[i + 3] * ink);
            }
            return result;
        }

        private static byte Deepen(double channel, double blackPoint) =>
            (byte)Math.Round(Math.Clamp((channel - blackPoint) / (1 - blackPoint), 0, 1) * 255);

        /// <summary>Reads any bitmap as BGRA32 pixels with straight alpha.</summary>
        internal static byte[] ReadPixels(BitmapSource source, out int width, out int height)
        {
            BitmapSource bgra = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            width = bgra.PixelWidth;
            height = bgra.PixelHeight;
            var pixels = new byte[width * height * 4];
            bgra.CopyPixels(pixels, width * 4, 0);
            return pixels;
        }

        /// <summary>Builds a frozen BGRA32 bitmap from pixels returned by <see cref="Apply"/>.</summary>
        internal static BitmapSource ToBitmap(byte[] bgra, int width, int height)
        {
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>Scales a large photo down for the live preview so the slider stays responsive.</summary>
        internal static BitmapSource PreviewSource(BitmapSource source, int maxEdge)
        {
            int edge = Math.Max(source.PixelWidth, source.PixelHeight);
            if (edge <= maxEdge) return source;
            double scale = (double)maxEdge / edge;
            var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            scaled.Freeze();
            return scaled;
        }

        // ---- Signature editor (threshold, pen weight, feather, ink color) ----------------------------------
        // Everything below works on BGRA32 pixels with straight alpha and never touches WPF, so the editor's
        // look can be tested and ported as plain arithmetic.

        internal const double MinWeight = -3, MaxWeight = 3;
        internal const double MinFeather = 0, MaxFeather = 6;

        /// <summary>Threshold for a picture whose paper is already gone: only the alpha changes, so a second
        /// pass over cleaned ink does not keep darkening it the way <see cref="Apply"/> would.</summary>
        internal static byte[] ThresholdAlpha(byte[] bgra, double sensitivity)
        {
            double s = Math.Clamp(sensitivity, MinSensitivity, MaxSensitivity) / MaxSensitivity;
            double cutoff = 0.45 + 0.5 * s;
            var result = (byte[])bgra.Clone();
            for (int i = 0; i + 3 < bgra.Length; i += 4)
            {
                double luminance = (0.2126 * bgra[i + 2] + 0.7152 * bgra[i + 1] + 0.0722 * bgra[i]) / 255.0;
                double ink = Math.Clamp((cutoff - luminance) / Softness, 0, 1);
                result[i + 3] = (byte)Math.Round(bgra[i + 3] * ink);
            }
            return result;
        }

        /// <summary>Grows (radius above 0) or thins (below 0) the ink by a square max or min filter on alpha.
        /// A grown pixel takes the color of the ink it grew from, so no paper color leaks into the edge.</summary>
        internal static byte[] Weight(byte[] bgra, int width, int height, int radius)
        {
            if (radius == 0 || width <= 0 || height <= 0) return (byte[])bgra.Clone();
            bool grow = radius > 0;
            int r = Math.Abs(radius);
            byte[] pass = WeightPass(bgra, width, height, r, grow, horizontal: true);
            return WeightPass(pass, width, height, r, grow, horizontal: false);
        }

        private static byte[] WeightPass(byte[] src, int width, int height, int r, bool grow, bool horizontal)
        {
            var dst = new byte[src.Length];
            int lineCount = horizontal ? height : width, lineLength = horizontal ? width : height;
            for (int line = 0; line < lineCount; line++)
                for (int pos = 0; pos < lineLength; pos++)
                {
                    int best = -1, bestAlpha = grow ? -1 : 256;
                    for (int k = pos - r; k <= pos + r; k++)
                    {
                        // Outside the picture counts as paper, so thinning eats in from the edges too.
                        int alpha = k < 0 || k >= lineLength ? 0 : src[Index(line, k) + 3];
                        if (grow ? alpha > bestAlpha : alpha < bestAlpha)
                        {
                            bestAlpha = alpha;
                            best = k < 0 || k >= lineLength ? -1 : Index(line, k);
                        }
                    }
                    int o = Index(line, pos);
                    // Thinning keeps the pixel's own color; growing borrows the strongest neighbour's.
                    int colorFrom = grow && best >= 0 ? best : o;
                    dst[o] = src[colorFrom];
                    dst[o + 1] = src[colorFrom + 1];
                    dst[o + 2] = src[colorFrom + 2];
                    dst[o + 3] = (byte)Math.Max(0, bestAlpha);
                }
            return dst;

            int Index(int line, int pos) => (horizontal ? line * width + pos : pos * width + line) * 4;
        }

        /// <summary>Paints every pixel in one ink color. Alpha keeps the ink's shape, scaled by the color's alpha.</summary>
        internal static byte[] Recolor(byte[] bgra, uint argb)
        {
            byte a = (byte)(argb >> 24), r = (byte)(argb >> 16), g = (byte)(argb >> 8), b = (byte)argb;
            var result = new byte[bgra.Length];
            for (int i = 0; i + 3 < bgra.Length; i += 4)
            {
                result[i] = b;
                result[i + 1] = g;
                result[i + 2] = r;
                result[i + 3] = (byte)Math.Round(bgra[i + 3] * a / 255.0);
            }
            return result;
        }

        /// <summary>Feathers the ink's edges without blurring it. The ink's outline (alpha at least half) stays
        /// where it is, so a line keeps its width; a ramp <paramref name="radius"/> pixels wide is laid across that
        /// outline, half inside and half outside, so the core stays fully opaque and sharp. A line thinner than the
        /// ramp gets a ramp only as deep as the line, so its middle never fades. New edge pixels outside the ink
        /// take the nearest ink pixel's color, so transparent paper never shows as a halo.</summary>
        internal static byte[] Feather(byte[] bgra, int width, int height, double radius)
        {
            if (radius < 0.5 || width <= 0 || height <= 0) return (byte[])bgra.Clone();
            int n = width * height;
            var ink = new bool[n];
            bool any = false;
            for (int p = 0; p < n; p++) any |= ink[p] = bgra[p * 4 + 3] >= 128;
            if (!any) return (byte[])bgra.Clone();

            // Distance from each pixel's center to the outline: inside to the nearest paper pixel, outside to the
            // nearest ink pixel (which also gives the color an outside pixel borrows).
            float[] inside = Distance(ink, width, height, toInk: false, out _);
            float[] outside = Distance(ink, width, height, toInk: true, out int[] nearestInk);

            // How deep the ink around each pixel goes: the deepest inside distance within one ramp's reach. A thick
            // stroke gets the full half ramp inside its edge; a thin one only as much as it has.
            double half = radius / 2;
            int reach = Math.Max(1, (int)Math.Ceiling(radius));
            float[] depth = MaxFilter(inside, width, height, reach);

            var result = new byte[bgra.Length];
            for (int p = 0; p < n; p++)
            {
                // Signed distance from the pixel's center to the outline. A fully covered edge pixel sits half a
                // pixel inside it and a bare neighbor half a pixel outside; an antialiased pixel's own coverage moves
                // the outline within it, so the smallest ramp gives back the original edge.
                double c = bgra[p * 4 + 3] / 255.0;
                double s = ink[p] ? inside[p] - 1.5 + c : c + 0.5 - outside[p];
                double h = Math.Clamp(Math.Min(half, depth[p] - 0.5), 0.5, Math.Max(0.5, half));
                double a = Math.Clamp(0.5 + s / (2 * h), 0, 1);
                int src = ink[p] ? p : nearestInk[p];
                if (a <= 0 || src < 0) continue;
                result[p * 4] = bgra[src * 4];
                result[p * 4 + 1] = bgra[src * 4 + 1];
                result[p * 4 + 2] = bgra[src * 4 + 2];
                result[p * 4 + 3] = (byte)Math.Round(a * 255);
            }
            return result;
        }

        // Two-pass chamfer distance (steps of 1 and the square root of 2) from every pixel to the nearest pixel of
        // the other kind; a target pixel is 0. Off the picture counts as paper, so ink at the border has an edge.
        private static float[] Distance(bool[] ink, int width, int height, bool toInk, out int[] nearest)
        {
            const float Diag = 1.41421356f;
            int n = width * height;
            var d = new float[n];
            nearest = new int[n];
            for (int p = 0; p < n; p++)
            {
                bool target = ink[p] == toInk;
                d[p] = target ? 0 : float.MaxValue;
                nearest[p] = target ? p : -1;
            }
            int[] near = nearest;
            void Relax(int p, int x, int y, int dx, int dy, float step)
            {
                int qx = x + dx, qy = y + dy;
                if (qx < 0 || qy < 0 || qx >= width || qy >= height)
                {
                    // The border: paper lies just beyond it.
                    if (!toInk && d[p] > step) { d[p] = step; }
                    return;
                }
                int q = qy * width + qx;
                if (d[q] + step < d[p]) { d[p] = d[q] + step; near[p] = near[q]; }
            }
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int p = y * width + x;
                    if (d[p] == 0) continue;
                    Relax(p, x, y, -1, 0, 1); Relax(p, x, y, 0, -1, 1);
                    Relax(p, x, y, -1, -1, Diag); Relax(p, x, y, 1, -1, Diag);
                }
            for (int y = height - 1; y >= 0; y--)
                for (int x = width - 1; x >= 0; x--)
                {
                    int p = y * width + x;
                    if (d[p] == 0) continue;
                    Relax(p, x, y, 1, 0, 1); Relax(p, x, y, 0, 1, 1);
                    Relax(p, x, y, 1, 1, Diag); Relax(p, x, y, -1, 1, Diag);
                }
            return d;
        }

        // The largest value within a square of the given reach around each pixel (rows, then columns).
        private static float[] MaxFilter(float[] src, int width, int height, int reach)
        {
            var rows = new float[src.Length];
            var result = new float[src.Length];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    float m = 0;
                    for (int k = Math.Max(0, x - reach); k <= Math.Min(width - 1, x + reach); k++) m = Math.Max(m, src[y * width + k]);
                    rows[y * width + x] = m;
                }
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                {
                    float m = 0;
                    for (int k = Math.Max(0, y - reach); k <= Math.Min(height - 1, y + reach); k++) m = Math.Max(m, rows[k * width + x]);
                    result[y * width + x] = m;
                }
            return result;
        }

        // How far a tap's second point sits from its first: far below a pixel, so the round caps draw a dot.
        private const double DotLength = 0.01;

        /// <summary>The stroke the pad keeps when the pen lifts, or null when nothing was drawn. A tap is a
        /// dot: its one point gets a second a hair away, because the page, the placing ghost, the picker and
        /// the saved PDF draw only strokes of two points or more, and their round caps make that a dot.</summary>
        internal static List<System.Windows.Point>? FinishStroke(List<System.Windows.Point>? points)
        {
            if (points is not { Count: > 0 }) return null;
            if (points.Count == 1) points.Add(new System.Windows.Point(points[0].X + DotLength, points[0].Y));
            return points;
        }

        /// <summary>Draws pen strokes as black ink on transparent pixels. Points are in canvas units; the
        /// picture is canvas size times <paramref name="scale"/>. Coverage comes from each pixel's distance
        /// to the nearest segment, so the edges are antialiased and the caps and joins are round.</summary>
        internal static byte[] RasterizeStrokes(IReadOnlyList<IReadOnlyList<(double X, double Y)>> strokes,
            double strokeWidth, int width, int height, double scale)
        {
            var pixels = new byte[width * height * 4];
            double half = Math.Max(0.5, strokeWidth * scale / 2);
            foreach (var stroke in strokes)
            {
                if (stroke.Count == 0) continue;
                // A single point is a dot: a zero-length segment, which the distance test draws as a disc.
                for (int i = stroke.Count == 1 ? 0 : 1; i < stroke.Count; i++)
                {
                    double x0 = stroke[Math.Max(0, i - 1)].X * scale, y0 = stroke[Math.Max(0, i - 1)].Y * scale;
                    double x1 = stroke[i].X * scale, y1 = stroke[i].Y * scale;
                    int left = Math.Max(0, (int)Math.Floor(Math.Min(x0, x1) - half - 1));
                    int right = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(x0, x1) + half + 1));
                    int top = Math.Max(0, (int)Math.Floor(Math.Min(y0, y1) - half - 1));
                    int bottom = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(y0, y1) + half + 1));
                    for (int y = top; y <= bottom; y++)
                        for (int x = left; x <= right; x++)
                        {
                            double d = DistanceToSegment(x + 0.5, y + 0.5, x0, y0, x1, y1);
                            double coverage = Math.Clamp(half + 0.5 - d, 0, 1);
                            if (coverage <= 0) continue;
                            int o = (y * width + x) * 4;
                            byte alpha = (byte)Math.Round(coverage * 255);
                            if (alpha > pixels[o + 3]) pixels[o + 3] = alpha;
                        }
                }
            }
            return pixels;
        }

        private static double DistanceToSegment(double px, double py, double x0, double y0, double x1, double y1)
        {
            double dx = x1 - x0, dy = y1 - y0;
            double lengthSquared = dx * dx + dy * dy;
            double t = lengthSquared <= 1e-12 ? 0 : Math.Clamp(((px - x0) * dx + (py - y0) * dy) / lengthSquared, 0, 1);
            double cx = x0 + t * dx - px, cy = y0 + t * dy - py;
            return Math.Sqrt(cx * cx + cy * cy);
        }

        /// <summary>The editor's whole look in order: threshold, weight, ink color, feather. Weight and
        /// feather are in signature units and multiplied by <paramref name="pixelsPerUnit"/>.</summary>
        internal static byte[] Process(byte[] bgra, int width, int height, SignatureLook look, double pixelsPerUnit)
        {
            byte[] pixels = look.Threshold is double threshold
                ? (look.RawScan ? Apply(bgra, threshold) : ThresholdAlpha(bgra, threshold))
                : bgra;
            int weight = (int)Math.Round(Math.Clamp(look.Weight, MinWeight, MaxWeight) * pixelsPerUnit);
            pixels = Weight(pixels, width, height, weight);
            if (look.InkColor is uint ink) pixels = Recolor(pixels, ink);
            return Feather(pixels, width, height, Math.Clamp(look.Feather, MinFeather, MaxFeather) * pixelsPerUnit);
        }

        /// <summary>Encodes a bitmap as PNG, which keeps the transparent background.</summary>
        internal static byte[] EncodePng(BitmapSource bitmap)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
    }

    /// <summary>The signature editor's settings. Threshold is null for drawn ink, which has no paper to drop.
    /// RawScan marks the untouched scan or photo, thresholded the way the import always did; otherwise only
    /// alpha is thresholded. InkColor is ARGB; null keeps the ink's own colors.</summary>
    internal readonly record struct SignatureLook(double? Threshold, bool RawScan, double Weight, double Feather, uint? InkColor);
}
