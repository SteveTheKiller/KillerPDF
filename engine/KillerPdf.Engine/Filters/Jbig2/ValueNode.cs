// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.
#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    using static HuffmanTable;

    /// <summary>
    /// Represents a value node in a Huffman tree. It is a leaf of a tree.
    /// </summary>
    internal sealed class ValueNode(HuffmanTable.Code c) : Node
    {
        private readonly int rangeLength = c.RangeLength;
        private readonly int rangeLow = c.RangeLow;
        private readonly bool isLowerRange = c.IsLowerRange;

        public override long Decode(IImageInputStream iis)
        {
            if (isLowerRange)
            {
                // B.4 4)
                return rangeLow - iis.ReadBits(rangeLength);
            }

            // B.4 5)
            return rangeLow + iis.ReadBits(rangeLength);
        }

        internal static string BitPattern(int v, int len)
        {
            var result = new char[len]; // Todo - stackalloc?
            for (int i = 1; i <= len; i++)
            {
                result[i - 1] = (v >> len - i & 1) != 0 ? '1' : '0';
            }

            return new string(result);
        }
    }
}
