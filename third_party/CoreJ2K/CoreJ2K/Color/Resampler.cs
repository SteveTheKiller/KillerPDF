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

    /// <summary> This class resamples the components of an image so that
    /// all have the same number of samples.  The current implementation
    /// only handles the case of 2:1 upsampling.
    /// 
    /// </summary>
    /// <seealso cref="j2k.colorspace.ColorSpace" />
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public class Resampler : ColorSpaceMapper
    {
        private int minCompSubsX;
        private int minCompSubsY;
        private int maxCompSubsX;
        private int maxCompSubsY;

        // Per-component subsampling factors cached at construction;
        // these come from the SIZ marker and are image-level constants.
        private readonly int[] _subsX;
        private readonly int[] _subsY;

        internal int wspan = 0;
        internal int hspan = 0;

        /// <summary> Factory method for creating instances of this class.</summary>
        /// <param name="src">-- source of image data
        /// </param>
        /// <param name="csMap">-- provides colorspace info
        /// </param>
        /// <returns> Resampler instance
        /// </returns>
        public new static BlkImgDataSrc createInstance(BlkImgDataSrc src, ColorSpace csMap)
        {
            return new Resampler(src, csMap);
        }

        /// <summary> Ctor resamples a BlkImgDataSrc so that all components
        /// have the same number of samples.
        /// 
        /// Note the present implementation does only two to one
        /// respampling in either direction (row, column).
        /// 
        /// </summary>
        /// <param name="src">-- Source of image data
        /// </param>
        /// <param name="csm">-- provides colorspace info
        /// </param>
        protected internal Resampler(BlkImgDataSrc src, ColorSpace csMap) : base(src, csMap)
        {

            int c;

            // Cache per-component subsampling factors; these are image-level
            // constants read from the SIZ marker and never change after construction.
            _subsX = new int[ncomps];
            _subsY = new int[ncomps];
            for (c = 0; c < ncomps; ++c)
            {
                _subsX[c] = src.GetCompSubsX(c);
                _subsY[c] = src.GetCompSubsY(c);
            }

            // Calculate the minimum and maximum subsampling factor
            // across all channels.

            var minX = _subsX[0];
            var minY = _subsY[0];
            var maxX = minX;
            var maxY = minY;

            for (c = 1; c < ncomps; ++c)
            {
                minX = Math.Min(minX, _subsX[c]);
                minY = Math.Min(minY, _subsY[c]);
                maxX = Math.Max(maxX, _subsX[c]);
                maxY = Math.Max(maxY, _subsY[c]);
            }

            // Throw an exception for other than 2:1 sampling.
            if ((maxX != 1 && maxX != 2) || (maxY != 1 && maxY != 2))
            {
                throw new ColorSpaceException("Upsampling by other than 2:1 not" + " supported");
            }

            minCompSubsX = minX;
            minCompSubsY = minY;
            maxCompSubsX = maxX;
            maxCompSubsY = maxY;
        }

        /// <summary> Return a DataBlk containing the requested component
        /// upsampled by the scale factor applied to the particular
        /// scaling direction
        /// 
        /// Returns, in the blk argument, a block of image data containing the
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
        /// <param name="compIndex">The index of the component from which to get the data. Only 0
        /// and 3 are valid.
        /// 
        /// </param>
        /// <returns> The requested DataBlk
        /// 
        /// </returns>
        /// <seealso cref="GetCompData">
        /// </seealso>
        public override DataBlk GetInternCompData(DataBlk outblk, int compIndex)
        {

            // Fast path: both subsample factors are 1 — pass straight through.
            if (_subsX[compIndex] == 1 && _subsY[compIndex] == 1)
                return src.GetInternCompData(outblk, compIndex);

            var wfactor = _subsX[compIndex];
            var hfactor = _subsY[compIndex];
            if ((wfactor != 2 && wfactor != 1) || (hfactor != 2 && hfactor != 1))
                throw new ArgumentException("Upsampling by other than 2:1" + " not supported");

            var leftedgeOut = -1; // offset to the start of the output scanline
            var rightedgeOut = -1; // offset to the end of the output
                                   // scanline + 1
            var leftedgeIn = -1; // offset to the start of the input scanline  
            var rightedgeIn = -1; // offset to the end of the input scanline + 1


            int y0In, y1In, y0Out, y1Out;
            int x0In, x1In, x0Out, x1Out;

            y0Out = outblk.uly;
            y1Out = y0Out + outblk.h - 1;

            x0Out = outblk.ulx;
            x1Out = x0Out + outblk.w - 1;

            y0In = y0Out / hfactor;
            y1In = y1Out / hfactor;

            x0In = x0Out / wfactor;
            x1In = x1Out / wfactor;


            // Calculate the requested height and width, requesting an extra
            // row and or for upsampled channels.
            var reqW = x1In - x0In + 1;
            var reqH = y1In - y0In + 1;

            // Initialize general input and output indexes
            var kOut = -1;
            var kIn = -1;
            int yIn;

            switch (outblk.DataType)
            {


                case DataBlk.TYPE_INT:

                    var inblkInt = inInt[compIndex];
                    inblkInt.ulx = x0In; inblkInt.uly = y0In; inblkInt.w = reqW; inblkInt.h = reqH;
                    inblkInt = (DataBlkInt)src.GetInternCompData(inblkInt, compIndex);
                    dataInt[compIndex] = inblkInt.DataInt;

                    // Reference the working array   
                    var outdataInt = (int[])outblk.Data!;

                    // Create data array if necessary
                    if (outdataInt == null || outdataInt.Length != outblk.w * outblk.h)
                    {
                        outdataInt = new int[outblk.h * outblk.w];
                        outblk.Data = outdataInt;
                    }

                    // The nitty-gritty.

                    for (var yOut = y0Out; yOut <= y1Out; ++yOut)
                    {

                        yIn = yOut / hfactor;


                        leftedgeIn = inblkInt.offset + (yIn - y0In) * inblkInt.scanw;
                        rightedgeIn = leftedgeIn + inblkInt.w;
                        leftedgeOut = outblk.offset + (yOut - y0Out) * outblk.scanw;
                        rightedgeOut = leftedgeOut + outblk.w;

                        kIn = leftedgeIn;
                        kOut = leftedgeOut;

                        if ((x0Out & 0x1) == 1)
                        {
                            // first is odd do the pixel once.
                            outdataInt[kOut++] = dataInt[compIndex][kIn++];
                        }

                        if ((x1Out & 0x1) == 0)
                        {
                            // last is even adjust loop bounds
                            rightedgeOut--;
                        }

                        while (kOut < rightedgeOut)
                        {
                            outdataInt[kOut++] = dataInt[compIndex][kIn];
                            outdataInt[kOut++] = dataInt[compIndex][kIn++];
                        }

                        if ((x1Out & 0x1) == 0)
                        {
                            // last is even do the pixel once.
                            outdataInt[kOut++] = dataInt[compIndex][kIn];
                        }
                    }

                    outblk.progressive = inblkInt.progressive;
                    break;


                case DataBlk.TYPE_FLOAT:

                    var inblkFloat = inFloat[compIndex];
                    inblkFloat.ulx = x0In; inblkFloat.uly = y0In; inblkFloat.w = reqW; inblkFloat.h = reqH;
                    inblkFloat = (DataBlkFloat)src.GetInternCompData(inblkFloat, compIndex);
                    dataFloat[compIndex] = inblkFloat.DataFloat;

                    // Reference the working array   
                    var outdataFloat = (float[])outblk.Data!;

                    // Create data array if necessary
                    if (outdataFloat == null || outdataFloat.Length != outblk.w * outblk.h)
                    {
                        outdataFloat = new float[outblk.h * outblk.w];
                        outblk.Data = outdataFloat;
                    }

                    // The nitty-gritty.

                    for (var yOut = y0Out; yOut <= y1Out; ++yOut)
                    {

                        yIn = yOut / hfactor;


                        leftedgeIn = inblkFloat.offset + (yIn - y0In) * inblkFloat.scanw;
                        rightedgeIn = leftedgeIn + inblkFloat.w;
                        leftedgeOut = outblk.offset + (yOut - y0Out) * outblk.scanw;
                        rightedgeOut = leftedgeOut + outblk.w;

                        kIn = leftedgeIn;
                        kOut = leftedgeOut;

                        if ((x0Out & 0x1) == 1)
                        {
                            // first is odd do the pixel once.
                            outdataFloat[kOut++] = dataFloat[compIndex][kIn++];
                        }

                        if ((x1Out & 0x1) == 0)
                        {
                            // last is even adjust loop bounds
                            rightedgeOut--;
                        }

                        while (kOut < rightedgeOut)
                        {
                            outdataFloat[kOut++] = dataFloat[compIndex][kIn];
                            outdataFloat[kOut++] = dataFloat[compIndex][kIn++];
                        }

                        if ((x1Out & 0x1) == 0)
                        {
                            // last is even do the pixel once.
                            outdataFloat[kOut++] = dataFloat[compIndex][kIn];
                        }
                    }

                    outblk.progressive = inblkFloat.progressive;
                    break;


                case DataBlk.TYPE_SHORT:
                case DataBlk.TYPE_BYTE:
                default:
                    // Unsupported output type. 
                    throw new ArgumentException("invalid source datablock " + "type");
            }

            return outblk;
        }


        /// <summary> Return an appropriate String representation of this Resampler instance.</summary>
        public override string ToString()
        {
            var rep = new System.Text.StringBuilder($"[Resampler: ncomps= {ncomps}");
            var body = new System.Text.StringBuilder("  ");
            for (var i = 0; i < ncomps; ++i)
            {
                body.Append(Environment.NewLine);
                body.Append("comp[");
                body.Append(i);
                body.Append("] xscale= ");
                body.Append(_subsX[i]);
                body.Append(", yscale= ");
                body.Append(_subsY[i]);
            }

            rep.Append(ColorSpace.indent("  ", body));
            return rep.Append("]").ToString();
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
        public override DataBlk GetCompData(DataBlk outblk, int c)
        {
            return GetInternCompData(outblk, c);
        }


        /// <summary> Returns the height in pixels of the specified component in the
        /// overall image.
        /// </summary>
        public override int GetCompImgHeight(int c)
        {
            return src.GetCompImgHeight(c) * _subsY[c];
        }

        /// <summary> Returns the width in pixels of the specified component in the
        /// overall image.
        /// </summary>
        public override int GetCompImgWidth(int c)
        {
            return src.GetCompImgWidth(c) * _subsX[c];
        }

        /// <summary> Returns the component subsampling factor in the horizontal
        /// direction, for the specified component.
        /// </summary>
        public override int GetCompSubsX(int c)
        {
            return 1;
        }

        /// <summary> Returns the component subsampling factor in the vertical
        /// direction, for the specified component.
        /// </summary>
        public override int GetCompSubsY(int c)
        {
            return 1;
        }

        /// <summary> Returns the height in pixels of the specified tile-component.</summary>
        public override int GetTileCompHeight(int t, int c)
        {
            return src.GetTileCompHeight(t, c) * src.GetCompSubsY(c);
        }

        /// <summary> Returns the width in pixels of the specified tile-component..</summary>
        public override int GetTileCompWidth(int t, int c)
        {
            return src.GetTileCompWidth(t, c) * src.GetCompSubsX(c);
        }
    }
}