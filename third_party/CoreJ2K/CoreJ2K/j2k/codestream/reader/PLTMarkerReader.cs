// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using CoreJ2K.j2k.codestream.metadata;
using System;
using System.IO;

namespace CoreJ2K.j2k.codestream.reader
{
    /// <summary>
    /// Reads PLT (Packet Length, tile-part header) markers from JPEG 2000 codestream.
    /// PLT markers contain the lengths of packets within a tile-part,
    /// enabling fast packet boundary detection without parsing packet headers.
    /// </summary>
    public static class PLTMarkerReader
    {
        /// <summary>
        /// Reads a PLT marker segment from the input stream and stores packet lengths.
        /// The stream should be positioned immediately after the PLT marker (0xFF58).
        /// </summary>
        /// <param name="stream">The input stream to read from (positioned after marker).</param>
        /// <param name="pltData">The PacketLengthsData to store the lengths in.</param>
        /// <param name="tileIdx">The current tile index.</param>
        /// <returns>The number of bytes read (including Lplt).</returns>
        /// <exception cref="ArgumentNullException">If stream or pltData is null.</exception>
        /// <exception cref="EndOfStreamException">If unexpected end of stream is encountered.</exception>
        /// <exception cref="IOException">If an I/O error occurs.</exception>
        public static int ReadPLT(Stream stream, PacketLengthsData pltData, int tileIdx)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (pltData == null)
                throw new ArgumentNullException(nameof(pltData));

            var bytesRead = 0;

            // Read Lplt (marker segment length) - big-endian 16-bit
            var lpltHigh = stream.ReadByte();
            var lpltLow = stream.ReadByte();
            if (lpltHigh == -1 || lpltLow == -1)
                throw new EndOfStreamException("Unexpected end of stream while reading PLT marker segment length");

            var lplt = (ushort)((lpltHigh << 8) | lpltLow);
            bytesRead += 2;

            // Read Zplt (PLT index)
            var zplt = stream.ReadByte();
            if (zplt == -1)
                throw new EndOfStreamException("Unexpected end of stream while reading PLT index");
            bytesRead++;

            // Calculate remaining bytes for packet lengths (Iplt field)
            // lplt includes itself (2 bytes) and Zplt (1 byte), so data = lplt - 3
            var ipltSize = lplt - 3;
            if (ipltSize < 0)
                throw new IOException($"Invalid PLT marker segment length: {lplt}");

            // Read variable-length encoded packet lengths
            var ipltBytesRead = 0;
            while (ipltBytesRead < ipltSize)
            {
                var startPos = stream.Position;
                var packetLength = DecodeVariableLengthInt(stream);
                var encodedBytes = (int)(stream.Position - startPos);

                // Add packet length to the data structure
                pltData.AddPacket(tileIdx, packetLength);

                ipltBytesRead += encodedBytes;
                bytesRead += encodedBytes;
            }

            // Verify we read exactly the expected number of bytes
            if (ipltBytesRead != ipltSize)
            {
                throw new IOException(
                    $"PLT marker segment size mismatch: expected {ipltSize} bytes of packet data, read {ipltBytesRead}");
            }

            return bytesRead;
        }

        /// <summary>
        /// Decodes a variable-length integer from the stream.
        /// Format: 7 bits of data per byte, MSB is continuation bit.
        /// MSB = 1 means more bytes follow, MSB = 0 means this is the last byte.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The decoded integer value.</returns>
        /// <exception cref="ArgumentNullException">If stream is null.</exception>
        /// <exception cref="EndOfStreamException">If unexpected end of stream is encountered.</exception>
        public static int DecodeVariableLengthInt(Stream stream)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            var value = 0;
            byte b;

            do
            {
                var readByte = stream.ReadByte();
                if (readByte == -1)
                    throw new EndOfStreamException("Unexpected end of stream while reading variable-length integer");

                b = (byte)readByte;

                // Extract 7 bits of data
                value = (value << 7) | (b & 0x7F);

            } while ((b & 0x80) != 0); // Continue if continuation bit (MSB) is set

            return value;
        }

        /// <summary>
        /// Reads all PLT marker segments for a tile-part.
        /// Multiple PLT markers with different Zplt values may exist if packet data is large.
        /// </summary>
        /// <param name="stream">The input stream.</param>
        /// <param name="pltData">The PacketLengthsData to store the lengths in.</param>
        /// <param name="tileIdx">The tile index.</param>
        /// <param name="maxMarkers">Maximum number of PLT markers to read (default 256).</param>
        /// <returns>The total number of bytes read across all PLT markers.</returns>
        public static int ReadAllPLTMarkers(Stream stream, PacketLengthsData pltData, int tileIdx, int maxMarkers = 256)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (pltData == null)
                throw new ArgumentNullException(nameof(pltData));

            var totalBytesRead = 0;
            var markersRead = 0;

            // Read PLT markers until we find a marker that's not PLT or reach max
            while (markersRead < maxMarkers)
            {
                // Peek at next 2 bytes to check if it's a PLT marker
                var marker1 = stream.ReadByte();
                var marker2 = stream.ReadByte();

                if (marker1 == -1 || marker2 == -1)
                    break; // End of stream

                var marker = (short)((marker1 << 8) | marker2);

                // Check if it's a PLT marker (0xFF58)
                if (marker == Markers.PLT)
                {
                    // It's a PLT marker, read it
                    var bytesRead = ReadPLT(stream, pltData, tileIdx);
                    totalBytesRead += bytesRead + 2; // +2 for marker itself
                    markersRead++;
                }
                else
                {
                    // Not a PLT marker, rewind and stop
                    stream.Seek(-2, SeekOrigin.Current);
                    break;
                }
            }

            return totalBytesRead;
        }

        /// <summary>
        /// Validates the packet lengths read from PLT markers.
        /// Checks for reasonable values and consistency.
        /// </summary>
        /// <param name="pltData">The packet length data to validate.</param>
        /// <param name="tileIdx">The tile index to validate.</param>
        /// <param name="maxPacketLength">Maximum allowed packet length (default 65535).</param>
        /// <returns>True if valid, false otherwise.</returns>
        public static bool ValidatePacketLengths(PacketLengthsData pltData, int tileIdx, int maxPacketLength = 65535)
        {
            if (pltData == null || !pltData.HasPacketLengths)
                return false;

            var packetCount = pltData.GetPacketCount(tileIdx);
            if (packetCount == 0)
                return false;

            // Validate each packet length
            foreach (var entry in pltData.GetPacketEntries(tileIdx))
            {
                if (entry.PacketLength < 0 || entry.PacketLength > maxPacketLength)
                    return false;
            }

            return true;
        }
    }
}
