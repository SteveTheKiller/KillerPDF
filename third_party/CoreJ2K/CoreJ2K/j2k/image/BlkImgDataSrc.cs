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

namespace CoreJ2K.j2k.image
{
    /// <summary> This interface defines the methods to transfer image data in blocks,
    /// without following any particular order (random access). This interface does
    /// not define the methods to access the image characteristics, such as width,
    /// height, number of components, tiles, etc., or to change the current
    /// tile. That is provided by other interfaces such as ImgData.
    /// This interface has the notion of a current tile. All data, coordinates
    /// and dimensions are always relative to the current tile. If there is only
    /// one tile then it is equivalent as having no tiles.
    /// A block of requested data may never cross tile boundaries. This should
    /// be enforced by the implementing class, or the source of image data.
    /// This interface defines the methods that can be used to retrieve image
    /// data. Implementing classes need not buffer all the image data, they can ask
    /// their source to load the data they need.
    /// </summary>
    /// <seealso cref="ImgData" />
    public interface BlkImgDataSrc : ImgData
    {

        /// <summary> Returns the position of the fixed point in the specified component, or
        /// equivalently the number of fractional bits. This is the position of the
        /// least significant integral (i.e. non-fractional) bit, which is
        /// equivalent to the number of fractional bits. For instance, for
        /// fixed-point values with 2 fractional bits, 2 is returned. For
        /// floating-point data this value does not apply and 0 should be
        /// returned. Position 0 is the position of the least significant bit in
        /// the data.
        /// 
        /// </summary>
        /// <param name="compIndex">The index of the component.
        /// 
        /// </param>
        /// <returns> The position of the fixed-point, which is the same as the
        /// number of fractional bits. For floating-point data 0 is returned.
        /// 
        /// </returns>
        int GetFixedPoint(int compIndex);

        /// <summary> Returns, in the blk argument, a block of image data containing the
        /// specifed rectangular area, in the specified component. The data is
        /// returned, as a reference to the internal data, if any, instead of as a
        /// copy, therefore the returned data should not be modified.
        /// The rectangular area to return is specified by the 'ulx', 'uly', 'w'
        /// and 'h' members of the 'blk' argument, relative to the current
        /// tile. These members are not modified by this method. The 'offset' and
        /// 'scanw' of the returned data can be arbitrary. See the 'DataBlk' class.
        /// This method, in general, is more efficient than the 'getCompData()'
        /// method since it may not copy the data. However, if the array of returned
        /// data is to be modified by the caller then the other method is probably
        /// preferable.
        /// If possible, the data in the returned 'DataBlk' should be the
        /// internal data itself, instead of a copy, in order to increase the data
        /// transfer efficiency. However, this depends on the particular
        /// implementation (it may be more convenient to just return a copy of the
        /// data). This is the reason why the returned data should not be modified.
        /// If the data array in <tt>blk</tt> is <tt>null</tt>, then a new one
        /// is created if necessary. The implementation of this interface may
        /// choose to return the same array or a new one, depending on what is more
        /// efficient. Therefore, the data array in <tt>blk</tt> prior to the
        /// method call should not be considered to contain the returned data, a
        /// new array may have been created. Instead, get the array from
        /// <tt>blk</tt> after the method has returned.
        /// The returned data may have its 'progressive' attribute set. In this
        /// case the returned data is only an approximation of the "final" data.
        /// </summary>
        /// <param name="blk">Its coordinates and dimensions specify the area to return,
        /// relative to the current tile. Some fields in this object are modified
        /// to return the data.
        /// 
        /// </param>
        /// <param name="compIndex">The index of the component from which to get the data.
        /// 
        /// </param>
        /// <returns> The requested DataBlk
        /// 
        /// </returns>
        /// <seealso cref="GetCompData">
        /// 
        /// </seealso>
        DataBlk GetInternCompData(DataBlk blk, int compIndex);

        /// <summary> Returns, in the blk argument, a block of image data containing the
        /// specifed rectangular area, in the specified component. The data is
        /// returned, as a copy of the internal data, therefore the returned data
        /// can be modified "in place".
        /// 
        /// The rectangular area to return is specified by the 'ulx', 'uly', 'w'
        /// and 'h' members of the 'blk' argument, relative to the current
        /// tile. These members are not modified by this method. The 'offset' of
        /// the returned data is 0, and the 'scanw' is the same as the block's
        /// width. See the 'DataBlk' class.
        /// 
        /// This method, in general, is less efficient than the
        /// 'getInternCompData()' method since, in general, it copies the
        /// data. However, if the array of returned data is to be modified by the
        /// caller then this method is preferable.
        /// 
        /// If the data array in 'blk' is 'null', then a new one is created. If
        /// the data array is not 'null' then it is reused, and it must be large
        /// enough to contain the block's data. Otherwise, an 'ArrayStoreException'
        /// or an 'IndexOutOfBoundsException' is thrown by the Java system.
        /// 
        /// The returned data may have its 'progressive' attribute set. In this
        /// case the returned data is only an approximation of the "final" data.
        /// 
        /// </summary>
        /// <param name="blk">Its coordinates and dimensions specify the area to return,
        /// relative to the current tile. If it contains a non-null data array,
        /// then it must be large enough. If it contains a null data array a new
        /// one is created. Some fields in this object are modified to return the
        /// data.
        /// 
        /// </param>
        /// <param name="c">The index of the component from which to get the data.
        /// 
        /// </param>
        /// <seealso cref="GetInternCompData">
        /// 
        /// </seealso>
        DataBlk GetCompData(DataBlk blk, int c);

        /// <summary> Closes the underlying file or network connection from where the
        /// image data is being read.
        /// 
        /// </summary>
        /// <exception cref="System.Exception">If an I/O error occurs.
        /// </exception>
        void Close();

        /// <summary> Returns true if the data read was originally signed in the specified
        /// component, false if not.
        /// 
        /// </summary>
        /// <param name="compIndex">The index of the component, from 0 to C-1.
        /// 
        /// </param>
        /// <returns> true if the data was originally signed, false if not.
        /// 
        /// </returns>
        bool IsOrigSigned(int compIndex);
    }
}
