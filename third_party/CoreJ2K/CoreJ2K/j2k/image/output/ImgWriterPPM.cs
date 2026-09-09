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

    /// <summary> This class writes 3 components from an image in 8 bit unsigned data to a
    /// binary PPM file.
    /// 
    /// The size of the image that is written is the size of the source
    /// image. No component subsampling is allowed in any of the components that
    /// are written to the file.
    /// 
    /// Before writing, all coefficients are inversly level-shifted and then
    /// "saturated" (they are limited * to the nominal dynamic range).<br>
    /// 
    /// <u>Ex:</u> if the nominal range is 0-255, the following algorithm is
    /// applied:<br>
    /// 
    /// <tt>if coeff<0, output=0<br>
    /// 
    /// if coeff>255, output=255<br>
    /// 
    /// else output=coeff</tt>
    /// 
    /// The write() methods of an object of this class may not be called
    /// concurrently from different threads.
    /// 
    /// NOTE: This class is not thread safe, for reasons of internal buffering.
    /// 
    /// </summary>
    public class ImgWriterPPM : ImgWriter
    {

        /// <summary>Value used to inverse level shift. One for each component </summary>
        private readonly int[] levShift = new int[3];

        /// <summary>Where to write the data </summary>
        private System.IO.Stream outStream;

        /// <summary>The array of indexes of the components from where to get the data </summary>
        private readonly int[] cps = new int[3];

        /// <summary>The array of the number of fractional bits in the components of the
        /// source data 
        /// </summary>
        private readonly int[] fb = new int[3];

        /// <summary>A DataBlk, just used to avoid allocating a new one each time
        /// it is needed 
        /// </summary>
        private DataBlkInt db = new DataBlkInt();

        /// <summary>The offset of the raw pixel data in the PPM file </summary>
        private int offset;

        /// <summary>The line buffer. </summary>
        // This makes the class not thrad safe
        // (but it is not the only one making it so)
        private byte[] buf;

        /// <summary> Creates a new writer to the specified File object, to write data from
        /// the specified component.
        /// 
        /// The three components that will be written as R, G and B must be
        /// specified through the b1, b2 and b3 arguments.
        /// 
        /// </summary>
        /// <param name="out">The file where to write the data
        /// 
        /// </param>
        /// <param name="imgSrc">The source from where to get the image data to write.
        /// 
        /// </param>
        /// <param name="n1">The index of the first component from where to get the data,
        /// that will be written as the red channel.
        /// 
        /// </param>
        /// <param name="n2">The index of the second component from where to get the data,
        /// that will be written as the green channel.
        /// 
        /// </param>
        /// <param name="n3">The index of the third component from where to get the data,
        /// that will be written as the green channel.
        /// 
        /// </param>
        /// <seealso cref="DataBlk" />
        public ImgWriterPPM(IFileInfo outStream, BlkImgDataSrc imgSrc, int n1, int n2, int n3)
        {
            // Check that imgSrc is of the correct type
            // Check that the component index is valid
            if ((n1 < 0) || (n1 >= imgSrc.NumComps) || (n2 < 0) || (n2 >= imgSrc.NumComps) || (n3 < 0) || (n3 >= imgSrc.NumComps) || (imgSrc.GetNomRangeBits(n1) > 8) || (imgSrc.GetNomRangeBits(n2) > 8) || (imgSrc.GetNomRangeBits(n3) > 8))
            {
                throw new ArgumentException("Invalid component indexes");
            }
            // Initialize
            w = imgSrc.GetCompImgWidth(n1);
            h = imgSrc.GetCompImgHeight(n1);
            // Check that all components have same width and height
            if (w != imgSrc.GetCompImgWidth(n2) || w != imgSrc.GetCompImgWidth(n3) || h != imgSrc.GetCompImgHeight(n2) || h != imgSrc.GetCompImgHeight(n3))
            {
                throw new ArgumentException("All components must have the same dimensions and no subsampling");
            }
            w = imgSrc.ImgWidth;
            h = imgSrc.ImgHeight;

            // Continue initialization
            if (outStream.Exists && !outStream.Delete())
            {
                throw new System.IO.IOException("Could not reset file");
            }
            this.outStream = FileStreamFactory.New(outStream.FullName, "rw");
            src = imgSrc;
            cps[0] = n1;
            cps[1] = n2;
            cps[2] = n3;
            fb[0] = imgSrc.GetFixedPoint(n1);
            fb[1] = imgSrc.GetFixedPoint(n2);
            fb[2] = imgSrc.GetFixedPoint(n3);

            levShift[0] = 1 << (imgSrc.GetNomRangeBits(n1) - 1);
            levShift[1] = 1 << (imgSrc.GetNomRangeBits(n2) - 1);
            levShift[2] = 1 << (imgSrc.GetNomRangeBits(n3) - 1);

            writeHeaderInfo();
        }

        /// <summary> Creates a new writer to the specified file, to write data from the
        /// specified component.
        /// 
        /// The three components that will be written as R, G and B must be
        /// specified through the b1, b2 and b3 arguments.
        /// 
        /// </summary>
        /// <param name="fname">The name of the file where to write the data
        /// 
        /// </param>
        /// <param name="imgSrc">The source from where to get the image data to write.
        /// 
        /// </param>
        /// <param name="n1">The index of the first component from where to get the data,
        /// that will be written as the red channel.
        /// 
        /// </param>
        /// <param name="n2">The index of the second component from where to get the data,
        /// that will be written as the green channel.
        /// 
        /// </param>
        /// <param name="n3">The index of the third component from where to get the data,
        /// that will be written as the green channel.
        /// 
        /// </param>
        /// <seealso cref="DataBlk" />
        public ImgWriterPPM(string fname, BlkImgDataSrc imgSrc, int n1, int n2, int n3) : this(FileInfoFactory.New(fname), imgSrc, n1, n2, n3)
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
            if (outStream.Length != 3 * w * h + offset)
            {
                // Goto end of file
                outStream.Seek(outStream.Length, System.IO.SeekOrigin.Begin);
                // Fill with 0s n all the components
                for (i = 3 * w * h + offset - (int)outStream.Length; i > 0; i--)
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
            int k, j, i, c;
            // In local variables for faster access
            int fracbits;
            // variables used during coeff saturation
            int shift, tmp, maxVal;
            int tOffx, tOffy; // Active tile offset in the X and Y direction

            // Active tiles in all components have same offset since they are at
            // same resolution (PPM does not support anything else)
            tOffx = src.GetCompULX(cps[0]) - (int)Math.Ceiling(src.ImgULX / (double)src.GetCompSubsX(cps[0]));
            tOffy = src.GetCompULY(cps[0]) - (int)Math.Ceiling(src.ImgULY / (double)src.GetCompSubsY(cps[0]));

            // Check the array size
            if (db.data_array != null && db.data_array.Length < w)
            {
                // A new one will be allocated by getInternCompData()
                db.data_array = null;
            }

            // Check the line buffer
            if (buf == null || buf.Length < 3 * w)
            {
                buf = new byte[3 * w];
            }

            // Write the data to the file
            // Write line by line
            for (i = 0; i < h; i++)
            {
                // Write into buffer first loop over the three components and
                // write for each
                for (c = 0; c < 3; c++)
                {
                    maxVal = (1 << src.GetNomRangeBits(cps[c])) - 1;
                    shift = levShift[c];

                    // Initialize db
                    db.ulx = ulx;
                    db.uly = uly + i;
                    db.w = w;
                    db.h = 1;

                    // Request the data and make sure it is not progressive
                    do
                    {
                        db = (DataBlkInt)src.GetInternCompData(db, cps[c]);
                    }
                    while (db.progressive);
                    // Get the fracbits value
                    fracbits = fb[c];
                    // Write all bytes in the line
                    if (fracbits == 0)
                    {
                        for (k = db.offset + w - 1, j = 3 * w - 1 + c - 2; j >= 0; k--)
                        {
                            tmp = db.data_array[k] + shift;
                            buf[j] = (byte)((tmp < 0) ? 0 : ((tmp > maxVal) ? maxVal : tmp));
                            j -= 3;
                        }
                    }
                    else
                    {
                        for (k = db.offset + w - 1, j = 3 * w - 1 + c - 2; j >= 0; k--)
                        {
                            tmp = ((db.data_array[k] >>> fracbits)) + shift;
                            buf[j] = (byte)((tmp < 0) ? 0 : ((tmp > maxVal) ? maxVal : tmp));
                            j -= 3;
                        }
                    }
                }
                // Write buffer into file
                outStream.Seek(offset + 3 * (this.w * (uly + tOffy + i) + ulx + tOffx), System.IO.SeekOrigin.Begin);
                outStream.Write(buf, 0, 3 * w);
            }
        }

        /// <summary> Writes the source's current tile to the output. The requests of data
        /// issued to the source BlkImgDataSrc object are done by strips, in order
        /// to reduce memory usage.
        /// 
        /// If the data returned from the BlkImgDataSrc source is progressive,
        /// then it is requested over and over until it is not progressive any
        /// more.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error occurs.
        /// 
        /// </exception>
        public override void write()
        {
            int i;
            var tIdx = src.TileIdx;
            var tw = src.GetTileCompWidth(tIdx, 0); // Tile width 
            var th = src.GetTileCompHeight(tIdx, 0); // Tile height
                                                     // Write in strips
            for (i = 0; i < th; i += DEF_STRIP_HEIGHT)
            {
                write(0, i, tw, ((th - i) < DEF_STRIP_HEIGHT) ? th - i : DEF_STRIP_HEIGHT);
            }
        }

        /// <summary> Writes the header info of the PPM file :
        /// 
        /// P6<br>
        /// 
        /// width height<br>
        /// 
        /// 255<br>
        /// 
        /// </summary>
        /// <exception cref="IOException">If there is an I/O Error 
        /// 
        /// </exception>
        private void writeHeaderInfo()
        {
            byte[] byteVals;
            int i;
            string val;

            // write 'P6' to file
            outStream.Seek(0, System.IO.SeekOrigin.Begin);
            outStream.WriteByte(80);
            outStream.WriteByte(54);
            outStream.WriteByte(10); // new line
            offset = 3;
            // Write width in ASCII
            val = Convert.ToString(w);
            byteVals = System.Text.Encoding.UTF8.GetBytes(val);
            for (i = 0; i < byteVals.Length; i++)
            {
                outStream.WriteByte(byteVals[i]);
                offset++;
            }
            outStream.WriteByte(32); // blank
            offset++;
            // Write height in ASCII
            val = Convert.ToString(h);
            byteVals = System.Text.Encoding.UTF8.GetBytes(val);
            for (i = 0; i < byteVals.Length; i++)
            {
                outStream.WriteByte(byteVals[i]);
                offset++;
            }

            outStream.WriteByte(10); // newline
            outStream.WriteByte(50); // '2'
            outStream.WriteByte(53); // '5'
            outStream.WriteByte(53); // '5'
            outStream.WriteByte(10); // newline
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
            return
                $"ImgWriterPPM: WxH = {w}x{h}, Components = {cps[0]},{cps[1]},{cps[2]}\nUnderlying RandomAccessFile:\n{outStream}";
        }
    }
}