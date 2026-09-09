// Copyright (c) 2007-2016 CSJ2K contributors.
// Licensed under the BSD 3-Clause License.

using System;
using System.IO;

namespace CoreJ2K
{
    /// <summary>
    /// Provides helper methods for stream I/O operations.
    /// </summary>
    internal static class StreamHelper
    {
        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes from the stream into the buffer,
        /// throwing <see cref="EndOfStreamException"/> if the stream ends prematurely.
        /// </summary>
        internal static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var read = stream.Read(buffer, offset, count);
                if (read == 0)
                    throw new EndOfStreamException($"Unexpected end of stream: needed {count} more bytes.");
                offset += read;
                count -= read;
            }
        }
    }
}
