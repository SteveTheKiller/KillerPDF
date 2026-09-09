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
using CoreJ2K.j2k.entropy.decoder;
using CoreJ2K.j2k.image;
using CoreJ2K.j2k.io;
using CoreJ2K.j2k.quantization.dequantizer;
using CoreJ2K.j2k.util;
using CoreJ2K.j2k.wavelet;
using CoreJ2K.j2k.wavelet.synthesis;
using System;

namespace CoreJ2K.j2k.codestream.reader
{

    /// <summary> This is the generic interface for bit stream reader agents. A bit stream
    /// reader agent is an entity that allows reading from a bit stream and
    /// requesting compressed code-blocks. It can be a simple file reader, or a
    /// network connection, or anything else.
    /// 
    /// The bit stream reader agent allows to make request for compressed block
    /// data in any order. The amount of data returned would normally depend on the
    /// data available at the time of the request, be it from a file or from a
    /// network connection.
    /// 
    /// The bit stream reader agent has the notion of a current tile, and
    /// coordinates are relative to the current tile, where applicable.
    /// 
    /// Resolution level 0 is the lowest resolution level, i.e. the LL subband
    /// alone.
    /// 
    /// </summary>
    public abstract class BitstreamReaderAgent : CodedCBlkDataSrcDec
    {
        /// <summary> Returns the horizontal code-block partition origin. Allowable values
        /// are 0 and 1, nothing else.
        /// 
        /// </summary>
        public virtual int CbULX => hd.CbULX;

        /// <summary> Returns the vertical code-block partition origin. Allowable values are
        /// 0 and 1, nothing else.
        /// 
        /// </summary>
        public virtual int CbULY => hd.CbULY;

        /// <summary> Returns the number of components in the image.
        /// 
        /// </summary>
        /// <returns> The number of components in the image.
        /// 
        /// </returns>
        public virtual int NumComps => nc;


        /// <summary> Returns the index of the current tile, relative to a standard scan-line
        /// order.
        /// 
        /// </summary>
        /// <returns> The current tile's index (starts at 0).
        /// 
        /// </returns>
        /// <exception cref="InvalidOperationException">If tile coordinates are invalid</exception>
        public virtual int TileIdx
        {
            get
            {
                // Validate tile coordinates before calculating index
                if (ctX < 0 || ctX >= ntX)
                {
                    throw new InvalidOperationException(
                        $"Invalid tile X coordinate: {ctX} (must be in range [0, {ntX}))");
                }
                if (ctY < 0 || ctY >= ntY)
                {
                    throw new InvalidOperationException(
                        $"Invalid tile Y coordinate: {ctY} (must be in range [0, {ntY}))");
                }
                
                int tileIdx = ctY * ntX + ctX;
                
                // Additional sanity check
                if (tileIdx < 0 || tileIdx >= ntX * ntY)
                {
                    throw new InvalidOperationException(
                        $"Calculated tile index {tileIdx} out of range [0, {ntX * ntY})");
                }
                
                return tileIdx;
            }
        }

        /// <summary> Returns the parameters that are used in this class and implementing
        /// classes. It returns a 2D String array. Each of the 1D arrays is for a
        /// different option, and they have 3 elements. The first element is the
        /// option name, the second one is the synopsis and the third one is a long
        /// description of what the parameter is. The synopsis or description may
        /// be 'null', in which case it is assumed that there is no synopsis or
        /// description of the option, respectively. Null may be returned if no
        /// options are supported.
        /// 
        /// </summary>
        /// <returns> the options name, their synopsis and their explanation, or null
        /// if no options are supported.
        /// 
        /// </returns>
        public static string[][] ParameterInfo => pinfo;

        /// <summary> Returns the image resolution level to reconstruct from the
        /// codestream. This value cannot be computed before every main and tile
        /// headers are read.
        /// 
        /// </summary>
        /// <returns> The image  resolution level
        /// 
        /// </returns>
        public virtual int ImgRes => targetRes;

        /// <summary> Return the target decoding rate in bits per pixel.
        /// 
        /// </summary>
        /// <returns> Target decoding rate in bpp.
        /// 
        /// </returns>
        public virtual float TargetRate => trate;

        /// <summary> Return the actual decoding rate in bits per pixel.
        /// 
        /// </summary>
        /// <returns> Actual decoding rate in bpp.
        /// 
        /// </returns>
        public virtual float ActualRate
        {
            get
            {
                arate = anbytes * 8f / hd.MaxCompImgWidth / hd.MaxCompImgHeight;
                return arate;
            }

        }
        /// <summary> Return the target number of read bytes.
        /// 
        /// </summary>
        /// <returns> Target decoding rate in bytes.
        /// 
        /// </returns>
        public virtual int TargetNbytes => tnbytes;

        /// <summary> Return the actual number of read bytes.
        /// 
        /// </summary>
        /// <returns> Actual decoding rate in bytes.
        /// 
        /// </returns>
        public virtual int ActualNbytes => anbytes;

        /// <summary>Returns the horizontal offset of tile partition </summary>
        public virtual int TilePartULX => hd.GetTilingOrigin(null).x;

        /// <summary>Returns the vertical offset of tile partition </summary>
        public virtual int TilePartULY => hd.GetTilingOrigin(null).y;

        /// <summary>Returns the nominal tile width </summary>
        public virtual int NomTileWidth => hd.NomTileWidth;

        /// <summary>Returns the nominal tile height </summary>
        public virtual int NomTileHeight => hd.NomTileHeight;

        /// <summary>The decoder specifications </summary>
        protected internal DecoderSpecs decSpec;

        /// <summary> Whether or not the components in the current tile uses a derived
        /// quantization step size (only relevant in non reversible quantization
        /// mode). This field is actualized by the SetTile method in
        /// FileBitstreamReaderAgent.
        /// 
        /// </summary>
        /// <seealso cref="FileBitstreamReaderAgent.initSubbandsFields" />
        protected internal bool[] derived = null;

        /// <summary> Number of guard bits off all component in the current tile. This field
        /// is actualized by the SetTile method in FileBitstreamReaderAgent.
        /// 
        /// </summary>
        /// <seealso cref="FileBitstreamReaderAgent.initSubbandsFields" />
        protected internal int[] gb = null;

        /// <summary> Dequantization parameters of all subbands and all components in the
        /// current tile. The value is actualized by the SetTile method in
        /// FileBitstreamReaderAgent.
        /// 
        /// </summary>
        /// <seealso cref="FileBitstreamReaderAgent.initSubbandsFields" />
        protected internal StdDequantizerParams[] dequantParams = null;

        /// <summary>The prefix for bit stream reader options: 'B' </summary>
        public const char OPT_PREFIX = 'B';

        /// <summary>The list of parameters that is accepted by the bit stream
        /// readers. They start with 'B'. 
        /// </summary>
        private static readonly string[][] pinfo = null;

        /// <summary> The maximum number of decompostion levels for each component of the
        /// current tile. It means that component c has mdl[c]+1 resolution levels
        /// (indexed from 0 to mdl[c])
        /// 
        /// </summary>
        protected internal int[] mdl;

        /// <summary>The number of components </summary>
        protected internal int nc;

        /// <summary>Image resolution level to generate </summary>
        protected internal int targetRes;

        /// <summary> The subband trees for each component in the current tile. Each element
        /// in the array is the root element of the subband tree for a
        /// component. The number of magnitude bits in each subband (magBits member
        /// variable) is not initialized.
        /// 
        /// </summary>
        protected internal SubbandSyn[] subbTrees;

        /// <summary>The image width on the hi-res reference grid </summary>
        protected internal int imgW;

        /// <summary>The image width on the hi-res reference grid </summary>
        protected internal int imgH;

        /// <summary>The horizontal coordinate of the image origin in the canvas system, on
        /// the reference grid. 
        /// </summary>
        protected internal int ax;

        /// <summary>The vertical coordinate of the image origin in the canvas system, on
        /// the reference grid. 
        /// </summary>
        protected internal int ay;

        /// <summary>The horizontal coordinate of the tiling origin in the canvas system, on
        /// the reference grid. 
        /// </summary>
        protected internal int px;

        /// <summary>The vertical coordinate of the tiling origin in the canvas system, on
        /// the reference grid. 
        /// </summary>
        protected internal int py;

        /// <summary>The horizontal offsets of the upper-left corner of the current tile
        /// (not active tile) with respect to the canvas origin, in the component
        /// hi-res grid, for each component. 
        /// </summary>
        protected internal int[] offX;

        /// <summary>The vertical offsets of the upper-left corner of the current tile (not
        /// active tile) with respect to the canvas origin, in the component hi-res
        /// grid, for each component. 
        /// </summary>
        protected internal int[] offY;

        /// <summary>The horizontal coordinates of the upper-left corner of the active
        /// tile, with respect to the canvas origin, in the component hi-res grid,
        /// for each component. 
        /// </summary>
        protected internal int[] culx;

        /// <summary>The vertical coordinates of the upper-left corner of the active tile,
        /// with respect to the canvas origin, in the component hi-res grid, for
        /// each component. 
        /// </summary>
        protected internal int[] culy;

        /// <summary>The nominal tile width, in the hi-res reference grid </summary>
        protected internal int ntW;

        /// <summary>The nominal tile height, in the hi-res reference grid </summary>
        protected internal int ntH;

        /// <summary>The number of tile in the horizontal direction </summary>
        protected internal int ntX;

        /// <summary>The number of tiles in the vertical direction </summary>
        protected internal int ntY;

        /// <summary>The total number of tiles </summary>
        protected internal int nt;

        /// <summary>The current tile horizontal index </summary>
        protected internal int ctX;

        /// <summary>The current tile vertical index </summary>
        protected internal int ctY;

        /// <summary>The decoded bit stream header </summary>
        protected internal HeaderDecoder hd;

        /// <summary>Number of bytes targeted to be read </summary>
        protected internal int tnbytes;

        /// <summary>Actual number of read bytes </summary>
        protected internal int anbytes;

        /// <summary>Target decoding rate in bpp </summary>
        protected internal float trate;

        /// <summary>Actual decoding rate in bpp </summary>
        protected internal float arate;

        /// <summary> Initializes members of this class. This constructor takes a
        /// HeaderDecoder object. This object must be initialized by the
        /// constructor of the implementing class from the header of the bit
        /// stream.
        /// 
        /// </summary>
        /// <param name="hd">The decoded header of the bit stream from where to initialize
        /// the values.
        /// 
        /// </param>
        /// <param name="decSpec">The decoder specifications
        /// 
        /// </param>
        protected internal BitstreamReaderAgent(HeaderDecoder hd, DecoderSpecs decSpec)
        {
            Coord co;
            //int i, j, max;

            this.decSpec = decSpec;
            this.hd = hd;

            // Number of components
            nc = hd.NumComps;
            offX = new int[nc];
            offY = new int[nc];
            culx = new int[nc];
            culy = new int[nc];

            // Image size and origin
            imgW = hd.ImgWidth;
            imgH = hd.ImgHeight;
            ax = hd.ImgULX;
            ay = hd.ImgULY;

            // Tiles
            co = hd.GetTilingOrigin(null);
            px = co.x;
            py = co.y;
            ntW = hd.NomTileWidth;
            ntH = hd.NomTileHeight;
            ntX = (ax + imgW - px + ntW - 1) / ntW;
            ntY = (ay + imgH - py + ntH - 1) / ntH;
            nt = ntX * ntY;
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
        /// <seealso cref="j2k.image.ImgData" />
        public int GetCompSubsX(int c)
        {
            return hd.GetCompSubsX(c);
        }

        /// <summary> Returns the component subsampling factor in the vertical direction, for
        /// the specified component. This is, approximately, the ratio of
        /// dimensions between the reference grid and the component itself, see the
        /// 'ImgData' interface desription for details.
        /// 
        /// </summary>
        /// <param name="c">The index of the component (between 0 and C-1)
        /// 
        /// </param>
        /// <returns> The vertical subsampling factor of component 'c'
        /// 
        /// </returns>
        /// <seealso cref="j2k.image.ImgData" />
        public virtual int GetCompSubsY(int c)
        {
            return hd.GetCompSubsY(c);
        }

        /// <summary> Returns the overall width of the current tile in pixels for the given
        /// (tile) resolution level. This is the tile's width without accounting
        /// for any component subsampling.
        /// 
        /// Note: Tile resolution level indexes may be different from
        /// tile-component resolution index. They are indeed indexed starting from
        /// the lowest number of decomposition levels of each component of the
        /// tile.
        /// 
        /// For an image (1 tile) with 2 components (component 0 having 2
        /// decomposition levels and component 1 having 3 decomposition levels),
        /// the first (tile-)component has 3 resolution levels and the second one
        /// has 4 resolution levels, whereas the tile has only 3 resolution levels
        /// available.
        /// 
        /// </summary>
        /// <param name="rl">The (tile) resolution level.
        /// 
        /// </param>
        /// <returns> The current tile's width in pixels.
        /// 
        /// </returns>
        public virtual int GetTileWidth(int rl)
        {
            // The minumum number of decomposition levels between all the
            // components
            var mindl = decSpec.dls.GetMinInTile(TileIdx);
            if (rl > mindl)
            {
                throw new ArgumentException(
                    $"Requested resolution level is not available for, at least, one component in tile: {ctX}x{ctY}");
            }
            int ctulx, ntulx;
            var dl = mindl - rl; // Number of decomposition to obtain this
                                 // resolution

            // Calculate starting X of current tile at hi-res
            ctulx = (ctX == 0) ? ax : px + ctX * ntW;
            // Calculate starting X of next tile X-wise at hi-res
            ntulx = (ctX < ntX - 1) ? px + (ctX + 1) * ntW : ax + imgW;

            // The difference at the rl resolution level is the width
            return (ntulx + (1 << dl) - 1) / (1 << dl) - (ctulx + (1 << dl) - 1) / (1 << dl);
        }

        /// <summary> Returns the overall height of the current tile in pixels, for the given
        /// resolution level. This is the tile's height without accounting for any
        /// component subsampling.
        /// 
        /// Note: Tile resolution level indexes may be different from
        /// tile-component resolution index. They are indeed indexed starting from
        /// the lowest number of decomposition levels of each component of the
        /// tile.
        /// 
        /// For an image (1 tile) with 2 components (component 0 having 2
        /// decomposition levels and component 1 having 3 decomposition levels),
        /// the first (tile-)component has 3 resolution levels and the second one
        /// has 4 resolution levels, whereas the tile has only 3 resolution levels
        /// available.
        /// 
        /// </summary>
        /// <param name="rl">The (tile) resolution level.
        /// 
        /// </param>
        /// <returns> The total current tile's height in pixels.
        /// 
        /// </returns>
        public virtual int GetTileHeight(int rl)
        {
            // The minumum number of decomposition levels between all the
            // components
            var mindl = decSpec.dls.GetMinInTile(TileIdx);
            if (rl > mindl)
            {
                throw new ArgumentException(
                    $"Requested resolution level is not available for, at least, one component in tile: {ctX}x{ctY}");
            }

            int ctuly, ntuly;
            var dl = mindl - rl; // Number of decomposition to obtain this
                                 // resolution

            // Calculate starting Y of current tile at hi-res
            ctuly = (ctY == 0) ? ay : py + ctY * ntH;
            // Calculate starting Y of next tile Y-wise at hi-res
            ntuly = (ctY < ntY - 1) ? py + (ctY + 1) * ntH : ay + imgH;
            // The difference at the rl level is the height
            return (ntuly + (1 << dl) - 1) / (1 << dl) - (ctuly + (1 << dl) - 1) / (1 << dl);
        }

        /// <summary> Returns the overall width of the image in pixels, for the given (image)
        /// resolution level. This is the image's width without accounting for any
        /// component subsampling or tiling.
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
        /// <returns> The total image's width in pixels.
        /// 
        /// </returns>
        public virtual int GetImgWidth(int rl)
        {
            // The minimum number of decomposition levels of each
            // tile-component
            var mindl = decSpec.dls.Min;
            if (rl > mindl)
            {
                throw new ArgumentException("Requested resolution level" + " is not available for, at " + "least, one tile-component");
            }
            // Retrieve number of decomposition levels corresponding to
            // this resolution level
            var dl = mindl - rl;
            return (ax + imgW + (1 << dl) - 1) / (1 << dl) - (ax + (1 << dl) - 1) / (1 << dl);
        }

        /// <summary> Returns the overall height of the image in pixels, for the given
        /// resolution level. This is the image's height without accounting for any
        /// component subsampling or tiling.
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
        /// <param name="rl">The image resolution level, from 0 to L.
        /// 
        /// </param>
        /// <returns> The total image's height in pixels.
        /// 
        /// </returns>
        public virtual int GetImgHeight(int rl)
        {
            var mindl = decSpec.dls.Min;
            if (rl > mindl)
            {
                throw new ArgumentException("Requested resolution level" + " is not available for, at " + "least, one tile-component");
            }
            // Retrieve number of decomposition levels corresponding to this
            // resolution level
            var dl = mindl - rl;
            return (ay + imgH + (1 << dl) - 1) / (1 << dl) - (ay + (1 << dl) - 1) / (1 << dl);
        }

        /// <summary> Returns the horizontal coordinate of the image origin, the top-left
        /// corner, in the canvas system, on the reference grid at the specified
        /// resolution level.
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
        /// <param name="rl">The resolution level, from 0 to L.
        /// 
        /// </param>
        /// <returns> The horizontal coordinate of the image origin in the canvas
        /// system, on the reference grid.
        /// 
        /// </returns>
        public virtual int GetImgULX(int rl)
        {
            var mindl = decSpec.dls.Min;
            if (rl > mindl)
            {
                throw new ArgumentException("Requested resolution level" + " is not available for, at " + "least, one tile-component");
            }
            // Retrieve number of decomposition levels corresponding to this
            // resolution level
            var dl = mindl - rl;
            return (ax + (1 << dl) - 1) / (1 << dl);
        }

        /// <summary> Returns the vertical coordinate of the image origin, the top-left
        /// corner, in the canvas system, on the reference grid at the specified
        /// resolution level.
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
        /// <param name="rl">The resolution level, from 0 to L.
        /// 
        /// </param>
        /// <returns> The vertical coordinate of the image origin in the canvas
        /// system, on the reference grid.
        /// 
        /// </returns>
        public virtual int GetImgULY(int rl)
        {
            var mindl = decSpec.dls.Min;
            if (rl > mindl)
            {
                throw new ArgumentException("Requested resolution level" + " is not available for, at " + "least, one tile-component");
            }
            // Retrieve number of decomposition levels corresponding to this
            // resolution level
            var dl = mindl - rl;
            return (ay + (1 << dl) - 1) / (1 << dl);
        }

        /// <summary> Returns the width in pixels of the specified tile-component for the
        /// given (tile-component) resolution level.
        /// 
        /// </summary>
        /// <param name="t">The tile index
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
        public int GetTileCompWidth(int t, int c, int rl)
        {
            var tIdx = TileIdx;
            if (t != tIdx)
            {
                throw new InvalidOperationException("Asking the tile-component width of a tile " + "different  from the current one.");
            }
            int ntulx;
            var dl = mdl[c] - rl;
            // Calculate starting X of next tile X-wise at reference grid hi-res
            ntulx = (ctX < ntX - 1) ? px + (ctX + 1) * ntW : ax + imgW;
            // Convert reference grid hi-res to component grid hi-res
            ntulx = (ntulx + hd.GetCompSubsX(c) - 1) / hd.GetCompSubsX(c);
            // Starting X of current tile at component grid hi-res is culx[c]
            // The difference at the rl level is the width
            return (ntulx + (1 << dl) - 1) / (1 << dl) - (culx[c] + (1 << dl) - 1) / (1 << dl);
        }

        /// <summary> Returns the height in pixels of the specified tile-component for the
        /// given (tile-component) resolution level.
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
        /// <returns> The height in pixels of component <tt>c</tt> in the current
        /// tile.
        /// 
        /// </returns>
        public int GetTileCompHeight(int t, int c, int rl)
        {
            var tIdx = TileIdx;
            if (t != tIdx)
            {
                throw new InvalidOperationException("Asking the tile-component width of a tile " + "different  from the current one.");
            }
            int ntuly;
            var dl = mdl[c] - rl; // Revert level indexation (0 is hi-res)
                                  // Calculate starting Y of next tile Y-wise at reference grid hi-res
            ntuly = (ctY < ntY - 1) ? py + (ctY + 1) * ntH : ay + imgH;
            // Convert reference grid hi-res to component grid hi-res
            ntuly = (ntuly + hd.GetCompSubsY(c) - 1) / hd.GetCompSubsY(c);
            // Starting Y of current tile at component grid hi-res is culy[c]
            // The difference at the rl level is the height
            return (ntuly + (1 << dl) - 1) / (1 << dl) - (culy[c] + (1 << dl) - 1) / (1 << dl);
        }

        /// <summary> Returns the width in pixels of the specified component in the overall
        /// image, for the given (component) resolution level.
        /// 
        /// Note: Component resolution level indexes may differ from
        /// tile-component resolution index. They are indeed indexed starting from
        /// the lowest number of decomposition levels of same component of each
        /// tile.
        /// 
        /// Example: For an image (2 tiles) with 1 component (tile 0 having 2
        /// decomposition levels and tile 1 having 3 decomposition levels), the
        /// first tile(-component) has 3 resolution levels and the second one has 4
        /// resolution levels, whereas the component has only 3 resolution levels
        /// available.
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
        public int GetCompImgWidth(int c, int rl)
        {
            int sx, ex;
            var dl = decSpec.dls.GetMinInComp(c) - rl;
            // indexation (0 is hi-res)
            // Calculate image starting x at component hi-res grid
            sx = (ax + hd.GetCompSubsX(c) - 1) / hd.GetCompSubsX(c);
            // Calculate image ending (excluding) x at component hi-res grid
            ex = (ax + imgW + hd.GetCompSubsX(c) - 1) / hd.GetCompSubsX(c);
            // The difference at the rl level is the width
            return (ex + (1 << dl) - 1) / (1 << dl) - (sx + (1 << dl) - 1) / (1 << dl);
        }

        /// <summary> Returns the height in pixels of the specified component in the overall
        /// image, for the given (component) resolution level.
        /// 
        /// Note: Component resolution level indexes may differ from
        /// tile-component resolution index. They are indeed indexed starting from
        /// the lowest number of decomposition levels of same component of each
        /// tile.
        /// 
        /// Example: For an image (2 tiles) with 1 component (tile 0 having 2
        /// decomposition levels and tile 1 having 3 decomposition levels), the
        /// first tile(-component) has 3 resolution levels and the second one has 4
        /// resolution levels, whereas the component has only 3 resolution levels
        /// available.
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
        public int GetCompImgHeight(int c, int rl)
        {
            int sy, ey;
            var dl = decSpec.dls.GetMinInComp(c) - rl;
            // indexation (0 is hi-res)
            // Calculate image starting x at component hi-res grid
            sy = (ay + hd.GetCompSubsY(c) - 1) / hd.GetCompSubsY(c);
            // Calculate image ending (excluding) x at component hi-res grid
            ey = (ay + imgH + hd.GetCompSubsY(c) - 1) / hd.GetCompSubsY(c);
            // The difference at the rl level is the width
            return (ey + (1 << dl) - 1) / (1 << dl) - (sy + (1 << dl) - 1) / (1 << dl);
        }

        /// <summary> Changes the current tile, given the new indexes. An
        /// IllegalArgumentException is thrown if the indexes do not correspond to
        /// a valid tile.
        /// 
        /// </summary>
        /// <param name="x">The horizontal indexes the tile.
        /// 
        /// </param>
        /// <param name="y">The vertical indexes of the new tile.
        /// 
        /// </param>
        public abstract void SetTile(int x, int y);

        /// <summary> Advances to the next tile, in standard scan-line order (by rows then
        /// columns). An NoNextElementException is thrown if the current tile is
        /// the last one (i.e. there is no next tile).
        /// 
        /// </summary>
        public abstract void NextTile();

        /// <summary> Returns the indexes of the current tile. These are the horizontal and
        /// vertical indexes of the current tile.
        /// 
        /// </summary>
        /// <param name="co">If not null this object is used to return the information. If
        /// null a new one is created and returned.
        /// 
        /// </param>
        /// <returns> The current tile's indexes (vertical and horizontal indexes).
        /// 
        /// </returns>
        public Coord GetTile(Coord? co)
        {
            if (co != null)
            {
                co.x = ctX;
                co.y = ctY;
                return co;
            }
            else
            {
                return new Coord(ctX, ctY);
            }
        }

        /// <summary> Returns the horizontal coordinate of the upper-left corner of the
        /// specified resolution in the given component of the current tile.
        /// 
        /// </summary>
        /// <param name="c">The component index.
        /// 
        /// </param>
        /// <param name="rl">The resolution level index.
        /// 
        /// </param>
        public int GetResULX(int c, int rl)
        {
            var dl = mdl[c] - rl;
            if (dl < 0)
            {
                throw new ArgumentException(
                    $"Requested resolution level is not available for, at least, one component in tile: {ctX}x{ctY}");
            }
            var tx0 = Math.Max(px + ctX * ntW, ax);
            var tcx0 = (int)Math.Ceiling(tx0 / (double)GetCompSubsX(c));
            return (int)Math.Ceiling(tcx0 / (double)(1 << dl));
        }

        /// <summary> Returns the vertical coordinate of the upper-left corner of the
        /// specified component in the given component of the current tile.
        /// 
        /// </summary>
        /// <param name="c">The component index.
        /// 
        /// </param>
        /// <param name="rl">The resolution level index.
        /// 
        /// </param>
        public int GetResULY(int c, int rl)
        {
            var dl = mdl[c] - rl;
            if (dl < 0)
            {
                throw new ArgumentException(
                    $"Requested resolution level is not available for, at least, one component in tile: {ctX}x{ctY}");
            }
            var ty0 = Math.Max(py + ctY * ntH, ay);
            var tcy0 = (int)Math.Ceiling(ty0 / (double)GetCompSubsY(c));
            return (int)Math.Ceiling(tcy0 / (double)(1 << dl));
        }

        /// <summary> Returns the number of tiles in the horizontal and vertical directions.
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
        public Coord GetNumTiles(Coord? co)
        {
            if (co != null)
            {
                co.x = ntX;
                co.y = ntY;
                return co;
            }
            else
            {
                return new Coord(ntX, ntY);
            }
        }

        /// <summary> Returns the total number of tiles in the image.
        /// 
        /// </summary>
        /// <returns> The total number of tiles in the image.
        /// 
        /// </returns>
        public int GetNumTiles()
        {
            return ntX * ntY;
        }

        /// <summary> Returns the subband tree, for the specified tile-component. This method
        /// returns the root element of the subband tree structure, see Subband and
        /// SubbandSyn. The tree comprises all the available resolution levels.
        /// 
        /// Note: this method is not able to return subband tree for a tile
        /// different than the current one.
        /// 
        /// The number of magnitude bits ('magBits' member variable) for each
        /// subband is not initialized.
        /// 
        /// </summary>
        /// <param name="t">The tile index
        /// 
        /// </param>
        /// <param name="c">The index of the component, from 0 to C-1.
        /// 
        /// </param>
        /// <returns> The root of the tree structure.
        /// 
        /// </returns>
        public SubbandSyn GetSynSubbandTree(int t, int c)
        {
            if (t != TileIdx)
            {
                throw new ArgumentException("Can not request subband" + " tree of a different tile" + " than the current one");
            }
            if (c < 0 || c >= nc)
            {
                throw new ArgumentException("Component index out of range");
            }
            return subbTrees[c];
        }

        /// <summary> Creates a bit stream reader of the correct type that works on the
        /// provided RandomAccessIO, with the special parameters from the parameter
        /// list.
        /// 
        /// </summary>
        /// <param name="in">The RandomAccessIO source from which to read the bit stream.
        /// 
        /// </param>
        /// <param name="hd">Header of the codestream.
        /// 
        /// </param>
        /// <param name="pl">The parameter list containing parameters applicable to the
        /// bit stream read (other parameters may also be present).
        /// 
        /// </param>
        /// <param name="decSpec">The decoder specifications
        /// 
        /// </param>
        /// <param name="cdstrInfo">Whether or not to print information found in
        /// codestream. 
        /// 
        /// </param>
        /// <param name="hi">Reference to the HeaderInfo instance.
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error occurs while reading initial
        /// data from the bit stream.
        /// </exception>
        /// <exception cref="IllegalArgumentException">If an unrecognised bit stream
        /// reader option is present.
        /// 
        /// </exception>
        public static BitstreamReaderAgent createInstance(RandomAccessIO inStream, HeaderDecoder hd, ParameterList pl, DecoderSpecs decSpec, bool cdstrInfo, HeaderInfo hi)
        {

            // Check parameters
            pl.checkList(OPT_PREFIX, ParameterList.toNameArray(ParameterInfo));

            return new FileBitstreamReaderAgent(hd, inStream, decSpec, pl, cdstrInfo, hi);
        }

        /// <summary> Returns the precinct partition width for the specified tile-component
        /// and (tile-component) resolution level.
        /// 
        /// </summary>
        /// <param name="t">the tile index
        /// 
        /// </param>
        /// <param name="c">The index of the component (between 0 and N-1)
        /// 
        /// </param>
        /// <param name="rl">The resolution level, from 0 to L.
        /// 
        /// </param>
        /// <returns> the precinct partition width for the specified component,
        /// resolution level and tile.
        /// 
        /// </returns>
        public int GetPPX(int t, int c, int rl)
        {
            return decSpec.pss.GetPPX(t, c, rl);
        }

        /// <summary> Returns the precinct partition height for the specified tile-component
        /// and (tile-component) resolution level.
        /// 
        /// </summary>
        /// <param name="t">The tile index
        /// 
        /// </param>
        /// <param name="c">The index of the component (between 0 and N-1)
        /// 
        /// </param>
        /// <param name="rl">The resolution level, from 0 to L.
        /// 
        /// </param>
        /// <returns> The precinct partition height in the specified component, for
        /// the specified resolution level, for the current tile.
        /// 
        /// </returns>
        public int GetPPY(int t, int c, int rl)
        {
            return decSpec.pss.GetPPY(t, c, rl);
        }

        /// <summary> Initialises subbands fields, such as number of code-blocks, code-blocks
        /// dimension and number of magnitude bits, in the subband tree. The
        /// nominal code-block width/height depends on the precincts dimensions if
        /// used. The way the number of magnitude bits is computed depends on the
        /// quantization type (reversible, derived, expounded).
        /// 
        /// </summary>
        /// <param name="c">The component index
        /// 
        /// </param>
        /// <param name="sb">The subband tree to be initialised.
        /// 
        /// </param>
        protected internal virtual void initSubbandsFields(int c, SubbandSyn sb)
        {
            var t = TileIdx;
            var rl = sb.resLvl;
            int cbw, cbh;

            cbw = decSpec.cblks.GetCBlkWidth(ModuleSpec.SPEC_TILE_COMP, t, c);
            cbh = decSpec.cblks.GetCBlkHeight(ModuleSpec.SPEC_TILE_COMP, t, c);

            if (!sb.isNode)
            {
                // Code-block dimensions
                if (hd.precinctPartitionUsed())
                {
                    // The precinct partition is used
                    int ppxExp, ppyExp, cbwExp, cbhExp;

                    // Get exponents
                    ppxExp = MathUtil.log2(GetPPX(t, c, rl));
                    ppyExp = MathUtil.log2(GetPPY(t, c, rl));
                    cbwExp = MathUtil.log2(cbw);
                    cbhExp = MathUtil.log2(cbh);

                    switch (sb.resLvl)
                    {

                        case 0:
                            sb.nomCBlkW = (cbwExp < ppxExp ? (1 << cbwExp) : (1 << ppxExp));
                            sb.nomCBlkH = (cbhExp < ppyExp ? (1 << cbhExp) : (1 << ppyExp));
                            break;


                        default:
                            sb.nomCBlkW = (cbwExp < ppxExp - 1 ? (1 << cbwExp) : (1 << (ppxExp - 1)));
                            sb.nomCBlkH = (cbhExp < ppyExp - 1 ? (1 << cbhExp) : (1 << (ppyExp - 1)));
                            break;

                    }
                }
                else
                {
                    sb.nomCBlkW = cbw;
                    sb.nomCBlkH = cbh;
                }

                // Number of code-blocks
                if (sb.numCb == null)
                    sb.numCb = new Coord();
                if (sb.w == 0 || sb.h == 0)
                {
                    sb.numCb.x = 0;
                    sb.numCb.y = 0;
                }
                else
                {
                    var cb0x = CbULX;
                    var cb0y = CbULY;
                    int tmp;

                    // Projects code-block partition origin to subband. Since the
                    // origin is always 0 or 1, it projects to the low-pass side
                    // (throught the ceil operator) as itself (i.e. no change) and
                    // to the high-pass side (through the floor operator) as 0,
                    // always.
                    var acb0x = cb0x;
                    var acb0y = cb0y;

                    switch (sb.sbandIdx)
                    {

                        case Subband.WT_ORIENT_LL:
                            // No need to project since all low-pass => nothing to do
                            break;

                        case Subband.WT_ORIENT_HL:
                            acb0x = 0;
                            break;

                        case Subband.WT_ORIENT_LH:
                            acb0y = 0;
                            break;

                        case Subband.WT_ORIENT_HH:
                            acb0x = 0;
                            acb0y = 0;
                            break;

                        default:
                            throw new InvalidOperationException("Internal JJ2000 error");

                    }
                    if (sb.ulcx - acb0x < 0 || sb.ulcy - acb0y < 0)
                    {
                        throw new ArgumentException("Invalid code-blocks " + "partition origin or " + "image offset in the " + "reference grid.");
                    }

                    // NOTE: when calculating "floor()" by integer division the
                    // dividend and divisor must be positive, we ensure that by
                    // adding the divisor to the dividend and then substracting 1
                    // to the result of the division

                    tmp = sb.ulcx - acb0x + sb.nomCBlkW;
                    sb.numCb.x = (tmp + sb.w - 1) / sb.nomCBlkW - (tmp / sb.nomCBlkW - 1);

                    tmp = sb.ulcy - acb0y + sb.nomCBlkH;
                    sb.numCb.y = (tmp + sb.h - 1) / sb.nomCBlkH - (tmp / sb.nomCBlkH - 1);
                }

                // Number of magnitude bits
                if (derived[c])
                {
                    // Validate array bounds before accessing
                    if (dequantParams[c].exp == null || dequantParams[c].exp.Length == 0)
                    {
                        throw new CorruptedCodestreamException(
                            $"Quantization exponent array not initialized for component {c}. " +
                            "QCD or QCC marker may be missing or corrupted.");
                    }
                    
                    if (dequantParams[c].exp[0] == null || dequantParams[c].exp[0].Length == 0)
                    {
                        throw new CorruptedCodestreamException(
                            $"Quantization exponent sub-array not initialized for component {c}. " +
                            "QCD or QCC marker may be missing or corrupted.");
                    }
                    
                    sb.magbits = gb[c] + (dequantParams[c].exp[0][0] - (mdl[c] - sb.level)) - 1;
                }
                else
                {
                    // Validate array bounds before accessing
                    if (dequantParams[c].exp == null || 
                        sb.resLvl >= dequantParams[c].exp.Length ||
                        sb.resLvl < 0)
                    {
                        throw new CorruptedCodestreamException(
                            $"Invalid resolution level {sb.resLvl} for component {c}. " +
                            $"Exponent array has {dequantParams[c].exp?.Length ?? 0} elements. " +
                            "QCD or QCC marker may specify incorrect decomposition levels.");
                    }
                    
                    if (dequantParams[c].exp[sb.resLvl] == null ||
                        sb.sbandIdx >= dequantParams[c].exp[sb.resLvl].Length ||
                        sb.sbandIdx < 0)
                    {
                        throw new CorruptedCodestreamException(
                            $"Invalid subband index {sb.sbandIdx} at resolution level {sb.resLvl} for component {c}. " +
                            $"Subband array has {dequantParams[c].exp[sb.resLvl]?.Length ?? 0} elements. " +
                            "Subband structure may be corrupted.");
                    }
                    
                    sb.magbits = gb[c] + dequantParams[c].exp[sb.resLvl][sb.sbandIdx] - 1;
                }
            }
            else
            {
                initSubbandsFields(c, (SubbandSyn)sb.LL);
                initSubbandsFields(c, (SubbandSyn)sb.HL);
                initSubbandsFields(c, (SubbandSyn)sb.LH);
                initSubbandsFields(c, (SubbandSyn)sb.HH);
            }
        }
        public abstract DecLyrdCBlk GetCodeBlock(int param1, int param2, int param3, SubbandSyn param4, int param5, int param6, DecLyrdCBlk param7);
    }
}