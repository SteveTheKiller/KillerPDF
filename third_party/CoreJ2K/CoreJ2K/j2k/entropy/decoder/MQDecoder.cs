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
using CoreJ2K.j2k.util;
using System;
using System.Runtime.CompilerServices;

namespace CoreJ2K.j2k.entropy.decoder
{

    /// <summary> This class implements the MQ arithmetic decoder. It is implemented using
    /// the software conventions decoder for better performance (i.e. execution
    /// time performance). The initial states for each context of the MQ-coder are
    /// specified in the constructor.
    /// 
    /// </summary>

    // A trick to test for increased speed: merge the Qe and mPS into 1 thing by
    // using the sign bit of Qe to signal mPS (positive-or-0 is 0, negative is 1),
    // and doubling the Qe, nMPS and nLPS tables. This gets rid of the swicthLM
    // table since it can be integrated as special cases in the doubled nMPS and
    // nLPS tables. See the JPEG book, chapter 13. The decoded decision can be
    // calculated as (q>>>31).

    public class MQDecoder
    {
        /// <summary> Returns the number of contexts in the arithmetic coder.
        /// 
        /// </summary>
        /// <returns> The number of contexts
        /// 
        /// </returns>
        public virtual int NumCtxts => I.Length;

        /// <summary> Returns the underlying 'ByteInputBuffer' from where the MQ coded input
        /// bytes are read.
        /// 
        /// </summary>
        /// <returns> The underlying ByteInputBuffer.
        /// 
        /// </returns>
        public virtual ByteInputBuffer ByteInputBuffer => inputBuffer;

        /// <summary>The data structures containing the probabilities for the LPS </summary>
        internal static readonly uint[] qe = { 0x5601, 0x3401, 0x1801, 0x0ac1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801, 0x3001, 0x2401, 0x1c01, 0x1601, 0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201, 0x1c01, 0x1801, 0x1601, 0x1401, 0x1201, 0x1101, 0x0ac1, 0x09c1, 0x08a1, 0x0521, 0x0441, 0x02a1, 0x0221, 0x0141, 0x0111, 0x0085, 0x0049, 0x0025, 0x0015, 0x0009, 0x0005, 0x0001, 0x5601 };

        /// <summary>The indexes of the next MPS </summary>
        internal static readonly int[] nMPS = { 1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46 };

        /// <summary>The indexes of the next LPS </summary>
        internal static readonly int[] nLPS = { 1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46 };

        /// <summary>Whether LPS and MPS should be switched </summary>
        internal static readonly int[] switchLM = { 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        /// <summary>The ByteInputBuffer used to read the compressed bit stream. </summary>
        internal ByteInputBuffer inputBuffer;

        /// <summary>The current most probable signal for each context </summary>
        internal int[] mPS;

        /// <summary>The current index of each context </summary>
        internal int[] I;

        /// <summary>The current bit code </summary>
        internal uint c;

        /// <summary>The bit code counter </summary>
        internal uint cT;

        /// <summary>The current interval </summary>
        internal uint a;

        /// <summary>The last byte read </summary>
        internal uint b;

        /// <summary>Flag indicating if a marker has been found </summary>
        internal bool markerFound;

        /// <summary>The initial state of each context </summary>
        internal int[] initStates;

        /// <summary> Instantiates a new MQ-decoder, with the specified number of contexts
        /// and initial states. The compressed bytestream is read from the
        /// 'iStream' object.
        /// 
        /// </summary>
        /// <param name="iStream">the stream that contains the coded bits 
        /// 
        /// </param>
        /// <param name="nrOfContexts">The number of contexts used
        /// 
        /// </param>
        /// <param name="initStates">The initial state for each context. A reference is
        /// kept to this array to reinitialize the contexts whenever 'reset()' or
        /// 'resetCtxts()' is called.
        /// 
        /// </param>
        public MQDecoder(ByteInputBuffer iStream, int nrOfContexts, int[] initStates)
        {
            inputBuffer = iStream;

            // Default initialization of the statistics bins is MPS=0 and
            // I=0
            I = new int[nrOfContexts];
            mPS = new int[nrOfContexts];
            // Save the initial states
            this.initStates = initStates;

            // Initialize
            init();

            // Set the contexts
            resetCtxts();
        }

        /// <summary> Decodes 'n' symbols from the bit stream using the same context
        /// 'ctxt'. If possible the MQ-coder speedup mode will be used to speed up
        /// decoding. The speedup mode is used if Q (the LPS probability for 'ctxt'
        /// is low enough) and the A and C registers permit decoding several MPS
        /// symbols without renormalization.
        /// 
        /// Speedup mode should be used when decoding long runs of MPS with high
        /// probability with the same context.
        /// 
        /// This methiod will return the decoded symbols differently if speedup 
        /// mode was used or not. If true is returned, then speedup mode was used
        /// and the 'n' decoded symbols are all the same and it is returned ain
        /// bits[0] only. If false is returned then speedup mode was not used, the
        /// decoded symbols are probably not all the same and they are returned in
        /// bits[0], bits[1], ... bits[n-1].
        /// 
        /// </summary>
        /// <param name="bits">The array where to put the decoded symbols. Must be of
        /// length 'n' or more.
        /// 
        /// </param>
        /// <param name="ctxt">The context to use in decoding the symbols.
        /// 
        /// </param>
        /// <param name="n">The number of symbols to decode.
        /// 
        /// </param>
        /// <returns> True if speedup mode was used, false if not. If speedup mode
        /// was used then all the decoded symbols are the same and its value is
        /// returned in 'bits[0]' only (not in bits[1], bits[2], etc.).
        /// 
        /// </returns>
        internal bool fastDecodeSymbols(int[] bits, int ctxt, uint n)
        {
            uint q; // LPS probability for context
            int idx; // Index of current state
            uint la; // cache for A register
            int i; // counter

            idx = I[ctxt];
            q = qe[idx];

            // This is a first attempt to implement speedup mode, it is probably
            // not the most efficient way of doing it.

            if ((q < 0x4000) && (n <= (a - (c >> 16) - 1) / q) && (n <= (a - 0x8000) / q + 1))
            {
                // Q is small enough. There will be no modification of C that
                // affects decoding, and Q can be substracted from A several
                // times. We will decode all MPS.
                a -= n * q;
                if (a >= 0x8000)
                {
                    // No renormalization needed
                    bits[0] = mPS[ctxt];
                    return true; // Done, used speedup mode
                }
                else
                {
                    // renormalization needed
                    I[ctxt] = nMPS[idx];
                    // Renormalize (MPS: no need for while loop)
                    if (cT == 0)
                        byteIn();
                    a <<= 1;
                    c <<= 1;
                    cT--;
                    // End renormalization
                    bits[0] = mPS[ctxt];
                    return true; // Done, used speedup mode
                }
            }
            else
            {
                // Normal mode
                la = a; // cache A register
                for (i = 0; i < n; i++)
                {
                    la -= q;
                    if ((c >> 16) < la)
                    {
                        if (la >= 0x8000)
                        {
                            bits[i] = mPS[ctxt];
                        }
                        else
                        {
                            // -- MPS Exchange
                            if (la >= q)
                            {
                                bits[i] = mPS[ctxt];
                                idx = nMPS[idx];
                                q = qe[idx];
                                // I[ctxt] set at end of loop
                                // -- Renormalize (MPS: no need for while loop)
                                if (cT == 0)
                                    byteIn();
                                la <<= 1;
                                c <<= 1;
                                cT--;
                                // -- End renormalization
                            }
                            else
                            {
                                bits[i] = 1 - mPS[ctxt];
                                if (switchLM[idx] == 1)
                                    mPS[ctxt] = 1 - mPS[ctxt];
                                idx = nLPS[idx];
                                q = qe[idx];
                                // I[ctxt] set at end of loop
                                // -- Renormalize
                                do
                                {
                                    if (cT == 0)
                                        byteIn();
                                    la <<= 1;
                                    c <<= 1;
                                    cT--;
                                }
                                while (la < 0x8000);
                                // -- End renormalization
                            }
                            // -- End MPS Exchange
                        }
                    }
                    else
                    {
                        c -= (la << 16);
                        // -- LPS Exchange
                        if (la < q)
                        {
                            la = q;
                            bits[i] = mPS[ctxt];
                            idx = nMPS[idx];
                            q = qe[idx];
                            // I[ctxt] set at end of loop
                            // -- Renormalize (MPS: no need for while loop)
                            if (cT == 0)
                                byteIn();
                            la <<= 1;
                            c <<= 1;
                            cT--;
                            // -- End renormalization
                        }
                        else
                        {
                            la = q;
                            bits[i] = 1 - mPS[ctxt];
                            if (switchLM[idx] == 1)
                                mPS[ctxt] = 1 - mPS[ctxt];
                            idx = nLPS[idx];
                            q = qe[idx];
                            // I[ctxt] set at end of loop
                            // -- Renormalize
                            do
                            {
                                if (cT == 0)
                                    byteIn();
                                la <<= 1;
                                c <<= 1;
                                cT--;
                            }
                            while (la < 0x8000);
                            // -- End renormalization
                        }
                        // -- End LPS Exchange
                    }
                }
                a = la; // save cached A register
                I[ctxt] = idx; // save current index for context
                return false; // done, did not use speedup mode
            } // End normal mode
        }

        /// <summary> This function performs the arithmetic decoding. The function receives 
        /// an array in which to put the decoded symbols and an array of contexts 
        /// with which to decode them. 
        /// 
        /// Each context has a current MPS and an index describing what the
        /// current probability is for the LPS. Each bit is decoded and if the
        /// probability of the LPS exceeds .5, the MPS and LPS are switched.
        /// 
        /// </summary>
        /// <param name="bits">The array where to place the decoded symbols. It should be
        /// long enough to contain 'n' elements.
        /// 
        /// </param>
        /// <param name="cX">The context to use in decoding each symbol.
        /// 
        /// </param>
        /// <param name="n">The number of symbols to decode
        /// 
        /// </param>
        public void decodeSymbols(int[] bits, int[] cX, int n)
        {
            uint q;
            int ctxt;
            uint la; // cache for A register value
            int index;
            int i;

            // NOTE: (a < 0x8000) is equivalent to ((a & 0x8000)==0)
            // since 'a' is always less than or equal to 0xFFFF

            // NOTE: conditional exchange guarantees that A for MPS is
            // always greater than 0x4000 (i.e. 0.375)
            // => one renormalization shift is enough for MPS
            // => no need to do a renormalization while loop for MPS

            for (i = 0; i < n; i++)
            {
                ctxt = cX[i];

                index = I[ctxt];
                q = qe[index];

                a -= q;
                if ((c >> 16) < a)
                {
                    if (a >= 0x8000)
                    {
                        bits[i] = mPS[ctxt];
                    }
                    else
                    {
                        la = a;
                        // -- MPS Exchange
                        if (la >= q)
                        {
                            bits[i] = mPS[ctxt];
                            I[ctxt] = nMPS[index];
                            // -- Renormalize (MPS: no need for while loop)
                            if (cT == 0)
                                byteIn();
                            la <<= 1;
                            c <<= 1;
                            cT--;
                            // -- End renormalization
                        }
                        else
                        {
                            bits[i] = 1 - mPS[ctxt];
                            if (switchLM[index] == 1)
                                mPS[ctxt] = 1 - mPS[ctxt];
                            I[ctxt] = nLPS[index];
                            // -- Renormalize
                            do
                            {
                                if (cT == 0)
                                    byteIn();
                                la <<= 1;
                                c <<= 1;
                                cT--;
                            }
                            while (la < 0x8000);
                            // -- End renormalization
                        }
                        // -- End MPS Exchange
                        a = la;
                    }
                }
                else
                {
                    la = a;
                    c -= (la << 16);
                    // -- LPS Exchange
                    if (la < q)
                    {
                        la = q;
                        bits[i] = mPS[ctxt];
                        I[ctxt] = nMPS[index];
                        // -- Renormalize (MPS: no need for while loop)
                        if (cT == 0)
                            byteIn();
                        la <<= 1;
                        c <<= 1;
                        cT--;
                        // -- End renormalization
                    }
                    else
                    {
                        la = q;
                        bits[i] = 1 - mPS[ctxt];
                        if (switchLM[index] == 1)
                            mPS[ctxt] = 1 - mPS[ctxt];
                        I[ctxt] = nLPS[index];
                        // -- Renormalize
                        do
                        {
                            if (cT == 0)
                                byteIn();
                            la <<= 1;
                            c <<= 1;
                            cT--;
                        }
                        while (la < 0x8000);
                        // -- End renormalization
                    }
                    // -- End LPS Exchange

                    a = la;
                }
            }
        }


        /// <summary> Arithmetically decodes one symbol from the bit stream with the given
        /// context and returns its decoded value.
        /// 
        /// Each context has a current MPS and an index describing what the
        /// current probability is for the LPS. Each bit is encoded and if the
        /// probability of the LPS exceeds .5, the MPS and LPS are switched.
        /// 
        /// </summary>
        /// <param name="context">The context to use in decoding the symbol
        /// 
        /// </param>
        /// <returns> The decoded symbol, 0 or 1.
        /// 
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int decodeSymbol(int context)
        {
            // Pin I[context] and mPS[context] as ref locals: single bounds check each,
            // all subsequent reads/writes go through the pinned reference.
            ref int iCtx = ref I[context];
            ref int mpsCtx = ref mPS[context];
            int index = iCtx;
            int curMps = mpsCtx;        // hoist: avoids a second indexed read on every path
            uint q = qe[index];
            uint la;
            int decision;

            // NOTE: (a < 0x8000) is equivalent to ((a & 0x8000)==0)
            // since 'a' is always less than or equal to 0xFFFF

            // NOTE: conditional exchange guarantees that A for MPS is
            // always greater than 0x4000 (i.e. 0.375)
            // => one renormalization shift is enough for MPS
            // => no need to do a renormalization while loop for MPS

            a -= q;
            if ((c >> 16) < a)
            {
                if (a >= 0x8000)
                {
                    // Most common fast path: MPS, interval already large — no renorm
                    decision = curMps;
                }
                else
                {
                    la = a;
                    // -- MPS Exchange
                    if (la >= q)
                    {
                        decision = curMps;
                        iCtx = nMPS[index];
                        // -- Renormalize (MPS: no need for while loop)
                        if (cT == 0)
                            byteIn();
                        la <<= 1;
                        c <<= 1;
                        cT--;
                        // -- End renormalization
                    }
                    else
                    {
                        decision = 1 - curMps;
                        if (switchLM[index] == 1)
                            mpsCtx = 1 - curMps;
                        iCtx = nLPS[index];
                        // -- Renormalize
                        do
                        {
                            if (cT == 0)
                                byteIn();
                            la <<= 1;
                            c <<= 1;
                            cT--;
                        }
                        while (la < 0x8000);
                        // -- End renormalization
                    }
                    // -- End MPS Exchange
                    a = la;
                }
            }
            else
            {
                la = a;
                c -= (la << 16);
                // -- LPS Exchange
                if (la < q)
                {
                    la = q;
                    decision = curMps;
                    iCtx = nMPS[index];
                    // -- Renormalize (MPS: no need for while loop)
                    if (cT == 0)
                        byteIn();
                    la <<= 1;
                    c <<= 1;
                    cT--;
                    // -- End renormalization
                }
                else
                {
                    la = q;
                    decision = 1 - curMps;
                    if (switchLM[index] == 1)
                        mpsCtx = 1 - curMps;
                    iCtx = nLPS[index];
                    // -- Renormalize
                    do
                    {
                        if (cT == 0)
                            byteIn();
                        la <<= 1;
                        c <<= 1;
                        cT--;
                    }
                    while (la < 0x8000);
                    // -- End renormalization
                }
                // -- End LPS Exchange

                a = la;
            }
            return decision;
        }

        /// <summary> Checks for past errors in the decoding process using the predictable
        /// error resilient termination. This works only if the encoder used the
        /// predictable error resilient MQ termination, otherwise it reports wrong
        /// results. If an error is detected it means that the MQ bit stream has
        /// been wrongly decoded or that the MQ terminated segment length is too
        /// long. If no errors are detected it does not necessarily mean that the
        /// MQ bit stream has been correctly decoded.
        /// 
        /// </summary>
        /// <returns> True if errors are found, false otherwise.
        /// 
        /// </returns>
        public virtual bool checkPredTerm()
        {
            int k; // Number of bits that where added in the termination process
            uint q;

            // 1) if everything has been OK, 'b' must be 0xFF if a terminating
            // marker has not yet been found
            if (b != 0xFF && !markerFound)
                return true;

            // 2) if cT is not 0, we must have already reached the terminating
            // marker
            if (cT != 0 && !markerFound)
                return true;

            // 3) If cT is 1 there where no spare bits at the encoder, this is all
            // that we can check
            if (cT == 1)
                return false;

            // 4) if cT is 0, then next byte must be the second byte of a
            // terminating marker (i.e. larger than 0x8F) if the terminating
            // marker has not been reached yet
            if (cT == 0)
            {
                if (!markerFound)
                {
                    // Get next byte and check
                    b = (uint)inputBuffer.read() & 0xFF;
                    if (b <= 0x8F)
                        return true;
                }
                // Adjust cT for last byte
                cT = 8;
            }

            // 5) Now we can calculate the number 'k' of bits having error
            // resilience information, which is the number of bits left to
            // normalization in the C register, minus 1.
            k = (int)(cT - 1);

            // 6) The predictable termination policy is as if an LPS interval was
            // coded that caused a renormalization of 'k' bits, before the
            // termination marker started

            // We first check if an LPS is decoded, that causes a renormalization
            // of 'k' bits. Worst case is smallest LPS probability 'q' that causes
            // a renormalization of 'k' bits.
            q = ((uint)0x8000) >> k;

            // Check that we can decode an LPS interval of probability 'q'
            a -= q;
            if ((c >> 16) < a)
            {
                // Error: MPS interval decoded
                return true;
            }
            // OK: LPS interval decoded
            c -= (a << 16);
            // -- LPS Exchange
            // Here 'a' can not be smaller than 'q' because the minimum value
            // for 'a' is 0x8000-0x4000=0x4000 and 'q' is set to a value equal
            // to or smaller than that.
            a = q;
            // -- Renormalize
            do
            {
                if (cT == 0)
                    byteIn();
                a <<= 1;
                c <<= 1;
                cT--;
            }
            while (a < 0x8000);
            // -- End renormalization
            // -- End LPS Exchange

            // 7) Everything seems OK, we have checked the C register for the LPS
            // symbols and ensured that it is followed by bits synthetized by the
            // termination marker.
            return false;
        }

        /// <summary> This function gets one byte of compressed bits from the in-stream.  the
        /// byte is added to c. If the byte is 0xFF and the next byte is greater
        /// than 0x8F, the byte after 0xFF is a marker.
        /// 
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void byteIn()
        {
            if (!markerFound)
            {
                if (b == 0xFF)
                {
                    b = ((uint)inputBuffer.read()) & 0xFF; // Convert EOFs (-1) to 0xFF

                    if (b > 0x8F)
                    {
                        markerFound = true;
                        // software-convention decoder: c unchanged
                        cT = 8;
                    }
                    else
                    {
                        c += 0xFE00 - (b << 9);
                        cT = 7;
                    }
                }
                else
                {
                    b = ((uint)inputBuffer.read()) & 0xFF; // Convert EOFs (-1) to 0xFF
                    c += 0xFF00 - (b << 8);
                    cT = 8;
                }
            }
            else
            {
                // software-convention decoder: c unchanged
                cT = 8;
            }
        }

        /// <summary> Resets a context to the original probability distribution.
        /// 
        /// </summary>
        /// <param name="c">The number of the context (it starts at 0).
        /// 
        /// </param>
        public void resetCtxt(int c)
        {
            I[c] = initStates[c];
            mPS[c] = 0;
        }

        /// <summary> Resets a context to the original probability distribution. The original
        /// probability distribution depends on the actual implementation of the
        /// arithmetic coder or decoder.
        /// 
        /// </summary>
        /// <param name="c">The index of the context (it starts at 0).
        /// 
        /// </param>
        public void resetCtxts()
        {
            Array.Copy(initStates, 0, I, 0, I.Length);
            ArrayUtil.intArraySet(mPS, 0);
        }

        /// <summary> Resets the MQ decoder to start a new segment. This is like recreating a
        /// new MQDecoder object with new input data.
        /// 
        /// </summary>
        /// <param name="buf">The byte array containing the MQ encoded data. If null the
        /// current byte array is assumed.
        /// 
        /// </param>
        /// <param name="off">The index of the first element in 'buf' to be decoded. If
        /// negative the byte just after the previous segment is assumed, only
        /// valid if 'buf' is null.
        /// 
        /// </param>
        /// <param name="len">The number of bytes in 'buf' to be decoded. Any subsequent
        /// bytes are taken to be 0xFF.
        /// 
        /// </param>
        public void nextSegment(byte[]? buf, int off, int len)
        {
            // Set the new input
            inputBuffer.SetByteArray(buf, off, len);
            // Reinitialize MQ
            init();
        }

        /// <summary> Initializes the state of the MQ coder, without modifying the current
        /// context states. It sets the registers (A,C,B) and the "marker found"
        /// state to the initial state, to start the decoding of a new segment.
        /// 
        /// To have a complete reset of the MQ (as if a new MQDecoder object was
        /// created) 'resetCtxts()' should be called after this method.
        /// 
        /// </summary>
        private void init()
        {
            // --- INITDEC
            markerFound = false;

            // Read first byte
            b = ((uint)inputBuffer.read()) & 0xFF;

            // Software conventions decoder
            c = (b ^ 0xFF) << 16;
            byteIn();
            c = c << 7;
            cT = cT - 7;
            a = 0x8000;

            // End of INITDEC ---
        }
    }
}