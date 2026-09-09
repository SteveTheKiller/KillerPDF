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

//#define DO_TIMING

using System;
using CoreJ2K.j2k.image;
using CoreJ2K.j2k.quantization.quantizer;
using CoreJ2K.j2k.util;
using CoreJ2K.j2k.wavelet;
using CoreJ2K.j2k.wavelet.analysis;

namespace CoreJ2K.j2k.entropy.encoder
{

    /// <summary> This class implements the JPEG 2000 entropy coder, which codes stripes in
    /// code-blocks. This entropy coding engine can function in a single-threaded
    /// mode where one code-block is encoded at a time, or in a multi-threaded mode
    /// where multiple code-blocks are entropy coded in parallel. The interface
    /// presented by this class is the same in both modes.
    /// 
    /// The number of threads used by this entropy coder is specified by the
    /// "jj2000.j2k.entropy.encoder.StdEntropyCoder.nthreads" Java system
    /// property. If set to "0" the single threaded implementation is used. If set
    /// to 'n' ('n' larger than 0) then 'n' extra threads are started by this class
    /// which are used to encode the code-blocks in parallel (i.e. ideally 'n'
    /// code-blocks will be encoded in parallel at a time). On multiprocessor
    /// machines under a "native threads" Java Virtual Machine implementation each
    /// one of these threads can run on a separate processor speeding up the
    /// encoding time. By default the single-threaded implementation is used. The
    /// multi-threaded implementation currently assumes that the vast majority of
    /// consecutive calls to 'GetNextCodeBlock()' will be done on the same
    /// component. If this is not the case, the speed-up that can be expected on
    /// multiprocessor machines might be significantly decreased.
    /// 
    /// The code-blocks are rectangular, with dimensions which must be powers of
    /// 2. Each dimension has to be no smaller than 4 and no larger than 256. The
    /// product of the two dimensions (i.e. area of the code-block) may not exceed
    /// 4096.
    /// 
    /// Context 0 of the MQ-coder is used as the uniform one (uniform,
    /// non-adaptive probability distribution). Context 1 is used for RLC
    /// coding. Contexts 2-10 are used for zero-coding (ZC), contexts 11-15 are
    /// used for sign-coding (SC) and contexts 16-18 are used for
    /// magnitude-refinement (MR).
    /// 
    /// This implementation buffers the symbols and calls the MQ coder only once
    /// per stripe and per coding pass, to reduce the method call overhead.
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
    /// The source module must implement the CBlkQuantDataSrcEnc interface and
    /// code-block's data is received in a CBlkWTData instance. This modules sends
    /// code-block's information in a CBlkRateDistStats instance.
    /// 
    /// </summary>
    /// <seealso cref="CBlkQuantDataSrcEnc" />
    /// <seealso cref="CBlkWTData" />
    /// <seealso cref="CBlkRateDistStats" />
    public sealed class StdEntropyCoder : EntropyCoder
    {
#if DO_TIMING
		/// <summary>The cumulative wall time for the entropy coding engine, for each
		/// component. In the single-threaded implementation it is the total time,
		/// in the multi-threaded implementation it is the time spent managing the
		/// compressor threads only. 
		/// </summary>
		private long[] time;
#endif

        /// <summary>A flag indicating for each component if all the code-blocks of the *
        /// current tile have been returned. Used in multithreaded implementation
        /// only. 
        /// </summary>
        private readonly bool[] finishedTileComponent;

        /// <summary>The MQ coder used, for each thread </summary>
        private readonly MQCoder[] mqT;

        /// <summary>The raw bit output used, for each thread </summary>
        private readonly BitToByteOutput[] boutT;

        /// <summary>The output stream used, for each thread </summary>
        private readonly ByteOutputBuffer[] outT;

        /// <summary>The code-block size specifications </summary>
        private readonly CBlkSizeSpec cblks;

        /// <summary>The precinct partition specifications </summary>
        private readonly PrecinctSizeSpec pss;

        /// <summary>By-pass mode specifications </summary>
        public StringSpec bms;

        /// <summary>MQ reset specifications </summary>
        public StringSpec mqrs;

        /// <summary>Regular termination specifications </summary>
        public StringSpec rts;

        /// <summary>Causal stripes specifications </summary>
        public StringSpec css;

        /// <summary>Error resilience segment symbol use specifications </summary>
        public StringSpec sss;

        /// <summary>The length calculation specifications </summary>
        public StringSpec lcs;

        /// <summary>The termination type specifications </summary>
        public StringSpec tts;

        /// <summary>The options that are turned on, as flag bits. One element for each
        /// tile-component. The options are 'OPT_TERM_PASS', 'OPT_RESET_MQ',
        /// 'OPT_VERT_STR_CAUSAL', 'OPT_BYPASS' and 'OPT_SEG_SYMBOLS' as defined in
        /// the StdEntropyCoderOptions interface
        /// 
        /// </summary>
        /// <seealso cref="StdEntropyCoderOptions" />
        private int[][] opts = null;

        /// <summary>The length calculation type for each tile-component </summary>
        private int[][] lenCalc = null;

        /// <summary>The termination type for each tile-component </summary>
        private int[][] tType = null;

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
        /// index, which correspond the the value in the 'state' array shifted by
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

        /// <summary>The 4 bits of the error resilience segmentation symbol (1010) </summary>
        private static readonly int[] SEG_SYMBOLS = { 1, 0, 1, 0 };

        /// <summary>The 4 contexts for the error resilience segmentation symbol (always
        /// the UNIFORM context, UNIF_CTXT) 
        /// </summary>
        private static readonly int[] SEG_SYMB_CTXTS = { UNIF_CTXT, UNIF_CTXT, UNIF_CTXT, UNIF_CTXT };

        /// <summary> The state array for each thread. Each element of the state array stores
        /// the state of two coefficients. The lower 16 bits store the state of a
        /// coefficient in row 'i' and column 'j', while the upper 16 bits store
        /// the state of a coefficient in row 'i+1' and column 'j'. The 'i' row is
        /// either the first or the third row of a stripe. This packing of the
        /// states into 32 bit words allows a faster scan of all coefficients on
        /// each coding pass and diminished the amount of data transferred. The
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
        private readonly int[][] stateT;

        /* The separation between the upper and lower bits in the state array: 16
		* */
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
        private static readonly int STATE_SIG_R2 = STATE_SIG_R1 << STATE_SEP;

        /// <summary>The flag bit for the "visited" bit in the state array, for row 2. </summary>
        private static readonly int STATE_VISITED_R2 = STATE_VISITED_R1 << STATE_SEP;

        /// <summary>The flag bit for the "not zero context" bit in the state array, for
        /// row 2. This bit is always the OR of bits STATE_H_L_R2, STATE_H_R_R2,
        /// STATE_V_U_R2, STATE_V_D_R2, STATE_D_UL_R2, STATE_D_UR_R2, STATE_D_DL_R2
        /// and STATE_D_DR_R2. 
        /// </summary>
        private static readonly int STATE_NZ_CTXT_R2 = STATE_NZ_CTXT_R1 << STATE_SEP;

        /// <summary>The flag bit for the horizontal-left sign in the state array, for row
        /// 2. This bit can only be set if the STATE_H_L_R2 is also set. 
        /// </summary>
        private static readonly int STATE_H_L_SIGN_R2 = STATE_H_L_SIGN_R1 << STATE_SEP;

        /// <summary>The flag bit for the horizontal-right sign in the state array, for
        /// row 2. This bit can only be set if the STATE_H_R_R2 is also set. 
        /// </summary>
        private static readonly int STATE_H_R_SIGN_R2 = STATE_H_R_SIGN_R1 << STATE_SEP;

        /// <summary>The flag bit for the vertical-up sign in the state array, for row
        /// 2. This bit can only be set if the STATE_V_U_R2 is also set. 
        /// </summary>
        private static readonly int STATE_V_U_SIGN_R2 = STATE_V_U_SIGN_R1 << STATE_SEP;

        /// <summary>The flag bit for the vertical-down sign in the state array, for row
        /// 2. This bit can only be set if the STATE_V_D_R2 is also set. 
        /// </summary>
        private static readonly int STATE_V_D_SIGN_R2 = STATE_V_D_SIGN_R1 << STATE_SEP;

        /// <summary>The flag bit for the previous MR primitive applied in the state array,
        /// for row 2. 
        /// </summary>
        private static readonly int STATE_PREV_MR_R2 = STATE_PREV_MR_R1 << STATE_SEP;

        /// <summary>The flag bit for the horizontal-left significance in the state array,
        /// for row 2. 
        /// </summary>
        private static readonly int STATE_H_L_R2 = STATE_H_L_R1 << STATE_SEP;

        /// <summary>The flag bit for the horizontal-right significance in the state array,
        /// for row 2. 
        /// </summary>
        private static readonly int STATE_H_R_R2 = STATE_H_R_R1 << STATE_SEP;

        /// <summary>The flag bit for the vertical-up significance in the state array, for
        /// row 2.  
        /// </summary>
        private static readonly int STATE_V_U_R2 = STATE_V_U_R1 << STATE_SEP;

        /// <summary>The flag bit for the vertical-down significance in the state array,
        /// for row 2.  
        /// </summary>
        private static readonly int STATE_V_D_R2 = STATE_V_D_R1 << STATE_SEP;

        /// <summary>The flag bit for the diagonal up-left significance in the state array,
        /// for row 2. 
        /// </summary>
        private static readonly int STATE_D_UL_R2 = STATE_D_UL_R1 << STATE_SEP;

        /// <summary>The flag bit for the diagonal up-right significance in the state
        /// array, for row 2.
        /// </summary>
        private static readonly int STATE_D_UR_R2 = STATE_D_UR_R1 << STATE_SEP;

        /// <summary>The flag bit for the diagonal down-left significance in the state
        /// array, for row 2. 
        /// </summary>
        private static readonly int STATE_D_DL_R2 = STATE_D_DL_R1 << STATE_SEP;

        /// <summary>The flag bit for the diagonal down-right significance in the state
        /// array , for row 2.
        /// </summary>
        private static readonly int STATE_D_DR_R2 = STATE_D_DR_R1 << STATE_SEP;

        /// <summary>The mask to isolate the significance bits for row 1 and 2 of the state 
        /// array. 
        /// </summary>
        private static readonly int SIG_MASK_R1R2 = STATE_SIG_R1 | STATE_SIG_R2;

        /// <summary>The mask to isolate the visited bits for row 1 and 2 of the state 
        /// array. 
        /// </summary>
        private static readonly int VSTD_MASK_R1R2 = STATE_VISITED_R1 | STATE_VISITED_R2;

        /// <summary>The mask to isolate the bits necessary to identify RLC coding state
        /// (significant, visited and non-zero context, for row 1 and 2). 
        /// </summary>
        private static readonly int RLC_MASK_R1R2 = STATE_SIG_R1 | STATE_SIG_R2 | STATE_VISITED_R1 | STATE_VISITED_R2 | STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2;

        /// <summary>The mask to obtain the ZC_LUT index from the state information </summary>
        // This is needed because of the STATE_V_D_SIGN_R1, STATE_V_U_SIGN_R1,
        // STATE_H_R_SIGN_R1, and STATE_H_L_SIGN_R1 bits.
        private const int ZC_MASK = (1 << 8) - 1;

        /// <summary>The shift to obtain the SC index to 'SC_LUT' from the state
        /// information, for row 1. 
        /// </summary>
        private const int SC_SHIFT_R1 = 4;

        /// <summary>The shift to obtain the SC index to 'SC_LUT' from the state
        /// information, for row 2. 
        /// </summary>
        private static readonly int SC_SHIFT_R2 = SC_SHIFT_R1 + STATE_SEP;

        /// <summary>The bit mask to isolate the state bits relative to the sign coding
        /// lookup table ('SC_LUT'). 
        /// </summary>
        private static readonly int SC_MASK = (1 << SC_LUT_BITS) - 1;

        /// <summary>The mask to obtain the MR index to 'MR_LUT' from the 'state'
        /// information. It is to be applied after the 'MR_SHIFT'. 
        /// </summary>
        private static readonly int MR_MASK = (1 << MR_LUT_BITS) - 1;

        /// <summary>The number of bits used to index in the 'fm' lookup table, 7. The 'fs' 
        /// table is indexed with one less bit. 
        /// </summary>
        private const int MSE_LKP_BITS = 7;

        /// <summary>The number of fractional bits used to store data in the 'fm' and 'fs'
        /// lookup tables. 
        /// </summary>
        private const int MSE_LKP_FRAC_BITS = 13;

        /// <summary>Distortion estimation lookup table for bits coded using the sign-code
        /// (SC) primative, for lossy coding (i.e. normal). 
        /// </summary>
        private static readonly int[] FS_LOSSY = new int[1 << (MSE_LKP_BITS - 1)];

        /// <summary>Distortion estimation lookup table for bits coded using the
        /// magnitude-refinement (MR) primative, for lossy coding (i.e. normal) 
        /// </summary>
        private static readonly int[] FM_LOSSY = new int[1 << MSE_LKP_BITS];

        /// <summary>Distortion estimation lookup table for bits coded using the sign-code
        /// (SC) primative, for lossless coding and last bit-plane. This table is
        /// different from 'fs_lossy' since when doing lossless coding the residual 
        /// distortion after the last bit-plane is coded is strictly 0. 
        /// </summary>
        private static readonly int[] FS_LOSSLESS = new int[1 << (MSE_LKP_BITS - 1)];

        /// <summary>Distortion estimation lookup table for bits coded using the
        /// magnitude-refinement (MR) primative, for lossless coding and last
        /// bit-plane. This table is different from 'fs_lossless' since when doing
        /// lossless coding the residual distortion after the last bit-plane is
        /// coded is strictly 0.
        /// </summary>
        private static readonly int[] FM_LOSSLESS = new int[1 << MSE_LKP_BITS];

        /// <summary>The buffer for distortion values (avoids reallocation for each
        /// code-block), for each thread. 
        /// </summary>
        private readonly double[][] distbufT;

        /// <summary>The buffer for rate values (avoids reallocation for each
        /// code-block), for each thread. 
        /// </summary>
        private readonly int[][] ratebufT;

        /// <summary>The buffer for indicating terminated passes (avoids reallocation for
        /// each code-block), for each thread. 
        /// </summary>
        private readonly bool[][] istermbufT;

        /// <summary>The source code-block to entropy code (avoids reallocation for each
        /// code-block), for each thread. 
        /// </summary>
        private readonly CBlkWTData[] srcblkT;

        /// <summary>Buffer for symbols to send to the MQ-coder, for each thread. Used to
        /// reduce the number of calls to the MQ coder. 
        /// </summary>
        // NOTE: The symbol buffer has not prooved to be of any great improvement
        // in encoding time, but it does not hurt. It's performance should be
        // better studied under different JVMs.
        private readonly int[][] symbufT;

        /// <summary>Buffer for the contexts to use when sending buffered symbols to the
        /// MQ-coder, for each thread. Used to reduce the number of calls to the MQ
        /// coder. 
        /// </summary>
        private readonly int[][] ctxtbufT;

        /// <summary>boolean used to signal if the precinct partition is used for
        /// each component and each tile.  
        /// </summary>
        private readonly bool[][] precinctPartition;

        /// <summary> Instantiates a new entropy coder engine, with the specified source of
        /// data, nominal block width and height.
        /// 
        /// If the 'OPT_PRED_TERM' option is given then the MQ termination must
        /// be 'TERM_PRED_ER' or an exception is thrown.
        /// 
        /// </summary>
        /// <param name="src">The source of data
        /// 
        /// </param>
        /// <param name="cbks">Code-block size specifications
        /// 
        /// </param>
        /// <param name="pss">Precinct partition specifications
        /// 
        /// </param>
        /// <param name="bms">By-pass mode specifications
        /// 
        /// </param>
        /// <param name="mqrs">MQ-reset specifications
        /// 
        /// </param>
        /// <param name="rts">Regular termination specifications
        /// 
        /// </param>
        /// <param name="css">Causal stripes specifications
        /// 
        /// </param>
        /// <param name="sss">Error resolution segment symbol use specifications
        /// 
        /// </param>
        /// <param name="lcs">Length computation specifications
        /// 
        /// </param>
        /// <param name="tts">Termination type specifications
        /// 
        /// </param>
        /// <seealso cref="MQCoder" />
        public StdEntropyCoder(CBlkQuantDataSrcEnc src, CBlkSizeSpec cblks, PrecinctSizeSpec pss, StringSpec bms, StringSpec mqrs, StringSpec rts, StringSpec css, StringSpec sss, StringSpec lcs, StringSpec tts) : base(src)
        {
            this.cblks = cblks;
            this.pss = pss;
            this.bms = bms;
            this.mqrs = mqrs;
            this.rts = rts;
            this.css = css;
            this.sss = sss;
            this.lcs = lcs;
            this.tts = tts;
            int maxCBlkWidth, maxCBlkHeight;
            int i; // Counter
            int tsl; // Size for thread structures

            // Get the biggest width/height for the code-blocks
            maxCBlkWidth = cblks.MaxCBlkWidth;
            maxCBlkHeight = cblks.MaxCBlkHeight;

            // If we do timing create necessary structures
#if DO_TIMING
			time = new long[src.NumComps];
			// If we are timing make sure that 'finalize' gets called.
			// CONVERSION PROBLEM?
#endif

            tsl = 1;
            finishedTileComponent = null;

            // Allocate data structures
            outT = new ByteOutputBuffer[tsl];
            mqT = new MQCoder[tsl];
            boutT = new BitToByteOutput[tsl];
            stateT = new int[tsl][];
            for (var i2 = 0; i2 < tsl; i2++)
            {
                stateT[i2] = new int[(maxCBlkWidth + 2) * ((maxCBlkHeight + 1) / 2 + 2)];
            }
            symbufT = new int[tsl][];
            for (var i3 = 0; i3 < tsl; i3++)
            {
                symbufT[i3] = new int[maxCBlkWidth * (StdEntropyCoderOptions.STRIPE_HEIGHT * 2 + 2)];
            }
            ctxtbufT = new int[tsl][];
            for (var i4 = 0; i4 < tsl; i4++)
            {
                ctxtbufT[i4] = new int[maxCBlkWidth * (StdEntropyCoderOptions.STRIPE_HEIGHT * 2 + 2)];
            }
            distbufT = new double[tsl][];
            for (var i5 = 0; i5 < tsl; i5++)
            {
                distbufT[i5] = new double[32 * StdEntropyCoderOptions.NUM_PASSES];
            }
            ratebufT = new int[tsl][];
            for (var i6 = 0; i6 < tsl; i6++)
            {
                ratebufT[i6] = new int[32 * StdEntropyCoderOptions.NUM_PASSES];
            }
            istermbufT = new bool[tsl][];
            for (var i7 = 0; i7 < tsl; i7++)
            {
                istermbufT[i7] = new bool[32 * StdEntropyCoderOptions.NUM_PASSES];
            }
            srcblkT = new CBlkWTData[tsl];
            for (i = 0; i < tsl; i++)
            {
                outT[i] = new ByteOutputBuffer();
                mqT[i] = new MQCoder(outT[i], NUM_CTXTS, MQ_INIT);
            }
            precinctPartition = new bool[src.NumComps][];
            for (var i8 = 0; i8 < src.NumComps; i8++)
            {
                precinctPartition[i8] = new bool[src.GetNumTiles()];
            }

            // Create the subband description for each component and each tile
            //Subband sb = null;
            Coord numTiles = null;
            var nc = NumComps;
            numTiles = src.GetNumTiles(numTiles);
            initTileComp(GetNumTiles(), nc);

            for (var c = 0; c < nc; c++)
            {
                for (var tY = 0; tY < numTiles.y; tY++)
                {
                    for (var tX = 0; tX < numTiles.x; tX++)
                    {
                        precinctPartition[c][tIdx] = false;
                    }
                }
            }
        }

#if DO_TIMING
		/// <summary> Prints the timing information, if collected, and calls 'finalize' on
		/// the super class.
		/// 
		/// </summary>
        ~StdEntropyCoder()
        {

            int c;
            System.Text.StringBuilder sb;

            // Single threaded implementation
            sb = new System.Text.StringBuilder("StdEntropyCoder compression wall clock time:");
            for (c = 0; c < time.Length; c++)
            {
                sb.Append("\n  component ");
                sb.Append(c);
                sb.Append(": ");
                sb.Append(time[c]);
                sb.Append(" ms");
            }
            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, sb.ToString());
        }
#endif

        /// <summary> Returns the code-block width for the specified tile and component.
        /// 
        /// </summary>
        /// <param name="t">The tile index
        /// 
        /// </param>
        /// <param name="c">the component index
        /// 
        /// </param>
        /// <returns> The code-block width for the specified tile and component
        /// 
        /// </returns>
        public override int GetCBlkWidth(int t, int c)
        {
            return cblks.GetCBlkWidth(ModuleSpec.SPEC_TILE_COMP, t, c);
        }

        /// <summary> Returns the code-block height for the specified tile and component.
        /// 
        /// </summary>
        /// <param name="t">The tile index
        /// 
        /// </param>
        /// <param name="c">The component index
        /// 
        /// </param>
        /// <returns> The code-block height for the specified tile and component.
        /// 
        /// </returns>
        public override int GetCBlkHeight(int t, int c)
        {
            return cblks.GetCBlkHeight(ModuleSpec.SPEC_TILE_COMP, t, c);
        }

        /// <summary> Returns the next coded code-block in the current tile for the specified
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
        /// data of this object, if any, and it can be modified "in place" without
        /// any problems after being returned.
        /// 
        /// </summary>
        /// <param name="c">The component for which to return the next code-block.
        /// 
        /// </param>
        /// <param name="ccb">If non-null this object might be used in returning the coded
        /// code-block in this or any subsequent call to this method. If null a new
        /// one is created and returned. If the 'data' array of 'cbb' is not null
        /// it may be reused to return the compressed data.
        /// 
        /// </param>
        /// <returns> The next coded code-block in the current tile for component
        /// 'n', or null if all code-blocks for the current tile have been
        /// returned.
        /// 
        /// </returns>
        /// <seealso cref="CBlkRateDistStats" />
        public override CBlkRateDistStats GetNextCodeBlock(int c, CBlkRateDistStats? ccb)
        {
#if DO_TIMING
			var stime = 0L; // Start time for timed sections
#endif
            // Use single threaded implementation
            // Get code-block data from source
            srcblkT[0] = src.GetNextInternCodeBlock(c, srcblkT[0]);

#if DO_TIMING
			stime = (System.DateTime.Now.Ticks - 621355968000000000) / 10000;
#endif

            if (srcblkT[0] == null)
            {
                // We got all code-blocks
                return null;
            }
            // Initialize thread local variables
            if ((opts[tIdx][c] & StdEntropyCoderOptions.OPT_BYPASS) != 0 && boutT[0] == null)
            {
                boutT[0] = new BitToByteOutput(outT[0]);
            }
            // Initialize output code-block
            if (ccb == null)
            {
                ccb = new CBlkRateDistStats();
            }
            // Compress code-block
            compressCodeBlock(c, ccb, srcblkT[0], mqT[0], boutT[0], outT[0], stateT[0], distbufT[0], ratebufT[0], istermbufT[0], symbufT[0], ctxtbufT[0], opts[tIdx][c], IsReversible(tIdx, c), lenCalc[tIdx][c], tType[tIdx][c]);

#if DO_TIMING
			time[c] += (System.DateTime.Now.Ticks - 621355968000000000) / 10000 - stime;
#endif

            // Return result
            return ccb;
        }

        /// <summary> Changes the current tile, given the new indexes. An
        /// IllegalArgumentException is thrown if the indexes do not correspond to
        /// a valid tile.
        /// 
        /// This default implementation just changes the tile in the source.
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
            base.SetTile(x, y);
            // Reset the tile specific variables
            if (finishedTileComponent != null)
            {
                for (var c = src.NumComps - 1; c >= 0; c--)
                {
                    finishedTileComponent[c] = false;
                }
            }
        }

        /// <summary> Advances to the next tile, in standard scan-line order (by rows then
        /// columns). An NoNextElementException is thrown if the current tile is
        /// the last one (i.e. there is no next tile).
        /// 
        /// This default implementation just advances to the next tile in the
        /// source.
        /// 
        /// </summary>
        public override void NextTile()
        {
            // Reset the tilespecific variables
            if (finishedTileComponent != null)
            {
                for (var c = src.NumComps - 1; c >= 0; c--)
                {
                    finishedTileComponent[c] = false;
                }
            }
            base.NextTile();
        }


        /// <summary> Compresses the code-block in 'srcblk' and puts the results in 'ccb',
        /// using the specified options and temporary storage.
        /// 
        /// </summary>
        /// <param name="c">The component for which to return the next code-block.
        /// 
        /// </param>
        /// <param name="ccb">The object where the compressed data will be stored. If the
        /// 'data' array of 'cbb' is not null it may be reused to return the
        /// compressed data.
        /// 
        /// </param>
        /// <param name="srcblk">The code-block data to code
        /// 
        /// </param>
        /// <param name="mq">The MQ-coder to use
        /// 
        /// </param>
        /// <param name="bout">The bit level output to use. Used only if 'OPT_BYPASS' is
        /// turned on in the 'options' argument.
        /// 
        /// </param>
        /// <param name="out">The byte buffer trough which the compressed data is stored.
        /// 
        /// </param>
        /// <param name="state">The state information for the code-block
        /// 
        /// </param>
        /// <param name="distbuf">The buffer where to store the distortion  at 
        /// the end of each coding pass.
        /// 
        /// </param>
        /// <param name="ratebuf">The buffer where to store the rate (i.e. coded lenth) at 
        /// the end of each coding pass.
        /// 
        /// </param>
        /// <param name="istermbuf">The buffer where to store the terminated flag for each 
        /// coding pass.
        /// 
        /// </param>
        /// <param name="symbuf">The buffer to hold symbols to send to the MQ coder
        /// 
        /// </param>
        /// <param name="ctxtbuf">A buffer to hold the contexts to use in sending the
        /// buffered symbols to the MQ coder.
        /// 
        /// </param>
        /// <param name="options">The options to use when coding this code-block
        /// 
        /// </param>
        /// <param name="rev">The reversible flag. Should be true if the source of this
        /// code-block's data is reversible.
        /// 
        /// </param>
        /// <param name="lcType">The type of length calculation to use with the MQ coder.
        /// 
        /// </param>
        /// <param name="tType">The type of termination to use with the MQ coder.
        /// 
        /// </param>
        /// <seealso cref="GetNextCodeBlock" />
        private static void compressCodeBlock(int c, CBlkRateDistStats ccb, CBlkWTData srcblk, MQCoder mq, BitToByteOutput bout, ByteOutputBuffer outputBuffer, int[] state, double[] distbuf, int[] ratebuf, bool[] istermbuf, int[] symbuf, int[] ctxtbuf, int options, bool rev, int lcType, int tType)
        {
            // NOTE: This method should not access any non-final instance or
            // static variables, either directly or indirectly through other
            // methods in order to be sure that the method is thread safe.

            int[] zc_lut; // The ZC lookup table to use
            int skipbp; // The number of non-significant bit-planes to skip
            int curbp; // The current magnitude bit-plane (starts at 30)
            int[] fm; // The distortion estimation lookup table for MR
            int[] fs; // The distortion estimation lookup table for SC
            int lmb; // The least significant magnitude bit
            int npass; // The number of coding passes, for R-D statistics
            double msew; // The distortion (MSE weight) for the current bit-plane
            double totdist; // The total cumulative distortion decrease
            int ltpidx; // The index of the last pass which is terminated

            // Check error-resilient termination
            if ((options & StdEntropyCoderOptions.OPT_PRED_TERM) != 0 && tType != MQCoder.TERM_PRED_ER)
            {
                throw new System.ArgumentException("Embedded error-resilient info " + "in MQ termination option " + "specified but incorrect MQ " + "termination " + "policy specified");
            }
            // Set MQ flags
            mq.LenCalcType = lcType;
            mq.TermType = tType;

            lmb = 30 - srcblk.magbits + 1;
            // If there are more bit-planes to code than the implementation
            // bit-depth set lmb to 0
            lmb = (lmb < 0) ? 0 : lmb;

            // Reset state
            ArrayUtil.intArraySet(state, 0);

            // Find the most significant bit-plane
            skipbp = calcSkipMSBP(srcblk, lmb);

            // Initialize output code-block
            ccb.m = srcblk.m;
            ccb.n = srcblk.n;
            ccb.sb = srcblk.sb;
            ccb.nROIcoeff = srcblk.nROIcoeff;
            ccb.skipMSBP = skipbp;
            if (ccb.nROIcoeff != 0)
            {
                ccb.nROIcp = 3 * (srcblk.nROIbp - skipbp - 1) + 1;
            }
            else
            {
                ccb.nROIcp = 0;
            }

            // Choose correct ZC lookup table for global orientation
            switch (srcblk.sb.orientation)
            {

                case Subband.WT_ORIENT_HL:
                    zc_lut = ZC_LUT_HL;
                    break;

                case Subband.WT_ORIENT_LL:
                case Subband.WT_ORIENT_LH:
                    zc_lut = ZC_LUT_LH;
                    break;

                case Subband.WT_ORIENT_HH:
                    zc_lut = ZC_LUT_HH;
                    break;

                default:
                    throw new System.InvalidOperationException("JJ2000 internal error");

            }

            // Loop on significant magnitude bit-planes doing the 3 passes
            curbp = 30 - skipbp;
            fs = FS_LOSSY;
            fm = FM_LOSSY;
            msew = System.Math.Pow(2, ((curbp - lmb) << 1) - MSE_LKP_FRAC_BITS) * srcblk.sb.stepWMSE * srcblk.wmseScaling;
            totdist = 0f;
            npass = 0;
            ltpidx = -1;

            // First significant bit-plane has only the pass pass
            if (curbp >= lmb)
            {
                // Do we need the "lossless" 'fs' table ?
                if (rev && curbp == lmb)
                {
                    fs = FM_LOSSLESS;
                }
                // We terminate if regular termination, last bit-plane, or next
                // bit-plane is "raw".
                istermbuf[npass] = (options & StdEntropyCoderOptions.OPT_TERM_PASS) != 0 || curbp == lmb || ((options & StdEntropyCoderOptions.OPT_BYPASS) != 0 && (31 - StdEntropyCoderOptions.NUM_NON_BYPASS_MS_BP - skipbp) >= curbp);
                totdist += cleanuppass(srcblk, mq, istermbuf[npass], curbp, state, fs, zc_lut, symbuf, ctxtbuf, ratebuf, npass, ltpidx, options) * msew;
                distbuf[npass] = totdist;
                if (istermbuf[npass])
                    ltpidx = npass;
                npass++;
                msew *= 0.25;
                curbp--;
            }
            // Other bit-planes have all passes
            while (curbp >= lmb)
            {
                // Do we need the "lossless" 'fs' and 'fm' tables ?
                if (rev && curbp == lmb)
                {
                    fs = FS_LOSSLESS;
                    fm = FM_LOSSLESS;
                }

                // Do the significance propagation pass
                // We terminate if regular termination only
                istermbuf[npass] = (options & StdEntropyCoderOptions.OPT_TERM_PASS) != 0;
                if ((options & StdEntropyCoderOptions.OPT_BYPASS) == 0 || (31 - StdEntropyCoderOptions.NUM_NON_BYPASS_MS_BP - skipbp <= curbp))
                {
                    // No bypass coding
                    totdist += sigProgPass(srcblk, mq, istermbuf[npass], curbp, state, fs, zc_lut, symbuf, ctxtbuf, ratebuf, npass, ltpidx, options) * msew;
                }
                else
                {
                    // Bypass ("raw") coding
                    bout.PredTerm = (options & StdEntropyCoderOptions.OPT_PRED_TERM) != 0;
                    totdist += rawSigProgPass(srcblk, bout, istermbuf[npass], curbp, state, fs, ratebuf, npass, ltpidx, options) * msew;
                }
                distbuf[npass] = totdist;
                if (istermbuf[npass])
                    ltpidx = npass;
                npass++;

                // Do the magnitude refinement pass
                // We terminate if regular termination or bypass ("raw") coding
                istermbuf[npass] = (options & StdEntropyCoderOptions.OPT_TERM_PASS) != 0 || ((options & StdEntropyCoderOptions.OPT_BYPASS) != 0 && (31 - StdEntropyCoderOptions.NUM_NON_BYPASS_MS_BP - skipbp > curbp));
                if ((options & StdEntropyCoderOptions.OPT_BYPASS) == 0 || (31 - StdEntropyCoderOptions.NUM_NON_BYPASS_MS_BP - skipbp <= curbp))
                {
                    // No bypass coding
                    totdist += magRefPass(srcblk, mq, istermbuf[npass], curbp, state, fm, symbuf, ctxtbuf, ratebuf, npass, ltpidx, options) * msew;
                }
                else
                {
                    // Bypass ("raw") coding
                    bout.PredTerm = (options & StdEntropyCoderOptions.OPT_PRED_TERM) != 0;
                    totdist += rawMagRefPass(srcblk, bout, istermbuf[npass], curbp, state, fm, ratebuf, npass, ltpidx, options) * msew;
                }
                distbuf[npass] = totdist;
                if (istermbuf[npass])
                    ltpidx = npass;
                npass++;

                // Do the clenup pass
                // We terminate if regular termination, last bit-plane, or next
                // bit-plane is "raw".
                istermbuf[npass] = (options & StdEntropyCoderOptions.OPT_TERM_PASS) != 0 || curbp == lmb || ((options & StdEntropyCoderOptions.OPT_BYPASS) != 0 && (31 - StdEntropyCoderOptions.NUM_NON_BYPASS_MS_BP - skipbp) >= curbp);
                totdist += cleanuppass(srcblk, mq, istermbuf[npass], curbp, state, fs, zc_lut, symbuf, ctxtbuf, ratebuf, npass, ltpidx, options) * msew;
                distbuf[npass] = totdist;

                if (istermbuf[npass])
                    ltpidx = npass;
                npass++;

                // Goto next bit-plane
                msew *= 0.25;
                curbp--;
            }

            // Copy compressed data and rate-distortion statistics to output
            ccb.data = new byte[outputBuffer.size()];
            outputBuffer.toByteArray(0, outputBuffer.size(), ccb.data, 0);
            checkEndOfPassFF(ccb.data, ratebuf, istermbuf, npass);
            ccb.selectConvexHull(ratebuf, distbuf, (options & (StdEntropyCoderOptions.OPT_BYPASS | StdEntropyCoderOptions.OPT_TERM_PASS)) != 0 ? istermbuf : null, npass, rev);

            // Reset MQ coder and bit output for next code-block
            mq.reset();
            if (bout != null)
                bout.reset();
        }

        /// <summary> Calculates the number of magnitude bit-planes that are to be skipped,
        /// because they are non-significant. The algorithm looks for the largest
        /// magnitude and calculates the most significant bit-plane of it.
        /// 
        /// </summary>
        /// <param name="cblk">The code-block of data to scan
        /// 
        /// </param>
        /// <param name="lmb">The least significant magnitude bit in the data
        /// 
        /// </param>
        /// <returns> The number of magnitude bit-planes to skip (i.e. all zero most
        /// significant bit-planes).
        /// 
        /// </returns>
        private static int calcSkipMSBP(CBlkWTData cblk, int lmb)
        {
            int k, kmax, mask;
            int[] data;
            int maxmag;
            int mag;
            int w, h;
            int msbp;
            int l;

            data = (int[])cblk.Data;
            w = cblk.w;
            h = cblk.h;

            // First look for the maximum magnitude in the code-block
            maxmag = 0;
            // Consider only magnitude bits that are in non-fractional bit-planes.
            mask = 0x7FFFFFFF & (~((1 << lmb) - 1));
            for (l = h - 1, k = cblk.offset; l >= 0; l--)
            {
                for (kmax = k + w; k < kmax; k++)
                {
                    mag = data[k] & mask;
                    if (mag > maxmag)
                        maxmag = mag;
                }
                k += cblk.scanw - w;
            }
            // Now calculate the number of all zero most significant
            // bit-planes for the maximum magnitude.
            msbp = 30;
            do
            {
                if (((1 << msbp) & maxmag) != 0)
                    break;
                msbp--;
            }
            while (msbp >= lmb);

            // Return the number of non-significant bit-planes to skip
            return 30 - msbp;
        }

        /// <summary> Performs the significance propagation pass on the specified data and
        /// bit-plane. It codes all insignificant samples which have, at least, one
        /// of its immediate eight neighbors already significant, using the ZC and
        /// SC primitives as needed. It toggles the "visited" state bit to 1 for
        /// all those samples.
        /// 
        /// </summary>
        /// <param name="srcblk">The code-block data to code
        /// 
        /// </param>
        /// <param name="mq">The MQ-coder to use
        /// 
        /// </param>
        /// <param name="doterm">If true it performs an MQ-coder termination after the end
        /// of the pass
        /// 
        /// </param>
        /// <param name="bp">The bit-plane to code
        /// 
        /// </param>
        /// <param name="state">The state information for the code-block
        /// 
        /// </param>
        /// <param name="fs">The distortion estimation lookup table for SC
        /// 
        /// </param>
        /// <param name="zc_lut">The ZC lookup table to use in ZC.
        /// 
        /// </param>
        /// <param name="symbuf">The buffer to hold symbols to send to the MQ coder
        /// 
        /// </param>
        /// <param name="ctxtbuf">A buffer to hold the contexts to use in sending the
        /// buffered symbols to the MQ coder.
        /// 
        /// </param>
        /// <param name="ratebuf">The buffer where to store the rate (i.e. coded lenth) at 
        /// the end of this coding pass.
        /// 
        /// </param>
        /// <param name="pidx">The coding pass index. Is the index in the 'ratebuf' array
        /// where to store the coded length after this coding pass.
        /// 
        /// </param>
        /// <param name="ltpidx">The index of the last pass that was terminated, or
        /// negative if none.
        /// 
        /// </param>
        /// <param name="options">The bitmask of entropy coding options to apply to the
        /// code-block
        /// 
        /// </param>
        /// <returns> The decrease in distortion for this pass, in the fixed-point
        /// normalized representation of the 'FS_LOSSY' and 'FS_LOSSLESS' tables.
        /// 
        /// </returns>
        private static int sigProgPass(CBlkWTData srcblk, MQCoder mq, bool doterm, int bp, int[] state, int[] fs, int[] zc_lut, int[] symbuf, int[] ctxtbuf, int[] ratebuf, int pidx, int ltpidx, int options)
        {
            int j, sj; // The state index for line and stripe
            int k, sk; // The data index for line and stripe
            var nsym = 0; // Symbol counter for symbol and context buffers
            int dscanw; // The data scan-width
            int sscanw; // The state and packed state scan-width
            int jstep; // Stripe to stripe step for 'sj'
            int kstep; // Stripe to stripe step for 'sk'
            int stopsk; // The loop limit on the variable sk
            int csj; // Local copy (i.e. cached) of 'state[j]'
            int mask; // The mask for the current bit-plane
            int sym; // The symbol to code
            int ctxt; // The context to use
            int[] data; // The data buffer
            int dist; // The distortion reduction for this pass
            int shift; // Shift amount for distortion
            int upshift; // Shift left amount for distortion
            int downshift; // Shift right amount for distortion
            int normval; // The normalized sample magnitude value
            int s; // The stripe index
            bool causal; // Flag to indicate if stripe-causal context
                         // formation is to be used
            int nstripes; // The number of stripes in the code-block
            int sheight; // Height of the current stripe
            int off_ul, off_ur, off_dr, off_dl; // offsets

            // Initialize local variables
            dscanw = srcblk.scanw;
            sscanw = srcblk.w + 2;
            jstep = sscanw * StdEntropyCoderOptions.STRIPE_HEIGHT / 2 - srcblk.w;
            kstep = dscanw * StdEntropyCoderOptions.STRIPE_HEIGHT - srcblk.w;
            mask = 1 << bp;
            data = (int[])srcblk.Data;
            nstripes = (srcblk.h + StdEntropyCoderOptions.STRIPE_HEIGHT - 1) / StdEntropyCoderOptions.STRIPE_HEIGHT;
            dist = 0;
            // We use the MSE_LKP_BITS-1 bits below the bit just coded for
            // distortion estimation.
            shift = bp - (MSE_LKP_BITS - 1);
            upshift = (shift >= 0) ? 0 : -shift;
            downshift = (shift <= 0) ? 0 : shift;
            causal = (options & StdEntropyCoderOptions.OPT_VERT_STR_CAUSAL) != 0;

            // Pre-calculate offsets in 'state' for diagonal neighbors
            off_ul = -sscanw - 1; // up-left
            off_ur = -sscanw + 1; // up-right
            off_dr = sscanw + 1; // down-right
            off_dl = sscanw - 1; // down-left

            // Code stripe by stripe
            sk = srcblk.offset;
            sj = sscanw + 1;
            for (s = nstripes - 1; s >= 0; s--, sk += kstep, sj += jstep)
            {
                sheight = (s != 0) ? StdEntropyCoderOptions.STRIPE_HEIGHT : srcblk.h - (nstripes - 1) * StdEntropyCoderOptions.STRIPE_HEIGHT;
                stopsk = sk + srcblk.w;
                // Scan by set of 1 stripe column at a time
                for (nsym = 0; sk < stopsk; sk++, sj++)
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
                            // Apply zero coding
                            ctxtbuf[nsym] = zc_lut[csj & ZC_MASK];
                            if ((symbuf[nsym++] = ((data[k] & mask) >>> bp)) != 0)
                            {
                                // Became significant
                                // Apply sign coding
                                sym = (data[k] >>> 31);
                                ctxt = SC_LUT[((csj >>> SC_SHIFT_R1)) & SC_MASK];
                                symbuf[nsym] = sym ^ ((ctxt >>> SC_SPRED_SHIFT));
                                ctxtbuf[nsym++] = ctxt & SC_LUT_MASK;
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
                                // Update sign state information of neighbors
                                if (sym != 0)
                                {
                                    csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2 | STATE_V_U_SIGN_R2;
                                    if (!causal)
                                    {
                                        // If in causal mode do not change
                                        // contexts of previous stripe.
                                        state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2 | STATE_V_D_SIGN_R2;
                                    }
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | STATE_H_L_SIGN_R1 | STATE_D_UL_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | STATE_H_R_SIGN_R1 | STATE_D_UR_R2;
                                }
                                else
                                {
                                    csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2;
                                    if (!causal)
                                    {
                                        // If in causal mode do not change
                                        // contexts of previous stripe.
                                        state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2;
                                    }
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | STATE_D_UL_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | STATE_D_UR_R2;
                                }
                                // Update distortion
                                normval = (data[k] >> downshift) << upshift;
                                dist += fs[normval & ((1 << (MSE_LKP_BITS - 1)) - 1)];
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
                            // Apply zero coding
                            ctxtbuf[nsym] = zc_lut[((csj >>> STATE_SEP)) & ZC_MASK];
                            if ((symbuf[nsym++] = ((data[k] & mask) >>> bp)) != 0)
                            {
                                // Became significant
                                // Apply sign coding
                                sym = (data[k] >>> 31);
                                ctxt = SC_LUT[((csj >>> SC_SHIFT_R2)) & SC_MASK];
                                symbuf[nsym] = sym ^ ((ctxt >>> SC_SPRED_SHIFT));
                                ctxtbuf[nsym++] = ctxt & SC_LUT_MASK;
                                // Update state information (significant bit,
                                // visited bit, neighbor significant bit of
                                // neighbors, non zero context of neighbors, sign
                                // of neighbors)
                                state[j + off_dl] |= STATE_NZ_CTXT_R1 | STATE_D_UR_R1;
                                state[j + off_dr] |= STATE_NZ_CTXT_R1 | STATE_D_UL_R1;
                                // Update sign state information of neighbors
                                if (sym != 0)
                                {
                                    csj |= STATE_SIG_R2 | STATE_VISITED_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1 | STATE_V_D_SIGN_R1;
                                    state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1 | STATE_V_U_SIGN_R1;
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2 | STATE_H_L_SIGN_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2 | STATE_H_R_SIGN_R2;
                                }
                                else
                                {
                                    csj |= STATE_SIG_R2 | STATE_VISITED_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1;
                                    state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1;
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2;
                                }
                                // Update distortion
                                normval = (data[k] >> downshift) << upshift;
                                dist += fs[normval & ((1 << (MSE_LKP_BITS - 1)) - 1)];
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
                            // Apply zero coding
                            ctxtbuf[nsym] = zc_lut[csj & ZC_MASK];
                            if ((symbuf[nsym++] = ((data[k] & mask) >>> bp)) != 0)
                            {
                                // Became significant
                                // Apply sign coding
                                sym = (data[k] >>> 31);
                                ctxt = SC_LUT[((csj >>> SC_SHIFT_R1)) & SC_MASK];
                                symbuf[nsym] = sym ^ ((ctxt >>> SC_SPRED_SHIFT));
                                ctxtbuf[nsym++] = ctxt & SC_LUT_MASK;
                                // Update state information (significant bit,
                                // visited bit, neighbor significant bit of
                                // neighbors, non zero context of neighbors, sign
                                // of neighbors)
                                state[j + off_ul] |= STATE_NZ_CTXT_R2 | STATE_D_DR_R2;
                                state[j + off_ur] |= STATE_NZ_CTXT_R2 | STATE_D_DL_R2;
                                // Update sign state information of neighbors
                                if (sym != 0)
                                {
                                    csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2 | STATE_V_U_SIGN_R2;
                                    state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2 | STATE_V_D_SIGN_R2;
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | STATE_H_L_SIGN_R1 | STATE_D_UL_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | STATE_H_R_SIGN_R1 | STATE_D_UR_R2;
                                }
                                else
                                {
                                    csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2;
                                    state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2;
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | STATE_D_UL_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | STATE_D_UR_R2;
                                }
                                // Update distortion
                                normval = (data[k] >> downshift) << upshift;
                                dist += fs[normval & ((1 << (MSE_LKP_BITS - 1)) - 1)];
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
                            // Apply zero coding
                            ctxtbuf[nsym] = zc_lut[((csj >>> STATE_SEP)) & ZC_MASK];
                            if ((symbuf[nsym++] = ((data[k] & mask) >>> bp)) != 0)
                            {
                                // Became significant
                                // Apply sign coding
                                sym = (data[k] >>> 31);
                                ctxt = SC_LUT[((csj >>> SC_SHIFT_R2)) & SC_MASK];
                                symbuf[nsym] = sym ^ ((ctxt >>> SC_SPRED_SHIFT));
                                ctxtbuf[nsym++] = ctxt & SC_LUT_MASK;
                                // Update state information (significant bit,
                                // visited bit, neighbor significant bit of
                                // neighbors, non zero context of neighbors, sign
                                // of neighbors)
                                state[j + off_dl] |= STATE_NZ_CTXT_R1 | STATE_D_UR_R1;
                                state[j + off_dr] |= STATE_NZ_CTXT_R1 | STATE_D_UL_R1;
                                // Update sign state information of neighbors
                                if (sym != 0)
                                {
                                    csj |= STATE_SIG_R2 | STATE_VISITED_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1 | STATE_V_D_SIGN_R1;
                                    state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1 | STATE_V_U_SIGN_R1;
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2 | STATE_H_L_SIGN_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2 | STATE_H_R_SIGN_R2;
                                }
                                else
                                {
                                    csj |= STATE_SIG_R2 | STATE_VISITED_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1;
                                    state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1;
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2;
                                }
                                // Update distortion
                                normval = (data[k] >> downshift) << upshift;
                                dist += fs[normval & ((1 << (MSE_LKP_BITS - 1)) - 1)];
                            }
                            else
                            {
                                csj |= STATE_VISITED_R2;
                            }
                        }
                        state[j] = csj;
                    }
                }
                // Code all buffered symbols
                mq.codeSymbols(symbuf, ctxtbuf, nsym);
            }
            // Reset the MQ context states if we need to
            if ((options & StdEntropyCoderOptions.OPT_RESET_MQ) != 0)
            {
                mq.resetCtxts();
            }

            // Terminate the MQ bit stream if we need to
            if (doterm)
            {
                ratebuf[pidx] = mq.terminate(); // Termination has special length
            }
            else
            {
                // Use normal length calculation
                ratebuf[pidx] = mq.NumCodedBytes;
            }
            // Add length of previous segments, if any
            if (ltpidx >= 0)
            {
                ratebuf[pidx] += ratebuf[ltpidx];
            }
            // Finish length calculation if needed
            if (doterm)
            {
                mq.finishLengthCalculation(ratebuf, pidx);
            }

            // Return the reduction in distortion
            return dist;
        }

        /// <summary> Performs the significance propagation pass on the specified data and
        /// bit-plane, without using the arithmetic coder. It codes all
        /// insignificant samples which have, at least, one of its immediate eight
        /// neighbors already significant, using the ZC and SC primitives as
        /// needed. It toggles the "visited" state bit to 1 for all those samples.
        /// 
        /// In this method, the arithmetic coder is bypassed, and raw bits are
        /// directly written in the bit stream (useful when distribution are close
        /// to uniform, for intance, at high bit-rates and at lossless
        /// compression).
        /// 
        /// </summary>
        /// <param name="srcblk">The code-block data to code
        /// 
        /// </param>
        /// <param name="bout">The bit based output
        /// 
        /// </param>
        /// <param name="doterm">If true the bit based output is byte aligned after the
        /// end of the pass.
        /// 
        /// </param>
        /// <param name="bp">The bit-plane to code
        /// 
        /// </param>
        /// <param name="state">The state information for the code-block
        /// 
        /// </param>
        /// <param name="fs">The distortion estimation lookup table for SC
        /// 
        /// </param>
        /// <param name="ratebuf">The buffer where to store the rate (i.e. coded lenth) at 
        /// the end of this coding pass.
        /// 
        /// </param>
        /// <param name="pidx">The coding pass index. Is the index in the 'ratebuf' array
        /// where to store the coded length after this coding pass.
        /// 
        /// </param>
        /// <param name="ltpidx">The index of the last pass that was terminated, or
        /// negative if none.
        /// 
        /// </param>
        /// <param name="options">The bitmask of entropy coding options to apply to the
        /// code-block
        /// 
        /// </param>
        /// <returns> The decrease in distortion for this pass, in the fixed-point
        /// normalized representation of the 'FS_LOSSY' and 'FS_LOSSLESS' tables.
        /// 
        /// </returns>
        private static int rawSigProgPass(CBlkWTData srcblk, BitToByteOutput bout, bool doterm, int bp, int[] state, int[] fs, int[] ratebuf, int pidx, int ltpidx, int options)
        {
            int j, sj; // The state index for line and stripe
            int k, sk; // The data index for line and stripe
            int dscanw; // The data scan-width
            int sscanw; // The state scan-width
            int jstep; // Stripe to stripe step for 'sj'
            int kstep; // Stripe to stripe step for 'sk'
            int stopsk; // The loop limit on the variable sk
            int csj; // Local copy (i.e. cached) of 'state[j]'
            int mask; // The mask for the current bit-plane
            var nsym = 0; // Number of symbol
            int sym; // The symbol to code
            int[] data; // The data buffer
            int dist; // The distortion reduction for this pass
            int shift; // Shift amount for distortion
            int upshift; // Shift left amount for distortion
            int downshift; // Shift right amount for distortion
            int normval; // The normalized sample magnitude value
            int s; // The stripe index
            bool causal; // Flag to indicate if stripe-causal context
                         // formation is to be used
            int nstripes; // The number of stripes in the code-block
            int sheight; // Height of the current stripe
            int off_ul, off_ur, off_dr, off_dl; // offsets

            // Initialize local variables
            dscanw = srcblk.scanw;
            sscanw = srcblk.w + 2;
            jstep = sscanw * StdEntropyCoderOptions.STRIPE_HEIGHT / 2 - srcblk.w;
            kstep = dscanw * StdEntropyCoderOptions.STRIPE_HEIGHT - srcblk.w;
            mask = 1 << bp;
            data = (int[])srcblk.Data;
            nstripes = (srcblk.h + StdEntropyCoderOptions.STRIPE_HEIGHT - 1) / StdEntropyCoderOptions.STRIPE_HEIGHT;
            dist = 0;
            // We use the MSE_LKP_BITS-1 bits below the bit just coded for
            // distortion estimation.
            shift = bp - (MSE_LKP_BITS - 1);
            upshift = (shift >= 0) ? 0 : -shift;
            downshift = (shift <= 0) ? 0 : shift;
            causal = (options & StdEntropyCoderOptions.OPT_VERT_STR_CAUSAL) != 0;

            // Pre-calculate offsets in 'state' for neighbors
            off_ul = -sscanw - 1; // up-left
            off_ur = -sscanw + 1; // up-right
            off_dr = sscanw + 1; // down-right
            off_dl = sscanw - 1; // down-left

            // Code stripe by stripe
            sk = srcblk.offset;
            sj = sscanw + 1;
            for (s = nstripes - 1; s >= 0; s--, sk += kstep, sj += jstep)
            {
                sheight = (s != 0) ? StdEntropyCoderOptions.STRIPE_HEIGHT : srcblk.h - (nstripes - 1) * StdEntropyCoderOptions.STRIPE_HEIGHT;
                stopsk = sk + srcblk.w;
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
                            // Apply zero coding
                            sym = ((data[k] & mask) >>> bp);
                            bout.writeBit(sym);
                            nsym++;
                            if (sym != 0)
                            {
                                // Became significant
                                // Apply sign coding
                                sym = (data[k] >>> 31);
                                bout.writeBit(sym);
                                nsym++;
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
                                // Update sign state information of neighbors
                                if (sym != 0)
                                {
                                    csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2 | STATE_V_U_SIGN_R2;
                                    if (!causal)
                                    {
                                        // If in causal mode do not change
                                        // contexts of previous stripe.
                                        state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2 | STATE_V_D_SIGN_R2;
                                    }
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | STATE_H_L_SIGN_R1 | STATE_D_UL_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | STATE_H_R_SIGN_R1 | STATE_D_UR_R2;
                                }
                                else
                                {
                                    csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2;
                                    if (!causal)
                                    {
                                        // If in causal mode do not change
                                        // contexts of previous stripe.
                                        state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2;
                                    }
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | STATE_D_UL_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | STATE_D_UR_R2;
                                }
                                // Update distortion
                                normval = (data[k] >> downshift) << upshift;
                                dist += fs[normval & ((1 << (MSE_LKP_BITS - 1)) - 1)];
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
                            // Apply zero coding
                            sym = ((data[k] & mask) >>> bp);
                            bout.writeBit(sym);
                            nsym++;
                            if (sym != 0)
                            {
                                // Became significant
                                // Apply sign coding
                                sym = (data[k] >>> 31);
                                bout.writeBit(sym);
                                nsym++;
                                // Update state information (significant bit,
                                // visited bit, neighbor significant bit of
                                // neighbors, non zero context of neighbors, sign
                                // of neighbors)
                                state[j + off_dl] |= STATE_NZ_CTXT_R1 | STATE_D_UR_R1;
                                state[j + off_dr] |= STATE_NZ_CTXT_R1 | STATE_D_UL_R1;
                                // Update sign state information of neighbors
                                if (sym != 0)
                                {
                                    csj |= STATE_SIG_R2 | STATE_VISITED_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1 | STATE_V_D_SIGN_R1;
                                    state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1 | STATE_V_U_SIGN_R1;
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2 | STATE_H_L_SIGN_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2 | STATE_H_R_SIGN_R2;
                                }
                                else
                                {
                                    csj |= STATE_SIG_R2 | STATE_VISITED_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1;
                                    state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1;
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2;
                                }
                                // Update distortion
                                normval = (data[k] >> downshift) << upshift;
                                dist += fs[normval & ((1 << (MSE_LKP_BITS - 1)) - 1)];
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
                            sym = ((data[k] & mask) >>> bp);
                            bout.writeBit(sym);
                            nsym++;
                            if (sym != 0)
                            {
                                // Became significant
                                // Apply sign coding
                                sym = (data[k] >>> 31);
                                bout.writeBit(sym);
                                nsym++;
                                // Update state information (significant bit,
                                // visited bit, neighbor significant bit of
                                // neighbors, non zero context of neighbors, sign
                                // of neighbors)
                                state[j + off_ul] |= STATE_NZ_CTXT_R2 | STATE_D_DR_R2;
                                state[j + off_ur] |= STATE_NZ_CTXT_R2 | STATE_D_DL_R2;
                                // Update sign state information of neighbors
                                if (sym != 0)
                                {
                                    csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2 | STATE_V_U_SIGN_R2;
                                    state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2 | STATE_V_D_SIGN_R2;
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | STATE_H_L_SIGN_R1 | STATE_D_UL_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | STATE_H_R_SIGN_R1 | STATE_D_UR_R2;
                                }
                                else
                                {
                                    csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2;
                                    state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2;
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | STATE_D_UL_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | STATE_D_UR_R2;
                                }
                                // Update distortion
                                normval = (data[k] >> downshift) << upshift;
                                dist += fs[normval & ((1 << (MSE_LKP_BITS - 1)) - 1)];
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
                        if ((csj & (STATE_SIG_R2 | STATE_NZ_CTXT_R2)) == STATE_NZ_CTXT_R2)
                        {
                            k += dscanw;
                            // Apply zero coding
                            sym = ((data[k] & mask) >>> bp);
                            bout.writeBit(sym);
                            nsym++;
                            if (sym != 0)
                            {
                                // Became significant
                                // Apply sign coding
                                sym = (data[k] >>> 31);
                                bout.writeBit(sym);
                                nsym++;
                                // Update state information (significant bit,
                                // visited bit, neighbor significant bit of
                                // neighbors, non zero context of neighbors, sign
                                // of neighbors)
                                state[j + off_dl] |= STATE_NZ_CTXT_R1 | STATE_D_UR_R1;
                                state[j + off_dr] |= STATE_NZ_CTXT_R1 | STATE_D_UL_R1;
                                // Update sign state information of neighbors
                                if (sym != 0)
                                {
                                    csj |= STATE_SIG_R2 | STATE_VISITED_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1 | STATE_V_D_SIGN_R1;
                                    state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1 | STATE_V_U_SIGN_R1;
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2 | STATE_H_L_SIGN_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2 | STATE_H_R_SIGN_R2;
                                }
                                else
                                {
                                    csj |= STATE_SIG_R2 | STATE_VISITED_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1;
                                    state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1;
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2;
                                }
                                // Update distortion
                                normval = (data[k] >> downshift) << upshift;
                                dist += fs[normval & ((1 << (MSE_LKP_BITS - 1)) - 1)];
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

            // Get length and terminate if needed
            if (doterm)
            {
                ratebuf[pidx] = bout.terminate();
            }
            else
            {
                ratebuf[pidx] = bout.length();
            }
            // Add length of previous segments, if any
            if (ltpidx >= 0)
            {
                ratebuf[pidx] += ratebuf[ltpidx];
            }

            // Return the reduction in distortion
            return dist;
        }

        /// <summary> Performs the magnitude refinement pass on the specified data and
        /// bit-plane. It codes the samples which are significant and which do not
        /// have the "visited" state bit turned on, using the MR primitive. The
        /// "visited" state bit is not mofified for any samples.
        /// 
        /// </summary>
        /// <param name="srcblk">The code-block data to code
        /// 
        /// </param>
        /// <param name="mq">The MQ-coder to use
        /// 
        /// </param>
        /// <param name="doterm">If true it performs an MQ-coder termination after the end
        /// of the pass
        /// 
        /// </param>
        /// <param name="bp">The bit-plane to code
        /// 
        /// </param>
        /// <param name="state">The state information for the code-block
        /// 
        /// </param>
        /// <param name="fm">The distortion estimation lookup table for MR
        /// 
        /// </param>
        /// <param name="symbuf">The buffer to hold symbols to send to the MQ coder
        /// 
        /// </param>
        /// <param name="ctxtbuf">A buffer to hold the contexts to use in sending the
        /// buffered symbols to the MQ coder.
        /// 
        /// </param>
        /// <param name="ratebuf">The buffer where to store the rate (i.e. coded lenth) at 
        /// the end of this coding pass.
        /// 
        /// </param>
        /// <param name="pidx">The coding pass index. Is the index in the 'ratebuf' array
        /// where to store the coded length after this coding pass.
        /// 
        /// </param>
        /// <param name="ltpidx">The index of the last pass that was terminated, or
        /// negative if none.
        /// 
        /// </param>
        /// <param name="options">The bitmask of entropy coding options to apply to the
        /// code-block
        /// 
        /// </param>
        /// <returns> The decrease in distortion for this pass, in the fixed-point
        /// normalized representation of the 'FS_LOSSY' and 'FS_LOSSLESS' tables.
        /// 
        /// </returns>
        private static int magRefPass(CBlkWTData srcblk, MQCoder mq, bool doterm, int bp, int[] state, int[] fm, int[] symbuf, int[] ctxtbuf, int[] ratebuf, int pidx, int ltpidx, int options)
        {
            int j, sj; // The state index for line and stripe
            int k, sk; // The data index for line and stripe
            var nsym = 0; // Symbol counter for symbol and context buffers
            int dscanw; // The data scan-width
            int sscanw; // The state scan-width
            int jstep; // Stripe to stripe step for 'sj'
            int kstep; // Stripe to stripe step for 'sk'
            int stopsk; // The loop limit on the variable sk
            int csj; // Local copy (i.e. cached) of 'state[j]'
            int mask; // The mask for the current bit-plane
            int[] data; // The data buffer
            int dist; // The distortion reduction for this pass
            int shift; // Shift amount for distortion
            int upshift; // Shift left amount for distortion
            int downshift; // Shift right amount for distortion
            int normval; // The normalized sample magnitude value
            int s; // The stripe index
            int nstripes; // The number of stripes in the code-block
            int sheight; // Height of the current stripe

            // Initialize local variables
            dscanw = srcblk.scanw;
            sscanw = srcblk.w + 2;
            jstep = sscanw * StdEntropyCoderOptions.STRIPE_HEIGHT / 2 - srcblk.w;
            kstep = dscanw * StdEntropyCoderOptions.STRIPE_HEIGHT - srcblk.w;
            mask = 1 << bp;
            data = (int[])srcblk.Data;
            nstripes = (srcblk.h + StdEntropyCoderOptions.STRIPE_HEIGHT - 1) / StdEntropyCoderOptions.STRIPE_HEIGHT;
            dist = 0;
            // We use the bit just coded plus MSE_LKP_BITS-1 bits below the bit
            // just coded for distortion estimation.
            shift = bp - (MSE_LKP_BITS - 1);
            upshift = (shift >= 0) ? 0 : -shift;
            downshift = (shift <= 0) ? 0 : shift;

            // Code stripe by stripe
            sk = srcblk.offset;
            sj = sscanw + 1;
            for (s = nstripes - 1; s >= 0; s--, sk += kstep, sj += jstep)
            {
                sheight = (s != 0) ? StdEntropyCoderOptions.STRIPE_HEIGHT : srcblk.h - (nstripes - 1) * StdEntropyCoderOptions.STRIPE_HEIGHT;
                stopsk = sk + srcblk.w;
                // Scan by set of 1 stripe column at a time
                for (nsym = 0; sk < stopsk; sk++, sj++)
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
                            // Apply MR primitive
                            symbuf[nsym] = ((data[k] & mask) >>> bp);
                            ctxtbuf[nsym++] = MR_LUT[csj & MR_MASK];
                            // Update the STATE_PREV_MR bit
                            csj |= STATE_PREV_MR_R1;
                            // Update distortion
                            normval = (data[k] >> downshift) << upshift;
                            dist += fm[normval & ((1 << MSE_LKP_BITS) - 1)];
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
                            // Apply MR primitive
                            symbuf[nsym] = ((data[k] & mask) >>> bp);
                            ctxtbuf[nsym++] = MR_LUT[((csj >>> STATE_SEP)) & MR_MASK];
                            // Update the STATE_PREV_MR bit
                            csj |= STATE_PREV_MR_R2;
                            // Update distortion
                            normval = (data[k] >> downshift) << upshift;
                            dist += fm[normval & ((1 << MSE_LKP_BITS) - 1)];
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
                            // Apply MR primitive
                            symbuf[nsym] = ((data[k] & mask) >>> bp);
                            ctxtbuf[nsym++] = MR_LUT[csj & MR_MASK];
                            // Update the STATE_PREV_MR bit
                            csj |= STATE_PREV_MR_R1;
                            // Update distortion
                            normval = (data[k] >> downshift) << upshift;
                            dist += fm[normval & ((1 << MSE_LKP_BITS) - 1)];
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
                            // Apply MR primitive
                            symbuf[nsym] = ((data[k] & mask) >>> bp);
                            ctxtbuf[nsym++] = MR_LUT[((csj >>> STATE_SEP)) & MR_MASK];
                            // Update the STATE_PREV_MR bit
                            csj |= STATE_PREV_MR_R2;
                            // Update distortion
                            normval = (data[k] >> downshift) << upshift;
                            dist += fm[normval & ((1 << MSE_LKP_BITS) - 1)];
                        }
                        state[j] = csj;
                    }
                }
                // Code all buffered symbols, if any
                if (nsym > 0)
                    mq.codeSymbols(symbuf, ctxtbuf, nsym);
            }

            // Reset the MQ context states if we need to
            if ((options & StdEntropyCoderOptions.OPT_RESET_MQ) != 0)
            {
                mq.resetCtxts();
            }

            // Terminate the MQ bit stream if we need to
            if (doterm)
            {
                ratebuf[pidx] = mq.terminate(); // Termination has special length
            }
            else
            {
                // Use normal length calculation
                ratebuf[pidx] = mq.NumCodedBytes;
            }
            // Add length of previous segments, if any
            if (ltpidx >= 0)
            {
                ratebuf[pidx] += ratebuf[ltpidx];
            }
            // Finish length calculation if needed
            if (doterm)
            {
                mq.finishLengthCalculation(ratebuf, pidx);
            }

            // Return the reduction in distortion
            return dist;
        }

        /// <summary> Performs the magnitude refinement pass on the specified data and
        /// bit-plane, without using the arithmetic coder. It codes the samples
        /// which are significant and which do not have the "visited" state bit
        /// turned on, using the MR primitive. The "visited" state bit is not
        /// mofified for any samples.
        /// 
        /// In this method, the arithmetic coder is bypassed, and raw bits are
        /// directly written in the bit stream (useful when distribution are close
        /// to uniform, for intance, at high bit-rates and at lossless
        /// compression). The 'STATE_PREV_MR_R1' and 'STATE_PREV_MR_R2' bits are
        /// not set because they are used only when the arithmetic coder is not
        /// bypassed.
        /// 
        /// </summary>
        /// <param name="srcblk">The code-block data to code
        /// 
        /// </param>
        /// <param name="bout">The bit based output
        /// 
        /// </param>
        /// <param name="doterm">If true the bit based output is byte aligned after the
        /// end of the pass.
        /// 
        /// </param>
        /// <param name="bp">The bit-plane to code
        /// 
        /// </param>
        /// <param name="state">The state information for the code-block
        /// 
        /// </param>
        /// <param name="fm">The distortion estimation lookup table for MR
        /// 
        /// </param>
        /// <param name="ratebuf">The buffer where to store the rate (i.e. coded lenth) at 
        /// the end of this coding pass.
        /// 
        /// </param>
        /// <param name="pidx">The coding pass index. Is the index in the 'ratebuf' array
        /// where to store the coded length after this coding pass.
        /// 
        /// </param>
        /// <param name="ltpidx">The index of the last pass that was terminated, or
        /// negative if none.
        /// 
        /// </param>
        /// <param name="options">The bitmask of entropy coding options to apply to the
        /// code-block
        /// 
        /// </param>
        /// <returns> The decrease in distortion for this pass, in the fixed-point
        /// normalized representation of the 'FS_LOSSY' and 'FS_LOSSLESS' tables.
        /// 
        /// </returns>
        private static int rawMagRefPass(CBlkWTData srcblk, BitToByteOutput bout, bool doterm, int bp, int[] state, int[] fm, int[] ratebuf, int pidx, int ltpidx, int options)
        {
            int j, sj; // The state index for line and stripe
            int k, sk; // The data index for line and stripe
            int dscanw; // The data scan-width
            int sscanw; // The state scan-width
            int jstep; // Stripe to stripe step for 'sj'
            int kstep; // Stripe to stripe step for 'sk'
            int stopsk; // The loop limit on the variable sk
            int csj; // Local copy (i.e. cached) of 'state[j]'
            int mask; // The mask for the current bit-plane
            int[] data; // The data buffer
            int dist; // The distortion reduction for this pass
            int shift; // Shift amount for distortion
            int upshift; // Shift left amount for distortion
            int downshift; // Shift right amount for distortion
            int normval; // The normalized sample magnitude value
            int s; // The stripe index
            int nstripes; // The number of stripes in the code-block
            int sheight; // Height of the current stripe
            var nsym = 0;

            // Initialize local variables
            dscanw = srcblk.scanw;
            sscanw = srcblk.w + 2;
            jstep = sscanw * StdEntropyCoderOptions.STRIPE_HEIGHT / 2 - srcblk.w;
            kstep = dscanw * StdEntropyCoderOptions.STRIPE_HEIGHT - srcblk.w;
            mask = 1 << bp;
            data = (int[])srcblk.Data;
            nstripes = (srcblk.h + StdEntropyCoderOptions.STRIPE_HEIGHT - 1) / StdEntropyCoderOptions.STRIPE_HEIGHT;
            dist = 0;
            // We use the bit just coded plus MSE_LKP_BITS-1 bits below the bit
            // just coded for distortion estimation.
            shift = bp - (MSE_LKP_BITS - 1);
            upshift = (shift >= 0) ? 0 : -shift;
            downshift = (shift <= 0) ? 0 : shift;

            // Code stripe by stripe
            sk = srcblk.offset;
            sj = sscanw + 1;
            for (s = nstripes - 1; s >= 0; s--, sk += kstep, sj += jstep)
            {
                sheight = (s != 0) ? StdEntropyCoderOptions.STRIPE_HEIGHT : srcblk.h - (nstripes - 1) * StdEntropyCoderOptions.STRIPE_HEIGHT;
                stopsk = sk + srcblk.w;
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
                            // Code bit "raw"
                            bout.writeBit(((data[k] & mask) >>> bp));
                            nsym++;
                            // No need to set STATE_PREV_MR_R1 since all magnitude 
                            // refinement passes to follow are "raw"
                            // Update distortion
                            normval = (data[k] >> downshift) << upshift;
                            dist += fm[normval & ((1 << MSE_LKP_BITS) - 1)];
                        }
                        if (sheight < 2)
                            continue;
                        // Scan second row
                        if ((csj & (STATE_SIG_R2 | STATE_VISITED_R2)) == STATE_SIG_R2)
                        {
                            k += dscanw;
                            // Code bit "raw"
                            bout.writeBit(((data[k] & mask) >>> bp));
                            nsym++;
                            // No need to set STATE_PREV_MR_R2 since all magnitude 
                            // refinement passes to follow are "raw"
                            // Update distortion
                            normval = (data[k] >> downshift) << upshift;
                            dist += fm[normval & ((1 << MSE_LKP_BITS) - 1)];
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
                            // Code bit "raw"
                            bout.writeBit(((data[k] & mask) >>> bp));
                            nsym++;
                            // No need to set STATE_PREV_MR_R1 since all magnitude 
                            // refinement passes to follow are "raw"
                            // Update distortion
                            normval = (data[k] >> downshift) << upshift;
                            dist += fm[normval & ((1 << MSE_LKP_BITS) - 1)];
                        }
                        if (sheight < 4)
                            continue;
                        // Scan second row
                        if ((state[j] & (STATE_SIG_R2 | STATE_VISITED_R2)) == STATE_SIG_R2)
                        {
                            k += dscanw;
                            // Code bit "raw"
                            bout.writeBit(((data[k] & mask) >>> bp));
                            nsym++;
                            // No need to set STATE_PREV_MR_R2 since all magnitude 
                            // refinement passes to follow are "raw"
                            // Update distortion
                            normval = (data[k] >> downshift) << upshift;
                            dist += fm[normval & ((1 << MSE_LKP_BITS) - 1)];
                        }
                    }
                }
            }

            // Get length and terminate if needed
            if (doterm)
            {
                ratebuf[pidx] = bout.terminate();
            }
            else
            {
                ratebuf[pidx] = bout.length();
            }

            // Add length of previous segments, if any
            if (ltpidx >= 0)
            {
                ratebuf[pidx] += ratebuf[ltpidx];
            }

            // Return the reduction in distortion
            return dist;
        }

        /// <summary> Performs the cleanup pass on the specified data and bit-plane. It codes
        /// all insignificant samples which have its "visited" state bit off, using
        /// the ZC, SC, and RLC primitives. It toggles the "visited" state bit to 0
        /// (off) for all samples in the code-block.
        /// 
        /// </summary>
        /// <param name="srcblk">The code-block data to code
        /// 
        /// </param>
        /// <param name="mq">The MQ-coder to use
        /// 
        /// </param>
        /// <param name="doterm">If true it performs an MQ-coder termination after the end
        /// of the pass
        /// 
        /// </param>
        /// <param name="bp">The bit-plane to code
        /// 
        /// </param>
        /// <param name="state">The state information for the code-block
        /// 
        /// </param>
        /// <param name="fs">The distortion estimation lookup table for SC
        /// 
        /// </param>
        /// <param name="zc_lut">The ZC lookup table to use in ZC.
        /// 
        /// </param>
        /// <param name="symbuf">The buffer to hold symbols to send to the MQ coder
        /// 
        /// </param>
        /// <param name="ctxtbuf">A buffer to hold the contexts to use in sending the
        /// buffered symbols to the MQ coder.
        /// 
        /// </param>
        /// <param name="ratebuf">The buffer where to store the rate (i.e. coded lenth) at 
        /// the end of this coding pass.
        /// 
        /// </param>
        /// <param name="pidx">The coding pass index. Is the index in the 'ratebuf' array
        /// where to store the coded length after this coding pass.
        /// 
        /// </param>
        /// <param name="ltpidx">The index of the last pass that was terminated, or
        /// negative if none.
        /// 
        /// </param>
        /// <param name="options">The bitmask of entropy coding options to apply to the
        /// code-block
        /// 
        /// </param>
        /// <returns> The decrease in distortion for this pass, in the fixed-point
        /// normalized representation of the 'FS_LOSSY' and 'FS_LOSSLESS' tables.
        /// 
        /// </returns>
        private static int cleanuppass(CBlkWTData srcblk, MQCoder mq, bool doterm, int bp, int[] state, int[] fs, int[] zc_lut, int[] symbuf, int[] ctxtbuf, int[] ratebuf, int pidx, int ltpidx, int options)
        {
            // NOTE: The speedup mode of the MQ coder has been briefly tried to
            // speed up the coding of insignificants RLCs, without any success
            // (i.e. no speedup whatsoever). The use of the speedup mode should be
            // revisisted more in depth and the implementationn of it in MQCoder
            // should be reviewed for optimization opportunities.
            int j, sj; // The state index for line and stripe
            int k, sk; // The data index for line and stripe
            var nsym = 0; // Symbol counter for symbol and context buffers
            int dscanw; // The data scan-width
            int sscanw; // The state scan-width
            int jstep; // Stripe to stripe step for 'sj'
            int kstep; // Stripe to stripe step for 'sk'
            int stopsk; // The loop limit on the variable sk
            int csj; // Local copy (i.e. cached) of 'state[j]'
            int mask; // The mask for the current bit-plane
            int sym; // The symbol to code
            int rlclen; // Length of RLC
            int ctxt; // The context to use
            int[] data; // The data buffer
            int dist; // The distortion reduction for this pass
            int shift; // Shift amount for distortion
            int upshift; // Shift left amount for distortion
            int downshift; // Shift right amount for distortion
            int normval; // The normalized sample magnitude value
            int s; // The stripe index
            bool causal; // Flag to indicate if stripe-causal context
                         // formation is to be used
            int nstripes; // The number of stripes in the code-block
            int sheight; // Height of the current stripe
            int off_ul, off_ur, off_dr, off_dl; // offsets

            // Initialize local variables
            dscanw = srcblk.scanw;
            sscanw = srcblk.w + 2;
            jstep = sscanw * StdEntropyCoderOptions.STRIPE_HEIGHT / 2 - srcblk.w;
            kstep = dscanw * StdEntropyCoderOptions.STRIPE_HEIGHT - srcblk.w;
            mask = 1 << bp;
            data = (int[])srcblk.Data;
            nstripes = (srcblk.h + StdEntropyCoderOptions.STRIPE_HEIGHT - 1) / StdEntropyCoderOptions.STRIPE_HEIGHT;
            dist = 0;
            // We use the MSE_LKP_BITS-1 bits below the bit just coded for
            // distortion estimation.
            shift = bp - (MSE_LKP_BITS - 1);
            upshift = (shift >= 0) ? 0 : -shift;
            downshift = (shift <= 0) ? 0 : shift;
            causal = (options & StdEntropyCoderOptions.OPT_VERT_STR_CAUSAL) != 0;

            // Pre-calculate offsets in 'state' for diagonal neighbors
            off_ul = -sscanw - 1; // up-left
            off_ur = -sscanw + 1; // up-right
            off_dr = sscanw + 1; // down-right
            off_dl = sscanw - 1; // down-left

            // Code stripe by stripe
            sk = srcblk.offset;
            sj = sscanw + 1;
            for (s = nstripes - 1; s >= 0; s--, sk += kstep, sj += jstep)
            {
                sheight = (s != 0) ? StdEntropyCoderOptions.STRIPE_HEIGHT : srcblk.h - (nstripes - 1) * StdEntropyCoderOptions.STRIPE_HEIGHT;
                stopsk = sk + srcblk.w;
                // Scan by set of 1 stripe column at a time
                for (nsym = 0; sk < stopsk; sk++, sj++)
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
                            k = sk;
                            if ((data[k] & mask) != 0)
                            {
                                rlclen = 0;
                            }
                            else if ((data[k += dscanw] & mask) != 0)
                            {
                                rlclen = 1;
                            }
                            else if ((data[k += dscanw] & mask) != 0)
                            {
                                rlclen = 2;
                                j += sscanw;
                                csj = state[j];
                            }
                            else if ((data[k += dscanw] & mask) != 0)
                            {
                                rlclen = 3;
                                j += sscanw;
                                csj = state[j];
                            }
                            else
                            {
                                // Code insignificant RLC
                                symbuf[nsym] = 0;
                                ctxtbuf[nsym++] = RLC_CTXT;
                                // Goto next column
                                continue;
                            }
                            // Code significant RLC
                            symbuf[nsym] = 1;
                            ctxtbuf[nsym++] = RLC_CTXT;
                            // Send MSB bit index
                            symbuf[nsym] = rlclen >> 1;
                            ctxtbuf[nsym++] = UNIF_CTXT;
                            // Send LSB bit index
                            symbuf[nsym] = rlclen & 0x01;
                            ctxtbuf[nsym++] = UNIF_CTXT;
                            // Code sign of sample that became significant
                            // Update distortion
                            normval = (data[k] >> downshift) << upshift;
                            dist += fs[normval & ((1 << (MSE_LKP_BITS - 1)) - 1)];
                            // Apply sign coding
                            sym = (data[k] >>> 31);
                            if ((rlclen & 0x01) == 0)
                            {
                                // Sample that became significant is first row of
                                // its column half
                                ctxt = SC_LUT[((csj >>> SC_SHIFT_R1)) & SC_MASK];
                                symbuf[nsym] = sym ^ ((ctxt >>> SC_SPRED_SHIFT));
                                ctxtbuf[nsym++] = ctxt & SC_LUT_MASK;
                                // Update state information (significant bit,
                                // visited bit, neighbor significant bit of
                                // neighbors, non zero context of neighbors, sign
                                // of neighbors)
                                if (rlclen != 0 || !causal)
                                {
                                    // If in causal mode do not change contexts of 
                                    // previous stripe.
                                    state[j + off_ul] |= STATE_NZ_CTXT_R2 | STATE_D_DR_R2;
                                    state[j + off_ur] |= STATE_NZ_CTXT_R2 | STATE_D_DL_R2;
                                }
                                // Update sign state information of neighbors
                                if (sym != 0)
                                {
                                    csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2 | STATE_V_U_SIGN_R2;
                                    if (rlclen != 0 || !causal)
                                    {
                                        // If in causal mode do not change
                                        // contexts of previous stripe.
                                        state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2 | STATE_V_D_SIGN_R2;
                                    }
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | STATE_H_L_SIGN_R1 | STATE_D_UL_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | STATE_H_R_SIGN_R1 | STATE_D_UR_R2;
                                }
                                else
                                {
                                    csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2;
                                    if (rlclen != 0 || !causal)
                                    {
                                        // If in causal mode do not change
                                        // contexts of previous stripe.
                                        state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2;
                                    }
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | STATE_D_UL_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | STATE_D_UR_R2;
                                }
                                // Changes to csj are saved later
                                if ((rlclen >> 1) != 0)
                                {
                                    // Sample that became significant is in bottom
                                    // half of column => jump to bottom half
                                    goto top_half_brk;
                                }
                                // Otherwise sample that became significant is in
                                // top half of column => continue on top half
                            }
                            else
                            {
                                // Sample that became significant is second row of
                                // its column half
                                ctxt = SC_LUT[((csj >>> SC_SHIFT_R2)) & SC_MASK];
                                symbuf[nsym] = sym ^ ((ctxt >>> SC_SPRED_SHIFT));
                                ctxtbuf[nsym++] = ctxt & SC_LUT_MASK;
                                // Update state information (significant bit,
                                // neighbor significant bit of neighbors, non zero
                                // context of neighbors, sign of neighbors)
                                state[j + off_dl] |= STATE_NZ_CTXT_R1 | STATE_D_UR_R1;
                                state[j + off_dr] |= STATE_NZ_CTXT_R1 | STATE_D_UL_R1;
                                // Update sign state information of neighbors
                                if (sym != 0)
                                {
                                    csj |= STATE_SIG_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1 | STATE_V_D_SIGN_R1;
                                    state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1 | STATE_V_U_SIGN_R1;
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2 | STATE_H_L_SIGN_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2 | STATE_H_R_SIGN_R2;
                                }
                                else
                                {
                                    csj |= STATE_SIG_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1;
                                    state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1;
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2;
                                }
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
                                // Apply zero coding
                                ctxtbuf[nsym] = zc_lut[csj & ZC_MASK];
                                if ((symbuf[nsym++] = ((data[k] & mask) >>> bp)) != 0)
                                {
                                    // Became significant
                                    // Apply sign coding
                                    sym = (data[k] >>> 31);
                                    ctxt = SC_LUT[((csj >>> SC_SHIFT_R1)) & SC_MASK];
                                    symbuf[nsym] = sym ^ ((ctxt >>> SC_SPRED_SHIFT));
                                    ctxtbuf[nsym++] = ctxt & SC_LUT_MASK;
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
                                    // Update sign state information of neighbors
                                    if (sym != 0)
                                    {
                                        csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2 | STATE_V_U_SIGN_R2;
                                        if (!causal)
                                        {
                                            // If in causal mode do not change
                                            // contexts of previous stripe.
                                            state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2 | STATE_V_D_SIGN_R2;
                                        }
                                        state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | STATE_H_L_SIGN_R1 | STATE_D_UL_R2;
                                        state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | STATE_H_R_SIGN_R1 | STATE_D_UR_R2;
                                    }
                                    else
                                    {
                                        csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2;
                                        if (!causal)
                                        {
                                            // If in causal mode do not change
                                            // contexts of previous stripe.
                                            state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2;
                                        }
                                        state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | STATE_D_UL_R2;
                                        state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | STATE_D_UR_R2;
                                    }
                                    // Update distortion
                                    normval = (data[k] >> downshift) << upshift;
                                    dist += fs[normval & ((1 << (MSE_LKP_BITS - 1)) - 1)];
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
                                // Apply zero coding
                                ctxtbuf[nsym] = zc_lut[((csj >>> STATE_SEP)) & ZC_MASK];
                                if ((symbuf[nsym++] = ((data[k] & mask) >>> bp)) != 0)
                                {
                                    // Became significant
                                    // Apply sign coding
                                    sym = (data[k] >>> 31);
                                    ctxt = SC_LUT[((csj >>> SC_SHIFT_R2)) & SC_MASK];
                                    symbuf[nsym] = sym ^ ((ctxt >>> SC_SPRED_SHIFT));
                                    ctxtbuf[nsym++] = ctxt & SC_LUT_MASK;
                                    // Update state information (significant bit,
                                    // visited bit, neighbor significant bit of
                                    // neighbors, non zero context of neighbors,
                                    // sign of neighbors)
                                    state[j + off_dl] |= STATE_NZ_CTXT_R1 | STATE_D_UR_R1;
                                    state[j + off_dr] |= STATE_NZ_CTXT_R1 | STATE_D_UL_R1;
                                    // Update sign state information of neighbors
                                    if (sym != 0)
                                    {
                                        csj |= STATE_SIG_R2 | STATE_VISITED_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1 | STATE_V_D_SIGN_R1;
                                        state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1 | STATE_V_U_SIGN_R1;
                                        state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2 | STATE_H_L_SIGN_R2;
                                        state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2 | STATE_H_R_SIGN_R2;
                                    }
                                    else
                                    {
                                        csj |= STATE_SIG_R2 | STATE_VISITED_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1;
                                        state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1;
                                        state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2;
                                        state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2;
                                    }
                                    // Update distortion
                                    normval = (data[k] >> downshift) << upshift;
                                    dist += fs[normval & ((1 << (MSE_LKP_BITS - 1)) - 1)];
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
                            // Apply zero coding
                            ctxtbuf[nsym] = zc_lut[csj & ZC_MASK];
                            if ((symbuf[nsym++] = ((data[k] & mask) >>> bp)) != 0)
                            {
                                // Became significant
                                // Apply sign coding
                                sym = (data[k] >>> 31);
                                ctxt = SC_LUT[((csj >>> SC_SHIFT_R1)) & SC_MASK];
                                symbuf[nsym] = sym ^ ((ctxt >>> SC_SPRED_SHIFT));
                                ctxtbuf[nsym++] = ctxt & SC_LUT_MASK;
                                // Update state information (significant bit,
                                // visited bit, neighbor significant bit of
                                // neighbors, non zero context of neighbors,
                                // sign of neighbors)
                                state[j + off_ul] |= STATE_NZ_CTXT_R2 | STATE_D_DR_R2;
                                state[j + off_ur] |= STATE_NZ_CTXT_R2 | STATE_D_DL_R2;
                                // Update sign state information of neighbors
                                if (sym != 0)
                                {
                                    csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2 | STATE_V_U_SIGN_R2;
                                    state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2 | STATE_V_D_SIGN_R2;
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | STATE_H_L_SIGN_R1 | STATE_D_UL_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | STATE_H_R_SIGN_R1 | STATE_D_UR_R2;
                                }
                                else
                                {
                                    csj |= STATE_SIG_R1 | STATE_VISITED_R1 | STATE_NZ_CTXT_R2 | STATE_V_U_R2;
                                    state[j - sscanw] |= STATE_NZ_CTXT_R2 | STATE_V_D_R2;
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_L_R1 | STATE_D_UL_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_H_R_R1 | STATE_D_UR_R2;
                                }
                                // Update distortion
                                normval = (data[k] >> downshift) << upshift;
                                dist += fs[normval & ((1 << (MSE_LKP_BITS - 1)) - 1)];
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
                            // Apply zero coding
                            ctxtbuf[nsym] = zc_lut[((csj >>> STATE_SEP)) & ZC_MASK];
                            if ((symbuf[nsym++] = ((data[k] & mask) >>> bp)) != 0)
                            {
                                // Became significant
                                // Apply sign coding
                                sym = (data[k] >>> 31);
                                ctxt = SC_LUT[((csj >>> SC_SHIFT_R2)) & SC_MASK];
                                symbuf[nsym] = sym ^ ((ctxt >>> SC_SPRED_SHIFT));
                                ctxtbuf[nsym++] = ctxt & SC_LUT_MASK;
                                // Update state information (significant bit,
                                // visited bit, neighbor significant bit of
                                // neighbors, non zero context of neighbors,
                                // sign of neighbors)
                                state[j + off_dl] |= STATE_NZ_CTXT_R1 | STATE_D_UR_R1;
                                state[j + off_dr] |= STATE_NZ_CTXT_R1 | STATE_D_UL_R1;
                                // Update sign state information of neighbors
                                if (sym != 0)
                                {
                                    csj |= STATE_SIG_R2 | STATE_VISITED_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1 | STATE_V_D_SIGN_R1;
                                    state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1 | STATE_V_U_SIGN_R1;
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2 | STATE_H_L_SIGN_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2 | STATE_H_R_SIGN_R2;
                                }
                                else
                                {
                                    csj |= STATE_SIG_R2 | STATE_VISITED_R2 | STATE_NZ_CTXT_R1 | STATE_V_D_R1;
                                    state[j + sscanw] |= STATE_NZ_CTXT_R1 | STATE_V_U_R1;
                                    state[j + 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DL_R1 | STATE_H_L_R2;
                                    state[j - 1] |= STATE_NZ_CTXT_R1 | STATE_NZ_CTXT_R2 | STATE_D_DR_R1 | STATE_H_R_R2;
                                }
                                // Update distortion
                                normval = (data[k] >> downshift) << upshift;
                                dist += fs[normval & ((1 << (MSE_LKP_BITS - 1)) - 1)];
                            }
                        }
                    }
                    csj &= ~(STATE_VISITED_R1 | STATE_VISITED_R2);
                    state[j] = csj;
                }
                // Code all buffered symbols, if any
                if (nsym > 0)
                    mq.codeSymbols(symbuf, ctxtbuf, nsym);
            }

            // Insert a segment marker if we need to
            if ((options & StdEntropyCoderOptions.OPT_SEG_SYMBOLS) != 0)
            {
                mq.codeSymbols(SEG_SYMBOLS, SEG_SYMB_CTXTS, SEG_SYMBOLS.Length);
            }

            // Reset the MQ context states if we need to
            if ((options & StdEntropyCoderOptions.OPT_RESET_MQ) != 0)
            {
                mq.resetCtxts();
            }

            // Terminate the MQ bit stream if we need to
            if (doterm)
            {
                ratebuf[pidx] = mq.terminate(); // Termination has special length
            }
            else
            {
                // Use normal length calculation
                ratebuf[pidx] = mq.NumCodedBytes;
            }
            // Add length of previous segments, if any
            if (ltpidx >= 0)
            {
                ratebuf[pidx] += ratebuf[ltpidx];
            }
            // Finish length calculation if needed
            if (doterm)
            {
                mq.finishLengthCalculation(ratebuf, pidx);
            }
            // Return the reduction in distortion
            return dist;
        }

        /// <summary> Ensures that at the end of a non-terminated coding pass there is not a
        /// 0xFF byte, modifying the stored rates if necessary.
        /// 
        /// Due to error resiliance reasons, a coding pass should never have its
        /// last byte be a 0xFF, since that can lead to the emulation of a resync
        /// marker. This method checks if that is the case, and reduces the rate
        /// for a given pass if necessary. The ommitted 0xFF will be synthetized by
        /// the decoder if necessary, as required by JPEG 2000. This method should
        /// only be called once that the entire code-block is coded.
        /// 
        /// Passes that are terminated are not checked for the 0xFF byte, since
        /// it is assumed that the termination procedure does not output any
        /// trailing 0xFF. Checking the terminated segments would involve much more
        /// than just modifying the stored rates.
        /// 
        /// NOTE: It is assumed by this method that the coded data does not
        /// contain consecutive 0xFF bytes, as is the case with the MQ and
        /// 'arithemetic coding bypass' bit stuffing policy. However, the
        /// termination policies used should also respect this requirement.
        /// 
        /// </summary>
        /// <param name="data">The coded data for the code-block
        /// 
        /// </param>
        /// <param name="rates">The rate (i.e. accumulated number of bytes) for each
        /// coding pass
        /// 
        /// </param>
        /// <param name="isterm">An array of flags indicating, for each pass, if it is
        /// terminated or not. If null it is assumed that no pass is terminated,
        /// except the last one.
        /// 
        /// </param>
        /// <param name="n">The number of coding passes
        /// 
        /// </param>
        private static void checkEndOfPassFF(byte[] data, int[] rates, bool[] isterm, int n)
        {
            int dp; // the position to test in 'data'

            // If a pass ends in 0xFF we need to reduce the number of bytes in it,
            // so that it does not end in 0xFF. We only need to go back one byte
            // since there can be no consecutive 0xFF bytes.

            // If there are no terminated passes avoid the test on 'isterm'
            if (isterm == null)
            {
                for (n--; n >= 0; n--)
                {
                    dp = rates[n] - 1;
                    if (dp >= 0 && (data[dp] == 0xFF))
                    {
                        rates[n]--;
                    }
                }
            }
            else
            {
                for (n--; n >= 0; n--)
                {
                    if (!isterm[n])
                    {
                        dp = rates[n] - 1;
                        if (dp >= 0 && (data[dp] == 0xFF))
                        {
                            rates[n]--;
                        }
                    }
                }
            }
        }

        /// <summary> Load options, length calculation type and termination type for each
        /// tile-component.
        /// 
        /// </summary>
        /// <param name="nt">The number of tiles
        /// 
        /// </param>
        /// <param name="nc">The number of components
        /// 
        /// </param>
        public void initTileComp(int nt, int nc)
        {

            opts = new int[nt][];
            for (var i2 = 0; i2 < nt; i2++)
            {
                opts[i2] = new int[nc];
            }
            lenCalc = new int[nt][];
            for (var i3 = 0; i3 < nt; i3++)
            {
                lenCalc[i3] = new int[nc];
            }
            tType = new int[nt][];
            for (var i4 = 0; i4 < nt; i4++)
            {
                tType[i4] = new int[nc];
            }

            for (var t = 0; t < nt; t++)
            {
                for (var c = 0; c < nc; c++)
                {
                    opts[t][c] = 0;

                    // Bypass coding mode ?
                    if (string.Equals((string)bms.GetTileCompVal(t, c), "on", StringComparison.OrdinalIgnoreCase))
                    {
                        opts[t][c] |= StdEntropyCoderOptions.OPT_BYPASS;
                    }
                    // MQ reset after each coding pass ?
                    if (string.Equals((string)mqrs.GetTileCompVal(t, c), "on", StringComparison.OrdinalIgnoreCase))
                    {
                        opts[t][c] |= StdEntropyCoderOptions.OPT_RESET_MQ;
                    }
                    // MQ termination after each arithmetically coded coding pass ?
                    if (string.Equals((string)rts.GetTileCompVal(t, c), "on", StringComparison.OrdinalIgnoreCase))
                    {
                        opts[t][c] |= StdEntropyCoderOptions.OPT_TERM_PASS;
                    }
                    // Vertically stripe-causal context mode ?
                    if (string.Equals((string)css.GetTileCompVal(t, c), "on", StringComparison.OrdinalIgnoreCase))
                    {
                        opts[t][c] |= StdEntropyCoderOptions.OPT_VERT_STR_CAUSAL;
                    }
                    // Error resilience segmentation symbol insertion ?
                    if (string.Equals((string)sss.GetTileCompVal(t, c), "on", StringComparison.OrdinalIgnoreCase))
                    {
                        opts[t][c] |= StdEntropyCoderOptions.OPT_SEG_SYMBOLS;
                    }

                    // Set length calculation type of the MQ coder
                    var lCalcType = (string)lcs.GetTileCompVal(t, c);
                    if (lCalcType.Equals("near_opt"))
                    {
                        lenCalc[t][c] = MQCoder.LENGTH_NEAR_OPT;
                    }
                    else if (lCalcType.Equals("lazy_good"))
                    {
                        lenCalc[t][c] = MQCoder.LENGTH_LAZY_GOOD;
                    }
                    else if (lCalcType.Equals("lazy"))
                    {
                        lenCalc[t][c] = MQCoder.LENGTH_LAZY;
                    }
                    else
                    {
                        throw new System.ArgumentException("Unrecognized or " + "unsupported MQ " + "length calculation.");
                    }

                    // Set termination type of MQ coder
                    var termType = (string)tts.GetTileCompVal(t, c);
                    if (string.Equals(termType, "easy", StringComparison.OrdinalIgnoreCase))
                    {
                        tType[t][c] = MQCoder.TERM_EASY;
                    }
                    else if (string.Equals(termType, "full", StringComparison.OrdinalIgnoreCase))
                    {
                        tType[t][c] = MQCoder.TERM_FULL;
                    }
                    else if (string.Equals(termType, "near_opt", StringComparison.OrdinalIgnoreCase))
                    {
                        tType[t][c] = MQCoder.TERM_NEAR_OPT;
                    }
                    else if (string.Equals(termType, "predict", StringComparison.OrdinalIgnoreCase))
                    {
                        tType[t][c] = MQCoder.TERM_PRED_ER;
                        opts[t][c] |= StdEntropyCoderOptions.OPT_PRED_TERM;
                        if ((opts[t][c] & (StdEntropyCoderOptions.OPT_TERM_PASS | StdEntropyCoderOptions.OPT_BYPASS)) == 0)
                        {
                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, "Using error resilient MQ" + " termination, but terminating only at " + "the end of code-blocks. The error " + "protection offered by this option will" + " be very weak. Specify the " + "'Cterminate' " + "and/or 'Cbypass' option for " + "increased error resilience.");
                        }
                    }
                    else
                    {
                        throw new System.ArgumentException("Unrecognized or " + "unsupported " + "MQ coder " + "termination.");
                    }
                } // End loop on components
            } // End loop on tiles
        }

        /// <summary> Returns the precinct partition width for the specified component, tile
        /// and resolution level.
        /// 
        /// </summary>
        /// <param name="t">the tile index
        /// 
        /// </param>
        /// <param name="c">the component
        /// 
        /// </param>
        /// <param name="rl">the resolution level
        /// 
        /// </param>
        /// <returns> The precinct partition width for the specified component, tile
        /// and resolution level
        /// 
        /// </returns>
        public override int GetPPX(int t, int c, int rl)
        {
            return pss.GetPPX(t, c, rl);
        }

        /// <summary> Returns the precinct partition height for the specified component, tile
        /// and resolution level.
        /// 
        /// </summary>
        /// <param name="t">the tile index
        /// 
        /// </param>
        /// <param name="c">the component
        /// 
        /// </param>
        /// <param name="rl">the resolution level
        /// 
        /// </param>
        /// <returns> The precinct partition height for the specified component, tile
        /// and resolution level
        /// 
        /// </returns>
        public override int GetPPY(int t, int c, int rl)
        {
            return pss.GetPPY(t, c, rl);
        }

        /// <summary> Returns true if precinct partition is used for the specified component
        /// and tile, returns false otherwise.
        /// 
        /// </summary>
        /// <param name="c">The component
        /// 
        /// </param>
        /// <param name="t">The tile 
        /// 
        /// </param>
        /// <returns> True if precinct partition is used for the specified component
        /// and tile, returns false otherwise.
        /// 
        /// </returns>
        public override bool precinctPartitionUsed(int c, int t)
        {
            return precinctPartition[c][t];
        }
        /// <summary>Static initializer: initializes all the lookup tables. </summary>
        static StdEntropyCoder()
        {
            {
                int i, j;
                double val, deltaMSE;
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

                // Initialize the distortion estimation lookup tables

                // fs tables
                for (i = 0; i < (1 << (MSE_LKP_BITS - 1)); i++)
                {
                    // In fs we index by val-1, since val is really: 1 <= val < 2
                    val = (double)i / (1 << (MSE_LKP_BITS - 1)) + 1.0;
                    deltaMSE = val * val;
                    FS_LOSSLESS[i] = (int)System.Math.Floor(deltaMSE * (1 << MSE_LKP_FRAC_BITS) + 0.5);
                    val -= 1.5;
                    deltaMSE -= val * val;
                    FS_LOSSY[i] = (int)System.Math.Floor(deltaMSE * (1 << MSE_LKP_FRAC_BITS) + 0.5);
                }

                // fm tables
                for (i = 0; i < (1 << MSE_LKP_BITS); i++)
                {
                    val = (double)i / (1 << (MSE_LKP_BITS - 1));
                    deltaMSE = (val - 1.0) * (val - 1.0);
                    FM_LOSSLESS[i] = (int)System.Math.Floor(deltaMSE * (1 << MSE_LKP_FRAC_BITS) + 0.5);
                    val -= ((i < (1 << (MSE_LKP_BITS - 1))) ? 0.5 : 1.5);
                    deltaMSE -= val * val;
                    FM_LOSSY[i] = (int)System.Math.Floor(deltaMSE * (1 << MSE_LKP_FRAC_BITS) + 0.5);
                }
            }
        }
    }
}