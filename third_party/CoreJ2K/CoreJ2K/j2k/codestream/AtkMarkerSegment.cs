// Copyright (c) 2026 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using System.IO;
using CoreJ2K.Util;

namespace CoreJ2K.j2k.codestream
{
    /// <summary>
    /// One lifting step of an arbitrary transformation kernel (ATK).
    /// </summary>
    /// <remarks>
    /// A step updates every sample of one parity (alternating between steps) from its
    /// opposite-parity neighbours. For a sample at position <c>p</c> the neighbour taps
    /// are at <c>p + 2*(k - Offset) - 1</c> for <c>k = 0 .. Coefficients.Length-1</c>,
    /// so with <c>Offset = 0</c> a two-tap step reads the samples immediately left and
    /// right of <c>p</c>. Signal boundaries use whole-sample symmetric extension.
    ///
    /// Reversible kernels update with
    /// <c>x[p] += floor((sum_k A[k]*x[nbr(k)] + Beta) / 2^Epsilon)</c>
    /// where <c>A[k]</c> are integers; irreversible kernels update with
    /// <c>x[p] += sum_k A[k]*x[nbr(k)]</c> using real coefficients.
    /// </remarks>
    public class AtkLiftingStep
    {
        /// <summary>Lifting coefficients A[k]. For reversible kernels each value must be an integer.</summary>
        public double[] Coefficients { get; set; } = Array.Empty<double>();

        /// <summary>Reversible kernels only: the downshift E applied to the weighted sum (0..31).</summary>
        public int Epsilon { get; set; }

        /// <summary>Reversible kernels only: the additive rounding offset B applied before the shift.</summary>
        public int Beta { get; set; }

        /// <summary>Tap placement offset O; the k-th tap reads position p + 2*(k - O) - 1.</summary>
        public int Offset { get; set; }

        /// <summary>The largest distance (in samples) any tap of this step reaches from the updated position.</summary>
        public int Reach
        {
            get
            {
                var reach = 1;
                for (var k = 0; k < Coefficients.Length; k++)
                {
                    reach = Math.Max(reach, Math.Abs(2 * (k - Offset) - 1));
                }
                return reach;
            }
        }
    }

    /// <summary>
    /// Models the Arbitrary Transformation Kernel (ATK) marker segment of JPEG 2000
    /// Part 2 (ISO/IEC 15444-2, marker 0xFF79).
    /// </summary>
    /// <remarks>
    /// An ATK marker segment defines a custom lifting-based wavelet kernel that COD/COC
    /// marker segments can reference through their SPcod/SPcoc transformation byte:
    /// values 0 and 1 select the Part 1 9/7 and 5/3 filters, while values 2..127
    /// select the ATK segment with the matching <see cref="Index"/>.
    ///
    /// CoreJ2K byte layout (big-endian; the 2-byte marker is excluded from Latk):
    /// <code>
    ///   ATK   (2)  = 0xFF79                 (marker, not counted in Latk)
    ///   Latk  (2)  = bytes from Latk through payload end
    ///   Satk  (2)  = bits 7-0: kernel index (2..127)
    ///                bit 8: 1 = reversible (integer lifting), 0 = irreversible
    ///                bit 9: 1 = first lifting step updates odd samples (high-pass first)
    ///   LGatk (8)  = low-pass subband gain, float64  (irreversible only)
    ///   HGatk (8)  = high-pass subband gain, float64 (irreversible only)
    ///   Natk  (1)  = number of lifting steps
    ///   per step:
    ///     Oatk  (1)  = signed tap placement offset
    ///     LCatk (1)  = number of coefficients
    ///     Eatk  (1)  = downshift, 0..31            (reversible only)
    ///     Batk  (2)  = signed rounding offset      (reversible only)
    ///     Aatk       = LCatk coefficients: int16 each (reversible)
    ///                  or float64 each (irreversible)
    /// </code>
    /// This layout follows the field semantics of the ISO/IEC 15444-2 ATK segment
    /// (index, reversibility, lifting steps with coefficients, shifts and offsets) but
    /// is CoreJ2K's documented representation: it round-trips faithfully through
    /// CoreJ2K and has not been validated against third-party ATK producers.
    /// </remarks>
    public class AtkMarkerSegment
    {
        /// <summary>Lowest kernel index available for custom kernels (0 and 1 are the Part 1 filters).</summary>
        public const int MinIndex = 2;

        /// <summary>Highest kernel index representable in the SPcod/SPcoc transformation byte.</summary>
        public const int MaxIndex = 127;

        /// <summary>The kernel index (2..127) that COD/COC transformation bytes reference.</summary>
        public int Index { get; set; } = MinIndex;

        /// <summary>True for integer (lossless-capable) lifting, false for real-valued lifting.</summary>
        public bool Reversible { get; set; } = true;

        /// <summary>Whether the first lifting step updates odd (high-pass) samples. Part 1 kernels do.</summary>
        public bool FirstStepUpdatesOdd { get; set; } = true;

        /// <summary>The lifting steps, applied in order on analysis and in reverse on synthesis.</summary>
        public List<AtkLiftingStep> Steps { get; set; } = new List<AtkLiftingStep>();

        /// <summary>Irreversible kernels only: scale factor applied to low-pass samples after analysis.</summary>
        public double LowGain { get; set; } = 1.0;

        /// <summary>Irreversible kernels only: scale factor applied to high-pass samples after analysis.</summary>
        public double HighGain { get; set; } = 1.0;

        /// <summary>The largest distance (in samples) the kernel reaches from any output position.</summary>
        public int Support
        {
            get
            {
                var support = 0;
                foreach (var step in Steps) support += step.Reach;
                return Math.Max(1, support);
            }
        }

        /// <summary>
        /// Validates the segment, throwing <see cref="ArgumentException"/> when it cannot be
        /// used as a wavelet kernel.
        /// </summary>
        public void Validate()
        {
            if (Index < MinIndex || Index > MaxIndex)
                throw new ArgumentException($"ATK kernel index must be in [{MinIndex},{MaxIndex}], got {Index}.");
            if (Steps == null || Steps.Count == 0)
                throw new ArgumentException("An ATK kernel requires at least one lifting step.");
            if (Steps.Count > 255)
                throw new ArgumentException("An ATK kernel supports at most 255 lifting steps.");
            foreach (var step in Steps)
            {
                if (step.Coefficients == null || step.Coefficients.Length == 0)
                    throw new ArgumentException("Every ATK lifting step requires at least one coefficient.");
                if (step.Coefficients.Length > 255)
                    throw new ArgumentException("An ATK lifting step supports at most 255 coefficients.");
                if (step.Offset < sbyte.MinValue || step.Offset > sbyte.MaxValue)
                    throw new ArgumentException($"ATK lifting step offset {step.Offset} is out of range.");
                if (Reversible)
                {
                    if (step.Epsilon < 0 || step.Epsilon > 31)
                        throw new ArgumentException($"ATK lifting step shift must be in [0,31], got {step.Epsilon}.");
                    if (step.Beta < short.MinValue || step.Beta > short.MaxValue)
                        throw new ArgumentException($"ATK lifting step rounding offset {step.Beta} is out of range.");
                    foreach (var c in step.Coefficients)
                    {
                        if (c != Math.Floor(c) || c < short.MinValue || c > short.MaxValue)
                            throw new ArgumentException(
                                $"Reversible ATK kernels require integer coefficients in [{short.MinValue},{short.MaxValue}], got {c}.");
                    }
                }
            }
            if (!Reversible && (LowGain == 0 || HighGain == 0 || double.IsNaN(LowGain) || double.IsNaN(HighGain)))
                throw new ArgumentException("Irreversible ATK kernels require non-zero, finite subband gains.");
        }

        /// <summary>Structural equality: same index, reversibility and lifting configuration.</summary>
        public bool StructurallyEquals(AtkMarkerSegment? other)
        {
            if (other == null) return false;
            if (Index != other.Index || Reversible != other.Reversible
                || FirstStepUpdatesOdd != other.FirstStepUpdatesOdd
                || Steps.Count != other.Steps.Count) return false;
            if (!Reversible && (LowGain != other.LowGain || HighGain != other.HighGain)) return false;
            for (var s = 0; s < Steps.Count; s++)
            {
                AtkLiftingStep a = Steps[s], b = other.Steps[s];
                if (a.Offset != b.Offset || a.Coefficients.Length != b.Coefficients.Length) return false;
                if (Reversible && (a.Epsilon != b.Epsilon || a.Beta != b.Beta)) return false;
                for (var k = 0; k < a.Coefficients.Length; k++)
                    if (a.Coefficients[k] != b.Coefficients[k]) return false;
            }
            return true;
        }

        #region Presets

        /// <summary>
        /// Creates a reversible kernel with the same lifting steps as the Part 1 5/3 filter,
        /// for interop testing and as a template for custom reversible kernels.
        /// </summary>
        public static AtkMarkerSegment CreateW5x3Equivalent(int index)
        {
            return new AtkMarkerSegment
            {
                Index = index,
                Reversible = true,
                FirstStepUpdatesOdd = true,
                Steps = new List<AtkLiftingStep>
                {
                    new AtkLiftingStep { Coefficients = new double[] { -1, -1 }, Epsilon = 1, Beta = 1 },
                    new AtkLiftingStep { Coefficients = new double[] { 1, 1 }, Epsilon = 2, Beta = 2 }
                }
            };
        }

        /// <summary>
        /// Creates an irreversible kernel with the same lifting steps and subband gains as
        /// the Part 1 9/7 filter, for interop testing and as a template for custom kernels.
        /// </summary>
        public static AtkMarkerSegment CreateW9x7Equivalent(int index)
        {
            const double alpha = -1.586134342;
            const double beta = -0.05298011854;
            const double gamma = 0.8829110762;
            const double delta = 0.4435068522;
            return new AtkMarkerSegment
            {
                Index = index,
                Reversible = false,
                FirstStepUpdatesOdd = true,
                LowGain = 0.8128930655,
                HighGain = 1.230174106,
                Steps = new List<AtkLiftingStep>
                {
                    new AtkLiftingStep { Coefficients = new[] { alpha, alpha } },
                    new AtkLiftingStep { Coefficients = new[] { beta, beta } },
                    new AtkLiftingStep { Coefficients = new[] { gamma, gamma } },
                    new AtkLiftingStep { Coefficients = new[] { delta, delta } }
                }
            };
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Reads an ATK marker segment from <paramref name="r"/>, assuming the 2-byte
        /// marker code has already been consumed and the reader is positioned at Latk.
        /// </summary>
        public static AtkMarkerSegment Read(BinaryReader r)
        {
            var seg = new AtkMarkerSegment();

            r.ReadUInt16(); // Latk (recomputed on write)

            var satk = r.ReadUInt16();
            seg.Index = satk & 0xFF;
            seg.Reversible = (satk & 0x100) != 0;
            seg.FirstStepUpdatesOdd = (satk & 0x200) != 0;

            if (!seg.Reversible)
            {
                seg.LowGain = r.ReadDouble();
                seg.HighGain = r.ReadDouble();
            }

            int natk = r.ReadByte();
            seg.Steps = new List<AtkLiftingStep>(natk);
            for (var s = 0; s < natk; s++)
            {
                var step = new AtkLiftingStep { Offset = (sbyte)r.ReadByte() };
                int lc = r.ReadByte();
                if (seg.Reversible)
                {
                    step.Epsilon = r.ReadByte();
                    step.Beta = r.ReadInt16();
                }
                var coeffs = new double[lc];
                for (var k = 0; k < lc; k++)
                    coeffs[k] = seg.Reversible ? r.ReadInt16() : r.ReadDouble();
                step.Coefficients = coeffs;
                seg.Steps.Add(step);
            }

            return seg;
        }

        /// <summary>
        /// Writes the complete ATK marker segment (including the 0xFF79 marker code)
        /// to <paramref name="w"/>, which must be big-endian.
        /// </summary>
        public void Write(BinaryWriter w)
        {
            Validate();

            // Latk = itself(2) + Satk(2) [+ gains(16)] + Natk(1) + steps
            var latk = 2 + 2 + (Reversible ? 0 : 16) + 1;
            foreach (var step in Steps)
                latk += 2 + (Reversible ? 3 + 2 * step.Coefficients.Length : 8 * step.Coefficients.Length);

            var satk = (Index & 0xFF)
                       | (Reversible ? 0x100 : 0)
                       | (FirstStepUpdatesOdd ? 0x200 : 0);

            w.Write(Markers.ATK);          // marker 0xFF79
            w.Write((ushort)latk);         // Latk
            w.Write((ushort)satk);         // Satk
            if (!Reversible)
            {
                w.Write(LowGain);          // LGatk
                w.Write(HighGain);         // HGatk
            }
            w.Write((byte)Steps.Count);    // Natk
            foreach (var step in Steps)
            {
                w.Write((byte)(sbyte)step.Offset);              // Oatk
                w.Write((byte)step.Coefficients.Length);        // LCatk
                if (Reversible)
                {
                    w.Write((byte)step.Epsilon);                // Eatk
                    w.Write((short)step.Beta);                  // Batk
                    foreach (var c in step.Coefficients)
                        w.Write((short)c);                      // Aatk
                }
                else
                {
                    foreach (var c in step.Coefficients)
                        w.Write(c);                             // Aatk
                }
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

        /// <summary>Parses a segment from bytes that begin with the 0xFF79 marker code.</summary>
        public static AtkMarkerSegment FromBytes(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new EndianBinaryReader(ms, true);
            r.ReadInt16(); // consume marker
            return Read(r);
        }

        #endregion

        public override string ToString()
        {
            return $"ATK[index={Index}, {(Reversible ? "reversible" : "irreversible")}, {Steps.Count} step(s)]";
        }
    }
}
