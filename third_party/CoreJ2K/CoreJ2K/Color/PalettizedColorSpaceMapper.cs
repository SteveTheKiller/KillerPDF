using CoreJ2K.Color.Boxes;
using CoreJ2K.j2k.image;
using CoreJ2K.j2k.util;
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

    /// <summary> This class provides decoding of images with palettized colorspaces.
    /// Here each sample in the input is treated as an index into a color
    /// palette of triplet sRGB output values.
    /// 
    /// </summary>
    /// <seealso cref="j2k.colorspace.ColorSpace" />
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public class PalettizedColorSpaceMapper : ColorSpaceMapper
    {
        /// <summary> Returns the number of components in the image.
        /// 
        /// </summary>
        /// <returns> The number of components in the image.
        /// </returns>
        public override int NumComps => pbox?.NumColumns ?? src.NumComps;

        internal int[] outShiftValueArray = null!;
        internal int srcChannel = 0;

        /// <summary>Access to the palette box information. </summary>
        private readonly PaletteBox pbox;

        /// <summary> Factory method for creating instances of this class.</summary>
        /// <param name="src">-- source of image data
        /// </param>
        /// <param name="csMap">-- provides colorspace info
        /// </param>
        /// <returns> PalettizedColorSpaceMapper instance
        /// </returns>
        public new static BlkImgDataSrc createInstance(BlkImgDataSrc src, ColorSpace csMap)
        {
            return new PalettizedColorSpaceMapper(src, csMap);
        }

        /// <summary> Ctor which creates an ICCProfile for the image and initializes
        /// all data objects (input, working, and output).
        /// 
        /// </summary>
        /// <param name="src">-- Source of image data
        /// </param>
        /// <param name="csm">-- provides colorspace info
        /// </param>
        protected internal PalettizedColorSpaceMapper(BlkImgDataSrc src, ColorSpace csMap) : base(src, csMap)
        {
            pbox = csMap.PaletteBox;
            initialize();
        }

        /// <summary>General utility used by ctors </summary>
        private void initialize()
        {
            if (ncomps != 1 && ncomps != 3 && ncomps != 4)
                throw new ColorSpaceException($"wrong number of components ({ncomps}) for palettized image");

            var outComps = NumComps;
            outShiftValueArray = new int[outComps];

            for (var i = 0; i < outComps; i++)
            {
                outShiftValueArray[i] = 1 << (GetNomRangeBits(i) - 1);
            }
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

            if (pbox == null)
                return src.GetCompData(output, c);

            if (ncomps != 1)
            {
                var msg =
                    $"PalettizedColorSpaceMapper: color palette _not_ applied, incorrect number ({Convert.ToString(ncomps)}) of components";
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING, msg);
                return src.GetCompData(output, c);
            }

            // Initialize general input and output indexes
            var leftedgeOut = -1; // offset to the start of the output scanline
            var rightedgeOut = -1; // offset to the end of the output
                                   // scanline + 1
            var leftedgeIn = -1; // offset to the start of the input scanline  
            var rightedgeIn = -1; // offset to the end of the input
                                  // scanline + 1
            var kOut = -1;
            var kIn = -1;

            // Assure a properly sized data buffer for output.
            InternalBuffer = output;

            switch (output.DataType)
            {

                // Int and Float data only
                case DataBlk.TYPE_INT:

                    copyGeometry(inInt[0], output);

                    // Request data from the source.        
                    inInt[0] = (DataBlkInt)src.GetInternCompData(inInt[0], 0);
                    dataInt[0] = (int[])inInt[0].Data!;
                    var outdataInt = ((DataBlkInt)output).DataInt;

                    // The nitty-gritty.

                    for (var row = 0; row < output.h; ++row)
                    {
                        leftedgeIn = inInt[0].offset + row * inInt[0].scanw;
                        rightedgeIn = leftedgeIn + inInt[0].w;
                        leftedgeOut = output.offset + row * output.scanw;
                        rightedgeOut = leftedgeOut + output.w;

                        for (kOut = leftedgeOut, kIn = leftedgeIn; kIn < rightedgeIn; ++kIn, ++kOut)
                        {
                            var paletteIndex = dataInt[0][kIn] + shiftValueArray[0];
                            paletteIndex = Math.Max(0, Math.Min(paletteIndex, pbox.NumEntries - 1));
                            outdataInt[kOut] = pbox.GetEntry(paletteIndex, c) - outShiftValueArray[c];
                        }
                    }
                    output.progressive = inInt[0].progressive;
                    break;


                case DataBlk.TYPE_FLOAT:

                    copyGeometry(inFloat[0], output);

                    // Request data from the source.        
                    inFloat[0] = (DataBlkFloat)src.GetInternCompData(inFloat[0], 0);
                    dataFloat[0] = (float[])inFloat[0].Data!;
                    var outdataFloat = ((DataBlkFloat)output).DataFloat;

                    // The nitty-gritty.

                    for (var row = 0; row < output.h; ++row)
                    {
                        leftedgeIn = inFloat[0].offset + row * inFloat[0].scanw;
                        rightedgeIn = leftedgeIn + inFloat[0].w;
                        leftedgeOut = output.offset + row * output.scanw;
                        rightedgeOut = leftedgeOut + output.w;

                        for (kOut = leftedgeOut, kIn = leftedgeIn; kIn < rightedgeIn; ++kIn, ++kOut)
                        {
                            var paletteIndexF = (int)dataFloat[0][kIn] + shiftValueArray[0];
                            paletteIndexF = Math.Max(0, Math.Min(paletteIndexF, pbox.NumEntries - 1));
                            outdataFloat[kOut] = pbox.GetEntry(paletteIndexF, c) - outShiftValueArray[c];
                        }
                    }
                    output.progressive = inFloat[0].progressive;
                    break;


                case DataBlk.TYPE_SHORT:
                case DataBlk.TYPE_BYTE:
                default:
                    // Unsupported output type. 
                    throw new ArgumentException("invalid source datablock" + " type");
            }

            // Initialize the output block geometry and set the profiled
            // data into the output block.
            output.offset = 0;
            output.scanw = output.w;
            return output;
        }


        /// <summary>Return a suitable String representation of the class instance, e.g.
        /// 
        /// [PalettizedColorSpaceMapper 
        /// ncomps= 3, scomp= 1, nentries= 1024
        /// column=0, 7 bit signed entry
        /// column=1, 7 bit unsigned entry
        /// column=2, 7 bit signed entry]
        /// 
        /// 
        /// </summary>
        public override string ToString()
        {

            int c;
            var rep = new System.Text.StringBuilder("[PalettizedColorSpaceMapper ");
            var body = new System.Text.StringBuilder($"  {Environment.NewLine}");

            if (pbox != null)
            {
                body.Append("ncomps= ").Append(NumComps).Append(", scomp= ").Append(srcChannel);
                for (c = 0; c < NumComps; ++c)
                {
                    body.Append(Environment.NewLine).Append("column= ").Append(c).Append(", ").Append(pbox.GetBitDepth(c)).Append(" bit ").Append(pbox.IsSigned(c) ? "signed entry" : "unsigned entry");
                }
            }
            else
            {
                body.Append("image does not contain a palette box");
            }

            rep.Append(ColorSpace.indent("  ", body));
            return rep.Append("]").ToString();
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
        /// <seealso cref="GetCompData">
        /// </seealso>
        public override DataBlk GetInternCompData(DataBlk output, int compIndex)
        {
            return GetCompData(output, compIndex);
        }

        /// <summary> Returns the number of bits, referred to as the "range bits",
        /// corresponding to the nominal range of the image data in the specified
        /// component. If this number is <i>n</i> then for unsigned data the
        /// nominal range is between 0 and 2^b-1, and for signed data it is between
        /// -2^(b-1) and 2^(b-1)-1. In the case of transformed data which is not in
        /// the image domain (e.g., wavelet coefficients), this method returns the
        /// "range bits" of the image data that generated the coefficients.
        /// 
        /// </summary>
        /// <param name="compIndex">The index of the component.
        /// 
        /// </param>
        /// <returns> The number of bits corresponding to the nominal range of the
        /// image data (in the image domain).
        /// </returns>
        public override int GetNomRangeBits(int compIndex)
        {
            return pbox?.GetBitDepth(compIndex) ?? src.GetNomRangeBits(compIndex);
        }

        public override int GetFixedPoint(int compIndex)
        {
            return src.GetFixedPoint(pbox != null ? srcChannel : compIndex);
        }


        /// <summary> Returns the component subsampling factor in the horizontal direction,
        /// for the specified component. This is, approximately, the ratio of
        /// dimensions between the reference grid and the component itself, see the
        /// 'ImgData' interface desription for details.
        /// 
        /// </summary>
        /// <param name="c">The index of the component (between 0 and N-1)
        /// 
        /// </param>
        /// <returns> The horizontal subsampling factor of component 'c'
        /// 
        /// </returns>
        /// <seealso cref="j2k.image.ImgData" />
        public override int GetCompSubsX(int c)
        {
            return imgdatasrc.GetCompSubsX(srcChannel);
        }

        /// <summary> Returns the component subsampling factor in the vertical direction, for
        /// the specified component. This is, approximately, the ratio of
        /// dimensions between the reference grid and the component itself, see the
        /// 'ImgData' interface desription for details.
        /// 
        /// </summary>
        /// <param name="c">The index of the component (between 0 and N-1)
        /// 
        /// </param>
        /// <returns> The vertical subsampling factor of component 'c'
        /// 
        /// </returns>
        /// <seealso cref="j2k.image.ImgData" />
        public override int GetCompSubsY(int c)
        {
            return imgdatasrc.GetCompSubsY(srcChannel);
        }

        /// <summary> Returns the width in pixels of the specified tile-component
        /// 
        /// </summary>
        /// <param name="t">Tile index
        /// 
        /// </param>
        /// <param name="c">The index of the component, from 0 to N-1.
        /// 
        /// </param>
        /// <returns> The width in pixels of component <tt>c</tt> in tile<tt>t</tt>.
        /// 
        /// </returns>
        public override int GetTileCompWidth(int t, int c)
        {
            return imgdatasrc.GetTileCompWidth(t, srcChannel);
        }

        /// <summary> Returns the height in pixels of the specified tile-component.
        /// 
        /// </summary>
        /// <param name="t">The tile index.
        /// 
        /// </param>
        /// <param name="c">The index of the component, from 0 to N-1.
        /// 
        /// </param>
        /// <returns> The height in pixels of component <tt>c</tt> in tile
        /// <tt>t</tt>.
        /// 
        /// </returns>
        public override int GetTileCompHeight(int t, int c)
        {
            return imgdatasrc.GetTileCompHeight(t, srcChannel);
        }

        /// <summary> Returns the width in pixels of the specified component in the overall
        /// image.
        /// 
        /// </summary>
        /// <param name="c">The index of the component, from 0 to N-1.
        /// 
        /// </param>
        /// <returns> The width in pixels of component <tt>c</tt> in the overall
        /// image.
        /// 
        /// </returns>
        public override int GetCompImgWidth(int c)
        {
            return imgdatasrc.GetCompImgWidth(srcChannel);
        }


        /// <summary> Returns the number of bits, referred to as the "range bits",
        /// corresponding to the nominal range of the image data in the specified
        /// component. If this number is <i>n</b> then for unsigned data the
        /// nominal range is between 0 and 2^b-1, and for signed data it is between
        /// -2^(b-1) and 2^(b-1)-1. In the case of transformed data which is not in
        /// the image domain (e.g., wavelet coefficients), this method returns the
        /// "range bits" of the image data that generated the coefficients.
        /// 
        /// </summary>
        /// <param name="c">The index of the component.
        /// 
        /// </param>
        /// <returns> The number of bits corresponding to the nominal range of the
        /// image data (in the image domain).
        /// 
        /// </returns>
        public override int GetCompImgHeight(int c)
        {
            return imgdatasrc.GetCompImgHeight(srcChannel);
        }

        /// <summary> Returns the horizontal coordinate of the upper-left corner of the
        /// specified component in the current tile.
        /// 
        /// </summary>
        /// <param name="c">The index of the component.
        /// 
        /// </param>
        public override int GetCompULX(int c)
        {
            return imgdatasrc.GetCompULX(srcChannel);
        }

        /// <summary> Returns the vertical coordinate of the upper-left corner of the
        /// specified component in the current tile.
        /// 
        /// </summary>
        /// <param name="c">The index of the component.
        /// 
        /// </param>
        public override int GetCompULY(int c)
        {
            return imgdatasrc.GetCompULY(srcChannel);
        }
    }
}