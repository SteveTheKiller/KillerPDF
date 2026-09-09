/*
* Interface:           RandomAccessIO.java
*
*
* COPYRIGHT:
* 
* This software module was originally developed by Rapha�l Grosbois and
* Diego Santa Cruz (Swiss Federal Institute of Technology-EPFL); Joel
* Askel�f (Ericsson Radio Systems AB); and Bertrand Berthelot, David
* Bouchard, F�lix Henry, Gerard Mozelle and Patrice Onno (Canon Research
* Centre France S.A) in the course of development of the JPEG2000
* standard as specified by ISO/IEC 15444 (JPEG 2000 Standard). This
* software module is an implementation of a part of the JPEG 2000
* Standard. Swiss Federal Institute of Technology-EPFL, Ericsson Radio
* Systems AB and Canon Research Centre France S.A (collectively JJ2000
* Partners) agree not to assert against ISO/IEC and users of the JPEG
* 2000 Standard (Users) any of their rights under the copyright, not
* including other intellectual property rights, for this software module
* with respect to the usage by ISO/IEC and Users of this software module
* or modifications thereof for use in hardware or software products
* claiming conformance to the JPEG 2000 Standard. Those intending to use
* this software module in hardware or software products are advised that
* their use may infringe existing patents. The original developers of
* this software module, JJ2000 Partners and ISO/IEC assume no liability
* for use of this software module or modifications thereof. No license
* or right to this software module is granted for non JPEG 2000 Standard
* conforming products. JJ2000 Partners have full right to use this
* software module for his/her own purpose, assign or donate this
* software module to any third party and to inhibit third parties from
* using this software module for non JPEG 2000 Standard conforming
* products. This copyright notice must be included in all copies or
* derivative works of this software module.
* 
* Copyright (c) 1999/2000 JJ2000 Partners.
* */
namespace CoreJ2K.j2k.io
{
    using System;
    using System.IO;
    using CoreJ2K.Util;

    /// <summary> This class defines a Buffered Random Access File.  It implements the
    /// <tt>BinaryDataInput</tt> and <tt>BinaryDataOutput</tt> interfaces so that
    /// binary data input/output can be performed. This class is abstract since no
    /// assumption is done about the byte ordering type (little Endian, big
    /// Endian). So subclasses will have to implement methods like
    /// <tt>readShort()</tt>, <tt>writeShort()</tt>, <tt>readFloat()</tt>, ...
    /// 
    /// <tt>BufferedRandomAccessFile</tt> (BRAF for short) is a
    /// <tt>RandomAccessFile</tt> containing an extra buffer. When the BRAF is
    /// accessed, it checks if the requested part of the file is in the buffer or
    /// not. If that is the case, the read/write is done on the buffer. If not, the
    /// file is uppdated to reflect the current status of the buffer and the file
    /// is then accessed for a new buffer containing the requested byte/bit.
    /// 
    /// </summary>
    /// <seealso cref="RandomAccessIO" />
    /// <seealso cref="BinaryDataOutput" />
    /// <seealso cref="BinaryDataInput" />
    /// <seealso cref="BEBufferedRandomAccessFile" />
    public abstract class BufferedRandomAccessFile : RandomAccessIO, EndianType
    {
        /// <summary> Returns the current offset in the file
        /// 
        /// </summary>
        public virtual int Pos => (offset + position);

        /// <summary> Returns the endianess (i.e., byte ordering) of the implementing
        /// class. Note that an implementing class may implement only one
        /// type of endianness or both, which would be decided at creation
        /// time.
        /// 
        /// </summary>
        /// <returns> Either <tt>EndianType.BIG_ENDIAN</tt> or
        /// <tt>EndianType.LITTLE_ENDIAN</tt>
        /// 
        /// </returns>
        /// <seealso cref="EndianType" />
        public virtual int ByteOrdering => byte_Ordering;

        /// <summary>The name of the current file </summary>
        private readonly string fileName;

        /// <summary> Whether the opened file is read only or not (defined by the constructor
        /// arguments)
        /// 
        /// </summary>
        private readonly bool isReadOnly = true;

        /// <summary> The RandomAccessFile associated with the buffer
        /// 
        /// </summary>
        private readonly Stream theFile;

        /// <summary> Buffer of bytes containing the part of the file that is currently being
        /// accessed
        /// 
        /// </summary>
        protected internal byte[] byteBuffer;

        /// <summary> Boolean keeping track of whether the byte buffer has been changed since
        /// it was read.
        /// 
        /// </summary>
        protected internal bool byteBufferChanged;

        /// <summary> The current offset of the buffer (which will differ from the offset of
        /// the file) 
        /// 
        /// </summary>
        protected internal int offset;

        /// <summary> The current position in the byte-buffer
        /// 
        /// </summary>
        protected internal int position;

        /// <summary> The maximum number of bytes that can be read from the buffer
        /// 
        /// </summary>
        protected internal int maxByte;

        /// <summary> Whether the end of the file is in the current buffer or not
        /// 
        /// </summary>
        protected internal bool isEOFInBuffer;

        /* The endianess of the class */
        protected internal int byte_Ordering;

        /// <summary> Constructor. Always needs a size for the buffer.
        /// 
        /// </summary>
        /// <param name="stream">The stream associated with the buffer
        /// 
        /// </param>
        /// <param name="isReadOnly">Indicates whether file is read-only or not.</param>
        /// <param name="bufferSize">The number of bytes to buffer
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        protected internal BufferedRandomAccessFile(Stream stream, bool isReadOnly, int bufferSize)
        {
            fileName = string.Empty;
            theFile = stream;
            this.isReadOnly = isReadOnly;
            byteBuffer = new byte[bufferSize];
            readNewBuffer(0);
        }

        /// <summary> Constructor. Uses the default value for the byte-buffer 
        /// size (512 bytes).
        /// 
        /// </summary>
        /// <param name="stream">The stream associated with the buffer
        /// 
        /// </param>
        /// <param name="isReadOnly">Indicates whether file is read-only or not.</param>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        protected internal BufferedRandomAccessFile(Stream stream, bool isReadOnly)
            : this(stream, isReadOnly, 512)
        {
        }

        /// <summary> Constructor. Always needs a size for the buffer.
        /// 
        /// </summary>
        /// <param name="file">The file associated with the buffer
        /// 
        /// </param>
        /// <param name="mode">"r" for read, "rw" or "rw+" for read and write mode ("rw+"
        /// opens the file for update whereas "rw" removes it
        /// before. So the 2 modes are different only if the file
        /// already exists).
        /// 
        /// </param>
        /// <param name="bufferSize">The number of bytes to buffer
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        protected internal BufferedRandomAccessFile(IFileInfo file, string mode, int bufferSize)
        {

            fileName = file.Name;
            if (mode.Equals("rw") || mode.Equals("rw+"))
            {
                // mode read / write
                isReadOnly = false;
                if (mode.Equals("rw"))
                {
                    if (file.Exists && !file.Delete())
                    {
                        throw new IOException("Could not delete existing file");
                    }
                }
                mode = "rw";
            }
            theFile = FileStreamFactory.New(file.FullName, mode);
            byteBuffer = new byte[bufferSize];
            readNewBuffer(0);
        }

        /// <summary> Constructor. Uses the default value for the byte-buffer 
        /// size (512 bytes).
        /// 
        /// </summary>
        /// <param name="file">The file associated with the buffer
        /// 
        /// </param>
        /// <param name="mode">"r" for read, "rw" or "rw+" for read and write mode
        /// ("rw+" opens the file for update whereas "rw" removes 
        /// it before. So the 2 modes are different only if the 
        /// file already exists).
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        protected internal BufferedRandomAccessFile(IFileInfo file, string mode) : this(file, mode, 512)
        {
        }

        /// <summary> Constructor. Always needs a size for the buffer.
        /// 
        /// </summary>
        /// <param name="name">The name of the file associated with the buffer
        /// 
        /// </param>
        /// <param name="mode">"r" for read, "rw" or "rw+" for read and write mode
        /// ("rw+" opens the file for update whereas "rw" removes 
        /// it before. So the 2 modes are different only if the 
        /// file already exists).
        /// 
        /// </param>
        /// <param name="bufferSize">The number of bytes to buffer
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        protected internal BufferedRandomAccessFile(string name, string mode, int bufferSize) : this(FileInfoFactory.New(name), mode, bufferSize)
        {
        }

        /// <summary> Constructor. Uses the default value for the byte-buffer 
        /// size (512 bytes).
        /// 
        /// </summary>
        /// <param name="name">The name of the file associated with the buffer
        /// 
        /// </param>
        /// <param name="mode">"r" for read, "rw" or "rw+" for read and write mode
        /// ("rw+" opens the file for update whereas "rw" removes 
        /// it before. So the 2 modes are different only if the 
        /// file already exists).
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        protected internal BufferedRandomAccessFile(string name, string mode) : this(name, mode, 512)
        {
        }

        /// <summary> Reads a new buffer from the file. If there has been any
        /// changes made since the buffer was read, the buffer is 
        /// first written to the file.
        /// 
        /// </summary>
        /// <param name="off">The offset where to move to.
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        protected internal void readNewBuffer(int off)
        {

            /* If the buffer have changed. We need to write it to 
			* the file before reading a new buffer.
			*/
            if (byteBufferChanged)
            {
                flush();
            }
            // Don't allow to seek beyond end of file if reading only
            if (isReadOnly && off >= theFile.Length)
            {
                throw new EndOfStreamException();
            }
            // Set new offset
            offset = off;

            theFile.Seek(offset, SeekOrigin.Begin);

            maxByte = theFile.Read(byteBuffer, 0, byteBuffer.Length);
            position = 0;

            if (maxByte < byteBuffer.Length)
            {
                // Not enough data in input file.
                isEOFInBuffer = true;
                if (maxByte == -1)
                {
                    maxByte++;
                }
            }
            else
            {
                isEOFInBuffer = false;
            }
        }

        /// <summary> Closes the buffered random access file
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
		public virtual void Close()
		{
			/* If the buffer has been changed, it need to be saved before
			* closing
			*/
			flush();
			byteBuffer = null; // Release the byte-buffer reference
		}

		/// <summary>
		/// Releases the resources held by this object. Calls <see cref="Close"/>.
		/// </summary>
		public void Dispose()
		{
			Close();
			GC.SuppressFinalize(this);
		}

        /// <summary> Returns the current length of the stream, in bytes, taking into
        /// account any buffering.
        /// 
        /// </summary>
        /// <returns> The length of the stream, in bytes.
        /// 
        /// </returns>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual int length()
        {
            int len;

            len = (int)theFile.Length;

            // If the position in the buffer is not past the end of the file,
            // the length of theFile is the length of the stream
            if ((offset + maxByte) <= len)
            {
                return (len);
            }
            else
            {
                // If not, the file is extended due to the buffering
                return (offset + maxByte);
            }
        }

        /// <summary> Moves the current position to the given offset at which the
        /// next read or write occurs. The offset is measured from the 
        /// beginning of the stream.
        /// 
        /// </summary>
        /// <param name="off">The offset where to move to.
        /// 
        /// </param>
        /// <exception cref="EOFException">If in read-only and seeking beyond EOF.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual void seek(int off)
        {
            /* If the new offset is within the buffer, only the pos value needs
			* to be modified. Else, the buffer must be moved. */
            if ((off >= offset) && (off < (offset + byteBuffer.Length)))
            {
                if (isReadOnly && isEOFInBuffer && off > offset + maxByte)
                {
                    // We are seeking beyond EOF in read-only mode!
                    throw new EndOfStreamException();
                }
                position = off - offset;
            }
            else
            {
                readNewBuffer(off);
            }
        }

        /// <summary> Reads an unsigned byte of data from the stream. Prior to reading, the
        /// stream is realigned at the byte level.
        /// 
        /// </summary>
        /// <returns> The byte read.
        /// 
        /// </returns>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        /// <exception cref="java.io.EOFException">If the end of file was reached
        /// 
        /// </exception>
        public byte readByte() { return read(); }
        public byte readUnsignedByte() { return read(); }
        public byte read()
        {
            if (position < maxByte)
            {
                // The byte can be read from the buffer
                // In Java, the bytes are always signed.
                return (byteBuffer[position++]);
            }
            else if (isEOFInBuffer)
            {
                // EOF is reached
                position = maxByte + 1; // Set position to EOF
                throw new EndOfStreamException();
            }
            else
            {
                // End of the buffer is reached
                readNewBuffer(offset + position);
                return read();
            }
        }

        /// <summary> Reads up to len bytes of data from this file into an array of
        /// bytes. This method reads repeatedly from the stream until all the bytes
        /// are read. This method blocks until all the bytes are read, the end of
        /// the stream is detected, or an exception is thrown.
        /// 
        /// </summary>
        /// <param name="b">The buffer into which the data is to be read. It must be long
        /// enough.
        /// 
        /// </param>
        /// <param name="off">The index in 'b' where to place the first byte read.
        /// 
        /// </param>
        /// <param name="len">The number of bytes to read.
        /// 
        /// </param>
        /// <exception cref="EOFException">If the end-of file was reached before
        /// getting all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public void readFully(byte[] b, int off, int len)
        {
            int clen; // current length to read
            while (len > 0)
            {
                // There still is some data to read
                if (position < maxByte)
                {
                    // We can read some data from buffer
                    clen = maxByte - position;
                    if (clen > len)
                        clen = len;
                    Array.Copy(byteBuffer, position, b, off, clen);
                    position += clen;
                    off += clen;
                    len -= clen;
                }
                else if (isEOFInBuffer)
                {
                    position = maxByte + 1; // Set position to EOF
                    throw new EndOfStreamException();
                }
                else
                {
                    // Buffer empty => get more data
                    readNewBuffer(offset + position);
                }
            }
        }

        /// <summary> Writes a byte to the stream. Prior to writing, the stream is
        /// realigned at the byte level.
        /// 
        /// </summary>
        /// <param name="b">The byte to write. The lower 8 bits of <tt>b</tt> are
        /// written.
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public void write(int b)
        {
            // As long as pos is less than the length of the buffer we can write
            // to the buffer. If the position is after the buffer a new buffer is
            // needed
            if (position < byteBuffer.Length)
            {
                if (isReadOnly)
                    throw new IOException("File is read only");
                byteBuffer[position] = (byte)b;
                if (position >= maxByte)
                {
                    maxByte = position + 1;
                }
                position++;
                byteBufferChanged = true;
            }
            else
            {
                readNewBuffer(offset + position);
                write(b);
            }
        }

        /// <summary> Writes a byte to the stream. Prior to writing, the stream is
        /// realigned at the byte level.
        /// 
        /// </summary>
        /// <param name="b">The byte to write.
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public void write(byte b)
        {
            // As long as pos is less than the length of the buffer we can write
            // to the buffer. If the position is after the buffer a new buffer is
            // needed
            if (position < byteBuffer.Length)
            {
                if (isReadOnly)
                    throw new IOException("File is read only");
                byteBuffer[position] = b;
                if (position >= maxByte)
                {
                    maxByte = position + 1;
                }
                position++;
                byteBufferChanged = true;
            }
            else
            {
                readNewBuffer(offset + position);
                write(b);
            }
        }

        /// <summary> Writes aan array of bytes to the stream. Prior to writing, the stream is
        /// realigned at the byte level.
        /// 
        /// </summary>
        /// <param name="b">The array of bytes to write. 
        /// 
        /// </param>
        /// <param name="offset">The first byte in b to write 
        /// 
        /// </param>
        /// <param name="length">The number of bytes from b to write 
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public void write(byte[] b, int offset, int length)
        {
            int i, stop;
            stop = offset + length;
            if (stop > b.Length)
                throw new IndexOutOfRangeException($"Index of bound {b.Length}");
            for (i = offset; i < stop; i++)
            {
                write(b[i]);
            }
        }

        /// <summary> Writes the byte value of <tt>v</tt> (i.e., 8 least
        /// significant bits) to the output. Prior to writing, the output
        /// should be realigned at the byte level.
        /// 
        /// Signed or unsigned data can be written. To write a signed
        /// value just pass the <tt>byte</tt> value as an argument. To
        /// write unsigned data pass the <tt>int</tt> value as an argument
        /// (it will be automatically casted, and only the 8 least
        /// significant bits will be written).
        /// 
        /// </summary>
        /// <param name="v">The value to write to the output
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public void writeByte(int v)
        {
            write(v);
        }

        /// <summary> Any data that has been buffered must be written (including
        /// buffering at the bit level), and the stream should be realigned
        /// at the byte level.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public void flush()
        {
            if (byteBufferChanged)
            {
                theFile.Seek(offset, SeekOrigin.Begin);
                theFile.Write(byteBuffer, 0, maxByte);
                byteBufferChanged = false;
            }
        }
        /*
		/// <summary> Reads a signed byte (i.e., 8 bit) from the input. Prior to 
		/// reading, the input should be realigned at the byte level.
		/// 
		/// </summary>
		/// <returns> The next byte-aligned signed byte (8 bit) from the
		/// input.
		/// 
		/// </returns>
		/// <exception cref="java.io.EOFException">If the end-of file was reached before
		/// getting all the necessary data.
		/// 
		/// </exception>
		/// <exception cref="java.io.IOException">If an I/O error ocurred.
		/// 
		/// </exception>
		public byte readByte()
		{
			if (pos < maxByte)
			{
				// The byte can be read from the buffer
				// In Java, the bytes are always signed.
				return byteBuffer[pos++];
			}
			else if (isEOFInBuffer)
			{
				// EOF is reached
				pos = maxByte + 1; // Set position to EOF
				throw new System.IO.EndOfStreamException();
			}
			else
			{
				// End of the buffer is reached
				readNewBuffer(offset + pos);
				return readByte();
			}
		}
		
		/// <summary> Reads an unsigned byte (i.e., 8 bit) from the input. It is
		/// returned as an <tt>int</tt> since Java does not have an
		/// unsigned byte type. Prior to reading, the input should be
		/// realigned at the byte level.
		/// 
		/// </summary>
		/// <returns> The next byte-aligned unsigned byte (8 bit) from the
		/// input, as an <tt>int</tt>.
		/// 
		/// </returns>
		/// <exception cref="java.io.EOFException">If the end-of file was reached before
		/// getting all the necessary data.
		/// 
		/// </exception>
		/// <exception cref="java.io.IOException">If an I/O error ocurred.
		/// 
		/// </exception>
		public int readUnsignedByte()
		{
			return read();
		}
		*/
        /// <summary> Skips <tt>n</tt> bytes from the input. Prior to skipping, the
        /// input should be realigned at the byte level.
        /// 
        /// </summary>
        /// <param name="n">The number of bytes to skip
        /// 
        /// </param>
        /// <exception cref="java.io.EOFException">If the end-of file was reached before
        /// all the bytes could be skipped.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual int skipBytes(int n)
        {
            if (n < 0)
                throw new ArgumentException("Can not skip negative number of bytes");
            if (n <= (maxByte - position))
            {
                position += n;
                return n;
            }
            else
            {
                seek(offset + position + n);
                return n;
            }
        }

        /// <summary> Returns a string of information about the file
        /// 
        /// </summary>
        public override string ToString()
        {
            return $"BufferedRandomAccessFile: {fileName} ({((isReadOnly) ? "read only" : "read/write")})";
        }
        public abstract int readUnsignedShort();
        public abstract void writeLong(long param1);
        public abstract void writeShort(int param1);
        public abstract float readFloat();
        public abstract short readShort();
        public abstract double readDouble();
        public abstract int readInt();
        public abstract long readLong();
        public abstract long readUnsignedInt();
        public abstract void writeDouble(double param1);
        public abstract void writeFloat(float param1);
        public abstract void writeInt(int param1);
    }
}