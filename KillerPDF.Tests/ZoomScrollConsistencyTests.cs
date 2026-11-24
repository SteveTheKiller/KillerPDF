using System;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ZoomScrollConsistencyTests
{
    [Fact]
    public void CenterAnchoringScalesOffsetsAroundViewportCenter()
    {
        // Viewport: 800 x 600. Center is at (400, 300).
        // Current offsets: (200, 400) at zoom 1.0.
        // Document coordinate at viewport center: (200 + 400) / 1.0 = 600, (400 + 300) / 1.0 = 700.
        const double originX = 400.0;
        const double originY = 300.0;
        (double anchorX, double anchorY) = PinchZoomMath.CaptureAnchor(
            zoom: 1.0, horizontalOffset: 200.0, verticalOffset: 400.0,
            originX: originX, originY: originY);

        Assert.Equal(600.0, anchorX, 6);
        Assert.Equal(700.0, anchorY, 6);

        // Zoom in to 1.5x
        PinchZoomResult zoomIn = PinchZoomMath.ApplyAtAnchor(
            zoom: 1.5, anchorX: anchorX, anchorY: anchorY,
            originX: originX, originY: originY);

        Assert.Equal(500.0, zoomIn.HorizontalOffset, 6);
        Assert.Equal(750.0, zoomIn.VerticalOffset, 6);

        // Center point in document coordinates must remain identical
        Assert.Equal(600.0, (zoomIn.HorizontalOffset + originX) / 1.5, 6);
        Assert.Equal(700.0, (zoomIn.VerticalOffset + originY) / 1.5, 6);

        // Zoom out to 0.8x
        PinchZoomResult zoomOut = PinchZoomMath.ApplyAtAnchor(
            zoom: 0.8, anchorX: anchorX, anchorY: anchorY,
            originX: originX, originY: originY);

        Assert.Equal(80.0, zoomOut.HorizontalOffset, 6);
        Assert.Equal(260.0, zoomOut.VerticalOffset, 6);

        Assert.Equal(600.0, (zoomOut.HorizontalOffset + originX) / 0.8, 6);
        Assert.Equal(700.0, (zoomOut.VerticalOffset + originY) / 0.8, 6);
    }

    [Fact]
    public void TopEdgeAnchoringPreservesZeroVerticalOffset()
    {
        // At the top of the document (verticalOffset == 0), originY is 0.
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
    public void SetZoomAnchorsViewportCenterAndQueuesScroll()
    {
        string viewport = ViewerSourcePin.Viewer("PdfViewer.Viewport.cs");
        string setZoom = ViewerSourcePin.Between(viewport, "internal void SetZoom(double level)", "internal void SetTrueZoom(");

        Assert.Contains("double oldZoom = _zoomLevel;", setZoom);
        Assert.Contains("PinchZoomMath.CaptureAnchor(", setZoom);
        Assert.Contains("PinchZoomMath.ApplyAtAnchor(", setZoom);
        Assert.Contains("_wheelZoomAnchorActive = true;", setZoom);
        Assert.Contains("StartZoomSettleTimer();", setZoom);
        Assert.Contains("Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded", setZoom);
        Assert.Contains("revision != _wheelZoomRevision", setZoom);
        Assert.Contains("PagePreviewPanel.ScrollToHorizontalOffset(anchored.HorizontalOffset);", setZoom);
        Assert.Contains("PagePreviewPanel.ScrollToVerticalOffset(anchored.VerticalOffset);", setZoom);
        Assert.Contains("_gridScrollToPage = _currentPage;", setZoom);
        Assert.DoesNotContain("_doc", setZoom);
    }

    [Fact]
    public void ZoomBoxWheelAnchorsViewportCenterAndQueuesScroll()
    {
        string zoomSource = ViewerSourcePin.Viewer("PdfViewer.Zoom.cs");
        string zoomBoxWheel = ViewerSourcePin.Between(zoomSource, "internal void ZoomBoxWheel(MouseWheelEventArgs e)", "private System.Windows.Threading.DispatcherTimer? _zoomSettleTimer;");

        Assert.Contains("PinchZoomMath.CaptureAnchor(", zoomBoxWheel);
        Assert.Contains("PinchZoomMath.ApplyAtAnchor(", zoomBoxWheel);
        Assert.Contains("_wheelZoomAnchorActive = true;", zoomBoxWheel);
        Assert.Contains("StartZoomSettleTimer();", zoomBoxWheel);
        Assert.Contains("Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded", zoomBoxWheel);
        Assert.Contains("revision != _wheelZoomRevision", zoomBoxWheel);
        Assert.Contains("PagePreviewPanel.ScrollToHorizontalOffset(anchored.HorizontalOffset);", zoomBoxWheel);
        Assert.Contains("PagePreviewPanel.ScrollToVerticalOffset(anchored.VerticalOffset);", zoomBoxWheel);
        Assert.DoesNotContain("_doc", zoomBoxWheel);
    }
}
