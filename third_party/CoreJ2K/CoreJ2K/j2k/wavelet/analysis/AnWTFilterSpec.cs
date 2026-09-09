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
using CoreJ2K.Util;
using CoreJ2K.j2k.quantization;
using CoreJ2K.j2k.util;
using System;

namespace CoreJ2K.j2k.wavelet.analysis
{

    /// <summary> This class extends ModuleSpec class for analysis filters specification
    /// holding purpose.
    /// 
    /// </summary>
    /// <seealso cref="ModuleSpec" />
    public sealed class AnWTFilterSpec : ModuleSpec
    {

        /// <summary>The reversible default filter </summary>
        private const string REV_FILTER_STR = "w5x3";

        /// <summary>The non-reversible default filter </summary>
        private const string NON_REV_FILTER_STR = "w9x7";

        /// <summary> Constructs a new 'AnWTFilterSpec' for the specified number of
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
        /// <param name="qts">Quantization specifications
        /// 
        /// </param>
        /// <param name="pl">The ParameterList
        /// 
        /// </param>
        public AnWTFilterSpec(int nt, int nc, byte type, QuantTypeSpec qts, ParameterList pl) : base(nt, nc, type)
        {

            // Check parameters
            pl.checkList(AnWTFilter.OPT_PREFIX, ParameterList.toNameArray(AnWTFilter.ParameterInfo));

            var param = pl.GetParameter("Ffilters");
            var isFilterSpecified = true;

            // No parameter specified
            if (param == null)
            {
                isFilterSpecified = false;

                // If lossless compression, uses the reversible filters in each
                // tile-components 
                if (pl.GetBooleanParameter("lossless"))
                {
                    SetDefault(parseFilters(REV_FILTER_STR));
                    return;
                }

                // If no filter is specified through the command-line, use
                // REV_FILTER_STR or NON_REV_FILTER_STR according to the
                // quantization type
                for (var t = nt - 1; t >= 0; t--)
                {
                    for (var c = nc - 1; c >= 0; c--)
                    {
                        switch (qts.GetSpecValType(t, c))
                        {

                            case SPEC_DEF:
                                if (GetDefault() == null)
                                {
                                    if (pl.GetBooleanParameter("lossless"))
                                        SetDefault(parseFilters(REV_FILTER_STR));
                                    SetDefault(((string)qts.GetDefault()).Equals("reversible")
                                        ? parseFilters(REV_FILTER_STR)
                                        : parseFilters(NON_REV_FILTER_STR));
                                }
                                specValType[t][c] = SPEC_DEF;
                                break;

                            case SPEC_COMP_DEF:
                                if (!IsCompSpecified(c))
                                {
                                    SetCompDef(c,
                                        ((string)qts.GetCompDef(c)).Equals("reversible")
                                            ? parseFilters(REV_FILTER_STR)
                                            : parseFilters(NON_REV_FILTER_STR));
                                }
                                specValType[t][c] = SPEC_COMP_DEF;
                                break;

                            case SPEC_TILE_DEF:
                                if (!IsTileSpecified(t))
                                {
                                    SetTileDef(t,
                                        ((string)qts.GetTileDef(t)).Equals("reversible")
                                            ? parseFilters(REV_FILTER_STR)
                                            : parseFilters(NON_REV_FILTER_STR));
                                }
                                specValType[t][c] = SPEC_TILE_DEF;
                                break;

                            case SPEC_TILE_COMP:
                                if (!IsTileCompSpecified(t, c))
                                {
                                    SetTileCompVal(t, c,
                                        ((string)qts.GetTileCompVal(t, c)).Equals("reversible")
                                            ? parseFilters(REV_FILTER_STR)
                                            : parseFilters(NON_REV_FILTER_STR));
                                }
                                specValType[t][c] = SPEC_TILE_COMP;
                                break;

                            default:
                                throw new ArgumentException("Unsupported " + "specification " + "type");

                        }
                    }
                }
                return;
            }

            // Parse argument
            var stk = new Tokenizer(param);
            string word; // current word
            var curSpecType = SPEC_DEF; // Specification type of the
                                        // current parameter
            bool[]? tileSpec = null; // Tiles concerned by the specification
            bool[]? compSpec = null; // Components concerned by the specification
            AnWTFilter[][] filter;

            while (stk.HasMoreTokens())
            {
                word = stk.NextToken();

                switch (word[0])
                {

                    case 't':
                    // Tiles specification
                    case 'T':  // Tiles specification
                        tileSpec = parseIdx(word, nTiles);
                        curSpecType = curSpecType == SPEC_COMP_DEF ? SPEC_TILE_COMP : SPEC_TILE_DEF;
                        break;

                    case 'c':
                    // Components specification
                    case 'C':  // Components specification
                        compSpec = parseIdx(word, nComp);
                        curSpecType = curSpecType == SPEC_TILE_DEF ? SPEC_TILE_COMP : SPEC_COMP_DEF;
                        break;

                    case 'w':
                    // WT filters specification
                    case 'W':  // WT filters specification
                        if (pl.GetBooleanParameter("lossless") && string.Equals(word, "w9x7", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new ArgumentException("Cannot use non " + "reversible " + "wavelet transform with" + " '-lossless' option");
                        }

                        filter = parseFilters(word);
                        switch (curSpecType)
                        {
                            case SPEC_DEF:
                                SetDefault(filter);
                                break;
                            case SPEC_TILE_DEF:
                                {
                                    for (var i = tileSpec!.Length - 1; i >= 0; i--)
                                        if (tileSpec[i])
                                        {
                                            SetTileDef(i, filter);
                                        }

                                    break;
                                }
                            case SPEC_COMP_DEF:
                                {
                                    for (var i = compSpec!.Length - 1; i >= 0; i--)
                                        if (compSpec[i])
                                        {
                                            SetCompDef(i, filter);
                                        }

                                    break;
                                }
                            default:
                                {
                                    for (var i = tileSpec!.Length - 1; i >= 0; i--)
                                    {
                                        for (var j = compSpec!.Length - 1; j >= 0; j--)
                                        {
                                            if (tileSpec[i] && compSpec[j])
                                            {
                                                SetTileCompVal(i, j, filter);
                                            }
                                        }
                                    }

                                    break;
                                }
                        }

                        // Re-initialize
                        curSpecType = SPEC_DEF;
                        tileSpec = null;
                        compSpec = null;
                        break;


                    default:
                        throw new ArgumentException($"Bad construction for parameter: {word}");

                }
            }

            // Check that default value has been specified
            if (GetDefault() == null)
            {
                var ndefspec = 0;
                for (var t = nt - 1; t >= 0; t--)
                {
                    for (var c = nc - 1; c >= 0; c--)
                    {
                        if (specValType[t][c] == SPEC_DEF)
                        {
                            ndefspec++;
                        }
                    }
                }

                // If some tile-component have received no specification, it takes
                // the default value defined in ParameterList
                if (ndefspec != 0)
                {
                    SetDefault(((string)qts.GetDefault()).Equals("reversible")
                        ? parseFilters(REV_FILTER_STR)
                        : parseFilters(NON_REV_FILTER_STR));
                }
                else
                {
                    // All tile-component have been specified, takes the first
                    // tile-component value as default.
                    SetDefault(GetTileCompVal(0, 0));
                    switch (specValType[0][0])
                    {

                        case SPEC_TILE_DEF:
                            for (var c = nc - 1; c >= 0; c--)
                            {
                                if (specValType[0][c] == SPEC_TILE_DEF)
                                    specValType[0][c] = SPEC_DEF;
                            }
                            tileDef[0] = null;
                            break;

                        case SPEC_COMP_DEF:
                            for (var t = nt - 1; t >= 0; t--)
                            {
                                if (specValType[t][0] == SPEC_COMP_DEF)
                                    specValType[t][0] = SPEC_DEF;
                            }
                            compDef[0] = null;
                            break;

                        case SPEC_TILE_COMP:
                            specValType[0][0] = SPEC_DEF;
                            tileCompVal[0] = null;
                            break;
                    }
                }
            }

            // Check consistency between filter and quantization type
            // specification
            for (var t = nt - 1; t >= 0; t--)
            {
                for (var c = nc - 1; c >= 0; c--)
                {
                    // Reversible quantization
                    if (((string)qts.GetTileCompVal(t, c)).Equals("reversible"))
                    {
                        // If filter is reversible, it is OK
                        if (IsReversible(t, c))
                            continue;

                        // If no filter has been defined, use reversible filter
                        if (!isFilterSpecified)
                        {
                            SetTileCompVal(t, c, parseFilters(REV_FILTER_STR));
                        }
                        else
                        {
                            // Non reversible filter specified -> Error
                            throw new ArgumentException(
                                $"Filter of tile-component ({t},{c}) does not allow reversible quantization. Specify '-Qtype expounded' or '-Qtype derived'in the command line.");
                        }
                    }
                    else
                    {
                        // No reversible quantization
                        // No reversible filter -> OK
                        if (!IsReversible(t, c))
                            continue;

                        // If no filter has been specified, use non-reversible
                        // filter
                        if (!isFilterSpecified)
                        {
                            SetTileCompVal(t, c, parseFilters(NON_REV_FILTER_STR));
                        }
                        else
                        {
                            // Reversible filter specified -> Error
                            throw new ArgumentException(
                                $"Filter of tile-component ({t},{c}) does not allow non-reversible quantization. Specify '-Qtype reversible' in the command line");
                        }
                    }
                }
            }
        }

        /// <summary> Parse filters from the given word
        /// 
        /// </summary>
        /// <param name="word">String to parse
        /// 
        /// </param>
        /// <returns> Analysis wavelet filter (first dimension: by direction,
        /// second dimension: by decomposition levels)
        /// </returns>
        private AnWTFilter[][] parseFilters(string word)
        {
            var filt = new AnWTFilter[2][];
            for (var i = 0; i < 2; i++)
            {
                filt[i] = new AnWTFilter[1];
            }
            if (string.Equals(word, "w5x3", StringComparison.OrdinalIgnoreCase))
            {
                filt[0][0] = new AnWTFilterIntLift5x3();
                filt[1][0] = new AnWTFilterIntLift5x3();
                return filt;
            }
            else if (string.Equals(word, "w9x7", StringComparison.OrdinalIgnoreCase))
            {
                filt[0][0] = new AnWTFilterFloatLift9x7();
                filt[1][0] = new AnWTFilterFloatLift9x7();
                return filt;
            }
            else
            {
                throw new ArgumentException($"Non JPEG 2000 part I filter: {word}");
            }
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
        public int GetWTDataType(int t, int c)
        {
            var an = (AnWTFilter[][])GetSpec(t, c);
            return an[0][0].DataType;
        }

        /// <summary> Returns the horizontal analysis filters to be used in component 'n' and 
        /// tile 't'.
        /// 
        /// The horizontal analysis filters are returned in an array of
        /// AnWTFilter. Each element contains the horizontal filter for each
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
        public AnWTFilter[] GetHFilters(int t, int c)
        {
            var an = (AnWTFilter[][])GetSpec(t, c);
            return an[0];
        }

        /// <summary> Returns the vertical analysis filters to be used in component 'n' and 
        /// tile 't'.
        /// 
        /// The vertical analysis filters are returned in an array of
        /// AnWTFilter. Each element contains the vertical filter for each
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
        public AnWTFilter[] GetVFilters(int t, int c)
        {
            var an = (AnWTFilter[][])GetSpec(t, c);
            return an[1];
        }

        /// <summary>Debugging method </summary>
        public override string ToString()
        {
            var str = "";
            AnWTFilter[][] an;

            str += $"nTiles={nTiles}\nnComp={nComp}\n\n";

            for (var t = 0; t < nTiles; t++)
            {
                for (var c = 0; c < nComp; c++)
                {
                    an = (AnWTFilter[][])GetSpec(t, c);

                    str += $"(t:{t},c:{c})\n";

                    // Horizontal filters
                    str += "\tH:";
                    for (var i = 0; i < an[0].Length; i++)
                    {
                        str += $" {an[0][i]}";
                    }
                    // Horizontal filters
                    str += "\n\tV:";
                    for (var i = 0; i < an[1].Length; i++)
                    {
                        str += $" {an[1][i]}";
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
        public bool IsReversible(int t, int c)
        {
            // Note: no need to buffer the result since this method is
            // normally called once per tile-component.
            AnWTFilter[] hfilter = GetHFilters(t, c), vfilter = GetVFilters(t, c);

            // As soon as a filter is not reversible, false can be returned
            for (var i = hfilter.Length - 1; i >= 0; i--)
                if (!hfilter[i].Reversible || !vfilter[i].Reversible)
                    return false;
            return true;
        }
    }
}