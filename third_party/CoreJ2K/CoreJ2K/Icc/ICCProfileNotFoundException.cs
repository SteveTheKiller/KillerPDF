/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>

namespace CoreJ2K.Icc
{

    /// <summary> This exception is thrown when an image contains no icc profile.
    /// is incorrect.
    /// 
    /// </summary>
    /// <seealso cref="j2k.icc.ICCProfile" />
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>

    public class ICCProfileNotFoundException : ICCProfileException
    {

        /// <summary> Contruct with message</summary>
        /// <param name="msg">returned by getMessage()
        /// </param>
        internal ICCProfileNotFoundException(string msg) : base(msg)
        {
        }


        /// <summary> Empty constructor</summary>
        internal ICCProfileNotFoundException() : base("no icc profile in image")
        {
        }
    }
}