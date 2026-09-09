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

namespace CoreJ2K.j2k.codestream.reader
{

    /// <summary> This class provides a bit based reading facility from a byte based one,
    /// applying the bit unstuffing procedure as required by the packet headers.
    /// 
    /// </summary>
    public class PktHeaderBitReader
    {

        /// <summary>The byte based source of data </summary>
        internal RandomAccessIO bitReader;

        /// <summary>The byte array that is the source of data if the PktHeaderBitReader
        /// is instantiated with a buffer instead of a RandomAccessIO
        /// </summary>
        internal System.IO.MemoryStream bais;

        /// <summary>Flag indicating whether the data should be read from the buffer </summary>
        internal bool usebais;

        /// <summary>The current bit buffer </summary>
        internal int bbuf;

        /// <summary>The position of the next bit to read in the bit buffer (0 means 
        /// empty, 8 full) 
        /// </summary>
        internal int bpos;

        /// <summary>The next bit buffer, if bit stuffing occurred (i.e. current bit 
        /// buffer holds 0xFF) 
        /// </summary>
        internal int nextbbuf;

        /// <summary> Instantiates a 'PktHeaderBitReader' that gets the byte data from the
        /// given source.
        /// 
        /// </summary>
        /// <param name="in">The source of byte data
        /// 
        /// </param>
        internal PktHeaderBitReader(RandomAccessIO bitReader)
        {
            this.bitReader = bitReader;
            usebais = false;
        }

        /// <summary> Instantiates a 'PktHeaderBitReader' that gets the byte data from the
        /// given source.
        /// 
        /// </summary>
        /// <param name="bais">The source of byte data
        /// 
        /// </param>
        internal PktHeaderBitReader(System.IO.MemoryStream bais)
        {
            this.bais = bais;
            usebais = true;
        }

        /// <summary> Reads a single bit from the input.
        /// 
        /// </summary>
        /// <returns> The read bit (0 or 1)
        /// 
        /// </returns>
        /// <exception cref="IOException">If an I/O error occurred
        /// </exception>
        /// <exception cref="EOFException">If teh end of file has been reached
        /// 
        /// </exception>
        internal int readBit()
        {
            if (bpos == 0)
            {
                // Is bit buffer empty?
                if (bbuf != 0xFF)
                {
                    // No bit stuffing
                    bbuf = usebais ? bais.ReadByte() : bitReader.read();
                    bpos = 8;
                    if (bbuf == 0xFF)
                    {
                        // If new bit stuffing get next byte
                        nextbbuf = usebais ? bais.ReadByte() : bitReader.read();
                    }
                }
                else
                {
                    // We had bit stuffing, nextbuf can not be 0xFF
                    bbuf = nextbbuf;
                    bpos = 7;
                }
            }
            return (bbuf >> --bpos) & 0x01;
        }

        /// <summary> Reads a specified number of bits and returns them in a single
        /// integer. The bits are returned in the 'n' least significant bits of the
        /// returned integer. The maximum number of bits that can be read is 31.
        /// 
        /// </summary>
        /// <param name="n">The number of bits to read
        /// 
        /// </param>
        /// <returns> The read bits, packed in the 'n' LSBs.
        /// 
        /// </returns>
        /// <exception cref="IOException">If an I/O error occurred
        /// </exception>
        /// <exception cref="EOFException">If teh end of file has been reached
        /// 
        /// </exception>
        internal int readBits(int n)
        {
            int bits; // The read bits

            // Can we get all bits from the bit buffer?
            if (n <= bpos)
            {
                return (bbuf >> (bpos -= n)) & ((1 << n) - 1);
            }
            else
            {
                // NOTE: The implementation need not be recursive but the not
                // recursive one exploits a bug in the IBM x86 JIT and caused
                // incorrect decoding (Diego Santa Cruz).
                bits = 0;
                do
                {
                    // Get all the bits we can from the bit buffer
                    bits <<= bpos;
                    n -= bpos;
                    bits |= readBits(bpos);
                    // Get an extra bit to load next byte (here bpos is 0)
                    if (bbuf != 0xFF)
                    {
                        // No bit stuffing
                        bbuf = usebais ? bais.ReadByte() : bitReader.read();

                        bpos = 8;
                        if (bbuf == 0xFF)
                        {
                            // If new bit stuffing get next byte
                            nextbbuf = usebais ? bais.ReadByte() : bitReader.read();
                        }
                    }
                    else
                    {
                        // We had bit stuffing, nextbuf can not be 0xFF
                        bbuf = nextbbuf;
                        bpos = 7;
                    }
                }
                while (n > bpos);
                // Get the last bits, if any
                bits <<= n;
                bits |= (bbuf >> (bpos -= n)) & ((1 << n) - 1);
                // Return result
                return bits;
            }
        }

        /// <summary> Synchronizes this object with the underlying byte based input. It
        /// discards and buffered bits and gets ready to read bits from the current 
        /// position in the underlying byte based input.
        /// 
        /// This method should always be called when some data has been read
        /// directly from the underlying byte based input since the last call to
        /// 'readBits()' or 'readBit()' before a new call to any of those
        /// methods.
        /// 
        /// </summary>
        internal virtual void sync()
        {
            bbuf = 0;
            bpos = 0;
        }

        /// <summary> Sets the underlying byte based input to the given object. This method
        /// discards any currently buffered bits and gets ready to start reading
        /// bits from 'in'.
        /// 
        /// This method is equivalent to creating a new 'PktHeaderBitReader'
        /// object.
        /// 
        /// </summary>
        /// <param name="in">The source of byte data
        /// 
        /// </param>
        internal virtual void SetInput(RandomAccessIO bitReader)
        {
            this.bitReader = bitReader;
            bbuf = 0;
            bpos = 0;
        }

        /// <summary> Sets the underlying byte based input to the given object. This method
        /// discards any currently buffered bits and gets ready to start reading
        /// bits from 'in'.
        /// 
        /// This method is equivalent to creating a new 'PktHeaderBitReader'
        /// object.
        /// 
        /// </summary>
        /// <param name="bais">The source of byte data
        /// 
        /// </param>
        internal virtual void SetInput(System.IO.MemoryStream bais)
        {
            this.bais = bais;
            bbuf = 0;
            bpos = 0;
        }
    }
}