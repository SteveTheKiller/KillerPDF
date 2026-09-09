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
using CoreJ2K.j2k.util;
using System;

namespace CoreJ2K.j2k.codestream.writer
{

    /// <summary> This class implements a buffer for writing bits, with the required bit
    /// stuffing policy for the packet headers. The bits are stored in a byte array
    /// in the order in which they are written. The byte array is automatically
    /// reallocated and enlarged whenever necessary. A BitOutputBuffer object may
    /// be reused by calling its 'reset()' method.
    /// 
    /// NOTE: The methods implemented in this class are intended to be used only
    /// in writing packet heads, since a special bit stuffing procedure is used, as
    /// required for the packet heads.
    /// 
    /// </summary>
    public class BitOutputBuffer
    {
        /// <summary> Returns the current length of the buffer, in bytes.
        /// 
        /// This method is declared final to increase performance.
        /// 
        /// </summary>
        /// <returns> The currebt length of the buffer in bytes.
        /// 
        /// </returns>
        public virtual int Length
        {
            get
            {
                if (avbits == 8)
                {
                    // A integral number of bytes
                    return curbyte;
                }
                else
                {
                    // Some bits in last byte
                    return curbyte + 1;
                }
            }

        }
        /// <summary> Returns the byte buffer. This is the internal byte buffer so it should
        /// not be modified. Only the first N elements have valid data, where N is
        /// the value returned by 'getLength()'
        /// 
        /// This method is declared final to increase performance.
        /// 
        /// </summary>
        /// <returns> The internal byte buffer.
        /// 
        /// </returns>
        public virtual byte[] Buffer => buf;

        /// <summary>The buffer where we store the data </summary>
        internal byte[] buf;

        /// <summary>The position of the current byte to write </summary>
        internal int curbyte;

        /// <summary>The number of available bits in the current byte </summary>
        internal int avbits = 8;

        /// <summary>The increment size for the buffer, 16 bytes. This is the
        /// number of bytes that are added to the buffer each time it is
        /// needed to enlarge it.
        /// </summary>
        // This must be always 6 or larger.
        public const int SZ_INCR = 16;

        /// <summary>The initial size for the buffer, 32 bytes. </summary>
        public const int SZ_INIT = 32;

        /// <summary> Creates a new BitOutputBuffer width a buffer of length
        /// 'SZ_INIT'.
        /// 
        /// </summary>
        public BitOutputBuffer()
        {
            buf = new byte[SZ_INIT];
        }

        /// <summary> Resets the buffer. This rewinds the current position to the start of
        /// the buffer and sets all tha data to 0. Note that no new buffer is
        /// allocated, so this will affect any data that was returned by the
        /// 'getBuffer()' method.
        /// 
        /// </summary>
        public virtual void reset()
        {
            //int i;
            // Reinit pointers
            curbyte = 0;
            avbits = 8;
            ArrayUtil.byteArraySet(buf, 0);
        }

        /// <summary> Writes a bit to the buffer at the current position. The value 'bit'
        /// must be either 0 or 1, otherwise it corrupts the bits that have been
        /// already written. The buffer is enlarged, by 'SZ_INCR' bytes, if
        /// necessary.
        /// 
        /// This method is declared final to increase performance.
        /// 
        /// </summary>
        /// <param name="bit">The bit to write, 0 or 1.
        /// 
        /// </param>
        public void writeBit(int bit)
        {
            buf[curbyte] |= (byte)(bit << --avbits);
            if (avbits > 0)
            {
                // There is still place in current byte for next bit
                return;
            }
            else
            {
                // End of current byte => goto next
                // We don't need bit stuffing
                avbits = buf[curbyte] != 0xFF ? 8 :
                    // We need to stuff a bit (next MSBit is 0)
                    7;
                curbyte++;
                if (curbyte == buf.Length)
                {
                    // We are at end of 'buf' => extend it
                    var oldbuf = buf;
                    buf = new byte[oldbuf.Length + SZ_INCR];
                    Array.Copy(oldbuf, 0, buf, 0, oldbuf.Length);
                }
            }
        }

        /// <summary> Writes the n least significant bits of 'bits' to the buffer at the
        /// current position. The least significant bit is written last. The 32-n
        /// most significant bits of 'bits' must be 0, otherwise corruption of the
        /// buffer will result. The buffer is enlarged, by 'SZ_INCR' bytes, if
        /// necessary.
        /// 
        /// This method is declared final to increase performance.
        /// 
        /// </summary>
        /// <param name="bits">The bits to write.
        /// 
        /// </param>
        /// <param name="n">The number of LSBs in 'bits' to write.
        /// 
        /// </param>
        public void writeBits(int bits, int n)
        {
            // Check that we have enough place in 'buf' for n bits, and that we do
            // not fill last byte, taking into account possibly stuffed bits (max
            // 2)
            if (((buf.Length - curbyte) << 3) - 8 + avbits <= n + 2)
            {
                // Not enough place, extend it
                var oldbuf = buf;
                buf = new byte[oldbuf.Length + SZ_INCR];
                Array.Copy(oldbuf, 0, buf, 0, oldbuf.Length);
                // SZ_INCR is always 6 or more, so it is enough to hold all the
                // new bits plus the ones to come after
            }
            // Now write the bits
            if (n >= avbits)
            {
                // Complete the current byte
                n -= avbits;
                buf[curbyte] |= (byte)(bits >> n);
                // We don't need bit stuffing
                avbits = buf[curbyte] != 0xFF ? 8 :
                    // We need to stuff a bit (next MSBit is 0)
                    7;
                curbyte++;
                // Write whole bytes
                while (n >= avbits)
                {
                    n -= avbits;
                    // CONVERSION PROBLEM?
                    buf[curbyte] |= (byte)((bits >> n) & (~(1 << avbits)));
                    // We don't need bit
                    // stuffing
                    avbits = buf[curbyte] != 0xFF ? 8 :
                        // We need to stuff a bit (next MSBit is 0)
                        7;
                    curbyte++;
                }
            }
            // Finish last byte (we know that now n < avbits)
            if (n > 0)
            {
                avbits -= n;
                buf[curbyte] |= (byte)((bits & ((1 << n) - 1)) << avbits);
            }
            if (avbits == 0)
            {
                // Last byte is full
                // We don't need bit stuffing
                avbits = buf[curbyte] != 0xFF ? 8 :
                    // We need to stuff a bit (next MSBit is 0)
                    7;
                curbyte++; // We already ensured that we have enough place
            }
        }

        /// <summary> Returns the byte buffer data in a new array. This is a copy of the
        /// internal byte buffer. If 'data' is non-null it is used to return the
        /// data. This array should be large enough to contain all the data,
        /// otherwise a IndexOutOfBoundsException is thrown by the Java system. The
        /// number of elements returned is what 'getLength()' returns.
        /// 
        /// </summary>
        /// <param name="data">If non-null this array is used to return the data, which
        /// mus be large enough. Otherwise a new one is created and returned.
        /// 
        /// </param>
        /// <returns> The byte buffer data.
        /// 
        /// </returns>
        public virtual byte[] toByteArray(byte[] data)
        {
            if (data == null)
            {
                data = new byte[(avbits == 8) ? curbyte : curbyte + 1];
            }
            Array.Copy(buf, 0, data, 0, (avbits == 8) ? curbyte : curbyte + 1);
            return data;
        }

        /// <summary> Prints information about this object for debugging purposes
        /// 
        /// </summary>
        /// <returns> Information about the object.
        /// 
        /// </returns>
        public override string ToString()
        {
            return $"bits written = {(curbyte * 8 + (8 - avbits))}, curbyte = {curbyte}, avbits = {avbits}";
        }
    }
}