// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using CoreJ2K.j2k.encoder;
using CoreJ2K.j2k.entropy;
using System.IO;

namespace CoreJ2K.j2k.codestream.writer.markers
{
    /// <summary>
    /// Writes POC (Progression Order Change) marker segments.
    /// </summary>
    internal class POCMarkerWriter
    {
        private readonly EncoderSpecs encSpec;
        private readonly int nComp;

        public POCMarkerWriter(EncoderSpecs encSpec, int nComp)
        {
            this.encSpec = encSpec;
            this.nComp = nComp;
        }

        public void Write(BinaryWriter writer, bool isMainHeader, int tileIdx)
        {
            Progression[] prog = isMainHeader ?
                (Progression[])(encSpec.pocs.GetDefault()) :
                (Progression[])(encSpec.pocs.GetTileDef(tileIdx));

            // Calculate component field length
            int lenCompField = (nComp < 257 ? 1 : 2);

            // POC marker
            writer.Write(Markers.POC);

            // Lpoc (marker segment length)
            int npoc = prog.Length;
            var markSegLen = 2 + npoc * (1 + lenCompField + 2 + 1 + lenCompField + 1);
            writer.Write((short)markSegLen);

            // Write each progression order change
            for (var i = 0; i < npoc; i++)
            {
                // RSpoc(i) - Resolution level start
                writer.Write((byte)prog[i].rs);

                // CSpoc(i) - Component start
                if (lenCompField == 2)
                {
                    writer.Write((short)prog[i].cs);
                }
                else
                {
                    writer.Write((byte)prog[i].cs);
                }

                // LYEpoc(i) - Layer end
                writer.Write((short)prog[i].lye);

                // REpoc(i) - Resolution level end
                writer.Write((byte)prog[i].re);

                // CEpoc(i) - Component end
                if (lenCompField == 2)
                {
                    writer.Write((short)prog[i].ce);
                }
                else
                {
                    writer.Write((byte)prog[i].ce);
                }

                // Ppoc(i) - Progression order
                writer.Write((byte)prog[i].type);
            }
        }
    }
}
