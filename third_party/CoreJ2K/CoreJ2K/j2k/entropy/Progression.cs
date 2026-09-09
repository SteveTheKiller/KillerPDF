/*
* CVS identifier:
*
* $Id : $
*
*
* Modified by:
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
* 
* 
* 
*/
using CoreJ2K.j2k.codestream;
using System;

namespace CoreJ2K.j2k.entropy
{

    /// <summary> This class holds one of the different progression orders defined in
    /// the bit stream. The type(s) of progression order are defined in the
    /// ProgressionType interface. A Progression object is totally defined
    /// by its component start and end, resolution level start and end and
    /// layer start and end indexes. If no progression order change is
    /// defined, there is only Progression instance. 
    /// 
    /// </summary>
    /// <seealso cref="ProgressionType" />
    public class Progression
    {

        /// <summary>Progression type as defined in ProgressionType interface </summary>
        public int type;

        /// <summary>Component index for the start of a progression </summary>
        public int cs;

        /// <summary>Component index for the end of a progression. </summary>
        public int ce;

        /// <summary>Resolution index for the start of a progression </summary>
        public int rs;

        /// <summary>Resolution index for the end of a progression. </summary>
        public int re;

        /// <summary>The index of the last layer. </summary>
        public int lye;

        /// <summary> Constructor. 
        /// 
        /// Builds a new Progression object with specified type and bounds
        /// of progression.
        /// 
        /// </summary>
        /// <param name="type">The progression type
        /// 
        /// </param>
        /// <param name="cs">The component index start
        /// 
        /// </param>
        /// <param name="ce">The component index end
        /// 
        /// </param>
        /// <param name="rs">The resolution level index start
        /// 
        /// </param>
        /// <param name="re">The resolution level index end
        /// 
        /// </param>
        /// <param name="lye">The layer index end
        /// 
        /// </param>
        public Progression(int type, int cs, int ce, int rs, int re, int lye)
        {
            this.type = type;
            this.cs = cs;
            this.ce = ce;
            this.rs = rs;
            this.re = re;
            this.lye = lye;
        }

        public override string ToString()
        {
            var str = "type= ";
            switch (type)
            {

                case ProgressionType.LY_RES_COMP_POS_PROG:
                    str += "layer, ";
                    break;

                case ProgressionType.RES_LY_COMP_POS_PROG:
                    str += "res, ";
                    break;

                case ProgressionType.RES_POS_COMP_LY_PROG:
                    str += "res-pos, ";
                    break;

                case ProgressionType.POS_COMP_RES_LY_PROG:
                    str += "pos-comp, ";
                    break;

                case ProgressionType.COMP_POS_RES_LY_PROG:
                    str += "pos-comp, ";
                    break;

                default:
                    throw new InvalidOperationException("Unknown progression type");

            }
            str += ("comp.: " + cs + "-" + ce + ", ");
            str += ("res.: " + rs + "-" + re + ", ");
            str += ("layer: up to " + lye);
            return str;
        }
    }
}