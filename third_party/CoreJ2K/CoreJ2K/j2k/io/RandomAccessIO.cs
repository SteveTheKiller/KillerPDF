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

    /// <summary> This abstract class defines the interface to perform random access I/O. It
    /// implements the <tt>BinaryDataInput</tt> and <tt>BinaryDataOutput</tt>
    /// interfaces so that binary data input/output can be performed.
    /// 
    /// This interface supports streams of up to 2 GB in length.
    /// 
    /// </summary>
    /// <seealso cref="BinaryDataInput" />
    /// <seealso cref="BinaryDataOutput" />
    public interface RandomAccessIO : BinaryDataInput, BinaryDataOutput, IDisposable
    {
        /// <summary> Returns the current position in the stream, which is the position from
        /// where the next byte of data would be read. The first byte in the stream
        /// is in position <tt>0</tt>.
        /// 
        /// </summary>
        /// <returns> The offset of the current position, in bytes.
        /// 
        /// </returns>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        int Pos
        {
            get;

        }

        /// <summary> Closes the I/O stream. Prior to closing the stream, any buffered data
        /// (at the bit and byte level) should be written.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error ocurred. 
        /// 
        /// </exception>
        void Close();

        /// <summary> Returns the current length of the stream, in bytes, taking into account
        /// any buffering.
        /// 
        /// </summary>
        /// <returns> The length of the stream, in bytes.
        /// 
        /// </returns>
        /// <exception cref="IOException">If an I/O error ocurred. 
        /// 
        /// </exception>
        int length();

        /// <summary> Moves the current position for the next read or write operation to
        /// offset. The offset is measured from the beginning of the stream. The
        /// offset may be set beyond the end of the file, if in write mode. Setting
        /// the offset beyond the end of the file does not change the file
        /// length. The file length will change only by writing after the offset
        /// has been set beyond the end of the file.
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
        void seek(int off);

        /// <summary> Reads a byte of data from the stream. Prior to reading, the stream is
        /// realigned at the byte level.
        /// 
        /// </summary>
        /// <returns> The byte read, as an int.
        /// 
        /// </returns>
        /// <exception cref="EOFException">If the end-of file was reached.
        /// 
        /// </exception>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        byte read();

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
        void readFully(byte[] b, int off, int len);

        /// <summary> Writes a byte to the stream. Prior to writing, the stream is realigned
        /// at the byte level.
        /// 
        /// </summary>
        /// <param name="b">The byte to write. The lower 8 bits of <tt>b</tt> are
        /// written.
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error ocurred. 
        /// 
        /// </exception>
        void write(byte b);
    }
}