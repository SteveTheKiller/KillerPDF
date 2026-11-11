using Xunit;
using static KillerPDF.Tests.ViewerSourcePin;

namespace KillerPDF.Tests;

// SOURCE PINS (UI2-004 with viewer BF-014). The viewer is not compiled into this project; behavior evidence is the
// written window check in the 1.9.3 notes (needs-vm).
public sealed class ResizeHandleUndoPinTests
{
    // A press on a corner handle used to push a page snapshot at once, so a click without movement left an undo step
    // that changed nothing, cleared Redo and could push the oldest real step out of the 20 step limit.
    [Fact]
    public void ACornerPressPushesNoUndoStepUntilThePointerHasMoved()
    {
        string annotations = Viewer("PdfViewer.Annotations.cs");
        string press = Between(annotations, "_resizeCorner = hd.Tag as string ?? \"SE\";", "// Anchor on the opposite corner so it stays put");
        string move = Between(annotations, "// Text box resize drag: width follows the dragged corner", "// Signature resize drag");

        Assert.DoesNotContain("PushPageSnapshotUndo(", press);
        Assert.Contains("_resizeUndoArmed = true;", press);
        Assert.Contains("_resizeStartBounds = AnnotBounds(_selectedAnnotation);", press);
        // The snapshot is taken before the first change of the gesture, in the handler that runs ahead of every resize branch.
        string before = Between(annotations, "// Live text-edit box resize (mid-edit corner handles)", "// Text box resize drag: width follows the dragged corner");
        Assert.Contains("CaptureResizeUndoOnFirstMove(pos);", before);
        Assert.DoesNotContain("CaptureAnnotationDragUndo(", move);
    }

    [Fact]
    public void AReleaseKeepsTheUndoStepAndTheDirtyMarkOnlyWhenTheSizeChanged()
    {
        string annotations = Viewer("PdfViewer.Annotations.cs");
        string finish = Between(annotations, "// Finish text box resize", "// Measure tool: a dragged distance finishes");

        Assert.Equal(4, Count(finish, "FinishResizeUndo("));
        Assert.DoesNotContain("MarkDirty()", finish);
        string helper = Between(annotations, "private void FinishResizeUndo(PageAnnotation annotation)", "private double CanvasLetterSpacing(");
        Assert.Contains("AnnotBounds(annotation) != _resizeStartBounds", helper);
        Assert.Contains("_annotationDragUndo = null;", helper);
        Assert.Contains("CommitAnnotationDragUndo();", helper);
        Assert.Contains("MarkDirty();", helper);
    }
}
