// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using CoreJ2K.j2k.image;
using System.IO;

namespace CoreJ2K.j2k.codestream.writer.markers
{
    /// <summary>
    /// Writes SIZ (Image and tile size) marker segments.
    /// </summary>
    internal class SIZMarkerWriter
    {
        private readonly ImgData origSrc;
        private readonly bool[] isOrigSig;
        private readonly Tiler tiler;
        private readonly int nComp;

        /// <summary>
        /// Codestream capabilities (Rsiz). 0 = JPEG 2000 Part 1 baseline (default).
        /// Set to <see cref="Markers.RSIZ_EXTENSIONS"/> (or other Part 2 value) to signal
        /// that the codestream uses ISO/IEC 15444-2 extensions enumerated in the CAP marker.
        /// </summary>
        public int Rsiz { get; set; } = 0;

        public SIZMarkerWriter(ImgData origSrc, bool[] isOrigSig, Tiler tiler, int nComp)
        {
            this.origSrc = origSrc;
            this.isOrigSig = isOrigSig;
            this.tiler = tiler;
            this.nComp = nComp;
        }

        public void Write(BinaryWriter writer)
        {
            int tmp;

            // SIZ marker
            writer.Write(Markers.SIZ);

            // Lsiz (Marker length)
            var markSegLen = 38 + 3 * nComp;
            writer.Write((short)markSegLen);

            // Rsiz (codestream capabilities); 0 = Part 1 baseline
            writer.Write((short)Rsiz);

            // Xsiz (original image width)
            writer.Write(tiler.ImgWidth + tiler.ImgULX);

            // Ysiz (original image height)
            writer.Write(tiler.ImgHeight + tiler.ImgULY);

            // XOsiz (horizontal offset from origin)
            writer.Write(tiler.ImgULX);

            // YOsiz (vertical offset from origin)
            writer.Write(tiler.ImgULY);

            // XTsiz (nominal tile width)
            writer.Write(tiler.NomTileWidth);

            // YTsiz (nominal tile height)
            writer.Write(tiler.NomTileHeight);

            var torig = tiler.GetTilingOrigin(null);
            // XTOsiz (horizontal offset from origin to first tile)
            writer.Write(torig.x);

            // YTOsiz (vertical offset from origin to first tile)
            writer.Write(torig.y);

            // Csiz (number of components)
            writer.Write((short)nComp);

            // Bit-depth and downsampling factors for each component
            for (var c = 0; c < nComp; c++)
            {
                // Ssiz bit-depth before mixing
                tmp = origSrc.GetNomRangeBits(c) - 1;
                tmp |= ((isOrigSig[c] ? 1 : 0) << Markers.SSIZ_DEPTH_BITS);
                writer.Write((byte)tmp);

                // XRsiz (component sub-sampling value x-wise)
                writer.Write((byte)tiler.GetCompSubsX(c));

                // YRsiz (component sub-sampling value y-wise)
                writer.Write((byte)tiler.GetCompSubsY(c));
            }
        }
    }
}
