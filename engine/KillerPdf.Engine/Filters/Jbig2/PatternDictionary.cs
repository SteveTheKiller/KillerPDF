// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.
#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    using System.Collections.Generic;

    /// <summary>
    /// This class represents the segment type "Pattern dictionary", 7.4.4.
    /// </summary>
    internal sealed class PatternDictionary : IJbigDictionary
    {
        private SubInputStream subInputStream;

        // Segment data structure (only necessary if MMR is used)
        private long dataHeaderOffset = 0;
        private long dataHeaderLength;
        private long dataOffset;
        private long dataLength;

        private short[] gbAtX = null;
        private short[] gbAtY = null;

        // Decoded bitmaps, stored to be used by segments, that refer to it
        private List<Jbig2Bitmap> patterns;

        // Pattern dictionary flags, 7.4.4.1.1
        public bool IsMMREncoded { get; private set; }
        public byte HdTemplate { get; private set; }

        // Width of the patterns in the pattern dictionary, 7.4.4.1.2
        public short HdpWidth { get; private set; }

        // Height of the patterns in the pattern dictionary, 7.4.4.1.3
        public short HdpHeight { get; private set; }

        // Largest gray-scale value, 7.4.4.1.4
        // Value: one less than the number of patterns defined in this pattern dictionary
        public int GrayMax { get; private set; }

        private void ParseHeader()
        {
            // Bit 3-7
            subInputStream.ReadBits(5); // Dirty read ...

            // Bit 1-2
            ReadTemplate();

            // Bit 0
            ReadIsMMREncoded();

            ReadPatternWidthAndHeight();

            ReadGrayMax();

            ComputeSegmentDataStructure();

            CheckInput();
        }

        private void ReadTemplate()
        {
            // Bit 1-2
            HdTemplate = (byte)subInputStream.ReadBits(2);
        }

        private void ReadIsMMREncoded()
        {
            // Bit 0
            if (subInputStream.ReadBit() == 1)
            {
                IsMMREncoded = true;
            }
        }

        private void ReadPatternWidthAndHeight()
        {
            HdpWidth = (sbyte)subInputStream.ReadByte();
            HdpHeight = (sbyte)subInputStream.ReadByte();
        }

        private void ReadGrayMax()
        {
            GrayMax = (int)(subInputStream.ReadBits(32) & 0xffffffff);
        }

        private void ComputeSegmentDataStructure()
        {
            dataOffset = subInputStream.Position;
            dataHeaderLength = dataOffset - dataHeaderOffset;
            dataLength = subInputStream.Length - dataHeaderLength;
        }

        private void CheckInput()
        {
            if (HdpHeight < 1 || HdpWidth < 1)
            {
                throw new InvalidHeaderValueException("Width/Heigth must be greater than zero.");
            }
        }

        /// <summary>
        /// This method decodes a pattern dictionary segment and returns an array of
        /// <see cref="Jbig2Bitmap"/>s. Each <see cref="Jbig2Bitmap"/> is a pattern.
        /// The procedure is described in 6.7.5 (page 43).
        /// </summary>
        /// <returns>An array of <see cref="Jbig2Bitmap"/>s as result of the decoding procedure.</returns>
        public List<Jbig2Bitmap> GetDictionary()
        {
            if (patterns is null)
            {
                if (!IsMMREncoded)
                {
                    SetGbAtPixels();
                }

                // 2)
                GenericRegion genericRegion = new GenericRegion(subInputStream);
                genericRegion.SetParameters(IsMMREncoded, dataOffset, dataLength, HdpHeight,
                        (GrayMax + 1) * HdpWidth, HdTemplate, false, false, gbAtX, gbAtY);

                Jbig2Bitmap collectiveBitmap = genericRegion.GetRegionBitmap();

                // 4)
                ExtractPatterns(collectiveBitmap);
            }

            return patterns;
        }

        private void ExtractPatterns(Jbig2Bitmap collectiveBitmap)
        {
            // 3)
            int gray = 0;
            patterns = new List<Jbig2Bitmap>(GrayMax + 1);

            // 4)
            while (gray <= GrayMax)
            {
                // 4) a) Retrieve a pattern bitmap by extracting it out of the collective bitmap
                Jbig2Rectangle roi = new Jbig2Rectangle(HdpWidth * gray, 0, HdpWidth, HdpHeight);
                Jbig2Bitmap patternBitmap = Jbig2Bitmaps.Extract(roi, collectiveBitmap);
                patterns.Add(patternBitmap);

                // 4) b)
                gray++;
            }
        }

        private void SetGbAtPixels()
        {
            if (HdTemplate == 0)
            {
                gbAtX = new short[4];
                gbAtY = new short[4];
                gbAtX[0] = (short)-HdpWidth;
                gbAtY[0] = 0;
                gbAtX[1] = -3;
                gbAtY[1] = -1;
                gbAtX[2] = 2;
                gbAtY[2] = -2;
                gbAtX[3] = -2;
                gbAtY[3] = -2;
            }
            else
            {
                gbAtX = new short[1];
                gbAtY = new short[1];
                gbAtX[0] = (short)-HdpWidth;
                gbAtY[0] = 0;
            }
        }

        public void Init(SegmentHeader header, SubInputStream sis)
        {
            subInputStream = sis;
            ParseHeader();
        }
    }
}
