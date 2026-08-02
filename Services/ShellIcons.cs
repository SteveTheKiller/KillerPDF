using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerPDF.Services
{
    // ============================================================
    // File-type (shell) icons - split out of FileOperations.cs
    // (KillerUI refactor). Same name as the KillerUI kit's
    // Services/ShellIcons.cs, which the family file picker uses, so
    // the picker rollout lands on a familiar shape.
    //
    // Cached per extension. Uses SHGFI_USEFILEATTRIBUTES so the
    // icon resolves from the extension alone - works even when the
    // file is missing, and never touches the file on disk.
    // ============================================================
    internal static class ShellIcons
    {
        private static readonly Dictionary<string, ImageSource?> _shellIconCache = new(System.StringComparer.OrdinalIgnoreCase);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]  public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        internal static ImageSource? GetShellIcon(string path)
        {
            string ext = System.IO.Path.GetExtension(path) ?? "";
            if (_shellIconCache.TryGetValue(ext, out var hit)) return hit;

            const uint SHGFI_ICON = 0x000000100, SHGFI_LARGEICON = 0x000000000, SHGFI_USEFILEATTRIBUTES = 0x000000010;
            const uint FILE_ATTRIBUTE_NORMAL = 0x80;
            ImageSource? src = null;
            try
            {
                var info = new SHFILEINFO();
                IntPtr res = SHGetFileInfo("file" + ext, FILE_ATTRIBUTE_NORMAL, ref info,
                    (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
                if (res != IntPtr.Zero && info.hIcon != IntPtr.Zero)
                {
                    src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    DestroyIcon(info.hIcon);
                }
            }
            catch { /* no icon available - the row simply shows none */ }
            _shellIconCache[ext] = src;
            return src;
        }

        // ── File picker (Controls/FileDialog.xaml) ────────────────────────────────────────────
        // Ported with the picker from Killendar, reusing the interop above rather than declaring
        // a second copy of SHFILEINFO / SHGetFileInfo / DestroyIcon.
        //
        // Icons are cached by EXTENSION, so ten thousand .txt rows share one HICON conversion, and
        // every HICON is destroyed after conversion - they are a limited USER handle, and leaking
        // them eventually takes the whole desktop down, not just this app.

        private const uint SHGFI_ICON_F              = 0x000000100;
        private const uint SHGFI_SMALLICON_F         = 0x000000001;
        private const uint SHGFI_LARGEICON_F         = 0x000000000;
        private const uint SHGFI_USEFILEATTRIBUTES_F = 0x000000010;
        private const uint FILE_ATTRIBUTE_NORMAL_F    = 0x00000080;
        private const uint FILE_ATTRIBUTE_DIRECTORY_F = 0x00000010;

        private static readonly Dictionary<string, ImageSource?> _small  = new(System.StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ImageSource?> _large  = new(System.StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ImageSource?> _places = new(System.StringComparer.OrdinalIgnoreCase);

        private const string FolderKey = " dir";

        /// <summary>16px icon for a list row. Null when the shell has nothing (caller falls back).</summary>
        internal static ImageSource? Small(string path, bool isFolder) => GetSized(path, isFolder, true);

        /// <summary>32px icon for the icon grid.</summary>
        internal static ImageSource? Large(string path, bool isFolder) => GetSized(path, isFolder, false);

        /// <summary>
        /// 16px icon for a REAL path - the places rail. Deliberately NOT SHGFI_USEFILEATTRIBUTES:
        /// the rail wants a drive's true icon (USB, network, optical) and a special folder's own,
        /// which only a real-path query returns. That touches the disk, so it stays off the
        /// per-row paths above and is cached by path.
        /// </summary>
        internal static ImageSource? Place(string path)
        {
            if (_places.TryGetValue(path, out var hit)) return hit;
            ImageSource? img = null;
            try
            {
                var info = new SHFILEINFO();
                IntPtr res = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                                           SHGFI_ICON_F | SHGFI_SMALLICON_F);
                if (res != IntPtr.Zero && info.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        img = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        img.Freeze();
                    }
                    catch { img = null; }
                    finally { DestroyIcon(info.hIcon); }
                }
            }
            catch { img = null; }
            _places[path] = img;
            return img;
        }

        private static ImageSource? GetSized(string path, bool isFolder, bool small)
        {
            string key = isFolder ? FolderKey : ExtKey(path);
            var cache = small ? _small : _large;
            if (cache.TryGetValue(key, out var hit)) return hit;
            var img = LoadSized(isFolder ? "dir" : "x" + key, isFolder, small);
            img?.Freeze();   // shared across threads and rows
            cache[key] = img;
            return img;
        }

        /// <summary>Extension including the dot, lowercased. Extensionless files share one key -
        /// the shell gives them the same generic icon anyway.</summary>
        private static string ExtKey(string path)
        {
            var ext = System.IO.Path.GetExtension(path);
            return string.IsNullOrEmpty(ext) ? " noext" : ext.ToLowerInvariant();
        }

        private static BitmapSource? LoadSized(string fakeName, bool isFolder, bool small)
        {
            try
            {
                var info = new SHFILEINFO();
                uint flags = SHGFI_ICON_F | SHGFI_USEFILEATTRIBUTES_F | (small ? SHGFI_SMALLICON_F : SHGFI_LARGEICON_F);
                uint attrs = isFolder ? FILE_ATTRIBUTE_DIRECTORY_F : FILE_ATTRIBUTE_NORMAL_F;
                IntPtr res = SHGetFileInfo(fakeName, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
                if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
                try
                {
                    return Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                }
                catch { return null; }
                finally { DestroyIcon(info.hIcon); }
            }
            catch { return null; }
        }
    }
}
