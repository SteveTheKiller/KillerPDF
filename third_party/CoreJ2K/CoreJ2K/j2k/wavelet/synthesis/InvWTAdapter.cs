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
using CoreJ2K.j2k.decoder;
using CoreJ2K.j2k.image;
using System;

namespace CoreJ2K.j2k.wavelet.synthesis
{

    /// <summary> This class provides default implementation of the methods in the 'InvWT'
    /// interface. The source is always a 'MultiResImgData', which is a
    /// multi-resolution image. The default implementation is just to return the
    /// value of the source at the current image resolution level, which is set by
    /// the 'setImgResLevel()' method.
    /// 
    /// This abstract class can be used to facilitate the development of other
    /// classes that implement the 'InvWT' interface, because most of the trivial
    /// methods are already implemented.
    /// 
    /// If the default implementation of a method provided in this class does
    /// not suit a particular implementation of the 'InvWT' interface, the method
    /// can be overriden to implement the proper behaviour.
    /// 
    /// If the 'setImgResLevel()' method is overriden then it is very important
    /// that the one of this class is called from the overriding method, so that
    /// the other methods in this class return the correct values.
    /// 
    /// </summary>
    /// <seealso cref="InvWT" />
    public abstract class InvWTAdapter : InvWT
    {
        /// <summary> Sets the image reconstruction resolution level. A value of 0 means
        /// reconstruction of an image with the lowest resolution (dimension)
        /// available.
        /// 
        /// Note: Image resolution level indexes may differ from tile-component
        /// resolution index. They are indeed indexed starting from the lowest
        /// number of decomposition levels of each component of each tile.
        /// 
        /// Example: For an image (1 tile) with 2 components (component 0 having
        /// 2 decomposition levels and component 1 having 3 decomposition levels),
        /// the first (tile-) component has 3 resolution levels and the second one
        /// has 4 resolution levels, whereas the image has only 3 resolution levels
        /// available.
        /// 
        /// </summary>
        /// <param name="rl">The image resolution level.
        /// 
        /// </param>
        /// <returns> The vertical coordinate of the image origin in the canvas
        /// system, on the reference grid.
        /// 
        /// </returns>
        public virtual int ImgResLevel
        {
            set
            {
                if (value < 0)
                {
                    throw new ArgumentException("Resolution level index " + "cannot be negative.");
                }
                reslvl = value;
            }

        }
        /// <summary> Returns the overall width of the current tile in pixels. This is the
        /// tile's width without accounting for any component subsampling. This is
        /// also referred as the reference grid width in the current tile.
        /// 
        /// This default implementation returns the value of the source at the
        /// current reconstruction resolution level.
        /// 
        /// </summary>
        /// <returns> The total current tile's width in pixels.
        /// 
        /// </returns>
        public virtual int TileWidth
        {
            get
            {
                // Retrieves the tile maximum resolution level index and request the
                // width from the source module.
                var tIdx = TileIdx;
                var rl = 10000;
                int mrl;
                var nc = mressrc.NumComps;
                for (var c = 0; c < nc; c++)
                {
                    mrl = mressrc.GetSynSubbandTree(tIdx, c).resLvl;
                    if (mrl < rl)
                        rl = mrl;
                }
                return mressrc.GetTileWidth(rl);
            }

        }
        /// <summary> Returns the overall height of the current tile in pixels. This
        /// is the tile's height without accounting for any component
        /// subsampling. This is also referred as the reference grid height
        /// in the current tile.
        /// 
        /// This default implementation returns the value of the source at the
        /// current reconstruction resolution level.
        /// 
        /// </summary>
        /// <returns> The total current tile's height in pixels.
        /// 
        /// </returns>
        public virtual int TileHeight
        {
            get
            {
                // Retrieves the tile maximum resolution level index and request the
                // height from the source module.
                var tIdx = TileIdx;
                var rl = 10000;
                int mrl;
                var nc = mressrc.NumComps;
                for (var c = 0; c < nc; c++)
                {
                    mrl = mressrc.GetSynSubbandTree(tIdx, c).resLvl;
                    if (mrl < rl)
                        rl = mrl;
                }
                return mressrc.GetTileHeight(rl);
            }

        }
        /// <summary>Returns the nominal width of tiles </summary>
        public virtual int NomTileWidth => mressrc.NomTileWidth;

        /// <summary>Returns the nominal height of tiles </summary>
        public virtual int NomTileHeight => mressrc.NomTileHeight;

        /// <summary> Returns the overall width of the image in pixels. This is the
        /// image's width without accounting for any component subsampling
        /// or tiling.
        /// 
        /// </summary>
        /// <returns> The total image's width in pixels.
        /// 
        /// </returns>
        public virtual int ImgWidth => mressrc.GetImgWidth(reslvl);

        /// <summary> Returns the overall height of the image in pixels. This is the
        /// image's height without accounting for any component subsampling
        /// or tiling.
        /// 
        /// </summary>
        /// <returns> The total image's height in pixels.
        /// 
        /// </returns>
        public virtual int ImgHeight => mressrc.GetImgHeight(reslvl);

        /// <summary> Returns the number of components in the image.
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

        /// <summary> Returns the horizontal coordinate of the image origin, the top-left
        /// corner, in the canvas system, on the reference grid.
        /// 
        /// This default implementation returns the value of the source at the
        /// current reconstruction resolution level.
        /// 
        /// </summary>
        /// <returns> The horizontal coordinate of the image origin in the canvas
        /// system, on the reference grid.
        /// 
        /// </returns>
        public virtual int ImgULX => mressrc.GetImgULX(reslvl);

        /// <summary> Returns the vertical coordinate of the image origin, the top-left
        /// corner, in the canvas system, on the reference grid.
        /// 
        /// This default implementation returns the value of the source at the
        /// current reconstruction resolution level.
        /// 
        /// </summary>
        /// <returns> The vertical coordinate of the image origin in the canvas
        /// system, on the reference grid.
        /// 
        /// </returns>
        public virtual int ImgULY => mressrc.GetImgULY(reslvl);

        /// <summary>Returns the horizontal tile partition offset in the reference grid </summary>
        public virtual int TilePartULX => mressrc.TilePartULX;

        /// <summary>Returns the vertical tile partition offset in the reference grid </summary>
        public virtual int TilePartULY => mressrc.TilePartULY;

        /// <summary>The decoder specifications </summary>
        protected internal DecoderSpecs decSpec;

        /// <summary>The 'MultiResImgData' source </summary>
        protected internal MultiResImgData mressrc;

        /// <summary>The resquested image resolution level for reconstruction. </summary>
        protected internal int reslvl;

        /// <summary>The maximum available image resolution level </summary>
        protected internal int maxImgRes;

        /// <summary> Instantiates the 'InvWTAdapter' object using the specified
        /// 'MultiResImgData' source. The reconstruction resolution level is set to
        /// full resolution (i.e. the maximum resolution level).
        /// 
        /// </summary>
        /// <param name="src">From where to obtain the values to return
        /// 
        /// </param>
        /// <param name="decSpec">The decoder specifications
        /// 
        /// </param>
        protected internal InvWTAdapter(MultiResImgData src, DecoderSpecs decSpec)
        {
            mressrc = src;
            this.decSpec = decSpec;
            maxImgRes = decSpec.dls.Min;
        }

        /// <summary> Returns the component subsampling factor in the horizontal
        /// direction, for the specified component. This is, approximately,
        /// the ratio of dimensions between the reference grid and the
        /// component itself, see the 'ImgData' interface desription for
        /// details.
        /// 
        /// </summary>
        /// <param name="c">The index of the component (between 0 and N-1).
        /// 
        /// </param>
        /// <returns> The horizontal subsampling factor of component 'c'.
        /// 
        /// </returns>
        /// <seealso cref="j2k.image.ImgData" />
        public virtual int GetCompSubsX(int c)
        {
            return mressrc.GetCompSubsX(c);
        }

        /// <summary> Returns the component subsampling factor in the vertical
        /// direction, for the specified component. This is, approximately,
        /// the ratio of dimensions between the reference grid and the
        /// component itself, see the 'ImgData' interface desription for
        /// details.
        /// 
        /// </summary>
        /// <param name="c">The index of the component (between 0 and N-1).
        /// 
        /// </param>
        /// <returns> The vertical subsampling factor of component 'c'.
        /// 
        /// </returns>
        /// <seealso cref="j2k.image.ImgData" />
        public virtual int GetCompSubsY(int c)
        {
            return mressrc.GetCompSubsY(c);
        }

        /// <summary> Returns the width in pixels of the specified tile-component
        /// 
        /// </summary>
        /// <param name="t">Tile index
        /// 
        /// </param>
        /// <param name="c">The index of the component, from 0 to N-1.
        /// 
        /// </param>
        /// <returns> The width in pixels of component <tt>n</tt> in tile <tt>t</tt>.
        /// 
        /// </returns>
        public virtual int GetTileCompWidth(int t, int c)
        {
            // Retrieves the tile-component maximum resolution index and gets the
            // width from the source.
            var rl = mressrc.GetSynSubbandTree(t, c).resLvl;
            return mressrc.GetTileCompWidth(t, c, rl);
        }

        /// <summary> Returns the height in pixels of the specified tile-component.
        /// 
        /// This default implementation returns the value of the source at the
        /// current reconstruction resolution level.
        /// 
        /// </summary>
        /// <param name="t">The tile index.
        /// 
        /// </param>
        /// <param name="c">The index of the component, from 0 to N-1.
        /// 
        /// </param>
        /// <returns> The height in pixels of component <tt>n</tt> in tile
        /// <tt>t</tt>. 
        /// 
        /// </returns>
        public virtual int GetTileCompHeight(int t, int c)
        {
            // Retrieves the tile-component maximum resolution index and gets the
            // height from the source.
            var rl = mressrc.GetSynSubbandTree(t, c).resLvl;
            return mressrc.GetTileCompHeight(t, c, rl);
        }

        /// <summary> Returns the width in pixels of the specified component in the overall
        /// image.
        /// 
        /// </summary>
        /// <param name="c">The index of the component, from 0 to N-1.
        /// 
        /// </param>
        /// <returns> The width in pixels of component <tt>c</tt> in the overall
        /// image.
        /// 
        /// </returns>
        public virtual int GetCompImgWidth(int c)
        {
            // Retrieves the component maximum resolution index and gets the width
            // from the source module.
            var rl = decSpec.dls.GetMinInComp(c);
            return mressrc.GetCompImgWidth(c, rl);
        }

        /// <summary> Returns the height in pixels of the specified component in the overall
        /// image.
        /// 
        /// This default implementation returns the value of the source at the
        /// current reconstruction resolution level.
        /// 
        /// </summary>
        /// <param name="c">The index of the component, from 0 to N-1.
        /// 
        /// </param>
        /// <returns> The height in pixels of component <tt>n</tt> in the overall
        /// image.
        /// 
        /// </returns>
        public virtual int GetCompImgHeight(int c)
        {
            // Retrieves the component maximum resolution index and gets the
            // height from the source module.
            var rl = decSpec.dls.GetMinInComp(c);
            return mressrc.GetCompImgHeight(c, rl);
        }

        /// <summary> Changes the current tile, given the new indices. An
        /// IllegalArgumentException is thrown if the coordinates do not correspond
        /// to a valid tile.
        /// 
        /// This default implementation calls the same method on the source.
        /// 
        /// </summary>
        /// <param name="x">The horizontal index of the tile.
        /// 
        /// </param>
        /// <param name="y">The vertical index of the new tile.
        /// 
        /// </param>
        public virtual void SetTile(int x, int y)
        {
            mressrc.SetTile(x, y);
        }

        /// <summary> Advances to the next tile, in standard scan-line order (by rows then
        /// columns). An NoNextElementException is thrown if the current tile is
        /// the last one (i.e. there is no next tile).
        /// 
        /// This default implementation calls the same method on the source.
        /// 
        /// </summary>
        public virtual void NextTile()
        {
            mressrc.NextTile();
        }

        /// <summary> Returns the indixes of the current tile. These are the horizontal and
        /// vertical indexes of the current tile.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="co">If not null this object is used to return the information. If
        /// null a new one is created and returned.
        /// 
        /// </param>
        /// <returns> The current tile's indices (vertical and horizontal indexes).
        /// 
        /// </returns>
        public virtual Coord GetTile(Coord? co)
        {
            return mressrc.GetTile(co);
        }

        /// <summary> Returns the horizontal coordinate of the upper-left corner of the
        /// specified component in the current tile.
        /// 
        /// </summary>
        /// <param name="c">The component index.
        /// 
        /// </param>
        public virtual int GetCompULX(int c)
        {
            // Find tile-component maximum resolution index and gets information
            // from the source module.
            var tIdx = TileIdx;
            var rl = mressrc.GetSynSubbandTree(tIdx, c).resLvl;
            return mressrc.GetResULX(c, rl);
        }

        /// <summary> Returns the vertical coordinate of the upper-left corner of the
        /// specified component in the current tile.
        /// 
        /// </summary>
        /// <param name="c">The component index.
        /// 
        /// </param>
        public virtual int GetCompULY(int c)
        {
            // Find tile-component maximum resolution index and gets information
            // from the source module.
            var tIdx = TileIdx;
            var rl = mressrc.GetSynSubbandTree(tIdx, c).resLvl;
            return mressrc.GetResULY(c, rl);
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

        /// <summary> Returns the specified synthesis subband tree 
        /// 
        /// </summary>
        /// <param name="t">Tile index.
        /// 
        /// </param>
        /// <param name="c">Component index.
        /// 
        /// </param>
        public virtual SubbandSyn GetSynSubbandTree(int t, int c)
        {
            return mressrc.GetSynSubbandTree(t, c);
        }
        public abstract bool IsReversible(int param1, int param2);
        public abstract int GetNomRangeBits(int compIndex);
        public abstract int GetImplementationType(int param1);
    }
}