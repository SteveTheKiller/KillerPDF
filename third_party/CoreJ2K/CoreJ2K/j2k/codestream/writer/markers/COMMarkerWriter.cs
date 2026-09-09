// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using CoreJ2K.Util;
using System.IO;
using System.Text;

namespace CoreJ2K.j2k.codestream.writer.markers
{
    /// <summary>
    /// Writes COM (Comment) marker segments.
    /// </summary>
    internal class COMMarkerWriter
    {
        private readonly bool enJJ2KMarkSeg;
        private readonly string otherCOMMarkSeg;

        public COMMarkerWriter(bool enJJ2KMarkSeg, string otherCOMMarkSeg)
        {
            this.enJJ2KMarkSeg = enJJ2KMarkSeg;
            this.otherCOMMarkSeg = otherCOMMarkSeg;
        }

        public void Write(BinaryWriter writer)
        {
            // JJ2000 COM marker segment
            if (enJJ2KMarkSeg)
            {
                WriteComment(writer, $"Created by: CoreJ2K version {JJ2KInfo.version}");
            }

            // Other COM marker segments
            if (otherCOMMarkSeg != null)
            {
                var stk = new Tokenizer(otherCOMMarkSeg, "#");
                while (stk.HasMoreTokens())
                {
                    var str = stk.NextToken();
                    WriteComment(writer, str);
                }
            }
        }

        private void WriteComment(BinaryWriter writer, string comment)
        {
            // COM marker
            writer.Write(Markers.COM);

            // Calculate length: Lcom(2) + Rcom(2) + string's length
            int markSegLen = 2 + 2 + comment.Length;
            writer.Write((short)markSegLen);

            // Rcom - General use (IS 8859-15:1999 Latin values)
            writer.Write((short)1);

            // Write comment string
            var chars = Encoding.UTF8.GetBytes(comment);
            foreach (var ch in chars)
            {
                writer.Write(ch);
            }
        }
    }
}
