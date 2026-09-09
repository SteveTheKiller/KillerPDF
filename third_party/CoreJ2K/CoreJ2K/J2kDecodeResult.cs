// Copyright (c) 2026 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using CoreJ2K.j2k.fileformat.metadata;
using CoreJ2K.Util;

namespace CoreJ2K
{
    /// <summary>
    /// The result of decoding a JPEG 2000 image: the decoded pixel data and any
    /// file-format metadata (comments, XML, UUID boxes, ICC profiles, rreq, etc.)
    /// found in the stream.
    /// </summary>
    public sealed class J2kDecodeResult
    {
        /// <summary>Gets the decoded image.</summary>
        public InterleavedImage Image { get; }

        /// <summary>
        /// Gets the file-format metadata extracted from the JP2/JPX wrapper, or an empty
        /// <see cref="J2KMetadata"/> instance when the input was a bare codestream.
        /// </summary>
        public J2KMetadata Metadata { get; }

        /// <param name="image">The decoded image.</param>
        /// <param name="metadata">The file-format metadata.</param>
        public J2kDecodeResult(InterleavedImage image, J2KMetadata metadata)
        {
            Image = image;
            Metadata = metadata;
        }

        /// <summary>Deconstructs the result for tuple-style assignment.</summary>
        public void Deconstruct(out InterleavedImage image, out J2KMetadata metadata)
        {
            image = Image;
            metadata = Metadata;
        }
    }
}
