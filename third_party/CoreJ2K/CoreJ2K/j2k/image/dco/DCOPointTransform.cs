// Copyright (c) 2026 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using CoreJ2K.j2k.codestream;

namespace CoreJ2K.j2k.image.dco
{
    /// <summary>
    /// Base class for the JPEG 2000 Part 2 (ISO/IEC 15444-2) Variable DC Offset (DCO)
    /// pipeline stages. Applies a per-component integer DC offset to every sample.
    /// </summary>
    /// <remarks>
    /// The encoder-side stage (<see cref="ForwDCO"/>) subtracts each component's offset
    /// before the wavelet transform; the decoder-side stage (<see cref="InvDCO"/>) adds
    /// it back after the inverse transform. Components whose index is out of range of
    /// the <see cref="DCOMarkerSegment.Offsets"/> array are passed through unchanged.
    /// Integer and floating-point block types are both supported.
    /// </remarks>
    public abstract class DCOPointTransform : ImgDataAdapter, BlkImgDataSrc
    {
        /// <summary>The source of image data.</summary>
        protected readonly BlkImgDataSrc src;

        private readonly int[] offsets;

        protected DCOPointTransform(BlkImgDataSrc src, DCOMarkerSegment seg)
            : base(src)
        {
            this.src = src;
            offsets = seg?.Offsets ?? Array.Empty<int>();
        }

        /// <summary>Applies the direction-specific offset to a single sample.</summary>
        protected abstract int Apply(int sample, int offset);

        public virtual int GetFixedPoint(int compIndex) => src.GetFixedPoint(compIndex);
        public virtual bool IsOrigSigned(int compIndex) => src.IsOrigSigned(compIndex);
        public virtual void Close() { }

        public DataBlk GetCompData(DataBlk blk, int compIndex)
        {
            var off = OffsetFor(compIndex);
            if (off == 0) return src.GetCompData(blk, compIndex);
            return Transform(blk, compIndex, off);
        }

        public DataBlk GetInternCompData(DataBlk blk, int compIndex)
        {
            var off = OffsetFor(compIndex);
            if (off == 0) return src.GetInternCompData(blk, compIndex);
            return Transform(blk, compIndex, off);
        }

        private int OffsetFor(int compIndex) =>
            (compIndex >= 0 && compIndex < offsets.Length) ? offsets[compIndex] : 0;

        private DataBlk Transform(DataBlk blk, int compIndex, int offset)
        {
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
                        outData[do_ + col] = Apply((int)Math.Round(srcData[so + col]), offset);
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
                        outData[do_ + col] = Apply(srcData[so + col], offset);
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
    /// Encoder-side DCO stage: subtracts each component's DC offset from samples
    /// before the wavelet transform.
    /// </summary>
    public sealed class ForwDCO : DCOPointTransform
    {
        public ForwDCO(BlkImgDataSrc src, DCOMarkerSegment seg) : base(src, seg) { }
        protected override int Apply(int sample, int offset) => sample - offset;
    }

    /// <summary>
    /// Decoder-side DCO stage: adds each component's DC offset back to samples
    /// after the inverse wavelet transform.
    /// </summary>
    public sealed class InvDCO : DCOPointTransform
    {
        public InvDCO(BlkImgDataSrc src, DCOMarkerSegment seg) : base(src, seg) { }
        protected override int Apply(int sample, int offset) => sample + offset;
    }
}
