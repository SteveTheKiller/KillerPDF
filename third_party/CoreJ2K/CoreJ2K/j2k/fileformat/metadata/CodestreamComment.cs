// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System.Text;

namespace CoreJ2K.j2k.fileformat.metadata
{
    /// <summary>
    /// Represents a comment from a COM (Comment) marker segment in the JPEG2000 codestream.
    /// COM markers can appear in main headers or tile-part headers and contain text or binary data.
    /// </summary>
    public class CodestreamComment
    {
        /// <summary>
        /// Gets or sets the comment text (for Latin text registration method).
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// Gets or sets the binary comment data (for non-text registration methods).
        /// </summary>
        public byte[] Data { get; set; }

        /// <summary>
        /// Gets or sets the registration method (Rcom field).
        /// 0 = Binary data (general use)
        /// 1 = ISO 8859-15:1999 (Latin) text
        /// Other values are reserved or vendor-specific.
        /// </summary>
        public int RegistrationMethod { get; set; } = 1;

        /// <summary>
        /// Gets or sets whether this comment is from the main header (true) or tile-part header (false).
        /// </summary>
        public bool IsMainHeader { get; set; } = true;

        /// <summary>
        /// Gets or sets the tile index if this comment is from a tile-part header.
        /// Value is -1 if from main header.
        /// </summary>
        public int TileIndex { get; set; } = -1;

        /// <summary>
        /// Gets or sets whether this comment contains binary data.
        /// </summary>
        public bool IsBinary { get; set; }

        /// <summary>
        /// Gets a formatted string representation of the comment.
        /// </summary>
        public string GetText()
        {
            if (!string.IsNullOrEmpty(Text))
                return Text;

            if (Data != null && RegistrationMethod == 1)
            {
                try
                {
                    return Encoding.UTF8.GetString(Data);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        public override string ToString()
        {
            var location = IsMainHeader ? "Main header" : $"Tile {TileIndex}";
            string regMethod;
            if (RegistrationMethod == 0)
                regMethod = "Binary";
            else if (RegistrationMethod == 1)
                regMethod = "Latin text";
            else
                regMethod = $"Unknown ({RegistrationMethod})";

            if (IsBinary || Data != null)
            {
                return $"COM [{location}, {regMethod}]: Binary data ({Data?.Length ?? 0} bytes)";
            }

            var preview = Text?.Length > 50 ? Text.Substring(0, 50) + "..." : Text;
            return $"COM [{location}, {regMethod}]: {preview}";
        }
    }
}
