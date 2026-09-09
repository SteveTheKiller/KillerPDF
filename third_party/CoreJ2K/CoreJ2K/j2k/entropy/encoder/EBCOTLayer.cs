/* 
* 
*                          class that was in EBCOTRateAllocator.
* 
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

namespace CoreJ2K.j2k.entropy.encoder
{

    /// <summary> This class holds information about each layer that is to be, or has already
    /// been, allocated . It is used in the rate-allocation process to keep the
    /// necessary layer information. It is used by EBCOTRateAllocator.
    /// 
    /// </summary>
    /// <seealso cref="EBCOTRateAllocator" />
    class EBCOTLayer
    {
        /// <summary> This is the maximum number of bytes that should be allocated for this
        /// and previous layers. This is actually the target length for the layer.
        /// 
        /// </summary>
        internal int maxBytes;

        /// <summary> The actual number of bytes which are consumed by the the current and
        /// any previous layers. This is the result from a simulation when the
        /// threshold for the layer has been set.
        /// 
        /// </summary>
        internal int actualBytes;

        /// <summary> If true the `maxBytes' value is the hard maximum and the threshold is
        /// determined iteratively. If false the `maxBytes' value is a target
        /// bitrate and the threshold is estimated from summary information
        /// accumulated during block coding.
        /// 
        /// </summary>
        internal bool optimize;

        /// <summary> The rate-distortion threshold associated with the bit-stream
        /// layer. When set the layer includes data up to the truncation points
        /// that have a slope no smaller than 'rdThreshold'.
        /// 
        /// </summary>
        internal float rdThreshold;
    }
}