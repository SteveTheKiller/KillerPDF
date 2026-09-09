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

namespace CoreJ2K.j2k.wavelet.analysis
{

    /// <summary> This extends the analysis wavelet filter general definitions of
    /// AnWTFilter by adding methods that work for float data
    /// specifically. Implementations that work on float data should inherit
    /// from this class.
    /// 
    /// See the AnWTFilter class for details such as
    /// normalization, how to split odd-length signals, etc.
    /// 
    /// The advantage of using the specialized method is that no casts
    /// are performed.
    /// 
    /// </summary>
    /// <seealso cref="AnWTFilter" />
    public abstract class AnWTFilterFloat : AnWTFilter
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

        /// <summary> A specific version of the analyze_lpf() method that works on int
        /// data. See the general description of the analyze_lpf() method in
        /// the AnWTFilter class for more details.
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
        /// <seealso cref="AnWTFilter.analyze_lpf">
        /// 
        /// 
        /// 
        /// 
        /// 
        /// </seealso>
        public abstract void analyze_lpf(float[] inSig, int inOff, int inLen, int inStep, float[] lowSig, int lowOff, int lowStep, float[] highSig, int highOff, int highStep);

        /// <summary> The general version of the analyze_lpf() method, it just calls the
        /// specialized version. See the description of the analyze_lpf()
        /// method of the AnWTFilter class for more details.
        /// 
        /// </summary>
        /// <param name="inSig">This is the array that contains the input
        /// signal. It must be an float[].
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
        /// signal is placed. It must be an float[].
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
        /// signal is placed. It must be an float[].
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
        /// <seealso cref="AnWTFilter.analyze_lpf">
        /// 
        /// 
        /// 
        /// </seealso>
        public override void analyze_lpf(object inSig, int inOff, int inLen, int inStep, object lowSig, int lowOff, int lowStep, object highSig, int highOff, int highStep)
        {

            analyze_lpf((float[])inSig, inOff, inLen, inStep, (float[])lowSig, lowOff, lowStep, (float[])highSig, highOff, highStep);
        }

        /// <summary> A specific version of the analyze_hpf() method that works on int
        /// data. See the general description of the analyze_hpf() method in the 
        /// AnWTFilter class for more details.
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
        /// <seealso cref="AnWTFilter.analyze_hpf">
        /// 
        /// 
        /// 
        /// </seealso>
        public abstract void analyze_hpf(float[] inSig, int inOff, int inLen, int inStep, float[] lowSig, int lowOff, int lowStep, float[] highSig, int highOff, int highStep);



        /// <summary> The general version of the analyze_hpf() method, it just calls the
        /// specialized version. See the description of the analyze_hpf()
        /// method of the AnWTFilter class for more details.
        /// 
        /// </summary>
        /// <param name="inSig">This is the array that contains the input
        /// signal. It must be an float[].
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
        /// signal is placed. It must be an float[].
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
        /// signal is placed. It must be an float[].
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
        /// <seealso cref="AnWTFilter.analyze_hpf">
        /// 
        /// 
        /// 
        /// </seealso>
        public override void analyze_hpf(object inSig, int inOff, int inLen, int inStep, object lowSig, int lowOff, int lowStep, object highSig, int highOff, int highStep)
        {

            analyze_hpf((float[])inSig, inOff, inLen, inStep, (float[])lowSig, lowOff, lowStep, (float[])highSig, highOff, highStep);
        }
    }
}