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

using CoreJ2K.j2k.wavelet;

namespace CoreJ2K.j2k.roi.encoder
{

    /// <summary> This class describes the ROI mask for a single subband. Each object of the
    /// class contains the mask for a particular subband and also has references to
    /// the masks of the children subbands of the subband corresponding to this
    /// mask. This class describes subband masks for images containing only
    /// rectangular ROIS
    /// 
    /// </summary>
    public class SubbandRectROIMask : SubbandROIMask
    {

        /// <summary>The upper left x coordinates of the applicable ROIs </summary>
        public int[] ulxs;

        /// <summary>The upper left y coordinates of the applicable ROIs </summary>
        public int[] ulys;

        /// <summary>The lower right x coordinates of the applicable ROIs </summary>
        public int[] lrxs;

        /// <summary>The lower right y coordinates of the applicable ROIs </summary>
        public int[] lrys;

        /// <summary> The constructor of the SubbandROIMask takes the dimensions of the
        /// subband as parameters. A tree of masks is generated from the subband
        /// sb. Each Subband contains the boundaries of each ROI.
        /// 
        /// </summary>
        /// <param name="sb">The subband corresponding to this Subband Mask
        /// 
        /// </param>
        /// <param name="ulxs">The upper left x coordinates of the ROIs
        /// 
        /// </param>
        /// <param name="ulys">The upper left y coordinates of the ROIs
        /// 
        /// </param>
        /// <param name="lrxs">The lower right x coordinates of the ROIs
        /// 
        /// </param>
        /// <param name="lrys">The lower right y coordinates of the ROIs
        /// 
        /// </param>
        /// <param name="lrys">The lower right y coordinates of the ROIs
        /// 
        /// </param>
        /// <param name="nr">Number of ROIs that affect this tile
        /// 
        /// </param>
        public SubbandRectROIMask(Subband sb, int[] ulxs, int[] ulys, int[] lrxs, int[] lrys, int nr) : base(sb.ulx, sb.uly, sb.w, sb.h)
        {
            this.ulxs = ulxs;
            this.ulys = ulys;
            this.lrxs = lrxs;
            this.lrys = lrys;
            int r;

            if (sb.isNode)
            {
                isNode = true;
                // determine odd/even - high/low filters
                var horEvenLow = sb.ulcx % 2;
                var verEvenLow = sb.ulcy % 2;

                // Get filter support lengths
                var hFilter = sb.HorWFilter;
                var vFilter = sb.VerWFilter;
                var hlnSup = hFilter.SynLowNegSupport;
                var hhnSup = hFilter.SynHighNegSupport;
                var hlpSup = hFilter.SynLowPosSupport;
                var hhpSup = hFilter.SynHighPosSupport;
                var vlnSup = vFilter.SynLowNegSupport;
                var vhnSup = vFilter.SynHighNegSupport;
                var vlpSup = vFilter.SynLowPosSupport;
                var vhpSup = vFilter.SynHighPosSupport;

                // Generate arrays for children
                int x, y;
                var lulxs = new int[nr];
                var lulys = new int[nr];
                var llrxs = new int[nr];
                var llrys = new int[nr];
                var hulxs = new int[nr];
                var hulys = new int[nr];
                var hlrxs = new int[nr];
                var hlrys = new int[nr];
                for (r = nr - 1; r >= 0; r--)
                {
                    // For all ROI calculate ...
                    // Upper left x for all children
                    x = ulxs[r];
                    if (horEvenLow == 0)
                    {
                        lulxs[r] = (x + 1 - hlnSup) / 2;
                        hulxs[r] = (x - hhnSup) / 2;
                    }
                    else
                    {
                        lulxs[r] = (x - hlnSup) / 2;
                        hulxs[r] = (x + 1 - hhnSup) / 2;
                    }
                    // Upper left y for all children
                    y = ulys[r];
                    if (verEvenLow == 0)
                    {
                        lulys[r] = (y + 1 - vlnSup) / 2;
                        hulys[r] = (y - vhnSup) / 2;
                    }
                    else
                    {
                        lulys[r] = (y - vlnSup) / 2;
                        hulys[r] = (y + 1 - vhnSup) / 2;
                    }
                    // lower right x for all children
                    x = lrxs[r];
                    if (horEvenLow == 0)
                    {
                        llrxs[r] = (x + hlpSup) / 2;
                        hlrxs[r] = (x - 1 + hhpSup) / 2;
                    }
                    else
                    {
                        llrxs[r] = (x - 1 + hlpSup) / 2;
                        hlrxs[r] = (x + hhpSup) / 2;
                    }
                    // lower right y for all children
                    y = lrys[r];
                    if (verEvenLow == 0)
                    {
                        llrys[r] = (y + vlpSup) / 2;
                        hlrys[r] = (y - 1 + vhpSup) / 2;
                    }
                    else
                    {
                        llrys[r] = (y - 1 + vlpSup) / 2;
                        hlrys[r] = (y + vhpSup) / 2;
                    }
                }
                // Create children
                hh = new SubbandRectROIMask(sb.HH, hulxs, hulys, hlrxs, hlrys, nr);
                lh = new SubbandRectROIMask(sb.LH, lulxs, hulys, llrxs, hlrys, nr);
                hl = new SubbandRectROIMask(sb.HL, hulxs, lulys, hlrxs, llrys, nr);
                ll = new SubbandRectROIMask(sb.LL, lulxs, lulys, llrxs, llrys, nr);
            }
        }
    }
}