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

namespace CoreJ2K.j2k.wavelet.synthesis
{

    /// <summary> This abstract class defines methods to transfer wavelet data in a
    /// code-block by code-block basis, for the decoder side. In each call to
    /// 'GetCodeBlock()' or 'GetInternCodeBlock()' a new code-block is
    /// returned. The code-blocks are returned in no specific order.
    /// 
    /// This class is the source of data, in general, for the inverse wavelet
    /// transforms. See the 'InverseWT' class.
    /// 
    /// </summary>
    /// <seealso cref="InvWTData" />
    /// <seealso cref="WaveletTransform" />
    /// <seealso cref="j2k.quantization.dequantizer.CBlkQuantDataSrcDec" />
    /// <seealso cref="InverseWT" />
    public interface CBlkWTDataSrcDec : InvWTData
    {

        /// <summary> Returns the number of bits, referred to as the "range bits",
        /// corresponding to the nominal range of the data in the specified
        /// component.
        /// 
        /// The returned value corresponds to the nominal dynamic range of the
        /// reconstructed image data, not of the wavelet coefficients
        /// themselves. This is because different subbands have different gains and
        /// thus different nominal ranges. To have an idea of the nominal range in
        /// each subband the subband analysis gain value from the subband tree
        /// structure, returned by the 'GetSynSubbandTree()' method, can be
        /// used. See the 'Subband' class for more details.
        /// 
        /// If this number is <i>b</b> then for unsigned data the nominal range
        /// is between 0 and 2^b-1, and for signed data it is between -2^(b-1) and
        /// 2^(b-1)-1.
        /// 
        /// </summary>
        /// <param name="c">The index of the component.
        /// 
        /// </param>
        /// <returns> The number of bits corresponding to the nominal range of the
        /// data.
        /// 
        /// </returns>
        /// <seealso cref="Subband" />
        int GetNomRangeBits(int c);

        /// <summary> Returns the position of the fixed point in the specified component, or
        /// equivalently the number of fractional bits. This is the position of the
        /// least significant integral (i.e. non-fractional) bit, which is
        /// equivalent to the number of fractional bits. For instance, for
        /// fixed-point values with 2 fractional bits, 2 is returned. For
        /// floating-point data this value does not apply and 0 should be
        /// returned. Position 0 is the position of the least significant bit in
        /// the data.
        /// 
        /// </summary>
        /// <param name="c">The index of the component.
        /// 
        /// </param>
        /// <returns> The position of the fixed-point, which is the same as the
        /// number of fractional bits. For floating-point data 0 is returned.
        /// 
        /// </returns>
        int GetFixedPoint(int c);

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
        /// </summary>
        /// <param name="c">The component for which to return the next code-block.
        /// 
        /// </param>
        /// <param name="m">The vertical index of the code-block to return,
        /// in the specified subband.
        /// 
        /// </param>
        /// <param name="n">The horizontal index of the code-block to return,
        /// in the specified subband.
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