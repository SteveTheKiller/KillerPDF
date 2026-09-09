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
    /// Writes QCD (Quantization Default) marker segments.
    /// </summary>
    internal class QCDMarkerWriter
    {
        private readonly EncoderSpecs encSpec;
        private readonly ForwardWT dwt;

        public QCDMarkerWriter(EncoderSpecs encSpec, ForwardWT dwt)
        {
            this.encSpec = encSpec;
            this.dwt = dwt;
        }

        public int WriteMain(BinaryWriter writer)
        {
            var qType = (string)encSpec.qts.GetDefault();
            var baseStep = (float)encSpec.qsss.GetDefault();
            var gb = ((int)encSpec.gbs.GetDefault());

            var IsDerived = qType.Equals("derived");
            var IsReversible = qType.Equals("reversible");

            int mrl = ((int)encSpec.dls.GetDefault());

            // Find representative tile/component
            var tcIdx = FindRepresentativeTileComponent(mrl, qType);
            SubbandAn sbRoot = dwt.GetAnSubbandTree(tcIdx[0], tcIdx[1]);
            int defimgn = dwt.GetNomRangeBits(tcIdx[1]);

            // Get quantization style
            int qstyle = GetQuantizationStyle(IsReversible, IsDerived);

            // QCD marker
            writer.Write(Markers.QCD);

            // Compute number of steps
            int nqcd = ComputeNumberOfSteps(qstyle, sbRoot, mrl);

            // Lqcd (marker segment length)
            var markSegLen = 3 + ((IsReversible) ? nqcd : 2 * nqcd);
            writer.Write((short)markSegLen);

            // Sqcd
            writer.Write((byte)(qstyle + (gb << Markers.SQCX_GB_SHIFT)));

            // SPqcd
            WriteQuantizationSteps(writer, qstyle, sbRoot, mrl, defimgn, baseStep);

            return defimgn;
        }

        public int WriteTile(BinaryWriter writer, int tileIdx, int deftilenr)
        {
            var qType = (string)encSpec.qts.GetTileDef(tileIdx);
            var baseStep = (float)encSpec.qsss.GetTileDef(tileIdx);
            int mrl = ((int)encSpec.dls.GetTileDef(tileIdx));

            var compIdx = FindRepresentativeComponent(tileIdx, mrl, qType);
            SubbandAn sbRoot = dwt.GetAnSubbandTree(tileIdx, compIdx);
            deftilenr = dwt.GetNomRangeBits(compIdx);
            var gb = ((int)encSpec.gbs.GetTileDef(tileIdx));

            var IsDerived = qType.Equals("derived");
            var IsReversible = qType.Equals("reversible");

            int qstyle = GetQuantizationStyle(IsReversible, IsDerived);

            // QCD marker
            writer.Write(Markers.QCD);

            // Compute number of steps
            int nqcd = ComputeNumberOfSteps(qstyle, sbRoot, mrl);

            // Lqcd
            var markSegLen = 3 + ((IsReversible) ? nqcd : 2 * nqcd);
            writer.Write((short)markSegLen);

            // Sqcd
            writer.Write((byte)(qstyle + (gb << Markers.SQCX_GB_SHIFT)));

            // SPqcd
            WriteQuantizationSteps(writer, qstyle, sbRoot, mrl, deftilenr, baseStep);
            
            return deftilenr;
        }

        private int[] FindRepresentativeTileComponent(int mrl, string qType)
        {
            var nt = dwt.GetNumTiles();
            var nc = dwt.NumComps;
            var tcIdx = new int[2];

            for (var t = 0; t < nt; t++)
            {
                for (var c = 0; c < nc; c++)
                {
                    int tmpI = ((int)encSpec.dls.GetTileCompVal(t, c));
                    string tmpStr = ((string)encSpec.qts.GetTileCompVal(t, c));
                    if (tmpI == mrl && tmpStr.Equals(qType))
                    {
                        tcIdx[0] = t; 
                        tcIdx[1] = c;
                        return tcIdx;
                    }
                }
            }

            throw new InvalidOperationException(
                "Default representative for quantization type and number of decomposition levels not found " +
                "in main QCD marker segment. You have found a JJ2000 bug.");
        }

        private int FindRepresentativeComponent(int tileIdx, int mrl, string qType)
        {
            var nc = dwt.NumComps;

            for (var c = 0; c < nc; c++)
            {
                int tmpI = ((int)encSpec.dls.GetTileCompVal(tileIdx, c));
                string tmpStr = ((string)encSpec.qts.GetTileCompVal(tileIdx, c));
                if (tmpI == mrl && tmpStr.Equals(qType))
                {
                    return c;
                }
            }

            throw new InvalidOperationException(
                $"Default representative for quantization type and number of decomposition levels not found " +
                $"in tile QCD (t={tileIdx}) marker segment. You have found a JJ2000 bug.");
        }

        private int GetQuantizationStyle(bool IsReversible, bool IsDerived)
        {
            if (IsReversible)
                return Markers.SQCX_NO_QUANTIZATION;
            if (IsDerived)
                return Markers.SQCX_SCALAR_DERIVED;
            return Markers.SQCX_SCALAR_EXPOUNDED;
        }

        private int ComputeNumberOfSteps(int qstyle, SubbandAn sbRoot, int mrl)
        {
            switch (qstyle)
            {
                case Markers.SQCX_SCALAR_DERIVED:
                    return 1;

                case Markers.SQCX_NO_QUANTIZATION:
                case Markers.SQCX_SCALAR_EXPOUNDED:
                    int nqcd = 0;
                    SubbandAn sb = (SubbandAn)sbRoot.GetSubbandByIdx(0, 0);

                    for (var j = 0; j <= mrl; j++)
                    {
                        SubbandAn csb = sb;
                        while (csb != null)
                        {
                            nqcd++;
                            csb = (SubbandAn)csb.nextSubband();
                        }
                        sb = (SubbandAn)sb.NextResLevel;
                    }
                    return nqcd;

                default:
                    throw new InvalidOperationException("Internal JJ2000 error");
            }
        }

        private void WriteQuantizationSteps(BinaryWriter writer, int qstyle, SubbandAn sbRoot, 
                                           int mrl, int nomRangeBits, float baseStep)
        {
            SubbandAn sb = (SubbandAn)sbRoot.GetSubbandByIdx(0, 0);

            switch (qstyle)
            {
                case Markers.SQCX_NO_QUANTIZATION:
                    for (var j = 0; j <= mrl; j++)
                    {
                        SubbandAn csb = sb;
                        while (csb != null)
                        {
                            var tmp = (nomRangeBits + csb.anGainExp);
                            writer.Write((byte)(tmp << Markers.SQCX_EXP_SHIFT));
                            csb = (SubbandAn)csb.nextSubband();
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
                        SubbandAn csb = sb;
                        while (csb != null)
                        {
                            float subbandStep = baseStep / (csb.l2Norm * (1 << csb.anGainExp));
                            writer.Write((short)StdQuantizer.convertToExpMantissa(subbandStep));
                            csb = (SubbandAn)csb.nextSubband();
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
