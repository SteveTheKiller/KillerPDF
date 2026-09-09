// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using CoreJ2K.j2k.encoder;
using CoreJ2K.j2k.quantization.quantizer;
using CoreJ2K.j2k.wavelet.analysis;
using System;
using System.IO;

namespace CoreJ2K.j2k.codestream.writer.markers
{
    /// <summary>
    /// Writes QCC (Quantization Component) marker segments.
    /// </summary>
    internal class QCCMarkerWriter
    {
        private readonly EncoderSpecs encSpec;
        private readonly ForwardWT dwt;
        private readonly int nComp;

        public QCCMarkerWriter(EncoderSpecs encSpec, ForwardWT dwt, int nComp)
        {
            this.encSpec = encSpec;
            this.dwt = dwt;
            this.nComp = nComp;
        }

        public void WriteMain(BinaryWriter writer, int compIdx)
        {
            var qType = (string)encSpec.qts.GetCompDef(compIdx);
            var baseStep = (float)encSpec.qsss.GetCompDef(compIdx);
            var gb = ((int)encSpec.gbs.GetCompDef(compIdx));

            var IsReversible = qType.Equals("reversible");
            var IsDerived = qType.Equals("derived");

            int mrl = ((int)encSpec.dls.GetCompDef(compIdx));

            // Find representative tile
            var tIdx = FindRepresentativeTile(compIdx, mrl, qType);
            SubbandAn sbRoot = dwt.GetAnSubbandTree(tIdx, compIdx);
            var imgnr = dwt.GetNomRangeBits(compIdx);

            int qstyle = GetQuantizationStyle(IsReversible, IsDerived);

            // QCC marker
            writer.Write(Markers.QCC);

            // Compute number of steps
            int nqcc = ComputeNumberOfSteps(qstyle, sbRoot, ref mrl);

            // Lqcc (marker segment length)
            var markSegLen = 3 + ((nComp < 257) ? 1 : 2) + ((IsReversible) ? nqcc : 2 * nqcc);
            writer.Write((short)markSegLen);

            // Cqcc (component index)
            if (nComp < 257)
            {
                writer.Write((byte)compIdx);
            }
            else
            {
                writer.Write((short)compIdx);
            }

            // Sqcc (quantization style)
            writer.Write((byte)(qstyle + (gb << Markers.SQCX_GB_SHIFT)));

            // SPqcc
            WriteQuantizationSteps(writer, qstyle, sbRoot, mrl, imgnr, baseStep);
        }

        public void WriteTile(BinaryWriter writer, int tileIdx, int compIdx)
        {
            var sbRoot = dwt.GetAnSubbandTree(tileIdx, compIdx);
            var imgnr = dwt.GetNomRangeBits(compIdx);
            var qType = (string)encSpec.qts.GetTileCompVal(tileIdx, compIdx);
            var baseStep = (float)encSpec.qsss.GetTileCompVal(tileIdx, compIdx);
            var gb = ((int)encSpec.gbs.GetTileCompVal(tileIdx, compIdx));

            var IsReversible = qType.Equals("reversible");
            var IsDerived = qType.Equals("derived");

            int mrl = ((int)encSpec.dls.GetTileCompVal(tileIdx, compIdx));

            int qstyle = GetQuantizationStyle(IsReversible, IsDerived);

            // QCC marker
            writer.Write(Markers.QCC);

            // Compute number of steps
            int nqcc = ComputeNumberOfSteps(qstyle, sbRoot, ref mrl);

            // Lqcc
            var markSegLen = 3 + ((nComp < 257) ? 1 : 2) + ((IsReversible) ? nqcc : 2 * nqcc);
            writer.Write((short)markSegLen);

            // Cqcc
            if (nComp < 257)
            {
                writer.Write((byte)compIdx);
            }
            else
            {
                writer.Write((short)compIdx);
            }

            // Sqcc
            writer.Write((byte)(qstyle + (gb << Markers.SQCX_GB_SHIFT)));

            // SPqcc
            WriteQuantizationSteps(writer, qstyle, sbRoot, mrl, imgnr, baseStep);
        }

        private int FindRepresentativeTile(int compIdx, int mrl, string qType)
        {
            var nt = dwt.GetNumTiles();
            var nc = dwt.NumComps;

            for (var t = 0; t < nt; t++)
            {
                for (var c = 0; c < nc; c++)
                {
                    int tmpI = ((int)encSpec.dls.GetTileCompVal(t, c));
                    string tmpStr = ((string)encSpec.qts.GetTileCompVal(t, c));
                    if (tmpI == mrl && tmpStr.Equals(qType))
                    {
                        return t;
                    }
                }
            }

            throw new InvalidOperationException(
                $"Default representative for quantization type and number of decomposition levels not found " +
                $"in main QCC (c={compIdx}) marker segment. You have found a JJ2000 bug.");
        }

        private int GetQuantizationStyle(bool IsReversible, bool IsDerived)
        {
            if (IsReversible)
                return Markers.SQCX_NO_QUANTIZATION;
            if (IsDerived)
                return Markers.SQCX_SCALAR_DERIVED;
            return Markers.SQCX_SCALAR_EXPOUNDED;
        }

        private int ComputeNumberOfSteps(int qstyle, SubbandAn sbRoot, ref int mrl)
        {
            switch (qstyle)
            {
                case Markers.SQCX_SCALAR_DERIVED:
                    return 1;

                case Markers.SQCX_NO_QUANTIZATION:
                case Markers.SQCX_SCALAR_EXPOUNDED:
                    int nqcc = 0;
                    SubbandAn sb = sbRoot;
                    mrl = sb.resLvl;

                    sb = (SubbandAn)sb.GetSubbandByIdx(0, 0);

                    // Find root element for LL subband
                    while (sb.resLvl != 0)
                    {
                        sb = sb.subb_LL;
                    }

                    for (var j = 0; j <= mrl; j++)
                    {
                        SubbandAn sb2 = sb;
                        while (sb2 != null)
                        {
                            nqcc++;
                            sb2 = (SubbandAn)sb2.nextSubband();
                        }
                        sb = (SubbandAn)sb.NextResLevel;
                    }
                    return nqcc;

                default:
                    throw new InvalidOperationException("Internal JJ2000 error");
            }
        }

        private void WriteQuantizationSteps(BinaryWriter writer, int qstyle, SubbandAn sbRoot,
                                           int mrl, int nomRangeBits, float baseStep)
        {
            SubbandAn sb = sbRoot;
            sb = (SubbandAn)sb.GetSubbandByIdx(0, 0);

            switch (qstyle)
            {
                case Markers.SQCX_NO_QUANTIZATION:
                    for (var j = 0; j <= mrl; j++)
                    {
                        SubbandAn sb2 = sb;
                        while (sb2 != null)
                        {
                            var tmp = (nomRangeBits + sb2.anGainExp);
                            writer.Write((byte)(tmp << Markers.SQCX_EXP_SHIFT));
                            sb2 = (SubbandAn)sb2.nextSubband();
                        }
                        sb = (SubbandAn)sb.NextResLevel;
                    }
                    break;

                case Markers.SQCX_SCALAR_DERIVED:
                    float step = baseStep / (1 << sb.level);
                    writer.Write((short)StdQuantizer.convertToExpMantissa(step));
                    break;

                case Markers.SQCX_SCALAR_EXPOUNDED:
                    for (var j = 0; j <= mrl; j++)
                    {
                        SubbandAn sb2 = sb;
                        while (sb2 != null)
                        {
                            float s = baseStep / (sb2.l2Norm * (1 << sb2.anGainExp));
                            writer.Write((short)StdQuantizer.convertToExpMantissa(s));
                            sb2 = (SubbandAn)sb2.nextSubband();
                        }
                        sb = (SubbandAn)sb.NextResLevel;
                    }
                    break;

                default:
                    throw new InvalidOperationException("Internal JJ2000 error");
            }
        }
    }
}
