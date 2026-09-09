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

    /// <summary> This abstract class defines the methods of all analysis wavelet
    /// filters. Specialized abstract classes that work on particular data types
    /// (int, float) provide more specific method calls while retaining the
    /// generality of this one. See the AnWTFilterInt and AnWTFilterFloat
    /// classes. Implementations of analysis filters should inherit from one of
    /// those classes.
    /// 
    /// All analysis wavelet filters should follow the following conventions:
    /// 
    /// <ul>
    /// <li>The first sample to filter is the low-pass one. As a consequence, if
    /// the input signal is of odd-length then the low-pass output signal is one
    /// sample longer than the high-pass output one. Therefore, if the length of
    /// input signal is N, the low-pass output signal is of length N/2 if N is even
    /// and N/2+1/2 if N is odd, while the high-pass output signal is of length N/2
    /// if N is even and N/2-1/2 if N is odd.</li>
    /// 
    /// <li>The normalization is 1 for the DC gain and 2 for the Nyquist gain (Type
    /// I normalization), for both reversible and non-reversible filters.</li>
    /// 
    /// <li>If the length of input signal is N, the low-pass output signal is of
    /// length N/2 if N is even and N/2+1/2 if N is odd, while the high-pass output
    /// sample is of length N/2 if N is even and N/2-1/2 if N is odd.</li>
    /// 
    /// <li>The analyze method may seem very complicated, but is designed to
    /// minimize the amount of data copying and redundant calculations when used
    /// for block-based or line-based wavelet transform implementations, while
    /// being applicable to full-frame transforms as well.</li>
    /// 
    /// <li>All filters should implement the equals() method of the Object
    /// class. The call x.equals(y) should test if the 'x' and 'y' filters are the
    /// same or not, in what concerns the bit stream header syntax (two filters are
    /// the same if the same filter code should be output to the bit stream).</li>
    /// </ul>
    /// 
    /// </summary>
    /// <seealso cref="AnWTFilterInt" />
    /// <seealso cref="AnWTFilterFloat" />
    public abstract class AnWTFilter : WaveletFilter
    {
        /// <summary> Returns the type of filter used according to the FilterTypes interface.
        /// 
        /// </summary>
        /// <seealso cref="FilterTypes" />
        /// <returns> The filter type.
        /// 
        /// </returns>
        public abstract int FilterType { get; }
        /// <summary> Returns the parameters that are used in this class and implementing
        /// classes. It returns a 2D String array. Each of the 1D arrays is for a
        /// different option, and they have 3 elements. The first element is the
        /// option name, the second one is the synopsis, the third one is a long
        /// description of what the parameter is and the fourth is its default
        /// value. The synopsis or description may be 'null', in which case it is
        /// assumed that there is no synopsis or description of the option,
        /// respectively. Null may be returned if no options are supported.
        /// 
        /// </summary>
        /// <returns> the options name, their synopsis and their explanation, or null
        /// if no options are supported.
        /// 
        /// </returns>
        public static string[][] ParameterInfo => pinfo;

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

        /// <summary>The prefix for wavelet filter options: 'F' </summary>
        public const char OPT_PREFIX = 'F';

        /// <summary>The list of parameters that is accepted for wavelet filters. Options
        /// for wavelet filters start with a 'F'. 
        /// </summary>
        private static readonly string[][] pinfo = { new string[] { "Ffilters", "[<tile-component idx>] <id> " + "[ [<tile-component idx>] <id> ...]", "Specifies which filters to use for specified tile-component. " + "If this option is not used, the encoder choses the filters " + " of the tile-components according to their quantization  type." + " If this option is used, a component transformation is applied " + "to the three first components.\n" + "<tile-component idx>: see general note\n" + "<id>: ',' separates horizontal and vertical filters, ':' separates" + " decomposition levels filters. JPEG 2000 part 1 only supports w5x3" + " and w9x7 filters.", null } };

        /// <summary> Filters the input signal by this analysis filter, decomposing it in a
        /// low-pass and a high-pass signal. This method performs the filtering and
        /// the subsampling with the low pass first filtering convention.
        /// 
        /// The input signal resides in the inSig array. The index of the first
        /// sample to filter (i.e. that will generate the first low-pass output
        /// sample) is given by inOff. The number of samples to filter is given by
        /// inLen. This array must be of the same type as the one for which the
        /// particular implementation works with (which is returned by the
        /// GetDataType() method).
        /// 
        /// The input signal can be interleaved with other signals in the same
        /// inSig array, and this is determined by the inStep argument. This means
        /// that the first sample of the input signal is inSig[inOff], the second
        /// is inSig[inOff+inStep], the third is inSig[inOff+2*inStep], and so
        /// on. Therefore if inStep is 1 there is no interleaving. This feature
        /// allows to filter columns of a 2-D signal, when it is stored in a line
        /// by line order in inSig, without having to copy the data, in this case
        /// the inStep argument should be the line width.
        /// 
        /// This method also allows to apply the analysis wavelet filter by
        /// parts in the input signal using an overlap and thus producing the same
        /// coefficients at the output. The tailOvrlp argument specifies how many
        /// samples in the input signal, before the first one to be filtered, can
        /// be used for overlap. Then, the filter instead of extending the input
        /// signal will use those samples to calculate the first output
        /// samples. The argument tailOvrlp can be 0 for no overlap, or some value
        /// that provides partial or full overlap. There should be enough samples
        /// in the input signal, before the first sample to be filtered, to support
        /// the overlap. The headOvrlp provides the same functionality but at the
        /// end of the input signal. The inStep argument also applies to samples
        /// used for overlap. This overlap feature can be used for line-based
        /// wavelet transforms (in which case it will only be used when filtering
        /// the columns) or for overlapping block-based wavelet transforms (in
        /// which case it will be used when filtering lines and columns).
        /// 
        /// The low-pass output signal is placed in the lowSig array. The lowOff
        /// and lowStep arguments are analogous to the inOff and inStep ones, but
        /// they apply to the lowSig array. The lowSig array must be long enough to
        /// hold the low-pass output signal.
        /// 
        /// The high-pass output signal is placed in the highSig array. The
        /// highOff and highStep arguments are analogous to the inOff and inStep
        /// ones, but they apply to the highSig array. The highSig array must be
        /// long enough to hold the high-pass output signal.
        /// 
        /// </summary>
        /// <param name="inSig">This is the array that contains the input signal. It must
        /// be of the correct type (e.g., it must be int[] if GetDataType() returns
        /// TYPE_INT).
        /// 
        /// </param>
        /// <param name="inOff">This is the index in inSig of the first sample to filter.
        /// 
        /// </param>
        /// <param name="inLen">This is the number of samples in the input signal to
        /// filter.
        /// 
        /// </param>
        /// <param name="inStep">This is the step, or interleave factor, of the input
        /// signal samples in the inSig array. See above.
        /// 
        /// </param>
        /// <param name="tailOvrlp">This is the number of samples in the input signal
        /// before the first sample to filter that can be used for overlap. See
        /// above.
        /// 
        /// </param>
        /// <param name="headOvrlp">This is the number of samples in the input signal
        /// after the last sample to filter that can be used for overlap. See
        /// above.
        /// 
        /// </param>
        /// <param name="lowSig">This is the array where the low-pass output signal is
        /// placed. It must be of the same type as inSig and it should be long
        /// enough to contain the output signal.
        /// 
        /// </param>
        /// <param name="lowOff">This is the index in lowSig of the element where to put
        /// the first low-pass output sample.
        /// 
        /// </param>
        /// <param name="lowStep">This is the step, or interleave factor, of the low-pass
        /// output samples in the lowSig array. See above.
        /// 
        /// </param>
        /// <param name="highSig">This is the array where the high-pass output signal is
        /// placed. It must be of the same type as inSig and it should be long
        /// enough to contain the output signal.
        /// 
        /// </param>
        /// <param name="highOff">This is the index in highSig of the element where to put
        /// the first high-pass output sample.
        /// 
        /// </param>
        /// <param name="highStep">This is the step, or interleave factor, of the
        /// high-pass output samples in the highSig array. See above.
        /// 
        /// </param>
        /// <seealso cref="WaveletFilter.GetDataType" />
        public abstract void analyze_lpf(object inSig, int inOff, int inLen, int inStep, object lowSig, int lowOff, int lowStep, object highSig, int highOff, int highStep);

        /// <summary> Filters the input signal by this analysis filter, decomposing it in a
        /// low-pass and a high-pass signal. This method performs the filtering and
        /// the subsampling with the high pass first filtering convention.
        /// 
        /// The input signal resides in the inSig array. The index of the first
        /// sample to filter (i.e. that will generate the first high-pass output
        /// sample) is given by inOff. The number of samples to filter is given by
        /// inLen. This array must be of the same type as the one for which the
        /// particular implementation works with (which is returned by the
        /// GetDataType() method).
        /// 
        /// The input signal can be interleaved with other signals in the same
        /// inSig array, and this is determined by the inStep argument. This means
        /// that the first sample of the input signal is inSig[inOff], the second
        /// is inSig[inOff+inStep], the third is inSig[inOff+2*inStep], and so
        /// on. Therefore if inStep is 1 there is no interleaving. This feature
        /// allows to filter columns of a 2-D signal, when it is stored in a line
        /// by line order in inSig, without having to copy the data, in this case
        /// the inStep argument should be the line width.
        /// 
        /// The low-pass output signal is placed in the lowSig array. The lowOff
        /// and lowStep arguments are analogous to the inOff and inStep ones, but
        /// they apply to the lowSig array. The lowSig array must be long enough to
        /// hold the low-pass output signal.
        /// 
        /// The high-pass output signal is placed in the highSig array. The
        /// highOff and highStep arguments are analogous to the inOff and inStep
        /// ones, but they apply to the highSig array. The highSig array must be
        /// long enough to hold the high-pass output signal.
        /// 
        /// </summary>
        /// <param name="inSig">This is the array that contains the input signal. It must
        /// be of the correct type (e.g., it must be int[] if GetDataType() returns
        /// TYPE_INT).
        /// 
        /// </param>
        /// <param name="inOff">This is the index in inSig of the first sample to filter.
        /// 
        /// </param>
        /// <param name="inLen">This is the number of samples in the input signal to
        /// filter.
        /// 
        /// </param>
        /// <param name="inStep">This is the step, or interleave factor, of the input
        /// signal samples in the inSig array. See above.
        /// 
        /// </param>
        /// <param name="lowSig">This is the array where the low-pass output signal is
        /// placed. It must be of the same type as inSig and it should be long
        /// enough to contain the output signal.
        /// 
        /// </param>
        /// <param name="lowOff">This is the index in lowSig of the element where to put
        /// the first low-pass output sample.
        /// 
        /// </param>
        /// <param name="lowStep">This is the step, or interleave factor, of the low-pass
        /// output samples in the lowSig array. See above.
        /// 
        /// </param>
        /// <param name="highSig">This is the array where the high-pass output signal is
        /// placed. It must be of the same type as inSig and it should be long
        /// enough to contain the output signal.
        /// 
        /// </param>
        /// <param name="highOff">This is the index in highSig of the element where to put
        /// the first high-pass output sample.
        /// 
        /// </param>
        /// <param name="highStep">This is the step, or interleave factor, of the
        /// high-pass output samples in the highSig array. See above.
        /// 
        /// </param>
        /// <seealso cref="WaveletFilter.GetDataType" />
        public abstract void analyze_hpf(object inSig, int inOff, int inLen, int inStep, object lowSig, int lowOff, int lowStep, object highSig, int highOff, int highStep);

        /// <summary> Returns the time-reversed low-pass synthesis waveform of the filter,
        /// which is the low-pass filter. This is the time-reversed impulse
        /// response of the low-pass synthesis filter. It is used to calculate the
        /// L2-norm of the synthesis basis functions for a particular subband (also
        /// called energy weight).
        /// 
        /// The returned array may not be modified (i.e. a reference to the
        /// internal array may be returned by the implementation of this
        /// method).
        /// 
        /// </summary>
        /// <returns> The time-reversed low-pass synthesis waveform of the filter.
        /// 
        /// </returns>
        public abstract float[] GetLPSynthesisFilter();

        /// <summary> Returns the time-reversed high-pass synthesis waveform of the filter,
        /// which is the high-pass filter. This is the time-reversed impulse
        /// response of the high-pass synthesis filter. It is used to calculate the
        /// L2-norm of the synthesis basis functions for a particular subband (also
        /// called energy weight).
        /// 
        /// The returned array may not be modified (i.e. a reference to the
        /// internal array may be returned by the implementation of this
        /// method).
        /// 
        /// </summary>
        /// <returns> The time-reversed high-pass synthesis waveform of the filter.
        /// 
        /// </returns>
        public abstract float[] GetHPSynthesisFilter();

        /// <summary> Returns the equivalent low-pass synthesis waveform of a cascade of
        /// filters, given the syhthesis waveform of the previous stage. This is
        /// the result of upsampling 'in' by 2, and concolving it with the low-pass
        /// synthesis waveform of the filter. The length of the returned signal is
        /// 2*in_l+lp_l-2, where in_l is the length of 'in' and 'lp_l' is the
        /// lengthg of the low-pass synthesis filter.
        /// 
        /// The length of the low-pass synthesis filter is
        /// getSynLowNegSupport()+getSynLowPosSupport().
        /// 
        /// </summary>
        /// <param name="in">The synthesis waveform of the previous stage.
        /// 
        /// </param>
        /// <param name="out">If non-null this array is used to store the resulting
        /// signal. It must be long enough, or an IndexOutOfBoundsException is
        /// thrown.
        /// 
        /// </param>
        /// <seealso cref="getSynLowNegSupport">
        /// </seealso>
        /// <seealso cref="getSynLowPosSupport" />
        public virtual float[] GetLPSynWaveForm(float[]? input, float[]? output)
        {
            return upsampleAndConvolve(input, GetLPSynthesisFilter(), output);
        }

        /// <summary> Returns the equivalent high-pass synthesis waveform of a cascade of
        /// filters, given the syhthesis waveform of the previous stage. This is
        /// the result of upsampling 'in' by 2, and concolving it with the
        /// high-pass synthesis waveform of the filter. The length of the returned
        /// signal is 2*in_l+hp_l-2, where in_l is the length of 'in' and 'hp_l' is
        /// the lengthg of the high-pass synthesis filter.
        /// 
        /// The length of the high-pass synthesis filter is
        /// getSynHighNegSupport()+getSynHighPosSupport().
        /// 
        /// </summary>
        /// <param name="in">The synthesis waveform of the previous stage.
        /// 
        /// </param>
        /// <param name="out">If non-null this array is used to store the resulting
        /// signal. It must be long enough, or an IndexOutOfBoundsException is
        /// thrown.
        /// 
        /// </param>
        /// <seealso cref="getSynHighNegSupport">
        /// </seealso>
        /// <seealso cref="getSynHighPosSupport" />
        public virtual float[] GetHPSynWaveForm(float[]? input, float[]? output)
        {
            return upsampleAndConvolve(input, GetHPSynthesisFilter(), output);
        }

        /// <summary> Returns the signal resulting of upsampling (by 2) the input signal 'in'
        /// and then convolving it with the time-reversed signal 'wf'. The returned
        /// signal is of length l_in*2+l_wf-2, where l_in is the length of 'in',
        /// and l_wf is the length of 'wf'.
        /// 
        /// The 'wf' signal has to be already time-reversed, therefore only a
        /// dot-product is performed (instead of a convolution). This is equivalent
        /// to convolving with the non-time-reversed 'wf' signal.
        /// 
        /// </summary>
        /// <param name="in">The signal to upsample and filter. If null it is considered
        /// to be a dirac.
        /// 
        /// </param>
        /// <param name="wf">The time-reversed impulse response used for filtering.
        /// 
        /// </param>
        /// <param name="out">If non-null this array is used to store the resulting
        /// signal, it must be of length in.length*2+wf.length-2 at least. An
        /// IndexOutOfBoundsException is thrown if this is not the case.
        /// 
        /// </param>
        /// <returns> The resulting signal, of length in.length*2+wf.length-2
        /// 
        /// </returns>
        private static float[] upsampleAndConvolve(float[]? input, float[] wf, float[]? output)
        {
            // NOTE: the effective length of the signal 'in' upsampled by
            // 2 is 2*in.length-1 (not 2*in.length), so the resulting signal
            // (after convolution) is of length 2*in.length-1+wf.length-1,
            // which is 2*in.length+wf.length-2

            int i, k, j;
            float tmp;
            int maxi, maxk;

            // If in null, then simulate dirac
            if (input == null)
            {
                input = new float[1];
                input[0] = 1.0f;
            }

            // Get output buffer if necessary
            if (output == null)
            {
                output = new float[input.Length * 2 + wf.Length - 2];
            }
            // Convolve the signals
            for (i = 0, maxi = input.Length * 2 + wf.Length - 2; i < maxi; i++)
            {
                tmp = 0.0f;

                // Calculate limits of loop below
                k = (i - wf.Length + 2) / 2;
                if (k < 0)
                    k = 0;
                maxk = i / 2 + 1;
                if (maxk > input.Length)
                    maxk = input.Length;

                // Calculate dot-product with upsampling of 'in' by 2.
                for (j = 2 * k - i + wf.Length - 1; k < maxk; k++, j += 2)
                {
                    tmp += input[k] * wf[j];
                }
                // Store result
                output[i] = tmp;
            }

            return output;
        }
        public abstract bool IsSameAsFullWT(int param1, int param2, int param3);
    }
}