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
*  */
using System;

namespace CoreJ2K.j2k.roi.encoder
{

    /// <summary> This abstract class describes the ROI mask for a single subband. Each
    /// object of the class contains the mask for a particular subband and also has
    /// references to the masks of the children subbands of the subband
    /// corresponding to this mask.  
    /// </summary>
    public abstract class SubbandROIMask
    {

        /// <summary>The subband masks of the child LL </summary>
        protected internal SubbandROIMask ll;

        /// <summary>The subband masks of the child LH </summary>
        protected internal SubbandROIMask lh;

        /// <summary>The subband masks of the child HL </summary>
        protected internal SubbandROIMask hl;

        /// <summary>The subband masks of the child HH </summary>
        protected internal SubbandROIMask hh;

        /// <summary>Flag indicating whether this subband mask is a node or not </summary>
        protected internal bool isNode;

        /// <summary>Horizontal uper-left coordinate of the subband mask </summary>
        public int ulx;

        /// <summary>Vertical uper-left coordinate of the subband mask </summary>
        public int uly;

        /// <summary>Width of the subband mask </summary>
        public int w;

        /// <summary>Height of the subband mask </summary>
        public int h;

        /// <summary> The constructor of the SubbandROIMask takes the dimensions of the
        /// subband as parameters
        /// 
        /// </summary>
        /// <param name="ulx">The upper left x coordinate of corresponding subband
        /// 
        /// </param>
        /// <param name="uly">The upper left y coordinate of corresponding subband
        /// 
        /// </param>
        /// <param name="w">The width of corresponding subband
        /// 
        /// </param>
        /// <param name="h">The height of corresponding subband
        /// 
        /// </param>
        protected SubbandROIMask(int ulx, int uly, int w, int h)
        {
            this.ulx = ulx;
            this.uly = uly;
            this.w = w;
            this.h = h;
        }

        /// <summary> Returns a reference to the Subband mask element to which the specified
        /// point belongs. The specified point must be inside this (i.e. the one
        /// defined by this object) subband mask. This method searches through the
        /// tree.
        /// 
        /// </summary>
        /// <param name="x">horizontal coordinate of the specified point.
        /// 
        /// </param>
        /// <param name="y">horizontal coordinate of the specified point.
        /// 
        /// </param>
        public virtual SubbandROIMask GetSubbandRectROIMask(int x, int y)
        {
            SubbandROIMask cur, hhs;

            // Check that we are inside this subband
            if (x < ulx || y < uly || x >= ulx + w || y >= uly + h)
            {
                throw new ArgumentException();
            }

            cur = this;
            while (cur.isNode)
            {
                hhs = cur.hh;
                // While we are still at a node -> continue
                if (x < hhs.ulx)
                {
                    // Is the result of horizontal low-pass
                    cur = y < hhs.uly ? cur.ll : cur.lh;
                }
                else
                {
                    // Is the result of horizontal high-pass
                    cur = y < hhs.uly ? cur.hl : cur.hh;
                }
            }
            return cur;
        }
    }
}