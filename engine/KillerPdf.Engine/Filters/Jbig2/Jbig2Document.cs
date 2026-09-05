// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.

#nullable disable


namespace KillerPdf.Engine.Filters.Jbig2
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    /// <summary>
    /// This class represents the document structure with its pages and global segments.
    /// </summary>
    internal sealed class Jbig2Document : IDisposable
    {
        // ID string in file header, see ISO/IEC 14492:2001, D.4.1
        private static readonly int[] FILE_HEADER_ID = [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];

        // This map contains all pages of this document. The key is the number of the page.
        private readonly Dictionary<int, Jbig2Page> pages = new Dictionary<int, Jbig2Page>();

        // The length of the file header if exists
        private short fileHeaderLength = 9;

        /// <summary>
        /// According to D.4.2 - File header bit 0
        /// This flag contains information about the file organization:
        /// 1: for sequential
        /// 0: for random access
        /// You can use the constants <see cref="RANDOM"/> and <see cref="SEQUENTIAL"/>.
        /// </summary>
        private short organisationType = (short)SEQUENTIAL;

        public static readonly int RANDOM = 0;
        public static readonly int SEQUENTIAL = 1;

        /// <summary>
        /// According to D.4.2 - Bit 1
        /// true: if amount of pages is unknown, amount of pages field is not present.
        /// false: if there is a field in the file header where the amount of pages can be read
        /// </summary>
        public bool IsNumberOfPageUnknown { get; private set; } = true;

        /// <summary>
        /// According to D.4.3 - Number of pages field (4 bytes).
        /// Only present if <see cref="IsNumberOfPageUnknown"/> is false.
        /// </summary>
        public int NumberOfPages { get; private set; }

        /// <summary>
        /// Defines whether extended Template is used.
        /// </summary>
        public bool IsGbUseExtTemplate { get; private set; }

        // This is the source data stream wrapped into a SubInputStream.
        private readonly SubInputStream subInputStream;

        /// <summary>
        /// Holds a load of segments, that aren't associated with a page.
        /// </summary>
        public Jbig2Globals GlobalSegments { get; private set; }

        public Jbig2Document(IImageInputStream input)
            : this(input, null)
        {
        }

        public Jbig2Document(IImageInputStream input, Jbig2Globals globals)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input), " must not be null");
            }

            subInputStream = new SubInputStream(input, 0, long.MaxValue);
            GlobalSegments = globals;

            MapStream();
        }

        /// <summary>
        /// Retrieves the segment with the given segment number considering only segments
        /// that aren't associated with a page.
        /// </summary>
        /// <param name="segmentNumber">The number of the requested segment.</param>
        /// <returns>The requested <see cref="SegmentHeader"/>.</returns>
        internal SegmentHeader GetGlobalSegment(int segmentNumber)
        {
            return GlobalSegments?.GetSegment(segmentNumber);
        }

        /// <summary>
        /// Retrieves a <see cref="Jbig2Page"/> specified by the given page number.
        /// </summary>
        /// <param name="pageNumber">The page number of the requested <see cref="Jbig2Page"/>.</param>
        /// <returns>The requested <see cref="Jbig2Page"/>.</returns>
        public Jbig2Page GetPage(int pageNumber)
        {
            return pages.ContainsKey(pageNumber) ? pages[pageNumber] : null;
        }

        /// <summary>
        /// Disposes the supplied <see cref="IImageInputStream"/>.
        /// </summary>
        public void Dispose()
        {
            subInputStream.Dispose();
        }

        /// <summary>
        /// Retrieves the amount of pages in this JBIG2 document. If the pages are striped,
        /// the document will be completely parsed and the amount of pages will be gathered.
        /// </summary>
        /// <returns>The amount of pages in this JBIG2 document.</returns>
        internal int GetAmountOfPages()
        {
            if (!IsNumberOfPageUnknown && NumberOfPages != 0)
            {
                return NumberOfPages;
            }

            if (pages.Count == 0)
            {
                MapStream();
            }

            return pages.Count;
        }

        /// <summary>
        /// This method maps the stream and stores all segments.
        /// </summary>
        private void MapStream()
        {
            var segments = new List<SegmentHeader>();

            long offset = 0;
            int segmentType = 0;

            // Parse the file header if there is one.
            if (IsFileHeaderPresent())
            {
                ParseFileHeader();
                offset += fileHeaderLength;
            }

            GlobalSegments ??= new Jbig2Globals();

            // If organisation type is random-access: walk through the segment headers until EOF segment
            // appears (specified with segment number 51)
            while (segmentType != 51 && !ReachedEndOfStream(offset))
            {
                var segment = new SegmentHeader(this, subInputStream, offset,
                        organisationType);

                int associatedPage = segment.PageAssociation;
                segmentType = segment.SegmentType;

                if (associatedPage != 0)
                {
                    Jbig2Page page = GetPage(associatedPage);
                    if (page is null)
                    {
                        page = new Jbig2Page(this, associatedPage);
                        pages[associatedPage] = page;
                    }
                    page.Add(segment);
                }
                else
                {
                    GlobalSegments.AddSegment(segment.SegmentNumber, segment);
                }
                segments.Add(segment);

                offset = subInputStream.Position;

                // Sequential organization skips data part and sets the offset
                if (organisationType == SEQUENTIAL)
                {
                    offset += segment.SegmentDataLength;
                }
            }

            // Random organization: segment headers are finished. Data part starts and the offset can be set.
            DetermineRandomDataOffsets(segments, offset);
        }

        private bool IsFileHeaderPresent()
        {
            subInputStream.Mark();

            foreach (int magicByte in FILE_HEADER_ID)
            {
                if (magicByte != subInputStream.Read())
                {
                    subInputStream.Reset();
                    return false;
                }
            }

            subInputStream.Reset();
            return true;
        }

        /// <summary>
        /// Determines the start of the data parts and sets the offset.
        /// </summary>
        private void DetermineRandomDataOffsets(List<SegmentHeader> segments, long offset)
        {
            if (organisationType != RANDOM)
            {
                return;
            }

            foreach (SegmentHeader s in segments)
            {
                s.SegmentDataStartOffset = offset;
                offset += s.SegmentDataLength;
            }
        }

        /// <summary>
        /// This method reads the stream and sets variables for information about organization type and length etc.
        /// </summary>
        private void ParseFileHeader()
        {
            subInputStream.Seek(0);

            // D.4.1 - ID string, read will be skipped
            subInputStream.SkipBytes(8);

            // D.4.2 Header flag (1 byte):

            // Bit 3-7 are reserved and must be 0
            subInputStream.ReadBits(5);

            // Bit 2 - Indicates if extended templates are used
            if (subInputStream.ReadBit() == 1)
            {
                IsGbUseExtTemplate = true;
            }

            // Bit 1 - Indicates if amount of pages are unknown
            if (subInputStream.ReadBit() != 1)
            {
                IsNumberOfPageUnknown = false;
            }

            // Bit 0 - Indicates file organisation type
            organisationType = (short)subInputStream.ReadBit();

            // D.4.3 Number of pages (field is only present if amount of pages are 'NOT unknown')
            if (!IsNumberOfPageUnknown)
            {
                NumberOfPages = (int)subInputStream.ReadUnsignedInt();
                fileHeaderLength = 13;
            }
        }

        /// <summary>
        /// This method checks, if the stream is at its end to avoid
        /// <see cref="EndOfStreamException"/>s and reads 32 bits.
        /// </summary>
        /// <returns>true, if end of stream reached. false, if there are more bytes to read</returns>
        private bool ReachedEndOfStream(long offset)
        {
            try
            {
                subInputStream.Seek(offset);
                subInputStream.ReadBits(32);
                return false;
            }
            catch (EndOfStreamException)
            {
                return true;
            }
        }
    }
}

