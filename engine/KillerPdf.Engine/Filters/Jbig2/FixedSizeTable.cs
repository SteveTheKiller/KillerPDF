// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.

#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    using System.Collections.Generic;

    /// <summary>
    /// This class represents a fixed size huffman table.
    /// </summary>
    internal sealed class FixedSizeTable : HuffmanTable
    {
        public FixedSizeTable(List<Code> runCodeTable)
        {
            InitTree(runCodeTable);
        }
    }
}
