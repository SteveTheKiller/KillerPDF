namespace KillerPDF.Services;

internal readonly record struct PinchZoomResult(
    double Zoom, double HorizontalOffset, double VerticalOffset);

internal static class PinchZoomMath
{
    internal static (double X, double Y) CaptureAnchor(
        double zoom, double horizontalOffset, double verticalOffset,
        double originX, double originY)
    {
        if (!double.IsFinite(zoom) || zoom <= 0) zoom = 1;
        return ((horizontalOffset + originX) / zoom,
            (verticalOffset + originY) / zoom);
    }

    internal static PinchZoomResult ApplyAtAnchor(
        double zoom, double anchorX, double anchorY, double originX, double originY)
    {
        double newHorizontal = anchorX * zoom - originX;
        double newVertical = anchorY * zoom - originY;
        return new PinchZoomResult(zoom,
            Math.Max(0, newHorizontal), Math.Max(0, newVertical));
    }

    internal static PinchZoomResult Apply(
        double oldZoom, double scale, double minimumZoom, double maximumZoom,
        double horizontalOffset, double verticalOffset, double originX, double originY)
    {
        if (!double.IsFinite(oldZoom) || oldZoom <= 0)
            oldZoom = minimumZoom;
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1;

        double zoom = Math.Max(minimumZoom, Math.Min(maximumZoom, oldZoom * scale));
        double appliedScale = zoom / oldZoom;
        double newHorizontal = (horizontalOffset + originX) * appliedScale - originX;
        double newVertical = (verticalOffset + originY) * appliedScale - originY;
        return new PinchZoomResult(zoom, Math.Max(0, newHorizontal), Math.Max(0, newVertical));
    }
}
