// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.

#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    using System;

    internal interface IImageInputStream : IDisposable
    {
        /// <summary>
        /// Returns the total length of the stream, if known. Otherwise, -1 is returned.
        /// </summary>
        long Length { get; }

        /// <summary>
        /// Returns the current byte position of the stream.
        /// </summary>
        long Position { get; }

        /// <summary>
        /// Marks a position in the stream to be returned to by a subsequent call to reset. Additionally, calls to mark and reset may be nested arbitrarily.
        /// An arbitrary amount of data may be read following the call to mark.
        /// The bit position used by the readBits method is saved and restored by each pair of calls to mark and reset.
        /// </summary>
        void Mark();

        /// <summary>
        /// Reads a single byte from the stream and returns it as an integer between 0 and 255. If the end of the stream is reached, -1 is returned.
        /// The bit offset within the stream is reset to zero before the read occurs.
        /// </summary>
        int Read();

        /// <summary>
        /// Reads up to b.length bytes from the stream, and stores them into b starting at index 0. The number of bytes read is returned.
        /// If no bytes can be read because the end of the stream has been reached, -1 is returned.
        /// The bit offset within the stream is reset to zero before the read occurs.
        /// </summary>
        int Read(Span<byte> b);

        /// <summary>
        /// Reads up to len bytes from the stream, and stores them into b starting at index off. The number of bytes read is returned. If no bytes can be read because the end of the stream has been reached, -1 is returned.
        /// The bit offset within the stream is reset to zero before the read occurs.
        /// </summary>
        int Read(Span<byte> b, int off, int len);

        /// <summary>
        /// Reads a single bit from the stream and returns it as an int with the value 0 or 1. The bit offset is advanced by one and reduced modulo 8.
        /// </summary>
        int ReadBit();

        /// <summary>
        /// Reads a bitstring from the stream and returns it as a long, with the first bit read becoming the most significant bit of the output. The read starts within the byte indicated by getStreamPosition, at the bit given by getBitOffset. The bit offset is advanced by numBits and reduced modulo 8.
        /// The byte order of the stream has no effect on this method. The return value of this method is constructed as though the bits were read one at a time, and shifted into the right side of the return value,
        /// </summary>
        long ReadBits(int numBits);

        /// <summary>
        /// Reads a byte from the stream and returns it as a byte value. Byte values between 0x00 and 0x7f represent integer values between 0 and 127. Values between 0x80 and 0xff represent negative values from -128 to /1.
        /// The bit offset within the stream is reset to zero before the read occurs.
        /// </summary>
        byte ReadByte();

        /// <summary>
        /// Reads 4 bytes from the stream, and (conceptually) concatenates them according to the current byte order, converts the result to a long, masks it with 0xffffffffL in order to strip off any sign-extension bits, and returns the result as an unsigned long value.
        /// The bit offset within the stream is reset to zero before the read occurs
        /// </summary>
        uint ReadUnsignedInt();

        /// <summary>
        /// Returns the stream pointer to its previous position, including the bit offset, at the time of the most recent unmatched call to mark.
        /// Calls to reset without a corresponding call to mark have no effect.
        /// </summary>
        void Reset();

        /// <summary>
        /// Sets the current stream position to the desired location. The next read will occur at this location. The bit offset is set to 0.
        /// It is legal to seek past the end of the file; an <see cref="EndOfStreamException"/> will be thrown only if a read is performed.
        /// </summary>
        void Seek(long pos);

        /// <summary>
        /// Skips remaining bits in the current byte.
        /// </summary>
        void SkipBits();

        /// <summary>
        /// Moves the stream position forward by a given number of bytes. It is possible that this method will only be able to skip forward by a smaller number of bytes than requested, for example if the end of the stream is reached. In all cases, the actual number of bytes skipped is returned.
        /// The bit offset is set to zero prior to advancing the position.
        /// </summary>
        long SkipBytes(int n);
    }
}
