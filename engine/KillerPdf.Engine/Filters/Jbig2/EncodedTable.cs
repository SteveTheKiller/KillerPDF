// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.

#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    using System.Collections.Generic;

    /// <summary>
    /// This class represents an encoded huffman table.
    /// </summary>
    internal sealed class EncodedTable : HuffmanTable
    {
        private readonly Table table;

        public EncodedTable(Table table)
        {
            this.table = table;
            ParseTable();
        }

        public void ParseTable()
        {
            var sis = table.GetSubInputStream();

            var codeTable = new List<Code>();

            int prefLen, rangeLen, rangeLow;
            int curRangeLow = table.HtLow;

            // Annex B.2 5) - decode table lines
            while (curRangeLow < table.HtHigh)
            {
                prefLen = (int)sis.ReadBits(table.HtPS);
                rangeLen = (int)sis.ReadBits(table.HtRS);
                rangeLow = curRangeLow;

                codeTable.Add(new Code(prefLen, rangeLen, rangeLow, false));

                curRangeLow += 1 << rangeLen;
            }

            // Annex B.2 6)
            prefLen = (int)sis.ReadBits(table.HtPS);

            //  Annex B.2 7) - lower range table line
            rangeLen = 32;
            rangeLow = table.HtLow - 1;
            codeTable.Add(new Code(prefLen, rangeLen, rangeLow, true));

            // Annex B.2 8)
            prefLen = (int)sis.ReadBits(table.HtPS);

            // Annex B.2 9) - upper range table line
            rangeLen = 32;
            rangeLow = table.HtHigh;
            codeTable.Add(new Code(prefLen, rangeLen, rangeLow, false));

            // Annex B.2 10) - out-of-band table line
            if (table.HtOutOfBand == 1)
            {
                prefLen = (int)sis.ReadBits(table.HtPS);
                codeTable.Add(new Code(prefLen, -1, -1, false));
            }

            InitTree(codeTable);
        }
    }
}
