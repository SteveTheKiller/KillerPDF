// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.
#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    /// <summary>
    /// This class represents a "Table" segment. It handles custom tables, see Annex B.
    /// </summary>
    internal sealed class Table : ISegmentData
    {
        private SubInputStream subInputStream;

        // Code table flags, B.2.1, page 87
        public int HtOutOfBand { get; private set; }
        public int HtPS { get; private set; }
        public int HtRS { get; private set; }

        // Code table lowest value, B.2.2, page 87
        public int HtLow { get; private set; }

        // Code table highest value, B.2.3, page 87
        public int HtHigh { get; private set; }

        private void ParseHeader()
        {
            int bit;

            // Bit 7
            if ((bit = subInputStream.ReadBit()) == 1)
            {
                throw new InvalidHeaderValueException(
                    "B.2.1 Code table flags: Bit 7 must be zero, but was " + bit);
            }

            // Bit 4-6
            HtRS = (int)(subInputStream.ReadBits(3) + 1 & 0xf);

            // Bit 1-3
            HtPS = (int)(subInputStream.ReadBits(3) + 1 & 0xf);

            // Bit 0
            HtOutOfBand = subInputStream.ReadBit();

            HtLow = (int)subInputStream.ReadBits(32);
            HtHigh = (int)subInputStream.ReadBits(32);
        }

        public void Init(SegmentHeader header, SubInputStream sis)
        {
            subInputStream = sis;
            ParseHeader();
        }

        public SubInputStream GetSubInputStream()
        {
            return subInputStream;
        }
    }
}
