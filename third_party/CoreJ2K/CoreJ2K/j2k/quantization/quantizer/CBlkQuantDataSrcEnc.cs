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

using CoreJ2K.j2k.entropy.encoder;
using CoreJ2K.j2k.wavelet.analysis;

namespace CoreJ2K.j2k.quantization.quantizer
{

    /// <summary> This interface defines a source of quantized wavelet coefficients and
    /// methods to transfer them in a code-block by code-block basis. In each call
    /// to 'GetNextCodeBlock()' or 'GetNextInternCodeBlock()' a new code-block is
    /// returned. The code-blocks are returned in no specific order.
    /// 
    /// This class is the source of data for the entropy coder. See the
    /// 'EntropyCoder' class.
    /// 
    /// Code-block data is returned in sign-magnitude representation, instead of
    /// the normal two's complement one. Only integral types are used. The sign
    /// magnitude representation is more adequate for entropy coding. In sign
    /// magnitude representation, the most significant bit is used for the sign (0
    /// if positive, 1 if negative) and the magnitude of the coefficient is stored
    /// in the next M most significant bits. The rest of the bits (least
    /// significant bits) can contain a fractional value of the quantized
    /// coefficient. The number 'M' of magnitude bits is communicated in the
    /// 'magbits' member variable of the 'CBlkWTData'.
    /// 
    /// Note that no more of one object may request data, otherwise one object
    /// would get some of the data and another one another part, in no defined
    /// manner.
    /// 
    /// </summary>
    /// <seealso cref="ForwWTDataProps" />
    /// <seealso cref="CBlkWTDataSrc" />
    /// <seealso cref="Quantizer" />
    /// <seealso cref="EntropyCoder" />
    public interface CBlkQuantDataSrcEnc : ForwWTDataProps
    {

        /// <summary> Returns the next code-block in the current tile for the specified
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
        /// any problems after being returned. The 'offset' of the returned data is
        /// 0, and the 'scanw' is the same as the code-block width. See the
        /// 'CBlkWTData' class.
        /// 
        /// The 'ulx' and 'uly' members of the returned 'CBlkWTData' object
        /// contain the coordinates of the top-left corner of the block, with
        /// respect to the tile, not the subband.
        /// 
        /// </summary>
        /// <param name="c">The component for which to return the next code-block.
        /// 
        /// </param>
        /// <param name="cblk">If non-null this object will be used to return the new
        /// code-block. If null a new one will be allocated and returned. If the
        /// "data" array of the object is non-null it will be reused, if possible,
        /// to return the data.
        /// 
        /// </param>
        /// <returns> The next code-block in the current tile for component 'c', or
        /// null if all code-blocks for the current tile have been returned.
        /// 
        /// </returns>
        /// <seealso cref="CBlkWTData" />
        CBlkWTData? GetNextCodeBlock(int c, CBlkWTData? cblk);

        /// <summary> Returns the next code-block in the current tile for the specified
        /// component. The order in which code-blocks are returned is not
        /// specified. However each code-block is returned only once and all
        /// code-blocks will be returned if the method is called 'N' times, where
        /// 'N' is the number of code-blocks in the tile. After all the code-blocks
        /// have been returned for the current tile calls to this method will
        /// return 'null'.
        /// 
        /// When changing the current tile (through 'SetTile()' or 'NextTile()')
        /// this method will always return the first code-block, as if this method
        /// was never called before for the new current tile.
        /// 
        /// The data returned by this method can be the data in the internal
        /// buffer of this object, if any, and thus can not be modified by the
        /// caller. The 'offset' and 'scanw' of the returned data can be
        /// arbitrary. See the 'CBlkWTData' class.
        /// 
        /// The 'ulx' and 'uly' members of the returned 'CBlkWTData' object
        /// contain the coordinates of the top-left corner of the block, with
        /// respect to the tile, not the subband.
        /// 
        /// </summary>
        /// <param name="c">The component for which to return the next code-block.
        /// 
        /// </param>
        /// <param name="cblk">If non-null this object will be used to return the new
        /// code-block. If null a new one will be allocated and returned. If the
        /// "data" array of the object is non-null it will be reused, if possible,
        /// to return the data.
        /// 
        /// </param>
        /// <returns> The next code-block in the current tile for component 'n', or
        /// null if all code-blocks for the current tile have been returned.
        /// 
        /// </returns>
        /// <seealso cref="CBlkWTData" />
        CBlkWTData? GetNextInternCodeBlock(int c, CBlkWTData? cblk);
    }
}