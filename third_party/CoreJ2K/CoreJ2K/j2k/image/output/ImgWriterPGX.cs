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
using CoreJ2K.Util;
using System;

namespace CoreJ2K.j2k.image.output
{

    /// <summary> This class extends the ImgWriter abstract class for writing PGX files.  PGX
    /// is a custom monochrome file format invented specifically to simplify the
    /// use of VM3A with images of different bit-depths in the range 1 to 31 bits
    /// per pixel.
    /// 
    /// The file consists of a one line text header followed by the data.
    /// 
    /// 
    /// <u>Header:</u> "PG"+ <i>ws</i> +&lt;<i>endianess</i>&gt;+ <i>ws</i>
    /// +[<i>sign</i>]+<i>ws</i> + &lt;<i>bit-depth</i>&gt;+"
    /// "+&lt;<i>width</i>&gt;+" "+&lt;<i>height</i>&gt;+'\n' 
    /// 
    /// where:<br>
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
    /// 
    ///  If the data is unsigned, level shifting is applied adding 2^(bit depth
    /// - 1)
    /// 
    /// <u>NOTE</u>: This class is not thread safe, for reasons of internal
    /// buffering.
    /// 
    /// </summary>
    /// <seealso cref="ImgWriter" />
    /// <seealso cref="BlkImgDataSrc" />
    public class ImgWriterPGX : ImgWriter
    {

        /// <summary>Used during saturation (2^bitdepth-1 if unsigned, 2^(bitdepth-1)-1 if
        /// signed)
        /// </summary>
        internal int maxVal;

        /// <summary>Used during saturation (0 if unsigned, -2^(bitdepth-1) if signed) </summary>
        internal int minVal;

        /// <summary>Used with level-shiting </summary>
        internal int levShift;

        /// <summary>Whether the data must be signed when writing or not. In the latter
        /// case inverse level shifting must be applied 
        /// </summary>
        internal bool IsSigned;

        /// <summary>The bit-depth of the input file (must be between 1 and 31)</summary>
        private readonly int bitDepth;

        /// <summary>Where to write the data </summary>
        private System.IO.Stream outStream;

        /// <summary>The offset of the raw pixel data in the PGX file </summary>
        private readonly int offset;

        /// <summary>A DataBlk, just used to avoid allocating a new one each time it is
        /// needed 
        /// </summary>
        private DataBlkInt db = new DataBlkInt();

        /// <summary>The number of fractional bits in the source data </summary>
        private readonly int fb;

        /// <summary>The index of the component from where to get the data </summary>
        private readonly int c;

        /// <summary>The pack length of one sample (in bytes, according to the output
        /// bit-depth 
        /// </summary>
        private readonly int packBytes;

        /// <summary>The line buffer. </summary>
        // This makes the class not thrad safe
        // (but it is not the only one making it so)
        private byte[] buf;

        /// <summary> Creates a new writer to the specified File object, to write data from
        /// the specified component.
        /// 
        /// The size of the image that is written to the file is the size of the
        /// component from which to get the data, specified by b, not the size of
        /// the source image (they differ if there is some sub-sampling).
        /// 
        /// All the header informations are given by the BlkImgDataSrc source
        /// (component width, component height, bit-depth) and sign flag, which are
        /// provided to the constructor. The endianness is always big-endian (MSB
        /// first).
        /// 
        /// </summary>
        /// <param name="out">The file where to write the data
        /// 
        /// </param>
        /// <param name="imgSrc">The source from where to get the image data to write.
        /// 
        /// </param>
        /// <param name="c">The index of the component from where to get the data.
        /// 
        /// </param>
        /// <param name="IsSigned">Whether the datas are signed or not (needed only when
        /// writing header).
        /// 
        /// </param>
        /// <seealso cref="DataBlk" />
        public ImgWriterPGX(IFileInfo outStream, BlkImgDataSrc imgSrc, int c, bool IsSigned)
        {
            //Initialize
            this.c = c;
            if (outStream.Exists && !outStream.Delete())
            {
                throw new System.IO.IOException("Could not reset file");
            }
            this.outStream = FileStreamFactory.New(outStream.FullName, "rw");
            this.IsSigned = IsSigned;
            src = imgSrc;
            w = src.ImgWidth;
            h = src.ImgHeight;
            fb = imgSrc.GetFixedPoint(c);

            bitDepth = src.GetNomRangeBits(this.c);
            if ((bitDepth <= 0) || (bitDepth > 31))
            {
                throw new System.IO.IOException("PGX supports only bit-depth between 1 and 31");
            }
            if (bitDepth <= 8)
            {
                packBytes = 1;
            }
            else if (bitDepth <= 16)
            {
                packBytes = 2;
            }
            else
            {
                // <= 31
                packBytes = 4;
            }

            // Writes PGX header
            var tmpString = $"PG ML {((this.IsSigned) ? "- " : "+ ")}{bitDepth} {w} {h}\n"; // component height

            var tmpByte = System.Text.Encoding.UTF8.GetBytes(tmpString);
            foreach (var t in tmpByte)
            {
                this.outStream.WriteByte(t);
            }

            offset = tmpByte.Length;
            maxVal = this.IsSigned ? ((1 << (src.GetNomRangeBits(c) - 1)) - 1) : ((1 << src.GetNomRangeBits(c)) - 1);
            minVal = this.IsSigned ? ((-1) * (1 << (src.GetNomRangeBits(c) - 1))) : 0;

            levShift = (this.IsSigned) ? 0 : 1 << (src.GetNomRangeBits(c) - 1);
        }

        /// <summary> Creates a new writer to the specified file, to write data from the
        /// specified component.
        /// 
        /// The size of the image that is written to the file is the size of the
        /// component from which to get the data, specified by b, not the size of
        /// the source image (they differ if there is some sub-sampling).
        /// 
        /// All header information is given by the BlkImgDataSrc source
        /// (component width, component height, bit-depth) and sign flag, which are
        /// provided to the constructor. The endianness is always big-endian (MSB
        /// first).
        /// 
        /// </summary>
        /// <param name="fname">The name of the file where to write the data
        /// 
        /// </param>
        /// <param name="imgSrc">The source from where to get the image data to write.
        /// 
        /// </param>
        /// <param name="c">The index of the component from where to get the data.
        /// 
        /// </param>
        /// <param name="IsSigned">Whether the datas are signed or not (needed only when
        /// writing header).
        /// 
        /// </param>
        /// <seealso cref="DataBlk" />
        public ImgWriterPGX(string fname, BlkImgDataSrc imgSrc, int c, bool IsSigned) : this(FileInfoFactory.New(fname), imgSrc, c, IsSigned)
        {
        }

        /// <summary> Closes the underlying file or netwrok connection to where the data is
        /// written. Any call to other methods of the class become illegal after a
        /// call to this one.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error occurs.
        /// 
        /// </exception>
        public override void Close()
        {
            int i;
            // Finish writing the file, writing 0s at the end if the data at end
            // has not been written.
            if (outStream.Length != w * h * packBytes + offset)
            {
                // Goto end of file
                outStream.Seek(outStream.Length, System.IO.SeekOrigin.Begin);
                // Fill with 0s
                for (i = offset + w * h * packBytes - (int)outStream.Length; i > 0; i--)
                {
                    outStream.WriteByte(0);
                }
            }
            outStream.Dispose();
            src = null;
            outStream = null;
            db = null;
        }

        /// <summary> Writes all buffered data to the file or resource.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error occurs.
        /// 
        /// </exception>
        public override void Flush()
        {
            // No flush is needed since we use RandomAccessFile
            // Get rid of line buffer (is this a good choice?)
            buf = null;
        }

        /// <summary> Writes the data of the specified area to the file, coordinates are
        /// relative to the current tile of the source. Before writing, the
        /// coefficients are limited to the nominal range and packed into 1,2 or 4
        /// bytes (according to the bit-depth).
        /// 
        /// If the data is unisigned, level shifting is applied adding 2^(bit
        /// depth - 1)
        /// 
        /// This method may not be called concurrently from different
        /// threads. 
        /// 
        /// If the data returned from the BlkImgDataSrc source is progressive,
        /// then it is requested over and over until it is not progressive
        /// anymore.
        /// 
        /// </summary>
        /// <param name="ulx">The horizontal coordinate of the upper-left corner of the
        /// area to write, relative to the current tile.
        /// 
        /// </param>
        /// <param name="uly">The vertical coordinate of the upper-left corner of the area
        /// to write, relative to the current tile.
        /// 
        /// </param>
        /// <param name="width">The width of the area to write.
        /// 
        /// </param>
        /// <param name="height">The height of the area to write.
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error occurs.
        /// 
        /// </exception>
        public override void write(int ulx, int uly, int w, int h)
        {
            int k, i, j;
            var fracbits = fb; // In local variable for faster access
            int tOffx, tOffy; // Active tile offset in the X and Y direction

            // Initialize db
            db.ulx = ulx;
            db.uly = uly;
            db.w = w;
            db.h = h;
            // Get the current active tile offset
            tOffx = src.GetCompULX(c) - (int)Math.Ceiling(src.ImgULX / (double)src.GetCompSubsX(c));
            tOffy = src.GetCompULY(c) - (int)Math.Ceiling(src.ImgULY / (double)src.GetCompSubsY(c));
            // Check the array size
            if (db.data_array != null && db.data_array.Length < w * h)
            {
                // A new one will be allocated by getInternCompData()
                db.data_array = null;
            }
            // Request the data and make sure it is not
            // progressive
            do
            {
                db = (DataBlkInt)src.GetInternCompData(db, c);
            }
            while (db.progressive);

            int tmp;


            // Check line buffer
            if (buf == null || buf.Length < packBytes * w)
            {
                buf = new byte[packBytes * w]; // Expand buffer
            }

            switch (packBytes)
            {


                case 1:  // Samples packed into 1 byte
                         // Write line by line
                    for (i = 0; i < h; i++)
                    {
                        // Skip to beggining of line in file
                        outStream.Seek(offset + this.w * (uly + tOffy + i) + ulx + tOffx, System.IO.SeekOrigin.Begin);
                        // Write all bytes in the line
                        if (fracbits == 0)
                        {
                            for (k = db.offset + i * db.scanw + w - 1, j = w - 1; j >= 0; k--)
                            {
                                tmp = db.data_array[k] + levShift;
                                buf[j--] = (byte)((tmp < minVal) ? minVal : ((tmp > maxVal) ? maxVal : tmp));
                            }
                        }
                        else
                        {
                            for (k = db.offset + i * db.scanw + w - 1, j = w - 1; j >= 0; k--)
                            {
                                tmp = ((db.data_array[k] >>> fracbits)) + levShift;
                                buf[j--] = (byte)((tmp < minVal) ? minVal : ((tmp > maxVal) ? maxVal : tmp));
                            }
                        }
                        outStream.Write(buf, 0, w);
                    }
                    break;


                case 2:  // Samples packed in to 2 bytes (short)
                         // Write line by line
                    for (i = 0; i < h; i++)
                    {

                        // Skip to beggining of line in file
                        outStream.Seek(offset + 2 * (this.w * (uly + tOffy + i) + ulx + tOffx), System.IO.SeekOrigin.Begin);
                        // Write all bytes in the line
                        if (fracbits == 0)
                        {
                            for (k = db.offset + i * db.scanw + w - 1, j = (w << 1) - 1; j >= 0; k--)
                            {
                                tmp = db.data_array[k] + levShift;
                                tmp = (tmp < minVal) ? minVal : ((tmp > maxVal) ? maxVal : tmp);
                                buf[j--] = (byte)tmp; // no need for 0xFF mask since
                                                      // truncation will do it already
                                buf[j--] = (byte)((tmp >>> 8));
                            }
                        }
                        else
                        {
                            for (k = db.offset + i * db.scanw + w - 1, j = (w << 1) - 1; j >= 0; k--)
                            {
                                tmp = ((db.data_array[k] >>> fracbits)) + levShift;
                                tmp = (tmp < minVal) ? minVal : ((tmp > maxVal) ? maxVal : tmp);
                                buf[j--] = (byte)tmp; // no need for 0xFF mask since
                                                      // truncation will do it already
                                buf[j--] = (byte)((tmp >>> 8));
                            }
                        }
                        outStream.Write(buf, 0, w << 1);
                    }
                    break;


                case 4:
                    // Write line by line
                    for (i = 0; i < h; i++)
                    {
                        // Skip to beggining of line in file
                        outStream.Seek(offset + 4 * (this.w * (uly + tOffy + i) + ulx + tOffx), System.IO.SeekOrigin.Begin);
                        // Write all bytes in the line
                        if (fracbits == 0)
                        {
                            for (k = db.offset + i * db.scanw + w - 1, j = (w << 2) - 1; j >= 0; k--)
                            {
                                tmp = db.data_array[k] + levShift;
                                tmp = (tmp < minVal) ? minVal : ((tmp > maxVal) ? maxVal : tmp);
                                buf[j--] = (byte)tmp; // No need to use 0xFF
                                buf[j--] = (byte)((tmp >>> 8)); // masks since truncation
                                buf[j--] = (byte)((tmp >>> 16)); // will have already the
                                buf[j--] = (byte)((tmp >>> 24)); // same effect
                            }
                        }
                        else
                        {
                            for (k = db.offset + i * db.scanw + w - 1, j = (w << 2) - 1; j >= 0; k--)
                            {
                                tmp = ((db.data_array[k] >>> fracbits)) + levShift;
                                tmp = (tmp < minVal) ? minVal : ((tmp > maxVal) ? maxVal : tmp);
                                buf[j--] = (byte)tmp; // No need to use 0xFF
                                buf[j--] = (byte)((tmp >>> 8)); // masks since truncation
                                buf[j--] = (byte)((tmp >>> 16)); // will have already the
                                buf[j--] = (byte)((tmp >>> 24)); // same effect
                            }
                        }
                        outStream.Write(buf, 0, w << 2);
                    }
                    break;


                default:
                    throw new System.IO.IOException("PGX supports only bit-depth between 1 and 31");

            }
        }

        /// <summary> Writes the source's current tile to the output. The requests of data
        /// issued to the source BlkImgDataSrc object are done by strips, in order
        /// to reduce memory usage.
        /// 
        /// If the data returned from the BlkImgDataSrc source is progressive,
        /// then it is requested over and over until it is not progressive
        /// anymore.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error occurs.
        /// 
        /// </exception>
        /// <seealso cref="DataBlk" />
        public override void write()
        {
            int i;
            var tIdx = src.TileIdx;
            var tw = src.GetTileCompWidth(tIdx, c); // Tile width
            var th = src.GetTileCompHeight(tIdx, c); // Tile height
                                                     // Write in strips
            for (i = 0; i < th; i += DEF_STRIP_HEIGHT)
            {
                write(0, i, tw, (th - i < DEF_STRIP_HEIGHT) ? th - i : DEF_STRIP_HEIGHT);
            }
        }

        /// <summary> Returns a string of information about the object, more than 1 line
        /// long. The information string includes information from the underlying
        /// RandomAccessFile (its toString() method is called in turn).
        /// 
        /// </summary>
        /// <returns> A string of information about the object.
        /// 
        /// </returns>
        public override string ToString()
        {
            return
                $"ImgWriterPGX: WxH = {w}x{h}, Component = {c}, Bit-depth = {bitDepth}, signed = {IsSigned}\nUnderlying RandomAccessFile:\n{outStream}";
        }
    }
}