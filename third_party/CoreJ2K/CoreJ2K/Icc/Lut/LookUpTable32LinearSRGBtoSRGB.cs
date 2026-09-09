/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>
using System;

namespace CoreJ2K.Icc.Lut
{

    /// <summary> A Linear 32 bit SRGB to SRGB lut
    /// 
    /// </summary>
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public class LookUpTable32LinearSRGBtoSRGB : LookUpTable32
    {

        /// <summary> Factory method for creating the lut.</summary>
        /// <param name="wShadowCutoff">size of shadow region
        /// </param>
        /// <param name="dfShadowSlope">shadow region parameter
        /// </param>
        /// <param name="ksRGBLinearMaxValue">size of lut
        /// </param>
        /// <param name="ksRGB8ScaleAfterExp">post shadow region parameter
        /// </param>
        /// <param name="ksRGBExponent">post shadow region parameter
        /// </param>
        /// <param name="ksRGB8ReduceAfterEx">post shadow region parameter
        /// </param>
        /// <returns> the lut
        /// </returns>
        public static LookUpTable32LinearSRGBtoSRGB createInstance(int inMax, int outMax, double shadowCutoff, double shadowSlope, double scaleAfterExp, double exponent, double reduceAfterExp)
        {
            return new LookUpTable32LinearSRGBtoSRGB(inMax, outMax, shadowCutoff, shadowSlope, scaleAfterExp, exponent, reduceAfterExp);
        }

        /// <summary> Construct the lut</summary>
        /// <param name="wShadowCutoff">size of shadow region
        /// </param>
        /// <param name="dfShadowSlope">shadow region parameter
        /// </param>
        /// <param name="ksRGBLinearMaxValue">size of lut
        /// </param>
        /// <param name="ksRGB8ScaleAfterExp">post shadow region parameter
        /// </param>
        /// <param name="ksRGBExponent">post shadow region parameter
        /// </param>
        /// <param name="ksRGB8ReduceAfterExp">post shadow region parameter
        /// </param>
        protected internal LookUpTable32LinearSRGBtoSRGB(int inMax, int outMax, double shadowCutoff, double shadowSlope, double scaleAfterExp, double exponent, double reduceAfterExp) : base(inMax + 1, outMax)
        {

            var i = -1;
            // Normalization factor for i.
            var normalize = 1.0 / inMax;

            // Generate the final linear-sRGB to non-linear sRGB LUT    

            // calculate where shadow portion of lut ends.
            var cutOff = (int)Math.Floor(shadowCutoff * inMax);

            // Scale to account for output
            shadowSlope *= outMax;

            // Our output needs to be centered on zero so we shift it down.
            var shift = (outMax + 1) / 2;

            for (i = 0; i <= cutOff; i++)
            {
                lut[i] = (int)(Math.Floor(shadowSlope * (i * normalize) + 0.5) - shift);
            }

            // Scale values for output.
            scaleAfterExp *= outMax;
            reduceAfterExp *= outMax;

            // Now calculate the rest
            for (; i <= inMax; i++)
            {
                lut[i] = (int)(Math.Floor(scaleAfterExp * Math.Pow(i * normalize, exponent) - reduceAfterExp + 0.5) - shift);
            }
        }

        public override string ToString()
        {
            var rep = new System.Text.StringBuilder("[LookUpTable32LinearSRGBtoSRGB:");
            return rep.Append("]").ToString();
        }
    }
}