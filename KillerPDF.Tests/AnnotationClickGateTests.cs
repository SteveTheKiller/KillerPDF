using System.Windows;
using System.Windows.Controls;
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
        Assert.Contains("if (DragStartGate.StillAClick(gc, pressOnScreen, e.GetPosition(gc))) return;", move);

        string release = Between(annotations, "// Finish annotation drag-to-move", "var da = _dragAnnot;");
        Assert.Contains("if (_annotDragPressScreen is not null)", release);
        Assert.DoesNotContain("MarkDirty()", release);
        int click = release.IndexOf("if (_annotDragPressScreen is not null)", StringComparison.Ordinal);
        Assert.True(release.IndexOf("return;", click, StringComparison.Ordinal) > click);
    }

    // A canvas torn down while the press is held has no screen to measure on. The helper must not throw, and must
    // not report a drag: the press is still a click, and the move handler ends the gesture instead of moving.
    [Fact]
    public void APressOnACanvasWithNoPresentationSourceIsStillAClick() =>
        OnSta(() => Assert.True(DragStartGate.StillAClick(new Canvas(), new Point(540, 537), new Point(900, 900))));

    [Fact]
    public void AGestureOnACanvasWithNoPresentationSourceIsAbandoned() =>
        OnSta(() =>
        {
            Assert.True(DragStartGate.ShouldAbandonDrag(new Canvas()));
            Assert.True(DragStartGate.ShouldAbandonDrag(null));
        });

    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    // SOURCE PIN (unverified in a window: the viewer is not compiled into this project). PointToScreen and
    // GetPosition throw on a visual with no presentation source, so the move handler ends a drag on a detached
    // gesture canvas before its first GetPosition, and UpdateMarquee checks both canvases before any screen point.
    [Fact]
    public void ScreenPointsAreNotTakenFromACanvasThatHasNoPresentationSource()
    {
        string annotations = Viewer("PdfViewer.Annotations.cs").Replace("\r\n", "\n");

        string move = Between(annotations, "if (e.LeftButton != MouseButtonState.Pressed && (_isDraggingAnnot || _isResizingSig))",
            "var pos = e.GetPosition(gc);");
        int abandon = move.IndexOf("if (_isDraggingAnnot && DragStartGate.ShouldAbandonDrag(gc))", StringComparison.Ordinal);
        Assert.True(abandon >= 0, "The move handler does not end a drag on a canvas with no presentation source.");
        string abandoned = move[abandon..];
        Assert.True(abandoned.IndexOf("FinishStuckGesture();", StringComparison.Ordinal) > 0);
        Assert.True(abandoned.IndexOf("return;", StringComparison.Ordinal) > abandoned.IndexOf("FinishStuckGesture();", StringComparison.Ordinal));
        Assert.DoesNotContain("e.GetPosition(", move);

        Assert.DoesNotContain("AnnotDragStillAClick", annotations);
        Assert.Contains("if (DragStartGate.StillAClick(gc, pressOnScreen, e.GetPosition(gc))) return;", annotations);

        string marquee = Between(annotations, "internal void UpdateMarquee(", "foreach (var canvas in _nativeMarqueePages)");
        int gcGuard = marquee.IndexOf("PresentationSource.FromVisual(gc) is null", StringComparison.Ordinal);
        Assert.True(gcGuard >= 0, "UpdateMarquee does not check for a presentation source.");
        Assert.True(gcGuard < marquee.IndexOf("PointToScreen", StringComparison.Ordinal));
    }
}
