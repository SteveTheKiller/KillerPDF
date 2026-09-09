/*
* Interface:           DataBlkFloat
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
using System;
namespace CoreJ2K.j2k.image
{

    /// <summary> This is an implementation of the <tt>DataBlk</tt> interface for 32 bit
    /// floating point data (float).
    /// 
    /// The methods in this class are declared final, so that they can be
    /// inlined by inlining compilers.
    /// 
    /// </summary>
    /// <seealso cref="DataBlk" />
    public class DataBlkFloat : DataBlk
    {
        /// <summary> Returns the identifier of this data type, <tt>TYPE_FLOAT</tt>, as
        /// defined in <tt>DataBlk</tt>.
        /// 
        /// </summary>
        /// <returns> The type of data stored. Always <tt>DataBlk.TYPE_FLOAT</tt>
        /// 
        /// </returns>
        /// <seealso cref="DataBlk.TYPE_FLOAT" />
        public override int DataType => TYPE_FLOAT;

        /// <summary> Returns the array containing the data, or null if there is no data
        /// array. The returned array is a float array.
        /// 
        /// </summary>
        /// <returns> The array of data (a float[]) or null if there is no data.
        /// 
        /// </returns>
        /// <summary> Sets the data array to the specified one. The provided array must be a
        /// float array, otherwise a ClassCastException is thrown. The size of the
        /// array is not checked for consistency with the block's dimensions.
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
        /// checked for consistency with the block's dimensions.
        /// 
        /// </summary>
        /// <param name="arr">The data array to use.
        /// 
        /// </param>
        public virtual float[] DataFloat
        {
            get => data;

            set => data = value;
        }
        /// <summary>The array where the data is stored </summary>
        private float[] data;

        /// <summary> Creates a DataBlkFloat with 0 dimensions and no data array
        /// (i.e. data is null).
        /// 
        /// </summary>
        public DataBlkFloat()
        {
        }

        /// <summary> Creates a DataBlkFloat with the specified dimensions and position. The
        /// data array is initialized to an array of size w*h.
        /// 
        /// </summary>
        /// <param name="ulx">The horizontal coordinate of the upper-left corner of the
        /// block
        /// 
        /// </param>
        /// <param name="uly">The vertical coordinate of the upper-left corner of the
        /// block
        /// 
        /// </param>
        /// <param name="w">The width of the block (in pixels)
        /// 
        /// </param>
        /// <param name="h">The height of the block (in pixels)
        /// 
        /// </param>
        public DataBlkFloat(int ulx, int uly, int w, int h)
        {
            this.ulx = ulx;
            this.uly = uly;
            this.w = w;
            this.h = h;
            offset = 0;
            scanw = w;
            
            // Validate dimensions to prevent integer overflow
            if (w < 0 || h < 0)
            {
                throw new ArgumentOutOfRangeException(
                    $"Block dimensions cannot be negative: w={w}, h={h}");
            }
            
            long totalSize = (long)w * h;
            if (totalSize > int.MaxValue)
            {
                throw new ArgumentException(
                    $"Block dimensions too large: w={w}, h={h}. " +
                    $"Total size {totalSize} exceeds maximum array size {int.MaxValue}");
            }
            
            data = new float[(int)totalSize];
        }

        /// <summary> Copy constructor. Creates a DataBlkFloat which is the copy of the
        /// DataBlkFloat given as paramter.
        /// 
        /// </summary>
        /// <param name="DataBlkFloat">the object to be copied.
        /// 
        /// </param>
        public DataBlkFloat(DataBlkFloat src)
        {
            ulx = src.ulx;
            uly = src.uly;
            w = src.w;
            h = src.h;
            offset = 0;
            scanw = w;
            data = new float[w * h];
            for (var i = 0; i < h; i++)
                Array.Copy(src.data, i * src.scanw, data, i * scanw, w);
        }

        /// <summary> Returns a string of informations about the DataBlkInt.
        /// 
        /// </summary>
        public override string ToString()
        {
            var str = base.ToString();
            if (data != null)
            {
                str += $",data={data.Length} bytes";
            }
            return str;
        }
    }
}