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


    /// <summary> An interpolated 8 bit lut
    /// 
    /// </summary>
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A.Kern
    /// </author>
    public class LookUpTable8Interp : LookUpTable8
    {

        /// <summary> Construct the lut from the curve data</summary>
        /// <oaram>   curve the data </oaram>
        /// <oaram>   dwNumInput the lut size </oaram>
        /// <oaram>   dwMaxOutput the lut max value </oaram>
        public LookUpTable8Interp(Tags_ICCCurveType curve, int dwNumInput, byte dwMaxOutput) : base(curve, dwNumInput, dwMaxOutput)
        {

            int dwLowIndex, dwHighIndex; // Indices of interpolation points
            double dfLowIndex, dfHighIndex; // FP indices of interpolation points
            double dfTargetIndex; // Target index into interpolation table
            double dfRatio; // Ratio of LUT input points to curve values
            double dfLow, dfHigh; // Interpolation values
            double dfOut; // Output LUT value

            dfRatio = (curve.count - 1) / (double)(dwNumInput - 1);

            for (var i = 0; i < dwNumInput; i++)
            {
                dfTargetIndex = i * dfRatio;
                dfLowIndex = Math.Floor(dfTargetIndex);
                dwLowIndex = (int)dfLowIndex;
                dfHighIndex = Math.Ceiling(dfTargetIndex);
                dwHighIndex = (int)dfHighIndex;

                if (dwLowIndex == dwHighIndex)
                    dfOut = Tags_ICCCurveType.CurveToDouble(curve.entry(dwLowIndex));
                else
                {
                    dfLow = ICCCurveType.CurveToDouble(curve.entry(dwLowIndex));
                    dfHigh = ICCCurveType.CurveToDouble(curve.entry(dwHighIndex));
                    dfOut = dfLow + (dfHigh - dfLow) * (dfTargetIndex - dfLowIndex);
                }

                lut[i] = (byte)Math.Floor(dfOut * dwMaxOutput + 0.5);
            }
        }
    }
}