// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System.IO;

namespace CoreJ2K.j2k.codestream.writer.markers
{
    /// <summary>
    /// Writes CRG (Component Registration) marker segments.
    /// Per ISO/IEC 15444-1 Annex A.11.3.
    /// </summary>
    internal class CRGMarkerWriter
    {
        private readonly int nComp;
        private readonly int[] xcrg;
        private readonly int[] ycrg;

        /// <summary>
        /// Creates a CRG marker writer.
        /// </summary>
        /// <param name="nComp">Number of components.</param>
        /// <param name="xcrg">Horizontal offsets for each component (or null for no CRG).</param>
        /// <param name="ycrg">Vertical offsets for each component (or null for no CRG).</param>
        public CRGMarkerWriter(int nComp, int[] xcrg, int[] ycrg)
        {
            this.nComp = nComp;
            this.xcrg = xcrg;
            this.ycrg = ycrg;
        }

        /// <summary>
        /// Checks if CRG marker should be written.
        /// CRG is optional and only written if offsets are non-zero.
        /// </summary>
        public bool ShouldWrite()
        {
            if (xcrg == null || ycrg == null)
                return false;

            // Check if any offsets are non-zero
            for (int i = 0; i < nComp; i++)
            {
                if (xcrg[i] != 0 || ycrg[i] != 0)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Writes the CRG marker segment.
        /// </summary>
        public void Write(BinaryWriter writer)
        {
            if (!ShouldWrite())
                return;

            // CRG marker
            writer.Write(Markers.CRG);

            // Lcrg (marker segment length)
            // Lcrg = 2 (for Lcrg itself) + 4 * Csiz (2 bytes per component per offset)
            int markSegLen = 2 + (4 * nComp);
            writer.Write((short)markSegLen);

            // Xcrg and Ycrg for each component
            for (int i = 0; i < nComp; i++)
            {
                // Xcrg[i] - horizontal offset (16-bit unsigned)
                writer.Write((ushort)xcrg[i]);

                // Ycrg[i] - vertical offset (16-bit unsigned)
                writer.Write((ushort)ycrg[i]);
            }
        }
    }
}
