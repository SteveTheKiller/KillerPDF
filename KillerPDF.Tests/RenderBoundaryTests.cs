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
        string fallback = Path.GetFullPath(Path.Combine(root, "Services", "DocnetRenderFallback.cs"));
        string interop = Path.GetFullPath(Path.Combine(root, "Services", "PdfiumInterop.cs"));
        string thisTest = Path.GetFullPath(Path.Combine(root, "KillerPDF.Tests", "RenderBoundaryTests.cs"));
        string[] forbidden =
        [
            "using Docnet.",
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
                || fullPath.Equals(fallback, StringComparison.OrdinalIgnoreCase)
                || fullPath.Equals(interop, StringComparison.OrdinalIgnoreCase)
                || fullPath.Equals(thisTest, StringComparison.OrdinalIgnoreCase))
                continue;

            string source = File.ReadAllText(fullPath);
            foreach (string text in forbidden)
                Assert.DoesNotContain(text, source, StringComparison.Ordinal);
        }

        string boundarySource = File.ReadAllText(boundary);
        Assert.DoesNotContain("using Docnet.", boundarySource, StringComparison.Ordinal);
        Assert.DoesNotContain("IDocReader", boundarySource, StringComparison.Ordinal);
        Assert.DoesNotContain("PdfiumInterop", boundarySource, StringComparison.Ordinal);
        Assert.DoesNotContain("PdfPageRenderSession Open(", boundarySource,
            StringComparison.Ordinal);
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

        int engineOpen = method.IndexOf("EngineDocument.OpenWithCompatibilityRecovery(",
            StringComparison.Ordinal);
        int fallbackRender = method.IndexOf("DocnetRenderFallback.RenderExactPage(",
            StringComparison.Ordinal);

        Assert.True(engineOpen >= 0);
        Assert.True(fallbackRender > engineOpen);
    }

    [Fact]
    public void ExactComparisonRenderingPassesCancellationIntoTheEngineBoundary()
    {
        string root = FindRepositoryRoot();
        string boundary = File.ReadAllText(
            Path.Combine(root, "Services", "PdfPageRenderSession.cs"));
        string comparison = File.ReadAllText(
            Path.Combine(root, "Shell", "PdfComparison.cs"));

        Assert.Contains("includeFormFields), cancellationToken)", boundary,
            StringComparison.Ordinal);
        Assert.Equal(2, Count(comparison, "cancellationToken: token"));
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
        string fallback = File.ReadAllText(
            Path.Combine(root, "Services", "TesseractOcrFallback.cs"));

        Assert.Contains("PdfOcrImagePreprocessor.PrepareBgra(", source,
            StringComparison.Ordinal);
        Assert.Contains("Pix.Create(image.Width, image.Height, 8)", fallback,
            StringComparison.Ordinal);
        Assert.Contains("RasterOptions, cancellationToken)", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Pix.Load", fallback, StringComparison.Ordinal);
        Assert.DoesNotContain("PngBitmapEncoder", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BitmapSource.Create", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OcrUsesAnInstalledEngineModelBeforeLoadingTesseract()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "Services", "OcrService.cs"));

        Assert.Contains("PdfOcrRecognitionModel.Load(", source, StringComparison.Ordinal);
        Assert.Contains("PdfOcrRecognizer.RecognizeBgra(", source, StringComparison.Ordinal);
        Assert.Contains("characterWhitelist, cancellationToken", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_engineModel is not null && string.IsNullOrEmpty",
            source, StringComparison.Ordinal);
        Assert.Contains("PdfOcrRecognitionModel.Combine(models)", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("language.Contains('+', StringComparison.Ordinal)",
            source, StringComparison.Ordinal);
        Assert.Contains("private TesseractOcrFallback NativeFallback()", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("using Tesseract", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TesseractEngine", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TesseractTypesStayInsideTheNativeOcrFallback()
    {
        string root = FindRepositoryRoot();
        string fallback = Path.GetFullPath(
            Path.Combine(root, "Services", "TesseractOcrFallback.cs"));
        string thisTest = Path.GetFullPath(
            Path.Combine(root, "KillerPDF.Tests", "RenderBoundaryTests.cs"));

        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string fullPath = Path.GetFullPath(file);
            if (fullPath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase)
                || fullPath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase)
                || fullPath.Equals(fallback, StringComparison.OrdinalIgnoreCase)
                || fullPath.Equals(thisTest, StringComparison.OrdinalIgnoreCase))
                continue;

            string source = File.ReadAllText(fullPath);
            Assert.DoesNotContain("using Tesseract", source, StringComparison.Ordinal);
            Assert.DoesNotContain("TesseractEngine", source, StringComparison.Ordinal);
            Assert.DoesNotContain("PageIteratorLevel", source, StringComparison.Ordinal);
        }
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
            < method.IndexOf("_nativeFallback.RenderBasePage(", StringComparison.Ordinal));
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
    public void LiveViewerUsesTheEngineFirstRenderSessionWithProfileSpecificFallback()
    {
        string root = FindRepositoryRoot();
        string viewer = File.ReadAllText(
            Path.Combine(root, "Controls", "Viewer", "PdfViewer.Viewport.cs"));
        string boundary = File.ReadAllText(
            Path.Combine(root, "Services", "PdfPageRenderSession.cs"));

        Assert.Equal(4, Count(viewer, "PdfPageRenderSession.OpenEngineFirst("));
        Assert.DoesNotContain("PdfPageRenderSession.Open(", viewer,
            StringComparison.Ordinal);
        Assert.Contains("_nativeFallbackProfiles.Add(profile)", boundary,
            StringComparison.Ordinal);
        Assert.Contains("!_nativeFallbackProfiles.Contains(profile)", boundary,
            StringComparison.Ordinal);
        Assert.Contains("IncludeAnnotations: false, IncludeFormFields: false", boundary,
            StringComparison.Ordinal);
        Assert.Contains("IncludeAnnotations: true, IncludeFormFields: includeFormFields", boundary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CancellableRenderWorkflowsPassTheirTokensIntoTheEngineBoundary()
    {
        string root = FindRepositoryRoot();
        string boundary = File.ReadAllText(
            Path.Combine(root, "Services", "PdfPageRenderSession.cs"));
        string rasterize = File.ReadAllText(
            Path.Combine(root, "Services", "PdfRasterize.cs"));
        string ocr = File.ReadAllText(
            Path.Combine(root, "Features", "Ocr", "OcrController.cs"));
        string viewer = File.ReadAllText(
            Path.Combine(root, "Controls", "Viewer", "PdfViewer.Viewport.cs"));

        Assert.Contains("includeFormFields), cancellationToken)", boundary,
            StringComparison.Ordinal);
        Assert.Contains("exception is not OperationCanceledException", boundary,
            StringComparison.Ordinal);
        Assert.Equal(2, Count(rasterize, "cancellationToken: ct"));
        Assert.Contains("RenderBasePage(pages[i], ct)", ocr, StringComparison.Ordinal);
        Assert.Contains("RenderBasePage(pageIdx, ct)", ocr, StringComparison.Ordinal);
        Assert.Equal(2, Count(ocr, "RenderBasePage(i, ct)"));
        Assert.Equal(3, Count(viewer, "cancellationToken: cts.Token"));
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
