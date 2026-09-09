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
    using input;
    using System.Collections.Generic;

    /// <summary> This class implements the ImgData interface and allows to obtain data from
    /// different sources. Here, one source is represented by an ImgData and a
    /// component index. The typical use of this class is when the encoder needs
    /// different components (Red, Green, Blue, alpha, ...) from different input
    /// files (i.e. from different ImgReader objects).
    /// 
    /// All input ImgData must not be tiled (i.e. must have only 1 tile) and the
    /// image origin must be the canvas origin. The different inputs can have
    /// different dimensions though (this will lead to different subsampling
    /// factors for each component).
    /// 
    /// The input ImgData and component index list must be defined when
    /// constructing this class and can not be modified later.
    /// 
    /// </summary>
    /// <seealso cref="ImgData">
    /// </seealso>
    /// <seealso cref="ImgReader">
    /// 
    /// </seealso>
    public class ImgDataJoiner : BlkImgDataSrc
    {
        /// <summary> Returns the overall width of the current tile in pixels. This is the
        /// tile's width without accounting for any component subsampling.
        /// 
        /// </summary>
        /// <returns> The total current tile's width in pixels.
        /// 
        /// </returns>
        public virtual int TileWidth => w;

        /// <summary> Returns the overall height of the current tile in pixels. This is the
        /// tile's height without accounting for any component subsampling.
        /// 
        /// </summary>
        /// <returns> The total current tile's height in pixels.
        /// 
        /// </returns>
        public virtual int TileHeight => h;

        /// <summary>Returns the nominal tiles width </summary>
        public virtual int NomTileWidth => w;

        /// <summary>Returns the nominal tiles height </summary>
        public virtual int NomTileHeight => h;

        /// <summary> Returns the overall width of the image in pixels. This is the image's
        /// width without accounting for any component subsampling or tiling.
        /// 
        /// </summary>
        /// <returns> The total image's width in pixels.
        /// 
        /// </returns>
        public virtual int ImgWidth => w;

        /// <summary> Returns the overall height of the image in pixels. This is the image's
        /// height without accounting for any component subsampling or tiling.
        /// 
        /// </summary>
        /// <returns> The total image's height in pixels.
        /// 
        /// </returns>
        public virtual int ImgHeight => h;

        /// <summary> Returns the number of components in the image.
        /// 
        /// </summary>
        /// <returns> The number of components in the image.
        /// 
        /// </returns>
        public virtual int NumComps => nc;

        /// <summary> Returns the index of the current tile, relative to a standard scan-line
        /// order. This default implementations assumes no tiling, so 0 is always
        /// returned.
        /// 
        /// </summary>
        /// <returns> The current tile's index (starts at 0).
        /// 
        /// </returns>
        public virtual int TileIdx => 0;

        /// <summary>Returns the horizontal tile partition offset in the reference grid </summary>
        public virtual int TilePartULX => 0;

        /// <summary>Returns the vertical tile partition offset in the reference grid </summary>
        public virtual int TilePartULY => 0;

        /// <summary> Returns the horizontal coordinate of the image origin, the top-left
        /// corner, in the canvas system, on the reference grid.
        /// 
        /// </summary>
        /// <returns> The horizontal coordinate of the image origin in the canvas
        /// system, on the reference grid.
        /// 
        /// </returns>
        public virtual int ImgULX => 0;

        /// <summary> Returns the vertical coordinate of the image origin, the top-left
        /// corner, in the canvas system, on the reference grid.
        /// 
        /// </summary>
        /// <returns> The vertical coordinate of the image origin in the canvas
        /// system, on the reference grid.
        /// 
        /// </returns>
        public virtual int ImgULY => 0;

        /// <summary>The width of the image </summary>
        private readonly int w;

        /// <summary>The height of the image </summary>
        private readonly int h;

        /// <summary>The number of components in the image </summary>
        private readonly int nc;

        /// <summary>The list of input ImgData </summary>
        private readonly IList<ImgReader> imageData;

        /// <summary>The component index associated with each ImgData </summary>
        private readonly IList<int> compIdx;

        /// <summary>The subsampling factor along the horizontal direction, for every
        /// component 
        /// </summary>
        private readonly int[] subsX;

        /// <summary>The subsampling factor along the vertical direction, for every
        /// component 
        /// </summary>
        private readonly int[] subsY;

        /// <summary> Class constructor. Each input BlkImgDataSrc and its component index
        /// must appear in the order wanted for the output components.<br>
        /// 
        /// <u>Example:</u> Reading R,G,B components from 3 PGM files.<br>
        /// <tt>
        /// BlkImgDataSrc[] idList = <br>
        /// {<br>
        /// new ImgReaderPGM(new BEBufferedRandomAccessFile("R.pgm", "r")),<br>
        /// new ImgReaderPGM(new BEBufferedRandomAccessFile("G.pgm", "r")),<br>
        /// new ImgReaderPGM(new BEBufferedRandomAccessFile("B.pgm", "r"))<br>
        /// };<br>
        /// int[] compIdx = {0,0,0};<br>
        /// ImgDataJoiner idj = new ImgDataJoiner(idList, compIdx);
        /// </tt>
        /// 
        /// Of course, the 2 arrays must have the same length (This length is
        /// the number of output components). The image width and height are
        /// definded to be the maximum values of all the input ImgData.
        /// 
        /// </summary>
        /// <param name="imD">The list of input BlkImgDataSrc in an array.
        /// 
        /// </param>
        /// <param name="cIdx">The component index associated with each ImgData.
        /// 
        /// </param>
        public ImgDataJoiner(IList<ImgReader> imD, IList<int> cIdx)
        {
            int i;
            int maxW, maxH;

            // Initializes
            imageData = imD;
            compIdx = cIdx;
            if (imageData.Count != compIdx.Count)
                throw new System.ArgumentException("imD and cIdx must have the same length");

            nc = imD.Count;

            subsX = new int[nc];
            subsY = new int[nc];

            // Check that no source is tiled and that the image origin is at the
            // canvas origin.
            for (i = 0; i < nc; i++)
            {
                if (imD[i].GetNumTiles() != 1 || imD[i].GetCompULX(cIdx[i]) != 0 || imD[i].GetCompULY(cIdx[i]) != 0)
                {
                    throw new System.ArgumentException("All input components must, not use tiles and must have the origin at the canvas origin");
                }
            }

            // Guess component subsampling factors based on the fact that the
            // ceil() operation relates the reference grid size to the component's
            // size, through the subsampling factor.

            // Mhhh, difficult problem. For now just assume that one of the
            // subsampling factors is always 1 and that the component width is
            // always larger than its subsampling factor, which covers most of the
            // cases. We check the correctness of the solution once found to chek
            // out hypothesis.

            // Look for max width and height.
            maxW = 0;
            maxH = 0;
            for (i = 0; i < nc; i++)
            {
                if (imD[i].GetCompImgWidth(cIdx[i]) > maxW)
                    maxW = imD[i].GetCompImgWidth(cIdx[i]);
                if (imD[i].GetCompImgHeight(cIdx[i]) > maxH)
                    maxH = imD[i].GetCompImgHeight(cIdx[i]);
            }
            // Set the image width and height as the maximum ones
            w = maxW;
            h = maxH;

            // Now get the sumsampling factors and check the subsampling factors,
            // just to see if above hypothesis were correct.
            for (i = 0; i < nc; i++)
            {
                // This calculation only holds if the subsampling factor is less
                // than the component width
                subsX[i] = (maxW + imD[i].GetCompImgWidth(cIdx[i]) - 1) / imD[i].GetCompImgWidth(cIdx[i]);
                subsY[i] = (maxH + imD[i].GetCompImgHeight(cIdx[i]) - 1) / imD[i].GetCompImgHeight(cIdx[i]);
                if ((maxW + subsX[i] - 1) / subsX[i] != imD[i].GetCompImgWidth(cIdx[i]) || (maxH + subsY[i] - 1) / subsY[i] != imD[i].GetCompImgHeight(cIdx[i]))
                {
                    throw new System.InvalidOperationException("Can not compute component subsampling factors: strange subsampling.");
                }
            }
        }

        /// <summary> Returns the component subsampling factor in the horizontal direction,
        /// for the specified component. This is, approximately, the ratio of
        /// dimensions between the reference grid and the component itself, see the
        /// 'ImgData' interface desription for details.
        /// 
        /// </summary>
        /// <param name="c">The index of the component (between 0 and N-1)
        /// 
        /// </param>
        /// <returns> The horizontal subsampling factor of component 'c'
        /// 
        /// </returns>
        /// <seealso cref="ImgData">
        /// 
        /// </seealso>
        public virtual int GetCompSubsX(int c)
        {
            return subsX[c];
        }

        /// <summary> Returns the component subsampling factor in the vertical direction, for
        /// the specified component. This is, approximately, the ratio of
        /// dimensions between the reference grid and the component itself, see the
        /// 'ImgData' interface desription for details.
        /// 
        /// </summary>
        /// <param name="c">The index of the component (between 0 and N-1)
        /// 
        /// </param>
        /// <returns> The vertical subsampling factor of component 'c'
        /// 
        /// </returns>
        /// <seealso cref="ImgData">
        /// 
        /// </seealso>
        public virtual int GetCompSubsY(int c)
        {
            return subsY[c];
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
        /// <returns> The width in pixels of component <tt>c</tt> in tile<tt>t</tt>.
        /// 
        /// </returns>
        public virtual int GetTileCompWidth(int t, int c)
        {
            return imageData[c].GetTileCompWidth(t, compIdx[c]);
        }

        /// <summary> Returns the height in pixels of the specified tile-component.
        /// 
        /// </summary>
        /// <param name="t">The tile index.
        /// 
        /// </param>
        /// <param name="c">The index of the component, from 0 to N-1.
        /// 
        /// </param>
        /// <returns> The height in pixels of component <tt>c</tt> in the current
        /// tile.
        /// 
        /// </returns>
        public virtual int GetTileCompHeight(int t, int c)
        {
            return imageData[c].GetTileCompHeight(t, compIdx[c]);
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
            return imageData[c].GetCompImgWidth(compIdx[c]);
        }

        /// <summary> Returns the height in pixels of the specified component in the
        /// overall image.
        /// 
        /// </summary>
        /// <param name="n">The index of the component, from 0 to N-1.
        /// 
        /// </param>
        /// <returns> The height in pixels of component <tt>n</tt> in the overall
        /// image.
        /// 
        /// 
        /// 
        /// </returns>
        public virtual int GetCompImgHeight(int n)
        {
            return imageData[n].GetCompImgHeight(compIdx[n]);
        }

        /// <summary> Returns the number of bits, referred to as the "range bits",
        /// corresponding to the nominal range of the data in the specified
        /// component. If this number is <i>b</b> then for unsigned data the
        /// nominal range is between 0 and 2^b-1, and for signed data it is between
        /// -2^(b-1) and 2^(b-1)-1. For floating point data this value is not
        /// applicable.
        /// 
        /// </summary>
        /// <param name="compIndex">The index of the component.
        /// 
        /// </param>
        /// <returns> The number of bits corresponding to the nominal range of the
        /// data. Fro floating-point data this value is not applicable and the
        /// return value is undefined.
        /// 
        /// </returns>
        public virtual int GetNomRangeBits(int compIndex)
        {
            return imageData[compIndex].GetNomRangeBits(compIdx[compIndex]);
        }

        /// <summary> Returns the position of the fixed point in the specified
        /// component. This is the position of the least significant integral
        /// (i.e. non-fractional) bit, which is equivalent to the number of
        /// fractional bits. For instance, for fixed-point values with 2 fractional
        /// bits, 2 is returned. For floating-point data this value does not apply
        /// and 0 should be returned. Position 0 is the position of the least
        /// significant bit in the data.
        /// 
        /// </summary>
        /// <param name="compIndex">The index of the component.
        /// 
        /// </param>
        /// <returns> The position of the fixed-point, which is the same as the
        /// number of fractional bits. For floating-point data 0 is returned.
        /// 
        /// </returns>
        public virtual int GetFixedPoint(int compIndex)
        {
            return imageData[compIndex].GetFixedPoint(compIdx[compIndex]);
        }

        /// <summary> Returns, in the blk argument, a block of image data containing the
        /// specifed rectangular area, in the specified component. The data is
        /// returned, as a reference to the internal data, if any, instead of as a
        /// copy, therefore the returned data should not be modified.
        /// 
        /// The rectangular area to return is specified by the 'ulx', 'uly', 'w'
        /// and 'h' members of the 'blk' argument, relative to the current
        /// tile. These members are not modified by this method. The 'offset' and
        /// 'scanw' of the returned data can be arbitrary. See the 'DataBlk' class.
        /// 
        /// This method, in general, is more efficient than the 'getCompData()'
        /// method since it may not copy the data. However if the array of returned
        /// data is to be modified by the caller then the other method is probably
        /// preferable.
        /// 
        /// If the data array in <tt>blk</tt> is <tt>null</tt>, then a new one
        /// is created if necessary. The implementation of this interface may
        /// choose to return the same array or a new one, depending on what is more
        /// efficient. Therefore, the data array in <tt>blk</tt> prior to the
        /// method call should not be considered to contain the returned data, a
        /// new array may have been created. Instead, get the array from
        /// <tt>blk</tt> after the method has returned.
        /// 
        /// The returned data may have its 'progressive' attribute set. In this
        /// case the returned data is only an approximation of the "final" data.
        /// 
        /// </summary>
        /// <param name="blk">Its coordinates and dimensions specify the area to return,
        /// relative to the current tile. Some fields in this object are modified
        /// to return the data.
        /// 
        /// </param>
        /// <param name="compIndex">The index of the component from which to get the data.
        /// 
        /// </param>
        /// <returns> The requested DataBlk
        /// 
        /// </returns>
        /// <seealso cref="GetCompData">
        /// 
        /// </seealso>
        public virtual DataBlk GetInternCompData(DataBlk blk, int compIndex)
        {
            return imageData[compIndex].GetInternCompData(blk, compIdx[compIndex]);
        }

        /// <summary> Returns, in the blk argument, a block of image data containing the
        /// specifed rectangular area, in the specified component. The data is
        /// returned, as a copy of the internal data, therefore the returned data
        /// can be modified "in place".
        /// 
        /// The rectangular area to return is specified by the 'ulx', 'uly', 'w'
        /// and 'h' members of the 'blk' argument, relative to the current
        /// tile. These members are not modified by this method. The 'offset' of
        /// the returned data is 0, and the 'scanw' is the same as the block's
        /// width. See the 'DataBlk' class.
        /// 
        /// This method, in general, is less efficient than the
        /// 'getInternCompData()' method since, in general, it copies the
        /// data. However if the array of returned data is to be modified by the
        /// caller then this method is preferable.
        /// 
        /// If the data array in 'blk' is 'null', then a new one is created. If
        /// the data array is not 'null' then it is reused, and it must be large
        /// enough to contain the block's data. Otherwise an 'ArrayStoreException'
        /// or an 'IndexOutOfBoundsException' is thrown by the Java system.
        /// 
        /// The returned data may have its 'progressive' attribute set. In this
        /// case the returned data is only an approximation of the "final" data.
        /// 
        /// </summary>
        /// <param name="blk">Its coordinates and dimensions specify the area to return,
        /// relative to the current tile. If it contains a non-null data array,
        /// then it must be large enough. If it contains a null data array a new
        /// one is created. Some fields in this object are modified to return the
        /// data.
        /// 
        /// </param>
        /// <param name="c">The index of the component from which to get the data.
        /// 
        /// </param>
        /// <returns> The requested DataBlk
        /// 
        /// </returns>
        /// <seealso cref="GetInternCompData">
        /// 
        /// </seealso>
        public virtual DataBlk GetCompData(DataBlk blk, int c)
        {
            return imageData[c].GetCompData(blk, compIdx[c]);
        }

        /// <summary> Closes the underlying file or network connection from where the
        /// image data is being read.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error occurs.
        /// </exception>
        public void Close()
        {
            foreach (var reader in imageData)
            {
                reader.Close();
            }
        }

        /// <summary> Returns true if the data read was originally signed in the specified
        /// component, false if not.
        /// 
        /// </summary>
        /// <param name="compIndex">The index of the component, from 0 to C-1.
        /// 
        /// </param>
        /// <returns> true if the data was originally signed, false if not.
        /// 
        /// </returns>
        public bool IsOrigSigned(int compIndex)
        {
            return false;
        }

        /// <summary> Changes the current tile, given the new coordinates. An
        /// IllegalArgumentException is thrown if the coordinates do not correspond
        /// to a valid tile.
        /// 
        /// </summary>
        /// <param name="x">The horizontal coordinate of the tile.
        /// 
        /// </param>
        /// <param name="y">The vertical coordinate of the new tile.
        /// 
        /// </param>
        public virtual void SetTile(int x, int y)
        {
            if (x != 0 || y != 0)
            {
                throw new System.ArgumentException();
            }
        }

        /// <summary> Advances to the next tile, in standard scan-line order (by rows then
        /// columns). A NoNextElementException is thrown if the current tile is the
        /// last one (i.e. there is no next tile). This default implementation
        /// assumes no tiling, so NoNextElementException() is always thrown.
        /// 
        /// </summary>
        public virtual void NextTile()
        {
            throw new NoNextElementException();
        }

        /// <summary> Returns the coordinates of the current tile. This default
        /// implementation assumes no-tiling, so (0,0) is returned.
        /// 
        /// </summary>
        /// <param name="co">If not null this object is used to return the information. If
        /// null a new one is created and returned.
        /// 
        /// </param>
        /// <returns> The current tile's coordinates.
        /// 
        /// </returns>
        public virtual Coord GetTile(Coord? co)
        {
            if (co != null)
            {
                co.x = 0;
                co.y = 0;
                return co;
            }
            else
            {
                return new Coord(0, 0);
            }
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
            return 0;
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
            return 0;
        }

        /// <summary> Returns the number of tiles in the horizontal and vertical
        /// directions. This default implementation assumes no tiling, so (1,1) is
        /// always returned.
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
            if (co != null)
            {
                co.x = 1;
                co.y = 1;
                return co;
            }
            else
            {
                return new Coord(1, 1);
            }
        }

        /// <summary> Returns the total number of tiles in the image. This default
        /// implementation assumes no tiling, so 1 is always returned.
        /// 
        /// </summary>
        /// <returns> The total number of tiles in the image.
        /// 
        /// </returns>
        public virtual int GetNumTiles()
        {
            return 1;
        }

        /// <summary> Returns a string of information about the object, more than 1 line
        /// long. The information string includes information from the several
        /// input ImgData (their toString() method are called one after the other).
        /// 
        /// </summary>
        /// <returns> A string of information about the object.
        /// 
        /// </returns>
        public override string ToString()
        {
            var result = $"ImgDataJoiner: WxH = {w}x{h}";
            for (var i = 0; i < nc; i++)
            {
                result += ($"\n- Component {i} {imageData[i]}");
            }
            return result;
        }
    }
}
