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

using CoreJ2K.j2k.entropy.decoder;
using CoreJ2K.j2k.image;
using CoreJ2K.j2k.wavelet.synthesis;

namespace CoreJ2K.j2k.quantization.dequantizer
{

    /// <summary> This interface defines a source of quantized wavelet coefficients and
    /// methods to transfer them in a code-block by code-block basis, fro the
    /// decoder side. In each call to 'GetCodeBlock()' or 'GetInternCodeBlock()' a
    /// new code-block is returned.
    /// 
    /// This class is the source of data for the dequantizer. See the
    /// 'Dequantizer' class.
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
    /// </summary>
    /// <seealso cref="InvWTData" />
    /// <seealso cref="CBlkWTDataSrcDec" />
    /// <seealso cref="Dequantizer" />
    /// <seealso cref="EntropyDecoder" />
    public interface CBlkQuantDataSrcDec : InvWTData
    {

        /// <summary> Returns the specified code-block in the current tile for the specified
        /// component, as a copy (see below).
        /// 
        /// The returned code-block may be progressive, which is indicated by
        /// the 'progressive' variable of the returned 'DataBlk' object. If a
        /// code-block is progressive it means that in a later request to this
        /// method for the same code-block it is possible to retrieve data which is
        /// a better approximation, since meanwhile more data to decode for the
        /// code-block could have been received. If the code-block is not
        /// progressive then later calls to this method for the same code-block
        /// will return the exact same data values.
        /// 
        /// The data returned by this method is always a copy of the internal
        /// data of this object, if any, and it can be modified "in place" without
        /// any problems after being returned. The 'offset' of the returned data is
        /// 0, and the 'scanw' is the same as the code-block width. See the
        /// 'DataBlk' class.
        /// 
        /// The 'ulx' and 'uly' members of the returned 'DataBlk' object contain
        /// the coordinates of the top-left corner of the block, with respect to
        /// the tile, not the subband.
        /// 
        /// </summary>
        /// <param name="c">The component for which to return the next code-block.
        /// 
        /// </param>
        /// <param name="m">The vertical index of the code-block to return, in the
        /// specified subband.
        /// 
        /// </param>
        /// <param name="n">The horizontal index of the code-block to return, in the
        /// specified subband.
        /// 
        /// </param>
        /// <param name="sb">The subband in which the code-block to return is.
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
        /// <seealso cref="DataBlk" />
        DataBlk GetCodeBlock(int c, int m, int n, SubbandSyn sb, DataBlk cblk);

        /// <summary> Returns the specified code-block in the current tile for the specified
        /// component (as a reference or copy).
        /// 
        /// The returned code-block may be progressive, which is indicated by
        /// the 'progressive' variable of the returned 'DataBlk' object. If a
        /// code-block is progressive it means that in a later request to this
        /// method for the same code-block it is possible to retrieve data which is
        /// a better approximation, since meanwhile more data to decode for the
        /// code-block could have been received. If the code-block is not
        /// progressive then later calls to this method for the same code-block
        /// will return the exact same data values.
        /// 
        /// The data returned by this method can be the data in the internal
        /// buffer of this object, if any, and thus can not be modified by the
        /// caller. The 'offset' and 'scanw' of the returned data can be
        /// arbitrary. See the 'DataBlk' class.
        /// 
        /// The 'ulx' and 'uly' members of the returned 'DataBlk' object contain
        /// the coordinates of the top-left corner of the block, with respect to
        /// the tile, not the subband.
        /// 
        /// </summary>
        /// <param name="c">The component for which to return the next code-block.
        /// 
        /// </param>
        /// <param name="m">The vertical index of the code-block to return, in the
        /// specified subband.
        /// 
        /// </param>
        /// <param name="n">The horizontal index of the code-block to return, in the
        /// specified subband.
        /// 
        /// </param>
        /// <param name="sb">The subband in which the code-block to return is.
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
        /// <seealso cref="DataBlk" />
        DataBlk GetInternCodeBlock(int c, int m, int n, SubbandSyn sb, DataBlk cblk);
    }
}