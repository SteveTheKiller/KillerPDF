// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.
#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    /// <summary>
    /// This class represents the segment type "Page information", 7.4.8 (page 73).
    /// </summary>
    internal sealed class PageInformation : ISegmentData
    {
        private SubInputStream subInputStream;

        // Page bitmap width, four bytes, 7.4.8.1
        public int BitmapWidth { get; private set; }

        // Page bitmap height, four bytes, 7.4.8.2
        public int BitmapHeight { get; private set; }

        // Page X resolution, four bytes, 7.4.8.3
        public int ResolutionX { get; private set; }

        // Page Y resolution, four bytes, 7.4.8.4
        public int ResolutionY { get; private set; }

        // Page segment flags, one byte, 7.4.8.5
        public bool IsCombinationOperatorOverrideAllowed { get; private set; }

        public CombinationOperator CombinationOperator { get; private set; }

        public bool RequiresAuxiliaryBuffer { get; private set; }

        public short DefaultPixelValue { get; private set; }

        public bool MightContainRefinements { get; private set; }

        public bool IsLossless { get; private set; }

        // Page striping information, two byte, 7.4.8.6
        public bool IsStriped { get; private set; }

        public short MaxStripeSize { get; private set; }

        private void ParseHeader()
        {
            ReadWidthAndHeight();
            ReadResolution();

            // Bit 7
            subInputStream.ReadBit(); // dirty read

            // Bit 6
            ReadCombinationOperatorOverrideAllowed();

            // Bit 5
            ReadRequiresAuxiliaryBuffer();

            // Bit 3-4
            ReadCombinationOperator();

            // Bit 2
            ReadDefaultPixelValue();

            // Bit 1
            ReadContainsRefinement();

            // Bit 0
            ReadIsLossless();

            // Bit 15
            ReadIsStriped();

            // Bit 0-14
            ReadMaxStripeSize();
        }

        private void ReadResolution()
        {
            ResolutionX = (int)(subInputStream.ReadBits(32) & 0xffffffff);
            ResolutionY = (int)(subInputStream.ReadBits(32) & 0xffffffff);
        }

        private void ReadCombinationOperatorOverrideAllowed()
        {
            // Bit 6
            if (subInputStream.ReadBit() == 1)
            {
                IsCombinationOperatorOverrideAllowed = true;
            }
        }

        private void ReadRequiresAuxiliaryBuffer()
        {
            // Bit 5
            if (subInputStream.ReadBit() == 1)
            {
                RequiresAuxiliaryBuffer = true;
            }
        }

        private void ReadCombinationOperator()
        {
            // Bit 3-4
            CombinationOperator = CombinationOperators
                    .TranslateOperatorCodeToEnum((short)(subInputStream.ReadBits(2) & 0xf));
        }

        private void ReadDefaultPixelValue()
        {
            // Bit 2
            DefaultPixelValue = (short)subInputStream.ReadBit();
        }

        private void ReadContainsRefinement()
        {
            // Bit 1
            if (subInputStream.ReadBit() == 1)
            {
                MightContainRefinements = true;
            }
        }

        private void ReadIsLossless()
        {
            // Bit 0
            if (subInputStream.ReadBit() == 1)
            {
                IsLossless = true;
            }
        }

        private void ReadIsStriped()
        {
            // Bit 15
            if (subInputStream.ReadBit() == 1)
            {
                IsStriped = true;
            }
        }

        private void ReadMaxStripeSize()
        {
            // Bit 0-14
            MaxStripeSize = (short)(subInputStream.ReadBits(15) & 0xffff);
        }

        private void ReadWidthAndHeight()
        {
            BitmapWidth = (int)subInputStream.ReadBits(32);
            BitmapHeight = (int)subInputStream.ReadBits(32);
        }

        public void Init(SegmentHeader header, SubInputStream sis)
        {
            subInputStream = sis;
            ParseHeader();
        }
    }
}
