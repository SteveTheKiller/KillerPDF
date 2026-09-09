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

namespace CoreJ2K.j2k.wavelet.analysis
{

    /// <summary> This interface extends the WaveletTransform with the specifics of forward
    /// wavelet transforms. Classes that implement forward wavelet transfoms should
    /// implement this interface.
    /// 
    /// This class does not define the methods to transfer data, just the
    /// specifics to forward wavelet transform. Different data transfer methods are 
    /// evisageable for different transforms.
    /// 
    /// </summary>
    public interface ForwWT : WaveletTransform, ForwWTDataProps
    {
        /// <summary> Returns the horizontal analysis wavelet filters used in each level, for
        /// the specified tile-component. The first element in the array is the
        /// filter used to obtain the lowest resolution (resolution level 0)
        /// subbands (i.e. lowest frequency LL subband), the second element is the
        /// one used to generate the resolution level 1 subbands, and so on. If
        /// there are less elements in the array than the number of resolution
        /// levels, then the last one is assumed to repeat itself.
        /// 
        /// The returned filters are applicable only to the specified component
        /// and in the current tile.
        /// 
        /// The resolution level of a subband is the resolution level to which a
        /// subband contributes, which is different from its decomposition
        /// level.
        /// 
        /// </summary>
        /// <param name="t">The index of the tile for which to return the filters.
        /// 
        /// </param>
        /// <param name="c">The index of the component for which to return the filters.
        /// 
        /// </param>
        /// <returns> The horizontal analysis wavelet filters used in each level.
        /// 
        /// </returns>
        WaveletFilter[] GetHorAnWaveletFilters(int t, int c);

        /// <summary> Returns the vertical analysis wavelet filters used in each level, for
        /// the specified tile-component. The first element in the array is the
        /// filter used to obtain the lowest resolution (resolution level 0)
        /// subbands (i.e. lowest frequency LL subband), the second element is the
        /// one used to generate the resolution level 1 subbands, and so on. If
        /// there are less elements in the array than the number of resolution
        /// levels, then the last one is assumed to repeat itself.
        /// 
        /// The returned filters are applicable only to the specified component
        /// and in the current tile.
        /// 
        /// The resolution level of a subband is the resolution level to which a
        /// subband contributes, which is different from its decomposition
        /// level.
        /// 
        /// </summary>
        /// <param name="t">The index of the tile for which to return the filters.
        /// 
        /// </param>
        /// <param name="c">The index of the component for which to return the filters.
        /// 
        /// </param>
        /// <returns> The vertical analysis wavelet filters used in each level.
        /// 
        /// </returns>
        WaveletFilter[] GetVertAnWaveletFilters(int t, int c);

        /// <summary> Returns the number of decomposition levels that are applied to obtain
        /// the LL band, in the specified tile-component. A value of 0 means that
        /// no wavelet transform is applied.
        /// 
        /// </summary>
        /// <param name="t">The tile index
        /// 
        /// </param>
        /// <param name="c">The index of the component.
        /// 
        /// </param>
        /// <returns> The number of decompositions applied to obtain the LL subband
        /// (0 for no wavelet transform).
        /// 
        /// </returns>
        int GetDecompLevels(int t, int c);

        /// <summary> Returns the wavelet tree decomposition. Only WT_DECOMP_DYADIC is
        /// supported by JPEG 2000 part I.
        /// 
        /// </summary>
        /// <param name="t">The tile index
        /// 
        /// </param>
        /// <param name="c">The index of the component.
        /// 
        /// </param>
        /// <returns> The wavelet decomposition.
        /// 
        /// </returns>
        int GetDecomp(int t, int c);
    }
}