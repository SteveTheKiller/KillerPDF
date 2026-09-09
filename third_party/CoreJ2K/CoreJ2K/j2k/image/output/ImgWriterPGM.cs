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
using CoreJ2K.j2k.util;
using CoreJ2K.Util;
using System;

namespace CoreJ2K.j2k.image.output
{

    /// <summary> This class writes a component from an image in 8 bit unsigned data to a
    /// binary PGM file. The size of the image that is written to the file is the
    /// size of the component from which to get the data, not the size of the
    /// source image (they differ if there is some sub-sampling).
    /// 
    /// Before writing, all coefficients are inversly level shifted and then
    /// "saturated" (they are limited to the nominal dynamic range).<br> <u>Ex:</u>
    /// if the nominal range is 0-255, the following algorithm is applied:<br>
    /// <tt>if coeff<0, output=0<br> if coeff>255, output=255<br> else
    /// output=coeff</tt>
    /// 
    /// The write() methods of an object of this class may not be called
    /// concurrently from different threads.
    /// 
    /// NOTE: This class is not thread safe, for reasons of internal
    /// buffering.
    /// 
    /// </summary>
    public class ImgWriterPGM : ImgWriter
    {

        /// <summary>Value used to inverse level shift </summary>
        private readonly int levShift;

        /// <summary>Where to write the data </summary>
        private System.IO.Stream outStream;

        /// <summary>The index of the component from where to get the data </summary>
        private readonly int c;

        /// <summary>The number of fractional bits in the source data </summary>
        private readonly int fb;

        /// <summary>A DataBlk, just used to avoid allocating a new one each time
        /// it is needed 
        /// </summary>
        private DataBlkInt db = new DataBlkInt();

        /// <summary>The offset of the raw pixel data in the PGM file </summary>
        private int offset;

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
        public ImgWriterPGM(IFileInfo outStream, BlkImgDataSrc imgSrc, int c)
        {
            // Check that imgSrc is of the correct type
            // Check that the component index is valid
            if (c < 0 || c >= imgSrc.NumComps)
            {
                throw new ArgumentException("Invalid number of components");
            }

            // Check that imgSrc is of the correct type
            if (imgSrc.GetNomRangeBits(c) > 8)
            {
                FacilityManager.GetMsgLogger().println(
                    $"Warning: Component {c} has nominal bitdepth {imgSrc.GetNomRangeBits(c)}. Pixel values will be down-shifted to fit bitdepth of 8 for PGM file", 8, 8);
            }

            // Initialize
            if (outStream.Exists && !outStream.Delete())
            {
                throw new System.IO.IOException("Could not reset file");
            }
            this.outStream = FileStreamFactory.New(outStream.FullName, "rw");
            src = imgSrc;
            this.c = c;
            w = imgSrc.ImgWidth;
            h = imgSrc.ImgHeight;
            fb = imgSrc.GetFixedPoint(c);
            levShift = 1 << (imgSrc.GetNomRangeBits(c) - 1);

            writeHeaderInfo();
        }

        /// <summary> Creates a new writer to the specified file, to write data from the
        /// specified component.
        /// 
        /// The size of the image that is written to the file is the size of the
        /// component from which to get the data, specified by b, not the size of
        /// the source image (they differ if there is some sub-sampling).
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
        public ImgWriterPGM(string fname, BlkImgDataSrc imgSrc, int c) : this(FileInfoFactory.New(fname), imgSrc, c)
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
            if (outStream.Length != w * h + offset)
            {
                // Goto end of file
                outStream.Seek(outStream.Length, System.IO.SeekOrigin.Begin);
                // Fill with 0s
                for (i = offset + w * h - (int)outStream.Length; i > 0; i--)
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
            // No flush needed here since we are using a RandomAccessFile Get rid
            // of line buffer (is this a good choice?)
            buf = null;
        }

        /// <summary> Writes the data of the specified area to the file, coordinates are
        /// relative to the current tile of the source. Before writing, the
        /// coefficients are limited to the nominal range.
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

            // variables used during coeff saturation
            int tmp, maxVal = (1 << src.GetNomRangeBits(c)) - 1;

            // If nominal bitdepth greater than 8, calculate down shift
            var downShift = src.GetNomRangeBits(c) - 8;
            if (downShift < 0)
            {
                downShift = 0;
            }

            // Check line buffer
            if (buf == null || buf.Length < w)
            {
                buf = new byte[w]; // Expand buffer
            }

            // Write line by line
            for (i = 0; i < h; i++)
            {
                // Skip to beggining of line in file
                outStream.Seek(offset + this.w * (uly + tOffy + i) + ulx + tOffx, System.IO.SeekOrigin.Begin);
                // Write all bytes in the line
                if (fracbits == 0)
                {
                    for (k = db.offset + i * db.scanw + w - 1, j = w - 1; j >= 0; j--, k--)
                    {
                        tmp = db.data_array[k] + levShift;
                        buf[j] = (byte)(((tmp < 0) ? 0 : ((tmp > maxVal) ? maxVal : tmp)) >> downShift);
                    }
                }
                else
                {
                    for (k = db.offset + i * db.scanw + w - 1, j = w - 1; j >= 0; j--, k--)
                    {
                        tmp = (db.data_array[k] >> fracbits) + levShift;
                        buf[j] = (byte)(((tmp < 0) ? 0 : ((tmp > maxVal) ? maxVal : tmp)) >> downShift);
                    }
                }
                outStream.Write(buf, 0, w);
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

        /// <summary> Writes the header info of the PGM file :
        /// 
        /// P5
        /// width height
        /// 255
        /// 
        /// </summary>
        /// <exception cref="IOException">If there is an IOException
        /// 
        /// </exception>
        private void writeHeaderInfo()
        {
            byte[] byteVals;
            int i;
            string val;

            // write 'P5' to file
            outStream.WriteByte((byte)'P'); // 'P'
            outStream.WriteByte((byte)'5'); // '5'
            outStream.WriteByte((byte)'\n'); // newline
            offset = 3;
            // Write width in ASCII
            val = Convert.ToString(w);
            byteVals = System.Text.Encoding.UTF8.GetBytes(val);
            for (i = 0; i < byteVals.Length; i++)
            {
                outStream.WriteByte(byteVals[i]);
                offset++;
            }
            outStream.WriteByte((byte)' '); // blank
            offset++;
            // Write height in ASCII
            val = Convert.ToString(h);
            byteVals = System.Text.Encoding.UTF8.GetBytes(val);
            for (i = 0; i < byteVals.Length; i++)
            {
                outStream.WriteByte(byteVals[i]);
                offset++;
            }
            // Write maxval
            outStream.WriteByte((byte)'\n'); // newline
            outStream.WriteByte((byte)'2'); // '2'
            outStream.WriteByte((byte)'5'); // '5'
            outStream.WriteByte((byte)'5'); // '5'
            outStream.WriteByte((byte)'\n'); // newline
            offset += 5;
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
            return $"ImgWriterPGM: WxH = {w}x{h}, Component={c}\nUnderlying RandomAccessFile:\n{outStream}";
        }
    }
}