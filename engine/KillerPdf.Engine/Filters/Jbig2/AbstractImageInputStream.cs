// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.

#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    internal abstract class AbstractImageInputStream : IImageInputStream
    {
        private readonly Stack<(long streamPos, int bitOffset)> markedPositions = new Stack<(long, int)>();

        private int bitOffset;

        /// <inheritdoc />
        public abstract long Length { get; }

        /// <inheritdoc />
        public abstract long Position { get; }

        /// <inheritdoc />
        public abstract void Seek(long pos);

        /// <inheritdoc />
        public abstract int Read();

        /// <inheritdoc />
        public abstract int Read(Span<byte> b, int off, int len);

        /// <inheritdoc />
        public int Read(Span<byte> b)
        {
            return Read(b, 0, b.Length);
        }

        /// <inheritdoc />
        public int ReadBit()
        {
            var savedBitOffset = bitOffset;
            var b = ReadByte();
            SetBitOffset(savedBitOffset);

            var bit = (b & 1 << 7 - bitOffset) != 0;

            bitOffset = (bitOffset + 1) % 8;

            // Rewind if we're still processing the byte
            if (bitOffset > 0)
            {
                Seek(Position - 1);
            }

            return (byte)(bit ? 1 : 0);
        }

        /// <inheritdoc />
        public long ReadBits(int numBits)
        {
            if (numBits > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(numBits));
            }

            long accum = 0L;
            for (int i = 0; i < numBits; i++)
            {
                accum <<= 1; // Shift left one bit to make room
                var bit = (long)ReadBit();
                accum |= bit;
            }

            return accum;
        }

        /// <inheritdoc />
        public byte ReadByte()
        {
            var value = Read();
            if (value == -1)
            {
                throw new EndOfStreamException();
            }

            return (byte)value;
        }

        /// <inheritdoc />
        public uint ReadUnsignedInt()
        {
            Span<byte> buffer = stackalloc byte[4];
            Read(buffer);

            buffer.Reverse();

#if NET
            return BitConverter.ToUInt32(buffer);
#else
            return BitConverter.ToUInt32(buffer.ToArray(), 0);
#endif
        }

        /// <inheritdoc />
        public void Mark()
        {
            markedPositions.Push((Position, bitOffset));
        }

        /// <inheritdoc />
        public void Reset()
        {
            if (markedPositions.Count == 0)
            {
                return;
            }

            var position = markedPositions.Pop();
            Seek(position.streamPos);
            bitOffset = position.bitOffset;
        }

        /// <inheritdoc />
        public long SkipBytes(int n)
        {
            var desiredPosition = Position + n;
            if (desiredPosition > Length)
            {
                Seek(Length);
                return desiredPosition - Length;
            }

            Seek(desiredPosition);
            return n;
        }

        /// <inheritdoc />
        public void SkipBits()
        {
            if (bitOffset == 0)
            {
                return;
            }

            bitOffset = 0;
            Seek(Position + 1);
        }

        /// <inheritdoc />
        public virtual void Dispose()
        {
        }

        /// <summary>
        /// Sets the bit offset to an integer between 0 and 7, inclusive. The byte offset
        /// within the stream, as returned by getStreamPosition, is left unchanged.
        /// A value of 0 indicates the most-significant bit, and a value of 7 indicates
        /// the least significant bit, of the byte being read.
        /// </summary>
        /// <param name="bitOffset">the desired offset, as an int between 0 and 7, inclusive.</param>
        /// <exception cref="ArgumentOutOfRangeException">thrown if bitOffset is not between 0 and 7, inclusive.</exception>
        protected void SetBitOffset(int bitOffset)
        {
            if (bitOffset < 0 || bitOffset > 7)
            {
                throw new ArgumentOutOfRangeException(nameof(bitOffset), "must be betwwen 0 and 7!");
            }

            this.bitOffset = bitOffset;
        }

        protected bool IsAtEnd()
        {
            return Position == Length;
        }
    }
}
