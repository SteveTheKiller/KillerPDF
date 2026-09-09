/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>

namespace CoreJ2K.Icc
{
    using ICCCurveType = Tags.ICCCurveType;
    using ICCXYZType = Tags.ICCXYZType;

    /// <summary> This profile is constructed by parsing an ICCProfile and
    /// is the profile actually applied to the image.
    /// 
    /// </summary>
    /// <seealso cref="j2k.icc.ICCProfile" />
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public abstract class RestrictedICCProfile
    {
        /// <summary>Returns the appropriate input type enum. </summary>
        public abstract int Type { get; }

        /// <summary> Factory method for creating a RestrictedICCProfile from 
        /// 3 component curve and colorant data.
        /// </summary>
        /// <param name="rcurve">red curve
        /// </param>
        /// <param name="gcurve">green curve
        /// </param>
        /// <param name="bcurve">blue curve
        /// </param>
        /// <param name="rcolorant">red colorant
        /// </param>
        /// <param name="gcolorant">green colorant
        /// </param>
        /// <param name="bcolorant">blue colorant
        /// </param>
        /// <returns> MatrixBasedRestrictedProfile
        /// </returns>
        public static RestrictedICCProfile createInstance(ICCCurveType rcurve, ICCCurveType gcurve, ICCCurveType bcurve, ICCXYZType rcolorant, ICCXYZType gcolorant, ICCXYZType bcolorant)
        {

            return MatrixBasedRestrictedProfile.createInstance(rcurve, gcurve, bcurve, rcolorant, gcolorant, bcolorant);
        }

        /// <summary> Factory method for creating a RestrictedICCProfile from 
        /// gray curve data.
        /// </summary>
        /// <param name="gcurve">gray curve
        /// </param>
        /// <returns> MonochromeInputRestrictedProfile
        /// </returns>
        public static RestrictedICCProfile createInstance(ICCCurveType gcurve)
        {
            return MonochromeInputRestrictedProfile.createInstance(gcurve);
        }

        /// <summary>Component index       </summary>
        protected internal static readonly int GRAY;
        /// <summary>Component index       </summary>
        protected internal static readonly int RED;
        /// <summary>Component index       </summary>
        protected internal static readonly int GREEN;
        /// <summary>Component index       </summary>
        protected internal static readonly int BLUE;
        /// <summary>input type enumerator </summary>
        public const int kMonochromeInput = 0;
        /// <summary>input type enumerator </summary>
        public const int kThreeCompInput = 1;

        /// <summary>Curve data    </summary>
        public ICCCurveType[] trc;
        /// <summary>Colorant data </summary>
        public ICCXYZType[]? colorant;

        /// <summary> Construct the common state of all gray RestrictedICCProfiles</summary>
        /// <param name="gcurve">curve data
        /// </param>
        protected internal RestrictedICCProfile(ICCCurveType gcurve)
        {
            trc = new ICCCurveType[1];
            colorant = null;
            trc[GRAY] = gcurve;
        }

        /// <summary> Construct the common state of all 3 component RestrictedICCProfiles
        /// 
        /// </summary>
        /// <param name="rcurve">red curve
        /// </param>
        /// <param name="gcurve">green curve
        /// </param>
        /// <param name="bcurve">blue curve
        /// </param>
        /// <param name="rcolorant">red colorant
        /// </param>
        /// <param name="gcolorant">green colorant
        /// </param>
        /// <param name="bcolorant">blue colorant
        /// </param>
        protected internal RestrictedICCProfile(ICCCurveType rcurve, ICCCurveType gcurve, ICCCurveType bcurve, ICCXYZType rcolorant, ICCXYZType gcolorant, ICCXYZType bcolorant)
        {
            trc = new ICCCurveType[3];
            colorant = new ICCXYZType[3];

            trc[RED] = rcurve;
            trc[GREEN] = gcurve;
            trc[BLUE] = bcurve;

            colorant[RED] = rcolorant;
            colorant[GREEN] = gcolorant;
            colorant[BLUE] = bcolorant;
        }
        static RestrictedICCProfile()
        {
            GRAY = ICCProfile.GRAY;
            RED = ICCProfile.RED;
            GREEN = ICCProfile.GREEN;
            BLUE = ICCProfile.BLUE;
        }
    }
}