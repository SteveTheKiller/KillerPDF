using Xunit;
using static KillerPDF.Tests.ViewerSourcePin;

namespace KillerPDF.Tests;

// SOURCE PINS (viewer review findings with no headless seam). The viewer is not compiled into this project,
// so these name the exact expressions the fixes changed; the written window checks in the 1.9.3 notes cover
// behavior (needs-vm).
public sealed class ViewerReviewFixPinTests
{
    // controls:BF-012. A render that clears the selection mid-resize finishes the resize first, so the
    // size change keeps its undo step and marks the tab unsaved.
    [Fact]
    public void ClearSelectionFinishesAResizeInProgressBeforeDroppingItsTargets()
    {
        string annotations = Viewer("PdfViewer.Annotations.cs");
        string method = Between(annotations, "internal void ClearSelection()", "_resizeSigAnnot = null;");

        Assert.Contains("if (_resizeUndoAnnot is { } resizing) FinishResizeUndo(resizing);", method);
        Assert.True(method.IndexOf("FinishResizeUndo(resizing)", StringComparison.Ordinal)
            < method.IndexOf("_isResizingSig = false;", StringComparison.Ordinal));
    }

    // controls:BF-017. The placement cache holds the closed document's strict parse; it is dropped where the
    // viewer releases its document session.
    [Fact]
    public void ReleasingTheDocumentSessionDropsThePlacementCache()
    {
        string links = Viewer("PdfViewer.Links.cs");
        string close = Between(links, "private void CloseEngineDocumentSession()", "_backgroundRenderCache.Clear();");
        Assert.Contains("ReleasePlacementCache();", close);

        string images = Viewer("PdfViewer.SourceImages.cs");
        string release = Between(images, "private void ReleasePlacementCache()", "private IReadOnlyList<PdfImagePlacementSelection>? PlacementsForPage(");
        foreach (string field in new[] { "_placementCache.Clear();", "_placementAssets.Clear();", "_placementCacheReader = null;",
            "_placementCacheSource = null;", "_placementCacheOwner = null;" })
            Assert.Contains(field, release);
    }
}
