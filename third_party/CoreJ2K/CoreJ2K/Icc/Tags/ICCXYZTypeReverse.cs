/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>

namespace CoreJ2K.Icc.Tags
{

    /// <summary> A tag containing a triplet.
    /// 
    /// </summary>
    /// <seealso cref="j2k.icc.tags.ICCXYZType" />
    /// <seealso cref="j2k.icc.types.XYZNumber" />
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public class ICCXYZTypeReverse : ICCXYZType
    {

        /// <summary>x component </summary>
        public new long x;
        /// <summary>y component </summary>
        public new long y;
        /// <summary>z component </summary>
        public new long z;

        /// <summary> Construct this tag from its constituant parts</summary>
        /// <param name="signature">tag id
        /// </param>
        /// <param name="data">array of bytes
        /// </param>
        /// <param name="offset">to data in the data array
        /// </param>
        /// <param name="length">of data in the data array
        /// </param>
        protected internal ICCXYZTypeReverse(int signature, byte[] data, int offset, int length) : base(signature, data, offset, length)
        {
            z = ICCProfile.GetInt(data, offset + 2 * ICCProfile.int_size);
            y = ICCProfile.GetInt(data, offset + 3 * ICCProfile.int_size);
            x = ICCProfile.GetInt(data, offset + 4 * ICCProfile.int_size);
        }


        /// <summary>Return the string rep of this tag. </summary>
        public override string ToString()
        {
            return $"[{base.ToString()}({x}, {y}, {z})]";
        }
    }
}