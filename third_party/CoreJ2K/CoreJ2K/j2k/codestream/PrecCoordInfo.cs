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

    /// <summary> This class is used to store the coordinates of precincts.
    /// 
    /// </summary>
    public class PrecCoordInfo : CoordInfo
    {

        /// <summary>Horizontal upper left coordinate in the reference grid </summary>
        public int xref;

        /// <summary>Vertical upper left coordinate on the reference grid </summary>
        public int yref;

        /// <summary> Constructor. Creates a PrecCoordInfo object.
        /// 
        /// </summary>
        /// <param name="ulx">Horizontal upper left coordinate in the subband
        /// 
        /// </param>
        /// <param name="uly">Vertical upper left coordinate in the subband
        /// 
        /// </param>
        /// <param name="w">Precinct's width
        /// 
        /// </param>
        /// <param name="h">Precinct's height
        /// 
        /// </param>
        /// <param name="xref">The horizontal coordinate on the reference grid 
        /// 
        /// </param>
        /// <param name="yref">The vertical coordinate on the reference grid 
        /// 
        /// </param>
        public PrecCoordInfo(int ulx, int uly, int w, int h, int xref, int yref) : base(ulx, uly, w, h)
        {
            this.xref = xref;
            this.yref = yref;
        }

        /// <summary> Empty Constructor. Creates an empty PrecCoordInfo object.
        /// 
        /// </summary>
        public PrecCoordInfo()
        {
        }

        /// <summary> Returns precinct's information in a String 
        /// 
        /// </summary>
        /// <returns> String with precinct's information
        /// 
        /// </returns>
        public override string ToString()
        {
            return $"{base.ToString()}, xref={xref}, yref={yref}";
        }
    }
}