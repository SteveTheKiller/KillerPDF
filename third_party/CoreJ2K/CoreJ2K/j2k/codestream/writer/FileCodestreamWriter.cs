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

    /// <summary> This class implements a CodestreamWriter for Java streams. The streams can
    /// be files or network connections, or any other resource that presents itself
    /// as a OutputStream. See the CodestreamWriter abstract class for more details
    /// on the implementation of the CodestreamWriter abstract class.
    /// 
    /// Before any packet data is written to the bit stream (even in simulation
    /// mode) the complete header should be written otherwise incorrect estimates
    /// are given by getMaxAvailableBytes() for rate allocation.
    /// 
    /// </summary>
    /// <seealso cref="CodestreamWriter" />
    public class FileCodestreamWriter : CodestreamWriter
    {
        /// <summary> Returns the number of bytes remaining available in the bit stream. This
        /// is the maximum allowed number of bytes minus the number of bytes that
        /// have already been written to the bit stream. If more bytes have been
        /// written to the bit stream than the maximum number of allowed bytes,
        /// then a negative value is returned.
        /// 
        /// </summary>
        /// <returns> The number of bytes remaining available in the bit stream.
        /// 
        /// </returns>
        public override int MaxAvailableBytes => maxBytes - ndata;

        /// <summary> Returns the current length of the entire bit stream.
        /// 
        /// </summary>
        /// <returns> the current length of the bit stream
        /// 
        /// </returns>
        public override int Length => MaxAvailableBytes >= 0 ? ndata : maxBytes;

        /// <summary> Gives the offset of the end of last packet containing ROI information 
        /// 
        /// </summary>
        /// <returns> End of last ROI packet 
        /// 
        /// </returns>
        public override int OffLastROIPkt => offLastROIPkt;

        /// <summary>The upper limit for the value of the Nsop field of the SOP marker </summary>
        private const int SOP_MARKER_LIMIT = 65535;

        /// <summary>Index of the current tile </summary>
        //private int tileIdx = 0;

        /// <summary>The file to write </summary>
        private readonly System.IO.Stream outStream;

        /// <summary>The number of bytes already written to the codestream, excluding the
        /// header length, magic number and header length info. 
        /// </summary>
        internal new int ndata = 0;

        /// <summary>The default buffer length, 1024 bytes </summary>
        public static int DEF_BUF_LEN = 1024;

        /// <summary>Array used to store the SOP markers values </summary>
        internal byte[] sopMarker;

        /// <summary>Array used to store the EPH markers values </summary>
        internal byte[] ephMarker;

        /// <summary>The packet index (when start of packet markers i.e. SOP markers) are
        /// used. 
        /// </summary>
        internal int packetIdx = 0;

        /// <summary>Offset of end of last packet containing ROI information </summary>
        private int offLastROIPkt = 0;

        /// <summary>Length of last packets containing no ROI information </summary>
        private int lenLastNoROI = 0;

        /// <summary> Uses the output stream 'os' for writing the bit stream, using the 'he'
        /// header encoder. The magic number is written to the bit
        /// stream. Normally, the header encoder must be empty (i.e. no data has
        /// been written to it yet). No BufferedOutputStream is used on top of the
        /// output stream 'os'.
        /// 
        /// </summary>
        /// <param name="os">The output stream where to write the bit stream.
        /// 
        /// </param>
        /// <param name="mb">The maximum number of bytes that can be written to the bit
        /// stream.
        /// 
        /// </param>
        /// <exception cref="IOException">If an error occurs while writing the magic
        /// number to the 'os' output stream.
        /// 
        /// </exception>
        public FileCodestreamWriter(System.IO.Stream os, int mb) : base(mb)
        {
            outStream = os;
            initSOP_EPHArrays();
        }

        /// <summary> Writes a packet head to the bit stream and returns the number of bytes
        /// used by this header. It returns the total number of bytes that the
        /// packet head takes in the bit stream. If in simulation mode then no data
        /// is written to the bit stream but the number of bytes is
        /// calculated. This can be used for iterative rate allocation.
        /// 
        /// If the length of the data that is to be written to the bit stream is
        /// more than the space left (as returned by getMaxAvailableBytes()) only
        /// the data that does not exceed the allowed length is written, the rest
        /// is discarded. However the value returned by the method is the total
        /// length of the packet, as if all of it was written to the bit stream.
        /// 
        /// If the bit stream header has not been commited yet and 'sim' is
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
        public override int writePacketHead(byte[] head, int hlen, bool sim, bool sop, bool eph)
        {
            // CONVERSION PROBLEM?
            var len = hlen + (sop ? Markers.SOP_LENGTH : 0) + (eph ? Markers.EPH_LENGTH : 0);

            // If not in simulation mode write the data 
            if (!sim)
            {
                // Write the head bytes 
                if (MaxAvailableBytes < len)
                {
                    len = MaxAvailableBytes;
                }

                if (len > 0)
                {
                    // Write Start Of Packet header markers if necessary 
                    if (sop)
                    {
                        // The first 4 bytes of the array have been filled in the 
                        // classe's constructor. 
                        sopMarker[4] = (byte)(packetIdx >> 8);
                        sopMarker[5] = (byte)(packetIdx);
                        outStream.Write(sopMarker, 0, Markers.SOP_LENGTH);
                        packetIdx++;
                        if (packetIdx > SOP_MARKER_LIMIT)
                        {
                            // Reset SOP value as we have reached its upper limit 
                            packetIdx = 0;
                        }
                    }
                    outStream.Write(head, 0, hlen);
                    // Update data length 
                    ndata += len;

                    // Write End of Packet Header markers if necessary 
                    if (eph)
                    {
                        outStream.Write(ephMarker, 0, Markers.EPH_LENGTH);
                    }

                    // Deal with ROI Information
                    lenLastNoROI += len;
                }
            }
            return len;
        }

        /// <summary> Writes a packet body to the bit stream and returns the number of bytes
        /// used by this body .If in simulation mode then no data is written to the
        /// bit stream but the number of bytes is calculated. This can be used for
        /// iterative rate allocation.
        /// 
        /// If the length of the data that is to be written to the bit stream is
        /// more than the space left (as returned by getMaxAvailableBytes()) only
        /// the data that does not exceed the allowed length is written, the rest
        /// is discarded. However the value returned by the method is the total
        /// length of the packet body , as if all of it was written to the bit
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
        /// <param name="roiInPkt">Whether or not this packet contains ROI information
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
        public override int writePacketBody(byte[] body, int blen, bool sim, bool roiInPkt, int roiLen)
        {

            var len = blen;

            // If not in simulation mode write the data 
            if (!sim)
            {
                // Write the body bytes 
                len = blen;
                if (MaxAvailableBytes < len)
                {
                    len = MaxAvailableBytes;
                }
                if (blen > 0)
                {
                    outStream.Write(body, 0, len);
                }
                // Update data length 
                ndata += len;

                // Deal with ROI information
                if (roiInPkt)
                {
                    offLastROIPkt += lenLastNoROI + roiLen;
                    lenLastNoROI = len - roiLen;
                }
                else
                {
                    lenLastNoROI += len;
                }
            }
            return len;
        }

        /// <summary> Writes the EOC marker.
        /// 
        /// </summary>
        /// <exception cref="IOException">If an error occurs while closing the underlying
        /// stream.
        /// 
        /// </exception>
        public override void Close()
        {

            // Write the EOC marker and close the codestream.
            outStream.WriteByte(unchecked((byte)(Markers.EOC >>> 8)));
            outStream.WriteByte(Markers.EOC & 0x00FF);

            ndata += 2; // Add two to length of codestream for EOC marker
        }

        /// <summary> Writes the header data in the codestream and actualize ndata with the
        /// header length. The header is either a MainHeaderEncoder or a
        /// TileHeaderEncoder.
        /// 
        /// </summary>
        /// <param name="he">The current header encoder.
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error occurs while writing the data.
        /// 
        /// </exception>
        public override void commitBitstreamHeader(HeaderEncoder he)
        {
            // Actualize ndata
            ndata += he.Length;
            he.writeTo(outStream); // Write the header
                                     // Reset packet index used for SOP markers
            packetIdx = 0;

            // Deal with ROI information
            lenLastNoROI += he.Length;
        }

        /// <summary> Performs the initialisation of the arrays that are used to store the
        /// values used to write SOP and EPH markers
        /// 
        /// </summary>
        private void initSOP_EPHArrays()
        {

            // Allocate and set first values of SOP marker as they will not be
            // modified
            sopMarker = new byte[Markers.SOP_LENGTH];
            sopMarker[0] = unchecked((byte)(Markers.SOP >> 8));
            sopMarker[1] = (byte)(Markers.SOP & 0xFF);
            sopMarker[2] = 0x00;
            sopMarker[3] = 0x04;

            // Allocate and set values of EPH marker as they will not be
            // modified
            ephMarker = new byte[Markers.EPH_LENGTH];
            ephMarker[0] = unchecked((byte)(Markers.EPH >> 8));
            ephMarker[1] = (byte)(Markers.EPH & 0xFF);
        }
    }
}