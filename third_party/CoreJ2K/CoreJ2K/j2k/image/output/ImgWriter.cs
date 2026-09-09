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
*  */
using System;

namespace CoreJ2K.j2k.image.output
{

    /// <summary> This is the generic interface to be implemented by all image file (or other
    /// resource) writers for different formats.
    /// 
    /// Each object inheriting from this class should have a source ImgData
    /// object associated with it. The image data to write to the file is obtained
    /// from the associated ImgData object. In general this object would be
    /// specified at construction time.
    /// 
    /// Depending on the actual type of file that is written a call to any
    /// write() or writeAll() method will write data from one component, several
    /// components or all components. For example, a PGM writer will write data
    /// from only one component (defined in the constructor) while a PPM writer
    /// will write 3 components (normally R,G,B).
    /// 
    /// </summary>
    public abstract class ImgWriter : IDisposable
    {

        /// <summary>The defaukt height used when writing strip by strip in the 'write()'
        /// method. It is 64. 
        /// </summary>
        public const int DEF_STRIP_HEIGHT = 64;

        /// <summary>The source ImagaData object, from where to get the image data </summary>
        protected internal BlkImgDataSrc src;

        /// <summary>The width of the image </summary>
        protected internal int w;

        /// <summary>The height of the image </summary>
        protected internal int h;

        /// <summary> Closes the underlying file or netwrok connection to where the data is
        /// written. The implementing class must write all buffered data before
        /// closing the file or resource. Any call to other methods of the class
        /// become illegal after a call to this one.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error occurs.
        /// 
        /// </exception>
        public abstract void Close();

        /// <summary> Writes all buffered data to the file or resource. If the implementing
        /// class does onot use buffering nothing should be done.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error occurs.
        /// 
        /// </exception>
        public abstract void Flush();

        /// <summary>
        /// Releases resources held by this writer. The default implementation
        /// flushes any buffered data and then closes the underlying resource.
        /// </summary>
        public virtual void Dispose()
        {
            try { Flush(); } catch { /* ignore on dispose */ }
            try { Close(); } catch { /* ignore on dispose */ }
            GC.SuppressFinalize(this);
        }

        /// <summary> Writes the source's current tile to the output. The requests of data
        /// issued by the implementing class to the source ImgData object should be
        /// done by blocks or strips, in order to reduce memory usage.
        /// 
        /// The implementing class should only write data that is not
        /// "progressive" (in other words that it is final), see DataBlk for
        /// details.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error occurs.
        /// 
        /// </exception>
        /// <seealso cref="DataBlk" />
        public abstract void write();

        /// <summary> Writes the entire image or only specified tiles to the output. The
        /// implementation in this class calls the write() method for each tile
        /// starting with the upper-left one and proceding in standard scanline
        /// order. It changes the current tile of the source data.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error occurs.
        /// 
        /// </exception>
        /// <seealso cref="DataBlk" />
        public virtual void writeAll()
        {
            // Find the list of tile to decode.
            var nT = src.GetNumTiles(null);

            // Loop on vertical tiles
            for (var y = 0; y < nT.y; y++)
            {
                // Loop on horizontal tiles
                for (var x = 0; x < nT.x; x++)
                {
                    src.SetTile(x, y);
                    write();
                } // End loop on horizontal tiles            
            } // End loop on vertical tiles
        }

        /// <summary> Writes the data of the specified area to the file, coordinates are
        /// relative to the current tile of the source.
        /// 
        /// The implementing class should only write data that is not
        /// "progressive" (in other words that is final), see DataBlk for
        /// details.
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
        public abstract void write(int ulx, int uly, int w, int h);
    }
}