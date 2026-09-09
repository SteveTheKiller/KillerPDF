// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using System.IO;

namespace CoreJ2K.j2k.codestream.writer
{
    /// <summary>
    /// Helper class for writing PPM (Packed Packet headers, Main header) marker segments.
    /// PPM markers contain packet headers for all tiles/tile-parts, stored in the main header.
    /// This allows decoders to access packet headers without parsing through tile-part headers.
    /// </summary>
    public static class PPMMarkerWriter
    {
        /// <summary>
        /// Maximum length of a PPM marker segment data (Nppm + Ippm fields).
        /// The actual marker length is this + 4 (for Lppm and Zppm).
        /// </summary>
        public const int MAX_PPM_DATA_LENGTH = Markers.MAX_LPPM - 4;

        /// <summary>
        /// Writes PPM marker segment(s) to the provided BinaryWriter.
        /// Multiple PPM markers may be written if there are many packet headers.
        /// </summary>
        /// <param name="writer">The BinaryWriter to write to</param>
        /// <param name="packetHeaders">Dictionary mapping tile index to list of packet headers</param>
        public static void WritePPM(BinaryWriter writer, Dictionary<int, List<byte[]>> packetHeaders)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
                
            if (packetHeaders == null || packetHeaders.Count == 0)
                return;

            try
            {
                var zppm = 0; // PPM marker index
                using (var ppmData = new MemoryStream())
                {
                    foreach (var tileEntry in packetHeaders)
                    {
                        var tileIdx = tileEntry.Key;
                        var headers = tileEntry.Value;

                        foreach (var header in headers)
                        {
                            if (header == null || header.Length == 0)
                                continue;

                            // Check if adding this header would exceed the max marker size
                            // Need 4 bytes for Nppm length + header data
                            var requiredSpace = 4 + header.Length;

                            if (ppmData.Length + requiredSpace > MAX_PPM_DATA_LENGTH)
                            {
                                // Write current PPM marker and start a new one
                                if (ppmData.Length > 0)
                                {
                                    WritePPMMarker(writer, ppmData, zppm++);
                                    ppmData.SetLength(0);
                                }

                                // Check if we've exceeded the maximum number of PPM markers
                                if (zppm > 255)
                                    throw new InvalidOperationException("Too many PPM markers required (max 256)");
                            }

                            // Write Nppm (length of this packet header) as 32-bit value
                            var nppm = header.Length;
                            ppmData.WriteByte((byte)(nppm >> 24));
                            ppmData.WriteByte((byte)(nppm >> 16));
                            ppmData.WriteByte((byte)(nppm >> 8));
                            ppmData.WriteByte((byte)nppm);

                            // Write Ippm (packet header data)
                            ppmData.Write(header, 0, header.Length);
                        }
                    }

                    // Write final PPM marker if there's any remaining data
                    if (ppmData.Length > 0)
                    {
                        WritePPMMarker(writer, ppmData, zppm);
                    }
                }
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Error writing PPM marker: {e.Message}", e);
            }
        }

        /// <summary>
        /// Writes a single PPM marker segment.
        /// </summary>
        /// <param name="writer">The writer to write to</param>
        /// <param name="data">The PPM data stream (Nppm + Ippm fields)</param>
        /// <param name="zppm">The PPM marker index (0-255)</param>
        private static void WritePPMMarker(BinaryWriter writer, MemoryStream data, int zppm)
        {
            if (zppm > 255)
                throw new ArgumentOutOfRangeException(nameof(zppm), "PPM index must be 0-255");

            var dataLen = (int)data.Length;
            data.TryGetBuffer(out var segment);

            // Write PPM marker
            writer.Write(Markers.PPM);

            // Write Lppm (marker segment length = data length + 3 for Lppm itself (2 bytes) + Zppm (1 byte))
            var lppm = (ushort)(dataLen + 3);
            writer.Write(lppm);

            // Write Zppm (PPM marker index)
            writer.Write((byte)zppm);

            // Write PPM data (Nppm + Ippm fields)
            writer.Write(segment.Array!, segment.Offset, dataLen);
        }

        /// <summary>
        /// Calculates the total size of PPM marker segment(s) needed for the given packet headers.
        /// </summary>
        /// <param name="packetHeaders">Dictionary mapping tile index to list of packet headers</param>
        /// <returns>The total size in bytes, including all marker overhead</returns>
        public static int CalculatePPMSize(Dictionary<int, List<byte[]>> packetHeaders)
        {
            if (packetHeaders == null || packetHeaders.Count == 0)
                return 0;

            var totalSize = 0;
            var currentMarkerSize = 0;
            var markerCount = 0;

            foreach (var tileEntry in packetHeaders)
            {
                foreach (var header in tileEntry.Value)
                {
                    if (header == null || header.Length == 0)
                        continue;

                    var requiredSpace = 4 + header.Length; // Nppm (4) + Ippm (header.Length)

                    if (currentMarkerSize + requiredSpace > MAX_PPM_DATA_LENGTH)
                    {
                        // Finish current marker: marker(2) + Lppm(2) + Zppm(1) + data
                        totalSize += 5 + currentMarkerSize;
                        currentMarkerSize = 0;
                        markerCount++;

                        if (markerCount > 255)
                            throw new InvalidOperationException("Too many PPM markers required (max 256)");
                    }

                    currentMarkerSize += requiredSpace;
                }
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
