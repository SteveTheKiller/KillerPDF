using System;
using System.Collections.Generic;
using System.Linq;
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
            internal Layers(int[] runs, uint[] inkValues, byte[] coverage, int width, int height, double dpi)
            {
                Runs = runs; InkValues = inkValues; Coverage = coverage; Width = width; Height = height; Dpi = dpi;
            }
            /// <summary>
            /// Rest/(1 - M) as premultiplied BGRA, kept sparse: most of the screen carries no ink, so only the ink pixels
            /// are stored. <see cref="Runs"/> holds (start, count) pairs of pixel indexes, every run inside one row, and
            /// <see cref="InkValues"/> their pixels in order. Every other pixel is transparent.
            /// </summary>
            internal int[] Runs { get; }
            internal uint[] InkValues { get; }
            /// <summary>The accent's coverage M, one byte per pixel.</summary>
            internal byte[] Coverage { get; }
            internal int Width { get; }
            internal int Height { get; }
            internal double Dpi { get; }
        }

        /// <summary>One set of layers: a piece at one screen size and device scale.</summary>
        internal readonly record struct LayerKey(int Seed, Size Size, double Scale, double Dpi);

        private static readonly Dictionary<LayerKey, Task<Layers?>> Builds = [];

        internal static bool BuildsSettled => Builds.Values.All(b => b.IsCompleted);
        internal static int BuildCount => Builds.Count;
        // Test counters (Shell/SelfTestThemeMotif.cs), written on the builder thread.
        internal static int CacheHits, CacheMisses;

        /// <summary>The layers for one piece at one screen size and device scale, or null while they are still being built
        /// (the caller draws the vector piece meanwhile and repaints when <paramref name="ready"/> runs).</summary>
        internal static Layers? Get(LayerKey key, Action ready)
        {
            if (!Builds.TryGetValue(key, out var build))
            {
                Builds[key] = build = Start(key.Seed, key.Size, key.Scale, key.Dpi);
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                build.ContinueWith(_ => dispatcher.BeginInvoke(ready), TaskScheduler.Default);
            }
            return build.IsCompleted && build.Status == TaskStatus.RanToCompletion ? build.Result : null;
        }

        /// <summary>
        /// Drops the finished layers for a screen size or a monitor scale no loaded element paints at any more. Each set
        /// is about 41 MB at 4K, and every DPI a window ever visited kept its own. A piece whose elements are only
        /// closed for now (a menu) keeps its layers while anything still paints at that size and scale, and nothing is
        /// dropped while no motif element is loaded at all.
        /// </summary>
        internal static void Trim(HashSet<LayerKey> inUse)
        {
            if (inUse.Count == 0) return;
            foreach (var key in Builds.Keys.ToList())
                if (Builds[key].IsCompleted && !inUse.Any(used => used.Size == key.Size && used.Dpi == key.Dpi && used.Scale == key.Scale))
                    Builds.Remove(key);
        }

        /// <summary>Forgets every set of layers, finished or building (a build still running lands into nothing).</summary>
        internal static void ReleaseAll()
        {
            Builds.Clear();
            OnDisk.Clear();
        }

        // Every piece builds on one STA thread, one after another, so only one piece's render buffers exist at a time.
        private static readonly System.Collections.Concurrent.BlockingCollection<Action> Queue = [];
        private static Thread? _builder;

        private static Task<Layers?> Start(int seed, Size size, double scale, double dpi)
        {
            var done = new TaskCompletionSource<Layers?>();
            // A failed build leaves the vector piece in place; the motif never depends on the bitmaps.
            Queue.Add(() =>
            {
                Action? save = null;
                try { done.SetResult(Build(seed, size, scale, dpi, out save)); }
                catch { done.SetResult(null); }
                // The disk copy is written after the piece is handed over, so a cold build shows as soon as it would without
                // the cache.
                save?.Invoke();
                save = null;
                // The render target's native pixels and the full-size array go with the next collection; collect now so
                // the next piece never stacks its buffers on this one's.
                GC.Collect();
                GC.WaitForPendingFinalizers();
            });
            EnsureBuilder();
            return done.Task;
        }

        private static void EnsureBuilder()
        {
            if (_builder is null)
            {
                _builder = new Thread(() => { foreach (var work in Queue.GetConsumingEnumerable()) work(); });
                _builder.SetApartmentState(ApartmentState.STA);
                _builder.IsBackground = true;
                _builder.Start();
            }
        }

        /// <summary>
        /// Builds and saves the pieces missing from the disk cache, one at a time on the builder thread at BelowNormal
        /// priority. Called by the cache builder child process. Nothing is kept: the layers are dropped and collected after each save, so under a theme with no motif
        /// the files are the only result. A key already built or building in this process is skipped.
        /// </summary>
        internal static Task Prewarm(IReadOnlyList<LayerKey> keys)
        {
            var done = new TaskCompletionSource();
            var wanted = keys.Where(k => !Builds.ContainsKey(k)).ToList();
            EnsureBuilder();
            Queue.Add(() =>
            {
                var priority = Thread.CurrentThread.Priority;
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                try
                {
                    foreach (var key in wanted)
                    {
                        if (IsCached(key)) continue;
                        try
                        {
                            Build(key.Seed, key.Size, key.Scale, key.Dpi, out var save, probeCache: false);
                            save?.Invoke();
                            PrewarmBuilt++;
                        }
                        catch (Exception) { }
                        // This runs in the windowless builder child (ThemeMotif.TryRunCacheBuilder), so a blocking collection
                        // pauses nothing the user sees, and the next piece never stacks on this one's buffers.
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                }
                finally { Thread.CurrentThread.Priority = priority; done.SetResult(); }
            });
            return done.Task;
        }

        internal static int PrewarmBuilt;

        private static readonly Dictionary<LayerKey, bool> OnDisk = [];

        /// <summary>
        /// Whether a piece still building will come from the disk cache (checked once per key, UI thread). The caller
        /// then skips the vector stand-in: drawing thousands of marks for the moment before a decode lands costs more than
        /// the decode.
        /// </summary>
        internal static bool LoadingFromDisk(LayerKey key)
        {
            if (Builds.TryGetValue(key, out var build) && build.IsCompleted) return false;
            if (!OnDisk.TryGetValue(key, out bool cached)) OnDisk[key] = cached = IsCached(key);
            return cached;
        }

        internal static bool IsCached(LayerKey key)
        {
            var (w, h) = PixelSize(key.Size, key.Scale, key.Dpi);
            string name = CacheKey(key.Seed, w, h, key.Scale, key.Dpi);
            return MotifDiskCache.Exists(CacheVersion, name, "ink") && MotifDiskCache.Exists(CacheVersion, name, "coverage");
        }

        private static (int W, int H) PixelSize(Size size, double scale, double dpi)
            => ((int)Math.Ceiling(size.Width * scale * dpi), (int)Math.Ceiling(size.Height * scale * dpi));

        /// <summary>
        /// A tint in progress: the bitmap is made and locked on the UI thread, and a worker writes the tinted pixels
        /// straight into its back buffer, so no full-size managed copy exists. <see cref="ToDrawing"/> unlocks and
        /// freezes it on the UI thread once the worker is done.
        /// </summary>
        internal sealed class Tinting(WriteableBitmap bitmap, Task work)
        {
            internal WriteableBitmap Bitmap { get; } = bitmap;
            internal Task Work { get; } = work;
        }

        internal static Tinting TintAsync(Layers layers, Color accent)
        {
            var bitmap = new WriteableBitmap(layers.Width, layers.Height, 96 * layers.Dpi, 96 * layers.Dpi, PixelFormats.Pbgra32, null);
            bitmap.Lock();
            IntPtr buffer = bitmap.BackBuffer;
            int stride = bitmap.BackBufferStride;
            return new Tinting(bitmap, Task.Run(() => Tint(layers, accent, buffer, stride)));
        }

        internal static Drawing ToDrawing(Size size, Tinting tinting)
        {
            var bitmap = tinting.Bitmap;
            bitmap.AddDirtyRect(new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
            bitmap.Unlock();
            bitmap.Freeze();
            var drawing = new ImageDrawing(bitmap, new Rect(0, 0, size.Width, size.Height));
            drawing.Freeze();
            return drawing;
        }

        /// <summary>Drops a tint a newer accent replaced (UI thread).</summary>
        internal static void Discard(Tinting tinting) => tinting.Bitmap.Unlock();

        /// <summary>
        /// The disk cache's format version (MotifDiskCache). A plain constant, not a hash: bump it whenever
        /// TrashPolkaInk.cs changes what it draws (marks, seeds, opacities, Scale) or <see cref="Build"/> changes the
        /// layer math, or old pictures keep loading. Writing a new version deletes the old one.
        /// </summary>
        internal const string CacheVersion = "trashpolka-1";

        private static string CacheKey(int seed, int w, int h, double scale, double dpi) => string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"s{seed}_{w}x{h}_x{scale:0.####}_d{dpi:0.####}");

        private static Layers Build(int seed, Size size, double scale, double dpi, out Action? save, bool probeCache = true)
        {
            var (w, h) = PixelSize(size, scale, dpi);
            save = null;
            string key = CacheKey(seed, w, h, scale, dpi);
            if (probeCache && MotifDiskCache.TryRead(CacheVersion, key, "ink", w, h, 4) is { } cachedInk
                && MotifDiskCache.TryRead(CacheVersion, key, "coverage", w, h, 1) is { } cachedCoverage)
            {
                CacheHits++;
                return Pack(cachedInk, cachedCoverage, w, h, dpi);
            }
            // One render target serves both accents. The black render is copied out whole and becomes the ink in place;
            // the white render is read back one row at a time.
            var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            Render(target, Colors.Black, seed, size, scale * dpi);
            var ink = new byte[w * h * 4];
            target.CopyPixels(ink, w * 4, 0);
            target.Clear();
            Render(target, Colors.White, seed, size, scale * dpi);
            var coverage = new byte[w * h];
            var light = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                target.CopyPixels(new Int32Rect(0, y, w, 1), light, w * 4, 0);
                for (int x = 0, i = y * w * 4; x < w * 4; x += 4, i += 4)
                {
                    int m = light[x + 1] - ink[i + 1];
                    if (m < 0) m = 0;
                    if (m >= 255)
                    {
                        coverage[i / 4] = 255;
                        ink[i] = ink[i + 1] = ink[i + 2] = ink[i + 3] = 0;
                        continue;
                    }
                    coverage[i / 4] = (byte)m;
                    double k = 255.0 / (255 - m);
                    int alpha = Math.Min(255, (int)Math.Round((ink[i + 3] - m) * k));
                    if (alpha < 0) alpha = 0;
                    for (int c = 0; c < 3; c++)
                        ink[i + c] = (byte)Math.Min(alpha, (int)Math.Round(ink[i + c] * k));
                    ink[i + 3] = (byte)alpha;
                }
            }
            target = null;
            light = null;
            CacheMisses++;
            if (MotifDiskCache.Root is not null)
                save = () =>
                {
                    MotifDiskCache.Write(CacheVersion, key, "ink", ink, w, h, 4);
                    MotifDiskCache.Write(CacheVersion, key, "coverage", coverage, w, h, 1);
                };
            return Pack(ink, coverage, w, h, dpi);
        }

        // The ink as runs of non-transparent pixels, each run inside one row.
        private static Layers Pack(byte[] ink, byte[] coverage, int w, int h, double dpi)
        {
            var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(ink);
            int runCount = 0, valueCount = 0;
            for (int y = 0; y < h; y++)
            {
                bool inRun = false;
                for (int x = 0, i = y * w; x < w; x++, i++)
                {
                    bool set = pixels[i] != 0;
                    if (set) valueCount++;
                    if (set && !inRun) runCount++;
                    inRun = set;
                }
            }
            var runs = new int[runCount * 2];
            var values = new uint[valueCount];
            int r = 0, v = 0;
            for (int y = 0; y < h; y++)
            {
                int start = -1;
                for (int x = 0, i = y * w; x <= w; x++, i++)
                {
                    bool set = x < w && pixels[i] != 0;
                    if (set)
                    {
                        if (start < 0) start = i;
                        values[v++] = pixels[i];
                    }
                    else if (start >= 0)
                    {
                        runs[r++] = start;
                        runs[r++] = i - start;
                        start = -1;
                    }
                }
            }
            return new Layers(runs, values, coverage, w, h, dpi);
        }

        private static void Render(RenderTargetBitmap target, Color accent, int seed, Size size, double zoom)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(zoom, zoom));
                dc.DrawDrawing(TrashPolkaInk.DrawFrozen(accent, size, seed));
                dc.Pop();
            }
            target.Render(visual);
        }

        // The tint laid over the ink, premultiplied: out = tint + ink * (1 - m), written one row at a time.
        private static void Tint(Layers layers, Color accent, IntPtr buffer, int stride)
        {
            int w = layers.Width;
            var row = new byte[w * 4];
            var rowPixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(row.AsSpan());
            var runs = layers.Runs;
            var values = layers.InkValues;
            int r = 0, v = 0;
            for (int y = 0; y < layers.Height; y++)
            {
                rowPixels.Clear();
                int rowStart = y * w, rowEnd = rowStart + w;
                while (r < runs.Length && runs[r] < rowEnd)
                {
                    int start = runs[r] - rowStart, count = runs[r + 1];
                    values.AsSpan(v, count).CopyTo(rowPixels.Slice(start, count));
                    v += count;
                    r += 2;
                }
                for (int x = 0, j = 0, i = rowStart; x < w; x++, j += 4, i++)
                {
                    int m = layers.Coverage[i];
                    if (m == 0) continue;
                    int keep = 255 - m;
                    row[j] = (byte)((accent.B * m + row[j] * keep) / 255);
                    row[j + 1] = (byte)((accent.G * m + row[j + 1] * keep) / 255);
                    row[j + 2] = (byte)((accent.R * m + row[j + 2] * keep) / 255);
                    row[j + 3] = (byte)((255 * m + row[j + 3] * keep) / 255);
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, buffer + y * stride, w * 4);
            }
        }
    }
}
