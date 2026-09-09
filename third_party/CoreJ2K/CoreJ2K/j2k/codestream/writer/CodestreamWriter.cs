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

namespace CoreJ2K.j2k.codestream.writer
{

    /// <summary> This is the abstract class for writing to a codestream. A codestream
    /// corresponds to headers (main and tile-parts) and packets. Each packet has a
    /// head and a body. The codestream always has a maximum number of bytes that
    /// can be written to it. After that many number of bytes no more data is
    /// written to the codestream but the number of bytes is counted so that the
    /// value returned by getMaxAvailableBytes() is negative. If the number of
    /// bytes is unlimited a ridicoulosly large value, such as Integer.MAX_VALUE,
    /// is equivalent.
    /// 
    /// Data writting to the codestream can be simulated. In this case, no byto
    /// is effectively written to the codestream but the resulting number of bytes
    /// is calculated and returned (although it is not accounted in the bit
    /// stream). This can be used in rate control loops.
    /// 
    /// Implementing classes should write the header of the bit stream before
    /// writing any packets. The bit stream header can be written with the help of
    /// the HeaderEncoder class.
    /// 
    /// </summary>
    /// <seealso cref="HeaderEncoder" />
    public abstract class CodestreamWriter : System.IDisposable
    {
        /// <summary> Returns the number of bytes remaining available in the codestream. This
        /// is the maximum allowed number of bytes minus the number of bytes that
        /// have already been written to the bit stream. If more bytes have been
        /// written to the bit stream than the maximum number of allowed bytes,
        /// then a negative value is returned.
        /// 
        /// </summary>
        /// <returns> The number of bytes remaining available in the bit stream.
        /// 
        /// </returns>
        public abstract int MaxAvailableBytes { get; }
        /// <summary> Returns the current length of the entire codestream.
        /// 
        /// </summary>
        /// <returns> the current length of the codestream
        /// 
        /// </returns>
        public abstract int Length { get; }
        /// <summary> Gives the offset of the end of last packet containing ROI information 
        /// 
        /// </summary>
        /// <returns> End of last ROI packet 
        /// 
        /// </returns>
        public abstract int OffLastROIPkt { get; }

        /// <summary>The number of bytes already written to the bit stream </summary>
        protected internal int ndata = 0;

        /// <summary>The maximum number of bytes that can be written to the bit stream </summary>
        protected internal int maxBytes;

        /// <summary> Allocates this object and initializes the maximum number of bytes.
        /// 
        /// </summary>
        /// <param name="mb">The maximum number of bytes that can be written to the
        /// codestream.
        /// 
        /// </param>
        protected internal CodestreamWriter(int mb)
        {
            maxBytes = mb;
        }

        /// <summary> Writes a packet head into the codestream and returns the number of
        /// bytes used by this header. If in simulation mode then no data is
        /// effectively written to the codestream but the number of bytes is
        /// calculated. This can be used for iterative rate allocation.
        /// 
        /// If the number of bytes that has to be written to the codestream is
        /// more than the space left (as returned by getMaxAvailableBytes()), only
        /// the data that does not exceed the allowed length is effectively written
        /// and the rest is discarded. However the value returned by the method is
        /// the total length of the packet, as if all of it was written to the bit
        /// stream.
        /// 
        /// If the codestream header has not been commited yet and if 'sim' is
        /// false, then the bit stream header is automatically commited (see
        /// commitBitstreamHeader() method) before writting the packet.
        /// 
        /// </summary>
        /// <param name="head">The packet head data.
        /// 
        /// </param>
        /// <param name="hlen">The number of bytes in the packet head.
        /// 
        /// </param>
        /// <param name="sim">Simulation mode flag. If true nothing is written to the bit
        /// stream, but the number of bytes that would be written is returned.
        /// 
        /// </param>
        /// <param name="sop">Start of packet header marker flag. This flag indicates
        /// whether or not SOP markers should be written. If true, SOP markers
        /// should be written, if false, they should not.
        /// 
        /// </param>
        /// <param name="eph">End of Packet Header marker flag. This flag indicates
        /// whether or not EPH markers should be written. If true, EPH markers
        /// should be written, if false, they should not.
        /// 
        /// </param>
        /// <returns> The number of bytes spent by the packet head.
        /// 
        /// </returns>
        /// <exception cref="IOException">If an I/O error occurs while writing to the
        /// output stream.
        /// 
        /// </exception>
        /// <seealso cref="commitBitstreamHeader" />
        public abstract int writePacketHead(byte[] head, int hlen, bool sim, bool sop, bool eph);

        /// <summary> Writes a packet body to the codestream and returns the number of bytes
        /// used by this body. If in simulation mode then no data is written to the
        /// bit stream but the number of bytes is calculated. This can be used for
        /// iterative rate allocation.
        /// 
        /// If the number of bytes that has to be written to the codestream is
        /// more than the space left (as returned by getMaxAvailableBytes()), only
        /// the data that does not exceed the allowed length is effectively written
        /// and the rest is discarded. However the value returned by the method is
        /// the total length of the packet, as if all of it was written to the bit
        /// stream.
        /// 
        /// </summary>
        /// <param name="body">The packet body data.
        /// 
        /// </param>
        /// <param name="blen">The number of bytes in the packet body.
        /// 
        /// </param>
        /// <param name="sim">Simulation mode flag. If true nothing is written to the bit
        /// stream, but the number of bytes that would be written is returned.
        /// 
        /// </param>
        /// <param name="roiInPkt">Whether or not there is ROI information in this packet
        /// 
        /// </param>
        /// <param name="roiLen">Number of byte to read in packet body to get all the ROI
        /// information 
        /// 
        /// </param>
        /// <returns> The number of bytes spent by the packet body.
        /// 
        /// </returns>
        /// <exception cref="IOException">If an I/O error occurs while writing to the
        /// output stream.
        /// 
        /// </exception>
        /// <seealso cref="commitBitstreamHeader" />
        public abstract int writePacketBody(byte[] body, int blen, bool sim, bool roiInPkt, int roiLen);


        /// <summary> Closes the underlying resource (file, stream, network connection,
        /// etc.). After a CodestreamWriter is closed no more data can be written
        /// to it.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error occurs while closing the
        /// resource.
        /// 
        /// </exception>
        public abstract void Close();

        /// <summary>
        /// Releases resources held by this writer. The default implementation
        /// delegates to <see cref="Close"/>.
        /// </summary>
        public virtual void Dispose()
        {
            Close();
            System.GC.SuppressFinalize(this);
        }

        /// <summary> Writes the header data to the bit stream, if it has not been already
        /// done. In some implementations this method can be called only once, and
        /// an IllegalArgumentException is thrown if called more than once.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error occurs while writing the data.
        /// 
        /// </exception>
        /// <exception cref="IllegalArgumentException">If this method has already been
        /// called.
        /// 
        /// </exception>
        public abstract void commitBitstreamHeader(HeaderEncoder he);
    }
}