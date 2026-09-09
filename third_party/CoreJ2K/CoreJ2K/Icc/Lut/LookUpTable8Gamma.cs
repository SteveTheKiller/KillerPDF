/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>
using System;
using Tags_ICCCurveType = CoreJ2K.Icc.Tags.ICCCurveType;

namespace CoreJ2K.Icc.Lut
{

    /// <summary> A Gamma based 16 bit lut.
    /// 
    /// </summary>
    /// <seealso cref="j2k.icc.tags.ICCCurveType" />
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public class LookUpTable8Gamma : LookUpTable8
    {

		/// <summary>Construct the lut</summary>
		/// <param name="curve">Curve data</param>
		/// <param name="dwNumInput">Size of lut</param>
		/// <param name="dwMaxOutput">Max value of lut</param>
		public LookUpTable8Gamma(Tags_ICCCurveType curve, int dwNumInput, byte dwMaxOutput) : base(curve, dwNumInput, dwMaxOutput)
        {
            var dfE = Tags_ICCCurveType.CurveGammaToDouble(curve.entry(0)); // Gamma exponent for inverse transformation
            for (var i = 0; i < dwNumInput; i++)
            {
                lut[i] = (byte)Math.Floor(Math.Pow((double)i / (dwNumInput - 1), dfE) * dwMaxOutput + 0.5);
            }
        }
    }
}