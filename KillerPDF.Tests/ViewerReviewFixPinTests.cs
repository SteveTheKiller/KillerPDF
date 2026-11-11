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
        Assert.Contains("_placementCache.Release();", release);
        string cache = Source("Services", "PlacementReadCache.cs");
        Assert.Contains("_owner = null;", Between(cache, "internal void Release()", "// Placement reads are cached per page"));
        string clear = Between(cache, "private void ClearAll()", "private PdfPageContentReader ReaderFor(");
        foreach (string field in new[] { "_pages.Clear();", "_assets.Clear();", "_reader = null;", "_parse = null;" })
            Assert.Contains(field, clear);
    }

    // controls:BF-018. The Delete key only asks whether a picture is selected; SelectedSourceImageExt would
    // base-64 encode it first.
    [Fact]
    public void DeleteKeyProbesSelectionWithoutEncodingThePicture()
    {
        string shortcuts = Source("Shell", "KeyboardShortcuts.cs");
        string delete = Between(shortcuts, "key == Key.Delete && (_selectedAnnotation is not null", "DeleteSelected();");
        Assert.Contains("ActiveViewer.HasSelectedSourceImageExt", delete);
        Assert.DoesNotContain("SelectedSourceImageExt is not null", delete);

        string images = Viewer("PdfViewer.SourceImages.cs");
        string has = Between(images, "internal bool HasSelectedSourceImageExt", "internal bool CanDeleteSelectedSourceImageExt");
        Assert.DoesNotContain("MaterializedSourceImage", has);
    }

    // controls:BF-020. The owner-restriction notice's dismissal follows the document to another viewer, as the
    // book notice's does.
    [Fact]
    public void RestrictionNoticeDismissalIsSharedByEveryViewer()
    {
        string restriction = Viewer("PdfViewer.Restriction.cs");
        Assert.Contains("private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _restrictionDismissed = [];", restriction);
    }

    // controls:BF-027. The click-versus-drag decision on a selected picture is measured in the viewer's own
    // units, which the page zoom does not scale, against a start point taken in the same units.
    [Fact]
    public void PictureDragThresholdIsMeasuredInZoomIndependentUnits()
    {
        string images = Viewer("PdfViewer.SourceImages.cs");
        Assert.Contains("private (Canvas Canvas, Point Start, Point ScreenStart)? _pictureSelectionPress;", images);
        Assert.Contains("_pictureSelectionPress = (canvas, point, Mouse.GetPosition(this));", images);
        string move = Between(images, "private bool MoveSourceImageDrag(MouseEventArgs e)", "BeginSelectionMarquee(press.Start);");
        Assert.Contains("Vector moved = e.GetPosition(this) - press.ScreenStart;", move);
        Assert.DoesNotContain("e.GetPosition(press.Canvas)", move);
    }
}
