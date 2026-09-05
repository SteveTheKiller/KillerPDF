// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.
#nullable disable


namespace KillerPdf.Engine.Filters.Jbig2
{
    using System;
    using System.IO;

    internal sealed class ImageInputStream : AbstractImageInputStream
    {
        private readonly Stream inner;

        /// <inheritdoc />
        public override long Length => inner.Length;

        /// <inheritdoc />
        public override long Position => inner.Position;

        /// <summary>
        /// Constructs a <see cref="ImageInputStream"/> that will read the image data
        /// from a given byte array.
        /// </summary>
        /// <param name="bytes"></param>
        public ImageInputStream(ReadOnlySpan<byte> bytes)
            : this(GetMemoryStream(bytes))
        { }

        /// <summary>
        /// Constructs a <see cref="ImageInputStream"/> that will read the image data
        /// from a given byte array.
        /// </summary>
        /// <param name="bytes"></param>
        public ImageInputStream(ReadOnlyMemory<byte> bytes)
            : this(new MemoryStream(bytes.ToArray(), writable: false))
        { }

        /// <summary>
        /// Constructs a <see cref="ImageInputStream"/> that will read the image data
        /// from a given <see cref="Stream"/>.
        /// </summary>
        /// <param name="input">the <see cref="Stream"/> to read the image data from.></param>
        public ImageInputStream(Stream input)
        {
            inner = input ?? throw new ArgumentNullException(nameof(input));
        }

        /// <inheritdoc />
        public override void Seek(long pos)
        {
            SetBitOffset(0);
            if (pos > inner.Length)
            {
                // As per the method documentation:
                // "It is legal to seek past the end of the file; an EndOfStreamException will be thrown only if a read is performed."
                // As a result, we need to prevent exception occuring here. The exception will be thrown on the Read() call.
                
                pos = inner.Length;
            }
            inner.Position = pos;
        }

        /// <inheritdoc />
        public override int Read()
        {
            if (IsAtEnd())
            {
                return -1;
            }

            SetBitOffset(0);
            return inner.ReadByte();
        }

        /// <inheritdoc />
        public override int Read(Span<byte> b, int off, int len)
        {
            if (IsAtEnd())
            {
                throw new EndOfStreamException();
            }

            SetBitOffset(0);

#if NET
            return inner.Read(b.Slice(0, len));
#else
            byte[] tempArray = new byte[len];
            int read = inner.Read(tempArray, 0, len);
            for (int i = 0; i < read; ++i)
            {
                b[i] = tempArray[i];
            }
            return read;
#endif
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            inner.Dispose();
        }

        private static MemoryStream GetMemoryStream(ReadOnlySpan<byte> bytes)
        {
            var memoryStream = new MemoryStream(bytes.Length);
            foreach (var b in bytes)
            {
                memoryStream.WriteByte(b);
            }
            memoryStream.Position = 0;
            return memoryStream;
        }
    }
}
