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
using System;

namespace CoreJ2K.j2k.wavelet.synthesis
{

    /// <summary> This class represents a subband in a tree structure that describes the
    /// subband decomposition for a wavelet transform, specifically for the
    /// syhthesis side.
    /// 
    /// The element can be either a node or a leaf of the tree. If it is a node
    /// then ther are 4 descendants (LL, HL, LH and HH). If it is a leaf there are
    /// no descendants.
    /// 
    /// The tree is bidirectional. Each element in the tree structure has a
    /// "parent", which is the subband from which the element was obtained by
    /// decomposition. The only exception is the root element which has no parent
    /// (i.e.it's null), for obvious reasons.
    /// 
    /// </summary>
    public class SubbandSyn : Subband
    {
        /// <summary> Returns the parent of this subband. The parent of a subband is the
        /// subband from which this one was obtained by decomposition. The root
        /// element has no parent subband (null).
        /// 
        /// </summary>
        /// <returns> The parent subband, or null for the root one.
        /// 
        /// </returns>
        public override Subband Parent => parent;

        /// <summary> Returns the LL child subband of this subband.
        /// 
        /// </summary>
        /// <returns> The LL child subband, or null if there are no childs.
        /// 
        /// </returns>
        public override Subband LL => subb_LL;

        /// <summary> Returns the HL (horizontal high-pass) child subband of this subband.
        /// 
        /// </summary>
        /// <returns> The HL child subband, or null if there are no childs.
        /// 
        /// </returns>
        public override Subband HL => subb_HL;

        /// <summary> Returns the LH (vertical high-pass) child subband of this subband.
        /// 
        /// </summary>
        /// <returns> The LH child subband, or null if there are no childs.
        /// 
        /// </returns>
        public override Subband LH => subb_LH;

        /// <summary> Returns the HH child subband of this subband.
        /// 
        /// </summary>
        /// <returns> The HH child subband, or null if there are no childs.
        /// 
        /// </returns>
        public override Subband HH => subb_HH;

        /// <summary> This function returns the horizontal wavelet filter relevant to this
        /// subband
        /// 
        /// </summary>
        /// <returns> The horizontal wavelet filter
        /// 
        /// </returns>
        public override WaveletFilter HorWFilter => hFilter;

        /// <summary> This function returns the vertical wavelet filter relevant to this
        /// subband
        /// 
        /// </summary>
        /// <returns> The vertical wavelet filter
        /// 
        /// </returns>
        public override WaveletFilter VerWFilter => hFilter;

        /// <summary>The reference to the parent of this subband. It is null for the root
        /// element. It is null by default.  
        /// </summary>
        private SubbandSyn? parent;

        /// <summary>The reference to the LL subband resulting from the decomposition of
        /// this subband. It is null by default.  
        /// </summary>
        private SubbandSyn? subb_LL;

        /// <summary>The reference to the HL subband (horizontal high-pass) resulting from
        /// the decomposition of this subband. It is null by default.  
        /// </summary>
        private SubbandSyn? subb_HL;

        /// <summary>The reference to the LH subband (vertical high-pass) resulting from
        /// the decomposition of this subband. It is null by default.
        /// 
        /// </summary>
        private SubbandSyn? subb_LH;

        /// <summary>The reference to the HH subband resulting from the decomposition of
        /// this subband. It is null by default.  
        /// </summary>
        private SubbandSyn? subb_HH;

        /// <summary>The horizontal analysis filter used to recompose this subband, from
        /// its childs. This is applicable to "node" elements only. The default
        /// value is null. 
        /// </summary>
        public SynWTFilter? hFilter;

        /// <summary>The vertical analysis filter used to decompose this subband, from its
        /// childs. This is applicable to "node" elements only. The default value
        /// is null. 
        /// </summary>
        public SynWTFilter? vFilter;

        /// <summary>The number of magnitude bits </summary>
        public int magbits = 0;

        /// <summary> Creates a SubbandSyn element with all the default values. The
        /// dimensions are (0,0) and the upper left corner is (0,0).
        /// 
        /// </summary>
        public SubbandSyn()
        {
        }

        /// <summary> Creates the top-level node and the entire subband tree, with the
        /// top-level dimensions, the number of decompositions, and the
        /// decomposition tree as specified.
        /// 
        /// This constructor just calls the same constructor of the super
        /// class.
        /// 
        /// </summary>
        /// <param name="w">The top-level width
        /// 
        /// </param>
        /// <param name="h">The top-level height
        /// 
        /// </param>
        /// <param name="ulcx">The horizontal coordinate of the upper-left corner with
        /// respect to the canvas origin, in the component grid.
        /// 
        /// </param>
        /// <param name="ulcy">The vertical coordinate of the upper-left corner with
        /// respect to the canvas origin, in the component grid.
        /// 
        /// </param>
        /// <param name="lvls">The number of levels (or LL decompositions) in the tree.
        /// 
        /// </param>
        /// <param name="hfilters">The horizontal wavelet synthesis filters for each
        /// resolution level, starting at resolution level 0.
        /// 
        /// </param>
        /// <param name="vfilters">The vertical wavelet synthesis filters for each
        /// resolution level, starting at resolution level 0.
        /// 
        /// </param>
        /// <seealso cref="Subband.Subband(int,int,int,int,int,">
        /// WaveletFilter[],WaveletFilter[])
        /// 
        /// </seealso>
        public SubbandSyn(int w, int h, int ulcx, int ulcy, int lvls, WaveletFilter[] hfilters, WaveletFilter[] vfilters) : base(w, h, ulcx, ulcy, lvls, hfilters, vfilters)
        {
        }

        /// <summary> Splits the current subband in its four subbands. It changes the status
        /// of this element (from a leaf to a node, and sets the filters), creates
        /// the childs and initializes them. An IllegalArgumentException is thrown
        /// if this subband is not a leaf.
        /// 
        /// It uses the initChilds() method to initialize the childs.
        /// 
        /// </summary>
        /// <param name="hfilter">The horizontal wavelet filter used to decompose this
        /// subband. It has to be a SynWTFilter object.
        /// 
        /// </param>
        /// <param name="vfilter">The vertical wavelet filter used to decompose this
        /// subband. It has to be a SynWTFilter object.
        /// 
        /// </param>
        /// <returns> A reference to the LL leaf (subb_LL).
        /// 
        /// </returns>
        /// <seealso cref="Subband.initChilds" />
        protected internal override Subband split(WaveletFilter hfilter, WaveletFilter vfilter)
        {
            // Test that this is a node
            if (isNode)
            {
                throw new ArgumentException();
            }

            // Modify this element into a node and set the filters
            isNode = true;
            hFilter = (SynWTFilter)hfilter;
            vFilter = (SynWTFilter)vfilter;

            // Create childs
            subb_LL = new SubbandSyn();
            subb_LH = new SubbandSyn();
            subb_HL = new SubbandSyn();
            subb_HH = new SubbandSyn();

            // Assign parent
            subb_LL.parent = this;
            subb_HL.parent = this;
            subb_LH.parent = this;
            subb_HH.parent = this;

            // Initialize childs
            initChilds();

            // Return reference to LL subband
            return subb_LL;
        }
    }
}