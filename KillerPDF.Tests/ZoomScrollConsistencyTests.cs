using System;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ZoomScrollConsistencyTests
{
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

        // Zoom box selection anchors through SetTrueZoomAnchored. SetTrueZoom stays unanchored, because the
        // view mode switch, Ctrl+0 and Ctrl+1 call it too.
        string zoomBoxSelection = ViewerSourcePin.Between(viewport, "internal void ZoomBox_SelectionChanged(", "// A secondary tile shares the primary's displayed width.");
        Assert.Contains("SetTrueZoomAnchored(z);", zoomBoxSelection);

        string setTrueZoom = ViewerSourcePin.Between(viewport, "internal void SetTrueZoom(double trueZoom)", "private void SetTrueZoomAnchored(");
        Assert.Contains("SetZoom(trueZoom / zf);", setTrueZoom);
        Assert.DoesNotContain("ZoomAnchoredToCenter", setTrueZoom);

        string setTrueZoomAnchored = ViewerSourcePin.Between(viewport, "private void SetTrueZoomAnchored(", "internal void ZoomAnchoredToCenter(");
        Assert.Contains("ZoomAnchoredToCenter(trueZoom / zf);", setTrueZoomAnchored);

        // ZoomBoxWheel delegates to ZoomAnchoredToCenter
        string zoomSource = ViewerSourcePin.Viewer("PdfViewer.Zoom.cs");
        string zoomBoxWheel = ViewerSourcePin.Between(zoomSource, "internal void ZoomBoxWheel(MouseWheelEventArgs e)", "private System.Windows.Threading.DispatcherTimer? _zoomSettleTimer;");
        Assert.Contains("ZoomAnchoredToCenter(targetZoom, lite: true);", zoomBoxWheel);
        Assert.DoesNotContain("Dispatcher.BeginInvoke", zoomBoxWheel);
        Assert.DoesNotContain("_wheelZoomRevision", zoomBoxWheel);
    }
}
