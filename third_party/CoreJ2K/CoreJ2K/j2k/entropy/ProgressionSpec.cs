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
using CoreJ2K.j2k.codestream;
using CoreJ2K.j2k.util;
using System;
using System.Collections.Generic;

namespace CoreJ2K.j2k.entropy
{

    /// <summary> This class extends ModuleSpec class for progression type(s) and progression
    /// order changes holding purposes.
    /// 
    /// It stores  the progression type(s) used in the  codestream. There can be
    /// several progression  type(s) if  progression order  changes are  used (POC
    /// markers).
    /// 
    /// </summary>
    public sealed class ProgressionSpec : ModuleSpec
    {

        /// <summary> Creates a new ProgressionSpec object for the specified number of tiles
        /// and components.
        /// 
        /// </summary>
        /// <param name="nt">The number of tiles
        /// 
        /// </param>
        /// <param name="nc">The number of components
        /// 
        /// </param>
        /// <param name="type">the type of the specification module i.e. tile specific,
        /// component specific or both. The ProgressionSpec class should only be
        /// used only with the type ModuleSpec.SPEC_TYPE_TILE.
        /// 
        /// </param>
        public ProgressionSpec(int nt, int nc, byte type) : base(nt, nc, type)
        {
            if (type != SPEC_TYPE_TILE)
            {
                throw new InvalidOperationException("Illegal use of class ProgressionSpec !");
            }
        }

        /// <summary> Creates a new ProgressionSpec object for the specified number of tiles,
        /// components and the ParameterList instance.
        /// 
        /// </summary>
        /// <param name="nt">The number of tiles
        /// 
        /// </param>
        /// <param name="nc">The number of components
        /// 
        /// </param>
        /// <param name="nl">The number of layer
        /// 
        /// </param>
        /// <param name="dls">The number of decomposition levels specifications
        /// 
        /// </param>
        /// <param name="type">the type of the specification module. The ProgressionSpec
        /// class should only be used only with the type ModuleSpec.SPEC_TYPE_TILE.
        /// 
        /// </param>
        /// <param name="pl">The ParameterList instance
        /// 
        /// </param>
        public ProgressionSpec(int nt, int nc, int nl, IntegerSpec dls, byte type, ParameterList pl) : base(nt, nc, type)
        {

            var param = pl.GetParameter("Aptype");
            Progression[] prog;
            var mode = -1;

            if (param == null)
            {
                // No parameter specified
                mode = checkProgMode(pl.GetParameter("Rroi") == null ? "res" : "layer");

                if (mode == -1)
                {
                    var errMsg = $"Unknown progression type : '{param}'";
                    throw new ArgumentException(errMsg);
                }
                prog = new Progression[1];
                prog[0] = new Progression(mode, 0, nc, 0, dls.Max + 1, nl);
                SetDefault(prog);
                return;
            }

            var stk = new Tokenizer(param);
            var curSpecType = SPEC_DEF; // Specification type of the
                                        // current parameter
            bool[] tileSpec = null; // Tiles concerned by the specification
            string word = null; // current word
            string errMsg2 = null; // Error message
            var needInteger = false; // True if an integer value is expected
            var intType = 0; // Type of read integer value (0=index of first
                             // resolution level, 1= index of first component, 2=index of first  
                             // layer not included, 3= index of first resolution level not
                             // included, 4= index of  first component not included
            var progression = new List<Progression>(10);
            var tmp = 0;
            Progression curProg = null;

            while (stk.HasMoreTokens())
            {
                word = stk.NextToken();

                switch (word[0])
                {

                    case 't':
                        // If progression were previously found, store them
                        if (progression.Count > 0)
                        {
                            // Ensure that all information has been taken
                            curProg.ce = nc;
                            curProg.lye = nl;
                            curProg.re = dls.Max + 1;
                            prog = new Progression[progression.Count];
                            progression.CopyTo(prog);
                            if (curSpecType == SPEC_DEF)
                            {
                                SetDefault(prog);
                            }
                            else if (curSpecType == SPEC_TILE_DEF)
                            {
                                for (var i = tileSpec.Length - 1; i >= 0; i--)
                                    if (tileSpec[i])
                                    {
                                        SetTileDef(i, prog);
                                    }
                            }
                        }
                        progression.Clear();
                        intType = -1;
                        needInteger = false;

                        // Tiles specification
                        tileSpec = parseIdx(word, nTiles);
                        curSpecType = SPEC_TILE_DEF;
                        break;

                    default:
                        // Here, words is either a Integer (progression bound index)
                        // or a String (progression order type). This is determined by
                        // the value of needInteger.
                        if (needInteger)
                        {
                            // Progression bound info
                            try
                            {
                                tmp = (int.Parse(word));
                            }
                            catch (FormatException)
                            {
                                // Progression has missing parameters
                                throw new ArgumentException(
                                    $"Progression order specification has missing parameters: {param}");
                            }

                            switch (intType)
                            {

                                case 0:  // cs
                                    if (tmp < 0 || tmp > (dls.Max + 1))
                                        throw new ArgumentException($"Invalid res_start in '-Aptype' option: {tmp}");
                                    curProg.rs = tmp; break;

                                case 1:  // rs
                                    if (tmp < 0 || tmp > nc)
                                    {
                                        throw new ArgumentException($"Invalid comp_start in '-Aptype' option: {tmp}");
                                    }
                                    curProg.cs = tmp; break;

                                case 2:  // lye
                                    if (tmp < 0)
                                        throw new ArgumentException($"Invalid layer_end in '-Aptype' option: {tmp}");
                                    if (tmp > nl)
                                    {
                                        tmp = nl;
                                    }
                                    curProg.lye = tmp; break;

                                case 3:  // ce
                                    if (tmp < 0)
                                        throw new ArgumentException($"Invalid res_end in '-Aptype' option: {tmp}");
                                    if (tmp > (dls.Max + 1))
                                    {
                                        tmp = dls.Max + 1;
                                    }
                                    curProg.re = tmp; break;

                                case 4:  // re
                                    if (tmp < 0)
                                        throw new ArgumentException($"Invalid comp_end in '-Aptype' option: {tmp}");
                                    if (tmp > nc)
                                    {
                                        tmp = nc;
                                    }
                                    curProg.ce = tmp; break;
                            }

                            if (intType < 4)
                            {
                                intType++;
                                needInteger = true;
                                break;
                            }
                            else if (intType == 4)
                            {
                                intType = 0;
                                needInteger = false;
                                break;
                            }
                            else
                            {
                                throw new InvalidOperationException($"Error in usage of 'Aptype' option: {param}");
                            }
                        }

                        if (!needInteger)
                        {
                            // Progression type info
                            mode = checkProgMode(word);
                            if (mode == -1)
                            {
                                errMsg2 = $"Unknown progression type : '{word}'";
                                throw new ArgumentException(errMsg2);
                            }
                            needInteger = true;
                            intType = 0;
                            curProg = progression.Count == 0
                                ? new Progression(mode, 0, nc, 0, dls.Max + 1, nl)
                                : new Progression(mode, 0, nc, 0, dls.Max + 1, nl);
                            progression.Add(curProg);
                        }
                        break;

                } // switch
            } // while 

            if (progression.Count == 0)
            {
                // No progression defined
                mode = checkProgMode(pl.GetParameter("Rroi") == null ? "res" : "layer");
                if (mode == -1)
                {
                    errMsg2 = $"Unknown progression type : '{param}'";
                    throw new ArgumentException(errMsg2);
                }
                prog = new Progression[1];
                prog[0] = new Progression(mode, 0, nc, 0, dls.Max + 1, nl);
                SetDefault(prog);
                return;
            }

            // Ensure that all information has been taken
            curProg.ce = nc;
            curProg.lye = nl;
            curProg.re = dls.Max + 1;

            // Store found progression
            prog = new Progression[progression.Count];
            progression.CopyTo(prog);

            if (curSpecType == SPEC_DEF)
            {
                SetDefault(prog);
            }
            else if (curSpecType == SPEC_TILE_DEF)
            {
                for (var i = tileSpec.Length - 1; i >= 0; i--)
                    if (tileSpec[i])
                    {
                        SetTileDef(i, prog);
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

                // If some tile-component have received no specification, they
                // receive the default progressiveness.
                if (ndefspec != 0)
                {
                    mode = checkProgMode(pl.GetParameter("Rroi") == null ? "res" : "layer");
                    if (mode == -1)
                    {
                        errMsg2 = $"Unknown progression type : '{param}'";
                        throw new ArgumentException(errMsg2);
                    }
                    prog = new Progression[1];
                    prog[0] = new Progression(mode, 0, nc, 0, dls.Max + 1, nl);
                    SetDefault(prog);
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
        }

        /// <summary> Check if the progression mode exists and if so, return its integer
        /// value. It returns -1 otherwise.
        /// 
        /// </summary>
        /// <param name="mode">The progression mode stored in a string
        /// 
        /// </param>
        /// <returns> The integer value of the progression mode or -1 if the
        /// progression mode does not exist.
        /// 
        /// </returns>
        /// <seealso cref="ProgressionType" />
        private int checkProgMode(string mode)
        {
            if (mode.Equals("res"))
            {
                return ProgressionType.RES_LY_COMP_POS_PROG;
            }
            else if (mode.Equals("layer"))
            {
                return ProgressionType.LY_RES_COMP_POS_PROG;
            }
            else if (mode.Equals("pos-comp"))
            {
                return ProgressionType.POS_COMP_RES_LY_PROG;
            }
            else if (mode.Equals("comp-pos"))
            {
                return ProgressionType.COMP_POS_RES_LY_PROG;
            }
            else if (mode.Equals("res-pos"))
            {
                return ProgressionType.RES_POS_COMP_LY_PROG;
            }
            else
            {
                // No corresponding progression mode, we return -1.
                return -1;
            }
        }
    }
}