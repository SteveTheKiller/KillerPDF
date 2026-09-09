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

namespace CoreJ2K.j2k.entropy.encoder
{

    /// <summary> This class provides an adapter to perform bit based output on byte based
    /// output objects that inherit from a 'ByteOutputBuffer' class. This class
    /// implements the bit stuffing policy needed for the 'selective arithmetic
    /// coding bypass' mode of the entropy coder. This class also delays the output
    /// of a trailing 0xFF, since they are synthetized be the decoder.
    /// 
    /// </summary>
    internal class BitToByteOutput
    {
        /// <summary> Set the flag according to whether or not the predictable termination is
        /// requested.
        /// 
        /// </summary>
        /// <param name="isPredTerm">Whether or not predictable termination is requested.
        /// 
        /// </param>
        internal virtual bool PredTerm
        {
            set => isPredTerm = value;
        }

        /// <summary>Whether or not predictable termination is requested. This value is
        /// important when the last byte before termination is an 0xFF  
        /// </summary>
        private bool isPredTerm = false;

        /// <summary>The alternating sequence of 0's and 1's used for byte padding </summary>
        internal const int PAD_SEQ = 0x2A;

        /// <summary>Flag that indicates if an FF has been delayed </summary>
        internal bool delFF = false;

        /// <summary>The byte based output </summary>
        internal ByteOutputBuffer outputBuffer;

        /// <summary>The bit buffer </summary>
        internal int bbuf;

        /// <summary>The position of the next bit to put in the bit buffer. When it is 7
        /// the bit buffer 'bbuf' is empty. The value should always be between 7
        /// and 0 (i.e. if it gets to -1, the bit buffer should be immediately
        /// written to the byte output). 
        /// </summary>
        internal int bpos = 7;

        /// <summary>The number of written bytes (excluding the bit buffer) </summary>
        internal int nb = 0;

        /// <summary> Instantiates a new 'BitToByteOutput' object that uses 'out' as the
        /// underlying byte based output.
        /// 
        /// </summary>
        /// <param name="out">The underlying byte based output
        /// 
        /// </param>
        internal BitToByteOutput(ByteOutputBuffer outputBuffer)
        {
            this.outputBuffer = outputBuffer;
        }

        /// <summary> Writes to the bit stream the symbols contained in the 'symbuf'
        /// buffer. The least significant bit of each element in 'symbuf'is
        /// written.
        /// 
        /// </summary>
        /// <param name="symbuf">The symbols to write
        /// 
        /// </param>
        /// <param name="nsym">The number of symbols in symbuf
        /// 
        /// </param>
        internal void writeBits(int[] symbuf, int nsym)
        {
            int i;
            int bbuf, bpos;
            bbuf = this.bbuf;
            bpos = this.bpos;
            // Write symbol by symbol to bit buffer
            for (i = 0; i < nsym; i++)
            {
                bbuf |= (symbuf[i] & 0x01) << (bpos--);
                if (bpos < 0)
                {
                    // Bit buffer is full, write it
                    if (bbuf != 0xFF)
                    {
                        // No bit-stuffing needed
                        if (delFF)
                        {
                            // Output delayed 0xFF if any
                            outputBuffer.write(0xFF);
                            nb++;
                            delFF = false;
                        }
                        outputBuffer.write(bbuf);
                        nb++;
                        bpos = 7;
                    }
                    else
                    {
                        // We need to do bit stuffing on next byte
                        delFF = true;
                        bpos = 6; // One less bit in next byte
                    }
                    bbuf = 0;
                }
            }
            this.bbuf = bbuf;
            this.bpos = bpos;
        }

        /// <summary> Write a bit to the output. The least significant bit of 'bit' is
        /// written to the output.
        /// 
        /// </summary>
        /// <param name="bit">
        /// </param>
        internal void writeBit(int bit)
        {
            bbuf |= (bit & 0x01) << (bpos--);
            if (bpos < 0)
            {
                if (bbuf != 0xFF)
                {
                    // No bit-stuffing needed
                    if (delFF)
                    {
                        // Output delayed 0xFF if any
                        outputBuffer.write(0xFF);
                        nb++;
                        delFF = false;
                    }
                    // Output the bit buffer
                    outputBuffer.write(bbuf);
                    nb++;
                    bpos = 7;
                }
                else
                {
                    // We need to do bit stuffing on next byte
                    delFF = true;
                    bpos = 6; // One less bit in next byte
                }
                bbuf = 0;
            }
        }

        /// <summary> Writes the contents of the bit buffer and byte aligns the output by
        /// filling bits with an alternating sequence of 0's and 1's.
        /// 
        /// </summary>
        internal virtual void flush()
        {
            if (delFF)
            {
                // There was a bit stuffing
                if (bpos != 6)
                {
                    // Bit buffer is not empty
                    // Output delayed 0xFF
                    outputBuffer.write(0xFF);
                    nb++;
                    delFF = false;
                    // Pad to byte boundary with an alternating sequence of 0's
                    // and 1's.
                    bbuf |= ((PAD_SEQ >>> (6 - bpos)));
                    // Output the bit buffer
                    outputBuffer.write(bbuf);
                    nb++;
                    bpos = 7;
                    bbuf = 0;
                }
                else if (isPredTerm)
                {
                    outputBuffer.write(0xFF);
                    nb++;
                    outputBuffer.write(0x2A);
                    nb++;
                    bpos = 7;
                    bbuf = 0;
                    delFF = false;
                }
            }
            else
            {
                // There was no bit stuffing
                if (bpos != 7)
                {
                    // Bit buffer is not empty
                    // Pad to byte boundary with an alternating sequence of 0's and
                    // 1's.
                    bbuf |= ((PAD_SEQ >>> (6 - bpos)));
                    // Output the bit buffer (bbuf can not be 0xFF)
                    outputBuffer.write(bbuf);
                    nb++;
                    bpos = 7;
                    bbuf = 0;
                }
            }
        }

        /// <summary> Terminates the bit stream by calling 'flush()' and then
        /// 'reset()'. Finally, it returns the number of bytes effectively written.
        /// 
        /// </summary>
        /// <returns> The number of bytes effectively written.
        /// 
        /// </returns>
        public virtual int terminate()
        {
            flush();
            var savedNb = nb;
            reset();
            return savedNb;
        }

        /// <summary> Resets the bit buffer to empty, without writing anything to the
        /// underlying byte output, and resets the byte count. The underlying byte
        /// output is NOT reset.
        /// 
        /// </summary>
        internal virtual void reset()
        {
            delFF = false;
            bpos = 7;
            bbuf = 0;
            nb = 0;
        }

        /// <summary> Returns the length, in bytes, of the output bit stream as written by
        /// this object. If the output bit stream does not have an integer number
        /// of bytes in length then it is rounded to the next integer.
        /// 
        /// </summary>
        /// <returns> The length, in bytes, of the output bit stream.
        /// 
        /// </returns>
        internal virtual int length()
        {
            if (delFF)
            {
                // If bit buffer is empty we just need 'nb' bytes. If not we need
                // the delayed FF and the padded bit buffer.
                return nb + 2;
            }
            else
            {
                // If the bit buffer is empty, we just need 'nb' bytes. If not, we
                // add length of the padded bit buffer
                return nb + ((bpos == 7) ? 0 : 1);
            }
        }
    }
}