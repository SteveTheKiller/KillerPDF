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

    /// <summary> This class is a 1 component RestrictedICCProfile
    /// 
    /// </summary>
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A Kern
    /// </author>
    public class MonochromeInputRestrictedProfile : RestrictedICCProfile
    {
        /// <summary> Get the type of RestrictedICCProfile for this object</summary>
        /// <returns> kMonochromeInput
        /// </returns>
        public override int Type => kMonochromeInput;

        /// <summary> Factory method which returns a 1 component RestrictedICCProfile</summary>
        /// <param name="c">Gray TRC curve
        /// </param>
        /// <returns> the RestrictedICCProfile
        /// </returns>
        public new static RestrictedICCProfile createInstance(ICCCurveType c)
        {
            return new MonochromeInputRestrictedProfile(c);
        }

        /// <summary> Construct a 1 component RestrictedICCProfile</summary>
        /// <param name="c">Gray TRC curve
        /// </param>
        private MonochromeInputRestrictedProfile(ICCCurveType c) : base(c)
        {
        }

        /// <returns> String representation of a MonochromeInputRestrictedProfile
        /// </returns>
        public override string ToString()
        {
            var rep = new System.Text.StringBuilder(
                $"Monochrome Input Restricted ICC profile{Environment.NewLine}");

            rep.Append($"trc[GRAY]:{Environment.NewLine}").Append(trc[GRAY]).Append(Environment.NewLine);

            return rep.ToString();
        }
    }
}