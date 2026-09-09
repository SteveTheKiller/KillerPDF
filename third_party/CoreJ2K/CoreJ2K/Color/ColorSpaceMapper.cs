using CoreJ2K.Icc;
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

    /// <summary> This is the base class for all modules in the colorspace and icc
    /// profiling steps of the decoding chain.  It is responsible for the
    /// allocation and iniitialization of all working storage.  It provides
    /// several utilities which are of generic use in preparing DataBlks
    /// for use and provides default implementations for the getCompData
    /// and getInternCompData methods.
    /// 
    /// </summary>
    /// <seealso cref="j2k.colorspace.ColorSpace">
    /// </seealso>
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public abstract class ColorSpaceMapper : ImgDataAdapter, BlkImgDataSrc
    {
        private void InitBlock()
        {
            computed = new ComputedComponents(this);
        }

        /// <summary> Returns the parameters that are used in this class and implementing
        /// classes. It returns a 2D String array. Each of the 1D arrays is for a
        /// different option, and they have 3 elements. The first element is the
        /// option name, the second one is the synopsis and the third one is a long
        /// description of what the parameter is. The synopsis or description may
        /// be 'null', in which case it is assumed that there is no synopsis or
        /// description of the option, respectively. Null may be returned if no
        /// options are supported.
        /// 
        /// </summary>
        /// <returns> the options name, their synopsis and their explanation, or null
        /// if no options are supported.
        /// 
        /// </returns>
        public static string?[][] ParameterInfo => pinfo;

        /// <summary> Arrange for the input DataBlk to receive an
        /// appropriately sized and typed data buffer
        /// </summary>
        /// <param name="db">input DataBlk
        /// </param>
        /// <seealso cref="j2k.image.DataBlk">
        /// </seealso>
        protected internal static DataBlk InternalBuffer
        {
            set
            {
                switch (value.DataType)
                {


                    case DataBlk.TYPE_INT:
                        if (value.Data == null || ((int[])value.Data).Length < value.w * value.h) value.Data = new int[value.w * value.h];
                        break;


                    case DataBlk.TYPE_FLOAT:
                        if (value.Data == null || ((float[])value.Data).Length < value.w * value.h)
                        {
                            value.Data = new float[value.w * value.h];
                        }
                        break;


                    default:
                        throw new ArgumentException("Invalid output datablock" + " type");

                }
            }

        }

        /// <summary>The prefix for ICC Profiler options </summary>
        public const char OPT_PREFIX = 'I';

        // Temporary data buffers needed during profiling.
        protected internal DataBlkInt[] inInt = null!; // Integer input data.

        protected internal DataBlkFloat[] inFloat = null!; // Floating point input data.

        protected internal DataBlkInt[] workInt = null!; // Input data shifted to zero-offset

        protected internal DataBlkFloat[] workFloat = null!; // Input data shifted to zero-offset.

        protected internal int[][] dataInt = null!; // Points to input data.

        protected internal float[][] dataFloat = null!; // Points to input data.

        protected internal float[][] workDataFloat = null!; // References working data pixels.

        protected internal int[][] workDataInt = null!; // References working data pixels.


        /* input data parameters by component */

        protected internal int[] shiftValueArray = null!;

        protected internal int[] maxValueArray = null!;

        protected internal int[] fixedPtBitsArray = null!;

        /// <summary>The list of parameters that are accepted for ICC profiling.</summary>
        private static readonly string?[][] pinfo = {
                                                                  new string?[]
                                                                      {
                                                                          "IcolorSpacedebug", null,
                                                                          "Print debugging messages during colorspace mapping.",
                                                                          "off"
                                                                      }
                                                              };

        /// <summary>Parameter Specs </summary>
        protected internal ParameterList? pl = null;

        /// <summary>ColorSpace info </summary>
        protected internal ColorSpace csMap = null!;

        /// <summary>Number of image components </summary>
        protected internal int ncomps = 0;

        /// <summary>The image source. </summary>
        protected internal BlkImgDataSrc src = null!;


        protected internal sealed class ComputedComponents
        {
            private void InitBlock(ColorSpaceMapper enclosingInstance)
            {
                this.Enclosing_Instance = enclosingInstance;
            }

            public ColorSpaceMapper Enclosing_Instance { get; private set; } = null!;

            //private int tIdx = - 1;
            private int h = -1;

            private int w = -1;

            private int ulx = -1;

            private int uly = -1;

            private int offset = -1;

            private int scanw = -1;

            public ComputedComponents(ColorSpaceMapper enclosingInstance)
            {
                InitBlock(enclosingInstance);
                clear();
            }

            public ComputedComponents(ColorSpaceMapper enclosingInstance, DataBlk db)
            {
                InitBlock(enclosingInstance);
                SetFrom(db);
            }

            public void SetFrom(DataBlk db)
            {
                h = db.h;
                w = db.w;
                ulx = db.ulx;
                uly = db.uly;
                offset = db.offset;
                scanw = db.scanw;
            }

            public void clear()
            {
                h = w = ulx = uly = offset = scanw = -1;
            }

            public bool Equals(ComputedComponents cc)
            {
                return (h == cc.h && w == cc.w && ulx == cc.ulx && uly == cc.uly && offset == cc.offset
                        && scanw == cc.scanw);
            }
        }

        protected internal ComputedComponents computed = null!;

        /// <summary> Copy the DataBlk geometry from source to target
        /// DataBlk and assure that the target has an appropriate
        /// data buffer.
        /// </summary>
        /// <param name="tgt">has its geometry set.
        /// </param>
        /// <param name="src">used to get the new geometric parameters.
        /// </param>
        protected internal static void copyGeometry(DataBlk tgt, DataBlk src)
        {
            tgt.offset = 0;
            tgt.h = src.h;
            tgt.w = src.w;
            tgt.ulx = src.ulx;
            tgt.uly = src.uly;
            tgt.scanw = src.w;

            // Create data array if necessary

            InternalBuffer = tgt;
        }


        /// <summary> Factory method for creating instances of this class.</summary>
        /// <param name="src">-- source of image data
        /// </param>
        /// <param name="csMap">-- provides colorspace info
        /// </param>
        /// <returns> ColorSpaceMapper instance
        /// </returns>
        /// <exception cref="IOException">profile access exception
        /// </exception>
        public static BlkImgDataSrc? createInstance(BlkImgDataSrc src, ColorSpace csMap)
        {

            // Check parameters
            csMap.pl.checkList(OPT_PREFIX, ParameterList.toNameArray(pinfo!));

            // Perform ICCProfiling or ColorSpace tranfsormation.
            if (csMap.Method == ColorSpace.MethodEnum.ICC_PROFILED)
            {
                return ICCProfiler.createInstance(src, csMap);
            }
            else
            {
                var colorspace = csMap.GetColorSpace();

                if (colorspace == ColorSpace.CSEnum.sRGB)
                {
                    return EnumeratedColorSpaceMapper.createInstance(src, csMap);
                }
                else if (colorspace == ColorSpace.CSEnum.GreyScale)
                {
                    return EnumeratedColorSpaceMapper.createInstance(src, csMap);
                }
                else if (colorspace == ColorSpace.CSEnum.sYCC)
                {
                    return SYccColorSpaceMapper.createInstance(src, csMap);
                }
                if (colorspace == ColorSpace.CSEnum.esRGB)
                {
                    return EsRgbColorSpaceMapper.createInstance(src, csMap);
                }
                else if (colorspace == ColorSpace.CSEnum.Unknown)
                {
                    return null;
                }
                else
                {
                    throw new ColorSpaceException("Bad color space specification in image");
                }
            }
        }

        /// <summary> Ctor which creates an ICCProfile for the image and initializes
        /// all data objects (input, working, and output).
        /// 
        /// </summary>
        /// <param name="src">-- Source of image data
        /// </param>
        /// <param name="csm">-- provides colorspace info
        /// 
        /// </param>
        protected internal ColorSpaceMapper(BlkImgDataSrc src, ColorSpace csMap)
            : base(src)
        {
            InitBlock();
            this.src = src;
            this.csMap = csMap;
            initialize();
        }

        /// <summary>General utility used by ctors </summary>
        private void initialize()
        {

			pl = csMap.pl;
			ncomps = src.NumComps;

			shiftValueArray = new int[ncomps];
			maxValueArray = new int[ncomps];
			fixedPtBitsArray = new int[ncomps];

			inInt = new DataBlkInt[ncomps];
			inFloat = new DataBlkFloat[ncomps];
			dataInt = new int[ncomps][];
			dataFloat = new float[ncomps][];

			/* For each component, get a reference to the pixel data and
			* set up working DataBlks for both integer and float output.
			*/
			for (var i = 0; i < ncomps; ++i)
			{
				shiftValueArray[i] = 1 << (src.GetNomRangeBits(i) - 1);
				maxValueArray[i] = (1 << src.GetNomRangeBits(i)) - 1;
				fixedPtBitsArray[i] = src.GetFixedPoint(i);

				inInt[i] = new DataBlkInt();
				inFloat[i] = new DataBlkFloat();
			}
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
        public virtual int GetFixedPoint(int compIndex)
        {
            return src.GetFixedPoint(compIndex);
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
        /// <param name="c">The index of the component from which to get the data.
        /// 
        /// </param>
        /// <seealso cref="GetInternCompData">
        /// 
        /// </seealso>
        public virtual DataBlk GetCompData(DataBlk output, int c)
        {
            return src.GetCompData(output, c);
        }

        /// <summary> Closes the underlying file or network connection from where the
        /// image data is being read.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error occurs.
        /// </exception>
        public void Close()
        {
            // Do nothing.
        }

        /// <summary> Returns true if the data read was originally signed in the specified
        /// component, false if not.
        /// 
        /// </summary>
        /// <param name="compIndex">The index of the component, from 0 to C-1.
        /// 
        /// </param>
        /// <returns> true if the data was originally signed, false if not.
        /// 
        /// </returns>
        public bool IsOrigSigned(int compIndex)
        {
            return false;
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
        /// 
        /// </seealso>
        public virtual DataBlk GetInternCompData(DataBlk output, int compIndex)
        {
            return src.GetInternCompData(output, compIndex);
        }
    }
}
