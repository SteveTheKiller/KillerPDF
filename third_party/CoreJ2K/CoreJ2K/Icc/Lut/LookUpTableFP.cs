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

    /// <summary> Toplevel class for a float [] lut.
    /// 
    /// </summary>
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public abstract class LookUpTableFP : LookUpTable
    {

        /// <summary>The lut values. </summary>
        public float[] lut;

        /// <summary> Factory method for getting a lut from a given curve.</summary>
        /// <param name="curve"> the data
        /// </param>
        /// <param name="dwNumInput">the size of the lut 
        /// </param>
        /// <returns> the lookup table
        /// </returns>

        public static LookUpTableFP createInstance(Tags_ICCCurveType curve, int dwNumInput)
        {

            if (curve.nEntries == 1)
                return new LookUpTableFPGamma(curve, dwNumInput);
            else
                return new LookUpTableFPInterp(curve, dwNumInput);
        }

        /// <summary> Construct an empty lut</summary>
        /// <param name="dwNumInput">the size of the lut t lut.
        /// </param>
        /// <param name="dwMaxOutput">max output value of the lut
        /// </param>
        protected internal LookUpTableFP(Tags_ICCCurveType curve, int dwNumInput) : base(curve, dwNumInput)
        {
            lut = new float[dwNumInput];
        }

        /// <summary> lut accessor</summary>
        /// <param name="index">of the element
        /// </param>
        /// <returns> the lut [index]
        /// </returns>
        public float elementAt(int index)
        {
            return lut[index];
        }
    }
}