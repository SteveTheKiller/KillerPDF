/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>
using System;
namespace CoreJ2K.Icc.Lut
{

    /// <summary> Thrown by MatrixBasedTransformTosRGB
    /// 
    /// </summary>
    /// <seealso cref="j2k.icc.lut.MatrixBasedTransformTosRGB" />
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>

    public class MatrixBasedTransformException : Exception
    {

        /// <summary> Contruct with message</summary>
        /// <param name="msg">returned by getMessage()
        /// </param>
        internal MatrixBasedTransformException(string msg) : base(msg)
        {
        }


        /// <summary> Empty constructor</summary>
        internal MatrixBasedTransformException()
        {
        }
    }
}