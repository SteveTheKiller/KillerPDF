// Copyright (c) 2026 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using CoreJ2K.j2k.codestream;

namespace CoreJ2K.j2k.image.mct
{
    /// <summary>
    /// Applies one multiple-component transform stage (JPEG 2000 Part 2, ISO/IEC 15444-2)
    /// across a collection of components: at each sample position the vector of the
    /// collection's component samples is transformed. Supports the three array-based MCC
    /// transform kinds — matrix decorrelation, dependency (lifting/prediction), and a 5/3
    /// wavelet across the components. Components outside the collection pass through unchanged.
    /// </summary>
    /// <remarks>
    /// One instance applies a single stage in one direction (<paramref name="inverse"/> selects
    /// analysis on the encoder vs synthesis on the decoder). Multiple stages compose by chaining
    /// instances. The matrix kind is direction-agnostic (the stored matrix already encodes the
    /// direction); the dependency and wavelet kinds are exactly reversible and use the direction
    /// flag. The dependency lifting recovers the originals exactly even with a fractional
    /// prediction matrix. Results are written into the caller-supplied <see cref="DataBlk"/> in place.
    /// </remarks>
    public class ComponentTransform : ImgDataAdapter, BlkImgDataSrc
    {
        private readonly BlkImgDataSrc src;
        private readonly MctStage stage;
        private readonly bool inverse;
        private readonly Dictionary<int, int> compToRow; // component index -> row within the collection

        public ComponentTransform(BlkImgDataSrc src, MctStage stage, bool inverse) : base(src)
        {
            this.src = src;
            this.stage = stage;
            this.inverse = inverse;
            compToRow = new Dictionary<int, int>(stage.Components.Length);
            for (var i = 0; i < stage.Components.Length; i++)
                compToRow[stage.Components[i]] = i;
        }

        /// <summary>Builds a chain of transform stages over <paramref name="src"/> (first stage innermost).</summary>
        public static BlkImgDataSrc BuildChain(BlkImgDataSrc src, IList<MctStage> stages, bool inverse)
        {
            var current = src;
            foreach (var stage in stages)
                current = new ComponentTransform(current, stage, inverse);
            return current;
        }

        public virtual int GetFixedPoint(int compIndex) => src.GetFixedPoint(compIndex);

        public virtual bool IsOrigSigned(int compIndex) => src.IsOrigSigned(compIndex);

        public virtual void Close() { /* source closed by the pipeline */ }

        public DataBlk GetCompData(DataBlk blk, int compIndex)
        {
            if (!compToRow.ContainsKey(compIndex)) return src.GetCompData(blk, compIndex);
            return Transform(blk, compIndex);
        }

        public DataBlk GetInternCompData(DataBlk blk, int compIndex)
        {
            if (!compToRow.ContainsKey(compIndex)) return src.GetInternCompData(blk, compIndex);
            return Transform(blk, compIndex);
        }

        private DataBlk Transform(DataBlk blk, int compIndex)
        {
            var row = compToRow[compIndex];
            var n = stage.Components.Length;
            var w = blk.w;
            var h = blk.h;
            var samples = w * h;
            var isFloat = blk.DataType == DataBlk.TYPE_FLOAT;

            // Read every collection component for the requested region.
            var inData = new double[n][];
            var progressive = false;
            for (var j = 0; j < n; j++)
            {
                if (isFloat)
                {
                    var tmp = new DataBlkFloat { ulx = blk.ulx, uly = blk.uly, w = w, h = h };
                    tmp = (DataBlkFloat)src.GetInternCompData(tmp, stage.Components[j]);
                    var d = new double[samples];
                    for (var r = 0; r < h; r++)
                        for (var col = 0; col < w; col++)
                            d[r * w + col] = tmp.DataFloat[tmp.offset + r * tmp.scanw + col];
                    inData[j] = d;
                    progressive |= tmp.progressive;
                }
                else
                {
                    var tmp = new DataBlkInt { ulx = blk.ulx, uly = blk.uly, w = w, h = h };
                    tmp = (DataBlkInt)src.GetInternCompData(tmp, stage.Components[j]);
                    var d = new double[samples];
                    for (var r = 0; r < h; r++)
                        for (var col = 0; col < w; col++)
                            d[r * w + col] = tmp.DataInt[tmp.offset + r * tmp.scanw + col];
                    inData[j] = d;
                    progressive |= tmp.progressive;
                }
            }

            var inVec = new double[n];
            var outVec = new double[n];

            if (isFloat)
            {
                var outData = blk.Data as float[];
                if (outData == null || outData.Length < samples) outData = new float[samples];
                for (var s = 0; s < samples; s++)
                {
                    for (var j = 0; j < n; j++) inVec[j] = inData[j][s];
                    ComputeVector(inVec, outVec);
                    outData[s] = (float)outVec[row];
                }
                ((DataBlkFloat)blk).DataFloat = outData;
            }
            else
            {
                var outData = blk.Data as int[];
                if (outData == null || outData.Length < samples) outData = new int[samples];
                for (var s = 0; s < samples; s++)
                {
                    for (var j = 0; j < n; j++) inVec[j] = inData[j][s];
                    ComputeVector(inVec, outVec);
                    outData[s] = (int)Math.Round(outVec[row]);
                }
                ((DataBlkInt)blk).DataInt = outData;
            }

            blk.progressive = progressive;
            blk.offset = 0;
            blk.scanw = w;
            return blk;
        }

        // Transforms a single sample's component vector (length N) according to the stage type
        // and direction, writing the result into <paramref name="outVec"/>.
        private void ComputeVector(double[] inVec, double[] outVec)
        {
            var n = inVec.Length;
            switch (stage.TransformType)
            {
                case MctTransformType.Matrix:
                {
                    for (var i = 0; i < n; i++)
                    {
                        var acc = stage.Offset != null ? stage.Offset[i] : 0.0;
                        for (var j = 0; j < n; j++) acc += stage.Matrix[i, j] * inVec[j];
                        outVec[i] = acc;
                    }
                    break;
                }

                case MctTransformType.Dependency:
                {
                    // Strictly-lower-triangular prediction P. The rounded prediction uses the
                    // original samples in both directions (originals on encode, reconstructed
                    // originals on decode), so the transform is exactly reversible.
                    if (!inverse)
                    {
                        for (var i = 0; i < n; i++)
                        {
                            var pred = 0.0;
                            for (var j = 0; j < i; j++) pred += stage.Matrix[i, j] * inVec[j];
                            outVec[i] = inVec[i] - Math.Round(pred);
                        }
                    }
                    else
                    {
                        for (var i = 0; i < n; i++)
                        {
                            var pred = 0.0;
                            for (var j = 0; j < i; j++) pred += stage.Matrix[i, j] * outVec[j];
                            outVec[i] = inVec[i] + Math.Round(pred);
                        }
                    }
                    break;
                }

                case MctTransformType.Wavelet:
                {
                    var v = new int[n];
                    for (var i = 0; i < n; i++) v[i] = (int)Math.Round(inVec[i]);
                    if (!inverse) Forward53(v); else Inverse53(v);
                    for (var i = 0; i < n; i++) outVec[i] = v[i];
                    break;
                }
            }
        }

        // Reversible 5/3 (LeGall) lifting over a 1-D integer vector with symmetric extension.
        private static void Forward53(int[] s)
        {
            var n = s.Length;
            if (n < 2) return;
            for (var i = 1; i < n; i += 2)
                s[i] -= (Mirror(s, i - 1) + Mirror(s, i + 1)) >> 1;
            for (var i = 0; i < n; i += 2)
                s[i] += (Mirror(s, i - 1) + Mirror(s, i + 1) + 2) >> 2;
        }

        private static void Inverse53(int[] s)
        {
            var n = s.Length;
            if (n < 2) return;
            for (var i = 0; i < n; i += 2)
                s[i] -= (Mirror(s, i - 1) + Mirror(s, i + 1) + 2) >> 2;
            for (var i = 1; i < n; i += 2)
                s[i] += (Mirror(s, i - 1) + Mirror(s, i + 1)) >> 1;
        }

        // Symmetric (whole-sample) boundary extension.
        private static int Mirror(int[] s, int i)
        {
            var n = s.Length;
            if (n == 1) return s[0];
            if (i < 0) i = -i;
            if (i >= n) i = 2 * (n - 1) - i;
            if (i < 0) i = 0;
            return s[i];
        }
    }
}
