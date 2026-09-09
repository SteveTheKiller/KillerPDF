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

    /// <summary> This exception is thrown when the content of a profile
    /// is incorrect.
    /// 
    /// </summary>
    /// <seealso cref="j2k.icc.ICCProfile" />
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public class ICCProfileException : Exception
    {

        /// <summary>  Contruct with message</summary>
        /// <param name="msg">returned by getMessage()
        /// </param>
        public ICCProfileException(string msg) : base(msg)
        {
        }


        /// <summary> Empty constructor</summary>
        public ICCProfileException()
        {
        }
    }
}