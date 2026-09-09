/*
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
using CoreJ2K.j2k.io;
using System;

namespace CoreJ2K.j2k.util
{

    /// <summary> This class implements a wrapper to turn an InputStream into a
    /// RandomAccessIO. To provide random access, the input data from the
    /// InputStream is cached in an in-memory buffer. The in-memory buffer size can
    /// be limited to a specified size. The data is read into the cache on a as
    /// needed basis, blocking only when necessary.
    /// 
    /// The cache grows automatically as necessary. However, if the data length
    /// is known prior to the creation of a ISRandomAccessIO object, it is best to
    /// specify that as the initial in-memory buffer size. That will minimize data
    /// copying and multiple allocation.
    /// 
    /// Multi-byte data is read in big-endian order. The in-memory buffer
    /// storage is released when 'close()' is called. This class can only be used
    /// for data input, not output. The wrapped InputStream is closed when all the
    /// input data is cached or when 'close()' is called.
    /// 
    /// If an out of memory condition is encountered when growing the in-memory
    /// buffer an IOException is thrown instead of an OutOfMemoryError. The
    /// exception message is "Out of memory to cache input data".
    /// 
    /// This class is intended for use as a "quick and dirty" way to give
    /// network connectivity to RandomAccessIO based classes. It is not intended as
    /// an efficient means of implementing network connectivity. Doing such
    /// requires reimplementing the RandomAccessIO based classes to directly use
    /// network connections.
    /// 
    /// This class does not use temporary files as buffers, because that would
    /// preclude the use in unsigned applets.
    /// 
    /// </summary>
    public class ISRandomAccessIO : RandomAccessIO
    {
        /// <summary> Returns the current position in the stream, which is the position from
        /// where the next byte of data would be read. The first byte in the stream
        /// is in position 0.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error occurred.
        /// 
        /// </exception>
        public virtual int Pos => pos;

        /// <summary> Returns the endianess (i.e., byte ordering) of multi-byte I/O
        /// operations. Always EndianType.BIG_ENDIAN since this class implements
        /// only big-endian.
        /// 
        /// </summary>
        /// <returns> Always EndianType.BIG_ENDIAN.
        /// 
        /// </returns>
        /// <seealso cref="EndianType" />
        public virtual int ByteOrdering => EndianType_Fields.BIG_ENDIAN;

        /// <summary>The InputStream that is wrapped </summary>
        private System.IO.Stream inputStream;

        /* Tha maximum size, in bytes, of the in memory buffer. The maximum size
		* includes the EOF. */
        private readonly int maxsize;

        /* The increment, in bytes, for the in-memory buffer size */
        private readonly int inc;

        /* The in-memory buffer to cache received data */
        private byte[] buf;

        /* The length of the already received data */
        private int len;

        /* The position of the next byte to be read from the in-memory buffer */
        private int pos;

        /* Flag to indicate if all the data has been received. That is, if the EOF 
		* has been reached. */
        private bool complete;

        /// <summary> Creates a new RandomAccessIO wrapper for the given InputStream
        /// 'is'. The internal cache buffer will have size 'size' and will
        /// increment by 'inc' each time it is needed. The maximum buffer size is
        /// limited to 'maxsize'.
        /// 
        /// </summary>
        /// <param name="is">The input from where to get the data.
        /// 
        /// </param>
        /// <param name="size">The initial size for the cache buffer, in bytes.
        /// 
        /// </param>
        /// <param name="inc">The size increment for the cache buffer, in bytes.
        /// 
        /// </param>
        /// <param name="maxsize">The maximum size for the cache buffer, in bytes.
        /// 
        /// </param>
        public ISRandomAccessIO(System.IO.Stream inputStream, int size, int inc, int maxsize)
        {
            if (size < 0 || inc <= 0 || maxsize <= 0 || inputStream == null)
            {
                throw new ArgumentException();
            }
            this.inputStream = inputStream;
            // Increase size by one to count in EOF
            if (size < int.MaxValue)
                size++;
            buf = new byte[size];
            this.inc = inc;
            // The maximum size is one byte more, to allow reading the EOF.
            if (maxsize < int.MaxValue)
                maxsize++;
            this.maxsize = maxsize;
            pos = 0;
            len = 0;
            complete = false;
        }

        /// <summary> Creates a new RandomAccessIO wrapper for the given InputStream
        /// 'is'. The internal cache buffer size and increment is to to 256 kB. The
        /// maximum buffer size is set to Integer.MAX_VALUE (2 GB).
        /// If 'is' is a <see cref="System.IO.MemoryStream"/> with an exposed backing
        /// array the buffer is borrowed directly (zero copy). Otherwise, if the full
        /// length is known upfront the entire content is read eagerly in one shot so
        /// that all subsequent reads hit the in-memory fast path and never call back
        /// into the stream.
        /// 
        /// </summary>
        /// <param name="is">The input from where to get the data.
        /// 
        /// </param>
        public ISRandomAccessIO(System.IO.Stream inputStream)
        {
            if (inputStream == null)
                throw new ArgumentException();

            // Fast-path: MemoryStream with an accessible backing array at offset 0 —
            // borrow it directly so no copy is needed at all.
            if (inputStream is System.IO.MemoryStream ms && ms.TryGetBuffer(out var segment)
                && segment.Offset == 0)
            {
                buf = segment.Array!;
                pos = (int)ms.Position; // logical stream position, equals buf index when offset==0
                len = segment.Count;
                complete = true;
                this.inputStream = null;
                inc = 1 << 18;
                maxsize = int.MaxValue;
                return;
            }

            // When the full length is known upfront (e.g. a seekable stream) bulk-load
            // everything now so that readInput() is never called during decode.
            if (inputStream.CanSeek && inputStream.CanRead && inputStream.Length <= int.MaxValue - 1)
            {
                int streamLen = (int)(inputStream.Length - inputStream.Position);
                // +1 to account for the EOF sentinel used by readInput()
                buf = new byte[streamLen + 1];
                int offset = 0;
                int remaining = streamLen;
                while (remaining > 0)
                {
                    int read = inputStream.Read(buf, offset, remaining);
                    if (read <= 0) break;
                    offset += read;
                    remaining -= read;
                }
                len = offset;
                complete = true;
                // Do not dispose: the caller owns the stream's lifetime.
                this.inputStream = null;
                inc = 1 << 18;
                maxsize = int.MaxValue;
                pos = 0;
                return;
            }

            // Fallback: incremental buffering for non-seekable or very large streams.
            this.inputStream = inputStream;
            int size = (1 << 18) + 1;
            buf = new byte[size];
            inc = 1 << 18;
            maxsize = int.MaxValue;
            pos = 0;
            len = 0;
            complete = false;
        }

        /// <summary> Grows the cache buffer by 'inc', upto a maximum of 'maxsize'. The
        /// buffer size will be increased by at least one byte, if no exception is
        /// thrown.
        /// 
        /// </summary>
        /// <exception cref="IOException">If the maximum cache size is reached or if not
        /// enough memory is available to grow the buffer.
        /// 
        /// </exception>
        private void growBuffer()
        {
            byte[] newbuf;
            int effinc; // effective increment

            effinc = inc;
            if (buf.Length + effinc > maxsize)
                effinc = maxsize - buf.Length;
            if (effinc <= 0)
            {
                throw new System.IO.IOException($"Reached maximum cache size ({maxsize})");
            }
            try
            {
                newbuf = new byte[buf.Length + inc];
            }
            catch (OutOfMemoryException)
            {
                throw new System.IO.IOException("Out of memory to cache input data");
            }
            Array.Copy(buf, 0, newbuf, 0, len);
            buf = newbuf;
        }

        /// <summary> Reads data from the wrapped InputStream and places it in the cache
        /// buffer. Reads all input data that will not cause it to block, but at
        /// least on byte is read (even if it blocks), unless EOF is reached. This
        /// method can not be called if EOF has been already reached
        /// (i.e. 'complete' is true). The wrapped InputStream is closed if the EOF
        /// is reached.
        /// 
        /// </summary>
        /// <exception cref="IOException">An I/O error occurred, out of meory to grow
        /// cache or maximum cache size reached.
        /// 
        /// </exception>
        private void readInput()
        {
            int n;
            //int b;
            int k;

            if (complete)
            {
                throw new ArgumentException("Already reached EOF");
            }
            long available;
            available = inputStream.Length - inputStream.Position;
            n = (int)available; /* how much can we read without blocking? */
            if (n == 0)
                n = 1; /* read at least one byte (even if it blocks) */
            while (len + n > buf.Length)
            {
                /* Ensure buffer size */
                growBuffer();
            }
            /* Read the data. Loop to be sure that we do read 'n' bytes */
            do
            {
                // CONVERSION PROBLEM? OPTIMIZE!!!
                k = inputStream.Read(buf, len, n);
                if (k > 0)
                {
                    /* Some data was read */
                    len += k;
                    n -= k;
                }
            }
            while (n > 0 && k > 0);
            if (k <= 0)
            {
                /* we reached EOF */
                complete = true;
                inputStream.Dispose();
                inputStream = null;
            }
        }

        /// <summary> Closes this object for reading as well as the wrapped InputStream, if
        /// not already closed. The memory used by the cache is released.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error occurs while closing the
        /// underlying InputStream.  
        /// 
        /// </exception>
        public virtual void Close()
        {
            buf = null;
            if (!complete)
            {
                inputStream.Dispose();
                inputStream = null;
            }
        }

        /// <summary>
        /// Releases the resources held by this object. Calls <see cref="Close"/>.
        /// </summary>
        public void Dispose()
        {
            Close();
            GC.SuppressFinalize(this);
        }

        /// <summary> Moves the current position for the next read operation to offset. The
        /// offset is measured from the beginning of the stream. If the offset is
        /// set beyond the currently cached data, the missing data will be read
        /// only when a read operation is performed. Setting the offset beyond the
        /// end of the data will cause an EOFException only if the data length is
        /// currently known, otherwise an IOException will occur when a read
        /// operation is attempted at that position.
        /// 
        /// </summary>
        /// <param name="off">The offset where to move to.
        /// 
        /// </param>
        /// <exception cref="EOFException">If seeking beyond EOF and the data length is
        /// known.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual void seek(int off)
        {
            if (complete)
            {
                /* we know the length, check seek is within length */
                if (off > len)
                {
                    throw new System.IO.EndOfStreamException();
                }
            }
            pos = off;
        }

        /// <summary> Returns the length of the stream. This will cause all the data to be
        /// read. This method will block until all the data is read, which can be
        /// lengthy across the network.
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
            while (!complete)
            {
                /* read until we reach EOF */
                readInput();
            }
            return len;
        }

        /// <summary> Reads a byte of data from the stream.
        /// 
        /// </summary>
        /// <returns> The byte read, as an int in the range [0-255].
        /// 
        /// </returns>
        /// <exception cref="EOFException">If the end-of file was reached.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual byte readByte() { return read(); }
        public virtual byte read()
        {
            if (pos < len)
            {
                // common, fast case
                return buf[pos++];
            }
            // general case
            while (!complete && pos >= len)
            {
                readInput();
            }
            if (pos == len)
            {
                throw new System.IO.EndOfStreamException();
            }
            else if (pos > len)
            {
                throw new System.IO.IOException("Position beyond EOF");
            }
            return buf[pos++];
        }

        /// <summary> Reads 'len' bytes of data from this file into an array of bytes. This
        /// method reads repeatedly from the stream until all the bytes are
        /// read. This method blocks until all the bytes are read, the end of the
        /// stream is detected, or an exception is thrown.
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
        /// <exception cref="EOFException">If the end-of file was reached before getting
        /// all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual void readFully(byte[] b, int off, int n)
        {
            // Validate parameters to prevent buffer overrun
            if (b == null)
            {
                throw new ArgumentNullException(nameof(b), "Buffer cannot be null");
            }
            if (off < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(off), off, "Offset cannot be negative");
            }
            if (n < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(n), n, "Length cannot be negative");
            }
            if (off + n > b.Length)
            {
                throw new ArgumentOutOfRangeException(
                    $"Buffer overrun: offset={off}, length={n}, buffer size={b.Length}. " +
                    $"Attempting to write beyond buffer boundary.");
            }

            if (pos + n <= len)
            {
                // common, fast case
                Array.Copy(buf, pos, b, off, n);
                pos += n;
                return;
            }
            // general case
            while (!complete && pos + n > len)
            {
                readInput();
            }
            if (pos + n > len)
            {
                throw new System.IO.EndOfStreamException();
            }
            Array.Copy(buf, pos, b, off, n);
            pos += n;
        }

        /// <summary> Reads an unsigned byte (8 bit) from the input.
        /// 
        /// </summary>
        /// <returns> The next byte-aligned unsigned byte (8 bit) from the input.
        /// 
        /// </returns>
        /// <exception cref="EOFException">If the end-of file was reached before getting
        /// all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual byte readUnsignedByte()
        {
            return pos < len ?
                // common, fast case
                buf[pos++] :
                // general case
                read();
        }

        /// <summary> Reads a signed short (16 bit) from the input.
        /// 
        /// </summary>
        /// <returns> The next byte-aligned signed short (16 bit) from the input.
        /// 
        /// </returns>
        /// <exception cref="EOFException">If the end-of file was reached before getting
        /// all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual short readShort()
        {
            if (pos + 1 < len)
            {
                // common, fast case
                return (short)((buf[pos++] << 8) | (0xFF & buf[pos++]));
            }
            // general case
            return (short)((read() << 8) | read());
        }

        /// <summary> Reads an unsigned short (16 bit) from the input.
        /// 
        /// </summary>
        /// <returns> The next byte-aligned unsigned short (16 bit) from the input.
        /// 
        /// </returns>
        /// <exception cref="EOFException">If the end-of file was reached before getting
        /// all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual int readUnsignedShort()
        {
            if (pos + 1 < len)
            {
                // common, fast case
                return ((0xFF & buf[pos++]) << 8) | (0xFF & buf[pos++]);
            }
            // general case
            return (read() << 8) | read();
        }

        /// <summary> Reads a signed int (32 bit) from the input.
        /// 
        /// </summary>
        /// <returns> The next byte-aligned signed int (32 bit) from the
        /// input.
        /// 
        /// </returns>
        /// <exception cref="EOFException">If the end-of file was reached before getting
        /// all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual int readInt()
        {
            if (pos + 3 < len)
            {
                // common, fast case
                return ((buf[pos++] << 24) | ((0xFF & buf[pos++]) << 16) | ((0xFF & buf[pos++]) << 8) | (0xFF & buf[pos++]));
            }
            // general case
            return (read() << 24) | (read() << 16) | (read() << 8) | read();
        }

        /// <summary> Reads a unsigned int (32 bit) from the input.
        /// 
        /// </summary>
        /// <returns> The next byte-aligned unsigned int (32 bit) from the input.
        /// 
        /// </returns>
        /// <exception cref="EOFException">If the end-of file was reached before getting
        /// all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual long readUnsignedInt()
        {
            if (pos + 3 < len)
            {
                // common, fast case
                return (unchecked((int)0xFFFFFFFFL) & (long)((buf[pos++] << 24) | ((0xFF & buf[pos++]) << 16) | ((0xFF & buf[pos++]) << 8) | (0xFF & buf[pos++])));
            }
            // general case
            return (unchecked((int)0xFFFFFFFFL) & (long)((read() << 24) | (read() << 16) | (read() << 8) | read()));
        }

        /// <summary> Reads a signed long (64 bit) from the input.
        /// 
        /// </summary>
        /// <returns> The next byte-aligned signed long (64 bit) from the input.
        /// 
        /// </returns>
        /// <exception cref="EOFException">If the end-of file was reached before getting
        /// all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual long readLong()
        {
            if (pos + 7 < len)
            {
                // common, fast case
                return (((long)buf[pos++] << 56) | ((long)buf[pos++] << 48) | ((long)buf[pos++] << 40) | ((long)buf[pos++] << 32) | ((long)buf[pos++] << 24) | ((long)buf[pos++] << 16) | ((long)buf[pos++] << 8) | buf[pos++]);
            }
            // general case
            return (((long)read() << 56) | ((long)read() << 48) | ((long)read() << 40) | ((long)read() << 32) | ((long)read() << 24) | ((long)read() << 16) | ((long)read() << 8) | read());
        }

        /// <summary> Reads an IEEE single precision (i.e., 32 bit) floating-point number
        /// from the input.
        /// 
        /// </summary>
        /// <returns> The next byte-aligned IEEE float (32 bit) from the input.
        /// 
        /// </returns>
        /// <exception cref="EOFException">If the end-of file was reached before getting
        /// all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual float readFloat()
        {
            // CONVERSION PROBLEM? BIGENDIAN
            int floatint;
            if (pos + 3 < len)
                floatint = (buf[pos++] << 24) | ((0xFF & buf[pos++]) << 16) | ((0xFF & buf[pos++]) << 8) | (0xFF & buf[pos++]);
            else
                floatint = (read() << 24) | (read() << 16) | (read() << 8) | read();
            return BitConverter.ToSingle(BitConverter.GetBytes(floatint), 0);

            /*
			if (pos + 3 < len)
			{
				// common, fast case
				return Float.intBitsToFloat((buf[pos++] << 24) | ((0xFF & buf[pos++]) << 16) | ((0xFF & buf[pos++]) << 8) | (0xFF & buf[pos++]));
			}
			// general case
			return Float.intBitsToFloat((read() << 24) | (read() << 16) | (read() << 8) | read());
            */
        }

        /// <summary> Reads an IEEE double precision (i.e., 64 bit) floating-point number
        /// from the input.
        /// 
        /// </summary>
        /// <returns> The next byte-aligned IEEE double (64 bit) from the input.
        /// 
        /// </returns>
        /// <exception cref="EOFException">If the end-of file was reached before getting
        /// all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual double readDouble()
        {
            // CONVERSION PROBLEM?  BIGENDIAN
            long doublelong;
            if (pos + 7 < len)
                doublelong = ((long)buf[pos++] << 56) | ((long)buf[pos++] << 48) | ((long)buf[pos++] << 40) | ((long)buf[pos++] << 32) | ((long)buf[pos++] << 24) | ((long)buf[pos++] << 16) | ((long)buf[pos++] << 8) | buf[pos++];
            else
                doublelong = ((long)read() << 56) | ((long)read() << 48) | ((long)read() << 40) | ((long)read() << 32) | ((long)read() << 24) | ((long)read() << 16) | ((long)read() << 8) | read();
            return BitConverter.ToDouble(BitConverter.GetBytes(doublelong), 0);

            /*
			if (pos + 7 < len)
			{
				// common, fast case
				return Double.longBitsToDouble(((long) buf[pos++] << 56) | ((long) (0xFF & buf[pos++]) << 48) | ((long) (0xFF & buf[pos++]) << 40) | ((long) (0xFF & buf[pos++]) << 32) | ((long) (0xFF & buf[pos++]) << 24) | ((long) (0xFF & buf[pos++]) << 16) | ((long) (0xFF & buf[pos++]) << 8) | (long) (0xFF & buf[pos++]));
			}
			// general case
			return Double.longBitsToDouble(((long) read() << 56) | ((long) read() << 48) | ((long) read() << 40) | ((long) read() << 32) | ((long) read() << 24) | ((long) read() << 16) | ((long) read() << 8) | (long) read());
            */
        }

        /// <summary> Skips 'n' bytes from the input.
        /// 
        /// </summary>
        /// <param name="n">The number of bytes to skip
        /// 
        /// </param>
        /// <returns> Always n.
        /// 
        /// </returns>
        /// <exception cref="EOFException">If the end-of file was reached before all the
        /// bytes could be skipped.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual int skipBytes(int n)
        {
            if (complete)
            {
                /* we know the length, check skip is within length */
                if (pos + n > len)
                {
                    throw new System.IO.EndOfStreamException();
                }
            }
            pos += n;
            return n;
        }

        /// <summary> Does nothing since this class does not implement data output.  
        /// 
        /// </summary>
        public virtual void flush()
        {
            /* no-op */
        }

        /// <summary> Throws an IOException since this class does not implement data output.
        /// 
        /// </summary>
        public virtual void write(byte b)
        {
            throw new System.IO.IOException("read-only");
        }

        /// <summary> Throws an IOException since this class does not implement data output.
        /// 
        /// </summary>
        public virtual void writeByte(int v)
        {
            throw new System.IO.IOException("read-only");
        }

        /// <summary> Throws an IOException since this class does not implement data output.
        /// 
        /// </summary>
        public virtual void writeShort(int v)
        {
            throw new System.IO.IOException("read-only");
        }

        /// <summary> Throws an IOException since this class does not implement data output.
        /// 
        /// </summary>
        public virtual void writeInt(int v)
        {
            throw new System.IO.IOException("read-only");
        }

        /// <summary> Throws an IOException since this class does not implement data output.
        /// 
        /// </summary>
        public virtual void writeLong(long v)
        {
            throw new System.IO.IOException("read-only");
        }

        /// <summary> Throws an IOException since this class does not implement data output.
        /// 
        /// </summary>
        public virtual void writeFloat(float v)
        {
            throw new System.IO.IOException("read-only");
        }

        /// <summary> Throws an IOException since this class does not implement data output.
        /// 
        /// </summary>
        public virtual void writeDouble(double v)
        {
            throw new System.IO.IOException("read-only");
        }
    }
}