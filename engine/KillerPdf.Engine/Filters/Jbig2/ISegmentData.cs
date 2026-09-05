// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.
#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    /// <summary>
    /// Interface for all data parts of segments.
    /// </summary>
    internal interface ISegmentData
    {
        /// <summary>
        /// Parse the stream and read information of header.
        /// </summary>
        /// <param name="header">The segments' header (to make referred-to segments available in data part).</param>
        /// <param name="sis">Wrapped { @code ImageInputStream} into {@code SubInputStream}.</param>
        /// <exception cref="InvalidHeaderValueException">if the segment header value is invalid.</exception>
        /// <exception cref="IntegerMaxValueException">if the maximum value limit of an integer is exceeded.</exception>
        /// <exception cref="IOException">if an underlying IO operation fails.</exception>
        void Init(SegmentHeader header, SubInputStream sis);
    }
}
