// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.IO;

namespace CoreJ2K.j2k.codestream.writer.markers
{
    /// <summary>
    /// Writes SOT (Start of Tile-part) marker segments.
    /// </summary>
    internal class SOTMarkerWriter
    {
        public void Write(BinaryWriter writer, int tileIdx, int tileLength)
        {
            // SOT marker
            writer.Write(unchecked((byte)(Markers.SOT >>> 8)));
            writer.Write((byte)(Markers.SOT & 0x00FF));

            // Lsot (10 bytes)
            writer.Write((byte)0);
            writer.Write((byte)10);

            // Isot (tile index)
            if (tileIdx > 65534)
            {
                throw new ArgumentException(
                    "Trying to write a tile-part header whose tile index is too high");
            }
            writer.Write((byte)(tileIdx >> 8));
            writer.Write((byte)tileIdx);

            // Psot (tile-part length)
            writer.Write((byte)(tileLength >> 24));
            writer.Write((byte)(tileLength >> 16));
            writer.Write((byte)(tileLength >> 8));
            writer.Write((byte)tileLength);

            // TPsot (tile-part index)
            writer.Write((byte)0); // Only one tile-part currently supported

            // TNsot (number of tile-parts)
            writer.Write((byte)1); // Only one tile-part currently supported
        }
    }
}
