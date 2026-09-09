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
using CoreJ2K.j2k.encoder;
using CoreJ2K.j2k.util;
using CoreJ2K.j2k.wavelet.analysis;
using System;

namespace CoreJ2K.j2k.image.forwcomptransf
{

    /// <summary> This class apply component transformations to the tiles depending on user
    /// specifications. These transformations can be used to improve compression
    /// efficiency but are not related to colour transforms used to map colour
    /// values for display purposes. JPEG 2000 part I defines 2 component
    /// transformations: RCT (Reversible Component Transformation) and ICT
    /// (Irreversible Component Transformation).</summary>
    /// <seealso cref="ModuleSpec" />
    public class ForwCompTransf : ImgDataAdapter, BlkImgDataSrc
    {
        /// <summary> Returns the parameters that are used in this class and implementing
        /// classes. It returns a 2D String array. Each of the 1D arrays is for a
        /// different option, and they have 4 elements. The first element is the
        /// option name, the second one is the synopsis, the third one is a long
        /// description of what the parameter is and the fourth is its default
        /// value. The synopsis or description may be 'null', in which case it is
        /// assumed that there is no synopsis or description of the option,
        /// respectively. Null may be returned if no options are supported.</summary>
        /// <returns> the options name, their synopsis and their explanation, or null
        /// if no options are supported.</returns>
        public static string[][] ParameterInfo => pinfo;

        /// <summary> Returns true if this transform is reversible in current
        /// tile. Reversible component transformations are those which operation
        /// can be completely reversed without any loss of information (not even
        /// due to rounding).</summary>
        /// <returns> Reversibility of component transformation in current tile</returns>
        public virtual bool Reversible
        {
            get
            {
                switch (transfType)
                {

                    case NONE:
                    case FORW_RCT:
                        return true;

                    case FORW_ICT:
                        return false;

                    default:
                        throw new ArgumentException("Non JPEG 2000 part I component transformation");

                }
            }

        }
        /// <summary>Identifier for no component transformation. Value is 0.</summary>
        public const int NONE = 0;

        /// <summary>Identifier for the Forward Reversible Component Transformation
        /// (FORW_RCT). Value is 1.</summary>
        public const int FORW_RCT = 1;

        /// <summary>Identifier for the Forward Irreversible Component Transformation
        /// (FORW_ICT). Value is 2</summary>
        public const int FORW_ICT = 2;

        /// <summary>The source of image data</summary>
        private readonly BlkImgDataSrc src;

        /// <summary>The component transformations specifications</summary>
        private readonly CompTransfSpec cts;

        /// <summary>The wavelet filter specifications</summary>
        private AnWTFilterSpec wfs;

        /// <summary>The type of the current component transformation. JPEG 2000 part 1
        /// supports only NONE, FORW_RCT and FORW_ICT types</summary>
        private int transfType = NONE;

        /// <summary>The bit-depths of transformed components</summary>
        private int[] tdepth;

        /// <summary>Output block used instead of the one provided as an argument if the
        /// latter is DataBlkFloat.</summary>
        private DataBlk outBlk;

        /// <summary>Block used to request component with index 0 </summary>
        private DataBlkInt block0;

        /// <summary>Block used to request component with index 1</summary>
        private DataBlkInt block1;

        /// <summary>Block used to request component with index 2</summary>
        private DataBlkInt block2;

        /// <summary> Constructs a new ForwCompTransf object that operates on the specified
        /// source of image data.</summary>
        /// <param name="imgSrc">The source from where to get the data to be transformed</param>
        /// <param name="encSpec">The encoder specifications</param>
        /// <seealso cref="BlkImgDataSrc" />
        public ForwCompTransf(BlkImgDataSrc imgSrc, EncoderSpecs encSpec) : base(imgSrc)
        {
            cts = encSpec.cts;
            wfs = encSpec.wfs;
            src = imgSrc;
        }

        /// <summary>The prefix for component transformation type: 'M'</summary>
        public const char OPT_PREFIX = 'M';

        /// <summary>The list of parameters that is accepted by the forward component
        /// transformation module. Options start with an 'M'.</summary>
        private static readonly string[][] pinfo = { new string[] { "Mct", "[<tile index>] [on|off] ...", "Specifies in which tiles to use a multiple component transform. Note that this multiple component transform can only be applied in tiles that contain at least three components and whose components are processed with the same wavelet filters and " + "quantization type. " + "If the wavelet transform is reversible (w5x3 filter), the " + "Reversible Component Transformation (RCT) is applied. If not " + "(w9x7 filter), the Irreversible Component Transformation (ICT)" + " is used.", null } };

        /// <summary> Returns the position of the fixed point in the specified
        /// component. This is the position of the least significant integral
        /// (i.e. non-fractional) bit, which is equivalent to the number of
        /// fractional bits. For instance, for fixed-point values with 2 fractional
        /// bits, 2 is returned. For floating-point data this value does not apply
        /// and 0 should be returned. Position 0 is the position of the least
        /// significant bit in the data.
        /// 
        /// This default implementation assumes that the number of fractional
        /// bits is not modified by the component mixer.</summary>
        /// <param name="compIndex">The index of the component.</param>
        /// <returns> The value of the fixed point position of the source since the
        /// color transform does not affect it.</returns>
        public virtual int GetFixedPoint(int compIndex)
        {
            return src.GetFixedPoint(compIndex);
        }

        /// <summary> Calculates the bitdepths of the transformed components, given the
        /// bitdepth of the un-transformed components and the component
        /// transformation type.</summary>
        /// <param name="ntdepth">The bitdepth of each non-transformed components.</param>
        /// <param name="ttype">The type ID of the component transformation.</param>
        /// <param name="tdepth">If not null the results are stored in this array,
        /// otherwise a new array is allocated and returned.</param>
        /// <returns> The bitdepth of each transformed component.</returns>
        public static int[] calcMixedBitDepths(int[] ntdepth, int ttype, int[] tdepth)
        {

            if (ntdepth.Length < 3 && ttype != NONE)
            {
                throw new ArgumentException();
            }

            if (tdepth == null)
            {
                tdepth = new int[ntdepth.Length];
            }

            switch (ttype)
            {

                case NONE:
                    Array.Copy(ntdepth, 0, tdepth, 0, ntdepth.Length);
                    break;

                case FORW_RCT:
                    if (ntdepth.Length > 3)
                    {
                        Array.Copy(ntdepth, 3, tdepth, 3, ntdepth.Length - 3);
                    }
                    // The formulas are:
                    // tdepth[0] = ceil(log2(2^(ntdepth[0])+2^ntdepth[1]+
                    //                        2^(ntdepth[2])))-2+1
                    // tdepth[1] = ceil(log2(2^(ntdepth[1])+2^(ntdepth[2])-1))+1
                    // tdepth[2] = ceil(log2(2^(ntdepth[0])+2^(ntdepth[1])-1))+1
                    // The MathUtil.log2(x) function calculates floor(log2(x)), so we
                    // use 'MathUtil.log2(2*x-1)+1', which calculates ceil(log2(x))
                    // for any x>=1, x integer.
                    tdepth[0] = MathUtil.log2((1 << ntdepth[0]) + (2 << ntdepth[1]) + (1 << ntdepth[2]) - 1) - 2 + 1;
                    tdepth[1] = MathUtil.log2((1 << ntdepth[2]) + (1 << ntdepth[1]) - 1) + 1;
                    tdepth[2] = MathUtil.log2((1 << ntdepth[0]) + (1 << ntdepth[1]) - 1) + 1;
                    break;

                case FORW_ICT:
                    if (ntdepth.Length > 3)
                    {
                        Array.Copy(ntdepth, 3, tdepth, 3, ntdepth.Length - 3);
                    }
                    // The MathUtil.log2(x) function calculates floor(log2(x)), so we
                    // use 'MathUtil.log2(2*x-1)+1', which calculates ceil(log2(x))
                    // for any x>=1, x integer.
                    tdepth[0] = MathUtil.log2((int)Math.Floor((1 << ntdepth[0]) * 0.299072 + (1 << ntdepth[1]) * 0.586914 + (1 << ntdepth[2]) * 0.114014) - 1) + 1;
                    tdepth[1] = MathUtil.log2((int)Math.Floor((1 << ntdepth[0]) * 0.168701 + (1 << ntdepth[1]) * 0.331299 + (1 << ntdepth[2]) * 0.5) - 1) + 1;
                    tdepth[2] = MathUtil.log2((int)Math.Floor((1 << ntdepth[0]) * 0.5 + (1 << ntdepth[1]) * 0.418701 + (1 << ntdepth[2]) * 0.081299) - 1) + 1;
                    break;
            }

            return tdepth;
        }

        /// <summary> Initialize some variables used with RCT. It must be called, at least,
        /// at the beginning of each new tile.</summary>
        private void initForwRCT()
        {
            int i;
            var tIdx = TileIdx;

            if (src.NumComps < 3)
            {
                throw new ArgumentException();
            }
            // Check that the 3 components have the same dimensions
            if (src.GetTileCompWidth(tIdx, 0) != src.GetTileCompWidth(tIdx, 1) || src.GetTileCompWidth(tIdx, 0) != src.GetTileCompWidth(tIdx, 2) || src.GetTileCompHeight(tIdx, 0) != src.GetTileCompHeight(tIdx, 1) || src.GetTileCompHeight(tIdx, 0) != src.GetTileCompHeight(tIdx, 2))
            {
                throw new ArgumentException("Can not use RCT " + "on components with different " + "dimensions");
            }
            // Initialize bitdepths
            int[] utd; // Premix bitdepths
            utd = new int[src.NumComps];
            for (i = utd.Length - 1; i >= 0; i--)
            {
                utd[i] = src.GetNomRangeBits(i);
            }
            tdepth = calcMixedBitDepths(utd, FORW_RCT, null);
        }

        /// <summary> Initialize some variables used with ICT. It must be called, at least,
        /// at the beginning of a new tile.</summary>
        private void initForwICT()
        {
            int i;
            var tIdx = TileIdx;

            if (src.NumComps < 3)
            {
                throw new ArgumentException();
            }
            // Check that the 3 components have the same dimensions
            if (src.GetTileCompWidth(tIdx, 0) != src.GetTileCompWidth(tIdx, 1) || src.GetTileCompWidth(tIdx, 0) != src.GetTileCompWidth(tIdx, 2) || src.GetTileCompHeight(tIdx, 0) != src.GetTileCompHeight(tIdx, 1) || src.GetTileCompHeight(tIdx, 0) != src.GetTileCompHeight(tIdx, 2))
            {
                throw new ArgumentException("Can not use ICT " + "on components with different " + "dimensions");
            }
            // Initialize bitdepths
            int[] utd; // Premix bitdepths
            utd = new int[src.NumComps];
            for (i = utd.Length - 1; i >= 0; i--)
            {
                utd[i] = src.GetNomRangeBits(i);
            }
            tdepth = calcMixedBitDepths(utd, FORW_ICT, null);
        }

        /// <summary>Returns a string with a descriptive text of which forward component
        /// transformation is used. This can be either "Forward RCT" or "Forward
        /// ICT" or "No component transformation" depending on the current tile.</summary>
        /// <returns>A descriptive string</returns>
        public override string ToString()
        {
            switch (transfType)
            {

                case FORW_RCT:
                    return "Forward RCT";

                case FORW_ICT:
                    return "Forward ICT";

                case NONE:
                    return "No component transformation";

                default:
                    throw new ArgumentException("Non JPEG 2000 part I" + " component transformation");

            }
        }

        /// <summary> Returns the number of bits, referred to as the "range bits",
        /// corresponding to the nominal range of the data in the specified
        /// component and in the current tile. If this number is <i>b</i> then for
        /// unsigned data the nominal range is between 0 and 2^b-1, and for signed
        /// data it is between -2^(b-1) and 2^(b-1)-1. Note that this value can be
        /// affected by the multiple component transform.</summary>
        /// <param name="compIndex">The index of the component.</param>
        /// <returns> The bitdepth of component 'c' after mixing.</returns>
        public override int GetNomRangeBits(int compIndex)
        {
            switch (transfType)
            {

                case FORW_RCT:
                case FORW_ICT:
                    return tdepth[compIndex];

                case NONE:
                    return src.GetNomRangeBits(compIndex);

                default:
                    throw new ArgumentException("Non JPEG 2000 part I" + " component transformation");

            }
        }

        /// <summary> Apply forward component transformation associated with the current
        /// tile. If no component transformation has been requested by the user,
        /// data are not modified.
        /// 
        /// This method calls the getInternCompData() method, but respects the
        /// definitions of the getCompData() method defined in the BlkImgDataSrc
        /// interface.</summary>
        /// <param name="blk">Determines the rectangular area to return, and the data is
        /// returned in this object.</param>
        /// <param name="c">Index of the output component.</param>
        /// <returns> The requested DataBlk</returns>
        /// <seealso cref="BlkImgDataSrc.GetCompData" />
        public virtual DataBlk GetCompData(DataBlk blk, int c)
        {
            // If requesting a component whose index is greater than 3 or there is
            // no transform return a copy of data (getInternCompData returns the
            // actual data in those cases)
            if (c >= 3 || transfType == NONE)
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
        /// image data is being read.</summary>
        public void Close()
        {
            // Do nothing.
        }

        /// <summary> Returns true if the data read was originally signed in the specified
        /// component, false if not.</summary>
        /// <param name="compIndex">The index of the component, from 0 to C-1.</param>
        /// <returns> true if the data was originally signed, false if not.</returns>
        public bool IsOrigSigned(int compIndex)
        {
            return false;
        }

        /// <summary> Apply the component transformation associated with the current tile. If
        /// no component transformation has been requested by the user, data are
        /// not modified. Else, appropriate method is called (forwRCT or forwICT).</summary>
        /// <seealso cref="forwRCT" />
        /// <seealso cref="forwICT" />
        /// <param name="blk">Determines the rectangular area to return.</param>
        /// <param name="compIndex">Index of the output component.</param>
        /// <returns> The requested DataBlk</returns>
        public virtual DataBlk GetInternCompData(DataBlk blk, int compIndex)
        {
            switch (transfType)
            {

                case NONE:
                    return src.GetInternCompData(blk, compIndex);

                case FORW_RCT:
                    return forwRCT(blk, compIndex);

                case FORW_ICT:
                    return forwICT(blk, compIndex);

                default:
                    throw new ArgumentException($"Non JPEG 2000 part 1 component transformation for tile: {tIdx}");

            }
        }

        /// <summary> Apply forward component transformation to obtain requested component
        /// from specified block of data. Whatever the type of requested DataBlk,
        /// it always returns a DataBlkInt.</summary>
        /// <param name="blk">Determine the rectangular area to return</param>
        /// <param name="c">The index of the requested component</param>
        /// <returns> Data of requested component</returns>
        private DataBlk forwRCT(DataBlk blk, int c)
        {
            int k, k0, k1, k2, mink, i;
            var w = blk.w; //width of output block
            var h = blk.h; //height of ouput block
            int[] outdata; //array of output data

            //If asking for Yr, Ur or Vr do transform
            if (c >= 0 && c <= 2)
            {
                // Check that request data type is int
                if (blk.DataType != DataBlk.TYPE_INT)
                {
                    if (outBlk == null || outBlk.DataType != DataBlk.TYPE_INT)
                    {
                        outBlk = new DataBlkInt();
                    }
                    outBlk.w = w;
                    outBlk.h = h;
                    outBlk.ulx = blk.ulx;
                    outBlk.uly = blk.uly;
                    blk = outBlk;
                }

                //Reference to output block data array
                outdata = (int[])blk.Data;

                //Create data array of blk if necessary
                if (outdata == null || outdata.Length < h * w)
                {
                    outdata = new int[h * w];
                    blk.Data = outdata;
                }

                // Block buffers for input RGB data
                int[] data0, data1, bdata; // input data arrays

                if (block0 == null)
                    block0 = new DataBlkInt();
                if (block1 == null)
                    block1 = new DataBlkInt();
                if (block2 == null)
                    block2 = new DataBlkInt();
                block0.w = block1.w = block2.w = blk.w;
                block0.h = block1.h = block2.h = blk.h;
                block0.ulx = block1.ulx = block2.ulx = blk.ulx;
                block0.uly = block1.uly = block2.uly = blk.uly;

                //Fill in buffer blocks (to be read only)
                // Returned blocks may have different size and position
                block0 = (DataBlkInt)src.GetInternCompData(block0, 0);
                if (block0 == null || block0.Data == null)
                {
                    throw new InvalidOperationException(
                        $"Component 0 data is null or invalid. Cannot perform forward RCT transform. " +
                        $"Block dimensions: ulx={blk.ulx}, uly={blk.uly}, w={blk.w}, h={blk.h}");
                }
                data0 = (int[])block0.Data;
                
                block1 = (DataBlkInt)src.GetInternCompData(block1, 1);
                if (block1 == null || block1.Data == null)
                {
                    throw new InvalidOperationException(
                        $"Component 1 data is null or invalid. Cannot perform forward RCT transform. " +
                        $"Block dimensions: ulx={blk.ulx}, uly={blk.uly}, w={blk.w}, h={blk.h}");
                }
                data1 = (int[])block1.Data;
                
                block2 = (DataBlkInt)src.GetInternCompData(block2, 2);
                if (block2 == null || block2.Data == null)
                {
                    throw new InvalidOperationException(
                        $"Component 2 data is null or invalid. Cannot perform forward RCT transform. " +
                        $"Block dimensions: ulx={blk.ulx}, uly={blk.uly}, w={blk.w}, h={blk.h}");
                }
                bdata = (int[])block2.Data;

                // Set the progressiveness of the output data
                blk.progressive = block0.progressive || block1.progressive || block2.progressive;
                blk.offset = 0;
                blk.scanw = w;

                //Perform conversion

                // Initialize general indexes
                k = w * h - 1;
                k0 = block0.offset + (h - 1) * block0.scanw + w - 1;
                k1 = block1.offset + (h - 1) * block1.scanw + w - 1;
                k2 = block2.offset + (h - 1) * block2.scanw + w - 1;

                switch (c)
                {

                    case 0:  //RGB to Yr conversion
                        for (i = h - 1; i >= 0; i--)
                        {
                            for (mink = k - w; k > mink; k--, k0--, k1--, k2--)
                            {
                                // Use int arithmetic with 12 fractional bits
                                // and rounding
                                outdata[k] = (data0[k] + 2 * data1[k] + bdata[k]) >> 2; // Same as / 4
                            }
                            // Jump to beggining of previous line in input
                            k0 -= (block0.scanw - w);
                            k1 -= (block1.scanw - w);
                            k2 -= (block2.scanw - w);
                        }
                        break;


                    case 1:  //RGB to Ur conversion
                        for (i = h - 1; i >= 0; i--)
                        {
                            for (mink = k - w; k > mink; k--, k1--, k2--)
                            {
                                // Use int arithmetic with 12 fractional bits
                                // and rounding
                                outdata[k] = bdata[k2] - data1[k1];
                            }
                            // Jump to beggining of previous line in input
                            k1 -= (block1.scanw - w);
                            k2 -= (block2.scanw - w);
                        }
                        break;


                    case 2:  //RGB to Vr conversion
                        for (i = h - 1; i >= 0; i--)
                        {
                            for (mink = k - w; k > mink; k--, k0--, k1--)
                            {
                                // Use int arithmetic with 12 fractional bits
                                // and rounding
                                outdata[k] = data0[k0] - data1[k1];
                            }
                            // Jump to beggining of previous line in input
                            k0 -= (block0.scanw - w);
                            k1 -= (block1.scanw - w);
                        }
                        break;
                }
            }
            else if (c >= 3)
            {
                // Requesting a component which is not Y, Ur or Vr =>
                // just pass the data            
                return src.GetInternCompData(blk, c);
            }
            else
            {
                // Requesting a non valid component index
                throw new ArgumentException();
            }
            return blk;
        }

        /// <summary> Apply forward irreversible component transformation to obtain requested
        /// component from specified block of data. Whatever the type of requested
        /// DataBlk, it always returns a DataBlkFloat.</summary>
        /// <param name="blk">Determine the rectangular area to return</param>
        /// <param name="c">The index of the requested component</param>
        /// <returns> Data of requested component</returns>
        private DataBlk forwICT(DataBlk blk, int c)
        {
            int k, k0, k1, k2, mink, i;
            var w = blk.w; //width of output block
            var h = blk.h; //height of ouput block
            float[] outdata; //array of output data

            if (blk.DataType != DataBlk.TYPE_FLOAT)
            {
                if (outBlk == null || outBlk.DataType != DataBlk.TYPE_FLOAT)
                {
                    outBlk = new DataBlkFloat();
                }
                outBlk.w = w;
                outBlk.h = h;
                outBlk.ulx = blk.ulx;
                outBlk.uly = blk.uly;
                blk = outBlk;
            }

            //Reference to output block data array
            outdata = (float[])blk.Data;

            //Create data array of blk if necessary
            if (outdata == null || outdata.Length < w * h)
            {
                outdata = new float[h * w];
                blk.Data = outdata;
            }

            //If asking for Y, Cb or Cr do transform
            if (c >= 0 && c <= 2)
            {

                int[] data0, data1, data2; // input data arrays

                if (block0 == null)
                {
                    block0 = new DataBlkInt();
                }
                if (block1 == null)
                {
                    block1 = new DataBlkInt();
                }
                if (block2 == null)
                {
                    block2 = new DataBlkInt();
                }
                block0.w = block1.w = block2.w = blk.w;
                block0.h = block1.h = block2.h = blk.h;
                block0.ulx = block1.ulx = block2.ulx = blk.ulx;
                block0.uly = block1.uly = block2.uly = blk.uly;

                // Returned blocks may have different size and position
                block0 = (DataBlkInt)src.GetInternCompData(block0, 0);
                if (block0 == null || block0.Data == null)
                {
                    throw new InvalidOperationException(
                        $"Component 0 data is null or invalid. Cannot perform forward ICT transform. " +
                        $"Block dimensions: ulx={blk.ulx}, uly={blk.uly}, w={blk.w}, h={blk.h}");
                }
                data0 = (int[])block0.Data;
                
                block1 = (DataBlkInt)src.GetInternCompData(block1, 1);
                if (block1 == null || block1.Data == null)
                {
                    throw new InvalidOperationException(
                        $"Component 1 data is null or invalid. Cannot perform forward ICT transform. " +
                        $"Block dimensions: ulx={blk.ulx}, uly={blk.uly}, w={blk.w}, h={blk.h}");
                }
                data1 = (int[])block1.Data;
                
                block2 = (DataBlkInt)src.GetInternCompData(block2, 2);
                if (block2 == null || block2.Data == null)
                {
                    throw new InvalidOperationException(
                        $"Component 2 data is null or invalid. Cannot perform forward ICT transform. " +
                        $"Block dimensions: ulx={blk.ulx}, uly={blk.uly}, w={blk.w}, h={blk.h}");
                }
                data2 = (int[])block2.Data;

                // Set the progressiveness of the output data
                blk.progressive = block0.progressive || block1.progressive || block2.progressive;
                blk.offset = 0;
                blk.scanw = w;

                //Perform conversion

                // Initialize general indexes
                k = w * h - 1;
                k0 = block0.offset + (h - 1) * block0.scanw + w - 1;
                k1 = block1.offset + (h - 1) * block1.scanw + w - 1;
                k2 = block2.offset + (h - 1) * block2.scanw + w - 1;

                switch (c)
                {

                    case 0:
                        //RGB to Y conversion
                        for (i = h - 1; i >= 0; i--)
                        {
                            for (mink = k - w; k > mink; k--, k0--, k1--, k2--)
                            {
                                outdata[k] = 0.299f * data0[k0] + 0.587f * data1[k1] + 0.114f * data2[k2];
                            }
                            // Jump to beggining of previous line in input
                            k0 -= (block0.scanw - w);
                            k1 -= (block1.scanw - w);
                            k2 -= (block2.scanw - w);
                        }
                        break;


                    case 1:
                        //RGB to Cb conversion
                        for (i = h - 1; i >= 0; i--)
                        {
                            for (mink = k - w; k > mink; k--, k0--, k1--, k2--)
                            {
                                outdata[k] = (-0.16875f) * data0[k0] - 0.33126f * data1[k1] + 0.5f * data2[k2];
                            }
                            // Jump to beginning of previous line in input
                            k0 -= (block0.scanw - w);
                            k1 -= (block1.scanw - w);
                            k2 -= (block2.scanw - w);
                        }
                        break;


                    case 2:
                        //RGB to Cr conversion
                        for (i = h - 1; i >= 0; i--)
                        {
                            for (mink = k - w; k > mink; k--, k0--, k1--, k2--)
                            {
                                outdata[k] = 0.5f * data0[k0] - 0.41869f * data1[k1] - 0.08131f * data2[k2];
                            }
                            // Jump to beginning of previous line in input
                            k0 -= (block0.scanw - w);
                            k1 -= (block1.scanw - w);
                            k2 -= (block2.scanw - w);
                        }
                        break;
                }
            }
            else if (c >= 3)
            {
                // Requesting a component which is not Y, Cb or Cr =>
                // just pass the data

                // Variables
                var indb = new DataBlkInt(blk.ulx, blk.uly, w, h);
                int[] indata; // input data array

                // Get the input data
                // (returned block may be larger than requested one)
                src.GetInternCompData(indb, c);
                indata = (int[])indb.Data;

                // Copy the data converting from int to float
                k = w * h - 1;
                k0 = indb.offset + (h - 1) * indb.scanw + w - 1;
                for (i = h - 1; i >= 0; i--)
                {
                    for (mink = k - w; k > mink; k--, k0--)
                    {
                        outdata[k] = indata[k0];
                    }
                    // Jump to beginning of next line in input
                    k0 += indb.w - w;
                }

                // Set the progressivity
                blk.progressive = indb.progressive;
                blk.offset = 0;
                blk.scanw = w;
            }
            else
            {
                // Requesting a non valid component index
                throw new ArgumentException();
            }
            return blk;
        }

        /// <summary> Changes the current tile, given the new indexes. An
        /// IllegalArgumentException is thrown if the indexes do not correspond to
        /// a valid tile.
        /// 
        /// This default implementation changes the tile in the source and
        /// re-initializes properly component transformation variables.</summary>
        /// <param name="x">The horizontal index of the tile.</param>
        /// <param name="y">The vertical index of the new tile.</param>
        public override void SetTile(int x, int y)
        {
            src.SetTile(x, y);
            tIdx = TileIdx; // index of the current tile

            // initializations
            var str = (string)cts.GetTileDef(tIdx);
            if (str.Equals("none"))
            {
                transfType = NONE;
            }
            else if (str.Equals("rct"))
            {
                transfType = FORW_RCT;
                initForwRCT();
            }
            else if (str.Equals("ict"))
            {
                transfType = FORW_ICT;
                initForwICT();
            }
            else
            {
                throw new ArgumentException("Component transformation not recognized");
            }
        }

        /// <summary> Goes to the next tile, in standard scan-line order (by rows then by
        /// columns). An NoNextElementException is thrown if the current tile is
        /// the last one (i.e. there is no next tile).
        /// 
        /// This default implementation just advances to the next tile in the
        /// source and re-initializes properly component transformation
        /// variables.</summary>
        public override void NextTile()
        {
            src.NextTile();
            tIdx = TileIdx; // index of the current tile

            // initializations
            var str = (string)cts.GetTileDef(tIdx);
            if (str.Equals("none"))
            {
                transfType = NONE;
            }
            else if (str.Equals("rct"))
            {
                transfType = FORW_RCT;
                initForwRCT();
            }
            else if (str.Equals("ict"))
            {
                transfType = FORW_ICT;
                initForwICT();
            }
            else
            {
                throw new ArgumentException("Component transformation not recognized");
            }
        }
    }
}