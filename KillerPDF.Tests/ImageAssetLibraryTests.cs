using System.IO;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ImageAssetLibraryTests
{
    private static (KillerPDF.Services.ImageAssetLibrary Library, string Dir) NewLibrary()
    {
        string dir = Path.Combine(Path.GetTempPath(), "KillerPDF-LibTests-" + Guid.NewGuid().ToString("N"));
        var library = new KillerPDF.Services.ImageAssetLibrary(
            dir, Path.Combine(dir, "imagelibrary.json"));
        return (library, dir);
    }

    [Fact]
    public void Add_Persist_Load_RoundTrips()
    {
        var (library, dir) = NewLibrary();
        library.Add("seal", "aGVsbG8=", 120, 40);
        library.Persist();

        var reloaded = new KillerPDF.Services.ImageAssetLibrary(
            dir, Path.Combine(dir, "imagelibrary.json"));
        reloaded.Load();
        var asset = Assert.Single(reloaded.Assets);
        Assert.Equal("seal", asset.Name);
        Assert.Equal("aGVsbG8=", asset.ImageData);
        Assert.Equal(120, asset.Width);
        Assert.False(string.IsNullOrWhiteSpace(asset.Id));
    }

    [Fact]
    public void Load_MissingOrCorrupt_StartsEmpty()
    {
        var (library, dir) = NewLibrary();
        library.Load();
        Assert.Empty(library.Assets);

        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "imagelibrary.json"), "{oops");
        library.Load();
        Assert.Empty(library.Assets);
    }

    [Fact]
    public void Add_DeduplicatesNames()
    {
        var (library, _) = NewLibrary();
        var first = library.Add("seal", "eA==", 10, 10);
        var second = library.Add("seal", "eQ==", 10, 10);
        Assert.Equal("seal", first.Name);
        Assert.Equal("seal (2)", second.Name);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Remove_Deletes()
    {
        var (library, _) = NewLibrary();
        var asset = library.Add("seal", "eA==", 10, 10);
        library.Remove(asset);
        Assert.Empty(library.Assets);
    }
}
