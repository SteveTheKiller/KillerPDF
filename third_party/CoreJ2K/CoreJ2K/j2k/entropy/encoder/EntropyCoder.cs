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

using CoreJ2K.j2k.image;
using CoreJ2K.j2k.quantization.quantizer;
using CoreJ2K.j2k.util;
using CoreJ2K.j2k.wavelet;
using CoreJ2K.j2k.wavelet.analysis;

namespace CoreJ2K.j2k.entropy.encoder
{

    /// <summary> This abstract class provides the general interface for block-based entropy
    /// encoders. The input to the entropy coder is the quantized wavelet
    /// coefficients, or codewords, represented in sign magnitude. The output is a
    /// compressed code-block with rate-distortion information.
    /// 
    /// The source of data for objects of this class are 'CBlkQuantDataSrcEnc'
    /// objects.
    /// 
    /// For more details on the sign magnitude representation used see the
    /// Quantizer class.
    /// 
    /// This class provides default implemenations for most of the methods
    /// (wherever it makes sense), under the assumption that the image and
    /// component dimensions, and the tiles, are not modifed by the entropy
    /// coder. If that is not the case for a particular implementation then the
    /// methods should be overriden.
    /// 
    /// </summary>
    /// <seealso cref="Quantizer" />
    /// <seealso cref="CBlkQuantDataSrcEnc" />
    public abstract class EntropyCoder : ImgDataAdapter, CodedCBlkDataSrcEnc
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
        /// <returns> the options name, their synopsis and their explanation, or null
        /// if no options are supported.
        /// 
        /// </returns>
        public static string[][] ParameterInfo => pinfo;

        /// <summary>The prefix for entropy coder options: 'C' </summary>
        public const char OPT_PREFIX = 'C';

        /// <summary>The list of parameters that is accepted for entropy coding. Options 
        /// for entropy coding start with 'C'. 
        /// </summary>
        private static readonly string[][] pinfo = {new string[]{"Cblksiz", "[<tile-component idx>] <width> <height> " + "[[<tile-component idx>] <width> <height>]", "Specifies the maximum code-block size to use for tile-component. " + "The maximum width and height is 1024, however the surface area " + "(i.e. width x height) must not exceed 4096. The minimum width and " + "height is 4.", "64 64"}, new string[]{"Cbypass", "[<tile-component idx>] on|off" + "[ [<tile-component idx>] on|off ...]", "Uses the lazy coding mode with the entropy coder. This will bypass " + "the MQ coder for some of the coding passes, where the distribution " + "is often close to uniform. Since the MQ codeword will be " + "terminated " + "at least once per lazy pass, it is important to use an efficient " + "termination algorithm, see the 'Cterm_type' option." + "'on' enables, 'off' disables it.", "off"}, new string[]{"CresetMQ", "[<tile-component idx>] on|off" + "[ [<tile-component idx>] on|off ...]", "If this is enabled the probability estimates of the MQ coder are " + "reset after each arithmetically coded (i.e. non-lazy) coding pass. " + "'on' enables, 'off' disables it.", "off"}, new string[]{"Cterminate", "[<tile-component idx>] on|off" + "[ [<tile-component idx>] on|off ...]", "If this is enabled the codeword (raw or MQ) is terminated on a " + "byte boundary after each coding pass. In this case it is important " + "to use an efficient termination algorithm, see the 'Cterm' option. " + "'on' enables, 'off' disables it.", "off"}, new string[]{"Ccausal", "[<tile-component idx>] on|off" + "[ [<tile-component idx>] on|off ...]", "Uses vertically stripe causal context formation. If this is " + "enabled " + "the context formation process in one stripe is independant of the " + "next stripe (i.e. the one below it). 'on' " + "enables, 'off' disables it.", "off"}, new string[]{"Cseg_symbol", "[<tile-component idx>] on|off" + "[ [<tile-component idx>] on|off ...]",
            "Inserts an error resilience segmentation symbol in the MQ " + "codeword at the end of " + "each bit-plane (cleanup pass). Decoders can use this " + "information to detect and " + "conceal errors.'on' enables, 'off' disables " + "it.", "off"}, new string[]{"Cterm_type", "[<tile-component idx>] near_opt|easy|predict|full" + "[ [<tile-component idx>] near_opt|easy|predict|full ...]", "Specifies the algorithm used to terminate the MQ codeword. " + "The most efficient one is 'near_opt', which delivers a codeword " + "which in almost all cases is the shortest possible. The 'easy' is " + "a simpler algorithm that delivers a codeword length that is close " + "to the previous one (in average 1 bit longer). The 'predict' is" + " almost " + "the same as the 'easy' but it leaves error resilient information " + "on " + "the spare least significant bits (in average 3.5 bits), which can " + "be used by a decoder to detect errors. The 'full' algorithm " + "performs a full flush of the MQ coder and is highly inefficient.\n" + "It is important to use a good termination policy since the MQ " + "codeword can be terminated quite often, specially if the 'Cbypass'" + " or " + "'Cterminate' options are enabled (in the normal case it would be " + "terminated once per code-block, while if 'Cterminate' is specified " + "it will be done almost 3 times per bit-plane in each code-block).", "near_opt"}, new string[]{"Clen_calc", "[<tile-component idx>] near_opt|lazy_good|lazy" + "[ [<tile-component idx>] ...]", "Specifies the algorithm to use in calculating the necessary MQ " + "length for each decoding pass. The best one is 'near_opt', which " + "performs a rather sophisticated calculation and provides the best " + "results. The 'lazy_good' and 'lazy' are very simple algorithms " + "that " + "provide rather conservative results, 'lazy_good' one being " + "slightly " + "better. Do not change this option unless you want to experiment " + "the effect of different length calculation algorithms.", "near_opt"}, new
            string[]{"Cpp", "[<tile-component idx>] <dim> <dim> [<dim> <dim>] " + "[ [<tile-component idx>] ...]", "Specifies precinct partition dimensions for tile-component. The " + "first " + "two values apply to the highest resolution and the following ones " + "(if " + "any) apply to the remaining resolutions in decreasing order. If " + "less " + "values than the number of decomposition levels are specified, " + "then the " + "last two values are used for the remaining resolutions.", null}};

        /// <summary>The source of quantized wavelet coefficients </summary>
        protected internal CBlkQuantDataSrcEnc src;

        /// <summary> Initializes the source of quantized wavelet coefficients.
        /// 
        /// </summary>
        /// <param name="src">The source of quantized wavelet coefficients.
        /// 
        /// </param>
        protected EntropyCoder(CBlkQuantDataSrcEnc src) : base(src)
        {
            this.src = src;
        }

        /// <summary> Returns the code-block width for the specified tile and component.
        /// 
        /// </summary>
        /// <param name="t">The tile index
        /// 
        /// </param>
        /// <param name="c">the component index
        /// 
        /// </param>
        /// <returns> The code-block width for the specified tile and component
        /// 
        /// </returns>
        public abstract int GetCBlkWidth(int t, int c);

        /// <summary> Returns the code-block height for the specified tile and component.
        /// 
        /// </summary>
        /// <param name="t">The tile index
        /// 
        /// </param>
        /// <param name="c">the component index
        /// 
        /// </param>
        /// <returns> The code-block height for the specified tile and component
        /// 
        /// </returns>
        public abstract int GetCBlkHeight(int t, int c);

        /// <summary> Returns the reversibility of the tile-component data that is provided
        /// by the object.  Data is reversible when it is suitable for lossless and
        /// lossy-to-lossless compression.
        /// 
        /// Since entropy coders themselves are always reversible, it returns
        /// the reversibility of the data that comes from the 'CBlkQuantDataSrcEnc'
        /// source object (i.e. ROIScaler).
        /// 
        /// </summary>
        /// <param name="t">Tile index
        /// 
        /// </param>
        /// <param name="c">Component index
        /// 
        /// </param>
        /// <returns> true is the data is reversible, false if not.
        /// 
        /// </returns>
        /// <seealso cref="j2k.roi.encoder.ROIScaler" />
        public virtual bool IsReversible(int t, int c)
        {
            return src.IsReversible(t, c);
        }

        /// <summary> Returns a reference to the root of subband tree structure representing
        /// the subband decomposition for the specified tile-component.
        /// 
        /// </summary>
        /// <param name="t">The index of the tile.
        /// 
        /// </param>
        /// <param name="c">The index of the component.
        /// 
        /// </param>
        /// <returns> The root of the subband tree structure, see Subband.
        /// 
        /// </returns>
        /// <seealso cref="SubbandAn" />
        /// <seealso cref="Subband" />
        public virtual SubbandAn GetAnSubbandTree(int t, int c)
        {
            return src.GetAnSubbandTree(t, c);
        }

        /// <summary> Creates a EntropyCoder object for the appropriate entropy coding
        /// parameters in the parameter list 'pl', and having 'src' as the source
        /// of quantized data.
        /// 
        /// </summary>
        /// <param name="src">The source of data to be entropy coded
        /// 
        /// </param>
        /// <param name="pl">The parameter list (or options).
        /// 
        /// </param>
        /// <param name="cbks">Code-block size specifications
        /// 
        /// </param>
        /// <param name="pss">Precinct partition specifications
        /// 
        /// </param>
        /// <param name="bms">By-pass mode specifications
        /// 
        /// </param>
        /// <param name="mqrs">MQ-reset specifications
        /// 
        /// </param>
        /// <param name="rts">Regular termination specifications
        /// 
        /// </param>
        /// <param name="css">Causal stripes specifications
        /// 
        /// </param>
        /// <param name="sss">Error resolution segment symbol use specifications
        /// 
        /// </param>
        /// <param name="lcs">Length computation specifications
        /// 
        /// </param>
        /// <param name="tts">Termination type specifications
        /// 
        /// </param>
        /// <exception cref="IllegalArgumentException">If an error occurs while parsing
        /// the options in 'pl'
        /// 
        /// </exception>
        public static EntropyCoder createInstance(CBlkQuantDataSrcEnc src, ParameterList pl, CBlkSizeSpec cblks, PrecinctSizeSpec pss, StringSpec bms, StringSpec mqrs, StringSpec rts, StringSpec css, StringSpec sss, StringSpec lcs, StringSpec tts)
        {
            // Check parameters
            pl.checkList(OPT_PREFIX, ParameterList.toNameArray(pinfo));
            return new StdEntropyCoder(src, cblks, pss, bms, mqrs, rts, css, sss, lcs, tts);
        }
        public abstract CBlkRateDistStats GetNextCodeBlock(int param1, CBlkRateDistStats? param2);
        public abstract bool precinctPartitionUsed(int param1, int param2);
        public abstract int GetPPX(int param1, int param2, int param3);
        public abstract int GetPPY(int param1, int param2, int param3);
    }
}