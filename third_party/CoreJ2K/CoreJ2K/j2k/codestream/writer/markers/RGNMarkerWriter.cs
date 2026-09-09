// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using CoreJ2K.j2k.encoder;
using System.IO;

namespace CoreJ2K.j2k.codestream.writer.markers
{
    /// <summary>
    /// Writes RGN (Region of Interest) marker segments.
    /// </summary>
    internal class RGNMarkerWriter
    {
        private readonly EncoderSpecs encSpec;
        private readonly int nComp;

        public RGNMarkerWriter(EncoderSpecs encSpec, int nComp)
        {
            this.encSpec = encSpec;
            this.nComp = nComp;
        }

        public void Write(BinaryWriter writer, int tileIdx)
        {
            // Write one RGN marker per component
            for (int i = 0; i < nComp; i++)
            {
                // RGN marker
                writer.Write(Markers.RGN);

                // Calculate length (Lrgn)
                int markSegLen = 4 + ((nComp < 257) ? 1 : 2);
                writer.Write((short)markSegLen);

                // Write component (Crgn)
                if (nComp < 257)
                {
                    writer.Write((byte)i);
                }
                else
                {
                    writer.Write((short)i);
                }

                // Write type of ROI (Srgn)
                writer.Write((byte)Markers.SRGN_IMPLICIT);

                // Write ROI info (SPrgn)
                writer.Write((byte)((int)(encSpec.rois.GetTileCompVal(tileIdx, i))));
            }
        }
    }
}
