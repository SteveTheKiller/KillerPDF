// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreJ2K.j2k.codestream.metadata
{
    /// <summary>
    /// Represents Tile-part Lengths (TLM) marker segment information for fast tile access.
    /// The TLM marker contains the lengths of all tile-parts, allowing decoders to
    /// quickly locate specific tiles without parsing the entire codestream.
    /// </summary>
    public class TilePartLengthsData
    {
        /// <summary>
        /// Gets the list of tile-part entries.
        /// </summary>
        public List<TilePartEntry> TilePartEntries { get; } = new List<TilePartEntry>();

        /// <summary>
        /// Gets the total number of tile-parts across all tiles.
        /// </summary>
        public int TotalTileParts => TilePartEntries.Count;

        /// <summary>
        /// Gets whether any TLM information has been recorded.
        /// </summary>
        public bool HasTilePartLengths => TilePartEntries.Count > 0;

        /// <summary>
        /// Adds a tile-part entry.
        /// </summary>
        /// <param name="tileIndex">The tile index.</param>
        /// <param name="tilePartIndex">The tile-part index within the tile.</param>
        /// <param name="tilePartLength">The length of the tile-part in bytes.</param>
        public void AddTilePart(int tileIndex, int tilePartIndex, int tilePartLength)
        {
            TilePartEntries.Add(new TilePartEntry
            {
                TileIndex = tileIndex,
                TilePartIndex = tilePartIndex,
                TilePartLength = tilePartLength
            });
        }

        /// <summary>
        /// Gets all tile-part entries for a specific tile.
        /// </summary>
        /// <param name="tileIndex">The tile index.</param>
        /// <returns>Enumerable of tile-part entries for the specified tile.</returns>
        public IEnumerable<TilePartEntry> GetTilePartEntries(int tileIndex)
        {
            return TilePartEntries.Where(e => e.TileIndex == tileIndex);
        }

        /// <summary>
        /// Gets the total length of all tile-parts for a specific tile.
        /// </summary>
        /// <param name="tileIndex">The tile index.</param>
        /// <returns>Total length in bytes of all tile-parts for the tile.</returns>
        public int GetTotalTileLength(int tileIndex)
        {
            return TilePartEntries
                .Where(e => e.TileIndex == tileIndex)
                .Sum(e => e.TilePartLength);
        }

        /// <summary>
        /// Gets the number of tile-parts for a specific tile.
        /// </summary>
        /// <param name="tileIndex">The tile index.</param>
        /// <returns>Number of tile-parts for the tile.</returns>
        public int GetTilePartCount(int tileIndex)
        {
            return TilePartEntries.Count(e => e.TileIndex == tileIndex);
        }

        /// <summary>
        /// Gets the maximum tile index.
        /// </summary>
        public int MaxTileIndex => TilePartEntries.Any() ? TilePartEntries.Max(e => e.TileIndex) : -1;

        /// <summary>
        /// Gets the total size of all tile-parts in bytes.
        /// </summary>
        public long TotalSize => TilePartEntries.Sum(e => (long)e.TilePartLength);

        /// <summary>
        /// Clears all tile-part entries.
        /// </summary>
        public void Clear()
        {
            TilePartEntries.Clear();
        }

        /// <summary>
        /// Returns a string representation of the TLM data.
        /// </summary>
        public override string ToString()
        {
            if (!HasTilePartLengths)
                return "No TLM data";

            var tileCount = MaxTileIndex + 1;
            return $"TLM: {TotalTileParts} tile-parts across {tileCount} tiles, {TotalSize:N0} bytes total";
        }

        /// <summary>
        /// Gets statistics about the tile-parts.
        /// </summary>
        public TilePartStatistics GetStatistics()
        {
            if (!HasTilePartLengths)
                return null;

            var stats = new TilePartStatistics
            {
                TotalTileParts = TotalTileParts,
                TotalTiles = MaxTileIndex + 1,
                TotalSize = TotalSize
            };

            // Calculate per-tile statistics
            for (var tileIdx = 0; tileIdx <= MaxTileIndex; tileIdx++)
            {
                var tileParts = GetTilePartEntries(tileIdx).ToList();
                if (tileParts.Any())
                {
                    stats.TilePartCounts.Add(tileIdx, tileParts.Count);
                    stats.TileLengths.Add(tileIdx, tileParts.Sum(e => e.TilePartLength));
                }
            }

            // Calculate average, min, max
            if (stats.TileLengths.Any())
            {
                stats.AverageTileLength = (int)stats.TileLengths.Values.Average();
                stats.MinTileLength = stats.TileLengths.Values.Min();
                stats.MaxTileLength = stats.TileLengths.Values.Max();
            }

            if (stats.TilePartCounts.Any())
            {
                stats.AverageTilePartCount = (int)Math.Ceiling(stats.TilePartCounts.Values.Average());
                stats.MinTilePartCount = stats.TilePartCounts.Values.Min();
                stats.MaxTilePartCount = stats.TilePartCounts.Values.Max();
            }

            return stats;
        }
    }

    /// <summary>
    /// Represents a single tile-part entry in the TLM marker.
    /// </summary>
    public class TilePartEntry
    {
        /// <summary>
        /// Gets or sets the tile index.
        /// </summary>
        public int TileIndex { get; set; }

        /// <summary>
        /// Gets or sets the tile-part index within the tile.
        /// </summary>
        public int TilePartIndex { get; set; }

        /// <summary>
        /// Gets or sets the length of the tile-part in bytes (including SOT marker).
        /// </summary>
        public int TilePartLength { get; set; }

        public override string ToString()
        {
            return $"Tile {TileIndex}, Part {TilePartIndex}: {TilePartLength:N0} bytes";
        }
    }

    /// <summary>
    /// Statistics about tile-parts from TLM data.
    /// </summary>
    public class TilePartStatistics
    {
        /// <summary>
        /// Total number of tile-parts.
        /// </summary>
        public int TotalTileParts { get; set; }

        /// <summary>
        /// Total number of tiles.
        /// </summary>
        public int TotalTiles { get; set; }

        /// <summary>
        /// Total size of all tile-parts in bytes.
        /// </summary>
        public long TotalSize { get; set; }

        /// <summary>
        /// Number of tile-parts per tile.
        /// </summary>
        public Dictionary<int, int> TilePartCounts { get; } = new Dictionary<int, int>();

        /// <summary>
        /// Total length per tile.
        /// </summary>
        public Dictionary<int, int> TileLengths { get; } = new Dictionary<int, int>();

        /// <summary>
        /// Average tile length.
        /// </summary>
        public int AverageTileLength { get; set; }

        /// <summary>
        /// Minimum tile length.
        /// </summary>
        public int MinTileLength { get; set; }

        /// <summary>
        /// Maximum tile length.
        /// </summary>
        public int MaxTileLength { get; set; }

        /// <summary>
        /// Average number of tile-parts per tile.
        /// </summary>
        public int AverageTilePartCount { get; set; }

        /// <summary>
        /// Minimum tile-parts in any tile.
        /// </summary>
        public int MinTilePartCount { get; set; }

        /// <summary>
        /// Maximum tile-parts in any tile.
        /// </summary>
        public int MaxTilePartCount { get; set; }

        public override string ToString()
        {
            return $"Tiles: {TotalTiles}, Tile-parts: {TotalTileParts}, " +
                   $"Avg tile size: {AverageTileLength:N0} bytes, " +
                   $"Avg parts/tile: {AverageTilePartCount}";
        }
    }
}
