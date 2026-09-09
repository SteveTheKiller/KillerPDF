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

namespace CoreJ2K.j2k.wavelet.analysis
{

    /// <summary> This class represents a subband in a bidirectional tree structure that
    /// describes the subband decomposition for a wavelet transform, specifically
    /// for the analysis side.
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
    public class SubbandAn : Subband
    {
        /// <summary> Returns the parent of this subband. The parent of a subband is the
        /// subband from which this one was obtained by decomposition. The root
        /// element has no parent subband (null).
        /// 
        /// </summary>
        /// <returns> The parent subband, or null for the root one.
        /// 
        /// </returns>
        public override Subband Parent => parentband;

        /// <summary> Returns the LL child subband of this subband.
        /// 
        /// </summary>
        /// <returns> The LL child subband, or null if there are no childs.
        /// 
        /// </returns>
        public override Subband LL => subb_LL!;

        /// <summary> Returns the HL (horizontal high-pass) child subband of this subband.
        /// 
        /// </summary>
        /// <returns> The HL child subband, or null if there are no childs.
        /// 
        /// </returns>
        public override Subband HL => subb_HL!;

        /// <summary> Returns the LH (vertical high-pass) child subband of this subband.
        /// 
        /// </summary>
        /// <returns> The LH child subband, or null if there are no childs.
        /// 
        /// </returns>
        public override Subband LH => subb_LH!;

        /// <summary> Returns the HH child subband of this subband.
        /// 
        /// </summary>
        /// <returns> The HH child subband, or null if there are no childs.
        /// 
        /// </returns>
        public override Subband HH => subb_HH!;

        /// <summary> This function returns the horizontal wavelet filter relevant to this
        /// subband
        /// 
        /// </summary>
        /// <returns> The horizontal wavelet filter
        /// 
        /// </returns>
        public override WaveletFilter HorWFilter => hFilter!;

        /// <summary> This function returns the vertical wavelet filter relevant to this
        /// subband
        /// 
        /// </summary>
        /// <returns> The vertical wavelet filter
        /// 
        /// </returns>
        public override WaveletFilter VerWFilter => vFilter!;

        /// <summary>The reference to the parent of this subband. It is null for the root
        /// element. It is null by default.  
        /// </summary>
        public SubbandAn? parentband = null;

        /// <summary>The reference to the LL subband resulting from the decomposition of
        /// this subband. It is null by default.  
        /// </summary>
        public SubbandAn? subb_LL;

        /// <summary>The reference to the HL subband (horizontal high-pass) resulting from
        /// the decomposition of this subband. It is null by default.  
        /// </summary>
        public SubbandAn? subb_HL;

        /// <summary>The reference to the LH subband (vertical high-pass) resulting from
        /// the decomposition of this subband. It is null by default.
        /// 
        /// </summary>
        public SubbandAn? subb_LH;

        /// <summary>The reference to the HH subband resulting from the decomposition of
        /// this subband. It is null by default.  
        /// </summary>
        public SubbandAn? subb_HH;

        /// <summary>The horizontal analysis filter used to decompose this subband. This is
        /// applicable to "node" elements only. The default value is null. 
        /// </summary>
        public AnWTFilter? hFilter;

        /// <summary>The vertical analysis filter used to decompose this subband. This is
        /// applicable to "node" elements only. The default value is null. 
        /// </summary>
        public AnWTFilter? vFilter;

        /// <summary>The L2-norm of the synthesis basis waveform of this subband,
        /// applicable to "leafs" only. By default it is -1 (i.e. not calculated
        /// yet).
        /// 
        /// </summary>
        public float l2Norm = -1.0f;

        /// <summary> The contribution to the MSE or WMSE error that would result in the
        /// image if there was an error of exactly one quantization step size in
        /// the sample of the subband. This value is expressed relative to a
        /// nominal dynamic range in the image domain of exactly 1.0. This field
        /// contains valid data only after quantization 9See Quantizer).
        /// 
        /// </summary>
        /// <seealso cref="j2k.quantization.quantizer.Quantizer" />
        public float stepWMSE;

        /// <summary> Creates a SubbandAn element with all the default values. The dimensions
        /// are (0,0) and the upper left corner is (0,0).
        /// 
        /// </summary>
        public SubbandAn()
        {
        }

        /// <summary> Creates the top-level node and the entire subband tree, with the
        /// top-level dimensions, the number of decompositions, and the
        /// decomposition tree as specified.
        /// 
        /// This constructor just calls the same constructor of the super class,
        /// and then calculates the L2-norm (or energy weight) of each leaf.
        /// 
        /// This constructor does not initialize the value of the magBits or
        /// stepWMSE member variables. This variables are normally initialized by
        /// the quantizer (see Quantizer).
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
        /// <param name="hfilters">The horizontal wavelet analysis filters for each
        /// resolution level, starting at resolution level 0.
        /// 
        /// </param>
        /// <param name="vfilters">The vertical wavelet analysis filters for each
        /// resolution level, starting at resolution level 0.
        /// 
        /// </param>
        /// <seealso cref="Subband.Subband(int,int,int,int,int,">
        /// WaveletFilter[],WaveletFilter[])
        /// 
        /// </seealso>
        /// <seealso cref="j2k.quantization.quantizer.Quantizer" />
        public SubbandAn(int w, int h, int ulcx, int ulcy, int lvls, WaveletFilter[] hfilters, WaveletFilter[] vfilters) : base(w, h, ulcx, ulcy, lvls, hfilters, vfilters)
        {
            // Caculate the L2-norms
            calcL2Norms();
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
        /// subband. It has to be a AnWTFilter object.
        /// 
        /// </param>
        /// <param name="vfilter">The vertical wavelet filter used to decompose this
        /// subband. It has to be a AnWTFilter object.
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
            hFilter = (AnWTFilter)hfilter;
            vFilter = (AnWTFilter)vfilter;

            // Create childs
            subb_LL = new SubbandAn();
            subb_LH = new SubbandAn();
            subb_HL = new SubbandAn();
            subb_HH = new SubbandAn();

            // Assign parent
            subb_LL.parentband = this;
            subb_HL.parentband = this;
            subb_LH.parentband = this;
            subb_HH.parentband = this;

            // Initialize childs
            initChilds();

            // Return reference to LL subband
            return subb_LL;
        }

        /// <summary> Calculates the basis waveform of the first leaf for which the L2-norm
        /// has not been calculated yet. This method searches recursively for the
        /// first leaf for which the value has not been calculated yet, and then
        /// calculates the L2-norm on the return path.
        /// 
        /// The wfs argument should be a size 2 array of float arrays (i.e. 2D
        /// array) and it must be of length 2 (or more). When returning, wfs[0]
        /// will contain the line waveform, and wfs[1] will contain the column
        /// waveform.
        /// 
        /// This method can not be called on an element that ahs a non-negative
        /// value in l2Norm, since that means that we are done.
        /// 
        /// </summary>
        /// <param name="wfs">An size 2 array where the line and column waveforms will be
        /// returned.
        /// 
        /// </param>
        private void calcBasisWaveForms(float[]?[] wfs)
        {
            if (l2Norm < 0)
            {
                // We are not finished with this element yet
                if (isNode)
                {
                    // We are on a node => search on childs
                    if (subb_LL!.l2Norm < 0f)
                    {
                        subb_LL.calcBasisWaveForms(wfs);
                        wfs[0] = hFilter!.GetLPSynWaveForm(wfs[0], null);
                        wfs[1] = vFilter!.GetLPSynWaveForm(wfs[1], null);
                    }
                    else if (subb_HL!.l2Norm < 0f)
                    {
                        subb_HL.calcBasisWaveForms(wfs);
                        wfs[0] = hFilter!.GetHPSynWaveForm(wfs[0], null);
                        wfs[1] = vFilter!.GetLPSynWaveForm(wfs[1], null);
                    }
                    else if (subb_LH!.l2Norm < 0f)
                    {
                        subb_LH.calcBasisWaveForms(wfs);
                        wfs[0] = hFilter!.GetLPSynWaveForm(wfs[0], null);
                        wfs[1] = vFilter!.GetHPSynWaveForm(wfs[1], null);
                    }
                    else if (subb_HH!.l2Norm < 0f)
                    {
                        subb_HH.calcBasisWaveForms(wfs);
                        wfs[0] = hFilter!.GetHPSynWaveForm(wfs[0], null);
                        wfs[1] = vFilter!.GetHPSynWaveForm(wfs[1], null);
                    }
                    else
                    {
                        // There is an error! If all childs have non-negative
                        // l2norm, then this node should have non-negative l2norm
                        throw new InvalidOperationException("You have found a bug in JJ2000!");
                    }
                }
                else
                {
                    // This is a leaf, just use diracs (null is equivalent to
                    // dirac)
                    wfs[0] = new float[] { 1.0f };
                    wfs[1] = new float[] { 1.0f };
                }
            }
            else
            {
                // This is an error! The calcBasisWaveForms() method is never
                // called on an element with non-negative l2norm
                throw new InvalidOperationException("You have found a bug in JJ2000!");
            }
        }

        /// <summary> Assigns the given L2-norm to the first leaf that does not have an
        /// L2-norm value yet (i.e. l2norm is negative). The search is done
        /// recursively and in the same order as that of the calcBasisWaveForms()
        /// method, so that this method is used to assigne the l2norm of the
        /// previously computed waveforms.
        /// 
        /// This method can not be called on an element that ahs a non-negative
        /// value in l2Norm, since that means that we are done.
        /// 
        /// </summary>
        /// <param name="l2n">The L2-norm to assign.
        /// 
        /// </param>
        private void assignL2Norm(float l2n)
        {
            if (l2Norm < 0)
            {
                // We are not finished with this element yet
                if (isNode)
                {
                    // We are on a node => search on childs
                    if (subb_LL!.l2Norm < 0f)
                    {
                        subb_LL.assignL2Norm(l2n);
                    }
                    else if (subb_HL!.l2Norm < 0f)
                    {
                        subb_HL.assignL2Norm(l2n);
                    }
                    else if (subb_LH!.l2Norm < 0f)
                    {
                        subb_LH.assignL2Norm(l2n);
                    }
                    else if (subb_HH!.l2Norm < 0f)
                    {
                        subb_HH.assignL2Norm(l2n);
                        // If child now is done, we are done
                        if (subb_HH.l2Norm >= 0f)
                        {
                            l2Norm = 0f; // We are on a node, any non-neg value OK
                        }
                    }
                    else
                    {
                        // There is an error! If all childs have non-negative
                        // l2norm, then this node should have non-negative l2norm
                        throw new InvalidOperationException("You have found a bug in JJ2000!");
                    }
                }
                else
                {
                    // This is a leaf, assign the L2-norm
                    l2Norm = l2n;
                }
            }
            else
            {
                // This is an error! The assignL2Norm() method is never called on
                // an element with non-negative l2norm
                throw new InvalidOperationException("You have found a bug in JJ2000!");
            }
        }


        /// <summary> Calculates the L2-norm of the sythesis waveforms of every leaf in the
        /// tree. This method should only be called on the root element.
        /// 
        /// </summary>
        private void calcL2Norms()
        {
            int i;
            var wfs = new float[]?[2];
            double acc;
            float l2n;

            // While we are not done on the root element, compute basis functions
            // and assign L2-norm
            while (l2Norm < 0f)
            {
                calcBasisWaveForms(wfs);
                // Compute line L2-norm, which is the product of the line
                // and column L2-norms
                acc = 0.0;
                for (i = wfs[0]!.Length - 1; i >= 0; i--)
                {
                    acc += wfs[0]![i] * wfs[0]![i];
                }
                l2n = (float)Math.Sqrt(acc);
                // Compute column L2-norm
                acc = 0.0;
                for (i = wfs[1]!.Length - 1; i >= 0; i--)
                {
                    acc += wfs[1]![i] * wfs[1]![i];
                }
                l2n *= (float)Math.Sqrt(acc);
                // Release waveforms
                wfs[0] = null;
                wfs[1] = null;
                // Assign the value
                assignL2Norm(l2n);
            }
        }
    }
}