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
using CoreJ2K.j2k.codestream;
using CoreJ2K.j2k.fileformat.metadata;
using CoreJ2K.j2k.io;
using CoreJ2K.j2k.util;
using System;
using System.Collections.Generic;
using System.Text;

namespace CoreJ2K.j2k.fileformat.reader
{

    /// <summary> This class reads the file format wrapper that may or may not exist around a
    /// valid JPEG 2000 codestream. Since no information from the file format is
    /// used in the actual decoding, this class simply goes through the file and
    /// finds the first valid codestream.
    /// 
    /// </summary>
    /// <seealso cref="j2k.fileformat.writer.FileFormatWriter" />
    public class FileFormatReader
    {
        /// <summary> This method creates and returns an array of positions to contiguous
        /// codestreams in the file
        /// 
        /// </summary>
        /// <returns> The positions of the contiguous codestreams in the file
        /// 
        /// </returns>
        public virtual long[] CodeStreamPos
        {
            get
            {
                var size = codeStreamPos.Count;
                var pos = new long[size];
                for (var i = 0; i < size; i++)
                    pos[i] = codeStreamPos[i];
                return pos;
            }

        }
        /// <summary> This method returns the position of the first contiguous codestreams in
        /// the file
        /// 
        /// </summary>
        /// <returns> The position of the first contiguous codestream in the file
        /// 
        /// </returns>
        public virtual int FirstCodeStreamPos => codeStreamPos[0];

        /// <summary> This method returns the length of the first contiguous codestreams in
        /// the file
        ///
        /// </summary>
        /// <returns> The length in bytes of the first contiguous codestream in the file
        /// (the jp2c box payload, excluding the box header)
        ///
        /// </returns>
        public virtual int FirstCodeStreamLength => codeStreamLength[0];

        /// <summary>The random access from which the file format boxes are read </summary>
        private readonly RandomAccessIO inStream;

        /// <summary>The positions of the codestreams in the fileformat</summary>
        private List<int> codeStreamPos;

        /// <summary>The lengths of the codestreams in the fileformat</summary>
        private List<int> codeStreamLength;

        /// <summary>Flag indicating whether or not the JP2 file format is used </summary>
        public bool JP2FFUsed;

        /// <summary>
        /// Gets the metadata (comments, XML, UUID boxes) extracted from the file.
        /// </summary>
        public J2KMetadata Metadata { get; } = new J2KMetadata();

        /// <summary>
        /// Gets the JP2 file structure information for validation.
        /// </summary>
        public JP2Structure FileStructure { get; private set; }

        /// <summary>
        /// Gets the validator used for comprehensive JP2 format validation.
        /// </summary>
        public JP2Validator Validator { get; private set; }

        /// <summary>
        /// Gets or sets whether to perform strict validation (throws exceptions on errors).
        /// Default is false (warnings only).
        /// </summary>
        public bool StrictValidation { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to perform comprehensive codestream validation.
        /// This validates all codestream markers per ISO/IEC 15444-1 Annex A.
        /// Default is false (only basic validation).
        /// </summary>
        public bool ComprehensiveCodestreamValidation { get; set; } = false;

        /// <summary>
        /// Gets or sets maximum bytes to read for codestream validation.
        /// 0 = validate entire codestream. Smaller values improve performance for large files.
        /// Default is 65536 (64KB) for performance.
        /// </summary>
        public int MaxCodestreamValidationBytes { get; set; } = 65536;

        /// <summary> The constructor of the FileFormatReader
        /// 
        /// </summary>
        /// <param name="in">The RandomAccessIO from which to read the file format
        /// 
        /// </param>
        public FileFormatReader(RandomAccessIO inStream)
        {
            this.inStream = inStream;
        }

        /// <summary> This method checks whether the given RandomAccessIO is a valid JP2 file
        /// and if so finds the first codestream in the file. Currently, the
        /// information in the codestream is not used
        /// 
        /// </summary>
        /// <param name="in">The RandomAccessIO from which to read the file format
        /// 
        /// </param>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        /// <exception cref="java.io.EOFException">If end of file is reached
        /// 
        /// </exception>
        public virtual void readFileFormat()
        {
            // Initialize validation structures
            FileStructure = new JP2Structure();
            Validator = new JP2Validator();

            //int foundCodeStreamBoxes = 0;
            int box;
            int length;
            long longLength = 0;
            int pos;
            short marker;
            var jp2HeaderBoxFound = false;
            var lastBoxFound = false;
            var jp2HeaderBoxOrder = 0; // Track order of boxes within JP2 Header

            try
            {

                // Go through the randomaccessio and find the first contiguous
                // codestream box. Check also that the File Format is correct

                // Make sure that the first 12 bytes is the JP2_SIGNATURE_BOX or
                // if not that the first 2 bytes is the SOC marker
                var firstInt = inStream.readInt();
                var secondInt = inStream.readInt();
                var thirdInt = inStream.readInt();

                if (firstInt != 0x0000000c || secondInt != FileFormatBoxes.JP2_SIGNATURE_BOX || thirdInt != 0x0d0a870a)
                {
                    // Not a JP2 file
                    inStream.seek(0);

                    marker = inStream.readShort();
                    if (marker != Markers.SOC)
                        //Standard syntax marker found
                        throw new InvalidOperationException("File is neither valid JP2 file nor " + "valid JPEG 2000 codestream");
                    JP2FFUsed = false;
                    inStream.seek(0);
                    return;
                }

                // The JP2 File format is being used
                JP2FFUsed = true;

                // Record signature box information
                FileStructure.HasSignatureBox = true;
                FileStructure.SignatureBoxPosition = 0;
                FileStructure.SignatureBoxLength = 12;

                // Read File Type box
                if (!readFileTypeBox())
                {
                    // Not a valid JP2 file or codestream
                    throw new InvalidOperationException("Invalid JP2 file: File Type box missing");
                }

                // Read all remaining boxes 
                while (!lastBoxFound)
                {
                    pos = inStream.Pos;
                    length = inStream.readInt();
                    if ((pos + length) == inStream.length())
                        lastBoxFound = true;

                    box = inStream.readInt();
                    if (length == 0)
                    {
                        // Box extends to end of file; reconstruct the total box length
                        // (including the 8-byte header) so it matches sized boxes.
                        lastBoxFound = true;
                        length = inStream.length() - pos;
                    }
                    else if (length == 1)
                    {
                        longLength = inStream.readLong();
                        throw new System.IO.IOException("File too long.");
                    }
                    else
                        longLength = 0;

                    switch (box)
                    {

                        case FileFormatBoxes.CONTIGUOUS_CODESTREAM_BOX:
                            if (!jp2HeaderBoxFound)
                            {
                                throw new InvalidOperationException("Invalid JP2 file: JP2Header box not " + "found before Contiguous codestream " + "box ");
                            }
                            readContiguousCodeStreamBox(pos, length, longLength);
                            FileStructure.HasContiguousCodestreamBox = true;
                            FileStructure.ContiguousCodestreamBoxPosition = pos;
                            break;

                        case FileFormatBoxes.JP2_HEADER_BOX:
                            if (jp2HeaderBoxFound)
                            {
                                FileStructure.JP2HeaderBoxCount++;
                                throw new InvalidOperationException("Invalid JP2 file: Multiple " + "JP2Header boxes found");
                            }
                            readJP2HeaderBox(pos, length, longLength, ref jp2HeaderBoxOrder);
                            jp2HeaderBoxFound = true;
                            FileStructure.HasJP2HeaderBox = true;
                            FileStructure.JP2HeaderBoxPosition = pos;
                            FileStructure.JP2HeaderBoxLength = length;
                            FileStructure.JP2HeaderBoxCount = 1;
                            break;

                        case FileFormatBoxes.INTELLECTUAL_PROPERTY_BOX:
                            readIntPropertyBox(length);
                            if (!jp2HeaderBoxFound)
                                FileStructure.HasMetadataBeforeHeader = true;
                            break;

                        case FileFormatBoxes.XML_BOX:
                            readXMLBox(length);
                            if (!jp2HeaderBoxFound)
                                FileStructure.HasMetadataBeforeHeader = true;
                            break;

                        case FileFormatBoxes.UUID_BOX:
                            readUUIDBox(length);
                            if (!jp2HeaderBoxFound)
                                FileStructure.HasMetadataBeforeHeader = true;
                            break;

                        case FileFormatBoxes.UUID_INFO_BOX:
                            readUUIDInfoBox(length);
                            break;

                        case FileFormatBoxes.READER_REQUIREMENTS_BOX:
                            readReaderRequirementsBox(length);
                            break;

                        case FileFormatBoxes.JPR_BOX:
                            readJPRBox(length);
                            break;

                        case FileFormatBoxes.LBL_BOX:
                            readLabelBox(length);
                            break;

                        case FileFormatBoxes.ASSOCIATION_BOX:
                            readAssociationBox(length);
                            break;

                        case FileFormatBoxes.DATA_REFERENCE_BOX:
                            readDataReferenceBox(length);
                            break;

                        case FileFormatBoxes.FRAGMENT_TABLE_BOX:
                            readFragmentTableBox(length);
                            break;

                        case FileFormatBoxes.CROSS_REFERENCE_BOX:
                            readCrossReferenceBox(length);
                            break;

                        case FileFormatBoxes.CODESTREAM_HEADER_BOX:
                            readCodestreamHeaderBox(length);
                            break;

                        case FileFormatBoxes.COMPOSITING_LAYER_HEADER_BOX:
                            readCompositingLayerHeaderBox(length);
                            break;

                        default:
                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                                $"Unknown box-type: 0x{Convert.ToString(box, 16)}");
                            break;

                    }
                    if (!lastBoxFound)
                        inStream.seek(pos + length);
                }
            }
            catch (System.IO.EndOfStreamException)
            {
                throw new InvalidOperationException("EOF reached before finding Contiguous " + "Codestream Box");
            }

            if (codeStreamPos.Count == 0)
            {
                // Not a valid JP2 file or codestream
                throw new InvalidOperationException("Invalid JP2 file: Contiguous codestream box " + "missing");
            }

            // Validate codestream markers
            try
            {
                // Read codestream data for validation
                var savedPos = inStream.Pos;
                inStream.seek(codeStreamPos[0]);
                
                // Determine how many bytes to validate
                var bytesToValidate = MaxCodestreamValidationBytes > 0 
                    ? Math.Min(codeStreamLength[0], MaxCodestreamValidationBytes)
                    : codeStreamLength[0];
                
                var codestreamSample = new byte[bytesToValidate];
                inStream.readFully(codestreamSample, 0, bytesToValidate);
                
                if (ComprehensiveCodestreamValidation)
                {
                    // Comprehensive validation (slower, more thorough)
                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                        $"Performing comprehensive codestream validation ({bytesToValidate} bytes)...");
                    Validator.ValidateCodestreamComprehensive(codestreamSample, bytesToValidate);
                }
                else
                {
                    // Basic validation (fast, checks main markers only)
                    Validator.ValidateBasicCodestreamMarkers(codestreamSample);
                }
                
                inStream.seek(savedPos);
            }
            catch (Exception e)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Could not validate codestream markers: {e.Message}");
            }

            // Perform validation
            Validator.ValidateFileFormat(FileStructure);

            // Log validation results
            if (Validator.HasErrors)
            {
                var report = Validator.GetValidationReport();
                if (StrictValidation)
                {
                    throw new InvalidOperationException($"JP2 validation failed:\n{report}");
                }
                else
                {
                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                        $"JP2 file has validation errors but continuing in non-strict mode:\n{report}");
                }
            }
            else if (Validator.HasWarnings)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                    Validator.GetValidationReport());
            }

            return;
        }

        /// <summary> This method reads the File Type box.
        /// 
        /// </summary>
        /// <returns> false if the File Type box was not found or invalid else true
        /// 
        /// </returns>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// </exception>
        /// <exception cref="java.io.EOFException">If the end of file was reached
        /// 
        /// </exception>
        public virtual bool readFileTypeBox()
        {
            int length;
            long longLength = 0;
            int pos;
            int nComp;
            var foundComp = false;

            // Get current position in file
            pos = inStream.Pos;

            // Read box length (LBox)
            length = inStream.readInt();
            if (length == 0)
            {
                // This can not be last box
                throw new InvalidOperationException("Zero-length of Profile Box");
            }

            // Check that this is a File Type box (TBox)
            if (inStream.readInt() != FileFormatBoxes.FILE_TYPE_BOX)
            {
                return false;
            }

            // Record File Type box information
            FileStructure.HasFileTypeBox = true;
            FileStructure.FileTypeBoxPosition = pos;
            FileStructure.FileTypeBoxLength = length;

            // Check for XLBox
            if (length == 1)
            {
                // Box has 8 byte length;
                longLength = inStream.readLong();
                
                // Detect extended length
                Validator.DetectExtendedLength(length, longLength);
                
                throw new System.IO.IOException("File too long.");
            }

            // Read Brand field
            var brand = inStream.readInt();
            FileStructure.HasValidBrand = (brand == FileFormatBoxes.FT_BR);

            // Read MinV field
            FileStructure.MinorVersion = inStream.readInt();

            // Check that there is at least one FT_BR entry in
            // compatibility list
            nComp = (length - 16) / 4; // Number of compatibilities.
            
            // Store compatibility list for validation
            var compatList = new int[nComp];
            for (var i = 0; i < nComp; i++)
            {
                compatList[i] = inStream.readInt();
                if (compatList[i] == FileFormatBoxes.FT_BR)
                {
                    foundComp = true;
                    FileStructure.HasJP2Compatibility = true;
                }
            }
            
            // Validate compatibility list
            Validator.ValidateCompatibilityList(compatList, true);
            
            return foundComp;
        }

        /// <summary> This method reads the JP2Header box
        /// 
        /// </summary>
        /// <param name="pos">The position in the file
        /// 
        /// </param>
        /// <param name="length">The length of the JP2Header box
        /// 
        /// </param>
        /// <param name="long">length The length of the JP2Header box if greater than
        /// 1<<32
        /// 
        /// </param>
        /// <param name="jp2HeaderBoxOrder">Reference parameter to track the order of boxes within JP2 Header
        /// 
        /// </param>
        /// <returns> false if the JP2Header box was not found or invalid else true
        /// 
        /// </returns>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        /// <exception cref="java.io.EOFException">If the end of file was reached
        /// 
        /// </exception>
        public virtual bool readJP2HeaderBox(long pos, int length, long longLength, ref int jp2HeaderBoxOrder)
        {

            if (length == 0)
            {
                // This can not be last box
                throw new InvalidOperationException("Zero-length of JP2Header Box");
            }

            // Read sub-boxes within JP2 Header to extract ICC profile, resolution, channel definitions, palette, and component mapping
            try
            {
                var boxHeader = new byte[16];
                var headerBoxEnd = pos + length;
                var currentPos = (int)(pos + 8); // Skip JP2 Header box header

                while (currentPos < headerBoxEnd)
                {
                    inStream.seek(currentPos);
                    inStream.readFully(boxHeader, 0, 8);

                    var boxLen = (boxHeader[0] << 24) | (boxHeader[1] << 16) | 
                                 (boxHeader[2] << 8) | boxHeader[3];
                    var boxType = (boxHeader[4] << 24) | (boxHeader[5] << 16) | 
                                  (boxHeader[6] << 8) | boxHeader[7];

                    // Track box types for validation
                    if (boxType == FileFormatBoxes.IMAGE_HEADER_BOX)
                    {
                        FileStructure.HasImageHeaderBox = true;
                        FileStructure.ImageHeaderBoxOrder = jp2HeaderBoxOrder++;

                        // Read BPC field from Image Header Box to check if Bits Per Component box is needed
                        // Skip HEIGHT(4), WIDTH(4), NC(2) to get to BPC(1)
                        inStream.seek(currentPos + 8 + 10); // Skip box header + HEIGHT + WIDTH + NC
                        FileStructure.ImageHeaderBPCValue = inStream.readByte();
                    }
                    else if (boxType == FileFormatBoxes.COLOUR_SPECIFICATION_BOX)
                    {
                        FileStructure.HasColourSpecificationBox = true;
                        FileStructure.ColourSpecificationBoxOrder = jp2HeaderBoxOrder++;

                        // Read color specification box contents
                        var csBoxData = new byte[boxLen - 8];
                        inStream.readFully(csBoxData, 0, boxLen - 8);

                        // Check method field (METH)
                        var method = csBoxData[0];

                        // Method 2 = ICC profile
                        if (method == 2)
                        {
                            // ICC profile starts at offset 3 in box data
                            // First read the profile size
                            var profileSize = (csBoxData[3] << 24) | (csBoxData[4] << 16) | 
                                            (csBoxData[5] << 8) | csBoxData[6];

                            if (profileSize > 0 && profileSize < csBoxData.Length - 3)
                            {
                                var iccProfile = new byte[profileSize];
                                Array.Copy(csBoxData, 3, iccProfile, 0, profileSize);
                                
                                // Validate ICC profile header
                                if (Validator.ValidateIccProfileBasic(iccProfile))
                                {
                                    // Add to metadata
                                    Metadata.SetIccProfile(iccProfile);

                                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                                        $"Found ICC Profile ({profileSize} bytes)");
                                }
                                else
                                {
                                    // Still add even if validation failed, but user is warned
                                    Metadata.SetIccProfile(iccProfile);
                                    
                                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                                        $"ICC Profile validation warnings detected, but profile was loaded ({profileSize} bytes)");
                                }
                            }
                        }
                    }
                    else if (boxType == FileFormatBoxes.BITS_PER_COMPONENT_BOX)
                    {
                        FileStructure.HasBitsPerComponentBox = true;
                        FileStructure.BitsPerComponentBoxOrder = jp2HeaderBoxOrder++;

                        // Read the Bits Per Component box data
                        // Format: BPC[i] (1 byte each) for i = 0 to NC-1
                        // Each byte: bits 0-6 = bit depth minus 1, bit 7 = sign bit
                        var bpcLength = boxLen - 8; // Box length minus header
                        if (bpcLength > 0)
                        {
                            var bpcBytes = new byte[bpcLength];
                            inStream.readFully(bpcBytes, 0, bpcLength);

                            // Store in metadata
                            Metadata.BitsPerComponent = new BitsPerComponentData
                            {
                                ComponentBitDepths = bpcBytes
                            };

                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                                $"Found Bits Per Component Box: {bpcLength} components");
                        }
                    }
                    // Check for Resolution Box (superbox containing capture and/or display resolution)
                    else if (boxType == FileFormatBoxes.RESOLUTION_BOX)
                    {
                        FileStructure.HasResolutionBox = true;
                        FileStructure.ResolutionBoxOrder = jp2HeaderBoxOrder++;

                        // Resolution box is a superbox containing resc and/or resd boxes
                        var resBoxStart = currentPos + 8;
                        var resBoxEnd = currentPos + boxLen;
                        
                        readResolutionSuperBox(resBoxStart, resBoxEnd);
                    }
                    // Check for Channel Definition Box
                    else if (boxType == FileFormatBoxes.CHANNEL_DEFINITION_BOX)
                    {
                        FileStructure.HasChannelDefinitionBox = true;
                        FileStructure.ChannelDefinitionBoxOrder = jp2HeaderBoxOrder++;

                        readChannelDefinitionBox(boxLen);
                    }
                    // Check for Palette Box
                    else if (boxType == FileFormatBoxes.PALETTE_BOX)
                    {
                        FileStructure.HasPaletteBox = true;
                        FileStructure.PaletteBoxOrder = jp2HeaderBoxOrder++;

                        readPaletteBox(boxLen);
                    }
                    // Check for Component Mapping Box
                    else if (boxType == FileFormatBoxes.COMPONENT_MAPPING_BOX)
                    {
                        FileStructure.HasComponentMappingBox = true;
                        FileStructure.ComponentMappingBoxOrder = jp2HeaderBoxOrder++;

                        readComponentMappingBox(boxLen);
                    }

                    // Move to next box
                    currentPos += boxLen;
                    if (boxLen == 0) break; // Avoid infinite loop
                }
            }
            catch (Exception e)
            {
                // Don't fail the whole operation if we can't read metadata
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Error extracting metadata from JP2 Header: {e.Message}");
            }

            return true;
        }

        /// <summary>
        /// Reads the Resolution superbox which can contain capture and/or display resolution boxes.
        /// </summary>
        /// <param name="boxStart">Start position of resolution superbox content.</param>
        /// <param name="boxEnd">End position of resolution superbox.</param>
        private void readResolutionSuperBox(int boxStart, int boxEnd)
        {
            var boxHeader = new byte[8];
            var currentPos = boxStart;

            while (currentPos < boxEnd)
            {
                inStream.seek(currentPos);
                inStream.readFully(boxHeader, 0, 8);

                var boxLen = (boxHeader[0] << 24) | (boxHeader[1] << 16) | 
                             (boxHeader[2] << 8) | boxHeader[3];
                var boxType = (boxHeader[4] << 24) | (boxHeader[5] << 16) | 
                              (boxHeader[6] << 8) | boxHeader[7];

                if (boxType == FileFormatBoxes.CAPTURE_RESOLUTION_BOX)
                {
                    // Read capture resolution (resc)
                    var resData = new byte[10]; // VR_N(2), VR_D(2), HR_N(2), HR_D(2), VR_E(1), HR_E(1)
                    inStream.readFully(resData, 0, 10);

                    var vr_n = (short)((resData[0] << 8) | resData[1]);
                    var vr_d = (short)((resData[2] << 8) | resData[3]);
                    var hr_n = (short)((resData[4] << 8) | resData[5]);
                    var hr_d = (short)((resData[6] << 8) | resData[7]);
                    var vr_e = (sbyte)resData[8];
                    var hr_e = (sbyte)resData[9];

                    // Calculate resolution: (numerator / denominator) * 10^exponent
                    var verticalRes = (vr_n / (double)vr_d) * Math.Pow(10, vr_e);
                    var horizontalRes = (hr_n / (double)hr_d) * Math.Pow(10, hr_e);

                    if (Metadata.Resolution == null)
                        Metadata.Resolution = new ResolutionData();

                    Metadata.Resolution.SetCaptureResolution(horizontalRes, verticalRes);

                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                        $"Found Capture Resolution: {horizontalRes:F2}x{verticalRes:F2} pixels/meter " +
                        $"({horizontalRes / 39.3701:F2}x{verticalRes / 39.3701:F2} DPI)");
                }
                else if (boxType == FileFormatBoxes.DEFAULT_DISPLAY_RESOLUTION_BOX)
                {
                    // Read display resolution (resd)
                    var resData = new byte[10];
                    inStream.readFully(resData, 0, 10);

                    var vr_n = (short)((resData[0] << 8) | resData[1]);
                    var vr_d = (short)((resData[2] << 8) | resData[3]);
                    var hr_n = (short)((resData[4] << 8) | resData[5]);
                    var hr_d = (short)((resData[6] << 8) | resData[7]);
                    var vr_e = (sbyte)resData[8];
                    var hr_e = (sbyte)resData[9];

                    var verticalRes = (vr_n / (double)vr_d) * Math.Pow(10, vr_e);
                    var horizontalRes = (hr_n / (double)hr_d) * Math.Pow(10, hr_e);

                    if (Metadata.Resolution == null)
                        Metadata.Resolution = new ResolutionData();

                    Metadata.Resolution.SetDisplayResolution(horizontalRes, verticalRes);

                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                        $"Found Display Resolution: {horizontalRes:F2}x{verticalRes:F2} pixels/meter " +
                        $"({horizontalRes / 39.3701:F2}x{verticalRes / 39.3701:F2} DPI)");
                }

                currentPos += boxLen;
                if (boxLen == 0) break;
            }
        }

        /// <summary>
        /// Reads the Channel Definition Box which defines the interpretation of image components.
        /// </summary>
        /// <param name="boxLength">Total length of the box including header.</param>
        private void readChannelDefinitionBox(int boxLength)
        {
            try
            {
                // Read number of channel definitions (N)
                var nDef = inStream.readShort();
                
                if (nDef <= 0)
                    return;

                if (Metadata.ChannelDefinitions == null)
                    Metadata.ChannelDefinitions = new ChannelDefinitionData();

                // Read each channel definition (6 bytes each: Cn(2), Typ(2), Asoc(2))
                for (int i = 0; i < nDef; i++)
                {
                    var cn = inStream.readShort();    // Channel index
                    var typ = inStream.readShort();   // Channel type
                    var asoc = inStream.readShort() & 0xFFFF;  // Association (treat as unsigned)

                    // Convert typ to ChannelType enum
                    var channelType = ChannelType.Unspecified;
                    switch (typ)
                    {
                        case 0:
                            channelType = ChannelType.Color;
                            break;
                        case 1:
                            channelType = ChannelType.Opacity;
                            break;
                        case 2:
                            channelType = ChannelType.PremultipliedOpacity;
                            break;
                        default:
                            channelType = ChannelType.Unspecified;
                            break;
                    }

                    Metadata.ChannelDefinitions.AddChannel(cn, channelType, asoc);
                }

                var alphaInfo = Metadata.ChannelDefinitions.HasAlphaChannel ? " (with alpha)" : "";
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                    $"Found Channel Definition Box: {nDef} channels{alphaInfo}");
            }
            catch (Exception e)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Error reading Channel Definition Box: {e.Message}");
            }
        }

        /// <summary> This method skips the Contiguous codestream box and adds position
        /// of contiguous codestream to a vector
        /// 
        /// </summary>
        /// <param name="pos">The position in the file
        /// 
        /// </param>
        /// <param name="length">The length of the JP2Header box
        /// 
        /// </param>
        /// <param name="long">length The length of the JP2Header box if greater than 1<<32
        /// 
        /// </param>
        /// <returns> false if the Contiguous codestream box was not found or invalid
        /// else true
        /// 
        /// </returns>
        /// <exception cref="java.io.IOException">If an I/O error ocurred.
        /// 
        /// </exception>
        /// <exception cref="java.io.EOFException">If the end of file was reached
        /// 
        /// </exception>
        public virtual bool readContiguousCodeStreamBox(long pos, int length, long longLength)
        {

            // Add new codestream position to position vector
            var ccpos = inStream.Pos;

            if (codeStreamPos == null)
                codeStreamPos = new List<int>(10);
            codeStreamPos.Add(ccpos);

            // Add new codestream length to length vector. 'length' is the total box
            // length including the header, while ccpos points past the header, so
            // subtract the header size to store the actual codestream byte count —
            // otherwise consumers reading 'length' bytes from ccpos overrun the box
            // (and the file, when jp2c is the last box).
            var payloadLength = length - (int)(ccpos - pos);
            if (payloadLength < 0)
            {
                throw new InvalidOperationException(
                    "Invalid JP2 file: Contiguous Codestream box length is smaller than its own header");
            }

            if (codeStreamLength == null)
                codeStreamLength = new List<int>(10);
            codeStreamLength.Add(payloadLength);

            return true;
        }

        /// <summary>
        /// Reads the contents of the JP2 Intellectual Property box ('jp2i') per ISO/IEC 15444-1 §I.7.3.
        /// The contents of this box are unconstrained by the standard, so the payload is captured as
        /// raw bytes plus a best-effort UTF-8 text decoding for convenience.
        /// </summary>
        /// <param name="length">Total box length including the 8-byte header.</param>
        public virtual void readIntPropertyBox(int length)
        {
            if (length <= 8) return; // Empty box

            try
            {
                var dataLength = length - 8;
                var data = new byte[dataLength];
                inStream.readFully(data, 0, dataLength);

                string? text = null;
                try { text = Encoding.UTF8.GetString(data); } catch { /* leave as binary */ }

                Metadata.IntellectualPropertyBoxes.Add(new IntellectualPropertyBox
                {
                    RawData = data,
                    Text = text
                });

                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                    $"Found JP2 Intellectual Property (jp2i) box ({dataLength} bytes)");
            }
            catch (Exception e)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Error reading JP2 Intellectual Property (jp2i) box: {e.Message}");
            }
        }

        /// <summary> This method reads the contents of the XML box
        /// 
        /// </summary>
        public virtual void readXMLBox(int length)
        {
            if (length <= 8) return; // Box too small

            try
            {
                // Read the XML content (length includes 8-byte box header)
                var dataLength = length - 8;
                var xmlBytes = new byte[dataLength];
                inStream.readFully(xmlBytes, 0, dataLength);

                // Convert to string (XML boxes are UTF-8)
                var xmlContent = Encoding.UTF8.GetString(xmlBytes);

                // Add to metadata
                Metadata.XmlBoxes.Add(new XmlBox { XmlContent = xmlContent });

                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                    $"Found XML box ({dataLength} bytes)");
            }
            catch (Exception e)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Error reading XML box: {e.Message}");
            }
        }

        /// <summary> This method reads the contents of the UUID box
        /// 
        /// </summary>
        public virtual void readUUIDBox(int length)
        {
            if (length <= 24) return; // Box too small (8 header + 16 UUID)

            try
            {
                // Read UUID (16 bytes)
                var uuidBytes = new byte[16];
                inStream.readFully(uuidBytes, 0, 16);
                var uuid = new Guid(uuidBytes);

                // Read data (remaining bytes)
                var dataLength = length - 24; // Box header (8) + UUID (16)
                var data = new byte[dataLength];
                inStream.readFully(data, 0, dataLength);

                // Add to metadata
                Metadata.UuidBoxes.Add(new UuidBox { Uuid = uuid, Data = data });

                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                    $"Found UUID box: {uuid} ({dataLength} bytes)");
            }
            catch (Exception e)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Error reading UUID box: {e.Message}");
            }
        }

        /// <summary> This method reads the contents of the UUID Info box
        /// 
        /// </summary>
        public virtual void readUUIDInfoBox(int length)
        {
            if (length <= 8) return; // Box too small

            try
            {
                var uuidInfo = new UuidInfoBox();
                var startPos = inStream.Pos;
                var endPos = startPos + length - 8; // Exclude box header

                // UUID Info is a superbox containing UUID List and optionally URL box
                while (inStream.Pos < endPos)
                {
                    var boxHeader = new byte[8];
                    inStream.readFully(boxHeader, 0, 8);

                    var boxLen = (boxHeader[0] << 24) | (boxHeader[1] << 16) |
                                 (boxHeader[2] << 8) | boxHeader[3];
                    var boxType = (boxHeader[4] << 24) | (boxHeader[5] << 16) |
                                  (boxHeader[6] << 8) | boxHeader[7];

                    if (boxType == FileFormatBoxes.UUID_LIST_BOX)
                    {
                        // Read UUID List box
                        // Format: NU(2) + UUID1(16) + UUID2(16) + ...
                        var numUuids = inStream.readShort();
                        
                        for (int i = 0; i < numUuids; i++)
                        {
                            var uuidBytes = new byte[16];
                            inStream.readFully(uuidBytes, 0, 16);
                            uuidInfo.UuidList.Add(new Guid(uuidBytes));
                        }

                        FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                            $"Found UUID List: {numUuids} UUID(s)");
                    }
                    else if (boxType == FileFormatBoxes.URL_BOX)
                    {
                        // Read URL box
                        // Format: VERS(1) + FLAG(3) + URL(variable length string)
                        uuidInfo.UrlVersion = inStream.readByte();
                        
                        // Read flags (3 bytes)
                        inStream.readByte(); // FLAG byte 0
                        inStream.readByte(); // FLAG byte 1
                        uuidInfo.UrlFlags = inStream.readByte(); // FLAG byte 2

                        // Read URL (remaining bytes in box)
                        var urlLength = boxLen - 12; // Box header (8) + VERS(1) + FLAG(3)
                        if (urlLength > 0)
                        {
                            var urlBytes = new byte[urlLength];
                            inStream.readFully(urlBytes, 0, urlLength);
                            uuidInfo.Url = Encoding.UTF8.GetString(urlBytes).TrimEnd('\0');

                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                                $"Found URL: {uuidInfo.Url}");
                        }
                    }
                    else
                    {
                        // Skip unknown box type
                        var skipBytes = boxLen - 8;
                        if (skipBytes > 0)
                        {
                            inStream.seek(inStream.Pos + skipBytes);
                        }
                    }
                }

                Metadata.UuidInfo = uuidInfo;
            }
            catch (Exception e)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Error reading UUID Info box: {e.Message}");
            }
        }

        /// <summary> This method reads the contents of the Reader requirements box
        /// 
        /// </summary>
        public virtual void readReaderRequirementsBox(int length)
        {
            if (length <= 8) return; // Box too small

            try
            {
                var readerReq = new ReaderRequirementsBox();

                // Read ML (mask length) - 1 byte
                var maskLength = inStream.readByte();

                // Read FUAM (fully understand aspects mask) - maskLength bytes
                var fuam = new byte[maskLength];
                inStream.readFully(fuam, 0, maskLength);

                // Read DCM (decode completely mask) - maskLength bytes
                var dcm = new byte[maskLength];
                inStream.readFully(dcm, 0, maskLength);

                // Read NSF (number of standard features) - 2 bytes
                var numStdFeatures = inStream.readShort();

                // Read standard features: each entry is SF(2) + SM(maskLength bytes)
                for (int i = 0; i < numStdFeatures; i++)
                {
                    var featureId = (ushort)inStream.readShort();
                    readerReq.StandardFeatures.Add(featureId);
                    // Consume SM (feature mask) — maskLength bytes, not stored
                    for (int b = 0; b < maskLength; b++) inStream.readByte();
                }

                // Read NVF (number of vendor features) - 2 bytes
                var numVendorFeatures = inStream.readShort();

                // Read vendor features: each entry is VF(16) + VM(maskLength bytes)
                for (int i = 0; i < numVendorFeatures; i++)
                {
                    var uuidBytes = new byte[16];
                    inStream.readFully(uuidBytes, 0, 16);
                    readerReq.VendorFeatures.Add(new Guid(uuidBytes));
                    // Consume VM (vendor feature mask) — maskLength bytes, not stored
                    for (int b = 0; b < maskLength; b++) inStream.readByte();
                }

                // Check if JP2 compatible (bit 5 of first FUAM byte should be 0 for baseline JP2)
                readerReq.IsJp2Compatible = (maskLength > 0) && ((fuam[0] & 0x20) == 0);

                Metadata.ReaderRequirements = readerReq;

                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                    $"Found Reader Requirements: {numStdFeatures} standard feature(s), {numVendorFeatures} vendor feature(s)");
            }
            catch (Exception e)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Error reading Reader Requirements box: {e.Message}");
            }
        }

        /// <summary>
        /// Reads the Intellectual Property Rights (JPR) box from JPEG 2000 Part 2.
        /// The JPR box contains copyright or intellectual property rights information as UTF-8 text.
        /// </summary>
        /// <param name="length">The total length of the box including header.</param>
        public virtual void readJPRBox(int length)
        {
            if (length <= 8) return; // Box too small

            try
            {
                // Read the JPR content (length includes 8-byte box header)
                var dataLength = length - 8;
                var jprBytes = new byte[dataLength];
                inStream.readFully(jprBytes, 0, dataLength);

                // Try to decode as UTF-8 text
                try
                {
                    var jprText = Encoding.UTF8.GetString(jprBytes);
                    
                    // Add to metadata with text
                    Metadata.IntellectualPropertyRights.Add(new JprBox { Text = jprText });

                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                        $"Found Intellectual Property Rights (JPR) box ({dataLength} bytes): {jprText.Substring(0, Math.Min(50, jprText.Length))}...");
                }
                catch
                {
                    // If not valid UTF-8, store as binary
                    Metadata.IntellectualPropertyRights.Add(new JprBox { RawData = jprBytes });

                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                        $"Found Intellectual Property Rights (JPR) box ({dataLength} bytes, binary data)");
                }
            }
            catch (Exception e)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Error reading Intellectual Property Rights (JPR) box: {e.Message}");
            }
        }

        /// <summary>
        /// Reads the Label (LBL) box from JPEG 2000 Part 2.
        /// The Label box contains human-readable text labels as UTF-8 text.
        /// </summary>
        /// <param name="length">The total length of the box including header.</param>
        public virtual void readLabelBox(int length)
        {
            if (length <= 8) return; // Box too small

            try
            {
                // Read the Label content (length includes 8-byte box header)
                var dataLength = length - 8;
                var labelBytes = new byte[dataLength];
                inStream.readFully(labelBytes, 0, dataLength);

                // Try to decode as UTF-8 text
                try
                {
                    var labelText = Encoding.UTF8.GetString(labelBytes);
                    
                    // Add to metadata with text
                    Metadata.Labels.Add(new LabelBox { Label = labelText });

                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                        $"Found Label (LBL) box ({dataLength} bytes): {labelText.Substring(0, Math.Min(50, labelText.Length))}...");
                }
                catch
                {
                    // If not valid UTF-8, store as binary
                    Metadata.Labels.Add(new LabelBox { RawData = labelBytes });

                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                        $"Found Label (LBL) box ({dataLength} bytes, binary data)");
                }
            }
            catch (Exception e)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Error reading Label (LBL) box: {e.Message}");
            }
        }

        /// <summary>
        /// Reads the payload (DBox) of the box currently positioned at the start of its content.
        /// Returns an empty array for boxes that carry no payload.
        /// </summary>
        /// <param name="length">Total box length including the 8-byte header (LBox value).</param>
        private byte[] ReadBoxPayload(int length)
        {
            if (length <= 8)
                return Array.Empty<byte>();

            var dataLength = length - 8;
            var payload = new byte[dataLength];
            inStream.readFully(payload, 0, dataLength);
            return payload;
        }

        /// <summary>
        /// Reads an Association ('asoc') box (JPEG 2000 Part 2 / JPX) and adds it to the metadata.
        /// </summary>
        /// <param name="length">The total length of the box including header.</param>
        public virtual void readAssociationBox(int length)
        {
            try
            {
                var asoc = AssociationBox.Parse(ReadBoxPayload(length));
                Metadata.Associations.Add(asoc);

                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                    $"Found Association (asoc) box: {asoc.Children.Count} child box(es)" +
                    (asoc.NumberList != null ? $", {asoc.NumberList.Entries.Count} entity ref(s)" : ""));
            }
            catch (Exception e)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Error reading Association (asoc) box: {e.Message}");
            }
        }

        /// <summary>
        /// Reads a Data Reference ('dtbl') box (JPEG 2000 Part 2 / JPX) and adds it to the metadata.
        /// </summary>
        /// <param name="length">The total length of the box including header.</param>
        public virtual void readDataReferenceBox(int length)
        {
            try
            {
                Metadata.DataReference = DataReferenceBox.Parse(ReadBoxPayload(length));

                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                    $"Found Data Reference (dtbl) box: {Metadata.DataReference.Entries.Count} URL(s)");
            }
            catch (Exception e)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Error reading Data Reference (dtbl) box: {e.Message}");
            }
        }

        /// <summary>
        /// Reads a Fragment Table ('ftbl') box (JPEG 2000 Part 2 / JPX) and adds it to the metadata.
        /// </summary>
        /// <param name="length">The total length of the box including header.</param>
        public virtual void readFragmentTableBox(int length)
        {
            try
            {
                var ftbl = FragmentTableBox.Parse(ReadBoxPayload(length));
                Metadata.FragmentTables.Add(ftbl);

                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                    $"Found Fragment Table (ftbl) box: {ftbl.FragmentList?.Fragments.Count ?? 0} fragment(s)");
            }
            catch (Exception e)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Error reading Fragment Table (ftbl) box: {e.Message}");
            }
        }

        /// <summary>
        /// Reads a Cross Reference ('cref') box (JPEG 2000 Part 2 / JPX) and adds it to the metadata.
        /// </summary>
        /// <param name="length">The total length of the box including header.</param>
        public virtual void readCrossReferenceBox(int length)
        {
            try
            {
                var cref = CrossReferenceBox.Parse(ReadBoxPayload(length));
                Metadata.CrossReferences.Add(cref);

                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                    $"Found Cross Reference (cref) box: {cref.FragmentList?.Fragments.Count ?? 0} fragment(s)");
            }
            catch (Exception e)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Error reading Cross Reference (cref) box: {e.Message}");
            }
        }

        /// <summary>
        /// Reads a Codestream Header ('jpch') box (JPEG 2000 Part 2 / JPX) and adds it to the metadata.
        /// </summary>
        /// <param name="length">The total length of the box including header.</param>
        public virtual void readCodestreamHeaderBox(int length)
        {
            try
            {
                var jpch = CodestreamHeaderBox.Parse(ReadBoxPayload(length));
                Metadata.CodestreamHeaders.Add(jpch);

                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                    $"Found Codestream Header (jpch) box: {jpch.Children.Count} child box(es)");
            }
            catch (Exception e)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Error reading Codestream Header (jpch) box: {e.Message}");
            }
        }

        /// <summary>
        /// Reads a Compositing Layer Header ('jplh') box (JPEG 2000 Part 2 / JPX) and adds it to the metadata.
        /// </summary>
        /// <param name="length">The total length of the box including header.</param>
        public virtual void readCompositingLayerHeaderBox(int length)
        {
            try
            {
                var jplh = CompositingLayerHeaderBox.Parse(ReadBoxPayload(length));
                Metadata.CompositingLayerHeaders.Add(jplh);

                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                    $"Found Compositing Layer Header (jplh) box: {jplh.Children.Count} child box(es)");
            }
            catch (Exception e)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Error reading Compositing Layer Header (jplh) box: {e.Message}");
            }
        }

        /// <summary>
        /// Reads the Palette Box (pclr) from JP2 Header.
        /// The Palette box defines a lookup table for indexed color images.
        /// Format: NE(2) + NPC(1) + B[NPC](1) + C[NE][NPC](1 or 2 each)
        /// </summary>
        /// <param name="boxLength">Total length of the box including header.</param>
        private void readPaletteBox(int boxLength)
        {
            if (boxLength <= 11) return; // Box too small (8 header + 3 minimum data)

            try
            {
                // Read NE (number of entries) - 2 bytes
                var numEntries = inStream.readShort() & 0xFFFF;
                
                // Read NPC (number of palette columns) - 1 byte
                var numColumns = inStream.readByte() & 0xFF;

                if (numEntries == 0 || numColumns == 0)
                {
                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                        "Invalid palette: zero entries or columns");
                    return;
                }

                // Read bit depths for each column (B array)
                var bitDepths = new short[numColumns];
                for (int i = 0; i < numColumns; i++)
                {
                    bitDepths[i] = (short)(inStream.readByte() & 0xFF);
                }

                // Read palette entries
                var entries = new int[numEntries][];
                for (int entryIdx = 0; entryIdx < numEntries; entryIdx++)
                {
                    entries[entryIdx] = new int[numColumns];
                    
                    for (int colIdx = 0; colIdx < numColumns; colIdx++)
                    {
                        var bitDepth = (bitDepths[colIdx] & 0x7F) + 1; // Bits 0-6 are depth minus 1
                        var IsSigned = (bitDepths[colIdx] & 0x80) != 0; // Bit 7 is sign flag
                        
                        int value;
                        if (bitDepth <= 8)
                        {
                            // 8-bit entry
                            value = inStream.readByte() & 0xFF;
                        }
                        else if (bitDepth <= 16)
                        {
                            // 16-bit entry
                            value = inStream.readShort() & 0xFFFF;
                        }
                        else
                        {
                            throw new InvalidOperationException($"Palette bit depth > 16 not supported: {bitDepth}");
                        }

                        // Handle sign extension for signed values
                        if (IsSigned && (value & (1 << (bitDepth - 1))) != 0)
                        {
                            // Sign bit is set, extend sign
                            var mask = unchecked((int)(0xFFFFFFFF << bitDepth));
                            entries[entryIdx][colIdx] = mask | value;
                        }
                        else
                        {
                            // Unsigned or positive signed value
                            var mask = (1 << bitDepth) - 1;
                            entries[entryIdx][colIdx] = mask & value;
                        }
                    }
                }

                // Store in metadata
                Metadata.SetPalette(numEntries, numColumns, bitDepths, entries);

                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                    $"Found Palette Box: {numEntries} entries, {numColumns} columns");
            }
            catch (Exception e)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Error reading Palette Box: {e.Message}");
            }
        }

        /// <summary>
        /// Reads the Component Mapping Box (cmap) from JP2 Header.
        /// The Component Mapping box defines how codestream components map to output channels.
        /// Format: Array of {CMP(2) + MTYP(1) + PCOL(1)} entries
        /// </summary>
        /// <param name="boxLength">Total length of the box including header.</param>
        private void readComponentMappingBox(int boxLength)
        {
            if (boxLength <= 8) return; // Box too small

            try
            {
                var dataLength = boxLength - 8;
                
                // Each mapping entry is 4 bytes: CMP (component index) - 2 bytes
                // MTYP (mapping type) - 1 byte, PCOL (palette column) - 1 byte
                if (dataLength % 4 != 0)
                {
                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                        $"Invalid Component Mapping Box: length {dataLength} is not multiple of 4");
                    return;
                }

                var numChannels = dataLength / 4;

                // Create component mapping data
                var componentMapping = new ComponentMappingData();

                for (int i = 0; i < numChannels; i++)
                {
                    // Read CMP (component index) - 2 bytes
                    var componentIndex = (ushort)(inStream.readShort() & 0xFFFF);
                    
                    // Read MTYP (mapping type) - 1 byte
                    // 0 = direct use, 1 = palette mapping
                    var mappingType = (byte)(inStream.readByte() & 0xFF);
                    
                    // Read PCOL (palette column) - 1 byte
                    var paletteColumn = (byte)(inStream.readByte() & 0xFF);

                    componentMapping.AddMapping(componentIndex, mappingType, paletteColumn);
                }

                // Store in metadata
                Metadata.ComponentMapping = componentMapping;

                var paletteInfo = componentMapping.UsesPalette ? " (uses palette)" : " (direct mapping)";
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                    $"Found Component Mapping Box: {numChannels} channels{paletteInfo}");
            }
            catch (Exception e)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Error reading Component Mapping Box: {e.Message}");
            }
        }
    }
}