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

using CoreJ2K.j2k.encoder;
using CoreJ2K.j2k.image;
using CoreJ2K.j2k.wavelet;
using CoreJ2K.j2k.wavelet.analysis;

namespace CoreJ2K.j2k.quantization.quantizer
{

    /// <summary> This abstract class provides the general interface for quantizers. The
    /// input of a quantizer is the output of a wavelet transform. The output of
    /// the quantizer is the set of quantized wavelet coefficients represented in
    /// sign-magnitude notation (see below).
    /// 
    /// This class provides default implementation for most of the methods
    /// (wherever it makes sense), under the assumption that the image, component
    /// dimensions, and the tiles, are not modifed by the quantizer. If it is not
    /// the case for a particular implementation, then the methods should be
    /// overriden.
    /// 
    /// Sign magnitude representation is used (instead of two's complement) for
    /// the output data. The most significant bit is used for the sign (0 if
    /// positive, 1 if negative). Then the magnitude of the quantized coefficient
    /// is stored in the next M most significat bits. The rest of the bits (least
    /// significant bits) can contain a fractional value of the quantized
    /// coefficient. This fractional value is not to be coded by the entropy
    /// coder. However, it can be used to compute rate-distortion measures with
    /// greater precision.
    /// 
    /// The value of M is determined for each subband as the sum of the number
    /// of guard bits G and the nominal range of quantized wavelet coefficients in
    /// the corresponding subband (Rq), minus 1:
    /// 
    /// M = G + Rq -1
    /// 
    /// The value of G should be the same for all subbands. The value of Rq
    /// depends on the quantization step size, the nominal range of the component
    /// before the wavelet transform and the analysis gain of the subband (see
    /// Subband).
    /// 
    /// The blocks of data that are requested should not cross subband
    /// boundaries.
    /// 
    /// NOTE: At the moment only quantizers that implement the
    /// 'CBlkQuantDataSrcEnc' interface are supported.
    /// 
    /// </summary>
    /// <seealso cref="Subband" />
    public abstract class Quantizer : ImgDataAdapter, CBlkQuantDataSrcEnc
    {
        /// <summary> Returns the horizontal offset of the code-block partition. Allowable
        /// values are 0 and 1, nothing else.
        /// 
        /// </summary>
        public virtual int CbULX => src.CbULX;

        /// <summary> Returns the vertical offset of the code-block partition. Allowable
        /// values are 0 and 1, nothing else.
        /// 
        /// </summary>
        public virtual int CbULY => src.CbULY;

        /// <summary> Returns the parameters that are used in this class and implementing
        /// classes. It returns a 2D String array. Each of the 1D arrays is for a
        /// different option, and they have 3 elements. The first element is the
        /// option name, the second one is the synopsis, the third one is a long
        /// description of what the parameter is and the fourth is its default
        /// value. The synopsis or description may be 'null', in which case it is
        /// assumed that there is no synopsis or description of the option,
        /// respectively. Null may be returned if no options are supported.
        /// 
        /// </summary>
        /// <returns> the options name, their synopsis and their explanation, 
        /// or null if no options are supported.
        /// 
        /// </returns>
        public static string[][] ParameterInfo => pinfo;

        /// <summary>The prefix for quantizer options: 'Q' </summary>
        public const char OPT_PREFIX = 'Q';

        /// <summary>The list of parameters that is accepted for quantization. Options 
        /// for quantization start with 'Q'. 
        /// </summary>
        private static readonly string[][] pinfo = { new string[] { "Qtype", "[<tile-component idx>] <id> " + "[ [<tile-component idx>] <id> ...]", "Specifies which quantization type to use for specified " + "tile-component. The default type is either 'reversible' or " + "'expounded' depending on whether or not the '-lossless' option " + " is specified.\n" + "<tile-component idx> : see general note.\n" + "<id>: Supported quantization types specification are : " + "'reversible' " + "(no quantization), 'derived' (derived quantization step size) and " + "'expounded'.\n" + "Example: -Qtype reversible or -Qtype t2,4-8 c2 reversible t9 " + "derived.", null }, new string[] { "Qstep", "[<tile-component idx>] <bnss> " + "[ [<tile-component idx>] <bnss> ...]", "This option specifies the base normalized quantization step " + "size (bnss) for tile-components. It is normalized to a " + "dynamic range of 1 in the image domain. This parameter is " + "ignored in reversible coding. The default value is '1/128'" + " (i.e. 0.0078125).", "0.0078125" }, new string[] { "Qguard_bits", "[<tile-component idx>] <gb> " + "[ [<tile-component idx>] <gb> ...]", "The number of bits used for each tile-component in the quantizer" + " to avoid overflow (gb).", "2" } };

        /// <summary>The source of wavelet transform coefficients </summary>
        protected internal CBlkWTDataSrc src;

        /// <summary> Initializes the source of wavelet transform coefficients.
        /// 
        /// </summary>
        /// <param name="src">The source of wavelet transform coefficients.
        /// 
        /// </param>
        protected Quantizer(CBlkWTDataSrc src) : base(src)
        {
            this.src = src;
        }

        /// <summary> Returns the number of guard bits used by this quantizer in the
        /// given tile-component.
        /// 
        /// </summary>
        /// <param name="t">Tile index
        /// 
        /// </param>
        /// <param name="c">Component index
        /// 
        /// </param>
        /// <returns> The number of guard bits
        /// 
        /// </returns>
        public abstract int GetNumGuardBits(int t, int c);

        /// <summary> Returns true if the quantizer of given tile-component uses derived
        /// quantization step sizes.
        /// 
        /// </summary>
        /// <param name="t">Tile index
        /// 
        /// </param>
        /// <param name="c">Component index
        /// 
        /// </param>
        /// <returns> True if derived quantization is used.
        /// 
        /// </returns>
        public abstract bool IsDerived(int t, int c);

        /// <summary> Calculates the parameters of the SubbandAn objects that depend on the
        /// Quantizer. The 'stepWMSE' field is calculated for each subband which is
        /// a leaf in the tree rooted at 'sb', for the specified component. The
        /// subband tree 'sb' must be the one for the component 'n'.
        /// 
        /// </summary>
        /// <param name="sb">The root of the subband tree.
        /// 
        /// </param>
        /// <param name="n">The component index.
        /// 
        /// </param>
        /// <seealso cref="SubbandAn.stepWMSE" />
        protected internal abstract void calcSbParams(SubbandAn sb, int n);

        /// <summary> Returns a reference to the subband tree structure representing the
        /// subband decomposition for the specified tile-component.
        /// 
        /// This method gets the subband tree from the source and then
        /// calculates the magnitude bits for each leaf using the method
        /// calcSbParams().
        /// 
        /// </summary>
        /// <param name="t">The index of the tile.
        /// 
        /// </param>
        /// <param name="c">The index of the component.
        /// 
        /// </param>
        /// <returns> The subband tree structure, see SubbandAn.
        /// 
        /// </returns>
        /// <seealso cref="SubbandAn" />
        /// <seealso cref="Subband" />
        /// <seealso cref="calcSbParams" />
        public virtual SubbandAn GetAnSubbandTree(int t, int c)
        {
            SubbandAn sbba;

            // Ask for the wavelet tree of the source
            sbba = src.GetAnSubbandTree(t, c);
            // Calculate the stepWMSE
            calcSbParams(sbba, c);
            return sbba;
        }

        /// <summary> Creates a Quantizer object for the appropriate type of quantization
        /// specified in the options in the parameter list 'pl', and having 'src'
        /// as the source of data to be quantized. The 'rev' flag indicates if the
        /// quantization should be reversible.
        /// 
        /// NOTE: At the moment only sources of wavelet data that implement the
        /// 'CBlkWTDataSrc' interface are supported.
        /// 
        /// </summary>
        /// <param name="src">The source of data to be quantized
        /// 
        /// </param>
        /// <param name="encSpec">Encoder specifications
        /// 
        /// </param>
        /// <exception cref="IllegalArgumentException">If an error occurs while parsing
        /// the options in 'pl'
        /// 
        /// </exception>
        public static Quantizer createInstance(CBlkWTDataSrc src, EncoderSpecs encSpec)
        {
            // Instantiate quantizer
            return new StdQuantizer(src, encSpec);
        }

        /// <summary> Returns the maximum number of magnitude bits in any subband in the
        /// current tile.
        /// 
        /// </summary>
        /// <param name="c">the component number
        /// 
        /// </param>
        /// <returns> The maximum number of magnitude bits in all subbands of the
        /// current tile.
        /// 
        /// </returns>
        public abstract int GetMaxMagBits(int c);
        public abstract CBlkWTData? GetNextInternCodeBlock(int param1, CBlkWTData? param2);
        public abstract CBlkWTData? GetNextCodeBlock(int param1, CBlkWTData? param2);
        public abstract bool IsReversible(int param1, int param2);
    }
}