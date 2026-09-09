// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using CoreJ2K.j2k.encoder;
using CoreJ2K.j2k.wavelet.analysis;
using CoreJ2K.j2k.entropy;

namespace CoreJ2K.j2k.codestream.writer
{
    /// <summary>
    /// Manages writing of main header sections.
    /// </summary>
    internal class MainHeaderWriter
    {
        private readonly EncoderSpecs encSpec;
        private readonly ForwardWT dwt;
        private readonly int nComp;

        public MainHeaderWriter(EncoderSpecs encSpec, ForwardWT dwt, int nComp)
        {
            this.encSpec = encSpec;
            this.dwt = dwt;
            this.nComp = nComp;
        }

        public bool ShouldWriteCOC(int compIdx, bool isEresUsed)
        {
            var isEresUsedinComp = ((string)encSpec.tts.GetCompDef(compIdx)).Equals("predict");
            
            return encSpec.wfs.IsCompSpecified(compIdx) ||
                   encSpec.dls.IsCompSpecified(compIdx) ||
                   encSpec.bms.IsCompSpecified(compIdx) ||
                   encSpec.mqrs.IsCompSpecified(compIdx) ||
                   encSpec.rts.IsCompSpecified(compIdx) ||
                   encSpec.sss.IsCompSpecified(compIdx) ||
                   encSpec.css.IsCompSpecified(compIdx) ||
                   encSpec.pss.IsCompSpecified(compIdx) ||
                   encSpec.cblks.IsCompSpecified(compIdx) ||
                   (isEresUsed != isEresUsedinComp);
        }

        public bool ShouldWriteQCC(int compIdx, int defimgn)
        {
            return dwt.GetNomRangeBits(compIdx) != defimgn ||
                   encSpec.qts.IsCompSpecified(compIdx) ||
                   encSpec.qsss.IsCompSpecified(compIdx) ||
                   encSpec.dls.IsCompSpecified(compIdx) ||
                   encSpec.gbs.IsCompSpecified(compIdx);
        }

        public bool ShouldWritePOC()
        {
            var prog = (Progression[])(encSpec.pocs.GetDefault());
            return prog.Length > 1;
        }
    }
}
