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
using CoreJ2K.j2k.fileformat.metadata;
using CoreJ2K.j2k.io;
using System;

namespace CoreJ2K.j2k.fileformat.writer
{
    using System.IO;
    using System.Text;

    /// <summary> This class writes the file format wrapper that may or may not exist around
    /// a valid JPEG 2000 codestream. This class writes the simple possible legal
    /// fileformat
    /// 
    /// </summary>
    /// <seealso cref="j2k.fileformat.reader.FileFormatReader" />
    public class FileFormatWriter
    {

        /// <summary>The file from which to read the codestream and write file</summary>
        private BEBufferedRandomAccessFile fi;

        /// <summary>The stream from which to read the codestream and to write
        /// the JP2 file
        /// </summary>
        private readonly Stream stream;

        /// <summary>Image height </summary>
        private readonly int height;

        /// <summary>Image width </summary>
        private readonly int width;

        /// <summary>Number of components </summary>
        private readonly int nc;

        /// <summary>Bits per component </summary>
        private readonly int[] bpc;

        /// <summary>Flag indicating whether number of bits per component varies </summary>
        private readonly bool bpcVaries;

        /// <summary>Length of codestream </summary>
        private readonly int clength;

        /// <summary>Length of Colour Specification Box </summary>
        private const int CSB_LENGTH = 15;

        /// <summary>Length of File Type Box </summary>
        private const int FTB_LENGTH = 20;

        /// <summary>Length of Image Header Box </summary>
        private const int IHB_LENGTH = 22;

        /// <summary>base length of Bits Per Component box </summary>
        private const int BPC_LENGTH = 8;



        /// <summary> The constructor of the FileFormatWriter. It receives all the
        /// information necessary about a codestream to generate a legal JP2 file
        /// 
        /// </summary>
        /// <param name="stream">The stream that is to be made a JP2 file
        /// 
        /// </param>
        /// <param name="height">The height of the image
        /// 
        /// </param>
        /// <param name="width">The width of the image
        /// 
        /// </param>
        /// <param name="nc">The number of components
        /// 
        /// </param>
        /// <param name="bpc">The number of bits per component
        /// 
        /// </param>
        /// <param name="clength">Length of codestream 
        /// 
        /// </param>
        public FileFormatWriter(Stream stream, int height, int width, int nc, int[] bpc, int clength)
        {
            this.height = height;
            this.width = width;
            this.nc = nc;
            this.bpc = bpc;
            this.stream = stream;
            this.clength = clength;

            bpcVaries = false;
            var fixbpc = bpc[0];
            for (var i = nc - 1; i > 0; i--)
            {
                if (bpc[i] != fixbpc)
                    bpcVaries = true;
            }
        }



        /// <summary> This method reads the codestream and writes the file format wrapper and
        /// the codestream to the same file
        /// 
        /// </summary>
        /// <returns> The number of bytes increases because of the file format
        /// 
        /// </returns>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual int writeFileFormat()
        {
            byte[] codestream;
            int metadataLength = 0;
            int ftbLength = FTB_LENGTH;

            try
            {
                // Read and buffer the codestream
                fi = new BEBufferedRandomAccessFile(stream, false);
                codestream = new byte[clength];
                fi.readFully(codestream, 0, clength);

                // Write the JP2_SINATURE_BOX
                fi.seek(0);
                fi.writeInt(0x0000000c);
                fi.writeInt(FileFormatBoxes.JP2_SIGNATURE_BOX);
                fi.writeInt(0x0d0a870a);

                // Write File Type box (length varies with the brand: jp2 vs jpx)
                ftbLength = writeFileTypeBox();

                // Write JP2 Header box
                writeJP2HeaderBox();

                // Write metadata boxes (XML, UUID) if present
                if (Metadata != null)
                {
                    metadataLength = writeMetadataBoxes();
                }

                // Write the Codestream box 
                writeContiguousCodeStreamBox(codestream);

                fi.Close();
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    $"Error while writing JP2 file format(2): {e.Message}\n{e.StackTrace}");
            }
            
            var baseLength = bpcVaries
                ? 12 + ftbLength + 8 + IHB_LENGTH + CSB_LENGTH + BPC_LENGTH + nc + 8
                : 12 + ftbLength + 8 + IHB_LENGTH + CSB_LENGTH + 8;

            return baseLength + metadataLength;
        }

        /// <summary>
        /// Writes all metadata boxes (XML, UUID, JPR, Label, and UUID Info boxes).
        /// For JPX output, an rreq (Reader Requirements) box is written first — it is
        /// required by ISO/IEC 15444-2 for every conformant JPX file.
        /// </summary>
        /// <returns>Total bytes written for metadata.</returns>
        private int writeMetadataBoxes()
        {
            var bytesWritten = 0;

            // Write Reader Requirements box (required for conformant JPX files)
            if (Metadata.HasJpxBoxes || Metadata.UseJpxBrand || Metadata.ReaderRequirements != null)
            {
                var rreq = Metadata.ReaderRequirements ?? new ReaderRequirementsBox();
                bytesWritten += writeContentBox(FileFormatBoxes.READER_REQUIREMENTS_BOX, rreq.GetContentBytes());
            }

            // Write XML boxes
            foreach (var xmlBox in Metadata.XmlBoxes)
            {
                bytesWritten += writeXMLBox(xmlBox);
            }

            // Write UUID boxes
            foreach (var uuidBox in Metadata.UuidBoxes)
            {
                bytesWritten += writeUUIDBox(uuidBox);
            }

            // Write UUID Info box if present
            if (Metadata.UuidInfo != null && Metadata.UuidInfo.UuidList.Count > 0)
            {
                bytesWritten += writeUUIDInfoBox(Metadata.UuidInfo);
            }

            // Write Intellectual Property Rights (JPR) boxes (JPEG 2000 Part 2)
            foreach (var jprBox in Metadata.IntellectualPropertyRights)
            {
                bytesWritten += writeJPRBox(jprBox);
            }

            // Write Label boxes (JPEG 2000 Part 2)
            foreach (var labelBox in Metadata.Labels)
            {
                bytesWritten += writeLabelBox(labelBox);
            }

            // Write JPEG 2000 Part 2 (JPX) extended boxes
            foreach (var asoc in Metadata.Associations)
            {
                bytesWritten += writeContentBox(FileFormatBoxes.ASSOCIATION_BOX, asoc.GetContentBytes());
            }

            if (Metadata.DataReference != null && Metadata.DataReference.Entries.Count > 0)
            {
                bytesWritten += writeContentBox(FileFormatBoxes.DATA_REFERENCE_BOX, Metadata.DataReference.GetContentBytes());
            }

            foreach (var ftbl in Metadata.FragmentTables)
            {
                bytesWritten += writeContentBox(FileFormatBoxes.FRAGMENT_TABLE_BOX, ftbl.GetContentBytes());
            }

            foreach (var cref in Metadata.CrossReferences)
            {
                bytesWritten += writeContentBox(FileFormatBoxes.CROSS_REFERENCE_BOX, cref.GetContentBytes());
            }

            foreach (var jpch in Metadata.CodestreamHeaders)
            {
                bytesWritten += writeContentBox(FileFormatBoxes.CODESTREAM_HEADER_BOX, jpch.GetContentBytes());
            }

            foreach (var jplh in Metadata.CompositingLayerHeaders)
            {
                bytesWritten += writeContentBox(FileFormatBoxes.COMPOSITING_LAYER_HEADER_BOX, jplh.GetContentBytes());
            }

            return bytesWritten;
        }

        /// <summary>
        /// Writes a box consisting of the standard 8-byte header followed by the supplied
        /// pre-serialized content bytes (DBox). Used for the JPEG 2000 Part 2 (JPX) boxes
        /// whose binary form is produced by their model classes.
        /// </summary>
        /// <param name="boxType">The box type (TBox).</param>
        /// <param name="content">The box payload, excluding the header.</param>
        /// <returns>Total number of bytes written, including the 8-byte header.</returns>
        private int writeContentBox(int boxType, byte[] content)
        {
            content = content ?? Array.Empty<byte>();
            var boxLength = 8 + content.Length;

            fi.writeInt(boxLength);   // LBox
            fi.writeInt(boxType);     // TBox
            if (content.Length > 0)
                fi.write(content, 0, content.Length);

            return boxLength;
        }

        /// <summary>
        /// Writes an XML box to the file.
        /// </summary>
        /// <param name="xmlBox">The XML box to write.</param>
        /// <returns>Number of bytes written.</returns>
        private int writeXMLBox(XmlBox xmlBox)
        {
            if (string.IsNullOrEmpty(xmlBox.XmlContent))
                return 0;

            try
            {
                // Convert XML content to UTF-8 bytes
                var xmlBytes = Encoding.UTF8.GetBytes(xmlBox.XmlContent);
                var boxLength = 8 + xmlBytes.Length; // 8 bytes for LBox + TBox

                // Write box length (LBox)
                fi.writeInt(boxLength);

                // Write XML box type (TBox)
                fi.writeInt(FileFormatBoxes.XML_BOX);

                // Write XML data
                fi.write(xmlBytes, 0, xmlBytes.Length);

                return boxLength;
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Error writing XML box: {e.Message}");
            }
        }

        /// <summary>
        /// Writes a UUID box to the file.
        /// </summary>
        /// <param name="uuidBox">The UUID box to write.</param>
        /// <returns>Number of bytes written.</returns>
        private int writeUUIDBox(UuidBox uuidBox)
        {
            if (uuidBox.Data == null || uuidBox.Data.Length == 0)
                return 0;

            try
            {
                var boxLength = 8 + 16 + uuidBox.Data.Length; // LBox(4) + TBox(4) + UUID(16) + Data

                // Write box length (LBox)
                fi.writeInt(boxLength);

                // Write UUID box type (TBox)
                fi.writeInt(FileFormatBoxes.UUID_BOX);

                // Write UUID (16 bytes)
                var uuidBytes = uuidBox.Uuid.ToByteArray();
                fi.write(uuidBytes, 0, 16);

                // Write data
                fi.write(uuidBox.Data, 0, uuidBox.Data.Length);

                return boxLength;
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Error writing UUID box: {e.Message}");
            }
        }

        /// <summary>
        /// Writes an Intellectual Property Rights (JPR) box to the file (JPEG 2000 Part 2).
        /// The JPR box contains copyright or intellectual property rights information.
        /// </summary>
        /// <param name="jprBox">The JPR box to write.</param>
        /// <returns>Number of bytes written.</returns>
        private int writeJPRBox(JprBox jprBox)
        {
            if (jprBox == null)
                return 0;

            try
            {
                byte[] dataBytes;

                // Use raw data if available, otherwise convert text to UTF-8
                if (jprBox.IsBinary)
                {
                    dataBytes = jprBox.RawData;
                }
                else if (!string.IsNullOrEmpty(jprBox.Text))
                {
                    dataBytes = Encoding.UTF8.GetBytes(jprBox.Text);
                }
                else
                {
                    return 0; // No data to write
                }

                var boxLength = 8 + dataBytes.Length; // 8 bytes for LBox + TBox

                // Write box length (LBox)
                fi.writeInt(boxLength);

                // Write JPR box type (TBox)
                fi.writeInt(FileFormatBoxes.JPR_BOX);

                // Write JPR data
                fi.write(dataBytes, 0, dataBytes.Length);

                return boxLength;
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Error writing Intellectual Property Rights (JPR) box: {e.Message}");
            }
        }

        /// <summary>
        /// Writes a Label (LBL) box to the file (JPEG 2000 Part 2).
        /// The Label box contains human-readable text labels.
        /// </summary>
        /// <param name="labelBox">The Label box to write.</param>
        /// <returns>Number of bytes written.</returns>
        private int writeLabelBox(LabelBox labelBox)
        {
            if (labelBox == null)
                return 0;

            try
            {
                byte[] dataBytes;

                // Use raw data if available, otherwise convert label to UTF-8
                if (labelBox.IsBinary)
                {
                    dataBytes = labelBox.RawData;
                }
                else if (!string.IsNullOrEmpty(labelBox.Label))
                {
                    dataBytes = Encoding.UTF8.GetBytes(labelBox.Label);
                }
                else
                {
                    return 0; // No data to write
                }

                var boxLength = 8 + dataBytes.Length; // 8 bytes for LBox + TBox

                // Write box length (LBox)
                fi.writeInt(boxLength);

                // Write Label box type (TBox)
                fi.writeInt(FileFormatBoxes.LBL_BOX);

                // Write Label data
                fi.write(dataBytes, 0, dataBytes.Length);

                return boxLength;
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Error writing Label (LBL) box: {e.Message}");
            }
        }

        /// <summary>
        /// Writes a UUID Info (uinf) box to the file.
        /// The UUID Info box is a superbox containing a UUID List box and optionally a URL box.
        /// Format per ISO/IEC 15444-1 Section I.7.1: uinf -> [ulst] + [url?]
        /// </summary>
        /// <param name="uuidInfo">The UUID Info box to write.</param>
        /// <returns>Number of bytes written.</returns>
        private int writeUUIDInfoBox(UuidInfoBox uuidInfo)
        {
            if (uuidInfo == null || uuidInfo.UuidList.Count == 0)
                return 0;

            try
            {
                // Calculate content length
                var contentLength = 0;

                // UUID List box length: LBox(4) + TBox(4) + NU(2) + UUIDs(16*count)
                var ulistLength = 8 + 2 + (16 * uuidInfo.UuidList.Count);
                contentLength += ulistLength;

                // URL box length if present: LBox(4) + TBox(4) + VERS(1) + FLAG(3) + URL
                var urlLength = 0;
                if (!string.IsNullOrEmpty(uuidInfo.Url))
                {
                    var urlBytes = Encoding.UTF8.GetBytes(uuidInfo.Url);
                    urlLength = 8 + 4 + urlBytes.Length; // LBox + TBox + VERS + FLAG(3) + URL
                    contentLength += urlLength;
                }

                // UUID Info superbox length: LBox(4) + TBox(4) + content
                var boxLength = 8 + contentLength;

                // Write UUID Info superbox header
                fi.writeInt(boxLength);
                fi.writeInt(FileFormatBoxes.UUID_INFO_BOX);

                // Write UUID List box
                fi.writeInt(ulistLength); // LBox
                fi.writeInt(FileFormatBoxes.UUID_LIST_BOX); // TBox

                // Write NU (number of UUIDs) - 2 bytes
                fi.writeShort((short)uuidInfo.UuidList.Count);

                // Write each UUID (16 bytes each)
                foreach (var uuid in uuidInfo.UuidList)
                {
                    var uuidBytes = uuid.ToByteArray();
                    fi.write(uuidBytes, 0, 16);
                }

                // Write URL box if present
                if (!string.IsNullOrEmpty(uuidInfo.Url))
                {
                    var urlBytes = Encoding.UTF8.GetBytes(uuidInfo.Url);

                    fi.writeInt(urlLength); // LBox
                    fi.writeInt(FileFormatBoxes.URL_BOX); // TBox

                    // Write VERS (1 byte)
                    fi.writeByte(uuidInfo.UrlVersion);

                    // Write FLAG (3 bytes) - FLAG[0]=0, FLAG[1]=0, FLAG[2]=UrlFlags
                    fi.writeByte(0);
                    fi.writeByte(0);
                    fi.writeByte(uuidInfo.UrlFlags);

                    // Write URL data
                    fi.write(urlBytes, 0, urlBytes.Length);
                }

                return boxLength;
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Error writing UUID Info box: {e.Message}");
            }
        }

        /// <summary> This method writes the File Type box.
        ///
        /// When the attached metadata contains JPEG 2000 Part 2 (JPX) boxes, or
        /// <see cref="J2KMetadata.UseJpxBrand"/> is set, the box advertises the 'jpx '
        /// brand with a 'jp2 ' + 'jpx ' compatibility list; otherwise the baseline
        /// 'jp2 ' brand is written.
        /// </summary>
        /// <returns>The total number of bytes written, including the 8-byte header.</returns>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        ///
        /// </exception>
        public virtual int writeFileTypeBox()
        {
            var useJpx = Metadata != null && (Metadata.UseJpxBrand || Metadata.HasJpxBoxes);

            if (useJpx)
            {
                // LBox(4) + TBox(4) + BR(4) + MinV(4) + CL[0](4) + CL[1](4) = 24
                const int jpxLength = 24;

                fi.writeInt(jpxLength);                  // LBox
                fi.writeInt(FileFormatBoxes.FILE_TYPE_BOX); // TBox
                fi.writeInt(FileFormatBoxes.FT_BR_JPX);  // BR = 'jpx '
                fi.writeInt(0);                          // MinV
                fi.writeInt(FileFormatBoxes.FT_BR);      // CL[0] = 'jp2 '
                fi.writeInt(FileFormatBoxes.FT_BR_JPX);  // CL[1] = 'jpx '

                return jpxLength;
            }

            // Baseline JP2 File Type box
            // LBox(4) + TBox (4) + BR(4) + MinV(4) + CL(4) = 20
            fi.writeInt(FTB_LENGTH);

            // Write File Type box (TBox)
            fi.writeInt(FileFormatBoxes.FILE_TYPE_BOX);

            // Write File Type data (DBox)
            // Write Brand box (BR)
            fi.writeInt(FileFormatBoxes.FT_BR);

            // Write Minor Version
            fi.writeInt(0);

            // Write Compatibility list
            fi.writeInt(FileFormatBoxes.FT_BR);

            return FTB_LENGTH;
        }

        /// <summary> This method writes the JP2Header box
        /// 
        /// </summary>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual void writeJP2HeaderBox()
        {
            // Calculate color specification box length
            int csbLength;
            if (Metadata != null && Metadata.IccProfile != null && Metadata.IccProfile.IsValid)
            {
                // ICC profiled: Header(8) + METH(1) + PREC(1) + APPROX(1) + Size(4) + Profile
                csbLength = 15 + Metadata.IccProfile.ProfileSize;
            }
            else
            {
                // Enumerated colorspace
                csbLength = CSB_LENGTH;
            }

            // Calculate palette box length
            int paletteLength = 0;
            if (Metadata != null && Metadata.Palette != null)
            {
                var palette = Metadata.Palette;
                // LBox(4) + TBox(4) + NE(2) + NPC(1) + B[NPC] + entries
                paletteLength = 8 + 3 + palette.NumColumns;
                
                // Add entry data size
                for (int i = 0; i < palette.NumEntries; i++)
                {
                    for (int j = 0; j < palette.NumColumns; j++)
                    {
                        var bitDepth = palette.GetBitDepth(j);
                        paletteLength += (bitDepth <= 8) ? 1 : 2;
                    }
                }
            }

            // Calculate component mapping box length
            int cmapLength = 0;
            if (Metadata != null && Metadata.ComponentMapping != null)
            {
                // LBox(4) + TBox(4) + (CMP(2) + MTYP(1) + PCOL(1)) * numChannels
                cmapLength = 8 + (4 * Metadata.ComponentMapping.NumChannels);
            }

            // Calculate resolution box length
            int resLength = 0;
            if (Metadata != null && Metadata.Resolution != null && Metadata.Resolution.HasResolution)
            {
                // Resolution superbox: LBox(4) + TBox(4) + content
                resLength = 8;
                
                if (Metadata.Resolution.HasCaptureResolution)
                {
                    resLength += 18; // Capture resolution box: LBox(4) + TBox(4) + data(10)
                }
                
                if (Metadata.Resolution.HasDisplayResolution)
                {
                    resLength += 18; // Display resolution box: LBox(4) + TBox(4) + data(10)
                }
            }

            // Calculate channel definition box length
            int cdefLength = 0;
            if (Metadata != null && Metadata.ChannelDefinitions != null && Metadata.ChannelDefinitions.HasDefinitions)
            {
                // Channel Definition box: LBox(4) + TBox(4) + N(2) + (Cn(2) + Typ(2) + Asoc(2)) * N
                var numChannels = Metadata.ChannelDefinitions.Channels.Count;
                cdefLength = 8 + 2 + (numChannels * 6);
            }

            // Calculate total JP2 header length
            var headerLength = 8 + IHB_LENGTH + csbLength;
            
            if (bpcVaries)
                headerLength += BPC_LENGTH + nc;
                
            if (paletteLength > 0)
                headerLength += paletteLength;
                
            if (cmapLength > 0)
                headerLength += cmapLength;
                
            if (cdefLength > 0)
                headerLength += cdefLength;

            if (resLength > 0)
                headerLength += resLength;

            // Write box length (LBox)
            fi.writeInt(headerLength);

            // Write a JP2Header (TBox)
            fi.writeInt(FileFormatBoxes.JP2_HEADER_BOX);

            // Write image header box (required, must be first)
            writeImageHeaderBox();

            // Write Colour Specification Box (required)
            writeColourSpecificationBox();

            // Write Bits Per Component box if needed (optional)
            if (bpcVaries)
                writeBitsPerComponentBox();

            // Write Palette box if present (optional, must come before cmap)
            if (paletteLength > 0)
                writePaletteBox();

            // Write Component Mapping box if present (optional, must come after palette)
            if (cmapLength > 0)
                writeComponentMappingBox();

            // Write channel definition box if present (optional)
            if (cdefLength > 0)
                writeChannelDefinitionBox();

            // Write resolution box if present (optional)
            if (resLength > 0)
                writeResolutionBox();
        }

        /// <summary>
        /// Writes the Channel Definition Box which defines the interpretation of image components.
        /// </summary>
        private void writeChannelDefinitionBox()
        {
            if (Metadata?.ChannelDefinitions == null || !Metadata.ChannelDefinitions.HasDefinitions)
                return;

            var channels = Metadata.ChannelDefinitions.Channels;
            var numChannels = channels.Count;

            // Calculate box length: LBox(4) + TBox(4) + N(2) + (Cn(2) + Typ(2) + Asoc(2)) * N
            var boxLength = 8 + 2 + (numChannels * 6);

            // Write box length (LBox)
            fi.writeInt(boxLength);

            // Write Channel Definition box type (TBox)
            fi.writeInt(FileFormatBoxes.CHANNEL_DEFINITION_BOX);

            // Write number of channel definitions (N)
            fi.writeShort((short)numChannels);

            // Write each channel definition
            foreach (var channel in channels)
            {
                fi.writeShort((short)channel.ChannelIndex);      // Cn
                fi.writeShort((short)channel.ChannelType);        // Typ
                fi.writeShort((short)channel.Association);        // Asoc
            }
        }

        /// <summary>
        /// Writes the Resolution superbox containing capture and/or display resolution boxes.
        /// </summary>
        private void writeResolutionBox()
        {
            if (Metadata?.Resolution == null || !Metadata.Resolution.HasResolution)
                return;

            // Calculate content length
            var contentLength = 0;
            if (Metadata.Resolution.HasCaptureResolution)
                contentLength += 18;
            if (Metadata.Resolution.HasDisplayResolution)
                contentLength += 18;

            // Write Resolution superbox header
            fi.writeInt(8 + contentLength); // LBox
            fi.writeInt(FileFormatBoxes.RESOLUTION_BOX); // TBox

            // Write capture resolution box if present
            if (Metadata.Resolution.HasCaptureResolution)
            {
                writeResolutionSubBox(
                    Metadata.Resolution.HorizontalCaptureResolution.Value,
                    Metadata.Resolution.VerticalCaptureResolution.Value,
                    FileFormatBoxes.CAPTURE_RESOLUTION_BOX);
            }

            // Write display resolution box if present
            if (Metadata.Resolution.HasDisplayResolution)
            {
                writeResolutionSubBox(
                    Metadata.Resolution.HorizontalDisplayResolution.Value,
                    Metadata.Resolution.VerticalDisplayResolution.Value,
                    FileFormatBoxes.DEFAULT_DISPLAY_RESOLUTION_BOX);
            }
        }

        /// <summary>
        /// Writes a resolution sub-box (capture or display resolution).
        /// Resolution is stored as: VR_N(2), VR_D(2), HR_N(2), HR_D(2), VR_E(1), HR_E(1)
        /// where resolution = (numerator / denominator) * 10^exponent
        /// </summary>
        private void writeResolutionSubBox(double horizontalRes, double verticalRes, int boxType)
        {
            // Convert resolution to fraction with exponent
            // We'll use a simple approach: express as integer * 10^exponent
            
            // Find appropriate exponent to keep values in a reasonable range
            var hr_exp = GetResolutionExponent(horizontalRes);
            var vr_exp = GetResolutionExponent(verticalRes);

            // Calculate numerator and denominator
            // For simplicity, use denominator = 1 (could be optimized for better precision)
            var hr_num = (short)(horizontalRes / Math.Pow(10, hr_exp));
            var vr_num = (short)(verticalRes / Math.Pow(10, vr_exp));
            const short denominator = 1;

            // Write resolution box
            fi.writeInt(18); // LBox: 4 (LBox) + 4 (TBox) + 10 (data)
            fi.writeInt(boxType); // TBox

            // Write vertical resolution
            fi.writeShort(vr_num); // VR_N
            fi.writeShort(denominator); // VR_D
            
            // Write horizontal resolution
            fi.writeShort(hr_num); // HR_N
            fi.writeShort(denominator); // HR_D
            
            // Write exponents
            fi.writeByte((sbyte)vr_exp); // VR_E
            fi.writeByte((sbyte)hr_exp); // HR_E
        }

        /// <summary>
        /// Calculates an appropriate exponent for resolution values.
        /// Tries to keep the mantissa in a reasonable range (avoiding very large or small shorts).
        /// </summary>
        private int GetResolutionExponent(double resolution)
        {
            if (resolution == 0)
                return 0;

            var absRes = Math.Abs(resolution);
            
            // Find exponent such that 1 <= (resolution / 10^exp) < 32767
            var exp = 0;
            
            while (absRes / Math.Pow(10, exp) >= 32767)
                exp++;
                
            while (absRes / Math.Pow(10, exp) < 1 && exp > -10)
                exp--;

            return exp;
        }



        /// <summary> This method writes the Bits Per Component box
        /// 
        /// </summary>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual void writeBitsPerComponentBox()
        {

            // Write box length (LBox)
            fi.writeInt(BPC_LENGTH + nc);

            // Write a Bits Per Component box (TBox)
            fi.writeInt(FileFormatBoxes.BITS_PER_COMPONENT_BOX);

            // Write bpc fields
            for (var i = 0; i < nc; i++)
            {
                fi.writeByte(bpc[i] - 1);
            }
        }

        /// <summary>
        /// Writes the Palette Box (pclr) which defines a lookup table for indexed color images.
        /// Format: NE(2) + NPC(1) + B[NPC](1) + C[NE][NPC](1 or 2 each)
        /// </summary>
        private void writePaletteBox()
        {
            if (Metadata?.Palette == null)
                return;

            var palette = Metadata.Palette;

            // Calculate box length
            var dataLength = 3 + palette.NumColumns; // NE(2) + NPC(1) + B[NPC]
            
            // Add entry data size
            for (int i = 0; i < palette.NumEntries; i++)
            {
                for (int j = 0; j < palette.NumColumns; j++)
                {
                    var bitDepth = palette.GetBitDepth(j);
                    dataLength += (bitDepth <= 8) ? 1 : 2;
                }
            }

            var boxLength = 8 + dataLength; // LBox(4) + TBox(4) + data

            // Write box length (LBox)
            fi.writeInt(boxLength);

            // Write Palette box type (TBox)
            fi.writeInt(FileFormatBoxes.PALETTE_BOX);

            // Write NE (number of entries) - 2 bytes
            fi.writeShort((short)palette.NumEntries);

            // Write NPC (number of palette columns) - 1 byte
            fi.writeByte((byte)palette.NumColumns);

            // Write B array (bit depths for each column)
            for (int i = 0; i < palette.NumColumns; i++)
            {
                fi.writeByte((byte)palette.BitDepths[i]);
            }

            // Write palette entries
            for (int entryIdx = 0; entryIdx < palette.NumEntries; entryIdx++)
            {
                for (int colIdx = 0; colIdx < palette.NumColumns; colIdx++)
                {
                    var value = palette.GetEntry(entryIdx, colIdx);
                    var bitDepth = palette.GetBitDepth(colIdx);

                    if (bitDepth <= 8)
                    {
                        // 8-bit entry
                        fi.writeByte((byte)(value & 0xFF));
                    }
                    else if (bitDepth <= 16)
                    {
                        // 16-bit entry
                        fi.writeShort((short)(value & 0xFFFF));
                    }
                    else
                    {
                        throw new InvalidOperationException($"Palette bit depth > 16 not supported: {bitDepth}");
                    }
                }
            }
        }

        /// <summary>
        /// Writes the Component Mapping Box (cmap) which maps codestream components to output channels.
        /// Format: Array of {CMP(2) + MTYP(1) + PCOL(1)} entries
        /// </summary>
        private void writeComponentMappingBox()
        {
            if (Metadata?.ComponentMapping == null)
                return;

            var componentMapping = Metadata.ComponentMapping;
            var numChannels = componentMapping.NumChannels;

            // Calculate box length: LBox(4) + TBox(4) + (CMP(2) + MTYP(1) + PCOL(1)) * numChannels
            var boxLength = 8 + (4 * numChannels);

            // Write box length (LBox)
            fi.writeInt(boxLength);

            // Write Component Mapping box type (TBox)
            fi.writeInt(FileFormatBoxes.COMPONENT_MAPPING_BOX);

            // Write each mapping entry
            for (int i = 0; i < numChannels; i++)
            {
                var componentIndex = componentMapping.GetComponentIndex(i);
                var mappingType = componentMapping.GetMappingType(i);
                var paletteColumn = componentMapping.GetPaletteColumn(i);

                // Write CMP (component index) - 2 bytes
                fi.writeShort((short)componentIndex);

                // Write MTYP (mapping type) - 1 byte
                fi.writeByte(mappingType);

                // Write PCOL (palette column) - 1 byte
                fi.writeByte(paletteColumn);
            }
        }

        /// <summary> This method writes the Colour Specification box
        /// 
        /// </summary>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual void writeColourSpecificationBox()
        {
            // Check if ICC profile is present in metadata
            if (Metadata != null && Metadata.IccProfile != null && Metadata.IccProfile.IsValid)
            {
                // Write ICC profiled color specification box
                var profileBytes = Metadata.IccProfile.ProfileBytes;
                var boxLength = 15 + profileBytes.Length; // Header(8) + METH(1) + PREC(1) + APPROX(1) + Profile size(4) + Profile

                // Write box length (LBox)
                fi.writeInt(boxLength);

                // Write Color Specification box type (TBox)
                fi.writeInt(FileFormatBoxes.COLOUR_SPECIFICATION_BOX);

                // Write METH field (2 = ICC profile)
                fi.writeByte(2);

                // Write PREC field (precedence = 0)
                fi.writeByte(0);

                // Write APPROX field (0 = accurate)
                fi.writeByte(0);

                // Write ICC profile size (stored as big-endian int)
                fi.writeInt(profileBytes.Length);

                // Write ICC profile data
                fi.write(profileBytes, 0, profileBytes.Length);
            }
            else
            {
                // Write enumerated color specification box (original behavior)
                
                // Write box length (LBox)
                fi.writeInt(CSB_LENGTH);

                // Write Color Specification box (TBox)
                fi.writeInt(FileFormatBoxes.COLOUR_SPECIFICATION_BOX);

                // Write METH field (1 = enumerated)
                fi.writeByte(FileFormatBoxes.CSB_METH);

                // Write PREC field
                fi.writeByte(FileFormatBoxes.CSB_PREC);

                // Write APPROX field
                fi.writeByte(FileFormatBoxes.CSB_APPROX);

                // Write EnumCS field
                fi.writeInt(nc > 1 ? FileFormatBoxes.CSB_ENUM_SRGB : FileFormatBoxes.CSB_ENUM_GREY);
            }
        }

        /// <summary> This method writes the Image Header box
        /// 
        /// </summary>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual void writeImageHeaderBox()
        {

            // Write box length
            fi.writeInt(IHB_LENGTH);

            // Write ihdr box name
            fi.writeInt(FileFormatBoxes.IMAGE_HEADER_BOX);

            // Write HEIGHT field
            fi.writeInt(height);

            // Write WIDTH field
            fi.writeInt(width);

            // Write NC field
            fi.writeShort(nc);

            // Write BPC field
            // if the number of bits per component varies write 0xff else write
            // number of bits per components
            if (bpcVaries)
                fi.writeByte(0xff);
            else
                fi.writeByte(bpc[0] - 1);

            // Write C field
            fi.writeByte(FileFormatBoxes.IMB_C);

            // Write UnkC field
            fi.writeByte(FileFormatBoxes.IMB_UnkC);

            // Write IPR field
            fi.writeByte(FileFormatBoxes.IMB_IPR);
        }

        /// <summary> This method writes the Contiguous codestream box
        /// 
        /// </summary>
        /// <param name="cs">The contiguous codestream
        /// 
        /// </param>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        public virtual void writeContiguousCodeStreamBox(byte[] cs)
        {
            // Write box with automatic XLBox support if needed
            WriteBoxHeader(FileFormatBoxes.CONTIGUOUS_CODESTREAM_BOX, clength);

            // Write codestream
            for (var i = 0; i < clength; i++)
                fi.writeByte(cs[i]);
        }

        /// <summary>
        /// Writes a box header with automatic Extended Length (XLBox) support.
        /// If the content length exceeds uint.MaxValue (4GB), automatically uses XLBox format.
        /// </summary>
        /// <param name="boxType">The box type (TBox) - e.g., FileFormatBoxes.CONTIGUOUS_CODESTREAM_BOX</param>
        /// <param name="contentLength">The length of the box content in bytes</param>
        /// <exception cref="IOException">If an I/O error occurred.</exception>
        private void WriteBoxHeader(int boxType, long contentLength)
        {
            var totalLength = contentLength + 8; // Standard header is 8 bytes

            if (totalLength > uint.MaxValue)
            {
                // Use Extended Length (XLBox) format
                // Header: LBox(4)=1 + TBox(4) + XLBox(8)
                fi.writeInt(1);                          // LBox = 1 indicates XLBox
                fi.writeInt(boxType);                    // TBox
                fi.writeLong(contentLength + 16);        // XLBox includes 16-byte header
            }
            else
            {
                // Use standard box format
                // Header: LBox(4) + TBox(4)
                fi.writeInt((int)totalLength);           // LBox
                fi.writeInt(boxType);                    // TBox
            }
        }

        /// <summary>
        /// Writes a box header with automatic Extended Length (XLBox) support, given total box length.
        /// This overload is useful when you already calculated the total length including header.
        /// </summary>
        /// <param name="boxType">The box type (TBox)</param>
        /// <param name="totalBoxLength">The total length of the box including header</param>
        /// <param name="isTotal">Set to true to indicate totalBoxLength includes header</param>
        private void WriteBoxHeaderTotal(int boxType, long totalBoxLength, bool isTotal = true)
        {
            if (totalBoxLength > uint.MaxValue)
            {
                // Use Extended Length (XLBox) format
                fi.writeInt(1);                          // LBox = 1
                fi.writeInt(boxType);                    // TBox
                fi.writeLong(totalBoxLength);            // XLBox
            }
            else
            {
                // Use standard box format
                fi.writeInt((int)totalBoxLength);        // LBox
                fi.writeInt(boxType);                    // TBox
            }
        }

        /// <summary>
        /// Gets or sets the metadata (comments, XML, UUID boxes) to write to the file.
        /// Set this before calling writeFileFormat().
        /// </summary>
        public J2KMetadata Metadata { get; set; }
    }
}