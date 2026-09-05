// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.
#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    using System;
    using System.Collections.Generic;
    using static HuffmanTable;

    /// <summary>
    /// This class represented the segment type "Text region", 7.4.3, page 56.
    /// </summary>
    internal sealed class TextRegion : IRegion
    {
        private SubInputStream subInputStream;

        // Text region segment flags, 7.4.3.1.1
        private short sbrTemplate;
        private short sbdsOffset; // 6.4.8
        private short defaultPixel;
        private CombinationOperator combinationOperator;
        private short isTransposed;
        private short referenceCorner;
        private short logSBStrips;
        private bool useRefinement;
        private bool isHuffmanEncoded;

        // Text region segment Huffman flags, 7.4.3.1.2
        private short sbHuffRSize;
        private short sbHuffRDY;
        private short sbHuffRDX;
        private short sbHuffRDHeight;
        private short sbHuffRDWidth;
        private short sbHuffDT;
        private short sbHuffDS;
        private short sbHuffFS;

        // Text region refinement AT flags, 7.4.3.1.3
        private short[] sbrATX;
        private short[] sbrATY;

        // Number of symbol instances, 7.4.3.1.4
        private long amountOfSymbolInstances;

        // Further parameters
        private long currentS;
        private int sbStrips;
        private int amountOfSymbols;

        private Jbig2Bitmap regionBitmap;
        private List<Jbig2Bitmap> symbols = new List<Jbig2Bitmap>();

        private ArithmeticDecoder arithmeticDecoder;
        private ArithmeticIntegerDecoder integerDecoder;
        private GenericRefinementRegion genericRefinementRegion;

        private CX cxIADT;
        private CX cxIAFS;
        private CX cxIADS;
        private CX cxIAIT;
        private CX cxIARI;
        private CX cxIARDW;
        private CX cxIARDH;
        private CX cxIAID;
        private CX cxIARDX;
        private CX cxIARDY;
        private CX cx;

        // codeTable including a code to each symbol used in that region
        private int symbolCodeLength;
        private FixedSizeTable symbolCodeTable;
        private SegmentHeader segmentHeader;

        // User-supplied tables
        private HuffmanTable fsTable;
        private HuffmanTable dsTable;
        private HuffmanTable table;
        private HuffmanTable rdwTable;
        private HuffmanTable rdhTable;
        private HuffmanTable rdxTable;
        private HuffmanTable rdyTable;
        private HuffmanTable rSizeTable;

        // Region segment information field, 7.4.1
        public RegionSegmentInformation RegionInfo { get; private set; }

        public TextRegion()
        {
        }

        public TextRegion(SubInputStream subInputStream, SegmentHeader segmentHeader)
        {
            this.subInputStream = subInputStream;
            RegionInfo = new RegionSegmentInformation(subInputStream);
            this.segmentHeader = segmentHeader;
        }

        private void ParseHeader()
        {
            RegionInfo.ParseHeader();

            ReadRegionFlags();

            if (isHuffmanEncoded)
            {
                ReadHuffmanFlags();
            }

            ReadUseRefinement();

            ReadAmountOfSymbolInstances();

            // 7.4.3.1.7
            GetSymbols();

            ComputeSymbolCodeLength();

            CheckInput();
        }

        private void ReadRegionFlags()
        {
            // Bit 15
            sbrTemplate = (short)subInputStream.ReadBit();

            // Bit 10-14
            sbdsOffset = (short)subInputStream.ReadBits(5);
            if (sbdsOffset > 0x0f)
            {
                sbdsOffset -= 0x20;
            }

            // Bit 9
            defaultPixel = (short)subInputStream.ReadBit();

            // Bit 7-8
            combinationOperator = CombinationOperators
                    .TranslateOperatorCodeToEnum((short)(subInputStream.ReadBits(2) & 0x3));

            // Bit 6
            isTransposed = (short)subInputStream.ReadBit();

            // Bit 4-5
            referenceCorner = (short)(subInputStream.ReadBits(2) & 0x3);

            // Bit 2-3
            logSBStrips = (short)(subInputStream.ReadBits(2) & 0x3);
            sbStrips = 1 << logSBStrips;

            // Bit 1
            if (subInputStream.ReadBit() == 1)
            {
                useRefinement = true;
            }

            // Bit 0
            if (subInputStream.ReadBit() == 1)
            {
                isHuffmanEncoded = true;
            }
        }

        private void ReadHuffmanFlags()
        {
            // Bit 15
            subInputStream.ReadBit(); // Dirty read...

            // Bit 14
            sbHuffRSize = (short)subInputStream.ReadBit();

            // Bit 12-13
            sbHuffRDY = (short)(subInputStream.ReadBits(2) & 0xf);

            // Bit 10-11
            sbHuffRDX = (short)(subInputStream.ReadBits(2) & 0xf);

            // Bit 8-9
            sbHuffRDHeight = (short)(subInputStream.ReadBits(2) & 0xf);

            // Bit 6-7
            sbHuffRDWidth = (short)(subInputStream.ReadBits(2) & 0xf);

            // Bit 4-5
            sbHuffDT = (short)(subInputStream.ReadBits(2) & 0xf);

            // Bit 2-3
            sbHuffDS = (short)(subInputStream.ReadBits(2) & 0xf);

            // Bit 0-1
            sbHuffFS = (short)(subInputStream.ReadBits(2) & 0xf);
        }

        private void ReadUseRefinement()
        {
            if (useRefinement && sbrTemplate == 0)
            {
                sbrATX = new short[2];
                sbrATY = new short[2];

                // Byte 0
                sbrATX[0] = (sbyte)subInputStream.ReadByte();

                // Byte 1
                sbrATY[0] = (sbyte)subInputStream.ReadByte();

                // Byte 2
                sbrATX[1] = (sbyte)subInputStream.ReadByte();

                // Byte 3
                sbrATY[1] = (sbyte)subInputStream.ReadByte();
            }
        }

        private void ReadAmountOfSymbolInstances()
        {
            amountOfSymbolInstances = subInputStream.ReadBits(32) & 0xffffffff;

            // sanity check: don't decode more than one symbol per pixel
            long pixels = RegionInfo.BitmapWidth * (long)RegionInfo.BitmapHeight;
            if (pixels < amountOfSymbolInstances)
            {
                amountOfSymbolInstances = pixels;
            }
        }

        private void GetSymbols()
        {
            if (segmentHeader.RtSegments != null)
            {
                InitSymbols();
            }
        }

        private void ComputeSymbolCodeLength()
        {
            if (isHuffmanEncoded)
            {
                SymbolIDCodeLengths();
            }
            else
            {
                symbolCodeLength = (int)Math.Ceiling(Math.Log(amountOfSymbols) / Math.Log(2));
            }
        }

        private void CheckInput()
        {
            if (!useRefinement)
            {
                sbrTemplate = 0;
            }

            if (sbHuffFS == 2 || sbHuffRDWidth == 2 || sbHuffRDHeight == 2 || sbHuffRDX == 2 || sbHuffRDY == 2)
            {
                throw new InvalidHeaderValueException(
                    "Huffman flag value of text region segment is not permitted");
            }

            if (useRefinement)
            {
                return;
            }

            sbHuffRSize = 0;
            sbHuffRDY = 0;
            sbHuffRDX = 0;
            sbHuffRDWidth = 0;
            sbHuffRDHeight = 0;
        }

        public Jbig2Bitmap GetRegionBitmap()
        {
            if (!isHuffmanEncoded)
            {
                SetCodingStatistics();
            }

            CreateRegionBitmap();
            DecodeSymbolInstances();

            // 4)
            return regionBitmap;
        }

        private void SetCodingStatistics()
        {
            if (cxIADT is null)
            {
                cxIADT = new CX(512, 1);
            }

            if (cxIAFS is null)
            {
                cxIAFS = new CX(512, 1);
            }

            if (cxIADS is null)
            {
                cxIADS = new CX(512, 1);
            }

            if (cxIAIT is null)
            {
                cxIAIT = new CX(512, 1);
            }

            if (cxIARI is null)
            {
                cxIARI = new CX(512, 1);
            }

            if (cxIARDW is null)
            {
                cxIARDW = new CX(512, 1);
            }

            if (cxIARDH is null)
            {
                cxIARDH = new CX(512, 1);
            }

            if (cxIAID is null)
            {
                cxIAID = new CX(1 << symbolCodeLength, 1);
            }

            if (cxIARDX is null)
            {
                cxIARDX = new CX(512, 1);
            }

            if (cxIARDY is null)
            {
                cxIARDY = new CX(512, 1);
            }

            if (arithmeticDecoder is null)
            {
                arithmeticDecoder = new ArithmeticDecoder(subInputStream);
            }

            if (integerDecoder is null)
            {
                integerDecoder = new ArithmeticIntegerDecoder(arithmeticDecoder);
            }
        }

        private void CreateRegionBitmap()
        {
            // 6.4.5
            regionBitmap = new Jbig2Bitmap(RegionInfo.BitmapWidth, RegionInfo.BitmapHeight);

            // 1)
            if (defaultPixel != 0)
            {
                regionBitmap.ByteArray.AsSpan().Fill(0xff);
            }
        }

        private long DecodeStripT()
        {
            long stripT;
            // 2)
            if (isHuffmanEncoded)
            {
                // 6.4.6
                if (sbHuffDT == 3)
                {
                    if (table is null)
                    {
                        int dtNr = 0;

                        if (sbHuffFS == 3)
                        {
                            dtNr++;
                        }

                        if (sbHuffDS == 3)
                        {
                            dtNr++;
                        }

                        table = GetUserTable(dtNr);
                    }
                    stripT = table.Decode(subInputStream);
                }
                else
                {
                    stripT = StandardTables.GetTable(11 + sbHuffDT).Decode(subInputStream);
                }
            }
            else
            {
                stripT = integerDecoder.Decode(cxIADT);
            }

            return stripT * -sbStrips;
        }

        private void DecodeSymbolInstances()
        {
            long stripT = DecodeStripT();

            // Last two sentences of 6.4.5 2)
            long firstS = 0;
            long instanceCounter = 0;

            // 6.4.5 3 a)
            while (instanceCounter < amountOfSymbolInstances)
            {
                long dT = DecodeDT();
                stripT += dT;
                long dfS;

                // 3 c) symbol instances in the strip
                bool first = true;
                currentS = 0;

                // do until OOB
                for (; ; )
                {
                    // 3 c) i) - first symbol instance in the strip
                    if (first)
                    {
                        // 6.4.7
                        dfS = DecodeDfS();
                        firstS += dfS;
                        currentS = firstS;
                        first = false;
                        // 3 c) ii) - the remaining symbol instances in the strip
                    }
                    else
                    {
                        // 6.4.8
                        long idS = DecodeIdS();

                        // If result is OOB, then all the symbol instances in this strip have been decoded; proceed to step
                        // 3 d) respectively 3 b). Also exit, if the expected number of instances have been decoded.
                        // The latter exit condition guards against pathological cases where a strip's S never contains OOB
                        // and thus never terminates as illustrated in
                        // https://bugs.chromium.org/p/chromium/issues/detail?id=450971 case pdfium-loop2.pdf.
                        if (idS == long.MaxValue || instanceCounter >= amountOfSymbolInstances)
                        {
                            break;
                        }

                        currentS += idS + sbdsOffset;
                    }

                    // 3 c) iii)
                    long currentT = DecodeCurrentT();
                    long t = stripT + currentT;

                    // 3 c) iv)
                    long id = DecodeID();

                    // 3 c) v)
                    long r = DecodeRI();
                    // 6.4.11
                    Jbig2Bitmap ib = DecodeIb(r, id);

                    // vi)
                    Blit(ib, t);

                    instanceCounter++;
                }
            }
        }

        private long DecodeDT()
        {
            // 3) b)
            // 6.4.6
            long dT;
            if (isHuffmanEncoded)
            {
                if (sbHuffDT == 3)
                {
                    dT = table.Decode(subInputStream);
                }
                else
                {
                    dT = StandardTables.GetTable(11 + sbHuffDT).Decode(subInputStream);
                }
            }
            else
            {
                dT = integerDecoder.Decode(cxIADT);
            }

            return dT * sbStrips;
        }

        private long DecodeDfS()
        {
            if (isHuffmanEncoded)
            {
                if (sbHuffFS == 3)
                {
                    if (fsTable is null)
                    {
                        fsTable = GetUserTable(0);
                    }
                    return fsTable.Decode(subInputStream);
                }

                return StandardTables.GetTable(6 + sbHuffFS).Decode(subInputStream);
            }

            return integerDecoder.Decode(cxIAFS);
        }

        private long DecodeIdS()
        {
            if (isHuffmanEncoded)
            {
                if (sbHuffDS == 3)
                {
                    if (dsTable is null)
                    {
                        int dsNr = 0;
                        if (sbHuffFS == 3)
                        {
                            dsNr++;
                        }

                        dsTable = GetUserTable(dsNr);
                    }
                    return dsTable.Decode(subInputStream);

                }

                return StandardTables.GetTable(8 + sbHuffDS).Decode(subInputStream);
            }

            return integerDecoder.Decode(cxIADS);
        }

        private long DecodeCurrentT()
        {
            if (sbStrips != 1)
            {
                if (isHuffmanEncoded)
                {
                    return subInputStream.ReadBits(logSBStrips);
                }

                return integerDecoder.Decode(cxIAIT);
            }

            return 0;
        }

        private long DecodeID()
        {
            if (isHuffmanEncoded)
            {
                if (symbolCodeTable is null)
                {
                    return subInputStream.ReadBits(symbolCodeLength);
                }

                return symbolCodeTable.Decode(subInputStream);
            }

            return integerDecoder.DecodeIAID(cxIAID, symbolCodeLength);
        }

        private long DecodeRI()
        {
            if (useRefinement)
            {
                if (isHuffmanEncoded)
                {
                    return subInputStream.ReadBit();
                }

                return integerDecoder.Decode(cxIARI);
            }

            return 0;
        }

        private Jbig2Bitmap DecodeIb(long r, long id)
        {
            Jbig2Bitmap ib;

            if (r == 0)
            {
                ib = symbols[(int)id];
            }
            else
            {
                // 1) - 4)
                long rdw = DecodeRdw();
                long rdh = DecodeRdh();
                long rdx = DecodeRdx();
                long rdy = DecodeRdy();

                // 5)
                /* long symInRefSize = 0; */
                if (isHuffmanEncoded)
                {
                    /* symInRefSize = */
                    DecodeSymInRefSize();
                    subInputStream.SkipBits();
                }

                // 6)
                Jbig2Bitmap ibo = symbols[(int)id];
                int wo = ibo.Width;
                int ho = ibo.Height;

                int genericRegionReferenceDX = (int)((rdw >> 1) + rdx);
                int genericRegionReferenceDY = (int)((rdh >> 1) + rdy);

                if (genericRefinementRegion == null)
                {
                    genericRefinementRegion = new GenericRefinementRegion(subInputStream);
                }

                genericRefinementRegion.SetParameters(cx, arithmeticDecoder, sbrTemplate,
                        (int)(wo + rdw), (int)(ho + rdh), ibo, genericRegionReferenceDX,
                        genericRegionReferenceDY, false, sbrATX, sbrATY);

                ib = genericRefinementRegion.GetRegionBitmap();

                // 7
                if (isHuffmanEncoded)
                {
                    subInputStream.SkipBits();
                }
            }
            return ib;
        }

        private long DecodeRdw()
        {
            if (isHuffmanEncoded)
            {
                if (sbHuffRDWidth == 3)
                {
                    if (rdwTable is null)
                    {
                        int rdwNr = 0;
                        if (sbHuffFS == 3)
                        {
                            rdwNr++;
                        }

                        if (sbHuffDS == 3)
                        {
                            rdwNr++;
                        }

                        if (sbHuffDT == 3)
                        {
                            rdwNr++;
                        }

                        rdwTable = GetUserTable(rdwNr);
                    }
                    return rdwTable.Decode(subInputStream);

                }

                return StandardTables.GetTable(14 + sbHuffRDWidth).Decode(subInputStream);
            }

            return integerDecoder.Decode(cxIARDW);
        }

        private long DecodeRdh()
        {
            if (isHuffmanEncoded)
            {
                if (sbHuffRDHeight == 3)
                {
                    if (rdhTable is null)
                    {
                        int rdhNr = 0;

                        if (sbHuffFS == 3)
                        {
                            rdhNr++;
                        }

                        if (sbHuffDS == 3)
                        {
                            rdhNr++;
                        }

                        if (sbHuffDT == 3)
                        {
                            rdhNr++;
                        }

                        if (sbHuffRDWidth == 3)
                        {
                            rdhNr++;
                        }

                        rdhTable = GetUserTable(rdhNr);
                    }
                    return rdhTable.Decode(subInputStream);
                }

                return StandardTables.GetTable(14 + sbHuffRDHeight).Decode(subInputStream);
            }

            return integerDecoder.Decode(cxIARDH);
        }

        private long DecodeRdx()
        {
            if (isHuffmanEncoded)
            {
                if (sbHuffRDX == 3)
                {
                    if (rdxTable is null)
                    {
                        int rdxNr = 0;
                        if (sbHuffFS == 3)
                        {
                            rdxNr++;
                        }

                        if (sbHuffDS == 3)
                        {
                            rdxNr++;
                        }

                        if (sbHuffDT == 3)
                        {
                            rdxNr++;
                        }

                        if (sbHuffRDWidth == 3)
                        {
                            rdxNr++;
                        }

                        if (sbHuffRDHeight == 3)
                        {
                            rdxNr++;
                        }

                        rdxTable = GetUserTable(rdxNr);
                    }

                    return rdxTable.Decode(subInputStream);
                }

                return StandardTables.GetTable(14 + sbHuffRDX).Decode(subInputStream);
            }

            return integerDecoder.Decode(cxIARDX);
        }

        private long DecodeRdy()
        {
            if (isHuffmanEncoded)
            {
                if (sbHuffRDY == 3)
                {
                    if (rdyTable is null)
                    {
                        int rdyNr = 0;
                        if (sbHuffFS == 3)
                        {
                            rdyNr++;
                        }

                        if (sbHuffDS == 3)
                        {
                            rdyNr++;
                        }

                        if (sbHuffDT == 3)
                        {
                            rdyNr++;
                        }

                        if (sbHuffRDWidth == 3)
                        {
                            rdyNr++;
                        }

                        if (sbHuffRDHeight == 3)
                        {
                            rdyNr++;
                        }

                        if (sbHuffRDX == 3)
                        {
                            rdyNr++;
                        }

                        rdyTable = GetUserTable(rdyNr);
                    }
                    return rdyTable.Decode(subInputStream);
                }

                return StandardTables.GetTable(14 + sbHuffRDY).Decode(subInputStream);
            }

            return integerDecoder.Decode(cxIARDY);
        }

        private long DecodeSymInRefSize()
        {
            if (sbHuffRSize == 0)
            {
                return StandardTables.GetTable(1).Decode(subInputStream);
            }

            if (rSizeTable is null)
            {
                int rSizeNr = 0;

                if (sbHuffFS == 3)
                {
                    rSizeNr++;
                }

                if (sbHuffDS == 3)
                {
                    rSizeNr++;
                }

                if (sbHuffDT == 3)
                {
                    rSizeNr++;
                }

                if (sbHuffRDWidth == 3)
                {
                    rSizeNr++;
                }

                if (sbHuffRDHeight == 3)
                {
                    rSizeNr++;
                }

                if (sbHuffRDX == 3)
                {
                    rSizeNr++;
                }

                if (sbHuffRDY == 3)
                {
                    rSizeNr++;
                }

                rSizeTable = GetUserTable(rSizeNr);
            }

            return rSizeTable.Decode(subInputStream);
        }

        private void Blit(Jbig2Bitmap ib, long t)
        {
            if (isTransposed == 0 && (referenceCorner == 2 || referenceCorner == 3))
            {
                currentS += ib.Width - 1;
            }
            else if (isTransposed == 1 && (referenceCorner == 0 || referenceCorner == 2))
            {
                currentS += ib.Height - 1;
            }

            // vii)
            long s = currentS;

            // viii)
            if (isTransposed == 1)
            {
                (t, s) = (s, t);
                /*
                 * long swap = t;
                 * t = s;
                 * s = swap;
                 */
            }

            if (referenceCorner != 1)
            {
                switch (referenceCorner)
                {
                    case 0:
                        // BL
                        t -= ib.Height - 1;
                        break;
                    case 2:
                        // BR
                        t -= ib.Height - 1;
                        s -= ib.Width - 1;
                        break;
                    case 3:
                        // TR
                        s -= ib.Width - 1;
                        break;
                }
            }

            Jbig2Bitmaps.Blit(ib, regionBitmap, (int)s, (int)t, combinationOperator);

            // x)
            if (isTransposed == 0 && (referenceCorner == 0 || referenceCorner == 1))
            {
                currentS += ib.Width - 1;
            }

            if (isTransposed == 1 && (referenceCorner == 1 || referenceCorner == 3))
            {
                currentS += ib.Height - 1;
            }
        }

        private void InitSymbols()
        {
            foreach (SegmentHeader segment in segmentHeader.RtSegments)
            {
                if (segment.SegmentType == 0)
                {
                    SymbolDictionary sd = (SymbolDictionary)segment.GetSegmentData();

                    sd.cxIAID = cxIAID;
                    symbols.AddRange(sd.GetDictionary());
                }
            }
            amountOfSymbols = symbols.Count;
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

        private void SymbolIDCodeLengths()
        {
            // 1) - 2)
            List<Code> runCodeTable = new List<Code>();

            for (int i = 0; i < 35; i++)
            {
                int prefLen = (int)(subInputStream.ReadBits(4) & 0xf);
                if (prefLen > 0)
                {
                    runCodeTable.Add(new Code(prefLen, 0, i, false));
                }
            }

            HuffmanTable ht = new FixedSizeTable(runCodeTable);

            // 3) - 5)
            long previousCodeLength = 0;

            int counter = 0;
            List<Code> sbSymCodes = new List<Code>();
            while (counter < amountOfSymbols)
            {
                long code = ht.Decode(subInputStream);
                if (code < 32)
                {
                    if (code > 0)
                    {
                        sbSymCodes.Add(new Code((int)code, 0, counter, false));
                    }

                    previousCodeLength = code;
                    counter++;
                }
                else
                {

                    long runLength = 0;
                    long currCodeLength = 0;
                    if (code == 32)
                    {
                        runLength = 3 + subInputStream.ReadBits(2);
                        if (counter > 0)
                        {
                            currCodeLength = previousCodeLength;
                        }
                    }
                    else if (code == 33)
                    {
                        runLength = 3 + subInputStream.ReadBits(3);
                    }
                    else if (code == 34)
                    {
                        runLength = 11 + subInputStream.ReadBits(7);
                    }

                    for (int j = 0; j < runLength; j++)
                    {
                        if (currCodeLength > 0)
                        {
                            sbSymCodes.Add(new Code((int)currCodeLength, 0, counter, false));
                        }
                        counter++;
                    }
                }
            }

            // 6) - Skip over remaining bits in the last Byte read
            subInputStream.SkipBits();

            // 7)
            symbolCodeTable = new FixedSizeTable(sbSymCodes);

        }

        public void Init(SegmentHeader header, SubInputStream sis)
        {
            segmentHeader = header;
            subInputStream = sis;
            RegionInfo = new RegionSegmentInformation(subInputStream);
            ParseHeader();
        }

        internal void SetContexts(CX cx, CX cxIADT, CX cxIAFS, CX cxIADS, CX cxIAIT, CX cxIAID,
                CX cxIARDW, CX cxIARDH, CX cxIARDX, CX cxIARDY)
        {
            this.cx = cx;

            this.cxIADT = cxIADT;
            this.cxIAFS = cxIAFS;
            this.cxIADS = cxIADS;
            this.cxIAIT = cxIAIT;

            this.cxIAID = cxIAID;

            this.cxIARDW = cxIARDW;
            this.cxIARDH = cxIARDH;
            this.cxIARDX = cxIARDX;
            this.cxIARDY = cxIARDY;
        }

        internal void SetParameters(ArithmeticDecoder arithmeticDecoder,
                ArithmeticIntegerDecoder iDecoder, bool isHuffmanEncoded, bool sbRefine, int sbw,
                int sbh, long sbNumInstances, int sbStrips, int sbNumSyms, short sbDefaultPixel,
                short sbCombinationOperator, short transposed, short refCorner, short sbdsOffset,
                short sbHuffFS, short sbHuffDS, short sbHuffDT, short sbHuffRDWidth,
                short sbHuffRDHeight, short sbHuffRDX, short sbHuffRDY, short sbHuffRSize,
                short sbrTemplate, short[] sbrATX, short[] sbrATY, List<Jbig2Bitmap> sbSyms,
                int sbSymCodeLen)
        {

            this.arithmeticDecoder = arithmeticDecoder;

            integerDecoder = iDecoder;

            this.isHuffmanEncoded = isHuffmanEncoded;
            useRefinement = sbRefine;

            RegionInfo.BitmapWidth = sbw;
            RegionInfo.BitmapHeight = sbh;

            amountOfSymbolInstances = sbNumInstances;
            this.sbStrips = sbStrips;
            amountOfSymbols = sbNumSyms;
            defaultPixel = sbDefaultPixel;
            combinationOperator = CombinationOperators
                    .TranslateOperatorCodeToEnum(sbCombinationOperator);
            isTransposed = transposed;
            referenceCorner = refCorner;
            this.sbdsOffset = sbdsOffset;

            this.sbHuffFS = sbHuffFS;
            this.sbHuffDS = sbHuffDS;
            this.sbHuffDT = sbHuffDT;
            this.sbHuffRDWidth = sbHuffRDWidth;
            this.sbHuffRDHeight = sbHuffRDHeight;
            this.sbHuffRDX = sbHuffRDX;
            this.sbHuffRDY = sbHuffRDY;
            this.sbHuffRSize = sbHuffRSize;

            this.sbrTemplate = sbrTemplate;
            this.sbrATX = sbrATX;
            this.sbrATY = sbrATY;

            symbols = sbSyms;
            symbolCodeLength = sbSymCodeLen;
        }
    }
}
