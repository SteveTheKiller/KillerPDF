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
using CoreJ2K.j2k.util;
using CoreJ2K.j2k.wavelet;
using CoreJ2K.j2k.wavelet.analysis;
using System;

namespace CoreJ2K.j2k.image.forwcomptransf
{

    /// <summary> This class extends CompTransfSpec class in order to hold encoder specific
    /// aspects of CompTransfSpec.
    /// 
    /// </summary>
    /// <seealso cref="CompTransfSpec" />
    public sealed class ForwCompTransfSpec : CompTransfSpec, FilterTypes
    {
        /// <summary> Constructs a new 'ForwCompTransfSpec' for the specified number of
        /// components and tiles, the wavelet filters type and the parameter of the
        /// option 'Mct'. This constructor is called by the encoder. It also checks
        /// that the arguments belong to the recognized arguments list.
        /// 
        /// This constructor chose the component transformation type depending
        /// on the wavelet filters : RCT with w5x3 filter and ICT with w9x7
        /// filter. Note: All filters must use the same data type.
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
        /// <param name="wfs">The wavelet filter specifications
        /// 
        /// </param>
        /// <param name="pl">The ParameterList
        /// 
        /// </param>
        public ForwCompTransfSpec(int nt, int nc, byte type, AnWTFilterSpec wfs, ParameterList pl) : base(nt, nc, type)
        {

            var param = pl.GetParameter("Mct");

            if (param == null)
            {
                // The option has not been specified

                // If less than three component, do not use any component
                // transformation 
                if (nc < 3)
                {
                    SetDefault("none");
                    return;
                }
                // If the compression is lossless, uses RCT
                else if (pl.GetBooleanParameter("lossless"))
                {
                    SetDefault("rct");
                    return;
                }
                else
                {
                    AnWTFilter[][] anfilt;
                    var filtType = new int[nComp];
                    for (var c = 0; c < 3; c++)
                    {
                        anfilt = (AnWTFilter[][])wfs.GetCompDef(c);
                        filtType[c] = anfilt[0][0].FilterType;
                    }

                    // Check that the three first components use the same filters
                    var reject = false;
                    for (var c = 1; c < 3; c++)
                    {
                        if (filtType[c] != filtType[0])
                            reject = true;
                    }

                    if (reject)
                    {
                        SetDefault("none");
                    }
                    else
                    {
                        anfilt = (AnWTFilter[][])wfs.GetCompDef(0);
                        SetDefault(anfilt[0][0].FilterType == FilterTypes_Fields.W9X7 ? "ict" : "rct");
                    }
                }

                // Each tile receives a component transform specification
                // according the type of wavelet filters that are used by the
                // three first components
                for (var t = 0; t < nt; t++)
                {
                    AnWTFilter[][] anfilt;
                    var filtType = new int[nComp];
                    for (var c = 0; c < 3; c++)
                    {
                        anfilt = (AnWTFilter[][])wfs.GetTileCompVal(t, c);
                        filtType[c] = anfilt[0][0].FilterType;
                    }

                    // Check that the three components use the same filters
                    var reject = false;
                    for (var c = 1; c < nComp; c++)
                    {
                        if (filtType[c] != filtType[0])
                            reject = true;
                    }

                    if (reject)
                    {
                        SetTileDef(t, "none");
                    }
                    else
                    {
                        anfilt = (AnWTFilter[][])wfs.GetTileCompVal(t, 0);
                        SetTileDef(t, anfilt[0][0].FilterType == FilterTypes_Fields.W9X7 ? "ict" : "rct");
                    }
                }
                return;
            }

            // Parse argument
            var stk = new Tokenizer(param);
            string word; // current word
            var curSpecType = SPEC_DEF; // Specification type of the
                                        // current parameter
            bool[] tileSpec = null; // Tiles concerned by the
                                    // specification
                                    //System.Boolean value;

            while (stk.HasMoreTokens())
            {
                word = stk.NextToken();

                switch (word[0])
                {

                    case 't':  // Tiles specification
                        tileSpec = parseIdx(word, nTiles);
                        curSpecType = curSpecType == SPEC_COMP_DEF ? SPEC_TILE_COMP : SPEC_TILE_DEF;
                        break;

                    case 'c':  // Components specification
                        throw new ArgumentException("Component specific  parameters not allowed with '-Mct' option");

                    default:
                        switch (word)
                        {
                            case "off":
                                switch (curSpecType)
                                {
                                    case SPEC_DEF:
                                        SetDefault("none");
                                        break;
                                    case SPEC_TILE_DEF:
                                        {
                                            for (var i = tileSpec.Length - 1; i >= 0; i--)
                                                if (tileSpec[i])
                                                {
                                                    SetTileDef(i, "none");
                                                }

                                            break;
                                        }
                                }

                                break;
                            case "on" when nc < 3:
                                throw new ArgumentException("Cannot use component" + " transformation on a " + "image with less than " + "three components");
                            case "on" when curSpecType == SPEC_DEF:
                                // Set arbitrarily the default
                                // value to RCT (later will be found the suitable
                                // component transform for each tile)
                                SetDefault("rct");
                                break;
                            case "on":
                                {
                                    if (curSpecType == SPEC_TILE_DEF)
                                    {
                                        for (var i = tileSpec.Length - 1; i >= 0; i--)
                                        {
                                            if (tileSpec[i])
                                            {
                                                SetTileDef(i,
                                                    GetFilterType(i, wfs) == FilterTypes_Fields.W5X3 ? "rct" : "ict");
                                            }
                                        }
                                    }

                                    break;
                                }
                            default:
                                throw new ArgumentException($"Default parameter of option Mct not recognized: {param}");
                        }

                        // Re-initialize
                        curSpecType = SPEC_DEF;
                        tileSpec = null;
                        break;

                }
            }

            // Check that default value has been specified
            if (GetDefault() == null)
            {
                // If not, set arbitrarily the default value to 'none' but
                // specifies explicitely a default value for each tile depending
                // on the wavelet transform that is used
                SetDefault("none");

                for (var t = 0; t < nt; t++)
                {
                    if (IsTileSpecified(t))
                    {
                        continue;
                    }

                    AnWTFilter[][] anfilt;
                    var filtType = new int[nComp];
                    for (var c = 0; c < 3; c++)
                    {
                        anfilt = (AnWTFilter[][])wfs.GetTileCompVal(t, c);
                        filtType[c] = anfilt[0][0].FilterType;
                    }

                    // Check that the three components use the same filters
                    var reject = false;
                    for (var c = 1; c < nComp; c++)
                    {
                        if (filtType[c] != filtType[0])
                            reject = true;
                    }

                    if (reject)
                    {
                        SetTileDef(t, "none");
                    }
                    else
                    {
                        anfilt = (AnWTFilter[][])wfs.GetTileCompVal(t, 0);
                        SetTileDef(t, anfilt[0][0].FilterType == FilterTypes_Fields.W9X7 ? "ict" : "rct");
                    }
                }
            }

            // Check validity of component transformation of each tile compared to
            // the filter used.
            for (var t = nt - 1; t >= 0; t--)
            {

                if (((string)GetTileDef(t)).Equals("none"))
                {
                    // No comp. transf is used. No check is needed
                    continue;
                }
                else if (((string)GetTileDef(t)).Equals("rct"))
                {
                    // Tile is using Reversible component transform
                    var filterType = GetFilterType(t, wfs);
                    switch (filterType)
                    {

                        case FilterTypes_Fields.W5X3:  // OK
                            break;

                        case FilterTypes_Fields.W9X7:  // Must use ICT
                            if (IsTileSpecified(t))
                            {
                                // User has requested RCT -> Error
                                throw new ArgumentException($"Cannot use RCT with 9x7 filter in tile {t}");
                            }
                            else
                            {
                                // Specify ICT for this tile
                                SetTileDef(t, "ict");
                            }
                            break;

                        default:
                            throw new ArgumentException("Default filter is " + "not JPEG 2000 part" + " I compliant");

                    }
                }
                else
                {
                    // ICT
                    var filterType = GetFilterType(t, wfs);
                    switch (filterType)
                    {

                        case FilterTypes_Fields.W5X3:  // Must use RCT
                            if (IsTileSpecified(t))
                            {
                                // User has requested ICT -> Error
                                throw new ArgumentException($"Cannot use ICT with filter 5x3 in tile {t}");
                            }
                            else
                            {
                                SetTileDef(t, "rct");
                            }
                            break;

                        case FilterTypes_Fields.W9X7:  // OK
                            break;

                        default:
                            throw new ArgumentException("Default filter is " + "not JPEG 2000 part" + " I compliant");

                    }
                }
            }
        }

        /// <summary> Get the filter type common to all component of a given tile. If the
        /// tile index is -1, it searches common filter type of default
        /// specifications.
        /// 
        /// </summary>
        /// <param name="t">The tile index
        /// 
        /// </param>
        /// <param name="wfs">The analysis filters specifications 
        /// 
        /// </param>
        /// <returns> The filter type common to all the components 
        /// 
        /// </returns>
        private int GetFilterType(int t, AnWTFilterSpec wfs)
        {
            AnWTFilter[][] anfilt;
            var filtType = new int[nComp];
            for (var c = 0; c < nComp; c++)
            {
                if (t == -1)
                {
                    anfilt = (AnWTFilter[][])wfs.GetCompDef(c);
                }
                else
                {
                    anfilt = (AnWTFilter[][])wfs.GetTileCompVal(t, c);
                }
                filtType[c] = anfilt[0][0].FilterType;
            }

            // Check that all filters are the same one
            var reject = false;
            for (var c = 1; c < nComp; c++)
            {
                if (filtType[c] != filtType[0])
                    reject = true;
            }
            if (reject)
            {
                throw new ArgumentException("Can not use component" + " transformation when " + "components do not use " + "the same filters");
            }
            return filtType[0];
        }
    }
}