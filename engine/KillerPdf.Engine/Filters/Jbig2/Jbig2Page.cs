// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.

#nullable disable


namespace KillerPdf.Engine.Filters.Jbig2
{
    using System.Collections.Generic;

    /// <summary>
    /// This class represents a JBIG2 page.
    /// </summary>
    internal sealed class Jbig2Page
    {
        // This list contains all segments of this page, sorted by segment number in ascending order.
        private readonly SortedDictionary<int, SegmentHeader> segments = new SortedDictionary<int, SegmentHeader>();

        // NOTE: page number != segmentList index
        private readonly int pageNumber;

        // The page bitmap that represents the page buffer
        private Jbig2Bitmap pageBitmap;

        private int finalHeight;
        private int finalWidth;
        private int resolutionX;
        private int resolutionY;

        private readonly Jbig2Document document;

        internal Jbig2Page(Jbig2Document document, int pageNumber)
        {
            this.document = document;
            this.pageNumber = pageNumber;
        }

        /// <summary>
        /// This method searches for a segment specified by its number.
        /// </summary>
        /// <param name="number">Segment number of the segment to search for.</param>
        /// <returns>The retrieved <see cref="SegmentHeader"/> or null.</returns>
        internal SegmentHeader GetSegment(int number)
        {
            SegmentHeader s = segments.ContainsKey(number) ? segments[number] : null;

            return s ?? document?.GetGlobalSegment(number);
        }

        /// <summary>
        /// Returns the associated page information segment.
        /// </summary>
        /// <returns>The associated <see cref="PageInformation"/> segment or null if not available.</returns>
        internal SegmentHeader GetPageInformationSegment()
        {
            foreach (SegmentHeader s in segments.Values)
            {
                if (s.SegmentType == 48)
                {
                    return s;
                }
            }
            return null;
        }

        /// <summary>
        /// Returns the decoded bitmap if present. Otherwise the page bitmap will be composed before returning
        /// the result.
        /// </summary>
        /// <returns>The result of decoding a page</returns>
        /// <exception cref="Jbig2Exception"/>
        /// <exception cref="IOException"/>
        public Jbig2Bitmap GetBitmap()
        {
            if (pageBitmap is null)
            {
                ComposePageBitmap();
            }
            return pageBitmap;
        }

        /// <summary>
        /// This method composes the bitmaps of segments to a page and stores the page as a <see cref="Jbig2Bitmap"/>.
        /// </summary>
        /// <exception cref="Jbig2Exception"/>
        /// <exception cref="IOException"/>
        private void ComposePageBitmap()
        {
            if (pageNumber > 0)
            {
                // Page 79, 1) Decoding the page information segment
                PageInformation pageInformation = (PageInformation)GetPageInformationSegment()
                        .GetSegmentData();
                CreatePage(pageInformation);
                ClearSegmentData();
            }
        }

        private void CreatePage(PageInformation pageInformation)
        {
            if (!pageInformation.IsStriped || pageInformation.BitmapHeight != -1)
            {
                // Page 79, 4)
                CreateNormalPage(pageInformation);
            }
            else
            {
                CreateStripedPage(pageInformation);
            }
        }

        private void CreateNormalPage(PageInformation pageInformation)
        {
            pageBitmap = new Jbig2Bitmap(pageInformation.BitmapWidth, pageInformation.BitmapHeight);

            // Page 79, 3)
            // If default pixel value is not 0, byte will be filled with 0xff
            if (pageInformation.DefaultPixelValue != 0)
            {
                pageBitmap.ByteArray.AsSpan().Fill(0xff);
            }

            foreach (SegmentHeader s in segments.Values)
            {
                // Page 79, 5)
                switch (s.SegmentType)
                {
                    case 6: // Immediate text region
                    case 7: // Immediate lossless text region
                    case 22: // Immediate halftone region
                    case 23: // Immediate lossless halftone region
                    case 38: // Immediate generic region
                    case 39: // Immediate lossless generic region
                    case 42: // Immediate generic refinement region
                    case 43: // Immediate lossless generic refinement region
                        IRegion r = (IRegion)s.GetSegmentData();

                        if (r is GenericRefinementRegion refinementRegion)
                        {
                            refinementRegion.SetPageBitmap(pageBitmap);
                        }

                        Jbig2Bitmap regionBitmap = r.GetRegionBitmap();

                        if (FitsPage(pageInformation, regionBitmap))
                        {
                            pageBitmap = regionBitmap;
                        }
                        else
                        {
                            RegionSegmentInformation regionInfo = r.RegionInfo;
                            CombinationOperator op = GetCombinationOperator(pageInformation,
                                    regionInfo.CombinationOperator);
                            Jbig2Bitmaps.Blit(regionBitmap, pageBitmap, regionInfo.X,
                                    regionInfo.Y, op);
                        }

                        break;
                }
            }
        }

        /// <summary>
        /// Check if we have only one region that forms the complete page. If the dimension equals the page's dimension set
        /// the region's bitmap as the page's bitmap. Otherwise we have to blit the smaller region's bitmap into the page's
        /// bitmap.
        /// </summary>
        private bool FitsPage(PageInformation pageInformation, Jbig2Bitmap regionBitmap)
        {
            return CountRegions() == 1 && pageInformation.DefaultPixelValue == 0
                    && pageInformation.BitmapWidth == regionBitmap.Width
                    && pageInformation.BitmapHeight == regionBitmap.Height;
        }

        private void CreateStripedPage(PageInformation pageInformation)
        {
            List<ISegmentData> pageStripes = CollectPageStripes();

            pageBitmap = new Jbig2Bitmap(pageInformation.BitmapWidth, finalHeight);

            int startLine = 0;
            foreach (ISegmentData sd in pageStripes)
            {
                if (sd is EndOfStripe oes)
                {
                    startLine = oes.GetLineNumber() + 1;
                }
                else
                {
                    IRegion r = (IRegion)sd;
                    RegionSegmentInformation regionInfo = r.RegionInfo;
                    CombinationOperator op = GetCombinationOperator(pageInformation, regionInfo.CombinationOperator);
                    Jbig2Bitmaps.Blit(r.GetRegionBitmap(), pageBitmap, regionInfo.X, startLine, op);
                }
            }
        }

        private List<ISegmentData> CollectPageStripes()
        {
            List<ISegmentData> pageStripes = new List<ISegmentData>();
            foreach (SegmentHeader s in segments.Values)
            {
                // Page 79, 5)
                switch (s.SegmentType)
                {
                    case 6: // Immediate text region
                    case 7: // Immediate lossless text region
                    case 22: // Immediate halftone region
                    case 23: // Immediate lossless halftone region
                    case 38: // Immediate generic region
                    case 39: // Immediate lossless generic region
                    case 42: // Immediate generic refinement region
                    case 43: // Immediate lossless generic refinement region
                        IRegion r = (IRegion)s.GetSegmentData();
                        pageStripes.Add(r);
                        break;

                    case 50: // End of stripe
                        EndOfStripe eos = (EndOfStripe)s.GetSegmentData();
                        pageStripes.Add(eos);
                        finalHeight = eos.GetLineNumber() + 1;
                        break;
                }
            }

            return pageStripes;
        }

        /// <summary>
        /// This method counts the regions segments. If there is only one region, the bitmap
        /// of this segment is equal to the page bitmap and blitting is not necessary.
        /// </summary>
        /// <returns>Number of regions.</returns>
        private int CountRegions()
        {
            int regionCount = 0;

            foreach (SegmentHeader s in segments.Values)
            {
                switch (s.SegmentType)
                {
                    case 6: // Immediate text region
                    case 7: // Immediate lossless text region
                    case 22: // Immediate halftone region
                    case 23: // Immediate lossless halftone region
                    case 38: // Immediate generic region
                    case 39: // Immediate lossless generic region
                    case 42: // Immediate generic refinement region
                    case 43: // Immediate lossless generic refinement region
                        regionCount++;
                        break;
                }
            }

            return regionCount;
        }

        /// <summary>
        /// This method checks and sets, which combination operator shall be used.
        /// </summary>
        /// <param name="pi"><see cref="PageInformation"/> object.</param>
        /// <param name="newOperator">The combination operator, specified by actual segment.</param>
        /// <returns>the new combination operator.</returns>
        private static CombinationOperator GetCombinationOperator(PageInformation pi, CombinationOperator newOperator)
        {
            if (pi.IsCombinationOperatorOverrideAllowed)
            {
                return newOperator;
            }

            return pi.CombinationOperator;
        }

        /// <summary>
        /// Adds a <see cref="SegmentHeader"/> into the page's segments map.
        /// </summary>
        /// <param name="segment">The segment to be added.</param>
        internal void Add(SegmentHeader segment)
        {
            segments[segment.SegmentNumber] = segment;
        }

        /// <summary>
        /// Resets the memory-critical segments to force on-demand-decoding and to avoid
        /// holding the segments' bitmap too long.
        /// </summary>
        private void ClearSegmentData()
        {
            var keySet = segments.Keys;

            foreach (int key in keySet)
            {
                segments[key].CleanSegmentData();
            }
        }

        /// <summary>
        /// Reset memory-critical parts of page.
        /// </summary>
        internal void ClearPageData()
        {
            pageBitmap = null;
        }

        internal int GetHeight()
        {
            if (finalHeight == 0)
            {
                PageInformation pi = GetPageInformation();
                if (pi.BitmapHeight == -1)
                {
                    GetBitmap();
                }
                else
                {
                    finalHeight = pi.BitmapHeight;
                }
            }

            return finalHeight;
        }

        internal int GetWidth()
        {
            if (finalWidth == 0)
            {
                finalWidth = GetPageInformation().BitmapWidth;
            }

            return finalWidth;
        }

        internal int GetResolutionX()
        {
            if (resolutionX == 0)
            {
                resolutionX = GetPageInformation().ResolutionX;
            }

            return resolutionX;
        }

        internal int GetResolutionY()
        {
            if (resolutionY == 0)
            {
                resolutionY = GetPageInformation().ResolutionY;
            }

            return resolutionY;
        }

        private PageInformation GetPageInformation()
        {
            return (PageInformation)GetPageInformationSegment().GetSegmentData();
        }

        public override string ToString()
        {
            return GetType().Name + " (Page number: " + pageNumber + ")";
        }
    }
}
