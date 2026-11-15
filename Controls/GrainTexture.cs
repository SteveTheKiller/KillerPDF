using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerPDF.Controls
{
    /// <summary>
    /// The procedural film grain the whole app is textured with. One tile, shared by every grain
    /// brush, so the pattern lines up across the title bar, the panes and the menus instead of each
    /// surface getting its own noise.
    ///
    /// Every pixel is a speck of a continuous gray shade with distinct dark and bright highlights,
    /// lightly blended with its neighbours for a fine sandpaper texture. A share of the specks
    /// leans toward the theme's secondary color: the complement of the accent hue (a green accent
    /// leans purple, a blue one leans orange-brown, and reds lean orange-brown too instead of
    /// blue), or of the background hue when the accent has no hue of its own. The tile is
    /// regenerated when the theme or accent changes. The seed is fixed so a rebuild produces the
    /// identical pattern.
    /// </summary>
    internal static class GrainTexture
    {
        private const int Size = 256;
        private const int Seed = 1337;
        private const byte Alpha = 70;
        private const double ShadeSpread = 150;  // standard deviation of a speck's gray around 128
        private const double Soften = 0.10;      // share of each speck taken from its four neighbours
        private const double Contrast = 1.35;    // keeps dark and bright specks distinct after blending
        private const double LeanShare = 0.40;   // share of specks that lean toward the secondary color
        private const double LeanMix = 0.80;     // how far a leaning speck moves toward it

        private static readonly byte[] Shades = new byte[Size * Size];
        private static readonly bool[] Leaning = new bool[Size * Size];
        private static readonly Dictionary<int, BitmapSource> Cache = new();
        private static readonly Queue<int> CacheOrder = new();
        private const int CacheLimit = 12;

        internal static int CacheCount => Cache.Count;

        /// <summary>The tile for a theme and accent, generated once. Accent picking can visit every
        /// color, so only the most recent few tiles stay.</summary>
        internal static BitmapSource Cached(int key, Func<BitmapSource> make)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit;
            var bmp = make();
            while (Cache.Count >= CacheLimit && CacheOrder.Count > 0) Cache.Remove(CacheOrder.Dequeue());
            Cache[key] = bmp;
            CacheOrder.Enqueue(key);
            return bmp;
        }
        // Every tracked brush binds its ImageSource to this one. A bound brush cannot be frozen, so a
        // style or template that seals it (a StaticResource grain layer, KillerPDF) leaves it live, and
        // a theme change repaints it instead of throwing on a frozen brush.
        private static readonly ImageBrush TileSource = new();
        private static BitmapSource? _current;
        private static int _currentKey = int.MinValue;
        private static bool _hooked;

        static GrainTexture()
        {
            var rng = new Random(Seed);
            var raw = new double[Size * Size];
            for (int i = 0; i < raw.Length; i++)
            {
                double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
                double gauss = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
                raw[i] = Clamp(128 + ShadeSpread * gauss);
                Leaning[i] = rng.NextDouble() < LeanShare;
            }

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    // Wrapping neighbours keep the tile seamless.
                    double around = (raw[((y + Size - 1) % Size) * Size + x] + raw[((y + 1) % Size) * Size + x]
                                   + raw[y * Size + (x + Size - 1) % Size] + raw[y * Size + (x + 1) % Size]) / 4;
                    double soft = raw[y * Size + x] * (1 - Soften) + around * Soften;
                    Shades[y * Size + x] = (byte)Math.Round(Clamp(128 + (soft - 128) * Contrast));
                }
            }
        }

        /// <summary>Generates the tile, leaning toward <paramref name="lean"/> when given. Frozen.</summary>
        internal static BitmapSource Generate(Color? lean, bool highlightsOnly = false)
        {
            var pixels = new byte[Size * Size * 4];
            for (int i = 0; i < Shades.Length; i++)
            {
                double shade = Shades[i], r = shade, g = shade, b = shade;
                if (lean is Color c && Leaning[i])
                {
                    // The leaning speck keeps its own lightness, so it tints without brightening.
                    double k = 0.35 + 1.1 * shade / 255.0;
                    r = shade * (1 - LeanMix) + Clamp(c.R * k) * LeanMix;
                    g = shade * (1 - LeanMix) + Clamp(c.G * k) * LeanMix;
                    b = shade * (1 - LeanMix) + Clamp(c.B * k) * LeanMix;
                }
                int p = i * 4;
                pixels[p] = (byte)Math.Round(b);
                pixels[p + 1] = (byte)Math.Round(g);
                pixels[p + 2] = (byte)Math.Round(r);
                // Fade midtones on near-black surfaces without dimming the brightest specks.
                pixels[p + 3] = highlightsOnly ? (byte)Math.Round(Alpha * Math.Pow(shade / 255.0, 6)) : Alpha;
            }

            var bmp = new WriteableBitmap(Size, Size, 96, 96, PixelFormats.Bgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, Size, Size), pixels, Size * 4, 0);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>
        /// The secondary color the grain leans toward: the complement of the accent hue, or of the
        /// background hue when the accent is a neutral (white, gray). Null when neither has a hue.
        /// </summary>
        internal static Color? LeanFor(Color accent, Color background)
        {
            if (!TryHue(accent, out double hue) && !TryHue(background, out hue)) return null;
            // Reds and peaches lean orange-brown like the blues, not to the blue of their complement.
            if (hue >= 330 || hue < 31) return FromHsv(25, 0.55, 0.85);
            hue = (hue + 180) % 360;
            // The plain complement of a green lands on pink; the family leans greens to purple.
            if (hue >= 300 && hue < 345) hue = 285;
            return FromHsv(hue, 0.55, 0.85);
        }

        /// <summary>
        /// Paints the tile into every named ImageBrush the window happens to declare (all optional)
        /// and keeps them current when the theme changes.
        /// </summary>
        internal static void Apply(FrameworkElement scope, params string[] brushNames)
        {
            Publish();
            foreach (var name in brushNames)
                if (scope.FindName(name) is ImageBrush ib) Track(ib);
        }

        /// <summary>Paints the tile into a brush and repaints it whenever the tile changes.</summary>
        internal static void Track(ImageBrush brush)
        {
            Publish();
            if (BindingOperations.IsDataBound(brush, ImageBrush.ImageSourceProperty)) return;
            BindingOperations.SetBinding(brush, ImageBrush.ImageSourceProperty,
                new Binding(nameof(ImageBrush.ImageSource)) { Source = TileSource, Mode = BindingMode.OneWay });
        }

        /// <summary>
        /// Generates the tile for the current theme and publishes it as the app-level GrainTileBrush.
        /// Call this from App.OnStartup too, so dialogs shown before any main window exists (install,
        /// uninstall, install repair) are textured like the rest of the app. Theme and accent changes
        /// republish it on their own.
        /// </summary>
        internal static BitmapSource Publish()
        {
            if (!_hooked)
            {
                _hooked = true;
                Services.ThemeManager.ThemeChanged += () => Publish();
            }

            var lean = CurrentLean();
            // Only a theme that asks (Black) gets bright specks alone. Defaulting to true gave every
            // theme light-only grain: Light turned white and the dark themes' surfaces ran together.
            bool highlightsOnly = Application.Current?.TryFindResource("GrainHighlightsOnly") is bool enabled && enabled;
            int key = lean is Color c ? (c.R << 16) | (c.G << 8) | c.B : -1;
            key = (key + 1) * 2 + (highlightsOnly ? 1 : 0);
            if (_current != null && key == _currentKey) return _current;

            var bmp = Cached(key, () => Generate(lean, highlightsOnly));
            _current = bmp;
            _currentKey = key;

            // The keyed resource brush is frozen, so its ImageSource cannot be set in place. Swap in
            // a fresh frozen brush instead; DynamicResource consumers re-resolve automatically.
            var tile = new ImageBrush(bmp)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, Size, Size),
                Stretch = Stretch.None
            };
            tile.Freeze();
            if (Application.Current != null) Application.Current.Resources["GrainTileBrush"] = tile;

            TileSource.ImageSource = bmp;
            return bmp;
        }

        private static Color? CurrentLean()
        {
            var app = Application.Current;
            if (app == null) return null;
            var accent = app.TryFindResource("PrimaryBrush") is SolidColorBrush a ? a.Color : Colors.Gray;
            var background = app.TryFindResource("BackgroundBrush") is SolidColorBrush b ? b.Color : Colors.Gray;
            return LeanFor(accent, background);
        }

        private static bool TryHue(Color c, out double hue)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            hue = 0;
            if (max < 0.08 || d / max < 0.25) return false;
            if (max == r) hue = 60 * (((g - b) / d) % 6);
            else if (max == g) hue = 60 * ((b - r) / d + 2);
            else hue = 60 * ((r - g) / d + 4);
            if (hue < 0) hue += 360;
            return true;
        }

        private static Color FromHsv(double hue, double saturation, double value)
        {
            double c = value * saturation, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = value - c;
            double r, g, b;
            if (hue < 60) { r = c; g = x; b = 0; }
            else if (hue < 120) { r = x; g = c; b = 0; }
            else if (hue < 180) { r = 0; g = c; b = x; }
            else if (hue < 240) { r = 0; g = x; b = c; }
            else if (hue < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        private static double Clamp(double v) => v < 0 ? 0 : v > 255 ? 255 : v;
    }
}
