// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.
#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    using System.Collections.Generic;

    /// <summary>
    /// This class stores segments, that aren't associated to a page.
    /// If the data is embedded in another format, for example PDF, this segments might be stored separately in the file.
    /// This segments will be decoded on demand and all results are stored in the document object and can be retrieved from
    /// there.
    /// </summary>
    internal sealed class Jbig2Globals
    {
        // This map contains all segments, that are not associated with a page. The key is the segment number.
        private readonly Dictionary<int, SegmentHeader> globalSegments = new Dictionary<int, SegmentHeader>();

        internal SegmentHeader GetSegment(int segmentNumber)
        {
            return globalSegments[segmentNumber];
        }

        internal void AddSegment(int segmentNumber, SegmentHeader segment)
        {
            globalSegments[segmentNumber] = segment;
        }
    }
}
