using System.Windows;
using System.Windows.Media;

namespace KillerPDF.Controls
{
    /// <summary>Where a ThemeMotif element sits in its window (Controls/ThemeMotif.cs).</summary>
    internal static class MotifPlacement
    {
        /// <summary>
        /// The element's on-screen bounds in its window root, and the local transform that undoes a mirror on the way
        /// there. A mirrored element (a document tab flipped below its pane by ScaleTransform(1,-1)) has its local origin
        /// at its on-screen bottom, so its window patch is read from its on-screen bounds and the brush is flipped back in
        /// local space. Without this the art is upside down, taken from a patch one tab height too low, and breaks at the
        /// seam with the pane it continues.
        /// </summary>
        internal static (Rect Bounds, Transform? Unflip) PlaceInWindow(FrameworkElement element, Visual root)
        {
            var local = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            var toRoot = element.TransformToAncestor(root);
            Rect bounds = toRoot.TransformBounds(local);
            Point origin = toRoot.Transform(new Point(0, 0));
            double sx = toRoot.Transform(new Point(1, 0)).X < origin.X ? -1 : 1;
            double sy = toRoot.Transform(new Point(0, 1)).Y < origin.Y ? -1 : 1;
            if (sx > 0 && sy > 0) return (bounds, null);
            var unflip = new ScaleTransform(sx, sy, local.Width / 2, local.Height / 2);
            unflip.Freeze();
            return (bounds, unflip);
        }
    }
}
