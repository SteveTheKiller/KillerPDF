using System;
using System.IO;
using System.Text;

namespace CoreJ2K.Util
{
    internal class EndianBinaryReader : BinaryReader
    {
        private readonly bool _bigEndian = false;

        // Summary:
        //     Initializes a new instance of the System.IO.BinaryReader class based on the
        //     supplied stream and using System.Text.UTF8Encoding.
        //
        // Parameters:
        //   input:
        //     A stream.
        //
        // Exceptions:
        //   System.ArgumentException:
        //     The stream does not support reading, the stream is null, or the stream is
        //     already closed.
        public EndianBinaryReader(Stream input) : base(input)
        {

        }
        //
        // Summary:
        //     Initializes a new instance of the System.IO.BinaryReader class based on the
        //     supplied stream and a specific character encoding.
        //
        // Parameters:
        //   encoding:
        //     The character encoding.
        //
        //   input:
        //     The supplied stream.
        //
        // Exceptions:
        //   System.ArgumentNullException:
        //     encoding is null.
        //
        //   System.ArgumentException:
        //     The stream does not support reading, the stream is null, or the stream is
        //     already closed.
        public EndianBinaryReader(Stream input, Encoding encoding) : base(input, encoding)
        {

        }

        public EndianBinaryReader(Stream input, Encoding encoding, bool bigEndian)
            : base(input, encoding)
        {
            _bigEndian = bigEndian;
        }

        public EndianBinaryReader(Stream input, bool bigEndian) : base(input, bigEndian ? Encoding.BigEndianUnicode : Encoding.UTF8)
        {
            _bigEndian = bigEndian;
        }

        // Summary:
        //     Exposes access to the underlying stream of the System.IO.BinaryReader.
        //
        // Returns:
        //     The underlying stream associated with the BinaryReader.
        //public virtual Stream BaseStream { get; }

        // Summary:
        //     Closes the current reader and the underlying stream.
        //public virtual void Close();
        //
        // Summary:
        //     Releases the unmanaged resources used by the System.IO.BinaryReader and optionally
        //     releases the managed resources.
        //
        // Parameters:
        //   disposing:
        //     true to release both managed and unmanaged resources; false to release only
        //     unmanaged resources.
        //protected virtual void Dispose(bool disposing);
        //
        // Summary:
        //     Fills the internal buffer with the specified number of bytes read from the
        //     stream.
        //
        // Parameters:
        //   numBytes:
        //     The number of bytes to be read.
        //
        // Exceptions:
        //   System.IO.EndOfStreamException:
        //     The end of the stream is reached before numBytes could be read.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //protected virtual void FillBuffer(int numBytes);
        //
        // Summary:
        //     Returns the next available character and does not advance the byte or character
        //     position.
        //
        // Returns:
        //     The next available character, or -1 if no more characters are available or
        //     the stream does not support seeking.
        //
        // Exceptions:
        //   System.IO.IOException:
        //     An I/O error occurs.
        //public virtual int PeekChar();
        //
        // Summary:
        //     Reads characters from the underlying stream and advances the current position
        //     of the stream in accordance with the Encoding used and the specific character
        //     being read from the stream.
        //
        // Returns:
        //     The next character from the input stream, or -1 if no characters are currently
        //     available.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //public virtual int Read();
        //
        // Summary:
        //     Reads count bytes from the stream with index as the starting point in the
        //     byte array.
        //
        // Parameters:
        //   count:
        //     The number of characters to read.
        //
        //   buffer:
        //     The buffer to read data into.
        //
        //   index:
        //     The starting point in the buffer at which to begin reading into the buffer.
        //
        // Returns:
        //     The number of characters read into buffer. This might be less than the number
        //     of bytes requested if that many bytes are not available, or it might be zero
        //     if the end of the stream is reached.
        //
        // Exceptions:
        //   System.ArgumentNullException:
        //     buffer is null.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.ArgumentOutOfRangeException:
        //     index or count is negative.
        //
        //   System.ArgumentException:
        //     The buffer length minus index is less than count.
        //public virtual int Read(byte[] buffer, int index, int count);
        //
        // Summary:
        //     Reads count characters from the stream with index as the starting point in
        //     the character array.
        //
        // Parameters:
        //   count:
        //     The number of characters to read.
        //
        //   buffer:
        //     The buffer to read data into.
        //
        //   index:
        //     The starting point in the buffer at which to begin reading into the buffer.
        //
        // Returns:
        //     The total number of characters read into the buffer. This might be less than
        //     the number of characters requested if that many characters are not currently
        //     available, or it might be zero if the end of the stream is reached.
        //
        // Exceptions:
        //   System.ArgumentNullException:
        //     buffer is null.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.ArgumentOutOfRangeException:
        //     index or count is negative.
        //
        //   System.ArgumentException:
        //     The buffer length minus index is less than count.
        //public virtual int Read(char[] buffer, int index, int count);
        //
        // Summary:
        //     Reads in a 32-bit integer in compressed format.
        //
        // Returns:
        //     A 32-bit integer in compressed format.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //
        //   System.FormatException:
        //     The stream is corrupted.
        //
        //   System.IO.EndOfStreamException:
        //     The end of the stream is reached.
        //protected internal int Read7BitEncodedInt();
        //
        // Summary:
        //     Reads a Boolean value from the current stream and advances the current position
        //     of the stream by one byte.
        //
        // Returns:
        //     true if the byte is nonzero; otherwise, false.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //
        //   System.IO.EndOfStreamException:
        //     The end of the stream is reached.
        //public virtual bool ReadBoolean();
        //
        // Summary:
        //     Reads the next byte from the current stream and advances the current position
        //     of the stream by one byte.
        //
        // Returns:
        //     The next byte read from the current stream.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //
        //   System.IO.EndOfStreamException:
        //     The end of the stream is reached.
        //public virtual byte ReadByte();
        //
        // Summary:
        //     Reads count bytes from the current stream into a byte array and advances
        //     the current position by count bytes.
        //
        // Parameters:
        //   count:
        //     The number of bytes to read.
        //
        // Returns:
        //     A byte array containing data read from the underlying stream. This might
        //     be less than the number of bytes requested if the end of the stream is reached.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //
        //   System.ArgumentOutOfRangeException:
        //     count is negative.
        //public virtual byte[] ReadBytes(int count);
        //
        // Summary:
        //     Reads the next character from the current stream and advances the current
        //     position of the stream in accordance with the Encoding used and the specific
        //     character being read from the stream.
        //
        // Returns:
        //     A character read from the current stream.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //
        //   System.IO.EndOfStreamException:
        //     The end of the stream is reached.
        //
        //   System.ArgumentException:
        //     A surrogate character was read.
        //public virtual char ReadChar();
        //
        // Summary:
        //     Reads count characters from the current stream, returns the data in a character
        //     array, and advances the current position in accordance with the Encoding
        //     used and the specific character being read from the stream.
        //
        // Parameters:
        //   count:
        //     The number of characters to read.
        //
        // Returns:
        //     A character array containing data read from the underlying stream. This might
        //     be less than the number of characters requested if the end of the stream
        //     is reached.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //
        //   System.IO.EndOfStreamException:
        //     The end of the stream is reached.
        //
        //   System.ArgumentOutOfRangeException:
        //     count is negative.
        //public virtual char[] ReadChars(int count);
        //
        // Summary:
        //     Reads a decimal value from the current stream and advances the current position
        //     of the stream by sixteen bytes.
        //
        // Returns:
        //     A decimal value read from the current stream.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //
        //   System.IO.EndOfStreamException:
        //     The end of the stream is reached.
        //
        // Summary:
        //     Reads an 8-byte floating point value from the current stream and advances
        //     the current position of the stream by eight bytes.
        //
        // Returns:
        //     An 8-byte floating point value read from the current stream.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //
        //   System.IO.EndOfStreamException:
        //     The end of the stream is reached.
        public override double ReadDouble()
        {
            if (_bigEndian)
            {
                var buf = ReadBytes(8);
                Array.Reverse(buf);
                return BitConverter.ToDouble(buf, 0);
            }
            else
                return base.ReadDouble();
        }
        //
        // Summary:
        //     Reads a 2-byte signed integer from the current stream and advances the current
        //     position of the stream by two bytes.
        //
        // Returns:
        //     A 2-byte signed integer read from the current stream.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //
        //   System.IO.EndOfStreamException:
        //     The end of the stream is reached.
        public override short ReadInt16()
        {
            if (_bigEndian)
            {
                var buf = ReadBytes(2);
                Array.Reverse(buf);
                return BitConverter.ToInt16(buf, 0);
            }
            else
                return base.ReadInt16();
        }
        //
        // Summary:
        //     Reads a 4-byte signed integer from the current stream and advances the current
        //     position of the stream by four bytes.
        //
        // Returns:
        //     A 4-byte signed integer read from the current stream.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //
        //   System.IO.EndOfStreamException:
        //     The end of the stream is reached.
        public override int ReadInt32()
        {
            if (_bigEndian)
            {
                var buf = ReadBytes(4);
                Array.Reverse(buf);
                return BitConverter.ToInt32(buf, 0);
            }
            else
                return base.ReadInt32();
        }
        //
        // Summary:
        //     Reads an 8-byte signed integer from the current stream and advances the current
        //     position of the stream by eight bytes.
        //
        // Returns:
        //     An 8-byte signed integer read from the current stream.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //
        //   System.IO.EndOfStreamException:
        //     The end of the stream is reached.
        public override long ReadInt64()
        {
            if (_bigEndian)
            {
                var buf = ReadBytes(8);
                Array.Reverse(buf);
                return BitConverter.ToInt64(buf, 0);
            }
            else
                return base.ReadInt64();
        }
        //
        // Summary:
        //     Reads a signed byte from this stream and advances the current position of
        //     the stream by one byte.
        //
        // Returns:
        //     A signed byte read from the current stream.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //
        //   System.IO.EndOfStreamException:
        //     The end of the stream is reached.
        //[CLSCompliant(false)]
        //public virtual byte Readbyte();
        //
        // Summary:
        //     Reads a 4-byte floating point value from the current stream and advances
        //     the current position of the stream by four bytes.
        //
        // Returns:
        //     A 4-byte floating point value read from the current stream.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //
        //   System.IO.EndOfStreamException:
        //     The end of the stream is reached.
        public override float ReadSingle()
        {
            if (_bigEndian)
            {
                var buf = ReadBytes(4);
                Array.Reverse(buf);
                return BitConverter.ToSingle(buf, 0);
            }
            else
                return base.ReadSingle();
        }
        //
        // Summary:
        //     Reads a string from the current stream. The string is prefixed with the length,
        //     encoded as an integer seven bits at a time.
        //
        // Returns:
        //     The string being read.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //
        //   System.IO.EndOfStreamException:
        //     The end of the stream is reached.
        //public virtual string ReadString();
        //
        // Summary:
        //     Reads a 2-byte unsigned integer from the current stream using little-endian
        //     encoding and advances the position of the stream by two bytes.
        //
        // Returns:
        //     A 2-byte unsigned integer read from this stream.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //
        //   System.IO.EndOfStreamException:
        //     The end of the stream is reached.
        public override ushort ReadUInt16()
        {
            if (_bigEndian)
            {
                var buf = ReadBytes(2);
                Array.Reverse(buf);
                return BitConverter.ToUInt16(buf, 0);
            }
            else
                return base.ReadUInt16();
        }
        //
        // Summary:
        //     Reads a 4-byte unsigned integer from the current stream and advances the
        //     position of the stream by four bytes.
        //
        // Returns:
        //     A 4-byte unsigned integer read from this stream.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //
        //   System.IO.EndOfStreamException:
        //     The end of the stream is reached.
        public override uint ReadUInt32()
        {
            if (_bigEndian)
            {
                var buf = ReadBytes(4);
                Array.Reverse(buf);
                return BitConverter.ToUInt32(buf, 0);
            }
            else
                return base.ReadUInt32();
        }
        //
        // Summary:
        //     Reads an 8-byte unsigned integer from the current stream and advances the
        //     position of the stream by eight bytes.
        //
        // Returns:
        //     An 8-byte unsigned integer read from this stream.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //
        //   System.IO.EndOfStreamException:
        //     The end of the stream is reached.
        public override ulong ReadUInt64()
        {
            if (_bigEndian)
            {
                var buf = ReadBytes(8);
                Array.Reverse(buf);
                return BitConverter.ToUInt64(buf, 0);
            }
            else
                return base.ReadUInt64();
        }
    }
}
