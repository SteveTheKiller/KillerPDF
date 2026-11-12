using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// Switching the view mode (F8 for Grid, for one) cancelled the page list's thumbnail window through CancelRenderWork
// but did not rebuild the page list. The rows already drawn stayed; every row scrolled to afterwards asked a cancelled
// window and stayed a plain "Page N" label until the list was rebuilt. A view mode keeps the document and its list, so
// it must leave the window running.
//
// PdfViewer needs a window and a host to run, so this reads the source: it pins the lifetime rule at the one call that
// broke it. The real behaviour is checked by opening a long document, switching the view mode and scrolling the list.
public sealed class ThumbnailWindowLifetimeTests
{
    [Fact]
    public void ChangingTheViewModeLeavesThePageListsThumbnailWindowRunning()
    {
        string root = FindRepositoryRoot();
        string viewport = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.Viewport.cs"));
        int start = viewport.IndexOf("internal void ApplyViewMode(", StringComparison.Ordinal);
        Assert.True(start >= 0, "ApplyViewMode was not found.");
        int cancel = viewport.IndexOf("CancelRenderWork(", start, StringComparison.Ordinal);
        Assert.True(cancel >= 0 && cancel - start < 400, "ApplyViewMode no longer cancels render work first.");
        Assert.StartsWith("CancelRenderWork(includeThumbnails: false);", viewport[cancel..], StringComparison.Ordinal);
    }

    [Fact]
    public void RenderWorkCancelsTheThumbnailWindowOnlyWhenAskedTo()
    {
        string root = FindRepositoryRoot();
        string tabs = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.Tabs.cs"));
        int start = tabs.IndexOf("private void CancelRenderWork(bool includeThumbnails = true)", StringComparison.Ordinal);
        Assert.True(start >= 0, "CancelRenderWork no longer says whether it cancels thumbnails.");
        int guard = tabs.IndexOf("if (includeThumbnails)", start, StringComparison.Ordinal);
        int cancel = tabs.IndexOf("_thumbCts?.Cancel();", start, StringComparison.Ordinal);
        Assert.True(guard > start && cancel > guard, "The thumbnail cancel is not behind includeThumbnails.");
    }

    private static string FindRepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the KillerPDF repository root.");
    }
}
