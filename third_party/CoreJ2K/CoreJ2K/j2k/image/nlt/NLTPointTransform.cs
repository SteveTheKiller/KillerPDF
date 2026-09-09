// Copyright (c) 2026 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using CoreJ2K.j2k.codestream;

namespace CoreJ2K.j2k.image.nlt
{
    /// <summary>
    /// Base class for the JPEG 2000 Part 2 (ISO/IEC 15444-2) Non-linearity point
    /// transformation (NLT) pipeline stages. It applies a per-component, per-sample
    /// point transform to the image data flowing through it, leaving all image
    /// geometry untouched.
    /// </summary>
    /// <remarks>
    /// On the encoder the forward transform (<see cref="ForwNLT"/>) sits between the
    /// tiler and the multiple-component transform; on the decoder the inverse
    /// transform (<see cref="InvNLT"/>) sits after the inverse component transform,
    /// reconstructing the original sample values. Components without an applicable NLT
    /// segment, and segments of type <see cref="NLTType.None"/>, pass through
    /// unchanged. Integer blocks are transformed exactly; floating-point blocks
    /// (irreversible path) are transformed via nearest-integer rounding.
    /// </remarks>
    public abstract class NLTPointTransform : ImgDataAdapter, BlkImgDataSrc
    {
        /// <summary>The source of image data.</summary>
        protected readonly BlkImgDataSrc src;

        // NLT segment effective for each component (null = no transform for that component).
        private readonly NLTMarkerSegment?[] perComp;

        /// <summary>
        /// Creates a point-transform stage over <paramref name="src"/> using the given NLT
        /// segments. A segment targeting <see cref="NLTMarkerSegment.AllComponents"/> applies
        /// to every component that does not have its own component-specific segment.
        /// </summary>
        protected NLTPointTransform(BlkImgDataSrc src, IList<NLTMarkerSegment> segments)
            : base(src)
        {
            this.src = src;
            perComp = new NLTMarkerSegment?[src.NumComps];

            if (segments != null)
            {
                // Component-specific segments take precedence over an all-components segment.
                foreach (var s in segments)
                {
                    if (s == null || s.ComponentIndex == NLTMarkerSegment.AllComponents) continue;
                    if (s.ComponentIndex >= 0 && s.ComponentIndex < perComp.Length)
                        perComp[s.ComponentIndex] = s;
                }
                foreach (var s in segments)
                {
                    if (s == null || s.ComponentIndex != NLTMarkerSegment.AllComponents) continue;
                    for (var c = 0; c < perComp.Length; c++)
                        if (perComp[c] == null) perComp[c] = s;
                }
            }
        }

        /// <summary>Applies the direction-specific transform to a single sample.</summary>
        protected abstract int Apply(NLTMarkerSegment seg, int sample);

        /// <summary>Returns true if any component carries an active (non-None) NLT segment.</summary>
        public bool HasActiveTransform
        {
            get
            {
                foreach (var s in perComp)
                    if (s != null && s.Type != NLTType.None) return true;
                return false;
            }
        }

        public virtual int GetFixedPoint(int compIndex) => src.GetFixedPoint(compIndex);

        public virtual bool IsOrigSigned(int compIndex) => src.IsOrigSigned(compIndex);

        public virtual void Close()
        {
            // Nothing to release; the source is closed by the pipeline.
        }

        public DataBlk GetCompData(DataBlk blk, int compIndex)
        {
            var seg = SegmentFor(compIndex);
            if (seg == null || seg.Type == NLTType.None)
                return src.GetCompData(blk, compIndex);
            return Transform(blk, compIndex);
        }

        public DataBlk GetInternCompData(DataBlk blk, int compIndex)
        {
            var seg = SegmentFor(compIndex);
            if (seg == null || seg.Type == NLTType.None)
                return src.GetInternCompData(blk, compIndex);
            // Returning a transformed copy is valid for getInternCompData (the caller must
            // not modify the result, and a fresh array is just as acceptable as shared data).
            return Transform(blk, compIndex);
        }

        private NLTMarkerSegment? SegmentFor(int compIndex) =>
            (compIndex >= 0 && compIndex < perComp.Length) ? perComp[compIndex] : null;

        // Reads the requested region from the source and writes the point-transformed result
        // into the supplied block (offset 0, scanw = w). The result is written into the passed
        // 'blk' — not merely returned — because callers (e.g. the decoder's sample copy loop)
        // read the populated block they passed in and ignore the return value.
        private DataBlk Transform(DataBlk blk, int compIndex)
        {
            var seg = perComp[compIndex]!;
            var w = blk.w;
            var h = blk.h;

            if (blk.DataType == DataBlk.TYPE_FLOAT)
            {
                var tmp = new DataBlkFloat { ulx = blk.ulx, uly = blk.uly, w = w, h = h };
                tmp = (DataBlkFloat)src.GetInternCompData(tmp, compIndex);
                var srcData = tmp.DataFloat;

                var outData = blk.Data as float[];
                if (outData == null || outData.Length < w * h) outData = new float[w * h];

                for (var r = 0; r < h; r++)
                {
                    var so = tmp.offset + r * tmp.scanw;
                    var do_ = r * w;
                    for (var col = 0; col < w; col++)
                        outData[do_ + col] = Apply(seg, (int)Math.Round(srcData[so + col]));
                }

                ((DataBlkFloat)blk).DataFloat = outData;
                blk.progressive = tmp.progressive;
            }
            else
            {
                var tmp = new DataBlkInt { ulx = blk.ulx, uly = blk.uly, w = w, h = h };
                tmp = (DataBlkInt)src.GetInternCompData(tmp, compIndex);
                var srcData = tmp.DataInt;

                var outData = blk.Data as int[];
                if (outData == null || outData.Length < w * h) outData = new int[w * h];

                for (var r = 0; r < h; r++)
                {
                    var so = tmp.offset + r * tmp.scanw;
                    var do_ = r * w;
                    for (var col = 0; col < w; col++)
                        outData[do_ + col] = Apply(seg, srcData[so + col]);
                }

                ((DataBlkInt)blk).DataInt = outData;
                blk.progressive = tmp.progressive;
            }

            blk.offset = 0;
            blk.scanw = w;
            return blk;
        }
    }

    /// <summary>
    /// Encoder-side NLT stage: applies the forward non-linearity point transform to
    /// image samples before the multiple-component and wavelet transforms.
    /// </summary>
    /// <remarks>
    /// The encoder feeds samples in the unsigned source domain ([0, 2^B-1] for an
    /// unsigned component); the DC level shift is applied downstream, so the forward
    /// transform operates directly in that domain.
    /// </remarks>
    public sealed class ForwNLT : NLTPointTransform
    {
        public ForwNLT(BlkImgDataSrc src, IList<NLTMarkerSegment> segments) : base(src, segments) { }

        protected override int Apply(NLTMarkerSegment seg, int sample) => seg.ForwardSample(sample);
    }

    /// <summary>
    /// Decoder-side NLT stage: applies the inverse non-linearity point transform,
    /// reconstructing the original-domain sample values.
    /// </summary>
    /// <remarks>
    /// The forward (encoder) and inverse (decoder) stages operate in the same sample
    /// domain — the values flowing through the coding pipeline — so the inverse transform
    /// is applied directly. The NLT segment must be configured for that domain (for
    /// example a DC level-shifted, signed-centred domain for an otherwise unsigned
    /// component).
    /// </remarks>
    public sealed class InvNLT : NLTPointTransform
    {
        public InvNLT(BlkImgDataSrc src, IList<NLTMarkerSegment> segments) : base(src, segments) { }

        protected override int Apply(NLTMarkerSegment seg, int sample) => seg.InverseSample(sample);
    }
}
