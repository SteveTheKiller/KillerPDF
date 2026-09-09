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

namespace CoreJ2K.j2k.entropy.decoder
{

    /// <summary> This class provides an adapter to perform bit based input on byte based
    /// output obejcts that inherit from a 'ByteInputBuffer' class. This class also
    /// performs the bit unstuffing procedure specified for the 'selective
    /// arithmetic coding bypass' mode of the JPEG 2000 entropy coder.
    /// 
    /// </summary>
    public class ByteToBitInput
    {

        /// <summary>The byte based input </summary>
        internal ByteInputBuffer inputBuffer;

        /// <summary>The bit buffer </summary>
        internal int bbuf;

        /// <summary>The position of the next bit to get from the byte buffer. When it is
        /// -1 the bit buffer is empty. 
        /// </summary>
        internal int bpos = -1;

        /// <summary> Instantiates a new 'ByteToBitInput' object that uses 'in' as the
        /// underlying byte based input.
        /// 
        /// </summary>
        /// <param name="in">The underlying byte based input.
        /// 
        /// </param>
        public ByteToBitInput(ByteInputBuffer inputBuffer)
        {
            this.inputBuffer = inputBuffer;
        }

        /// <summary> Reads from the bit stream one bit. If 'bpos' is -1 then a byte is read
        /// and loaded into the bit buffer, from where the bit is read. If
        /// necessary the bit unstuffing will be applied.
        /// 
        /// </summary>
        /// <returns> The read bit (0 or 1).
        /// 
        /// </returns>
        public int readBit()
        {
            if (bpos < 0)
            {
                if ((bbuf & 0xFF) != 0xFF)
                {
                    // Normal byte to read
                    bbuf = inputBuffer.read();
                    bpos = 7;
                }
                else
                {
                    // Previous byte is 0xFF => there was bit stuffing
                    bbuf = inputBuffer.read();
                    bpos = 6;
                }
            }
            return (bbuf >> bpos--) & 0x01;
        }

        /// <summary> Checks for past errors in the decoding process by verifying the byte
        /// padding with an alternating sequence of 0's and 1's. If an error is
        /// detected it means that the raw bit stream has been wrongly decoded or
        /// that the raw terminated segment length is too long. If no errors are
        /// detected it does not necessarily mean that the raw bit stream has been
        /// correctly decoded.
        /// 
        /// </summary>
        /// <returns> True if errors are found, false otherwise.
        /// 
        /// </returns>
        public virtual bool checkBytePadding()
        {
            int seq; // Byte padding sequence in last byte

            // If there are no spare bits and bbuf is 0xFF (not EOF), then there
            // is a next byte with bit stuffing that we must load.
            if (bpos < 0 && (bbuf & 0xFF) == 0xFF)
            {
                bbuf = inputBuffer.read();
                bpos = 6;
            }

            // 1) Not yet read bits in the last byte must be an alternating
            // sequence of 0s and 1s, starting with 0.
            if (bpos >= 0)
            {
                seq = bbuf & ((1 << (bpos + 1)) - 1);
                if (seq != (0x55 >> (7 - bpos)))
                    return true;
            }

            // 2) We must have already reached the last byte in the terminated
            // segment, unless last bit read is LSB of FF in which case an encoder
            // can output an extra byte which is smaller than 0x80.
            if (bbuf != -1)
            {
                if (bbuf == 0xFF && bpos == 0)
                {
                    if ((inputBuffer.read() & 0xFF) >= 0x80)
                        return true;
                }
                else
                {
                    if (inputBuffer.read() != -1)
                        return true;
                }
            }

            // Nothing detected
            return false;
        }

        /// <summary> Flushes (i.e. empties) the bit buffer, without loading any new
        /// bytes. This realigns the input at the next byte boundary, if not
        /// already at one.
        /// 
        /// </summary>
        internal void flush()
        {
            bbuf = 0; // reset any bit stuffing state
            bpos = -1;
        }

        /// <summary> Resets the underlying byte input to start a new segment. The bit buffer
        /// is flushed.
        /// 
        /// </summary>
        /// <param name="buf">The byte array containing the byte data. If null the
        /// current byte array is assumed.
        /// 
        /// </param>
        /// <param name="off">The index of the first element in 'buf' to be decoded. If
        /// negative the byte just after the previous segment is assumed, only
        /// valid if 'buf' is null.
        /// 
        /// </param>
        /// <param name="len">The number of bytes in 'buf' to be decoded. Any subsequent
        /// bytes are taken to be 0xFF.
        /// 
        /// </param>
        internal void SetByteArray(byte[]? buf, int off, int len)
        {
            inputBuffer.SetByteArray(buf, off, len);
            bbuf = 0; // reset any bit stuffing state
            bpos = -1;
        }
    }
}