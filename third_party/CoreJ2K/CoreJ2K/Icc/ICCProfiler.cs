using CoreJ2K.Color;
using CoreJ2K.Icc.Lut;
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

namespace CoreJ2K.Icc
{

    /// <summary> This class provides ICC Profiling API for the jj2000.j2k imaging chain
    /// by implementing the BlkImgDataSrc interface, in particular the getCompData
    /// and getInternCompData methods.
    /// 
    /// </summary>
    /// <seealso cref="j2k.icc.ICCProfile" />
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public class ICCProfiler : ColorSpaceMapper
    {

        /// <summary>The prefix for ICC Profiler options </summary>
        public new const char OPT_PREFIX = 'I';

        // Renamed for convenience:
        private static readonly int GRAY;
        private static readonly int RED;
        private static readonly int GREEN;
        private static readonly int BLUE;

        // ICCProfiles.
        internal RestrictedICCProfile? ricc = null;
        internal ICCProfile? icc = null;

        // Temporary variables needed during profiling.
        private DataBlkInt[]? tempInt; // Holds the results of the transform.
        private DataBlkFloat[]? tempFloat; // Holds the results of the transform.

        private readonly object? xform = null;

        /// <summary>The image's ICC profile. </summary>
        private readonly RestrictedICCProfile? iccp = null;

        /// <summary> Factory method for creating instances of this class.</summary>
        /// <param name="src">-- source of image data
        /// </param>
        /// <param name="csMap">-- provides colorspace info
        /// </param>
        /// <returns> ICCProfiler instance
        /// </returns>
        /// <exception cref="IOException">profile access exception
        /// </exception>
        /// <exception cref="ICCProfileException">profile content exception
        /// </exception>
        public new static BlkImgDataSrc createInstance(BlkImgDataSrc src, ColorSpace csMap)
        {
            return new ICCProfiler(src, csMap);
        }

        /// <summary> Ctor which creates an ICCProfile for the image and initializes
        /// all data objects (input, working, output).
        /// 
        /// </summary>
        /// <param name="src">-- Source of image data
        /// </param>
        /// <param name="csm">-- provides colorspace info
        /// 
        /// </param>
        /// <exception cref="IOException">
        /// </exception>
        /// <exception cref="ICCProfileException">
        /// </exception>
        /// <exception cref="IllegalArgumentException">
        /// </exception>
        protected internal ICCProfiler(BlkImgDataSrc src, ColorSpace csMap) : base(src, csMap)
        {
            initialize();

            iccp = GetICCProfile(csMap);
            if (ncomps == 1)
            {
                xform = new MonochromeTransformTosRGB(iccp, maxValueArray[0], shiftValueArray[0]);
            }
            else
            {
                xform = new MatrixBasedTransformTosRGB(iccp, maxValueArray, shiftValueArray);
            }
        }

		/// <summary>General utility used by ctors </summary>
		private void initialize()
		{
			workInt = new DataBlkInt[ncomps];
			workFloat = new DataBlkFloat[ncomps];
			workDataInt = new int[ncomps][];
			workDataFloat = new float[ncomps][];
			tempInt = new DataBlkInt[ncomps];
			tempFloat = new DataBlkFloat[ncomps];

			/* For each component, get the maximum data value, a reference
			* to the pixel data and set up working and temporary DataBlks
			* for both integer and float output.
			*/
			for (var i = 0; i < ncomps; ++i)
			{
				workInt[i] = new DataBlkInt();
				workFloat[i] = new DataBlkFloat();
				tempInt[i] = new DataBlkInt();
				tempFloat[i] = new DataBlkFloat();
			}
		}

        /// <summary> Get the ICCProfile information JP2 ColorSpace</summary>
        /// <param name="csm">provides all necessary info about the colorspace
        /// </param>
        /// <returns> ICCMatrixBasedInputProfile for 3 component input and
        /// ICCMonochromeInputProfile for a 1 component source.  Returns
        /// null if exceptions were encountered.
        /// </returns>
        /// <exception cref="ColorSpaceException">
        /// </exception>
        /// <exception cref="ICCProfileException">
        /// </exception>
        /// <exception cref="IllegalArgumentException">
        /// </exception>
        private RestrictedICCProfile GetICCProfile(ColorSpace csm)
        {

            switch (ncomps)
            {

                case 1:
                    icc = ICCMonochromeInputProfile.createInstance(csm);
                    ricc = icc.parse();
                    if (ricc.Type != RestrictedICCProfile.kMonochromeInput)
                        throw new ArgumentException("wrong ICCProfile type" + " for image");
                    break;

                case 3:
                    icc = ICCMatrixBasedInputProfile.createInstance(csm);
                    ricc = icc.parse();
                    if (ricc.Type != RestrictedICCProfile.kThreeCompInput)
                        throw new ArgumentException("wrong ICCProfile type" + " for image");
                    break;

                default:
                    throw new ArgumentException($"illegal number of components ({ncomps}) in image");

            }
            return ricc;
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
        /// <param name="out">Its coordinates and dimensions specify the area to
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

            try
            {
                if (ncomps != 1 && ncomps != 3)
                {
                    var msg = $"ICCProfiler: icc profile _not_ applied to {ncomps} component image";
                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING, msg);
                    return src.GetCompData(outblk, c);
                }

                var type = outblk.DataType;

                var leftedgeOut = -1; // offset to the start of the output scanline
                var rightedgeOut = -1; // offset to the end of the output
                                       // scanline + 1
                var leftedgeIn = -1; // offset to the start of the input scanline  
                var rightedgeIn = -1; // offset to the end of the input
                                      // scanline + 1

                // Calculate all components:
                for (var i = 0; i < ncomps; ++i)
                {

                    var fixedPtBits = src.GetFixedPoint(i);
                    var shiftVal = shiftValueArray[i];
                    var maxVal = maxValueArray[i];

                    // Initialize general input and output indexes
                    var kOut = -1;
                    var kIn = -1;

                    switch (type)
                    {

                        // Int and Float data only
                        case DataBlk.TYPE_INT:

                            // Set up the DataBlk geometry
                            copyGeometry(workInt[i], outblk);
                            copyGeometry(tempInt[i], outblk);
                            copyGeometry(inInt[i], outblk);
                            InternalBuffer = outblk;

                            // Reference the output array
                            workDataInt[i] = (int[])workInt[i].Data!;

                            // Request data from the source.    
                            inInt[i] = (DataBlkInt)src.GetInternCompData(inInt[i], i);
                            dataInt[i] = inInt[i].DataInt;

                            // The nitty-gritty.

                            for (var row = 0; row < outblk.h; ++row)
                            {
                                leftedgeIn = inInt[i].offset + row * inInt[i].scanw;
                                rightedgeIn = leftedgeIn + inInt[i].w;
                                leftedgeOut = outblk.offset + row * outblk.scanw;
                                rightedgeOut = leftedgeOut + outblk.w;

                                for (kOut = leftedgeOut, kIn = leftedgeIn; kIn < rightedgeIn; ++kIn, ++kOut)
                                {
                                    var tmpInt = (dataInt[i][kIn] >> fixedPtBits) + shiftVal;
                                    workDataInt[i][kOut] = ((tmpInt < 0) ? 0 : ((tmpInt > maxVal) ? maxVal : tmpInt));
                                }
                            }
                            break;


                        case DataBlk.TYPE_FLOAT:

                            // Set up the DataBlk geometry
                            copyGeometry(workFloat[i], outblk);
                            copyGeometry(tempFloat[i], outblk);
                            copyGeometry(inFloat[i], outblk);
                            InternalBuffer = outblk;

                            // Reference the output array
                            workDataFloat[i] = (float[])workFloat[i].Data!;

                            // Request data from the source.    
                            inFloat[i] = (DataBlkFloat)src.GetInternCompData(inFloat[i], i);
                            dataFloat[i] = inFloat[i].DataFloat;

                            // The nitty-gritty.

                            for (var row = 0; row < outblk.h; ++row)
                            {
                                leftedgeIn = inFloat[i].offset + row * inFloat[i].scanw;
                                rightedgeIn = leftedgeIn + inFloat[i].w;
                                leftedgeOut = outblk.offset + row * outblk.scanw;
                                rightedgeOut = leftedgeOut + outblk.w;

                                for (kOut = leftedgeOut, kIn = leftedgeIn; kIn < rightedgeIn; ++kIn, ++kOut)
                                {
                                    var tmpFloat = dataFloat[i][kIn] / (1 << fixedPtBits) + shiftVal;
                                    workDataFloat[i][kOut] = ((tmpFloat < 0) ? 0 : ((tmpFloat > maxVal) ? maxVal : tmpFloat));
                                }
                            }
                            break;


                        case DataBlk.TYPE_SHORT:
                        case DataBlk.TYPE_BYTE:
                        default:
                            // Unsupported output type. 
                            throw new ArgumentException("Invalid source " + "datablock type");
                    }
                }

                switch (type)
                {

                    // Int and Float data only
                    case DataBlk.TYPE_INT:

                        if (ncomps == 1)
                        {
                            ((MonochromeTransformTosRGB)xform!).apply(workInt[c], tempInt[c]);
                        }
                        else
                        {
                            // ncomps == 3
                            ((MatrixBasedTransformTosRGB)xform!).apply(workInt, tempInt);
                        }

                        outblk.progressive = inInt[c].progressive;
                        outblk.Data = tempInt[c].Data;
                        break;


                    case DataBlk.TYPE_FLOAT:

                        if (ncomps == 1)
                        {
                            ((MonochromeTransformTosRGB)xform!).apply(workFloat[c], tempFloat[c]);
                        }
                        else
                        {
                            // ncomps == 3
                            ((MatrixBasedTransformTosRGB)xform!).apply(workFloat, tempFloat);
                        }

                        outblk.progressive = inFloat[c].progressive;
                        outblk.Data = tempFloat[c].Data;
                        break;


                    case DataBlk.TYPE_SHORT:
                    case DataBlk.TYPE_BYTE:
                    default:
                        // Unsupported output type. 
                        throw new ArgumentException("invalid source datablock" + " type");
                }

                // Initialize the output block geometry and set the profiled
                // data into the output block.
                outblk.offset = 0;
                outblk.scanw = outblk.w;
            }
            catch (MatrixBasedTransformException e)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.ERROR,
                    $"matrix transform problem:\n{e.Message}");
                if (pl.GetParameter("debug")?.Equals("on") == true)
                {
                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.ERROR, e.StackTrace!);
                }
                else
                {
                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.ERROR, "Use '-debug' option for more details");
                }
                return null!;
            }
            catch (MonochromeTransformException e)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.ERROR,
                    $"monochrome transform problem:\n{e.Message}");
                if (pl.GetParameter("debug")?.Equals("on") == true)
                {
                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.ERROR, e.StackTrace!);
                }
                else
                {
                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.ERROR, "Use '-debug' option for more details");
                }
                return null!;
            }

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
        /// <seealso cref="GetCompData" />
        public override DataBlk GetInternCompData(DataBlk output, int compIndex)
        {
            return GetCompData(output, compIndex);
        }

        /// <summary>Return a suitable String representation of the class instance. </summary>
        public override string ToString()
        {
            var rep = new System.Text.StringBuilder("[ICCProfiler:");
            var body = new System.Text.StringBuilder();
            if (icc != null)
            {
                body.Append(Environment.NewLine).Append(ColorSpace.indent("  ", icc.ToString()));
            }
            if (xform != null)
            {
                body.Append(Environment.NewLine).Append(ColorSpace.indent("  ", xform.ToString()));
            }
            rep.Append(ColorSpace.indent("  ", body));
            return rep.Append("]").ToString();
        }
        static ICCProfiler()
        {
            GRAY = RestrictedICCProfile.GRAY;
            RED = RestrictedICCProfile.RED;
            GREEN = RestrictedICCProfile.GREEN;
            BLUE = RestrictedICCProfile.BLUE;
        }
    }
}