// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.
#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    using static HuffmanTable;

    /// <summary>
    /// Represents an out of band node in a Huffman tree.
    /// </summary>
    internal sealed class OutOfBandNode : Node
    {
        public OutOfBandNode(Code c)
        {
        }

        public override long Decode(IImageInputStream iis)
        {
            return long.MaxValue;
        }
    }
}
