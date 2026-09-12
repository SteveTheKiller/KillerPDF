using System.Text.Json;

namespace KillerPDF.Services
{
    /// <summary>
    /// One reusable image in the shared library (#326): PNG bytes plus a display name.
    /// Unlike signatures (strokes or per-kind graphics), assets are plain images any tool
    /// - signature picker, stamp watermark, future comments - can consume in its own way.
    /// </summary>
    internal sealed record ImageAsset(
        string Id,
        string Name,
        string ImageData,
        int Width,
        int Height,
        DateTime CreatedUtc);

    /// <summary>
    /// File-backed image asset library (imagelibrary.json next to signatures.json).
    /// Mirrors SignatureStore's crash-safe shape: corrupt files reset to empty, writes are
    /// best-effort, and the (dir, file) ctor exists so tests can point at a temp folder.
    /// </summary>
    internal sealed class ImageAssetLibrary
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private readonly string _dir;
        private readonly string _file;

        public ImageAssetLibrary()
            : this(AppDataPaths.UserRoot, AppDataPaths.ImageLibraryFile) { }

        internal ImageAssetLibrary(string dir, string file)
        {
            _dir = dir;
            _file = file;
        }

        private List<ImageAsset> _items = [];

        public IReadOnlyList<ImageAsset> Assets => _items;

        public void Load()
        {
            try
            {
                if (System.IO.File.Exists(_file))
                {
                    var json = System.IO.File.ReadAllText(_file);
                    _items = JsonSerializer.Deserialize<List<ImageAsset>>(json)
                        ?.Where(IsValid).ToList() ?? [];
                }
            }
            catch { _items = []; }
        }

        public void Persist()
        {
            try
            {
                System.IO.Directory.CreateDirectory(_dir);
                var json = JsonSerializer.Serialize(_items, JsonOptions);
                System.IO.File.WriteAllText(_file, json);
            }
            catch { /* best effort */ }
        }

        public ImageAsset Add(string name, string imageData, int width, int height)
        {
            var asset = new ImageAsset(
                Guid.NewGuid().ToString("N"),
                UniqueName(name),
                imageData, Math.Max(1, width), Math.Max(1, height),
                DateTime.UtcNow);
            _items.Add(asset);
            return asset;
        }

        public void Remove(ImageAsset asset) => _items.Remove(asset);

        public string UniqueName(string stem)
        {
            stem = string.IsNullOrWhiteSpace(stem) ? "Image" : stem.Trim();
            if (_items.All(a => a.Name != stem)) return stem;
            int n = 2;
            while (_items.Any(a => a.Name == $"{stem} ({n})")) n++;
            return $"{stem} ({n})";
        }

        private static bool IsValid(ImageAsset asset) =>
            !string.IsNullOrWhiteSpace(asset.Id)
            && !string.IsNullOrWhiteSpace(asset.Name)
            && !string.IsNullOrEmpty(asset.ImageData);
    }
}
