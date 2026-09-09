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
using System;

namespace CoreJ2K.j2k.io
{
    using System.IO;

    /// <summary> This class defines a Buffered Random Access File, where all I/O is
    /// considered to be big-endian. It extends the
    /// <tt>BufferedRandomAccessFile</tt> class.
    /// 
    /// </summary>
    /// <seealso cref="RandomAccessIO" />
    /// <seealso cref="BinaryDataOutput" />
    /// <seealso cref="BinaryDataInput" />
    /// <seealso cref="BufferedRandomAccessFile" />
    public class BEBufferedRandomAccessFile : BufferedRandomAccessFile, RandomAccessIO, EndianType
    {
        /// <summary> Constructor. Always needs a size for the buffer.
        /// 
        /// </summary>
        /// <param name="stream">The stream associated with the buffer</param>
        /// <param name="isReadOnly">Indicates whether file is read-only or not.</param>
        /// <param name="bufferSize">The number of bytes to buffer</param>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public BEBufferedRandomAccessFile(Stream stream, bool isReadOnly, int bufferSize)
            : base(stream, isReadOnly, bufferSize)
        {
            byte_Ordering = EndianType_Fields.BIG_ENDIAN;
        }

        /// <summary> Constructor. Uses the default value for the byte-buffer size (512
        /// bytes).
        /// </summary>
        /// <param name="stream">The stream associated with the buffer</param>
        /// <param name="isReadOnly">Indicates whether file is read-only or not.</param>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.</exception>
        public BEBufferedRandomAccessFile(Stream stream, bool isReadOnly)
            : base(stream, isReadOnly)
        {
            byte_Ordering = EndianType_Fields.BIG_ENDIAN;
        }

        /// <summary> Constructor. Always needs a size for the buffer.
        /// 
        /// </summary>
        /// <param name="name">The name of the file associated with the buffer
        /// 
        /// </param>
        /// <param name="mode">"r" for read, "rw" or "rw+" for read and write mode ("rw+"
        /// opens the file for update whereas "rw" removes it
        /// before. So the 2 modes are different only if the file
        /// already exists).
        /// 
        /// </param>
        /// <param name="bufferSize">The number of bytes to buffer
        /// 
        /// </param>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public BEBufferedRandomAccessFile(string name, string mode, int bufferSize) : base(name, mode, bufferSize)
        {
            byte_Ordering = EndianType_Fields.BIG_ENDIAN;
        }

        /// <summary> Constructor. Uses the default value for the byte-buffer size (512
        /// bytes).
        /// 
        /// </summary>
        /// <param name="name">The name of the file associated with the buffer
        /// 
        /// </param>
        /// <param name="mode">"r" for read, "rw" or "rw+" for read and write mode ("rw+"
        /// opens the file for update whereas "rw" removes it
        /// before. So the 2 modes are different only if the file
        /// already exists).
        /// 
        /// </param>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public BEBufferedRandomAccessFile(string name, string mode) : base(name, mode)
        {
            byte_Ordering = EndianType_Fields.BIG_ENDIAN;
        }

        /// <summary> Writes the short value of <tt>v</tt> (i.e., 16 least significant bits)
        /// to the output. Prior to writing, the output should be realigned at the
        /// byte level.
        /// 
        /// Signed or unsigned data can be written. To write a signed value just
        /// pass the <tt>short</tt> value as an argument. To write unsigned data
        /// pass the <tt>int</tt> value as an argument (it will be automatically
        /// casted, and only the 16 least significant bits will be written).
        /// 
        /// </summary>
        /// <param name="v">The value to write to the output
        /// 
        /// </param>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public override void writeShort(int v)
        {
            write((v >>> 8));
            write(v);
        }

        /// <summary> Writes the int value of <tt>v</tt> (i.e., the 32 bits) to the
        /// output. Prior to writing, the output should be realigned at the byte
        /// level.
        /// 
        /// </summary>
        /// <param name="v">The value to write to the output
        /// 
        /// </param>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public override void writeInt(int v)
        {
            write((v >>> 24));
            write((v >>> 16));
            write((v >>> 8));
            write(v);
        }

        /// <summary> Writes the long value of <tt>v</tt> (i.e., the 64 bits) to the
        /// output. Prior to writing, the output should be realigned at the byte
        /// level.
        /// 
        /// </summary>
        /// <param name="v">The value to write to the output
        /// 
        /// </param>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public override void writeLong(long v)
        {
            write((int)((v >>> 56)));
            write((int)((v >>> 48)));
            write((int)((v >>> 40)));
            write((int)((v >>> 32)));
            write((int)((v >>> 24)));
            write((int)((v >>> 16)));
            write((int)((v >>> 8)));
            write((int)v);
        }

        /// <summary> Writes the IEEE float value <tt>v</tt> (i.e., 32 bits) to the
        /// output. Prior to writing, the output should be realigned at the byte
        /// level.
        /// 
        /// </summary>
        /// <param name="v">The value to write to the output
        /// 
        /// </param>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public override void writeFloat(float v)
        {
            // CONVERSION PROBLEM? OPTIMIZE!!!
            //byte[] floatbytes = BitConverter.GetBytes(v);
            //for (int i = floatbytes.Length-1; i >= 0 ; i--) write(floatbytes[i]);

            //int intV = Float.floatToIntBits(v);
            var intV = BitConverter.ToInt32(BitConverter.GetBytes(v), 0);
            write((intV >>> 24));
            write((intV >>> 16));
            write((intV >>> 8));
            write(intV);
        }

        /// <summary> Writes the IEEE double value <tt>v</tt> (i.e., 64 bits) to the
        /// output. Prior to writing, the output should be realigned at the byte
        /// level.
        /// 
        /// </summary>
        /// <param name="v">The value to write to the output
        /// 
        /// </param>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public override void writeDouble(double v)
        {
            //byte[] doublebytes = BitConverter.GetBytes(v);
            //for (int i = doublebytes.Length-1; i >= 0 ; i--) write(doublebytes[i]);

            //long longV = Double.doubleToLongBits(v);
            var longV = BitConverter.ToInt64(BitConverter.GetBytes(v), 0);
            write((int)((longV >>> 56)));
            write((int)((longV >>> 48)));
            write((int)((longV >>> 40)));
            write((int)((longV >>> 32)));
            write((int)((longV >>> 24)));
            write((int)((longV >>> 16)));
            write((int)((longV >>> 8)));
            write((int)(longV));

        }

        /// <summary> Reads a signed short (i.e. 16 bit) from the input. Prior to reading,
        /// the input should be realigned at the byte level.
        /// 
        /// </summary>
        /// <returns> The next byte-aligned signed short (16 bit) from the input.
        /// 
        /// </returns>
        /// <exception cref="java.io.EOFException">If the end-of file was reached before
        /// getting all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public override short readShort()
        {
            return (short)((read() << 8) | (read()));
        }

        /// <summary> Reads an unsigned short (i.e., 16 bit) from the input. It is returned
        /// as an <tt>int</tt> since Java does not have an unsigned short
        /// type. Prior to reading, the input should be realigned at the byte
        /// level.
        /// 
        /// </summary>
        /// <returns> The next byte-aligned unsigned short (16 bit) from the input,
        /// as an <tt>int</tt>.
        /// 
        /// </returns>
        /// <exception cref="java.io.EOFException">If the end-of file was reached before
        /// getting all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public override int readUnsignedShort()
        {
            return ((read() << 8) | read());
        }

        /// <summary> Reads a signed int (i.e., 32 bit) from the input. Prior to reading, the
        /// input should be realigned at the byte level.
        /// 
        /// </summary>
        /// <returns> The next byte-aligned signed int (32 bit) from the input.
        /// 
        /// </returns>
        /// <exception cref="java.io.EOFException">If the end-of file was reached before
        /// getting all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public override int readInt()
        {
            return ((read() << 24) | (read() << 16) | (read() << 8) | read());
        }

        /// <summary> Reads an unsigned int (i.e., 32 bit) from the input. It is returned as
        /// a <tt>long</tt> since Java does not have an unsigned short type. Prior
        /// to reading, the input should be realigned at the byte level.
        /// 
        /// </summary>
        /// <returns> The next byte-aligned unsigned int (32 bit) from the input, as
        /// a <tt>long</tt>.
        /// 
        /// </returns>
        /// <exception cref="java.io.EOFException">If the end-of file was reached before
        /// getting all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public override long readUnsignedInt()
        {
            return (read() << 24) | (read() << 16) | (read() << 8) | read();
        }

        /// <summary> Reads a signed long (i.e., 64 bit) from the input. Prior to reading,
        /// the input should be realigned at the byte level.
        /// 
        /// </summary>
        /// <returns> The next byte-aligned signed long (64 bit) from the input.
        /// 
        /// </returns>
        /// <exception cref="java.io.EOFException">If the end-of file was reached before
        /// getting all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public override long readLong()
        {
            //byte[] longbytes = new byte[8];
            //for (int i = longbytes.Length-1; i >= 0; i--) longbytes[i] = read();
            //return BitConverter.ToInt64(longbytes, 0);
            return ((long)(((ulong)read() << 56) | ((ulong)read() << 48) | ((ulong)read() << 40) | ((ulong)read() << 32) | ((ulong)read() << 24) | ((ulong)read() << 16) | ((ulong)read() << 8) | read()));
        }

        /// <summary> Reads an IEEE single precision (i.e., 32 bit) floating-point number
        /// from the input. Prior to reading, the input should be realigned at the
        /// byte level.
        /// 
        /// </summary>
        /// <returns> The next byte-aligned IEEE float (32 bit) from the input.
        /// 
        /// </returns>
        /// <exception cref="java.io.EOFException">If the end-of file was reached before
        /// getting all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public override float readFloat()
        {
            // CONVERSION PROBLEM?  OPTIMIZE!!!
            //byte[] floatbytes = new byte[4];
            //for (int i = floatbytes.Length-1; i >= 0 ; i--) floatbytes[i] = (byte)read();
            //return BitConverter.ToSingle(floatbytes, 0);

            //return Float.intBitsToFloat((read() << 24) | (read() << 16) | (read() << 8) | (read()));
            return BitConverter.ToSingle(BitConverter.GetBytes((read() << 24) | (read() << 16) | (read() << 8) | (read())), 0);

        }

        /// <summary> Reads an IEEE double precision (i.e., 64 bit) floating-point number
        /// from the input. Prior to reading, the input should be realigned at the
        /// byte level.
        /// 
        /// </summary>
        /// <returns> The next byte-aligned IEEE double (64 bit) from the input.
        /// 
        /// </returns>
        /// <exception cref="java.io.EOFException">If the end-of file was reached before
        /// getting all the necessary data.
        /// 
        /// </exception>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public override double readDouble()
        {
            // CONVERSION PROBLEM?  OPTIMIZE!!!
            //byte[] doublebytes = new byte[8];
            //for (int i = doublebytes.Length-1; i >=0 ; i--) doublebytes[i] = (byte)read();
            //return BitConverter.ToDouble(doublebytes, 0);

            //return Double.longBitsToDouble(((long) read() << 56) | ((long) read() << 48) | ((long) read() << 40) | ((long) read() << 32) | ((long) read() << 24) | ((long) read() << 16) | ((long) read() << 8) | ((long) read()));

            return BitConverter.ToDouble(BitConverter.GetBytes(((long)read() << 56) | ((long)read() << 48) | ((long)read() << 40) | ((long)read() << 32) | ((long)read() << 24) | ((long)read() << 16) | ((long)read() << 8) | read()), 0);

        }

        /// <summary> Returns a string of information about the file and the endianess 
        /// 
        /// </summary>
        public override string ToString()
        {
            return $"{base.ToString()}\nBig-Endian ordering";
        }
    }
}