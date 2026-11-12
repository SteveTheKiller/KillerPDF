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

    // A tab switch still cancels the outgoing tab's window. Its rows were kept as a finished list and re-seated when the
    // tab came back, so rows not drawn yet asked a cancelled window. A cancelled window marks its list unfinished, so the
    // tab's return rebuilds the list (carrying the pictures already drawn) instead.
    [Fact]
    public void ACancelledThumbnailWindowMarksItsListUnfinished()
    {
        string root = FindRepositoryRoot();
        string pages = File.ReadAllText(Path.Combine(root, "Shell", "PageOperations.cs"));
        int refresh = pages.IndexOf("internal void RefreshPageList()", StringComparison.Ordinal);
        Assert.True(refresh >= 0, "RefreshPageList was not found.");
        int register = pages.IndexOf("ct.Register(() =>", refresh, StringComparison.Ordinal);
        Assert.True(register > refresh, "RefreshPageList no longer lets go of its window on cancel.");
        int end = pages.IndexOf("});", register, StringComparison.Ordinal);
        string onCancel = pages[register..end];
        Assert.Contains("window.Dispose();", onCancel, StringComparison.Ordinal);
        Assert.Contains("MarkThumbnailCacheIncomplete(items)", onCancel, StringComparison.Ordinal);

        string api = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.TabsApi.cs"));
        int mark = api.IndexOf("internal void MarkThumbnailCacheIncomplete(PageThumbnailVm[] cache)", StringComparison.Ordinal);
        Assert.True(mark >= 0, "No way to mark a cancelled list unfinished.");
        Assert.Contains("ThumbCacheComplete = false", api[mark..Math.Min(api.Length, mark + 500)], StringComparison.Ordinal);
    }

    // A tab that comes back rebuilds its list, and the rebuild carries pictures forward so the list never flashes
    // blank. They come from that tab's own list: the list on screen belongs to the tab just left, so carrying from it
    // showed the other document's pages. The tab's list is used whatever its file path, because a page edit reloads
    // the document from a fresh temporary path and the pictures must still carry over then.
    [Fact]
    public void ARebuiltListCarriesPicturesFromItsOwnTabOnly()
    {
        string root = FindRepositoryRoot();
        string pages = File.ReadAllText(Path.Combine(root, "Shell", "PageOperations.cs"));
        int refresh = pages.IndexOf("internal void RefreshPageList()", StringComparison.Ordinal);
        int carry = pages.IndexOf("var oldItems =", refresh, StringComparison.Ordinal);
        Assert.True(refresh >= 0 && carry > refresh, "RefreshPageList no longer carries pictures forward.");
        string line = pages[carry..pages.IndexOf(';', carry)];
        Assert.DoesNotContain("PageList.ItemsSource", line, StringComparison.Ordinal);
        Assert.Contains("thumbnailOwner.ThumbCache", line, StringComparison.Ordinal);
        Assert.DoesNotContain("ThumbCacheFile", line, StringComparison.Ordinal);
    }

    // Flushing render caches (an invert toggle, a working CMYK change) repaints pages in place and keeps the page list,
    // so it leaves the list's thumbnail window running; a caller whose thumbnails really changed rebuilds the list.
    [Fact]
    public void FlushingRenderCachesLeavesThePageListsThumbnailWindowRunning()
    {
        string root = FindRepositoryRoot();
        string tabs = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.Tabs.cs"));
        int start = tabs.IndexOf("private void FlushAllRenderCaches()", StringComparison.Ordinal);
        Assert.True(start >= 0, "FlushAllRenderCaches was not found.");
        int cancel = tabs.IndexOf("CancelRenderWork(", start, StringComparison.Ordinal);
        Assert.True(cancel > start && cancel - start < 200, "FlushAllRenderCaches no longer cancels render work first.");
        Assert.StartsWith("CancelRenderWork(includeThumbnails: false);", tabs[cancel..], StringComparison.Ordinal);
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
