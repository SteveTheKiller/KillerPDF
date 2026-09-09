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

    /// <summary> Class Description
    /// 
    /// </summary>
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>

    public class LookUpTableFPGamma : LookUpTableFP
    {

        internal double dfE = -1;

        public LookUpTableFPGamma(Tags_ICCCurveType curve, int dwNumInput) : base(curve, dwNumInput)
        {

            // Gamma exponent for inverse transformation
            dfE = Tags_ICCCurveType.CurveGammaToDouble(curve.entry(0));
            for (var i = 0; i < dwNumInput; i++)
            {
                lut[i] = (float)Math.Pow((double)i / (dwNumInput - 1), dfE);
            }
        }

        /// <summary> Create an abbreviated string representation of a 16 bit lut.</summary>
        /// <returns> the lut as a String
        /// </returns>
        public override string ToString()
        {
            var rep = new System.Text.StringBuilder("[LookUpTableGamma ");
            //int row, col;
            rep.Append($"dfe= {dfE}");
            rep.Append($", nentries= {lut.Length}");
            return rep.Append("]").ToString();
        }
    }
}