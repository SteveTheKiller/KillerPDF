using System.Windows;

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
    }
}
