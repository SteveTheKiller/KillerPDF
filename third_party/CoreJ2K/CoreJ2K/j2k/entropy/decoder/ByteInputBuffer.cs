/*
*
*                          the old jj2000.j2k.io.ByteArrayInput class by
*                          Diego SANTA CRUZ, Apr-26-1999
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
using System;
namespace CoreJ2K.j2k.entropy.decoder
{

    /// <summary> This class provides a byte input facility from byte buffers. It is similar
    /// to the ByteArrayInputStream class, but adds the possibility to add data to
    /// the stream after the creation of the object.
    /// 
    /// Unlike the ByteArrayInputStream this class is not thread safe (i.e. no
    /// two threads can use the same object at the same time, but different objects
    /// may be used in different threads).
    /// 
    /// This class can modify the contents of the buffer given to the
    /// constructor, when the addByteArray() method is called.
    /// 
    /// </summary>
    /// <seealso cref="InputStream" />
    public class ByteInputBuffer
    {

        /// <summary>The byte array containing the data </summary>
        private byte[]? buf;

        /// <summary>The index one greater than the last valid character in the input
        /// stream buffer 
        /// </summary>
        private int count;

        /// <summary>The index of the next character to read from the input stream buffer
        /// 
        /// </summary>
        private int pos;

        /// <summary> Creates a new byte array input stream that reads data from the
        /// specified byte array. The byte array is not copied.
        /// 
        /// </summary>
        /// <param name="buf">the input buffer.
        /// 
        /// </param>
        public ByteInputBuffer(byte[] buf)
        {
            this.buf = buf;
            count = buf.Length;
        }

        /// <summary> Creates a new byte array input stream that reads data from the
        /// specified byte array. Up to length characters are to be read from the
        /// byte array, starting at the indicated offset.
        /// 
        /// The byte array is not copied.
        /// 
        /// </summary>
        /// <param name="buf">the input buffer.
        /// 
        /// </param>
        /// <param name="offset">the offset in the buffer of the first byte to read.
        /// 
        /// </param>
        /// <param name="length">the maximum number of bytes to read from the buffer.
        /// 
        /// </param>
        public ByteInputBuffer(byte[] buf, int offset, int length)
        {
            this.buf = buf;
            pos = offset;
            count = offset + length;
        }

        /// <summary> Sets the underlying buffer byte array to the given one, with the given
        /// offset and length. If 'buf' is null then the current byte buffer is
        /// assumed. If 'offset' is negative, then it will be assumed to be
        /// 'off+len', where 'off' and 'len' are the offset and length of the
        /// current byte buffer.
        /// 
        /// The byte array is not copied.
        /// 
        /// </summary>
        /// <param name="buf">the input buffer. If null it is the current input buffer.
        /// 
        /// </param>
        /// <param name="offset">the offset in the buffer of the first byte to read. If
        /// negative it is assumed to be the byte just after the end of the current
        /// input buffer, only permitted if 'buf' is null.
        /// 
        /// </param>
        /// <param name="length">the maximum number of bytes to read frmo the buffer.
        /// 
        /// </param>
        public virtual void SetByteArray(byte[]? buf, int offset, int length)
        {
            // In same buffer?
            if (buf == null)
            {
                if (length < 0 || count + length > this.buf.Length)
                {
                    throw new ArgumentException();
                }
                if (offset < 0)
                {
                    pos = count;
                    count += length;
                }
                else
                {
                    count = offset + length;
                    pos = offset;
                }
            }
            else
            {
                // New input buffer
                if (offset < 0 || length < 0 || offset + length > buf.Length)
                {
                    throw new ArgumentException();
                }
                this.buf = buf;
                count = offset + length;
                pos = offset;
            }
        }

        /// <summary> Adds the specified data to the end of the byte array stream. This
        /// method modifies the byte array buffer. It can also discard the already
        /// read input.
        /// 
        /// </summary>
        /// <param name="data">The data to add. The data is copied.
        /// 
        /// </param>
        /// <param name="off">The index, in data, of the first element to add to the
        /// stream.
        /// 
        /// </param>
        /// <param name="len">The number of elements to add to the array.
        /// 
        /// </param>
        public virtual void addByteArray(byte[] data, int off, int len)
        {
            lock (this)
            {
                // Check integrity
                if (len < 0 || off < 0 || len + off > buf.Length)
                {
                    throw new ArgumentException();
                }
                // Copy new data
                if (count + len <= buf.Length)
                {
                    // Enough place in 'buf'
                    Array.Copy(data, off, buf, count, len);
                    count += len;
                }
                else
                {
                    if (count - pos + len <= buf.Length)
                    {
                        // Enough place in 'buf' if we move input data
                        // Move buffer
                        Array.Copy(buf, pos, buf, 0, count - pos);
                    }
                    else
                    {
                        // Not enough place in 'buf', use new buffer
                        var oldbuf = buf;
                        buf = new byte[count - pos + len];
                        // Copy buffer
                        Array.Copy(oldbuf, count, buf, 0, count - pos);
                    }
                    count -= pos;
                    pos = 0;
                    // Copy new data
                    Array.Copy(data, off, buf, count, len);
                    count += len;
                }
            }
        }

        /// <summary> Reads the next byte of data from this input stream. The value byte is
        /// returned as an int in the range 0 to 255. If no byte is available
        /// because the end of the stream has been reached, the EOFException
        /// exception is thrown.
        /// 
        /// This method is not synchronized, so it is not thread safe.
        /// 
        /// </summary>
        /// <returns> The byte read in the range 0-255.
        /// 
        /// </returns>
        /// <exception cref="EOFException">If the end of the stream is reached.
        /// 
        /// </exception>
        public virtual int readChecked()
        {
            if (pos < count)
            {
                return buf[pos++] & 0xFF;
            }
            else
            {
                throw new System.IO.EndOfStreamException();
            }
        }

        /// <summary> Reads the next byte of data from this input stream. The value byte is
        /// returned as an int in the range 0 to 255. If no byte is available
        /// because the end of the stream has been reached, -1 is returned.
        /// 
        /// This method is not synchronized, so it is not thread safe.
        /// 
        /// </summary>
        /// <returns> The byte read in the range 0-255, or -1 if the end of stream
        /// has been reached.
        /// 
        /// </returns>
        public virtual int read()
        {
            if (pos < count)
            {
                return buf[pos++] & 0xFF;
            }
            else
            {
                return -1;
            }
        }
    }
}