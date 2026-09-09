/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>
using System;
using DataBlk = CoreJ2K.j2k.image.DataBlk;
using FacilityManager = CoreJ2K.j2k.util.FacilityManager;
using image_BlkImgDataSrc = CoreJ2K.j2k.image.BlkImgDataSrc;
using image_DataBlk = CoreJ2K.j2k.image.DataBlk;
using image_DataBlkFloat = CoreJ2K.j2k.image.DataBlkFloat;
using image_DataBlkInt = CoreJ2K.j2k.image.DataBlkInt;

namespace CoreJ2K.Color
{


    /// <summary> This decodes maps which are defined in the sYCC 
    /// colorspace into the sRGB colorspadce.
    /// 
    /// </summary>
    /// <seealso cref="j2k.colorspace.ColorSpace" />
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public class SYccColorSpaceMapper : ColorSpaceMapper
    {
        /// <summary>Matrix component for ycc transform. </summary>
        /* sYCC colorspace matrix */

        protected internal static float Matrix00 = 1;
        /// <summary>Matrix component for ycc transform. </summary>
        protected internal static float Matrix01 = 0;
        /// <summary>Matrix component for ycc transform. </summary>
        protected internal static float Matrix02 = (float)1.402;
        /// <summary>Matrix component for ycc transform. </summary>
        protected internal static float Matrix10 = 1;
        /// <summary>Matrix component for ycc transform. </summary>
        protected internal static float Matrix11 = (float)(-0.34413);
        /// <summary>Matrix component for ycc transform. </summary>
        protected internal static float Matrix12 = (float)(-0.71414);
        /// <summary>Matrix component for ycc transform. </summary>
        protected internal static float Matrix20 = 1;
        /// <summary>Matrix component for ycc transform. </summary>
        protected internal static float Matrix21 = (float)1.772;
        /// <summary>Matrix component for ycc transform. </summary>
        protected internal static float Matrix22 = 0;


        /// <summary> Factory method for creating instances of this class.</summary>
        /// <param name="src">-- source of image data
        /// </param>
        /// <param name="csMap">-- provides colorspace info
        /// </param>
        /// <returns> SYccColorSpaceMapper instance
        /// </returns>
        public new static image_BlkImgDataSrc createInstance(image_BlkImgDataSrc src, ColorSpace csMap)
        {
            return new SYccColorSpaceMapper(src, csMap);
        }

        /// <summary> Ctor which creates an ICCProfile for the image and initializes
        /// all data objects (input, working, and output).
        /// 
        /// </summary>
        /// <param name="src">-- Source of image data
        /// </param>
        /// <param name="csm">-- provides colorspace info
        /// </param>
        protected internal SYccColorSpaceMapper(image_BlkImgDataSrc src, ColorSpace csMap) : base(src, csMap)
        {
            initialize();
        }

        /// <summary>General utility used by ctors </summary>
        private void initialize()
        {
            if (ncomps != 1 && ncomps != 3)
            {
                var msg = $"SYccColorSpaceMapper: ycc transformation _not_ applied to {ncomps} component image";
                FacilityManager.GetMsgLogger().printmsg(j2k.util.MsgLogger_Fields.ERROR, msg);
                throw new ColorSpaceException(msg);
            }

            workInt = new image_DataBlkInt[ncomps];
            workFloat = new image_DataBlkFloat[ncomps];
            workDataInt = new int[ncomps][];
            workDataFloat = new float[ncomps][];
            for (var i = 0; i < ncomps; ++i)
            {
                workInt[i] = new image_DataBlkInt();
                workFloat[i] = new image_DataBlkFloat();
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
        /// </returns>
        /// <seealso cref="GetInternCompData" />
        public override image_DataBlk GetCompData(image_DataBlk outblk, int c)
        {

            var type = outblk.DataType;

            int i; // j removed

            // Calculate all components:
            for (i = 0; i < ncomps; ++i)
            {

                // Set up the working DataBlk geometry.
                copyGeometry(workInt[i], outblk);
                copyGeometry(workFloat[i], outblk);
                copyGeometry(inInt[i], outblk);
                copyGeometry(inFloat[i], outblk);

                // Request data from the source.
                inInt[i] = (image_DataBlkInt)src.GetInternCompData(inInt[i], i);
            }

            if (type == image_DataBlk.TYPE_INT)
            {
                if (ncomps == 1)
                    workInt[c] = inInt[c];
                else
                    workInt = mult(inInt);
                outblk.progressive = inInt[c].progressive;
                outblk.Data = workInt[c].Data;
            }

            if (type == DataBlk.TYPE_FLOAT)
            {
                if (ncomps == 1)
                    workFloat[c] = inFloat[c];
                else
                    workFloat = mult(inFloat);
                outblk.progressive = inFloat[c].progressive;
                outblk.Data = workFloat[c].Data;
            }


            // Initialize the output block geometry and set the profiled
            // data into the output block.
            outblk.offset = 0;
            outblk.scanw = outblk.w;

            return outblk;
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
        public override image_DataBlk GetInternCompData(image_DataBlk output, int compIndex)
        {
            return GetCompData(output, compIndex);
        }


        /// <summary> Output a DataBlkFloat array where each sample in each component
        /// is the product of the YCC matrix * the vector of samples across 
        /// the input components.
        /// </summary>
        /// <param name="inblk">input DataBlkFloat array
        /// </param>
        /// <returns> output DataBlkFloat array
        /// </returns>
        private static image_DataBlkFloat[] mult(image_DataBlkFloat[] inblk)
        {

            if (inblk.Length != 3)
                throw new ArgumentException("bad input array size");

            int i, j;
            var length = inblk[0].h * inblk[0].w;
            var outblk = new image_DataBlkFloat[3];
            var output = new float[3][];
            var inStream = new float[3][];

            for (i = 0; i < 3; ++i)
            {
                inStream[i] = inblk[i].DataFloat;
                outblk[i] = new image_DataBlkFloat();
                copyGeometry(outblk[i], inblk[i]);
                outblk[i].offset = inblk[i].offset;
                output[i] = new float[length];
                outblk[i].Data = output[i];
            }

            for (j = 0; j < length; ++j)
            {
                output[0][j] = (Matrix00 * inStream[0][inblk[0].offset + j] + Matrix01 * inStream[1][inblk[1].offset + j] + Matrix02 * inStream[2][inblk[2].offset + j]);

                output[1][j] = (Matrix10 * inStream[0][inblk[0].offset + j] + Matrix11 * inStream[1][inblk[1].offset + j] + Matrix12 * inStream[2][inblk[2].offset + j]);

                output[2][j] = (Matrix20 * inStream[0][inblk[0].offset + j] + Matrix21 * inStream[1][inblk[1].offset + j] + Matrix22 * inStream[2][inblk[2].offset + j]);
            }

            return outblk;
        }



        /// <summary> Output a DataBlkInt array where each sample in each component
        /// is the product of the YCC matrix * the vector of samples across 
        /// the input components.
        /// </summary>
        /// <param name="inblk">input DataBlkInt array
        /// </param>
        /// <returns> output DataBlkInt array
        /// </returns>
        private static image_DataBlkInt[] mult(image_DataBlkInt[] inblk)
        {

            if (inblk.Length != 3)
                throw new ArgumentException("bad input array size");


            int i, j;
            var length = inblk[0].h * inblk[0].w;
            var outblk = new image_DataBlkInt[3];
            var output = new int[3][];
            var inStream = new int[3][];

            for (i = 0; i < 3; ++i)
            {
                inStream[i] = inblk[i].DataInt;
                outblk[i] = new image_DataBlkInt();
                copyGeometry(outblk[i], inblk[i]);
                outblk[i].offset = inblk[i].offset;
                output[i] = new int[length];
                outblk[i].Data = output[i];
            }

            for (j = 0; j < length; ++j)
            {
                output[0][j] = (int)(Matrix00 * inStream[0][inblk[0].offset + j] + Matrix01 * inStream[1][inblk[1].offset + j] + Matrix02 * inStream[2][inblk[2].offset + j]);

                output[1][j] = (int)(Matrix10 * inStream[0][inblk[0].offset + j] + Matrix11 * inStream[1][inblk[1].offset + j] + Matrix12 * inStream[2][inblk[2].offset + j]);

                output[2][j] = (int)(Matrix20 * inStream[0][inblk[0].offset + j] + Matrix21 * inStream[1][inblk[1].offset + j] + Matrix22 * inStream[2][inblk[2].offset + j]);
            }

            return outblk;
        }


        /// <summary>Return a suitable String representation of the class instance. </summary>
        public override string ToString()
        {
            int i;

            var rep_nComps = new System.Text.StringBuilder("ncomps= ").Append(Convert.ToString(ncomps));
            var rep_comps = new System.Text.StringBuilder();

            for (i = 0; i < ncomps; ++i)
            {
                rep_comps.Append("  ").Append("component[").Append(Convert.ToString(i)).Append("] height, width = (").Append(src.GetCompImgHeight(i)).Append(", ").Append(src.GetCompImgWidth(i)).Append(")").Append(Environment.NewLine);
            }

            var rep = new System.Text.StringBuilder("[SYccColorSpaceMapper ");
            rep.Append(rep_nComps).Append(Environment.NewLine);
            rep.Append(rep_comps).Append("  ");

            return rep.Append("]").ToString();
        }
    }
}