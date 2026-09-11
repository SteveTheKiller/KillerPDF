using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ImageAssetStoreTests
{
    [Fact]
    public void ImportRenameAndRemove_OwnsRasterByStableIdentifier()
    {
        string root = Path.Combine(Path.GetTempPath(), $"killerpdf-assets-{Guid.NewGuid():N}");
        string source = Path.Combine(root, "source.png");
        try
        {
            Directory.CreateDirectory(root);
            byte[] png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3];
            File.WriteAllBytes(source, png);
            var store = new ImageAssetStore(Path.Combine(root, "library"));

            ImageAsset asset = store.Import(source, "Approval mark");
            File.Delete(source);
            store.Rename(asset.Id, "Approved");

            ImageAsset loaded = Assert.Single(store.Load());
            Assert.Equal(asset.Id, loaded.Id);
            Assert.Equal("Approved", loaded.Name);
            Assert.Equal(png, File.ReadAllBytes(store.GetPath(loaded)));
            store.Remove(loaded.Id);
            Assert.Empty(store.Load());
            Assert.False(File.Exists(store.GetPath(loaded)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
