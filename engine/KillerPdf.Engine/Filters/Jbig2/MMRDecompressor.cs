// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.

#nullable disable


namespace KillerPdf.Engine.Filters.Jbig2
{
    using System;
    using System.IO;
    using System.Text;

    /// <summary>
    /// A decompressor for MMR compression.
    /// </summary>
    internal sealed class MMRDecompressor
    {
        private readonly int width;
        private readonly int height;

        /// <summary>
        /// A class encapsulating the compressed raw data.
        /// </summary>
        private class RunData
        {
            private static readonly int MAX_RUN_DATA_BUFFER = 1024 << 7; // 1024 * 128
            private static readonly int MIN_RUN_DATA_BUFFER = 3; // min. bytes to decompress
            private static readonly int CODE_OFFSET = 24;

            // Compressed data stream.
            private readonly IImageInputStream stream;
            private readonly byte[] buffer;

            private int bufferBase;
            private int bufferTop;

            private int lastOffset = 0;
            private int lastCode = 0;

            internal int Offset { get; set; }

            internal RunData(IImageInputStream stream)
            {
                this.stream = stream;

                Offset = 0;
                lastOffset = 1;

                try
                {
                    long length = stream.Length;
                    length = Math.Min(Math.Max(MIN_RUN_DATA_BUFFER, length), MAX_RUN_DATA_BUFFER);
                    buffer = new byte[(int)length];
                    FillBuffer(0);
                }
                catch (IOException)
                {
                    buffer = new byte[10];
                }
            }

            internal Code UncompressGetCode(Code[] table)
            {
                return UncompressGetCodeLittleEndian(table);
            }

            private Code UncompressGetCodeLittleEndian(Code[] table)
            {
                int code = UncompressGetNextCodeLittleEndian() & 0xffffff;
                Code result = table[code >> CODE_OFFSET - FIRST_LEVEL_TABLE_SIZE];

                // perform second-level lookup
                if (null != result && null != result.SubTable)
                {
                    result = result.SubTable[code >> CODE_OFFSET - FIRST_LEVEL_TABLE_SIZE
                            - SECOND_LEVEL_TABLE_SIZE & SECOND_LEVEL_TABLE_MASK];
                }

                return result;
            }

            /// <summary>
            /// Fill up the code word in little endian mode. This is a hotspot, therefore the algorithm is heavily optimised.
            /// For the frequent cases (i.e. short words) we try to get away with as little work as possible.
            /// This method returns code words of 16 bits, which are aligned to the 24th bit.The lowest 8 bits are used as a
            /// "queue" of bits so that an access to the actual data is only needed, when this queue becomes empty.
            /// </summary>
            private int UncompressGetNextCodeLittleEndian()
            {
                try
                {
                    // the number of bits to fill (offset difference)
                    int bitsToFill = Offset - lastOffset;

                    // check whether we can refill, or need to fill in absolute mode
                    if (bitsToFill < 0 || bitsToFill > 24)
                    {
                        // refill at absolute offset
                        int byteOffset = (Offset >> 3) - bufferBase; // offset >> 3 is equivalent to offset / 8

                        if (byteOffset >= bufferTop)
                        {
                            byteOffset += bufferBase;
                            FillBuffer(byteOffset);
                            byteOffset -= bufferBase;
                        }

                        lastCode = (buffer[byteOffset] & 0xff) << 16
                                   | (buffer[byteOffset + 1] & 0xff) << 8
                                   | buffer[byteOffset + 2] & 0xff;

                        int bitOffset = Offset & 7; // equivalent to offset % 8
                        lastCode <<= bitOffset;
                    }
                    else
                    {
                        // the offset to the next byte boundary as seen from the last offset
                        int bitOffset = lastOffset & 7;
                        int avail = 7 - bitOffset;

                        // check whether there are enough bits in the "queue"
                        if (bitsToFill <= avail)
                        {
                            lastCode <<= bitsToFill;
                        }
                        else
                        {
                            int byteOffset = (lastOffset >> 3) + 3 - bufferBase;

                            if (byteOffset >= bufferTop)
                            {
                                byteOffset += bufferBase;
                                FillBuffer(byteOffset);
                                byteOffset -= bufferBase;
                            }

                            bitOffset = 8 - bitOffset;
                            do
                            {
                                lastCode <<= bitOffset;
                                lastCode |= buffer[byteOffset] & 0xff;
                                bitsToFill -= bitOffset;
                                byteOffset++;
                                bitOffset = 8;
                            } while (bitsToFill >= 8);

                            lastCode <<= bitsToFill; // shift the rest
                        }
                    }

                    lastOffset = Offset;

                    return lastCode;
                }
                catch (IOException e)
                {
                    throw new IndexOutOfRangeException("Corrupted RLE data caused by an IOException while reading raw data: "
                                                       + e.ToString());
                }
            }

            private void FillBuffer(int byteOffset)
            {
                bufferBase = byteOffset;
                lock (stream)
                {
                    try
                    {
                        stream.Seek(byteOffset);
                        bufferTop = stream.Read(buffer);
                    }
                    catch (EndOfStreamException)
                    {
                        // you never know which kind of EOF will kick in
                        bufferTop = -1;
                    }
                    // check filling degree
                    if (bufferTop > -1 && bufferTop < 3)
                    {
                        // CK: if filling degree is too small,
                        // smoothly fill up to the next three bytes or substitute with with
                        // empty bytes
                        int read = 0;
                        while (bufferTop < 3)
                        {
                            try
                            {
                                read = stream.Read();
                            }
                            catch (EndOfStreamException)
                            {
                                read = -1;
                            }
                            buffer[bufferTop++] = read == -1 ? (byte)0 : (byte)(read & 0xff);
                        }
                    }
                }
                // leave some room, in order to save a few tests in the calling code
                bufferTop -= 3;

                if (bufferTop < 0)
                {
                    // if we're at EOF, just supply zero-bytes
                    buffer.AsSpan().Fill(0);
                    bufferTop = buffer.Length - 3;
                }
            }

            /// <summary>
            /// Skip to next byte
            /// </summary>
            internal void Align()
            {
                Offset = Offset + 7 >> 3 << 3;
            }
        }

        private sealed class Code
        {
            internal Code[] SubTable { get; set; }

            internal int BitLength { get; }
            internal int CodeWord { get; }
            internal int RunLength { get; }

            internal Code(int[] codeData)
            {
                BitLength = codeData[0];
                CodeWord = codeData[1];
                RunLength = codeData[2];
            }

            public override string ToString()
            {
                return BitLength + "/" + CodeWord + "/" + RunLength;
            }

            public override bool Equals(object obj)
            {
                return obj is Code code &&
                        code.BitLength == BitLength &&
                        code.CodeWord == CodeWord &&
                        code.RunLength == RunLength;
            }

            public override int GetHashCode()
            {
                return (BitLength, CodeWord, RunLength).GetHashCode();
            }
        }

        private static readonly int FIRST_LEVEL_TABLE_SIZE = 8;
        private static readonly int FIRST_LEVEL_TABLE_MASK = (1 << FIRST_LEVEL_TABLE_SIZE) - 1;
        private static readonly int SECOND_LEVEL_TABLE_SIZE = 5;
        private static readonly int SECOND_LEVEL_TABLE_MASK = (1 << SECOND_LEVEL_TABLE_SIZE) - 1;

        private static Code[] WhiteTable = null;
        private static Code[] BlackTable = null;
        private static Code[] ModeTable = null;

        private readonly RunData data;

        private static void InitTables()
        {
            if (WhiteTable is null)
            {
                WhiteTable = CreateLittleEndianTable(MMRConstants.WhiteCodes);
                BlackTable = CreateLittleEndianTable(MMRConstants.BlackCodes);
                ModeTable = CreateLittleEndianTable(MMRConstants.ModeCodes);
            }
        }

        private static int Uncompress2D(RunData runData, int[] referenceOffsets, int refRunLength,
            int[] runOffsets, int width)
        {
            int referenceBufferOffset = 0;
            int currentBufferOffset = 0;
            int currentLineBitPosition = 0;

            bool whiteRun = true; // Always start with a white run
            Code code = null; // Storage var for current code being processed

            referenceOffsets[refRunLength] = referenceOffsets[refRunLength + 1] = width;
            referenceOffsets[refRunLength + 2] = referenceOffsets[refRunLength + 3] = width + 1;

            try
            {
            decodeLoop:
                while (currentLineBitPosition < width)
                {
                    // Get the mode code
                    code = runData.UncompressGetCode(ModeTable);

                    if (code == null)
                    {
                        runData.Offset++;
                        goto endDecodeLoop;
                    }

                    // Add the code length to the bit offset
                    runData.Offset += code.BitLength;

                    switch (code.RunLength)
                    {
                        case MMRConstants.CODE_V0:
                            currentLineBitPosition = referenceOffsets[referenceBufferOffset];
                            break;

                        case MMRConstants.CODE_VR1:
                            currentLineBitPosition = referenceOffsets[referenceBufferOffset] + 1;
                            break;

                        case MMRConstants.CODE_VL1:
                            currentLineBitPosition = referenceOffsets[referenceBufferOffset] - 1;
                            break;

                        case MMRConstants.CODE_H:
                            for (int ever = 1; ever > 0;)
                            {
                                code = runData.UncompressGetCode(whiteRun == true ? WhiteTable : BlackTable);

                                if (code is null)
                                {
                                    goto endDecodeLoop;
                                }

                                runData.Offset += code.BitLength;
                                if (code.RunLength < 64)
                                {
                                    if (code.RunLength < 0)
                                    {
                                        runOffsets[currentBufferOffset++] = currentLineBitPosition;
                                        code = null;
                                        goto endDecodeLoop;

                                    }

                                    currentLineBitPosition += code.RunLength;
                                    runOffsets[currentBufferOffset++] = currentLineBitPosition;
                                    break;
                                }

                                currentLineBitPosition += code.RunLength;
                            }

                            int firstHalfBitPos = currentLineBitPosition;
                            for (int ever1 = 1; ever1 > 0;)
                            {
                                code = runData.UncompressGetCode(whiteRun != true ? WhiteTable : BlackTable);
                                if (code is null)
                                {
                                    goto endDecodeLoop;
                                }

                                runData.Offset += code.BitLength;
                                if (code.RunLength < 64)
                                {
                                    if (code.RunLength < 0)
                                    {
                                        runOffsets[currentBufferOffset++] = currentLineBitPosition;
                                        goto endDecodeLoop;
                                    }

                                    currentLineBitPosition += code.RunLength;
                                    // don't generate 0-length run at EOL for cases where the line ends in an H-run.
                                    if (currentLineBitPosition < width
                                        || currentLineBitPosition != firstHalfBitPos)
                                    {
                                        runOffsets[currentBufferOffset++] = currentLineBitPosition;
                                    }

                                    break;
                                }

                                currentLineBitPosition += code.RunLength;
                            }

                            while (currentLineBitPosition < width
                                   && referenceOffsets[referenceBufferOffset] <= currentLineBitPosition)
                            {
                                referenceBufferOffset += 2;
                            }

                            goto decodeLoop;

                        case MMRConstants.CODE_P:
                            referenceBufferOffset++;
                            currentLineBitPosition = referenceOffsets[referenceBufferOffset++];
                            goto decodeLoop;

                        case MMRConstants.CODE_VR2:
                            currentLineBitPosition = referenceOffsets[referenceBufferOffset] + 2;
                            break;

                        case MMRConstants.CODE_VL2:
                            currentLineBitPosition = referenceOffsets[referenceBufferOffset] - 2;
                            break;

                        case MMRConstants.CODE_VR3:
                            currentLineBitPosition = referenceOffsets[referenceBufferOffset] + 3;
                            break;

                        case MMRConstants.CODE_VL3:
                            currentLineBitPosition = referenceOffsets[referenceBufferOffset] - 3;
                            break;

                        case MMRConstants.EOL:
                        default:
                            // Possibly MMR-decoded
                            if (runData.Offset == 12 && code.RunLength == MMRConstants.EOL)
                            {
                                runData.Offset = 0;
                                Uncompress1D(runData, referenceOffsets, width);
                                runData.Offset++;
                                Uncompress1D(runData, runOffsets, width);
                                int retCode = Uncompress1D(runData, referenceOffsets, width);
                                runData.Offset++;
                                return retCode;
                            }

                            currentLineBitPosition = width;
                            goto decodeLoop;
                    }

                    // Only vertical modes get this far
                    if (currentLineBitPosition <= width)
                    {
                        whiteRun = !whiteRun;

                        runOffsets[currentBufferOffset++] = currentLineBitPosition;

                        if (referenceBufferOffset > 0)
                        {
                            referenceBufferOffset--;
                        }
                        else
                        {
                            referenceBufferOffset++;
                        }

                        while (currentLineBitPosition < width
                               && referenceOffsets[referenceBufferOffset] <= currentLineBitPosition)
                        {
                            referenceBufferOffset += 2;
                        }
                    }
                }
            }
            catch (Exception)
            {
                var strBuf = new StringBuilder();
                strBuf.Append("whiteRun           = ");
                strBuf.Append(whiteRun);
                strBuf.Append("\n");
                strBuf.Append("code               = ");
                strBuf.Append(code);
                strBuf.Append("\n");
                strBuf.Append("refOffset          = ");
                strBuf.Append(referenceBufferOffset);
                strBuf.Append("\n");
                strBuf.Append("curOffset          = ");
                strBuf.Append(currentBufferOffset);
                strBuf.Append("\n");
                strBuf.Append("bitPos             = ");
                strBuf.Append(currentLineBitPosition);
                strBuf.Append("\n");
                strBuf.Append("runData.offset = ");
                strBuf.Append(runData.Offset);
                strBuf.Append(" ( byte:");
                strBuf.Append(runData.Offset / 8);
                strBuf.Append(", bit:");
                strBuf.Append(runData.Offset & 0x07);
                strBuf.Append(" )");

                return MMRConstants.EOF;
            }

        endDecodeLoop:

            if (runOffsets[currentBufferOffset] != width)
            {
                runOffsets[currentBufferOffset] = width;
            }

            if (code is null)
            {
                return MMRConstants.EOL;
            }

            return currentBufferOffset;
        }

        public MMRDecompressor(int width, int height, IImageInputStream stream)
        {
            this.width = width;
            this.height = height;

            data = new RunData(stream);

            InitTables();
        }

        public Jbig2Bitmap Uncompress()
        {
            Jbig2Bitmap result = new Jbig2Bitmap(width, height);

            int[] currentOffsets = new int[width + 5];
            int[] referenceOffsets = new int[width + 5];
            referenceOffsets[0] = width;
            int refRunLength = 1;

            int count;

            for (int line = 0; line < height; line++)
            {
                count = Uncompress2D(data, referenceOffsets, refRunLength, currentOffsets, width);

                if (count == MMRConstants.EOF)
                {
                    break;
                }

                if (count > 0)
                {
                    FillBitmap(result, line, currentOffsets, count);
                }

                // Swap lines
                int[] tempOffsets = referenceOffsets;
                referenceOffsets = currentOffsets;
                currentOffsets = tempOffsets;
                refRunLength = count;
            }

            DetectAndSkipEOL();

            data.Align();

            return result;
        }

        private void DetectAndSkipEOL()
        {
            while (true)
            {
                Code code = data.UncompressGetCode(ModeTable);
                if (null != code && code.RunLength == MMRConstants.EOL)
                {
                    data.Offset += code.BitLength;
                }
                else
                {
                    break;
                }
            }
        }

        private static void FillBitmap(Jbig2Bitmap result, int line, int[] currentOffsets, int count)
        {
            int x = 0;
            int targetByte = result.GetByteIndex(0, line);
            byte targetByteValue = 0;
            for (int index = 0; index < count; index++)
            {

                int offset = currentOffsets[index];
                byte value;

                if ((index & 1) == 0)
                {
                    value = 0;
                }
                else
                {
                    value = 1;
                }

                while (x < offset)
                {
                    targetByteValue = (byte)(targetByteValue << 1 | value);
                    x++;

                    if ((x & 7) == 0)
                    {
                        result.SetByte(targetByte++, targetByteValue);
                        targetByteValue = 0;
                    }
                }
            }

            if ((x & 7) != 0)
            {
                targetByteValue <<= 8 - (x & 7);
                result.SetByte(targetByte, targetByteValue);
            }
        }

        private static int Uncompress1D(RunData runData, int[] runOffsets, int width)
        {
            bool whiteRun = true;
            int iBitPos = 0;
            Code code = null;
            int refOffset = 0;

            while (iBitPos < width)
            {
                while (true)
                {
                    if (whiteRun)
                    {
                        code = runData.UncompressGetCode(WhiteTable);
                    }
                    else
                    {
                        code = runData.UncompressGetCode(BlackTable);
                    }

                    runData.Offset += code.BitLength;

                    if (code.RunLength < 0)
                    {
                        goto endloop;
                    }

                    iBitPos += code.RunLength;

                    if (code.RunLength < 64)
                    {
                        whiteRun = !whiteRun;
                        runOffsets[refOffset++] = iBitPos;
                        break;
                    }
                }
            }

        endloop:

            runOffsets[refOffset] = width;

            return code != null && code.RunLength != MMRConstants.EOL ? refOffset : MMRConstants.EOL;
        }

        /// <summary>
        /// For little endian, the tables are structured like this:
        /// 
        ///  v--------v length = FIRST_LEVEL_TABLE_LENGTH
        ///  v---- - v length = SECOND_LEVEL_TABLE_LENGTH
        ///  A code word which fits into the first level table(length= 3)
        ///   [Cccvvvvv]
        ///
        ///  A code word which needs the second level table also(length= 10)
        ///   [Cccccccc] -> [ccvvv]
        ///
        ///
        ///  "C" denotes the first code word bit
        ///  "c" denotes a code word bit
        ///  "v" denotes a variant bit
        /// </summary>
        private static Code[] CreateLittleEndianTable(int[][] codes)
        {
            var firstLevelTable = new Code[FIRST_LEVEL_TABLE_MASK + 1];
            for (int i = 0; i < codes.Length; i++)
            {
                var code = new Code(codes[i]);

                if (code.BitLength <= FIRST_LEVEL_TABLE_SIZE)
                {
                    int variantLength = FIRST_LEVEL_TABLE_SIZE - code.BitLength;
                    int baseWord = code.CodeWord << variantLength;

                    for (int variant = (1 << variantLength) - 1; variant >= 0; variant--)
                    {
                        int index = baseWord | variant;
                        firstLevelTable[index] = code;
                    }
                }
                else
                {
                    // init second level table
                    int firstLevelIndex = (int)((uint)code.CodeWord >> code.BitLength
                            - FIRST_LEVEL_TABLE_SIZE);

                    if (firstLevelTable[firstLevelIndex] == null)
                    {
                        var firstLevelCode = new Code(new int[3]);
                        firstLevelCode.SubTable = new Code[SECOND_LEVEL_TABLE_MASK + 1];
                        firstLevelTable[firstLevelIndex] = firstLevelCode;
                    }

                    // fill second level table
                    if (code.BitLength <= FIRST_LEVEL_TABLE_SIZE + SECOND_LEVEL_TABLE_SIZE)
                    {
                        Code[] secondLevelTable = firstLevelTable[firstLevelIndex].SubTable;
                        int variantLength = FIRST_LEVEL_TABLE_SIZE + SECOND_LEVEL_TABLE_SIZE
                                - code.BitLength;
                        int baseWord = code.CodeWord << variantLength & SECOND_LEVEL_TABLE_MASK;

                        for (int variant = (1 << variantLength) - 1; variant >= 0; variant--)
                        {
                            secondLevelTable[baseWord | variant] = code;
                        }
                    }
                    else
                    {
                        throw new ArgumentException("Code table overflow in MMRDecompressor");
                    }
                }
            }
            return firstLevelTable;
        }
    }
}

