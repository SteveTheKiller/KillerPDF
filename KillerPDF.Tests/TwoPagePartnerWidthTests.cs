using System;
using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// Two-Page and Grid partner tiles take the primary page's width, which the primary render sets
// once that page is on screen. A zoom or fit refresh while a document was still opening built
// the partners against an unset canvas with a 595 DIP stand-in (a Letter page is 1583 DIP) and
// cached them: page 2 of the first spread showed at about a third of page 1's size, and paging
// back reused the small bitmap inside a full-size frame.
public sealed class TwoPagePartnerWidthTests
{
    [Fact]
    public void PartnerTilesWaitForThePrimaryPageAndItsRealWidth()
    {
        string method = RenderAdditionalPages();
        Assert.DoesNotContain(": 595", method, StringComparison.Ordinal);
        Assert.Contains("if (_annotationCanvas.Tag is not int shownPage || shownPage != primaryPageIdx", method, StringComparison.Ordinal);
        Assert.Contains("|| !(_annotationCanvas.Width > 0)) return;", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ACachedTileBuiltForAnotherWidthIsNotReused()
    {
        string method = RenderAdditionalPages();
        int hit = method.IndexOf("TryGetCachedRender(session, i, tileBucket, rot", StringComparison.Ordinal);
        Assert.True(hit >= 0);
        string next = method[hit..method.IndexOf("if (cachedTile != null)", hit, StringComparison.Ordinal)];
        Assert.Contains("Math.Abs(cachedTile.Width - Math.Round(primaryDipW)) > 0.5", next, StringComparison.Ordinal);
    }

    private static string RenderAdditionalPages()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Controls", "Viewer", "PdfViewer.Viewport.cs"));
        int start = source.IndexOf("private async void RenderAdditionalPages(int primaryPageIdx)", StringComparison.Ordinal);
        int end = source.IndexOf("private void AddSecondaryTile(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
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
