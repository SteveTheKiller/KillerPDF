// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using System.IO;

namespace CoreJ2K.j2k.codestream.writer
{
    /// <summary>
    /// Helper class for writing PPT (Packed Packet headers, Tile-part header) marker segments.
    /// PPT markers contain packet headers for a single tile-part, stored in the tile-part header.
    /// This allows decoders to access packet headers without parsing through packet bodies.
    /// </summary>
    public static class PPTMarkerWriter
    {
        /// <summary>
        /// Maximum length of a PPT marker segment data (Ippt field).
        /// The actual marker length is this + 3 (for Lppt and Zppt).
        /// </summary>
        public const int MAX_PPT_DATA_LENGTH = Markers.MAX_LPPT - 3;

        /// <summary>
        /// Writes PPT marker segment(s) for a single tile-part to the provided BinaryWriter.
        /// Multiple PPT markers may be written if there are many packet headers.
        /// </summary>
        /// <param name="writer">The BinaryWriter to write to</param>
        /// <param name="packetHeaders">List of packet headers for this tile-part</param>
        public static void WritePPT(BinaryWriter writer, List<byte[]> packetHeaders)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
                
            if (packetHeaders == null || packetHeaders.Count == 0)
                return;

            try
            {
                var zppt = 0; // PPT marker index for this tile-part
                using (var pptData = new MemoryStream())
                {
                    foreach (var header in packetHeaders)
                    {
                        if (header == null || header.Length == 0)
                            continue;

                        // Check if adding this header would exceed the max marker size
                        if (pptData.Length + header.Length > MAX_PPT_DATA_LENGTH)
                        {
                            // Write current PPT marker and start a new one
                            if (pptData.Length > 0)
                            {
                                WritePPTMarker(writer, pptData, zppt++);
                                pptData.SetLength(0);
                            }

                            // Check if we've exceeded the maximum number of PPT markers
                            if (zppt > 255)
                                throw new InvalidOperationException("Too many PPT markers required for tile-part (max 256)");
                        }

                        // Write Ippt (packet header data) directly
                        pptData.Write(header, 0, header.Length);
                    }

                    // Write final PPT marker if there's any remaining data
                    if (pptData.Length > 0)
                    {
                        WritePPTMarker(writer, pptData, zppt);
                    }
                }
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Error writing PPT marker: {e.Message}", e);
            }
        }

        /// <summary>
        /// Writes a single PPT marker segment.
        /// </summary>
        /// <param name="writer">The writer to write to</param>
        /// <param name="data">The PPT data stream (Ippt field)</param>
        /// <param name="zppt">The PPT marker index (0-255)</param>
        private static void WritePPTMarker(BinaryWriter writer, MemoryStream data, int zppt)
        {
            if (zppt > 255)
                throw new ArgumentOutOfRangeException(nameof(zppt), "PPT index must be 0-255");

            var dataLen = (int)data.Length;
            data.TryGetBuffer(out var segment);

            // Write PPT marker
            writer.Write(Markers.PPT);

            // Write Lppt (marker segment length = data length + 3 for Lppt itself (2 bytes) + Zppt (1 byte))
            var lppt = (ushort)(dataLen + 3);
            writer.Write(lppt);

            // Write Zppt (PPT marker index)
            writer.Write((byte)zppt);

            // Write PPT data (Ippt field - concatenated packet headers)
            writer.Write(segment.Array!, segment.Offset, dataLen);
        }

        /// <summary>
        /// Calculates the total size of PPT marker segment(s) needed for the given packet headers.
        /// </summary>
        /// <param name="packetHeaders">List of packet headers for a tile-part</param>
        /// <returns>The total size in bytes, including all marker overhead</returns>
        public static int CalculatePPTSize(List<byte[]> packetHeaders)
        {
            if (packetHeaders == null || packetHeaders.Count == 0)
                return 0;

            var totalSize = 0;
            var currentMarkerSize = 0;
            var markerCount = 0;

            foreach (var header in packetHeaders)
            {
                if (header == null || header.Length == 0)
                    continue;

                if (currentMarkerSize + header.Length > MAX_PPT_DATA_LENGTH)
                {
                    // Finish current marker: marker(2) + Lppt(2) + Zppt(1) + data
                    totalSize += 5 + currentMarkerSize;
                    currentMarkerSize = 0;
                    markerCount++;

                    if (markerCount > 255)
                        throw new InvalidOperationException("Too many PPT markers required (max 256)");
                }

                currentMarkerSize += header.Length;
            }

            // Add final marker
            if (currentMarkerSize > 0)
            {
                totalSize += 5 + currentMarkerSize;
            }

            return totalSize;
        }
    }
}
