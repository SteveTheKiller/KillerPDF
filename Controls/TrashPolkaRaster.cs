using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerPDF.Controls
{
    /// <summary>
    /// Trash Polka's ink pieces as bitmaps instead of thousands of vector marks. Drawing the vectors costs nothing
    /// on the GPU, but every software render of them (the theme picker's crossfade snapshots, RenderTargetBitmap) walked
    /// the whole piece once per painted pane: about a second per snapshot, two snapshots per accent click, measured on
    /// KillerPDF's main window on 2026-11-17.
    ///
    /// Compositing is linear in the accent color: an accent mark of alpha a over a pixel P gives a*c + (1 - a)*P. So the
    /// finished piece is always Rest + c*M, where Rest is the piece drawn with a black accent and M is the accent's
    /// coverage after everything drawn over it (the white-accent render minus the black one). Shown as a base layer of
    /// Rest/(1 - M) with M tinted in the accent color laid over it, the result is the vector piece for every accent,
    /// and an accent switch only retints the coverage.
    /// </summary>
    internal static class TrashPolkaRaster
    {
        internal sealed class Layers
        {
            internal Layers(byte[] inkPixels, byte[] coverage, int width, int height, double dpi)
            {
                InkPixels = inkPixels; Coverage = coverage; Width = width; Height = height; Dpi = dpi;
            }
            /// <summary>Rest/(1 - M) as premultiplied BGRA.</summary>
            internal byte[] InkPixels { get; }
            /// <summary>The accent's coverage M, one byte per pixel.</summary>
            internal byte[] Coverage { get; }
            internal int Width { get; }
            internal int Height { get; }
            internal double Dpi { get; }
        }

        private static readonly Dictionary<string, Task<Layers?>> Builds = [];

        /// <summary>The layers for one piece at one screen size and device scale, or null while they are still being built
        /// (the caller draws the vector piece meanwhile and repaints when <paramref name="ready"/> runs).</summary>
        internal static Layers? Get(int seed, Size size, double scale, double dpi, Action ready)
        {
            string key = $"{seed}|{size}|{scale}|{dpi}";
            if (!Builds.TryGetValue(key, out var build))
            {
                Builds[key] = build = Start(seed, size, scale, dpi);
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                build.ContinueWith(_ => dispatcher.BeginInvoke(ready), TaskScheduler.Default);
            }
            return build.IsCompleted && build.Status == TaskStatus.RanToCompletion ? build.Result : null;
        }

        private static Task<Layers?> Start(int seed, Size size, double scale, double dpi)
        {
            var done = new TaskCompletionSource<Layers?>();
            var thread = new Thread(() =>
            {
                // A failed build leaves the vector piece in place; the motif never depends on the bitmaps.
                try { done.SetResult(Build(seed, size, scale, dpi)); }
                catch { done.SetResult(null); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return done.Task;
        }

        private static Layers Build(int seed, Size size, double scale, double dpi)
        {
            int w = (int)Math.Ceiling(size.Width * scale * dpi), h = (int)Math.Ceiling(size.Height * scale * dpi);
            byte[] dark = Render(Colors.Black, seed, size, scale * dpi, w, h);
            byte[] light = Render(Colors.White, seed, size, scale * dpi, w, h);
            var ink = new byte[dark.Length];
            var coverage = new byte[w * h];
            for (int i = 0; i < dark.Length; i += 4)
            {
                int m = light[i + 1] - dark[i + 1];
                if (m < 0) m = 0;
                if (m >= 255) { coverage[i / 4] = 255; continue; }
                coverage[i / 4] = (byte)m;
                double k = 255.0 / (255 - m);
                int alpha = Math.Min(255, (int)Math.Round((dark[i + 3] - m) * k));
                if (alpha < 0) alpha = 0;
                ink[i + 3] = (byte)alpha;
                for (int c = 0; c < 3; c++)
                    ink[i + c] = (byte)Math.Min(alpha, (int)Math.Round(dark[i + c] * k));
            }
            return new Layers(ink, coverage, w, h, dpi);
        }

        private static byte[] Render(Color accent, int seed, Size size, double zoom, int w, int h)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(zoom, zoom));
                dc.DrawDrawing(TrashPolkaInk.DrawFrozen(accent, size, seed));
                dc.Pop();
            }
            var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var pixels = new byte[w * h * 4];
            target.CopyPixels(pixels, w * 4, 0);
            return pixels;
        }

        private static BitmapSource Bitmap(byte[] pixels, int w, int h, double dpi)
        {
            var bitmap = BitmapSource.Create(w, h, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32, null, pixels, w * 4);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>
        /// The piece as one frozen bitmap in the vector piece's own units, with the accent coverage tinted with
        /// <paramref name="accent"/> and laid over the ink. Tinting is one pass over the pixels (tens of milliseconds),
        /// and it keeps every pane one plain image draw; software renders pay per full-size bitmap drawn.
        /// </summary>
        internal static Drawing Compose(Layers layers, Size size, Color accent)
        {
            // The tint laid over the ink, premultiplied: out = tint + ink * (1 - m).
            var pixels = (byte[])layers.InkPixels.Clone();
            for (int i = 0, j = 0; i < layers.Coverage.Length; i++, j += 4)
            {
                int m = layers.Coverage[i];
                if (m == 0) continue;
                int keep = 255 - m;
                pixels[j] = (byte)((accent.B * m + pixels[j] * keep) / 255);
                pixels[j + 1] = (byte)((accent.G * m + pixels[j + 1] * keep) / 255);
                pixels[j + 2] = (byte)((accent.R * m + pixels[j + 2] * keep) / 255);
                pixels[j + 3] = (byte)((255 * m + pixels[j + 3] * keep) / 255);
            }
            var drawing = new ImageDrawing(Bitmap(pixels, layers.Width, layers.Height, layers.Dpi), new Rect(0, 0, size.Width, size.Height));
            drawing.Freeze();
            return drawing;
        }
    }
}
