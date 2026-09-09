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
*  */

using CoreJ2K.j2k.image.invcomptransf;

namespace CoreJ2K.j2k.image
{

    /// <summary> This class extends the ModuleSpec class in order to hold tile
    /// specifications for multiple component transformation
    /// 
    /// </summary>
    /// <seealso cref="ModuleSpec" />
    public class CompTransfSpec : ModuleSpec
    {
        /// <summary> Check if component transformation is used in any of the tiles. This
        /// method must not be used by the encoder.
        /// 
        /// </summary>
        /// <returns> True if a component transformation is used in at least on
        /// tile.
        /// 
        /// </returns>
        public virtual bool CompTransfUsed
        {
            get
            {
                if (((int)def) != InvCompTransf.NONE)
                {
                    return true;
                }

                if (tileDef != null)
                {
                    for (var t = nTiles - 1; t >= 0; t--)
                    {
                        var td = tileDef[t];
                        if (td != null && (((int)td) != InvCompTransf.NONE))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }

        }

        /// <summary> Constructs an empty 'CompTransfSpec' with the specified number of tiles
        /// and components. This constructor is called by the decoder. Note: The
        /// number of component is here for symmetry purpose. It is useless since
        /// only tile specifications are meaningful.
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
        public CompTransfSpec(int nt, int nc, byte type) : base(nt, nc, type)
        {
        }
    }
}