/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>
using System;

namespace CoreJ2K.Icc.Tags
{

    /// <summary> A text based ICC tag
    /// 
    /// </summary>
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public class ICCTextType : ICCTag
    {

        /// <summary>Tag fields </summary>
        public new int type;
        /// <summary>Tag fields </summary>
        public int reserved;
        /// <summary>Tag fields </summary>
        public byte[] ascii;

        /// <summary> Construct this tag from its constituent parts</summary>
        /// <param name="signature">tag id</param>
        /// <param name="data">array of bytes</param>
        /// <param name="offset">to data in the data array</param>
        /// <param name="length">of data in the data array</param>
        protected internal ICCTextType(int signature, byte[] data, int offset, int length) : base(signature, data, offset, length)
        {
            type = ICCProfile.GetInt(data, offset);
            offset += ICCProfile.int_size;
            reserved = ICCProfile.GetInt(data, offset);
            offset += ICCProfile.int_size;
            var size = 0;
            while (data[offset + size] != 0)
                ++size;
            ascii = new byte[size];
            Array.Copy(data, offset, ascii, 0, size);
        }

        /// <summary>Return the string rep of this tag. </summary>
        public override string ToString()
        {
            return $"[{base.ToString()} \"{System.Text.Encoding.UTF8.GetString(ascii, 0, ascii.Length)}\"]";
        }
    }
}