using System;

namespace KillerPDF.Controls
{
    // Row and place models for FileDialog.
    public sealed class PickerPlace(string label, string path, bool pinned = false)
    {
        public string Label { get; } = label;
        public string Path  { get; } = path;

        /// <summary>True for a user-pinned (removable) entry; drives are dynamic and never pinned.</summary>
        public bool Pinned { get; } = pinned;

        /// <summary>Real shell icon, resolved by PATH - a drive shows its true icon (USB,
        /// network, optical) and a special folder its own. Cached in ShellIcons.</summary>
        public System.Windows.Media.ImageSource? Icon => Services.ShellIcons.Place(Path);
    }

    // One row in the folder pane: a subfolder or a (dimmed, non-pickable) file.
    public sealed class PickerEntry(string name, string fullPath, bool isFolder, long sizeBytes, DateTime modified)
    {
        private static readonly string GlyphFolder = ((char)0xE8B7).ToString();
        private static readonly string GlyphFile   = ((char)0xE8A5).ToString();

        public string   Name      { get; } = name;
        public string   FullPath  { get; } = fullPath;
        public bool     IsFolder  { get; } = isFolder;
        public long     SizeBytes { get; } = sizeBytes;
        public DateTime Modified  { get; } = modified;

        public string Glyph         => IsFolder ? GlyphFolder : GlyphFile;

        /// <summary>Shell icon, 16px, for the list and details rows. Cached by extension, so
        /// binding it per row is cheap.</summary>
        public System.Windows.Media.ImageSource? Icon
            => Services.ShellIcons.Small(FullPath, IsFolder);

        /// <summary>Shell icon, 32px, for the icon grid.</summary>
        public System.Windows.Media.ImageSource? IconLarge
            => Services.ShellIcons.Large(FullPath, IsFolder);

        public string SizeLabel     => IsFolder ? string.Empty : FormatSize(SizeBytes);
        public string ModifiedLabel => Modified == DateTime.MinValue ? string.Empty : Modified.ToString("yyyy-MM-dd HH:mm");

        private static string FormatSize(long b)
        {
            if (b < 1024) return b + " B";
            double kb = b / 1024.0;
            if (kb < 1024) return kb.ToString("0") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.0") + " MB";
            return (mb / 1024.0).ToString("0.00") + " GB";
        }
    }
}
