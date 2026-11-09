using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// An EPUB or DjVu tab has no PDF working document, so view code that counts that document's pages, or waits for it,
// skips books. The page badge ("12 / 1573" in the corner while scrolling) never showed for a book, and a move to a
// monitor of another scale never redrew a book's pages. The viewer cannot be built without a window here, so these
// pin the sites; the badge was checked on SN01.
public sealed class NativePageCountTests
{
    [Fact]
    public void ThePageBadgeCountsTheShownDocumentsPages()
    {
        string badge = Between(Viewport(), "private void ShowPageBadge(int page)", "private void ShowPageBadgeText(string text)");

        Assert.DoesNotContain("_doc", badge, StringComparison.Ordinal);
        Assert.Contains("ShowPageBadgeText($\"{page + 1} / {ReadPageTotal}\");", badge, StringComparison.Ordinal);
        Assert.Contains("$\"{first + 1}-{last + 1} / {ReadPageCount}\"", badge, StringComparison.Ordinal);
    }

    [Fact]
    public void AMonitorScaleChangeRedrawsABook()
    {
        string dpi = Between(Viewport(), "protected override void OnDpiChanged(", "private void ResetZoom()");

        Assert.Contains("if (HasReadDocument) StartRerenderTimer();", dpi, StringComparison.Ordinal);
    }

    private static string Viewport() => File.ReadAllText(Path.Combine(Root(), "Controls", "Viewer", "PdfViewer.Viewport.cs"));

    private static string Between(string source, string start, string end)
    {
        int from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "Not found: " + start);
        int to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, "Not found: " + end);
        return source[from..to];
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return directory!.FullName;
    }
}
