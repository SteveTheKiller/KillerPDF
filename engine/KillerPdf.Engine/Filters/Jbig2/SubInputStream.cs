// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.
#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    using System;

    /// <summary>
    /// A wrapper for an <see cref="IImageInputStream"/> which is able to provide a view of a specific part of the wrapped stream.
    /// Read accesses to the wrapped stream are synchronized, so that users of this stream need to deal with synchronization
    /// against other users of the same instance, but not against other users of the wrapped stream.
    /// </summary>
    internal sealed class SubInputStream : AbstractImageInputStream
    {
        private readonly IImageInputStream wrappedStream;

        /// <summary>
        /// The position in the wrapped stream at which the window starts. Offset is an absolut value.
        /// </summary>
        private readonly long offset;

        /// <summary>
        /// A buffer which is used to improve read performance.
        /// </summary>
        private readonly byte[] buffer = new byte[4096];

        /// <summary>
        /// Location of the first byte in the buffer with respect to the start of the stream.
        /// </summary>
        private long bufferBase;

        /// <summary>
        /// Location of the last byte in the buffer with respect to the start of the stream.
        /// </summary>
        private long bufferTop;

        private long streamPosition;

        /// <summary>
        /// <inheritdoc />
        /// The length of the window. Length is a relative value.
        /// </summary>
        public override long Length { get; }

        /// <inheritdoc />
        public override long Position => streamPosition;

        /// <summary>
        /// Constructs a new SubInputStream which provides a view of the wrapped stream.
        /// </summary>
        /// <param name="iis">The stream to be wrapped.</param>
        /// <param name="offset">The absolute position in the wrapped stream at which the sub-stream starts.</param>
        /// <param name="length">The length of the sub-stream.</param>
        public SubInputStream(IImageInputStream iis, long offset, long length)
        {
            wrappedStream = iis;
            this.offset = offset;
            Length = length;
        }

        /// <inheritdoc />
        public override int Read()
        {
            if (streamPosition >= Length)
            {
                return -1;
            }

            if (streamPosition >= bufferTop || streamPosition < bufferBase)
            {
                if (!FillBuffer())
                {
                    return -1;
                }
            }

            int read = 0xff & buffer[(int)(streamPosition - bufferBase)];

            streamPosition++;

            return read;
        }

        /// <inheritdoc />
        public override int Read(Span<byte> b, int off, int len)
        {
            if (streamPosition >= Length)
            {
                return -1;
            }

            lock (wrappedStream)
            {
                var desiredPosition = streamPosition + offset;
                if (wrappedStream.Position != desiredPosition)
                {
                    wrappedStream.Seek(desiredPosition);
                }

                int toRead = (int)Math.Min(len, Length - Position);
                int read = wrappedStream.Read(b.Slice(off, toRead));
                streamPosition += read;

                return read;
            }
        }

        /// <inheritdoc />
        public override void Seek(long pos)
        {
            streamPosition = pos;
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            wrappedStream.Dispose();
        }

        /// <summary>
        /// Fill the buffer at the current stream position.
        /// </summary>
        /// <returns>true if successful, false otherwise</returns>
        private bool FillBuffer()
        {
            lock (wrappedStream)
            {
                var desiredPosition = streamPosition + offset;
                if (wrappedStream.Position != desiredPosition)
                {
                    wrappedStream.Seek(desiredPosition);
                }

                bufferBase = streamPosition;
                int toRead = (int)Math.Min(buffer.Length, Length - streamPosition);
                int read = wrappedStream.Read(buffer, 0, toRead);
                bufferTop = bufferBase + read;

                return read > 0;
            }
        }
    }
}
