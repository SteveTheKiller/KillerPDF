/*
*
* COPYRIGHT:
* 
* This software module was originally developed by Rapha�l Grosbois and
* Diego Santa Cruz (Swiss Federal Institute of Technology-EPFL); Joel
* Askel�f (Ericsson Radio Systems AB); and Bertrand Berthelot, David
* Bouchard, F�lix Henry, Gerard Mozelle and Patrice Onno (Canon Research
* Centre France S.A) in the course of development of the JPEG2000
* standard as specified by ISO/IEC 15444 (JPEG 2000 Standard). This
* software module is an implementation of a part of the JPEG 2000
* Standard. Swiss Federal Institute of Technology-EPFL, Ericsson Radio
* Systems AB and Canon Research Centre France S.A (collectively JJ2000
* Partners) agree not to assert against ISO/IEC and users of the JPEG
* 2000 Standard (Users) any of their rights under the copyright, not
* including other intellectual property rights, for this software module
* with respect to the usage by ISO/IEC and Users of this software module
* or modifications thereof for use in hardware or software products
* claiming conformance to the JPEG 2000 Standard. Those intending to use
* this software module in hardware or software products are advised that
* their use may infringe existing patents. The original developers of
* this software module, JJ2000 Partners and ISO/IEC assume no liability
* for use of this software module or modifications thereof. No license
* or right to this software module is granted for non JPEG 2000 Standard
* conforming products. JJ2000 Partners have full right to use this
* software module for his/her own purpose, assign or donate this
* software module to any third party and to inhibit third parties from
* using this software module for non JPEG 2000 Standard conforming
* products. This copyright notice must be included in all copies or
* derivative works of this software module.
* 
* Copyright (c) 1999/2000 JJ2000 Partners.
* */
using CoreJ2K.Util;
using CoreJ2K.j2k.encoder;
using CoreJ2K.j2k.image;
using CoreJ2K.j2k.image.input;
using CoreJ2K.j2k.quantization.quantizer;
using CoreJ2K.j2k.util;
using CoreJ2K.j2k.wavelet;
using CoreJ2K.j2k.wavelet.analysis;
using System;
using System.Collections.Generic;

namespace CoreJ2K.j2k.roi.encoder
{

    /// <summary> This class deals with the ROI functionality.
    /// 
    /// The ROI method is the Maxshift method. The ROIScaler works by scaling
    /// the quantized wavelet coefficients that do not affect the ROI (i.e
    /// background coefficients) so that these samples get a lower significance
    /// than the ROI ones. By scaling the coefficients sufficiently, the ROI
    /// coefficients can be recognized by their amplitude alone and no ROI mask
    /// needs to be generated at the decoder side.
    /// 
    /// The source module must be a quantizer and code-block's data is exchange
    /// with thanks to CBlkWTData instances.
    /// 
    /// </summary>
    /// <seealso cref="Quantizer" />
    /// <seealso cref="CBlkWTData" />
    public class ROIScaler : ImgDataAdapter, CBlkQuantDataSrcEnc
    {
        /// <summary> Returns the horizontal offset of the code-block partition. Allowable
        /// values are 0 and 1, nothing else.
        /// 
        /// </summary>
        public virtual int CbULX => src.CbULX;

        /// <summary> Returns the vertical offset of the code-block partition. Allowable
        /// values are 0 and 1, nothing else.
        /// 
        /// </summary>
        public virtual int CbULY => src.CbULY;

        /// <summary> This function returns the ROI mask generator.
        /// 
        /// </summary>
        /// <returns> The roi mask generator
        /// </returns>
        public virtual ROIMaskGenerator ROIMaskGenerator => mg;

        /// <summary> This function returns the blockAligned flag
        /// 
        /// </summary>
        /// <returns> Flag indicating whether the ROIs were block aligned
        /// </returns>
        public virtual bool BlockAligned => blockAligned;

        /// <summary> Returns the parameters that are used in this class and
        /// implementing classes. It returns a 2D String array. Each of the
        /// 1D arrays is for a different option, and they have 3
        /// elements. The first element is the option name, the second one
        /// is the synopsis, the third one is a long description of what
        /// the parameter is and the fourth is its default value. The
        /// synopsis or description may be 'null', in which case it is
        /// assumed that there is no synopsis or description of the option,
        /// respectively. Null may be returned if no options are supported.
        /// 
        /// </summary>
        /// <returns> the options name, their synopsis and their explanation, 
        /// or null if no options are supported.
        /// 
        /// </returns>
        public static string[][] ParameterInfo => pinfo;

        /// <summary>The prefix for ROI Scaler options: 'R' </summary>
        public const char OPT_PREFIX = 'R';

        /// <summary>The list of parameters that are accepted for ROI coding. Options 
        /// for ROI Scaler start with 'R'. 
        /// </summary>
        private static readonly string[][] pinfo = { new string[] { "Rroi", "[<component idx>] R <left> <top> <width> <height> or [<component idx>] C <centre column> <centre row> <radius> or [<component idx>] A <filename>", "Specifies ROIs shape and location. The shape can be either rectangular 'R', or circular 'C' or arbitrary 'A'. Each new occurrence of an 'R', a 'C' or an 'A' is a new ROI. For circular and rectangular ROIs, all values are given as their pixel values relative to the canvas origin. Arbitrary shapes must be included in a PGM file where non 0 values correspond to ROI coefficients. The PGM file must have the size as the image. The component idx specifies which components contain the ROI. The component index is specified as described by points 3 and 4 in the general comment on tile-component idx. If this option is used, the codestream is layer progressive by default unless it is overridden by the 'Aptype' option.", null }, new string[] { "Ralign", "[on|off]", "By specifying this argument, the ROI mask will be limited to covering only entire code-blocks. The ROI coding can then be performed without any actual scaling of the coefficients but by instead scaling the distortion estimates.", "off" }, new string[] { "Rstart_level", "<level>", "This argument forces the lowest <level> resolution levels to " + "belong to the ROI. By doing this, it is possible to avoid only " + "getting information for the ROI at an early stage of " + "transmission.<level> = 0 means the lowest resolution level " + "belongs to the ROI, 1 means the two lowest etc. (-1 deactivates" + " the option)", "-1" }, new string[] { "Rno_rect", "[on|off]", "This argument makes sure that the ROI mask generation is not done " + "using the fast ROI mask generation for rectangular ROIs " + "regardless of whether the specified ROIs are rectangular or not", "off" } };

        /// <summary>The maximum number of magnitude bit-planes in any subband. One value
        /// for each tile-component 
        /// </summary>
        private int[][] maxMagBits;

        /// <summary>Flag indicating the presence of ROIs </summary>
        private readonly bool roi;

        /// <summary>Flag indicating if block aligned ROIs are used </summary>
        private readonly bool blockAligned;

        /// <summary>Number of resolution levels to include in ROI mask </summary>
        private readonly int useStartLevel;

        /// <summary>The class generating the ROI mask </summary>
        private readonly ROIMaskGenerator mg;

        /// <summary>The ROI mask </summary>
        private readonly DataBlkInt roiMask;

        /// <summary>The source of quantized wavelet transform coefficients </summary>
        private readonly Quantizer src;

        /// <summary> Constructor of the ROI scaler, takes a Quantizer as source of data to
        /// scale.
        /// 
        /// </summary>
        /// <param name="src">The quantizer that is the source of data.
        /// 
        /// </param>
        /// <param name="mg">The mask generator that will be used for all components
        /// 
        /// </param>
        /// <param name="roi">Flag indicating whether there are rois specified.
        /// 
        /// </param>
        /// <param name="sLev">The resolution levels that belong entirely to ROI
        /// 
        /// </param>
        /// <param name="uba">Flag indicating whether block aligning is used.
        /// 
        /// </param>
        /// <param name="encSpec">The encoder specifications for addition of roi specs
        /// 
        /// </param>
        public ROIScaler(Quantizer src, ROIMaskGenerator mg, bool roi, int sLev, bool uba, EncoderSpecs encSpec) : base(src)
        {
            this.src = src;
            this.roi = roi;
            useStartLevel = sLev;
            if (roi)
            {
                // If there is no ROI, no need to do this
                this.mg = mg;
                roiMask = new DataBlkInt();
                calcMaxMagBits(encSpec);
                blockAligned = uba;
            }
        }

        /// <summary> Since ROI scaling is always a reversible operation, it calls
        /// IsReversible() method of it source (the quantizer module).
        /// 
        /// </summary>
        /// <param name="t">The tile to test for reversibility
        /// 
        /// </param>
        /// <param name="c">The component to test for reversibility
        /// 
        /// </param>
        /// <returns> True if the quantized data is reversible, false if not.
        /// 
        /// </returns>
        public virtual bool IsReversible(int t, int c)
        {
            return src.IsReversible(t, c);
        }

        /// <summary> Returns a reference to the subband tree structure representing the
        /// subband decomposition for the specified tile-component.
        /// 
        /// </summary>
        /// <param name="t">The index of the tile.
        /// 
        /// </param>
        /// <param name="c">The index of the component.
        /// 
        /// </param>
        /// <returns> The subband tree structure, see SubbandAn.
        /// 
        /// </returns>
        /// <seealso cref="SubbandAn" />
        /// <seealso cref="Subband" />
        public virtual SubbandAn GetAnSubbandTree(int t, int c)
        {
            return src.GetAnSubbandTree(t, c);
        }

        /// <summary> Creates a ROIScaler object. The Quantizer is the source of data to
        /// scale.
        /// 
        /// The ROI Scaler creates a ROIMaskGenerator depending on what ROI
        /// information is in the ParameterList. If only rectangular ROI are used,
        /// the fast mask generator for rectangular ROI can be used.
        /// 
        /// </summary>
        /// <param name="src">The source of data to scale
        /// 
        /// </param>
        /// <param name="pl">The parameter list (or options).
        /// 
        /// </param>
        /// <param name="encSpec">The encoder specifications for addition of roi specs
        /// 
        /// </param>
        /// <exception cref="IllegalArgumentException">If an error occurs while parsing
        /// the options in 'pl'
        /// 
        /// </exception>
        public static ROIScaler createInstance(Quantizer src, ParameterList pl, EncoderSpecs encSpec)
        {
            var roiVector = new List<ROI>(10);
            ROIMaskGenerator maskGen = null;

            // Check parameters
            pl.checkList(OPT_PREFIX, ParameterList.toNameArray(pinfo));

            // Get parameters and check if there are and ROIs specified 
            var roiopt = pl.GetParameter("Rroi");
            if (roiopt == null)
            {
                // No ROIs specified! Create ROIScaler with no mask generator
                return new ROIScaler(src, null, false, -1, false, encSpec);
            }

            // Check if the lowest resolution levels should belong to the ROI 
            var sLev = pl.GetIntParameter("Rstart_level");

            // Check if the ROIs are block-aligned
            var useBlockAligned = pl.GetBooleanParameter("Ralign");

            // Check if generic mask generation is specified 
            var onlyRect = !pl.GetBooleanParameter("Rno_rect");

            // Parse the ROIs
            parseROIs(roiopt, src.NumComps, roiVector);
            var roiArray = new ROI[roiVector.Count];
            roiVector.CopyTo(roiArray);

            // If onlyRect has been forced, check if there are any non-rectangular
            // ROIs specified.  Currently, only the presence of circular ROIs will
            // make this false
            if (onlyRect)
            {
                for (var i = roiArray.Length - 1; i >= 0; i--)
                    if (!roiArray[i].rect)
                    {
                        onlyRect = false;
                        break;
                    }
            }

            if (onlyRect)
            {
                // It's possible to use the fast ROI mask generation when only
                // rectangular ROIs are specified.
                maskGen = new RectROIMaskGenerator(roiArray, src.NumComps);
            }
            else
            {
                // It's necessary to use the generic mask generation
                maskGen = new ArbROIMaskGenerator(roiArray, src.NumComps, src);
            }
            return new ROIScaler(src, maskGen, true, sLev, useBlockAligned, encSpec);
        }

        /// <summary> This function parses the values given for the ROIs with the argument
        /// -Rroi. Currently only circular and rectangular ROIs are supported.
        /// 
        /// A rectangular ROI is indicated by a 'R' followed the coordinates for
        /// the upper left corner of the ROI and then its width and height.
        /// 
        /// A circular ROI is indicated by a 'C' followed by the coordinates of
        /// the circle center and then the radius.
        /// 
        /// Before the R and C values, the component that are affected by the
        /// ROI are indicated.
        /// 
        /// </summary>
        /// <param name="roiopt">The info on the ROIs
        /// 
        /// </param>
        /// <param name="nc">number of components
        /// 
        /// </param>
        /// <param name="roiVector">The vcector containing the ROI parsed from the cmd line
        /// 
        /// </param>
        /// <returns> The ROIs specified in roiopt
        /// 
        /// </returns>
        protected internal static List<ROI> parseROIs(string roiopt, int nc, List<ROI> roiVector)
        {
            //ROI[] ROIs;
            ROI roi;
            Tokenizer stok;
            //char tok;
            var nrOfROIs = 0;
            //char c;
            int ulx, uly, w, h, x, y, rad; // comp removed
            bool[] roiInComp = null;

            stok = new Tokenizer(roiopt);

            string word;
            while (stok.HasMoreTokens())
            {
                word = stok.NextToken();

                switch (word[0])
                {

                    case 'c':  // Components specification
                        roiInComp = ModuleSpec.parseIdx(word, nc);
                        break;

                    case 'R':  // Rectangular ROI to be read
                        nrOfROIs++;
                        try
                        {
                            word = stok.NextToken();
                            ulx = (int.Parse(word));
                            word = stok.NextToken();
                            uly = (int.Parse(word));
                            word = stok.NextToken();
                            w = (int.Parse(word));
                            word = stok.NextToken();
                            h = (int.Parse(word));
                        }
                        catch (FormatException)
                        {
                            throw new ArgumentException($"Bad parameter for '-Rroi R' option : {word}");
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            throw new ArgumentException("Wrong number of " + "parameters for  " + "h'-Rroi R' option.");
                        }

                        // If the ROI is component-specific, check which comps.
                        if (roiInComp != null)
                            for (var i = 0; i < nc; i++)
                            {
                                if (roiInComp[i])
                                {
                                    roi = new ROI(i, ulx, uly, w, h);
                                    roiVector.Add(roi);
                                }
                            }
                        else
                        {
                            // Otherwise add ROI for all components
                            for (var i = 0; i < nc; i++)
                            {
                                roi = new ROI(i, ulx, uly, w, h);
                                roiVector.Add(roi);
                            }
                        }
                        break;

                    case 'C':  // Circular ROI to be read
                        nrOfROIs++;

                        try
                        {
                            word = stok.NextToken();
                            x = (int.Parse(word));
                            word = stok.NextToken();
                            y = (int.Parse(word));
                            word = stok.NextToken();
                            rad = (int.Parse(word));
                        }
                        catch (FormatException)
                        {
                            throw new ArgumentException($"Bad parameter for '-Rroi C' option : {word}");
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            throw new ArgumentException("Wrong number of " + "parameters for " + "'-Rroi C' option.");
                        }

                        // If the ROI is component-specific, check which comps.
                        if (roiInComp != null)
                            for (var i = 0; i < nc; i++)
                            {
                                if (roiInComp[i])
                                {
                                    roi = new ROI(i, x, y, rad);
                                    roiVector.Add(roi);
                                }
                            }
                        else
                        {
                            // Otherwise add ROI for all components
                            for (var i = 0; i < nc; i++)
                            {
                                roi = new ROI(i, x, y, rad);
                                roiVector.Add(roi);
                            }
                        }
                        break;

                    case 'A':  // ROI wth arbitrary shape
                        nrOfROIs++;

                        string filename;
                        ImgReaderPGM maskPGM = null;

                        try
                        {
                            filename = stok.NextToken();
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            throw new ArgumentException("Wrong number of " + "parameters for " + "'-Rroi A' option.");
                        }
                        try
                        {
                            maskPGM = new ImgReaderPGM(filename);
                        }
                        catch (System.IO.IOException)
                        {
                            throw new InvalidOperationException("Cannot read PGM file with ROI");
                        }

                        // If the ROI is component-specific, check which comps.
                        if (roiInComp != null)
                            for (var i = 0; i < nc; i++)
                            {
                                if (roiInComp[i])
                                {
                                    roi = new ROI(i, maskPGM);
                                    roiVector.Add(roi);
                                }
                            }
                        else
                        {
                            // Otherwise add ROI for all components
                            for (var i = 0; i < nc; i++)
                            {
                                roi = new ROI(i, maskPGM);
                                roiVector.Add(roi);
                            }
                        }
                        break;

                    default:
                        throw new InvalidOperationException($"Bad parameters for ROI nr {roiVector.Count}");

                }
            }

            return roiVector;
        }

        /// <summary> This function gets a datablk from the entropy coder. The sample sin the
        /// block, which consists of  the quantized coefficients from the quantizer,
        /// are scaled by the values given for any ROIs specified.
        /// 
        /// The function calls on a ROIMaskGenerator to get the mask for scaling
        /// the coefficients in the current block.
        /// 
        /// The data returned by this method is a copy of the orignal
        /// data. Therfore it can be modified "in place" without any problems after
        /// being returned. The 'offset' of the returned data is 0, and the 'scanw'
        /// is the same as the code-block width. See the 'CBlkWTData' class.
        /// 
        /// </summary>
        /// <param name="c">The component for which to return the next code-block.
        /// 
        /// </param>
        /// <param name="cblk">If non-null this object will be used to return the new
        /// code-block. If null a new one will be allocated and returned. If the
        /// "data" array of the object is non-null it will be reused, if possible,
        /// to return the data.
        /// 
        /// </param>
        /// <returns> The next code-block in the current tile for component 'n', or
        /// null if all code-blocks for the current tile have been returned.
        /// 
        /// </returns>
        /// <seealso cref="CBlkWTData" />
        public virtual CBlkWTData? GetNextCodeBlock(int c, CBlkWTData? cblk)
        {
            return GetNextInternCodeBlock(c, cblk);
        }

        /// <summary> This function gets a datablk from the entropy coder. The sample sin the
        /// block, which consists of  the quantized coefficients from the quantizer,
        /// are scaled by the values given for any ROIs specified.
        /// 
        /// The function calls on a ROIMaskGenerator to get the mask for scaling
        /// the coefficients in the current block.
        /// 
        /// </summary>
        /// <param name="c">The component for which to return the next code-block.
        /// 
        /// </param>
        /// <param name="cblk">If non-null this object will be used to return the new
        /// code-block. If null a new one will be allocated and returned. If the
        /// "data" array of the object is non-null it will be reused, if possible,
        /// to return the data.
        /// 
        /// </param>
        /// <returns> The next code-block in the current tile for component 'n', or
        /// null if all code-blocks for the current tile have been returned.
        /// 
        /// </returns>
        /// <seealso cref="CBlkWTData" />
        public virtual CBlkWTData? GetNextInternCodeBlock(int c, CBlkWTData? cblk)
        {
            int mi, i, j, k, wrap;
            int ulx, uly, w, h;
            var mask = roiMask; // local copy of mask
            int[] maskData; // local copy of mask data
            int[] data; // local copy of quantized data
            int tmp;
            var bitMask = 0x7FFFFFFF;
            SubbandAn root, sb;
            var maxBits = 0; // local copy
            bool roiInTile;
            bool sbInMask;
            var nROIcoeff = 0;

            // Get codeblock's data from quantizer
            cblk = src.GetNextCodeBlock(c, cblk);

            // If there is no ROI in the image, or if we already got all
            // code-blocks
            if (!roi || cblk == null)
            {
                return cblk;
            }

            data = (int[])cblk.Data;
            sb = cblk.sb;
            ulx = cblk.ulx;
            uly = cblk.uly;
            w = cblk.w;
            h = cblk.h;
            sbInMask = (sb.resLvl <= useStartLevel);

            // Check that there is an array for the mask and set it to zero
            maskData = mask.DataInt; // local copy of mask data
            if (maskData == null || w * h > maskData.Length)
            {
                maskData = new int[w * h];
                mask.DataInt = maskData;
            }
            else
            {
                for (i = w * h - 1; i >= 0; i--)
                    maskData[i] = 0;
            }
            mask.ulx = ulx;
            mask.uly = uly;
            mask.w = w;
            mask.h = h;

            // Get ROI mask from generator
            root = src.GetAnSubbandTree(tIdx, c);
            maxBits = maxMagBits[tIdx][c];
            roiInTile = mg.GetROIMask(mask, root, maxBits, c);

            // If there is no ROI in this tile, return the code-block untouched
            if (!roiInTile && (!sbInMask))
            {
                cblk.nROIbp = 0;
                return cblk;
            }

            // Update field containing the number of ROI magnitude bit-planes
            cblk.nROIbp = cblk.magbits;

            // If the entire subband belongs to the ROI mask, The code-block is
            // set to belong entirely to the ROI with the highest scaling value
            if (sbInMask)
            {
                // Scale the wmse so that instead of scaling the coefficients, the
                // wmse is scaled.
                cblk.wmseScaling *= 1 << (maxBits << 1);
                cblk.nROIcoeff = w * h;
                return cblk;
            }

            // In 'block aligned' mode, the code-block is set to belong entirely
            // to the ROI with the highest scaling value if one coefficient, at
            // least, belongs to the ROI
            if (blockAligned)
            {
                wrap = cblk.scanw - w;
                mi = h * w - 1;
                i = cblk.offset + cblk.scanw * (h - 1) + w - 1;
                var nroicoeff = 0;
                for (j = h; j > 0; j--)
                {
                    for (k = w - 1; k >= 0; k--, i--, mi--)
                    {
                        if (maskData[mi] != 0)
                        {
                            nroicoeff++;
                        }
                    }
                    i -= wrap;
                }
                if (nroicoeff != 0)
                {
                    // Include the subband
                    cblk.wmseScaling *= 1 << (maxBits << 1);
                    cblk.nROIcoeff = w * h;
                }
                return cblk;
            }

            // Scale background coefficients
            bitMask = (((1 << cblk.magbits) - 1) << (31 - cblk.magbits));
            wrap = cblk.scanw - w;
            mi = h * w - 1;
            i = cblk.offset + cblk.scanw * (h - 1) + w - 1;
            for (j = h; j > 0; j--)
            {
                for (k = w; k > 0; k--, i--, mi--)
                {
                    tmp = data[i];
                    if (maskData[mi] != 0)
                    {
                        // ROI coeff. We need to erase fractional bits to ensure
                        // that they do not conflict with BG coeffs. This is only
                        // strictly necessary for ROI coeffs. which non-fractional
                        // magnitude is zero, but much better BG quality can be
                        // achieved if done if reset to zero since coding zeros is
                        // much more efficient (the entropy coder knows nothing
                        // about ROI and cannot avoid coding the ROI fractional
                        // bits, otherwise this would not be necessary).
                        data[i] = (unchecked((int)0x80000000) & tmp) | (tmp & bitMask);
                        nROIcoeff++;
                    }
                    else
                    {
                        // BG coeff. it is not necessary to erase fractional bits
                        data[i] = (unchecked((int)0x80000000) & tmp) | ((tmp & 0x7FFFFFFF) >> maxBits);
                    }
                }
                i -= wrap;
            }

            // Modify the number of significant bit-planes in the code-block
            cblk.magbits += maxBits;

            // Store the number of ROI coefficients present in the code-block
            cblk.nROIcoeff = nROIcoeff;

            return cblk;
        }

        /// <summary> This function returns the flag indicating if any ROI functionality used
        /// 
        /// </summary>
        /// <returns> Flag indicating whether there are ROIs in the image
        /// </returns>
        public virtual bool useRoi()
        {
            return roi;
        }

        /// <summary> Changes the current tile, given the new indexes. An
        /// IllegalArgumentException is thrown if the indexes do not
        /// correspond to a valid tile.
        /// 
        /// </summary>
        /// <param name="x">The horizontal index of the tile.
        /// 
        /// </param>
        /// <param name="y">The vertical index of the new tile.
        /// 
        /// </param>
        public override void SetTile(int x, int y)
        {
            base.SetTile(x, y);
            if (roi)
                mg.tileChanged();
        }

        /// <summary> Advances to the next tile, in standard scan-line order (by rows then
        /// columns). An NoNextElementException is thrown if the current tile is
        /// the last one (i.e. there is no next tile).
        /// 
        /// </summary>
        public override void NextTile()
        {
            base.NextTile();
            if (roi)
                mg.tileChanged();
        }

        /// <summary> Calculates the maximum amount of magnitude bits for each
        /// tile-component, and stores it in the 'maxMagBits' array. This is called
        /// by the constructor
        /// 
        /// </summary>
        /// <param name="encSpec">The encoder specifications for addition of roi specs
        /// 
        /// </param>
        private void calcMaxMagBits(EncoderSpecs encSpec)
        {
            int tmp;
            var rois = encSpec.rois;

            var nt = src.GetNumTiles();
            var nc = src.NumComps;

            maxMagBits = new int[nt][];
            for (var i = 0; i < nt; i++)
            {
                maxMagBits[i] = new int[nc];
            }

            src.SetTile(0, 0);
            for (var t = 0; t < nt; t++)
            {
                for (var c = nc - 1; c >= 0; c--)
                {
                    tmp = src.GetMaxMagBits(c);
                    maxMagBits[t][c] = tmp;
                    rois.SetTileCompVal(t, c, tmp);
                }
                if (t < nt - 1)
                    src.NextTile();
            }
            // Reset to current initial tile position
            src.SetTile(0, 0);
        }
    }
}