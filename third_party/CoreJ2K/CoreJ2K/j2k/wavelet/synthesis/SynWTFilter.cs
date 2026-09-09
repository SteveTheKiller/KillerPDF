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

namespace CoreJ2K.j2k.wavelet.synthesis
{

    /// <summary> This abstract class defines the methods of all synthesis wavelet
    /// filters. Specialized abstract classes that work on particular data types
    /// (int, float) provide more specific method calls while retaining the
    /// generality of this one. See the SynWTFilterInt and SynWTFilterFloat
    /// classes. Implementations of snythesis filters should inherit from one of
    /// those classes.
    /// 
    /// The length of the output signal is always the sum of the length of the
    /// low-pass and high-pass input signals.
    /// 
    /// All synthesis wavelet filters should follow the following conventions:
    /// 
    /// <ul> 
    /// 
    /// <li>The first sample of the output corresponds to the low-pass one. As a
    /// consequence, if the output signal is of odd-length then the low-pass input
    /// signal is one sample longer than the high-pass input one. Therefore, if the
    /// length of output signal is N, the low-pass input signal is of length N/2 if
    /// N is even and N/2+1/2 if N is odd, while the high-pass input signal is of
    /// length N/2 if N is even and N/2-1/2 if N is odd.</li>
    /// 
    /// <li>The normalization of the analysis filters is 1 for the DC gain and 2
    /// for the Nyquist gain (Type I normalization), for both reversible and
    /// non-reversible filters. The normalization of the synthesis filters should
    /// ensure prefect reconstruction according to this normalization of the
    /// analysis wavelet filters.</li>
    /// 
    /// </ul>
    /// 
    /// The synthetize method may seem very complicated, but is designed to
    /// minimize the amount of data copying and redundant calculations when used
    /// for block-based or line-based wavelet transform implementations, while
    /// being applicable to full-frame transforms as well.
    /// 
    /// </summary>
    /// <seealso cref="SynWTFilterInt" />
    /// <seealso cref="SynWTFilterFloat" />
    public abstract class SynWTFilter : WaveletFilter
    {
        public abstract int AnHighPosSupport { get; }
        public abstract int AnLowNegSupport { get; }
        public abstract int AnLowPosSupport { get; }
        public abstract bool Reversible { get; }
        public abstract int ImplType { get; }
        public abstract int SynHighNegSupport { get; }
        public abstract int SynHighPosSupport { get; }
        public abstract int AnHighNegSupport { get; }
        public abstract int DataType { get; }
        public abstract int SynLowNegSupport { get; }
        public abstract int SynLowPosSupport { get; }

        /// <summary> Reconstructs the output signal by the synthesis filter, recomposing the
        /// low-pass and high-pass input signals in one output signal. This method
        /// performs the upsampling and fitering with the low pass first filtering
        /// convention.
        /// 
        /// The input low-pass (high-pass) signal resides in the lowSig
        /// array. The index of the first sample to filter (i.e. that will generate
        /// the first (second) output sample). is given by lowOff (highOff). This
        /// array must be of the same type as the one for which the particular
        /// implementation works with (which is returned by the GetDataType()
        /// method).
        /// 
        /// The low-pass (high-pass) input signal can be interleaved with other
        /// signals in the same lowSig (highSig) array, and this is determined by
        /// the lowStep (highStep) argument. This means that the first sample of
        /// the low-pass (high-pass) input signal is lowSig[lowOff]
        /// (highSig[highOff]), the second is lowSig[lowOff+lowStep]
        /// (highSig[highOff+highStep]), the third is lowSig[lowOff+2*lowStep]
        /// (highSig[highOff+2*highStep]), and so on. Therefore if lowStep
        /// (highStep) is 1 there is no interleaving. This feature allows to filter
        /// columns of a 2-D signal, when it is stored in a line by line order in
        /// lowSig (highSig), without having to copy the data, in this case the
        /// lowStep (highStep) argument should be the line width of the low-pass
        /// (high-pass) signal.
        /// 
        /// The output signal is placed in the outSig array. The outOff and
        /// outStep arguments are analogous to the lowOff and lowStep ones, but
        /// they apply to the outSig array. The outSig array must be long enough to
        /// hold the low-pass output signal.
        /// 
        /// </summary>
        /// <param name="lowSig">This is the array that contains the low-pass input
        /// signal. It must be of the correct type (e.g., it must be int[] if
        /// GetDataType() returns TYPE_INT).
        /// 
        /// </param>
        /// <param name="lowOff">This is the index in lowSig of the first sample to
        /// filter.
        /// 
        /// </param>
        /// <param name="lowLen">This is the number of samples in the low-pass input
        /// signal to filter.
        /// 
        /// </param>
        /// <param name="lowStep">This is the step, or interleave factor, of the low-pass
        /// input signal samples in the lowSig array. See above.
        /// 
        /// </param>
        /// <param name="highSig">This is the array that contains the high-pass input
        /// signal. It must be of the correct type (e.g., it must be int[] if
        /// GetDataType() returns TYPE_INT).
        /// 
        /// </param>
        /// <param name="highOff">This is the index in highSig of the first sample to
        /// filter.
        /// 
        /// </param>
        /// <param name="highLen">This is the number of samples in the high-pass input
        /// signal to filter.
        /// 
        /// </param>
        /// <param name="highStep">This is the step, or interleave factor, of the
        /// high-pass input signal samples in the highSig array. See above.
        /// 
        /// </param>
        /// <param name="outSig">This is the array where the output signal is placed. It
        /// must be of the same type as lowSig and it should be long enough to
        /// contain the output signal.
        /// 
        /// </param>
        /// <param name="outOff">This is the index in outSig of the element where to put
        /// the first output sample.
        /// 
        /// </param>
        /// <param name="outStep">This is the step, or interleave factor, of the output
        /// samples in the outSig array. See above.
        /// 
        /// </param>
        public abstract void synthetize_lpf(object? lowSig, int lowOff, int lowLen, int lowStep, object? highSig, int highOff, int highLen, int highStep, object? outSig, int outOff, int outStep);

        /// <summary> Reconstructs the output signal by the synthesis filter, recomposing the
        /// low-pass and high-pass input signals in one output signal. This method
        /// performs the upsampling and fitering with the high pass first filtering
        /// convention.
        /// 
        /// The input low-pass (high-pass) signal resides in the lowSig
        /// array. The index of the first sample to filter (i.e. that will generate
        /// the first (second) output sample). is given by lowOff (highOff). This
        /// array must be of the same type as the one for which the particular
        /// implementation works with (which is returned by the GetDataType()
        /// method).
        /// 
        /// The low-pass (high-pass) input signal can be interleaved with other
        /// signals in the same lowSig (highSig) array, and this is determined by
        /// the lowStep (highStep) argument. This means that the first sample of
        /// the low-pass (high-pass) input signal is lowSig[lowOff]
        /// (highSig[highOff]), the second is lowSig[lowOff+lowStep]
        /// (highSig[highOff+highStep]), the third is lowSig[lowOff+2*lowStep]
        /// (highSig[highOff+2*highStep]), and so on. Therefore if lowStep
        /// (highStep) is 1 there is no interleaving. This feature allows to filter
        /// columns of a 2-D signal, when it is stored in a line by line order in
        /// lowSig (highSig), without having to copy the data, in this case the
        /// lowStep (highStep) argument should be the line width of the low-pass
        /// (high-pass) signal.
        /// 
        /// The output signal is placed in the outSig array. The outOff and
        /// outStep arguments are analogous to the lowOff and lowStep ones, but
        /// they apply to the outSig array. The outSig array must be long enough to
        /// hold the low-pass output signal.
        /// 
        /// </summary>
        /// <param name="lowSig">This is the array that contains the low-pass input
        /// signal. It must be of the correct type (e.g., it must be int[] if
        /// GetDataType() returns TYPE_INT).
        /// 
        /// </param>
        /// <param name="lowOff">This is the index in lowSig of the first sample to
        /// filter.
        /// 
        /// </param>
        /// <param name="lowLen">This is the number of samples in the low-pass input
        /// signal to filter.
        /// 
        /// </param>
        /// <param name="lowStep">This is the step, or interleave factor, of the low-pass
        /// input signal samples in the lowSig array. See above.
        /// 
        /// </param>
        /// <param name="highSig">This is the array that contains the high-pass input
        /// signal. It must be of the correct type (e.g., it must be int[] if
        /// GetDataType() returns TYPE_INT).
        /// 
        /// </param>
        /// <param name="highOff">This is the index in highSig of the first sample to
        /// filter.
        /// 
        /// </param>
        /// <param name="highLen">This is the number of samples in the high-pass input
        /// signal to filter.
        /// 
        /// </param>
        /// <param name="highStep">This is the step, or interleave factor, of the
        /// high-pass input signal samples in the highSig array. See above.
        /// 
        /// </param>
        /// <param name="outSig">This is the array where the output signal is placed. It
        /// must be of the same type as lowSig and it should be long enough to
        /// contain the output signal.
        /// 
        /// </param>
        /// <param name="outOff">This is the index in outSig of the element where to put
        /// the first output sample.
        /// 
        /// </param>
        /// <param name="outStep">This is the step, or interleave factor, of the output
        /// samples in the outSig array. See above.
        /// 
        /// </param>
        public abstract void synthetize_hpf(object? lowSig, int lowOff, int lowLen, int lowStep, object? highSig, int highOff, int highLen, int highStep, object? outSig, int outOff, int outStep);
        public abstract bool IsSameAsFullWT(int param1, int param2, int param3);
    }
}