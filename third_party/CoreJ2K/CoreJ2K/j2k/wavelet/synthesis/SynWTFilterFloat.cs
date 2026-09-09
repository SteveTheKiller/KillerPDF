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
* 
* 
* 
*/

using CoreJ2K.j2k.image;

namespace CoreJ2K.j2k.wavelet.synthesis
{

    /// <summary> This extends the synthesis wavelet filter general definitions of
    /// SynWTFilter by adding methods that work for float data
    /// specifically. Implementations that work on float data should inherit
    /// from this class.
    /// 
    /// See the SynWTFilter class for details such as
    /// normalization, how to split odd-length signals, etc.
    /// 
    /// The advantage of using the specialized method is that no casts
    /// are performed.
    /// 
    /// </summary>
    /// <seealso cref="SynWTFilter" />
    public abstract class SynWTFilterFloat : SynWTFilter
    {
        /// <summary> Returns the type of data on which this filter works, as defined
        /// in the DataBlk interface, which is always TYPE_FLOAT for this
        /// class.
        /// 
        /// </summary>
        /// <returns> The type of data as defined in the DataBlk interface.
        /// 
        /// </returns>
        /// <seealso cref="j2k.image.DataBlk">
        /// 
        /// 
        /// 
        /// </seealso>
        public override int DataType => DataBlk.TYPE_FLOAT;

        /// <summary> A specific version of the synthetize_lpf() method that works on float
        /// data. See the general description of the synthetize_lpf() method in the 
        /// SynWTFilter class for more details.
        /// 
        /// </summary>
        /// <param name="lowSig">This is the array that contains the low-pass
        /// input signal.
        /// 
        /// </param>
        /// <param name="lowOff">This is the index in lowSig of the first sample to
        /// filter.
        /// 
        /// </param>
        /// <param name="lowLen">This is the number of samples in the low-pass
        /// input signal to filter.
        /// 
        /// </param>
        /// <param name="lowStep">This is the step, or interleave factor, of the
        /// low-pass input signal samples in the lowSig array.
        /// 
        /// </param>
        /// <param name="highSig">This is the array that contains the high-pass
        /// input signal.
        /// 
        /// </param>
        /// <param name="highOff">This is the index in highSig of the first sample to
        /// filter.
        /// 
        /// </param>
        /// <param name="highLen">This is the number of samples in the high-pass
        /// input signal to filter.
        /// 
        /// </param>
        /// <param name="highStep">This is the step, or interleave factor, of the
        /// high-pass input signal samples in the highSig array.
        /// 
        /// </param>
        /// <param name="outSig">This is the array where the output signal is
        /// placed. It should be long enough to contain the output signal.
        /// 
        /// </param>
        /// <param name="outOff">This is the index in outSig of the element where
        /// to put the first output sample.
        /// 
        /// </param>
        /// <param name="outStep">This is the step, or interleave factor, of the
        /// output samples in the outSig array.
        /// 
        /// </param>
        /// <seealso cref="SynWTFilter.synthetize_lpf">
        /// 
        /// 
        /// 
        /// 
        /// 
        /// </seealso>
        public abstract void synthetize_lpf(float[] lowSig, int lowOff, int lowLen, int lowStep, float[] highSig, int highOff, int highLen, int highStep, float[] outSig, int outOff, int outStep);

        /// <summary> The general version of the synthetize_lpf() method, it just calls
        /// the specialized version. See the description of the synthetize_lpf()
        /// method of the SynWTFilter class for more details.
        /// 
        /// </summary>
        /// <param name="lowSig">This is the array that contains the low-pass
        /// input signal. It must be an float[].
        /// 
        /// </param>
        /// <param name="lowOff">This is the index in lowSig of the first sample to
        /// filter.
        /// 
        /// </param>
        /// <param name="lowLen">This is the number of samples in the low-pass
        /// input signal to filter.
        /// 
        /// </param>
        /// <param name="lowStep">This is the step, or interleave factor, of the
        /// low-pass input signal samples in the lowSig array.
        /// 
        /// </param>
        /// <param name="highSig">This is the array that contains the high-pass
        /// input signal. It must be an float[].
        /// 
        /// </param>
        /// <param name="highOff">This is the index in highSig of the first sample to
        /// filter.
        /// 
        /// </param>
        /// <param name="highLen">This is the number of samples in the high-pass
        /// input signal to filter.
        /// 
        /// </param>
        /// <param name="highStep">This is the step, or interleave factor, of the
        /// high-pass input signal samples in the highSig array.
        /// 
        /// </param>
        /// <param name="outSig">This is the array where the output signal is
        /// placed. It should be and float[] and long enough to contain the
        /// output signal.
        /// 
        /// </param>
        /// <param name="outOff">This is the index in outSig of the element where
        /// to put the first output sample.
        /// 
        /// </param>
        /// <param name="outStep">This is the step, or interleave factor, of the
        /// output samples in the outSig array.
        /// 
        /// </param>
        /// <seealso cref="SynWTFilter.synthetize_hpf">
        /// 
        /// 
        /// 
        /// 
        /// 
        /// </seealso>
        public override void synthetize_lpf(object? lowSig, int lowOff, int lowLen, int lowStep, object? highSig, int highOff, int highLen, int highStep, object? outSig, int outOff, int outStep)
        {

            synthetize_lpf((float[])lowSig!, lowOff, lowLen, lowStep, (float[])highSig!, highOff, highLen, highStep, (float[])outSig!, outOff, outStep);
        }

        /// <summary> A specific version of the synthetize_hpf() method that works on float
        /// data. See the general description of the synthetize_hpf() method in the 
        /// SynWTFilter class for more details.
        /// 
        /// </summary>
        /// <param name="lowSig">This is the array that contains the low-pass
        /// input signal.
        /// 
        /// </param>
        /// <param name="lowOff">This is the index in lowSig of the first sample to
        /// filter.
        /// 
        /// </param>
        /// <param name="lowLen">This is the number of samples in the low-pass
        /// input signal to filter.
        /// 
        /// </param>
        /// <param name="lowStep">This is the step, or interleave factor, of the
        /// low-pass input signal samples in the lowSig array.
        /// 
        /// </param>
        /// <param name="highSig">This is the array that contains the high-pass
        /// input signal.
        /// 
        /// </param>
        /// <param name="highOff">This is the index in highSig of the first sample to
        /// filter.
        /// 
        /// </param>
        /// <param name="highLen">This is the number of samples in the high-pass
        /// input signal to filter.
        /// 
        /// </param>
        /// <param name="highStep">This is the step, or interleave factor, of the
        /// high-pass input signal samples in the highSig array.
        /// 
        /// </param>
        /// <param name="outSig">This is the array where the output signal is
        /// placed. It should be long enough to contain the output signal.
        /// 
        /// </param>
        /// <param name="outOff">This is the index in outSig of the element where
        /// to put the first output sample.
        /// 
        /// </param>
        /// <param name="outStep">This is the step, or interleave factor, of the
        /// output samples in the outSig array.
        /// 
        /// </param>
        /// <seealso cref="SynWTFilter.synthetize_hpf">
        /// 
        /// 
        /// 
        /// 
        /// 
        /// </seealso>
        public abstract void synthetize_hpf(float[] lowSig, int lowOff, int lowLen, int lowStep, float[] highSig, int highOff, int highLen, int highStep, float[] outSig, int outOff, int outStep);

        /// <summary> The general version of the synthetize_hpf() method, it just calls
        /// the specialized version. See the description of the synthetize_hpf()
        /// method of the SynWTFilter class for more details.
        /// 
        /// </summary>
        /// <param name="lowSig">This is the array that contains the low-pass
        /// input signal. It must be an float[].
        /// 
        /// </param>
        /// <param name="lowOff">This is the index in lowSig of the first sample to
        /// filter.
        /// 
        /// </param>
        /// <param name="lowLen">This is the number of samples in the low-pass
        /// input signal to filter.
        /// 
        /// </param>
        /// <param name="lowStep">This is the step, or interleave factor, of the
        /// low-pass input signal samples in the lowSig array.
        /// 
        /// </param>
        /// <param name="highSig">This is the array that contains the high-pass
        /// input signal. It must be an float[].
        /// 
        /// </param>
        /// <param name="highOff">This is the index in highSig of the first sample to
        /// filter.
        /// 
        /// </param>
        /// <param name="highLen">This is the number of samples in the high-pass
        /// input signal to filter.
        /// 
        /// </param>
        /// <param name="highStep">This is the step, or interleave factor, of the
        /// high-pass input signal samples in the highSig array.
        /// 
        /// </param>
        /// <param name="outSig">This is the array where the output signal is
        /// placed. It should be and float[] and long enough to contain the
        /// output signal.
        /// 
        /// </param>
        /// <param name="outOff">This is the index in outSig of the element where
        /// to put the first output sample.
        /// 
        /// </param>
        /// <param name="outStep">This is the step, or interleave factor, of the
        /// output samples in the outSig array.
        /// 
        /// </param>
        /// <seealso cref="SynWTFilter.synthetize_hpf">
        /// 
        /// 
        /// 
        /// 
        /// 
        /// </seealso>
        public override void synthetize_hpf(object? lowSig, int lowOff, int lowLen, int lowStep, object? highSig, int highOff, int highLen, int highStep, object? outSig, int outOff, int outStep)
        {

            synthetize_hpf((float[])lowSig!, lowOff, lowLen, lowStep, (float[])highSig!, highOff, highLen, highStep, (float[])outSig!, outOff, outStep);
        }
    }
}