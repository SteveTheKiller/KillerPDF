// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.

#nullable disable


namespace KillerPdf.Engine.Filters.Jbig2
{
    using System;

    /// <summary>
    /// This class represents a generic refinement region and implements the procedure described in JBIG2 ISO standard, 6.3
    /// and 7.4.7.
    /// </summary>
    internal sealed class GenericRefinementRegion : IRegion
    {
        public abstract class Template
        {
            internal abstract short Form(short c1, short c2, short c3, short c4, short c5);

            internal abstract void SetIndex(CX cx);
        }

        private sealed class Template0 : Template
        {
            internal override short Form(short c1, short c2, short c3, short c4, short c5)
            {
                return (short)(c1 << 10 | c2 << 7 | c3 << 4 | c4 << 1 | (int)c5);
            }

            internal override void SetIndex(CX cx)
            {
                // Figure 14, page 22
                cx.Index = 0x100;
            }
        }

        private sealed class Template1 : Template
        {
            internal override short Form(short c1, short c2, short c3, short c4, short c5)
            {
                return (short)((c1 & 0x02) << 8 | c2 << 6 | (c3 & 0x03) << 4 | c4 << 1 | (int)c5);
            }

            internal override void SetIndex(CX cx)
            {
                // Figure 15, page 22
                cx.Index = 0x080;
            }
        }

        private static readonly Template T0 = new Template0();
        private static readonly Template T1 = new Template1();

        private SubInputStream subInputStream;

        private SegmentHeader segmentHeader;

        // Region segment information flags, 7.4.1
        public RegionSegmentInformation RegionInfo { get; private set; }

        // Generic refinement region segment flags, 7.4.7.2
        private bool isTPGROn;
        private short templateID;

        private Template template;
        // Generic refinement region segment AT flags, 7.4.7.3
        private short[] grAtX;
        private short[] grAtY;

        // Decoded data as pixel values (use row stride/width to wrap line)
        private Jbig2Bitmap regionBitmap;

        // Variables for decoding
        private Jbig2Bitmap referenceBitmap;
        private Jbig2Bitmap pageBitmap;
        private int referenceDX;
        private int referenceDY;

        private ArithmeticDecoder arithDecoder;
        private CX cx;

        // If true, AT pixels are not on their nominal location and have to be overridden.
        private bool isOverride;
        private bool[] grAtOverride;

        public GenericRefinementRegion()
        {
        }

        public GenericRefinementRegion(SubInputStream subInputStream)
        {
            this.subInputStream = subInputStream;
            RegionInfo = new RegionSegmentInformation(subInputStream);
        }

        public GenericRefinementRegion(SubInputStream subInputStream,
                SegmentHeader segmentHeader)
        {
            this.subInputStream = subInputStream;
            this.segmentHeader = segmentHeader;
            RegionInfo = new RegionSegmentInformation(subInputStream);
        }

        /// <summary>
        /// Parses the flags described in JBIG2 ISO standard:
        /// 7.4.7.2 Generic refinement region segment flags.
        /// 7.4.7.3 Generic refinement region segment AT flags.
        /// </summary>
        private void ParseHeader()
        {
            RegionInfo.ParseHeader();

            // Bit 2-7
            subInputStream.ReadBits(6); // Dirty read...

            // Bit 1
            if (subInputStream.ReadBit() == 1)
            {
                isTPGROn = true;
            }

            // Bit 0
            templateID = (short)subInputStream.ReadBit();

            switch (templateID)
            {
                case 0:
                    template = T0;
                    ReadAtPixels();
                    break;

                case 1:
                    template = T1;
                    break;
            }
        }

        private void ReadAtPixels()
        {
            grAtX = new short[2];
            grAtY = new short[2];

            // Byte 0
            grAtX[0] = (sbyte)subInputStream.ReadByte();
            // Byte 1
            grAtY[0] = (sbyte)subInputStream.ReadByte();
            // Byte 2
            grAtX[1] = (sbyte)subInputStream.ReadByte();
            // Byte 3
            grAtY[1] = (sbyte)subInputStream.ReadByte();
        }

        /// <summary>
        /// Decode using a template and arithmetic coding, as described in 6.3.5.6
        /// </summary>
        /// <returns>The decoded <see cref="Jbig2Bitmap"/>.</returns>
        /// <exception cref="IOException">if an underlying IO operation fails</exception>
        /// <exception cref="InvalidHeaderValueException">if a segment header value is invalid</exception>
        /// <exception cref="IntegerMaxValueException"> if the maximum value limit of an integer is exceeded</exception>
        public Jbig2Bitmap GetRegionBitmap()
        {
            if (regionBitmap is null)
            {
                // 6.3.5.6 - 1)
                int isLineTypicalPredicted = 0;

                // Get the reference bitmap, which is the base of refinement process
                referenceBitmap ??= GetGrReference();

                arithDecoder ??= new ArithmeticDecoder(subInputStream);

                cx ??= new CX(8192, 1);

                // 6.3.5.6 - 2)
                regionBitmap = new Jbig2Bitmap(RegionInfo.BitmapWidth, RegionInfo.BitmapHeight);

                if (templateID == 0)
                {
                    // AT pixel may only occur in template 0
                    UpdateOverride();
                }

                int paddedWidth = regionBitmap.Width + 7 & -8;
                int deltaRefStride = isTPGROn ? -referenceDY * referenceBitmap.RowStride : 0;
                int yOffset = deltaRefStride + 1;

                // 6.3.5.6 - 3)
                for (int y = 0; y < regionBitmap.Height; y++)
                {
                    // 6.3.5.6 - 3 b)
                    if (isTPGROn)
                    {
                        isLineTypicalPredicted ^= DecodeSLTP();
                    }

                    if (isLineTypicalPredicted == 0)
                    {
                        // 6.3.5.6 - 3 c)
                        DecodeOptimized(y, regionBitmap.Width, regionBitmap.RowStride,
                                referenceBitmap.RowStride, paddedWidth, deltaRefStride, yOffset);
                    }
                    else
                    {
                        // 6.3.5.6 - 3 d)
                        DecodeTypicalPredictedLine(y, regionBitmap.Width,
                                regionBitmap.RowStride, referenceBitmap.RowStride,
                                paddedWidth, deltaRefStride);
                    }
                }
            }

            // 6.3.5.6 - 4)
            return regionBitmap;
        }

        private int DecodeSLTP()
        {
            template.SetIndex(cx);
            return arithDecoder.Decode(cx);
        }

        private Jbig2Bitmap GetGrReference()
        {
            SegmentHeader[] segments = segmentHeader.RtSegments;
            if (segments is null)
            {
                if (RegionInfo.CombinationOperator != CombinationOperator.REPLACE)
                {
                    throw new InvalidHeaderValueException(
                        "A page-buffer refinement region must use the REPLACE combination operator.");
                }

                var pageRegion = new Jbig2Rectangle(RegionInfo.X, RegionInfo.Y,
                    RegionInfo.BitmapWidth, RegionInfo.BitmapHeight);
                return Jbig2Bitmaps.Extract(pageRegion, pageBitmap);
            }

            IRegion region = (IRegion)segments[0].GetSegmentData();

            return region.GetRegionBitmap();
        }

        internal void SetPageBitmap(Jbig2Bitmap bitmap)
        {
            pageBitmap = bitmap;
        }

        private void DecodeOptimized(int lineNumber, int width, int rowStride,
                int refRowStride, int paddedWidth, int deltaRefStride,
                int lineOffset)
        {
            // Offset of the reference bitmap with respect to the bitmap being decoded
            // For example: if referenceDY = -1, y is 1 HIGHER that currY
            int currentLine = lineNumber - referenceDY;
            int referenceByteIndex = referenceBitmap.GetByteIndex(Math.Max(0, -referenceDX),
                    currentLine);

            int byteIndex = regionBitmap.GetByteIndex(Math.Max(0, referenceDX), lineNumber);

            switch (templateID)
            {
                case 0:
                    DecodeTemplate(lineNumber, width, rowStride, refRowStride, paddedWidth, deltaRefStride,
                            lineOffset, byteIndex, currentLine, referenceByteIndex, T0);
                    break;
                case 1:
                    DecodeTemplate(lineNumber, width, rowStride, refRowStride, paddedWidth, deltaRefStride,
                            lineOffset, byteIndex, currentLine, referenceByteIndex, T1);
                    break;
            }
        }

        private void DecodeTemplate(int lineNumber, int width, int rowStride,
                int refRowStride, int paddedWidth, int deltaRefStride,
                int lineOffset, int byteIndex, int currentLine, int refByteIndex,
                Template templateFormation)
        {
            short c1, c2, c3, c4, c5;

            int w1, w2, w3, w4;
            w1 = w2 = w3 = w4 = 0;

            if (currentLine >= 1 && currentLine - 1 < referenceBitmap.Height)
            {
                w1 = referenceBitmap.GetByteAsInteger(refByteIndex - refRowStride);
            }

            if (currentLine >= 0 && currentLine < referenceBitmap.Height)
            {
                w2 = referenceBitmap.GetByteAsInteger(refByteIndex);
            }

            if (currentLine >= -1 && currentLine + 1 < referenceBitmap.Height)
            {
                w3 = referenceBitmap.GetByteAsInteger(refByteIndex + refRowStride);
            }

            refByteIndex++;

            if (lineNumber >= 1)
            {
                w4 = regionBitmap.GetByteAsInteger(byteIndex - rowStride);
            }

            byteIndex++;

            int modReferenceDX = referenceDX % 8;
            int shiftOffset = 6 + modReferenceDX;
            int modRefByteIdx = refByteIndex % refRowStride;

            if (shiftOffset >= 0)
            {
                c1 = (short)((shiftOffset >= 8 ? 0 : (int)((uint)w1 >> shiftOffset)) & 0x07);
                c2 = (short)((shiftOffset >= 8 ? 0 : (int)((uint)w2 >> shiftOffset)) & 0x07);
                c3 = (short)((shiftOffset >= 8 ? 0 : (int)((uint)w3 >> shiftOffset)) & 0x07);

                if (shiftOffset == 6 && modRefByteIdx > 1)
                {
                    if (currentLine >= 1 && currentLine - 1 < referenceBitmap.Height)
                    {
                        c1 = (short)((int)c1 | referenceBitmap.GetByteAsInteger(refByteIndex - refRowStride - 2) << 2 & 0x04);
                    }

                    if (currentLine >= 0 && currentLine < referenceBitmap.Height)
                    {
                        c2 = (short)((int)c2 | referenceBitmap.GetByteAsInteger(refByteIndex - 2) << 2 & 0x04);
                    }

                    if (currentLine >= -1 && currentLine + 1 < referenceBitmap.Height)
                    {
                        c3 = (short)((int)c3 | referenceBitmap.GetByteAsInteger(refByteIndex + refRowStride - 2) << 2 & 0x04);
                    }
                }

                if (shiftOffset == 0)
                {
                    w1 = w2 = w3 = 0;
                    if (modRefByteIdx < refRowStride - 1)
                    {
                        if (currentLine >= 1 && currentLine - 1 < referenceBitmap.Height)
                        {
                            w1 = referenceBitmap.GetByteAsInteger(refByteIndex - refRowStride);
                        }

                        if (currentLine >= 0 && currentLine < referenceBitmap.Height)
                        {
                            w2 = referenceBitmap.GetByteAsInteger(refByteIndex);
                        }

                        if (currentLine >= -1 && currentLine + 1 < referenceBitmap.Height)
                        {
                            w3 = referenceBitmap.GetByteAsInteger(refByteIndex + refRowStride);
                        }
                    }
                    refByteIndex++;
                }
            }
            else
            {
                c1 = (short)(w1 << 1 & 0x07);
                c2 = (short)(w2 << 1 & 0x07);
                c3 = (short)(w3 << 1 & 0x07);
                w1 = w2 = w3 = 0;
                if (modRefByteIdx < refRowStride - 1)
                {
                    if (currentLine >= 1 && currentLine - 1 < referenceBitmap.Height)
                    {
                        w1 = referenceBitmap.GetByteAsInteger(refByteIndex - refRowStride);
                    }

                    if (currentLine >= 0 && currentLine < referenceBitmap.Height)
                    {
                        w2 = referenceBitmap.GetByteAsInteger(refByteIndex);
                    }

                    if (currentLine >= -1 && currentLine + 1 < referenceBitmap.Height)
                    {
                        w3 = referenceBitmap.GetByteAsInteger(refByteIndex + refRowStride);
                    }

                    refByteIndex++;
                }
                c1 |= (short)((int)((uint)w1 >> 7) & 0x07);
                c2 |= (short)((int)((uint)w2 >> 7) & 0x07);
                c3 |= (short)((int)((uint)w3 >> 7) & 0x07);
            }

            c4 = (short)(int)((uint)w4 >> 6);
            c5 = 0;

            int modBitsToTrim = (2 - modReferenceDX) % 8;
            w1 <<= modBitsToTrim;
            w2 <<= modBitsToTrim;
            w3 <<= modBitsToTrim;

            w4 <<= 2;

            for (int x = 0; x < width; x++)
            {
                int minorX = x & 0x07;

                short tval = templateFormation.Form(c1, c2, c3, c4, c5);

                if (isOverride)
                {
                    cx.Index = OverrideAtTemplate0(tval, x, lineNumber,
                            regionBitmap.GetByte(regionBitmap.GetByteIndex(x, lineNumber)), minorX);
                }
                else
                {
                    cx.Index = tval;
                }
                int bit = arithDecoder.Decode(cx);
                regionBitmap.SetPixel(x, lineNumber, (byte)bit);

                c1 = (short)((c1 << 1 | 0x01 & (int)((uint)w1 >> 7)) & 0x07);
                c2 = (short)((c2 << 1 | 0x01 & (int)((uint)w2 >> 7)) & 0x07);
                c3 = (short)((c3 << 1 | 0x01 & (int)((uint)w3 >> 7)) & 0x07);
                c4 = (short)((c4 << 1 | 0x01 & (int)((uint)w4 >> 7)) & 0x07);
                c5 = (short)bit;

                if ((x - referenceDX) % 8 == 5)
                {
                    if ((x - referenceDX) / 8 + 1 >= referenceBitmap.RowStride)
                    {
                        w1 = w2 = w3 = 0;
                    }
                    else
                    {
                        if (currentLine >= 1 && currentLine - 1 < referenceBitmap.Height)
                        {
                            w1 = referenceBitmap.GetByteAsInteger(refByteIndex - refRowStride);
                        }
                        else
                        {
                            w1 = 0;
                        }
                        if (currentLine >= 0 && currentLine < referenceBitmap.Height)
                        {
                            w2 = referenceBitmap.GetByteAsInteger(refByteIndex);
                        }
                        else
                        {
                            w2 = 0;
                        }
                        if (currentLine >= -1 && currentLine + 1 < referenceBitmap.Height)
                        {
                            w3 = referenceBitmap.GetByteAsInteger(refByteIndex + refRowStride);
                        }
                        else
                        {
                            w3 = 0;
                        }
                    }
                    refByteIndex++;
                }
                else
                {
                    w1 <<= 1;
                    w2 <<= 1;
                    w3 <<= 1;
                }

                if (minorX == 5 && lineNumber >= 1)
                {
                    if ((x >> 3) + 1 >= regionBitmap.RowStride)
                    {
                        w4 = 0;
                    }
                    else
                    {
                        w4 = regionBitmap.GetByteAsInteger(byteIndex - rowStride);
                    }
                    byteIndex++;
                }
                else
                {
                    w4 <<= 1;
                }
            }
        }

        private void UpdateOverride()
        {
            if (grAtX is null || grAtY is null)
            {
                return;
            }

            if (grAtX.Length != grAtY.Length)
            {
                return;
            }

            grAtOverride = new bool[grAtX.Length];

            switch (templateID)
            {
                case 0:
                    if (grAtX[0] != -1 && grAtY[0] != -1)
                    {
                        grAtOverride[0] = true;
                        isOverride = true;
                    }

                    if (grAtX[1] != -1 && grAtY[1] != -1)
                    {
                        grAtOverride[1] = true;
                        isOverride = true;
                    }
                    break;
                case 1:
                    isOverride = false;
                    break;
            }
        }

        private void DecodeTypicalPredictedLine(int lineNumber, int width,
                int rowStride, int refRowStride, int paddedWidth,
                int deltaRefStride)
        {
            // Offset of the reference bitmap with respect to the bitmap being
            // decoded
            // For example: if grReferenceDY = -1, y is 1 HIGHER that currY
            int currentLine = lineNumber - referenceDY;
            int refByteIndex = referenceBitmap.GetByteIndex(0, currentLine);

            int byteIndex = regionBitmap.GetByteIndex(0, lineNumber);

            switch (templateID)
            {
                case 0:
                    DecodeTypicalPredictedLineTemplate0(lineNumber, width, rowStride, refRowStride,
                            paddedWidth, deltaRefStride, byteIndex, currentLine, refByteIndex);
                    break;
                case 1:
                    DecodeTypicalPredictedLineTemplate1(lineNumber, width, rowStride, refRowStride,
                            paddedWidth, deltaRefStride, byteIndex, currentLine, refByteIndex);
                    break;
            }
        }

        private void DecodeTypicalPredictedLineTemplate0(int lineNumber, int width,
                int rowStride, int refRowStride, int paddedWidth,
                int deltaRefStride, int byteIndex, int currentLine, int refByteIndex)
        {
            int previousLine;
            int previousReferenceLine;
            int currentReferenceLine;
            int nextReferenceLine;

            if (lineNumber > 0)
            {
                previousLine = regionBitmap.GetByteAsInteger(byteIndex - rowStride);
            }
            else
            {
                previousLine = 0;
            }

            if (currentLine > 0 && currentLine <= referenceBitmap.Height)
            {
                previousReferenceLine = referenceBitmap
                        .GetByteAsInteger(refByteIndex - refRowStride + deltaRefStride) << 4;
            }
            else
            {
                previousReferenceLine = 0;
            }

            if (currentLine >= 0 && currentLine < referenceBitmap.Height)
            {
                currentReferenceLine = referenceBitmap
                        .GetByteAsInteger(refByteIndex + deltaRefStride) << 1;
            }
            else
            {
                currentReferenceLine = 0;
            }

            if (currentLine > -2 && currentLine < referenceBitmap.Height - 1)
            {
                nextReferenceLine = referenceBitmap
                        .GetByteAsInteger(refByteIndex + refRowStride + deltaRefStride);
            }
            else
            {
                nextReferenceLine = 0;
            }

            var context = previousLine >> 5 & 0x6 | nextReferenceLine >> 2 & 0x30
                                                      | currentReferenceLine & 0x180 | previousReferenceLine & 0xc00;

            int nextByte;
            for (int x = 0; x < paddedWidth; x = nextByte)
            {
                byte result = 0;
                nextByte = x + 8;
                int minorWidth = width - x > 8 ? 8 : width - x;
                bool readNextByte = nextByte < width;
                bool refReadNextByte = nextByte < referenceBitmap.Width;

                int yOffset = deltaRefStride + 1;

                if (lineNumber > 0)
                {
                    previousLine = previousLine << 8 | (readNextByte
                            ? regionBitmap.GetByteAsInteger(byteIndex - rowStride + 1) : 0);
                }

                if (currentLine > 0 && currentLine <= referenceBitmap.Height)
                {
                    previousReferenceLine = previousReferenceLine << 8
                            | (refReadNextByte ? referenceBitmap
                                    .GetByteAsInteger(refByteIndex - refRowStride + yOffset) << 4 : 0);
                }

                if (currentLine >= 0 && currentLine < referenceBitmap.Height)
                {
                    currentReferenceLine = currentReferenceLine << 8 | (refReadNextByte
                            ? referenceBitmap.GetByteAsInteger(refByteIndex + yOffset) << 1 : 0);
                }

                if (currentLine > -2 && currentLine < referenceBitmap.Height - 1)
                {
                    nextReferenceLine = nextReferenceLine << 8 | (refReadNextByte
                            ? referenceBitmap.GetByteAsInteger(refByteIndex + refRowStride + yOffset)
                            : 0);
                }

                for (int minorX = 0; minorX < minorWidth; minorX++)
                {
                    bool isPixelTypicalPredicted = false;
                    int bit = 0;

                    // i)
                    int bitmapValue = context >> 4 & 0x1FF;

                    if (bitmapValue == 0x1ff)
                    {
                        isPixelTypicalPredicted = true;
                        bit = 1;
                    }
                    else if (bitmapValue == 0x00)
                    {
                        isPixelTypicalPredicted = true;
                        bit = 0;
                    }

                    if (!isPixelTypicalPredicted)
                    {
                        // iii) - is like 3 c) but for one pixel only
                        if (isOverride)
                        {
                            cx.Index = OverrideAtTemplate0(context, x + minorX, lineNumber, result, minorX); // overriddenContext
                        }
                        else
                        {
                            cx.Index = context;
                        }
                        bit = arithDecoder.Decode(cx);
                    }

                    int toShift = 7 - minorX;
                    result = (byte)(result | bit << toShift);

                    context = (context & 0xdb6) << 1 | bit | previousLine >> toShift + 5 & 0x002
                            | nextReferenceLine >> toShift + 2 & 0x010
                            | currentReferenceLine >> toShift & 0x080
                            | previousReferenceLine >> toShift & 0x400;
                }
                regionBitmap.SetByte(byteIndex++, result);
                refByteIndex++;
            }
        }

        private void DecodeTypicalPredictedLineTemplate1(int lineNumber, int width,
                int rowStride, int refRowStride, int paddedWidth,
                int deltaRefStride, int byteIndex, int currentLine, int refByteIndex)
        {
            int context;
            int grReferenceValue;

            int previousLine;
            int previousReferenceLine;
            int currentReferenceLine;
            int nextReferenceLine;

            if (lineNumber > 0)
            {
                previousLine = regionBitmap.GetByteAsInteger(byteIndex - rowStride);
            }
            else
            {
                previousLine = 0;
            }

            if (currentLine > 0 && currentLine <= referenceBitmap.Height)
            {
                previousReferenceLine = referenceBitmap
                        .GetByteAsInteger(byteIndex - refRowStride + deltaRefStride) << 2;
            }
            else
            {
                previousReferenceLine = 0;
            }

            if (currentLine >= 0 && currentLine < referenceBitmap.Height)
            {
                currentReferenceLine = referenceBitmap.GetByteAsInteger(byteIndex + deltaRefStride);
            }
            else
            {
                currentReferenceLine = 0;
            }

            if (currentLine > -2 && currentLine < referenceBitmap.Height - 1)
            {
                nextReferenceLine = referenceBitmap
                        .GetByteAsInteger(byteIndex + refRowStride + deltaRefStride);
            }
            else
            {
                nextReferenceLine = 0;
            }

            context = previousLine >> 5 & 0x6 | nextReferenceLine >> 2 & 0x30
                    | currentReferenceLine & 0xc0 | previousReferenceLine & 0x200;

            grReferenceValue = nextReferenceLine >> 2 & 0x70 | currentReferenceLine & 0xc0
                    | previousReferenceLine & 0x700;

            int nextByte;
            for (int x = 0; x < paddedWidth; x = nextByte)
            {
                byte result = 0;
                nextByte = x + 8;
                int minorWidth = width - x > 8 ? 8 : width - x;
                bool readNextByte = nextByte < width;
                bool refReadNextByte = nextByte < referenceBitmap.Width;

                int yOffset = deltaRefStride + 1;

                if (lineNumber > 0)
                {
                    previousLine = previousLine << 8 | (readNextByte
                            ? regionBitmap.GetByteAsInteger(byteIndex - rowStride + 1) : 0);
                }

                if (currentLine > 0 && currentLine <= referenceBitmap.Height)
                {
                    previousReferenceLine = previousReferenceLine << 8
                            | (refReadNextByte ? referenceBitmap
                                    .GetByteAsInteger(refByteIndex - refRowStride + yOffset) << 2 : 0);
                }

                if (currentLine >= 0 && currentLine < referenceBitmap.Height)
                {
                    currentReferenceLine = currentReferenceLine << 8 | (refReadNextByte
                            ? referenceBitmap.GetByteAsInteger(refByteIndex + yOffset) : 0);
                }

                if (currentLine > -2 && currentLine < referenceBitmap.Height - 1)
                {
                    nextReferenceLine = nextReferenceLine << 8 | (refReadNextByte
                            ? referenceBitmap.GetByteAsInteger(refByteIndex + refRowStride + yOffset)
                            : 0);
                }

                for (int minorX = 0; minorX < minorWidth; minorX++)
                {
                    // i)
                    int bitmapValue = grReferenceValue >> 4 & 0x1ff;

                    int bit;
                    if (bitmapValue == 0x1ff)
                    {
                        bit = 1;
                    }
                    else if (bitmapValue == 0x00)
                    {
                        bit = 0;
                    }
                    else
                    {
                        cx.Index = context;
                        bit = arithDecoder.Decode(cx);
                    }

                    int toShift = 7 - minorX;
                    result = (byte)(result | bit << toShift);

                    context = (context & 0x0d6) << 1 | bit | previousLine >> toShift + 5 & 0x002
                            | nextReferenceLine >> toShift + 2 & 0x010
                            | currentReferenceLine >> toShift & 0x040
                            | previousReferenceLine >> toShift & 0x200;

                    grReferenceValue = (grReferenceValue & 0x0db) << 1
                            | nextReferenceLine >> toShift + 2 & 0x010
                            | currentReferenceLine >> toShift & 0x080
                            | previousReferenceLine >> toShift & 0x400;
                }
                regionBitmap.SetByte(byteIndex++, result);
                refByteIndex++;
            }
        }

        private int OverrideAtTemplate0(int context, int x, int y, int result, int minorX)
        {
            if (grAtOverride[0])
            {
                context &= 0xfff7;
                if (grAtY[0] == 0 && grAtX[0] >= -minorX)
                {
                    context |= (result >> 7 - (minorX + grAtX[0]) & 0x1) << 3;
                }
                else
                {
                    context |= GetPixel(regionBitmap, x + grAtX[0], y + grAtY[0]) << 3;
                }
            }

            if (grAtOverride[1])
            {
                context &= 0xefff;
                if (grAtY[1] == 0 && grAtX[1] >= -minorX)
                {
                    context |= (result >> 7 - (minorX + grAtX[1]) & 0x1) << 12;
                }
                else
                {
                    context |= GetPixel(referenceBitmap, x + grAtX[1] + referenceDX,
                            y + grAtY[1] + referenceDY) << 12;
                }
            }
            return context;
        }

        private static byte GetPixel(Jbig2Bitmap b, int x, int y)
        {
            if (x < 0 || x >= b.Width)
            {
                return 0;
            }

            if (y < 0 || y >= b.Height)
            {
                return 0;
            }

            return b.GetPixel(x, y);
        }

        public void Init(SegmentHeader header, SubInputStream sis)
        {
            segmentHeader = header;
            subInputStream = sis;
            RegionInfo = new RegionSegmentInformation(subInputStream);
            ParseHeader();
        }

        internal void SetParameters(CX cx, ArithmeticDecoder arithmeticDecoder,
                short grTemplate, int regionWidth, int regionHeight,
                Jbig2Bitmap grReference, int grReferenceDX, int grReferenceDY,
                bool isTPGRon, short[] grAtX, short[] grAtY)
        {

            if (cx != null)
            {
                this.cx = cx;
            }

            if (arithmeticDecoder != null)
            {
                arithDecoder = arithmeticDecoder;
            }

            templateID = grTemplate;

            RegionInfo.BitmapWidth = regionWidth;
            RegionInfo.BitmapHeight = regionHeight;

            referenceBitmap = grReference;
            referenceDX = grReferenceDX;
            referenceDY = grReferenceDY;

            isTPGROn = isTPGRon;

            this.grAtX = grAtX;
            this.grAtY = grAtY;

            regionBitmap = null;
        }
    }
}
