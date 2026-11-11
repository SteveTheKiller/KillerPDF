using System.Windows;
using System.Windows.Media;

namespace KillerPDF.Controls
{
    /// <summary>
    /// Decides whether a press on an annotation has become a drag. The pointer is measured on the screen, not on the
    /// page canvas: opening the text bar or a scroll bar during the press re-fits the page under a pointer that has not
    /// moved, and that shifts every canvas coordinate. A press that never travels the system drag distance is a click.
    /// </summary>
    internal static class DragStartGate
    {
        /// <summary>True once the pointer is at least the minimum drag distance (in device pixels) from the press point on either axis.</summary>
        internal static bool HasTravelled(Point pressOnScreen, Point nowOnScreen, double minimumX, double minimumY) =>
            Math.Abs(nowOnScreen.X - pressOnScreen.X) >= minimumX || Math.Abs(nowOnScreen.Y - pressOnScreen.Y) >= minimumY;

        /// <summary>True while a press has not become a drag. A canvas with no presentation source (torn down while the
        /// press is held) has no screen to measure on, so the press is still a click and never starts a move.</summary>
        internal static bool StillAClick(UIElement relativeTo, Point onScreenPress, Point pageNow)
        {
            if (PresentationSource.FromVisual(relativeTo) is null) return true;
            DpiScale dpi = VisualTreeHelper.GetDpi(relativeTo);
            return !HasTravelled(onScreenPress, relativeTo.PointToScreen(pageNow),
                SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX,
                SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY);
        }

        /// <summary>True when a gesture running on this canvas must be ended: the canvas has no presentation source, so
        /// no pointer coordinate measured on it means anything.</summary>
        internal static bool ShouldAbandonDrag(UIElement? gestureCanvas) =>
            gestureCanvas is null || PresentationSource.FromVisual(gestureCanvas) is null;
    }
}
