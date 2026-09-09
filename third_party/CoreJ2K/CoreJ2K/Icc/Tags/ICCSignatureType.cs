namespace CoreJ2K.Icc.Tags
{
    public class ICCSignatureType : ICCTag
    {
        public new int type;
        public int reserved;
        public new int signature;

        /// <summary> Construct this tag from its constituant parts</summary>
        /// <param name="signature">tag id</param>
        /// <param name="data">array of bytes</param>
        /// <param name="offset">to data in the data array</param>
        /// <param name="length">of data in the data array</param>
        protected internal ICCSignatureType(int signature, byte[] data, int offset, int length)
            : base(signature, data, offset, offset + 2 * ICCProfile.int_size)
        {
            type = ICCProfile.GetInt(data, offset);
            reserved = ICCProfile.GetInt(data, offset + ICCProfile.int_size);
            signature = ICCProfile.GetInt(data, offset + ICCProfile.int_size);
        }
    }
}