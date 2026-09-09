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

namespace CoreJ2K.j2k.wavelet.synthesis
{

    /// <summary> This class extends ModuleSpec class for synthesis filters specification
    /// holding purpose.
    /// 
    /// </summary>
    /// <seealso cref="ModuleSpec" />
    public class SynWTFilterSpec : ModuleSpec
    {

        /// <summary> Constructs a new 'SynWTFilterSpec' for the specified number of
        /// components and tiles.
        /// 
        /// </summary>
        /// <param name="nt">The number of tiles
        /// 
        /// </param>
        /// <param name="nc">The number of components
        /// 
        /// </param>
        /// <param name="type">the type of the specification module i.e. tile specific,
        /// component specific or both.
        /// 
        /// </param>
        public SynWTFilterSpec(int nt, int nc, byte type) : base(nt, nc, type)
        {
        }

        /// <summary> Returns the data type used by the filters in this object, as defined in
        /// the 'DataBlk' interface for specified tile-component.
        /// 
        /// </summary>
        /// <param name="t">Tile index
        /// 
        /// </param>
        /// <param name="c">Component index
        /// 
        /// </param>
        /// <returns> The data type of the filters in this object
        /// 
        /// </returns>
        /// <seealso cref="j2k.image.DataBlk" />
        public virtual int GetWTDataType(int t, int c)
        {
            var an = (SynWTFilter[][])GetSpec(t, c);
            return an[0][0].DataType;
        }

        /// <summary> Returns the horizontal analysis filters to be used in component 'n' and
        /// tile 't'.
        /// 
        /// The horizontal analysis filters are returned in an array of
        /// SynWTFilter. Each element contains the horizontal filter for each
        /// resolution level starting with resolution level 1 (i.e. the analysis
        /// filter to go from resolution level 1 to resolution level 0). If there
        /// are less elements than the maximum resolution level, then the last
        /// element is assumed to be repeated.
        /// 
        /// </summary>
        /// <param name="t">The tile index, in raster scan order
        /// 
        /// </param>
        /// <param name="c">The component index.
        /// 
        /// </param>
        /// <returns> The array of horizontal analysis filters for component 'n' and
        /// tile 't'.
        /// 
        /// </returns>
        public virtual SynWTFilter[] GetHFilters(int t, int c)
        {
            var an = (SynWTFilter[][])GetSpec(t, c);
            return an[0];
        }

        /// <summary> Returns the vertical analysis filters to be used in component 'n' and
        /// tile 't'.
        /// 
        /// The vertical analysis filters are returned in an array of
        /// SynWTFilter. Each element contains the vertical filter for each
        /// resolution level starting with resolution level 1 (i.e. the analysis
        /// filter to go from resolution level 1 to resolution level 0). If there
        /// are less elements than the maximum resolution level, then the last
        /// element is assumed to be repeated.
        /// 
        /// </summary>
        /// <param name="t">The tile index, in raster scan order
        /// 
        /// </param>
        /// <param name="c">The component index.
        /// 
        /// </param>
        /// <returns> The array of horizontal analysis filters for component 'n' and
        /// tile 't'.
        /// 
        /// </returns>
        public virtual SynWTFilter[] GetVFilters(int t, int c)
        {
            var an = (SynWTFilter[][])GetSpec(t, c);
            return an[1];
        }

        /// <summary>Debugging method </summary>
        public override string ToString()
        {
            var str = "";
            SynWTFilter[][] an;

            str += $"nTiles={nTiles}\nnComp={nComp}\n\n";

            for (var t = 0; t < nTiles; t++)
            {
                for (var c = 0; c < nComp; c++)
                {
                    an = (SynWTFilter[][])GetSpec(t, c);

                    str += $"(t:{t},c:{c})\n";

                    // Horizontal filters
                    str += "\tH:";
                    for (var i = 0; i < an[0].Length; i++)
                    {
                        str += ($" {an[0][i]}");
                    }
                    // Horizontal filters
                    str += "\n\tV:";
                    for (var i = 0; i < an[1].Length; i++)
                    {
                        str += ($" {an[1][i]}");
                    }
                    str += "\n";
                }
            }

            return str;
        }

        /// <summary> Check the reversibility of filters contained is the given
        /// tile-component.
        /// 
        /// </summary>
        /// <param name="t">The index of the tile
        /// 
        /// </param>
        /// <param name="c">The index of the component
        /// 
        /// </param>
        public virtual bool IsReversible(int t, int c)
        {
            // Note: no need to buffer the result since this method is normally
            // called once per tile-component.
            SynWTFilter[] hfilter = GetHFilters(t, c), vfilter = GetVFilters(t, c);

            // As soon as a filter is not reversible, false can be returned
            for (var i = hfilter.Length - 1; i >= 0; i--)
                if (!hfilter[i].Reversible || !vfilter[i].Reversible)
                    return false;
            return true;
        }
    }
}