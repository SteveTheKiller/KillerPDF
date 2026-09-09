/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>
using System;
using ICCCurveType = CoreJ2K.Icc.Tags.ICCCurveType;
using Tags_ICCCurveType = CoreJ2K.Icc.Tags.ICCCurveType;

namespace CoreJ2K.Icc.Lut
{

    /// <summary> An interpolated floating point lut
    /// 
    /// </summary>
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A.Kern
    /// </author>
    public class LookUpTableFPInterp : LookUpTableFP
    {

        /// <summary> Create an abbreviated string representation of a 16 bit lut.</summary>
        /// <returns> the lut as a String
        /// </returns>
        public override string ToString()
        {
            var rep = new System.Text.StringBuilder("[LookUpTable32 ").Append(
                $" nentries= {lut.Length}");
            return rep.Append("]").ToString();
        }

        /// <summary> Construct the lut from the curve data</summary>
        /// <oaram>   curve the data </oaram>
        /// <oaram>   dwNumInput the lut size </oaram>
        public LookUpTableFPInterp(Tags_ICCCurveType curve, int dwNumInput) : base(curve, dwNumInput)
        {

            int dwLowIndex, dwHighIndex; // Indices of interpolation points
            double dfLowIndex, dfHighIndex; // FP indices of interpolation points
            double dfTargetIndex; // Target index into interpolation table
            double dfRatio; // Ratio of LUT input points to curve values
            double dfLow, dfHigh; // Interpolation values

            dfRatio = (curve.nEntries - 1) / (double)(dwNumInput - 1);

            for (var i = 0; i < dwNumInput; i++)
            {
                dfTargetIndex = i * dfRatio;
                dfLowIndex = Math.Floor(dfTargetIndex);
                dwLowIndex = (int)dfLowIndex;
                dfHighIndex = Math.Ceiling(dfTargetIndex);
                dwHighIndex = (int)dfHighIndex;
                if (dwLowIndex == dwHighIndex)
                {
                    lut[i] = (float)Tags_ICCCurveType.CurveToDouble(curve.entry(dwLowIndex));
                }
                else
                {
                    dfLow = ICCCurveType.CurveToDouble(curve.entry(dwLowIndex));
                    dfHigh = ICCCurveType.CurveToDouble(curve.entry(dwHighIndex));
                    lut[i] = (float)(dfLow + (dfHigh - dfLow) * (dfTargetIndex - dfLowIndex));
                }
            }
        }
    }
}