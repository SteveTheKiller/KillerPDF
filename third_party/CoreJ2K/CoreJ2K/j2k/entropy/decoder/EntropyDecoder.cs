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
using CoreJ2K.j2k.quantization.dequantizer;
using CoreJ2K.j2k.wavelet.synthesis;

namespace CoreJ2K.j2k.entropy.decoder
{

    /// <summary> This is the abstract class from which all entropy decoders must
    /// inherit. This class implements the 'MultiResImgData', therefore it has the
    /// concept of a current tile and all operations are performed on the current
    /// tile.
    /// 
    /// Default implementations of the methods in 'MultiResImgData' are provided
    /// through the 'MultiResImgDataAdapter' abstract class.
    /// 
    /// Sign magnitude representation is used (instead of two's complement) for
    /// the output data. The most significant bit is used for the sign (0 if
    /// positive, 1 if negative). Then the magnitude of the quantized coefficient
    /// is stored in the next most significat bits. The most significant magnitude
    /// bit corresponds to the most significant bit-plane and so on.</p
    /// 
    /// </summary>
    /// <seealso cref="MultiResImgData" />
    /// <seealso cref="MultiResImgDataAdapter" />
    public abstract class EntropyDecoder : MultiResImgDataAdapter, CBlkQuantDataSrcDec
    {
        /// <summary> Returns the horizontal code-block partition origin. Allowable values
        /// are 0 and 1, nothing else.
        /// 
        /// </summary>
        public virtual int CbULX => src.CbULX;

        /// <summary> Returns the vertical code-block partition origin. Allowable values are
        /// 0 and 1, nothing else.
        /// 
        /// </summary>
        public virtual int CbULY => src.CbULY;

        /// <summary> Returns the parameters that are used in this class and
        /// implementing classes. It returns a 2D String array. Each of the
        /// 1D arrays is for a different option, and they have 3
        /// elements. The first element is the option name, the second one
        /// is the synopsis and the third one is a long description of what
        /// the parameter is. The synopsis or description may be 'null', in
        /// which case it is assumed that there is no synopsis or
        /// description of the option, respectively. Null may be returned
        /// if no options are supported.
        /// 
        /// </summary>
        /// <returns> the options name, their synopsis and their explanation, 
        /// or null if no options are supported.</returns>
        public static string[][] ParameterInfo => pinfo;

        /// <summary>The prefix for entropy decoder optiojns: 'C' </summary>
        public const char OPT_PREFIX = 'C';

        /// <summary>The list of parameters that is accepted by the entropy
        /// decoders. They start with 'C'.</summary>
        private static readonly string[][] pinfo = { new string[] { "Cverber", "[on|off]", "Specifies if the entropy decoder should be verbose about detected errors. If 'on' a message is printed whenever an error is detected.", "on" }, new string[] { "Cer", "[on|off]", "Specifies if error detection should be performed by the entropy decoder engine. If errors are detected they will be concealed and " + "the resulting distortion will be less important. Note that errors " + "can only be detected if the encoder that generated the data " + "included error resilience information.", "on" } };

        /// <summary>The bit stream transport from where to get the compressed data
        /// (the source) 
        /// </summary>
        protected internal CodedCBlkDataSrcDec src;

        /// <summary> Initializes the source of compressed data.
        /// 
        /// </summary>
        /// <param name="src">From where to obtain the compressed data.
        /// 
        /// </param>
        protected EntropyDecoder(CodedCBlkDataSrcDec src) : base(src)
        {
            this.src = src;
        }

        /// <summary> Returns the subband tree, for the specified tile-component. This method
        /// returns the root element of the subband tree structure, see Subband and
        /// SubbandSyn. The tree comprises all the available resolution levels.
        /// 
        /// The number of magnitude bits ('magBits' member variable) for
        /// each subband is not initialized.
        /// 
        /// </summary>
        /// <param name="t">The index of the tile, from 0 to T-1.
        /// 
        /// </param>
        /// <param name="c">The index of the component, from 0 to C-1.
        /// 
        /// </param>
        /// <returns> The root of the tree structure.
        /// 
        /// </returns>
        public override SubbandSyn GetSynSubbandTree(int t, int c)
        {
            return src.GetSynSubbandTree(t, c);
        }
        public abstract DataBlk GetCodeBlock(int param1, int param2, int param3, SubbandSyn param4, DataBlk param5);
        public abstract DataBlk GetInternCodeBlock(int param1, int param2, int param3, SubbandSyn param4, DataBlk param5);
    }
}