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

using CoreJ2K.j2k.image;
using CoreJ2K.j2k.wavelet;

namespace CoreJ2K.j2k.roi.encoder
{

    /// <summary> This class generates the ROI masks for the ROIScaler.It gives the scaler
    /// the ROI mask for the current code-block.
    /// 
    /// The values are calculated from the scaling factors of the ROIs. The
    /// values with which to scale are equal to u-umin where umin is the lowest
    /// scaling factor within the block. The umin value is sent to the entropy
    /// coder to be used for scaling the distortion values.
    /// 
    /// </summary>
    /// <seealso cref="RectROIMaskGenerator" />
    /// <seealso cref="ArbROIMaskGenerator" />
    public abstract class ROIMaskGenerator
    {
        /// <summary> This function returns the ROIs in the image
        /// 
        /// </summary>
        /// <returns> The ROIs in the image
        /// </returns>
        public virtual ROI[] ROIs => roi_array;

        /// <summary>Array containing the ROIs </summary>
        protected internal ROI[] roi_array;

        /// <summary>Number of components </summary>
        protected internal int nrc;

        /// <summary>Flag indicating whether a mask has been made for the current tile </summary>
        protected internal bool[] tileMaskMade;

        /* Flag indicating whether there are any ROIs in this tile */
        protected internal bool roiInTile;

        /// <summary> The constructor of the mask generator
        /// 
        /// </summary>
        /// <param name="rois">The ROIs in the image
        /// 
        /// </param>
        /// <param name="nrc">The number of components
        /// </param>
        protected ROIMaskGenerator(ROI[] rois, int nrc)
        {
            roi_array = rois;
            this.nrc = nrc;
            tileMaskMade = new bool[nrc];
        }

        /// <summary> This functions gets a DataBlk with the size of the current code-block
        /// and fills it with the ROI mask. The lowest scaling value in the mask
        /// for this code-block is returned by the function to be used for
        /// modifying the rate distortion estimations.
        /// 
        /// </summary>
        /// <param name="db">The data block that is to be filled with the mask
        /// 
        /// </param>
        /// <param name="sb">The root of the current subband tree
        /// 
        /// </param>
        /// <param name="magbits">The number of magnitude bits in this code-block
        /// 
        /// </param>
        /// <param name="c">Component number
        /// 
        /// </param>
        /// <returns> Whether or not a mask was needed for this tile 
        /// </returns>
        public abstract bool GetROIMask(DataBlkInt db, Subband sb, int magbits, int c);

        /// <summary> This function generates the ROI mask for the entire tile. The mask is
        /// generated for one component. This method is called once for each tile
        /// and component.
        /// 
        /// </summary>
        /// <param name="sb">The root of the subband tree used in the decomposition
        /// 
        /// </param>
        /// <param name="magbits">The max number of magnitude bits in any code-block
        /// 
        /// </param>
        /// <param name="n">component number
        /// </param>
        public abstract void makeMask(Subband sb, int magbits, int n);

        /// <summary> This function is called every time the tile is changed to indicate
        /// that there is need to make a new mask
        /// </summary>
        public virtual void tileChanged()
        {
            for (var i = 0; i < nrc; i++)
                tileMaskMade[i] = false;
        }
    }
}