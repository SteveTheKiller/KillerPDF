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

namespace CoreJ2K.j2k.wavelet.analysis
{

    /// <summary> This class inherits from the analysis wavelet filter definition
    /// for int data. It implements the forward wavelet transform
    /// specifically for the 9x7 filter. The implementation is based on
    /// the lifting scheme.
    /// 
    /// See the AnWTFilter class for details such as
    /// normalization, how to split odd-length signals, etc. In particular,
    /// this method assumes that the low-pass coefficient is computed first.
    /// 
    /// </summary>
    /// <seealso cref="AnWTFilter" />
    /// <seealso cref="AnWTFilterFloat" />
    public class AnWTFilterFloatLift9x7 : AnWTFilterFloat
    {
        /// <summary> Returns the negative support of the low-pass analysis
        /// filter. That is the number of taps of the filter in the
        /// negative direction.
        /// 
        /// </summary>
        /// <returns> 2
        /// 
        /// </returns>
        public override int AnLowNegSupport => 4;

        /// <summary> Returns the positive support of the low-pass analysis
        /// filter. That is the number of taps of the filter in the
        /// negative direction.
        /// 
        /// </summary>
        /// <returns> The number of taps of the low-pass analysis filter in
        /// the positive direction
        /// 
        /// </returns>
        public override int AnLowPosSupport => 4;

        /// <summary> Returns the negative support of the high-pass analysis
        /// filter. That is the number of taps of the filter in the
        /// negative direction.
        /// 
        /// </summary>
        /// <returns> The number of taps of the high-pass analysis filter in
        /// the negative direction
        /// 
        /// </returns>
        public override int AnHighNegSupport => 3;

        /// <summary> Returns the positive support of the high-pass analysis
        /// filter. That is the number of taps of the filter in the
        /// negative direction.
        /// 
        /// </summary>
        /// <returns> The number of taps of the high-pass analysis filter in
        /// the positive direction
        /// 
        /// </returns>
        public override int AnHighPosSupport => 3;

        /// <summary> Returns the negative support of the low-pass synthesis
        /// filter. That is the number of taps of the filter in the
        /// negative direction.
        /// 
        /// A MORE PRECISE DEFINITION IS NEEDED
        /// 
        /// </summary>
        /// <returns> The number of taps of the low-pass synthesis filter in
        /// the negative direction
        /// 
        /// </returns>
        public override int SynLowNegSupport => 3;

        /// <summary> Returns the positive support of the low-pass synthesis
        /// filter. That is the number of taps of the filter in the
        /// negative direction.
        /// 
        /// A MORE PRECISE DEFINITION IS NEEDED
        /// 
        /// </summary>
        /// <returns> The number of taps of the low-pass synthesis filter in
        /// the positive direction
        /// 
        /// </returns>
        public override int SynLowPosSupport => 3;

        /// <summary> Returns the negative support of the high-pass synthesis
        /// filter. That is the number of taps of the filter in the
        /// negative direction.
        /// 
        /// A MORE PRECISE DEFINITION IS NEEDED
        /// 
        /// </summary>
        /// <returns> The number of taps of the high-pass synthesis filter in
        /// the negative direction
        /// 
        /// </returns>
        public override int SynHighNegSupport => 4;

        /// <summary> Returns the positive support of the high-pass synthesis
        /// filter. That is the number of taps of the filter in the
        /// negative direction.
        /// 
        /// A MORE PRECISE DEFINITION IS NEEDED
        /// 
        /// </summary>
        /// <returns> The number of taps of the high-pass synthesis filter in
        /// the positive direction
        /// 
        /// </returns>
        public override int SynHighPosSupport => 4;

        /// <summary> Returns the implementation type of this filter, as defined in
        /// this class, such as WT_FILTER_INT_LIFT, WT_FILTER_FLOAT_LIFT,
        /// WT_FILTER_FLOAT_CONVOL.
        /// 
        /// </summary>
        /// <returns> WT_FILTER_INT_LIFT.
        /// 
        /// </returns>
        public override int ImplType => WaveletFilter_Fields.WT_FILTER_FLOAT_LIFT;

        /// <summary> Returns the reversibility of the filter. A filter is considered
        /// reversible if it is suitable for lossless coding.
        /// 
        /// </summary>
        /// <returns> true since the 9x7 is reversible, provided the appropriate
        /// rounding is performed.
        /// 
        /// </returns>
        public override bool Reversible => false;

        /// <summary> Returns the type of filter used according to the FilterTypes
        /// interface(W9x7).
        /// 
        /// </summary>
        /// <seealso cref="FilterTypes" />
        /// <returns> The filter type.
        /// 
        /// </returns>
        public override int FilterType => FilterTypes_Fields.W9X7;

        /// <summary>The low-pass synthesis filter of the 9x7 wavelet transform </summary>
        private static readonly float[] LPSynthesisFilter = { -0.091272f, -0.057544f, 0.591272f, 1.115087f, 0.591272f, -0.057544f, -0.091272f };

        /// <summary>The high-pass synthesis filter of the 9x7 wavelet transform </summary>
        private static readonly float[] HPSynthesisFilter = { 0.026749f, 0.016864f, -0.078223f, -0.266864f, 0.602949f, -0.266864f, -0.078223f, 0.016864f, 0.026749f };

        /// <summary>The value of the first lifting step coefficient </summary>
        public const float ALPHA = -1.586134342f;

        /// <summary>The value of the second lifting step coefficient </summary>
        public const float BETA = -0.05298011854f;

        /// <summary>The value of the third lifting step coefficient </summary>
        public const float GAMMA = 0.8829110762f;

        /// <summary>The value of the fourth lifting step coefficient </summary>
        public const float DELTA = 0.4435068522f;

        /// <summary>The value of the low-pass subband normalization factor </summary>
        public const float KL = 0.8128930655f;

        /// <summary>The value of the high-pass subband normalization factor </summary>
        public const float KH = 1.230174106f;

        /// <summary> An implementation of the analyze_lpf() method that works on int
        /// data, for the forward 9x7 wavelet transform using the
        /// lifting scheme. See the general description of the analyze_lpf() 
        /// method in the AnWTFilter class for more details.
        /// 
        /// The coefficients of the first lifting step are [ALPHA 1 ALPHA]. 
        /// 
        /// The coefficients of the second lifting step are [BETA 1 BETA].
        /// 
        /// The coefficients of the third lifting step are [GAMMA 1 GAMMA]. 
        /// 
        /// The coefficients of the fourth lifting step are [DELTA 1 DELTA].
        /// 
        /// The low-pass and high-pass subbands are normalized by respectively
        /// a factor of KL and a factor of KH   
        /// 
        /// </summary>
        /// <param name="inSig">This is the array that contains the input
        /// signal.
        /// 
        /// </param>
        /// <param name="inOff">This is the index in inSig of the first sample to
        /// filter.
        /// 
        /// </param>
        /// <param name="inLen">This is the number of samples in the input signal
        /// to filter.
        /// 
        /// </param>
        /// <param name="inStep">This is the step, or interleave factor, of the
        /// input signal samples in the inSig array.
        /// 
        /// </param>
        /// <param name="lowSig">This is the array where the low-pass output
        /// signal is placed.
        /// 
        /// </param>
        /// <param name="lowOff">This is the index in lowSig of the element where
        /// to put the first low-pass output sample.
        /// 
        /// </param>
        /// <param name="lowStep">This is the step, or interleave factor, of the
        /// low-pass output samples in the lowSig array.
        /// 
        /// </param>
        /// <param name="highSig">This is the array where the high-pass output
        /// signal is placed.
        /// 
        /// </param>
        /// <param name="highOff">This is the index in highSig of the element where
        /// to put the first high-pass output sample.
        /// 
        /// </param>
        /// <param name="highStep">This is the step, or interleave factor, of the
        /// high-pass output samples in the highSig array.
        /// 
        /// </param>
        public override void analyze_lpf(float[] inSig, int inOff, int inLen, int inStep, float[] lowSig, int lowOff, int lowStep, float[] highSig, int highOff, int highStep)
        {
            int i, maxi;
            var iStep = 2 * inStep; //Subsampling in inSig
            int ik; //Indexing inSig
            int lk; //Indexing lowSig
            int hk; //Indexing highSig

            // Generate intermediate high frequency subband

            //Initialize counters
            ik = inOff + inStep;
            lk = lowOff;
            hk = highOff;

            //Apply first lifting step to each "inner" sample
            for (i = 1, maxi = inLen - 1; i < maxi; i += 2)
            {
                highSig[hk] = inSig[ik] + ALPHA * (inSig[ik - inStep] + inSig[ik + inStep]);

                ik += iStep;
                hk += highStep;
            }

            //Handle head boundary effect if input signal has even length
            if (inLen % 2 == 0)
            {
                highSig[hk] = inSig[ik] + 2 * ALPHA * inSig[ik - inStep];
            }

            // Generate intermediate low frequency subband

            //Initialize counters
            ik = inOff;
            lk = lowOff;
            hk = highOff;

            if (inLen > 1)
            {
                lowSig[lk] = inSig[ik] + 2 * BETA * highSig[hk];
            }
            else
            {
                lowSig[lk] = inSig[ik];
            }

            ik += iStep;
            lk += lowStep;
            hk += highStep;

            //Apply lifting step to each "inner" sample
            for (i = 2, maxi = inLen - 1; i < maxi; i += 2)
            {
                lowSig[lk] = inSig[ik] + BETA * (highSig[hk - highStep] + highSig[hk]);

                ik += iStep;
                lk += lowStep;
                hk += highStep;
            }

            //Handle head boundary effect if input signal has odd length
            if ((inLen % 2 == 1) && (inLen > 2))
            {
                lowSig[lk] = inSig[ik] + 2 * BETA * highSig[hk - highStep];
            }

            // Generate high frequency subband

            //Initialize counters
            lk = lowOff;
            hk = highOff;

            //Apply first lifting step to each "inner" sample
            for (i = 1, maxi = inLen - 1; i < maxi; i += 2)
            {
                highSig[hk] += GAMMA * (lowSig[lk] + lowSig[lk + lowStep]);

                lk += lowStep;
                hk += highStep;
            }

            //Handle head boundary effect if input signal has even length
            if (inLen % 2 == 0)
            {
                highSig[hk] += 2 * GAMMA * lowSig[lk];
            }

            // Generate low frequency subband

            //Initialize counters
            lk = lowOff;
            hk = highOff;

            //Handle tail boundary effect
            //If access the overlap then perform the lifting step
            if (inLen > 1)
            {
                lowSig[lk] += 2 * DELTA * highSig[hk];
            }

            lk += lowStep;
            hk += highStep;

            //Apply lifting step to each "inner" sample
            for (i = 2, maxi = inLen - 1; i < maxi; i += 2)
            {
                lowSig[lk] += DELTA * (highSig[hk - highStep] + highSig[hk]);

                lk += lowStep;
                hk += highStep;
            }

            //Handle head boundary effect if input signal has odd length
            if ((inLen % 2 == 1) && (inLen > 2))
            {
                lowSig[lk] += 2 * DELTA * highSig[hk - highStep];
            }

            // Normalize low and high frequency subbands

            //Re-initialize counters
            lk = lowOff;
            hk = highOff;

            //Normalize each sample
            for (i = 0; i < (inLen >> 1); i++)
            {
                lowSig[lk] *= KL;
                highSig[hk] *= KH;
                lk += lowStep;
                hk += highStep;
            }
            //If the input signal has odd length then normalize the last low-pass
            //coefficient (if input signal is length one filter is identity)
            if (inLen % 2 == 1 && inLen != 1)
            {
                lowSig[lk] *= KL;
            }
        }

        /// <summary> An implementation of the analyze_hpf() method that works on int
        /// data, for the forward 9x7 wavelet transform using the
        /// lifting scheme. See the general description of the analyze_hpf() method 
        /// in the AnWTFilter class for more details.
        /// 
        /// The coefficients of the first lifting step are [ALPHA 1 ALPHA]. 
        /// 
        /// The coefficients of the second lifting step are [BETA 1 BETA].
        /// 
        /// The coefficients of the third lifting step are [GAMMA 1 GAMMA]. 
        /// 
        /// The coefficients of the fourth lifting step are [DELTA 1 DELTA].
        /// 
        /// The low-pass and high-pass subbands are normalized by respectively
        /// a factor of KL and a factor of KH   
        /// 
        /// </summary>
        /// <param name="inSig">This is the array that contains the input
        /// signal.
        /// 
        /// </param>
        /// <param name="inOff">This is the index in inSig of the first sample to
        /// filter.
        /// 
        /// </param>
        /// <param name="inLen">This is the number of samples in the input signal
        /// to filter.
        /// 
        /// </param>
        /// <param name="inStep">This is the step, or interleave factor, of the
        /// input signal samples in the inSig array.
        /// 
        /// </param>
        /// <param name="lowSig">This is the array where the low-pass output
        /// signal is placed.
        /// 
        /// </param>
        /// <param name="lowOff">This is the index in lowSig of the element where
        /// to put the first low-pass output sample.
        /// 
        /// </param>
        /// <param name="lowStep">This is the step, or interleave factor, of the
        /// low-pass output samples in the lowSig array.
        /// 
        /// </param>
        /// <param name="highSig">This is the array where the high-pass output
        /// signal is placed.
        /// 
        /// </param>
        /// <param name="highOff">This is the index in highSig of the element where
        /// to put the first high-pass output sample.
        /// 
        /// </param>
        /// <param name="highStep">This is the step, or interleave factor, of the
        /// high-pass output samples in the highSig array.
        /// 
        /// </param>
        /// <seealso cref="AnWTFilter.analyze_hpf" />
        public override void analyze_hpf(float[] inSig, int inOff, int inLen, int inStep, float[] lowSig, int lowOff, int lowStep, float[] highSig, int highOff, int highStep)
        {

            int i; // maxi removed
            var iStep = 2 * inStep; //Subsampling in inSig
            int ik; //Indexing inSig
            int lk; //Indexing lowSig
            int hk; //Indexing highSig

            // Generate intermediate high frequency subband

            //Initialize counters
            ik = inOff;
            lk = lowOff;
            hk = highOff;

            if (inLen > 1)
            {
                // apply symmetric extension.
                highSig[hk] = inSig[ik] + 2 * ALPHA * inSig[ik + inStep];
            }
            else
            {
                // Normalize for Nyquist gain
                highSig[hk] = inSig[ik] * 2;
            }

            ik += iStep;
            hk += highStep;

            //Apply first lifting step to each "inner" sample
            for (i = 2; i < inLen - 1; i += 2)
            {
                highSig[hk] = inSig[ik] + ALPHA * (inSig[ik - inStep] + inSig[ik + inStep]);
                ik += iStep;
                hk += highStep;
            }

            //If input signal has odd length then we perform the lifting step
            // i.e. apply a symmetric extension.
            if ((inLen % 2 == 1) && (inLen > 1))
            {
                highSig[hk] = inSig[ik] + 2 * ALPHA * inSig[ik - inStep];
            }

            // Generate intermediate low frequency subband

            //Initialize counters
            //ik = inOff + inStep;
            ik = inOff + inStep;
            lk = lowOff;
            hk = highOff;

            //Apply lifting step to each "inner" sample
            // we are at the component boundary
            for (i = 1; i < inLen - 1; i += 2)
            {
                lowSig[lk] = inSig[ik] + BETA * (highSig[hk] + highSig[hk + highStep]);

                ik += iStep;
                lk += lowStep;
                hk += highStep;
            }
            if (inLen > 1 && inLen % 2 == 0)
            {
                // symetric extension
                lowSig[lk] = inSig[ik] + 2 * BETA * highSig[hk];
            }

            // Generate high frequency subband

            //Initialize counters
            lk = lowOff;
            hk = highOff;

            if (inLen > 1)
            {
                // symmetric extension.
                highSig[hk] += GAMMA * 2 * lowSig[lk];
            }
            //lk += lowStep;   
            hk += highStep;

            //Apply first lifting step to each "inner" sample
            for (i = 2; i < inLen - 1; i += 2)
            {
                highSig[hk] += GAMMA * (lowSig[lk] + lowSig[lk + lowStep]);
                lk += lowStep;
                hk += highStep;
            }

            //Handle head boundary effect
            if (inLen > 1 && inLen % 2 == 1)
            {
                // symmetric extension.
                highSig[hk] += GAMMA * 2 * lowSig[lk];
            }

            // Generate low frequency subband

            //Initialize counters
            lk = lowOff;
            hk = highOff;

            // we are at the component boundary
            for (i = 1; i < inLen - 1; i += 2)
            {
                lowSig[lk] += DELTA * (highSig[hk] + highSig[hk + highStep]);
                lk += lowStep;
                hk += highStep;
            }

            if (inLen > 1 && inLen % 2 == 0)
            {
                lowSig[lk] += DELTA * 2 * highSig[hk];
            }

            // Normalize low and high frequency subbands

            //Re-initialize counters
            lk = lowOff;
            hk = highOff;

            //Normalize each sample
            for (i = 0; i < (inLen >> 1); i++)
            {
                lowSig[lk] *= KL;
                highSig[hk] *= KH;
                lk += lowStep;
                hk += highStep;
            }
            //If the input signal has odd length then normalize the last high-pass
            //coefficient (if input signal is length one filter is identity)
            if (inLen % 2 == 1 && inLen != 1)
            {
                highSig[hk] *= KH;
            }
        }

        /// <summary> Returns the time-reversed low-pass synthesis waveform of the
        /// filter, which is the low-pass filter. This is the time-reversed
        /// impulse response of the low-pass synthesis filter. It is used
        /// to calculate the L2-norm of the synthesis basis functions for a
        /// particular subband (also called energy weight).
        /// 
        /// The returned array may not be modified (i.e. a reference to
        /// the internal array may be returned by the implementation of
        /// this method).
        /// 
        /// </summary>
        /// <returns> The time-reversed low-pass synthesis waveform of the
        /// filter.
        /// 
        /// </returns>
        public override float[] GetLPSynthesisFilter()
        {
            return LPSynthesisFilter;
        }

        /// <summary> Returns the time-reversed high-pass synthesis waveform of the
        /// filter, which is the high-pass filter. This is the
        /// time-reversed impulse response of the high-pass synthesis
        /// filter. It is used to calculate the L2-norm of the synthesis
        /// basis functions for a particular subband (also called energy
        /// weight).
        /// 
        /// The returned array may not be modified (i.e. a reference to
        /// the internal array may be returned by the implementation of
        /// this method).
        /// 
        /// </summary>
        /// <returns> The time-reversed high-pass synthesis waveform of the
        /// filter.
        /// 
        /// </returns>
        public override float[] GetHPSynthesisFilter()
        {
            return HPSynthesisFilter;
        }

        /// <summary> Returns true if the wavelet filter computes or uses the
        /// same "inner" subband coefficient as the full frame wavelet transform,
        /// and false otherwise. In particular, for block based transforms with 
        /// reduced overlap, this method should return false. The term "inner"
        /// indicates that this applies only with respect to the coefficient that 
        /// are not affected by image boundaries processings such as symmetric
        /// extension, since there is not reference method for this.
        /// 
        /// The result depends on the length of the allowed overlap when
        /// compared to the overlap required by the wavelet filter. It also
        /// depends on how overlap processing is implemented in the wavelet
        /// filter.
        /// 
        /// </summary>
        /// <param name="tailOvrlp">This is the number of samples in the input
        /// signal before the first sample to filter that can be used for
        /// overlap.
        /// 
        /// </param>
        /// <param name="headOvrlp">This is the number of samples in the input
        /// signal after the last sample to filter that can be used for
        /// overlap.
        /// 
        /// </param>
        /// <param name="inLen">This is the lenght of the input signal to filter.The
        /// required number of samples in the input signal after the last sample
        /// depends on the length of the input signal.
        /// 
        /// </param>
        /// <returns> true if both overlaps are greater than 2, and correct 
        /// processing is applied in the analyze() method.
        /// 
        /// </returns>
        public override bool IsSameAsFullWT(int tailOvrlp, int headOvrlp, int inLen)
        {

            //If the input signal has even length.
            if (inLen % 2 == 0)
            {
                return tailOvrlp >= 4 && headOvrlp >= 3;
            }
            //Else if the input signal has odd length.
            else
            {
                return tailOvrlp >= 4 && headOvrlp >= 4;
            }
        }

        /// <summary> Tests if the 'obj' object is the same filter as this one. Two filters
        /// are the same if the same filter code should be output for both filters
        /// by the encodeFilterCode() method.
        /// 
        /// Currently the implementation of this method only tests if 'obj' is
        /// also of the class AnWTFilterFloatLift9x7
        /// 
        /// </summary>
        /// <param name="The">object against which to test inequality.
        /// 
        /// </param>
        public override bool Equals(object? obj)
        {
            // To speed up test, first test for reference equality
            return obj == this || obj is AnWTFilterFloatLift9x7;
        }

        /// <summary>Debugging method </summary>
        public override string ToString()
        {
            return "w9x7";
        }
        public override int GetHashCode()
        {
            return base.GetHashCode();
        }
    }
}