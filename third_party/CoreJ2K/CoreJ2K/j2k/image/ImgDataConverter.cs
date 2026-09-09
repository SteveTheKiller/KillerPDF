/*
* Interface:           ImgDataConverter
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
using System.Buffers;

namespace CoreJ2K.j2k.image
{

    /// <summary> This class is responsible of all data type conversions. It should be used,
    /// at encoder side, between Tiler and ForwardWT modules and, at decoder side,
    /// between InverseWT/CompDemixer and ImgWriter modules. The conversion is
    /// realized when a block of data is requested: if source and destination data
    /// type are the same one, it does nothing, else appropriate cast is done. All
    /// the methods of the 'ImgData' interface are implemented by the
    /// 'ImgDataAdapter' class that is the superclass of this one, so they don't
    /// need to be reimplemented by subclasses.
    /// 
    /// </summary>
    public class ImgDataConverter : ImgDataAdapter, BlkImgDataSrc
    {

        /// <summary>The block used to request data from the source in the case that a
        /// conversion seems necessary. It can be either int or float at
        /// initialization time. It will be checked (and corrected if necessary) by
        /// the source whenever necessary 
        /// </summary>
        private DataBlk srcBlk = new DataBlkInt();

        /// <summary>The source of image data </summary>
        private readonly BlkImgDataSrc src;

        /// <summary>The number of fraction bits in the casted ints </summary>
        private int fp;

        // Grow-only scratch buffers for type-conversion paths.
        // Avoids a new float[]/int[] allocation per block on every INT↔FLOAT conversion.
        private float[]? _scratchFloat;
        private int[]? _scratchInt;

        /// <summary> Constructs a new ImgDataConverter object that operates on the specified
        /// source of image data.
        /// 
        /// </summary>
        /// <param name="imgSrc">The source from where to get the data to be transformed
        /// 
        /// </param>
        /// <param name="fp">The number of fraction bits in the casted ints
        /// 
        /// </param>
        /// <seealso cref="BlkImgDataSrc">
        /// 
        /// </seealso>
        public ImgDataConverter(BlkImgDataSrc imgSrc, int fp)
            : base(imgSrc)
        {
            src = imgSrc;
            this.fp = fp;
        }

        /// <summary> Constructs a new ImgDataConverter object that operates on the specified
        /// source of image data.
        /// 
        /// </summary>
        /// <param name="imgSrc">The source from where to get the data to be transformed
        /// 
        /// </param>
        /// <seealso cref="BlkImgDataSrc">
        /// 
        /// </seealso>
        public ImgDataConverter(BlkImgDataSrc imgSrc)
            : base(imgSrc)
        {
            src = imgSrc;
            fp = 0;
        }

        /// <summary> Returns the position of the fixed point in the specified
        /// component. This is the position of the least significant integral
        /// (i.e. non-fractional) bit, which is equivalent to the number of
        /// fractional bits. For instance, for fixed-point values with 2 fractional
        /// bits, 2 is returned. For floating-point data this value does not apply
        /// and 0 should be returned. Position 0 is the position of the least
        /// significant bit in the data.
        /// 
        /// </summary>
        /// <param name="compIndex">The index of the component.
        /// 
        /// </param>
        /// <returns> The position of the fixed-point, which is the same as the
        /// number of fractional bits.
        /// 
        /// </returns>
        public virtual int GetFixedPoint(int compIndex)
        {
            return fp;
        }

        /// <summary> Returns, in the blk argument, a block of image data containing the
        /// specifed rectangular area, in the specified component, using the
        /// 'transfer type' specified in the block given as argument. The data is
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
        public virtual DataBlk GetCompData(DataBlk blk, int c)
        {
            return GetData(blk, c, false);
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
        /// specifed rectangular area, in the specified component, using the
        /// 'transfer type' defined in the block given as argument. The data is
        /// returned, as a reference to the internal data, if any, instead of as a
        /// copy, therefore the returned data should not be modified.
        /// 
        /// The rectangular area to return is specified by the 'ulx', 'uly', 'w'
        /// and 'h' members of the 'blk' argument, relative to the current
        /// tile. These members are not modified by this method. The 'offset' and
        /// 'scanw' of the returned data can be arbitrary. See the 'DataBlk' class.
        /// 
        ///  If source data and expected data (blk) are using the same type,
        /// block returned without any modification. If not appropriate cast is
        /// used.
        /// 
        /// This method, in general, is more efficient than the 'getCompData()'
        /// method since it may not copy the data. However if the array of returned
        /// data is to be modified by the caller then the other method is probably
        /// preferable.
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
        public DataBlk GetInternCompData(DataBlk blk, int compIndex)
        {
            return GetData(blk, compIndex, true);
        }

        /// <summary> Implements the 'getInternCompData()' and the 'getCompData()'
        /// methods. The 'intern' flag signals which of the two methods should run
        /// as.
        /// 
        /// </summary>
        /// <param name="blk">The data block to get.
        /// 
        /// </param>
        /// <param name="c">The index of the component from which to get the data.
        /// 
        /// </param>
        /// <param name="intern">If true behave as 'getInternCompData(). Otherwise behave
        /// as 'getCompData()'
        /// 
        /// </param>
        /// <returns> The requested data block
        /// 
        /// </returns>
        /// <seealso cref="GetInternCompData">
        /// 
        /// </seealso>
        /// <seealso cref="GetCompData">
        /// 
        /// </seealso>
        private DataBlk GetData(DataBlk blk, int c, bool intern)
        {
            DataBlk reqBlk; // Reference to block used in request to source

            // Keep request data type
            var otype = blk.DataType;

            if (otype == srcBlk.DataType)
            {
                // Probably requested type is same as source type
                reqBlk = blk;
            }
            else
            {
                // Probably requested type is not the same as source type
                reqBlk = srcBlk;
                // We need to copy requested coordinates and size
                reqBlk.ulx = blk.ulx;
                reqBlk.uly = blk.uly;
                reqBlk.w = blk.w;
                reqBlk.h = blk.h;
            }

            // Get source data block
            // We can use the intern variant
            srcBlk = intern ? src.GetInternCompData(reqBlk, c) :
                // Do not use the intern variant. Note that this is not optimal
                // since if we are going to convert below then we could have used
                // the intern variant. But there is currently no way to know if we
                // will need to do conversion or not before getting the data.
                src.GetCompData(reqBlk, c);

            // Check if casting is needed
            if (srcBlk.DataType == otype)
            {
                return srcBlk;
            }

            int i;
            int k, kSrc, kmin;
            float mult;
            var w = srcBlk.w;
            var h = srcBlk.h;

            switch (otype)
            {

                case DataBlk.TYPE_FLOAT: // Cast INT -> FLOAT

                    float[] farr;
                    int[] srcIArr;

                    // Get data array from resulting blk.
                    // Prefer caller's existing buffer; fall back to grow-only scratch
                    // to avoid per-block allocation.
                    farr = (float[])blk.Data;
                    if (farr == null || farr.Length < w * h)
                    {
                        int needF = w * h;
                        if (_scratchFloat == null || _scratchFloat.Length < needF)
                            _scratchFloat = new float[needF];
                        farr = _scratchFloat;
                        blk.Data = farr;
                    }

                    blk.scanw = srcBlk.w;
                    blk.offset = 0;
                    blk.progressive = srcBlk.progressive;
                    srcIArr = (int[])srcBlk.Data;

                    // Cast data from source to blk
                    fp = src.GetFixedPoint(c);
                    if (fp != 0)
                    {
                        mult = 1.0f / (1 << fp);
                        for (i = h - 1, k = w * h - 1, kSrc = srcBlk.offset + (h - 1) * srcBlk.scanw + w - 1;
                             i >= 0;
                             i--)
                        {
                            for (kmin = k - w; k > kmin; k--, kSrc--)
                            {
                                farr[k] = ((srcIArr[kSrc] * mult));
                            }
                            // Jump to geggining of next line in source
                            kSrc -= (srcBlk.scanw - w);
                        }
                    }
                    else
                    {
                        for (i = h - 1, k = w * h - 1, kSrc = srcBlk.offset + (h - 1) * srcBlk.scanw + w - 1;
                             i >= 0;
                             i--)
                        {
                            for (kmin = k - w; k > kmin; k--, kSrc--)
                            {
                                farr[k] = srcIArr[kSrc];
                            }
                            // Jump to geggining of next line in source
                            kSrc -= (srcBlk.scanw - w);
                        }
                    }
                    break; // End of cast INT-> FLOAT


                case DataBlk.TYPE_INT: // cast FLOAT -> INT
                    int[] iarr;
                    float[] srcFArr;

                    // Get data array from resulting blk.
                    // Prefer caller's existing buffer; fall back to grow-only scratch
                    // to avoid per-block allocation.
                    iarr = (int[])blk.Data;
                    if (iarr == null || iarr.Length < w * h)
                    {
                        int needI = w * h;
                        if (_scratchInt == null || _scratchInt.Length < needI)
                            _scratchInt = new int[needI];
                        iarr = _scratchInt;
                        blk.Data = iarr;
                    }
                    blk.scanw = srcBlk.w;
                    blk.offset = 0;
                    blk.progressive = srcBlk.progressive;
                    srcFArr = (float[])srcBlk.Data;

                    // Cast data from source to blk
                    if (fp != 0)
                    {
                        mult = 1 << fp;
                        for (i = h - 1, k = w * h - 1, kSrc = srcBlk.offset + (h - 1) * srcBlk.scanw + w - 1;
                             i >= 0;
                             i--)
                        {
                            for (kmin = k - w; k > kmin; k--, kSrc--)
                            {
                                if (srcFArr[kSrc] > 0.0f)
                                {
                                    iarr[k] = (int)(srcFArr[kSrc] * mult + 0.5f);
                                }
                                else
                                {
                                    iarr[k] = (int)(srcFArr[kSrc] * mult - 0.5f);
                                }
                            }
                            // Jump to geggining of next line in source
                            kSrc -= (srcBlk.scanw - w);
                        }
                    }
                    else
                    {
                        for (i = h - 1, k = w * h - 1, kSrc = srcBlk.offset + (h - 1) * srcBlk.scanw + w - 1;
                             i >= 0;
                             i--)
                        {
                            for (kmin = k - w; k > kmin; k--, kSrc--)
                            {
                                if (srcFArr[kSrc] > 0.0f)
                                {
                                    iarr[k] = (int)(srcFArr[kSrc] + 0.5f);
                                }
                                else
                                {
                                    iarr[k] = (int)(srcFArr[kSrc] - 0.5f);
                                }
                            }
                            // Jump to geggining of next line in source
                            kSrc -= (srcBlk.scanw - w);
                        }
                    }
                    break; // End cast FLOAT -> INT

                default:
                    throw new ArgumentException("Only integer and float data are supported by JJ2000");

            }
            return blk;
        }
    }
}
