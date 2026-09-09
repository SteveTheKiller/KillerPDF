/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>

using Tags_ICCCurveType = CoreJ2K.Icc.Tags.ICCCurveType;

namespace CoreJ2K.Icc.Lut
{


    /// <summary> Toplevel class for a lut.  All lookup tables must
    /// extend this class.
    /// 
    /// </summary>
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public abstract class LookUpTable
    {
        /// <summary>The curve data                  </summary>
        protected internal Tags_ICCCurveType? curve = null;
        /// <summary>Number of values in created lut </summary>
        protected internal int dwNumInput = 0;


        /// <summary> For subclass usage.</summary>
        /// <param name="curve">The curve data  
        /// </param>
        /// <param name="dwNumInput">Number of values in created lut
        /// </param>
        protected internal LookUpTable(Tags_ICCCurveType? curve, int dwNumInput)
        {
            this.curve = curve;
            this.dwNumInput = dwNumInput;
        }
    }
}