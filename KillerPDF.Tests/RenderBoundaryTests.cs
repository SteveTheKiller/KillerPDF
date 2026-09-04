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
