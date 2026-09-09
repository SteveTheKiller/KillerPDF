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
using System;

namespace CoreJ2K.j2k
{

    /// <summary> This class extends ModuleSpec class in order to hold tile-component
    /// specifications using Strings.
    /// 
    /// </summary>
    /// <seealso cref="ModuleSpec" />
    public sealed class StringSpec : ModuleSpec
    {

        /// <summary> Constructs an empty 'StringSpec' with specified number of
        /// tile and components. This constructor is called by the decoder.
        /// 
        /// </summary>
        /// <param name="nt">Number of tiles
        /// 
        /// </param>
        /// <param name="nc">Number of components
        /// 
        /// </param>
        /// <param name="type">the type of the specification module i.e. tile specific,
        /// component specific or both.
        /// 
        /// </param>
        public StringSpec(int nt, int nc, byte type) : base(nt, nc, type)
        {
        }

        /// <summary> Constructs a new 'StringSpec' for the specified number of
        /// components:tiles and the arguments of <tt>optName</tt>
        /// option. This constructor is called by the encoder. It also
        /// checks that the arguments belongs to the recognized arguments
        /// list.
        /// 
        /// <u>Note:</u> The arguments must not start with 't' or 'c'
        /// since it is reserved for respectively tile and components
        /// indexes specification.
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
        /// <param name="optName">of the option using boolean spec.
        /// 
        /// </param>
        /// <param name="list">The list of all recognized argument in a String array
        /// 
        /// </param>
        /// <param name="pl">The ParameterList
        /// 
        /// </param>
        public StringSpec(int nt, int nc, byte type, string optName, string[] list, ParameterList pl) : base(nt, nc, type)
        {

            var param = pl.GetParameter(optName);
            var recognized = false;

            if (param == null)
            {
                param = pl.DefaultParameterList.GetParameter(optName);
                for (var i = list.Length - 1; i >= 0; i--)
                    if (string.Equals(param, list[i], StringComparison.OrdinalIgnoreCase))
                        recognized = true;
                if (!recognized)
                    throw new ArgumentException($"Default parameter of option -{optName} not recognized: {param}");
                SetDefault(param);
                return;
            }

            // Parse argument
            var stk = new Tokenizer(param);
            string word; // current word
            var curSpecType = SPEC_DEF; // Specification type of the
                                        // current parameter
            bool[] tileSpec = null; // Tiles concerned by the
                                    // specification
            bool[] compSpec = null; // Components concerned by the specification
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
                        compSpec = parseIdx(word, nComp);
                        curSpecType = curSpecType == SPEC_TILE_DEF ? SPEC_TILE_COMP : SPEC_COMP_DEF;
                        break;

                    default:
                        recognized = false;

                        for (var i = list.Length - 1; i >= 0; i--)
                            if (string.Equals(word, list[i], StringComparison.OrdinalIgnoreCase))
                                recognized = true;
                        if (!recognized)
                            throw new ArgumentException(
                                $"Default parameter of option -{optName} not recognized: {word}");

                        switch (curSpecType)
                        {
                            case SPEC_DEF:
                                SetDefault(word);
                                break;
                            case SPEC_TILE_DEF:
                                {
                                    for (var i = tileSpec.Length - 1; i >= 0; i--)
                                        if (tileSpec[i])
                                        {
                                            SetTileDef(i, word);
                                        }

                                    break;
                                }
                            case SPEC_COMP_DEF:
                                {
                                    for (var i = compSpec.Length - 1; i >= 0; i--)
                                        if (compSpec[i])
                                        {
                                            SetCompDef(i, word);
                                        }

                                    break;
                                }
                            default:
                                {
                                    for (var i = tileSpec.Length - 1; i >= 0; i--)
                                    {
                                        for (var j = compSpec.Length - 1; j >= 0; j--)
                                        {
                                            if (tileSpec[i] && compSpec[j])
                                            {
                                                SetTileCompVal(i, j, word);
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
                    param = pl.DefaultParameterList.GetParameter(optName);
                    for (var i = list.Length - 1; i >= 0; i--)
                        if (string.Equals(param, list[i], StringComparison.OrdinalIgnoreCase))
                            recognized = true;
                    if (!recognized)
                        throw new ArgumentException($"Default parameter of option -{optName} not recognized: {param}");
                    SetDefault(param);
                }
                else
                {
                    // All tile-component have been specified, takes the first
                    // tile-component value as default.
                    SetDefault(GetSpec(0, 0));
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
        }
    }
}