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
using CoreJ2K.j2k.codestream;
using CoreJ2K.j2k.encoder;
using CoreJ2K.j2k.entropy;
using CoreJ2K.j2k.image;
using CoreJ2K.j2k.util;
using System;
using System.Buffers;

namespace CoreJ2K.j2k.wavelet.analysis
{
    /// <summary> This class implements the ForwardWT abstract class with the full-page
    /// approach to be used either with integer or floating-point filters
    /// 
    /// </summary>
    /// <seealso cref="ForwardWT" />
    public class ForwWTFull : ForwardWT
    {
        /// <summary> Returns the horizontal offset of the code-block partition. Allowable
        /// values are 0 and 1, nothing else.
        /// 
        /// </summary>
        public override int CbULX => cb0x;

        /// <summary> Returns the vertical offset of the code-block partition. Allowable
        /// values are 0 and 1, nothing else.
        /// 
        /// </summary>
        public override int CbULY => cb0y;

        /// <summary>Boolean to know if one are currently dealing with int or float data.</summary>
        private bool intData;

        /// <summary> The subband trees of each tile-component. The array is allocated by the
        /// constructor of this class and updated by the GetAnSubbandTree() method
        /// when needed. The first index is the tile index (in lexicographical
        /// order) and the second index is the component index.
        /// 
        /// The subband tree for a component in the current tile is created on
        /// the first call to GetAnSubbandTree() for that component, in the current
        /// tile. Before that, the element in 'subbTrees' is null.
        /// 
        /// </summary>
        private readonly SubbandAn[][] subbTrees;

        /// <summary>The source of image data </summary>
        private readonly BlkImgDataSrc src;

        /// <summary>The horizontal coordinate of the code-block partition origin on the
        /// reference grid 
        /// </summary>
        private readonly int cb0x;

        /// <summary>The vertical coordinate of the code-block partition on the reference
        /// grid 
        /// </summary>
        private readonly int cb0y;

        /// <summary>The number of decomposition levels specification </summary>
        private readonly IntegerSpec dls;

        /// <summary>Wavelet filters for all components and tiles </summary>
        private readonly AnWTFilterSpec filters;

        /// <summary>The code-block size specifications </summary>
        private readonly CBlkSizeSpec cblks;

        /// <summary>The precinct partition specifications </summary>
        private readonly PrecinctSizeSpec pss;

        /// <summary>Block storing the full band decomposition for each component. </summary>
        private readonly DataBlk?[] decomposedComps;
        // Track rented backing buffers for decomposedComps to return to pool
        private object?[] decomposedBuffers;

        /// <summary>The horizontal index of the last "sent" code-block in the current
        /// subband in each component. It should be -1 if none have been sent yet.
        /// 
        /// </summary>
        private readonly int[] lastn;

        /// <summary>The vertical index of the last "sent" code-block in the current
        /// subband in each component. It should be 0 if none have been sent yet.
        /// 
        /// </summary>
        private readonly int[] lastm;

        /// <summary>The subband being dealt with in each component </summary>
        internal SubbandAn?[] currentSubband;

        /// <summary>Cache  object   to  avoid  excessive  allocation/desallocation.  This
        /// variable makes the class inheritently thread unsafe. 
        /// </summary>
        internal Coord? ncblks;

        /// <summary> Initializes this object with the given source of image data and with
        /// all the decompositon parameters
        /// 
        /// </summary>
        /// <param name="src">From where the image data should be obtained.
        /// 
        /// </param>
        /// <param name="encSpec">The encoder specifications
        /// 
        /// </param>
        /// <param name="cb0x">The horizontal coordinate of the code-block partition
        /// origin on the reference grid.
        /// 
        /// </param>
        /// <param name="cb0y">The vertical coordinate of the code-block partition origin
        /// on the reference grid.
        /// 
        /// </param>
        /// <seealso cref="ForwardWT" />
        public ForwWTFull(BlkImgDataSrc src, EncoderSpecs encSpec, int cb0x, int cb0y) : base(src)
        {
            this.src = src;
            this.cb0x = cb0x;
            this.cb0y = cb0y;
            dls = encSpec.dls;
            filters = encSpec.wfs;
            cblks = encSpec.cblks;
            pss = encSpec.pss;

            var ncomp = src.NumComps;
            var ntiles = src.GetNumTiles();

            currentSubband = new SubbandAn[ncomp];
            decomposedComps = new DataBlk[ncomp];
            decomposedBuffers = new object[ncomp];
            subbTrees = new SubbandAn[ntiles][];
            for (var i = 0; i < ntiles; i++)
            {
                subbTrees[i] = new SubbandAn[ncomp];
            }
            lastn = new int[ncomp];
            lastm = new int[ncomp];
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
        public override int GetImplementationType(int c)
        {
            return WaveletTransform_Fields.WT_IMPL_FULL;
        }

        /// <summary> Returns the number of decomposition levels that are applied to the LL
        /// band, in the specified tile-component. A value of 0 means that no
        /// wavelet transform is applied.
        /// 
        /// </summary>
        /// <param name="t">The tile index
        /// 
        /// </param>
        /// <param name="c">The index of the component.
        /// 
        /// </param>
        /// <returns> The number of decompositions applied to the LL band (0 for no
        /// wavelet transform).
        /// 
        /// </returns>
        public override int GetDecompLevels(int t, int c)
        {
            return dls.GetIntTileCompVal(t, c);
        }

        /// <summary> Returns the wavelet tree decomposition. Actually JPEG 2000 part 1 only
        /// supports WT_DECOMP_DYADIC decomposition.
        /// 
        /// </summary>
        /// <param name="t">The tile-index
        /// 
        /// </param>
        /// <param name="c">The index of the component.
        /// 
        /// </param>
        /// <returns> The wavelet decomposition.
        /// 
        /// </returns>
        public override int GetDecomp(int t, int c)
        {
            return WT_DECOMP_DYADIC;
        }

        /// <summary> Returns the horizontal analysis wavelet filters used in each level, for
        /// the specified component and tile. The first element in the array is the
        /// filter used to obtain the lowest resolution (resolution level 0)
        /// subbands (i.e. lowest frequency LL subband), the second element is the
        /// one used to generate the resolution level 1 subbands, and so on. If
        /// there are less elements in the array than the number of resolution
        /// levels, then the last one is assumed to repeat itself.
        /// 
        /// The returned filters are applicable only to the specified component
        /// and in the current tile.
        /// 
        /// The resolution level of a subband is the resolution level to which a
        /// subband contributes, which is different from its decomposition
        /// level.
        /// 
        /// </summary>
        /// <param name="t">The index of the tile for which to return the filters.
        /// 
        /// </param>
        /// <param name="c">The index of the component for which to return the filters.
        /// 
        /// </param>
        /// <returns> The horizontal analysis wavelet filters used in each level.
        /// 
        /// </returns>
        public override WaveletFilter[] GetHorAnWaveletFilters(int t, int c)
        {
            return filters.GetHFilters(t, c);
        }

        /// <summary> Returns the vertical analysis wavelet filters used in each level, for
        /// the specified component and tile. The first element in the array is the
        /// filter used to obtain the lowest resolution (resolution level 0)
        /// subbands (i.e. lowest frequency LL subband), the second element is the
        /// one used to generate the resolution level 1 subbands, and so on. If
        /// there are less elements in the array than the number of resolution
        /// levels, then the last one is assumed to repeat itself.
        /// 
        /// The returned filters are applicable only to the specified component
        /// and in the current tile.
        /// 
        /// The resolution level of a subband is the resolution level to which a
        /// subband contributes, which is different from its decomposition
        /// level.
        /// 
        /// </summary>
        /// <param name="t">The index of the tile for which to return the filters.
        /// 
        /// </param>
        /// <param name="c">The index of the component for which to return the filters.
        /// 
        /// </param>
        /// <returns> The vertical analysis wavelet filters used in each level.
        /// 
        /// </returns>
        public override WaveletFilter[] GetVertAnWaveletFilters(int t, int c)
        {
            return filters.GetVFilters(t, c);
        }

        /// <summary> Returns the reversibility of the wavelet transform for the specified
        /// component and tile. A wavelet transform is reversible when it is
        /// suitable for lossless and lossy-to-lossless compression.
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
            return filters.IsReversible(t, c);
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
        /// <param name="c">The index of the component.
        /// 
        /// </param>
        /// <returns> The position of the fixed-point, which is the same as the
        /// number of fractional bits. For floating-point data 0 is returned.
        /// 
        /// </returns>
        public override int GetFixedPoint(int c)
        {
            return src.GetFixedPoint(c);
        }

        /// <summary> Returns the next code-block in the current tile for the specified
        /// component. The order in which code-blocks are returned is not
        /// specified. However each code-block is returned only once and all
        /// code-blocks will be returned if the method is called 'N' times, where
        /// 'N' is the number of code-blocks in the tile. After all the code-blocks
        /// have been returned for the current tile calls to this method will
        /// return 'null'.
        /// 
        /// When changing the current tile (through 'SetTile()' or 'NextTile()')
        /// this method will always return the first code-block, as if this method
        /// was never called before for the new current tile.
        /// 
        /// The data returned by this method is the data in the internal buffer
        /// of this object, and thus can not be modified by the caller. The
        /// 'offset' and 'scanw' of the returned data have, in general, some
        /// non-zero value. The 'magbits' of the returned data is not set by this
        /// method and should be ignored. See the 'CBlkWTData' class.
        /// 
        /// The 'ulx' and 'uly' members of the returned 'CBlkWTData' object
        /// contain the coordinates of the top-left corner of the block, with
        /// respect to the tile, not the subband.
        /// 
        /// </summary>
        /// <param name="c">The component for which to return the next code-block.
        /// 
        /// </param>
        /// <param name="cblk">If non-null this object will be used to return the new
        /// code-block. If null a new one will be allocated and returned.
        /// 
        /// </param>
        /// <returns> The next code-block in the current tile for component 'n', or
        /// null if all code-blocks for the current tile have been returned.
        /// 
        /// </returns>
        /// <seealso cref="CBlkWTData" />
        public override CBlkWTData? GetNextInternCodeBlock(int c, CBlkWTData? cblk)
        {
            int cbm, cbn, cn, cm;
            int acb0x, acb0y;
            SubbandAn? sb;
            intData = (filters.GetWTDataType(tIdx, c) == DataBlk.TYPE_INT);

            //If the source image has not been decomposed 
            if (decomposedComps[c] == null)
            {
                int k, w, h;
                DataBlk bufblk;
                object? dst_data;

                w = GetTileCompWidth(tIdx, c);
                h = GetTileCompHeight(tIdx, c);

                //Get the source image data
                if (intData)
                {
                    var dbi = new DataBlkInt();
                    dbi.ulx = 0; dbi.uly = 0; dbi.w = w; dbi.h = h; dbi.offset = 0; dbi.scanw = w;
                    
                    // Validate buffer size to prevent integer overflow
                    long bufferSize = (long)w * h;
                    if (bufferSize > int.MaxValue)
                    {
                        throw new InvalidOperationException(
                            $"Tile component too large: w={w}, h={h}. " +
                            $"Buffer size {bufferSize} exceeds maximum array size.");
                    }
                    
                    // Rent buffer for int data
                    var intBuf = ArrayPool<int>.Shared.Rent((int)bufferSize);
                    dbi.DataInt = intBuf;
                    decomposedComps[c] = dbi;
                    decomposedBuffers[c] = intBuf;
                    bufblk = new DataBlkInt();
                }
                else
                {
                    var dbf = new DataBlkFloat();
                    dbf.ulx = 0; dbf.uly = 0; dbf.w = w; dbf.h = h; dbf.offset = 0; dbf.scanw = w;
                    
                    // Validate buffer size to prevent integer overflow
                    long bufferSize = (long)w * h;
                    if (bufferSize > int.MaxValue)
                    {
                        throw new InvalidOperationException(
                            $"Tile component too large: w={w}, h={h}. " +
                            $"Buffer size {bufferSize} exceeds maximum array size.");
                    }
                    
                    // Rent buffer for float data
                    var floatBuf = ArrayPool<float>.Shared.Rent((int)bufferSize);
                    dbf.DataFloat = floatBuf;
                    decomposedComps[c] = dbf;
                    decomposedBuffers[c] = floatBuf;
                    bufblk = new DataBlkFloat();
                }

                // Get data from source line by line (this diminishes the memory
                // requirements on the data source)
                dst_data = decomposedComps[c]!.Data;
                var lstart = GetCompULX(c);
                bufblk.ulx = lstart;
                bufblk.w = w;
                bufblk.h = 1;
                var kk = GetCompULY(c);
                for (k = 0; k < h; k++, kk++)
                {
                    bufblk.uly = kk;
                    bufblk.ulx = lstart;
                    bufblk = src.GetInternCompData(bufblk, c);
                    Array.Copy((Array)bufblk.Data!, bufblk.offset, (Array)dst_data!, k * w, w);
                }

                //Decompose source image
                waveletTreeDecomposition(decomposedComps[c], GetAnSubbandTree(tIdx, c), c);

                // Make the first subband the current one
                currentSubband[c] = GetNextSubband(c);

                lastn[c] = -1;
                lastm[c] = 0;
            }

            // Get the next code-block to "send"
            do
            {
                // Calculate number of code-blocks in current subband
                ncblks = currentSubband[c]!.numCb;
                // Goto next code-block
                lastn[c]++;
                if (lastn[c] == ncblks.x)
                {
                    // Got to end of this row of
                    // code-blocks
                    lastn[c] = 0;
                    lastm[c]++;
                }
                if (lastm[c] < ncblks.y)
                {
                    // Not past the last code-block in the subband, we can return
                    // this code-block
                    break;
                }
                // If we get here we already sent all code-blocks in this subband,
                // goto next subband
                currentSubband[c] = GetNextSubband(c);
                lastn[c] = -1;
                lastm[c] = 0;
                if (currentSubband[c] == null)
                {
                    // We don't need the transformed data any more (a priori)
                    // Return rented buffer if any and clear
                    if (decomposedBuffers[c] != null)
                    {
                        if (decomposedBuffers[c] is int[] ai) { try { ArrayPool<int>.Shared.Return(ai, clearArray: false); } catch { } }
                        else if (decomposedBuffers[c] is float[] af) { try { ArrayPool<float>.Shared.Return(af, clearArray: false); } catch { } }
                        decomposedBuffers[c] = null;
                    }
                    decomposedComps[c] = null;
                    // All code-blocks from all subbands in the current
                    // tile have been returned so we return a null
                    // reference
                    return null;
                }
                // Loop to find the next code-block
            }
            while (true);


            // Project code-block partition origin to subband. Since the origin is
            // always 0 or 1, it projects to the low-pass side (throught the ceil
            // operator) as itself (i.e. no change) and to the high-pass side
            // (through the floor operator) as 0, always.
            acb0x = cb0x;
            acb0y = cb0y;
            switch (currentSubband[c]!.sbandIdx)
            {

                case Subband.WT_ORIENT_LL:
                    // No need to project since all low-pass => nothing to do
                    break;

                case Subband.WT_ORIENT_HL:
                    acb0x = 0;
                    break;

                case Subband.WT_ORIENT_LH:
                    acb0y = 0;
                    break;

                case Subband.WT_ORIENT_HH:
                    acb0x = 0;
                    acb0y = 0;
                    break;

                default:
                    throw new InvalidOperationException("Internal JJ2000 error");

            }

            // Initialize output code-block
            if (cblk == null)
            {
                if (intData)
                {
                    cblk = new CBlkWTDataInt();
                }
                else
                {
                    cblk = new CBlkWTDataFloat();
                }
            }
            cbn = lastn[c];
            cbm = lastm[c];
            sb = currentSubband[c];
            cblk.n = cbn;
            cblk.m = cbm;
            cblk.sb = sb;
            cn = (sb!.ulcx - acb0x + sb.nomCBlkW) / sb.nomCBlkW - 1;
            cm = (sb.ulcy - acb0y + sb.nomCBlkH) / sb.nomCBlkH - 1;
            if (cbn == 0)
            {
                // Left-most code-block, starts where subband starts
                cblk.ulx = sb.ulx;
            }
            else
            {
                // Calculate starting canvas coordinate and convert to subb. coords
                cblk.ulx = (cn + cbn) * sb.nomCBlkW - (sb.ulcx - acb0x) + sb.ulx;
            }
            if (cbm == 0)
            {
                // Bottom-most code-block, starts where subband starts
                cblk.uly = sb.uly;
            }
            else
            {
                cblk.uly = (cm + cbm) * sb.nomCBlkH - (sb.ulcy - acb0y) + sb.uly;
            }
            if (cbn < ncblks.x - 1)
            {
                // Calculate where next code-block starts => width
                cblk.w = (cn + cbn + 1) * sb.nomCBlkW - (sb.ulcx - acb0x) + sb.ulx - cblk.ulx;
            }
            else
            {
                // Right-most code-block, ends where subband ends
                cblk.w = sb.ulx + sb.w - cblk.ulx;
            }
            if (cbm < ncblks.y - 1)
            {
                // Calculate where next code-block starts => height
                cblk.h = (cm + cbm + 1) * sb.nomCBlkH - (sb.ulcy - acb0y) + sb.uly - cblk.uly;
            }
            else
            {
                // Bottom-most code-block, ends where subband ends
                cblk.h = sb.uly + sb.h - cblk.uly;
            }
            cblk.wmseScaling = 1f;

            // Since we are in GetNextInternCodeBlock() we can return a
            // reference to the internal buffer, no need to copy. Just initialize
            // the 'offset' and 'scanw'
            cblk.offset = cblk.uly * decomposedComps[c]!.w + cblk.ulx;
            cblk.scanw = decomposedComps[c]!.w;

            // For the data just put a reference to our buffer
            cblk.Data = decomposedComps[c]!.Data;
            // Return code-block
            return cblk;
        }

        /// <summary> Returns the next code-block in the current tile for the specified
        /// component, as a copy (see below). The order in which code-blocks are
        /// returned is not specified. However each code-block is returned only
        /// once and all code-blocks will be returned if the method is called 'N'
        /// times, where 'N' is the number of code-blocks in the tile. After all
        /// the code-blocks have been returned for the current tile calls to this
        /// method will return 'null'.
        /// 
        /// When changing the current tile (through 'SetTile()' or 'NextTile()')
        /// this method will always return the first code-block, as if this method
        /// was never called before for the new current tile.
        /// 
        /// The data returned by this method is always a copy of the internal
        /// data of this object, and it can be modified "in place" without
        /// any problems after being returned. The 'offset' of the returned data is
        /// 0, and the 'scanw' is the same as the code-block width.  The 'magbits'
        /// of the returned data is not set by this method and should be
        /// ignored. See the 'CBlkWTData' class.
        /// 
        /// The 'ulx' and 'uly' members of the returned 'CBlkWTData' object
        /// contain the coordinates of the top-left corner of the block, with
        /// respect to the tile, not the subband.
        /// 
        /// </summary>
        /// <param name="c">The component for which to return the next code-block.
        /// 
        /// </param>
        /// <param name="cblk">If non-null this object will be used to return the new
        /// code-block. If null a new one will be allocated and returned. If the
        /// "data" array of the object is non-null it will be reused, if possible,
        /// to return the data.
        /// 
        /// </param>
        /// <returns> The next code-block in the current tile for component 'c', or
        /// null if all code-blocks for the current tile have been returned.
        /// 
        /// </returns>
        /// <seealso cref="CBlkWTData" />
        public override CBlkWTData? GetNextCodeBlock(int c, CBlkWTData? cblk)
        {
            // We can not directly use GetNextInternCodeBlock() since that returns
            // a reference to the internal buffer, we have to copy that data

            int j, k;
            int w;
            object? dst_data; // a int[] or float[] object
            int[]? dst_data_int;
            float[]? dst_data_float;
            object? src_data; // a int[] or float[] object

            intData = (filters.GetWTDataType(tIdx, c) == DataBlk.TYPE_INT);

            dst_data = null;

            // Cache the data array, if any
            if (cblk != null)
            {
                dst_data = cblk.Data;
            }

            // Get the next code-block
            cblk = GetNextInternCodeBlock(c, cblk);

            if (cblk == null)
            {
                return null; // No more code-blocks in current tile for component
                             // c
            }

            // Ensure size of output buffer
            if (intData)
            {
                // int data
                dst_data_int = (int[]?)dst_data;
                if (dst_data_int == null || dst_data_int.Length < cblk.w * cblk.h)
                {
                    dst_data = new int[cblk.w * cblk.h];
                }
            }
            else
            {
                // float data
                dst_data_float = (float[]?)dst_data;
                if (dst_data_float == null || dst_data_float.Length < cblk.w * cblk.h)
                {
                    dst_data = new float[cblk.w * cblk.h];
                }
            }

            // Copy data line by line
            src_data = cblk.Data;
            w = cblk.w;
            for (j = w * (cblk.h - 1), k = cblk.offset + (cblk.h - 1) * cblk.scanw; j >= 0; j -= w, k -= cblk.scanw)
            {
                Array.Copy((Array)src_data!, k, (Array)dst_data!, j, w);
            }
            cblk.Data = dst_data;
            cblk.offset = 0;
            cblk.scanw = w;

            return cblk;
        }

        /// <summary> Return the data type of this CBlkWTDataSrc. Its value should be either
        /// DataBlk.TYPE_INT or DataBlk.TYPE_FLOAT but can change according to the
        /// current tile-component.
        /// 
        /// </summary>
        /// <param name="t">The index of the tile for which to return the data type.
        /// 
        /// </param>
        /// <param name="c">The index of the component for which to return the data type.
        /// 
        /// </param>
        /// <returns> Current data type
        /// 
        /// </returns>
        public override int GetDataType(int t, int c)
        {
            return filters.GetWTDataType(t, c);
        }

        /// <summary> Returns the next subband that will be used to get the next code-block
        /// to return by the getNext[Intern]CodeBlock method.
        /// 
        /// </summary>
        /// <param name="c">The component
        /// 
        /// </param>
        /// <returns> Its returns the next subband that will be used to get the next
        /// code-block to return by the getNext[Intern]CodeBlock method.
        /// 
        /// </returns>
        private SubbandAn? GetNextSubband(int c)
        {
            const int down = 1;
            const int up = 0;
            var direction = down;
            SubbandAn? nextsb;

            nextsb = currentSubband[c];
            //If it is the first call to this method
            if (nextsb == null)
            {
                nextsb = GetAnSubbandTree(tIdx, c);
                //If there is no decomposition level then send the whole image
                if (!nextsb.isNode)
                {
                    return nextsb;
                }
            }

            //Find the next subband to send
            do
            {
                //If the current subband is null then break
                if (!nextsb.isNode)
                {
                    switch (nextsb.orientation)
                    {

                        case Subband.WT_ORIENT_HH:
                            nextsb = (SubbandAn)nextsb.Parent!.LH!;
                            direction = down;
                            break;

                        case Subband.WT_ORIENT_LH:
                            nextsb = (SubbandAn)nextsb.Parent!.HL!;
                            direction = down;
                            break;

                        case Subband.WT_ORIENT_HL:
                            nextsb = (SubbandAn)nextsb.Parent!.LL!;
                            direction = down;
                            break;

                        case Subband.WT_ORIENT_LL:
                            nextsb = (SubbandAn)nextsb.Parent!;
                            direction = up;
                            break;
                    }
                }
                //Else if the current subband is a node 
                else if (nextsb.isNode)
                {
                    //If the direction is down the select the HH subband of the
                    //current node.
                    if (direction == down)
                    {
                        nextsb = (SubbandAn)nextsb.HH!;
                    }
                    //Else the direction is up the select the next node to cover
                    //or still go up in the decomposition tree if the node is a LL
                    //subband
                    else if (direction == up)
                    {
                        switch (nextsb.orientation)
                        {

                            case Subband.WT_ORIENT_HH:
                                nextsb = (SubbandAn)nextsb.Parent!.LH!;
                                direction = down;
                                break;

                            case Subband.WT_ORIENT_LH:
                                nextsb = (SubbandAn)nextsb.Parent!.HL!;
                                direction = down;
                                break;

                            case Subband.WT_ORIENT_HL:
                                nextsb = (SubbandAn)nextsb.Parent!.LL!;
                                direction = down;
                                break;

                            case Subband.WT_ORIENT_LL:
                                nextsb = (SubbandAn)nextsb.Parent!;
                                direction = up;
                                break;
                        }
                    }
                }

                if (nextsb == null)
                {
                    break;
                }
            }
            while (nextsb.isNode);
            return nextsb;
        }

        /// <summary> Performs the forward wavelet transform on the whole band. It
        /// iteratively decomposes the subbands from the top node to the leaves.
        /// 
        /// </summary>
        /// <param name="band">The band containing the float data to decompose
        /// 
        /// </param>
        /// <param name="subband">The structure containing the coordinates of the current
        /// subband in the whole band to decompose.
        /// 
        /// </param>
        /// <param name="c">The index of the current component to decompose
        /// 
        /// </param>
        private void waveletTreeDecomposition(DataBlk? band, SubbandAn subband, int c)
        {

            //If the current subband is a leaf then nothing to be done (a leaf is
            //not decomposed).
            if (!subband.isNode)
            {
                return;
            }
            else
            {
                //Perform the 2D wavelet decomposition of the current subband
                wavelet2DDecomposition(band, subband, c);

                //Perform the decomposition of the four resulting subbands
                waveletTreeDecomposition(band, (SubbandAn)subband.HH!, c);
                waveletTreeDecomposition(band, (SubbandAn)subband.LH!, c);
                waveletTreeDecomposition(band, (SubbandAn)subband.HL!, c);
                waveletTreeDecomposition(band, (SubbandAn)subband.LL!, c);
            }
        }

        /// <summary> Performs the 2D forward wavelet transform on a subband of the initial
        /// band. This method will successively perform 1D filtering steps on all
        /// lines and then all columns of the subband. In this class only filters
        /// with floating point implementations can be used.
        /// 
        /// </summary>
        /// <param name="band">The band containing the float data to decompose
        /// 
        /// </param>
        /// <param name="subband">The structure containing the coordinates of the subband
        /// in the whole band to decompose.
        /// 
        /// </param>
        /// <param name="c">The index of the current component to decompose
        /// 
        /// </param>
        private void wavelet2DDecomposition(DataBlk? band, SubbandAn subband, int c)
        {

            int ulx, uly, w, h;
            int band_w, band_h;

            // If subband is empty (i.e. zero size) nothing to do
            if (subband.w == 0 || subband.h == 0)
            {
                return;
            }

            ulx = subband.ulx;
            uly = subband.uly;
            w = subband.w;
            h = subband.h;
            band_w = GetTileCompWidth(tIdx, c);
            band_h = GetTileCompHeight(tIdx, c);

            if (intData)
            {
                //Perform the decompositions if the filter is implemented with an
                //integer arithmetic.
                int i, j;
                int offset;
                var tmpVector = ArrayPool<int>.Shared.Rent(Math.Max(w, h));
                try
                {
                    var data = ((DataBlkInt)band!).DataInt!;

                    //Perform the vertical decomposition
                    if (subband.ulcy % 2 == 0)
                    {
                        // Even start index => use LPF
                        for (j = 0; j < w; j++)
                        {
                            offset = uly * band_w + ulx + j;
                            for (i = 0; i < h; i++)
                                tmpVector[i] = data[offset + (i * band_w)];
                            subband.vFilter!.analyze_lpf(tmpVector, 0, h, 1, data, offset, band_w, data, offset + ((h + 1) / 2) * band_w, band_w);
                        }
                    }
                    else
                    {
                        // Odd start index => use HPF
                        for (j = 0; j < w; j++)
                        {
                            offset = uly * band_w + ulx + j;
                            for (i = 0; i < h; i++)
                                tmpVector[i] = data[offset + (i * band_w)];
                            subband.vFilter!.analyze_hpf(tmpVector, 0, h, 1, data, offset, band_w, data, offset + (h / 2) * band_w, band_w);
                        }
                    }

                    //Perform the horizontal decomposition.
                    if (subband.ulcx % 2 == 0)
                    {
                        // Even start index => use LPF
                        for (i = 0; i < h; i++)
                        {
                            offset = (uly + i) * band_w + ulx;
                            for (j = 0; j < w; j++)
                                tmpVector[j] = data[offset + j];
                            subband.hFilter!.analyze_lpf(tmpVector, 0, w, 1, data, offset, 1, data, offset + (w + 1) / 2, 1);
                        }
                    }
                    else
                    {
                        // Odd start index => use HPF
                        for (i = 0; i < h; i++)
                        {
                            offset = (uly + i) * band_w + ulx;
                            for (j = 0; j < w; j++)
                                tmpVector[j] = data[offset + j];
                            subband.hFilter!.analyze_hpf(tmpVector, 0, w, 1, data, offset, 1, data, offset + w / 2, 1);
                        }
                    }
                }
                finally
                {
                    try { ArrayPool<int>.Shared.Return(tmpVector, clearArray: false); } catch { }
                }
            }
            else
            {
                //Perform the decompositions if the filter is implemented with a
                //float arithmetic.
                int i, j;
                int offset;
                var tmpVector = ArrayPool<float>.Shared.Rent(Math.Max(w, h));
                try
                {
                    var data = ((DataBlkFloat)band!).DataFloat!;

                    //Perform the vertical decomposition.
                    if (subband.ulcy % 2 == 0)
                    {
                        // Even start index => use LPF
                        for (j = 0; j < w; j++)
                        {
                            offset = uly * band_w + ulx + j;
                            for (i = 0; i < h; i++)
                                tmpVector[i] = data[offset + (i * band_w)];
                            subband.vFilter!.analyze_lpf(tmpVector, 0, h, 1, data, offset, band_w, data, offset + ((h + 1) / 2) * band_w, band_w);
                        }
                    }
                    else
                    {
                        // Odd start index => use HPF
                        for (j = 0; j < w; j++)
                        {
                            offset = uly * band_w + ulx + j;
                            for (i = 0; i < h; i++)
                                tmpVector[i] = data[offset + (i * band_w)];
                            subband.vFilter!.analyze_hpf(tmpVector, 0, h, 1, data, offset, band_w, data, offset + (h / 2) * band_w, band_w);
                        }
                    }
                    //Perform the horizontal decomposition.
                    if (subband.ulcx % 2 == 0)
                    {
                        // Even start index => use LPF
                        for (i = 0; i < h; i++)
                        {
                            offset = (uly + i) * band_w + ulx;
                            for (j = 0; j < w; j++)
                                tmpVector[j] = data[offset + j];
                            subband.hFilter!.analyze_lpf(tmpVector, 0, w, 1, data, offset, 1, data, offset + (w + 1) / 2, 1);
                        }
                    }
                    else
                    {
                        // Odd start index => use HPF
                        for (i = 0; i < h; i++)
                        {
                            offset = (uly + i) * band_w + ulx;
                            for (j = 0; j < w; j++)
                                tmpVector[j] = data[offset + j];
                            subband.hFilter!.analyze_hpf(tmpVector, 0, w, 1, data, offset, 1, data, offset + w / 2, 1);
                        }
                    }
                }
                finally
                {
                    try { ArrayPool<float>.Shared.Return(tmpVector, clearArray: false); } catch { }
                }
            }
        }

        /// <summary> Changes the current tile, given the new coordinates. 
        /// 
        /// This method resets the 'subbTrees' array, and recalculates the
        /// values of the 'reversible' array. It also resets the decomposed
        /// component buffers.
        /// 
        /// </summary>
        /// <param name="x">The horizontal coordinate of the tile.
        /// 
        /// </param>
        /// <param name="y">The vertical coordinate of the new tile.
        /// 
        /// </param>
        public override void SetTile(int x, int y)
        {
            int i;

            // Change tile
            base.SetTile(x, y);

            // Reset the decomposed component buffers.
            if (decomposedComps != null)
            {
                for (i = decomposedComps.Length - 1; i >= 0; i--)
                {
                    // Return any rented buffer
                    if (decomposedBuffers != null && decomposedBuffers[i] != null)
                    {
                        if (decomposedBuffers[i] is int[] ai) { try { ArrayPool<int>.Shared.Return(ai, clearArray: false); } catch { } }
                        else if (decomposedBuffers[i] is float[] af) { try { ArrayPool<float>.Shared.Return(af, clearArray: false); } catch { } }
                        decomposedBuffers[i] = null;
                    }
                    decomposedComps[i] = null;
                    currentSubband[i] = null;
                }
            }
        }

        /// <summary> Advances to the next tile, in standard scan-line order (by rows then
        /// columns). An NoNextElementException is thrown if the current tile is
        /// the last one (i.e. there is no next tile).
        /// 
        /// This method resets the 'subbTrees' array, and recalculates the
        /// values of the 'reversible' array. It also resets the decomposed
        /// component buffers.
        /// 
        /// </summary>
        public override void NextTile()
        {
            int i;

            // Change tile
            base.NextTile();
            // Reset the decomposed component buffers
            if (decomposedComps != null)
            {
                for (i = decomposedComps.Length - 1; i >= 0; i--)
                {
                    // Return any rented buffer
                    if (decomposedBuffers != null && decomposedBuffers[i] != null)
                    {
                        if (decomposedBuffers[i] is int[] ai) { try { ArrayPool<int>.Shared.Return(ai, clearArray: false); } catch { } }
                        else if (decomposedBuffers[i] is float[] af) { try { ArrayPool<float>.Shared.Return(af, clearArray: false); } catch { } }
                        decomposedBuffers[i] = null;
                    }
                    decomposedComps[i] = null;
                    currentSubband[i] = null;
                }
            }
        }

        /// <summary> Returns a reference to the subband tree structure representing the
        /// subband decomposition for the specified tile-component of the source.
        /// 
        /// </summary>
        /// <param name="t">The index of the tile. 
        /// 
        /// </param>
        /// <param name="c">The index of the component. 
        /// 
        /// </param>
        /// <returns> The subband tree structure, see Subband. 
        /// 
        /// </returns>
        /// <seealso cref="SubbandAn">
        /// </seealso>
        /// <seealso cref="Subband" />
        public override SubbandAn GetAnSubbandTree(int t, int c)
        {
            if (subbTrees[t][c] == null)
            {
                subbTrees[t][c] = new SubbandAn(GetTileCompWidth(t, c), GetTileCompHeight(t, c), GetCompULX(c), GetCompULY(c), GetDecompLevels(t, c), GetHorAnWaveletFilters(t, c), GetVertAnWaveletFilters(t, c));
                initSubbandsFields(t, c, subbTrees[t][c]);
            }
            return subbTrees[t][c];
        }

        /// <summary> Initialises subbands fields, such as number of code-blocks and
        /// code-blocks dimension, in the subband tree. The nominal code-block
        /// width/height depends on the precincts dimensions if used.
        /// 
        /// </summary>
        /// <param name="t">The tile index of the subband
        /// 
        /// </param>
        /// <param name="c">The component index
        /// 
        /// </param>
        /// <param name="sb">The subband tree to be initialised.
        /// 
        /// </param>
        private void initSubbandsFields(int t, int c, Subband sb)
        {
            var cbw = cblks.GetCBlkWidth(ModuleSpec.SPEC_TILE_COMP, t, c);
            var cbh = cblks.GetCBlkHeight(ModuleSpec.SPEC_TILE_COMP, t, c);

            if (!sb.isNode)
            {
                // Code-blocks dimension
                int ppx, ppy;
                int ppxExp, ppyExp, cbwExp, cbhExp;
                ppx = pss.GetPPX(t, c, sb.resLvl);
                ppy = pss.GetPPY(t, c, sb.resLvl);

                if (ppx != Markers.PRECINCT_PARTITION_DEF_SIZE || ppy != Markers.PRECINCT_PARTITION_DEF_SIZE)
                {

                    ppxExp = MathUtil.log2(ppx);
                    ppyExp = MathUtil.log2(ppy);
                    cbwExp = MathUtil.log2(cbw);
                    cbhExp = MathUtil.log2(cbh);

                    // Precinct partition is used
                    switch (sb.resLvl)
                    {

                        case 0:
                            sb.nomCBlkW = (cbwExp < ppxExp ? (1 << cbwExp) : (1 << ppxExp));
                            sb.nomCBlkH = (cbhExp < ppyExp ? (1 << cbhExp) : (1 << ppyExp));
                            break;


                        default:
                            sb.nomCBlkW = (cbwExp < ppxExp - 1 ? (1 << cbwExp) : (1 << (ppxExp - 1)));
                            sb.nomCBlkH = (cbhExp < ppyExp - 1 ? (1 << cbhExp) : (1 << (ppyExp - 1)));
                            break;

                    }
                }
                else
                {
                    sb.nomCBlkW = cbw;
                    sb.nomCBlkH = cbh;
                }

                // Number of code-blocks
                if (sb.numCb == null)
                    sb.numCb = new Coord();
                if (sb.w != 0 && sb.h != 0)
                {
                    var acb0x = cb0x;
                    var acb0y = cb0y;
                    int tmp;

                    // Project code-block partition origin to subband. Since the
                    // origin is always 0 or 1, it projects to the low-pass side
                    // (throught the ceil operator) as itself (i.e. no change) and
                    // to the high-pass side (through the floor operator) as 0,
                    // always.
                    switch (sb.sbandIdx)
                    {

                        case Subband.WT_ORIENT_LL:
                            // No need to project since all low-pass => nothing to do
                            break;

                        case Subband.WT_ORIENT_HL:
                            acb0x = 0;
                            break;

                        case Subband.WT_ORIENT_LH:
                            acb0y = 0;
                            break;

                        case Subband.WT_ORIENT_HH:
                            acb0x = 0;
                            acb0y = 0;
                            break;

                        default:
                            throw new InvalidOperationException("Internal JJ2000 error");

                    }
                    if (sb.ulcx - acb0x < 0 || sb.ulcy - acb0y < 0)
                    {
                        throw new ArgumentException("Invalid code-blocks " + "partition origin or " + "image offset in the " + "reference grid.");
                    }
                    // NOTE: when calculating "floor()" by integer division the
                    // dividend and divisor must be positive, we ensure that by
                    // adding the divisor to the dividend and then substracting 1
                    // to the result of the division
                    tmp = sb.ulcx - acb0x + sb.nomCBlkW;
                    sb.numCb.x = (tmp + sb.w - 1) / sb.nomCBlkW - (tmp / sb.nomCBlkW - 1);
                    tmp = sb.ulcy - acb0y + sb.nomCBlkH;
                    sb.numCb.y = (tmp + sb.h - 1) / sb.nomCBlkH - (tmp / sb.nomCBlkH - 1);
                }
                else
                {
                    sb.numCb.x = sb.numCb.y = 0;
                }
            }
            else
            {
                initSubbandsFields(t, c, sb.LL!);
                initSubbandsFields(t, c, sb.HL!);
                initSubbandsFields(t, c, sb.LH!);
                initSubbandsFields(t, c, sb.HH!);
            }
        }
    }
}