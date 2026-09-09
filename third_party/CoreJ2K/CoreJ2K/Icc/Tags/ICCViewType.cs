using CoreJ2K.Icc.Types;

namespace CoreJ2K.Icc.Tags
{
    public class ICCViewType : ICCTag
    {
        public new int type;
        public int reserved;
        public XYZNumber CIEilluminant;
        public XYZNumber CIEsurround;
        public int illuminant;

        /// <summary> Construct this tag from its constituant parts</summary>
        /// <param name="signature">tag id</param>
        /// <param name="data">array of bytes</param>
        /// <param name="offset">to data in the data array</param>
        /// <param name="length">of data in the data array</param>
        protected internal ICCViewType(int signature, byte[] data, int offset, int length)
            : base(signature, data, offset, offset + 2 * ICCProfile.int_size)
        {
            type = ICCProfile.GetInt(data, offset);
            reserved = ICCProfile.GetInt(data, offset + ICCProfile.int_size);
            CIEilluminant = ICCProfile.GetXYZNumber(data, offset + ICCProfile.int_size);
            CIEsurround = ICCProfile.GetXYZNumber(data, offset + (ICCProfile.int_size * 3));
            illuminant = ICCProfile.GetInt(data, offset + (ICCProfile.int_size * 3));
        }
    }
}