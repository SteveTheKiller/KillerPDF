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
    public void NativePdfiumFileFallbacksStayInsideTheInteropBoundary()
    {
        string root = FindRepositoryRoot();
        string interop = Path.GetFullPath(Path.Combine(root, "Services", "PdfiumInterop.cs"));
        string thisTest = Path.GetFullPath(
            Path.Combine(root, "KillerPDF.Tests", "RenderBoundaryTests.cs"));

        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string fullPath = Path.GetFullPath(file);
            if (fullPath.StartsWith(Path.Combine(root, "bin"), StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(Path.Combine(root, "obj"), StringComparison.OrdinalIgnoreCase)
                || fullPath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || fullPath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || fullPath.Equals(interop, StringComparison.OrdinalIgnoreCase)
                || fullPath.Equals(thisTest, StringComparison.OrdinalIgnoreCase))
                continue;

            string source = File.ReadAllText(fullPath);
            Assert.DoesNotContain("PdfiumInterop.TryPdfiumStripEncryption(", source,
                StringComparison.Ordinal);
            Assert.DoesNotContain("PdfiumInterop.TryPdfiumSaveWithZeroRotations(", source,
                StringComparison.Ordinal);
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
    public void GuiFlattenAndImageExportUseTheEngineFirstRenderSession()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(
            Path.Combine(root, "Services", "PdfRasterize.cs"));

        Assert.Equal(2, Count(source, "PdfPageRenderSession.OpenEngineFirst("));
        Assert.DoesNotContain("PdfPageRenderSession.Open(", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("using Docnet.", source, StringComparison.Ordinal);
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

    [Fact]
    public void OcrWorkflowsUseTheEngineFirstRenderSession()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(
            Path.Combine(root, "Features", "Ocr", "OcrController.cs"));

        Assert.Equal(4, Count(source, "PdfPageRenderSession.OpenEngineFirst("));
        Assert.DoesNotContain("PdfPageRenderSession.Open(", source,
            StringComparison.Ordinal);

        string boundary = File.ReadAllText(
            Path.Combine(root, "Services", "PdfPageRenderSession.cs"));
        int methodStart = boundary.IndexOf("RenderBasePage(", StringComparison.Ordinal);
        int methodEnd = boundary.IndexOf("internal PdfRenderedPage RenderPage(", methodStart,
            StringComparison.Ordinal);
        string method = boundary[methodStart..methodEnd];
        Assert.Contains("RenderEnginePage(", method, StringComparison.Ordinal);
        Assert.Contains("includeAnnotations: false", method, StringComparison.Ordinal);
        Assert.Contains("includeFormFields: false", method, StringComparison.Ordinal);
        Assert.True(method.IndexOf("RenderEnginePage(", StringComparison.Ordinal)
            < method.IndexOf("NativeReader()", StringComparison.Ordinal));
    }

    [Fact]
    public void PrintWorkflowsUseTheEngineFirstRenderSession()
    {
        string root = FindRepositoryRoot();
        string cli = File.ReadAllText(
            Path.Combine(root, "Features", "Cli", "CliRunner.cs"));
        int printStart = cli.IndexOf("private static int CliPrint(",
            StringComparison.Ordinal);
        int printEnd = cli.IndexOf("private static int CliOcr(", printStart,
            StringComparison.Ordinal);
        string print = cli[printStart..printEnd];
        string preview = File.ReadAllText(
            Path.Combine(root, "Controls", "PrintPreviewWindow.cs"));

        Assert.Contains("PdfPageRenderSession.OpenEngineFirst(", print,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PdfPageRenderSession.Open(", print,
            StringComparison.Ordinal);
        Assert.Contains("PdfPageRenderSession.OpenEngineFirst(", preview,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PdfPageRenderSession.Open(", preview,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RasterRepairUsesTheEngineFirstRenderSession()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "Services", "PdfImport.cs"));

        Assert.Contains("RepairViaRasterizeToFile", source, StringComparison.Ordinal);
        Assert.Contains("PdfPageRenderSession.OpenEngineFirst(", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PdfPageRenderSession.Open(", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("using Docnet.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveViewerUsesTheEngineFirstRenderSessionWithStickyPageFallback()
    {
        string root = FindRepositoryRoot();
        string viewer = File.ReadAllText(
            Path.Combine(root, "Controls", "Viewer", "PdfViewer.Viewport.cs"));
        string boundary = File.ReadAllText(
            Path.Combine(root, "Services", "PdfPageRenderSession.cs"));

        Assert.Equal(4, Count(viewer, "PdfPageRenderSession.OpenEngineFirst("));
        Assert.DoesNotContain("PdfPageRenderSession.Open(", viewer,
            StringComparison.Ordinal);
        Assert.Contains("_nativeFallbackPages.Add(pageIndex)", boundary,
            StringComparison.Ordinal);
        Assert.Contains("!_nativeFallbackPages.Contains(pageIndex)", boundary,
            StringComparison.Ordinal);
    }

    private static int Count(string source, string value)
    {
        int count = 0;
        for (int index = source.IndexOf(value, StringComparison.Ordinal);
            index >= 0;
            index = source.IndexOf(value, index + value.Length,
                StringComparison.Ordinal))
            count++;
        return count;
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
