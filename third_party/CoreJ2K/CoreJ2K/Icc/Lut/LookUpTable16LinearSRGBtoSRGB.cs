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

    /// <summary> A Linear 16 bit SRGB to SRGB lut
    /// 
    /// </summary>
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public class LookUpTable16LinearSRGBtoSRGB : LookUpTable16
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
        public static LookUpTable16LinearSRGBtoSRGB createInstance(int wShadowCutoff, double dfShadowSlope, int ksRGBLinearMaxValue, double ksRGB8ScaleAfterExp, double ksRGBExponent, double ksRGB8ReduceAfterEx)
        {
            return new LookUpTable16LinearSRGBtoSRGB(wShadowCutoff, dfShadowSlope, ksRGBLinearMaxValue, ksRGB8ScaleAfterExp, ksRGBExponent, ksRGB8ReduceAfterEx);
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
        protected internal LookUpTable16LinearSRGBtoSRGB(int wShadowCutoff, double dfShadowSlope, int ksRGBLinearMaxValue, double ksRGB8ScaleAfterExp, double ksRGBExponent, double ksRGB8ReduceAfterExp) : base(ksRGBLinearMaxValue + 1, 0)
        {

            var i = -1;
            var dfNormalize = 1.0 / (float)ksRGBLinearMaxValue;

            // Generate the final linear-sRGB to non-linear sRGB LUT
            for (i = 0; i <= wShadowCutoff; i++)
            {
                lut[i] = (byte)Math.Floor(dfShadowSlope * i + 0.5);
            }

            // Now calculate the rest
            for (; i <= ksRGBLinearMaxValue; i++)
            {
                lut[i] = (byte)Math.Floor(ksRGB8ScaleAfterExp * Math.Pow(i * dfNormalize, ksRGBExponent) - ksRGB8ReduceAfterExp + 0.5);
            }
        }
    }
}