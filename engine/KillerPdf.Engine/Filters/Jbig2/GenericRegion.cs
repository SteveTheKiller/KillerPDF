// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.

#nullable disable


namespace KillerPdf.Engine.Filters.Jbig2
{
    /// <summary>
    /// This class represents a generic region segment.
    /// Parsing is done as described in 7.4.5.
    /// Decoding procedure is done as described in 6.2.5.7 and 7.4.6.4.
    /// </summary>
    internal sealed class GenericRegion : IRegion
    {
        private SubInputStream subInputStream;
        private long dataHeaderOffset = 0;
        private long dataHeaderLength;
        private long dataOffset;
        private long dataLength;

        // Generic region segment flags, 7.4.6.2
        public bool UseExtTemplates { get; private set; }
        public bool IsTPGDon { get; private set; }
        public byte GbTemplate { get; private set; }
        public bool IsMMREncoded { get; private set; }

        // Generic region segment AT flags, 7.4.6.3
        public short[] GbAtX { get; private set; }
        public short[] GbAtY { get; private set; }
        private bool[] gbAtOverride;

        // If true, AT pixels are not on their nominal location and have to be overridden
        private bool isOverride;

        // Decoded data as pixel values (use row stride/width to wrap line)
        private Jbig2Bitmap regionBitmap;

        private ArithmeticDecoder arithDecoder;
        private CX cx;

        private MMRDecompressor mmrDecompressor;

        // Region segment information field, 7.4.1
        public RegionSegmentInformation RegionInfo { get; private set; }

        public GenericRegion()
        {
        }

        public GenericRegion(SubInputStream subInputStream)
        {
            this.subInputStream = subInputStream;
            RegionInfo = new RegionSegmentInformation(subInputStream);
        }

        private void ParseHeader()
        {
            RegionInfo.ParseHeader();

            // Bit 5-7
            subInputStream.ReadBits(3); // Dirty read...

            // Bit 4
            if (subInputStream.ReadBit() == 1)
            {
                UseExtTemplates = true;
            }

            // Bit 3
            if (subInputStream.ReadBit() == 1)
            {
                IsTPGDon = true;
            }

            // Bit 1-2
            GbTemplate = (byte)(subInputStream.ReadBits(2) & 0xf);

            // Bit 0
            if (subInputStream.ReadBit() == 1)
            {
                IsMMREncoded = true;
            }

            if (!IsMMREncoded)
            {
                int amountOfGbAt;
                if (GbTemplate == 0)
                {
                    if (UseExtTemplates)
                    {
                        amountOfGbAt = 12;
                    }
                    else
                    {
                        amountOfGbAt = 4;
                    }
                }
                else
                {
                    amountOfGbAt = 1;
                }

                ReadGbAtPixels(amountOfGbAt);
            }

            ComputeSegmentDataStructure();
        }

        private void ReadGbAtPixels(int amountOfGbAt)
        {
            GbAtX = new short[amountOfGbAt];
            GbAtY = new short[amountOfGbAt];

            for (int i = 0; i < amountOfGbAt; i++)
            {
                GbAtX[i] = (sbyte)subInputStream.ReadByte();
                GbAtY[i] = (sbyte)subInputStream.ReadByte();
            }
        }

        private void ComputeSegmentDataStructure()
        {
            dataOffset = subInputStream.Position;
            dataHeaderLength = dataOffset - dataHeaderOffset;
            dataLength = subInputStream.Length - dataHeaderLength;
        }

        /// <summary>
        /// The procedure is described in 6.2.5.7, page 17.
        /// </summary>
        /// <returns>The decoded <see cref="Jbig2Bitmap"/>.</returns>
        public Jbig2Bitmap GetRegionBitmap()
        {
            if (regionBitmap is null)
            {
                if (IsMMREncoded)
                {
                    // MMR DECODER CALL
                    if (mmrDecompressor is null)
                    {
                        mmrDecompressor = new MMRDecompressor(RegionInfo.BitmapWidth,
                                RegionInfo.BitmapHeight,
                                new SubInputStream(subInputStream, dataOffset, dataLength));
                    }

                    // 6.2.6
                    regionBitmap = mmrDecompressor.Uncompress();

                }
                else
                {
                    // ARITHMETIC DECODER PROCEDURE for generic region segments
                    UpdateOverrideFlags();

                    // 6.2.5.7 - 1)
                    int ltp = 0;

                    arithDecoder ??= new ArithmeticDecoder(subInputStream);
                    cx ??= new CX(65536, 1);

                    // 6.2.5.7 - 2)
                    regionBitmap = new Jbig2Bitmap(RegionInfo.BitmapWidth, RegionInfo.BitmapHeight);

                    int paddedWidth = regionBitmap.Width + 7 & -8;

                    // 6.2.5.7 - 3
                    for (int line = 0; line < regionBitmap.Height; line++)
                    {
                        // 6.2.5.7 - 3 b)
                        if (IsTPGDon)
                        {
                            ltp ^= DecodeSLTP();
                        }

                        // 6.2.5.7 - 3 c)
                        if (ltp == 1)
                        {
                            if (line > 0)
                            {
                                CopyLineAbove(line);
                            }
                        }
                        else
                        {
                            // 3 d)
                            // NOT USED ATM - If corresponding pixel of SKIP bitmap is 0, set
                            // current pixel to 0. Something like that:
                            // if (useSkip) {
                            // for (int i = 1; i < rowstride; i++) {
                            // if (skip[pixel] == 1) {
                            // gbReg[pixel] = 0;
                            // }
                            // pixel++;
                            // }
                            // } else {
                            DecodeLine(line, regionBitmap.Width, regionBitmap.RowStride, paddedWidth);
                            // }
                        }
                    }
                }
            }

            // 4
            return regionBitmap;
        }

        private int DecodeSLTP()
        {
            switch (GbTemplate)
            {
                case 0:
                    cx.Index = 0x9b25;
                    break;
                case 1:
                    cx.Index = 0x795;
                    break;
                case 2:
                    cx.Index = 0xe5;
                    break;
                case 3:
                    cx.Index = 0x195;
                    break;
            }
            return arithDecoder.Decode(cx);
        }

        private void DecodeLine(int lineNumber, int width, int rowStride, int paddedWidth)
        {
            int byteIndex = regionBitmap.GetByteIndex(0, lineNumber);
            int idx = byteIndex - rowStride;

            switch (GbTemplate)
            {
                case 0:
                    if (!UseExtTemplates)
                    {
                        DecodeTemplate0a(lineNumber, width, rowStride, paddedWidth, byteIndex, idx);
                    }
                    else
                    {
                        DecodeTemplate0b(lineNumber, width, rowStride, paddedWidth, byteIndex, idx);
                    }
                    break;
                case 1:
                    DecodeTemplate1(lineNumber, width, rowStride, paddedWidth, byteIndex, idx);
                    break;
                case 2:
                    DecodeTemplate2(lineNumber, width, rowStride, paddedWidth, byteIndex, idx);
                    break;
                case 3:
                    DecodeTemplate3(lineNumber, width, rowStride, paddedWidth, byteIndex, idx);
                    break;
            }
        }

        /// <summary>
        /// Each pixel gets the value from the corresponding pixel of the row above. Line 0 cannot get copied values (source
        /// will be -1, doesn't exist).
        /// </summary>
        /// <param name="lineNumber">Coordinate of the row that should be set.</param>
        private void CopyLineAbove(int lineNumber)
        {
            int targetByteIndex = lineNumber * regionBitmap.RowStride;
            int sourceByteIndex = targetByteIndex - regionBitmap.RowStride;

            for (int i = 0; i < regionBitmap.RowStride; i++)
            {
                // Get the byte that should be copied and put it into Bitmap
                regionBitmap.SetByte(targetByteIndex++, regionBitmap.GetByte(sourceByteIndex++));
            }
        }

        private void DecodeTemplate0a(int lineNumber, int width, int rowStride,
                int paddedWidth, int byteIndex, int idx)
        {
            int context;
            int overriddenContext;

            int line1 = 0;
            int line2 = 0;

            if (lineNumber >= 1)
            {
                line1 = regionBitmap.GetByteAsInteger(idx);
            }

            if (lineNumber >= 2)
            {
                line2 = regionBitmap.GetByteAsInteger(idx - rowStride) << 6;
            }

            context = line1 & 0xf0 | line2 & 0x3800;

            int nextByte;
            for (int x = 0; x < paddedWidth; x = nextByte)
            {
                // 6.2.5.7 3d
                byte result = 0;
                nextByte = x + 8;
                int minorWidth = width - x > 8 ? 8 : width - x;

                if (lineNumber > 0)
                {
                    line1 = line1 << 8
                            | (nextByte < width ? regionBitmap.GetByteAsInteger(idx + 1) : 0);
                }

                if (lineNumber > 1)
                {
                    line2 = line2 << 8 | (nextByte < width
                            ? regionBitmap.GetByteAsInteger(idx - rowStride + 1) << 6 : 0);
                }

                for (int minorX = 0; minorX < minorWidth; minorX++)
                {
                    int toShift = 7 - minorX;
                    if (isOverride)
                    {
                        overriddenContext = OverrideAtTemplate0a(context, x + minorX, lineNumber,
                                result, minorX, toShift);
                        cx.Index = overriddenContext;
                    }
                    else
                    {
                        cx.Index = context;
                    }

                    int bit = arithDecoder.Decode(cx);

                    result = (byte)(result | bit << toShift);

                    context = (context & 0x7bf7) << 1 | bit | line1 >> toShift & 0x10
                            | line2 >> toShift & 0x800;
                }

                regionBitmap.SetByte(byteIndex++, result);
                idx++;
            }
        }

        private void DecodeTemplate0b(int lineNumber, int width, int rowStride,
                int paddedWidth, int byteIndex, int idx)
        {
            int context;
            int overriddenContext;

            int line1 = 0;
            int line2 = 0;

            if (lineNumber >= 1)
            {
                line1 = regionBitmap.GetByteAsInteger(idx);
            }

            if (lineNumber >= 2)
            {
                line2 = regionBitmap.GetByteAsInteger(idx - rowStride) << 6;
            }

            context = line1 & 0xf0 | line2 & 0x3800;

            int nextByte;
            for (int x = 0; x < paddedWidth; x = nextByte)
            {
                // 6.2.5.7 3d
                byte result = 0;
                nextByte = x + 8;
                int minorWidth = width - x > 8 ? 8 : width - x;

                if (lineNumber > 0)
                {
                    line1 = line1 << 8
                            | (nextByte < width ? regionBitmap.GetByteAsInteger(idx + 1) : 0);
                }

                if (lineNumber > 1)
                {
                    line2 = line2 << 8 | (nextByte < width
                            ? regionBitmap.GetByteAsInteger(idx - rowStride + 1) << 6 : 0);
                }

                for (int minorX = 0; minorX < minorWidth; minorX++)
                {
                    int toShift = 7 - minorX;
                    if (isOverride)
                    {
                        overriddenContext = OverrideAtTemplate0b(context, x + minorX, lineNumber,
                                result, minorX, toShift);
                        cx.Index = overriddenContext;
                    }
                    else
                    {
                        cx.Index = context;
                    }

                    int bit = arithDecoder.Decode(cx);

                    result = (byte)(result | bit << toShift);

                    context = (context & 0x7bf7) << 1 | bit | line1 >> toShift & 0x10
                            | line2 >> toShift & 0x800;
                }

                regionBitmap.SetByte(byteIndex++, result);
                idx++;
            }
        }

        private void DecodeTemplate1(int lineNumber, int width, int rowStride,
                int paddedWidth, int byteIndex, int idx)
        {
            int context;
            int overriddenContext;

            int line1 = 0;
            int line2 = 0;

            if (lineNumber >= 1)
            {
                line1 = regionBitmap.GetByteAsInteger(idx);
            }

            if (lineNumber >= 2)
            {
                line2 = regionBitmap.GetByteAsInteger(idx - rowStride) << 5;
            }

            context = line1 >> 1 & 0x1f8 | line2 >> 1 & 0x1e00;

            int nextByte;
            for (int x = 0; x < paddedWidth; x = nextByte)
            {
                // 6.2.5.7 3d
                byte result = 0;
                nextByte = x + 8;
                int minorWidth = width - x > 8 ? 8 : width - x;

                if (lineNumber >= 1)
                {
                    line1 = line1 << 8
                            | (nextByte < width ? regionBitmap.GetByteAsInteger(idx + 1) : 0);
                }

                if (lineNumber >= 2)
                {
                    line2 = line2 << 8 | (nextByte < width
                            ? regionBitmap.GetByteAsInteger(idx - rowStride + 1) << 5 : 0);
                }

                for (int minorX = 0; minorX < minorWidth; minorX++)
                {
                    if (isOverride)
                    {
                        overriddenContext = OverrideAtTemplate1(context, x + minorX, lineNumber, result,
                                minorX);
                        cx.Index = overriddenContext;
                    }
                    else
                    {
                        cx.Index = context;
                    }

                    int bit = arithDecoder.Decode(cx);

                    result = (byte)(result | bit << 7 - minorX);

                    int toShift = 8 - minorX;
                    context = (context & 0xefb) << 1 | bit | line1 >> toShift & 0x8
                            | line2 >> toShift & 0x200;
                }

                regionBitmap.SetByte(byteIndex++, result);
                idx++;
            }
        }

        private void DecodeTemplate2(int lineNumber, int width, int rowStride,
                int paddedWidth, int byteIndex, int idx)
        {
            int context;
            int overriddenContext;

            int line1 = 0;
            int line2 = 0;

            if (lineNumber >= 1)
            {
                line1 = regionBitmap.GetByteAsInteger(idx);
            }

            if (lineNumber >= 2)
            {
                line2 = regionBitmap.GetByteAsInteger(idx - rowStride) << 4;
            }

            context = line1 >> 3 & 0x7c | line2 >> 3 & 0x380;

            int nextByte;
            for (int x = 0; x < paddedWidth; x = nextByte)
            {
                // 6.2.5.7 3d
                byte result = 0;
                nextByte = x + 8;
                int minorWidth = width - x > 8 ? 8 : width - x;

                if (lineNumber >= 1)
                {
                    line1 = line1 << 8
                            | (nextByte < width ? regionBitmap.GetByteAsInteger(idx + 1) : 0);
                }

                if (lineNumber >= 2)
                {
                    line2 = line2 << 8 | (nextByte < width
                            ? regionBitmap.GetByteAsInteger(idx - rowStride + 1) << 4 : 0);
                }

                for (int minorX = 0; minorX < minorWidth; minorX++)
                {
                    if (isOverride)
                    {
                        overriddenContext = OverrideAtTemplate2(context, x + minorX, lineNumber, result,
                                minorX);
                        cx.Index = overriddenContext;
                    }
                    else
                    {
                        cx.Index = context;
                    }

                    int bit = arithDecoder.Decode(cx);

                    result = (byte)(result | bit << 7 - minorX);

                    int toShift = 10 - minorX;
                    context = (context & 0x1bd) << 1 | bit | line1 >> toShift & 0x4
                            | line2 >> toShift & 0x80;
                }

                regionBitmap.SetByte(byteIndex++, result);
                idx++;
            }
        }

        private void DecodeTemplate3(int lineNumber, int width, int rowStride,
                int paddedWidth, int byteIndex, int idx)
        {
            int context;
            int overriddenContext;

            int line1 = 0;

            if (lineNumber >= 1)
            {
                line1 = regionBitmap.GetByteAsInteger(idx);
            }

            context = line1 >> 1 & 0x70;

            int nextByte;
            for (int x = 0; x < paddedWidth; x = nextByte)
            {
                // 6.2.5.7 3d
                byte result = 0;
                nextByte = x + 8;
                int minorWidth = width - x > 8 ? 8 : width - x;

                if (lineNumber >= 1)
                {
                    line1 = line1 << 8
                            | (nextByte < width ? regionBitmap.GetByteAsInteger(idx + 1) : 0);
                }

                for (int minorX = 0; minorX < minorWidth; minorX++)
                {
                    if (isOverride)
                    {
                        overriddenContext = OverrideAtTemplate3(context, x + minorX, lineNumber, result,
                                minorX);
                        cx.Index = overriddenContext;
                    }
                    else
                    {
                        cx.Index = context;
                    }

                    int bit = arithDecoder.Decode(cx);

                    result = (byte)(result | bit << 7 - minorX);
                    context = (context & 0x1f7) << 1 | bit | line1 >> 8 - minorX & 0x010;
                }

                regionBitmap.SetByte(byteIndex++, result);
                idx++;
            }
        }

        private void UpdateOverrideFlags()
        {
            if (GbAtX is null || GbAtY is null)
            {
                return;
            }

            if (GbAtX.Length != GbAtY.Length)
            {
                return;
            }

            gbAtOverride = new bool[GbAtX.Length];

            switch (GbTemplate)
            {
                case 0:
                    if (!UseExtTemplates)
                    {
                        if (GbAtX[0] != 3 || GbAtY[0] != -1)
                        {
                            SetOverrideFlag(0);
                        }

                        if (GbAtX[1] != -3 || GbAtY[1] != -1)
                        {
                            SetOverrideFlag(1);
                        }

                        if (GbAtX[2] != 2 || GbAtY[2] != -2)
                        {
                            SetOverrideFlag(2);
                        }

                        if (GbAtX[3] != -2 || GbAtY[3] != -2)
                        {
                            SetOverrideFlag(3);
                        }
                    }
                    else
                    {
                        if (GbAtX[0] != -2 || GbAtY[0] != 0)
                        {
                            SetOverrideFlag(0);
                        }

                        if (GbAtX[1] != 0 || GbAtY[1] != -2)
                        {
                            SetOverrideFlag(1);
                        }

                        if (GbAtX[2] != -2 || GbAtY[2] != -1)
                        {
                            SetOverrideFlag(2);
                        }

                        if (GbAtX[3] != -1 || GbAtY[3] != -2)
                        {
                            SetOverrideFlag(3);
                        }

                        if (GbAtX[4] != 1 || GbAtY[4] != -2)
                        {
                            SetOverrideFlag(4);
                        }

                        if (GbAtX[5] != 2 || GbAtY[5] != -1)
                        {
                            SetOverrideFlag(5);
                        }

                        if (GbAtX[6] != -3 || GbAtY[6] != 0)
                        {
                            SetOverrideFlag(6);
                        }

                        if (GbAtX[7] != -4 || GbAtY[7] != 0)
                        {
                            SetOverrideFlag(7);
                        }

                        if (GbAtX[8] != 2 || GbAtY[8] != -2)
                        {
                            SetOverrideFlag(8);
                        }

                        if (GbAtX[9] != 3 || GbAtY[9] != -1)
                        {
                            SetOverrideFlag(9);
                        }

                        if (GbAtX[10] != -2 || GbAtY[10] != -2)
                        {
                            SetOverrideFlag(10);
                        }

                        if (GbAtX[11] != -3 || GbAtY[11] != -1)
                        {
                            SetOverrideFlag(11);
                        }
                    }
                    break;

                case 1:
                    if (GbAtX[0] != 3 || GbAtY[0] != -1)
                    {
                        SetOverrideFlag(0);
                    }

                    break;

                case 2:
                    if (GbAtX[0] != 2 || GbAtY[0] != -1)
                    {
                        SetOverrideFlag(0);
                    }

                    break;

                case 3:
                    if (GbAtX[0] != 2 || GbAtY[0] != -1)
                    {
                        SetOverrideFlag(0);
                    }

                    break;
            }
        }

        private void SetOverrideFlag(int index)
        {
            gbAtOverride[index] = true;
            isOverride = true;
        }

        private int OverrideAtTemplate0a(int context, int x, int y, int result,
                int minorX, int toShift)
        {
            if (gbAtOverride[0])
            {
                context &= 0xffef;
                if (GbAtY[0] == 0 && GbAtX[0] >= -minorX)
                {
                    context |= (result >> toShift - GbAtX[0] & 0x1) << 4;
                }
                else
                {
                    context |= GetPixel(x + GbAtX[0], y + GbAtY[0]) << 4;
                }
            }

            if (gbAtOverride[1])
            {
                context &= 0xfbff;
                if (GbAtY[1] == 0 && GbAtX[1] >= -minorX)
                {
                    context |= (result >> toShift - GbAtX[1] & 0x1) << 10;
                }
                else
                {
                    context |= GetPixel(x + GbAtX[1], y + GbAtY[1]) << 10;
                }
            }

            if (gbAtOverride[2])
            {
                context &= 0xf7ff;
                if (GbAtY[2] == 0 && GbAtX[2] >= -minorX)
                {
                    context |= (result >> toShift - GbAtX[2] & 0x1) << 11;
                }
                else
                {
                    context |= GetPixel(x + GbAtX[2], y + GbAtY[2]) << 11;
                }
            }

            if (gbAtOverride[3])
            {
                context &= 0x7fff;
                if (GbAtY[3] == 0 && GbAtX[3] >= -minorX)
                {
                    context |= (result >> toShift - GbAtX[3] & 0x1) << 15;
                }
                else
                {
                    context |= GetPixel(x + GbAtX[3], y + GbAtY[3]) << 15;
                }
            }
            return context;
        }

        private int OverrideAtTemplate0b(int context, int x, int y, int result,
                int minorX, int toShift)
        {
            if (gbAtOverride[0])
            {
                context &= 0xfffd;
                if (GbAtY[0] == 0 && GbAtX[0] >= -minorX)
                {
                    context |= (result >> toShift - GbAtX[0] & 0x1) << 1;
                }
                else
                {
                    context |= GetPixel(x + GbAtX[0], y + GbAtY[0]) << 1;
                }
            }

            if (gbAtOverride[1])
            {
                context &= 0xdfff;
                if (GbAtY[1] == 0 && GbAtX[1] >= -minorX)
                {
                    context |= (result >> toShift - GbAtX[1] & 0x1) << 13;
                }
                else
                {
                    context |= GetPixel(x + GbAtX[1], y + GbAtY[1]) << 13;
                }
            }

            if (gbAtOverride[2])
            {
                context &= 0xfdff;
                if (GbAtY[2] == 0 && GbAtX[2] >= -minorX)
                {
                    context |= (result >> toShift - GbAtX[2] & 0x1) << 9;
                }
                else
                {
                    context |= GetPixel(x + GbAtX[2], y + GbAtY[2]) << 9;
                }
            }

            if (gbAtOverride[3])
            {
                context &= 0xbfff;
                if (GbAtY[3] == 0 && GbAtX[3] >= -minorX)
                {
                    context |= (result >> toShift - GbAtX[3] & 0x1) << 14;
                }
                else
                {
                    context |= GetPixel(x + GbAtX[3], y + GbAtY[3]) << 14;
                }
            }

            if (gbAtOverride[4])
            {
                context &= 0xefff;
                if (GbAtY[4] == 0 && GbAtX[4] >= -minorX)
                {
                    context |= (result >> toShift - GbAtX[4] & 0x1) << 12;
                }
                else
                {
                    context |= GetPixel(x + GbAtX[4], y + GbAtY[4]) << 12;
                }
            }

            if (gbAtOverride[5])
            {
                context &= 0xffdf;
                if (GbAtY[5] == 0 && GbAtX[5] >= -minorX)
                {
                    context |= (result >> toShift - GbAtX[5] & 0x1) << 5;
                }
                else
                {
                    context |= GetPixel(x + GbAtX[5], y + GbAtY[5]) << 5;
                }
            }

            if (gbAtOverride[6])
            {
                context &= 0xfffb;
                if (GbAtY[6] == 0 && GbAtX[6] >= -minorX)
                {
                    context |= (result >> toShift - GbAtX[6] & 0x1) << 2;
                }
                else
                {
                    context |= GetPixel(x + GbAtX[6], y + GbAtY[6]) << 2;
                }
            }

            if (gbAtOverride[7])
            {
                context &= 0xfff7;
                if (GbAtY[7] == 0 && GbAtX[7] >= -minorX)
                {
                    context |= (result >> toShift - GbAtX[7] & 0x1) << 3;
                }
                else
                {
                    context |= GetPixel(x + GbAtX[7], y + GbAtY[7]) << 3;
                }
            }

            if (gbAtOverride[8])
            {
                context &= 0xf7ff;
                if (GbAtY[8] == 0 && GbAtX[8] >= -minorX)
                {
                    context |= (result >> toShift - GbAtX[8] & 0x1) << 11;
                }
                else
                {
                    context |= GetPixel(x + GbAtX[8], y + GbAtY[8]) << 11;
                }
            }

            if (gbAtOverride[9])
            {
                context &= 0xffef;
                if (GbAtY[9] == 0 && GbAtX[9] >= -minorX)
                {
                    context |= (result >> toShift - GbAtX[9] & 0x1) << 4;
                }
                else
                {
                    context |= GetPixel(x + GbAtX[9], y + GbAtY[9]) << 4;
                }
            }

            if (gbAtOverride[10])
            {
                context &= 0x7fff;
                if (GbAtY[10] == 0 && GbAtX[10] >= -minorX)
                {
                    context |= (result >> toShift - GbAtX[10] & 0x1) << 15;
                }
                else
                {
                    context |= GetPixel(x + GbAtX[10], y + GbAtY[10]) << 15;
                }
            }

            if (gbAtOverride[11])
            {
                context &= 0xfdff;
                if (GbAtY[11] == 0 && GbAtX[11] >= -minorX)
                {
                    context |= (result >> toShift - GbAtX[11] & 0x1) << 10;
                }
                else
                {
                    context |= GetPixel(x + GbAtX[11], y + GbAtY[11]) << 10;
                }
            }

            return context;
        }

        private int OverrideAtTemplate1(int context, int x, int y, int result, int minorX)
        {
            context &= 0x1ff7;
            if (GbAtY[0] == 0 && GbAtX[0] >= -minorX)
            {
                return context | (result >> 7 - (minorX + GbAtX[0]) & 0x1) << 3;
            }

            return context | GetPixel(x + GbAtX[0], y + GbAtY[0]) << 3;
        }

        private int OverrideAtTemplate2(int context, int x, int y, int result, int minorX)
        {
            context &= 0x3fb;
            if (GbAtY[0] == 0 && GbAtX[0] >= -minorX)
            {
                return context | (result >> 7 - (minorX + GbAtX[0]) & 0x1) << 2;
            }

            return context | GetPixel(x + GbAtX[0], y + GbAtY[0]) << 2;
        }

        private int OverrideAtTemplate3(int context, int x, int y, int result, int minorX)
        {
            context &= 0x3ef;
            if (GbAtY[0] == 0 && GbAtX[0] >= -minorX)
            {
                return context | (result >> 7 - (minorX + GbAtX[0]) & 0x1) << 4;
            }

            return context | GetPixel(x + GbAtX[0], y + GbAtY[0]) << 4;
        }

        private byte GetPixel(int x, int y)
        {
            if (x < 0 || x >= regionBitmap.Width)
            {
                return 0;
            }

            if (y < 0 || y >= regionBitmap.Height)
            {
                return 0;
            }

            return regionBitmap.GetPixel(x, y);
        }

        /// <summary>
        /// Used by <see cref="SymbolDictionary"/>.
        /// </summary>
        /// <param name="isMMREncoded">whether the data is MMR-encoded</param>
        /// <param name="dataOffset">the offset</param>
        /// <param name="dataLength">the length of the data</param>
        /// <param name="gbh">bitmap height</param>
        /// <param name="gbw">bitmap width</param>
        internal void SetParameters(bool isMMREncoded, long dataOffset, long dataLength, int gbh, int gbw)
        {
            IsMMREncoded = isMMREncoded;
            this.dataOffset = dataOffset;
            this.dataLength = dataLength;
            RegionInfo.BitmapHeight = gbh;
            RegionInfo.BitmapWidth = gbw;
            mmrDecompressor = null;
            ResetBitmap();
        }

        /// <summary>
        /// Used by <see cref="SymbolDictionary"/>.
        /// </summary>
        /// <param name="isMMREncoded">whether the data is MMR-encoded</param>
        /// <param name="sdTemplate">sd template</param>
        /// <param name="isTPGDon">is TPGDon</param>
        /// <param name="useSkip">use skip</param>
        /// <param name="sdATX">x values gbA pixels</param>
        /// <param name="sdATY">y values gbA pixels</param>
        /// <param name="symWidth">bitmap width</param>
        /// <param name="hcHeight">bitmap height</param>
        /// <param name="cx">context for the arithmetic decoder</param>
        /// <param name="arithmeticDecoder">the arithmetic decode to be used</param>
        internal void SetParameters(bool isMMREncoded, byte sdTemplate,
                bool isTPGDon, bool useSkip, short[] sdATX, short[] sdATY,
                int symWidth, int hcHeight, CX cx,
                ArithmeticDecoder arithmeticDecoder)
        {
            IsMMREncoded = isMMREncoded;
            GbTemplate = sdTemplate;
            IsTPGDon = isTPGDon;
            GbAtX = sdATX;
            GbAtY = sdATY;
            RegionInfo.BitmapWidth = symWidth;
            RegionInfo.BitmapHeight = hcHeight;

            if (null != cx)
            {
                this.cx = cx;
            }

            if (null != arithmeticDecoder)
            {
                arithDecoder = arithmeticDecoder;
            }

            mmrDecompressor = null;
            ResetBitmap();
        }

        /// <summary>
        /// Used by <see cref="PatternDictionary"/> and <see cref="HalftoneRegion"/>.
        /// </summary>
        /// <param name="isMMREncoded">whether the data is MMR-encoded</param>
        /// <param name="dataOffset">the offset</param>
        /// <param name="dataLength">the length of the data</param>
        /// <param name="gbh">bitmap height</param>
        /// <param name="gbw">bitmap width</param>
        /// <param name="gbTemplate">gb template</param>
        /// <param name="isTPGDon">is TPGDon</param>
        /// <param name="useSkip">use skip</param>
        /// <param name="gbAtX">x values of gbA pixels</param>
        /// <param name="gbAtY">y values of gbA pixels</param>
        internal void SetParameters(bool isMMREncoded, long dataOffset,
                    long dataLength, int gbh, int gbw, byte gbTemplate,
                    bool isTPGDon, bool useSkip, short[] gbAtX, short[] gbAtY)
        {
            this.dataOffset = dataOffset;
            this.dataLength = dataLength;

            RegionInfo = new RegionSegmentInformation(gbw, gbh);
            GbTemplate = gbTemplate;

            IsMMREncoded = isMMREncoded;
            IsTPGDon = isTPGDon;
            GbAtX = gbAtX;
            GbAtY = gbAtY;
        }

        /// <summary>
        /// Simply sets the memory-critical bitmap of this region to {@code null}.
        /// </summary>
        internal void ResetBitmap()
        {
            regionBitmap = null;
        }

        public void Init(SegmentHeader header, SubInputStream sis)
        {
            subInputStream = sis;
            RegionInfo = new RegionSegmentInformation(subInputStream);
            ParseHeader();
        }
    }
}

