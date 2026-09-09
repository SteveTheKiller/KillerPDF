/*
* Interface:           BinaryDataOutput
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
* 
* 
* 
*/

namespace CoreJ2K.j2k.io
{

    /// <summary> This interface defines the output of binary data to streams and/or files.
    /// 
    /// Byte level output (i.e., for byte, int, long, float, etc.) should
    /// always be byte aligned. For example, a request to write an
    /// <tt>int</tt> should always realign the output at the byte level.
    /// 
    /// The implementation of this interface should clearly define if
    /// multi-byte output data is written in little- or big-endian byte
    /// ordering (least significant byte first or most significant byte
    /// first, respectively).
    /// 
    /// </summary>
    /// <seealso cref="EndianType" />
    public interface BinaryDataOutput
    {
        /// <summary> Returns the endianness (i.e., byte ordering) of the implementing
        /// class. Note that an implementing class may implement only one
        /// type of endianness or both, which would be decided at creatiuon
        /// time.
        /// 
        /// </summary>
        /// <returns> Either <tt>EndianType.BIG_ENDIAN</tt> or
        /// <tt>EndianType.LITTLE_ENDIAN</tt>
        /// 
        /// </returns>
        /// <seealso cref="EndianType">
        /// 
        /// 
        /// 
        /// </seealso>
        int ByteOrdering
        {
            get;

        }

        /// <summary> Should write the byte value of <tt>v</tt> (i.e., 8 least
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
        /// 
        /// 
        /// </exception>
        void writeByte(int v);

        /// <summary> Should write the short value of <tt>v</tt> (i.e., 16 least
        /// significant bits) to the output. Prior to writing, the output
        /// should be realigned at the byte level.
        /// 
        /// Signed or unsigned data can be written. To write a signed
        /// value just pass the <tt>short</tt> value as an argument. To
        /// write unsigned data pass the <tt>int</tt> value as an argument
        /// (it will be automatically casted, and only the 16 least
        /// significant bits will be written).
        /// 
        /// </summary>
        /// <param name="v">The value to write to the output
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// 
        /// 
        /// </exception>
        void writeShort(int v);

        /// <summary> Should write the int value of <tt>v</tt> (i.e., the 32 bits) to
        /// the output. Prior to writing, the output should be realigned at
        /// the byte level.
        /// 
        /// </summary>
        /// <param name="v">The value to write to the output
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// 
        /// 
        /// </exception>
        void writeInt(int v);

        /// <summary> Should write the long value of <tt>v</tt> (i.e., the 64 bits)
        /// to the output. Prior to writing, the output should be realigned
        /// at the byte level.
        /// 
        /// </summary>
        /// <param name="v">The value to write to the output
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// 
        /// 
        /// </exception>
        void writeLong(long v);

        /// <summary> Should write the IEEE float value <tt>v</tt> (i.e., 32 bits) to
        /// the output. Prior to writing, the output should be realigned at
        /// the byte level.
        /// 
        /// </summary>
        /// <param name="v">The value to write to the output
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// 
        /// 
        /// </exception>
        void writeFloat(float v);

        /// <summary> Should write the IEEE double value <tt>v</tt> (i.e., 64 bits)
        /// to the output. Prior to writing, the output should be realigned
        /// at the byte level.
        /// 
        /// </summary>
        /// <param name="v">The value to write to the output
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// 
        /// 
        /// </exception>
        void writeDouble(double v);

        /// <summary> Any data that has been buffered must be written, and the stream should
        /// be realigned at the byte level.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// 
        /// 
        /// </exception>
        void flush();
    }
}