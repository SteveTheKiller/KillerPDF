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

namespace CoreJ2K.j2k.codestream
{

    /// <summary> This interface defines the values of the different markers in the JPEG 2000
    /// codestream. There are 16 bit values, always appearing in big-endian (most
    /// significant byte first) and byte-aligned in the codestream. This interface
    /// also defines some other constants such as bit-masks and bit-shifts.
    /// 
    /// </summary>
    public struct Markers
    {
        /// <summary>Start of codestream (SOC): 0xFF4F </summary>
        public const short SOC = unchecked((short)0xff4f);
        /// <summary>Start of tile-part (SOT): 0xFF90 </summary>
        public const short SOT = unchecked((short)0xff90);
        /// <summary>Start of data (SOD): 0xFF93 </summary>
        public const short SOD = unchecked((short)0xff93);
        /// <summary>End of codestream (EOC): 0xFFD9 </summary>
        public const short EOC = unchecked((short)0xffd9);
        /// <summary>SIZ marker (Image and tile size): 0xFF51 </summary>
        public const short SIZ = unchecked((short)0xff51);
        /// <summary>No special capabilities (baseline) in codestream, in Rsiz field of SIZ
        /// marker: 0x00. All flag bits are turned off 
        /// </summary>
        public const int RSIZ_BASELINE = 0x00;
        /// <summary>Error resilience marker flag bit in Rsiz field in SIZ marker: 0x01 </summary>
        public const int RSIZ_ER_FLAG = 0x01;
        /// <summary>ROI present marker flag bit in Rsiz field in SIZ marker: 0x02 </summary>
        public const int RSIZ_ROI = 0x02;
        /// <summary>Extended-capabilities flag bit (MSB) in the Rsiz field in the SIZ marker
        /// (ISO/IEC 15444-2): 0x8000. When set, the codestream uses JPEG 2000 Part 2
        /// extensions whose capabilities are enumerated in the CAP marker segment.
        /// </summary>
        public const int RSIZ_EXTENSIONS = 0x8000;
        /// <summary>Extended capabilities marker (CAP): 0xFF50 (ISO/IEC 15444-2 / 15444-1 Amd).
        /// Signals which Part 2 capabilities are required to decode the codestream.
        /// </summary>
        public const short CAP = unchecked((short)0xff50);
        /// <summary>Variable DC offset marker (DCO): 0xFF70 (ISO/IEC 15444-2).
        /// Defines per-component integer DC offsets applied to image samples before the
        /// forward transform (removed on encode, restored on decode).
        /// </summary>
        public const short DCO = unchecked((short)0xff70);
        /// <summary>Non-linearity point transformation marker (NLT): 0xFF76 (ISO/IEC 15444-2).
        /// Defines a per-component point transformation applied to image samples.
        /// </summary>
        public const short NLT = unchecked((short)0xff76);
        /// <summary>Multiple component transformation definition marker (MCT): 0xFF74 (ISO/IEC 15444-2).
        /// Defines a reusable transform array (decorrelation matrix or offset vector).
        /// </summary>
        public const short MCT = unchecked((short)0xff74);
        /// <summary>Multiple component collection marker (MCC): 0xFF75 (ISO/IEC 15444-2).
        /// Defines a component collection and the transform applied to it.
        /// </summary>
        public const short MCC = unchecked((short)0xff75);
        /// <summary>Multiple component transformation ordering marker (MCO): 0xFF77 (ISO/IEC 15444-2).
        /// Specifies the order in which the component collection transforms are applied.
        /// </summary>
        public const short MCO = unchecked((short)0xff77);
        /// <summary>Component bit depth definition marker (CBD): 0xFF78 (ISO/IEC 15444-2).
        /// Defines the bit depths of the (intermediate) components produced by a
        /// multiple component transformation.
        /// </summary>
        public const short CBD = unchecked((short)0xff78);
        /// <summary>Arbitrary transformation kernel marker (ATK): 0xFF79 (ISO/IEC 15444-2).
        /// Defines a custom lifting-based wavelet kernel that COD/COC transformation
        /// bytes can reference by index (values 2..127).
        /// </summary>
        public const short ATK = unchecked((short)0xff79);
        /// <summary>Component bitdepth bits in Ssiz field in SIZ marker: 7 </summary>
        public const int SSIZ_DEPTH_BITS = 7;
        /// <summary>The maximum number of component bitdepth </summary>
        public const int MAX_COMP_BITDEPTH = 38;
        /// <summary>Coding style default (COD): 0xFF52 </summary>
        public const short COD = unchecked((short)0xff52);
        /// <summary>Coding style component (COC): 0xFF53 </summary>
        public const short COC = unchecked((short)0xff53);
        /// <summary>Precinct used flag </summary>
        public const int SCOX_PRECINCT_PARTITION = 1;
        /// <summary>Use start of packet marker </summary>
        public const int SCOX_USE_SOP = 2;
        /// <summary>Use end of packet header marker </summary>
        public const int SCOX_USE_EPH = 4;
        /// <summary>Horizontal code-block partition origin is at x=1 </summary>
        public const int SCOX_HOR_CB_PART = 8;
        /// <summary>Vertical code-block partition origin is at y=1 </summary>
        public const int SCOX_VER_CB_PART = 16;
        /// <summary>The default size exponent of the precincts </summary>
        public const int PRECINCT_PARTITION_DEF_SIZE = 0xffff;
        // ** RGN marker segment **
        /// <summary>Region-of-interest (RGN): 0xFF5E </summary>
        public const short RGN = unchecked((short)0xff5e);
        /// <summary>Implicit (i.e. max-shift) ROI flag for Srgn field in RGN marker
        /// segment: 0x00 
        /// </summary>
        public const int SRGN_IMPLICIT = 0x00;
        /// <summary>Quantization default (QCD): 0xFF5C </summary>
        public const short QCD = unchecked((short)0xff5c);
        /// <summary>Quantization component (QCC): 0xFF5D </summary>
        public const short QCC = unchecked((short)0xff5d);
        /// <summary>Guard bits shift in SQCX field: 5 </summary>
        public const int SQCX_GB_SHIFT = 5;
        /// <summary>Guard bits mask in SQCX field: 7 </summary>
        public const int SQCX_GB_MSK = 7;
        /// <summary>No quantization (i.e. embedded reversible) flag for Sqcd or Sqcc
        /// (Sqcx) fields: 0x00. 
        /// </summary>
        public const int SQCX_NO_QUANTIZATION = 0x00;
        /// <summary>Scalar derived (i.e. LL values only) quantization flag for Sqcd or
        /// Sqcc (Sqcx) fields: 0x01. 
        /// </summary>
        public const int SQCX_SCALAR_DERIVED = 0x01;
        /// <summary>Scalar expounded (i.e. all values) quantization flag for Sqcd or Sqcc
        /// (Sqcx) fields: 0x02. 
        /// </summary>
        public const int SQCX_SCALAR_EXPOUNDED = 0x02;
        /// <summary>Exponent shift in SPQCX when no quantization: 3 </summary>
        public const int SQCX_EXP_SHIFT = 3;
        /// <summary>Exponent bitmask in SPQCX when no quantization: 3 </summary>
        public const int SQCX_EXP_MASK = (1 << 5) - 1;
        /// <summary>The "SOP marker segments used" flag within Sers: 1 </summary>
        public const int ERS_SOP = 1;
        /// <summary>The "segmentation symbols used" flag within Sers: 2 </summary>
        public const int ERS_SEG_SYMBOLS = 2;
        // ** Progression order change **
        public const short POC = unchecked((short)0xff5f);
        /// <summary>Tile-part lengths (TLM): 0xFF55 </summary>
        public const short TLM = unchecked((short)0xff55);
        /// <summary>Packet length, main header (PLM): 0xFF57 </summary>
        public const short PLM = unchecked((short)0xff57);
        /// <summary>Packet length, tile-part header (PLT): 0xFF58 </summary>
        public const short PLT = unchecked((short)0xff58);
        /// <summary>Packed packet headers, main header (PPM): 0xFF60 </summary>
        public const short PPM = unchecked((short)0xff60);
        /// <summary>Packed packet headers, tile-part header (PPT): 0xFF61 </summary>
        public const short PPT = unchecked((short)0xff61);
        /// <summary>Maximum length of PPT marker segment </summary>
        public const int MAX_LPPT = 65535;
        /// <summary>Maximum length of PPM marker segment </summary>
        public const int MAX_LPPM = 65535;
        /// <summary>Start pf packet (SOP): 0xFF91 </summary>
        public const short SOP = unchecked((short)0xff91);
        /// <summary>Length of SOP marker (in bytes) </summary>
        public const short SOP_LENGTH = 6;
        /// <summary>End of packet header (EPH): 0xFF92 </summary>
        public const short EPH = unchecked((short)0xff92);
        /// <summary>Length of EPH marker (in bytes) </summary>
        public const short EPH_LENGTH = 2;
        /// <summary>Component registration (CRG): 0xFF63 </summary>
        public const short CRG = unchecked((short)0xff63);
        /// <summary>Comment (COM): 0xFF64 </summary>
        public const short COM = unchecked((short)0xff64);
        /// <summary>General use registration value (COM): 0x0001 </summary>
        public const short RCOM_GEN_USE = unchecked(0x0001);
    }
}