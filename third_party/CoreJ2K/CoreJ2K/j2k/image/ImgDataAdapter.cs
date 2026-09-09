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

namespace CoreJ2K.j2k.image
{

    /// <summary> This class provides a default implementation of the methods in the
    /// 'ImgData' interface. The default implementation is just to return the value
    /// of the source, where the source is another 'ImgData' object.
    /// 
    /// This abstract class can be used to facilitate the development of other
    /// classes that implement 'ImgData'. For example a YCbCr color transform can
    /// inherit from this class and all the trivial methods do not have to be
    /// re-implemented.
    /// 
    /// If the default implementation of a method provided in this class does
    /// not suit a particular implementation of the 'ImgData' interface, the method
    /// can be overridden to implement the proper behavior.
    /// 
    /// </summary>
    /// <seealso cref="ImgData" />
    public abstract class ImgDataAdapter : ImgData
    {
        /// <summary> Returns the overall width of the current tile in pixels. This is the
        /// tile's width without accounting for any component subsampling. This is
        /// also referred as the reference grid width in the current tile.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <returns> The total current tile's width in pixels.
        /// 
        /// </returns>
        public virtual int TileWidth => imgdatasrc.TileWidth;

        /// <summary> Returns the overall height of the current tile in pixels. This is the
        /// tile's height without accounting for any component subsampling. This is
        /// also referred as the reference grid height in the current tile.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <returns> The total current tile's height in pixels.
        /// 
        /// </returns>
        public virtual int TileHeight => imgdatasrc.TileHeight;

        /// <summary>Returns the nominal tiles width </summary>
        public virtual int NomTileWidth => imgdatasrc.NomTileWidth;

        /// <summary>Returns the nominal tiles height </summary>
        public virtual int NomTileHeight => imgdatasrc.NomTileHeight;

        /// <summary> Returns the overall width of the image in pixels. This is the image's
        /// width without accounting for any component subsampling or tiling.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <returns> The total image's width in pixels.
        /// 
        /// </returns>
        public virtual int ImgWidth => imgdatasrc.ImgWidth;

        /// <summary> Returns the overall height of the image in pixels. This is the image's
        /// height without accounting for any component subsampling or tiling.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <returns> The total image's height in pixels.
        /// 
        /// </returns>
        public virtual int ImgHeight => imgdatasrc.ImgHeight;

        /// <summary> Returns the number of components in the image.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <returns> The number of components in the image.
        /// 
        /// </returns>
        public virtual int NumComps => imgdatasrc.NumComps;

        /// <summary> Returns the index of the current tile, relative to a standard scan-line
        /// order.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <returns> The current tile's index (starts at 0).
        /// 
        /// </returns>
        public virtual int TileIdx => imgdatasrc.TileIdx;

        /// <summary>Returns the horizontal tile partition offset in the reference grid </summary>
        public virtual int TilePartULX => imgdatasrc.TilePartULX;

        /// <summary>Returns the vertical tile offset in the reference grid </summary>
        public virtual int TilePartULY => imgdatasrc.TilePartULY;

        /// <summary> Returns the horizontal coordinate of the image origin, the top-left
        /// corner, in the canvas system, on the reference grid.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <returns> The horizontal coordinate of the image origin in the canvas
        /// system, on the reference grid.
        /// 
        /// </returns>
        public virtual int ImgULX => imgdatasrc.ImgULX;

        /// <summary> Returns the vertical coordinate of the image origin, the top-left
        /// corner, in the canvas system, on the reference grid.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <returns> The vertical coordinate of the image origin in the canvas
        /// system, on the reference grid.
        /// 
        /// </returns>
        public virtual int ImgULY => imgdatasrc.ImgULY;

        /// <summary>Index of the current tile </summary>
        protected internal int tIdx = 0;

        /// <summary>The ImgData source </summary>
        protected internal ImgData imgdatasrc;

        /// <summary> Instantiates the ImgDataAdapter object specifying the ImgData source.
        /// 
        /// </summary>
        /// <param name="src">From where to obtain all the ImgData values.
        /// 
        /// </param>
        protected internal ImgDataAdapter(ImgData src)
        {
            imgdatasrc = src;
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
        /// <seealso cref="ImgData" />
        public virtual int GetCompSubsX(int c)
        {
            return imgdatasrc.GetCompSubsX(c);
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
        /// <seealso cref="ImgData" />
        public virtual int GetCompSubsY(int c)
        {
            return imgdatasrc.GetCompSubsY(c);
        }

        /// <summary> Returns the width in pixels of the specified tile-component
        /// tile.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="t">Tile index
        /// 
        /// </param>
        /// <param name="c">The index of the component, from 0 to N-1.
        /// 
        /// </param>
        /// <returns> The width in pixels of component <tt>c</tt> in tile<tt>t</tt>.
        /// 
        /// </returns>
        public virtual int GetTileCompWidth(int t, int c)
        {
            return imgdatasrc.GetTileCompWidth(t, c);
        }

        /// <summary> Returns the height in pixels of the specified tile-component.
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
        /// <returns> The height in pixels of component <tt>c</tt> in tile
        /// <tt>t</tt>.
        /// 
        /// </returns>
        public virtual int GetTileCompHeight(int t, int c)
        {
            return imgdatasrc.GetTileCompHeight(t, c);
        }

        /// <summary> Returns the width in pixels of the specified component in the overall
        /// image.
        /// 
        /// This default implementation returns the value of the source.
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
            return imgdatasrc.GetCompImgWidth(c);
        }

        /// <summary> Returns the height in pixels of the specified component in the overall
        /// image.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="c">The index of the component, from 0 to N-1.
        /// 
        /// </param>
        /// <returns> The height in pixels of component <tt>c</tt> in the overall
        /// image.
        /// 
        /// </returns>
        public virtual int GetCompImgHeight(int c)
        {
            return imgdatasrc.GetCompImgHeight(c);
        }

        /// <summary> Returns the number of bits, referred to as the "range bits",
        /// corresponding to the nominal range of the image data in the specified
        /// component. If this number is <i>n</b> then for unsigned data the
        /// nominal range is between 0 and 2^b-1, and for signed data it is between
        /// -2^(b-1) and 2^(b-1)-1. In the case of transformed data which is not in
        /// the image domain (e.g., wavelet coefficients), this method returns the
        /// "range bits" of the image data that generated the coefficients.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="compIndex">The index of the component.
        /// 
        /// </param>
        /// <returns> The number of bits corresponding to the nominal range of the
        /// image data (in the image domain).
        /// 
        /// </returns>
        public virtual int GetNomRangeBits(int compIndex)
        {
            return imgdatasrc.GetNomRangeBits(compIndex);
        }

        /// <summary> Changes the current tile, given the new indexes. An
        /// IllegalArgumentException is thrown if the indexes do not correspond to
        /// a valid tile.
        /// 
        /// This default implementation just changes the tile in the source.
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
            imgdatasrc.SetTile(x, y);
            tIdx = TileIdx;
        }

        /// <summary> Advances to the next tile, in standard scan-line order (by rows then
        /// columns). An NoNextElementException is thrown if the current tile is
        /// the last one (i.e. there is no next tile).
        /// 
        /// This default implementation just advances to the next tile in the
        /// source.
        /// 
        /// </summary>
        public virtual void NextTile()
        {
            imgdatasrc.NextTile();
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
            return imgdatasrc.GetTile(co);
        }

        /// <summary> Returns the horizontal coordinate of the upper-left corner of the
        /// specified component in the current tile.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="c">The component index.
        /// 
        /// </param>
        public virtual int GetCompULX(int c)
        {
            return imgdatasrc.GetCompULX(c);
        }

        /// <summary> Returns the vertical coordinate of the upper-left corner of the
        /// specified component in the current tile.
        /// 
        /// This default implementation returns the value of the source.
        /// 
        /// </summary>
        /// <param name="c">The component index.
        /// 
        /// </param>
        public virtual int GetCompULY(int c)
        {
            return imgdatasrc.GetCompULY(c);
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
            return imgdatasrc.GetNumTiles(co);
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
            return imgdatasrc.GetNumTiles();
        }
    }
}