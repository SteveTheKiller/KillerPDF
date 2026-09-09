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

using System;

namespace CoreJ2K.j2k.codestream.reader
{

    /// <summary> This class contains location of code-blocks' piece of codewords (there is
    /// one piece per layer) and some other information.
    /// 
    /// </summary>
    public class CBlkInfo
    {

        /// <summary>Upper-left x-coordinate of the code-block (relative to the tile) </summary>
        public int ulx;

        /// <summary>Upper-left y-coordinate of the code-block (relative to the tile) </summary>
        public int uly;

        /// <summary>Width of the code-block </summary>
        public int w;

        /// <summary>Height of the code-block </summary>
        public int h;

        /// <summary>The number of most significant bits which are skipped for this
        /// code-block (= Mb-1-bitDepth). 
        /// </summary>
        public int msbSkipped;

        /// <summary>Length of each piece of code-block's codewords </summary>
        public int[] len;

        /// <summary>Offset of each piece of code-block's codewords in the file </summary>
        public int[] off;

        /// <summary>The number of truncation point for each layer </summary>
        public int[] ntp;

        /// <summary>The cumulative number of truncation points </summary>
        public int ctp;

        /// <summary>The length of each segment (used with regular termination or in
        /// selective arithmetic bypass coding mode) 
        /// </summary>
        public int[][] segLen;

        /// <summary>Index of the packet where each layer has been found </summary>
        public int[] pktIdx;

        /// <summary> Constructs a new instance with specified number of layers and
        /// code-block coordinates. The number corresponds to the maximum piece of
        /// codeword for one code-block.
        /// 
        /// </summary>
        /// <param name="ulx">The uper-left x-coordinate
        /// 
        /// </param>
        /// <param name="uly">The uper-left y-coordinate
        /// 
        /// </param>
        /// <param name="w">Width of the code-block
        /// 
        /// </param>
        /// <param name="h">Height of the code-block
        /// 
        /// </param>
        /// <param name="nl">The number of layers
        /// 
        /// </param>
        public CBlkInfo(int ulx, int uly, int w, int h, int nl)
        {
            this.ulx = ulx;
            this.uly = uly;
            this.w = w;
            this.h = h;
            off = new int[nl];
            len = new int[nl];
            ntp = new int[nl];
            segLen = new int[nl][];
            pktIdx = new int[nl];
            for (var i = nl - 1; i >= 0; i--)
            {
                pktIdx[i] = -1;
            }
        }

        /// <summary>Resets this instance for reuse with the given code-block coordinates
        /// and layer count. Reuses existing arrays when the layer count is unchanged
        /// to avoid heap allocations on repeated tile decodes.</summary>
        public void Reset(int ulx, int uly, int w, int h, int nl)
        {
            this.ulx = ulx;
            this.uly = uly;
            this.w = w;
            this.h = h;
            msbSkipped = 0;
            ctp = 0;
            if (len != null && len.Length == nl)
            {
                Array.Clear(off, 0, nl);
                Array.Clear(len, 0, nl);
                Array.Clear(ntp, 0, nl);
                Array.Clear(segLen, 0, nl);
                for (var i = nl - 1; i >= 0; i--)
                    pktIdx[i] = -1;
            }
            else
            {
                off = new int[nl];
                len = new int[nl];
                ntp = new int[nl];
                segLen = new int[nl][];
                pktIdx = new int[nl];
                for (var i = nl - 1; i >= 0; i--)
                    pktIdx[i] = -1;
            }
        }

        /// <summary>Resets this instance for reuse with the same code-block coordinates
        /// and a (potentially new) layer count. Coordinates are preserved because the
        /// caller guarantees same-geometry tiles. Arrays are reused when layer count
        /// is unchanged.</summary>
        public void Reset(int nl)
        {
            msbSkipped = 0;
            ctp = 0;
            if (len != null && len.Length == nl)
            {
                Array.Clear(off, 0, nl);
                Array.Clear(len, 0, nl);
                Array.Clear(ntp, 0, nl);
                Array.Clear(segLen, 0, nl);
                for (var i = nl - 1; i >= 0; i--)
                    pktIdx[i] = -1;
            }
            else
            {
                off = new int[nl];
                len = new int[nl];
                ntp = new int[nl];
                segLen = new int[nl][];
                pktIdx = new int[nl];
                for (var i = nl - 1; i >= 0; i--)
                    pktIdx[i] = -1;
            }
        }

        /// <summary> Adds the number of new truncation for specified layer.
        /// 
        /// </summary>
        /// <param name="l">layer index
        /// 
        /// </param>
        /// <param name="newtp">Number of new truncation points 
        /// 
        /// </param>
        public virtual void addNTP(int l, int newtp)
        {
            ntp[l] = newtp;
            ctp = 0;
            for (var lIdx = 0; lIdx <= l; lIdx++)
            {
                ctp += ntp[lIdx];
            }
        }

        /// <summary> Object information in a string.
        /// 
        /// </summary>
        /// <returns> Object information
        /// 
        /// </returns>
        public override string ToString()
        {
            var result = $"(ulx,uly,w,h)= ({ulx},{uly},{w},{h}";
            result += (") " + msbSkipped + " MSB bit(s) skipped\n");
            if (len != null)
                for (var i = 0; i < len.Length; i++)
                {
                    result += ("\tl:" + i + ", start:" + off[i] + ", len:" + len[i] + ", ntp:" + ntp[i] + ", pktIdx=" + pktIdx[i]);
                    if (segLen?[i] != null)
                    {
                        result += " { ";
                        for (var j = 0; j < segLen[i].Length; j++)
                            result += (segLen[i][j] + " ");
                        result += "}";
                    }
                    result += "\n";
                }
            result += ("\tctp=" + ctp);
            return result;
        }
    }
}