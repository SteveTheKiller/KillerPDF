// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.

#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    using System;
    using System.Collections.Generic;
    
    /// <summary>
    /// This class represents the data of segment type "Symbol dictionary". Parsing is described in
    /// 7.4.2.1.1 - 7.4.1.1.5 and decoding procedure is described in 6.5.
    /// </summary>
    internal sealed class SymbolDictionary : IJbigDictionary
    {
        private SubInputStream subInputStream;

        // Symbol dictionary flags, 7.4.2.1.1
        private short sdrTemplate;
        private byte sdTemplate;
        private bool isCodingContextRetained;
        private bool isCodingContextUsed;
        private short sdHuffAggInstanceSelection;
        private short sdHuffBMSizeSelection;
        private short sdHuffDecodeWidthSelection;
        private short sdHuffDecodeHeightSelection;
        private bool useRefinementAggregation;
        private bool isHuffmanEncoded;

        // Symbol dictionary AT flags, 7.4.2.1.2
        private short[] sdATX;
        private short[] sdATY;

        // Symbol dictionary refinement AT flags, 7.4.2.1.3
        private short[] sdrATX;
        private short[] sdrATY;

        // Number of exported symbols, 7.4.2.1.4
        private int amountOfExportSymbolss;

        // Number of new symbols, 7.4.2.1.5
        private int amountOfNewSymbols;

        // Further parameters
        private SegmentHeader segmentHeader;
        private int amountOfImportedSymbolss;
        private List<Jbig2Bitmap> importSymbols;
        private int amountOfDecodedSymbols;
        private Jbig2Bitmap[] newSymbols;

        // User-supplied tables
        private HuffmanTable dhTable;
        private HuffmanTable dwTable;
        private HuffmanTable bmSizeTable;
        private HuffmanTable aggInstTable;

        // Return value of that segment
        private List<Jbig2Bitmap> exportSymbols;
        private List<Jbig2Bitmap> sbSymbols;

        private ArithmeticDecoder arithmeticDecoder;
        private ArithmeticIntegerDecoder iDecoder;

        private TextRegion textRegion;
        private GenericRegion genericRegion;
        private GenericRefinementRegion genericRefinementRegion;
        private CX cx;

        private CX cxIADH;
        private CX cxIADW;
        private CX cxIAAI;
        private CX cxIAEX;
        private CX cxIARDX;
        private CX cxIARDY;
        private CX cxIADT;

        internal CX cxIAID;
        private int sbSymCodeLen;
        private SymbolDictionary lastSymbolDictionary;

        public SymbolDictionary()
        {
        }

        public SymbolDictionary(SubInputStream subInputStream, SegmentHeader segmentHeader)
        {
            this.subInputStream = subInputStream;
            this.segmentHeader = segmentHeader;
        }

        private void ParseHeader()
        {
            ReadRegionFlags();
            SetAtPixels();
            SetRefinementAtPixels();
            ReadAmountOfExportedSymbols();
            ReadAmountOfNewSymbols();
            SetInSyms();

            bool isContextAvailable = false;
            SegmentHeader[] rtSegments = segmentHeader.RtSegments;
            if (rtSegments != null)
            {
                for (int i = rtSegments.Length - 1; i >= 0; i--)
                {
                    if (rtSegments[i].SegmentType == 0)
                    {
                        lastSymbolDictionary = (SymbolDictionary)rtSegments[i].GetSegmentData();
                        if (isCodingContextUsed && lastSymbolDictionary.isCodingContextRetained)
                        {
                            ValidateContextValues(lastSymbolDictionary);
                            isContextAvailable = true;
                        }
                        break;
                    }
                }
            }

            if (isCodingContextUsed && !isContextAvailable)
            {
                throw new InvalidOperationException(lastSymbolDictionary is null
                    ? "Coding context reuse requested without a referred symbol dictionary."
                    : "Coding context reuse requested from a dictionary that did not retain it.");
            }

            CheckInput();
        }

        private void ReadRegionFlags()
        {
            // Bit 13-15
            subInputStream.ReadBits(3); // Dirty read... reserved bits must be 0

            // Bit 12
            sdrTemplate = (short)subInputStream.ReadBit();

            // Bit 10-11
            sdTemplate = (byte)(subInputStream.ReadBits(2) & 0xf);

            // Bit 9
            if (subInputStream.ReadBit() == 1)
            {
                isCodingContextRetained = true;
            }

            // Bit 8
            if (subInputStream.ReadBit() == 1)
            {
                isCodingContextUsed = true;
            }

            // Bit 7
            sdHuffAggInstanceSelection = (short)subInputStream.ReadBit();

            // Bit 6
            sdHuffBMSizeSelection = (short)subInputStream.ReadBit();

            // Bit 4-5
            sdHuffDecodeWidthSelection = (short)(subInputStream.ReadBits(2) & 0xf);

            // Bit 2-3
            sdHuffDecodeHeightSelection = (short)(subInputStream.ReadBits(2) & 0xf);

            // Bit 1
            if (subInputStream.ReadBit() == 1)
            {
                useRefinementAggregation = true;
            }

            // Bit 0
            if (subInputStream.ReadBit() == 1)
            {
                isHuffmanEncoded = true;
            }
        }

        private void SetAtPixels()
        {
            if (isHuffmanEncoded)
            {
                return;
            }

            ReadAtPixels(sdTemplate == 0 ? 4 : 1);
        }

        private void SetRefinementAtPixels()
        {
            if (useRefinementAggregation && sdrTemplate == 0)
            {
                ReadRefinementAtPixels(2);
            }
        }

        private void ReadAtPixels(int amountOfPixels)
        {
            sdATX = new short[amountOfPixels];
            sdATY = new short[amountOfPixels];

            for (int i = 0; i < amountOfPixels; i++)
            {
                sdATX[i] = (sbyte)subInputStream.ReadByte();
                sdATY[i] = (sbyte)subInputStream.ReadByte();
            }
        }

        private void ReadRefinementAtPixels(int amountOfAtPixels)
        {
            sdrATX = new short[amountOfAtPixels];
            sdrATY = new short[amountOfAtPixels];

            for (int i = 0; i < amountOfAtPixels; i++)
            {
                sdrATX[i] = (sbyte)subInputStream.ReadByte();
                sdrATY[i] = (sbyte)subInputStream.ReadByte();
            }
        }

        private void ReadAmountOfExportedSymbols()
        {
            amountOfExportSymbolss = (int)subInputStream.ReadBits(32);
        }

        private void ReadAmountOfNewSymbols()
        {
            amountOfNewSymbols = (int)subInputStream.ReadBits(32);
        }

        private void SetInSyms()
        {
            if (segmentHeader.RtSegments != null)
            {
                RetrieveImportSymbols();
            }
            else
            {
                importSymbols = new List<Jbig2Bitmap>();
            }
        }

        private void AdoptRetainedCodingContexts(SymbolDictionary sd)
        {
            cx = sd.cx.Copy();
        }

        private void ValidateContextValues(SymbolDictionary sd)
        {
            if (isHuffmanEncoded != sd.isHuffmanEncoded
                || useRefinementAggregation != sd.useRefinementAggregation
                || sdTemplate != sd.sdTemplate
                || sdrTemplate != sd.sdrTemplate
                || !ArraysEqual(sdATX, sd.sdATX)
                || !ArraysEqual(sdATY, sd.sdATY)
                || !ArraysEqual(sdrATX, sd.sdrATX)
                || !ArraysEqual(sdrATY, sd.sdrATY))
            {
                throw new InvalidOperationException("Reused JBIG2 symbol dictionary contexts have incompatible settings.");
            }
        }

        private static bool ArraysEqual(short[] left, short[] right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is null || right is null || left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private void CheckInput()
        {
            if (isHuffmanEncoded)
            {
                sdTemplate = 0;

                if (!useRefinementAggregation)
                {
                    if (isCodingContextRetained)
                    {
                        isCodingContextRetained = false;
                    }

                    if (isCodingContextUsed)
                    {
                        isCodingContextUsed = false;
                    }
                }
            }
            else
            {
                sdHuffBMSizeSelection = 0;
                sdHuffDecodeWidthSelection = 0;
                sdHuffDecodeHeightSelection = 0;
            }

            if (!useRefinementAggregation)
            {
                sdrTemplate = 0;
            }

            if (!isHuffmanEncoded || !useRefinementAggregation)
            {
                sdHuffAggInstanceSelection = 0;
            }
        }

        /// <summary>
        /// 6.5.5 Decoding the symbol dictionary.
        /// </summary>
        /// <returns>List of decoded symbol bitmaps.</returns>
        public List<Jbig2Bitmap> GetDictionary()
        {
            if (exportSymbols is null)
            {
                if (useRefinementAggregation)
                {
                    sbSymCodeLen = GetSbSymCodeLen();
                }

                SetSymbolsArray();

                if (!isHuffmanEncoded || useRefinementAggregation)
                {
                    if (isCodingContextUsed)
                    {
                        AdoptRetainedCodingContexts(lastSymbolDictionary);
                    }
                    else
                    {
                        cx = new CX(65536, 1);
                    }
                }

                if (!isHuffmanEncoded)
                {
                    SetCodingStatistics();
                }

                // 6.5.5 1)
                newSymbols = new Jbig2Bitmap[amountOfNewSymbols];

                // 6.5.5 2)
                int[] newSymbolsWidths = null;
                if (isHuffmanEncoded && !useRefinementAggregation)
                {
                    newSymbolsWidths = new int[amountOfNewSymbols];
                }

                // 6.5.5 3)
                int heightClassHeight = 0;
                amountOfDecodedSymbols = 0;

                // 6.5.5 4 a)
                while (amountOfDecodedSymbols < amountOfNewSymbols)
                {
                    // 6.5.5 4 b)
                    heightClassHeight += (int)DecodeHeightClassDeltaHeight();
                    int symbolWidth = 0;
                    int totalWidth = 0;
                    int heightClassFirstSymbolIndex = amountOfDecodedSymbols;

                    // 6.5.5 4 c)

                    // Repeat until OOB - OOB sends a break;
                    while (true)
                    {
                        // 4 c) i)
                        long differenceWidth = DecodeDifferenceWidth();

                        // If result is OOB (out-of-band), then all the symbols in this height
                        // class has been decoded; proceed to step 4 d). Also exit, if the
                        // expected number of symbols have been decoded.
                        // The latter exit condition guards against pathological cases where
                        // a symbol's DW never contains OOB and thus never terminates.
                        if (differenceWidth == long.MaxValue || amountOfDecodedSymbols >= amountOfNewSymbols)
                        {
                            break;
                        }

                        symbolWidth += (int)differenceWidth;
                        totalWidth += symbolWidth;

                        // 4 c) ii)
                        if (!isHuffmanEncoded || useRefinementAggregation)
                        {
                            if (!useRefinementAggregation)
                            {
                                // 6.5.8.1 - Direct coded
                                DecodeDirectlyThroughGenericRegion(symbolWidth, heightClassHeight);
                            }
                            else
                            {
                                // 6.5.8.2 - Refinement/Aggregate-coded
                                DecodeAggregate(symbolWidth, heightClassHeight);
                            }
                        }
                        else if (isHuffmanEncoded && !useRefinementAggregation)
                        {
                            // 4 c) iii)
                            newSymbolsWidths[amountOfDecodedSymbols] = symbolWidth;
                        }
                        amountOfDecodedSymbols++;
                    }

                    // 6.5.5 4 d)
                    if (isHuffmanEncoded && !useRefinementAggregation)
                    {
                        // 6.5.9
                        long bmSize;
                        if (sdHuffBMSizeSelection == 0)
                        {
                            bmSize = StandardTables.GetTable(1).Decode(subInputStream);
                        }
                        else
                        {
                            bmSize = HuffDecodeBmSize();
                        }

                        subInputStream.SkipBits();

                        Jbig2Bitmap heightClassCollectiveBitmap =
                            DecodeHeightClassCollectiveBitmap(bmSize, heightClassHeight, totalWidth);

                        subInputStream.SkipBits();
                        DecodeHeightClassBitmap(heightClassCollectiveBitmap,
                            heightClassFirstSymbolIndex, heightClassHeight, newSymbolsWidths);
                    }
                }

                // 5)
                // 6.5.10 1) - 5)

                int[] exFlags = GetToExportFlags();

                // 6.5.10 6) - 8)
                SetExportedSymbols(exFlags);
            }

            return exportSymbols;
        }

        private void SetCodingStatistics()
        {
            if (cxIADT is null)
            {
                cxIADT = new CX(512, 1);
            }

            if (cxIADH is null)
            {
                cxIADH = new CX(512, 1);
            }

            if (cxIADW is null)
            {
                cxIADW = new CX(512, 1);
            }

            if (cxIAAI is null)
            {
                cxIAAI = new CX(512, 1);
            }

            if (cxIAEX is null)
            {
                cxIAEX = new CX(512, 1);
            }

            if (useRefinementAggregation && cxIAID is null)
            {
                cxIAID = new CX(1 << sbSymCodeLen, 1);
                cxIARDX = new CX(512, 1);
                cxIARDY = new CX(512, 1);
            }

            arithmeticDecoder = new ArithmeticDecoder(subInputStream);
            iDecoder = new ArithmeticIntegerDecoder(arithmeticDecoder);
        }

        private void DecodeHeightClassBitmap(Jbig2Bitmap heightClassCollectiveBitmap,
                int heightClassFirstSymbol, int heightClassHeight, int[] newSymbolsWidths)
        {
            for (int i = heightClassFirstSymbol; i < amountOfDecodedSymbols; i++)
            {
                int startColumn = 0;

                for (int j = heightClassFirstSymbol; j <= i - 1; j++)
                {
                    startColumn += newSymbolsWidths[j];
                }

                var roi = new Jbig2Rectangle(startColumn, 0, newSymbolsWidths[i], heightClassHeight);
                var symbolBitmap = Jbig2Bitmaps.Extract(roi, heightClassCollectiveBitmap);
                newSymbols[i] = symbolBitmap;
                sbSymbols.Add(symbolBitmap);
            }
        }

        private void DecodeAggregate(int symbolWidth, int heightClassHeight)
        {
            // 6.5.8.2 1)
            // 6.5.8.2.1 - Number of symbol instances in aggregation
            long amountOfRefinementAggregationInstances;
            if (isHuffmanEncoded)
            {
                amountOfRefinementAggregationInstances = HuffDecodeRefAggNInst();
            }
            else
            {
                amountOfRefinementAggregationInstances = iDecoder.Decode(cxIAAI);
            }

            if (amountOfRefinementAggregationInstances > 1)
            {
                // 6.5.8.2 2)
                DecodeThroughTextRegion(symbolWidth, heightClassHeight,
                        amountOfRefinementAggregationInstances);
            }
            else if (amountOfRefinementAggregationInstances == 1)
            {
                // 6.5.8.2 3) refers to 6.5.8.2.2
                DecodeRefinedSymbol(symbolWidth, heightClassHeight);
            }
        }

        private long HuffDecodeRefAggNInst()
        {
            if (sdHuffAggInstanceSelection == 0)
            {
                return StandardTables.GetTable(1).Decode(subInputStream);
            }

            if (sdHuffAggInstanceSelection == 1)
            {
                if (aggInstTable is null)
                {
                    int aggregationInstanceNumber = 0;

                    if (sdHuffDecodeHeightSelection == 3)
                    {
                        aggregationInstanceNumber++;
                    }
                    if (sdHuffDecodeWidthSelection == 3)
                    {
                        aggregationInstanceNumber++;
                    }
                    if (sdHuffBMSizeSelection == 3)
                    {
                        aggregationInstanceNumber++;
                    }

                    aggInstTable = GetUserTable(aggregationInstanceNumber);
                }
                return aggInstTable.Decode(subInputStream);
            }
            return 0;
        }

        private void DecodeThroughTextRegion(int symbolWidth, int heightClassHeight,
                long amountOfRefinementAggregationInstances)
        {
            if (textRegion is null)
            {
                textRegion = new TextRegion(subInputStream, null);

                textRegion.SetContexts(cx, // default context
                        new CX(512, 1), // IADT
                        new CX(512, 1), // IAFS
                        new CX(512, 1), // IADS
                        new CX(512, 1), // IAIT
                        cxIAID, // IAID
                        new CX(512, 1), // IARDW
                        new CX(512, 1), // IARDH
                        new CX(512, 1), // IARDX
                        new CX(512, 1) // IARDY
                );
            }

            // 6.5.8.2.4 Concatenating the array used as parameter later.
            SetSymbolsArray();

            // 6.5.8.2 2) Parameters set according to Table 17, page 36
            textRegion.SetParameters(arithmeticDecoder, iDecoder, isHuffmanEncoded, true, symbolWidth,
                    heightClassHeight, amountOfRefinementAggregationInstances, 1,
                    amountOfImportedSymbolss + amountOfDecodedSymbols, 0, 0, 0, 1, 0, 0, 0,
                    0, 0, 0, 0, 0, 0, sdrTemplate, sdrATX, sdrATY, sbSymbols, sbSymCodeLen);

            AddSymbol(textRegion);
        }

        private void DecodeRefinedSymbol(int symbolWidth, int heightClassHeight)
        {
            int id;
            int rdx;
            int rdy;
            long symInRefSize = 0;
            long streamPosition0 = 0;
            if (isHuffmanEncoded)
            {
                // 2) - 4)
                id = (int)subInputStream.ReadBits(sbSymCodeLen);
                rdx = (int)StandardTables.GetTable(15).Decode(subInputStream);
                rdy = (int)StandardTables.GetTable(15).Decode(subInputStream);

                // 5) a)
                symInRefSize = StandardTables.GetTable(1).Decode(subInputStream);

                // 5) b) - Skip over remaining bits
                subInputStream.SkipBits();
                streamPosition0 = subInputStream.Position;
            }
            else
            {
                // 2) - 4)
                id = iDecoder.DecodeIAID(cxIAID, sbSymCodeLen);
                rdx = (int)iDecoder.Decode(cxIARDX);
                rdy = (int)iDecoder.Decode(cxIARDY);
            }

            // 6)
            SetSymbolsArray();
            Jbig2Bitmap ibo = sbSymbols[id];
            DecodeNewSymbols(symbolWidth, heightClassHeight, ibo, rdx, rdy);

            // 7)
            if (isHuffmanEncoded)
            {
                if (subInputStream.Position > streamPosition0 + symInRefSize)
                {
                    throw new InvalidOperationException(
                        $"Refinement bitmap bytes expected: {symInRefSize}, bytes read: {subInputStream.Position - streamPosition0}.");
                }

                subInputStream.Seek(streamPosition0 + symInRefSize);
            }
        }

        private void DecodeNewSymbols(int symWidth, int hcHeight, Jbig2Bitmap ibo, int rdx, int rdy)
        {
            if (genericRefinementRegion is null)
            {
                genericRefinementRegion = new GenericRefinementRegion(subInputStream);

                if (arithmeticDecoder is null)
                {
                    arithmeticDecoder = new ArithmeticDecoder(subInputStream);
                }

                if (cx is null)
                {
                    cx = new CX(65536, 1);
                }
            }

            // Parameters as shown in Table 18, page 36
            genericRefinementRegion.SetParameters(cx, arithmeticDecoder, sdrTemplate, symWidth,
                    hcHeight, ibo, rdx, rdy, false, sdrATX, sdrATY);

            AddSymbol(genericRefinementRegion);
        }

        private void DecodeDirectlyThroughGenericRegion(int symWidth, int hcHeight)
        {
            if (genericRegion is null)
            {
                genericRegion = new GenericRegion(subInputStream);
            }

            // Parameters set according to Table 16, page 35
            genericRegion.SetParameters(false, sdTemplate, false, false, sdATX, sdATY, symWidth,
                    hcHeight, cx, arithmeticDecoder);

            AddSymbol(genericRegion);
        }

        private void AddSymbol(IRegion region)
        {
            Jbig2Bitmap symbol = region.GetRegionBitmap();
            newSymbols[amountOfDecodedSymbols] = symbol;
            sbSymbols.Add(symbol);
        }

        private long DecodeDifferenceWidth()
        {
            if (isHuffmanEncoded)
            {
                switch (sdHuffDecodeWidthSelection)
                {
                    case 0:
                        return StandardTables.GetTable(2).Decode(subInputStream);

                    case 1:
                        return StandardTables.GetTable(3).Decode(subInputStream);

                    case 3:
                        if (dwTable is null)
                        {
                            int dwNr = 0;

                            if (sdHuffDecodeHeightSelection == 3)
                            {
                                dwNr++;
                            }
                            dwTable = GetUserTable(dwNr);
                        }

                        return dwTable.Decode(subInputStream);
                }
            }
            else
            {
                return iDecoder.Decode(cxIADW);
            }
            return 0;
        }

        private long DecodeHeightClassDeltaHeight()
        {
            if (isHuffmanEncoded)
            {
                return DecodeHeightClassDeltaHeightWithHuffman();
            }
            else
            {
                return iDecoder.Decode(cxIADH);
            }
        }


        /// <summary>
        /// 6.5.6 if isHuffmanEncoded,
        /// </summary>
        /// <returns>Result of decoding HCDH</returns>
        private long DecodeHeightClassDeltaHeightWithHuffman()
        {
            switch (sdHuffDecodeHeightSelection)
            {
                case 0:
                    return StandardTables.GetTable(4).Decode(subInputStream);

                case 1:
                    return StandardTables.GetTable(5).Decode(subInputStream);

                case 3:
                    if (dhTable is null)
                    {
                        dhTable = GetUserTable(0);
                    }
                    return dhTable.Decode(subInputStream);
            }

            return 0;
        }

        private Jbig2Bitmap DecodeHeightClassCollectiveBitmap(long bmSize,
                int heightClassHeight, int totalWidth)
        {
            if (bmSize == 0)
            {
                Jbig2Bitmap heightClassCollectiveBitmap = new Jbig2Bitmap(totalWidth, heightClassHeight);

                for (int i = 0; i < heightClassCollectiveBitmap.ByteArray.Length; i++)
                {
                    heightClassCollectiveBitmap.SetByte(i, subInputStream.ReadByte());
                }

                return heightClassCollectiveBitmap;
            }
            else
            {
                if (genericRegion is null)
                {
                    genericRegion = new GenericRegion(subInputStream);
                }

                genericRegion.SetParameters(true, subInputStream.Position, bmSize,
                        heightClassHeight, totalWidth);

                return genericRegion.GetRegionBitmap();
            }
        }

        private void SetExportedSymbols(int[] toExportFlags)
        {
            exportSymbols = new List<Jbig2Bitmap>(amountOfExportSymbolss);

            for (int i = 0; i < amountOfImportedSymbolss + amountOfNewSymbols; i++)
            {
                if (toExportFlags[i] == 1)
                {
                    if (i < amountOfImportedSymbolss)
                    {
                        exportSymbols.Add(importSymbols[i]);
                    }
                    else
                    {
                        exportSymbols.Add(newSymbols[i - amountOfImportedSymbolss]);
                    }
                }
            }
        }

        private int[] GetToExportFlags()
        {
            int currentExportFlag = 0;
            int[] exportFlags = new int[amountOfImportedSymbolss + amountOfNewSymbols];

            long exRunLength;
            for (int exportIndex = 0; exportIndex < amountOfImportedSymbolss
                    + amountOfNewSymbols; exportIndex += (int)exRunLength)
            {
                if (isHuffmanEncoded)
                {
                    exRunLength = StandardTables.GetTable(1).Decode(subInputStream);
                }
                else
                {
                    exRunLength = iDecoder.Decode(cxIAEX);
                }

                if (exRunLength != 0)
                {
                    if (exRunLength < 0 || exRunLength > exportFlags.Length - exportIndex)
                    {
                        throw new InvalidOperationException("JBIG2 symbol export flags exceed the dictionary symbol count.");
                    }

                    for (int index = exportIndex; index < exportIndex + (int)exRunLength; index++)
                    {
                        exportFlags[index] = currentExportFlag;
                    }
                }

                currentExportFlag = currentExportFlag == 0 ? 1 : 0;
            }

            return exportFlags;
        }

        private long HuffDecodeBmSize()
        {
            if (bmSizeTable is null)
            {
                int bmNr = 0;

                if (sdHuffDecodeHeightSelection == 3)
                {
                    bmNr++;
                }

                if (sdHuffDecodeWidthSelection == 3)
                {
                    bmNr++;
                }

                bmSizeTable = GetUserTable(bmNr);
            }
            return bmSizeTable.Decode(subInputStream);
        }

        /// <summary>
        /// 6.5.8.2.3 - Setting SBSYMCODES and SBSYMCODELEN
        /// </summary>
        /// <returns>Result of computing SBSYMCODELEN</returns>
        private int GetSbSymCodeLen()
        {
            if (isHuffmanEncoded)
            {
                return Math.Max(
                        (int)Math.Ceiling(
                                Math.Log(amountOfImportedSymbolss + amountOfNewSymbols) / Math.Log(2)),
                        1);
            }
            return (int)Math.Ceiling(Math.Log(amountOfImportedSymbolss + amountOfNewSymbols) / Math.Log(2));
        }


        /// <summary>
        /// 6.5.8.2.4 - Setting SBSYMS
        /// </summary>
        private void SetSymbolsArray()
        {
            if (importSymbols is null)
            {
                RetrieveImportSymbols();
            }

            if (sbSymbols is null)
            {
                sbSymbols = new List<Jbig2Bitmap>(importSymbols);
            }
        }

        /// <summary>
        /// Concatenates symbols from all referred-to segments.
        /// </summary>
        private void RetrieveImportSymbols()
        {
            importSymbols = new List<Jbig2Bitmap>();
            foreach (SegmentHeader referredToSegmentHeader in segmentHeader.RtSegments)
            {
                if (referredToSegmentHeader.SegmentType == 0)
                {
                    var sd = (SymbolDictionary)referredToSegmentHeader.GetSegmentData();
                    importSymbols.AddRange(sd.GetDictionary());
                    amountOfImportedSymbolss += sd.amountOfExportSymbolss;
                }
            }
        }

        private HuffmanTable GetUserTable(int tablePosition)
        {
            int tableCounter = 0;

            foreach (SegmentHeader referredToSegmentHeader in segmentHeader.RtSegments)
            {
                if (referredToSegmentHeader.SegmentType == 53)
                {
                    if (tableCounter == tablePosition)
                    {
                        Table t = (Table)referredToSegmentHeader.GetSegmentData();
                        return new EncodedTable(t);
                    }

                    tableCounter++;
                }
            }
            return null;
        }

        public void Init(SegmentHeader header, SubInputStream sis)
        {
            subInputStream = sis;
            segmentHeader = header;
            ParseHeader();
        }
    }
}
