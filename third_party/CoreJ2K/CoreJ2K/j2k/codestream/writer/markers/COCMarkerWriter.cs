// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using CoreJ2K.j2k.encoder;
using CoreJ2K.j2k.entropy;
using CoreJ2K.j2k.util;
using CoreJ2K.j2k.wavelet.analysis;
using System;
using System.IO;

namespace CoreJ2K.j2k.codestream.writer.markers
{
    /// <summary>
    /// Writes COC (Coding style Component) marker segments.
    /// </summary>
    internal class COCMarkerWriter
    {
        private readonly EncoderSpecs encSpec;
        private readonly ForwardWT dwt;
        private readonly int nComp;

        public COCMarkerWriter(EncoderSpecs encSpec, ForwardWT dwt, int nComp)
        {
            this.encSpec = encSpec;
            this.dwt = dwt;
            this.nComp = nComp;
        }

        public void Write(BinaryWriter writer, bool isMainHeader, int tileIdx, int compIdx)
        {
            AnWTFilter[][] filt;
            bool precinctPartitionUsed;
            int tmp;
            int mrl = 0, a = 0;
            int ppx = 0, ppy = 0;

            if (isMainHeader)
            {
                mrl = ((int)encSpec.dls.GetCompDef(compIdx));
                ppx = encSpec.pss.GetPPX(-1, compIdx, mrl);
                ppy = encSpec.pss.GetPPY(-1, compIdx, mrl);
            }
            else
            {
                mrl = ((int)encSpec.dls.GetTileCompVal(tileIdx, compIdx));
                ppx = encSpec.pss.GetPPX(tileIdx, compIdx, mrl);
                ppy = encSpec.pss.GetPPY(tileIdx, compIdx, mrl);
            }

            precinctPartitionUsed = (ppx != Markers.PRECINCT_PARTITION_DEF_SIZE || 
                                    ppy != Markers.PRECINCT_PARTITION_DEF_SIZE);

            if (precinctPartitionUsed)
            {
                a = mrl + 1;
            }

            // COC marker
            writer.Write(Markers.COC);

            // Lcoc (marker segment length)
            var markSegLen = 8 + ((nComp < 257) ? 1 : 2) + a;
            writer.Write((short)markSegLen);

            // Ccoc (component index)
            if (nComp < 257)
            {
                writer.Write((byte)compIdx);
            }
            else
            {
                writer.Write((short)compIdx);
            }

            // Scod (coding style parameter)
            tmp = 0;
            if (precinctPartitionUsed)
            {
                tmp = Markers.SCOX_PRECINCT_PARTITION;
            }
            writer.Write((byte)tmp);

            // SPcoc - Number of decomposition levels
            writer.Write((byte)mrl);

            // Code-block width and height
            if (isMainHeader)
            {
                tmp = encSpec.cblks.GetCBlkWidth(ModuleSpec.SPEC_COMP_DEF, -1, compIdx);
                writer.Write((byte)(MathUtil.log2(tmp) - 2));
                tmp = encSpec.cblks.GetCBlkHeight(ModuleSpec.SPEC_COMP_DEF, -1, compIdx);
                writer.Write((byte)(MathUtil.log2(tmp) - 2));
            }
            else
            {
                tmp = encSpec.cblks.GetCBlkWidth(ModuleSpec.SPEC_TILE_COMP, tileIdx, compIdx);
                writer.Write((byte)(MathUtil.log2(tmp) - 2));
                tmp = encSpec.cblks.GetCBlkHeight(ModuleSpec.SPEC_TILE_COMP, tileIdx, compIdx);
                writer.Write((byte)(MathUtil.log2(tmp) - 2));
            }

            // Entropy coding mode options
            tmp = GetCodeBlockStyle(isMainHeader, tileIdx, compIdx);
            writer.Write((byte)tmp);

            // Wavelet filter
            if (isMainHeader)
            {
                filt = ((AnWTFilter[][])encSpec.wfs.GetCompDef(compIdx));
                writer.Write((byte)filt[0][0].FilterType);
            }
            else
            {
                filt = ((AnWTFilter[][])encSpec.wfs.GetTileCompVal(tileIdx, compIdx));
                writer.Write((byte)filt[0][0].FilterType);
            }

            // Precinct partition
            if (precinctPartitionUsed)
            {
                WritePrecinctPartition(writer, isMainHeader, tileIdx, compIdx, mrl);
            }
        }

        private int GetCodeBlockStyle(bool isMainHeader, int tileIdx, int compIdx)
        {
            int tmp = 0;

            if (isMainHeader)
            {
                if (((string)encSpec.bms.GetCompDef(compIdx)).Equals("on"))
                    tmp |= StdEntropyCoderOptions.OPT_BYPASS;
                if (string.Equals((string)encSpec.mqrs.GetCompDef(compIdx), "ON", StringComparison.OrdinalIgnoreCase))
                    tmp |= StdEntropyCoderOptions.OPT_RESET_MQ;
                if (((string)encSpec.rts.GetCompDef(compIdx)).Equals("on"))
                    tmp |= StdEntropyCoderOptions.OPT_TERM_PASS;
                if (((string)encSpec.css.GetCompDef(compIdx)).Equals("on"))
                    tmp |= StdEntropyCoderOptions.OPT_VERT_STR_CAUSAL;
                if (((string)encSpec.tts.GetCompDef(compIdx)).Equals("predict"))
                    tmp |= StdEntropyCoderOptions.OPT_PRED_TERM;
                if (((string)encSpec.sss.GetCompDef(compIdx)).Equals("on"))
                    tmp |= StdEntropyCoderOptions.OPT_SEG_SYMBOLS;
            }
            else
            {
                if (((string)encSpec.bms.GetTileCompVal(tileIdx, compIdx)).Equals("on"))
                    tmp |= StdEntropyCoderOptions.OPT_BYPASS;
                if (((string)encSpec.mqrs.GetTileCompVal(tileIdx, compIdx)).Equals("on"))
                    tmp |= StdEntropyCoderOptions.OPT_RESET_MQ;
                if (((string)encSpec.rts.GetTileCompVal(tileIdx, compIdx)).Equals("on"))
                    tmp |= StdEntropyCoderOptions.OPT_TERM_PASS;
                if (((string)encSpec.css.GetTileCompVal(tileIdx, compIdx)).Equals("on"))
                    tmp |= StdEntropyCoderOptions.OPT_VERT_STR_CAUSAL;
                if (((string)encSpec.tts.GetTileCompVal(tileIdx, compIdx)).Equals("predict"))
                    tmp |= StdEntropyCoderOptions.OPT_PRED_TERM;
                if (((string)encSpec.sss.GetTileCompVal(tileIdx, compIdx)).Equals("on"))
                    tmp |= StdEntropyCoderOptions.OPT_SEG_SYMBOLS;
            }

            return tmp;
        }

        private void WritePrecinctPartition(BinaryWriter writer, bool isMainHeader, int tileIdx, int compIdx, int mrl)
        {
            System.Collections.Generic.List<int>[] v = isMainHeader ?
                (System.Collections.Generic.List<int>[])encSpec.pss.GetCompDef(compIdx) :
                (System.Collections.Generic.List<int>[])encSpec.pss.GetTileCompVal(tileIdx, compIdx);

            for (var r = mrl; r >= 0; r--)
            {
                int tmp = r >= v[1].Count ? v[1][v[1].Count - 1] : v[1][r];
                var yExp = (MathUtil.log2(tmp) << 4) & 0x00F0;

                tmp = r >= v[0].Count ? v[0][v[0].Count - 1] : v[0][r];
                var xExp = MathUtil.log2(tmp) & 0x000F;
                writer.Write((byte)(yExp | xExp));
            }
        }
    }
}
