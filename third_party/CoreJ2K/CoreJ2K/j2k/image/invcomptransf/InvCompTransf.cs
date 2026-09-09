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
using CoreJ2K.j2k.decoder;
using CoreJ2K.j2k.image.Simd;
using CoreJ2K.j2k.util;
using CoreJ2K.j2k.wavelet.synthesis;
using System;

namespace CoreJ2K.j2k.image.invcomptransf
{

    /// <summary> This class apply inverse component transformations to the tiles depending
    /// on specification read from the codestream header. These transformations can
    /// be used to improve compression efficiency but are not related to colour
    /// transforms used to map colour values for display purposes. JPEG 2000 part I
    /// defines 2 component transformations: RCT (Reversible Component
    /// Transformation) and ICT (Irreversible Component Transformation).
    /// 
    /// </summary>
    /// <seealso cref="ModuleSpec">
    /// 
    /// </seealso>
    public class InvCompTransf : ImgDataAdapter, BlkImgDataSrc
    {
        /// <summary> Returns the parameters that are used in this class and implementing
        /// classes. It returns a 2D String array. Each of the 1D arrays is for a
        /// different option, and they have 4 elements. The first element is the
        /// option name, the second one is the synopsis, the third one is a long
        /// description of what the parameter is and the fourth is its default
        /// value. The synopsis or description may be 'null', in which case it is
        /// assumed that there is no synopsis or description of the option,
        /// respectively. Null may be returned if no options are supported.
        /// 
        /// </summary>
        /// <returns> the options name, their synopsis and their explanation, 
        /// or null if no options are supported.
        /// 
        /// </returns>
        public static string[][] ParameterInfo => pinfo;

        /// <summary> Returns true if this transform is reversible in current
        /// tile. Reversible component transformations are those which operation
        /// can be completely reversed without any loss of information (not even
        /// due to rounding).
        /// 
        /// </summary>
        /// <returns> Reversibility of component transformation in current
        /// tile
        /// 
        /// </returns>
        public virtual bool Reversible
        {
            get
            {
                switch (transfType)
                {

                    case NONE:
                    case INV_RCT:
                        return true;

                    case INV_ICT:
                        return false;

                    default:
                        throw new ArgumentException("Non JPEG 2000 part I component transformation");

                }
            }

        }

        /// <summary>Identifier for no component transformation. Value is 0. </summary>
        public const int NONE = 0;

        /// <summary>The prefix for inverse component transformation options: 'M' </summary>
        public const char OPT_PREFIX = 'M';

        /// <summary>The list of parameters that is accepted by the inverse
        /// component transformation module. They start with 'M'. 
        /// </summary>
        private static readonly string[][] pinfo = null;

        /// <summary>Identifier for the Inverse Reversible Component Transformation
        /// (INV_RCT). Value is 1. 
        /// </summary>
        public const int INV_RCT = 1;

        /// <summary>Identifier for the Inverse Irreversible Component
        /// Transformation (INV_ICT). Value is 2 
        /// </summary>
        public const int INV_ICT = 2;

        /// <summary>The source of image data </summary>
        private readonly BlkImgDataSrc src;

        /// <summary>The component transformations specifications </summary>
        private readonly CompTransfSpec cts;

        /// <summary>The wavelet filter specifications </summary>
        private readonly SynWTFilterSpec wfs;

        /// <summary>The type of the current component transformation JPEG 2000
        /// part I only support NONE, FORW_RCT and FORW_ICT types
        /// </summary>
        private int transfType = NONE;

        /// <summary>Buffer for each component of output data </summary>
        private readonly int[][] outdata = new int[3][];

        /// <summary>Cached auxiliary output buffers for component transforms – reused when large enough to avoid per-block allocations.</summary>
        private int[] _auxOut1;
        private int[] _auxOut2;

        /// <summary>Cached DataBlkFloat wrapper for the c&gt;=3 invICT branch – avoids a per-call wrapper allocation.</summary>
        private DataBlkFloat _indbICT;

        /// <summary>Block used to request component 0 </summary>
        private DataBlk block0;

        /// <summary>Block used to request component 1 </summary>
        private DataBlk block1;

        /// <summary>Block used to request component 2 </summary>
        private DataBlk block2;

        /// <summary>Data block used only to store coordinates and progressiveness 
        /// of the buffered blocks 
        /// </summary>
        private readonly DataBlkInt dbi = new DataBlkInt();

        /// <summary>The bit-depths of un-transformed components </summary>
        private readonly int[] utdepth;

        /// <summary>Flag indicating whether the decoder should skip the component 
        /// transform
        /// </summary>
        private readonly bool noCompTransf = false;

        /// <summary> Constructs a new ForwCompTransf object that operates on the
        /// specified source of image data.
        /// 
        /// </summary>
        /// <param name="imgSrc">The source from where to get the data to be
        /// transformed
        /// 
        /// </param>
        /// <param name="decSpec">The decoder specifications
        /// 
        /// </param>
        /// <param name="utdepth">The bit depth of the un-transformed components 
        /// 
        /// </param>
        /// <param name="pl">The command line optinons of the decoder
        /// 
        /// </param>
        /// <seealso cref="BlkImgDataSrc">
        /// 
        /// </seealso>
        public InvCompTransf(BlkImgDataSrc imgSrc, DecoderSpecs decSpec, int[] utdepth, ParameterList pl)
            : base(imgSrc)
        {
            cts = decSpec.cts;
            wfs = decSpec.wfs;
            src = imgSrc;
            this.utdepth = utdepth;
            noCompTransf = !(pl.GetBooleanParameter("comp_transf"));
        }

        /// <summary> Returns a string with a descriptive text of which inverse component
        /// transformation is used. This can be either "Inverse RCT" or "Inverse
        /// ICT" or "No component transformation" depending on the current tile.
        /// 
        /// </summary>
        /// <returns> A descriptive string
        /// 
        /// </returns>
        public override string ToString()
        {
            switch (transfType)
            {

                case INV_RCT:
                    return "Inverse RCT";

                case INV_ICT:
                    return "Inverse ICT";

                case NONE:
                    return "No component transformation";

                default:
                    throw new ArgumentException("Non JPEG 2000 part I component transformation");

            }
        }

        /// <summary> Returns the position of the fixed point in the specified
        /// component. This is the position of the least significant integral
        /// (i.e. non-fractional) bit, which is equivalent to the number of
        /// fractional bits. For instance, for fixed-point values with 2 fractional
        /// bits, 2 is returned. For floating-point data this value does not apply
        /// and 0 should be returned. Position 0 is the position of the least
        /// significant bit in the data.
        /// 
        /// This default implementation assumes that the number of fractional
        /// bits is not modified by the component mixer.
        /// 
        /// </summary>
        /// <param name="compIndex">The index of the component.
        /// 
        /// </param>
        /// <returns> The value of the fixed point position of the source since the
        /// color transform does not affect it.
        /// 
        /// </returns>
        public virtual int GetFixedPoint(int compIndex)
        {
            return src.GetFixedPoint(compIndex);
        }

        /// <summary> Calculates the bitdepths of the transformed components, given the
        /// bitdepth of the un-transformed components and the component
        /// tranformation type.
        /// 
        /// </summary>
        /// <param name="utdepth">The bitdepth of each un-transformed component
        /// 
        /// </param>
        /// <param name="ttype">The type ID of the inverse component tranformation
        /// 
        /// </param>
        /// <param name="tdepth">If not null the results are stored in this
        /// array, otherwise a new array is allocated and returned.
        /// 
        /// </param>
        /// <returns> The bitdepth of each transformed component.
        /// 
        /// </returns>
        public static int[] calcMixedBitDepths(int[] utdepth, int ttype, int[] tdepth)
        {

            if (utdepth.Length < 3 && ttype != NONE)
            {
                throw new ArgumentException();
            }

            if (tdepth == null)
            {
                tdepth = new int[utdepth.Length];
            }

            switch (ttype)
            {

                case NONE:
                    Array.Copy(utdepth, 0, tdepth, 0, utdepth.Length);
                    break;

                case INV_RCT:
                    if (utdepth.Length > 3)
                    {
                        Array.Copy(utdepth, 3, tdepth, 3, utdepth.Length - 3);
                    }
                    // The formulas are:
                    // tdepth[0] = ceil(log2(2^(utdepth[0])+2^utdepth[1]+
                    //                        2^(utdepth[2])))-2+1
                    // tdepth[1] = ceil(log2(2^(utdepth[0])+2^(utdepth[1])-1))+1
                    // tdepth[2] = ceil(log2(2^(utdepth[1])+2^(utdepth[2])-1))+1
                    // The MathUtil.log2(x) function calculates floor(log2(x)), so we
                    // use 'MathUtil.log2(2*x-1)+1', which calculates ceil(log2(x))
                    // for any x>=1, x integer.
                    tdepth[0] = MathUtil.log2((1 << utdepth[0]) + (2 << utdepth[1]) + (1 << utdepth[2]) - 1) - 2 + 1;
                    tdepth[1] = MathUtil.log2((1 << utdepth[2]) + (1 << utdepth[1]) - 1) + 1;
                    tdepth[2] = MathUtil.log2((1 << utdepth[0]) + (1 << utdepth[1]) - 1) + 1;
                    break;

                case INV_ICT:
                    if (utdepth.Length > 3)
                    {
                        Array.Copy(utdepth, 3, tdepth, 3, utdepth.Length - 3);
                    }
                    // The MathUtil.log2(x) function calculates floor(log2(x)), so we
                    // use 'MathUtil.log2(2*x-1)+1', which calculates ceil(log2(x))
                    // for any x>=1, x integer.
                    tdepth[0] =
                        MathUtil.log2(
                            (int)
                            Math.Floor(
                                (1 << utdepth[0]) * 0.299072 + (1 << utdepth[1]) * 0.586914
                                + (1 << utdepth[2]) * 0.114014) - 1) + 1;
                    tdepth[1] =
                        MathUtil.log2(
                            (int)
                            Math.Floor(
                                (1 << utdepth[0]) * 0.168701 + (1 << utdepth[1]) * 0.331299 + (1 << utdepth[2]) * 0.5)
                            - 1) + 1;
                    tdepth[2] =
                        MathUtil.log2(
                            (int)
                            Math.Floor(
                                (1 << utdepth[0]) * 0.5 + (1 << utdepth[1]) * 0.418701 + (1 << utdepth[2]) * 0.081299)
                            - 1) + 1;
                    break;
            }

            return tdepth;
        }

        /// <summary> Returns the number of bits, referred to as the "range bits",
        /// corresponding to the nominal range of the data in the specified
        /// component. If this number is <i>b</b> then for unsigned data the
        /// nominal range is between 0 and 2^b-1, and for signed data it is between
        /// -2^(b-1) and 2^(b-1)-1.
        /// 
        /// </summary>
        /// <param name="compIndex">The index of the component.
        /// 
        /// </param>
        /// <returns> The bitdepth of un-transformed component 'c'.
        /// 
        /// </returns>
        public override int GetNomRangeBits(int compIndex)
        {
            return utdepth[compIndex];
        }

        /// <summary> Apply inverse component transformation associated with the current
        /// tile. If no component transformation has been requested by the user,
        /// data are not modified.
        /// 
        /// This method calls the getInternCompData() method, but respects the
        /// definitions of the getCompData() method defined in the BlkImgDataSrc
        /// interface.
        /// 
        /// </summary>
        /// <param name="blk">Determines the rectangular area to return, and the
        /// data is returned in this object.
        /// 
        /// </param>
        /// <param name="c">Index of the output component.
        /// 
        /// </param>
        /// <returns> The requested DataBlk
        /// 
        /// </returns>
        /// <seealso cref="BlkImgDataSrc.GetCompData">
        /// 
        /// </seealso>
        public virtual DataBlk GetCompData(DataBlk blk, int c)
        {
            // If requesting a component whose index is greater than 3 or there is
            // no transform return a copy of data (getInternCompData returns the
            // actual data in those cases)
            if (c >= 3 || transfType == NONE || noCompTransf)
            {
                return src.GetCompData(blk, c);
            }
            else
            {
                // We can use getInternCompData (since data is a copy anyways)
                return GetInternCompData(blk, c);
            }
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

        /// <summary> Apply the inverse component transformation associated with the current
        /// tile. If no component transformation has been requested by the user,
        /// data are not modified. Else, appropriate method is called (invRCT or
        /// invICT).
        /// 
        /// </summary>
        /// <seealso cref="invRCT">
        /// 
        /// </seealso>
        /// <seealso cref="invICT">
        /// 
        /// </seealso>
        /// <param name="blk">Determines the rectangular area to return.
        /// 
        /// </param>
        /// <param name="compIndex">Index of the output component.
        /// 
        /// </param>
        /// <returns> The requested DataBlk
        /// 
        /// </returns>
        public virtual DataBlk GetInternCompData(DataBlk blk, int compIndex)
        {
            // if specified in the command line that no component transform should
            // be made, return original data
            if (noCompTransf) return src.GetInternCompData(blk, compIndex);

            switch (transfType)
            {

                case NONE:
                    return src.GetInternCompData(blk, compIndex);


                case INV_RCT:
                    return invRCT(blk, compIndex);

                case INV_ICT:
                    return invICT(blk, compIndex);

                default:
                    throw new ArgumentException("Non JPEG 2000 part I component transformation");

            }
        }

        /// <summary> Apply inverse component transformation to obtain requested component
        /// from specified block of data. Whatever the type of requested DataBlk,
        /// it always returns a DataBlkInt.
        /// 
        /// </summary>
        /// <param name="blk">Determine the rectangular area to return 
        /// 
        /// </param>
        /// <param name="c">The index of the requested component
        /// 
        /// </param>
        /// <returns> Data of requested component
        /// 
        /// </returns>
        private DataBlk invRCT(DataBlk blk, int c)
        {
            // If the component number is three or greater, return original data
            if (c >= 3 && c < NumComps)
            {
                // Requesting a component whose index is greater than 3
                return src.GetInternCompData(blk, c);
            }
            // If asking a component for the first time for this block,
            // do transform for the 3 components
            else if ((outdata[c] == null) || (dbi.ulx > blk.ulx) || (dbi.uly > blk.uly)
                     || (dbi.ulx + dbi.w < blk.ulx + blk.w) || (dbi.uly + dbi.h < blk.uly + blk.h))
            {
                int i;
                var w = blk.w; //width of output block
                var h = blk.h; //height of ouput block

                //Reference to output block data array
                outdata[c] = (int[])blk.Data;

                //Create data array of blk if necessary
                if (outdata[c] == null || outdata[c].Length != h * w)
                {
                    outdata[c] = new int[h * w];
                    blk.Data = outdata[c];
                }

                var auxSize = outdata[c].Length;
                if (_auxOut1 == null || _auxOut1.Length < auxSize) _auxOut1 = new int[auxSize];
                if (_auxOut2 == null || _auxOut2.Length < auxSize) _auxOut2 = new int[auxSize];
                outdata[(c + 1) % 3] = _auxOut1;
                outdata[(c + 2) % 3] = _auxOut2;

                if (block0 == null || block0.DataType != DataBlk.TYPE_INT) block0 = new DataBlkInt();
                if (block1 == null || block1.DataType != DataBlk.TYPE_INT) block1 = new DataBlkInt();
                if (block2 == null || block2.DataType != DataBlk.TYPE_INT) block2 = new DataBlkInt();
                block0.w = block1.w = block2.w = blk.w;
                block0.h = block1.h = block2.h = blk.h;
                block0.ulx = block1.ulx = block2.ulx = blk.ulx;
                block0.uly = block1.uly = block2.uly = blk.uly;

                int[] data0, data1, data2; // input data arrays

                // Fill in buffer blocks (to be read only)
                // Returned blocks may have different size and position
                block0 = (DataBlkInt)src.GetInternCompData(block0, 0);
                if (block0 == null || block0.Data == null)
                {
                    throw new InvalidOperationException(
                        $"Component 0 data is null or invalid. Cannot perform inverse RCT transform. " +
                        $"Block dimensions: ulx={blk.ulx}, uly={blk.uly}, w={blk.w}, h={blk.h}");
                }
                data0 = (int[])block0.Data;
                
                block1 = (DataBlkInt)src.GetInternCompData(block1, 1);
                if (block1 == null || block1.Data == null)
                {
                    throw new InvalidOperationException(
                        $"Component 1 data is null or invalid. Cannot perform inverse RCT transform. " +
                        $"Block dimensions: ulx={blk.ulx}, uly={blk.uly}, w={blk.w}, h={blk.h}");
                }
                data1 = (int[])block1.Data;
                
                block2 = (DataBlkInt)src.GetInternCompData(block2, 2);
                if (block2 == null || block2.Data == null)
                {
                    throw new InvalidOperationException(
                        $"Component 2 data is null or invalid. Cannot perform inverse RCT transform. " +
                        $"Block dimensions: ulx={blk.ulx}, uly={blk.uly}, w={blk.w}, h={blk.h}");
                }
                data2 = (int[])block2.Data;

                // Set the progressiveness of the output data
                blk.progressive = block0.progressive || block1.progressive || block2.progressive;
                blk.offset = 0;
                blk.scanw = w;

                // set attributes of the DataBlk used for buffering
                dbi.progressive = blk.progressive;
                dbi.ulx = blk.ulx;
                dbi.uly = blk.uly;
                dbi.w = blk.w;
                dbi.h = blk.h;

                // Perform conversion row-by-row using a SIMD-accelerated kernel.
                // The output buffers (outdata[*]) are contiguous, h*w. The input
                // blocks may have an arbitrary scanw and offset.
                for (i = 0; i < h; i++)
                {
                    int outRow = i * w;
                    int inRow0 = block0.offset + i * block0.scanw;
                    int inRow1 = block1.offset + i * block1.scanw;
                    int inRow2 = block2.offset + i * block2.scanw;
                    SimdColorTransform.InvRctRow(
                        new ReadOnlySpan<int>(data0, inRow0, w),
                        new ReadOnlySpan<int>(data1, inRow1, w),
                        new ReadOnlySpan<int>(data2, inRow2, w),
                        new Span<int>(outdata[0], outRow, w),
                        new Span<int>(outdata[1], outRow, w),
                        new Span<int>(outdata[2], outRow, w));
                }
                outdata[c] = null;
            }
            else if ((c >= 0) && (c < 3))
            {
                //Asking for the 2nd or 3rd block component
                blk.Data = outdata[c];
                blk.progressive = dbi.progressive;
                blk.offset = (blk.uly - dbi.uly) * dbi.w + blk.ulx - dbi.ulx;
                blk.scanw = dbi.w;
                outdata[c] = null;
            }
            else
            {
                // Requesting a non valid component index
                throw new ArgumentException();
            }
            return blk;
        }

        /// <summary> Apply inverse irreversible component transformation to obtain requested
        /// component from specified block of data. Whatever the type of requested
        /// DataBlk, it always returns a DataBlkFloat.
        /// 
        /// </summary>
        /// <param name="blk">Determine the rectangular area to return 
        /// 
        /// </param>
        /// <param name="c">The index of the requested component
        /// 
        /// </param>
        /// <returns> Data of requested component
        /// 
        /// </returns>
        private DataBlk invICT(DataBlk blk, int c)
        {
            if (c >= 3 && c < NumComps)
            {
                // Requesting a component whose index is greater than 3            
                int k, k0, mink, i; //  k1, k2 removed
                var w = blk.w; //width of output block
                var h = blk.h; //height of ouput block

                int[] out_data; // array of output data

                //Reference to output block data array
                out_data = (int[])blk.Data;

                //Create data array of blk if necessary
                if (out_data == null)
                {
                    out_data = new int[h * w];
                    blk.Data = out_data;
                }

                // Variables
                if (_indbICT == null) _indbICT = new DataBlkFloat();
                _indbICT.ulx = blk.ulx;
                _indbICT.uly = blk.uly;
                _indbICT.w = w;
                _indbICT.h = h;
                var indb = _indbICT;
                float[] indata; // input data array

                // Get the input data
                // (returned block may be larger than requested one)
                src.GetInternCompData(indb, c);
                indata = (float[])indb.Data;

                // Copy the data converting from int to int
                k = w * h - 1;
                k0 = indb.offset + (h - 1) * indb.scanw + w - 1;
                for (i = h - 1; i >= 0; i--)
                {
                    for (mink = k - w; k > mink; k--, k0--)
                    {
                        out_data[k] = (int)(indata[k0]);
                    }
                    // Jump to beggining of previous line in input
                    k0 -= (indb.scanw - w);
                }

                // Set the progressivity and offset
                blk.progressive = indb.progressive;
                blk.offset = 0;
                blk.scanw = w;
            }
            // If asking a component for the first time for this block,
            // do transform for the 3 components
            else if ((outdata[c] == null) || (dbi.ulx > blk.ulx) || (dbi.uly > blk.uly)
                     || (dbi.ulx + dbi.w < blk.ulx + blk.w) || (dbi.uly + dbi.h < blk.uly + blk.h))
            {
                int i;
                var w = blk.w; //width of output block
                var h = blk.h; //height of ouput block

                //Reference to output block data array
                outdata[c] = (int[])blk.Data;

                // Validate block dimensions
                long totalSize = (long)w * h;
                if (totalSize > int.MaxValue)
                {
                    throw new InvalidOperationException(
                        $"Block dimensions too large for inverse ICT: w={w}, h={h}. " +
                        $"Total size {totalSize} exceeds maximum array size.");
                }
                int blockSize = (int)totalSize;

                //Create data array of blk if necessary
                if (outdata[c] == null || outdata[c].Length != blockSize)
                {
                    outdata[c] = new int[blockSize];
                    blk.Data = outdata[c];
                }

                var auxSize = outdata[c].Length;
                if (_auxOut1 == null || _auxOut1.Length < auxSize) _auxOut1 = new int[auxSize];
                if (_auxOut2 == null || _auxOut2.Length < auxSize) _auxOut2 = new int[auxSize];
                outdata[(c + 1) % 3] = _auxOut1;
                outdata[(c + 2) % 3] = _auxOut2;

                if (block0 == null || block0.DataType != DataBlk.TYPE_FLOAT) block0 = new DataBlkFloat();
                if (block2 == null || block2.DataType != DataBlk.TYPE_FLOAT) block2 = new DataBlkFloat();
                if (block1 == null || block1.DataType != DataBlk.TYPE_FLOAT) block1 = new DataBlkFloat();
                block0.w = block2.w = block1.w = blk.w;
                block0.h = block2.h = block1.h = blk.h;
                block0.ulx = block2.ulx = block1.ulx = blk.ulx;
                block0.uly = block2.uly = block1.uly = blk.uly;

                float[] data0, data1, data2; // input data arrays

                // Fill in buffer blocks (to be read only)
                // Returned blocks may have different size and position
                block0 = (DataBlkFloat)src.GetInternCompData(block0, 0);
                if (block0 == null || block0.Data == null)
                {
                    throw new InvalidOperationException(
                        $"Component 0 data is null or invalid. Cannot perform inverse ICT transform. " +
                        $"Block dimensions: ulx={blk.ulx}, uly={blk.uly}, w={blk.w}, h={blk.h}");
                }
                data0 = (float[])block0.Data;
                
                block2 = (DataBlkFloat)src.GetInternCompData(block2, 1);
                if (block2 == null || block2.Data == null)
                {
                    throw new InvalidOperationException(
                        $"Component 1 data is null or invalid. Cannot perform inverse ICT transform. " +
                        $"Block dimensions: ulx={blk.ulx}, uly={blk.uly}, w={blk.w}, h={blk.h}");
                }
                data2 = (float[])block2.Data;
                
                block1 = (DataBlkFloat)src.GetInternCompData(block1, 2);
                if (block1 == null || block1.Data == null)
                {
                    throw new InvalidOperationException(
                        $"Component 2 data is null or invalid. Cannot perform inverse ICT transform. " +
                        $"Block dimensions: ulx={blk.ulx}, uly={blk.uly}, w={blk.w}, h={blk.h}");
                }
                data1 = (float[])block1.Data;

                // Set the progressiveness of the output data
                blk.progressive = block0.progressive || block1.progressive || block2.progressive;
                blk.offset = 0;
                blk.scanw = w;

                // set attributes of the DataBlk used for buffering
                dbi.progressive = blk.progressive;
                dbi.ulx = blk.ulx;
                dbi.uly = blk.uly;
                dbi.w = blk.w;
                dbi.h = blk.h;

                //Perform conversion row-by-row using a SIMD-accelerated kernel.
                for (i = 0; i < h; i++)
                {
                    int outRow = i * w;
                    int inRow0 = block0.offset + i * block0.scanw;
                    int inRow2 = block2.offset + i * block2.scanw;
                    int inRow1 = block1.offset + i * block1.scanw;
                    SimdColorTransform.InvIctRow(
                        new ReadOnlySpan<float>(data0, inRow0, w),  // Y
                        new ReadOnlySpan<float>(data2, inRow2, w),  // Cb
                        new ReadOnlySpan<float>(data1, inRow1, w),  // Cr
                        new Span<int>(outdata[0], outRow, w),       // R
                        new Span<int>(outdata[1], outRow, w),       // G
                        new Span<int>(outdata[2], outRow, w));      // B
                }
                outdata[c] = null;
            }
            else if ((c >= 0) && (c <= 3))
            {
                //Asking for the 2nd or 3rd block component
                blk.Data = outdata[c];
                blk.progressive = dbi.progressive;
                blk.offset = (blk.uly - dbi.uly) * dbi.w + blk.ulx - dbi.ulx;
                blk.scanw = dbi.w;
                outdata[c] = null;
            }
            else
            {
                // Requesting a non valid component index
                throw new ArgumentException();
            }
            return blk;
        }

        /// <summary> Changes the current tile, given the new indexes. An
        /// IllegalArgumentException is thrown if the indexes do not
        /// correspond to a valid tile.
        /// 
        /// This default implementation changes the tile in the source
        /// and re-initializes properly component transformation variables..
        /// 
        /// </summary>
        /// <param name="x">The horizontal index of the tile.
        /// 
        /// </param>
        /// <param name="y">The vertical index of the new tile.
        /// 
        /// 
        /// </param>
        public override void SetTile(int x, int y)
        {
            src.SetTile(x, y);
            tIdx = TileIdx; // index of the current tile

            // initializations
            if (cts.GetIntTileDef(tIdx) == NONE) transfType = NONE;
            else
            {
                var nc = src.NumComps > 3 ? 3 : src.NumComps;
                var rev = 0;
                for (var c = 0; c < nc; c++)
                {
                    rev += (wfs.IsReversible(tIdx, c) ? 1 : 0);
                }
                if (rev == 3)
                {
                    // All WT are reversible
                    transfType = INV_RCT;
                }
                else if (rev == 0)
                {
                    // All WT irreversible
                    transfType = INV_ICT;
                }
                else
                {
                    // Error
                    throw new ArgumentException(
                        $"Wavelet transformation and component transformation not coherent in tile{tIdx}");
                }
            }
        }

        /// <summary> Advances to the next tile, in standard scan-line order (by rows
        /// then columns). An NoNextElementException is thrown if the
        /// current tile is the last one (i.e. there is no next tile).
        /// 
        /// This default implementation just advances to the next tile
        /// in the source and re-initializes properly component
        /// transformation variables.
        /// 
        /// 
        /// </summary>
        public override void NextTile()
        {
            src.NextTile();
            tIdx = TileIdx; // index of the current tile

            // initializations
            if (cts.GetIntTileDef(tIdx) == NONE) transfType = NONE;
            else
            {
                var nc = src.NumComps > 3 ? 3 : src.NumComps;
                var rev = 0;
                for (var c = 0; c < nc; c++)
                {
                    rev += (wfs.IsReversible(tIdx, c) ? 1 : 0);
                }
                if (rev == 3)
                {
                    // All WT are reversible
                    transfType = INV_RCT;
                }
                else if (rev == 0)
                {
                    // All WT irreversible
                    transfType = INV_ICT;
                }
                else
                {
                    // Error
                    throw new ArgumentException(
                        $"Wavelet transformation and component transformation not coherent in tile{tIdx}");
                }
            }
        }
    }
}
