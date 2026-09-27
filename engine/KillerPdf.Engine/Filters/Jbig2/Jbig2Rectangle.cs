// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.
#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    /// <summary>
    /// Create a new <see cref="Jbig2Rectangle"/>.
    /// </summary>
    /// <param name="x">The x-coordinate of the upper-left corner of the rectangle.</param>
    /// <param name="y">The y-coordinate of the upper-left corner of the rectangle.</param>
    /// <param name="width">The width of the rectangle.</param>
    /// <param name="height">The height of the rectangle.</param>
    internal readonly struct Jbig2Rectangle(int x, int y, int width, int height)
    {
        /// <summary>
        /// The x-coordinate of the upper-left corner of the rectangle.
        /// </summary>
        public int X { get; } = x;

        /// <summary>
        /// The y-coordinate of the upper-left corner of the rectangle.
        /// </summary>
        public int Y { get; } = y;

        /// <summary>
        /// The width of the rectangle.
        /// </summary>
        public int Width { get; } = width;

        /// <summary>
        /// The height of the rectangle.
        /// </summary>
        public int Height { get; } = height;
    }
}
