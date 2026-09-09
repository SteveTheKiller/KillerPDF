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
using CoreJ2K.Util;
using System;

namespace CoreJ2K.j2k.image.input
{

    /// <summary> This class extends the ImgReader abstract class for reading PGX files. PGX
    /// is a custom monochrome file format invented specifically to simplify the
    /// use of JPEG 2000 with images of different bit-depths in the range 1 to 31
    /// bits per pixel.
    /// 
    /// The file consists of a one line text header followed by the data.
    /// 
    /// 
    /// <u>Header:</u> "PG"+ <i>ws</i> +&lt;<i>endianess</i>&gt;+ <i>ws</i>
    /// +[<i>sign</i>]+<i>ws</i> + &lt;<i>bit-depth</i>&gt;+"
    /// "+&lt;<i>width</i>&gt;+" "+&lt;<i>height</i>&gt;+'\n'
    /// 
    /// where:
    /// <ul>
    /// <li><i>ws</i> (white-spaces) is any combination of characters ' ' and
    /// '\t'.</li> 
    /// <li><i>endianess</i> equals "LM" or "ML"(resp. little-endian or
    /// big-endian)</li> 
    /// <li><i>sign</i> equals "+" or "-" (resp. unsigned or signed). If omited,
    /// values are supposed to be unsigned.</li> 
    /// <li><i>bit-depth</i> that can be any number between 1 and 31.</li>
    /// <li><i>width</i> and <i>height</i> are the image dimensions (in
    /// pixels).</li> 
    /// </ul>
    /// 
    /// <u>Data:</u> The image binary values appear one after the other (in raster
    /// order) immediately after the last header character ('\n') and are
    /// byte-aligned (they are packed into 1,2 or 4 bytes per sample, depending
    /// upon the bit-depth value).
    /// 
    ///  If the data is unisigned, level shifting is applied subtracting
    /// 2^(bitdepth - 1)
    /// 
    /// Since it is not possible to know the input file byte-ordering before
    /// reading its header, this class can not be construct from a
    /// RandomAccessIO. So, the constructor has to open first the input file, to
    /// read only its header, and then it can create the appropriate
    /// BufferedRandomAccessFile (Big-Endian or Little-Endian byte-ordering).
    /// 
    /// NOTE: This class is not thread safe, for reasons of internal
    /// buffering.</summary>
    /// <seealso cref="image.ImgData" />
    /// <seealso cref="RandomAccessIO" />
    /// <seealso cref="BufferedRandomAccessFile" />
    /// <seealso cref="BEBufferedRandomAccessFile" />
    public class ImgReaderPGX : ImgReader, EndianType
    {

        /// <summary>The offset of the raw pixel data in the PGX file </summary>
        private readonly int offset;

        /// <summary>The RandomAccessIO where to get datas from </summary>
        private System.IO.Stream inRenamed;

        /// <summary>The bit-depth of the input file (must be between 1 and 31)</summary>
        private readonly int bitDepth;

        /// <summary>Whether the input datas are signed or not </summary>
        private readonly bool IsSigned;


        /// <summary>The pack length of one sample (in bytes, according to the output
        /// bit-depth 
        /// </summary>
        private readonly int packBytes;

        /// <summary>The byte ordering to use, as defined in EndianType </summary>
        private readonly int byteOrder;

        /// <summary>The line buffer. </summary>
        // This makes the class not thread safe
        // (but it is not the only one making it so)
        private byte[] buf;

        /// <summary>Temporary DataBlkInt object (needed when encoder uses floating-point
        /// filters). This avoid allocating new DataBlk at each time 
        /// </summary>
        private DataBlkInt intBlk;

        /// <summary> Creates a new PGX file reader from the specified File object.
        /// 
        /// </summary>
        /// <param name="inRenamed">The input file as File object.
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error occurs.
        /// 
        /// </exception>
        public ImgReaderPGX(System.IO.Stream inRenamed)
        {
            string header;

            //Opens the given file
            this.inRenamed = inRenamed;
            try
            {
                var inReader = new System.IO.StreamReader(this.inRenamed);
                header = inReader.ReadLine();
            }
            catch (System.IO.IOException)
            {
                throw new System.IO.IOException("Not a PGX file");
            }
            if (header == null)
            {
                throw new System.IO.IOException("Empty file");
            }
            offset = (header.Length + 1);

            //Get information from header
            var st = new Tokenizer(header);
            try
            {
                var nTokens = st.Count;

                // Magic Number
                if (!(st.NextToken()).Equals("PG"))
                    throw new System.IO.IOException("Not a PGX file");

                // Endianess
                var tmp = st.NextToken();
                if (tmp.Equals("LM"))
                    byteOrder = EndianType_Fields.LITTLE_ENDIAN;
                else if (tmp.Equals("ML"))
                    byteOrder = EndianType_Fields.BIG_ENDIAN;
                else
                    throw new System.IO.IOException("Not a PGX file");

                // Unsigned/signed if present in the header
                if (nTokens == 6)
                {
                    tmp = st.NextToken();
                    if (tmp.Equals("+"))
                        IsSigned = false;
                    else if (tmp.Equals("-"))
                        IsSigned = true;
                    else
                        throw new System.IO.IOException("Not a PGX file");
                }

                // bit-depth, width, height
                try
                {
                    bitDepth = (int.Parse(st.NextToken()));
                    // bitDepth must be between 1 and 31
                    if ((bitDepth <= 0) || (bitDepth > 31))
                        throw new System.IO.IOException("Not a PGX file");

                    w = (int.Parse(st.NextToken()));
                    h = (int.Parse(st.NextToken()));
                }
                catch (FormatException)
                {
                    throw new System.IO.IOException("Not a PGX file");
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new System.IO.IOException("Not a PGX file");
            }

            // Number of component
            nc = 1;

            // Number of bytes per data
            if (bitDepth <= 8)
                packBytes = 1;
            else if (bitDepth <= 16)
                packBytes = 2;
            // <= 31
            else
                packBytes = 4;
        }

        /// <summary> Creates a new PGX file reader from the specified File object.
        /// 
        /// </summary>
        /// <param name="inRenamed">The input file as File object.
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error occurs.
        /// 
        /// </exception>
        public ImgReaderPGX(IFileInfo inRenamed)
            : this(FileStreamFactory.New(inRenamed.FullName, "r"))
        {
        }

        /// <summary> Creates a new PGX file reader from the specified file name.
        /// 
        /// </summary>
        /// <param name="inName">The input file name.
        /// 
        /// </param>
        public ImgReaderPGX(string inName)
            : this(FileInfoFactory.New(inName))
        {
        }

        /// <summary> Closes the underlying RandomAccessIO from where the image data is being
        /// read. No operations are possible after a call to this method.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error occurs.
        /// 
        /// </exception>
        public override void Close()
        {
            inRenamed.Dispose();
            inRenamed = null;
            buf = null;
        }

        /// <summary>Returns the number of bits corresponding to the nominal range of the
        /// data in the specified component. This is the value of bitDepth which is
        /// read in the PGX file header.
        /// 
        /// If this number is <i>b</i> then the nominal range is between
        /// -2^(b-1) and 2^(b-1)-1, for originally signed or unsigned data
        /// (unsigned data is level shifted to have a nominal average of 0).</summary>
        /// <param name="compIndex">The index of the component.</param>
        /// <returns> The number of bits corresponding to the nominal range of the
        /// data.</returns>
        public override int GetNomRangeBits(int compIndex)
        {
            // Check component index
            if (compIndex != 0)
                throw new ArgumentOutOfRangeException(nameof(compIndex) + " is out of range");

            return bitDepth;
        }

        /// <summary> Returns the position of the fixed point in the specified component
        /// (i.e. the number of fractional bits), which is always 0 for this
        /// ImgReader.</summary>
        /// <param name="compIndex">The index of the component.</param>
        /// <returns> The position of the fixed-point (i.e. the number of fractional
        /// bits). Always 0 for this ImgReader.</returns>
        public override int GetFixedPoint(int compIndex)
        {
            // Check component index
            if (compIndex != 0)
                throw new ArgumentOutOfRangeException(nameof(compIndex) + " is out of range");
            return 0;
        }

        /// <summary> Returns, in the blk argument, the block of image data containing the
        /// specified rectangular area, in the specified component. The data is
        /// returned, as a reference to the internal data, if any, instead of as a
        /// copy, therefore the returned data should not be modified.
        /// 
        /// After being read the coefficients are level shifted by subtracting
        /// 2^(nominal bit range - 1)
        /// 
        /// The rectangular area to return is specified by the 'ulx', 'uly', 'w'
        /// and 'h' members of the 'blk' argument, relative to the current
        /// tile. These members are not modified by this method. The 'offset' and
        /// 'scanw' of the returned data can be arbitrary. See the 'DataBlk'
        /// class.
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
        /// JJ2KExceptionHandler. See JJ2KExceptionHandler for details.</summary>
        /// <param name="blk">Its coordinates and dimensions specify the area to
        /// return. Some fields in this object are modified to return the data.</param>
        /// <param name="compIndex">The index of the component from which to get the data. Only 0
        /// is valid.</param>
        /// <returns> The requested DataBlk</returns>
        /// <seealso cref="GetCompData" />
        /// <seealso cref="JJ2KExceptionHandler" />
        public override DataBlk GetInternCompData(DataBlk blk, int compIndex)
        {
            int k, j, i, mi; // counters
            var levShift = 1 << (bitDepth - 1);

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
            if (barr == null || barr.Length < blk.w * blk.h * packBytes)
            {
                barr = new int[blk.w * blk.h];
                blk.Data = barr;
            }

            var paddingLength = (32 - bitDepth);
            if (buf == null || buf.Length < packBytes * blk.w)
            {
                buf = new byte[packBytes * blk.w];
            }
            try
            {
                switch (packBytes)
                {

                    // Switch between one of the 3 byte packet type
                    case 1:  // Samples packed into 1 byte
                             // Read line by line
                        mi = blk.uly + blk.h;
                        if (IsSigned)
                        {
                            for (i = blk.uly; i < mi; i++)
                            {
                                // Reposition in input
                                inRenamed.Seek(offset + i * w + blk.ulx, System.IO.SeekOrigin.Begin);
                                var read = inRenamed.Read(buf, 0, blk.w);
                                for (k = (i - blk.uly) * blk.w + blk.w - 1, j = blk.w - 1; j >= 0; k--)
                                    barr[k] = (((buf[j--] & 0xFF) << paddingLength) >> paddingLength);
                            }
                        }
                        else
                        {
                            // Not signed
                            for (i = blk.uly; i < mi; i++)
                            {
                                // Reposition in input
                                inRenamed.Seek(offset + i * w + blk.ulx, System.IO.SeekOrigin.Begin);
                                var read = inRenamed.Read(buf, 0, blk.w);
                                for (k = (i - blk.uly) * blk.w + blk.w - 1, j = blk.w - 1; j >= 0; k--)
                                    barr[k] = ((((buf[j--] & 0xFF) << paddingLength) >>> paddingLength)) - levShift;
                            }
                        }
                        break;


                    case 2:  // Samples packed into 2 bytes
                             // Read line by line
                        mi = blk.uly + blk.h;
                        if (IsSigned)
                        {
                            for (i = blk.uly; i < mi; i++)
                            {
                                // Reposition in input
                                inRenamed.Seek(offset + 2 * (i * w + blk.ulx), System.IO.SeekOrigin.Begin);
                                var read = inRenamed.Read(buf, 0, blk.w << 1);
                                switch (byteOrder)
                                {

                                    case EndianType_Fields.LITTLE_ENDIAN:
                                        for (k = (i - blk.uly) * blk.w + blk.w - 1, j = (blk.w << 1) - 1; j >= 0; k--)
                                        {
                                            barr[k] = ((((buf[j--] & 0xFF) << 8) | (buf[j--] & 0xFF)) << paddingLength) >> paddingLength;
                                        }
                                        break;

                                    case EndianType_Fields.BIG_ENDIAN:
                                        for (k = (i - blk.uly) * blk.w + blk.w - 1, j = (blk.w << 1) - 1; j >= 0; k--)
                                        {
                                            barr[k] = (((buf[j--] & 0xFF) | ((buf[j--] & 0xFF) << 8)) << paddingLength) >> paddingLength;
                                        }
                                        break;

                                    default:
                                        throw new InvalidOperationException("Internal JJ2000 bug");

                                }
                            }
                        }
                        else
                        {
                            // If not signed
                            for (i = blk.uly; i < mi; i++)
                            {
                                // Reposition in input
                                inRenamed.Seek(offset + 2 * (i * w + blk.ulx), System.IO.SeekOrigin.Begin);
                                var read = inRenamed.Read(buf, 0, blk.w << 1);
                                switch (byteOrder)
                                {

                                    case EndianType_Fields.LITTLE_ENDIAN:
                                        for (k = (i - blk.uly) * blk.w + blk.w - 1, j = (blk.w << 1) - 1; j >= 0; k--)
                                        {
                                            barr[k] = ((((((buf[j--] & 0xFF) << 8) | (buf[j--] & 0xFF)) << paddingLength) >>> paddingLength)) - levShift;
                                        }
                                        break;

                                    case EndianType_Fields.BIG_ENDIAN:
                                        for (k = (i - blk.uly) * blk.w + blk.w - 1, j = (blk.w << 1) - 1; j >= 0; k--)
                                        {
                                            barr[k] = (((((buf[j--] & 0xFF) | ((buf[j--] & 0xFF) << 8)) << paddingLength) >>> paddingLength)) - levShift;
                                        }
                                        break;

                                    default:
                                        throw new InvalidOperationException("Internal JJ2000 bug");

                                }
                            }
                        }
                        break;


                    case 4:  // Samples packed into 4 bytes
                             // Read line by line
                        mi = blk.uly + blk.h;
                        if (IsSigned)
                        {
                            for (i = blk.uly; i < mi; i++)
                            {
                                // Reposition in input
                                inRenamed.Seek(offset + 4 * (i * w + blk.ulx), System.IO.SeekOrigin.Begin);
                                var read = inRenamed.Read(buf, 0, blk.w << 2);
                                switch (byteOrder)
                                {

                                    case EndianType_Fields.LITTLE_ENDIAN:
                                        for (k = (i - blk.uly) * blk.w + blk.w - 1, j = (blk.w << 2) - 1; j >= 0; k--)
                                        {
                                            barr[k] = ((((buf[j--] & 0xFF) << 24) | ((buf[j--] & 0xFF) << 16) | ((buf[j--] & 0xFF) << 8) | (buf[j--] & 0xFF)) << paddingLength) >> paddingLength;
                                        }
                                        break;

                                    case EndianType_Fields.BIG_ENDIAN:
                                        for (k = (i - blk.uly) * blk.w + blk.w - 1, j = (blk.w << 2) - 1; j >= 0; k--)
                                        {
                                            barr[k] = (((buf[j--] & 0xFF) | ((buf[j--] & 0xFF) << 8) | ((buf[j--] & 0xFF) << 16) | ((buf[j--] & 0xFF) << 24)) << paddingLength) >> paddingLength;
                                        }
                                        break;

                                    default:
                                        throw new InvalidOperationException("Internal JJ2000 bug");

                                }
                            }
                        }
                        else
                        {
                            for (i = blk.uly; i < mi; i++)
                            {
                                // Reposition in input
                                inRenamed.Seek(offset + 4 * (i * w + blk.ulx), System.IO.SeekOrigin.Begin);
                                var read = inRenamed.Read(buf, 0, blk.w << 2);
                                switch (byteOrder)
                                {

                                    case EndianType_Fields.LITTLE_ENDIAN:
                                        for (k = (i - blk.uly) * blk.w + blk.w - 1, j = (blk.w << 2) - 1; j >= 0; k--)
                                        {
                                            barr[k] = ((((((buf[j--] & 0xFF) << 24) | ((buf[j--] & 0xFF) << 16) | ((buf[j--] & 0xFF) << 8) | (buf[j--] & 0xFF)) << paddingLength) >>> paddingLength)) - levShift;
                                        }
                                        break;

                                    case EndianType_Fields.BIG_ENDIAN:
                                        for (k = (i - blk.uly) * blk.w + blk.w - 1, j = (blk.w << 2) - 1; j >= 0; k--)
                                        {
                                            barr[k] = (((((buf[j--] & 0xFF) | ((buf[j--] & 0xFF) << 8) | ((buf[j--] & 0xFF) << 16) | ((buf[j--] & 0xFF) << 24)) << paddingLength) >>> paddingLength)) - levShift;
                                        }
                                        break;

                                    default:
                                        throw new InvalidOperationException("Internal JJ2000 bug");

                                }
                            }
                        }
                        break;


                    default:
                        throw new System.IO.IOException("PGX supports only bit-depth between 1 and 31");

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
        /// specified rectangular area, in the specified component. The data is
        /// returned, as a copy of the internal data, therefore the returned data
        /// can be modified "in place".
        /// 
        /// After being read the coefficients are level shifted by subtracting
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
        /// This method just calls 'getInternCompData(blk,c)'.
        /// 
        /// When an I/O exception is encountered the JJ2KExceptionHandler is
        /// used. The exception is passed to its handleException method. The action
        /// that is taken depends on the action that has been registered in
        /// JJ2KExceptionHandler. See JJ2KExceptionHandler for details.</summary>
        /// <param name="blk">Its coordinates and dimensions specify the area to
        /// return. If it contains a non-null data array, then it must have the
        /// correct dimensions. If it contains a null data array a new one is
        /// created. The fields in this object are modified to return the data.</param>
        /// <param name="c">The index of the component from which to get the data. Only 0
        /// is valid.</param>
        /// <returns> The requested DataBlk</returns>
        /// <seealso cref="GetInternCompData" />
        /// <seealso cref="JJ2KExceptionHandler" />
        public override DataBlk GetCompData(DataBlk blk, int c)
        {
            return GetInternCompData(blk, c);
        }

        /// <summary> Returns true if the data read was originally signed in the specified
        /// component, false if not.</summary>
        /// <param name="compIndex">The index of the component, from 0 to N-1.</param>
        /// <returns> true if the data was originally signed, false if not.</returns>
        public override bool IsOrigSigned(int compIndex)
        {
            // Check component index
            if (compIndex != 0)
                throw new ArgumentOutOfRangeException(nameof(compIndex) + " is out of range");
            return IsSigned;
        }

        /// <summary> Returns a string of information about the object, more than 1 line
        /// long. The information string includes information from the underlying
        /// RandomAccessIO (its toString() method is called in turn).</summary>
        /// <returns> A string of information about the object.</returns>
        public override string ToString()
        {
            return
                $"ImgReaderPGX: WxH = {w}x{h}, Component = 0, Bit-depth = {bitDepth}, signed = {IsSigned}\nUnderlying RandomAccessIO:\n{inRenamed}";
        }
    }
}