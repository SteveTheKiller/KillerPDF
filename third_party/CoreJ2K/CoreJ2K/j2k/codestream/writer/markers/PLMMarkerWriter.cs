// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.IO;

namespace CoreJ2K.j2k.codestream.writer
{
    /// <summary>
    /// Helper class for writing PLM (Packet Length, Main header) marker segments.
    /// PLM markers contain packet lengths for all tiles, allowing decoders to
    /// quickly locate packets without parsing packet headers.
    /// </summary>
    public static class PLMMarkerWriter
    {
        /// <summary>
        /// Writes PLM marker segment(s) to the provided BinaryWriter.
        /// Multiple PLM markers may be written if there are many packets.
        /// </summary>
        /// <param name="writer">The BinaryWriter to write to</param>
        /// <param name="plm">The packet length data to write</param>
        public static void WritePLM(BinaryWriter writer, metadata.PacketLengthsData plm)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
                
            if (plm == null || !plm.HasPacketLengths)
                return;

            try
            {
                // Group packets by tile for PLM format
                var maxTileIndex = plm.MaxTileIndex;
                var packetsByTile = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<int>>();
                
                for (int t = 0; t <= maxTileIndex; t++)
                {
                    packetsByTile[t] = new System.Collections.Generic.List<int>();
                }
                
                foreach (var entry in plm.PacketEntries)
                {
                    packetsByTile[entry.TileIndex].Add(entry.PacketLength);
                }
                
                // Write PLM markers using variable-length encoding
                WritePLMMarkersForTiles(writer, packetsByTile, maxTileIndex);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Error writing PLM marker: {e.Message}", e);
            }
        }

        private static void WritePLMMarkersForTiles(
            BinaryWriter writer,
            System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<int>> packetsByTile,
            int maxTileIndex)
        {
            int zplm = 0; // PLM marker index
            var tileIndex = 0;
            var packetIndex = 0;
            
            while (tileIndex <= maxTileIndex)
            {
                // Create PLM marker data
                using (var plmData = new MemoryStream())
                using (var plmWriter = new BinaryWriter(plmData))
                {
                    // Write Zplm (marker index)
                    plmWriter.Write((byte)zplm++);
                    
                    int plmDataSize = 1; // Just Zplm so far
                    bool hasData = false;
                    
                    // Fill this PLM marker (max ~65530 bytes to stay under 65535 limit)
                    while (tileIndex <= maxTileIndex && plmDataSize < 65530)
                    {
                        var currentPackets = packetsByTile[tileIndex];
                        
                        if (packetIndex >= currentPackets.Count)
                        {
                            // Move to next tile
                            tileIndex++;
                            packetIndex = 0;
                            continue;
                        }
                        
                        // Write packets for this tile
                        var (bytesWritten, packetsWritten) = WritePacketsForTile(
                            plmWriter, currentPackets, ref packetIndex, 65530 - plmDataSize);
                        
                        if (packetsWritten > 0)
                        {
                            plmDataSize += bytesWritten;
                            hasData = true;
                        }
                        
                        // Move to next tile if we finished this one
                        if (packetIndex >= currentPackets.Count)
                        {
                            tileIndex++;
                            packetIndex = 0;
                        }
                        
                        // Break if no more space
                        if (plmDataSize >= 65530)
                            break;
                    }
                    
                    // Write this PLM marker if it has data
                    if (hasData)
                    {
                        plmData.TryGetBuffer(out var plmSegment);
                        var plmDataLen = (int)plmData.Length;
                        int lplm = 2 + plmDataLen; // Lplm includes length field itself

                        // Write PLM marker
                        writer.Write(Markers.PLM);

                        // Write Lplm (marker length)
                        writer.Write((short)lplm);

                        // Write PLM data (Zplm + Nplm + packet lengths)
                        writer.Write(plmSegment.Array!, plmSegment.Offset, plmDataLen);
                    }
                }
                
                // Break if we've processed all tiles
                if (tileIndex > maxTileIndex)
                    break;
            }
        }

        private static (int bytesWritten, int packetsWritten) WritePacketsForTile(
            BinaryWriter writer,
            System.Collections.Generic.List<int> packets,
            ref int startIndex,
            int maxBytes)
        {
            using (var nplmData = new MemoryStream())
            using (var nplmWriter = new BinaryWriter(nplmData))
            {
                int packetsWritten = 0;
                int currentIndex = startIndex;
                
                // Write packet lengths using variable-length encoding
                while (currentIndex < packets.Count)
                {
                    int packetLength = packets[currentIndex];
                    int encodedSize = GetEncodedSize(packetLength);
                    
                    // Check if we have space (including Nplm header)
                    if (nplmData.Length + encodedSize + 4 > maxBytes) // +4 for max Nplm size
                        break;
                    
                    // Write encoded packet length
                    WriteVariableLengthInt(nplmWriter, packetLength);
                    
                    currentIndex++;
                    packetsWritten++;
                }
                
                // Only write if we wrote some packets
                if (packetsWritten > 0)
                {
                    nplmData.TryGetBuffer(out var nplmSegment);
                    int nplmLength = (int)nplmData.Length;

                    // Write Nplm (number of bytes for this tile's packets)
                    long startPos = writer.BaseStream.Position;
                    WriteVariableLengthInt(writer, nplmLength);
                    long nplmHeaderSize = writer.BaseStream.Position - startPos;

                    // Write the packet length data
                    writer.Write(nplmSegment.Array!, nplmSegment.Offset, nplmLength);
                    
                    startIndex = currentIndex;
                    return ((int)(nplmHeaderSize + nplmLength), packetsWritten);
                }
                
                return (0, 0);
            }
        }

        private static void WriteVariableLengthInt(BinaryWriter writer, int value)
        {
            // Variable-length encoding: bit 7 is continuation bit
            // Values < 128: 1 byte
            // Values < 16384: 2 bytes  
            // Values < 2097152: 3 bytes
            // Values < 268435456: 4 bytes
            
            if (value < 128)
            {
                writer.Write((byte)value);
            }
            else if (value < 16384)
            {
                writer.Write((byte)(0x80 | (value >> 7)));
                writer.Write((byte)(value & 0x7F));
            }
            else if (value < 2097152)
            {
                writer.Write((byte)(0x80 | (value >> 14)));
                writer.Write((byte)(0x80 | ((value >> 7) & 0x7F)));
                writer.Write((byte)(value & 0x7F));
            }
            else
            {
                writer.Write((byte)(0x80 | (value >> 21)));
                writer.Write((byte)(0x80 | ((value >> 14) & 0x7F)));
                writer.Write((byte)(0x80 | ((value >> 7) & 0x7F)));
                writer.Write((byte)(value & 0x7F));
            }
        }

        private static int GetEncodedSize(int value)
        {
            if (value < 128) return 1;
            if (value < 16384) return 2;
            if (value < 2097152) return 3;
            return 4;
        }
    }
}
