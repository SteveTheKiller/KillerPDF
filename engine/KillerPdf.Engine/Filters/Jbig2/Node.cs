// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.
#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    /// <summary>
    /// Base class for all nodes in a Huffman tree.
    /// </summary>
    internal abstract class Node
    {
        public abstract long Decode(IImageInputStream iis);
    }
}
