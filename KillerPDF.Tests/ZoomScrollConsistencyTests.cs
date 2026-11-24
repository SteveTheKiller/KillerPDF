using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ZoomScrollConsistencyTests
{
    [Fact]
    public void ContinuousToolbarZoomPreservesSpotInMiddleOfWindow()
    {
        // When zooming in Continuous view, the spot on the page at the center of the viewport
        // before zoom must remain at the center of the viewport after zoom, with the scroll
        // updated synchronously in the same layout pass right after UpdateLayout().
        Exception? failure = null;
        Window? window = null;
        var thread = new Thread(() =>
        {
            try
            {
                const double viewportWidth = 800.0;
                const double viewportHeight = 600.0;
                const double initialZoom = 1.0;
                const double initialVOffset = 400.0;
                const double initialHOffset = 100.0;

                // Viewport center origin
                const double originX = viewportWidth * 0.5;
                const double originY = viewportHeight * 0.5;

                // The spot of the document currently visible at the middle of the window:
                const double expectedCenterDocX = (initialHOffset + originX) / initialZoom;
                const double expectedCenterDocY = (initialVOffset + originY) / initialZoom;

                // Capture initial anchor
                (double anchorX, double anchorY) = PinchZoomMath.CaptureAnchor(
                    initialZoom, initialHOffset, initialVOffset, originX, originY);

                // Set up a ScrollViewer with scalable content to verify layout pass + scroll execution
                var scrollViewer = new ScrollViewer
                {
                    Width = viewportWidth,
                    Height = viewportHeight,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };
                var contentTransform = new ScaleTransform(initialZoom, initialZoom);
                var content = new Grid
                {
                    Width = 1000.0,
                    Height = 3000.0,
                    LayoutTransform = contentTransform
                };
                scrollViewer.Content = content;

                window = new Window
                {
                    Content = scrollViewer,
                    Width = viewportWidth + 50,
                    Height = viewportHeight + 50,
                    Opacity = 0,
                    ShowInTaskbar = false,
                    ShowActivated = false
                };
                window.Show();

                scrollViewer.Measure(new Size(viewportWidth, viewportHeight));
                scrollViewer.Arrange(new Rect(0, 0, viewportWidth, viewportHeight));
                scrollViewer.UpdateLayout();
                scrollViewer.ScrollToHorizontalOffset(initialHOffset);
                scrollViewer.ScrollToVerticalOffset(initialVOffset);
                scrollViewer.UpdateLayout();

                // 1) Zoom in to 1.5x
                double targetZoomIn = 1.5;
                PinchZoomResult anchoredIn = PinchZoomMath.ApplyAtAnchor(
                    targetZoomIn, anchorX, anchorY, originX, originY);

                // In the layout pass: scale transform updates, UpdateLayout runs, scroll applies synchronously
                contentTransform.ScaleX = targetZoomIn;
                contentTransform.ScaleY = targetZoomIn;
                scrollViewer.UpdateLayout();
                scrollViewer.ScrollToHorizontalOffset(anchoredIn.HorizontalOffset);
                scrollViewer.ScrollToVerticalOffset(anchoredIn.VerticalOffset);
                scrollViewer.UpdateLayout();

                // Verify immediate application in layout
                Assert.Equal(anchoredIn.HorizontalOffset, scrollViewer.HorizontalOffset, 2);
                Assert.Equal(anchoredIn.VerticalOffset, scrollViewer.VerticalOffset, 2);

                // Check that the exact same spot of the page stays in the middle of the window
                double centerDocXIn = (scrollViewer.HorizontalOffset + originX) / targetZoomIn;
                double centerDocYIn = (scrollViewer.VerticalOffset + originY) / targetZoomIn;
                Assert.Equal(expectedCenterDocX, centerDocXIn, 2);
                Assert.Equal(expectedCenterDocY, centerDocYIn, 2);

                // 2) Zoom out to 0.8x
                double targetZoomOut = 0.8;
                PinchZoomResult anchoredOut = PinchZoomMath.ApplyAtAnchor(
                    targetZoomOut, anchorX, anchorY, originX, originY);

                contentTransform.ScaleX = targetZoomOut;
                contentTransform.ScaleY = targetZoomOut;
                scrollViewer.UpdateLayout();
                scrollViewer.ScrollToHorizontalOffset(anchoredOut.HorizontalOffset);
                scrollViewer.ScrollToVerticalOffset(anchoredOut.VerticalOffset);
                scrollViewer.UpdateLayout();

                Assert.Equal(anchoredOut.HorizontalOffset, scrollViewer.HorizontalOffset, 2);
                Assert.Equal(anchoredOut.VerticalOffset, scrollViewer.VerticalOffset, 2);

                double centerDocXOut = (scrollViewer.HorizontalOffset + originX) / targetZoomOut;
                double centerDocYOut = (scrollViewer.VerticalOffset + originY) / targetZoomOut;
                Assert.Equal(expectedCenterDocX, centerDocXOut, 2);
                Assert.Equal(expectedCenterDocY, centerDocYOut, 2);
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Continuous zoom centering test timed out.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void TopEdgeAnchoringPreservesZeroVerticalOffset()
    {
        // At the top of the document (verticalOffset == 0), originY is 0 so the top does not jump.
        (double anchorX, double anchorY) = PinchZoomMath.CaptureAnchor(
            zoom: 1.0, horizontalOffset: 0.0, verticalOffset: 0.0,
            originX: 0.0, originY: 0.0);

        Assert.Equal(0.0, anchorX);
        Assert.Equal(0.0, anchorY);

        PinchZoomResult zoomIn = PinchZoomMath.ApplyAtAnchor(
            zoom: 2.0, anchorX: anchorX, anchorY: anchorY,
            originX: 0.0, originY: 0.0);

        Assert.Equal(0.0, zoomIn.HorizontalOffset);
        Assert.Equal(0.0, zoomIn.VerticalOffset);

        PinchZoomResult zoomOut = PinchZoomMath.ApplyAtAnchor(
            zoom: 0.5, anchorX: anchorX, anchorY: anchorY,
            originX: 0.0, originY: 0.0);

        Assert.Equal(0.0, zoomOut.HorizontalOffset);
        Assert.Equal(0.0, zoomOut.VerticalOffset);
    }

    [Fact]
    public void SuccessiveZoomIncrementsWithFixedAnchorDoNotDrift()
    {
        const double initialZoom = 1.0;
        const double hOffset = 150.0;
        const double vOffset = 350.0;
        const double originX = 500.0;
        const double originY = 400.0;

        (double anchorX, double anchorY) = PinchZoomMath.CaptureAnchor(
            initialZoom, hOffset, vOffset, originX, originY);

        const double expectedDocX = (hOffset + originX) / initialZoom;
        const double expectedDocY = (vOffset + originY) / initialZoom;

        double[] zoomLevels = [1.1, 1.25, 1.5, 1.75, 2.0, 1.8, 1.4, 1.0];
        foreach (double z in zoomLevels)
        {
            PinchZoomResult res = PinchZoomMath.ApplyAtAnchor(z, anchorX, anchorY, originX, originY);
            double centerDocX = (res.HorizontalOffset + originX) / z;
            double centerDocY = (res.VerticalOffset + originY) / z;
            Assert.Equal(expectedDocX, centerDocX, 5);
            Assert.Equal(expectedDocY, centerDocY, 5);
        }
    }

    [Fact]
    public void SetZoomRemainsUnanchoredForProgrammaticCallers()
    {
        // SetZoom has ~20 callers (tab restore, view mode switching, comparison restore, grid column changes).
        // It must NOT capture an anchor or scroll to the middle of the window.
        string viewport = ViewerSourcePin.Viewer("PdfViewer.Viewport.cs");
        string setZoom = ViewerSourcePin.Between(viewport, "internal void SetZoom(double level)", "internal void SetTrueZoom(");

        Assert.DoesNotContain("CaptureAnchor", setZoom);
        Assert.DoesNotContain("ApplyAtAnchor", setZoom);
        Assert.DoesNotContain("ScrollTo", setZoom);
        Assert.DoesNotContain("UpdateLayout", setZoom);
        Assert.DoesNotContain("Dispatcher.BeginInvoke", setZoom);
        Assert.DoesNotContain("_wheelZoomRevision", setZoom);
    }

    [Fact]
    public void ToolbarZoomAnchorsInSameLayoutPassWithOwnAnchor()
    {
        // 1) Toolbar/center zoom has its own anchor state separate from Ctrl+wheel cursor zoom.
        // 2) Scroll happens immediately after PagePreviewPanel.UpdateLayout() in the same pass.
        // 3) No Dispatcher.BeginInvoke or _wheelZoomRevision.
        string viewport = ViewerSourcePin.Viewer("PdfViewer.Viewport.cs");
        string zoomAnchored = ViewerSourcePin.Between(viewport, "internal void ZoomAnchoredToCenter(", "internal void ZoomIn_Click(");

        Assert.Contains("_toolbarZoomAnchorActive", zoomAnchored);
        Assert.DoesNotContain("_wheelZoomAnchorActive", zoomAnchored);
        Assert.Contains("PagePreviewPanel.UpdateLayout();", zoomAnchored);
        Assert.Contains("PagePreviewPanel.ScrollToHorizontalOffset(anchored.HorizontalOffset);", zoomAnchored);
        Assert.Contains("PagePreviewPanel.ScrollToVerticalOffset(anchored.VerticalOffset);", zoomAnchored);
        Assert.DoesNotContain("Dispatcher.BeginInvoke", zoomAnchored);
        Assert.DoesNotContain("_wheelZoomRevision", zoomAnchored);

        // Zoom in/out step delegates to ZoomAnchoredToCenter
        string stepZoom = ViewerSourcePin.Between(viewport, "internal void StepZoomByUser(bool zoomIn)", "private bool _syncingZoomBox;");
        Assert.Contains("ZoomAnchoredToCenter(_zoomLevel + step);", stepZoom);

        // Zoom box selection delegates through SetTrueZoom to ZoomAnchoredToCenter
        string zoomBoxSelection = ViewerSourcePin.Between(viewport, "internal void ZoomBox_SelectionChanged(", "// A secondary tile shares the primary's displayed width.");
        Assert.Contains("SetTrueZoom(z);", zoomBoxSelection);

        string setTrueZoom = ViewerSourcePin.Between(viewport, "internal void SetTrueZoom(double trueZoom)", "internal void ZoomAnchoredToCenter(");
        Assert.Contains("ZoomAnchoredToCenter(trueZoom / zf);", setTrueZoom);

        // ZoomBoxWheel delegates to ZoomAnchoredToCenter
        string zoomSource = ViewerSourcePin.Viewer("PdfViewer.Zoom.cs");
        string zoomBoxWheel = ViewerSourcePin.Between(zoomSource, "internal void ZoomBoxWheel(MouseWheelEventArgs e)", "private System.Windows.Threading.DispatcherTimer? _zoomSettleTimer;");
        Assert.Contains("ZoomAnchoredToCenter(targetZoom, lite: true);", zoomBoxWheel);
        Assert.DoesNotContain("Dispatcher.BeginInvoke", zoomBoxWheel);
        Assert.DoesNotContain("_wheelZoomRevision", zoomBoxWheel);
    }
}
