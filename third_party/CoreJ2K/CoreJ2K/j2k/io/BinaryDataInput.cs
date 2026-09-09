/*
* Interface:           BinaryDataInput
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

    /// <summary> This interface defines the input of binary data from streams and/or files.
    /// 
    /// Byte level input (i.e., for byte, int, long, float, etc.) should always
    /// be byte aligned. For example, a request to read an <tt>int</tt> should
    /// always realign the input at the byte level.
    /// 
    /// The implementation of this interface should clearly define if multi-byte
    /// input data is read in little- or big-endian byte ordering (least
    /// significant byte first or most significant byte first, respectively).
    /// 
    /// </summary>
    /// <seealso cref="EndianType" />
    public interface BinaryDataInput
    {
        /// <summary> Returns the endianess (i.e., byte ordering) of the implementing
        /// class. Note that an implementing class may implement only one type of
        /// endianness or both, which would be decided at creatiuon time.
        /// 
        /// </summary>
        /// <returns> Either <tt>EndianType.BIG_ENDIAN</tt> or
        /// <tt>EndianType.LITTLE_ENDIAN</tt>
        /// 
        /// </returns>
        /// <seealso cref="EndianType" />
        int ByteOrdering
        {
            get;

        }

        /// <summary> Should read a signed byte (i.e., 8 bit) from the input.  reading, the
        /// input should be realigned at the byte level.
        /// 
        /// </summary>
        /// <returns> The next byte-aligned signed byte (8 bit) from the input.
        /// 
        /// </returns>
        /// <exception cref="EOFException">If the end-of file was reached before getting
        /// all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        byte readByte();

        /// <summary> Should read an unsigned byte (i.e., 8 bit) from the input. It is
        /// returned as an <tt>int</tt> since Java does not have an unsigned byte
        /// type. Prior to reading, the input should be realigned at the byte
        /// level.
        /// 
        /// </summary>
        /// <returns> The next byte-aligned unsigned byte (8 bit) from the input, as
        /// an <tt>int</tt>.
        /// 
        /// </returns>
        /// <exception cref="EOFException">If the end-of file was reached before getting
        /// all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        byte readUnsignedByte();

        /// <summary> Should read a signed short (i.e., 16 bit) from the input. Prior to
        /// reading, the input should be realigned at the byte level.
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
        short readShort();

        /// <summary> Should read an unsigned short (i.e., 16 bit) from the input. It is
        /// returned as an <tt>int</tt> since Java does not have an unsigned short
        /// type. Prior to reading, the input should be realigned at the byte
        /// level.
        /// 
        /// </summary>
        /// <returns> The next byte-aligned unsigned short (16 bit) from the input,
        /// as an <tt>int</tt>.
        /// 
        /// </returns>
        /// <exception cref="EOFException">If the end-of file was reached before getting
        /// all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        int readUnsignedShort();

        /// <summary> Should read a signed int (i.e., 32 bit) from the input. Prior to
        /// reading, the input should be realigned at the byte level.
        /// 
        /// </summary>
        /// <returns> The next byte-aligned signed int (32 bit) from the input.
        /// 
        /// </returns>
        /// <exception cref="EOFException">If the end-of file was reached before getting
        /// all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        int readInt();

        /// <summary> Should read an unsigned int (i.e., 32 bit) from the input. It is
        /// returned as a <tt>long</tt> since Java does not have an unsigned short
        /// type. Prior to reading, the input should be realigned at the byte
        /// level.
        /// 
        /// </summary>
        /// <returns> The next byte-aligned unsigned int (32 bit) from the input, as
        /// a <tt>long</tt>.
        /// 
        /// </returns>
        /// <exception cref="EOFException">If the end-of file was reached before getting
        /// all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        long readUnsignedInt();

        /// <summary> Should read a signed long (i.e., 64 bit) from the input. Prior to
        /// reading, the input should be realigned at the byte level.
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
        long readLong();

        /// <summary> Should read an IEEE single precision (i.e., 32 bit) floating-point
        /// number from the input. Prior to reading, the input should be realigned
        /// at the byte level.
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
        float readFloat();

        /// <summary> Should read an IEEE double precision (i.e., 64 bit) floating-point
        /// number from the input. Prior to reading, the input should be realigned
        /// at the byte level.
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
        double readDouble();

        /// <summary> Skips <tt>n</tt> bytes from the input. Prior to skipping, the input
        /// should be realigned at the byte level.
        /// 
        /// </summary>
        /// <param name="n">The number of bytes to skip
        /// 
        /// </param>
        /// <exception cref="EOFException">If the end-of file was reached before all the
        /// bytes could be skipped.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        int skipBytes(int n);
    }
}