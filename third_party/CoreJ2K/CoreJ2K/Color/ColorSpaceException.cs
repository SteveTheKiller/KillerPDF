/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>
using System;
namespace CoreJ2K.Color
{

    /// <summary> This exception is thrown when the content of an
    /// image contains an incorrect colorspace box
    /// 
    /// </summary>
    /// <seealso cref="j2k.colorspace.ColorSpaceMapper" />
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>

    public class ColorSpaceException : Exception
    {

        /// <summary> Contruct with message</summary>
        /// <param name="msg">returned by getMessage()
        /// </param>
        public ColorSpaceException(string msg) : base(msg)
        {
        }


        /// <summary> Empty constructor</summary>
        public ColorSpaceException()
        {
        }
    }
}