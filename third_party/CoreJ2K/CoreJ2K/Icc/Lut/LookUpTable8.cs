/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>
using System;
using Tags_ICCCurveType = CoreJ2K.Icc.Tags.ICCCurveType;

namespace CoreJ2K.Icc.Lut
{

    /// <summary> Toplevel class for a byte [] lut.
    /// 
    /// </summary>
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public abstract class LookUpTable8 : LookUpTable
    {

        /// <summary>Maximum output value of the LUT </summary>
        protected internal byte dwMaxOutput;
        /// <summary>The lut values.                 </summary>
        // Maximum output value of the LUT
        protected internal byte[] lut;


        /// <summary> Create an abbreviated string representation of a 16 bit lut.</summary>
        /// <returns> the lut as a String
        /// </returns>
        public override string ToString()
        {
            var rep = new System.Text.StringBuilder("[LookUpTable8 ");
            //int row, col;
            rep.Append($"max= {dwMaxOutput}");
            rep.Append($", nentries= {dwMaxOutput}");
            return rep.Append("]").ToString();
        }



        public virtual string toStringWholeLut()
        {
            var rep = new System.Text.StringBuilder($"LookUpTable8{Environment.NewLine}");
            rep.Append($"maxOutput = {dwMaxOutput}{Environment.NewLine}");
            for (var i = 0; i < dwNumInput; ++i)
                rep.Append($"lut[{i}] = {lut[i]}{Environment.NewLine}");
            return rep.Append("]").ToString();
        }

        protected internal LookUpTable8(int dwNumInput, byte dwMaxOutput) : base(null, dwNumInput)
        {
            lut = new byte[dwNumInput];
            this.dwMaxOutput = dwMaxOutput;
        }


        /// <summary> Create the string representation of a 16 bit lut.</summary>
        /// <returns> the lut as a String
        /// </returns>
        protected internal LookUpTable8(Tags_ICCCurveType curve, int dwNumInput, byte dwMaxOutput) : base(curve, dwNumInput)
        {
            this.dwMaxOutput = dwMaxOutput;
            lut = new byte[dwNumInput];
        }

        /// <summary> lut accessor</summary>
        /// <param name="index">of the element
        /// </param>
        /// <returns> the lut [index]
        /// </returns>
        public byte elementAt(int index)
        {
            return lut[index];
        }
    }
}