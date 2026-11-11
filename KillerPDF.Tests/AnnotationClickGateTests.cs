using System.Windows;
using KillerPDF.Controls;
using Xunit;
using static KillerPDF.Tests.ViewerSourcePin;

namespace KillerPDF.Tests;

public sealed class AnnotationClickGateTests
{
    // 4 device pixels is the Windows default drag distance at 100 percent scale.
    [Theory]
    [InlineData(540, 537, 540, 537, false)]   // a pointer that stood still while the page re-fit under it
    [InlineData(540, 537, 542, 539, false)]   // jitter inside the drag distance
    [InlineData(540, 537, 544, 537, true)]    // exactly the drag distance on one axis
    [InlineData(540, 537, 540, 533, true)]
    [InlineData(540, 537, 530, 547, true)]
    public void AnAnnotationPressBecomesADragOnlyOnceThePointerTravelsOnTheScreen(
        double pressX, double pressY, double nowX, double nowY, bool expected) =>
        Assert.Equal(expected, DragStartGate.HasTravelled(new Point(pressX, pressY), new Point(nowX, nowY), 4, 4));

    [Fact]
    public void TheDragDistanceScalesWithTheScreenDpi()
    {
        Assert.False(DragStartGate.HasTravelled(new Point(0, 0), new Point(5, 0), 6, 6));
        Assert.True(DragStartGate.HasTravelled(new Point(0, 0), new Point(6, 0), 6, 6));
    }

    // SOURCE PIN for the wiring. The viewer is not compiled into this project; the window evidence is
    // --verify-replacement-selection on an owned UI VM.
    [Fact]
    public void ThePressRecordsItsScreenPointTheMoveWaitsForTheDragDistanceAndAClickReleaseChangesNothing()
    {
        string annotations = Viewer("PdfViewer.Annotations.cs");
        string press = Between(annotations, "_annotationDragUndo = null;\r\n                                        _annotDragPressScreen", "_dragAnnot = pa;");
        Assert.Contains("_annotDragPressScreen = _activeCanvas.PointToScreen(pos);", press.Replace("\r\n", "\n"));

        string move = Between(annotations, "if (_isDraggingAnnot && _dragAnnot is not null)", "double dx = pos.X - _dragAnnotStart.X;");
        Assert.Contains("if (AnnotDragStillAClick(gc, pressOnScreen, e.GetPosition(gc))) return;", move);

        string release = Between(annotations, "// Finish annotation drag-to-move", "var da = _dragAnnot;");
        Assert.Contains("if (_annotDragPressScreen is not null)", release);
        Assert.DoesNotContain("MarkDirty()", release);
        int click = release.IndexOf("if (_annotDragPressScreen is not null)", StringComparison.Ordinal);
        Assert.True(release.IndexOf("return;", click, StringComparison.Ordinal) > click);
    }

    // SOURCE PIN. The viewer is not compiled into this project, so the helper cannot be called here with a canvas that
    // was never shown; PointToScreen throws on a visual with no presentation source, so both callers check first.
    [Fact]
    public void ScreenPointsAreNotTakenFromACanvasThatHasNoPresentationSource()
    {
        string annotations = Viewer("PdfViewer.Annotations.cs").Replace("\r\n", "\n");
        string gate = Between(annotations, "private static bool AnnotDragStillAClick(", "private void CaptureAnnotationDragUndo(");
        int guard = gate.IndexOf("PresentationSource.FromVisual(relativeTo) is null", StringComparison.Ordinal);
        Assert.True(guard >= 0, "AnnotDragStillAClick does not check for a presentation source.");
        Assert.True(guard < gate.IndexOf("PointToScreen", StringComparison.Ordinal));

        string marquee = Between(annotations, "internal void UpdateMarquee(", "PlaceMarquee(_selectRect, MarqueeLayer, a, b);");
        int marqueeGuard = marquee.IndexOf("PresentationSource.FromVisual(gc) is null", StringComparison.Ordinal);
        Assert.True(marqueeGuard >= 0, "UpdateMarquee does not check for a presentation source.");
        Assert.True(marqueeGuard < marquee.IndexOf("PointToScreen", StringComparison.Ordinal));
    }
}
