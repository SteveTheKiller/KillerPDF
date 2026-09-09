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

namespace CoreJ2K.j2k.entropy
{

    /// <summary> This class extends ModuleSpec class for code-blocks sizes holding purposes.
    /// 
    /// It stores the size a of code-block. 
    /// 
    /// </summary>
    public class CBlkSizeSpec : ModuleSpec
    {
        /// <summary> Returns the maximum code-block's width
        /// 
        /// </summary>
        public virtual int MaxCBlkWidth => maxCBlkWidth;

        /// <summary> Returns the maximum code-block's height
        /// 
        /// </summary>
        public virtual int MaxCBlkHeight => maxCBlkHeight;

        /// <summary>Name of the option </summary>
        private const string optName = "Cblksiz";

        /// <summary>The maximum code-block width </summary>
        private int maxCBlkWidth = 0;

        /// <summary>The maximum code-block height </summary>
        private int maxCBlkHeight = 0;

        /// <summary> Creates a new CBlkSizeSpec object for the specified number of tiles and
        /// components.
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
        public CBlkSizeSpec(int nt, int nc, byte type) : base(nt, nc, type)
        {
        }

        /// <summary> Creates a new CBlkSizeSpec object for the specified number of tiles and
        /// components and the ParameterList instance.
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
        /// <param name="imgsrc">The image source (used to get the image size)
        /// 
        /// </param>
        /// <param name="pl">The ParameterList instance
        /// 
        /// </param>
        public CBlkSizeSpec(int nt, int nc, byte type, ParameterList pl) : base(nt, nc, type)
        {

            var firstVal = true;
            var param = pl.GetParameter(optName);

            // Precinct partition is used : parse arguments
            var stk = new Tokenizer(param);
            var curSpecType = SPEC_DEF; // Specification type of the
                                        // current parameter
            bool[] tileSpec = null; // Tiles concerned by the specification
            bool[] compSpec = null; // Components concerned by the specification
            int ci, ti; //  i, xIdx removed
            string word = null; // current word
            string errMsg = null;

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
                        if (!char.IsDigit(word[0]))
                        {
                            errMsg = $"Bad construction for parameter: {word}";
                            throw new ArgumentException(errMsg);
                        }
                        var dim = new int[2];
                        // Get code-block's width
                        try
                        {
                            dim[0] = int.Parse(word);
                            // Check that width is not >
                            // StdEntropyCoderOptions.MAX_CB_DIM
                            if (dim[0] > StdEntropyCoderOptions.MAX_CB_DIM)
                            {
                                errMsg =
                                    $"'{optName}' option : the code-block's width cannot be greater than {StdEntropyCoderOptions.MAX_CB_DIM}";
                                throw new ArgumentException(errMsg);
                            }
                            // Check that width is not <
                            // StdEntropyCoderOptions.MIN_CB_DIM
                            if (dim[0] < StdEntropyCoderOptions.MIN_CB_DIM)
                            {
                                errMsg =
                                    $"'{optName}' option : the code-block's width cannot be less than {StdEntropyCoderOptions.MIN_CB_DIM}";
                                throw new ArgumentException(errMsg);
                            }
                            // Check that width is a power of 2
                            if (dim[0] != (1 << MathUtil.log2(dim[0])))
                            {
                                errMsg = $"'{optName}' option : the code-block's width must be a power of 2";
                                throw new ArgumentException(errMsg);
                            }
                        }
                        catch (FormatException)
                        {
                            errMsg = $"'{optName}' option : the code-block's width could not be parsed.";
                            throw new ArgumentException(errMsg);
                        }
                        // Get the next word in option
                        try
                        {
                            word = stk.NextToken();
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            errMsg = $"'{optName}' option : could not parse the code-block's height";
                            throw new ArgumentException(errMsg);
                        }
                        // Get the code-block's height
                        try
                        {
                            dim[1] = int.Parse(word);
                            // Check that height is not >
                            // StdEntropyCoderOptions.MAX_CB_DIM
                            if (dim[1] > StdEntropyCoderOptions.MAX_CB_DIM)
                            {
                                errMsg =
                                    $"'{optName}' option : the code-block's height cannot be greater than {StdEntropyCoderOptions.MAX_CB_DIM}";
                                throw new ArgumentException(errMsg);
                            }
                            // Check that height is not <
                            // StdEntropyCoderOptions.MIN_CB_DIM
                            if (dim[1] < StdEntropyCoderOptions.MIN_CB_DIM)
                            {
                                errMsg =
                                    $"'{optName}' option : the code-block's height cannot be less than {StdEntropyCoderOptions.MIN_CB_DIM}";
                                throw new ArgumentException(errMsg);
                            }
                            // Check that height is a power of 2
                            if (dim[1] != (1 << MathUtil.log2(dim[1])))
                            {
                                errMsg = $"'{optName}' option : the code-block's height must be a power of 2";
                                throw new ArgumentException(errMsg);
                            }
                            // Check that the code-block 'area' (i.e. width*height) is
                            // not greater than StdEntropyCoderOptions.MAX_CB_AREA
                            if (dim[0] * dim[1] > StdEntropyCoderOptions.MAX_CB_AREA)
                            {
                                errMsg =
                                    $"'{optName}' option : The code-block's area (i.e. width*height) cannot be greater than {StdEntropyCoderOptions.MAX_CB_AREA}";
                                throw new ArgumentException(errMsg);
                            }
                        }
                        catch (FormatException)
                        {
                            errMsg = $"'{optName}' option : the code-block's height could not be parsed.";
                            throw new ArgumentException(errMsg);
                        }

                        // Store the maximum dimensions if necessary
                        if (dim[0] > maxCBlkWidth)
                        {
                            maxCBlkWidth = dim[0];
                        }

                        if (dim[1] > maxCBlkHeight)
                        {
                            maxCBlkHeight = dim[1];
                        }

                        if (firstVal)
                        {
                            // This is the first time a value is given so we set it as
                            // the default one 
                            SetDefault(dim);
                            firstVal = false;
                        }

                        switch (curSpecType)
                        {

                            case SPEC_DEF:
                                SetDefault(dim);
                                break;

                            case SPEC_TILE_DEF:
                                for (ti = tileSpec.Length - 1; ti >= 0; ti--)
                                {
                                    if (tileSpec[ti])
                                    {
                                        SetTileDef(ti, dim);
                                    }
                                }
                                break;

                            case SPEC_COMP_DEF:
                                for (ci = compSpec.Length - 1; ci >= 0; ci--)
                                {
                                    if (compSpec[ci])
                                    {
                                        SetCompDef(ci, dim);
                                    }
                                }
                                break;

                            default:
                                for (ti = tileSpec.Length - 1; ti >= 0; ti--)
                                {
                                    for (ci = compSpec.Length - 1; ci >= 0; ci--)
                                    {
                                        if (tileSpec[ti] && compSpec[ci])
                                        {
                                            SetTileCompVal(ti, ci, dim);
                                        }
                                    }
                                }
                                break;

                        }
                        break;

                } // end switch
            }
        }

        /// <summary> Returns the code-block width :
        /// 
        /// <ul>
        /// <li>for the specified tile/component</li>
        /// <li>for the specified tile</li>
        /// <li>for the specified component</li>
        /// <li>default value</li>
        /// </ul>
        /// 
        /// The value returned depends on the value of the variable 'type' which
        /// can take the following values :<br>
        /// 
        /// <ul> 
        /// <li>SPEC_DEF -> Default value is returned. t and c values are
        /// ignored</li> 
        /// <li>SPEC_COMP_DEF -> Component default value is returned. t value is
        /// ignored</li>
        /// <li>SPEC_TILE_DEF -> Tile default value is returned. c value is
        /// ignored</li>
        /// <li>SPEC_TILE_COMP -> Tile/Component value is returned.</li>
        /// </ul>
        /// 
        /// </summary>
        /// <param name="type">The type of the value we want to be returned
        /// 
        /// </param>
        /// <param name="t">The tile index
        /// 
        /// </param>
        /// <param name="c">the component index
        /// 
        /// </param>
        /// <returns> The code-block width for the specified tile and component
        /// 
        /// </returns>
        public virtual int GetCBlkWidth(byte type, int t, int c)
        {
            int[] dim = null;
            switch (type)
            {

                case SPEC_DEF:
                    dim = (int[])GetDefault();
                    break;

                case SPEC_COMP_DEF:
                    dim = (int[])GetCompDef(c);
                    break;

                case SPEC_TILE_DEF:
                    dim = (int[])GetTileDef(t);
                    break;

                case SPEC_TILE_COMP:
                    dim = (int[])GetTileCompVal(t, c);
                    break;
            }
            return dim[0];
        }

        /// <summary> Returns the code-block height:
        /// 
        /// <ul>
        /// <li>for the specified tile/component</li>
        /// <li>for the specified tile</li>
        /// <li>for the specified component</li>
        /// <li>default value</li>
        /// </ul>
        /// 
        /// The value returned depends on the value of the variable 'type' which
        /// can take the following values :
        /// 
        /// <ul> 
        /// <li>SPEC_DEF -> Default value is returned. t and c values are
        /// ignored</li> 
        /// <li>SPEC_COMP_DEF -> Component default value is returned. t value is
        /// ignored</li>
        /// <li>SPEC_TILE_DEF -> Tile default value is returned. c value is
        /// ignored</li>
        /// <li>SPEC_TILE_COMP -> Tile/Component value is returned.</li>
        /// </ul>
        /// 
        /// </summary>
        /// <param name="type">The type of the value we want to be returned
        /// 
        /// </param>
        /// <param name="t">The tile index
        /// 
        /// </param>
        /// <param name="c">the component index
        /// 
        /// </param>
        /// <returns> The code-block height for the specified tile and component
        /// 
        /// </returns>
        public virtual int GetCBlkHeight(byte type, int t, int c)
        {
            int[] dim = null;
            switch (type)
            {

                case SPEC_DEF:
                    dim = (int[])GetDefault();
                    break;

                case SPEC_COMP_DEF:
                    dim = (int[])GetCompDef(c);
                    break;

                case SPEC_TILE_DEF:
                    dim = (int[])GetTileDef(t);
                    break;

                case SPEC_TILE_COMP:
                    dim = (int[])GetTileCompVal(t, c);
                    break;
            }
            return dim[1];
        }

        /// <summary> Sets default value for this module 
        /// 
        /// </summary>
        /// <param name="value">Default value
        /// 
        /// </param>
        public sealed override void SetDefault(object value)
        {
            base.SetDefault(value);

            // Store the biggest code-block dimensions
            storeHighestDims((int[])value);
        }

        /// <summary> Sets default value for specified tile and specValType tag if allowed by
        /// its priority.
        /// 
        /// </summary>
        /// <param name="c">Tile index.
        /// 
        /// </param>
        /// <param name="value">Tile's default value
        /// 
        /// </param>
        public sealed override void SetTileDef(int t, object value)
        {
            base.SetTileDef(t, value);

            // Store the biggest code-block dimensions
            storeHighestDims((int[])value);
        }

        /// <summary> Sets default value for specified component and specValType tag if
        /// allowed by its priority.
        /// 
        /// </summary>
        /// <param name="c">Component index 
        /// 
        /// </param>
        /// <param name="value">Component's default value
        /// 
        /// </param>
        public sealed override void SetCompDef(int c, object value)
        {
            base.SetCompDef(c, value);

            // Store the biggest code-block dimensions
            storeHighestDims((int[])value);
        }

        /// <summary> Sets value for specified tile-component.
        /// 
        /// </summary>
        /// <param name="t">Tie index 
        /// 
        /// </param>
        /// <param name="c">Component index 
        /// 
        /// </param>
        /// <param name="value">Tile-component's value
        /// 
        /// </param>
        public sealed override void SetTileCompVal(int t, int c, object value)
        {
            base.SetTileCompVal(t, c, value);

            // Store the biggest code-block dimensions
            storeHighestDims((int[])value);
        }

        /// <summary> Stores the highest code-block width and height
        /// 
        /// </summary>
        /// <param name="dim">The 2 elements array that contains the code-block width and
        /// height.
        /// 
        /// </param>
        private void storeHighestDims(int[] dim)
        {
            // Store the biggest code-block dimensions
            if (dim[0] > maxCBlkWidth)
            {
                maxCBlkWidth = dim[0];
            }
            if (dim[1] > maxCBlkHeight)
            {
                maxCBlkHeight = dim[1];
            }
        }
    }
}