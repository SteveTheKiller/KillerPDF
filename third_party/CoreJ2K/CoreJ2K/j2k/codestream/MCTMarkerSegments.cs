// Copyright (c) 2026 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using System.IO;
using CoreJ2K.Util;

namespace CoreJ2K.j2k.codestream
{
    /// <summary>Type of a multiple-component transform array (MCT marker).</summary>
    public enum MctArrayType
    {
        /// <summary>Dependency (lifting) transform array.</summary>
        Dependency = 0,
        /// <summary>Decorrelation (matrix) transform array.</summary>
        Decorrelation = 1,
        /// <summary>Per-component offset (DC) array.</summary>
        Offset = 2
    }

    /// <summary>Element data type of an MCT array.</summary>
    public enum MctElementType
    {
        Int16 = 0,
        Int32 = 1,
        Float32 = 2,
        Float64 = 3
    }

    /// <summary>The kind of transform a component collection (MCC) applies.</summary>
    public enum MctTransformType
    {
        /// <summary>Array-based decorrelation (matrix multiply) across the components.</summary>
        Matrix = 0,
        /// <summary>Dependency-based reversible lifting/prediction across the components.</summary>
        Dependency = 1,
        /// <summary>Reversible 5/3 wavelet transform across the components.</summary>
        Wavelet = 2
    }

    /// <summary>
    /// Models a Multiple Component Transform definition (MCT, 0xFF74) marker segment of
    /// JPEG 2000 Part 2 (ISO/IEC 15444-2): a single reusable transform array (a
    /// decorrelation matrix, a dependency array, or an offset vector).
    /// </summary>
    /// <remarks>
    /// Layout (big-endian; marker code excluded from Lmct):
    /// <code>
    ///   Lmct  (2)  bytes from Lmct through array data
    ///   Zmct  (2)  segment index within the array (only 0 is produced/consumed here)
    ///   Imct  (2)  (ElementType &lt;&lt; 10) | (ArrayType &lt;&lt; 8) | (Index &amp; 0xFF)
    ///   data       array values serialized per ElementType
    /// </code>
    /// Single-segment arrays only (Zmct = 0). This is CoreJ2K's representation of the MCT
    /// concept; it round-trips faithfully but is not validated against third-party producers.
    /// </remarks>
    public class MctArrayMarkerSegment
    {
        public int Index { get; set; }
        public MctArrayType ArrayType { get; set; } = MctArrayType.Decorrelation;
        public MctElementType ElementType { get; set; } = MctElementType.Float64;

        /// <summary>The array values (row-major for a decorrelation matrix, a vector for an offset).</summary>
        public double[] Values { get; set; } = Array.Empty<double>();

        public static MctArrayMarkerSegment Read(BinaryReader r)
        {
            var lmct = r.ReadUInt16();
            r.ReadUInt16(); // Zmct (segment index; single-segment only)
            var imct = r.ReadUInt16();

            var seg = new MctArrayMarkerSegment
            {
                Index = imct & 0xFF,
                ArrayType = (MctArrayType)((imct >> 8) & 0x3),
                ElementType = (MctElementType)((imct >> 10) & 0x3)
            };

            var dataBytes = lmct - 6;
            var size = ElementSize(seg.ElementType);
            var count = size > 0 ? dataBytes / size : 0;
            var values = new double[count];
            for (var i = 0; i < count; i++) values[i] = ReadElement(r, seg.ElementType);
            seg.Values = values;
            return seg;
        }

        public void Write(BinaryWriter w)
        {
            var size = ElementSize(ElementType);
            var lmct = 6 + Values.Length * size;
            var imct = (((int)ElementType & 0x3) << 10) | (((int)ArrayType & 0x3) << 8) | (Index & 0xFF);

            w.Write(Markers.MCT);
            w.Write((ushort)lmct);
            w.Write((ushort)0); // Zmct
            w.Write((ushort)imct);
            foreach (var v in Values) WriteElement(w, ElementType, v);
        }

        private static int ElementSize(MctElementType t) => t switch
        {
            MctElementType.Int16 => 2,
            MctElementType.Int32 => 4,
            MctElementType.Float32 => 4,
            MctElementType.Float64 => 8,
            _ => 8
        };

        private static double ReadElement(BinaryReader r, MctElementType t) => t switch
        {
            MctElementType.Int16 => r.ReadInt16(),
            MctElementType.Int32 => r.ReadInt32(),
            MctElementType.Float32 => r.ReadSingle(),
            MctElementType.Float64 => r.ReadDouble(),
            _ => r.ReadDouble()
        };

        private static void WriteElement(BinaryWriter w, MctElementType t, double v)
        {
            switch (t)
            {
                case MctElementType.Int16: w.Write((short)Math.Round(v)); break;
                case MctElementType.Int32: w.Write((int)Math.Round(v)); break;
                case MctElementType.Float32: w.Write((float)v); break;
                default: w.Write(v); break;
            }
        }
    }

    /// <summary>
    /// Models a Multiple Component Collection (MCC, 0xFF75) marker segment: a collection of
    /// components and the (matrix decorrelation) transform applied to them, referencing MCT
    /// arrays by index. A single transform stage per collection is modelled.
    /// </summary>
    /// <remarks>
    /// Layout (big-endian; CoreJ2K representation):
    /// <code>
    ///   Lmcc (2)
    ///   Imcc (1)  collection index
    ///   Flags(1)  bit0 = irreversible
    ///   Ttyp (1)  transform type (0 matrix, 1 dependency, 2 wavelet)
    ///   Didx (1)  decorrelation/dependency array index (unused for wavelet; 0xFF if none)
    ///   Oidx (1)  offset array index (0xFF = none)
    ///   Ncmp (2)  number of components
    ///   comps     Ncmp x 2 bytes (component indices, input == output)
    /// </code>
    /// </remarks>
    public class MccMarkerSegment
    {
        public const int NoOffset = 0xFF;

        public int Index { get; set; }
        public bool Irreversible { get; set; }
        public MctTransformType TransformType { get; set; } = MctTransformType.Matrix;
        public int DecorrelationArrayIndex { get; set; } = NoOffset;
        public int OffsetArrayIndex { get; set; } = NoOffset;
        public int[] Components { get; set; } = Array.Empty<int>();

        public static MccMarkerSegment Read(BinaryReader r)
        {
            r.ReadUInt16(); // Lmcc
            var seg = new MccMarkerSegment
            {
                Index = r.ReadByte(),
                Irreversible = (r.ReadByte() & 0x1) != 0,
                TransformType = (MctTransformType)r.ReadByte(),
                DecorrelationArrayIndex = r.ReadByte(),
                OffsetArrayIndex = r.ReadByte()
            };
            var n = r.ReadUInt16();
            var comps = new int[n];
            for (var i = 0; i < n; i++) comps[i] = r.ReadUInt16();
            seg.Components = comps;
            return seg;
        }

        public void Write(BinaryWriter w)
        {
            var lmcc = 2 + 1 + 1 + 1 + 1 + 1 + 2 + Components.Length * 2;
            w.Write(Markers.MCC);
            w.Write((ushort)lmcc);
            w.Write((byte)Index);
            w.Write((byte)(Irreversible ? 0x1 : 0x0));
            w.Write((byte)TransformType);
            w.Write((byte)DecorrelationArrayIndex);
            w.Write((byte)OffsetArrayIndex);
            w.Write((ushort)Components.Length);
            foreach (var c in Components) w.Write((ushort)c);
        }
    }

    /// <summary>
    /// Models a Multiple Component transformation Ordering (MCO, 0xFF77) marker segment:
    /// the ordered list of MCC collection indices to apply.
    /// </summary>
    public class McoMarkerSegment
    {
        public int[] Stages { get; set; } = Array.Empty<int>();

        public static McoMarkerSegment Read(BinaryReader r)
        {
            r.ReadUInt16(); // Lmco
            var n = r.ReadByte();
            var stages = new int[n];
            for (var i = 0; i < n; i++) stages[i] = r.ReadByte();
            return new McoMarkerSegment { Stages = stages };
        }

        public void Write(BinaryWriter w)
        {
            var lmco = 2 + 1 + Stages.Length;
            w.Write(Markers.MCO);
            w.Write((ushort)lmco);
            w.Write((byte)Stages.Length);
            foreach (var s in Stages) w.Write((byte)s);
        }
    }

    /// <summary>
    /// Models a Component Bit Depth definition (CBD, 0xFF78) marker segment: the bit depth
    /// and sign of each (intermediate) component produced by a multiple component transform.
    /// </summary>
    public class CbdMarkerSegment
    {
        /// <summary>Per-component packed bit depth: bits 0-6 = depth-1, bit 7 = signed.</summary>
        public byte[] ComponentDepths { get; set; } = Array.Empty<byte>();

        public int GetBitDepth(int c) => (ComponentDepths[c] & 0x7F) + 1;
        public bool IsSigned(int c) => (ComponentDepths[c] & 0x80) != 0;

        public static CbdMarkerSegment Read(BinaryReader r)
        {
            r.ReadUInt16(); // Lcbd
            var n = r.ReadUInt16();
            var depths = new byte[n];
            for (var i = 0; i < n; i++) depths[i] = r.ReadByte();
            return new CbdMarkerSegment { ComponentDepths = depths };
        }

        public void Write(BinaryWriter w)
        {
            var lcbd = 2 + 2 + ComponentDepths.Length;
            w.Write(Markers.CBD);
            w.Write((ushort)lcbd);
            w.Write((ushort)ComponentDepths.Length);
            foreach (var b in ComponentDepths) w.Write(b);
        }

        public static CbdMarkerSegment FromComponents(int count, int bitDepth, bool signed)
        {
            var packed = (byte)(((bitDepth - 1) & 0x7F) | (signed ? 0x80 : 0x00));
            var depths = new byte[count];
            for (var i = 0; i < count; i++) depths[i] = packed;
            return new CbdMarkerSegment { ComponentDepths = depths };
        }
    }

    /// <summary>
    /// An assembled, ready-to-apply matrix multiple-component transform stage: the matrix the
    /// decoder applies (synthesis) across <see cref="Components"/>, plus an optional per-output
    /// offset added on synthesis. Built from the parsed MCT/MCC/MCO markers (decode) or from a
    /// caller-supplied spec (encode).
    /// </summary>
    public class MctStage
    {
        /// <summary>The kind of transform this stage applies.</summary>
        public MctTransformType TransformType { get; set; } = MctTransformType.Matrix;

        /// <summary>The component indices this stage transforms (input == output).</summary>
        public int[] Components { get; set; } = Array.Empty<int>();

        /// <summary>
        /// The N×N coefficient matrix, row-major. For <see cref="MctTransformType.Matrix"/> it is the
        /// matrix applied (synthesis on decode, analysis on encode). For
        /// <see cref="MctTransformType.Dependency"/> it is the strictly-lower-triangular prediction
        /// matrix P (the same P is used on both sides). Unused for <see cref="MctTransformType.Wavelet"/>.
        /// </summary>
        public double[,] Matrix { get; set; } = new double[0, 0];

        /// <summary>Optional per-component offset added after the matrix (length N), or null.</summary>
        public double[]? Offset { get; set; }
    }
}
