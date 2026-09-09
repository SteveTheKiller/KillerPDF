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

using CoreJ2K.j2k.wavelet.analysis;

namespace CoreJ2K.j2k.entropy.encoder
{

    /// <summary> This interface defines a source of entropy coded data and methods to
    /// transfer it in a code-block by code-block basis. In each call to
    /// 'GetNextCodeBlock()' a new coded code-block is returned. The code-block are
    /// retruned in no specific-order.
    /// 
    /// This interface is the source of data for the rate allocator. See the
    /// 'PostCompRateAllocator' class.
    /// 
    /// For each coded-code-block the entropy-coded data is returned along with
    /// the rate-distortion statistics in a 'CBlkRateDistStats' object.
    /// 
    /// </summary>
    /// <seealso cref="PostCompRateAllocator" />
    /// <seealso cref="CBlkRateDistStats" />
    /// <seealso cref="EntropyCoder" />
    public interface CodedCBlkDataSrcEnc : ForwWTDataProps
    {

        /// <summary> Returns the next coded code-block in the current tile for the specified
        /// component, as a copy (see below). The order in which code-blocks are
        /// returned is not specified. However each code-block is returned only
        /// once and all code-blocks will be returned if the method is called 'N'
        /// times, where 'N' is the number of code-blocks in the tile. After all
        /// the code-blocks have been returned for the current tile calls to this
        /// method will return 'null'.
        /// 
        /// When changing the current tile (through 'SetTile()' or 'NextTile()')
        /// this method will always return the first code-block, as if this method
        /// was never called before for the new current tile.
        /// 
        /// The data returned by this method is always a copy of the internal
        /// data of this object, if any, and it can be modified "in place" without
        /// any problems after being returned.
        /// 
        /// </summary>
        /// <param name="c">The component for which to return the next code-block.
        /// 
        /// </param>
        /// <param name="ccb">If non-null this object might be used in returning the coded
        /// code-block in this or any subsequent call to this method. If null a new
        /// one is created and returned. If the 'data' array of 'cbb' is not null
        /// it may be reused to return the compressed data.
        /// 
        /// </param>
        /// <returns> The next coded code-block in the current tile for component
        /// 'c', or null if all code-blocks for the current tile have been
        /// returned.
        /// 
        /// </returns>
        /// <seealso cref="CBlkRateDistStats" />
        CBlkRateDistStats GetNextCodeBlock(int c, CBlkRateDistStats ccb);

        /// <summary> Returns the width of a packet for the specified tile-component and
        /// resolution level.
        /// 
        /// </summary>
        /// <param name="t">The tile
        /// 
        /// </param>
        /// <param name="c">The component
        /// 
        /// </param>
        /// <param name="r">The resolution level
        /// 
        /// </param>
        /// <returns> The width of a packet for the specified tile- component and
        /// resolution level.
        /// 
        /// </returns>
        int GetPPX(int t, int c, int r);

        /// <summary> Returns the height of a packet for the specified tile-component and
        /// resolution level.
        /// 
        /// </summary>
        /// <param name="t">The tile
        /// 
        /// </param>
        /// <param name="c">The component
        /// 
        /// </param>
        /// <param name="r">The resolution level
        /// 
        /// </param>
        /// <returns> The height of a packet for the specified tile- component and
        /// resolution level.
        /// 
        /// </returns>
        int GetPPY(int t, int c, int r);

        /// <summary> Returns true if the precinct partition is used for the specified
        /// component and tile, returns false otherwise
        /// 
        /// </summary>
        /// <param name="c">The component
        /// 
        /// </param>
        /// <param name="t">The tile
        /// 
        /// </param>
        bool precinctPartitionUsed(int c, int t);
    }
}