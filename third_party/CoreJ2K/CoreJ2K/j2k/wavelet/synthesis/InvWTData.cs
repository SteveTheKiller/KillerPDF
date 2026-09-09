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

    /// <summary> This interface extends the MultiResImgData interface with the methods that
    /// are necessary for inverse wavelet data (i.e. data that is the source to an
    /// inverse wavlet trasnform).
    /// 
    /// </summary>
    public interface InvWTData : MultiResImgData
    {
        /// <summary> Returns the horizontal code-block partition origin. Allowable values
        /// are 0 and 1, nothing else.
        /// 
        /// </summary>
        int CbULX
        {
            get;

        }
        /// <summary> Returns the vertical code-block partition origin Allowable values are 0
        /// and 1, nothing else.
        /// 
        /// </summary>
        int CbULY
        {
            get;

        }

        /// <summary> Returns the subband tree, for the specified tile-component. This method
        /// returns the root element of the subband tree structure, see 'Subband'
        /// and 'SubbandSyn'. The tree comprises all the available resolution
        /// levels.
        /// 
        /// </summary>
        /// <param name="t">The index of the tile, from 0 to T-1.
        /// 
        /// </param>
        /// <param name="c">The index of the component, from 0 to C-1.
        /// 
        /// </param>
        /// <returns> The root of the tree structure.
        /// 
        /// </returns>
        new SubbandSyn GetSynSubbandTree(int t, int c);
    }
}