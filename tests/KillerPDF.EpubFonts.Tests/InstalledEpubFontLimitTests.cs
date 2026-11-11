using KillerPDF.Services;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using Xunit;

namespace KillerPDF.EpubFonts.Tests;

// A separate test process keeps the real catalog's one-time index and font-directory
// environment isolated from the main application test suite.
public sealed class InstalledEpubFontLimitTests
{
    [Fact]
    public void AnOversizedInstalledFamilyFallsBackWithoutThrowing()
    {
        string source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "arial.ttf");
        Assert.True(File.Exists(source), "This Windows integration test requires the installed Arial TrueType face.");
        byte[] bytes = File.ReadAllBytes(source);
        var fallback = TrueTypeFont.Load(bytes);
        string family;
        using (var metadata = new MemoryStream(bytes, writable: false))
            family = Assert.Single(InstalledFontMetadata.Read(metadata)).Family;
        string root = Path.Combine(Environment.GetEnvironmentVariable("KILLERPDF_TEST_ROOT") ?? Path.GetTempPath(),
            "epub-font-limit-" + Guid.NewGuid().ToString("N"));
        string fonts = Path.Combine(root, "Windows", "Fonts");
        Directory.CreateDirectory(fonts);
        string? systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        string? localData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            for (int index = 0; index < 257; index++)
                File.WriteAllBytes(Path.Combine(fonts, $"face-{index:D3}.ttf"), bytes);
            Environment.SetEnvironmentVariable("SystemRoot", Path.Combine(root, "Windows"));
            Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(root, "Local"));
            AppDataPaths.StartupSmokeRoot = Path.Combine(root, "AppData");

            var resolve = PdfEngineIntegration.EpubFontFaceResolver(family);
            Assert.Null(resolve(new EpubFontFaceRequest(new EpubFontFamily(family), 400, fallback)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SystemRoot", systemRoot);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", localData);
            AppDataPaths.StartupSmokeRoot = null;
            Directory.Delete(root, recursive: true);
        }
    }
}
