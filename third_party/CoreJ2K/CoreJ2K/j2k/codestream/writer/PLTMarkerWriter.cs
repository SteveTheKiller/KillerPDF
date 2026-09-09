// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using CoreJ2K.j2k.codestream.metadata;
using System;
using System.Collections.Generic;
using System.IO;

namespace CoreJ2K.j2k.codestream.writer
{
    /// <summary>
    /// Writes PLT (Packet Length, tile-part header) markers.
    /// PLT markers store the lengths of packets within a tile-part,
    /// enabling fast packet boundary detection without parsing packet headers.
    /// </summary>
    public static class PLTMarkerWriter
    {
        /// <summary>
        /// Maximum length of a PLT marker segment (including Lplt field).
        /// </summary>
        public const int MAX_PLT_LENGTH = 65535;

        /// <summary>
        /// Writes a PLT marker segment for the specified tile and PLT index.
        /// </summary>
        /// <param name="out">The output stream to write to.</param>
        /// <param name="pltData">The packet length data.</param>
        /// <param name="tileIdx">The tile index.</param>
        /// <param name="zplt">The PLT marker index (0-255).</param>
        /// <returns>The number of bytes written.</returns>
        public static int WritePLT(Stream out_stream, PacketLengthsData pltData, int tileIdx, byte zplt)
        {
            if (out_stream == null)
                throw new ArgumentNullException(nameof(out_stream));
            if (pltData == null)
                throw new ArgumentNullException(nameof(pltData));

            var packetLengths = pltData.GetPacketEntries(tileIdx).GetEnumerator();
            if (!packetLengths.MoveNext())
                return 0; // No packets for this tile

            // Calculate how many packet lengths we can fit in one marker
            var tempList = new List<PacketLengthEntry>();
            foreach (var entry in pltData.GetPacketEntries(tileIdx))
            {
                tempList.Add(entry);
            }

            if (tempList.Count == 0)
                return 0;

            // Calculate encoded size for this set of packets
            var ipltSize = 0;
            foreach (var entry in tempList)
            {
                ipltSize += GetEncodedSize(entry.PacketLength);
            }

            // Check if we need to split across multiple PLT markers
            var maxDataSize = MAX_PLT_LENGTH - 3; // -3 for Lplt (2) and Zplt (1)
            if (ipltSize > maxDataSize)
            {
                // For now, just write what fits (TODO: implement multi-marker support)
                return WritePLTSegment(out_stream, tempList, maxDataSize, zplt);
            }

            return WritePLTSegment(out_stream, tempList, ipltSize, zplt);
        }

        /// <summary>
        /// Writes a single PLT marker segment.
        /// </summary>
        private static int WritePLTSegment(Stream out_stream, List<PacketLengthEntry> packets, int dataSize, byte zplt)
        {
            var bytesWritten = 0;
            var lplt = (ushort)(dataSize + 3); // +3 for Lplt itself (2 bytes) and Zplt (1 byte)

            // Write PLT marker (0xFF58)
            out_stream.WriteByte(0xFF);
            out_stream.WriteByte(0x58);
            bytesWritten += 2;

            // Write Lplt (marker segment length)
            out_stream.WriteByte((byte)(lplt >> 8));
            out_stream.WriteByte((byte)(lplt & 0xFF));
            bytesWritten += 2;

            // Write Zplt (PLT index)
            out_stream.WriteByte(zplt);
            bytesWritten++;

            // Write Iplt (packet lengths in variable-length format) directly — no per-packet allocation.
            var scratch = new byte[5]; // max 5 bytes for a 32-bit VLI
            foreach (var entry in packets)
            {
                int n = WriteVariableLengthIntToBuffer(entry.PacketLength, scratch);
                out_stream.Write(scratch, 0, n);
                bytesWritten += n;
            }

            return bytesWritten;
        }

        /// <summary>
        /// Encodes a non-negative integer as a variable-length value into <paramref name="buf"/> and
        /// returns the number of bytes written. The buffer must be at least 5 bytes.
        /// </summary>
        private static int WriteVariableLengthIntToBuffer(int value, byte[] buf)
        {
            // Build 7-bit chunks from LSB to MSB.
            int pos = 0;
            do
            {
                buf[pos++] = (byte)(value & 0x7F);
                value >>= 7;
            } while (value > 0);

            // Reverse so the most-significant chunk is first (big-endian output).
            for (int lo = 0, hi = pos - 1; lo < hi; lo++, hi--)
            {
                byte tmp = buf[lo];
                buf[lo] = buf[hi];
                buf[hi] = tmp;
            }

            // Set the continuation bit (MSB) on every byte except the last.
            for (int i = 0; i < pos - 1; i++)
                buf[i] |= 0x80;

            return pos;
        }

        /// <summary>
        /// Encodes an integer as a variable-length value (7 bits per byte with continuation bit).
        /// MSB = 1 means more bytes follow, MSB = 0 means this is the last byte.
        /// </summary>
        /// <param name="value">The value to encode.</param>
        /// <returns>The encoded bytes.</returns>
        public static byte[] EncodeVariableLengthInt(int value)
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Value must be non-negative");

            var buf = new byte[5];
            int len = WriteVariableLengthIntToBuffer(value, buf);
            var result = new byte[len];
            Array.Copy(buf, result, len);
            return result;
        }

        /// <summary>
        /// Decodes a variable-length integer from a stream.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The decoded value.</returns>
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

            } while ((b & 0x80) != 0); // Continue if continuation bit is set

            return value;
        }

        /// <summary>
        /// Calculates the encoded size (in bytes) of a value.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The number of bytes required to encode the value.</returns>
        public static int GetEncodedSize(int value)
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Value must be non-negative");

            if (value == 0)
                return 1;

            var size = 0;
            do
            {
                size++;
                value >>= 7;
            } while (value > 0);

            return size;
        }

        /// <summary>
        /// Calculates the total size of PLT marker segment(s) needed for the given packet lengths.
        /// </summary>
        /// <param name="pltData">The packet length data.</param>
        /// <param name="tileIdx">The tile index.</param>
        /// <returns>The total size in bytes, including all marker overhead.</returns>
        public static int CalculatePLTSize(PacketLengthsData pltData, int tileIdx)
        {
            if (pltData == null || !pltData.HasPacketLengths)
                return 0;

            var totalSize = 0;
            var ipltSize = 0;
            var zplt = 0;

            foreach (var entry in pltData.GetPacketEntries(tileIdx))
            {
                var encodedSize = GetEncodedSize(entry.PacketLength);

                // Check if adding this would exceed max marker size
                if (ipltSize + encodedSize > MAX_PLT_LENGTH - 3)
                {
                    // Finish current marker
                    totalSize += ipltSize + 5; // +5 for marker(2) + Lplt(2) + Zplt(1)
                    ipltSize = 0;
                    zplt++;

                    if (zplt > 255)
                        throw new InvalidOperationException("Too many PLT markers required (max 256)");
                }

                ipltSize += encodedSize;
            }

            // Add final marker
            if (ipltSize > 0)
            {
                totalSize += ipltSize + 5;
            }

            return totalSize;
        }
    }
}
