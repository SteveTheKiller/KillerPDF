using System.IO;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ReadingTextCacheTests
{
    // Every edit used to make the screen-reader text model read and parse the whole file again on
    // the UI thread. The structure order, comments and crop boxes depend only on the working file.
    [Fact]
    public void DocumentFactsAreReadAgainOnlyWhenTheWorkingFileChanges()
    {
        string source = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!,
            "Controls", "Viewer", "PdfViewer.Reading.cs"));
        string facts = source[source.IndexOf("private void ReadDocumentFacts(", StringComparison.Ordinal)..];
        Assert.Contains("if (Equals(facts, _factsVersion)) return;", facts[..facts.IndexOf("OpenForReading", StringComparison.Ordinal)]);
        string invalidate = source[source.IndexOf("internal void InvalidateText()", StringComparison.Ordinal)..];
        invalidate = invalidate[..invalidate.IndexOf("TextChanged?.Invoke();", StringComparison.Ordinal)];
        Assert.DoesNotContain("_structureOrder", invalidate);
        Assert.DoesNotContain("_factsVersion", invalidate);
    }

    // Screen readers ask for the visible ranges often; mapping each character to the screen walked
    // the visual tree twice per character. VisibleTextGeometryTests checks the mapping's result.
    [Fact]
    public void VisibleRangesMapCharactersWithOneTransformPerPage()
    {
        string root = Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!;
        string viewer = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.Reading.cs"));
        string method = viewer[viewer.IndexOf("public IReadOnlyList<(int Page, int Start, int End)> VisibleCaretRanges()", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("public (int Page, int Caret)? CaretFromScreenPoint(", StringComparison.Ordinal)];
        Assert.Contains("VisibleTextGeometry.VisibleCarets(", method);
        // Only the page's own bounds are mapped here; the characters go through the helper.
        Assert.Equal(2, method.Split("PointToScreen(").Length - 1);
        string geometry = File.ReadAllText(Path.Combine(root, "Services", "VisibleTextGeometry.cs"));
        string loop = geometry[geometry.IndexOf("for (int caret = 0;", StringComparison.Ordinal)..];
        Assert.DoesNotContain("toScreen(", loop);
        Assert.Contains("Rect.Transform(rect, matrix)", loop);
    }

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
