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

namespace CoreJ2K.j2k.wavelet.analysis
{

    /// <summary> This is an implementation of the 'CBlkWTData' abstract class for 32 bit
    /// floating point data (float).
    /// 
    /// The methods in this class are declared final, so that they can be
    /// inlined by inlining compilers.
    /// 
    /// </summary>
    /// <seealso cref="CBlkWTData" />
    public class CBlkWTDataFloat : CBlkWTData
    {
        /// <summary> Returns the identifier of this data type, <tt>TYPE_FLOAT</tt>, as
        /// defined in <tt>DataBlk</tt>.
        /// 
        /// </summary>
        /// <returns> The type of data stored. Always <tt>DataBlk.TYPE_FLOAT</tt>
        /// 
        /// </returns>
        /// <seealso cref="DataBlk.TYPE_FLOAT" />
        public override int DataType => DataBlk.TYPE_FLOAT;

        /// <summary> Returns the array containing the data, or null if there is no data
        /// array. The returned array is a float array.
        /// 
        /// </summary>
        /// <returns> The array of data (a float[]) or null if there is no data.
        /// 
        /// </returns>
        /// <summary> Sets the data array to the specified one. The provided array must be a
        /// float array, otherwise a ClassCastException is thrown. The size of the
        /// array is not checked for consistency with the code-block dimensions.
        /// 
        /// </summary>
        /// <param name="arr">The data array to use. Must be a float array.
        /// 
        /// </param>
        public override object? Data
        {
            get => data;

            set => data = (float[]?)value;
        }
        /// <summary> Returns the array containing the data, or null if there is no data
        /// array.
        /// 
        /// </summary>
        /// <returns> The array of data or null if there is no data.
        /// 
        /// </returns>
        /// <summary> Sets the data array to the specified one. The size of the array is not
        /// checked for consistency with the code-block dimensions. This method is
        /// more efficient than 'setData()'.
        /// 
        /// </summary>
        /// <param name="arr">The data array to use.
        /// 
        /// </param>
        public virtual float[]? DataFloat
        {
            get => data;

            set => data = value;
        }

        /// <summary>The array where the data is stored </summary>
        private float[]? data;
    }
}