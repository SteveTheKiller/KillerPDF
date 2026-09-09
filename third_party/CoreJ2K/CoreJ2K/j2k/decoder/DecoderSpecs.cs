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
using CoreJ2K.j2k.entropy;
using CoreJ2K.j2k.image;
using CoreJ2K.j2k.quantization;
using CoreJ2K.j2k.roi;
using CoreJ2K.j2k.wavelet.synthesis;
using System;

namespace CoreJ2K.j2k.decoder
{

    /// <summary> This class holds references to each module specifications used in the
    /// decoding chain. This avoid big amount of arguments in method calls. A
    /// specification contains values of each tile-component for one module. All
    /// members must be instance of ModuleSpec class (or its children).
    /// 
    /// </summary>
    /// <seealso cref="ModuleSpec" />
    public class DecoderSpecs
    {
        /// <summary> Returns a copy of the current object.
        /// 
        /// </summary>
        public virtual DecoderSpecs Copy
        {
            get
            {
                DecoderSpecs decSpec2;
                try
                {
                    decSpec2 = (DecoderSpecs)Clone();
                }
                catch (Exception)
                {
                    throw new InvalidOperationException("Cannot clone the DecoderSpecs instance");
                }
                // Quantization
                decSpec2.qts = (QuantTypeSpec)qts.Copy;
                decSpec2.qsss = (QuantStepSizeSpec)qsss.Copy;
                decSpec2.gbs = (GuardBitsSpec)gbs.Copy;
                // Wavelet transform
                decSpec2.wfs = (SynWTFilterSpec)wfs.Copy;
                decSpec2.dls = (IntegerSpec)dls.Copy;
                // Component transformation
                decSpec2.cts = (CompTransfSpec)cts.Copy;
                // ROI
                if (rois != null)
                {
                    decSpec2.rois = (MaxShiftSpec)rois.Copy;
                }
                return decSpec2;
            }

        }

        /// <summary>ICC Profiling specifications </summary>
        public ModuleSpec iccs;

        /// <summary>ROI maxshift value specifications </summary>
        public MaxShiftSpec rois;

        /// <summary>Quantization type specifications </summary>
        public QuantTypeSpec qts;

        /// <summary>Quantization normalized base step size specifications </summary>
        public QuantStepSizeSpec qsss;

        /// <summary>Number of guard bits specifications </summary>
        public GuardBitsSpec gbs;

        /// <summary>Analysis wavelet filters specifications </summary>
        public SynWTFilterSpec wfs;

        /// <summary>Number of decomposition levels specifications </summary>
        public IntegerSpec dls;

        /// <summary>Number of layers specifications </summary>
        public IntegerSpec nls;

        /// <summary>Progression order specifications </summary>
        public IntegerSpec pos;

        /// <summary>The Entropy decoder options specifications </summary>
        public ModuleSpec ecopts;

        /// <summary>The component transformation specifications </summary>
        public CompTransfSpec cts;

        /// <summary>The progression changes specifications </summary>
        public ModuleSpec pcs;

        /// <summary>The error resilience specifications concerning the entropy
        /// decoder 
        /// </summary>
        public ModuleSpec ers;

        /// <summary>Precinct partition specifications </summary>
        public PrecinctSizeSpec pss;

        /// <summary>The Start Of Packet (SOP) markers specifications </summary>
        public ModuleSpec sops;

        /// <summary>The End of Packet Headers (EPH) markers specifications </summary>
        public ModuleSpec ephs;

        /// <summary>Code-blocks sizes specification </summary>
        public CBlkSizeSpec cblks;

        /// <summary>Packed packet header specifications </summary>
        public ModuleSpec pphs;

        /// <summary> Initialize all members with the given number of tiles and components.
        /// 
        /// </summary>
        /// <param name="nt">Number of tiles
        /// 
        /// </param>
        /// <param name="nc">Number of components
        /// 
        /// </param>
        public DecoderSpecs(int nt, int nc)
        {
            // Quantization
            qts = new QuantTypeSpec(nt, nc, ModuleSpec.SPEC_TYPE_TILE_COMP);
            qsss = new QuantStepSizeSpec(nt, nc, ModuleSpec.SPEC_TYPE_TILE_COMP);
            gbs = new GuardBitsSpec(nt, nc, ModuleSpec.SPEC_TYPE_TILE_COMP);

            // Wavelet transform
            wfs = new SynWTFilterSpec(nt, nc, ModuleSpec.SPEC_TYPE_TILE_COMP);
            dls = new IntegerSpec(nt, nc, ModuleSpec.SPEC_TYPE_TILE_COMP);

            // Component transformation
            cts = new CompTransfSpec(nt, nc, ModuleSpec.SPEC_TYPE_TILE_COMP);

            // Entropy decoder
            ecopts = new ModuleSpec(nt, nc, ModuleSpec.SPEC_TYPE_TILE_COMP);
            ers = new ModuleSpec(nt, nc, ModuleSpec.SPEC_TYPE_TILE_COMP);
            cblks = new CBlkSizeSpec(nt, nc, ModuleSpec.SPEC_TYPE_TILE_COMP);

            // Precinct partition
            pss = new PrecinctSizeSpec(nt, nc, ModuleSpec.SPEC_TYPE_TILE_COMP, dls);

            // Codestream
            nls = new IntegerSpec(nt, nc, ModuleSpec.SPEC_TYPE_TILE);
            pos = new IntegerSpec(nt, nc, ModuleSpec.SPEC_TYPE_TILE);
            pcs = new ModuleSpec(nt, nc, ModuleSpec.SPEC_TYPE_TILE);
            sops = new ModuleSpec(nt, nc, ModuleSpec.SPEC_TYPE_TILE);
            ephs = new ModuleSpec(nt, nc, ModuleSpec.SPEC_TYPE_TILE);
            pphs = new ModuleSpec(nt, nc, ModuleSpec.SPEC_TYPE_TILE);
            iccs = new ModuleSpec(nt, nc, ModuleSpec.SPEC_TYPE_TILE);
            pphs.SetBoolDefault(false);
        }
        public virtual object Clone()
        {
            return null;
        }
    }
}