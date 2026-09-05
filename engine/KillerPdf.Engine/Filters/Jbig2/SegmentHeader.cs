// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.

#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    using System;
    using System.Text;

    /// <summary>
    /// The basic class for all JBIG2 segments.
    /// </summary>
    internal sealed class SegmentHeader
    {
        private static ISegmentData CreateSegmentData(int segmentType)
        {
            switch (segmentType)
            {
                case 0:
                    return new SymbolDictionary();

                case 4:
                case 6:
                case 7:
                    return new TextRegion();

                case 16:
                    return new PatternDictionary();

                case 20:
                case 22:
                case 23:
                    return new HalftoneRegion();

                case 36:
                case 38:
                case 39:
                    return new GenericRegion();

                case 40:
                case 42:
                case 43:
                    return new GenericRefinementRegion();

                case 48:
                    return new PageInformation();

                case 50:
                    return new EndOfStripe();

                case 52:
                    return new Profiles();

                case 53:
                    return new Table();

                default:
                    throw new InvalidOperationException($"No segment class for type {segmentType}.");
            }
        }

        private readonly SubInputStream subInputStream;

        private byte pageAssociationFieldSize;

        private WeakReference<ISegmentData> segmentData;

        public SegmentHeader[] RtSegments { get; private set; }

        public int SegmentNumber { get; private set; }

        public int SegmentType { get; private set; }

        public int PageAssociation { get; private set; }

        public long SegmentHeaderLength { get; private set; }

        public long SegmentDataLength { get; private set; }

        public long SegmentDataStartOffset { get; set; }

        public bool IsRetained { get; private set; }

        public SegmentHeader(Jbig2Document document, SubInputStream sis, long offset, int organisationType)
        {
            subInputStream = sis;
            Parse(document, sis, offset, organisationType);
        }

        private void Parse(Jbig2Document document, IImageInputStream subInputStr, long offset, int organisationType)
        {
            subInputStr.Seek(offset);

            // 7.2.2 Segment number
            ReadSegmentNumber(subInputStr);

            // 7.2.3 Segment header flags
            ReadSegmentHeaderFlag(subInputStr);

            // 7.2.4 Amount of referred-to segments
            int countOfReferredToSegments = ReadAmountOfReferredToSegments(subInputStr);

            // 7.2.5 Referred-to segments numbers
            int[] referredToSegmentNumbers = ReadReferredToSegmentsNumbers(subInputStr, countOfReferredToSegments);

            // 7.2.6 Segment page association (Checks how big the page association field is.)
            ReadSegmentPageAssociation(document, subInputStr, countOfReferredToSegments, referredToSegmentNumbers);

            // 7.2.7 Segment data length (Contains the length of the data part (in bytes).)
            ReadSegmentDataLength(subInputStr);

            ReadDataStartOffset(subInputStr, organisationType);
            ReadSegmentHeaderLength(subInputStr, offset);
        }

        /// <summary>
        /// 7.2.2 Segment number
        /// </summary>
        private void ReadSegmentNumber(IImageInputStream subInputStr)
        {
            SegmentNumber = (int)(subInputStr.ReadBits(32) & 0xffffffff);
        }

        /// <summary>
        /// 7.2.3 Segment header flags
        /// </summary>
        private void ReadSegmentHeaderFlag(IImageInputStream subInputStr)
        {
            // Bit 7: Retain Flag, if 1, this segment is flagged as retained;
            IsRetained = subInputStr.ReadBit() == 1;

            // Bit 6: Size of the page association field. One byte if 0, four bytes if 1;
            pageAssociationFieldSize = (byte)subInputStr.ReadBit();

            // Bit 5-0: Contains the values (between 0 and 62 with gaps) for segment types, specified in 7.3
            SegmentType = (int)(subInputStr.ReadBits(6) & 0xff);
        }

        /// <summary>
        /// 7.2.4 Amount of referred-to segments
        /// </summary>
        private static int ReadAmountOfReferredToSegments(IImageInputStream subInputStr)
        {
            int countOfRTS = (int)(subInputStr.ReadBits(3) & 0xf);

            byte[] retainBit;

            if (countOfRTS <= 4)
            {
                // Short format
                retainBit = new byte[5];
                for (int i = 0; i <= 4; i++)
                {
                    retainBit[i] = (byte)subInputStr.ReadBit();
                }
            }
            else
            {
                // Long format
                countOfRTS = (int)(subInputStr.ReadBits(29) & 0xffffffff);

                int arrayLength = countOfRTS + 8 >> 3;
                retainBit = new byte[arrayLength <<= 3];

                for (int i = 0; i < arrayLength; i++)
                {
                    retainBit[i] = (byte)subInputStr.ReadBit();
                }
            }
            return countOfRTS;
        }

        /// <summary>
        /// 7.2.5 Referred-to segments numbers
        /// Gathers all segment numbers of referred-to segments.The segments itself are stored in the
        /// <see cref="RtSegments"/> array.
        /// </summary>
        /// <param name="subInputStr">Wrapped source data input stream.</param>
        /// <param name="countOfReferredToSegments">The number of referred - to segments.</param>
        /// <returns>An array with the segment number of all referred - to segments.</returns>
        private int[] ReadReferredToSegmentsNumbers(IImageInputStream subInputStr, int countOfReferredToSegments)
        {
            int[] result = new int[countOfReferredToSegments];

            if (countOfReferredToSegments > 0)
            {
                short rtsSize = 1;
                if (SegmentNumber > 256)
                {
                    rtsSize = 2;
                    if (SegmentNumber > 65536)
                    {
                        rtsSize = 4;
                    }
                }

                RtSegments = new SegmentHeader[countOfReferredToSegments];

                for (int i = 0; i < countOfReferredToSegments; i++)
                {
                    result[i] = (int)(subInputStr.ReadBits(rtsSize << 3) & 0xffffffff);
                }
            }

            return result;
        }

        /// <summary>
        /// 7.2.6 Segment page association
        /// </summary>
        private void ReadSegmentPageAssociation(Jbig2Document document, IImageInputStream subInputStr,
                int countOfReferredToSegments, int[] referredToSegmentNumbers)
        {
            if (pageAssociationFieldSize == 0)
            {
                // Short format
                PageAssociation = (short)(subInputStr.ReadBits(8) & 0xff);
            }
            else
            {
                // Long format
                PageAssociation = (int)(subInputStr.ReadBits(32) & 0xffffffff);
            }

            if (countOfReferredToSegments > 0)
            {
                Jbig2Page page = document.GetPage(PageAssociation);
                for (int i = 0; i < countOfReferredToSegments; i++)
                {
                    RtSegments[i] = null != page ? page.GetSegment(referredToSegmentNumbers[i])
                            : document.GetGlobalSegment(referredToSegmentNumbers[i]);
                }
            }
        }

        /// <summary>
        /// 7.2.7 Segment data length. Reads the length of the data part in bytes.
        /// </summary>
        private void ReadSegmentDataLength(IImageInputStream subInputStr)
        {
            SegmentDataLength = subInputStr.ReadBits(32) & 0xffffffff;
        }

        /// <summary>
        /// Sets the offset only if organization type is SEQUENTIAL. If random, data starts after segment headers and can be
        /// determined when all segment headers are parsed and allocated.
        /// </summary>
        private void ReadDataStartOffset(IImageInputStream subInputStr, int organisationType)
        {
            if (organisationType == Jbig2Document.SEQUENTIAL)
            {
                SegmentDataStartOffset = subInputStr.Position;
            }
        }

        private void ReadSegmentHeaderLength(IImageInputStream subInputStr, long offset)
        {
            SegmentHeaderLength = subInputStr.Position - offset;
        }

        /// <summary>
        /// Creates and returns a new <see cref="SubInputStream"/> that provides the data part of this segment.
        /// It is a clipped view of the source input stream.
        /// </summary>
        /// <returns>The <see cref="SubInputStream"/> that represents the data part of the segment.</returns>
        public SubInputStream GetDataInputStream()
        {
            return new SubInputStream(subInputStream, SegmentDataStartOffset, SegmentDataLength);
        }

        /// <summary>
        /// Retrieves the segments' data part.
        /// </summary>
        public ISegmentData GetSegmentData()
        {
            ISegmentData segmentDataPart = null;

            segmentData?.TryGetTarget(out segmentDataPart);

            if (segmentDataPart is null)
            {
                try
                {
                    segmentDataPart = CreateSegmentData(SegmentType);
                    segmentDataPart.Init(this, GetDataInputStream());

                    segmentData = new WeakReference<ISegmentData>(segmentDataPart);
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException("Can't instantiate segment class.", e);
                }
            }

            return segmentDataPart;
        }

        public void CleanSegmentData()
        {
            segmentData = null;
        }

        public override string ToString()
        {
            StringBuilder stringBuilder = new StringBuilder();

            if (RtSegments != null)
            {
                foreach (SegmentHeader s in RtSegments)
                {
                    stringBuilder.Append(s.SegmentNumber + " ");
                }
            }
            else
            {
                stringBuilder.Append("none");
            }

            return "\n#SegmentNr: " + SegmentNumber //
                    + "\n SegmentType: " + SegmentType //
                    + "\n PageAssociation: " + PageAssociation //
                    + "\n Referred-to segments: " + stringBuilder.ToString() //
                    + "\n"; //
        }
    }
}

