using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class SignatureLibraryValidationTests
{
    [Fact]
    public void NullEntryIsPreservedWithoutPublishingAnUnusableLibrary()
    {
        CheckDamaged("[null]");
    }

    [Theory]
    [InlineData("[{\"Strokes\":null}]")]
    [InlineData("[{\"Strokes\":[null]}]")]
    [InlineData("[{\"Strokes\":[[null,null]]}]")]
    [InlineData("[{\"CanvasWidth\":0}]")]
    [InlineData("[{\"CanvasHeight\":-1}]")]
    [InlineData("[{\"ImageData\":\"broken\"}]")]
    [InlineData("[{\"ImageData\":\"aGVsbG8=\"}]")]
    [InlineData("[{\"Vectors\":[null]}]")]
    [InlineData("[{\"Vectors\":[{\"Data\":\"broken\",\"Fill\":4278190080}]}]")]
    [InlineData("[{\"Vectors\":[{\"Data\":\"M0 0 L10 10\"}]}]")]
    [InlineData("[{\"Vectors\":[{\"Data\":\"M0 0 L10 10\",\"Fill\":0}]}]")]
    [InlineData("[{\"Vectors\":[{\"Data\":\"M0 0\",\"Fill\":4278190080},{\"Data\":\"broken\",\"Fill\":4278190080}]}]")]
    [InlineData("[{}]")]
    [InlineData("null")]
    public void UnusablePreviewDataIsPreserved(string json) => CheckDamaged(json);

    [Fact]
    public void FiniteDimensionsThatOverflowPreviewScalingArePreserved() => CheckDamaged(
        "[{\"CanvasWidth\":1e-320,\"CanvasHeight\":1e-320,\"Strokes\":[[{\"X\":0,\"Y\":0},{\"X\":1,\"Y\":1}]]}]");

    [Theory]
    [InlineData("\"Strokes\":[[{\"X\":0,\"Y\":0},{\"X\":1e308,\"Y\":1}]]")]
    [InlineData("\"Vectors\":[{\"Data\":\"M0 0 C1e308 0 1 1 10 10\",\"Stroke\":4278190080}]")]
    [InlineData("\"Vectors\":[{\"Data\":\"M0 0 L1 1\",\"Stroke\":4278190080,\"StrokeWidth\":1e308}]")]
    public void FiniteGeometryThatOverflowsThePickerPreviewIsPreserved(string geometry) => CheckDamaged(
        "[{\"CanvasWidth\":1e-300,\"CanvasHeight\":1e-300," + geometry + "}]");

    [Theory]
    [InlineData("<path d='M0 0'/>", true)]
    [InlineData("<path d='M0 0 L5 5' opacity='0'/>", true)]
    [InlineData("<path d='M0 0 L5 5' opacity='0.001'/>", true)]
    [InlineData("<path d='M0 0 L5 5' fill='none' stroke='none'/>", true)]
    [InlineData("<path d='M0 0'/>", false)]
    [InlineData("<path d='M0 0 L5 5' opacity='0.001'/>", false)]
    public void ImportedHarmlessOutlinesDoNotHideAnOtherwiseUsableLibrary(string harmless, bool visible)
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string library = Path.Combine(dir, "signatures.json");
        string svgPath = Path.Combine(dir, "signature.svg");
        try
        {
            File.WriteAllText(svgPath, "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 200 100'>"
                + harmless + (visible ? "<path d='M10 20 L30 40 L50 20' fill='none' stroke='#000'/>" : "") + "</svg>");
            var (vectors, width, height) = SvgSignatureImporter.Import(svgPath).Fit(400, 150);
            var prior = new SavedSignature { Name = "Existing", Strokes = [[new() { X = 1, Y = 2 }, new() { X = 3, Y = 4 }]] };
            var imported = new SavedSignature { Name = "Imported", CanvasWidth = width, CanvasHeight = height, Vectors = vectors };
            var store = new SignatureStore(dir, library);
            store.Add(prior);
            Assert.True(store.Persist());
            store.Add(imported);
            Assert.True(store.Persist());
            byte[] original = File.ReadAllBytes(library);
            var reloaded = new SignatureStore(dir, library);
            reloaded.Load();
            if (visible)
            {
                Assert.Null(reloaded.PreservedDamagedFile);
                Assert.Equal(new[] { prior.Id, imported.Id }, reloaded.Signatures.Select(signature => signature.Id));
                Assert.Equal(vectors.Select(vector => vector.Data), reloaded.Signatures[1].Vectors!.Select(vector => vector.Data));
                Assert.Equal(vectors.Select(vector => vector.Fill), reloaded.Signatures[1].Vectors!.Select(vector => vector.Fill));
                Assert.Equal(vectors.Select(vector => vector.Stroke), reloaded.Signatures[1].Vectors!.Select(vector => vector.Stroke));
            }
            else
            {
                Assert.Empty(reloaded.Signatures);
                Assert.NotNull(reloaded.PreservedDamagedFile);
                Assert.Equal(original, File.ReadAllBytes(reloaded.PreservedDamagedFile!));
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ImportRejectsAnEntirelyTransparentSignatureBeforeItCanBeAdded()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "transparent.svg");
        try
        {
            File.WriteAllText(path, "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 200 100'>"
                + "<path d='M0 0 L5 5' opacity='0'/></svg>");
            Assert.Throws<InvalidDataException>(() => SvgSignatureImporter.Import(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("[{\"Name\":\"Drawn\",\"Strokes\":[[{\"X\":1,\"Y\":2},{\"X\":10,\"Y\":20}]]}]")]
    [InlineData("[{\"Name\":\"Vector\",\"Kind\":1,\"Vectors\":[{\"Data\":\"M0 0 L10 10\",\"Stroke\":4278190080}]}]")]
    [InlineData("[{\"Name\":\"Image\",\"ImageData\":\"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a4noAAAAASUVORK5CYII=\"}]")]
    public void ValidDrawingImageAndVectorRemainAvailable(string json)
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "signatures.json");
        try
        {
            File.WriteAllText(file, json);
            var store = new SignatureStore(dir, file);
            store.Load();
            Assert.Single(store.Signatures);
            Assert.Null(store.PreservedDamagedFile);
            Assert.True(store.Persist());
            store.Load();
            Assert.Single(store.Signatures);
            Assert.Null(store.PreservedDamagedFile);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static void CheckDamaged(string json)
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "signatures.json");
        try
        {
            File.WriteAllText(file, json);
            var store = new SignatureStore(dir, file);
            store.Load();
            Assert.Empty(store.Signatures);
            Assert.NotNull(store.PreservedDamagedFile);
            Assert.Equal(json, File.ReadAllText(store.PreservedDamagedFile!));
            Assert.True(store.Persist());
            Assert.Equal(json, File.ReadAllText(store.PreservedDamagedFile!));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
