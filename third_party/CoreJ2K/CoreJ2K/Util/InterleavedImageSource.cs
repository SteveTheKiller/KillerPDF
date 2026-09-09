// Copyright (c) 2007-2016 CSJ2K contributors.
// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

namespace CoreJ2K.Util
{
    using j2k;
    using j2k.image;
    using System;

    /// <summary>
    /// Provides a BlkImgDataSrc implementation for interleaved image data.
    /// Used to feed image component data to the encoder from memory.
    /// </summary>
    public class InterleavedImageSource : BlkImgDataSrc
    {
        #region FIELDS

        private readonly int rb;

        private readonly bool[] sgnd;

        private readonly int[][] comps;

        #endregion

        #region CONSTRUCTORS

        public InterleavedImageSource(int w, int h, int nc, int rb, bool[] sgnd, int[][] comps)
        {
            this.TileWidth = w;
            this.TileHeight = h;
            this.NumComps = nc;
            this.rb = rb;
            this.sgnd = sgnd;
            this.comps = comps;
        }

        #endregion

        #region PROPERTIES

        public int TileWidth { get; }

        public int TileHeight { get; }

        public int NomTileWidth => TileWidth;

        public int NomTileHeight => TileHeight;

        public int ImgWidth => TileWidth;

        public int ImgHeight => TileHeight;

        public int NumComps { get; }

        public int TileIdx => 0;

        public int TilePartULX => 0;

        public int TilePartULY => 0;

        public int ImgULX => 0;

        public int ImgULY => 0;

        #endregion

        #region METHODS

        public int GetCompSubsX(int c)
        {
            return 1;
        }

        public int GetCompSubsY(int c)
        {
            return 1;
        }

        public int GetTileCompWidth(int t, int c)
        {
            if (t != 0)
            {
                throw new InvalidOperationException("Asking a tile-component width for a tile index" + " greater than 0 whereas there is only one tile");
            }
            return TileWidth;
        }

        public int GetTileCompHeight(int t, int c)
        {
            if (t != 0)
            {
                throw new InvalidOperationException("Asking a tile-component width for a tile index" + " greater than 0 whereas there is only one tile");
            }
            return TileHeight;
        }

        public int GetCompImgWidth(int c)
        {
            return TileWidth;
        }

        public int GetCompImgHeight(int c)
        {
            return TileHeight;
        }

        public int GetNomRangeBits(int compIndex)
        {
            return rb;
        }

        public void SetTile(int x, int y)
        {
            if (x != 0 || y != 0)
            {
                throw new ArgumentException();
            }
        }

        public void NextTile()
        {
            throw new NoNextElementException();
        }

        public Coord GetTile(Coord? co)
        {
            if (co != null)
            {
                co.x = 0;
                co.y = 0;
                return co;
            }

            return new Coord(0, 0);
        }

        public int GetCompULX(int c)
        {
            return 0;
        }

        public int GetCompULY(int c)
        {
            return 0;
        }

        public Coord GetNumTiles(Coord? co)
        {
            if (co != null)
            {
                co.x = 1;
                co.y = 1;
                return co;
            }

            return new Coord(1, 1);
        }

        public int GetNumTiles()
        {
            return 1;
        }

        public int GetFixedPoint(int compIndex)
        {
            return 0;
        }

        public DataBlk GetInternCompData(DataBlk blk, int compIndex)
        {
            if (compIndex < 0 || compIndex >= NumComps)
            {
                throw new ArgumentOutOfRangeException(nameof(compIndex));
            }

            // Validate block coordinates
            if (blk.ulx < 0 || blk.uly < 0 || blk.w < 0 || blk.h < 0 || blk.ulx + blk.w > TileWidth || blk.uly + blk.h > TileHeight)
            {
                throw new ArgumentOutOfRangeException(nameof(blk), "Requested block is outside image bounds");
            }

            var width = blk.w;
            var height = blk.h;
            var dest = new int[width * height];

            var compArr = comps[compIndex];
            var tileW = TileWidth;

            // Copy row-by-row using Array.Copy to leverage optimized native implementation
            var destOffset = 0;
            var srcRowStart = blk.uly * tileW + blk.ulx;
            for (var row = 0; row < height; ++row)
            {
                Array.Copy(compArr, srcRowStart + row * tileW, dest, destOffset, width);
                destOffset += width;
            }

            blk.offset = 0;
            blk.scanw = blk.w;
            blk.progressive = false;
            blk.Data = dest;

            return blk;
        }

        public DataBlk GetCompData(DataBlk blk, int c)
        {
            var newBlk = new DataBlkInt(blk.ulx, blk.uly, blk.w, blk.h);
            return GetInternCompData(newBlk, c);
        }

        public void Close()
        {
            // Do nothing.
        }

        public bool IsOrigSigned(int compIndex)
        {
            if (compIndex < 0 || compIndex >= NumComps)
            {
                throw new ArgumentOutOfRangeException(nameof(compIndex));
            }

            return sgnd[compIndex];
        }

        #endregion
    }
}
