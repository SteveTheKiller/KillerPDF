// Copyright (c) 2026 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreJ2K.j2k.codestream
{
    /// <summary>
    /// Describes one matrix multiple-component transform to apply on encode: the analysis
    /// (forward) matrix that maps original component vectors to coded component vectors,
    /// across the listed components.
    /// </summary>
    public class MctEncodeSpec
    {
        /// <summary>The kind of transform to apply.</summary>
        public MctTransformType TransformType { get; set; } = MctTransformType.Matrix;

        /// <summary>Component indices the transform applies to (input == output).</summary>
        public int[] Components { get; set; } = Array.Empty<int>();

        /// <summary>
        /// For <see cref="MctTransformType.Matrix"/>, the N×N analysis (forward) matrix
        /// (coded = ForwardMatrix · original). For <see cref="MctTransformType.Dependency"/>, the
        /// strictly-lower-triangular prediction matrix P. Unused for <see cref="MctTransformType.Wavelet"/>.
        /// </summary>
        public double[,] ForwardMatrix { get; set; } = new double[0, 0];

        /// <summary>Optional per-component offset applied after the forward matrix, or null.</summary>
        public double[]? Offset { get; set; }

        /// <summary>Whether the transform is irreversible (informational; stored in the MCC flags).</summary>
        public bool Irreversible { get; set; } = true;

        /// <summary>Element type used to serialize the stored array.</summary>
        public MctElementType ElementType { get; set; } = MctElementType.Float64;
    }

    /// <summary>
    /// Helpers that bridge the MCT/MCC/MCO/CBD marker model and the runtime matrix transform:
    /// assembling decode stages from parsed markers, and building markers from encode specs.
    /// </summary>
    public static class MctTransform
    {
        /// <summary>
        /// Assembles the ordered list of decode (synthesis) matrix stages from the parsed
        /// MCT arrays, MCC collections, and the MCO ordering. Returns an empty list if no
        /// usable matrix transform is present.
        /// </summary>
        public static List<MctStage> AssembleDecodeStages(
            IList<MctArrayMarkerSegment> arrays,
            IList<MccMarkerSegment> mccs,
            McoMarkerSegment? mco)
        {
            var stages = new List<MctStage>();
            if (mco == null || arrays == null || mccs == null) return stages;

            foreach (var stageIdx in mco.Stages)
            {
                var mcc = mccs.FirstOrDefault(m => m.Index == stageIdx);
                if (mcc == null) continue;

                var n = mcc.Components.Length;

                double[,] matrix = new double[0, 0];
                if (mcc.TransformType == MctTransformType.Matrix || mcc.TransformType == MctTransformType.Dependency)
                {
                    var wanted = mcc.TransformType == MctTransformType.Matrix
                        ? MctArrayType.Decorrelation
                        : MctArrayType.Dependency;
                    var arr = arrays.FirstOrDefault(a => a.Index == mcc.DecorrelationArrayIndex && a.ArrayType == wanted);
                    if (arr == null || arr.Values.Length < n * n) continue;

                    matrix = new double[n, n];
                    for (var row = 0; row < n; row++)
                        for (var col = 0; col < n; col++)
                            matrix[row, col] = arr.Values[row * n + col];
                }

                double[]? offset = null;
                if (mcc.OffsetArrayIndex != MccMarkerSegment.NoOffset)
                {
                    var off = arrays.FirstOrDefault(a =>
                        a.Index == mcc.OffsetArrayIndex && a.ArrayType == MctArrayType.Offset);
                    if (off != null && off.Values.Length >= n)
                        offset = off.Values.Take(n).ToArray();
                }

                stages.Add(new MctStage
                {
                    TransformType = mcc.TransformType,
                    Components = mcc.Components,
                    Matrix = matrix,
                    Offset = offset
                });
            }

            return stages;
        }

        /// <summary>Builds the runtime forward (analysis) stages used by the encoder.</summary>
        public static List<MctStage> BuildForwardStages(IList<MctEncodeSpec> specs)
        {
            var stages = new List<MctStage>();
            foreach (var spec in specs)
                stages.Add(new MctStage
                {
                    TransformType = spec.TransformType,
                    Components = spec.Components,
                    Matrix = spec.ForwardMatrix,
                    Offset = spec.Offset
                });
            return stages;
        }

        /// <summary>
        /// Builds the MCT/MCC/MCO marker segments for the supplied encode specs. The stored
        /// decorrelation array is the synthesis matrix (the inverse of the analysis matrix),
        /// so a decoder reconstructs the originals directly.
        /// </summary>
        public static (List<MctArrayMarkerSegment> Arrays, List<MccMarkerSegment> Mccs, McoMarkerSegment Mco)
            BuildMarkers(IList<MctEncodeSpec> specs)
        {
            var arrays = new List<MctArrayMarkerSegment>();
            var mccs = new List<MccMarkerSegment>();
            var stageOrder = new List<int>();

            var arrayIndex = 0;
            var mccIndex = 0;

            foreach (var spec in specs)
            {
                var n = spec.Components.Length;
                var arrayRefIndex = MccMarkerSegment.NoOffset;

                if (spec.TransformType == MctTransformType.Matrix)
                {
                    // Store the synthesis matrix (inverse of the analysis matrix) so a decoder
                    // reconstructs originals directly.
                    var synthesis = Invert(spec.ForwardMatrix);
                    var flat = new double[n * n];
                    for (var row = 0; row < n; row++)
                        for (var col = 0; col < n; col++)
                            flat[row * n + col] = synthesis[row, col];

                    arrayRefIndex = arrayIndex++;
                    arrays.Add(new MctArrayMarkerSegment
                    {
                        Index = arrayRefIndex,
                        ArrayType = MctArrayType.Decorrelation,
                        ElementType = spec.ElementType,
                        Values = flat
                    });
                }
                else if (spec.TransformType == MctTransformType.Dependency)
                {
                    // The same prediction matrix P is applied (in opposite directions) on both
                    // sides, so it is stored as-is.
                    var flat = new double[n * n];
                    for (var row = 0; row < n; row++)
                        for (var col = 0; col < n; col++)
                            flat[row * n + col] = spec.ForwardMatrix[row, col];

                    arrayRefIndex = arrayIndex++;
                    arrays.Add(new MctArrayMarkerSegment
                    {
                        Index = arrayRefIndex,
                        ArrayType = MctArrayType.Dependency,
                        ElementType = spec.ElementType,
                        Values = flat
                    });
                }
                // Wavelet: no array is needed (the 5/3 kernel is implicit).

                var offsetIndex = MccMarkerSegment.NoOffset;
                if (spec.Offset != null && spec.Offset.Length == n)
                {
                    offsetIndex = arrayIndex++;
                    arrays.Add(new MctArrayMarkerSegment
                    {
                        Index = offsetIndex,
                        ArrayType = MctArrayType.Offset,
                        ElementType = spec.ElementType,
                        Values = (double[])spec.Offset.Clone()
                    });
                }

                mccs.Add(new MccMarkerSegment
                {
                    Index = mccIndex,
                    Irreversible = spec.Irreversible,
                    TransformType = spec.TransformType,
                    DecorrelationArrayIndex = arrayRefIndex,
                    OffsetArrayIndex = offsetIndex,
                    Components = spec.Components
                });
                stageOrder.Add(mccIndex);
                mccIndex++;
            }

            return (arrays, mccs, new McoMarkerSegment { Stages = stageOrder.ToArray() });
        }

        /// <summary>Inverts a square matrix via Gauss-Jordan elimination with partial pivoting.</summary>
        public static double[,] Invert(double[,] m)
        {
            var n = m.GetLength(0);
            if (m.GetLength(1) != n)
                throw new ArgumentException("Matrix must be square to invert.");

            var a = new double[n, 2 * n];
            for (var i = 0; i < n; i++)
            {
                for (var j = 0; j < n; j++) a[i, j] = m[i, j];
                a[i, n + i] = 1.0;
            }

            for (var col = 0; col < n; col++)
            {
                // Partial pivot.
                var pivot = col;
                var best = Math.Abs(a[col, col]);
                for (var r = col + 1; r < n; r++)
                {
                    var v = Math.Abs(a[r, col]);
                    if (v > best) { best = v; pivot = r; }
                }
                if (best == 0.0)
                    throw new InvalidOperationException("MCT matrix is singular and cannot be inverted.");

                if (pivot != col)
                    for (var j = 0; j < 2 * n; j++)
                        (a[col, j], a[pivot, j]) = (a[pivot, j], a[col, j]);

                var diag = a[col, col];
                for (var j = 0; j < 2 * n; j++) a[col, j] /= diag;

                for (var r = 0; r < n; r++)
                {
                    if (r == col) continue;
                    var factor = a[r, col];
                    if (factor == 0.0) continue;
                    for (var j = 0; j < 2 * n; j++) a[r, j] -= factor * a[col, j];
                }
            }

            var inv = new double[n, n];
            for (var i = 0; i < n; i++)
                for (var j = 0; j < n; j++)
                    inv[i, j] = a[i, n + j];
            return inv;
        }
    }
}
