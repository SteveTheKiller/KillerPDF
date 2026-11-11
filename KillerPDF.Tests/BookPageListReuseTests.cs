using Xunit;

namespace KillerPDF.Tests;

// Focusing the other pane re-seats a complete thumbnail list instead of drawing it again. A book has no
// PdfDocument, so the reuse check counted its pages from the book instead, and a growing book still rebuilds.
public sealed class BookPageListReuseTests
{
    [Fact]
    public void ACompleteListOfABookIsReusedOnAFocusSwitch()
    {
        string pages = ShellSource.Read("Shell", "PageOperations.cs");
        string restore = ShellSource.Between(pages, "internal void RestorePageListForActivePane()", "internal void RefreshPageList()");

        Assert.DoesNotContain("&& _doc != null", restore, StringComparison.Ordinal);
        Assert.Contains("thumbnailOwner.NativeDocumentRef", restore, StringComparison.Ordinal);
        Assert.Contains("IsPaginating", restore, StringComparison.Ordinal);
        Assert.Contains("cached.Length == pageCount", restore, StringComparison.Ordinal);
    }
}