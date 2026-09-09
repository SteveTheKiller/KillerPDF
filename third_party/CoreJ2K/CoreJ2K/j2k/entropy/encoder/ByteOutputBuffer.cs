/*
*
*                          the old jj2000.j2k.io.ByteArrayOutput class by
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
namespace CoreJ2K.j2k.entropy.encoder
{

    /// <summary> This class provides a buffering output stream similar to
    /// ByteArrayOutputStream, with some additional methods.
    /// 
    /// Once an array has been written to an output stream or to a byte array,
    /// the object can be reused as a new stream if the reset() method is
    /// called.
    /// 
    /// Unlike the ByteArrayOutputStream class, this class is not thread
    /// safe.
    /// 
    /// </summary>
    /// <seealso cref="reset" />
    public class ByteOutputBuffer
    {

        /// <summary>The buffer where the data is stored </summary>
        internal byte[] buf;

        /// <summary>The number of valid bytes in the buffer </summary>
        internal int count;

        /// <summary>The buffer increase size </summary>
        public const int BUF_INC = 512;

        /// <summary>The default initial buffer size </summary>
        public const int BUF_DEF_LEN = 256;

        /// <summary> Creates a new byte array output stream. The buffer capacity is
        /// initially BUF_DEF_LEN bytes, though its size increases if necessary.
        /// 
        /// </summary>
        public ByteOutputBuffer()
        {
            buf = new byte[BUF_DEF_LEN];
        }

        /// <summary> Creates a new byte array output stream, with a buffer capacity of the
        /// specified size, in bytes.
        /// 
        /// </summary>
        /// <param name="size">the initial size.
        /// 
        /// </param>
        public ByteOutputBuffer(int size)
        {
            buf = new byte[size];
        }

        /// <summary> Writes the specified byte to this byte array output stream. The
        /// functionality provided by this implementation is the same as for the
        /// one in the superclass, however this method is not synchronized and
        /// therefore not safe thread, but faster.
        /// 
        /// </summary>
        /// <param name="b">The byte to write
        /// 
        /// </param>
        public void write(int b)
        {
            if (count == buf.Length)
            {
                // Resize buffer
                var tmpbuf = buf;
                buf = new byte[buf.Length + BUF_INC];
                Array.Copy(tmpbuf, 0, buf, 0, count);
            }
            buf[count++] = (byte)b;
        }

        /// <summary> Copies the specified part of the stream to the 'outbuf' byte array.
        /// 
        /// </summary>
        /// <param name="off">The index of the first element in the stream to copy.
        /// 
        /// </param>
        /// <param name="len">The number of elements of the array to copy
        /// 
        /// </param>
        /// <param name="outbuf">The destination array
        /// 
        /// </param>
        /// <param name="outoff">The index of the first element in 'outbuf' where to write
        /// the data.
        /// 
        /// </param>
        public virtual void toByteArray(int off, int len, byte[] outbuf, int outoff)
        {
            // Copy the data
            Array.Copy(buf, off, outbuf, outoff, len);
        }

        /// <summary> Returns the number of valid bytes in the output buffer (count class
        /// variable).
        /// 
        /// </summary>
        /// <returns> The number of bytes written to the buffer
        /// 
        /// </returns>
        public virtual int size()
        {
            return count;
        }

        /// <summary> Discards all the buffered data, by resetting the counter of written
        /// bytes to 0.
        /// 
        /// </summary>
        public virtual void reset()
        {
            count = 0;
        }

        /// <summary> Returns the byte buffered at the given position in the buffer. The
        /// position in the buffer is the index of the 'write()' method call after
        /// the last call to 'reset()'.
        /// 
        /// </summary>
        /// <param name="pos">The position of the byte to return
        /// 
        /// </param>
        /// <returns> The value (betweeb 0-255) of the byte at position 'pos'.
        /// 
        /// </returns>
        public virtual int GetByte(int pos)
        {
            if (pos >= count)
            {
                throw new ArgumentException();
            }
            return buf[pos] & 0xFF;
        }
    }
}