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

using CoreJ2K.j2k.wavelet;

namespace CoreJ2K.j2k.quantization.dequantizer
{

    /// <summary> This class holds the parameters for the scalar deadzone dequantizer
    /// (StdDequantizer class) for the current tile. Its constructor decodes the
    /// parameters from the main header and tile headers.
    /// 
    /// </summary>
    /// <seealso cref="StdDequantizer" />
    public class StdDequantizerParams : DequantizerParams
    {
        /// <summary> Returns the type of the dequantizer for which the parameters are. The
        /// types are defined in the Dequantizer class.
        /// 
        /// </summary>
        /// <returns> The type of the dequantizer for which the parameters
        /// are. Always Q_TYPE_SCALAR_DZ.
        /// 
        /// </returns>
        /// <seealso cref="Dequantizer" />
        public override int DequantizerType => QuantizationType_Fields.Q_TYPE_SCALAR_DZ;

        /// <summary> The quantization step "exponent" value, for each resolution level and
        /// subband, as it appears in the codestream. The first index is the
        /// resolution level, and the second the subband index (within the
        /// resolution level), as specified in the Subband class. When in derived
        /// quantization mode only the first resolution level (level 0) appears.
        /// 
        /// For non-reversible systems this value corresponds to ceil(log2(D')),
        /// where D' is the quantization step size normalized to data of a dynamic
        /// range of 1. The true quantization step size is (2^R)*D', where R is
        /// ceil(log2(dr)), where 'dr' is the dynamic range of the subband samples,
        /// in the corresponding subband.
        /// 
        /// For reversible systems the exponent value in 'exp' is used to
        /// determine the number of magnitude bits in the quantized
        /// coefficients. It is, in fact, the dynamic range of the subband data.
        /// 
        /// In general the index of the first subband in a resolution level is
        /// not 0. The exponents appear, within each resolution level, at their
        /// subband index, and not in the subband order starting from 0. For
        /// instance, resolution level 3, the first subband has the index 16, then
        /// the exponent of the subband is exp[3][16], not exp[3][0].
        /// 
        /// </summary>
        /// <seealso cref="Subband" />
        public int[][] exp;

        /// <summary> The quantization step for non-reversible systems, normalized to a
        /// dynamic range of 1, for each resolution level and subband, as derived
        /// from the exponent-mantissa representation in the codestream. The first
        /// index is the resolution level, and the second the subband index (within
        /// the resolution level), as specified in the Subband class. When in
        /// derived quantization mode only the first resolution level (level 0)
        /// appears.
        /// 
        /// The true step size D is obtained as follows: D=(2^R)*D', where
        /// 'R=ceil(log2(dr))' and 'dr' is the dynamic range of the subband
        /// samples, in the corresponding subband.
        /// 
        /// This value is 'null' for reversible systems (i.e. there is no true
        /// quantization, 'D' is always 1).
        /// 
        /// In general the index of the first subband in a resolution level is
        /// not 0. The steps appear, within each resolution level, at their subband
        /// index, and not in the subband order starting from 0. For instance, if
        /// resolution level 3, the first subband has the index 16, then the step
        /// of the subband is nStep[3][16], not nStep[3][0].
        /// 
        /// </summary>
        /// <seealso cref="Subband" />
        public float[][] nStep;
    }
}