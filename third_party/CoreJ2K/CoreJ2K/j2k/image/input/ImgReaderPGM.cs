/*
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
using CoreJ2K.Util;
using System;

namespace CoreJ2K.j2k.image.input
{

    /// <summary> This class implements the ImgData interface for reading 8 bit unsigned data
    /// from a binary PGM file.
    /// 
    /// After being read the coefficients are level shifted by subtracting
    /// 2^(nominal bit range-1)
    /// 
    /// The TransferType (see ImgData) of this class is TYPE_INT.
    /// 
    /// NOTE: This class is not thread safe, for reasons of internal buffering.
    /// 
    /// </summary>
    /// <seealso cref="image.ImgData" />
    public class ImgReaderPGM : ImgReader
    {
        /// <summary>DC offset value used when reading image </summary>
        public const int DC_OFFSET = 128;

        /// <summary>Where to read the data from </summary>
        private System.IO.Stream inRenamed;

        /// <summary>The offset of the raw pixel data in the PGM file </summary>
        private int offset;

        /// <summary>The number of bits that determine the nominal dynamic range </summary>
        private readonly int rb;

        /// <summary>The line buffer. </summary>
        // This makes the class not thread safe
        // (but it is not the only one making it so)
        private byte[] buf;

        /// <summary>Temporary DataBlkInt object (needed when encoder uses floating-point
        /// filters). This avoid allocating new DataBlk at each time 
        /// </summary>
        private DataBlkInt intBlk;

        /// <summary> Creates a new PGM file reader from the specified file.
        /// 
        /// </summary>
        /// <param name="file">The input file.
        /// 
        /// </param>
        /// <exception cref="IOException">If an error occurs while opening the file.
        /// 
        /// </exception>
        public ImgReaderPGM(IFileInfo file)
            : this(FileStreamFactory.New(file.FullName, "r"))
        {
        }

        /// <summary> Creates a new PGM file reader from the specified file name.
        /// 
        /// </summary>
        /// <param name="fname">The input file name.
        /// 
        /// </param>
        /// <exception cref="IOException">If an error occurs while opening the file.
        /// 
        /// </exception>
        public ImgReaderPGM(string fname)
            : this(FileStreamFactory.New(fname, "r"))
        {
        }

        /// <summary> Creates a new PGM file reader from the specified RandomAccessFile
        /// object. The file header is read to acquire the image size.
        /// 
        /// </summary>
        /// <param name="inRenamed">From where to read the data 
        /// 
        /// </param>
        /// <exception cref="EOFException">if an EOF is read
        /// </exception>
        /// <exception cref="IOException">if an error occurs when opening the file
        /// 
        /// </exception>
        public ImgReaderPGM(System.IO.Stream inRenamed)
        {
            this.inRenamed = inRenamed;

            ConfirmFileType();
            SkipCommentAndWhiteSpace();
            w = ReadHeaderInt();
            SkipCommentAndWhiteSpace();
            h = ReadHeaderInt();
            SkipCommentAndWhiteSpace();
            /*Read the highest pixel value from header (not used)*/
            ReadHeaderInt();
            nc = 1;
            rb = 8;
        }


        /// <summary> Closes the underlying RandomAccessFile from where the image data is
        /// being read. No operations are possible after a call to this method.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error occurs.
        /// 
        /// </exception>
        public override void Close()
        {
            inRenamed.Dispose();
            inRenamed = null;
        }

        /// <summary> Returns the number of bits corresponding to the nominal range of the
        /// data in the specified component. This is the value rb (range bits) that
        /// was specified in the constructor, which normally is 8 for non bilevel
        /// data, and 1 for bilevel data.
        /// 
        /// If this number is <i>b</b> then the nominal range is between
        /// -2^(b-1) and 2^(b-1)-1, since unsigned data is level shifted to have a
        /// nominal average of 0.
        /// 
        /// </summary>
        /// <param name="compIndex">The index of the component.
        /// 
        /// </param>
        /// <returns> The number of bits corresponding to the nominal range of the
        /// data. Fro floating-point data this value is not applicable and the
        /// return value is undefined.
        /// 
        /// </returns>
        public override int GetNomRangeBits(int compIndex)
        {
            // Check component index
            if (compIndex != 0)
                throw new ArgumentOutOfRangeException(nameof(compIndex) + " is out of range");
            return rb;
        }


        /// <summary> Returns the position of the fixed point in the specified component
        /// (i.e. the number of fractional bits), which is always 0 for this
        /// ImgReader.
        /// 
        /// </summary>
        /// <param name="compIndex">The index of the component.
        /// 
        /// </param>
        /// <returns> The position of the fixed-point (i.e. the number of fractional
        /// bits). Always 0 for this ImgReader.
        /// 
        /// </returns>
        public override int GetFixedPoint(int compIndex)
        {
            // Check component index
            if (compIndex != 0)
                throw new ArgumentOutOfRangeException(nameof(compIndex) + " is out of range");
            return 0;
        }


        /// <summary> Returns, in the blk argument, the block of image data containing the
        /// specifed rectangular area, in the specified component. The data is
        /// returned, as a reference to the internal data, if any, instead of as a
        /// copy, therefore the returned data should not be modified.
        /// 
        ///  After being read the coefficients are level shifted by subtracting
        /// 2^(nominal bit range - 1)
        /// 
        /// The rectangular area to return is specified by the 'ulx', 'uly', 'w'
        /// and 'h' members of the 'blk' argument, relative to the current
        /// tile. These members are not modified by this method. The 'offset' and
        /// 'scanw' of the returned data can be arbitrary. See the 'DataBlk' class.
        /// 
        /// If the data array in <tt>blk</tt> is <tt>null</tt>, then a new one
        /// is created if necessary. The implementation of this interface may
        /// choose to return the same array or a new one, depending on what is more
        /// efficient. Therefore, the data array in <tt>blk</tt> prior to the
        /// method call should not be considered to contain the returned data, a
        /// new array may have been created. Instead, get the array from
        /// <tt>blk</tt> after the method has returned.
        /// 
        /// The returned data always has its 'progressive' attribute unset
        /// (i.e. false).
        /// 
        /// When an I/O exception is encountered the JJ2KExceptionHandler is
        /// used. The exception is passed to its handleException method. The action
        /// that is taken depends on the action that has been registered in
        /// JJ2KExceptionHandler. See JJ2KExceptionHandler for details.
        /// 
        /// </summary>
        /// <param name="blk">Its coordinates and dimensions specify the area to
        /// return. Some fields in this object are modified to return the data.
        /// 
        /// </param>
        /// <param name="compIndex">The index of the component from which to get the data. Only 0
        /// is valid.
        /// 
        /// </param>
        /// <returns> The requested DataBlk
        /// 
        /// </returns>
        /// <seealso cref="GetCompData" />
        /// <seealso cref="JJ2KExceptionHandler" />
        public override DataBlk GetInternCompData(DataBlk blk, int compIndex)
        {
            int k, j, i, mi;

            // Check component index
            if (compIndex != 0)
                throw new ArgumentOutOfRangeException(nameof(compIndex) + " is out of range");

            // Check type of block provided as an argument
            if (blk.DataType != DataBlk.TYPE_INT)
            {
                if (intBlk == null)
                    intBlk = new DataBlkInt(blk.ulx, blk.uly, blk.w, blk.h);
                else
                {
                    intBlk.ulx = blk.ulx;
                    intBlk.uly = blk.uly;
                    intBlk.w = blk.w;
                    intBlk.h = blk.h;
                }
                blk = intBlk;
            }

            // Get data array
            var barr = (int[])blk.Data;
            if (barr == null || barr.Length < blk.w * blk.h)
            {
                barr = new int[blk.w * blk.h];
                blk.Data = barr;
            }

            // Check line buffer
            if (buf == null || buf.Length < blk.w)
            {
                buf = new byte[blk.w];
            }

            try
            {
                // Read line by line
                mi = blk.uly + blk.h;
                for (i = blk.uly; i < mi; i++)
                {
                    // Reposition in input
                    inRenamed.Seek(offset + i * w + blk.ulx, System.IO.SeekOrigin.Begin);
                    var read = inRenamed.Read(buf, 0, blk.w);
                    for (k = (i - blk.uly) * blk.w + blk.w - 1, j = blk.w - 1; j >= 0; j--, k--)
                    {
                        barr[k] = (buf[j] & 0xFF) - DC_OFFSET;
                    }
                }
            }
            catch (System.IO.IOException e)
            {
                JJ2KExceptionHandler.handleException(e);
            }

            // Turn off the progressive attribute
            blk.progressive = false;
            // Set buffer attributes
            blk.offset = 0;
            blk.scanw = blk.w;
            return blk;
        }

        /// <summary> Returns, in the blk argument, a block of image data containing the
        /// specifed rectangular area, in the specified component. The data is
        /// returned, as a copy of the internal data, therefore the returned data
        /// can be modified "in place".
        /// 
        ///  After being read the coefficients are level shifted by subtracting
        /// 2^(nominal bit range - 1)
        /// 
        /// The rectangular area to return is specified by the 'ulx', 'uly', 'w'
        /// and 'h' members of the 'blk' argument, relative to the current
        /// tile. These members are not modified by this method. The 'offset' of
        /// the returned data is 0, and the 'scanw' is the same as the block's
        /// width. See the 'DataBlk' class.
        /// 
        /// If the data array in 'blk' is 'null', then a new one is created. If
        /// the data array is not 'null' then it is reused, and it must be large
        /// enough to contain the block's data. Otherwise an 'ArrayStoreException'
        /// or an 'IndexOutOfBoundsException' is thrown by the Java system.
        /// 
        /// The returned data has its 'progressive' attribute unset
        /// (i.e. false).
        /// 
        /// This method just calls 'getInternCompData(blk, n)'.
        /// 
        /// When an I/O exception is encountered the JJ2KExceptionHandler is
        /// used. The exception is passed to its handleException method. The action
        /// that is taken depends on the action that has been registered in
        /// JJ2KExceptionHandler. See JJ2KExceptionHandler for details.
        /// 
        /// </summary>
        /// <param name="blk">Its coordinates and dimensions specify the area to
        /// return. If it contains a non-null data array, then it must have the
        /// correct dimensions. If it contains a null data array a new one is
        /// created. The fields in this object are modified to return the data.
        /// 
        /// </param>
        /// <param name="c">The index of the component from which to get the data. Only 0
        /// is valid.
        /// 
        /// </param>
        /// <returns> The requested DataBlk
        /// 
        /// </returns>
        /// <seealso cref="GetInternCompData" />
        /// <seealso cref="JJ2KExceptionHandler" />
        public override DataBlk GetCompData(DataBlk blk, int c)
        {
            return GetInternCompData(blk, c);
        }

        /// <summary> Returns a byte read from the RandomAccessIO. The number of read byted
        /// are counted to keep track of the offset of the pixel data in the PGM
        /// file
        /// 
        /// </summary>
        /// <returns> One byte read from the header of the PGM file.
        /// 
        /// </returns>
        /// <exception cref="IOException">If an I/O error occurs.
        /// 
        /// </exception>
        /// <exception cref="EOFException">If an EOF is read 
        /// 
        /// </exception>
        private byte CountedByteRead()
        {
            offset++;
            return (byte)inRenamed.ReadByte();
        }

        /// <summary> Checks that the RandomAccessIO begins with 'P5'
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error occurs.
        /// </exception>
        /// <exception cref="EOFException">If an EOF is read
        /// 
        /// </exception>
        private void ConfirmFileType()
        {
            var type = new byte[] { 80, 53 }; // 'P5'
            int i;
            byte b;

            for (i = 0; i < 2; i++)
            {
                b = CountedByteRead();
                if (b != type[i])
                {
                    if (i == 1 && b == 50)
                    {
                        //i.e 'P2'
                        throw new ArgumentException("JJ2000 does not support ascii-PGM files. Use  raw-PGM file instead. ");
                    }
                    else
                    {
                        throw new ArgumentException("Not a raw-PGM file");
                    }
                }
            }
        }

        /// <summary> Skips any line in the header starting with '#' and any space, tab, line
        /// feed or carriage return.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error occurs.  
        /// </exception>
        /// <exception cref="EOFException">if an EOF is read
        /// 
        /// </exception>
        private void SkipCommentAndWhiteSpace()
        {

            var done = false;
            byte b;

            while (!done)
            {
                b = CountedByteRead();
                if (b == 35)
                {
                    // Comment start
                    while (b != 10 && b != 13)
                    {
                        // Comment ends in end of line
                        b = CountedByteRead();
                    }
                }
                else if (!(b == 9 || b == 10 || b == 13 || b == 32))
                {
                    // If not whitespace
                    done = true;
                }
            }
            // Put last valid byte in
            offset--;
            inRenamed.Seek(offset, System.IO.SeekOrigin.Begin);
        }


        /// <summary> Returns an int read from the header of the PGM file.
        /// 
        /// </summary>
        /// <returns> One int read from the header of the PGM file.
        /// 
        /// </returns>
        /// <exception cref="IOException">If an I/O error occurs.
        /// </exception>
        /// <exception cref="EOFException">If an EOF is read 
        /// 
        /// </exception>
        private int ReadHeaderInt()
        {
            var res = 0;
            byte b = 0;

            b = CountedByteRead();
            while (b != 32 && b != 10 && b != 9 && b != 13)
            {
                // While not whitespace
                res = res * 10 + b - 48; // Covert ASCII to numerical value
                b = CountedByteRead();
            }
            return res;
        }

        /// <summary> Returns true if the data read was originally signed in the specified
        /// component, false if not. This method returns always false since PGM
        /// data is always unsigned.
        /// 
        /// </summary>
        /// <param name="compIndex">The index of the component, from 0 to N-1.
        /// 
        /// </param>
        /// <returns> always false, since PGM data is always unsigned.
        /// 
        /// </returns>
        public override bool IsOrigSigned(int compIndex)
        {
            // Check component index
            if (compIndex != 0)
                throw new ArgumentException();
            return false;
        }

        /// <summary> Returns a string of information about the object, more than 1 line
        /// long. The information string includes information from the underlying
        /// RandomAccessIO (its toString() method is called in turn).
        /// 
        /// </summary>
        /// <returns> A string of information about the object.  
        /// 
        /// </returns>
        public override string ToString()
        {
            return $"ImgReaderPGM: WxH = {w}x{h}, Component = 0\nUnderlying RandomAccessIO:\n{inRenamed}";
        }
    }
}