// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.IO;

namespace CoreJ2K.j2k.codestream.writer
{
    /// <summary>
    /// Helper class for writing TLM (Tile-part Lengths) marker segments.
    /// TLM markers contain tile-part lengths for fast random tile access.
    /// </summary>
    public static class TLMMarkerWriter
    {
        /// <summary>
        /// Writes TLM marker segment(s) to the provided BinaryWriter.
        /// Multiple TLM markers may be written if there are many tile-parts.
        /// Note: This method writes in big-endian format as required by JPEG 2000.
        /// </summary>
        /// <param name="writer">The BinaryWriter to write to</param>
        /// <param name="tlm">The tile-part length data to write</param>
        public static void WriteTLM(BinaryWriter writer, metadata.TilePartLengthsData tlm)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
                
            if (tlm == null || !tlm.HasTilePartLengths)
                return;

            try
            {
                // Determine optimal field sizes
                int maxTileIndex = tlm.MaxTileIndex;
                long maxLength = 0;
                foreach (var entry in tlm.TilePartEntries)
                {
                    if (entry.TilePartLength > maxLength)
                        maxLength = entry.TilePartLength;
                }
                
                // Determine Ttlm size (0, 1, or 2 bytes)
                // Use 1 byte if max tile index < 256, 2 bytes otherwise
                int ttlmSize;
                if (maxTileIndex < 256)
                    ttlmSize = 1;
                else
                    ttlmSize = 2;
                
                // Determine Ptlm size (2 or 4 bytes)
                int ptlmSize = (maxLength <= 65535) ? 2 : 4;
                
                // Calculate Stlm field
                // Bits 6-7: Ttlm size (00=0, 01=1 byte, 10=2 bytes)
                // Bits 4-5: Ptlm size (00=2 bytes, 01=4 bytes)
                int stlm = (ttlmSize << 6) | ((ptlmSize == 4 ? 1 : 0) << 4);
                
                // Calculate entry size and max entries per marker
                int entrySize = ttlmSize + ptlmSize;
                int maxEntries = (65535 - 4) / entrySize;  // Max entries in one TLM marker
                
                var entries = new System.Collections.Generic.List<metadata.TilePartEntry>(tlm.TilePartEntries);
                int totalEntries = entries.Count;
                int entryIndex = 0;
                int ztlm = 0;
                
                // Write TLM markers (may need multiple if many tiles)
                while (entryIndex < totalEntries)
                {
                    int entriesInThisMarker = System.Math.Min(maxEntries, totalEntries - entryIndex);
                    int ltlm = 4 + (entriesInThisMarker * entrySize);
                    
                    // Write TLM marker (big-endian)
                    WriteBigEndianShort(writer, Markers.TLM);
                    
                    // Write Ltlm (marker length) (big-endian)
                    WriteBigEndianShort(writer, (short)ltlm);
                    
                    // Write Ztlm (marker index)
                    writer.Write((byte)ztlm++);
                    
                    // Write Stlm (size parameters)
                    writer.Write((byte)stlm);
                    
                    // Write tile-part entries
                    for (int i = 0; i < entriesInThisMarker; i++)
                    {
                        var entry = entries[entryIndex++];
                        
                        // Write Ttlm (tile index) (big-endian)
                        if (ttlmSize == 1)
                        {
                            writer.Write((byte)entry.TileIndex);
                        }
                        else if (ttlmSize == 2)
                        {
                            WriteBigEndianShort(writer, (short)entry.TileIndex);
                        }
                        // ttlmSize == 0 means implicit (sequential), not used here
                        
                        // Write Ptlm (tile-part length) (big-endian)
                        if (ptlmSize == 2)
                        {
                            WriteBigEndianShort(writer, (short)entry.TilePartLength);
                        }
                        else // ptlmSize == 4
                        {
                            WriteBigEndianInt(writer, entry.TilePartLength);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Error writing TLM marker: {e.Message}", e);
            }
        }

        /// <summary>
        /// Writes a short value in big-endian format.
        /// </summary>
        private static void WriteBigEndianShort(BinaryWriter writer, short value)
        {
            writer.Write((byte)((value >> 8) & 0xFF));
            writer.Write((byte)(value & 0xFF));
        }

        /// <summary>
        /// Writes an int value in big-endian format.
        /// </summary>
        private static void WriteBigEndianInt(BinaryWriter writer, int value)
        {
            writer.Write((byte)((value >> 24) & 0xFF));
            writer.Write((byte)((value >> 16) & 0xFF));
            writer.Write((byte)((value >> 8) & 0xFF));
            writer.Write((byte)(value & 0xFF));
        }
    }
}
