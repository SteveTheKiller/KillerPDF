/* 
* 
* COPYRIGHT:
* 
* This software module was originally developed by Rapha�l Grosbois and
* Diego Santa Cruz (Swiss Federal Institute of Technology-EPFL); Joel
* Askel�f (Ericsson Radio Systems AB); and Bertrand Berthelot, David
* Bouchard, F�lix Henry, Gerard Mozelle and Patrice Onno (Canon Research
* Centre France S.A) in the course of development of the JPEG2000
* standard as specified by ISO/IEC 15444 (JPEG 2000 Standard). This
* software module is an implementation of a part of the JPEG 2000
* Standard. Swiss Federal Institute of Technology-EPFL, Ericsson Radio
* Systems AB and Canon Research Centre France S.A (collectively JJ2000
* Partners) agree not to assert against ISO/IEC and users of the JPEG
* 2000 Standard (Users) any of their rights under the copyright, not
* including other intellectual property rights, for this software module
* with respect to the usage by ISO/IEC and Users of this software module
* or modifications thereof for use in hardware or software products
* claiming conformance to the JPEG 2000 Standard. Those intending to use
* this software module in hardware or software products are advised that
* their use may infringe existing patents. The original developers of
* this software module, JJ2000 Partners and ISO/IEC assume no liability
* for use of this software module or modifications thereof. No license
* or right to this software module is granted for non JPEG 2000 Standard
* conforming products. JJ2000 Partners have full right to use this
* software module for his/her own purpose, assign or donate this
* software module to any third party and to inhibit third parties from
* using this software module for non JPEG 2000 Standard conforming
* products. This copyright notice must be included in all copies or
* derivative works of this software module.
* 
* Copyright (c) 1999/2000 JJ2000 Partners.
* */

using CoreJ2K.j2k.image;

namespace CoreJ2K.j2k.wavelet.synthesis
{

    /// <summary> This class provides a default implementation for the methods of the
    /// 'MultiResImgData' interface. The default implementation consists just in
    /// returning the value of the source, where the source is another
    /// 'MultiResImgData' object.
    /// 
    /// This abstract class can be used to facilitate the development of other
    /// classes that implement 'MultiResImgData'. For example a dequantizer can
    /// inherit from this class and all the trivial methods do not have to be
    /// reimplemented.
    /// 
    /// If the default implementation of a method provided in this class does
    /// not suit a particular implementation of the 'MultiResImgData' interface,
    /// the method can be overriden to implement the proper behaviour.
    /// 
    /// </summary>
    /// <seealso cref="MultiResImgData" />
    public abstract class MultiResImgDataAdapter : MultiResImgData
    {
        /// <summary>Returns the nominal tiles width </summary>
        public virtual int NomTileWidth => mressrc.NomTileWidth;

        /// <summary>Returns the nominal tiles height </summary>
        public virtual int NomTileHeight => mressrc.NomTileHeight;

        /// <summary> Returns the number of components in the image.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <returns> The number of components in the image.
        /// 
        /// </returns>
        public virtual int NumComps => mressrc.NumComps;

        /// <summary> Returns the index of the current tile, relative to a standard scan-line
        /// order.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <returns> The current tile's index (starts at 0).
        /// 
        /// </returns>
        public virtual int TileIdx => mressrc.TileIdx;

        /// <summary>Returns the horizontal tile partition offset in the reference grid </summary>
        public virtual int TilePartULX => mressrc.TilePartULX;

        /// <summary>Returns the vertical tile partition offset in the reference grid </summary>
        public virtual int TilePartULY => mressrc.TilePartULY;

        /// <summary>Index of the current tile </summary>
        protected internal int tIdx = 0;

        /// <summary>The MultiResImgData source </summary>
        protected internal MultiResImgData mressrc;

        /// <summary> Instantiates the MultiResImgDataAdapter object specifying the
        /// MultiResImgData source.
        /// 
        /// </summary>
        /// <param name="src">From where to obrtain the MultiResImgData values.
        /// 
        /// </param>
        protected internal MultiResImgDataAdapter(MultiResImgData src)
        {
            mressrc = src;
        }

        /// <summary> Returns the overall width of the current tile in pixels, for the given
        /// resolution level. This is the tile's width without accounting for any
        /// component subsampling.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="rl">The resolution level, from 0 to L.
        /// 
        /// </param>
        /// <returns> The total current tile's width in pixels.
        /// 
        /// </returns>
        public virtual int GetTileWidth(int rl)
        {
            return mressrc.GetTileWidth(rl);
        }

        /// <summary> Returns the overall height of the current tile in pixels, for the given
        /// resolution level. This is the tile's height without accounting for any
        /// component subsampling.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="rl">The resolution level, from 0 to L.
        /// 
        /// </param>
        /// <returns> The total current tile's height in pixels.
        /// 
        /// </returns>
        public virtual int GetTileHeight(int rl)
        {
            return mressrc.GetTileHeight(rl);
        }

        /// <summary> Returns the overall width of the image in pixels, for the given
        /// resolution level. This is the image's width without accounting for any
        /// component subsampling or tiling.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="rl">The resolution level, from 0 to L.
        /// 
        /// </param>
        /// <returns> The total image's width in pixels.
        /// 
        /// </returns>
        public virtual int GetImgWidth(int rl)
        {
            return mressrc.GetImgWidth(rl);
        }

        /// <summary> Returns the overall height of the image in pixels, for the given
        /// resolution level. This is the image's height without accounting for any
        /// component subsampling or tiling.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="rl">The resolution level, from 0 to L.
        /// 
        /// </param>
        /// <returns> The total image's height in pixels.
        /// 
        /// </returns>
        public virtual int GetImgHeight(int rl)
        {
            return mressrc.GetImgHeight(rl);
        }

        /// <summary> Returns the component subsampling factor in the horizontal direction,
        /// for the specified component. This is, approximately, the ratio of
        /// dimensions between the reference grid and the component itself, see the
        /// 'ImgData' interface desription for details.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="c">The index of the component (between 0 and N-1)
        /// 
        /// </param>
        /// <returns> The horizontal subsampling factor of component 'c'
        /// 
        /// </returns>
        /// <seealso cref="j2k.image.ImgData" />
        public virtual int GetCompSubsX(int c)
        {
            return mressrc.GetCompSubsX(c);
        }

        /// <summary> Returns the component subsampling factor in the vertical direction, for
        /// the specified component. This is, approximately, the ratio of
        /// dimensions between the reference grid and the component itself, see the
        /// 'ImgData' interface desription for details.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="c">The index of the component (between 0 and N-1)
        /// 
        /// </param>
        /// <returns> The vertical subsampling factor of component 'c'
        /// 
        /// </returns>
        /// <seealso cref="j2k.image.ImgData" />
        public virtual int GetCompSubsY(int c)
        {
            return mressrc.GetCompSubsY(c);
        }

        /// <summary> Returns the width in pixels of the specified tile-component for the
        /// given resolution level.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="t">Tile index.
        /// 
        /// </param>
        /// <param name="c">The index of the component, from 0 to N-1.
        /// 
        /// </param>
        /// <param name="rl">The resolution level, from 0 to L.
        /// 
        /// </param>
        /// <returns> The width in pixels of component <tt>c</tt> in tile <tt>t</tt>
        /// for resolution level <tt>rl</tt>.
        /// 
        /// </returns>
        public virtual int GetTileCompWidth(int t, int c, int rl)
        {
            return mressrc.GetTileCompWidth(t, c, rl);
        }

        /// <summary> Returns the height in pixels of the specified tile-component for the
        /// given resolution level.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="t">The tile index.
        /// 
        /// </param>
        /// <param name="c">The index of the component, from 0 to N-1.
        /// 
        /// </param>
        /// <param name="rl">The resolution level, from 0 to L.
        /// 
        /// </param>
        /// <returns> The height in pixels of component <tt>c</tt> in tile
        /// <tt>t</tt>. 
        /// 
        /// </returns>
        public virtual int GetTileCompHeight(int t, int c, int rl)
        {
            return mressrc.GetTileCompHeight(t, c, rl);
        }

        /// <summary> Returns the width in pixels of the specified component in the overall
        /// image, for the given resolution level.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="c">The index of the component, from 0 to N-1.
        /// 
        /// </param>
        /// <param name="rl">The resolution level, from 0 to L.
        /// 
        /// </param>
        /// <returns> The width in pixels of component <tt>c</tt> in the overall
        /// image.
        /// 
        /// </returns>
        public virtual int GetCompImgWidth(int c, int rl)
        {
            return mressrc.GetCompImgWidth(c, rl);
        }

        /// <summary> Returns the height in pixels of the specified component in the overall
        /// image, for the given resolution level.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="c">The index of the component, from 0 to N-1.
        /// 
        /// </param>
        /// <param name="rl">The resolution level, from 0 to L.
        /// 
        /// </param>
        /// <returns> The height in pixels of component <tt>c</tt> in the overall
        /// image.
        /// 
        /// </returns>
        public virtual int GetCompImgHeight(int c, int rl)
        {
            return mressrc.GetCompImgHeight(c, rl);
        }

        /// <summary> Changes the current tile, given the new indexes. An
        /// IllegalArgumentException is thrown if the indexes do not correspond to
        /// a valid tile.
        /// 
        /// This default implementation just changes the tile in the source.
        /// 
        /// </summary>
        /// <param name="x">The horizontal indexes the tile.
        /// 
        /// </param>
        /// <param name="y">The vertical indexes of the new tile.
        /// 
        /// </param>
        public virtual void SetTile(int x, int y)
        {
            mressrc.SetTile(x, y);
            tIdx = TileIdx;
        }

        /// <summary> Advances to the next tile, in standard scan-line order (by rows then
        /// columns). An NoNextElementException is thrown if the current tile is
        /// the last one (i.e. there is no next tile).
        /// 
        /// This default implementation just changes the tile in the source.
        /// 
        /// </summary>
        public virtual void NextTile()
        {
            mressrc.NextTile();
            tIdx = TileIdx;
        }

        /// <summary> Returns the indexes of the current tile. These are the horizontal and
        /// vertical indexes of the current tile.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="co">If not null this object is used to return the information. If
        /// null a new one is created and returned.
        /// 
        /// </param>
        /// <returns> The current tile's indexes (vertical and horizontal indexes).
        /// 
        /// </returns>
        public virtual Coord GetTile(Coord? co)
        {
            return mressrc.GetTile(co);
        }

        /// <summary> Returns the horizontal coordinate of the upper-left corner of the
        /// specified resolution level in the given component of the current tile. 
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="c">The component index.
        /// 
        /// </param>
        /// <param name="rl">The resolution level index.
        /// 
        /// </param>
        public virtual int GetResULX(int c, int rl)
        {
            return mressrc.GetResULX(c, rl);
        }

        /// <summary> Returns the vertical coordinate of the upper-left corner of the
        /// specified resolution in the given component of the current tile. 
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="c">The component index.
        /// 
        /// </param>
        /// <param name="rl">The resolution level index.
        /// 
        /// </param>
        public virtual int GetResULY(int c, int rl)
        {
            return mressrc.GetResULY(c, rl);
        }

        /// <summary> Returns the horizontal coordinate of the image origin, the top-left
        /// corner, in the canvas system, on the reference grid at the specified
        /// resolution level.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="rl">The resolution level, from 0 to L.
        /// 
        /// </param>
        /// <returns> The horizontal coordinate of the image origin in the canvas
        /// system, on the reference grid.
        /// 
        /// </returns>
        public virtual int GetImgULX(int rl)
        {
            return mressrc.GetImgULX(rl);
        }

        /// <summary> Returns the vertical coordinate of the image origin, the top-left
        /// corner, in the canvas system, on the reference grid at the specified
        /// resolution level.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="rl">The resolution level, from 0 to L.
        /// 
        /// </param>
        /// <returns> The vertical coordinate of the image origin in the canvas
        /// system, on the reference grid.
        /// 
        /// </returns>
        public virtual int GetImgULY(int rl)
        {
            return mressrc.GetImgULY(rl);
        }

        /// <summary> Returns the number of tiles in the horizontal and vertical directions.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="co">If not null this object is used to return the information. If
        /// null a new one is created and returned.
        /// 
        /// </param>
        /// <returns> The number of tiles in the horizontal (Coord.x) and vertical
        /// (Coord.y) directions.
        /// 
        /// </returns>
        public virtual Coord GetNumTiles(Coord? co)
        {
            return mressrc.GetNumTiles(co);
        }

        /// <summary> Returns the total number of tiles in the image.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <returns> The total number of tiles in the image.
        /// 
        /// </returns>
        public virtual int GetNumTiles()
        {
            return mressrc.GetNumTiles();
        }
        public abstract SubbandSyn GetSynSubbandTree(int param1, int param2);
    }
}