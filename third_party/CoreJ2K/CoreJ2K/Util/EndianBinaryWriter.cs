using System;
using System.IO;
using System.Text;

namespace CoreJ2K.Util
{
    internal class EndianBinaryWriter : BinaryWriter
    {
        private readonly bool _bigEndian = false;

        //
        // Summary:
        //     Initializes a new instance of the System.IO.BinaryWriter class based on the
        //     supplied stream and using UTF-8 as the encoding for strings.
        //
        // Parameters:
        //   output:
        //     The output stream.
        //
        // Exceptions:
        //   System.ArgumentException:
        //     The stream does not support writing, or the stream is already closed.
        //
        //   System.ArgumentNullException:
        //     output is null.
        public EndianBinaryWriter(Stream input) : base(input)
        {
        }
        //
        // Summary:
        //     Initializes a new instance of the System.IO.BinaryWriter class based on the
        //     supplied stream and a specific character encoding.
        //
        // Parameters:
        //   encoding:
        //     The character encoding.
        //
        //   output:
        //     The supplied stream.
        //
        // Exceptions:
        //   System.ArgumentNullException:
        //     output or encoding is null.
        //
        //   System.ArgumentException:
        //     The stream does not support writing, or the stream is already closed.
        public EndianBinaryWriter(Stream input, Encoding encoding) : base(input, encoding)
        {

        }
        public EndianBinaryWriter(Stream input, Encoding encoding, bool bigEndian)
            : base(input, encoding)
        {
            _bigEndian = bigEndian;
        }

        public EndianBinaryWriter(Stream input, bool bigEndian) : base(input, bigEndian ? Encoding.BigEndianUnicode : Encoding.UTF8)
        {
            _bigEndian = bigEndian;
        }

        // Summary:
        //     Gets the underlying stream of the System.IO.BinaryWriter.
        //
        // Returns:
        //     The underlying stream associated with the BinaryWriter.
        //public virtual Stream BaseStream { get; }

        // Summary:
        //     Closes the current System.IO.BinaryWriter and the underlying stream.
        //public virtual void Close();
        //
        // Summary:
        //     Releases the unmanaged resources used by the System.IO.BinaryWriter and optionally
        //     releases the managed resources.
        //
        // Parameters:
        //   disposing:
        //     true to release both managed and unmanaged resources; false to release only
        //     unmanaged resources.
        //protected virtual void Dispose(bool disposing);
        //
        // Summary:
        //     Clears all buffers for the current writer and causes any buffered data to
        //     be written to the underlying device.
        //public virtual void Flush();
        //
        // Summary:
        //     Sets the position within the current stream.
        //
        // Parameters:
        //   offset:
        //     A byte offset relative to origin.
        //
        //   origin:
        //     A field of System.IO.SeekOrigin indicating the reference point from which
        //     the new position is to be obtained.
        //
        // Returns:
        //     The position with the current stream.
        //
        // Exceptions:
        //   System.ArgumentException:
        //     The System.IO.SeekOrigin value is invalid.
        //
        //   System.IO.IOException:
        //     The file pointer was moved to an invalid location.
        //public virtual long Seek(int offset, SeekOrigin origin);
        //
        // Summary:
        //     Writes a one-byte Boolean value to the current stream, with 0 representing
        //     false and 1 representing true.
        //
        // Parameters:
        //   value:
        //     The Boolean value to write (0 or 1).
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //public virtual void Write(bool value);
        //
        // Summary:
        //     Writes an unsigned byte to the current stream and advances the stream position
        //     by one byte.
        //
        // Parameters:
        //   value:
        //     The unsigned byte to write.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //public virtual void Write(byte value);
        //
        // Summary:
        //     Writes a byte array to the underlying stream.
        //
        // Parameters:
        //   buffer:
        //     A byte array containing the data to write.
        //
        // Exceptions:
        //   System.ArgumentNullException:
        //     buffer is null.
        //
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.

        //
        // Summary:
        //     Writes an eight-byte floating-point value to the current stream and advances
        //     the stream position by eight bytes.
        //
        // Parameters:
        //   value:
        //     The eight-byte floating-point value to write.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        public override void Write(double value)
        {
            if (_bigEndian)
            {
                var buf = BitConverter.GetBytes(value);
                Array.Reverse(buf);
                base.Write(buf);
            }
            else base.Write(value);
        }
        //
        // Summary:
        //     Writes a four-byte floating-point value to the current stream and advances
        //     the stream position by four bytes.
        //
        // Parameters:
        //   value:
        //     The four-byte floating-point value to write.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        public override void Write(float value)
        {
            if (_bigEndian)
            {
                var buf = BitConverter.GetBytes(value);
                Array.Reverse(buf);
                base.Write(buf);
            }
            else base.Write(value);
        }
        //
        // Summary:
        //     Writes a four-byte signed integer to the current stream and advances the
        //     stream position by four bytes.
        //
        // Parameters:
        //   value:
        //     The four-byte signed integer to write.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        public override void Write(int value)
        {
            if (_bigEndian)
            {
                var buf = BitConverter.GetBytes(value);
                Array.Reverse(buf);
                base.Write(buf);
            }
            else base.Write(value);
        }
        //
        // Summary:
        //     Writes an eight-byte signed integer to the current stream and advances the
        //     stream position by eight bytes.
        //
        // Parameters:
        //   value:
        //     The eight-byte signed integer to write.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        public override void Write(long value)
        {
            if (_bigEndian)
            {
                var buf = BitConverter.GetBytes(value);
                Array.Reverse(buf);
                base.Write(buf);
            }
            else base.Write(value);
        }
        //
        // Summary:
        //     Writes a signed byte to the current stream and advances the stream position
        //     by one byte.
        //
        // Parameters:
        //   value:
        //     The signed byte to write.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //[CLSCompliant(false)]
        //public virtual void Write(sbyte value);
        //
        // Summary:
        //     Writes a two-byte signed integer to the current stream and advances the stream
        //     position by two bytes.
        //
        // Parameters:
        //   value:
        //     The two-byte signed integer to write.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        public override void Write(short value)
        {
            if (_bigEndian)
            {
                var buf = BitConverter.GetBytes(value);
                Array.Reverse(buf);
                base.Write(buf);
            }
            else base.Write(value);
        }
        //
        // Summary:
        //     Writes a length-prefixed string to this stream in the current encoding of
        //     the System.IO.BinaryWriter, and advances the current position of the stream
        //     in accordance with the encoding used and the specific characters being written
        //     to the stream.
        //
        // Parameters:
        //   value:
        //     The value to write.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        //
        //   System.ArgumentNullException:
        //     value is null.
        //public virtual void Write(string value);
        //
        // Summary:
        //     Writes a four-byte unsigned integer to the current stream and advances the
        //     stream position by four bytes.
        //
        // Parameters:
        //   value:
        //     The four-byte unsigned integer to write.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        public override void Write(uint value)
        {
            if (_bigEndian)
            {
                var buf = BitConverter.GetBytes(value);
                Array.Reverse(buf);
                base.Write(buf);
            }
            else base.Write(value);
        }
        //
        // Summary:
        //     Writes an eight-byte unsigned integer to the current stream and advances
        //     the stream position by eight bytes.
        //
        // Parameters:
        //   value:
        //     The eight-byte unsigned integer to write.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        public override void Write(ulong value)
        {
            if (_bigEndian)
            {
                var buf = BitConverter.GetBytes(value);
                Array.Reverse(buf);
                base.Write(buf);
            }
            else base.Write(value);
        }
        //
        // Summary:
        //     Writes a two-byte unsigned integer to the current stream and advances the
        //     stream position by two bytes.
        //
        // Parameters:
        //   value:
        //     The two-byte unsigned integer to write.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.IOException:
        //     An I/O error occurs.
        public override void Write(ushort value)
        {
            if (_bigEndian)
            {
                var buf = BitConverter.GetBytes(value);
                Array.Reverse(buf);
                base.Write(buf);
            }
            else base.Write(value);
        }
        //
        // Summary:
        //     Writes a region of a byte array to the current stream.
        //
        // Parameters:
        //   count:
        //     The number of bytes to write.
        //
        //   buffer:
        //     A byte array containing the data to write.
        //
        //   index:
        //     The starting point in buffer at which to begin writing.
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
        //public virtual void Write(byte[] buffer, int index, int count);
        //
        // Summary:
        //     Writes a section of a character array to the current stream, and advances
        //     the current position of the stream in accordance with the Encoding used and
        //     perhaps the specific characters being written to the stream.
        //
        // Parameters:
        //   chars:
        //     A character array containing the data to write.
        //
        //   count:
        //     The number of characters to write.
        //
        //   index:
        //     The starting point in buffer from which to begin writing.
        //
        // Exceptions:
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
        //
        //   System.ArgumentNullException:
        //     chars is null.
        //public virtual void Write(char[] chars, int index, int count);
        //
        // Summary:
        //     Writes a 32-bit integer in a compressed format.
        //
        // Parameters:
        //   value:
        //     The 32-bit integer to be written.
        //
        // Exceptions:
        //   System.ObjectDisposedException:
        //     The stream is closed.
        //
        //   System.IO.EndOfStreamException:
        //     The end of the stream is reached.
        //
        //   System.IO.IOException:
        //     The stream is closed.
        //protected void Write7BitEncodedInt(int value)

    }
}
