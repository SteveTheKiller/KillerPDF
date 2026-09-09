/* 
* 
*                          the InvWTFullInt and InvWTFullFloat
*                          classes by Bertrand Berthelot, Apr-19-1999
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
using CoreJ2K.j2k.decoder;
using CoreJ2K.j2k.image;
using System;
using System.Collections.Generic;
using System.Buffers;

namespace CoreJ2K.j2k.wavelet.synthesis
{

    /// <summary> This class implements the InverseWT with the full-page approach for int and
    /// float data.
    /// 
    /// The image can be reconstructed at different (image) resolution levels
    /// indexed from the lowest resolution available for each tile-component. This
    /// is controlled by the setImgResLevel() method.
    /// 
    /// Note: Image resolution level indexes may differ from tile-component
    /// resolution index. They are indeed indexed starting from the lowest number
    /// of decomposition levels of each component of each tile.
    /// 
    /// Example: For an image (1 tile) with 2 components (component 0 having 2
    /// decomposition levels and component 1 having 3 decomposition levels), the
    /// first (tile-) component has 3 resolution levels and the second one has 4
    /// resolution levels, whereas the image has only 3 resolution levels
    /// available.
    /// 
    /// This implementation does not support progressive data: Data is
    /// considered to be non-progressive (i.e. "final" data) and the 'progressive'
    /// attribute of the 'DataBlk' class is always set to false, see the 'DataBlk'
    /// class.
    /// 
    /// </summary>
    /// <seealso cref="DataBlk" />
    public class InvWTFull : InverseWT
    {

        /// <summary>The total number of code-blocks to decode </summary>
        private int cblkToDecode = 0;

        /// <summary>the code-block buffer's source i.e. the quantizer </summary>
        private readonly CBlkWTDataSrcDec src;

        /// <summary>Current data type </summary>
        private int dtype;

        /// <summary>Block storing the reconstructed image for each component </summary>
        private readonly DataBlk[] reconstructedComps;

        /// <summary>Number of decomposition levels in each component </summary>
        private readonly int[] ndl;

        // Rented buffers for reconstructed components to avoid LOH allocations
        private float[]?[] rentedFloatBuffers;
        private int[]?[] rentedIntBuffers;

        // Cached DataBlk wrappers for waveletTreeReconstruction leaf nodes – avoids per-subband allocation
        private DataBlkInt? _subbDataInt;
        private DataBlkFloat? _subbDataFloat;

        // Grow-only scratch line buffers for wavelet2DReconstruction.
        // Sized to max(w,h) of the largest subband seen so far; never returned to a pool,
        // so every subband reconstruction reuses the same allocation with zero rent/return overhead.
        private int[]?   _waveletScratchInt;
        private float[]? _waveletScratchFloat;

        /// <summary>
        /// Configures whether ArrayPool buffers should be cleared when returned.
        /// Setting this to true improves security by preventing sensitive image data 
        /// from remaining in memory, but has a small performance cost.
        /// 
        /// Default: false (prioritizes performance)
        /// Recommended: true for sensitive images (medical, personal photos, etc.)
        /// </summary>
        public static bool ClearArrayPoolBuffersOnReturn { get; set; } = false;

        /// <summary> The reversible flag for each component in each tile. The first index is
        /// the tile index, the second one is the component index. The
        /// reversibility of the components for each tile are calculated on a as
        /// needed basis.
        /// 
        /// </summary>
        private readonly Dictionary<int, bool[]> reversible = new Dictionary<int, bool[]>();
        //private bool[][] reversible;

        /// <summary> Initializes this object with the given source of wavelet
        /// coefficients. It initializes the resolution level for full resolutioin
        /// reconstruction.
        /// 
        /// </summary>
        /// <param name="src">from where the wavelet coefficinets should be obtained.
        /// 
        /// </param>
        /// <param name="decSpec">The decoder specifications
        /// 
        /// </param>
        public InvWTFull(CBlkWTDataSrcDec src, DecoderSpecs decSpec) : base(src, decSpec)
        {
            this.src = src;
            var nc = src.NumComps;
            reconstructedComps = new DataBlk[nc];
            ndl = new int[nc];
            rentedFloatBuffers = new float[nc][];
            rentedIntBuffers = new int[nc][];
        }

        /// <summary> Returns the reversibility of the current subband. It computes
        /// iteratively the reversibility of the child subbands. For each subband
        /// it tests the reversibility of the horizontal and vertical synthesis
        /// filters used to reconstruct this subband.
        /// 
        /// </summary>
        /// <param name="subband">The current subband.
        /// 
        /// </param>
        /// <returns> true if all the  filters used to reconstruct the current 
        /// subband are reversible
        /// 
        /// </returns>
        private bool IsSubbandReversible(Subband? subband)
        {
            if (subband == null) return true;
            if (subband.isNode)
            {
                // It's reversible if the filters to obtain the 4 subbands are
                // reversible and the ones for this one are reversible too.
                return IsSubbandReversible(subband.LL) && IsSubbandReversible(subband.HL) && IsSubbandReversible(subband.LH) && IsSubbandReversible(subband.HH) && ((SubbandSyn)subband).hFilter!.Reversible && ((SubbandSyn)subband).vFilter!.Reversible;
            }
            else
            {
                // Leaf subband. Reversibility of data depends on source, so say
                // it's true
                return true;
            }
        }

        /// <summary> Returns the reversibility of the wavelet transform for the specified
        /// component, in the current tile. A wavelet transform is reversible when
        /// it is suitable for lossless and lossy-to-lossless compression.
        /// 
        /// </summary>
        /// <param name="t">The index of the tile.
        /// 
        /// </param>
        /// <param name="c">The index of the component.
        /// 
        /// </param>
        /// <returns> true is the wavelet transform is reversible, false if not.
        /// 
        /// </returns>
        public override bool IsReversible(int t, int c)
        {
            if (reversible[t] == null)
            {
                // Reversibility not yet calculated for this tile
                reversible[t] = new bool[NumComps];
                for (var i = reversible[t].Length - 1; i >= 0; i--)
                {
                    reversible[t][i] = IsSubbandReversible(src.GetSynSubbandTree(t, i));
                }
            }
            return reversible[t][c];
        }

        /// <summary> Returns the number of bits, referred to as the "range bits",
        /// corresponding to the nominal range of the data in the specified
        /// component.
        /// 
        /// The returned value corresponds to the nominal dynamic range of the
        /// reconstructed image data, as long as the GetNomRangeBits() method of
        /// the source returns a value corresponding to the nominal dynamic range
        /// of the image data and not not of the wavelet coefficients.
        /// 
        /// If this number is <i>b</b> then for unsigned data the nominal range
        /// is between 0 and 2^b-1, and for signed data it is between -2^(b-1) and
        /// 2^(b-1)-1.
        /// 
        /// </summary>
        /// <param name="compIndex">The index of the component.
        /// 
        /// </param>
        /// <returns> The number of bits corresponding to the nominal range of the
        /// data.
        /// 
        /// </returns>
        public override int GetNomRangeBits(int compIndex)
        {
            return src.GetNomRangeBits(compIndex);
        }

        /// <summary> Returns the position of the fixed point in the specified
        /// component. This is the position of the least significant integral
        /// (i.e. non-fractional) bit, which is equivalent to the number of
        /// fractional bits. For instance, for fixed-point values with 2 fractional
        /// bits, 2 is returned. For floating-point data this value does not apply
        /// and 0 should be returned. Position 0 is the position of the least
        /// significant bit in the data.
        /// 
        /// This default implementation assumes that the wavelet transform does
        /// not modify the fixed point. If that were the case this method should be
        /// overriden.
        /// 
        /// </summary>
        /// <param name="compIndex">The index of the component.
        /// 
        /// </param>
        /// <returns> The position of the fixed-point, which is the same as the
        /// number of fractional bits. For floating-point data 0 is returned.
        /// 
        /// </returns>
        public override int GetFixedPoint(int compIndex)
        {
            return src.GetFixedPoint(compIndex);
        }

        /// <summary> Returns a block of image data containing the specifed rectangular area,
        /// in the specified component, as a reference to the internal buffer (see
        /// below). The rectangular area is specified by the coordinates and
        /// dimensions of the 'blk' object.
        /// 
        /// The area to return is specified by the 'ulx', 'uly', 'w' and 'h'
        /// members of the 'blk' argument. These members are not modified by this
        /// method.
        /// 
        /// The data returned by this method can be the data in the internal
        /// buffer of this object, if any, and thus can not be modified by the
        /// caller. The 'offset' and 'scanw' of the returned data can be
        /// arbitrary. See the 'DataBlk' class.
        /// 
        /// The returned data has its 'progressive' attribute unset
        /// (i.e. false).
        /// 
        /// </summary>
        /// <param name="blk">Its coordinates and dimensions specify the area to return.
        /// 
        /// </param>
        /// <param name="compIndex">The index of the component from which to get the data.
        /// 
        /// </param>
        /// <returns> The requested DataBlk
        /// 
        /// </returns>
        /// <seealso cref="GetInternCompData" />
        public override DataBlk GetInternCompData(DataBlk blk, int compIndex)
        {
            var tIdx = TileIdx;

            //If the source image has not been decomposed (or was invalidated by a tile change)
            if (reconstructedComps[compIndex] == null || reconstructedComps[compIndex].Data == null)
            {
                // Call GetSynSubbandTree exactly once on the slow path; reuse for both
                // dtype determination and waveletTreeReconstruction — avoids the extra
                // virtual dispatch that was occurring on every call in the original code.
                var synTree = src.GetSynSubbandTree(tIdx, compIndex);
                dtype = synTree.HorWFilter! == null ? DataBlk.TYPE_INT : synTree.HorWFilter!.DataType;

                //Allocate component data buffer
                switch (dtype)
                {

                    case DataBlk.TYPE_FLOAT:
                        var fwidth = GetTileCompWidth(tIdx, compIndex);
                        var fheight = GetTileCompHeight(tIdx, compIndex);

                        // Validate dimensions to prevent integer overflow
                        long fBufferSize = (long)fwidth * fheight;
                        if (fBufferSize > int.MaxValue)
                        {
                            throw new InvalidOperationException(
                                $"Tile component too large for float reconstruction: " +
                                $"w={fwidth}, h={fheight}. " +
                                $"Buffer size {fBufferSize} exceeds maximum array size.");
                        }

                        // Reuse the existing wrapper if it has the right type; otherwise create a
                        // new one using the no-arg ctor so no internal float[] is allocated.
                        if (reconstructedComps[compIndex] is DataBlkFloat existingFloat)
                        {
                            existingFloat.ulx = 0; existingFloat.uly = 0;
                            existingFloat.w = fwidth; existingFloat.h = fheight;
                            existingFloat.offset = 0; existingFloat.scanw = fwidth;
                        }
                        else
                        {
                            var dbf = new DataBlkFloat();
                            dbf.ulx = 0; dbf.uly = 0;
                            dbf.w = fwidth; dbf.h = fheight;
                            dbf.offset = 0; dbf.scanw = fwidth;
                            reconstructedComps[compIndex] = dbf;
                        }
                        try
                        {
                            var rent = ArrayPool<float>.Shared.Rent((int)fBufferSize);
                            reconstructedComps[compIndex].Data = rent;
                            rentedFloatBuffers[compIndex] = rent;
                        }
                        catch
                        {
                            // fallback to default allocation if renting fails
                            reconstructedComps[compIndex].Data = new float[(int)fBufferSize];
                        }
                        break;

                    case DataBlk.TYPE_INT:
                        var iwidth = GetTileCompWidth(tIdx, compIndex);
                        var iheight = GetTileCompHeight(tIdx, compIndex);

                        // Validate dimensions to prevent integer overflow
                        long iBufferSize = (long)iwidth * iheight;
                        if (iBufferSize > int.MaxValue)
                        {
                            throw new InvalidOperationException(
                                $"Tile component too large for int reconstruction: " +
                                $"w={iwidth}, h={iheight}. " +
                                $"Buffer size {iBufferSize} exceeds maximum array size.");
                        }

                        // Reuse the existing wrapper if it has the right type; otherwise create a
                        // new one using the no-arg ctor so no internal int[] is allocated.
                        if (reconstructedComps[compIndex] is DataBlkInt existingInt)
                        {
                            existingInt.ulx = 0; existingInt.uly = 0;
                            existingInt.w = iwidth; existingInt.h = iheight;
                            existingInt.offset = 0; existingInt.scanw = iwidth;
                        }
                        else
                        {
                            var dbi = new DataBlkInt();
                            dbi.ulx = 0; dbi.uly = 0;
                            dbi.w = iwidth; dbi.h = iheight;
                            dbi.offset = 0; dbi.scanw = iwidth;
                            reconstructedComps[compIndex] = dbi;
                        }
                        try
                        {
                            var irent = ArrayPool<int>.Shared.Rent((int)iBufferSize);
                            reconstructedComps[compIndex].Data = irent;
                            rentedIntBuffers[compIndex] = irent;
                        }
                        catch
                        {
                            // fallback to default allocation
                            reconstructedComps[compIndex].Data = new int[(int)iBufferSize];
                        }
                        break;
                }
                //Reconstruct source image — reuse the synTree reference already fetched above
                waveletTreeReconstruction(reconstructedComps[compIndex], synTree, compIndex);
            }
            else
            {
                // Fast path: reconstruction already cached. Derive dtype from the concrete
                // type of the cached block — zero virtual calls to GetSynSubbandTree.
                dtype = reconstructedComps[compIndex] is DataBlkInt ? DataBlk.TYPE_INT : DataBlk.TYPE_FLOAT;
            }

            if (blk.DataType != dtype)
            {
                if (dtype == DataBlk.TYPE_INT)
                {
                    blk = new DataBlkInt(blk.ulx, blk.uly, blk.w, blk.h);
                }
                else
                {
                    blk = new DataBlkFloat(blk.ulx, blk.uly, blk.w, blk.h);
                }
            }
            // Set the reference to the internal buffer
            blk.Data = reconstructedComps[compIndex].Data;
            blk.offset = reconstructedComps[compIndex].w * blk.uly + blk.ulx;
            blk.scanw = reconstructedComps[compIndex].w;
            blk.progressive = false;
            return blk;
        }

        /// <summary> Returns a block of image data containing the specifed rectangular area,
        /// in the specified component, as a copy (see below). The rectangular area
        /// is specified by the coordinates and dimensions of the 'blk' object.
        /// 
        /// The area to return is specified by the 'ulx', 'uly', 'w' and 'h'
        /// members of the 'blk' argument. These members are not modified by this
        /// method.
        /// 
        /// The data returned by this method is always a copy of the internal
        /// data of this object, if any, and it can be modified "in place" without
        /// any problems after being returned. The 'offset' of the returned data is
        /// 0, and the 'scanw' is the same as the block's width. See the 'DataBlk'
        /// class.
        /// 
        /// If the data array in 'blk' is <tt>null</tt>, then a new one is
        /// created. If the data array is not <tt>null</tt> then it must be big
        /// enough to contain the requested area.
        /// 
        /// The returned data always has its 'progressive' attribute unset (i.e
        /// false)
        /// 
        /// </summary>
        /// <param name="blk">Its coordinates and dimensions specify the area to
        /// return. If it contains a non-null data array, then it must be large
        /// enough. If it contains a null data array a new one is created. The
        /// fields in this object are modified to return the data.
        /// 
        /// </param>
        /// <param name="c">The index of the component from which to get the data.
        /// 
        /// </param>
        /// <returns> The requested DataBlk
        /// 
        /// </returns>
        /// <seealso cref="GetCompData" />
        public override DataBlk GetCompData(DataBlk blk, int c)
        {
            //int j;
            // dst_data declared below
            int[]? dst_data_int; // src_data_int removed
            float[]? dst_data_float; // src_data_float removed

            // To keep compiler happy
            object? dst_data = null;

            // Ensure output buffer
            switch (blk.DataType)
            {

                case DataBlk.TYPE_INT:
                    // Validate dimensions to prevent integer overflow
                    long intBufferSize = (long)blk.w * blk.h;
                    if (intBufferSize > int.MaxValue)
                    {
                        throw new InvalidOperationException(
                            $"Block dimensions too large for int buffer: " +
                            $"w={blk.w}, h={blk.h}. " +
                            $"Buffer size {intBufferSize} exceeds maximum array size.");
                    }
                    
                    dst_data_int = (int[]?)blk.Data;
                    if (dst_data_int == null || dst_data_int.Length < intBufferSize)
                    {
                        dst_data_int = new int[(int)intBufferSize];
                    }
                    dst_data = dst_data_int;
                    break;

                case DataBlk.TYPE_FLOAT:
                    // Validate dimensions to prevent integer overflow
                    long floatBufferSize = (long)blk.w * blk.h;
                    if (floatBufferSize > int.MaxValue)
                    {
                        throw new InvalidOperationException(
                            $"Block dimensions too large for float buffer: " +
                            $"w={blk.w}, h={blk.h}. " +
                            $"Buffer size {floatBufferSize} exceeds maximum array size.");
                    }
                    
                    dst_data_float = (float[]?)blk.Data;
                    if (dst_data_float == null || dst_data_float.Length < floatBufferSize)
                    {
                        dst_data_float = new float[(int)floatBufferSize];
                    }
                    dst_data = dst_data_float;
                    break;
            }

            // Use getInternCompData() to get the data, since getInternCompData()
            // returns reference to internal buffer, we must copy it.
            blk = GetInternCompData(blk, c);

            // Copy the data
            blk.Data = dst_data;
            blk.offset = 0;
            blk.scanw = blk.w;
            return blk;
        }

        /// <summary> Performs the 2D inverse wavelet transform on a subband of the image, on
        /// the specified component. This method will successively perform 1D
        /// filtering steps on all columns and then all lines of the subband.
        /// 
        /// </summary>
        /// <param name="db">the buffer for the image/wavelet data.
        /// 
        /// </param>
        /// <param name="sb">The subband to reconstruct.
        /// 
        /// </param>
        /// <param name="c">The index of the component to reconstruct 
        /// 
        /// </param>
        private void wavelet2DReconstruction(DataBlk db, SubbandSyn sb, int c)
        {
            object? data;
            object? buf;
            int ulx, uly, w, h;
            int i, j, k;
            int offset;

            // If subband is empty (i.e. zero size) nothing to do
            if (sb.w == 0 || sb.h == 0)
            {
                return;
            }

            data = db.Data;

            ulx = sb.ulx;
            uly = sb.uly;
            w = sb.w;
            h = sb.h;

            buf = null; // To keep compiler happy

            // Fast path: both filters are the concrete 5x3 int type – call typed methods
            // directly to eliminate the object-typed virtual dispatch chain and the slower
            // Array.Copy(Array,...) overload used in the generic fallback.
            if (sb.hFilter is SynWTFilterIntLift5x3 hf5x3 && sb.vFilter is SynWTFilterIntLift5x3 vf5x3)
            {
                int[] data_int5x3 = (int[])data!;
                int need5x3 = (w >= h) ? w : h;
                if (_waveletScratchInt == null || _waveletScratchInt.Length < need5x3)
                    _waveletScratchInt = new int[need5x3];
                int[] buf_int5x3 = _waveletScratchInt;
                {
                    // Hoist loop-invariant half-lengths
                    int wHalf = w / 2, wHalfCeil = (w + 1) / 2;
                    int hHalf = h / 2, hHalfCeil = (h + 1) / 2;

                    // Horizontal reconstruction
                    offset = (uly - db.uly) * db.w + ulx - db.ulx;
                    if (sb.ulcx % 2 == 0)
                    {
                        for (i = 0; i < h; i++, offset += db.w)
                        {
                            new ReadOnlySpan<int>(data_int5x3, offset, w).CopyTo(buf_int5x3);
                            hf5x3.synthetize_lpf(buf_int5x3, 0, wHalfCeil, 1, buf_int5x3, wHalfCeil, wHalf, 1, data_int5x3!, offset, 1);
                        }
                    }
                    else
                    {
                        for (i = 0; i < h; i++, offset += db.w)
                        {
                            new ReadOnlySpan<int>(data_int5x3, offset, w).CopyTo(buf_int5x3);
                            hf5x3.synthetize_hpf(buf_int5x3, 0, wHalf, 1, buf_int5x3, wHalf, wHalfCeil, 1, data_int5x3!, offset, 1);
                        }
                    }

                    // Vertical reconstruction — forward gather for hardware-prefetch friendliness
                    offset = (uly - db.uly) * db.w + ulx - db.ulx;
                    if (sb.ulcy % 2 == 0)
                    {
                        for (j = 0; j < w; j++, offset++)
                        {
                            for (i = 0, k = offset; i < h; i++, k += db.w)
                                buf_int5x3[i] = data_int5x3[k];
                            vf5x3.synthetize_lpf(buf_int5x3, 0, hHalfCeil, 1, buf_int5x3, hHalfCeil, hHalf, 1, data_int5x3!, offset, db.w);
                        }
                    }
                    else
                    {
                        for (j = 0; j < w; j++, offset++)
                        {
                            for (i = 0, k = offset; i < h; i++, k += db.w)
                                buf_int5x3[i] = data_int5x3[k];
                            vf5x3.synthetize_hpf(buf_int5x3, 0, hHalf, 1, buf_int5x3, hHalf, hHalfCeil, 1, data_int5x3!, offset, db.w);
                        }
                    }
                }
                return;
            }

            // Fast path: both filters are the concrete 9x7 float type – call typed sealed
            // methods directly to eliminate the object-typed virtual dispatch chain.
            if (sb.hFilter is SynWTFilterFloatLift9x7 hf9x7 && sb.vFilter is SynWTFilterFloatLift9x7 vf9x7)
            {
                float[] data_float = (float[])data!;
                int need9x7 = (w >= h) ? w : h;
                if (_waveletScratchFloat == null || _waveletScratchFloat.Length < need9x7)
                    _waveletScratchFloat = new float[need9x7];
                float[] buf_float = _waveletScratchFloat;
                {
                    // Hoist loop-invariant half-lengths
                    int wHalf9 = w / 2, wHalfCeil9 = (w + 1) / 2;
                    int hHalf9 = h / 2, hHalfCeil9 = (h + 1) / 2;

                    // Horizontal reconstruction
                    offset = (uly - db.uly) * db.w + ulx - db.ulx;
                    if (sb.ulcx % 2 == 0)
                    {
                        for (i = 0; i < h; i++, offset += db.w)
                        {
                            new ReadOnlySpan<float>(data_float, offset, w).CopyTo(buf_float);
                            hf9x7.synthetize_lpf(buf_float, 0, wHalfCeil9, 1, buf_float, wHalfCeil9, wHalf9, 1, data_float, offset, 1);
                        }
                    }
                    else
                    {
                        for (i = 0; i < h; i++, offset += db.w)
                        {
                            new ReadOnlySpan<float>(data_float, offset, w).CopyTo(buf_float);
                            hf9x7.synthetize_hpf(buf_float, 0, wHalf9, 1, buf_float, wHalf9, wHalfCeil9, 1, data_float, offset, 1);
                        }
                    }

                    // Vertical reconstruction — forward gather for hardware-prefetch friendliness
                    offset = (uly - db.uly) * db.w + ulx - db.ulx;
                    if (sb.ulcy % 2 == 0)
                    {
                        for (j = 0; j < w; j++, offset++)
                        {
                            for (i = 0, k = offset; i < h; i++, k += db.w)
                                buf_float[i] = data_float[k];
                            vf9x7.synthetize_lpf(buf_float, 0, hHalfCeil9, 1, buf_float, hHalfCeil9, hHalf9, 1, data_float, offset, db.w);
                        }
                    }
                    else
                    {
                        for (j = 0; j < w; j++, offset++)
                        {
                            for (i = 0, k = offset; i < h; i++, k += db.w)
                                buf_float[i] = data_float[k];
                            vf9x7.synthetize_hpf(buf_float, 0, hHalf9, 1, buf_float, hHalf9, hHalfCeil9, 1, data_float, offset, db.w);
                        }
                    }
                }
                return;
            }

            int needGen = (w >= h) ? w : h;
            switch (sb.HorWFilter!.DataType)
            {

                case DataBlk.TYPE_INT:
                    if (_waveletScratchInt == null || _waveletScratchInt.Length < needGen)
                        _waveletScratchInt = new int[needGen];
                    buf = _waveletScratchInt;
                    break;

                case DataBlk.TYPE_FLOAT:
                    if (_waveletScratchFloat == null || _waveletScratchFloat.Length < needGen)
                        _waveletScratchFloat = new float[needGen];
                    buf = _waveletScratchFloat;
                    break;
            }

            try
            {
                int wHalfG = w / 2, wHalfCeilG = (w + 1) / 2;
                int hHalfG = h / 2, hHalfCeilG = (h + 1) / 2;

                //Perform the horizontal reconstruction
                offset = (uly - db.uly) * db.w + ulx - db.ulx;
                if (sb.ulcx % 2 == 0)
                {
                    // start index is even => use LPF
                    for (i = 0; i < h; i++, offset += db.w)
                    {
                        Array.Copy((Array)data!, offset, (Array)buf!, 0, w);
                        sb.hFilter!.synthetize_lpf(buf, 0, wHalfCeilG, 1, buf, wHalfCeilG, wHalfG, 1, data, offset, 1);
                    }
                }
                else
                {
                    // start index is odd => use HPF
                    for (i = 0; i < h; i++, offset += db.w)
                    {
                        Array.Copy((Array)data!, offset, (Array)buf!, 0, w);
                        sb.hFilter!.synthetize_hpf(buf, 0, wHalfG, 1, buf, wHalfG, wHalfCeilG, 1, data, offset, 1);
                    }
                }

                //Perform the vertical reconstruction 
                offset = (uly - db.uly) * db.w + ulx - db.ulx;
                switch (sb.VerWFilter!.DataType)
                {

                    case DataBlk.TYPE_INT:
                        int[] data_int, buf_int;
                        data_int = (int[])data!;
                        buf_int = (int[])buf!;
                        if (sb.ulcy % 2 == 0)
                        {
                            // start index is even => use LPF
                            for (j = 0; j < w; j++, offset++)
                            {
                                for (i = 0, k = offset; i < h; i++, k += db.w)
                                    buf_int[i] = data_int[k];
                                sb.vFilter!.synthetize_lpf(buf, 0, hHalfCeilG, 1, buf, hHalfCeilG, hHalfG, 1, data, offset, db.w);
                            }
                        }
                        else
                        {
                            // start index is odd => use HPF
                            for (j = 0; j < w; j++, offset++)
                            {
                                for (i = 0, k = offset; i < h; i++, k += db.w)
                                    buf_int[i] = data_int[k];
                                sb.vFilter!.synthetize_hpf(buf, 0, hHalfG, 1, buf, hHalfG, hHalfCeilG, 1, data, offset, db.w);
                            }
                        }
                        break;

                    case DataBlk.TYPE_FLOAT:
                        float[] data_float2, buf_float2;
                        data_float2 = (float[])data!;
                        buf_float2 = (float[])buf!;
                        if (sb.ulcy % 2 == 0)
                        {
                            // start index is even => use LPF
                            for (j = 0; j < w; j++, offset++)
                            {
                                for (i = 0, k = offset; i < h; i++, k += db.w)
                                    buf_float2[i] = data_float2[k];
                                sb.vFilter!.synthetize_lpf(buf, 0, hHalfCeilG, 1, buf, hHalfCeilG, hHalfG, 1, data, offset, db.w);
                            }
                        }
                        else
                        {
                            // start index is odd => use HPF
                            for (j = 0; j < w; j++, offset++)
                            {
                                for (i = 0, k = offset; i < h; i++, k += db.w)
                                    buf_float2[i] = data_float2[k];
                                sb.vFilter!.synthetize_hpf(buf, 0, hHalfG, 1, buf, hHalfG, hHalfCeilG, 1, data, offset, db.w);
                            }
                        }
                        break;
                }
            }
            finally
            {
                // buf points to an instance-level scratch field; nothing to return.
            }
        }

        /// <summary> Performs the inverse wavelet transform on the whole component. It
        /// iteratively reconstructs the subbands from leaves up to the root
        /// node. This method is recursive, the first call to it the 'sb' must be
        /// the root of the subband tree. The method will then process the entire
        /// subband tree by calling itslef recursively.
        /// 
        /// </summary>
        /// <param name="img">The buffer for the image/wavelet data.
        /// 
        /// </param>
        /// <param name="sb">The subband to reconstruct.
        /// 
        /// </param>
        /// <param name="c">The index of the component to reconstruct 
        /// 
        /// </param>
        private void waveletTreeReconstruction(DataBlk img, SubbandSyn sb, int c)
        {

            DataBlk subbData;

            // If the current subband is a leaf then get the data from the source
            if (!sb.isNode)
            {
                int i, m, n;
                Coord ncblks;

                if (sb.w == 0 || sb.h == 0)
                {
                    return; // If empty subband do nothing
                }

                // Get all code-blocks in subband
                if (dtype == DataBlk.TYPE_INT)
                {
                    if (_subbDataInt == null) _subbDataInt = new DataBlkInt();
                    subbData = _subbDataInt;
                }
                else
                {
                    if (_subbDataFloat == null) _subbDataFloat = new DataBlkFloat();
                    subbData = _subbDataFloat;
                }
                ncblks = sb.numCb;
                if (dtype == DataBlk.TYPE_INT)
                {
                    int[] dstArr = (int[])img.Data!;
                    for (m = 0; m < ncblks.y; m++)
                    {
                        for (n = 0; n < ncblks.x; n++)
                        {
                            subbData = src.GetInternCodeBlock(c, m, n, sb, subbData);
                            int[] srcArr = (int[])subbData.Data!;
                            int dstBase = subbData.uly * img.w + subbData.ulx;
                            for (i = subbData.h - 1; i >= 0; i--)
                            {
                                new ReadOnlySpan<int>(srcArr, subbData.offset + i * subbData.scanw, subbData.w)
                                    .CopyTo(dstArr.AsSpan(dstBase + i * img.w, subbData.w));
                            }
                        }
                    }
                }
                else
                {
                    float[] dstArr = (float[])img.Data!;
                    for (m = 0; m < ncblks.y; m++)
                    {
                        for (n = 0; n < ncblks.x; n++)
                        {
                            subbData = src.GetInternCodeBlock(c, m, n, sb, subbData);
                            float[] srcArr = (float[])subbData.Data!;
                            int dstBase = subbData.uly * img.w + subbData.ulx;
                            for (i = subbData.h - 1; i >= 0; i--)
                            {
                                new ReadOnlySpan<float>(srcArr, subbData.offset + i * subbData.scanw, subbData.w)
                                    .CopyTo(dstArr.AsSpan(dstBase + i * img.w, subbData.w));
                            }
                        }
                    }
                }
            }
            else if (sb.isNode)
            {
                // Reconstruct the lower resolution levels if the current subbands
                // is a node

                //Perform the reconstruction of the LL subband
                waveletTreeReconstruction(img, (SubbandSyn)sb.LL!, c);

                if (sb.resLvl <= reslvl - maxImgRes + ndl[c])
                {
                    //Reconstruct the other subbands
                    waveletTreeReconstruction(img, (SubbandSyn)sb.HL, c);
                    waveletTreeReconstruction(img, (SubbandSyn)sb.LH, c);
                    waveletTreeReconstruction(img, (SubbandSyn)sb.HH, c);

                    //Perform the 2D wavelet decomposition of the current subband
                    wavelet2DReconstruction(img, sb, c);
                }
            }
        }

        /// <summary> Returns the implementation type of this wavelet transform, WT_IMPL_FULL
        /// (full-page based transform). All components return the same.
        /// 
        /// </summary>
        /// <param name="c">The index of the component.
        /// 
        /// </param>
        /// <returns> WT_IMPL_FULL
        /// 
        /// </returns>
        /// <seealso cref="WaveletTransform.WT_IMPL_FULL" />
        public override int GetImplementationType(int c)
        {
            return WaveletTransform_Fields.WT_IMPL_FULL;
        }

        /// <summary> Changes the current tile, given the new indexes. An
        /// IllegalArgumentException is thrown if the indexes do not correspond to
        /// a valid tile.
        /// 
        /// </summary>
        /// <param name="x">The horizontal index of the tile.
        /// 
        /// </param>
        /// <param name="y">The vertical index of the new tile.
        /// 
        /// </param>
        public override void SetTile(int x, int y)
        {
            int i;

            // Change tile
            base.SetTile(x, y);

            var nc = src.NumComps;
            var tIdx = src.TileIdx;
            for (var c = 0; c < nc; c++)
            {
                ndl[c] = src.GetSynSubbandTree(tIdx, c).resLvl;
            }

            // Ensure rented buffers are large enough for new tile; do not return them so they are reused
            for (i = 0; i < nc; i++)
            {
                var newWidth = GetTileCompWidth(tIdx, i);
                var newHeight = GetTileCompHeight(tIdx, i);
                
                // Validate dimensions to prevent integer overflow
                long needed = (long)newWidth * newHeight;
                if (needed > int.MaxValue)
                {
                    throw new InvalidOperationException(
                        $"Tile component {i} too large in SetTile: " +
                        $"w={newWidth}, h={newHeight}. " +
                        $"Buffer size {needed} exceeds maximum array size.");
                }
                int neededInt = (int)needed;

                if (rentedFloatBuffers != null && rentedFloatBuffers.Length > i && rentedFloatBuffers[i] != null)
                {
                    if (rentedFloatBuffers[i]!.Length < neededInt)
                    {
                        // Rent a larger buffer and replace the old one
                        var old = rentedFloatBuffers[i];
                        var rent = ArrayPool<float>.Shared.Rent(neededInt);
                        rentedFloatBuffers[i] = rent;
                        try { ArrayPool<float>.Shared.Return(old!, clearArray: false); } catch { }
                    }
                }
                if (rentedIntBuffers != null && rentedIntBuffers.Length > i && rentedIntBuffers[i] != null)
                {
                    if (rentedIntBuffers[i]!.Length < neededInt)
                    {
                        var old = rentedIntBuffers[i];
                        var rent = ArrayPool<int>.Shared.Rent(neededInt);
                        rentedIntBuffers[i] = rent;
                        try { ArrayPool<int>.Shared.Return(old!, clearArray: false); } catch { }
                    }
                }

                // The wavelet data must be reconstructed since we've switched to a different tile.
                // Null only the backing Data array so the wrapper object itself can be reused,
                // avoiding a fresh DataBlkFloat/DataBlkInt allocation per tile per component.
                if (reconstructedComps != null && i < reconstructedComps.Length && reconstructedComps[i] != null)
                {
                    reconstructedComps[i].Data = null;
                }
            }

            cblkToDecode = 0;
            SubbandSyn root, sb;
            for (var c = 0; c < nc; c++)
            {
                root = src.GetSynSubbandTree(tIdx, c);
                for (var r = 0; r <= reslvl - maxImgRes + root.resLvl; r++)
                {
                    if (r == 0)
                    {
                        sb = (SubbandSyn)root.GetSubbandByIdx(0, 0);
                        if (sb != null)
                            cblkToDecode += sb.numCb.x * sb.numCb.y;
                    }
                    else
                    {
                        sb = (SubbandSyn)root.GetSubbandByIdx(r, 1);
                        if (sb != null)
                            cblkToDecode += sb.numCb.x * sb.numCb.y;
                        sb = (SubbandSyn)root.GetSubbandByIdx(r, 2);
                        if (sb != null)
                            cblkToDecode += sb.numCb.x * sb.numCb.y;
                        sb = (SubbandSyn)root.GetSubbandByIdx(r, 3);
                        if (sb != null)
                            cblkToDecode += sb.numCb.x * sb.numCb.y;
                    }
                } // Loop on resolution levels
            } // Loop on components
        }

        public override void NextTile()
        {
            int i;

            // Change tile
            base.NextTile();

            var nc = src.NumComps;
            var tIdx = src.TileIdx;
            for (var c = 0; c < nc; c++)
            {
                ndl[c] = src.GetSynSubbandTree(tIdx, c).resLvl;
            }

            // Ensure rented buffers are large enough for new tile; keep them for reuse
            for (i = 0; i < nc; i++)
            {
                var newWidth = GetTileCompWidth(tIdx, i);
                var newHeight = GetTileCompHeight(tIdx, i);
                
                // Validate dimensions to prevent integer overflow
                long needed = (long)newWidth * newHeight;
                if (needed > int.MaxValue)
                {
                    throw new InvalidOperationException(
                        $"Tile component {i} too large in NextTile: " +
                        $"w={newWidth}, h={newHeight}. " +
                        $"Buffer size {needed} exceeds maximum array size.");
                }
                int neededInt = (int)needed;

                if (rentedFloatBuffers != null && rentedFloatBuffers.Length > i && rentedFloatBuffers[i] != null)
                {
                    if (rentedFloatBuffers[i]!.Length < neededInt)
                    {
                        var old = rentedFloatBuffers[i];
                        var rent = ArrayPool<float>.Shared.Rent(neededInt);
                        rentedFloatBuffers[i] = rent;
                        try { ArrayPool<float>.Shared.Return(old!, clearArray: false); } catch { }
                    }
                }
                if (rentedIntBuffers != null && rentedIntBuffers.Length > i && rentedIntBuffers[i] != null)
                {
                    if (rentedIntBuffers[i]!.Length < neededInt)
                    {
                        var old = rentedIntBuffers[i];
                        var rent = ArrayPool<int>.Shared.Rent(neededInt);
                        rentedIntBuffers[i] = rent;
                        try { ArrayPool<int>.Shared.Return(old!, clearArray: false); } catch { }
                    }
                }

                // The wavelet data must be reconstructed since we've switched to a different tile.
                // Null only the backing Data array so the wrapper object itself can be reused,
                // avoiding a fresh DataBlkFloat/DataBlkInt allocation per tile per component.
                if (reconstructedComps != null && i < reconstructedComps.Length && reconstructedComps[i] != null)
                {
                    reconstructedComps[i].Data = null;
                }
            }
        }

        /// <summary> Closes this object, releasing any system resources it may be using.
        /// This should be the last method called on an object of this class.
        /// 
        /// </summary>
        public new void Close()
        {
            // Return any rented buffers
            // Use configurable clearing for security vs performance trade-off
            if (rentedFloatBuffers != null)
            {
                for (var i = 0; i < rentedFloatBuffers.Length; i++)
                {
                    var buf = rentedFloatBuffers[i];
                    if (buf != null)
                    {
                        try { ArrayPool<float>.Shared.Return(buf!, clearArray: ClearArrayPoolBuffersOnReturn); } catch { }
                        rentedFloatBuffers[i] = null;
                    }
                }
            }
            if (rentedIntBuffers != null)
            {
                for (var i = 0; i < rentedIntBuffers.Length; i++)
                {
                    var ibuf = rentedIntBuffers[i];
                    if (ibuf != null)
                    {
                        try { ArrayPool<int>.Shared.Return(ibuf!, clearArray: ClearArrayPoolBuffersOnReturn); } catch { }
                        rentedIntBuffers[i] = null;
                    }
                }
            }

            // Call base Close (does nothing, but keep behavior consistent)
            base.Close();
        }
    }
}