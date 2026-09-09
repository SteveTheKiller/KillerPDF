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

using CoreJ2K.j2k.image;

namespace CoreJ2K.j2k.wavelet
{

    /// <summary> This interface defines how a forward or inverse wavelet transform should
    /// present itself. As specified in the ImgData interface, from which this
    /// class inherits, all operations are confined to the current tile, and all
    /// coordinates are relative to it.
    /// 
    /// The definition of the methods in this interface allows for different
    /// types of implementation, reversibility and levels of decompositions for
    /// each component and each tile. An implementation of this interface does not
    /// need to support all this flexibility (e.g., it may provide the same
    /// implementation type and decomposition levels for all tiles and
    /// components).
    /// 
    /// </summary>
    public struct WaveletTransform_Fields
    {
        /// <summary> ID for line based implementations of wavelet transforms.
        /// 
        /// </summary>
        public static readonly int WT_IMPL_LINE = 0;

        /// <summary> ID for full-page based implementations of wavelet transforms. Full-page
        /// based implementations should be avoided since they require large
        /// amounts of memory.
        /// 
        /// </summary>
        public const int WT_IMPL_FULL = 2;
    }
    public interface WaveletTransform : ImgData
    {


        /// <summary> Returns the reversibility of the wavelet transform for the specified
        /// component and tile. A wavelet transform is reversible when it is
        /// suitable for lossless and lossy-to-lossless compression.
        /// 
        /// </summary>
        /// <param name="t">The index of the tile.
        /// 
        /// </param>
        /// <param name="c">The index of the component.
        /// 
        /// </param>
        /// <returns> true is the wavelet transform is reversible, false if not.
        /// 
        /// </returns>
        bool IsReversible(int t, int c);

        /// <summary> Returns the implementation type of this wavelet transform (WT_IMPL_LINE
        /// or WT_IMPL_FRAME) for the specified component, in the current tile.
        /// 
        /// </summary>
        /// <param name="c">The index of the component.
        /// 
        /// </param>
        /// <returns> WT_IMPL_LINE or WT_IMPL_FULL for line, block or full-page based
        /// transforms.
        /// 
        /// </returns>
        int GetImplementationType(int c);
    }
}