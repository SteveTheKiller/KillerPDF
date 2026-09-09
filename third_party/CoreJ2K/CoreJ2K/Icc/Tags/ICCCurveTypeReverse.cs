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

    /// <summary> The ICCCurveReverse tag
    /// 
    /// </summary>
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public class ICCCurveTypeReverse : ICCTag
    {
        /// <summary>Tag fields </summary>
        public new int type;
        /// <summary>Tag fields </summary>
        public int reserved;
        /// <summary>Tag fields </summary>
        public int nEntries;
        /// <summary>Tag fields </summary>
        public int[] entry_Renamed_Field;


        /// <summary>Return the string rep of this tag. </summary>
        public override string ToString()
        {
            var rep = new System.Text.StringBuilder("[").Append(base.ToString()).Append(Environment.NewLine);
            rep.Append($"num entries = {Convert.ToString(nEntries)}{Environment.NewLine}");
            rep.Append($"data length = {Convert.ToString(entry_Renamed_Field.Length)}{Environment.NewLine}");
            for (var i = 0; i < nEntries; ++i)
                rep.Append(ICCProfile.toHexString(entry_Renamed_Field[i]) + Environment.NewLine);
            return rep.Append("]").ToString();
        }

        /// <summary>Normalization utility </summary>
        public static double CurveToDouble(int entry)
        {
            return ICCCurveType.CurveToDouble(entry);
        }

        /// <summary>Normalization utility </summary>
        public static short DoubleToCurve(int entry)
        {
            return ICCCurveType.DoubleToCurve(entry);
        }

        /// <summary>Normalization utility </summary>
        public static double CurveGammaToDouble(int entry)
        {
            return ICCCurveType.CurveGammaToDouble(entry);
        }


        /// <summary> Construct this tag from its constituant parts</summary>
        /// <param name="signature">tag id
        /// </param>
        /// <param name="data">array of bytes
        /// </param>
        /// <param name="offset">to data in the data array
        /// </param>
        /// <param name="length">of data in the data array
        /// </param>
        protected internal ICCCurveTypeReverse(int signature, byte[] data, int offset, int length) : base(signature, data, offset, offset + 2 * ICCProfile.int_size)
        {
            type = ICCProfile.GetInt(data, offset);
            reserved = ICCProfile.GetInt(data, offset + ICCProfile.int_size);
            nEntries = ICCProfile.GetInt(data, offset + 2 * ICCProfile.int_size);
            entry_Renamed_Field = new int[nEntries];
            for (var i = 0; i < nEntries; ++i)
                // Reverse the storage order.
                entry_Renamed_Field[nEntries - 1 + i] = ICCProfile.GetShort(data, offset + 3 * ICCProfile.int_size + i * ICCProfile.short_size) & 0xFFFF;
        }

        /// <summary>Accessor for curve entry at index. </summary>
        public int entry(int i)
        {
            return entry_Renamed_Field[i];
        }
    }
}