using System.IO;
using Xunit;

namespace KillerPDF.Tests;

public sealed class RenderBoundaryTests
{
    [Fact]
    public void RenderingBackendsAreOnlyUsedByTheRenderBoundary()
    {
        string root = FindRepositoryRoot();
        string boundary = Path.GetFullPath(Path.Combine(root, "Services", "PdfPageRenderSession.cs"));
        string interop = Path.GetFullPath(Path.Combine(root, "Services", "PdfiumInterop.cs"));
        string thisTest = Path.GetFullPath(Path.Combine(root, "KillerPDF.Tests", "RenderBoundaryTests.cs"));
        string[] forbidden =
        [
            "DocLib.Instance.GetDocReader",
            ".GetPageReader(",
            "PdfiumInterop.RenderPageWithAnnotations"
        ];

        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string fullPath = Path.GetFullPath(file);
            if (fullPath.StartsWith(Path.Combine(root, "bin"), StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(Path.Combine(root, "obj"), StringComparison.OrdinalIgnoreCase)
                || fullPath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || fullPath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || fullPath.Equals(boundary, StringComparison.OrdinalIgnoreCase)
                || fullPath.Equals(interop, StringComparison.OrdinalIgnoreCase)
                || fullPath.Equals(thisTest, StringComparison.OrdinalIgnoreCase))
                continue;

            string source = File.ReadAllText(fullPath);
            foreach (string text in forbidden)
                Assert.DoesNotContain(text, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PageThumbnailsUseTheEngineBeforeTheNativeFallback()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "Models", "PageThumbnailVm.cs"));

        Assert.Contains("PdfPageRenderSession.OpenEngineFirst(", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PdfPageRenderSession.Open(", source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExactComparisonPagesUseTheEngineBeforeTheNativeFallback()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(
            Path.Combine(root, "Services", "PdfPageRenderSession.cs"));
        int methodStart = source.IndexOf("RenderExactPage(", StringComparison.Ordinal);
        int methodEnd = source.IndexOf("public void Dispose", methodStart,
            StringComparison.Ordinal);
        string method = source[methodStart..methodEnd];

        int engineOpen = method.IndexOf("EngineDocument.Open(", StringComparison.Ordinal);
        int fallbackRender = method.IndexOf("PdfiumInterop.RenderPageWithAnnotations(",
            StringComparison.Ordinal);

        Assert.True(engineOpen >= 0);
        Assert.True(fallbackRender > engineOpen);
    }

    [Fact]
    public void ImageExportUsesTheEngineFirstRenderSession()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(
            Path.Combine(root, "Features", "Cli", "CliRunner.cs"));
        int methodStart = source.IndexOf("private static int CliToImage(",
            StringComparison.Ordinal);
        int methodEnd = source.IndexOf("private static int CliFlatten(", methodStart,
            StringComparison.Ordinal);
        string method = source[methodStart..methodEnd];

        Assert.Contains("PdfPageRenderSession.OpenEngineFirst(", method,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PdfPageRenderSession.Open(", method,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FlatteningUsesTheEngineFirstRenderSession()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(
            Path.Combine(root, "Features", "Cli", "CliRunner.cs"));
        int methodStart = source.IndexOf("private static int CliFlatten(",
            StringComparison.Ordinal);
        int methodEnd = source.IndexOf("private static int ParseBoundedIntOption(",
            methodStart, StringComparison.Ordinal);
        string method = source[methodStart..methodEnd];

        Assert.Contains("PdfPageRenderSession.OpenEngineFirst(", method,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PdfPageRenderSession.Open(", method,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OcrConsumesEnginePreparedPixelsWithoutAPngRoundTrip()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "Services", "OcrService.cs"));

        Assert.Contains("PdfOcrImagePreprocessor.PrepareBgra(", source,
            StringComparison.Ordinal);
        Assert.Contains("Pix.Create(image.Width, image.Height, 8)", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PngBitmapEncoder", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BitmapSource.Create", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the KillerPDF repository root.");
    }
}
