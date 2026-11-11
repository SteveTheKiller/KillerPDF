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

    // Resizes and the text-edit handle drag read the pointer from the gesture canvas just like a move does, so all
    // three end on a canvas with no presentation source, and a gesture that is not running never ends.
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    public void EveryGestureThatReadsThePointerIsAbandonedOnADetachedCanvas(bool moving, bool resizing, bool handle, bool expected) =>
        OnSta(() =>
        {
            Assert.Equal(expected, DragStartGate.ShouldAbandonGesture(new Canvas(), moving, resizing, handle));
            Assert.Equal(expected, DragStartGate.ShouldAbandonGesture(null, moving, resizing, handle));
        });

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]    // the press already travelled: it changed the page
    [InlineData(false, true, false)]    // not a move at all
    public void OnlyAMovePressThatNeverTravelledChangesNothing(bool moving, bool pressed, bool expected) =>
        Assert.Equal(expected, DragStartGate.IsUntravelledPress(moving, pressed ? new Point(540, 537) : null));

    // SOURCE PIN (unverified in a window). The abandon guard covers every gesture that reads its position from the
    // canvas, and ending a gesture clears the text-edit handle flag and skips the changed mark and the undo step for
    // a press that never travelled, for both callers of FinishStuckGesture.
    [Fact]
    public void EndingAStuckGestureClearsTheHandleDragAndKeepsAnUntravelledPressClean()
    {
        string annotations = Viewer("PdfViewer.Annotations.cs").Replace("\r\n", "\n");

        string move = Between(annotations, "if (e.LeftButton != MouseButtonState.Pressed && (_isDraggingAnnot || _isResizingSig))",
            "var pos = e.GetPosition(gc);");
        Assert.Contains("DragStartGate.ShouldAbandonGesture(gc, _isDraggingAnnot, _isResizingSig, _draggingTextEditHandle)", move);

        string finish = Between(annotations, "private void FinishStuckGesture()", "private void AddAnnotation(");
        int computed = finish.IndexOf("bool untravelledPress = DragStartGate.IsUntravelledPress(_isDraggingAnnot, _annotDragPressScreen);", StringComparison.Ordinal);
        Assert.True(computed >= 0, "FinishStuckGesture does not work out whether the press never travelled.");
        Assert.True(computed < finish.IndexOf("_annotDragPressScreen = null;", StringComparison.Ordinal));
        int undo = finish.IndexOf("if (untravelledPress) _annotationDragUndo = null;", StringComparison.Ordinal);
        Assert.True(undo > computed, "An untravelled press still keeps an undo step.");
        Assert.True(undo < finish.IndexOf("CommitAnnotationDragUndo();", StringComparison.Ordinal));
        Assert.Contains("_draggingTextEditHandle = false;", finish);
        Assert.Contains("if (!unchangedResize && !untravelledPress) MarkDirty();", finish);
    }
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
        int abandon = move.IndexOf("if (DragStartGate.ShouldAbandonGesture(gc, _isDraggingAnnot, _isResizingSig, _draggingTextEditHandle))", StringComparison.Ordinal);
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
        int layerGuard = marquee.IndexOf("PresentationSource.FromVisual(MarqueeLayer) is not { } sharedSource", StringComparison.Ordinal);
        Assert.True(layerGuard >= 0, "UpdateMarquee does not check the marquee layer for a presentation source.");
        Assert.True(layerGuard < marquee.IndexOf("PlaceMarquee(_selectRect, MarqueeLayer", StringComparison.Ordinal));
        Assert.True(layerGuard < marquee.IndexOf("MarqueeLayer.PointToScreen", StringComparison.Ordinal));
        Assert.Equal(1, marquee.Split("PresentationSource.FromVisual(MarqueeLayer)").Length - 1);
    }
}
