/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>

using Color_ColorSpace = CoreJ2K.Color.ColorSpace;
using ColorSpaceException = CoreJ2K.Color.ColorSpaceException;

namespace CoreJ2K.Icc
{

    /// <summary> The monochrome ICCProfile.
    /// 
    /// </summary>
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public class ICCMonochromeInputProfile : ICCProfile
    {

        /// <summary> Return the ICCProfile embedded in the input image</summary>
        /// <param name="in">jp2 image with embedded profile
        /// </param>
        /// <returns> ICCMonochromeInputProfile 
        /// </returns>
        /// <exception cref="ColorSpaceICCProfileInvalidExceptionException">
        /// </exception>
        /// <exception cref="">
        /// </exception>
        public static ICCMonochromeInputProfile createInstance(Color_ColorSpace csm)
        {
            return new ICCMonochromeInputProfile(csm);
        }

        /// <summary> Construct a ICCMonochromeInputProfile corresponding to the profile file</summary>
        /// <param name="f">disk based ICCMonochromeInputProfile
        /// </param>
        /// <returns> theICCMonochromeInputProfile
        /// </returns>
        /// <exception cref="ColorSpaceException">
        /// </exception>
        /// <exception cref="ICCProfileInvalidException">
        /// </exception>
        protected internal ICCMonochromeInputProfile(Color_ColorSpace csm) : base(csm)
        {
        }
    }
}