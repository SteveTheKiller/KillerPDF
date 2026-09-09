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
* 
* 
* 
*/
using System;

namespace CoreJ2K.j2k.wavelet
{

    /// <summary> This class holds the decomposition type to be used in each part of the
    /// image; the default one, the component specific ones, the tile default ones 
    /// and the component-tile specific ones.
    /// 
    /// The decomposition type identifiers values are the same as in the
    /// codestream.
    /// 
    /// The hierarchy is:<br>
    /// - Tile and component specific decomposition<br>
    /// - Tile specific default decomposition<br>
    /// - Component main default decomposition<br>
    /// - Main default decomposition<br>
    /// 
    /// At the moment tiles are not supported by this class.
    /// 
    /// </summary>
    public class WTDecompSpec
    {
        /// <summary> Returns the main default decomposition type.
        /// 
        /// </summary>
        /// <returns> The main default decomposition type.
        /// 
        /// 
        /// 
        /// </returns>
        public virtual int MainDefDecompType => mainDefDecompType;

        /// <summary> Returns the main default decomposition number of levels.
        /// 
        /// </summary>
        /// <returns> The main default decomposition number of levels.
        /// 
        /// 
        /// 
        /// </returns>
        public virtual int MainDefLevels => mainDefLevels;

        /// <summary> ID for the dyadic wavelet tree decomposition (also called
        /// "Mallat" in JPEG 2000): 0x00.
        /// </summary>
        public const int WT_DECOMP_DYADIC = 0;

        /// <summary> ID for the SPACL (as defined in JPEG 2000) wavelet tree
        /// decomposition (1 level of decomposition in the high bands and
        /// some specified number for the lowest LL band): 0x02.  
        /// </summary>
        public const int WT_DECOMP_SPACL = 2;

        /// <summary> ID for the PACKET (as defined in JPEG 2000) wavelet tree
        /// decomposition (2 levels of decomposition in the high bands and
        /// some specified number for the lowest LL band): 0x01. 
        /// </summary>
        public const int WT_DECOMP_PACKET = 1;

        /// <summary>The identifier for "main default" specified decomposition </summary>
        public const byte DEC_SPEC_MAIN_DEF = 0;

        /// <summary>The identifier for "component default" specified decomposition </summary>
        public const byte DEC_SPEC_COMP_DEF = 1;

        /// <summary>The identifier for "tile specific default" specified decomposition </summary>
        public const byte DEC_SPEC_TILE_DEF = 2;

        /// <summary>The identifier for "tile and component specific" specified
        /// decomposition 
        /// </summary>
        public const byte DEC_SPEC_TILE_COMP = 3;

        /// <summary>The spec type for each tile and component. The first index is the
        /// component index, the second is the tile index. NOTE: The tile specific
        /// things are not supported yet. 
        /// </summary>
        // Use byte to save memory (no need for speed here).
        private readonly byte[] specValType;

        /// <summary>The main default decomposition </summary>
        private readonly int mainDefDecompType;

        /// <summary>The main default number of decomposition levels </summary>
        private readonly int mainDefLevels;

        /// <summary>The component main default decomposition, for each component. </summary>
        private int[] compMainDefDecompType;

        /// <summary>The component main default decomposition levels, for each component </summary>
        private int[] compMainDefLevels;

        /// <summary> Constructs a new 'WTDecompSpec' for the specified number of components
        /// and tiles, with the given main default decomposition type and number of 
        /// levels.
        /// 
        /// NOTE: The tile specific things are not supported yet
        /// 
        /// </summary>
        /// <param name="nc">The number of components
        /// 
        /// </param>
        /// <param name="nt">The number of tiles
        /// 
        /// </param>
        /// <param name="dec">The main default decomposition type
        /// 
        /// </param>
        /// <param name="lev">The main default number of decomposition levels
        /// 
        /// 
        /// 
        /// </param>
        public WTDecompSpec(int nc, int dec, int lev)
        {
            mainDefDecompType = dec;
            mainDefLevels = lev;
            specValType = new byte[nc];
        }

        /// <summary> Sets the "component main default" decomposition type and number of
        /// levels for the specified component. Both 'dec' and 'lev' can not be
        /// negative at the same time.
        /// 
        /// </summary>
        /// <param name="n">The component index
        /// 
        /// </param>
        /// <param name="dec">The decomposition type. If negative then the main default is
        /// used.
        /// 
        /// </param>
        /// <param name="lev">The number of levels. If negative then the main defaul is
        /// used.
        /// 
        /// 
        /// 
        /// </param>
        public virtual void SetMainCompDefDecompType(int n, int dec, int lev)
        {
            if (dec < 0 && lev < 0)
            {
                throw new ArgumentException();
            }
            // Set spec type and decomp
            specValType[n] = DEC_SPEC_COMP_DEF;
            if (compMainDefDecompType == null)
            {
                compMainDefDecompType = new int[specValType.Length];
                compMainDefLevels = new int[specValType.Length];
            }
            compMainDefDecompType[n] = (dec >= 0) ? dec : mainDefDecompType;
            compMainDefLevels[n] = (lev >= 0) ? lev : mainDefLevels;
            // For the moment disable it since other parts of JJ2000 do not
            // support this
            throw new NotImplementedException("Currently, in JJ2000, all components and tiles must have the same decomposition type and number of levels");
        }

        /// <summary> Returns the type of specification for the decomposition in the
        /// specified component and tile. The specification type is one of:
        /// 'DEC_SPEC_MAIN_DEF', 'DEC_SPEC_COMP_DEF', 'DEC_SPEC_TILE_DEF',
        /// 'DEC_SPEC_TILE_COMP'.
        /// 
        /// NOTE: The tile specific things are not supported yet
        /// 
        /// </summary>
        /// <param name="n">The component index
        /// 
        /// </param>
        /// <param name="t">The tile index, in raster scan order.
        /// 
        /// </param>
        /// <returns> The specification type for component 'n' and tile 't'.
        /// 
        /// 
        /// 
        /// </returns>
        public virtual byte GetDecSpecType(int n)
        {
            return specValType[n];
        }

        /// <summary> Returns the decomposition type to be used in component 'n' and tile
        /// 't'.
        /// 
        /// NOTE: The tile specific things are not supported yet
        /// 
        /// </summary>
        /// <param name="n">The component index.
        /// 
        /// </param>
        /// <param name="t">The tile index, in raster scan order
        /// 
        /// </param>
        /// <returns> The decomposition type to be used.
        /// 
        /// 
        /// 
        /// </returns>
        public virtual int GetDecompType(int n)
        {
            switch (specValType[n])
            {

                case DEC_SPEC_MAIN_DEF:
                    return mainDefDecompType;

                case DEC_SPEC_COMP_DEF:
                    return compMainDefDecompType[n];

                case DEC_SPEC_TILE_DEF:
                    throw new NotImplementedException();

                case DEC_SPEC_TILE_COMP:
                    throw new NotImplementedException();

                default:
                    throw new InvalidOperationException("Internal JJ2000 error");

            }
        }

        /// <summary> Returns the decomposition number of levels in component 'n' and tile
        /// 't'.
        /// 
        /// NOTE: The tile specific things are not supported yet
        /// 
        /// </summary>
        /// <param name="n">The component index.
        /// 
        /// </param>
        /// <param name="t">The tile index, in raster scan order
        /// 
        /// </param>
        /// <returns> The decomposition number of levels.
        /// 
        /// 
        /// 
        /// </returns>
        public virtual int GetLevels(int n)
        {
            switch (specValType[n])
            {

                case DEC_SPEC_MAIN_DEF:
                    return mainDefLevels;

                case DEC_SPEC_COMP_DEF:
                    return compMainDefLevels[n];

                case DEC_SPEC_TILE_DEF:
                    throw new NotImplementedException();

                case DEC_SPEC_TILE_COMP:
                    throw new NotImplementedException();

                default:
                    throw new InvalidOperationException("Internal JJ2000 error");

            }
        }
    }
}