/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>
using System;
using image_BlkImgDataSrc = CoreJ2K.j2k.image.BlkImgDataSrc;
using image_DataBlk = CoreJ2K.j2k.image.DataBlk;

namespace CoreJ2K.Color
{

    /// <summary> This class provides Enumerated ColorSpace API for the jj2000.j2k imaging chain
    /// by implementing the BlkImgDataSrc interface, in particular the getCompData
    /// and getInternCompData methods.
    /// 
    /// </summary>
    /// <seealso cref="j2k.colorspace.ColorSpace" />
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public class EnumeratedColorSpaceMapper : ColorSpaceMapper
    {
        /// <summary> Factory method for creating instances of this class.</summary>
        /// <param name="src">-- source of image data
        /// </param>
        /// <param name="csMap">-- provides colorspace info
        /// </param>
        /// <returns> EnumeratedColorSpaceMapper instance
        /// </returns>
        public new static image_BlkImgDataSrc createInstance(image_BlkImgDataSrc src, ColorSpace csMap)
        {
            return new EnumeratedColorSpaceMapper(src, csMap);
        }

        /// <summary> Ctor which creates an ICCProfile for the image and initializes
        /// all data objects (input, working, and output).
        /// 
        /// </summary>
        /// <param name="src">-- Source of image data
        /// </param>
        /// <param name="csm">-- provides colorspace info
        /// </param>
        protected internal EnumeratedColorSpaceMapper(image_BlkImgDataSrc src, ColorSpace csMap) : base(src, csMap)
        {
        }


        /// <summary> Returns, in the blk argument, a block of image data containing the
        /// specifed rectangular area, in the specified component. The data is
        /// returned, as a copy of the internal data, therefore the returned data
        /// can be modified "in place".
        /// 
        /// The rectangular area to return is specified by the 'ulx', 'uly', 'w'
        /// and 'h' members of the 'blk' argument, relative to the current
        /// tile. These members are not modified by this method. The 'offset' of
        /// the returned data is 0, and the 'scanw' is the same as the block's
        /// width. See the 'DataBlk' class.
        /// 
        /// If the data array in 'blk' is 'null', then a new one is created. If
        /// the data array is not 'null' then it is reused, and it must be large
        /// enough to contain the block's data. Otherwise an 'ArrayStoreException'
        /// or an 'IndexOutOfBoundsException' is thrown by the Java system.
        /// 
        /// The returned data has its 'progressive' attribute set to that of the
        /// input data.
        /// 
        /// </summary>
        /// <param name="blk">Its coordinates and dimensions specify the area to
        /// return. If it contains a non-null data array, then it must have the
        /// correct dimensions. If it contains a null data array a new one is
        /// created. The fields in this object are modified to return the data.
        /// 
        /// </param>
        /// <param name="c">The index of the component from which to get the data. Only 0
        /// and 3 are valid.
        /// 
        /// </param>
        /// <returns> The requested DataBlk
        /// 
        /// </returns>
        /// <seealso cref="GetInternCompData" />
        public override image_DataBlk GetCompData(image_DataBlk output, int c)
        {
            return src.GetCompData(output, c);
        }

        /// <summary> Returns, in the blk argument, a block of image data containing the
        /// specifed rectangular area, in the specified component. The data is
        /// returned, as a reference to the internal data, if any, instead of as a
        /// copy, therefore the returned data should not be modified.
        /// 
        /// The rectangular area to return is specified by the 'ulx', 'uly', 'w'
        /// and 'h' members of the 'blk' argument, relative to the current
        /// tile. These members are not modified by this method. The 'offset' and
        /// 'scanw' of the returned data can be arbitrary. See the 'DataBlk' class.
        /// 
        /// This method, in general, is more efficient than the 'getCompData()'
        /// method since it may not copy the data. However if the array of returned
        /// data is to be modified by the caller then the other method is probably
        /// preferable.
        /// 
        /// If possible, the data in the returned 'DataBlk' should be the
        /// internal data itself, instead of a copy, in order to increase the data
        /// transfer efficiency. However, this depends on the particular
        /// implementation (it may be more convenient to just return a copy of the
        /// data). This is the reason why the returned data should not be modified.
        /// 
        /// If the data array in <tt>blk</tt> is <tt>null</tt>, then a new one
        /// is created if necessary. The implementation of this interface may
        /// choose to return the same array or a new one, depending on what is more
        /// efficient. Therefore, the data array in <tt>blk</tt> prior to the
        /// method call should not be considered to contain the returned data, a
        /// new array may have been created. Instead, get the array from
        /// <tt>blk</tt> after the method has returned.
        /// 
        /// The returned data may have its 'progressive' attribute set. In this
        /// case the returned data is only an approximation of the "final" data.
        /// 
        /// </summary>
        /// <param name="blk">Its coordinates and dimensions specify the area to return,
        /// relative to the current tile. Some fields in this object are modified
        /// to return the data.
        /// 
        /// </param>
        /// <param name="compIndex">The index of the component from which to get the data.
        /// 
        /// </param>
        /// <returns> The requested DataBlk
        /// 
        /// </returns>
        /// <seealso cref="GetCompData" />
        public override image_DataBlk GetInternCompData(image_DataBlk output, int compIndex)
        {
            return src.GetInternCompData(output, compIndex);
        }



        public override string ToString()
        {
            int i;
            var rep_nComps = new System.Text.StringBuilder("ncomps= ").Append(Convert.ToString(ncomps));

            var rep_fixedValue = new System.Text.StringBuilder("fixedPointBits= (");
            var rep_shiftValue = new System.Text.StringBuilder("shiftValue= (");
            var rep_maxValue = new System.Text.StringBuilder("maxValue= (");

            for (i = 0; i < ncomps; ++i)
            {
                if (i != 0)
                {
                    rep_shiftValue.Append(", ");
                    rep_maxValue.Append(", ");
                    rep_fixedValue.Append(", ");
                }
                rep_shiftValue.Append(Convert.ToString(shiftValueArray[i]));
                rep_maxValue.Append(Convert.ToString(maxValueArray[i]));
                rep_fixedValue.Append(Convert.ToString(fixedPtBitsArray[i]));
            }

            rep_shiftValue.Append(")");
            rep_maxValue.Append(")");
            rep_fixedValue.Append(")");

            var rep = new System.Text.StringBuilder("[EnumeratedColorSpaceMapper ");
            rep.Append(rep_nComps);
            rep.Append(Environment.NewLine).Append("  ").Append(rep_shiftValue);
            rep.Append(Environment.NewLine).Append("  ").Append(rep_maxValue);
            rep.Append(Environment.NewLine).Append("  ").Append(rep_fixedValue);

            return rep.Append("]").ToString();
        }
    }
}