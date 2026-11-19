using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerPDF.Controls
{
    /// <summary>
    /// A disk cache for built motif pictures: exact pixel arrays saved as lossless PNGs under
    /// %LocalAppData%\&lt;app&gt;\Motif\&lt;version&gt;\&lt;key&gt;.&lt;layer&gt;.png. The app folder is the kit's
    /// <see cref="DialogChrome.AppName"/>; with no name set the cache stays off.
    ///
    /// The bytes are stored verbatim: a four-byte layer goes in as Bgra32 and a one-byte layer as Gray8, so a
    /// premultiplied picture is never converted and decodes back bit for bit. Every write goes to a unique temp file in
    /// the same folder and is then renamed, so a reader never sees half a file and two app instances writing the same
    /// key just race to an identical result. A file that is missing, will not decode, or has the wrong size is a miss,
    /// and the caller renders instead. Writing a version deletes every other version's folder.
    /// </summary>
    internal static class MotifDiskCache
    {
        private static string? _root;
        private static bool _rootSet;

        /// <summary>The folder that holds the version folders, or null when the cache is off. Tests point it at a temp
        /// folder (or null) before any motif paints.</summary>
        internal static string? Root
        {
            get
            {
                if (_rootSet) return _root;
                string app = DialogChrome.AppName;
                return string.IsNullOrWhiteSpace(app) ? null
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), app, "Motif");
            }
            set { _root = value; _rootSet = true; }
        }

        private static string? Folder(string version) => Root is { } root ? Path.Combine(root, version) : null;

        internal static bool Exists(string version, string key, string layer)
            => Folder(version) is { } folder && File.Exists(Path.Combine(folder, $"{key}.{layer}.png"));

        /// <summary>Reads one layer into a new array of width*height*bytesPerPixel, or null on any miss.</summary>
        internal static byte[]? TryRead(string version, string key, string layer, int width, int height, int bytesPerPixel)
        {
            try
            {
                if (Folder(version) is not { } folder) return null;
                string path = Path.Combine(folder, $"{key}.{layer}.png");
                if (!File.Exists(path)) return null;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                var expected = bytesPerPixel == 4 ? PixelFormats.Bgra32 : PixelFormats.Gray8;
                if (frame.PixelWidth != width || frame.PixelHeight != height || frame.Format != expected) return null;
                var pixels = new byte[width * height * bytesPerPixel];
                frame.CopyPixels(pixels, width * bytesPerPixel, 0);
                return pixels;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Saves one layer; failures are ignored (the next run renders again).</summary>
        internal static void Write(string version, string key, string layer, byte[] pixels, int width, int height, int bytesPerPixel)
        {
            string? temp = null;
            try
            {
                if (Folder(version) is not { } folder) return;
                Directory.CreateDirectory(folder);
                PruneOtherVersions(version);
                string path = Path.Combine(folder, $"{key}.{layer}.png");
                temp = Path.Combine(folder, $"{key}.{layer}.{Guid.NewGuid():N}.tmp");
                var format = bytesPerPixel == 4 ? PixelFormats.Bgra32 : PixelFormats.Gray8;
                var source = BitmapSource.Create(width, height, 96, 96, format, null, pixels, width * bytesPerPixel);
                var encoder = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
                encoder.Frames.Add(BitmapFrame.Create(source));
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    encoder.Save(file);
                    file.Flush(flushToDisk: true);
                }
                // Another instance may have landed the same key first: its file holds the same pixels, so keep it.
                try { File.Move(temp, path, overwrite: false); temp = null; }
                catch (IOException) when (File.Exists(path)) { }
            }
            catch (Exception) { }
            finally
            {
                if (temp is not null) try { File.Delete(temp); } catch (Exception) { }
            }
        }

        private static void PruneOtherVersions(string version)
        {
            if (Root is not { } root) return;
            foreach (var dir in Directory.EnumerateDirectories(root))
                if (!string.Equals(Path.GetFileName(dir), version, StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
        }
    }
}
