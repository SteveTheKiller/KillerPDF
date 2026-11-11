using Xunit;
using static KillerPDF.Tests.ViewerSourcePin;

namespace KillerPDF.Tests;

// SOURCE PINS (VIEW-005). The viewer is not compiled into this project, so no headless test can press the mouse
// on a page. Behavior evidence is the written window check in the 1.9.3 notes (needs-vm).
public sealed class SourceImagePressPinTests
{
    // On a scanned page the full-page picture sits under every empty point. 1.9.2 drew the selection box there;
    // the first press now only selects the picture, a move starts only from a picture that was already selected.
    [Fact]
    public void FirstPressOnAnUnselectedPictureOnlySelectsItAndNeverStartsAMove()
    {
        string annotations = Viewer("PdfViewer.Annotations.cs");
        string press = Between(annotations, "if (!shiftSel && !_ocrRegionMode && TrySelectSourceImage(", "// Keep the existing multi-selection when shift is held");

        Assert.Contains("out bool pictureWasSelected)", press);
        Assert.Contains("if (pictureWasSelected) BeginSourceImageDrag(", press);
        Assert.Contains("else BeginPictureSelectionPress(", press);
        Assert.Equal(1, Count(press, "BeginSourceImageDrag("));
    }

    [Fact]
    public void ADragAfterTheSelectingPressStartsTheSelectionBox()
    {
        string images = Viewer("PdfViewer.SourceImages.cs");
        string move = Between(images, "private bool MoveSourceImageDrag(MouseEventArgs e)", "private bool FinishSourceImageDrag()");

        Assert.Contains("_pictureSelectionPress is { } press", move);
        Assert.Contains("BeginSelectionMarquee(press.Start)", move);
        Assert.Contains("MinimumHorizontalDragDistance", move);
        // The release without a drag leaves the picture selected and starts nothing.
        string finish = Between(images, "private bool FinishSourceImageDrag()", "internal ImageAnnotation? SelectedSourceImageExt");
        Assert.Contains("_pictureSelectionPress is not null", finish);
    }

    [Fact]
    public void ShiftStillForcesTheSelectionBoxAndTheMarqueeCodeIsShared()
    {
        string annotations = Viewer("PdfViewer.Annotations.cs");

        Assert.Contains("if (!shiftSel && !_ocrRegionMode && TrySelectSourceImage(", annotations);
        Assert.Equal(2, Count(annotations, "BeginSelectionMarquee("));
        Assert.Contains("private void BeginSelectionMarquee(Point pos)", annotations);
    }
}
