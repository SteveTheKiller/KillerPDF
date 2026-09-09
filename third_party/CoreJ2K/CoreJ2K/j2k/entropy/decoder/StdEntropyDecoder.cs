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
using CoreJ2K.j2k.image;
using CoreJ2K.j2k.util;
using CoreJ2K.j2k.wavelet;
using CoreJ2K.j2k.wavelet.synthesis;
using System;
using System.Runtime.CompilerServices;

namespace CoreJ2K.j2k.entropy.decoder
{

    /// <summary> This class implements the JPEG 2000 entropy decoder, which codes stripes in
    /// code-blocks. This entropy decoding engine decodes one code-block at a time.
    /// 
    /// The code-blocks are rectangular and their dimensions must be powers of
    /// 2. Each dimension cannot be smaller than 4 and larger than 256. The product
    /// of the two dimensions (i.e. area of the code-block) cannot exceed 4096.
    /// 
    /// Context 0 of the MQ-coder is used as the uniform one (uniform,
    /// non-adaptive probability distribution). Context 1 is used for RLC
    /// coding. Contexts 2-10 are used for zero-coding (ZC), contexts 11-15 are
    /// used for sign-coding (SC) and contexts 16-18 are used for
    /// magnitude-refinement (MR).
    /// 
    /// This implementation also provides some timing features. They can be
    /// enabled by setting the 'DO_TIMING' constant of this class to true and
    /// recompiling. The timing uses the 'System.currentTimeMillis()' Java API
    /// call, which returns wall clock time, not the actual CPU time used. The
    /// timing results will be printed on the message output. Since the times
    /// reported are wall clock times and not CPU usage times they can not be added
    /// to find the total used time (i.e. some time might be counted in several
    /// places). When timing is disabled ('DO_TIMING' is false) there is no penalty
    /// if the compiler performs some basic optimizations. Even if not the penalty
    /// should be negligeable.
    /// 
    /// </summary>
    public class StdEntropyDecoder : EntropyDecoder
    {

        /// <summary>Whether to collect timing information or not: false. Used as a compile 
        /// time directive. 
        /// </summary>
        private const bool DO_TIMING = false;

        /// <summary>The cumulative wall time for the entropy coding engine, for each
        /// component. 
        /// </summary>
        //private long[] time;

        /// <summary>The bit based input for arithmetic coding bypass (i.e. raw) coding </summary>
        private ByteToBitInput? bin;

        /// <summary>The MQ decoder to use. It has in as the underlying source of coded
        /// data. 
        /// </summary>
        private MQDecoder? mq;

        /// <summary>The decoder spec </summary>
        private readonly DecoderSpecs decSpec;

        /// <summary>The options that are turned on, as flag bits. The options are
        /// 'OPT_TERM_PASS', 'OPT_RESET_MQ', 'OPT_VERT_STR_CAUSAL', 'OPT_BYPASS'
        /// and 'OPT_SEG_SYMBOLS' as defined in the StdEntropyCoderOptions
        /// interface
        /// 
        /// </summary>
        /// <seealso cref="StdEntropyCoderOptions" />
        private int options;

        // Cache for options lookup — avoids a virtual ModuleSpec.GetSpec dispatch per code-block.
        private int _cachedOptsTile = -1;
        private int _cachedOptsComp = -1;
        private int _cachedOptions;

        /// <summary>Flag to indicate if we should try to detect errors or just ignore any
        /// error resilient information 
        /// </summary>
        private readonly bool doer;

        /// <summary>Flag to indicate if we should be verbose about bit stream errors
        /// detected with the error resilience options 
        /// </summary>
        private readonly bool verber;

        /// <summary>Number of bits used for the Zero Coding lookup table </summary>
        private const int ZC_LUT_BITS = 8;

        /// <summary>Zero Coding context lookup tables for the LH global orientation </summary>
        private static readonly int[] ZC_LUT_LH = new int[1 << ZC_LUT_BITS];

        /// <summary>Zero Coding context lookup tables for the HL global orientation </summary>
        private static readonly int[] ZC_LUT_HL = new int[1 << ZC_LUT_BITS];

        /// <summary>Zero Coding context lookup tables for the HH global orientation </summary>
        private static readonly int[] ZC_LUT_HH = new int[1 << ZC_LUT_BITS];

        /// <summary>Number of bits used for the Sign Coding lookup table </summary>
        private const int SC_LUT_BITS = 9;

        /// <summary>Sign Coding context lookup table. The index into the table is a 9 bit
        /// index, which correspond to the value in the 'state' array shifted by
        /// 'SC_SHIFT'. Bits 8-5 are the signs of the horizontal-left,
        /// horizontal-right, vertical-up and vertical-down neighbors,
        /// respectively. Bit 4 is not used (0 or 1 makes no difference). Bits 3-0
        /// are the significance of the horizontal-left, horizontal-right,
        /// vertical-up and vertical-down neighbors, respectively. The least 4 bits
        /// of the value in the lookup table define the context number and the sign
        /// bit defines the "sign predictor". 
        /// </summary>
        private static readonly int[] SC_LUT = new int[1 << SC_LUT_BITS];

        /// <summary>The mask to obtain the context index from the 'SC_LUT' </summary>
        private const int SC_LUT_MASK = (1 << 4) - 1;

        /// <summary>The shift to obtain the sign predictor from the 'SC_LUT'. It must be
        /// an unsigned shift. 
        /// </summary>
        private const int SC_SPRED_SHIFT = 31;

        /// <summary>The sign bit for int data </summary>
        private const int INT_SIGN_BIT = 1 << 31;

        /// <summary>The number of bits used for the Magnitude Refinement lookup table </summary>
        private const int MR_LUT_BITS = 9;

        /// <summary>Magnitude Refinement context lookup table </summary>
        private static readonly int[] MR_LUT = new int[1 << MR_LUT_BITS];

        /// <summary>The number of contexts used </summary>
        private const int NUM_CTXTS = 19;

        /// <summary>The RLC context </summary>
        private const int RLC_CTXT = 1;

        /// <summary>The UNIFORM context (with a uniform probability distribution which
        /// does not adapt) 
        /// </summary>
        private const int UNIF_CTXT = 0;

        /// <summary>The initial states for the MQ coder </summary>
        private static readonly int[] MQ_INIT = { 46, 3, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        /// <summary>The 4 bits of the error resilience segmentation symbol (decimal 10,
        /// which is binary sequence 1010) 
        /// </summary>
        private const int SEG_SYMBOL = 10;

        /// <summary> The state array for entropy coding. Each element of the state array
        /// stores the state of two coefficients. The lower 16 bits store the state
        /// of a coefficient in row 'i' and column 'j', while the upper 16 bits
        /// store the state of a coefficient in row 'i+1' and column 'j'. The 'i'
        /// row is either the first or the third row of a stripe. This packing of
        /// the states into 32 bit words allows a faster scan of all coefficients
        /// on each coding pass and diminished the amount of data transferred. The
        /// size of the state array is increased by 1 on each side (top, bottom,
        /// left, right) to handle boundary conditions without any special logic.
        /// 
        /// The state of a coefficient is stored in the following way in the
        /// lower 16 bits, where bit 0 is the least significant bit. Bit 15 is the
        /// significance of a coefficient (0 if non-significant, 1 otherwise). Bit
        /// 14 is the visited state (i.e. if a coefficient has been coded in the
        /// significance propagation pass of the current bit-plane). Bit 13 is the
        /// "non zero-context" state (i.e. if one of the eight immediate neighbors
        /// is significant it is 1, otherwise is 0). Bits 12 to 9 store the sign of
        /// the already significant left, right, up and down neighbors (1 for
        /// negative, 0 for positive or not yet significant). Bit 8 indicates if
        /// the magnitude refinement has already been applied to the
        /// coefficient. Bits 7 to 4 store the significance of the left, right, up
        /// and down neighbors (1 for significant, 0 for non significant). Bits 3
        /// to 0 store the significance of the diagonal coefficients (up-left,
        /// up-right, down-left and down-right; 1 for significant, 0 for non
        /// significant).
        /// 
        /// The upper 16 bits the state is stored as in the lower 16 bits, but
        /// with the bits shifted up by 16.
        /// 
        /// The lower 16 bits are referred to as "row 1" ("R1") while the upper
        /// 16 bits are referred to as "row 2" ("R2").
        /// 
        /// </summary>
        private readonly int[] state;

        /// <summary>The separation between the upper and lower bits in the state array: 16
        /// 
        /// </summary>
        private const int STATE_SEP = 16;

        /// <summary>The flag bit for the significance in the state array, for row 1. </summary>
        private const int STATE_SIG_R1 = 1 << 15;

        /// <summary>The flag bit for the "visited" bit in the state array, for row 1. </summary>
        private const int STATE_VISITED_R1 = 1 << 14;

        /// <summary>The flag bit for the "not zero context" bit in the state array, for
        /// row 1. This bit is always the OR of bits STATE_H_L_R1, STATE_H_R_R1,
        /// STATE_V_U_R1, STATE_V_D_R1, STATE_D_UL_R1, STATE_D_UR_R1, STATE_D_DL_R1
        /// and STATE_D_DR_R1. 
        /// </summary>
        private const int STATE_NZ_CTXT_R1 = 1 << 13;

        /// <summary>The flag bit for the horizontal-left sign in the state array, for row
        /// 1. This bit can only be set if the STATE_H_L_R1 is also set. 
        /// </summary>
        private const int STATE_H_L_SIGN_R1 = 1 << 12;

        /// <summary>The flag bit for the horizontal-right sign in the state array, for
        /// row 1. This bit can only be set if the STATE_H_R_R1 is also set. 
        /// </summary>
        private const int STATE_H_R_SIGN_R1 = 1 << 11;

        /// <summary>The flag bit for the vertical-up sign in the state array, for row
        /// 1. This bit can only be set if the STATE_V_U_R1 is also set. 
        /// </summary>
        private const int STATE_V_U_SIGN_R1 = 1 << 10;

        /// <summary>The flag bit for the vertical-down sign in the state array, for row
        /// 1. This bit can only be set if the STATE_V_D_R1 is also set. 
        /// </summary>
        private const int STATE_V_D_SIGN_R1 = 1 << 9;

        /// <summary>The flag bit for the previous MR primitive applied in the state array,
        /// for row 1. 
        /// </summary>
        private const int STATE_PREV_MR_R1 = 1 << 8;

        /// <summary>The flag bit for the horizontal-left significance in the state array,
        /// for row 1. 
        /// </summary>
        private const int STATE_H_L_R1 = 1 << 7;

        /// <summary>The flag bit for the horizontal-right significance in the state array,
        /// for row 1. 
        /// </summary>
        private const int STATE_H_R_R1 = 1 << 6;

        /// <summary>The flag bit for the vertical-up significance in the state array, for
        /// row 1.  
        /// </summary>
        private const int STATE_V_U_R1 = 1 << 5;

        /// <summary>The flag bit for the vertical-down significance in the state array,
        /// for row 1.  
        /// </summary>
        private const int STATE_V_D_R1 = 1 << 4;

        /// <summary>The flag bit for the diagonal up-left significance in the state array,
        /// for row 1. 
        /// </summary>
        private const int STATE_D_UL_R1 = 1 << 3;

        /// <summary>The flag bit for the diagonal up-right significance in the state
        /// array, for row 1.
        /// </summary>
        private const int STATE_D_UR_R1 = 1 << 2;

        /// <summary>The flag bit for the diagonal down-left significance in the state
        /// array, for row 1. 
        /// </summary>
        private const int STATE_D_DL_R1 = 1 << 1;

        /// <summary>The flag bit for the diagonal down-right significance in the state
        /// array , for row 1.
        /// </summary>
        private const int STATE_D_DR_R1 = 1;

        /// <summary>The flag bit for the significance in the state array, for row 2. </summary>
        private const int STATE_SIG_R2 = STATE_SIG_R1 << STATE_SEP;

        /// <summary>The flag bit for the "visited" bit in the state array, for row 2. </summary>
        private const int STATE_VISITED_R2 = STATE_VISITED_R1 << STATE_SEP;

        /// <summary>The flag bit for the "not zero context" bit in the state array, for
        /// row 2. This bit is always the OR of bits STATE_H_L_R2, STATE_H_R_R2,
        /// STATE_V_U_R2, STATE_V_D_R2, STATE_D_UL_R2, STATE_D_UR_R2, STATE_D_DL_R2
        /// and STATE_D_DR_R2. 
        /// </summary>
        private const int STATE_NZ_CTXT_R2 = STATE_NZ_CTXT_R1 << STATE_SEP;

        /// <summary>The flag bit for the horizontal-left sign in the state array, for row
        /// 2. This bit can only be set if the STATE_H_L_R2 is also set. 
        /// </summary>
        private const int STATE_H_L_SIGN_R2 = STATE_H_L_SIGN_R1 << STATE_SEP;

        /// <summary>The flag bit for the horizontal-right sign in the state array, for row
        /// 2. This bit can only be set if the STATE_H_R_R2 is also set. 
        /// </summary>
        private const int STATE_H_R_SIGN_R2 = STATE_H_R_SIGN_R1 << STATE_SEP;

        /// <summary>The flag bit for the vertical-up sign in the state array, for row
        /// 2. This bit can only be set if the STATE_V_U_R2 is also set. 
        /// </summary>
        private const int STATE_V_U_SIGN_R2 = STATE_V_U_SIGN_R1 << STATE_SEP;

        /// <summary>The flag bit for the vertical-down sign in the state array, for row
        /// 2. This bit can only be set if the STATE_V_D_R2 is also set. 
        /// </summary>
        private const int STATE_V_D_SIGN_R2 = STATE_V_D_SIGN_R1 << STATE_SEP;

        /// <summary>The flag bit for the previous MR primitive applied in the state array,
        /// for row 2. 
        /// </summary>
        private const int STATE_PREV_MR_R2 = STATE_PREV_MR_R1 << STATE_SEP;

        /// <summary>The flag bit for the horizontal-left significance in the state array,
        /// for row 2. 
        /// </summary>
        private const int STATE_H_L_R2 = STATE_H_L_R1 << STATE_SEP;

        /// <summary>The flag bit for the horizontal-right significance in the state array,
        /// for row 2. 
        /// </summary>
        private const int STATE_H_R_R2 = STATE_H_R_R1 << STATE_SEP;

        /// <summary>The flag bit for the vertical-up significance in the state array, for
        /// row 2.  
        /// </summary>
        private const int STATE_V_U_R2 = STATE_V_U_R1 << STATE_SEP;

        /// <summary>The flag bit for the vertical-down significance in the state array,
        /// for row 2.  
        /// </summary>
        private const int STATE_V_D_R2 = STATE_V_D_R1 << STATE_SEP;

        /// <summary>The flag bit for the diagonal up-left significance in the state array,
        /// for row 2. 
        /// </summary>
        private const int STATE_D_UL_R2 = STATE_D_UL_R1 << STATE_SEP;

        /// <summary>The flag bit for the diagonal up-right significance in the state
        /// array, for row 2.
        /// </summary>
        private const int STATE_D_UR_R2 = STATE_D_UR_R1 << STATE_SEP;

        /// <summary>The flag bit for the diagonal down-left significance in the state
        /// array, for row 2. 
        /// </summary>
        private const int STATE_D_DL_R2 = STATE_D_DL_R1 << STATE_SEP;

        /// <summary>The flag bit for the diagonal down-right significance in the state
        /// array , for row 2.
        /// </summary>
        private const int STATE_D_DR_R2 = STATE_D_DR_R1 << STATE_SEP;

        /// <summary>The mask to isolate the significance bits for row 1 and 2 of the state 
        /// array. 
        /// </summary>
        private const int SIG_MASK_R1R2 = STATE_SIG_R1 | STATE_SIG_R2;

        /// <summary>The mask to isolate the visited bits for row 1 and 2 of the state 
        /// array. 
        /// </summary>
        private const int VSTD_MASK_R1R2 = STATE_VISITED_R1 | STATE_VISITED_R2;

        /// <summary>The mask to isolate the bits necessary to identify RLC coding state
        /// (significant, visited and non-zero context, for row 1 and 2). 
        /// </summary>
        private const int RLC_MASK_R1R2 = STATE_SIG_R1 | STATE_SIG_R2 | STATE_VISITED_R1 | STATE_VISITED_R2 | STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2;

        /// <summary>The mask to obtain the ZC_LUT index from the 'state' information </summary>
        // This is needed because of the STATE_V_D_SIGN, STATE_V_U_SIGN,
        // STATE_H_R_SIGN, and STATE_H_L_SIGN bits.
        private const int ZC_MASK = (1 << 8) - 1;

        /// <summary>The shift to obtain the SC index to 'SC_LUT' from the 'state'
        /// information, for row 1. 
        /// </summary>
        private const int SC_SHIFT_R1 = 4;

        /// <summary>The shift to obtain the SC index to 'SC_LUT' from the state
        /// information, for row 2. 
        /// </summary>
        private const int SC_SHIFT_R2 = SC_SHIFT_R1 + STATE_SEP;

        /// <summary>The bit mask to isolate the state bits relative to the sign coding
        /// lookup table ('SC_LUT'). 
        /// </summary>
        private const int SC_MASK = (1 << SC_LUT_BITS) - 1;

        /// <summary>The mask to obtain the MR index to 'MR_LUT' from the 'state'
        /// information. It is to be applied after the 'MR_SHIFT' 
        /// </summary>
        private const int MR_MASK = (1 << 9) - 1;

        /// <summary>The source code-block to entropy code (avoids reallocation for each
        /// code-block). 
        /// </summary>
        private DecLyrdCBlk srcblk = new DecLyrdCBlk();

        /// <summary>The maximum number of bit planes to decode for any code-block </summary>
        private readonly int mQuit;

        /// <summary> Instantiates a new entropy decoder engine, with the specified source of
        /// data, nominal block width and height.
        /// 
        /// </summary>
        /// <param name="src">The source of data
        /// 
        /// </param>
        /// <param name="opt">The options to use for this encoder. It is a mix of the
        /// 'OPT_TERM_PASS', 'OPT_RESET_MQ', 'OPT_VERT_STR_CAUSAL', 'OPT_BYPASS'
        /// and 'OPT_SEG_SYMBOLS' option flags.
        /// 
        /// </param>
        /// <param name="doer">If true error detection will be performed, if any error
        /// detection features have been enabled.
        /// 
        /// </param>
        /// <param name="verber">This flag indicates if the entropy decoder should be
        /// verbose about bit stream errors that are detected and concealed.
        /// 
        /// </param>
        /// <param name="mQuit">the maximum number of bit planes to decode according to
        /// the m quit condition
        /// 
        /// </param>
        public StdEntropyDecoder(CodedCBlkDataSrcDec src, DecoderSpecs decSpec, bool doer, bool verber, int mQuit) : base(src)
        {

            this.decSpec = decSpec;
            this.doer = doer;
            this.verber = verber;
            this.mQuit = mQuit;

            // If we do timing create necessary structures
#if DO_TIMING
			time = new long[src.NumComps];
			// If we are timing make sure that 'finalize' gets called.
            // CONVERSION PROBLEM?
#endif

            // Initialize internal variables
            state = new int[(decSpec.cblks.MaxCBlkWidth + 2) * ((decSpec.cblks.MaxCBlkHeight + 1) / 2 + 2)];
        }

#if DO_TIMING
		/// <summary> Prints the timing information, if collected, and calls 'finalize' on
		/// the super class.
		/// 
		/// </summary>
        ~StdEntropyDecoder()
        {
            int c;
            System.Text.StringBuilder sb;

            sb = new System.Text.StringBuilder("StdEntropyDecoder decompression wall " + "clock time:");
            for (c = 0; c < time.Length; c++)
            {
                sb.Append("\n  component ");
                sb.Append(c);
                sb.Append(": ");
                sb.Append(time[c]);
                sb.Append(" ms");
            }
            FacilityManager.GetMsgLogger().printmsg(CoreJ2K.j2k.util.MsgLogger_Fields.INFO, sb.ToString());

        }
#endif

        /// <summary> Returns the specified code-block in the current tile for the specified
        /// component, as a copy (see below).
        /// 
        /// The returned code-block may be progressive, which is indicated by
        /// the 'progressive' variable of the returned 'DataBlk' object. If a
        /// code-block is progressive it means that in a later request to this
        /// method for the same code-block it is possible to retrieve data which is
        /// a better approximation, since meanwhile more data to decode for the
        /// code-block could have been received. If the code-block is not
        /// progressive then later calls to this method for the same code-block
        /// will return the exact same data values.
        /// 
        /// The data returned by this method is always a copy of the internal
        /// data of this object, if any, and it can be modified "in place" without
        /// any problems after being returned. The 'offset' of the returned data is
        /// 0, and the 'scanw' is the same as the code-block width. See the
        /// 'DataBlk' class.
        /// 
        /// The 'ulx' and 'uly' members of the returned 'DataBlk' object
        /// contain the coordinates of the top-left corner of the block, with
        /// respect to the tile, not the subband.
        /// 
        /// </summary>
        /// <param name="c">The component for which to return the next code-block.
        /// 
        /// </param>
        /// <param name="m">The vertical index of the code-block to return, in the
        /// specified subband.
        /// 
        /// </param>
        /// <param name="n">The horizontal index of the code-block to return, in the
        /// specified subband.
        /// 
        /// </param>
        /// <param name="sb">The subband in which the code-block to return is.
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
        /// <seealso cref="DataBlk" />
        public override DataBlk GetCodeBlock(int c, int m, int n, SubbandSyn sb, DataBlk cblk)
        {
            //long stime = 0L; // Start time for timed sections
            int[] zc_lut; // The ZC lookup table to use
            int[] out_data; // The outupt data buffer
            int npasses; // The number of coding passes to perform
            int curbp; // The current magnitude bit-plane (starts at 30)
            bool error; // Error indicator
            int tslen; // Length of first terminated segment
            int tsidx; // Index of current terminated segment
            ByteInputBuffer inputBuffer = null;

            bool isterm;

            // Get the code-block to decode
            srcblk = src.GetCodeBlock(c, m, n, sb, 1, -1, srcblk);

#if DO_TIMING
			stime = (System.DateTime.Now.Ticks - 621355968000000000) / 10000;
#endif

            // Retrieve options from decSpec — cached per (tIdx, c) to avoid a virtual
            // ModuleSpec.GetSpec dispatch on every code-block invocation.
            if (_cachedOptsTile == tIdx && _cachedOptsComp == c)
            {
                options = _cachedOptions;
            }
            else
            {
                options = ((int)decSpec.ecopts.GetTileCompVal(tIdx, c));
                _cachedOptions = options;
                _cachedOptsTile = tIdx;
                _cachedOptsComp = c;
            }

            // Reset state — clear only the region actually used by this code-block.
            // The state scan-width is (w+2); stripes are ceil(h/2)+2 rows tall.
            // Clearing the full (max-size) array wastes up to ~4× work for small blocks.
            Array.Clear(state, 0, (srcblk.w + 2) * ((srcblk.h + 1) / 2 + 2));

            // Initialize output code-block
            if (cblk == null)
                cblk = new DataBlkInt();
            cblk.progressive = srcblk.prog;
            cblk.ulx = srcblk.ulx;
            cblk.uly = srcblk.uly;
            cblk.w = srcblk.w;
            cblk.h = srcblk.h;
            cblk.offset = 0;
            cblk.scanw = cblk.w;
            out_data = (int[])cblk.Data;

            if (out_data == null || out_data.Length < srcblk.w * srcblk.h)
            {
                out_data = new int[srcblk.w * srcblk.h];
                cblk.Data = out_data;
            }
            else
            {
                // Zero only the pixels used by this code-block, not the entire
                // (potentially larger, reused) buffer. Array.Clear is intrinsified.
                Array.Clear(out_data, 0, srcblk.w * srcblk.h);
            }

            if (srcblk.nl <= 0 || srcblk.nTrunc <= 0)
            {
                // 0 layers => no data to decode => return all 0s
                return cblk;
            }

            // Get the length of the first terminated segment
            tslen = (srcblk.tsLengths == null) ? srcblk.dl : srcblk.tsLengths[0];
            tsidx = 0;
            // Initialize for decoding
            npasses = srcblk.nTrunc;
            if (mq == null)
            {
                inputBuffer = new ByteInputBuffer(srcblk.data, 0, tslen);
                mq = new MQDecoder(inputBuffer, NUM_CTXTS, MQ_INIT);
            }
            else
            {
                // We always start by an MQ segment
                mq.nextSegment(srcblk.data, 0, tslen);
                mq.resetCtxts();
            }
            error = false;

            if ((options & StdEntropyCoderOptions.OPT_BYPASS) != 0)
            {
                if (bin == null)
                {
                    if (inputBuffer == null)
                        inputBuffer = mq.ByteInputBuffer;
                    bin = new ByteToBitInput(inputBuffer);
                }
            }

            // Choose correct ZC lookup table for global orientation
            switch (sb.orientation)
            {

                case Subband.WT_ORIENT_HL:
                    zc_lut = ZC_LUT_HL;
                    break;

                case Subband.WT_ORIENT_LH:
                case Subband.WT_ORIENT_LL:
                    zc_lut = ZC_LUT_LH;
                    break;

                case Subband.WT_ORIENT_HH:
                    zc_lut = ZC_LUT_HH;
                    break;

                default:
                    throw new InvalidOperationException("JJ2000 internal error");

            }

            // NOTE: we don't currently detect which is the last magnitude
            // bit-plane so that 'isterm' is true for the last pass of it. Doing
            // so would aid marginally in error detection with the predictable
            // error resilient MQ termination. However, determining which is the
            // last magnitude bit-plane is quite hard (due to ROI, quantization,
            // etc.)  and in any case the predictable error resilient termination
            // used without the arithmetic coding bypass and/or regular
            // termination modes is almost useless.

            // Loop on bit-planes and passes

            curbp = 30 - srcblk.skipMSBP;

            // Check for maximum number of bitplanes quit condition
            if (mQuit != -1 && (mQuit * 3 - 2) < npasses)
            {
                npasses = mQuit * 3 - 2;
            }

            // First bit-plane has only the cleanup pass
            if (curbp >= 0 && npasses > 0)
            {
                isterm = (options & StdEntropyCoderOptions.OPT_TERM_PASS) != 0 || ((options & StdEntropyCoderOptions.OPT_BYPASS) != 0 && (31 - StdEntropyCoderOptions.NUM_NON_BYPASS_MS_BP - srcblk.skipMSBP) >= curbp);
                error = cleanuppass(cblk, mq, curbp, state, zc_lut, isterm);
                npasses--;
                if (!error || !doer)
                    curbp--;
            }

            // Other bit-planes have the three coding passes
            if (!error || !doer)
            {
                while (curbp >= 0 && npasses > 0)
                {

                    if ((options & StdEntropyCoderOptions.OPT_BYPASS) != 0 && (curbp < 31 - StdEntropyCoderOptions.NUM_NON_BYPASS_MS_BP - srcblk.skipMSBP))
                    {
                        // Use bypass decoding mode (only all bit-planes
                        // after the first 4 bit-planes).

                        // Here starts a new raw segment
                        bin.SetByteArray(null, -1, srcblk.tsLengths[++tsidx]);
                        isterm = (options & StdEntropyCoderOptions.OPT_TERM_PASS) != 0;
                        error = rawSigProgPass(cblk, bin, curbp, state, isterm);
                        npasses--;
                        if (npasses <= 0 || (error && doer))
                            break;

                        if ((options & StdEntropyCoderOptions.OPT_TERM_PASS) != 0)
                        {
                            // Start a new raw segment
                            bin.SetByteArray(null, -1, srcblk.tsLengths[++tsidx]);
                        }
                        isterm = (options & StdEntropyCoderOptions.OPT_TERM_PASS) != 0 || ((options & StdEntropyCoderOptions.OPT_BYPASS) != 0 && (31 - StdEntropyCoderOptions.NUM_NON_BYPASS_MS_BP - srcblk.skipMSBP > curbp));
                        error = rawMagRefPass(cblk, bin, curbp, state, isterm);
                    }
                    else
                    {
                        // Do not use bypass decoding mode
                        if ((options & StdEntropyCoderOptions.OPT_TERM_PASS) != 0)
                        {
                            // Here starts a new MQ segment
                            mq.nextSegment(null, -1, srcblk.tsLengths[++tsidx]);
                        }
                        isterm = (options & StdEntropyCoderOptions.OPT_TERM_PASS) != 0;
                        error = sigProgPass(cblk, mq, curbp, state, zc_lut, isterm);
                        npasses--;
                        if (npasses <= 0 || (error && doer))
                            break;

                        if ((options & StdEntropyCoderOptions.OPT_TERM_PASS) != 0)
                        {
                            // Here starts a new MQ segment
                            mq.nextSegment(null, -1, srcblk.tsLengths[++tsidx]);
                        }
                        isterm = (options & StdEntropyCoderOptions.OPT_TERM_PASS) != 0 || ((options & StdEntropyCoderOptions.OPT_BYPASS) != 0 && (31 - StdEntropyCoderOptions.NUM_NON_BYPASS_MS_BP - srcblk.skipMSBP > curbp));
                        error = magRefPass(cblk, mq, curbp, state, isterm);
                    }

                    npasses--;
                    if (npasses <= 0 || (error && doer))
                        break;

                    if ((options & StdEntropyCoderOptions.OPT_TERM_PASS) != 0 || ((options & StdEntropyCoderOptions.OPT_BYPASS) != 0 && (curbp < 31 - StdEntropyCoderOptions.NUM_NON_BYPASS_MS_BP - srcblk.skipMSBP)))
                    {
                        // Here starts a new MQ segment
                        mq.nextSegment(null, -1, srcblk.tsLengths[++tsidx]);
                    }
                    isterm = (options & StdEntropyCoderOptions.OPT_TERM_PASS) != 0 || ((options & StdEntropyCoderOptions.OPT_BYPASS) != 0 && (31 - StdEntropyCoderOptions.NUM_NON_BYPASS_MS_BP - srcblk.skipMSBP) >= curbp);
                    error = cleanuppass(cblk, mq, curbp, state, zc_lut, isterm);
                    npasses--;
                    if (error && doer)
                        break;
                    // Goto next bit-plane
                    curbp--;
                }
            }

            // If an error ocurred conceal it
            if (error && doer)
            {
                if (verber)
                {
                    // Use string.Concat with explicit ToString() conversions to avoid
                    // DefaultInterpolatedStringHandler and its ArrayPool<char> rent/return
                    // overhead on every concealed code-block (was measured at ~12% self-CPU).
                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                        string.Concat("Error detected at bit-plane ", curbp.ToString(),
                            " in code-block (", m.ToString(), ",", n.ToString(),
                            "), sb_idx ", sb.sbandIdx.ToString(),
                            ", res. level ", sb.resLvl.ToString(), ". Concealing..."));
                }
                conceal(cblk, curbp);
            }

#if DO_TIMING
			time[c] += (System.DateTime.Now.Ticks - 621355968000000000) / 10000 - stime;
#endif

            // Return decoded block
            return cblk;
        }

        /// <summary> Returns the specified code-block in the current tile for the specified
        /// component (as a reference or copy).
        /// 
        /// The returned code-block may be progressive, which is indicated by
        /// the 'progressive' variable of the returned 'DataBlk' object. If a
        /// code-block is progressive it means that in a later request to this
        /// method for the same code-block it is possible to retrieve data which is
        /// a better approximation, since meanwhile more data to decode for the
        /// code-block could have been received. If the code-block is not
        /// progressive then later calls to this method for the same code-block
        /// will return the exact same data values.
        /// 
        /// The data returned by this method can be the data in the internal
        /// buffer of this object, if any, and thus can not be modified by the
        /// caller. The 'offset' and 'scanw' of the returned data can be
        /// arbitrary. See the 'DataBlk' class.
        /// 
        /// The 'ulx' and 'uly' members of the returned 'DataBlk' object contain
        /// the coordinates of the top-left corner of the block, with respect to
        /// the tile, not the subband.
        /// 
        /// </summary>
        /// <param name="c">The component for which to return the next code-block.
        /// 
        /// </param>
        /// <param name="m">The vertical index of the code-block to return, in the
        /// specified subband.
        /// 
        /// </param>
        /// <param name="n">The horizontal index of the code-block to return, in the
        /// specified subband.
        /// 
        /// </param>
        /// <param name="sb">The subband in which the code-block to return is.
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
        /// <seealso cref="DataBlk" />
        public override DataBlk GetInternCodeBlock(int c, int m, int n, SubbandSyn sb, DataBlk cblk)
        {
            return GetCodeBlock(c, m, n, sb, cblk);
        }

        /// <summary> Performs the significance propagation pass on the specified data and
        /// bit-plane. It decodes all insignificant samples which have, at least,
        /// one of its immediate eight neighbors already significant, using the ZC
        /// and SC primitives as needed. It toggles the "visited" state bit to 1
        /// for all those samples.
        /// 
        /// This method also checks for segmentation markers if those are
        /// present and returns true if an error is detected, or false
        /// otherwise. If an error is detected it means that the bit stream
        /// contains some erroneous bit that have led to the decoding of incorrect
        /// data. This data affects the whole last decoded bit-plane
        /// (i.e. 'bp'). If 'true' is returned the 'conceal' method should be
        /// called and no more passes should be decoded for this code-block's bit
        /// stream.
        /// 
        /// </summary>
        /// <param name="cblk">The code-block data to decode
        /// 
        /// </param>
        /// <param name="mq">The MQ-coder to use
        /// 
        /// </param>
        /// <param name="bp">The bit-plane to decode
        /// 
        /// </param>
        /// <param name="state">The state information for the code-block
        /// 
        /// </param>
        /// <param name="zc_lut">The ZC lookup table to use in ZC.
        /// 
        /// </param>
        /// <param name="isterm">If this pass has been terminated. If the pass has been
        /// terminated it can be used to check error resilience.
        /// 
        /// </param>
        /// <returns> True if an error was detected in the bit stream, false
        /// otherwise.
        /// 
        /// </returns>
        #if NET5_0_OR_GREATER || NETCOREAPP3_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
        private bool sigProgPass(DataBlk cblk, MQDecoder mq, int bp, int[] state, int[] zc_lut, bool isterm)
        {
            int j, sj; // The state index for line and stripe
            int k, sk; // The data index for line and stripe
            int dscanw; // The data scan-width
            int sscanw; // The state scan-width
            int jstep; // Stripe to stripe step for 'sj'
            int kstep; // Stripe to stripe step for 'sk'
            int stopsk; // The loop limit on the variable sk
            int csj; // Local copy (i.e. cached) of 'state[j]'
            int setmask; // The mask to set current and lower bit-planes to 1/2
                         // approximation
            int sym; // The symbol to code
            int ctxt; // The context to use
            int[] data; // The data buffer
            int s; // The stripe index
            bool causal; // Flag to indicate if stripe-causal context
                         // formation is to be used
            int nstripes; // The number of stripes in the code-block
            int sheight; // Height of the current stripe
            int off_ul, off_ur, off_dr, off_dl; // offsets
            bool error; // The error condition
            int[] sc_lut = SC_LUT; // Hoist static array reference into local
            int localOptions = options; // Hoist instance field into local

            // Initialize local variables
            dscanw = cblk.scanw;
            sscanw = cblk.w + 2;
            jstep = sscanw * StdEntropyCoderOptions.STRIPE_HEIGHT / 2 - cblk.w;
            kstep = dscanw * StdEntropyCoderOptions.STRIPE_HEIGHT - cblk.w;
            setmask = (int)(((long)3 << bp) >> 1);
            data = (int[])cblk.Data;
            nstripes = (cblk.h + StdEntropyCoderOptions.STRIPE_HEIGHT - 1) / StdEntropyCoderOptions.STRIPE_HEIGHT;
            causal = (options & StdEntropyCoderOptions.OPT_VERT_STR_CAUSAL) != 0;

            // Pre-calculate offsets in 'state' for diagonal neighbors
            off_ul = -sscanw - 1; // up-left
            off_ur = -sscanw + 1; // up-right
            off_dr = sscanw + 1; // down-right
            off_dl = sscanw - 1; // down-left

            // Decode stripe by stripe
            sk = cblk.offset;
            sj = sscanw + 1;
            for (s = nstripes - 1; s >= 0; s--, sk += kstep, sj += jstep)
            {
                sheight = (s != 0) ? StdEntropyCoderOptions.STRIPE_HEIGHT : cblk.h - (nstripes - 1) * StdEntropyCoderOptions.STRIPE_HEIGHT;
                stopsk = sk + cblk.w;
                // Scan by set of 1 stripe column at a time
                for (; sk < stopsk; sk++, sj++)
                {
                    // Do half top of column
                    j = sj;
                    csj = state[j];
                    // If any of the two samples is not significant and has a
                    // non-zero context (i.e. some neighbor is significant) we can 
                    // not skip them
                    if ((((~csj) & (csj << 2)) & SIG_MASK_R1R2) != 0)
                    {
                        k = sk;
                        // Scan first row
                        if ((csj & (STATE_SIG_R1 | STATE_NZ_CTXT_R1)) == STATE_NZ_CTXT_R1)
                        {
                            // Use zero coding
                            if (mq.decodeSymbol(zc_lut[csj & ZC_MASK]) != 0)
                            {
                                // Became significant
                                // Use sign coding
                                ctxt = sc_lut[((csj >>> SC_SHIFT_R1)) & SC_MASK];
                                sym = mq.decodeSymbol(ctxt & SC_LUT_MASK) ^ ((ctxt >>> SC_SPRED_SHIFT));
                                // Update data
                                data[k] = (sym << 31) | setmask;
                                // Update state information (significant bit,
                                // visited bit, neighbor significant bit of
                                // neighbors, non zero context of neighbors, sign
                                // of neighbors)
                                if (!causal)
                                {
                                    // If in causal mode do not change contexts of 
                                    // previous stripe.
                                    state[j + off_ul] |= STATE_NZ_CTXT_R2 | STATE_D_DR_R2;
                                    state[j + off_ur] |= STATE_NZ_CTXT_R2 | STATE_D_DL_R2;
                                }
                                // Update sign state information of neighbors (branchless)
                                int signMask = -sym;
                                csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2 | (STATE_V_U_SIGN_R2 & signMask);
                                if (!causal)
                                {
                                    // If in causal mode do not change
                                    // contexts of previous stripe.
                                    state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2 | (STATE_V_D_SIGN_R2 & signMask);
                                }
                                state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | (STATE_H_L_SIGN_R1 & signMask) | STATE_D_UL_R2;
                                state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | (STATE_H_R_SIGN_R1 & signMask) | STATE_D_UR_R2;
                            }
                            else
                            {
                                csj |= STATE_VISITED_R1;
                            }
                        }
                        if (sheight < 2)
                        {
                            state[j] = csj;
                            continue;
                        }
                        // Scan second row
                        if ((csj & (STATE_SIG_R2 | STATE_NZ_CTXT_R2)) == STATE_NZ_CTXT_R2)
                        {
                            k += dscanw;
                            // Use zero coding
                            if (mq.decodeSymbol(zc_lut[((csj >>> STATE_SEP)) & ZC_MASK]) != 0)
                            {
                                // Became significant
                                // Use sign coding
                                ctxt = sc_lut[((csj >>> SC_SHIFT_R2)) & SC_MASK];
                                sym = mq.decodeSymbol(ctxt & SC_LUT_MASK) ^ ((ctxt >>> SC_SPRED_SHIFT));
                                // Update data
                                data[k] = (sym << 31) | setmask;
                                // Update state information (significant bit,
                                // visited bit, neighbor significant bit of
                                // neighbors, non zero context of neighbors, sign
                                // of neighbors)
                                state[j + off_dl] |= STATE_NZ_CTXT_R1 | STATE_D_UR_R1;
                                state[j + off_dr] |= STATE_NZ_CTXT_R1 | STATE_D_UL_R1;
                                // Update sign state information of neighbors (branchless)
                                {
                                    int signMask = -sym;
                                    csj |= STATE_SIG_R2 | STATE_VISITED_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1 | (STATE_V_D_SIGN_R1 & signMask);
                                    state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1 | (STATE_V_U_SIGN_R1 & signMask);
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2 | (STATE_H_L_SIGN_R2 & signMask);
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2 | (STATE_H_R_SIGN_R2 & signMask);
                                }
                            }
                            else
                            {
                                csj |= STATE_VISITED_R2;
                            }
                        }
                        state[j] = csj;
                    }
                    // Do half bottom of column
                    if (sheight < 3)
                        continue;
                    j += sscanw;
                    csj = state[j];
                    // If any of the two samples is not significant and has a
                    // non-zero context (i.e. some neighbor is significant) we can 
                    // not skip them
                    if ((((~csj) & (csj << 2)) & SIG_MASK_R1R2) != 0)
                    {
                        k = sk + (dscanw << 1);
                        // Scan first row
                        if ((csj & (STATE_SIG_R1 | STATE_NZ_CTXT_R1)) == STATE_NZ_CTXT_R1)
                        {
                            // Use zero coding
                            if (mq.decodeSymbol(zc_lut[csj & ZC_MASK]) != 0)
                            {
                                // Became significant
                                // Use sign coding
                                ctxt = sc_lut[((csj >>> SC_SHIFT_R1)) & SC_MASK];
                                sym = mq.decodeSymbol(ctxt & SC_LUT_MASK) ^ ((ctxt >>> SC_SPRED_SHIFT));
                                // Update data
                                data[k] = (sym << 31) | setmask;
                                // Update state information (significant bit,
                                // visited bit, neighbor significant bit of
                                // neighbors, non zero context of neighbors, sign
                                // of neighbors)
                                state[j + off_ul] |= STATE_NZ_CTXT_R2 | STATE_D_DR_R2;
                                state[j + off_ur] |= STATE_NZ_CTXT_R2 | STATE_D_DL_R2;
                                // Update sign state information of neighbors (branchless)
                                int signMask = -sym;
                                csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2 | (STATE_V_U_SIGN_R2 & signMask);
                                state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2 | (STATE_V_D_SIGN_R2 & signMask);
                                state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | (STATE_H_L_SIGN_R1 & signMask) | STATE_D_UL_R2;
                                state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | (STATE_H_R_SIGN_R1 & signMask) | STATE_D_UR_R2;
                            }
                            else
                            {
                                csj |= STATE_VISITED_R1;
                            }
                        }
                        if (sheight < 4)
                        {
                            state[j] = csj;
                            continue;
                        }
                        // Scan second row
                        if ((csj & (STATE_SIG_R2 | STATE_NZ_CTXT_R2)) == STATE_NZ_CTXT_R2)
                        {
                            k += dscanw;
                            // Use zero coding
                            if (mq.decodeSymbol(zc_lut[((csj >>> STATE_SEP)) & ZC_MASK]) != 0)
                            {
                                // Became significant
                                // Use sign coding
                                ctxt = sc_lut[((csj >>> SC_SHIFT_R2)) & SC_MASK];
                                sym = mq.decodeSymbol(ctxt & SC_LUT_MASK) ^ ((ctxt >>> SC_SPRED_SHIFT));
                                // Update data
                                data[k] = (sym << 31) | setmask;
                                // Update state information (significant bit,
                                // visited bit, neighbor significant bit of
                                // neighbors, non zero context of neighbors, sign
                                // of neighbors)
                                state[j + off_dl] |= STATE_NZ_CTXT_R1 | STATE_D_UR_R1;
                                state[j + off_dr] |= STATE_NZ_CTXT_R1 | STATE_D_UL_R1;
                                // Update sign state information of neighbors (branchless)
                                {
                                    int signMask = -sym;
                                    csj |= STATE_SIG_R2 | STATE_VISITED_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1 | (STATE_V_D_SIGN_R1 & signMask);
                                    state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1 | (STATE_V_U_SIGN_R1 & signMask);
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2 | (STATE_H_L_SIGN_R2 & signMask);
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2 | (STATE_H_R_SIGN_R2 & signMask);
                                }
                            }
                            else
                            {
                                csj |= STATE_VISITED_R2;
                            }
                        }
                        state[j] = csj;
                    }
                }
            }

            error = false;

            // Check the error resilience termination
            if (isterm && (localOptions & StdEntropyCoderOptions.OPT_PRED_TERM) != 0)
            {
                error = mq.checkPredTerm();
            }

            // Reset the MQ context states if we need to
            if ((localOptions & StdEntropyCoderOptions.OPT_RESET_MQ) != 0)
            {
                mq.resetCtxts();
            }

            // Return error condition
            return error;
        }

        /// <summary> Performs the significance propagation pass on the specified data and
        /// bit-plane. It decodes all insignificant samples which have, at least,
        /// one of its immediate eight neighbors already significant, using the ZC
        /// and SC primitives as needed. It toggles the "visited" state bit to 1
        /// for all those samples.
        /// 
        /// This method bypasses the arithmetic coder and reads "raw" symbols
        /// from the bit stream.
        /// 
        /// This method also checks for segmentation markers if those are
        /// present and returns true if an error is detected, or false
        /// otherwise. If an error is detected it measn that the bit stream contains
        /// some erroneous bit that have led to the decoding of incorrect
        /// data. This data affects the whole last decoded bit-plane (i.e. 'bp'). If
        /// 'true' is returned the 'conceal' method should be called and no more
        /// passes should be decoded for this code-block's bit stream.
        /// 
        /// </summary>
        /// <param name="cblk">The code-block data to decode
        /// 
        /// </param>
        /// <param name="bin">The raw bit based input
        /// 
        /// </param>
        /// <param name="bp">The bit-plane to decode
        /// 
        /// </param>
        /// <param name="state">The state information for the code-block
        /// 
        /// </param>
        /// <param name="isterm">If this pass has been terminated. If the pass has been
        /// terminated it can be used to check error resilience.
        /// 
        /// </param>
        /// <returns> True if an error was detected in the bit stream, false
        /// otherwise.
        /// 
        /// </returns>
        #if NET5_0_OR_GREATER || NETCOREAPP3_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
        private bool rawSigProgPass(DataBlk cblk, ByteToBitInput bin, int bp, int[] state, bool isterm)
        {
            int j, sj; // The state index for line and stripe
            int k, sk; // The data index for line and stripe
            int dscanw; // The data scan-width
            int sscanw; // The state scan-width
            int jstep; // Stripe to stripe step for 'sj'
            int kstep; // Stripe to stripe step for 'sk'
            int stopsk; // The loop limit on the variable sk
            int csj; // Local copy (i.e. cached) of 'state[j]'
            int setmask; // The mask to set current and lower bit-planes to 1/2
                         // approximation
            int sym; // The symbol to code
            int[] data; // The data buffer
            int s; // The stripe index
            bool causal; // Flag to indicate if stripe-causal context
                         // formation is to be used
            int nstripes; // The number of stripes in the code-block
            int sheight; // Height of the current stripe
            int off_ul, off_ur, off_dr, off_dl; // offsets
            bool error; // The error condition
            int localOptions = options; // Hoist instance field into local

            // Initialize local variables
            dscanw = cblk.scanw;
            sscanw = cblk.w + 2;
            jstep = sscanw * StdEntropyCoderOptions.STRIPE_HEIGHT / 2 - cblk.w;
            kstep = dscanw * StdEntropyCoderOptions.STRIPE_HEIGHT - cblk.w;
            setmask = (int)(((long)3 << bp) >> 1);
            data = (int[])cblk.Data;
            nstripes = (cblk.h + StdEntropyCoderOptions.STRIPE_HEIGHT - 1) / StdEntropyCoderOptions.STRIPE_HEIGHT;
            causal = (localOptions & StdEntropyCoderOptions.OPT_VERT_STR_CAUSAL) != 0;

            // Pre-calculate offsets in 'state' for diagonal neighbors
            off_ul = -sscanw - 1; // up-left
            off_ur = -sscanw + 1; // up-right
            off_dr = sscanw + 1; // down-right
            off_dl = sscanw - 1; // down-left

            // Decode stripe by stripe
            sk = cblk.offset;
            sj = sscanw + 1;
            for (s = nstripes - 1; s >= 0; s--, sk += kstep, sj += jstep)
            {
                sheight = (s != 0) ? StdEntropyCoderOptions.STRIPE_HEIGHT : cblk.h - (nstripes - 1) * StdEntropyCoderOptions.STRIPE_HEIGHT;
                stopsk = sk + cblk.w;
                // Scan by set of 1 stripe column at a time
                for (; sk < stopsk; sk++, sj++)
                {
                    // Do half top of column
                    j = sj;
                    csj = state[j];
                    // If any of the two samples is not significant and has a
                    // non-zero context (i.e. some neighbor is significant) we can 
                    // not skip them
                    if ((((~csj) & (csj << 2)) & SIG_MASK_R1R2) != 0)
                    {
                        k = sk;
                        // Scan first row
                        if ((csj & (STATE_SIG_R1 | STATE_NZ_CTXT_R1)) == STATE_NZ_CTXT_R1)
                        {
                            // Use zero coding
                            if (bin.readBit() != 0)
                            {
                                // Became significant
                                // Use sign coding
                                sym = bin.readBit();
                                // Update data
                                data[k] = (sym << 31) | setmask;
                                // Update state information (significant bit,
                                // visited bit, neighbor significant bit of
                                // neighbors, non zero context of neighbors, sign
                                // of neighbors)
                                if (!causal)
                                {
                                    // If in causal mode do not change contexts of 
                                    // previous stripe.
                                    state[j + off_ul] |= STATE_NZ_CTXT_R2 | STATE_D_DR_R2;
                                    state[j + off_ur] |= STATE_NZ_CTXT_R2 | STATE_D_DL_R2;
                                }
                                // Update sign state information of neighbors (branchless)
                                {
                                    int signMask = -sym;
                                    csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2 | (STATE_V_U_SIGN_R2 & signMask);
                                    if (!causal)
                                    {
                                        // If in causal mode do not change
                                        // contexts of previous stripe.
                                        state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2 | (STATE_V_D_SIGN_R2 & signMask);
                                    }
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | (STATE_H_L_SIGN_R1 & signMask) | STATE_D_UL_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | (STATE_H_R_SIGN_R1 & signMask) | STATE_D_UR_R2;
                                }
                            }
                            else
                            {
                                csj |= STATE_VISITED_R1;
                            }
                        }
                        if (sheight < 2)
                        {
                            state[j] = csj;
                            continue;
                        }
                        if ((csj & (STATE_SIG_R2 | STATE_NZ_CTXT_R2)) == STATE_NZ_CTXT_R2)
                        {
                            k += dscanw;
                            // Use zero coding
                            if (bin.readBit() != 0)
                            {
                                // Became significant
                                // Use sign coding
                                sym = bin.readBit();
                                // Update data
                                data[k] = (sym << 31) | setmask;
                                // Update state information (significant bit,
                                // visited bit, neighbor significant bit of
                                // neighbors, non zero context of neighbors, sign
                                // of neighbors)
                                state[j + off_dl] |= STATE_NZ_CTXT_R1 | STATE_D_UR_R1;
                                state[j + off_dr] |= STATE_NZ_CTXT_R1 | STATE_D_UL_R1;
                                // Update sign state information of neighbors (branchless)
                                {
                                    int signMask = -sym;
                                    csj |= STATE_SIG_R2 | STATE_VISITED_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1 | (STATE_V_D_SIGN_R1 & signMask);
                                    state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1 | (STATE_V_U_SIGN_R1 & signMask);
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2 | (STATE_H_L_SIGN_R2 & signMask);
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2 | (STATE_H_R_SIGN_R2 & signMask);
                                }
                            }
                            else
                            {
                                csj |= STATE_VISITED_R2;
                            }
                        }
                        state[j] = csj;
                    }
                    // Do half bottom of column
                    if (sheight < 3)
                        continue;
                    j += sscanw;
                    csj = state[j];
                    // If any of the two samples is not significant and has a
                    // non-zero context (i.e. some neighbor is significant) we can 
                    // not skip them
                    if ((((~csj) & (csj << 2)) & SIG_MASK_R1R2) != 0)
                    {
                        k = sk + (dscanw << 1);
                        // Scan first row
                        if ((csj & (STATE_SIG_R1 | STATE_NZ_CTXT_R1)) == STATE_NZ_CTXT_R1)
                        {
                            // Use zero coding
                            if (bin.readBit() != 0)
                            {
                                // Became significant
                                // Use sign coding
                                sym = bin.readBit();
                                // Update data
                                data[k] = (sym << 31) | setmask;
                                // Update state information (significant bit,
                                // visited bit, neighbor significant bit of
                                // neighbors, non zero context of neighbors, sign
                                // of neighbors)
                                state[j + off_ul] |= STATE_NZ_CTXT_R2 | STATE_D_DR_R2;
                                state[j + off_ur] |= STATE_NZ_CTXT_R2 | STATE_D_DL_R2;
                                // Update sign state information of neighbors (branchless)
                                {
                                    int signMask = -sym;
                                    csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2 | (STATE_V_U_SIGN_R2 & signMask);
                                    state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2 | (STATE_V_D_SIGN_R2 & signMask);
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | (STATE_H_L_SIGN_R1 & signMask) | STATE_D_UL_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | (STATE_H_R_SIGN_R1 & signMask) | STATE_D_UR_R2;
                                }
                            }
                            else
                            {
                                csj |= STATE_VISITED_R1;
                            }
                        }
                        if (sheight < 4)
                        {
                            state[j] = csj;
                            continue;
                        }
                        // Scan second row
                        if ((csj & (STATE_SIG_R2 | STATE_NZ_CTXT_R2)) == STATE_NZ_CTXT_R2)
                        {
                            k += dscanw;
                            // Use zero coding
                            if (bin.readBit() != 0)
                            {
                                // Became significant
                                // Use sign coding
                                sym = bin.readBit();
                                // Update data
                                data[k] = (sym << 31) | setmask;
                                // Update state information (significant bit,
                                // visited bit, neighbor significant bit of
                                // neighbors, non zero context of neighbors, sign
                                // of neighbors)
                                state[j + off_dl] |= STATE_NZ_CTXT_R1 | STATE_D_UR_R1;
                                state[j + off_dr] |= STATE_NZ_CTXT_R1 | STATE_D_UL_R1;
                                // Update sign state information of neighbors (branchless)
                                {
                                    int signMask = -sym;
                                    csj |= STATE_SIG_R2 | STATE_VISITED_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1 | (STATE_V_D_SIGN_R1 & signMask);
                                    state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1 | (STATE_V_U_SIGN_R1 & signMask);
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2 | (STATE_H_L_SIGN_R2 & signMask);
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2 | (STATE_H_R_SIGN_R2 & signMask);
                                }
                            }
                            else
                            {
                                csj |= STATE_VISITED_R2;
                            }
                        }
                        state[j] = csj;
                    }
                }
            }

            error = false;

            // Check the byte padding if the pass is terminated and if the error
            // resilience predictable termination is signaled in COx marker.
            if (isterm && (localOptions & StdEntropyCoderOptions.OPT_PRED_TERM) != 0)
            {
                error = bin.checkBytePadding();
            }

            // Return error condition
            return error;
        }

        /// <summary> Performs the magnitude refinement pass on the specified data and
        /// bit-plane. It decodes the samples which are significant and which do not
        /// have the "visited" state bit turned on, using the MR primitive. The
        /// "visited" state bit is not mofified for any samples.
        /// 
        /// This method also checks for segmentation markers if those are
        /// present and returns true if an error is detected, or false
        /// otherwise. If an error is detected it means that the bit stream contains
        /// some erroneous bit that have led to the decoding of incorrect
        /// data. This data affects the whole last decoded bit-plane (i.e. 'bp'). If
        /// 'true' is returned the 'conceal' method should be called and no more
        /// passes should be decoded for this code-block's bit stream.
        /// 
        /// </summary>
        /// <param name="cblk">The code-block data to decode
        /// 
        /// </param>
        /// <param name="mq">The MQ-decoder to use
        /// 
        /// </param>
        /// <param name="bp">The bit-plane to decode
        /// 
        /// </param>
        /// <param name="state">The state information for the code-block
        /// 
        /// </param>
        /// <param name="isterm">If this pass has been terminated. If the pass has been
        /// terminated it can be used to check error resilience.
        /// 
        /// </param>
        /// <returns> True if an error was detected in the bit stream, false
        /// otherwise.
        /// 
        /// </returns>
        #if NET5_0_OR_GREATER || NETCOREAPP3_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
        private bool magRefPass(DataBlk cblk, MQDecoder mq, int bp, int[] state, bool isterm)
        {
            int j, sj; // The state index for line and stripe
            int k, sk; // The data index for line and stripe
            int dscanw; // The data scan-width
            int sscanw; // The state scan-width
            int jstep; // Stripe to stripe step for 'sj'
            int kstep; // Stripe to stripe step for 'sk'
            int stopsk; // The loop limit on the variable sk
            int csj; // Local copy (i.e. cached) of 'state[j]'
            int setmask; // The mask to set lower bit-planes to 1/2 approximation
            int resetmask; // The mask to reset approximation bit-planes
            int sym; // The symbol to decode
            int[] data; // The data buffer
            int s; // The stripe index
            int nstripes; // The number of stripes in the code-block
            int sheight; // Height of the current stripe
            bool error; // The error condition
            int[] mr_lut = MR_LUT; // Hoist static array reference into local
            int localOptions = options; // Hoist instance field into local

            // Initialize local variables
            dscanw = cblk.scanw;
            sscanw = cblk.w + 2;
            jstep = sscanw * StdEntropyCoderOptions.STRIPE_HEIGHT / 2 - cblk.w;
            kstep = dscanw * StdEntropyCoderOptions.STRIPE_HEIGHT - cblk.w;
            setmask = (1 << bp) >> 1;
            resetmask = (-1) << (bp + 1);
            data = (int[])cblk.Data;
            nstripes = (cblk.h + StdEntropyCoderOptions.STRIPE_HEIGHT - 1) / StdEntropyCoderOptions.STRIPE_HEIGHT;

            // Decode stripe by stripe
            sk = cblk.offset;
            sj = sscanw + 1;
            for (s = nstripes - 1; s >= 0; s--, sk += kstep, sj += jstep)
            {
                sheight = (s != 0) ? StdEntropyCoderOptions.STRIPE_HEIGHT : cblk.h - (nstripes - 1) * StdEntropyCoderOptions.STRIPE_HEIGHT;
                stopsk = sk + cblk.w;
                // Scan by set of 1 stripe column at a time
                for (; sk < stopsk; sk++, sj++)
                {
                    // Do half top of column
                    j = sj;
                    csj = state[j];
                    // If any of the two samples is significant and not yet
                    // visited in the current bit-plane we can not skip them
                    if (((((csj >>> 1)) & (~csj)) & VSTD_MASK_R1R2) != 0)
                    {
                        k = sk;
                        // Scan first row
                        if ((csj & (STATE_SIG_R1 | STATE_VISITED_R1)) == STATE_SIG_R1)
                        {
                            // Use MR primitive
                            sym = mq.decodeSymbol(mr_lut[csj & MR_MASK]);
                            // Update the data
                            data[k] &= resetmask;
                            data[k] |= (sym << bp) | setmask;
                            // Update the STATE_PREV_MR bit
                            csj |= STATE_PREV_MR_R1;
                        }
                        if (sheight < 2)
                        {
                            state[j] = csj;
                            continue;
                        }
                        // Scan second row
                        if ((csj & (STATE_SIG_R2 | STATE_VISITED_R2)) == STATE_SIG_R2)
                        {
                            k += dscanw;
                            // Use MR primitive
                            sym = mq.decodeSymbol(mr_lut[((csj >>> STATE_SEP)) & MR_MASK]);
                            // Update the data
                            data[k] &= resetmask;
                            data[k] |= (sym << bp) | setmask;
                            // Update the STATE_PREV_MR bit
                            csj |= STATE_PREV_MR_R2;
                        }
                        state[j] = csj;
                    }
                    // Do half bottom of column
                    if (sheight < 3)
                        continue;
                    j += sscanw;
                    csj = state[j];
                    // If any of the two samples is significant and not yet
                    // visited in the current bit-plane we can not skip them
                    if (((((csj >>> 1)) & (~csj)) & VSTD_MASK_R1R2) != 0)
                    {
                        k = sk + (dscanw << 1);
                        // Scan first row
                        if ((csj & (STATE_SIG_R1 | STATE_VISITED_R1)) == STATE_SIG_R1)
                        {
                            // Use MR primitive
                            sym = mq.decodeSymbol(mr_lut[csj & MR_MASK]);
                            // Update the data
                            data[k] &= resetmask;
                            data[k] |= (sym << bp) | setmask;
                            // Update the STATE_PREV_MR bit
                            csj |= STATE_PREV_MR_R1;
                        }
                        if (sheight < 4)
                        {
                            state[j] = csj;
                            continue;
                        }
                        // Scan second row
                        if ((state[j] & (STATE_SIG_R2 | STATE_VISITED_R2)) == STATE_SIG_R2)
                        {
                            k += dscanw;
                            // Use MR primitive
                            sym = mq.decodeSymbol(mr_lut[((csj >>> STATE_SEP)) & MR_MASK]);
                            // Update the data
                            data[k] &= resetmask;
                            data[k] |= (sym << bp) | setmask;
                            // Update the STATE_PREV_MR bit
                            csj |= STATE_PREV_MR_R2;
                        }
                        state[j] = csj;
                    }
                }
            }

            error = false;

            // Check the error resilient termination
            if (isterm && (localOptions & StdEntropyCoderOptions.OPT_PRED_TERM) != 0)
            {
                error = mq.checkPredTerm();
            }

            // Reset the MQ context states if we need to
            if ((localOptions & StdEntropyCoderOptions.OPT_RESET_MQ) != 0)
            {
                mq.resetCtxts();
            }

            // Return error condition
            return error;
        }

        /// <summary> Performs the magnitude refinement pass on the specified data and
        /// bit-plane. It decodes the samples which are significant and which do
        /// not have the "visited" state bit turned on, using the MR primitive. The
        /// "visited" state bit is not mofified for any samples.
        /// 
        /// This method bypasses the arithmetic coder and reads "raw" symbols
        /// from the bit stream.
        /// 
        /// This method also checks for segmentation markers if those are
        /// present and returns true if an error is detected, or false
        /// otherwise. If an error is detected it measn that the bit stream
        /// contains some erroneous bit that have led to the decoding of incorrect
        /// data. This data affects the whole last decoded bit-plane
        /// (i.e. 'bp'). If 'true' is returned the 'conceal' method should be
        /// called and no more passes should be decoded for this code-block's bit
        /// stream.
        /// 
        /// </summary>
        /// <param name="cblk">The code-block data to decode
        /// 
        /// </param>
        /// <param name="bin">The raw bit based input
        /// 
        /// </param>
        /// <param name="bp">The bit-plane to decode
        /// 
        /// </param>
        /// <param name="state">The state information for the code-block
        /// 
        /// </param>
        /// <param name="isterm">If this pass has been terminated. If the pass has been
        /// terminated it can be used to check error resilience.
        /// 
        /// </param>
        /// <returns> True if an error was detected in the bit stream, false
        /// otherwise.
        /// 
        /// </returns>
        #if NET5_0_OR_GREATER || NETCOREAPP3_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
        private bool rawMagRefPass(DataBlk cblk, ByteToBitInput bin, int bp, int[] state, bool isterm)
        {
            int j, sj; // The state index for line and stripe
            int k, sk; // The data index for line and stripe
            int dscanw; // The data scan-width
            int sscanw; // The state scan-width
            int jstep; // Stripe to stripe step for 'sj'
            int kstep; // Stripe to stripe step for 'sk'
            int stopsk; // The loop limit on the variable sk
            int csj; // Local copy (i.e. cached) of 'state[j]'
            int setmask; // The mask to set lower bit-planes to 1/2 approximation
            int resetmask; // The mask to reset approximation bit-planes
            int sym; // The symbol to decode
            int[] data; // The data buffer
            int s; // The stripe index
            int nstripes; // The number of stripes in the code-block
            int sheight; // Height of the current stripe
            bool error; // The error condition
            int localOptions = options; // Hoist instance field into local

            // Initialize local variables
            dscanw = cblk.scanw;
            sscanw = cblk.w + 2;
            jstep = sscanw * StdEntropyCoderOptions.STRIPE_HEIGHT / 2 - cblk.w;
            kstep = dscanw * StdEntropyCoderOptions.STRIPE_HEIGHT - cblk.w;
            setmask = (1 << bp) >> 1;
            resetmask = (-1) << (bp + 1);
            data = (int[])cblk.Data;
            nstripes = (cblk.h + StdEntropyCoderOptions.STRIPE_HEIGHT - 1) / StdEntropyCoderOptions.STRIPE_HEIGHT;

            // Decode stripe by stripe
            sk = cblk.offset;
            sj = sscanw + 1;
            for (s = nstripes - 1; s >= 0; s--, sk += kstep, sj += jstep)
            {
                sheight = (s != 0) ? StdEntropyCoderOptions.STRIPE_HEIGHT : cblk.h - (nstripes - 1) * StdEntropyCoderOptions.STRIPE_HEIGHT;
                stopsk = sk + cblk.w;
                // Scan by set of 1 stripe column at a time
                for (; sk < stopsk; sk++, sj++)
                {
                    // Do half top of column
                    j = sj;
                    csj = state[j];
                    // If any of the two samples is significant and not yet
                    // visited in the current bit-plane we can not skip them
                    if (((((csj >>> 1)) & (~csj)) & VSTD_MASK_R1R2) != 0)
                    {
                        k = sk;
                        // Scan first row
                        if ((csj & (STATE_SIG_R1 | STATE_VISITED_R1)) == STATE_SIG_R1)
                        {
                            // Read raw bit (no MR primative)
                            sym = bin.readBit();
                            // Update the data
                            data[k] &= resetmask;
                            data[k] |= (sym << bp) | setmask;
                            // No need to set STATE_PREV_MR_R1 since all magnitude 
                            // refinement passes to follow are "raw"
                        }
                        if (sheight < 2)
                            continue;
                        // Scan second row
                        if ((csj & (STATE_SIG_R2 | STATE_VISITED_R2)) == STATE_SIG_R2)
                        {
                            k += dscanw;
                            // Read raw bit (no MR primative)
                            sym = bin.readBit();
                            // Update the data
                            data[k] &= resetmask;
                            data[k] |= (sym << bp) | setmask;
                            // No need to set STATE_PREV_MR_R1 since all magnitude 
                            // refinement passes to follow are "raw"
                        }
                    }
                    // Do half bottom of column
                    if (sheight < 3)
                        continue;
                    j += sscanw;
                    csj = state[j];
                    // If any of the two samples is significant and not yet
                    // visited in the current bit-plane we can not skip them
                    if (((((csj >>> 1)) & (~csj)) & VSTD_MASK_R1R2) != 0)
                    {
                        k = sk + (dscanw << 1);
                        // Scan first row
                        if ((csj & (STATE_SIG_R1 | STATE_VISITED_R1)) == STATE_SIG_R1)
                        {
                            // Read raw bit (no MR primative)
                            sym = bin.readBit();
                            // Update the data
                            data[k] &= resetmask;
                            data[k] |= (sym << bp) | setmask;
                            // No need to set STATE_PREV_MR_R1 since all magnitude 
                            // refinement passes to follow are "raw"
                        }
                        if (sheight < 4)
                            continue;
                        // Scan second row
                        if ((state[j] & (STATE_SIG_R2 | STATE_VISITED_R2)) == STATE_SIG_R2)
                        {
                            k += dscanw;
                            // Read raw bit (no MR primative)
                            sym = bin.readBit();
                            // Update the data
                            data[k] &= resetmask;
                            data[k] |= (sym << bp) | setmask;
                            // No need to set STATE_PREV_MR_R1 since all magnitude 
                            // refinement passes to follow are "raw"
                        }
                    }
                }
            }

            error = false;

            //  Check the byte padding if the pass is terminated and the
            // predictable termination is signaled in COx marker.
            if (isterm && (localOptions & StdEntropyCoderOptions.OPT_PRED_TERM) != 0)
            {
                error = bin.checkBytePadding();
            }

            // Return error condition
            return error;
        }

        /// <summary> Performs the cleanup pass on the specified data and bit-plane. It
        /// decodes all insignificant samples which have its "visited" state bit
        /// off, using the ZC, SC, and RLC primitives. It toggles the "visited"
        /// state bit to 0 (off) for all samples in the code-block.
        /// 
        /// This method also checks for segmentation markers if those are
        /// present and returns true if an error is detected, or false
        /// otherwise. If an error is detected it measn that the bit stream
        /// contains some erroneous bit that have led to the decoding of incorrect
        /// data. This data affects the whole last decoded bit-plane
        /// (i.e. 'bp'). If 'true' is returned the 'conceal' method should be
        /// called and no more passes should be decoded for this code-block's bit
        /// stream.
        /// 
        /// </summary>
        /// <param name="cblk">The code-block data to code
        /// 
        /// </param>
        /// <param name="mq">The MQ-coder to use
        /// 
        /// </param>
        /// <param name="bp">The bit-plane to decode
        /// 
        /// </param>
        /// <param name="state">The state information for the code-block
        /// 
        /// </param>
        /// <param name="zc_lut">The ZC lookup table to use in ZC.
        /// 
        /// </param>
        /// <param name="isterm">If this pass has been terminated. If the pass has been
        /// terminated it can be used to check error resilience.
        /// 
        /// </param>
        /// <returns> True if an error was detected in the bit stream, false
        /// otherwise.
        /// 
        /// </returns>
        #if NET5_0_OR_GREATER || NETCOREAPP3_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
        private bool cleanuppass(DataBlk cblk, MQDecoder mq, int bp, int[] state, int[] zc_lut, bool isterm)
        {
            int j, sj; // The state index for line and stripe
            int k, sk; // The data index for line and stripe
            int dscanw; // The data scan-width
            int sscanw; // The state scan-width
            int jstep; // Stripe to stripe step for 'sj'
            int kstep; // Stripe to stripe step for 'sk'
            int stopsk; // The loop limit on the variable sk
            int csj; // Local copy (i.e. cached) of 'state[j]'
            int setmask; // The mask to set current and lower bit-planes to 1/2
                         // approximation
            int sym; // The decoded symbol
            int rlclen; // Length of RLC
            int ctxt; // The context to use
            int[] data; // The data buffer
            int s; // The stripe index
            bool causal; // Flag to indicate if stripe-causal context
                         // formation is to be used
            int nstripes; // The number of stripes in the code-block
            int sheight; // Height of the current stripe
            int off_ul, off_ur, off_dr, off_dl; // offsets
            bool error; // The error condition
            int[] sc_lut = SC_LUT; // Hoist static array reference into local
            int localOptions = options; // Hoist instance field into local

            // Initialize local variables
            dscanw = cblk.scanw;
            sscanw = cblk.w + 2;
            jstep = sscanw * StdEntropyCoderOptions.STRIPE_HEIGHT / 2 - cblk.w;
            kstep = dscanw * StdEntropyCoderOptions.STRIPE_HEIGHT - cblk.w;
            setmask = (int)(((long)3 << bp) >> 1);
            data = (int[])cblk.Data;
            nstripes = (cblk.h + StdEntropyCoderOptions.STRIPE_HEIGHT - 1) / StdEntropyCoderOptions.STRIPE_HEIGHT;
            causal = (localOptions & StdEntropyCoderOptions.OPT_VERT_STR_CAUSAL) != 0;

            // Pre-calculate offsets in 'state' for diagonal neighbors
            off_ul = -sscanw - 1; // up-left
            off_ur = -sscanw + 1; // up-right
            off_dr = sscanw + 1; // down-right
            off_dl = sscanw - 1; // down-left

            // Decode stripe by stripe
            sk = cblk.offset;
            sj = sscanw + 1;
            for (s = nstripes - 1; s >= 0; s--, sk += kstep, sj += jstep)
            {
                sheight = (s != 0) ? StdEntropyCoderOptions.STRIPE_HEIGHT : cblk.h - (nstripes - 1) * StdEntropyCoderOptions.STRIPE_HEIGHT;
                stopsk = sk + cblk.w;
                // Scan by set of 1 stripe column at a time
                for (; sk < stopsk; sk++, sj++)
                {
                    // Start column
                    j = sj;
                    csj = state[j];
                    {
                        // Check for RLC: if all samples are not significant, not
                        // visited and do not have a non-zero context, and column
                        // is full height, we do RLC.
                        if (csj == 0 && state[j + sscanw] == 0 && sheight == StdEntropyCoderOptions.STRIPE_HEIGHT)
                        {
                            if (mq.decodeSymbol(RLC_CTXT) != 0)
                            {
                                // run-length is significant, decode length
                                rlclen = mq.decodeSymbol(UNIF_CTXT) << 1;
                                rlclen |= mq.decodeSymbol(UNIF_CTXT);
                                // Set 'k' and 'j' accordingly
                                k = sk + rlclen * dscanw;
                                if (rlclen > 1)
                                {
                                    j += sscanw;
                                    csj = state[j];
                                }
                            }
                            else
                            {
                                // RLC is insignificant
                                // Goto next column
                                continue;
                            }
                            // We just decoded the length of a significant RLC
                            // and a sample became significant
                            // Use sign coding
                            if ((rlclen & 0x01) == 0)
                            {
                                // Sample that became significant is first row of
                                // its column half
                                ctxt = sc_lut[(csj >> SC_SHIFT_R1) & SC_MASK];
                                sym = mq.decodeSymbol(ctxt & SC_LUT_MASK) ^ ((ctxt >>> SC_SPRED_SHIFT));
                                // Update the data
                                data[k] = (sym << 31) | setmask;
                                // Update state information (significant bit,
                                // visited bit, neighbor significant bit of
                                // neighbors, non zero context of neighbors, sign
                                // of neighbors)
                                if (rlclen != 0 || !causal)
                                {
                                    // If in causal mode do not change
                                    // contexts of previous stripe.
                                    state[j + off_ul] |= STATE_NZ_CTXT_R2 | STATE_D_DR_R2;
                                    state[j + off_ur] |= STATE_NZ_CTXT_R2 | STATE_D_DL_R2;
                                }
                                // Update sign state information of neighbors (branchless: signMask=0xFFFFFFFF when sym=1, 0 when sym=0)
                                int signMask = -sym;
                                csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2 | (STATE_V_U_SIGN_R2 & signMask);
                                if (rlclen != 0 || !causal)
                                {
                                    // If in causal mode do not change
                                    // contexts of previous stripe.
                                    state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2 | (STATE_V_D_SIGN_R2 & signMask);
                                }
                                state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | (STATE_H_L_SIGN_R1 & signMask) | STATE_D_UL_R2;
                                state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | (STATE_H_R_SIGN_R1 & signMask) | STATE_D_UR_R2;
                                // Changes to csj are saved later
                                if ((rlclen >> 1) != 0)
                                {
                                    // Sample that became significant is in
                                    // bottom half of column => jump to bottom
                                    // half
                                    goto top_half_brk;
                                }
                                // Otherwise sample that became significant is in
                                // top half of column => continue on top half
                            }
                            else
                            {
                                // Sample that became significant is second row of
                                // its column half
                                ctxt = sc_lut[(csj >> SC_SHIFT_R2) & SC_MASK];
                                sym = mq.decodeSymbol(ctxt & SC_LUT_MASK) ^ ((ctxt >>> SC_SPRED_SHIFT));
                                // Update the data
                                data[k] = (sym << 31) | setmask;
                                // Update state information (significant bit,
                                // neighbor significant bit of neighbors, non zero
                                // context of neighbors, sign of neighbors)
                                state[j + off_dl] |= STATE_NZ_CTXT_R1 | STATE_D_UR_R1;
                                state[j + off_dr] |= STATE_NZ_CTXT_R1 | STATE_D_UL_R1;
                                // Update sign state information of neighbors (branchless)
                                int signMask = -sym;
                                csj |= STATE_SIG_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1 | (STATE_V_D_SIGN_R1 & signMask);
                                state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1 | (STATE_V_U_SIGN_R1 & signMask);
                                state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2 | (STATE_H_L_SIGN_R2 & signMask);
                                state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2 | (STATE_H_R_SIGN_R2 & signMask);
                                // Save changes to csj
                                state[j] = csj;
                                if ((rlclen >> 1) != 0)
                                {
                                    // Sample that became significant is in bottom
                                    // half of column => we're done with this
                                    // column
                                    continue;
                                }
                                // Otherwise sample that became significant is in
                                // top half of column => we're done with top
                                // column
                                j += sscanw;
                                csj = state[j];
                                goto top_half_brk;
                            }
                        }
                        // Do half top of column
                        // If any of the two samples is not significant and has
                        // not been visited in the current bit-plane we can not
                        // skip them
                        if ((((csj >> 1) | csj) & VSTD_MASK_R1R2) != VSTD_MASK_R1R2)
                        {
                            k = sk;
                            // Scan first row
                            if ((csj & (STATE_SIG_R1 | STATE_VISITED_R1)) == 0)
                            {
                                // Use zero coding
                                if (mq.decodeSymbol(zc_lut[csj & ZC_MASK]) != 0)
                                {
                                    // Became significant
                                    // Use sign coding
                                    ctxt = sc_lut[((csj >>> SC_SHIFT_R1)) & SC_MASK];
                                    sym = mq.decodeSymbol(ctxt & SC_LUT_MASK) ^ ((ctxt >>> SC_SPRED_SHIFT));
                                    // Update the data
                                    data[k] = (sym << 31) | setmask;
                                    // Update state information (significant bit,
                                    // visited bit, neighbor significant bit of
                                    // neighbors, non zero context of neighbors,
                                    // sign of neighbors)
                                    if (!causal)
                                    {
                                        // If in causal mode do not change
                                        // contexts of previous stripe.
                                        state[j + off_ul] |= STATE_NZ_CTXT_R2 | STATE_D_DR_R2;
                                        state[j + off_ur] |= STATE_NZ_CTXT_R2 | STATE_D_DL_R2;
                                    }
                                    // Update sign state information of neighbors (branchless)
                                    int signMask = -sym;
                                    csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2 | (STATE_V_U_SIGN_R2 & signMask);
                                    if (!causal)
                                    {
                                        // If in causal mode do not change
                                        // contexts of previous stripe.
                                        state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2 | (STATE_V_D_SIGN_R2 & signMask);
                                    }
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | (STATE_H_L_SIGN_R1 & signMask) | STATE_D_UL_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | (STATE_H_R_SIGN_R1 & signMask) | STATE_D_UR_R2;
                                }
                            }
                            if (sheight < 2)
                            {
                                csj &= ~(STATE_VISITED_R1 | STATE_VISITED_R2);
                                state[j] = csj;
                                continue;
                            }
                            // Scan second row
                            if ((csj & (STATE_SIG_R2 | STATE_VISITED_R2)) == 0)
                            {
                                k += dscanw;
                                // Use zero coding
                                if (mq.decodeSymbol(zc_lut[((csj >>> STATE_SEP)) & ZC_MASK]) != 0)
                                {
                                    // Became significant
                                    // Use sign coding
                                    ctxt = sc_lut[((csj >>> SC_SHIFT_R2)) & SC_MASK];
                                    sym = mq.decodeSymbol(ctxt & SC_LUT_MASK) ^ ((ctxt >>> SC_SPRED_SHIFT));
                                    // Update the data
                                    data[k] = (sym << 31) | setmask;
                                    // Update state information (significant bit,
                                    // visited bit, neighbor significant bit of
                                    // neighbors, non zero context of neighbors,
                                    // sign of neighbors)
                                    state[j + off_dl] |= STATE_NZ_CTXT_R1 | STATE_D_UR_R1;
                                    state[j + off_dr] |= STATE_NZ_CTXT_R1 | STATE_D_UL_R1;
                                    // Update sign state information of neighbors (branchless)
                                    int signMask = -sym;
                                    csj |= STATE_SIG_R2 | STATE_VISITED_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1 | (STATE_V_D_SIGN_R1 & signMask);
                                    state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1 | (STATE_V_U_SIGN_R1 & signMask);
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2 | (STATE_H_L_SIGN_R2 & signMask);
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2 | (STATE_H_R_SIGN_R2 & signMask);
                                }
                            }
                        }
                        csj &= ~(STATE_VISITED_R1 | STATE_VISITED_R2);
                        state[j] = csj;
                        // Do half bottom of column
                        if (sheight < 3)
                            continue;
                        j += sscanw;
                        csj = state[j];
                    }

                top_half_brk:;
                    // end of 'top_half' block
                    // If any of the two samples is not significant and has
                    // not been visited in the current bit-plane we can not
                    // skip them
                    if ((((csj >> 1) | csj) & VSTD_MASK_R1R2) != VSTD_MASK_R1R2)
                    {
                        k = sk + (dscanw << 1);
                        // Scan first row
                        if ((csj & (STATE_SIG_R1 | STATE_VISITED_R1)) == 0)
                        {
                            // Use zero coding
                            if (mq.decodeSymbol(zc_lut[csj & ZC_MASK]) != 0)
                            {
                                // Became significant
                                // Use sign coding
                                ctxt = sc_lut[(csj >> SC_SHIFT_R1) & SC_MASK];
                                sym = mq.decodeSymbol(ctxt & SC_LUT_MASK) ^ ((ctxt >>> SC_SPRED_SHIFT));
                                // Update the data
                                data[k] = (sym << 31) | setmask;
                                // Update state information (significant bit,
                                // visited bit, neighbor significant bit of
                                // neighbors, non zero context of neighbors,
                                // sign of neighbors)
                                state[j + off_ul] |= STATE_NZ_CTXT_R2 | STATE_D_DR_R2;
                                state[j + off_ur] |= STATE_NZ_CTXT_R2 | STATE_D_DL_R2;
                                // Update sign state information of neighbors (branchless)
                                int signMask = -sym;
                                csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2 | (STATE_V_U_SIGN_R2 & signMask);
                                state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2 | (STATE_V_D_SIGN_R2 & signMask);
                                state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | (STATE_H_L_SIGN_R1 & signMask) | STATE_D_UL_R2;
                                state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | (STATE_H_R_SIGN_R1 & signMask) | STATE_D_UR_R2;
                            }
                        }
                        if (sheight < 4)
                        {
                            csj &= ~(STATE_VISITED_R1 | STATE_VISITED_R2);
                            state[j] = csj;
                            continue;
                        }
                        // Scan second row
                        if ((csj & (STATE_SIG_R2 | STATE_VISITED_R2)) == 0)
                        {
                            k += dscanw;
                            // Use zero coding
                            if (mq.decodeSymbol(zc_lut[((csj >>> STATE_SEP)) & ZC_MASK]) != 0)
                            {
                                // Became significant
                                // Use sign coding
                                ctxt = sc_lut[((csj >>> SC_SHIFT_R2)) & SC_MASK];
                                sym = mq.decodeSymbol(ctxt & SC_LUT_MASK) ^ ((ctxt >>> SC_SPRED_SHIFT));
                                // Update the data
                                data[k] = (sym << 31) | setmask;
                                // Update state information (significant bit,
                                // visited bit, neighbor significant bit of
                                // neighbors, non zero context of neighbors,
                                // sign of neighbors)
                                state[j + off_dl] |= STATE_NZ_CTXT_R1 | STATE_D_UR_R1;
                                state[j + off_dr] |= STATE_NZ_CTXT_R1 | STATE_D_UL_R1;
                                // Update sign state information of neighbors (branchless)
                                int signMask = -sym;
                                csj |= STATE_SIG_R2 | STATE_VISITED_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1 | (STATE_V_D_SIGN_R1 & signMask);
                                state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1 | (STATE_V_U_SIGN_R1 & signMask);
                                state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2 | (STATE_H_L_SIGN_R2 & signMask);
                                state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2 | (STATE_H_R_SIGN_R2 & signMask);
                            }
                        }
                    }
                    csj &= ~(STATE_VISITED_R1 | STATE_VISITED_R2);
                    state[j] = csj;
                }
            }

            // Decode segment symbol if we need to
            if ((localOptions & StdEntropyCoderOptions.OPT_SEG_SYMBOLS) != 0)
            {
                sym = mq.decodeSymbol(UNIF_CTXT) << 3;
                sym |= mq.decodeSymbol(UNIF_CTXT) << 2;
                sym |= mq.decodeSymbol(UNIF_CTXT) << 1;
                sym |= mq.decodeSymbol(UNIF_CTXT);
                // Set error condition accordingly
                error = sym != SEG_SYMBOL;
            }
            else
            {
                // We can not detect any errors
                error = false;
            }

            // Check the error resilience termination
            if (isterm && (localOptions & StdEntropyCoderOptions.OPT_PRED_TERM) != 0)
            {
                error = mq.checkPredTerm();
            }

            // Reset the MQ context states if we need to
            if ((localOptions & StdEntropyCoderOptions.OPT_RESET_MQ) != 0)
            {
                mq.resetCtxts();
            }

            // Return error condition
            return error;
        }

        /// <summary> Conceals decoding errors detected in the last bit-plane. The
        /// concealement resets the state of the decoded data to what it was before
        /// the decoding of bit-plane 'bp' started. No more data should be decoded
        /// after this method is called for this code-block's data to which it is
        /// applied.
        /// 
        /// </summary>
        /// <param name="cblk">The code-block's data
        /// 
        /// </param>
        /// <param name="bp">The last decoded bit-plane (which contains errors).
        /// 
        /// </param>
        private void conceal(DataBlk cblk, int bp)
        {
            int l; // line index
            int k; // array index
            int kmax; // 'k' limit
            int dk; // Value of data[k]
            int[] data; // the data array
            int setmask; // Bitmask to set approximation to 1/2 of
                         // known interval on significant data
            int resetmask; // Bitmask to erase all the data from
                           // bit-plane 'bp' 

            // Initialize masks
            setmask = 1 << bp;
            resetmask = (-1) << (bp);

            // Get the data array
            data = (int[])cblk.Data;

            // Visit each sample, apply the reset mask to it and add an
            // approximation if significant.
            for (l = cblk.h - 1, k = cblk.offset; l >= 0; l--)
            {
                for (kmax = k + cblk.w; k < kmax; k++)
                {
                    dk = data[k];
                    if ((dk & resetmask & 0x7FFFFFFF) != 0)
                    {
                        // Something was decoded in previous bit-planes => set the
                        // approximation for previous bit-plane
                        data[k] = (dk & resetmask) | setmask;
                    }
                    else
                    {
                        // Was insignificant in previous bit-planes = set to zero
                        data[k] = 0;
                    }
                }
                k += cblk.scanw - cblk.w;
            }
        }
        /// <summary>Static initializer: initializes all the lookup tables. </summary>
        static StdEntropyDecoder()
        {
            {
                int i, j;
                //double val, deltaMSE;
                int[] inter_sc_lut;
                int ds, us, rs, ls;
                int dsgn, usgn, rsgn, lsgn;
                int h, v;

                // Initialize the zero coding lookup tables

                // LH

                // - No neighbors significant
                ZC_LUT_LH[0] = 2;
                // - No horizontal or vertical neighbors significant
                for (i = 1; i < 16; i++)
                {
                    // Two or more diagonal coeffs significant
                    ZC_LUT_LH[i] = 4;
                }
                for (i = 0; i < 4; i++)
                {
                    // Only one diagonal coeff significant
                    ZC_LUT_LH[1 << i] = 3;
                }
                // - No horizontal neighbors significant, diagonal irrelevant
                for (i = 0; i < 16; i++)
                {
                    // Only one vertical coeff significant
                    ZC_LUT_LH[STATE_V_U_R1 | i] = 5;
                    ZC_LUT_LH[STATE_V_D_R1 | i] = 5;
                    // The two vertical coeffs significant
                    ZC_LUT_LH[STATE_V_U_R1 | STATE_V_D_R1 | i] = 6;
                }
                // - One horiz. neighbor significant, diagonal/vertical non-significant
                ZC_LUT_LH[STATE_H_L_R1] = 7;
                ZC_LUT_LH[STATE_H_R_R1] = 7;
                // - One horiz. significant, no vertical significant, one or more
                // diagonal significant
                for (i = 1; i < 16; i++)
                {
                    ZC_LUT_LH[STATE_H_L_R1 | i] = 8;
                    ZC_LUT_LH[STATE_H_R_R1 | i] = 8;
                }
                // - One horiz. significant, one or more vertical significant,
                // diagonal irrelevant
                for (i = 1; i < 4; i++)
                {
                    for (j = 0; j < 16; j++)
                    {
                        ZC_LUT_LH[STATE_H_L_R1 | (i << 4) | j] = 9;
                        ZC_LUT_LH[STATE_H_R_R1 | (i << 4) | j] = 9;
                    }
                }
                // - Two horiz. significant, others irrelevant
                for (i = 0; i < 64; i++)
                {
                    ZC_LUT_LH[STATE_H_L_R1 | STATE_H_R_R1 | i] = 10;
                }

                // HL

                // - No neighbors significant
                ZC_LUT_HL[0] = 2;
                // - No horizontal or vertical neighbors significant
                for (i = 1; i < 16; i++)
                {
                    // Two or more diagonal coeffs significant
                    ZC_LUT_HL[i] = 4;
                }
                for (i = 0; i < 4; i++)
                {
                    // Only one diagonal coeff significant
                    ZC_LUT_HL[1 << i] = 3;
                }
                // - No vertical significant, diagonal irrelevant
                for (i = 0; i < 16; i++)
                {
                    // One horiz. significant
                    ZC_LUT_HL[STATE_H_L_R1 | i] = 5;
                    ZC_LUT_HL[STATE_H_R_R1 | i] = 5;
                    // Two horiz. significant
                    ZC_LUT_HL[STATE_H_L_R1 | STATE_H_R_R1 | i] = 6;
                }
                // - One vert. significant, diagonal/horizontal non-significant
                ZC_LUT_HL[STATE_V_U_R1] = 7;
                ZC_LUT_HL[STATE_V_D_R1] = 7;
                // - One vert. significant, horizontal non-significant, one or more
                // diag. significant
                for (i = 1; i < 16; i++)
                {
                    ZC_LUT_HL[STATE_V_U_R1 | i] = 8;
                    ZC_LUT_HL[STATE_V_D_R1 | i] = 8;
                }
                // - One vertical significant, one or more horizontal significant,
                // diagonal irrelevant
                for (i = 1; i < 4; i++)
                {
                    for (j = 0; j < 16; j++)
                    {
                        ZC_LUT_HL[(i << 6) | STATE_V_U_R1 | j] = 9;
                        ZC_LUT_HL[(i << 6) | STATE_V_D_R1 | j] = 9;
                    }
                }
                // - Two vertical significant, others irrelevant
                for (i = 0; i < 4; i++)
                {
                    for (j = 0; j < 16; j++)
                    {
                        ZC_LUT_HL[(i << 6) | STATE_V_U_R1 | STATE_V_D_R1 | j] = 10;
                    }
                }

                // HH
                var twoBits = new int[] { 3, 5, 6, 9, 10, 12 }; // Figures (between 0 and 15)
                                                                // countaning 2 and only 2 bits on in its binary representation.

                var oneBit = new int[] { 1, 2, 4, 8 }; // Figures (between 0 and 15)
                                                       // countaning 1 and only 1 bit on in its binary representation.

                var twoLeast = new int[] { 3, 5, 6, 7, 9, 10, 11, 12, 13, 14, 15 }; // Figures
                                                                                    // (between 0 and 15) countaining, at least, 2 bits on in its
                                                                                    // binary representation. 

                var threeLeast = new int[] { 7, 11, 13, 14, 15 }; // Figures
                                                                  // (between 0 and 15) countaining, at least, 3 bits on in its
                                                                  // binary representation.

                // - None significant
                ZC_LUT_HH[0] = 2;

                // - One horizontal+vertical significant, none diagonal
                for (i = 0; i < oneBit.Length; i++)
                    ZC_LUT_HH[oneBit[i] << 4] = 3;

                // - Two or more horizontal+vertical significant, diagonal non-signif
                for (i = 0; i < twoLeast.Length; i++)
                    ZC_LUT_HH[twoLeast[i] << 4] = 4;

                // - One diagonal significant, horiz./vert. non-significant
                for (i = 0; i < oneBit.Length; i++)
                    ZC_LUT_HH[oneBit[i]] = 5;

                // - One diagonal significant, one horiz.+vert. significant
                for (i = 0; i < oneBit.Length; i++)
                    for (j = 0; j < oneBit.Length; j++)
                        ZC_LUT_HH[(oneBit[i] << 4) | oneBit[j]] = 6;

                // - One diag signif, two or more horiz+vert signif
                for (i = 0; i < twoLeast.Length; i++)
                    for (j = 0; j < oneBit.Length; j++)
                        ZC_LUT_HH[(twoLeast[i] << 4) | oneBit[j]] = 7;

                // - Two diagonal significant, none horiz+vert significant
                for (i = 0; i < twoBits.Length; i++)
                    ZC_LUT_HH[twoBits[i]] = 8;

                // - Two diagonal significant, one or more horiz+vert significant
                for (j = 0; j < twoBits.Length; j++)
                    for (i = 1; i < 16; i++)
                        ZC_LUT_HH[(i << 4) | twoBits[j]] = 9;

                // - Three or more diagonal significant, horiz+vert irrelevant
                for (i = 0; i < 16; i++)
                    for (j = 0; j < threeLeast.Length; j++)
                        ZC_LUT_HH[(i << 4) | threeLeast[j]] = 10;


                // Initialize the SC lookup tables

                // Use an intermediate sign code lookup table that is similar to the
                // one in the VM text, in that it depends on the 'h' and 'v'
                // quantities. The index into this table is a 6 bit index, the top 3
                // bits are (h+1) and the low 3 bits (v+1).
                inter_sc_lut = new int[36];
                inter_sc_lut[(2 << 3) | 2] = 15;
                inter_sc_lut[(2 << 3) | 1] = 14;
                inter_sc_lut[(2 << 3) | 0] = 13;
                inter_sc_lut[(1 << 3) | 2] = 12;
                inter_sc_lut[(1 << 3) | 1] = 11;
                inter_sc_lut[(1 << 3) | 0] = 12 | INT_SIGN_BIT;
                inter_sc_lut[(0 << 3) | 2] = 13 | INT_SIGN_BIT;
                inter_sc_lut[(0 << 3) | 1] = 14 | INT_SIGN_BIT;
                inter_sc_lut[(0 << 3) | 0] = 15 | INT_SIGN_BIT;

                // Using the intermediate sign code lookup table create the final
                // one. The index into this table is a 9 bit index, the low 4 bits are
                // the significance of the 4 horizontal/vertical neighbors, while the
                // top 4 bits are the signs of those neighbors. The bit in the middle
                // is ignored. This index arrangement matches the state bits in the
                // 'state' array, thus direct addressing of the table can be done from
                // the sate information.
                for (i = 0; i < (1 << SC_LUT_BITS) - 1; i++)
                {
                    ds = i & 0x01; // significance of down neighbor
                    us = (i >> 1) & 0x01; // significance of up neighbor
                    rs = (i >> 2) & 0x01; // significance of right neighbor
                    ls = (i >> 3) & 0x01; // significance of left neighbor
                    dsgn = (i >> 5) & 0x01; // sign of down neighbor
                    usgn = (i >> 6) & 0x01; // sign of up neighbor
                    rsgn = (i >> 7) & 0x01; // sign of right neighbor
                    lsgn = (i >> 8) & 0x01; // sign of left neighbor
                                            // Calculate 'h' and 'v' as in VM text
                    h = ls * (1 - 2 * lsgn) + rs * (1 - 2 * rsgn);
                    h = (h >= -1) ? h : -1;
                    h = (h <= 1) ? h : 1;
                    v = us * (1 - 2 * usgn) + ds * (1 - 2 * dsgn);
                    v = (v >= -1) ? v : -1;
                    v = (v <= 1) ? v : 1;
                    // Get context and sign predictor from 'inter_sc_lut'
                    SC_LUT[i] = inter_sc_lut[(h + 1) << 3 | (v + 1)];
                }
                inter_sc_lut = null;

                // Initialize the MR lookup tables

                // None significant, prev MR off
                MR_LUT[0] = 16;
                // One or more significant, prev MR off
                for (i = 1; i < (1 << (MR_LUT_BITS - 1)); i++)
                {
                    MR_LUT[i] = 17;
                }
                // Previous MR on, significance irrelevant
                for (; i < (1 << MR_LUT_BITS); i++)
                {
                    MR_LUT[i] = 18;
                }
            }
        }
    }
}