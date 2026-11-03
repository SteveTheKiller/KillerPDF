using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;

namespace KillerPDF.Controls
{
    // Row and place models for FileDialog.
    public sealed class PickerPlace(string label, string path, bool pinned = false,
                                    System.Windows.Media.ImageSource? icon = null) : INotifyPropertyChanged
    {
        public string Label { get; } = label;
        public string Path  { get; } = path;

        public override string ToString() => Label;

        /// <summary>True for a user-pinned (removable) entry; drives are dynamic and never pinned.</summary>
        public bool Pinned { get; } = pinned;

        private ImageSource? _icon = icon;
        private bool _iconRequested = icon != null;
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>The real path icon arrives asynchronously after the generic folder icon.</summary>
        public ImageSource? Icon
        {
            get
            {
                if (!_iconRequested)
                {
                    _iconRequested = true;
                    LoadIcon(Dispatcher.CurrentDispatcher);
                }
                return _icon ?? Services.ShellIcons.Small(Path, true);
            }
        }

        private async void LoadIcon(Dispatcher dispatcher)
        {
            var image = await Task.Run(() => Services.ShellIcons.Place(Path)).ConfigureAwait(false);
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            await dispatcher.InvokeAsync(() =>
            {
                _icon = image;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            });
        }
    }

    // One row in the folder pane: a subfolder or a (dimmed, non-pickable) file.
    public sealed class PickerEntry(string name, string fullPath, bool isFolder, long sizeBytes, DateTime modified) : INotifyPropertyChanged
    {
        private static readonly string GlyphFolder = ((char)0xE8B7).ToString();
        private static readonly string GlyphFile   = ((char)0xE8A5).ToString();

        public string   Name      { get; } = name;
        public string   FullPath  { get; } = fullPath;
        public bool     IsFolder  { get; } = isFolder;
        public long     SizeBytes { get; } = sizeBytes;
        public DateTime Modified  { get; } = modified;

        public override string ToString() => Name;

        public string Glyph         => IsFolder ? GlyphFolder : GlyphFile;

        /// <summary>Shell icon, 16px, for the list and details rows. Cached by extension, so
        /// binding it per row is cheap.</summary>
        public System.Windows.Media.ImageSource? Icon
            => Services.ShellIcons.Small(FullPath, IsFolder);

        /// <summary>Shell icon, 32px, for the icon grid - or a real thumbnail when the file is an
        /// image, so a folder of pictures does not read as a grid of identical type icons.</summary>
        private ImageSource? _thumbnail;
        private bool _thumbnailRequested;
        public event PropertyChangedEventHandler? PropertyChanged;

        public ImageSource? IconLarge
        {
            get
            {
                if (ShowThumbnails && !IsFolder && !_thumbnailRequested && Services.ShellIcons.CanThumbnail(FullPath))
                {
                    _thumbnailRequested = true;
                    LoadThumbnail(Dispatcher.CurrentDispatcher);
                }
                return (ShowThumbnails ? _thumbnail : null) ?? Services.ShellIcons.Large(FullPath, IsFolder);
            }
        }

        private async void LoadThumbnail(Dispatcher dispatcher)
        {
            var image = await Task.Run(() => Services.ShellIcons.Thumbnail(FullPath, 32)).ConfigureAwait(false);
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            await dispatcher.InvokeAsync(() =>
            {
                _thumbnail = image;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconLarge)));
            });
        }

        /// <summary>Image thumbnails in the icon grid. On by default; an app that keeps plain
        /// shell icons for pictures sets this false once at startup.</summary>
        public static bool ShowThumbnails { get; set; } = true;

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
