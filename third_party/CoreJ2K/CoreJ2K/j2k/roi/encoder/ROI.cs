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
*/

using CoreJ2K.j2k.image.input;

namespace CoreJ2K.j2k.roi.encoder
{

    /// <summary> This class contains the shape of a single ROI. In the current 
    /// implementation only rectangles and circles are supported.
    /// 
    /// </summary>
    /// <seealso cref="ROIMaskGenerator" />
    public class ROI
    {

        /// <summary>ImgReaderPGM object with the arbrtrary ROI </summary>
        public ImgReaderPGM maskPGM = null;

        /// <summary>Where or not the ROI shape is arbitrary </summary>
        public bool arbShape;

        /// <summary>Flag indicating whether the ROI is rectangular or not </summary>
        public bool rect;

        /// <summary>The components for which the ROI is relevant </summary>
        public int comp;

        /// <summary>x coordinate of upper left corner of rectangular ROI </summary>
        public int ulx;

        /// <summary>y coordinate of upper left corner of rectangular ROI </summary>
        public int uly;

        /// <summary>width of rectangular ROI  </summary>
        public int w;

        /// <summary>height of rectangular ROI </summary>
        public int h;

        /// <summary>x coordinate of center of circular ROI </summary>
        public int x;

        /// <summary>y coordinate of center of circular ROI </summary>
        public int y;

        /// <summary>radius of circular ROI  </summary>
        public int r;


        /// <summary> Constructor for ROI with arbitrary shape
        /// 
        /// </summary>
        /// <param name="comp">The component the ROI belongs to
        /// 
        /// </param>
        /// <param name="maskPGM">ImgReaderPGM containing the ROI
        /// </param>
        public ROI(int comp, ImgReaderPGM maskPGM)
        {
            arbShape = true;
            rect = false;
            this.comp = comp;
            this.maskPGM = maskPGM;
        }

        /// <summary> Constructor for rectangular ROIs
        /// 
        /// </summary>
        /// <param name="comp">The component the ROI belongs to
        /// 
        /// </param>
        /// <param name="x">x-coordinate of upper left corner of ROI
        /// 
        /// </param>
        /// <param name="y">y-coordinate of upper left corner of ROI
        /// 
        /// </param>
        /// <param name="w">width of ROI
        /// 
        /// </param>
        /// <param name="h">height of ROI
        /// </param>
        public ROI(int comp, int ulx, int uly, int w, int h)
        {
            arbShape = false;
            this.comp = comp;
            this.ulx = ulx;
            this.uly = uly;
            this.w = w;
            this.h = h;
            rect = true;
        }

        /// <summary> Constructor for circular ROIs
        /// 
        /// </summary>
        /// <param name="comp">The component the ROI belongs to
        /// 
        /// </param>
        /// <param name="x">x-coordinate of center of ROI
        /// 
        /// </param>
        /// <param name="y">y-coordinate of center of ROI
        /// 
        /// </param>
        /// <param name="w">radius of ROI
        /// </param>
        public ROI(int comp, int x, int y, int rad)
        {
            arbShape = false;
            this.comp = comp;
            this.x = x;
            this.y = y;
            r = rad;
        }

        /// <summary> This function prints all relevant data for the ROI</summary>
        public override string ToString()
        {
            if (arbShape)
            {
                return $"ROI with arbitrary shape, PGM file= {maskPGM}";
            }
            else if (rect)
                return $"Rectangular ROI, comp={comp} ulx={ulx} uly={uly} w={w} h={h}";
            else
                return $"Circular ROI,  comp={comp} x={x} y={y} radius={r}";
        }
    }
}