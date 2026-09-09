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
namespace CoreJ2K.j2k.util
{
    using io;
    using System.Collections.Generic;
    using System.IO;

    /// <summary> This class takes a legal JPEG 2000 codestream and performs some
    /// manipulation on it. Currently the manipulations supported are: Tile-parts
    /// 
    /// </summary>
    public class CodestreamManipulator
    {
        private void InitBlock()
        {
            ppt = new int[nt];
        }

        /// <summary>Flag indicating whether packed packet headers in main header is used
        /// 
        /// </summary>
        private readonly bool ppmUsed;

        /// <summary>Flag indicating whether packed packet headers in tile headers is used
        /// 
        /// </summary>
        private readonly bool pptUsed;

        /// <summary>Flag indicating whether SOP marker was only intended for parsing in
        /// This class and should be removed 
        /// </summary>
        private readonly bool tempSop;

        /// <summary>Flag indicating whether EPH marker was only intended for parsing in
        /// This class and should be removed 
        /// </summary>
        private readonly bool tempEph;

        /// <summary>The number of tiles in the image </summary>
        private readonly int nt;

        /// <summary>The number of packets per tile-part </summary>
        private int pptp;

        /// <summary>The name of the outfile </summary>
        private readonly Stream stream;

        /// <summary>The length of a SOT plus a SOD marker </summary>
        private const int TP_HEAD_LEN = 14;

        /// <summary>The maximum number of a tile part index (TPsot) </summary>
        //private static int MAX_TPSOT = 16;

        /// <summary>The maximum number of tile parts in any tile </summary>
        private int maxtp;

        /// <summary>The number of packets per tile </summary>
        private int[] ppt;

        /// <summary>The positions of the SOT, SOP and EPH markers </summary>
        private int[] positions;

        /// <summary>The main header </summary>
        private byte[] mainHeader;

        /// <summary>Buffers containing the tile parts </summary>
        private byte[][][] tileParts;

        /// <summary>Buffers containing the original tile headers </summary>
        private byte[][] tileHeaders;

        /// <summary>Buffers contaning the packet headers </summary>
        private byte[][][] packetHeaders;

        /// <summary>Buffers containing the packet data </summary>
        private byte[][][] packetData;

        /// <summary>Buffers containing the SOP marker segments </summary>
        private byte[][][] sopMarkSeg;

        /// <summary> Instantiates a codestream manipulator..
        /// 
        /// </summary>
        /// <param name="stream">The output stream
        /// 
        /// </param>
        /// <param name="nt">The number of tiles in the image
        /// 
        /// </param>
        /// <param name="pptp">Packets per tile-part. If zero, no division into tileparts
        /// is performed
        /// 
        /// </param>
        /// <param name="ppm">Flag indicating that PPM marker is used
        /// 
        /// </param>
        /// <param name="ppt">Flag indicating that PPT marker is used
        /// 
        /// </param>
        /// <param name="tempSop">Flag indicating whether SOP merker should be removed
        /// 
        /// </param>
        /// <param name="tempEph">Flag indicating whether EPH merker should be removed
        /// 
        /// </param>
        public CodestreamManipulator(Stream stream, int nt, int pptp, bool ppm, bool ppt, bool tempSop, bool tempEph)
        {
            InitBlock();
            this.stream = stream;
            this.nt = nt;
            this.pptp = pptp;
            ppmUsed = ppm;
            pptUsed = ppt;
            this.tempSop = tempSop;
            this.tempEph = tempEph;
        }

        /// <summary> This method performs the actual manipulation of the codestream which is
        /// the reparsing for tile parts and packed packet headers
        /// 
        /// </summary>
        /// <returns> The number of bytes that the file has increased by
        /// 
        /// </returns>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual int doCodestreamManipulation()
        {
            BEBufferedRandomAccessFile fi;
            var addedHeaderBytes = 0;
            ppt = new int[nt];
            tileParts = new byte[nt][][];
            tileHeaders = new byte[nt][];
            packetHeaders = new byte[nt][][];
            packetData = new byte[nt][][];
            sopMarkSeg = new byte[nt][][];

            // If neither packed packet header nor tile parts are used, return 0
            if (ppmUsed == false && pptUsed == false && pptp == 0)
                return 0;

            // Open file for reading and writing
            fi = new BEBufferedRandomAccessFile(stream, false);
            addedHeaderBytes -= fi.length();

            // Parse the codestream for SOT, SOP and EPH markers
            parseAndFind(fi);

            // Read and buffer the tile headers, packet headers and packet data
            readAndBuffer(fi);

            // Rewind and overwrite with new contents
            fi.seek(0);

            // Create tile-parts
            createTileParts();

            // Write new codestream
            writeNewCodestream(fi);

            // Close file
            fi.flush();
            addedHeaderBytes += fi.length();
            fi.Close();

            return addedHeaderBytes;
        }

        /// <summary> This method parses the codestream for SOT, SOP and EPH markers and
        /// removes header header bits signalling SOP and EPH markers if packed
        /// packet headers are used
        /// 
        /// </summary>
        /// <param name="fi">The file to parse the markers from
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        private void parseAndFind(BufferedRandomAccessFile fi)
        {
            int length, pos, i, t, sop = 0, eph = 0;
            short marker;
            int halfMarker;
            int tileEnd;
            var markPos = new List<int>(10);

            // Find position of first SOT marker
            marker = (short)fi.readUnsignedShort(); // read SOC marker
            marker = (short)fi.readUnsignedShort();
            while (marker != codestream.Markers.SOT)
            {
                pos = fi.Pos;
                length = fi.readUnsignedShort();

                // If SOP and EPH markers were only used for parsing in this
                // class remove SOP and EPH markers from Scod field
                if (marker == codestream.Markers.COD)
                {
                    int scod = fi.readUnsignedByte();
                    if (tempSop)
                        scod &= 0xfd; // Remove bits indicating SOP 
                    if (tempEph)
                        scod &= 0xfb; // Remove bits indicating SOP 
                    fi.seek(pos + 2);
                    fi.write(scod);
                }

                fi.seek(pos + length);
                marker = (short)fi.readUnsignedShort();
            }
            pos = fi.Pos;
            fi.seek(pos - 2);

            // Find all packet headers, packed data and tile headers
            for (t = 0; t < nt; t++)
            {
                // Read SOT marker
                fi.readUnsignedShort(); // Skip SOT
                pos = fi.Pos;
                markPos.Add(fi.Pos);
                fi.readInt(); // Skip Lsot and Isot
                length = fi.readInt(); // Read Psot
                fi.readUnsignedShort(); // Skip TPsot & TNsot
                tileEnd = pos + length - 2; // Last byte of tile

                // Find position of SOD marker
                marker = (short)fi.readUnsignedShort();
                while (marker != codestream.Markers.SOD)
                {
                    pos = fi.Pos;
                    length = fi.readUnsignedShort();

                    // If SOP and EPH markers were only used for parsing in this
                    // class remove SOP and EPH markers from Scod field
                    if (marker == codestream.Markers.COD)
                    {
                        int scod = fi.readUnsignedByte();
                        if (tempSop)
                            scod &= 0xfd; // Remove bits indicating SOP 
                        if (tempEph)
                            scod &= 0xfb; // Remove bits indicating SOP 
                        fi.seek(pos + 2);
                        fi.write(scod);
                    }
                    fi.seek(pos + length);
                    marker = (short)fi.readUnsignedShort();
                }

                // Find all SOP and EPH markers in tile
                sop = 0;
                eph = 0;

                i = fi.Pos;
                while (i < tileEnd)
                {
                    halfMarker = fi.readUnsignedByte();
                    if (halfMarker == 0xff)
                    {
                        marker = (short)((halfMarker << 8) + fi.readUnsignedByte());
                        i++;
                        if (marker == codestream.Markers.SOP)
                        {
                            markPos.Add(fi.Pos);
                            ppt[t]++;
                            sop++;
                            fi.skipBytes(4);
                            i += 4;
                        }

                        if (marker == codestream.Markers.EPH)
                        {
                            markPos.Add(fi.Pos);
                            eph++;
                        }
                    }
                    i++;
                }
            }
            markPos.Add(fi.Pos + 2);
            positions = new int[markPos.Count];
            markPos.CopyTo(positions);
        }

        /// <summary> This method reads and buffers the tile headers, packet headers and
        /// packet data.
        /// 
        /// </summary>
        /// <param name="fi">The file to read the headers and data from
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        private void readAndBuffer(BufferedRandomAccessFile fi)
        {
            int p, prem, length, t, markIndex;

            // Buffer main header
            fi.seek(0);
            length = positions[0] - 2;
            mainHeader = new byte[length];
            fi.readFully(mainHeader, 0, length);
            markIndex = 0;

            for (t = 0; t < nt; t++)
            {
                prem = ppt[t];

                packetHeaders[t] = new byte[prem][];
                packetData[t] = new byte[prem][];
                sopMarkSeg[t] = new byte[prem][];

                // Read tile header
                length = positions[markIndex + 1] - positions[markIndex];
                tileHeaders[t] = new byte[length];
                fi.readFully(tileHeaders[t], 0, length);
                markIndex++;

                for (p = 0; p < prem; p++)
                {
                    // Read packet header 
                    length = positions[markIndex + 1] - positions[markIndex];

                    if (tempSop)
                    {
                        // SOP marker is skipped
                        length -= codestream.Markers.SOP_LENGTH;
                        fi.skipBytes(codestream.Markers.SOP_LENGTH);
                    }
                    else
                    {
                        // SOP marker is read and buffered
                        length -= codestream.Markers.SOP_LENGTH;
                        sopMarkSeg[t][p] = new byte[codestream.Markers.SOP_LENGTH];
                        fi.readFully(sopMarkSeg[t][p], 0, codestream.Markers.SOP_LENGTH);
                    }

                    if (!tempEph)
                    {
                        // EPH marker is kept in header
                        length += codestream.Markers.EPH_LENGTH;
                    }
                    packetHeaders[t][p] = new byte[length];
                    fi.readFully(packetHeaders[t][p], 0, length);
                    markIndex++;

                    // Read packet data 
                    length = positions[markIndex + 1] - positions[markIndex];

                    length -= codestream.Markers.EPH_LENGTH;
                    if (tempEph)
                    {
                        // EPH marker is used and is skipped
                        fi.skipBytes(codestream.Markers.EPH_LENGTH);
                    }

                    packetData[t][p] = new byte[length];
                    fi.readFully(packetData[t][p], 0, length);
                    markIndex++;
                }
            }
        }

        /// <summary> This method creates the tileparts from the buffered tile headers,
        /// packet headers and packet data
        /// 
        /// </summary>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        private void createTileParts()
        {
            int i, prem, t, length;
            int pIndex; // phIndex removed
            int tppStart;
            int tilePart;
            int p, np, nomnp;
            int numTileParts;
            int numPackets;
            var temp = new MemoryStream();
            byte[] tempByteArr;

            // Create tile parts
            tileParts = new byte[nt][][];
            maxtp = 0;

            for (t = 0; t < nt; t++)
            {
                // Calculate number of tile parts. If tileparts are not used, 
                // put all packets in the first tilepart
                if (pptp == 0)
                    pptp = ppt[t];
                prem = ppt[t];
                numTileParts = (int)System.Math.Ceiling(((double)prem) / pptp);
                numPackets = packetHeaders[t].Length;
                maxtp = (numTileParts > maxtp) ? numTileParts : maxtp;
                tileParts[t] = new byte[numTileParts][];

                // Create all the tile parts for tile t
                tppStart = 0;
                pIndex = 0;
                p = 0;
                //phIndex = 0;

                for (tilePart = 0; tilePart < numTileParts; tilePart++)
                {

                    // Calculate number of packets in this tilepart
                    nomnp = (pptp > prem) ? prem : pptp;
                    np = nomnp;

                    // Write tile part header
                    if (tilePart == 0)
                    {
                        // Write original tile part header up to SOD marker
                        temp.Write(tileHeaders[t], 0, tileHeaders[t].Length - 2);
                    }
                    else
                    {
                        // Write empty header of length TP_HEAD_LEN-2
                        temp.Write(new byte[TP_HEAD_LEN - 2], 0, TP_HEAD_LEN - 2);
                    }

                    // Write PPT marker segments if PPT used
                    if (pptUsed)
                    {
                        var pptLength = 3; // Zppt and Lppt
                        var pptIndex = 0;
                        int phLength;

                        p = pIndex;
                        while (np > 0)
                        {
                            phLength = packetHeaders[t][p].Length;

                            // If the total legth of the packet headers is greater
                            // than MAX_LPPT, several PPT markers are needed
                            if (pptLength + phLength > codestream.Markers.MAX_LPPT)
                            {

                                temp.WriteByte(unchecked((byte)(codestream.Markers.PPT >>> 8)));
                                temp.WriteByte(codestream.Markers.PPT & 0x00FF);
                                temp.WriteByte((byte)(pptLength >>> 8));
                                temp.WriteByte((byte)pptLength);
                                temp.WriteByte((byte)pptIndex++);
                                for (i = pIndex; i < p; i++)
                                {
                                    temp.Write(packetHeaders[t][i], 0, packetHeaders[t][i].Length);
                                }
                                pptLength = 3; // Zppt and Lppt
                                pIndex = p;
                            }
                            pptLength += phLength;
                            p++;
                            np--;
                        }
                        // Write last PPT marker
                        temp.WriteByte(unchecked((byte)(codestream.Markers.PPT >>> 8)));
                        temp.WriteByte(codestream.Markers.PPT & 0x00FF);
                        temp.WriteByte((byte)(pptLength >>> 8));
                        temp.WriteByte((byte)pptLength);
                        temp.WriteByte((byte)pptIndex);
                        for (i = pIndex; i < p; i++)
                        {

                            temp.Write(packetHeaders[t][i], 0, packetHeaders[t][i].Length);
                        }
                    }
                    pIndex = p;
                    np = nomnp;

                    // Write SOD marker
                    temp.WriteByte(unchecked((byte)(codestream.Markers.SOD >>> 8)));
                    temp.WriteByte(codestream.Markers.SOD & 0x00FF);

                    // Write packet data and packet headers if PPT and PPM not used
                    for (p = tppStart; p < tppStart + np; p++)
                    {
                        if (!tempSop)
                        {
                            temp.Write(sopMarkSeg[t][p], 0, codestream.Markers.SOP_LENGTH);
                        }

                        if (!(ppmUsed || pptUsed))
                        {
                            temp.Write(packetHeaders[t][p], 0, packetHeaders[t][p].Length);
                        }

                        temp.Write(packetData[t][p], 0, packetData[t][p].Length);
                    }
                    tppStart += np;

                    // Edit tile part header
                    tempByteArr = temp.ToArray();
                    tileParts[t][tilePart] = tempByteArr;
                    length = (int)temp.Length;

                    if (tilePart == 0)
                    {
                        // Edit first tile part header
                        tempByteArr[6] = (byte)((length >>> 24)); // Psot
                        tempByteArr[7] = (byte)((length >>> 16));
                        tempByteArr[8] = (byte)((length >>> 8));
                        tempByteArr[9] = (byte)(length);
                        tempByteArr[10] = 0; // TPsot
                        tempByteArr[11] = (byte)(numTileParts); // TNsot
                    }
                    else
                    {
                        // Edit tile part header
                        tempByteArr[0] = unchecked((byte)((codestream.Markers.SOT >>> 8))); // SOT
                        tempByteArr[1] = codestream.Markers.SOT & 0x00FF;
                        tempByteArr[2] = 0; // Lsot
                        tempByteArr[3] = 10;
                        tempByteArr[4] = (byte)((t >>> 8)); // Isot
                        tempByteArr[5] = (byte)(t); // 
                        tempByteArr[6] = (byte)((length >>> 24)); // Psot
                        tempByteArr[7] = (byte)((length >>> 16));
                        tempByteArr[8] = (byte)((length >>> 8));
                        tempByteArr[9] = (byte)(length);
                        tempByteArr[10] = (byte)(tilePart); //TPsot
                        tempByteArr[11] = (byte)(numTileParts); // TNsot
                    }
                    //temp.reset();
                    temp.SetLength(0);
                    prem -= np;
                }
            }
            temp.Dispose();
        }

        /// <summary> This method writes the new codestream to the file. 
        /// 
        /// </summary>
        /// <param name="fi">The file to write the new codestream to
        /// 
        /// </param>
        /// <exception cref="IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        private void writeNewCodestream(BufferedRandomAccessFile fi)
        {
            int t, p, tp; // i removed
            var numTiles = tileParts.Length;
            var packetHeaderLengths = new int[numTiles][];
            for (var i2 = 0; i2 < numTiles; i2++)
            {
                packetHeaderLengths[i2] = new int[maxtp];
            }
            byte[] temp;
            int length;

            // Write main header up to SOT marker
            fi.write(mainHeader, 0, mainHeader.Length);

            // If PPM used write all packet headers in PPM markers
            if (ppmUsed)
            {
                var ppmMarkerSegment = new MemoryStream();
                int numPackets;
                int totNumPackets;
                var ppmIndex = 0;
                int ppmLength;
                int pStart, pStop;
                var prem = new int[numTiles];

                // Set number of remaining packets 
                for (t = 0; t < numTiles; t++)
                {
                    prem[t] = packetHeaders[t].Length;
                }

                // Calculate Nppm values 
                for (tp = 0; tp < maxtp; tp++)
                {
                    for (t = 0; t < numTiles; t++)
                    {
                        if (tileParts[t].Length > tp)
                        {
                            totNumPackets = packetHeaders[t].Length;
                            // Calculate number of packets in this tilepart
                            numPackets = (tp == tileParts[t].Length - 1) ? prem[t] : pptp;

                            pStart = totNumPackets - prem[t];
                            pStop = pStart + numPackets;

                            // Calculate number of packet header bytes for this
                            // tile part
                            for (p = pStart; p < pStop; p++)
                                packetHeaderLengths[t][tp] += packetHeaders[t][p].Length;

                            prem[t] -= numPackets;
                        }
                    }
                }

                // Write first PPM marker
                ppmMarkerSegment.WriteByte(unchecked((byte)(codestream.Markers.PPM >>> 8)));
                ppmMarkerSegment.WriteByte(codestream.Markers.PPM & 0x00FF);
                ppmMarkerSegment.WriteByte(0); // Temporary Lppm value
                ppmMarkerSegment.WriteByte(0); // Temporary Lppm value
                ppmMarkerSegment.WriteByte(0); // zppm
                ppmLength = 3;
                ppmIndex++;

                // Set number of remaining packets 
                for (t = 0; t < numTiles; t++)
                    prem[t] = packetHeaders[t].Length;

                // Write all PPM markers and information
                for (tp = 0; tp < maxtp; tp++)
                {
                    for (t = 0; t < numTiles; t++)
                    {

                        if (tileParts[t].Length > tp)
                        {
                            totNumPackets = packetHeaders[t].Length;

                            // Calculate number of packets in this tilepart
                            numPackets = (tp == tileParts[t].Length - 1) ? prem[t] : pptp;

                            pStart = totNumPackets - prem[t];
                            pStop = pStart + numPackets;

                            // If Nppm value wont fit in current PPM marker segment
                            // write current PPM marker segment and start new
                            if (ppmLength + 4 > codestream.Markers.MAX_LPPM)
                            {
                                // Write current PPM marker
                                ppmMarkerSegment.TryGetBuffer(out var ppmSeg1);
                                temp = ppmSeg1.Array!;
                                length = (int)ppmMarkerSegment.Length - 2;
                                temp[2] = (byte)((length >>> 8));
                                temp[3] = (byte)length;
                                fi.write(temp, 0, length + 2);

                                // Start new PPM marker segment
                                //ppmMarkerSegment.reset();
                                ppmMarkerSegment.SetLength(0);
                                ppmMarkerSegment.WriteByte(unchecked((byte)(codestream.Markers.PPM >>> 8)));
                                ppmMarkerSegment.WriteByte(codestream.Markers.PPM & 0x00FF);
                                ppmMarkerSegment.WriteByte(0); // Temporary Lppm value
                                ppmMarkerSegment.WriteByte(0); // Temporary Lppm value
                                ppmMarkerSegment.WriteByte((byte)ppmIndex++); // zppm
                                ppmLength = 3;
                            }

                            // Write Nppm value
                            length = packetHeaderLengths[t][tp];
                            ppmMarkerSegment.WriteByte((byte)(length >>> 24));
                            ppmMarkerSegment.WriteByte((byte)(length >>> 16));
                            ppmMarkerSegment.WriteByte((byte)(length >>> 8));
                            ppmMarkerSegment.WriteByte((byte)length);
                            ppmLength += 4;

                            // Write packet headers
                            for (p = pStart; p < pStop; p++)
                            {
                                length = packetHeaders[t][p].Length;

                                // If next packet header value wont fit in 
                                // current PPM marker segment write current PPM 
                                // marker segment and start new
                                if (ppmLength + length > codestream.Markers.MAX_LPPM)
                                {
                                    // Write current PPM marker
                                    ppmMarkerSegment.TryGetBuffer(out var ppmSeg2);
                                    temp = ppmSeg2.Array!;
                                    length = (int)ppmMarkerSegment.Length - 2;
                                    temp[2] = (byte)((length >>> 8));
                                    temp[3] = (byte)length;
                                    fi.write(temp, 0, length + 2);

                                    // Start new PPM marker segment
                                    //ppmMarkerSegment.reset();
                                    ppmMarkerSegment.SetLength(0);
                                    ppmMarkerSegment.WriteByte(unchecked((byte)(codestream.Markers.PPM >>> 8)));
                                    ppmMarkerSegment.WriteByte(codestream.Markers.PPM & 0x00FF);
                                    ppmMarkerSegment.WriteByte(0); // Temp Lppm value
                                    ppmMarkerSegment.WriteByte(0); // Temp Lppm value
                                    ppmMarkerSegment.WriteByte((byte)ppmIndex++); // zppm
                                    ppmLength = 3;
                                }

                                // write packet header 
                                ppmMarkerSegment.Write(packetHeaders[t][p], 0, packetHeaders[t][p].Length);
                                ppmLength += packetHeaders[t][p].Length;
                            }
                            prem[t] -= numPackets;
                        }
                    }
                }
                // Write last PPM marker segment
                ppmMarkerSegment.TryGetBuffer(out var ppmSegFinal);
                temp = ppmSegFinal.Array!;
                length = (int)ppmMarkerSegment.Length - 2;
                temp[2] = (byte)((length >>> 8));
                temp[3] = (byte)length;
                fi.write(temp, 0, length + 2);
            }

            // Write tile parts interleaved
            for (tp = 0; tp < maxtp; tp++)
            {
                for (t = 0; t < nt; t++)
                {
                    if (tileParts[t].Length > tp)
                    {
                        temp = tileParts[t][tp];
                        length = temp.Length;
                        fi.write(temp, 0, length);
                    }
                }
            }
            fi.writeShort(codestream.Markers.EOC);
        }
    }
}