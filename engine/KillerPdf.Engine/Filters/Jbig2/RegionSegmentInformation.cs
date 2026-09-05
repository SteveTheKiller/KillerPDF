// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.
#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    /// <summary>
    /// This class represents the "Region segment information" field, 7.4.1(page 50).
    /// Every region segment data starts with this part.
    /// </summary>
    internal sealed class RegionSegmentInformation : ISegmentData
    {
        private readonly SubInputStream subInputStream;

        /// <summary>
        /// Region segment bitmap width, 7.4.1.1
        /// </summary>
        public int BitmapWidth { get; set; }

        /// <summary>
        /// Region segment bitmap height, 7.4.1.2 
        /// </summary>
        public int BitmapHeight { get; set; }

        /// <summary>
        /// Region segment bitmap X location, 7.4.1.3
        /// </summary>
        public int X { get; private set; }

        /// <summary>
        /// Region segment bitmap Y location, 7.4.1.4
        /// </summary>
        public int Y { get; private set; }

        /// <summary>
        /// Region segment flags, 7.4.1.5
        /// </summary>
        public CombinationOperator CombinationOperator { get; private set; }

        public RegionSegmentInformation(SubInputStream subInputStream)
        {
            this.subInputStream = subInputStream;
        }

        public RegionSegmentInformation(int bitmapWidth, int bitmapHeight)
        {
            BitmapWidth = bitmapWidth;
            BitmapHeight = bitmapHeight;
        }

        public void ParseHeader()
        {
            BitmapWidth = (int)(subInputStream.ReadBits(32) & 0xffffffff);
            BitmapHeight = (int)(subInputStream.ReadBits(32) & 0xffffffff);
            X = (int)(subInputStream.ReadBits(32) & 0xffffffff);
            Y = (int)(subInputStream.ReadBits(32) & 0xffffffff);

            // Bit 3-7
            subInputStream.ReadBits(5); // Dirty read... reserved bits are 0

            // Bit 0-2
            ReadCombinationOperator();
        }

        private void ReadCombinationOperator()
        {
            CombinationOperator = CombinationOperators.TranslateOperatorCodeToEnum((short)(subInputStream.ReadBits(3) & 0xf));
        }

        public void Init(SegmentHeader header, SubInputStream sis)
        {
        }
    }
}
