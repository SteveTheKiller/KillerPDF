using CoreJ2K.j2k.image;
/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>
using System;

namespace CoreJ2K.Color
{

    /// <summary> This class is responsible for the mapping between
    /// requested components and image channels.
    /// 
    /// </summary>
    /// <seealso cref="j2k.colorspace.ColorSpace" />
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public class ChannelDefinitionMapper : ColorSpaceMapper
    {
        // Channel definition mapping cached at construction; csMap.GetChannelDefinition
        // calls into the cdef box on every invocation and is called per-row per-component.
        private readonly int[] _channelDef;
        /// <summary> Factory method for creating instances of this class.</summary>
        /// <param name="src">-- source of image data
        /// </param>
        /// <param name="csMap">-- provides colorspace info
        /// </param>
        /// <returns> ChannelDefinitionMapper instance
        /// </returns>
        /// <exception cref="ColorSpaceException">
        /// </exception>
        public new static BlkImgDataSrc createInstance(BlkImgDataSrc src, ColorSpace csMap)
        {

            return new ChannelDefinitionMapper(src, csMap);
        }

        /// <summary> Ctor which creates an ICCProfile for the image and initializes
        /// all data objects (input, working, and output).
        /// 
        /// </summary>
        /// <param name="src">-- Source of image data
        /// </param>
        /// <param name="csm">-- provides colorspace info
        /// </param>
        protected internal ChannelDefinitionMapper(BlkImgDataSrc src, ColorSpace csMap) : base(src, csMap)
        {
            _channelDef = new int[ncomps];
            for (var i = 0; i < ncomps; i++)
                _channelDef[i] = csMap.GetChannelDefinition(i);
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
        public override DataBlk GetCompData(DataBlk output, int c)
        {
            return src.GetCompData(output, _channelDef[c]);
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
        /// This method, in general, is less efficient than the
        /// 'getInternCompData()' method since, in general, it copies the
        /// data. However if the array of returned data is to be modified by the
        /// caller then this method is preferable.
        /// 
        /// If the data array in 'blk' is 'null', then a new one is created. If
        /// the data array is not 'null' then it is reused, and it must be large
        /// enough to contain the block's data. Otherwise an 'ArrayStoreException'
        /// or an 'IndexOutOfBoundsException' is thrown by the Java system.
        /// 
        /// The returned data may have its 'progressive' attribute set. In this
        /// case the returned data is only an approximation of the "final" data.
        /// 
        /// </summary>
        /// <param name="blk">Its coordinates and dimensions specify the area to return,
        /// relative to the current tile. If it contains a non-null data array,
        /// then it must be large enough. If it contains a null data array a new
        /// one is created. Some fields in this object are modified to return the
        /// data.
        /// 
        /// </param>
        /// <param name="compIndex">The index of the component from which to get the data.
        /// 
        /// </param>
        /// <seealso cref="GetCompData" />
        public override DataBlk GetInternCompData(DataBlk output, int compIndex)
        {
            return src.GetInternCompData(output, _channelDef[compIndex]);
        }

        /// <summary> Returns the number of bits, referred to as the "range bits",
        /// corresponding to the nominal range of the data in the specified
        /// component. If this number is <i>b</b> then for unsigned data the
        /// nominal range is between 0 and 2^b-1, and for signed data it is between
        /// -2^(b-1) and 2^(b-1)-1. For floating point data this value is not
        /// applicable.
        /// 
        /// </summary>
        /// <param name="compIndex">The index of the component.
        /// 
        /// </param>
        /// <returns> The number of bits corresponding to the nominal range of the
        /// data. Fro floating-point data this value is not applicable and the
        /// return value is undefined.
        /// </returns>
        public override int GetFixedPoint(int compIndex)
        {
            return src.GetFixedPoint(_channelDef[compIndex]);
        }

        public override int GetNomRangeBits(int compIndex)
        {
            return src.GetNomRangeBits(_channelDef[compIndex]);
        }

        public override int GetCompImgHeight(int c)
        {
            return src.GetCompImgHeight(_channelDef[c]);
        }

        public override int GetCompImgWidth(int c)
        {
            return src.GetCompImgWidth(_channelDef[c]);
        }

        public override int GetCompSubsX(int c)
        {
            return src.GetCompSubsX(_channelDef[c]);
        }

        public override int GetCompSubsY(int c)
        {
            return src.GetCompSubsY(_channelDef[c]);
        }

        public override int GetCompULX(int c)
        {
            return src.GetCompULX(_channelDef[c]);
        }

        public override int GetCompULY(int c)
        {
            return src.GetCompULY(_channelDef[c]);
        }

        public override int GetTileCompHeight(int t, int c)
        {
            return src.GetTileCompHeight(t, _channelDef[c]);
        }

        public override int GetTileCompWidth(int t, int c)
        {
            return src.GetTileCompWidth(t, _channelDef[c]);
        }

        public override string ToString()
        {
            int i;
            var rep = new System.Text.StringBuilder("[ChannelDefinitionMapper nchannels= ").Append(ncomps);

            for (i = 0; i < ncomps; ++i)
            {
                rep.Append(Environment.NewLine).Append("  component[").Append(i).Append("] mapped to channel[").Append(_channelDef[i]).Append("]");
            }

            return rep.Append("]").ToString();
        }
    }
}