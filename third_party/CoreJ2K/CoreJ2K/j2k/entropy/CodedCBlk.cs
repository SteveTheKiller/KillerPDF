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

namespace CoreJ2K.j2k.entropy
{

    /// <summary> This is the generic class to store coded (compressed) code-block. It stores
    /// the compressed data as well as the necessary side-information.
    /// 
    /// This class is normally not used. Instead the EncRDCBlk, EncLyrdCBlk and
    /// the DecLyrdCBlk subclasses are used.
    /// 
    /// </summary>
    /// <seealso cref="j2k.entropy.encoder.CBlkRateDistStats" />
    /// <seealso cref="j2k.entropy.decoder.DecLyrdCBlk" />
    public class CodedCBlk
    {

        /// <summary>The horizontal index of the code-block, within the subband. </summary>
        public int n;

        /// <summary>The vertical index of the code-block, within the subband. </summary>
        public int m;

        /// <summary>The number of skipped most significant bit-planes. </summary>
        public int skipMSBP;

        /// <summary>The compressed data </summary>
        public byte[]? data;

        /// <summary> Creates a new CodedCBlk object wit the default values and without
        /// allocating any space for its members.
        /// 
        /// </summary>
        public CodedCBlk()
        {
        }

        /// <summary> Creates a new CodedCBlk object with the specified values.
        /// 
        /// </summary>
        /// <param name="m">The horizontal index of the code-block, within the subband.
        /// 
        /// </param>
        /// <param name="n">The vertical index of the code-block, within the subband.
        /// 
        /// </param>
        /// <param name="skipMSBP">The number of skipped most significant bit-planes for
        /// this code-block.
        /// 
        /// </param>
        /// <param name="data">The compressed data. This array is referenced by this
        /// object so it should not be modified after.
        /// 
        /// </param>
        public CodedCBlk(int m, int n, int skipMSBP, byte[] data)
        {
            this.m = m;
            this.n = n;
            this.skipMSBP = skipMSBP;
            this.data = data;
        }

        /// <summary> Returns the contents of the object in a string. The string contains the
        /// following data: 'm', 'n', 'skipMSBP' and 'data.length. This is used for
        /// debugging.
        /// 
        /// </summary>
        /// <returns> A string with the contents of the object
        /// 
        /// </returns>
        public override string ToString()
        {
            return $"m={m}, n={n}, skipMSBP={skipMSBP}, data.length={((data != null) ? $"{data.Length}" : "(null)")}";
        }
    }
}