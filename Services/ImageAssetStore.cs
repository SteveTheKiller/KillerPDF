using System.IO;
using System.Text.Json;

namespace KillerPDF.Services;

internal sealed record ImageAsset(string Id, string Name, string FileName);

/// <summary>Owns copied reusable raster images through stable asset identifiers.</summary>
internal sealed class ImageAssetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _directory;
    private readonly string _manifest;

    internal ImageAssetStore() : this(AppDataPaths.ImageAssetsDirectory) { }

    internal ImageAssetStore(string directory)
    {
        _directory = directory;
        _manifest = Path.Combine(directory, "assets.json");
    }

    internal IReadOnlyList<ImageAsset> Load() => ReadAssets();

    internal ImageAsset Import(string sourcePath, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        byte[] data = File.ReadAllBytes(sourcePath);
        string extension = DetectExtension(data);
        string id = Guid.NewGuid().ToString("N");
        var asset = new ImageAsset(id,
            string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(sourcePath) : name.Trim(),
            id + extension);
        List<ImageAsset> assets = ReadAssets().ToList();
        assets.Add(asset);
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Combine(_directory, asset.FileName), data);
        WriteAssets(assets);
        return asset;
    }

    internal void Rename(string id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        List<ImageAsset> assets = ReadAssets().ToList();
        int index = Find(assets, id);
        assets[index] = assets[index] with { Name = name.Trim() };
        WriteAssets(assets);
    }

    internal void Remove(string id)
    {
        List<ImageAsset> assets = ReadAssets().ToList();
        int index = Find(assets, id);
        string file = Path.Combine(_directory, assets[index].FileName);
        assets.RemoveAt(index);
        WriteAssets(assets);
        if (File.Exists(file)) File.Delete(file);
    }

    internal string GetPath(ImageAsset asset) => Path.Combine(_directory, asset.FileName);

    private List<ImageAsset> ReadAssets()
    {
        if (!File.Exists(_manifest)) return [];
        return JsonSerializer.Deserialize<List<ImageAsset>>(File.ReadAllText(_manifest)) ?? [];
    }

    private void WriteAssets(List<ImageAsset> assets)
    {
        Directory.CreateDirectory(_directory);
        string temporary = _manifest + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(assets, JsonOptions));
        File.Move(temporary, _manifest, overwrite: true);
    }

    private static int Find(IReadOnlyList<ImageAsset> assets, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        for (int index = 0; index < assets.Count; index++)
            if (string.Equals(assets[index].Id, id, StringComparison.Ordinal)) return index;
        throw new KeyNotFoundException($"Image asset '{id}' was not found.");
    }

    private static string DetectExtension(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        if (data.Length >= png.Length && data[..png.Length].SequenceEqual(png)) return ".png";
        if (data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff) return ".jpg";
        if (data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M') return ".bmp";
        throw new InvalidDataException("Only PNG, JPEG, and BMP image assets are supported.");
    }
}
