// Copyright (c) 2026 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.IO;
using CoreJ2K.Util;

namespace CoreJ2K.j2k.codestream
{
    /// <summary>Kind of non-linearity carried by an <see cref="NLTMarkerSegment"/>.</summary>
    public enum NLTType
    {
        /// <summary>No transformation (Tnlt = 0).</summary>
        None = 0,
        /// <summary>Parametric gamma power-law (Tnlt = 1).</summary>
        Gamma = 1,
        /// <summary>Explicit lookup table (Tnlt = 2).</summary>
        LookupTable = 2
    }

    /// <summary>
    /// Models the Non-linearity point transformation (NLT) marker segment of
    /// JPEG 2000 Part 2 (ISO/IEC 15444-2, marker 0xFF76) and applies the
    /// corresponding per-sample point transform.
    /// </summary>
    /// <remarks>
    /// The NLT marker defines, per component (or for all components), a monotonic
    /// point transformation applied to image samples before the multiple-component
    /// and wavelet transforms on the encoder, and inverted as the final step on the
    /// decoder. This class captures the standard's field semantics — the component
    /// selector (Cnlt), the transformed-domain bit depth/sign (BDnlt), and the type
    /// (Tnlt) — and serializes the type-specific payload using the following
    /// documented layout (big-endian; the 2-byte marker is excluded from Lnlt):
    /// <code>
    ///   NLT   (2)  = 0xFF76                         (marker, not counted in Lnlt)
    ///   Lnlt  (2)  = bytes from Lnlt through payload end
    ///   Cnlt  (2)  = component index, or 0xFFFF for all components
    ///   BDnlt (1)  = bits 0..6: depth-1, bit 7: sign (1 = signed)
    ///   Tnlt  (1)  = 0 none | 1 gamma | 2 lookup table
    ///   Gamma:  Enlt (4) = exponent E as unsigned 16.16 fixed point
    ///   LUT:    Npts (4) = entry count; DTnlt (1) = value width (0:1B,1:2B,2:4B);
    ///           then Npts signed values (original-domain sample for transformed index i)
    /// </code>
    /// The gamma exponent encoding and LUT payload layout are this library's
    /// representation of the NLT concept; they round-trip faithfully through CoreJ2K
    /// but have not been validated against third-party NLT producers.
    /// </remarks>
    public class NLTMarkerSegment
    {
        /// <summary>Cnlt value meaning the segment applies to every component.</summary>
        public const int AllComponents = 0xFFFF;

        /// <summary>Component index this segment applies to, or <see cref="AllComponents"/>.</summary>
        public int ComponentIndex { get; set; } = AllComponents;

        /// <summary>Transformed-domain bit depth (1..38).</summary>
        public int BitDepth { get; set; } = 8;

        /// <summary>Whether transformed-domain samples are signed.</summary>
        public bool Signed { get; set; }

        /// <summary>The kind of non-linearity.</summary>
        public NLTType Type { get; set; } = NLTType.None;

        /// <summary>Gamma exponent E (used when <see cref="Type"/> is <see cref="NLTType.Gamma"/>).</summary>
        public double GammaExponent { get; set; } = 1.0;

        /// <summary>
        /// Lookup table mapping a transformed sample value to its original-domain value.
        /// Index <c>i</c> corresponds to transformed value <c>MinValue + i</c>
        /// (used when <see cref="Type"/> is <see cref="NLTType.LookupTable"/>).
        /// </summary>
        public int[]? Lut { get; set; }

        /// <summary>Minimum representable sample value for the configured depth/sign.</summary>
        public long MinValue => Signed ? -(1L << (BitDepth - 1)) : 0L;

        /// <summary>Maximum representable sample value for the configured depth/sign.</summary>
        public long MaxValue => Signed ? (1L << (BitDepth - 1)) - 1 : (1L << BitDepth) - 1;

        // Cached inverse of the LUT (original value -> transformed sample), built lazily.
        // forwardMap is indexed by (original - MinValue); forwardFilled marks which slots
        // have an exact preimage (the rest fall through to identity in ForwardSample).
        private int[]? forwardMap;
        private bool[]? forwardFilled;

        /// <summary>Returns true if this segment applies to the given component index.</summary>
        public bool AppliesTo(int component) =>
            ComponentIndex == AllComponents || ComponentIndex == component;

        #region Transform

        /// <summary>
        /// Applies the inverse transform (decoder direction): maps a transformed-domain
        /// sample back to the original-domain sample.
        /// </summary>
        public int InverseSample(int transformed)
        {
            switch (Type)
            {
                case NLTType.None:
                    return transformed;

                case NLTType.LookupTable:
                {
                    if (Lut == null || Lut.Length == 0) return transformed;
                    var idx = (int)(transformed - MinValue);
                    // Values outside the table domain pass through unchanged (identity),
                    // so a table that permutes its own domain is globally invertible.
                    if (idx < 0 || idx >= Lut.Length) return transformed;
                    return Lut[idx];
                }

                case NLTType.Gamma:
                {
                    var span = (double)(MaxValue - MinValue);
                    if (span <= 0) return transformed;
                    var y = (transformed - MinValue) / span;
                    if (y < 0) y = 0; else if (y > 1) y = 1;
                    var x = Math.Pow(y, GammaExponent);
                    return (int)Math.Round(MinValue + x * span);
                }

                default:
                    return transformed;
            }
        }

        /// <summary>
        /// Applies the forward transform (encoder direction): maps an original-domain
        /// sample to its transformed-domain representation.
        /// </summary>
        public int ForwardSample(int original)
        {
            switch (Type)
            {
                case NLTType.None:
                    return original;

                case NLTType.LookupTable:
                {
                    if (Lut == null || Lut.Length == 0) return original;
                    EnsureForwardMap();
                    var idx = (int)(original - MinValue);
                    // Identity outside the mapped domain (mirrors InverseSample).
                    if (idx < 0 || idx >= forwardMap!.Length || !forwardFilled![idx]) return original;
                    return forwardMap[idx];
                }

                case NLTType.Gamma:
                {
                    var span = (double)(MaxValue - MinValue);
                    if (span <= 0) return original;
                    var x = (original - MinValue) / span;
                    if (x < 0) x = 0; else if (x > 1) x = 1;
                    var y = Math.Pow(x, 1.0 / GammaExponent);
                    return (int)Math.Round(MinValue + y * span);
                }

                default:
                    return original;
            }
        }

        // Builds the original-value -> transformed-value map from the LUT. The map domain
        // matches the LUT's transformed domain ([MinValue, MinValue + Lut.Length)). A slot
        // is marked filled when some transformed index maps back to that original value;
        // for a table that permutes its own domain every slot is filled and the mapping is
        // exact. Unfilled slots fall through to identity in ForwardSample.
        private void EnsureForwardMap()
        {
            if (forwardMap != null || Lut == null) return;

            var n = Lut.Length;
            var map = new int[n];
            var filled = new bool[n];

            for (var i = 0; i < n; i++)
            {
                var slot = (int)(Lut[i] - MinValue);
                if (slot >= 0 && slot < n && !filled[slot])
                {
                    map[slot] = (int)(MinValue + i);
                    filled[slot] = true;
                }
            }

            forwardMap = map;
            forwardFilled = filled;
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Reads an NLT marker segment from <paramref name="r"/>, assuming the 2-byte
        /// marker code has already been consumed and the reader is positioned at Lnlt.
        /// </summary>
        public static NLTMarkerSegment Read(BinaryReader r)
        {
            var seg = new NLTMarkerSegment();

            r.ReadUInt16();                  // Lnlt (length; recomputed on write)
            seg.ComponentIndex = r.ReadUInt16();

            var bd = r.ReadByte();
            seg.BitDepth = (bd & 0x7F) + 1;
            seg.Signed = (bd & 0x80) != 0;

            seg.Type = (NLTType)r.ReadByte();

            switch (seg.Type)
            {
                case NLTType.Gamma:
                    seg.GammaExponent = r.ReadUInt32() / 65536.0;
                    break;

                case NLTType.LookupTable:
                {
                    var npts = (int)r.ReadUInt32();
                    var width = r.ReadByte();
                    var lut = new int[npts];
                    for (var i = 0; i < npts; i++)
                        lut[i] = ReadSignedValue(r, width);
                    seg.Lut = lut;
                    break;
                }
            }

            return seg;
        }

        /// <summary>
        /// Writes the complete NLT marker segment (including the 0xFF76 marker code)
        /// to <paramref name="w"/>, which must be big-endian.
        /// </summary>
        public void Write(BinaryWriter w)
        {
            var width = LutValueWidth();

            // Lnlt counts every byte from the Lnlt field through the payload.
            var lnlt = 2 + 2 + 1 + 1; // Lnlt + Cnlt + BDnlt + Tnlt
            switch (Type)
            {
                case NLTType.Gamma: lnlt += 4; break;
                case NLTType.LookupTable: lnlt += 4 + 1 + (Lut?.Length ?? 0) * width; break;
            }

            w.Write(Markers.NLT);                  // marker 0xFF76
            w.Write((ushort)lnlt);                 // Lnlt
            w.Write((ushort)ComponentIndex);       // Cnlt

            var bd = (byte)(((BitDepth - 1) & 0x7F) | (Signed ? 0x80 : 0x00));
            w.Write(bd);                           // BDnlt
            w.Write((byte)Type);                   // Tnlt

            switch (Type)
            {
                case NLTType.Gamma:
                    w.Write((uint)Math.Round(GammaExponent * 65536.0));
                    break;

                case NLTType.LookupTable:
                    w.Write((uint)(Lut?.Length ?? 0));
                    w.Write((byte)WidthCode(width));
                    if (Lut != null)
                        foreach (var v in Lut)
                            WriteSignedValue(w, v, width);
                    break;
            }
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

        /// <summary>Parses a segment from bytes that begin with the 0xFF76 marker code.</summary>
        public static NLTMarkerSegment FromBytes(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new EndianBinaryReader(ms, true);
            r.ReadInt16(); // consume marker
            return Read(r);
        }

        private int LutValueWidth()
        {
            if (Lut == null || Lut.Length == 0) return 1;

            // Values are stored as signed two's complement, so the width is chosen by the
            // signed range. (An unsigned value such as 255 therefore needs two bytes.)
            long min = long.MaxValue, max = long.MinValue;
            foreach (var v in Lut)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }

            if (min >= sbyte.MinValue && max <= sbyte.MaxValue) return 1;
            if (min >= short.MinValue && max <= short.MaxValue) return 2;
            return 4;
        }

        private static int WidthCode(int width) => width == 1 ? 0 : width == 2 ? 1 : 2;

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
            var comp = ComponentIndex == AllComponents ? "all" : ComponentIndex.ToString();
            return $"NLT[comp={comp}, depth={BitDepth}{(Signed ? "S" : "U")}, type={Type}" +
                   (Type == NLTType.Gamma ? $", E={GammaExponent:0.###}" : "") +
                   (Type == NLTType.LookupTable ? $", npts={Lut?.Length ?? 0}" : "") + "]";
        }
    }
}
