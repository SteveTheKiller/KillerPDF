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

namespace CoreJ2K.j2k.codestream
{

    /// <summary> Class that holds precinct coordinates and references to contained
    /// code-blocks in each subband. 
    /// 
    /// </summary>
    public class PrecInfo
    {

        /// <summary>Precinct horizontal upper-left coordinate in the reference grid </summary>
        public int rgulx;

        /// <summary>Precinct vertical upper-left coordinate in the reference grid </summary>
        public int rguly;

        /// <summary>Precinct width reported in the reference grid </summary>
        public int rgw;

        /// <summary>Precinct height reported in the reference grid </summary>
        public int rgh;

        /// <summary>Precinct horizontal upper-left coordinate in the corresponding
        /// resolution level
        /// </summary>
        public int ulx;

        /// <summary>Precinct vertical upper-left coordinate in the corresponding
        /// resolution level
        /// </summary>
        public int uly;

        /// <summary>Precinct width in the corresponding resolution level </summary>
        public int w;

        /// <summary>Precinct height in the corresponding resolution level </summary>
        public int h;

        /// <summary>Resolution level index </summary>
        public int r;

        /// <summary>Code-blocks belonging to this precinct in each subbands of the
        /// resolution level 
        /// </summary>
        public CBlkCoordInfo[][][] cblk;

        /// <summary>Number of code-blocks in each subband belonging to this precinct </summary>
        public int[] nblk;

        /// <summary> Class constructor.
        /// 
        /// </summary>
        /// <param name="r">Resolution level index.
        /// </param>
        /// <param name="ulx">Precinct horizontal offset.
        /// </param>
        /// <param name="uly">Precinct vertical offset.
        /// </param>
        /// <param name="w">Precinct width.
        /// </param>
        /// <param name="h">Precinct height.
        /// </param>
        /// <param name="rgulx">Precinct horizontal offset in the image reference grid.
        /// </param>
        /// <param name="rguly">Precinct horizontal offset in the image reference grid.
        /// </param>
        /// <param name="rgw">Precinct width in the reference grid.
        /// </param>
        /// <param name="rgh">Precinct height in the reference grid.
        /// 
        /// </param>
        public PrecInfo(int r, int ulx, int uly, int w, int h, int rgulx, int rguly, int rgw, int rgh)
        {
            this.r = r;
            this.ulx = ulx;
            this.uly = uly;
            this.w = w;
            this.h = h;
            this.rgulx = rgulx;
            this.rguly = rguly;
            this.rgw = rgw;
            this.rgh = rgh;

            if (r == 0)
            {
                cblk = new CBlkCoordInfo[1][][];
                nblk = new int[1];
            }
            else
            {
                cblk = new CBlkCoordInfo[4][][];
                nblk = new int[4];
            }
        }

        /// <summary>Resets all coordinate fields of this instance for reuse on the next
        /// tile. Existing <c>cblk</c> and <c>nblk</c> arrays are kept so callers
        /// can overwrite the slots without reallocating the outer arrays.</summary>
        public void Reset(int r, int ulx, int uly, int w, int h, int rgulx, int rguly, int rgw, int rgh)
        {
            this.r = r;
            this.ulx = ulx;
            this.uly = uly;
            this.w = w;
            this.h = h;
            this.rgulx = rgulx;
            this.rguly = rguly;
            this.rgw = rgw;
            this.rgh = rgh;
            // cblk and nblk arrays are the right length for this r-level;
            // callers replace individual cblk[s] entries and nblk[s] values directly.
        }

        /// <summary> Returns PrecInfo object information in a String
        /// 
        /// </summary>
        /// <returns> PrecInfo information 
        /// 
        /// </returns>
        public override string ToString()
        {
            return $"ulx={ulx},uly={uly},w={w},h={h},rgulx={rgulx},rguly={rguly},rgw={rgw},rgh={rgh}";
        }
    }
}