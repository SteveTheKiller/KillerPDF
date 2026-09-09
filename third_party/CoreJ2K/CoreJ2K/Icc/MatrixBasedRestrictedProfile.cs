/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>
using System;

namespace CoreJ2K.Icc
{
    using ICCCurveType = Tags.ICCCurveType;
    using ICCXYZType = Tags.ICCXYZType;

    /// <summary> This class is a 3 component RestrictedICCProfile
    /// 
    /// </summary>
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A Kern
    /// </author>
    public class MatrixBasedRestrictedProfile : RestrictedICCProfile
    {
        /// <summary> Get the type of RestrictedICCProfile for this object</summary>
        /// <returns> kThreeCompInput
        /// </returns>
        public override int Type => kThreeCompInput;

        /// <summary> Factory method which returns a 3 component RestrictedICCProfile</summary>
        /// <param name="rcurve">Red TRC curve
        /// </param>
        /// <param name="gcurve">Green TRC curve
        /// </param>
        /// <param name="bcurve">Blue TRC curve
        /// </param>
        /// <param name="rcolorant">Red colorant
        /// </param>
        /// <param name="gcolorant">Green colorant
        /// </param>
        /// <param name="bcolorant">Blue colorant
        /// </param>
        /// <returns> the RestrictedICCProfile
        /// </returns>
        public new static RestrictedICCProfile createInstance(ICCCurveType rcurve, ICCCurveType gcurve, ICCCurveType bcurve, ICCXYZType rcolorant, ICCXYZType gcolorant, ICCXYZType bcolorant)
        {
            return new MatrixBasedRestrictedProfile(rcurve, gcurve, bcurve, rcolorant, gcolorant, bcolorant);
        }

        /// <summary> Construct a 3 component RestrictedICCProfile</summary>
        /// <param name="rcurve">Red TRC curve
        /// </param>
        /// <param name="gcurve">Green TRC curve
        /// </param>
        /// <param name="bcurve">Blue TRC curve
        /// </param>
        /// <param name="rcolorant">Red colorant
        /// </param>
        /// <param name="gcolorant">Green colorant
        /// </param>
        /// <param name="bcolorant">Blue colorant
        /// </param>
        protected internal MatrixBasedRestrictedProfile(ICCCurveType rcurve, ICCCurveType gcurve, ICCCurveType bcurve, ICCXYZType rcolorant, ICCXYZType gcolorant, ICCXYZType bcolorant) : base(rcurve, gcurve, bcurve, rcolorant, gcolorant, bcolorant)
        {
        }

        /// <returns> String representation of a MatrixBasedRestrictedProfile
        /// </returns>
        public override string ToString()
        {
            var rep = new System.Text.StringBuilder("[Matrix-Based Input Restricted ICC profile").Append(Environment.NewLine);

            rep.Append("trc[RED]:").Append(Environment.NewLine).Append(trc[RED]).Append(Environment.NewLine);
            rep.Append("trc[RED]:").Append(Environment.NewLine).Append(trc[GREEN]).Append(Environment.NewLine);
            rep.Append("trc[RED]:").Append(Environment.NewLine).Append(trc[BLUE]).Append(Environment.NewLine);

            rep.Append("Red colorant:  ").Append(colorant![RED]).Append(Environment.NewLine);
            rep.Append("Red colorant:  ").Append(colorant[GREEN]).Append(Environment.NewLine);
            rep.Append("Red colorant:  ").Append(colorant[BLUE]).Append(Environment.NewLine);

            return rep.Append("]").ToString();
        }
    }
}