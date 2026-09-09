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

    /// <summary> This class enables an application to construct an 3 component ICCProfile
    /// 
    /// </summary>
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>

    public class ICCMatrixBasedInputProfile : ICCProfile
    {

        /// <summary> Factory method to create ICCMatrixBasedInputProfile based on a
        /// suppled profile file.
        /// </summary>
        /// <param name="f">contains a disk based ICCProfile.
        /// </param>
        /// <returns> the ICCMatrixBasedInputProfile
        /// </returns>
        /// <exception cref="ICCProfileInvalidException">
        /// </exception>
        /// <exception cref="ColorSpaceException">
        /// </exception>
        public static ICCMatrixBasedInputProfile createInstance(Color_ColorSpace csm)
        {
            return new ICCMatrixBasedInputProfile(csm);
        }

        /// <summary> Construct an ICCMatrixBasedInputProfile based on a
        /// suppled profile file.
        /// </summary>
        /// <param name="f">contains a disk based ICCProfile.
        /// </param>
        /// <exception cref="ColorSpaceException">
        /// </exception>
        /// <exception cref="ICCProfileInvalidException">
        /// </exception>
        protected internal ICCMatrixBasedInputProfile(Color_ColorSpace csm) : base(csm)
        {
        }
    }
}