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

namespace CoreJ2K.j2k.wavelet
{

    /// <summary> This is the generic class from which the ones that hold the analysis or
    /// synthesis filters to be used in each part of the image derive. See
    /// AnWTFilterSpec and SynWTFilterSpec.
    /// 
    /// The filters to use are defined by a hierarchy. The hierarchy is:
    /// 
    /// - Tile and component specific filters<br>
    /// - Tile specific default filters<br>
    /// - Component main default filters<br>
    /// - Main default filters<br>
    /// 
    /// At the moment tiles are not supported by this class.
    /// 
    /// </summary>
    /// <seealso cref="j2k.wavelet.analysis.AnWTFilterSpec" />
    /// <seealso cref="j2k.wavelet.synthesis.SynWTFilterSpec" />

    public abstract class WTFilterSpec
    {
        /// <summary> Returns the data type used by the filters in this object, as defined in 
        /// the 'DataBlk' interface.
        /// 
        /// </summary>
        /// <returns> The data type of the filters in this object
        /// 
        /// </returns>
        /// <seealso cref="j2k.image.DataBlk">
        /// 
        /// 
        /// 
        /// </seealso>
        public abstract int WTDataType { get; }

        /// <summary>The identifier for "main default" specified filters </summary>
        public const byte FILTER_SPEC_MAIN_DEF = 0;

        /// <summary>The identifier for "component default" specified filters </summary>
        public const byte FILTER_SPEC_COMP_DEF = 1;

        /// <summary>The identifier for "tile specific default" specified filters </summary>
        public const byte FILTER_SPEC_TILE_DEF = 2;

        /// <summary>The identifier for "tile and component specific" specified filters </summary>
        public const byte FILTER_SPEC_TILE_COMP = 3;

        /// <summary>The spec type for each tile and component. The first index is the
        /// component index, the second is the tile index. NOTE: The tile specific
        /// things are not supported yet. 
        /// </summary>
        // Use byte to save memory (no need for speed here).
        protected internal byte[] specValType;

        /// <summary> Constructs a 'WTFilterSpec' object, initializing all the components and
        /// tiles to the 'FILTER_SPEC_MAIN_DEF' spec type, for the specified number
        /// of components and tiles.
        /// 
        /// NOTE: The tile specific things are not supported yet
        /// 
        /// </summary>
        /// <param name="nc">The number of components
        /// 
        /// </param>
        /// <param name="nt">The number of tiles
        /// 
        /// 
        /// 
        /// </param>
        protected internal WTFilterSpec(int nc)
        {
            specValType = new byte[nc];
        }

        /// <summary> Returns the type of specification for the filters in the specified
        /// component and tile. The specification type is one of:
        /// 'FILTER_SPEC_MAIN_DEF', 'FILTER_SPEC_COMP_DEF', 'FILTER_SPEC_TILE_DEF',
        /// 'FILTER_SPEC_TILE_COMP'.
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
        public virtual byte GetKerSpecType(int n)
        {
            return specValType[n];
        }
    }
}