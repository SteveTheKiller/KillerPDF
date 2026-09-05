// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.

#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    using System.Collections.Generic;

    /// <summary>
    /// Interface for all JBIG2 dictionaries segments.
    /// </summary>
    internal interface IJbigDictionary : ISegmentData
    {
        /// <summary>
        /// Decodes a dictionary segment and returns the result.
        /// </summary>
        /// <returns>A list of <see cref="Jbig2Bitmap"/>s as a result of the decoding process of dictionary segments.</returns>
        /// <exception cref="InvalidHeaderValueException">if the segment header value is invalid.</exception>
        /// <exception cref="IntegerMaxValueException">if the maximum value limit of an integer is exceeded.</exception>
        /// <exception cref="IOException">if an underlying IO operation fails.</exception>
        List<Jbig2Bitmap> GetDictionary();
    }
}
