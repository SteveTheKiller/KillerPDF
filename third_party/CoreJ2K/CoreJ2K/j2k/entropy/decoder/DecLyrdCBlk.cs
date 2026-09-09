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

using System.Linq;

namespace CoreJ2K.j2k.entropy.decoder
{

    /// <summary> This class stores coded (compressed) code-blocks that are organized in
    /// layers. This object can contain either all code-block data (i.e. all
    /// layers), or a subset of all the layers that make up the whole compressed
    /// code-block. It is applicable to the decoder engine only. Some data of the
    /// coded-block is stored in the super class, see CodedCBlk.
    /// 
    /// A code-block may have its progressive attribute set (i.e. the 'prog'
    /// flag is true). If a code-block is progressive then it means that more data
    /// for it may be obtained for an improved quality. If the progressive flag is
    /// false then no more data is available from the source for this
    /// code-block.
    /// 
    /// </summary>
    /// <seealso cref="CodedCBlk" />
    public class DecLyrdCBlk : CodedCBlk
    {

        /// <summary>The horizontal coordinate of the upper-left corner of the code-block </summary>
        public int ulx;

        /// <summary>The vertical coordinate of the upper left corner of the code-block </summary>
        public int uly;

        /// <summary>The width of the code-block </summary>
        public int w;

        /// <summary>The height of the code-block </summary>
        public int h;

        /// <summary>The coded (compressed) data length. The data is stored in the 'data'
        /// array (see super class).  
        /// </summary>
        public int dl;

        /// <summary>The progressive flag, false by default (see above). </summary>
        public bool prog;

        /// <summary>The number of layers in the coded data. </summary>
        public int nl;

        /// <summary>The index of the first truncation point returned </summary>
        public int ftpIdx;

        /// <summary>The total number of truncation points from layer 1 to the last one in
        /// this object. The number of truncation points in 'data' is
        /// 'nTrunc-ftpIdx'. 
        /// </summary>
        public int nTrunc;

        /// <summary>The length of each terminated segment. If null then there is only one
        /// terminated segment, and its length is 'dl'. The number of terminated
        /// segments is to be deduced from 'ftpIdx', 'nTrunc' and the coding
        /// options. This array contains all terminated segments from the 'ftpIdx'
        /// truncation point, upto, and including, the 'nTrunc-1' truncation
        /// point. Any data after 'nTrunc-1' is not included in any length. 
        /// </summary>
        public int[] tsLengths;

        /// <summary> Object information in a string
        /// 
        /// </summary>
        /// <returns> Information in a string
        /// 
        /// </returns>
        public override string ToString()
        {
            var str =
                $"Coded code-block ({m},{n}): {skipMSBP} MSB skipped, {dl} bytes, {nTrunc} truncation points, {nl} layers, progressive={prog}, ulx={ulx}, uly={uly}, w={w}, h={h}, ftpIdx={ftpIdx}";
            if (tsLengths != null)
            {
                str += " {";
                str = tsLengths.Aggregate(str, (current, t) => current + (" " + t));

                str += " }";
            }
            return str;
        }
    }
}