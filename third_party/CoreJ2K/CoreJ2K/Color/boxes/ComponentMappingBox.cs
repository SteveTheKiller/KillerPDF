/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>
using System;
using System.Collections.Generic;
using ICCProfile = CoreJ2K.Icc.ICCProfile;
using io_RandomAccessIO = CoreJ2K.j2k.io.RandomAccessIO;

namespace CoreJ2K.Color.Boxes
{

    /// <summary> This class maps the components in the codestream
    /// to channels in the image.  It models the Component
    /// Mapping box in the JP2 header.
    /// 
    /// </summary>
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public sealed class ComponentMappingBox : JP2Box
    {
        public int NChannels { get; private set; }

        private readonly List<byte[]> map = new List<byte[]>(10);

        /// <summary> Construct a ComponentMappingBox from an input image.</summary>
        /// <param name="in">RandomAccessIO jp2 image
        /// </param>
        /// <param name="boxStart">offset to the start of the box in the image
        /// </param>
        /// <exception cref="IOException,">ColorSpaceException 
        /// </exception>
        public ComponentMappingBox(io_RandomAccessIO inStream, int boxStart) : base(inStream, boxStart)
        {
            readBox();
        }

        /// <summary>Analyze the box content. </summary>
        internal void readBox()
        {
            NChannels = (boxEnd - dataStart) / 4;
            inStream.seek(dataStart);
            for (var offset = dataStart; offset < boxEnd; offset += 4)
            {
                var mapping = new byte[4];
                inStream.readFully(mapping, 0, 4);
                map.Add(mapping);
            }
        }

        /* Return the component mapped to the channel. */
        public int GetCMP(int channel)
        {
            var mapping = map[channel];
            return ICCProfile.GetShort(mapping, 0) & 0x0000ffff;
        }

        /// <summary>Return the channel type. </summary>
        public short GetMTYP(int channel)
        {
            var mapping = map[channel];
            return (short)(mapping[2] & 0x00ff);
        }

        /// <summary>Return the palette index for the channel. </summary>
        public short GetPCOL(int channel)
        {
            var mapping = map[channel];
            return (short)(mapping[3] & 0x000ff);
        }

        /// <summary>Return a suitable String representation of the class instance. </summary>
        public override string ToString()
        {
            var rep = new System.Text.StringBuilder("[ComponentMappingBox ").Append("  ");
            rep.Append("nChannels= ").Append(Convert.ToString(NChannels));
            System.Collections.IEnumerator Enum = map.GetEnumerator();
            while (Enum.MoveNext())
            {
                var bfr = (byte[])Enum.Current;
                rep.Append(Environment.NewLine).Append("  ").Append("CMP= ").Append(Convert.ToString(GetCMP(bfr))).Append(", ");
                rep.Append("MTYP= ").Append(Convert.ToString(GetMTYP(bfr))).Append(", ");
                rep.Append("PCOL= ").Append(Convert.ToString(GetPCOL(bfr)));
            }
            rep.Append("]");
            return rep.ToString();
        }

        private int GetCMP(byte[] mapping)
        {
            return ICCProfile.GetShort(mapping, 0) & 0x0000ffff;
        }

        private short GetMTYP(byte[] mapping)
        {
            return (short)(mapping[2] & 0x00ff);
        }

        private short GetPCOL(byte[] mapping)
        {
            return (short)(mapping[3] & 0x000ff);
        }
        static ComponentMappingBox()
        {
            {
                type = 0x636d6170;
            }
        }
    }
}