/*
*
*                          Mergerd from StdQuantizerInt and
*                          StdQuantizerFloat from Joel Askelof.
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
using CoreJ2K.j2k.encoder;
using CoreJ2K.j2k.image;
using CoreJ2K.j2k.wavelet;
using CoreJ2K.j2k.wavelet.analysis;
using System;

namespace CoreJ2K.j2k.quantization.quantizer
{

    /// <summary> This class implements scalar quantization of integer or floating-point
    /// valued source data. The source data is the wavelet transformed image data
    /// and the output is the quantized wavelet coefficients represented in
    /// sign-magnitude (see below).
    /// 
    /// Sign magnitude representation is used (instead of two's complement) for
    /// the output data. The most significant bit is used for the sign (0 if
    /// positive, 1 if negative). Then the magnitude of the quantized coefficient
    /// is stored in the next M most significat bits. The rest of the bits (least
    /// significant bits) can contain a fractional value of the quantized
    /// coefficient. This fractional value is not to be coded by the entropy
    /// coder. However, it can be used to compute rate-distortion measures with
    /// greater precision.
    /// 
    /// The value of M is determined for each subband as the sum of the number
    /// of guard bits G and the nominal range of quantized wavelet coefficients in
    /// the corresponding subband (Rq), minus 1:
    /// 
    /// M = G + Rq -1
    /// 
    /// The value of G should be the same for all subbands. The value of Rq
    /// depends on the quantization step size, the nominal range of the component
    /// before the wavelet transform and the analysis gain of the subband (see
    /// Subband).
    /// 
    /// The blocks of data that are requested should not cross subband
    /// boundaries.
    /// 
    /// </summary>
    /// <seealso cref="Subband" />
    /// <seealso cref="Quantizer" />
    public class StdQuantizer : Quantizer
    {
        /// <summary> Returns the quantization type spec object associated to the quantizer.
        /// 
        /// </summary>
        /// <returns> The quantization type spec
        /// 
        /// </returns>
        public virtual QuantTypeSpec QuantTypeSpec => qts;

        /// <summary>The number of mantissa bits for the quantization steps </summary>
        public const int QSTEP_MANTISSA_BITS = 11;

        /// <summary>The number of exponent bits for the quantization steps </summary>
        // NOTE: formulas in 'convertFromExpMantissa()' and
        // 'convertToExpMantissa()' methods do not support more than 5 bits.
        public const int QSTEP_EXPONENT_BITS = 5;

        /// <summary>The maximum value of the mantissa for the quantization steps </summary>
        public const int QSTEP_MAX_MANTISSA = (1 << QSTEP_MANTISSA_BITS) - 1;

        /// <summary>The maximum value of the exponent for the quantization steps </summary>
        public const int QSTEP_MAX_EXPONENT = (1 << QSTEP_EXPONENT_BITS) - 1;

        /// <summary>Natural log of 2, used as a convenience variable </summary>
        private static readonly double log2 = Math.Log(2);

        /// <summary>The quantization type specifications </summary>
        private readonly QuantTypeSpec qts;

        /// <summary>The quantization step size specifications </summary>
        private readonly QuantStepSizeSpec qsss;

        /// <summary>The guard bits specifications</summary>
        private readonly GuardBitsSpec gbs;

        /// <summary>The 'CBlkWTDataFloat' object used to request data, used when
        /// quantizing floating-point data. 
        /// </summary>
        // This variable makes the class thread unsafe, but it avoids allocating
        // new objects for code-block that is quantized.
        private CBlkWTDataFloat infblk;

        /// <summary> Initializes the source of wavelet transform coefficients. The
        /// constructor takes information on whether the quantizer is in
        /// reversible, derived or expounded mode. If the quantizer is reversible
        /// the value of 'derived' is ignored. If the source data is not integer
        /// (int) then the quantizer can not be reversible.
        /// 
        /// After initializing member attributes, GetAnSubbandTree is called for
        /// all components setting the 'stepWMSE' for all subbands in the current
        /// tile.
        /// 
        /// </summary>
        /// <param name="src">The source of wavelet transform coefficients.
        /// 
        /// </param>
        /// <param name="encSpec">The encoder specifications
        /// 
        /// </param>
        public StdQuantizer(CBlkWTDataSrc src, EncoderSpecs encSpec) : base(src)
        {
            qts = encSpec.qts;
            qsss = encSpec.qsss;
            gbs = encSpec.gbs;
        }

        /// <summary> Returns the number of guard bits used by this quantizer in the given
        /// tile-component.
        /// 
        /// </summary>
        /// <param name="t">Tile index
        /// 
        /// </param>
        /// <param name="c">Component index
        /// 
        /// </param>
        /// <returns> The number of guard bits
        /// 
        /// </returns>
        public override int GetNumGuardBits(int t, int c)
        {
            return gbs.GetIntTileCompVal(t, c);
        }

        /// <summary> Returns true if the quantized data is reversible, for the specified
        /// tile-component. For the quantized data to be reversible it is necessary
        /// and sufficient that the quantization is reversible.
        /// 
        /// </summary>
        /// <param name="t">The tile to test for reversibility
        /// 
        /// </param>
        /// <param name="c">The component to test for reversibility
        /// 
        /// </param>
        /// <returns> True if the quantized data is reversible, false if not.
        /// 
        /// </returns>
        public override bool IsReversible(int t, int c)
        {
            return qts.IsReversible(t, c);
        }

        /// <summary> Returns true if given tile-component uses derived quantization step
        /// sizes.
        /// 
        /// </summary>
        /// <param name="t">Tile index
        /// 
        /// </param>
        /// <param name="c">Component index
        /// 
        /// </param>
        /// <returns> True if derived
        /// 
        /// </returns>
        public override bool IsDerived(int t, int c)
        {
            return qts.IsDerived(t, c);
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
        /// The data returned by this method is always a copy of the
        /// data. Therfore it can be modified "in place" without any problems after
        /// being returned. The 'offset' of the returned data is 0, and the 'scanw'
        /// is the same as the code-block width. See the 'CBlkWTData' class.
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
        /// <returns> The next code-block in the current tile for component 'n', or
        /// null if all code-blocks for the current tile have been returned.
        /// 
        /// </returns>
        /// <seealso cref="CBlkWTData" />
        public override CBlkWTData? GetNextCodeBlock(int c, CBlkWTData? cblk)
        {
            return GetNextInternCodeBlock(c, cblk);
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
        /// The data returned by this method can be the data in the internal
        /// buffer of this object, if any, and thus can not be modified by the
        /// caller. The 'offset' and 'scanw' of the returned data can be
        /// arbitrary. See the 'CBlkWTData' class.
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
        /// <returns> The next code-block in the current tile for component 'n', or
        /// null if all code-blocks for the current tile have been returned.
        /// 
        /// </returns>
        /// <seealso cref="CBlkWTData" />
        public override CBlkWTData? GetNextInternCodeBlock(int c, CBlkWTData? cblk)
        {
            // NOTE: this method is declared final since GetNextCodeBlock() relies
            // on this particular implementation
            int k, j;
            int tmp, shiftBits, jmin;
            int w, h;
            int[] outarr;
            float[] infarr = null;
            CBlkWTDataFloat infblk;
            float invstep; // The inverse of the quantization step size
            bool intq; // flag for quantizig ints
            SubbandAn sb;
            float stepUDR; // The quantization step size (for a dynamic
                           // range of 1, or unit)
            var g = gbs.GetIntTileCompVal(tIdx, c);

            // Are we quantizing ints or floats?
            intq = (src.GetDataType(tIdx, c) == DataBlk.TYPE_INT);

            // Check that we have an output object
            if (cblk == null)
            {
                cblk = new CBlkWTDataInt();
            }

            // Cache input float code-block
            infblk = this.infblk;

            // Get data to quantize. When quantizing int data 'cblk' is used to
            // get the data to quantize and to return the quantized data as well,
            // that's why 'GetNextCodeBlock()' is used. This can not be done when
            // quantizing float data because of the different data types, that's
            // why 'GetNextInternCodeBlock()' is used in that case.
            if (intq)
            {
                // Source data is int
                cblk = src.GetNextCodeBlock(c, cblk);
                if (cblk == null)
                {
                    return null; // No more code-blocks in current tile for comp.
                }
                // Input and output arrays are the same (for "in place" quant.)
                outarr = (int[])cblk.Data;
            }
            else
            {
                // Source data is float
                // Can not use 'cblk' to get float data, use 'infblk'
                infblk = (CBlkWTDataFloat)src.GetNextInternCodeBlock(c, infblk);
                if (infblk == null)
                {
                    // Release buffer from infblk: this enables to garbage collect
                    // the big buffer when we are done with last code-block of
                    // component.
                    this.infblk.Data = null;
                    return null; // No more code-blocks in current tile for comp.
                }
                this.infblk = infblk; // Save local cache
                infarr = (float[])infblk.Data;
                // Get output data array and check that there is memory to put the
                // quantized coeffs in
                outarr = (int[])cblk.Data;
                if (outarr == null || outarr.Length < infblk.w * infblk.h)
                {
                    outarr = new int[infblk.w * infblk.h];
                    cblk.Data = outarr;
                }
                cblk.m = infblk.m;
                cblk.n = infblk.n;
                cblk.sb = infblk.sb;
                cblk.ulx = infblk.ulx;
                cblk.uly = infblk.uly;
                cblk.w = infblk.w;
                cblk.h = infblk.h;
                cblk.wmseScaling = infblk.wmseScaling;
                cblk.offset = 0;
                cblk.scanw = cblk.w;
            }

            // Cache width, height and subband of code-block
            w = cblk.w;
            h = cblk.h;
            sb = cblk.sb;

            if (IsReversible(tIdx, c))
            {
                // Reversible only for int data
                cblk.magbits = g - 1 + src.GetNomRangeBits(c) + sb.anGainExp;
                shiftBits = 31 - cblk.magbits;

                // Update the convertFactor field
                cblk.convertFactor = (1 << shiftBits);

                // Since we used GetNextCodeBlock() to get the int data then
                // 'offset' is 0 and 'scanw' is the width of the code-block The
                // input and output arrays are the same (i.e. "in place")
                for (j = w * h - 1; j >= 0; j--)
                {
                    tmp = (outarr[j] << shiftBits);
                    outarr[j] = ((tmp < 0) ? (1 << 31) | (-tmp) : tmp);
                }
            }
            else
            {
                // Non-reversible, use step size
                var baseStep = qsss.GetFloatTileCompVal(tIdx, c);

                // Calculate magnitude bits and quantization step size 
                if (IsDerived(tIdx, c))
                {
                    cblk.magbits = g - 1 + sb.level - (int)Math.Floor(Math.Log(baseStep) / log2);
                    stepUDR = baseStep / (1 << sb.level);
                }
                else
                {
                    cblk.magbits = g - 1 - (int)Math.Floor(Math.Log(baseStep / (sb.l2Norm * (1 << sb.anGainExp))) / log2);
                    stepUDR = baseStep / (sb.l2Norm * (1 << sb.anGainExp));
                }
                shiftBits = 31 - cblk.magbits;
                // Calculate step that decoder will get and use that one.
                stepUDR = convertFromExpMantissa(convertToExpMantissa(stepUDR));
                invstep = 1.0f / ((1L << (src.GetNomRangeBits(c) + sb.anGainExp)) * stepUDR);
                // Normalize to magnitude bits (output fractional point)
                invstep *= (1 << (shiftBits - src.GetFixedPoint(c)));

                // Update convertFactor and stepSize fields
                cblk.convertFactor = invstep;
                cblk.stepSize = ((1L << (src.GetNomRangeBits(c) + sb.anGainExp)) * stepUDR);

                if (intq)
                {
                    // Quantizing int data
                    // Since we used GetNextCodeBlock() to get the int data then
                    // 'offset' is 0 and 'scanw' is the width of the code-block
                    // The input and output arrays are the same (i.e. "in place")
                    for (j = w * h - 1; j >= 0; j--)
                    {
                        tmp = (int)(outarr[j] * invstep);
                        outarr[j] = ((tmp < 0) ? (1 << 31) | (-tmp) : tmp);
                    }
                }
                else
                {
                    // Quantizing float data
                    for (j = w * h - 1, k = infblk.offset + (h - 1) * infblk.scanw + w - 1, jmin = w * (h - 1); j >= 0; jmin -= w)
                    {
                        for (; j >= jmin; k--, j--)
                        {
                            tmp = (int)(infarr[k] * invstep);
                            outarr[j] = ((tmp < 0) ? (1 << 31) | (-tmp) : tmp);
                        }
                        // Jump to beggining of previous line in input
                        k -= (infblk.scanw - w);
                    }
                }
            }
            // Return the quantized code-block
            return cblk;
        }

        /// <summary> Calculates the parameters of the SubbandAn objects that depend on the
        /// Quantizer. The 'stepWMSE' field is calculated for each subband which is
        /// a leaf in the tree rooted at 'sb', for the specified component. The
        /// subband tree 'sb' must be the one for the component 'n'.
        /// 
        /// </summary>
        /// <param name="sb">The root of the subband tree.
        /// 
        /// </param>
        /// <param name="c">The component index
        /// 
        /// </param>
        /// <seealso cref="SubbandAn.stepWMSE" />
        protected internal override void calcSbParams(SubbandAn sb, int c)
        {
            float baseStep;

            if (sb.stepWMSE > 0f)
                // parameters already calculated
                return;
            if (!sb.isNode)
            {
                if (IsReversible(tIdx, c))
                {
                    sb.stepWMSE = (float)Math.Pow(2, -(src.GetNomRangeBits(c) << 1)) * sb.l2Norm * sb.l2Norm;
                }
                else
                {
                    baseStep = qsss.GetFloatTileCompVal(tIdx, c);
                    if (IsDerived(tIdx, c))
                    {
                        sb.stepWMSE = baseStep * baseStep * (float)Math.Pow(2, (sb.anGainExp - sb.level) << 1) * sb.l2Norm * sb.l2Norm;
                    }
                    else
                    {
                        sb.stepWMSE = baseStep * baseStep;
                    }
                }
            }
            else
            {
                calcSbParams((SubbandAn)sb.LL, c);
                calcSbParams((SubbandAn)sb.HL, c);
                calcSbParams((SubbandAn)sb.LH, c);
                calcSbParams((SubbandAn)sb.HH, c);
                sb.stepWMSE = 1f; // Signal that we already calculated this branch
            }
        }

        /// <summary> Converts the floating point value to its exponent-mantissa
        /// representation. The mantissa occupies the 11 least significant bits
        /// (bits 10-0), and the exponent the previous 5 bits (bits 15-11).
        /// 
        /// </summary>
        /// <param name="step">The quantization step, normalized to a dynamic range of 1.
        /// 
        /// </param>
        /// <returns> The exponent mantissa representation of the step.
        /// 
        /// </returns>
        public static int convertToExpMantissa(float step)
        {
            // Validate step to prevent Math.Log and division errors
            if (step <= 0f)
            {
                throw new ArgumentException(
                    $"Quantization step must be positive, got: {step}. " +
                    "Invalid quantization parameters.", nameof(step));
            }
            
            if (float.IsNaN(step) || float.IsInfinity(step))
            {
                throw new ArgumentException(
                    $"Quantization step must be a finite number, got: {step}", nameof(step));
            }

            int exp;

            exp = (int)Math.Ceiling((-Math.Log(step)) / log2);
            if (exp > QSTEP_MAX_EXPONENT)
            {
                // If step size is too small for exponent representation, use the
                // minimum, which is exponent QSTEP_MAX_EXPONENT and mantissa 0.
                return (QSTEP_MAX_EXPONENT << QSTEP_MANTISSA_BITS);
            }
            // NOTE: this formula does not support more than 5 bits for the
            // exponent, otherwise (-1<<exp) might overflow (the - is used to be
            // able to represent 2**31)
            return (exp << QSTEP_MANTISSA_BITS) | ((int)(((-step) * (-1 << exp) - 1f) * (1 << QSTEP_MANTISSA_BITS) + 0.5f));
        }

        /// <summary> Converts the exponent-mantissa representation to its floating-point
        /// value. The mantissa occupies the 11 least significant bits (bits 10-0),
        /// and the exponent the previous 5 bits (bits 15-11).
        /// 
        /// </summary>
        /// <param name="ems">The exponent-mantissa representation of the step.
        /// 
        /// </param>
        /// <returns> The floating point representation of the step, normalized to a
        /// dynamic range of 1.
        /// 
        /// </returns>
        private static float convertFromExpMantissa(int ems)
        {
            // Extract exponent and validate
            int exponent = (ems >> QSTEP_MANTISSA_BITS) & QSTEP_MAX_EXPONENT;
            
            // NOTE: this formula does not support more than 5 bits for the
            // exponent, otherwise (-1<<exp) might overflow (the - is used to be
            // able to represent 2**31)
            int denominator = -1 << exponent;
            
            // Validate denominator (should never be zero given the bit manipulation,
            // but check for safety in case of corrupted data)
            if (denominator == 0)
            {
                throw new InvalidOperationException(
                    $"Invalid exponent-mantissa representation: exponent={exponent}, " +
                    "would result in division by zero.");
            }
            
            return (-1f - (ems & QSTEP_MAX_MANTISSA) / ((float)(1 << QSTEP_MANTISSA_BITS))) / denominator;
        }

        /// <summary> Returns the maximum number of magnitude bits in any subband of the
        /// current tile.
        /// 
        /// </summary>
        /// <param name="c">the component number
        /// 
        /// </param>
        /// <returns> The maximum number of magnitude bits in all subbands of the
        /// current tile.
        /// 
        /// </returns>
        public override int GetMaxMagBits(int c)
        {
            Subband sb = GetAnSubbandTree(tIdx, c);
            if (IsReversible(tIdx, c))
            {
                return GetMaxMagBitsRev(sb, c);
            }
            else
            {
                return IsDerived(tIdx, c)
                    ? GetMaxMagBitsDerived(sb, tIdx, c)
                    : GetMaxMagBitsExpounded(sb, tIdx, c);
            }
        }


        /// <summary> Returns the maximum number of magnitude bits in any subband of the
        /// current tile if reversible quantization is used
        /// 
        /// </summary>
        /// <param name="sb">The root of the subband tree of the current tile
        /// 
        /// </param>
        /// <param name="c">the component number
        /// 
        /// </param>
        /// <returns> The highest number of magnitude bit-planes
        /// 
        /// </returns>
        private int GetMaxMagBitsRev(Subband sb, int c)
        {
            int tmp, max = 0;
            var g = gbs.GetIntTileCompVal(tIdx, c);

            if (!sb.isNode)
                return g - 1 + src.GetNomRangeBits(c) + sb.anGainExp;

            max = GetMaxMagBitsRev(sb.LL, c);
            tmp = GetMaxMagBitsRev(sb.LH, c);
            if (tmp > max)
                max = tmp;
            tmp = GetMaxMagBitsRev(sb.HL, c);
            if (tmp > max)
                max = tmp;
            tmp = GetMaxMagBitsRev(sb.HH, c);
            if (tmp > max)
                max = tmp;

            return max;
        }

        /// <summary> Returns the maximum number of magnitude bits in any subband in the
        /// given tile-component if derived quantization is used
        /// 
        /// </summary>
        /// <param name="sb">The root of the subband tree of the tile-component
        /// 
        /// </param>
        /// <param name="t">Tile index
        /// 
        /// </param>
        /// <param name="c">Component index
        /// 
        /// </param>
        /// <returns> The highest number of magnitude bit-planes
        /// 
        /// </returns>
        private int GetMaxMagBitsDerived(Subband sb, int t, int c)
        {
            int tmp, max = 0;
            var g = gbs.GetIntTileCompVal(t, c);

            if (!sb.isNode)
            {
                var baseStep = qsss.GetFloatTileCompVal(t, c);
                return g - 1 + sb.level - (int)Math.Floor(Math.Log(baseStep) / log2);
            }

            max = GetMaxMagBitsDerived(sb.LL, t, c);
            tmp = GetMaxMagBitsDerived(sb.LH, t, c);
            if (tmp > max)
                max = tmp;
            tmp = GetMaxMagBitsDerived(sb.HL, t, c);
            if (tmp > max)
                max = tmp;
            tmp = GetMaxMagBitsDerived(sb.HH, t, c);
            if (tmp > max)
                max = tmp;

            return max;
        }


        /// <summary> Returns the maximum number of magnitude bits in any subband in the
        /// given tile-component if expounded quantization is used
        /// 
        /// </summary>
        /// <param name="sb">The root of the subband tree of the tile-component
        /// 
        /// </param>
        /// <param name="t">Tile index
        /// 
        /// </param>
        /// <param name="c">Component index
        /// 
        /// </param>
        /// <returns> The highest number of magnitude bit-planes
        /// 
        /// </returns>
        private int GetMaxMagBitsExpounded(Subband sb, int t, int c)
        {
            int tmp, max = 0;
            var g = gbs.GetIntTileCompVal(t, c);

            if (!sb.isNode)
            {
                var baseStep = qsss.GetFloatTileCompVal(t, c);
                return g - 1 - (int)Math.Floor(Math.Log(baseStep / (((SubbandAn)sb).l2Norm * (1 << sb.anGainExp))) / log2);
            }

            max = GetMaxMagBitsExpounded(sb.LL, t, c);
            tmp = GetMaxMagBitsExpounded(sb.LH, t, c);
            if (tmp > max)
                max = tmp;
            tmp = GetMaxMagBitsExpounded(sb.HL, t, c);
            if (tmp > max)
                max = tmp;
            tmp = GetMaxMagBitsExpounded(sb.HH, t, c);
            if (tmp > max)
                max = tmp;

            return max;
        }
    }
}