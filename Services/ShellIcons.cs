using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerPDF.Services
{
    /// <summary>
    /// Shell icons for the file dialog, via SHGetFileInfo - a glyph font cannot distinguish a .pdf
    /// from a .zip, and these are the icons already familiar from Explorer.
    ///
    /// SHGFI_USEFILEATTRIBUTES is the important flag: it resolves the icon from the NAME and a
    /// synthetic attribute set, so nothing is read off disk and a path on a sleeping network share
    /// cannot stall the list. The trade-off is that per-file icons (an .exe's own icon, a .lnk's
    /// target, a custom folder icon) come back generic - correct for a file list, and worth it to
    /// keep scrolling responsive.
    ///
    /// Icons are cached by extension, so ten thousand .txt rows share one HICON conversion. Every
    /// HICON is destroyed after conversion: they are a limited USER handle and leaking them
    /// eventually takes the whole desktop down, not just this app.
    /// </summary>
    internal static class ShellIcons
    {
        private const uint SHGFI_ICON              = 0x000000100;
        private const uint SHGFI_SMALLICON         = 0x000000001;
        private const uint SHGFI_LARGEICON         = 0x000000000;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

        private const uint FILE_ATTRIBUTE_NORMAL    = 0x00000080;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int    iIcon;
            public uint   dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]  public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
                                                   ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        // key: extension (lowercase) or the two folder sentinels, per size
        private static readonly Dictionary<string, ImageSource?> _small = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ImageSource?> _large = new(StringComparer.OrdinalIgnoreCase);

        private const string FolderKey = " dir";

        /// <summary>16px icon for a list row. Null if the shell has nothing (caller falls back).</summary>
        internal static ImageSource? Small(string path, bool isFolder) => Get(path, isFolder, true);

        /// <summary>32px icon for the icon grid.</summary>
        internal static ImageSource? Large(string path, bool isFolder) => Get(path, isFolder, false);

        private static ImageSource? _killerPdfDocument;
        private static bool _killerPdfDocumentRead;

        /// <summary>
        /// KillerPDF's own document icon, the pdf-file.ico the installer registers for PDFs, so a list of open
        /// documents shows the app's page rather than whatever the default PDF app's icon is. The 32px frame is
        /// kept: drawn at 18 DIPs it stays sharp up to about 175% scaling. Null in a build without the icon.
        /// </summary>
        internal static ImageSource? KillerPdfDocument()
        {
            lock (CacheGate)
            {
                if (_killerPdfDocumentRead) return _killerPdfDocument;
                _killerPdfDocumentRead = true;
                try
                {
                    var assembly = typeof(ShellIcons).Assembly;
                    string? name = Array.Find(assembly.GetManifestResourceNames(),
                        n => n.Contains("pdf-file", StringComparison.OrdinalIgnoreCase)
                             && n.EndsWith(".ico", StringComparison.OrdinalIgnoreCase));
                    if (name is null) return null;
                    using Stream stream = assembly.GetManifestResourceStream(name)!;
                    var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    BitmapFrame? best = null;
                    foreach (BitmapFrame frame in decoder.Frames)
                        if (best is null || Math.Abs(frame.PixelWidth - 32) < Math.Abs(best.PixelWidth - 32)
                            || frame.PixelWidth == best.PixelWidth && frame.Format.BitsPerPixel > best.Format.BitsPerPixel)
                            best = frame;
                    best?.Freeze();
                    _killerPdfDocument = best;
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException
                    or InvalidOperationException or ArgumentException)
                {
                    _killerPdfDocument = null;
                }
                return _killerPdfDocument;
            }
        }

        /// <summary>
        /// The icon for an open document's menu row: KillerPDF's own for a PDF or an untitled document, and the
        /// shell's for a file of another kind the app opened (EPUB, DjVu, Office).
        /// </summary>
        internal static ImageSource? DocumentIcon(string? path) =>
            string.IsNullOrEmpty(path) || string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase)
                ? KillerPdfDocument()
                : Large(path, isFolder: false) ?? KillerPdfDocument();

        /// <summary>
        /// A file icon for a menu's icon column, drawn as the Recent files rows draw theirs: 18 DIPs, smooth
        /// scaling and the theme's icon shadow. Null when there is no icon, so the caller keeps its glyph.
        /// </summary>
        internal static System.Windows.Controls.Image? MenuIcon(ImageSource? source, FrameworkElement themeOwner)
        {
            if (source is null) return null;
            var image = new System.Windows.Controls.Image
            {
                Source = source,
                Width = 18,
                Height = 18,
                VerticalAlignment = VerticalAlignment.Center,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            image.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 4, ShadowDepth = 2, Direction = 270,
                Opacity = themeOwner.TryFindResource("IconShadowOpacity") is double opacity ? opacity : 0.5
            };
            return image;
        }

        private static readonly object CacheGate = new();
        private const int ThumbnailLimit = 256;
        private static readonly Dictionary<string, (ImageSource? Image, LinkedListNode<string> Node)> _thumbs = new(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<string> ThumbnailOrder = new();

        private static readonly HashSet<string> ThumbExts = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico" };

        /// <summary>True when the file is a raster image we can render a real thumbnail for.</summary>
        internal static bool CanThumbnail(string path) =>
            !string.IsNullOrEmpty(path) && ThumbExts.Contains(Path.GetExtension(path));

        /// <summary>
        /// A real thumbnail of an image file, or null when it is not an image / cannot be decoded.
        /// Picking an image from a list of generic type icons is guesswork, which is what this
        /// removes.
        ///
        /// DecodePixelWidth is what makes it affordable: WPF decodes at the requested size instead
        /// of loading a 40-megapixel photo into memory to draw it 32px wide. OnLoad caching closes
        /// the file handle immediately - without it the picker keeps every previewed file locked,
        /// and the user cannot rename or delete what they just looked at. Frozen and cached by
        /// path so scrolling a folder decodes each file once.
        /// </summary>
        internal static ImageSource? Thumbnail(string path, int px)
        {
            ImageSource? img = null;
            try
            {
                if (!CanThumbnail(path)) return null;
                var file = new FileInfo(path);
                if (!file.Exists) return null;
                string key = path + "|" + px + "|" + file.LastWriteTimeUtc.Ticks + "|" + file.Length;
                lock (CacheGate)
                {
                    if (_thumbs.TryGetValue(key, out var hit))
                    {
                        ThumbnailOrder.Remove(hit.Node);
                        ThumbnailOrder.AddLast(hit.Node);
                        return hit.Image;
                    }
                }
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(path);
                    bmp.DecodePixelWidth = px;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;
                    bmp.EndInit();
                    bmp.Freeze();
                    img = bmp;
                }
                lock (CacheGate)
                {
                    if (_thumbs.ContainsKey(key)) return _thumbs[key].Image;
                    _thumbs[key] = (img, ThumbnailOrder.AddLast(key));
                    while (_thumbs.Count > ThumbnailLimit && ThumbnailOrder.First is { } first)
                    {
                        _thumbs.Remove(first.Value);
                        ThumbnailOrder.RemoveFirst();
                    }
                }
            }
            catch { img = null; }   // unreadable, corrupt, or not really an image - fall back to the type icon
            return img;
        }

        // key: full path - the places rail is a dozen entries, so per-path caching is cheap
        private static readonly Dictionary<string, ImageSource?> _places = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 16px icon for a REAL path - the places rail. Deliberately NOT SHGFI_USEFILEATTRIBUTES:
        /// the rail wants a drive's true icon (USB, network, optical) and a special folder's own,
        /// which only a real-path query returns. That touches the disk, so it stays off the
        /// per-row paths above and is cached by path.
        /// </summary>
        internal static ImageSource? Place(string path)
        {
            lock (CacheGate) if (_places.TryGetValue(path, out var hit)) return hit;

            ImageSource? img = null;
            var info = new SHFILEINFO();
            IntPtr res = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info),
                                       SHGFI_ICON | SHGFI_SMALLICON);
            if (res != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                try
                {
                    img = Imaging.CreateBitmapSourceFromHIcon(
                        info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    img.Freeze();
                }
                catch { img = null; }
                finally { DestroyIcon(info.hIcon); }
            }

            lock (CacheGate) _places[path] = img;
            return img;
        }

        private static ImageSource? Get(string path, bool isFolder, bool small)
        {
            string key = isFolder ? FolderKey : ExtOf(path);
            var cache = small ? _small : _large;
            lock (CacheGate) if (cache.TryGetValue(key, out var hit)) return hit;

            var img = Load(isFolder ? "dir" : "x" + key, isFolder, small);
            if (img != null) img.Freeze();          // shared across threads and rows
            lock (CacheGate) cache[key] = img;
            return img;
        }

        /// <summary>
        /// Extension including the dot, lowercased. Extensionless files all share one key - the
        /// shell gives them the same generic icon anyway.
        /// </summary>
        private static string ExtOf(string path)
        {
            var ext = Path.GetExtension(path);
            return string.IsNullOrEmpty(ext) ? " noext" : ext.ToLowerInvariant();
        }

        private static BitmapSource? Load(string fakeName, bool isFolder, bool small)
        {
            var info = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | (small ? SHGFI_SMALLICON : SHGFI_LARGEICON);
            uint attrs = isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

            IntPtr res = SHGetFileInfo(fakeName, attrs, ref info, (uint)Marshal.SizeOf(info), flags);
            if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

            try
            {
                return Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            catch { return null; }
            finally { DestroyIcon(info.hIcon); }
        }
    }
}
