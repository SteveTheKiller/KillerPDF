// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreJ2K.j2k.codestream.metadata
{
    /// <summary>
    /// Represents Packet Length (PLM/PLT) marker segment information.
    /// PLM markers in the main header contain packet lengths for all tiles,
    /// while PLT markers in tile-part headers contain packet lengths for that tile-part.
    /// This allows decoders to quickly locate packets without parsing packet headers.
    /// </summary>
    public class PacketLengthsData
    {
        /// <summary>
        /// Gets the list of packet length entries.
        /// </summary>
        public List<PacketLengthEntry> PacketEntries { get; } = new List<PacketLengthEntry>();

        /// <summary>
        /// Gets the total number of packets.
        /// </summary>
        public int TotalPackets => PacketEntries.Count;

        /// <summary>
        /// Gets whether any packet length information has been recorded.
        /// </summary>
        public bool HasPacketLengths => PacketEntries.Count > 0;

        /// <summary>
        /// Adds a packet length entry.
        /// </summary>
        /// <param name="tileIndex">The tile index.</param>
        /// <param name="packetLength">The length of the packet in bytes (header + body).</param>
        public void AddPacket(int tileIndex, int packetLength)
        {
            PacketEntries.Add(new PacketLengthEntry
            {
                TileIndex = tileIndex,
                PacketLength = packetLength
            });
        }

        /// <summary>
        /// Gets all packet entries for a specific tile.
        /// </summary>
        /// <param name="tileIndex">The tile index.</param>
        /// <returns>Enumerable of packet entries for the specified tile.</returns>
        public IEnumerable<PacketLengthEntry> GetPacketEntries(int tileIndex)
        {
            return PacketEntries.Where(e => e.TileIndex == tileIndex);
        }

        /// <summary>
        /// Gets the total length of all packets for a specific tile.
        /// </summary>
        /// <param name="tileIndex">The tile index.</param>
        /// <returns>Total length in bytes of all packets for the tile.</returns>
        public int GetTotalPacketLength(int tileIndex)
        {
            return PacketEntries
                .Where(e => e.TileIndex == tileIndex)
                .Sum(e => e.PacketLength);
        }

        /// <summary>
        /// Gets the number of packets for a specific tile.
        /// </summary>
        /// <param name="tileIndex">The tile index.</param>
        /// <returns>Number of packets for the tile.</returns>
        public int GetPacketCount(int tileIndex)
        {
            return PacketEntries.Count(e => e.TileIndex == tileIndex);
        }

        /// <summary>
        /// Gets the maximum tile index.
        /// </summary>
        public int MaxTileIndex => PacketEntries.Any() ? PacketEntries.Max(e => e.TileIndex) : -1;

        /// <summary>
        /// Gets the total size of all packets in bytes.
        /// </summary>
        public long TotalSize => PacketEntries.Sum(e => (long)e.PacketLength);

        /// <summary>
        /// Clears all packet entries.
        /// </summary>
        public void Clear()
        {
            PacketEntries.Clear();
        }

        /// <summary>
        /// Returns a string representation of the packet length data.
        /// </summary>
        public override string ToString()
        {
            if (!HasPacketLengths)
                return "No packet length data";

            var tileCount = MaxTileIndex + 1;
            return $"PLM: {TotalPackets} packets across {tileCount} tiles, {TotalSize:N0} bytes total";
        }

        /// <summary>
        /// Gets statistics about the packets.
        /// </summary>
        public PacketStatistics GetStatistics()
        {
            if (!HasPacketLengths)
                return null;

            var stats = new PacketStatistics
            {
                TotalPackets = TotalPackets,
                TotalTiles = MaxTileIndex + 1,
                TotalSize = TotalSize
            };

            // Calculate per-tile statistics
            for (var tileIdx = 0; tileIdx <= MaxTileIndex; tileIdx++)
            {
                var packets = GetPacketEntries(tileIdx).ToList();
                if (packets.Any())
                {
                    stats.PacketCounts.Add(tileIdx, packets.Count);
                    stats.TilePacketLengths.Add(tileIdx, packets.Sum(e => e.PacketLength));
                }
            }

            // Calculate average, min, max
            if (stats.TilePacketLengths.Any())
            {
                stats.AverageTilePacketLength = (int)stats.TilePacketLengths.Values.Average();
                stats.MinTilePacketLength = stats.TilePacketLengths.Values.Min();
                stats.MaxTilePacketLength = stats.TilePacketLengths.Values.Max();
            }

            if (stats.PacketCounts.Any())
            {
                stats.AveragePacketCount = (int)Math.Ceiling(stats.PacketCounts.Values.Average());
                stats.MinPacketCount = stats.PacketCounts.Values.Min();
                stats.MaxPacketCount = stats.PacketCounts.Values.Max();
            }

            if (PacketEntries.Any())
            {
                stats.AveragePacketLength = (int)PacketEntries.Average(e => e.PacketLength);
                stats.MinPacketLength = PacketEntries.Min(e => e.PacketLength);
                stats.MaxPacketLength = PacketEntries.Max(e => e.PacketLength);
            }

            return stats;
        }
    }

    /// <summary>
    /// Represents a single packet length entry.
    /// </summary>
    public class PacketLengthEntry
    {
        /// <summary>
        /// Gets or sets the tile index.
        /// </summary>
        public int TileIndex { get; set; }

        /// <summary>
        /// Gets or sets the length of the packet in bytes (header + body).
        /// </summary>
        public int PacketLength { get; set; }

        public override string ToString()
        {
            return $"Tile {TileIndex}: {PacketLength:N0} bytes";
        }
    }

    /// <summary>
    /// Statistics about packets from PLM/PLT data.
    /// </summary>
    public class PacketStatistics
    {
        /// <summary>
        /// Total number of packets.
        /// </summary>
        public int TotalPackets { get; set; }

        /// <summary>
        /// Total number of tiles.
        /// </summary>
        public int TotalTiles { get; set; }

        /// <summary>
        /// Total size of all packets in bytes.
        /// </summary>
        public long TotalSize { get; set; }

        /// <summary>
        /// Number of packets per tile.
        /// </summary>
        public Dictionary<int, int> PacketCounts { get; } = new Dictionary<int, int>();

        /// <summary>
        /// Total packet length per tile.
        /// </summary>
        public Dictionary<int, int> TilePacketLengths { get; } = new Dictionary<int, int>();

        /// <summary>
        /// Average packet length per tile.
        /// </summary>
        public int AverageTilePacketLength { get; set; }

        /// <summary>
        /// Minimum packet length per tile.
        /// </summary>
        public int MinTilePacketLength { get; set; }

        /// <summary>
        /// Maximum packet length per tile.
        /// </summary>
        public int MaxTilePacketLength { get; set; }

        /// <summary>
        /// Average number of packets per tile.
        /// </summary>
        public int AveragePacketCount { get; set; }

        /// <summary>
        /// Minimum packets in any tile.
        /// </summary>
        public int MinPacketCount { get; set; }

        /// <summary>
        /// Maximum packets in any tile.
        /// </summary>
        public int MaxPacketCount { get; set; }

        /// <summary>
        /// Average individual packet length.
        /// </summary>
        public int AveragePacketLength { get; set; }

        /// <summary>
        /// Minimum individual packet length.
        /// </summary>
        public int MinPacketLength { get; set; }

        /// <summary>
        /// Maximum individual packet length.
        /// </summary>
        public int MaxPacketLength { get; set; }

        public override string ToString()
        {
            return $"Tiles: {TotalTiles}, Packets: {TotalPackets}, " +
                   $"Avg packet: {AveragePacketLength:N0} bytes, " +
                   $"Avg packets/tile: {AveragePacketCount}";
        }
    }
}
