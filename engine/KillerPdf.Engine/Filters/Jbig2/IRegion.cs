// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.
#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    /// <summary>
    /// Interface for all JBIG2 region segments.
    /// </summary>
    internal interface IRegion : ISegmentData
    {
        /// <summary>
        /// Returns <see cref="RegionSegmentInformation"/> about this region.
        /// </summary>
        RegionSegmentInformation RegionInfo { get; }

        /// <summary>
        /// Decodes and returns a regions content.
        /// </summary>
        /// <returns>The decoded region as <see cref="Jbig2Bitmap"/>.</returns>
        /// <exception cref="InvalidHeaderValueException">if the segment header value is invalid.</exception>
        /// <exception cref="IntegerMaxValueException">if the maximum value limit of an integer is exceeded.</exception>
        /// <exception cref="IOException">if an underlying IO operation fails.</exception>
        Jbig2Bitmap GetRegionBitmap();
    }
}
