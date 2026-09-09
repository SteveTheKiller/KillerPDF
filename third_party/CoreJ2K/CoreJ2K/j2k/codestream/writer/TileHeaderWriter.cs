// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using CoreJ2K.j2k.encoder;
using CoreJ2K.j2k.wavelet.analysis;
using CoreJ2K.j2k.entropy;

namespace CoreJ2K.j2k.codestream.writer
{
    /// <summary>
    /// Manages writing of tile header sections.
    /// </summary>
    internal class TileHeaderWriter
    {
        private readonly EncoderSpecs encSpec;
        private readonly ForwardWT dwt;
        private readonly int nComp;

        public TileHeaderWriter(EncoderSpecs encSpec, ForwardWT dwt, int nComp)
        {
            this.encSpec = encSpec;
            this.dwt = dwt;
            this.nComp = nComp;
        }

        public bool ShouldWriteCOD(int tileIdx, bool isEresUsed)
        {
            var isEresUsedInTile = ((string)encSpec.tts.GetTileDef(tileIdx)).Equals("predict");
            
            return encSpec.wfs.IsTileSpecified(tileIdx) ||
                   encSpec.cts.IsTileSpecified(tileIdx) ||
                   encSpec.dls.IsTileSpecified(tileIdx) ||
                   encSpec.bms.IsTileSpecified(tileIdx) ||
                   encSpec.mqrs.IsTileSpecified(tileIdx) ||
                   encSpec.rts.IsTileSpecified(tileIdx) ||
                   encSpec.css.IsTileSpecified(tileIdx) ||
                   encSpec.pss.IsTileSpecified(tileIdx) ||
                   encSpec.sops.IsTileSpecified(tileIdx) ||
                   encSpec.sss.IsTileSpecified(tileIdx) ||
                   encSpec.pocs.IsTileSpecified(tileIdx) ||
                   encSpec.ephs.IsTileSpecified(tileIdx) ||
                   encSpec.cblks.IsTileSpecified(tileIdx) ||
                   (isEresUsed != isEresUsedInTile);
        }

        public bool ShouldWriteCOC(int tileIdx, int compIdx, bool isEresUsed, bool tileCODwritten)
        {
            var isEresUsedInTileComp = ((string)encSpec.tts.GetTileCompVal(tileIdx, compIdx)).Equals("predict");

            if (encSpec.wfs.IsTileCompSpecified(tileIdx, compIdx) ||
                encSpec.dls.IsTileCompSpecified(tileIdx, compIdx) ||
                encSpec.bms.IsTileCompSpecified(tileIdx, compIdx) ||
                encSpec.mqrs.IsTileCompSpecified(tileIdx, compIdx) ||
                encSpec.rts.IsTileCompSpecified(tileIdx, compIdx) ||
                encSpec.css.IsTileCompSpecified(tileIdx, compIdx) ||
                encSpec.pss.IsTileCompSpecified(tileIdx, compIdx) ||
                encSpec.sss.IsTileCompSpecified(tileIdx, compIdx) ||
                encSpec.cblks.IsTileCompSpecified(tileIdx, compIdx) ||
                (isEresUsedInTileComp != isEresUsed))
            {
                return true;
            }
            
            if (tileCODwritten)
            {
                return encSpec.wfs.IsCompSpecified(compIdx) ||
                       encSpec.dls.IsCompSpecified(compIdx) ||
                       encSpec.bms.IsCompSpecified(compIdx) ||
                       encSpec.mqrs.IsCompSpecified(compIdx) ||
                       encSpec.rts.IsCompSpecified(compIdx) ||
                       encSpec.sss.IsCompSpecified(compIdx) ||
                       encSpec.css.IsCompSpecified(compIdx) ||
                       encSpec.pss.IsCompSpecified(compIdx) ||
                       encSpec.cblks.IsCompSpecified(compIdx) ||
                       (encSpec.tts.IsCompSpecified(compIdx) && 
                        ((string)encSpec.tts.GetCompDef(compIdx)).Equals("predict"));
            }

            return false;
        }

        public bool ShouldWriteQCD(int tileIdx)
        {
            return encSpec.qts.IsTileSpecified(tileIdx) ||
                   encSpec.qsss.IsTileSpecified(tileIdx) ||
                   encSpec.dls.IsTileSpecified(tileIdx) ||
                   encSpec.gbs.IsTileSpecified(tileIdx);
        }

        public bool ShouldWriteQCC(int tileIdx, int compIdx, int deftilenr, bool tileQCDwritten)
        {
            if (dwt.GetNomRangeBits(compIdx) != deftilenr ||
                encSpec.qts.IsTileCompSpecified(tileIdx, compIdx) ||
                encSpec.qsss.IsTileCompSpecified(tileIdx, compIdx) ||
                encSpec.dls.IsTileCompSpecified(tileIdx, compIdx) ||
                encSpec.gbs.IsTileCompSpecified(tileIdx, compIdx))
            {
                return true;
            }
            
            if (tileQCDwritten)
            {
                return encSpec.qts.IsCompSpecified(compIdx) ||
                       encSpec.qsss.IsCompSpecified(compIdx) ||
                       encSpec.dls.IsCompSpecified(compIdx) ||
                       encSpec.gbs.IsCompSpecified(compIdx);
            }

            return false;
        }

        public bool ShouldWritePOC(int tileIdx)
        {
            if (encSpec.pocs.IsTileSpecified(tileIdx))
            {
                var prog = (Progression[])(encSpec.pocs.GetTileDef(tileIdx));
                return prog.Length > 1;
            }
            return false;
        }
    }
}
