// Copyright (c) 2026 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.IO;
using CoreJ2K.Util;

namespace CoreJ2K.j2k.codestream
{
    /// <summary>
    /// Models the Variable DC Offset (DCO) marker segment of JPEG 2000 Part 2
    /// (ISO/IEC 15444-2, marker 0xFF70).
    /// </summary>
    /// <remarks>
    /// The DCO marker specifies per-component integer DC offsets that are removed
    /// from image samples on the encoder (before the wavelet transform) and restored
    /// on the decoder (after the inverse transform). This is useful for multi-channel
    /// scientific or medical data where individual bands carry different DC pedestals.
    ///
    /// The offsets operate in the same signed-centred domain as the rest of the
    /// JPEG 2000 coding pipeline: for an 8-bit unsigned image whose samples have
    /// already been DC-level-shifted by −128, an offset of +10 means each sample
    /// of that component is further reduced by 10 before coding and raised by 10
    /// on decode.
    ///
    /// CoreJ2K byte layout (big-endian; the 2-byte marker is excluded from Ldco):
    /// <code>
    ///   DCO   (2)  = 0xFF70                  (marker, not counted in Ldco)
    ///   Ldco  (2)  = bytes from Ldco through payload end
    ///   Sdco  (1)  = bits 1-0: offset width code (0=1 byte, 1=2 bytes, 2=4 bytes)
    ///   Nc    (2)  = number of component offsets
    ///   Oidco[i]   = signed offset for component i, width bytes each
    /// </code>
    /// This layout is CoreJ2K's documented representation of DCO and has not been
    /// validated against third-party DCO producers.
    /// </remarks>
    public class DCOMarkerSegment
    {
        /// <summary>
        /// Per-component DC offsets. <c>Offsets[c]</c> is the value that is subtracted
        /// from component <c>c</c> samples on encode and added back on decode.
        /// </summary>
        public int[] Offsets { get; set; } = Array.Empty<int>();

        /// <summary>Number of components covered by this segment.</summary>
        public int NumComponents => Offsets.Length;

        #region Serialization

        /// <summary>
        /// Reads a DCO marker segment from <paramref name="r"/>, assuming the 2-byte
        /// marker code has already been consumed and the reader is positioned at Ldco.
        /// </summary>
        public static DCOMarkerSegment Read(BinaryReader r)
        {
            var seg = new DCOMarkerSegment();

            r.ReadUInt16(); // Ldco (recomputed on write)

            var sdco = r.ReadByte();
            var widthCode = sdco & 0x03;

            var nc = r.ReadUInt16();
            var offsets = new int[nc];
            for (var i = 0; i < nc; i++)
                offsets[i] = ReadSignedValue(r, widthCode);

            seg.Offsets = offsets;
            return seg;
        }

        /// <summary>
        /// Writes the complete DCO marker segment (including the 0xFF70 marker code)
        /// to <paramref name="w"/>, which must be big-endian.
        /// </summary>
        public void Write(BinaryWriter w)
        {
            var widthCode = OffsetWidthCode();
            var width = WidthFromCode(widthCode);
            var nc = Offsets.Length;

            // Ldco = itself(2) + Sdco(1) + Nc(2) + Nc * width
            var ldco = 2 + 1 + 2 + nc * width;

            w.Write(Markers.DCO);         // marker 0xFF70
            w.Write((ushort)ldco);        // Ldco
            w.Write((byte)widthCode);     // Sdco
            w.Write((ushort)nc);          // Nc
            foreach (var v in Offsets)
                WriteSignedValue(w, v, width);
        }

        /// <summary>Serializes the segment to a byte array, including the marker code.</summary>
        public byte[] ToBytes()
        {
            using var ms = new MemoryStream();
            using var w = new EndianBinaryWriter(ms, true);
            Write(w);
            w.Flush();
            return ms.ToArray();
        }

        /// <summary>Parses a segment from bytes that begin with the 0xFF70 marker code.</summary>
        public static DCOMarkerSegment FromBytes(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new EndianBinaryReader(ms, true);
            r.ReadInt16(); // consume marker
            return Read(r);
        }

        // Choose the narrowest signed width code that fits all offsets.
        private int OffsetWidthCode()
        {
            if (Offsets == null || Offsets.Length == 0) return 0;
            long min = long.MaxValue, max = long.MinValue;
            foreach (var v in Offsets)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }
            if (min >= sbyte.MinValue && max <= sbyte.MaxValue) return 0;
            if (min >= short.MinValue && max <= short.MaxValue) return 1;
            return 2;
        }

        private static int WidthFromCode(int code) => code == 0 ? 1 : code == 1 ? 2 : 4;

        private static int ReadSignedValue(BinaryReader r, int widthCode)
        {
            switch (widthCode)
            {
                case 0: return (sbyte)r.ReadByte();
                case 1: return r.ReadInt16();
                default: return r.ReadInt32();
            }
        }

        private static void WriteSignedValue(BinaryWriter w, int value, int width)
        {
            switch (width)
            {
                case 1: w.Write((byte)(sbyte)value); break;
                case 2: w.Write((short)value); break;
                default: w.Write(value); break;
            }
        }

        #endregion

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder("DCO[");
            for (var i = 0; i < Offsets.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"c{i}={Offsets[i]}");
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
