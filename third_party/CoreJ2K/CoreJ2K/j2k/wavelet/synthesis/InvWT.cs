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

namespace CoreJ2K.j2k.wavelet.synthesis
{

    /// <summary> This interface extends the WaveletTransform with the specifics of inverse
    /// wavelet transforms. Classes that implement inverse wavelet transfoms should
    /// implement this interface.
    /// 
    /// This class does not define the methods to transfer data, just the
    /// specifics to inverse wavelet transform. Different data transfer methods are
    /// envisageable for different transforms.
    /// 
    /// </summary>
    public interface InvWT : WaveletTransform
    {
        /// <summary> Sets the image reconstruction resolution level. A value of 0 means
        /// reconstruction of an image with the lowest resolution (dimension)
        /// available.
        /// 
        /// Note: Image resolution level indexes may differ from tile-component
        /// resolution index. They are indeed indexed starting from the lowest
        /// number of decomposition levels of each component of each tile.
        /// 
        /// Example: For an image (1 tile) with 2 components (component 0 having
        /// 2 decomposition levels and component 1 having 3 decomposition levels),
        /// the first (tile-) component has 3 resolution levels and the second one
        /// has 4 resolution levels, whereas the image has only 3 resolution levels
        /// available.
        /// 
        /// </summary>
        /// <param name="rl">The image resolution level.
        /// 
        /// </param>
        /// <returns> The vertical coordinate of the image origin in the canvas
        /// system, on the reference grid.
        /// 
        /// </returns>
        int ImgResLevel
        {
            set;

        }
    }
}