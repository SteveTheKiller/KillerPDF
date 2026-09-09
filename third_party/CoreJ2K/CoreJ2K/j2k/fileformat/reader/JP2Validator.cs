// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using CoreJ2K.j2k.util;

namespace CoreJ2K.j2k.fileformat.reader
{
    /// <summary>
    /// Provides comprehensive validation for JPEG 2000 JP2 file format per ISO/IEC 15444-1.
    /// Validates box ordering, required boxes, proper structure, and performs enhanced compliance checks.
    /// </summary>
    public class JP2Validator
    {
        private readonly List<string> errors = new List<string>();
        private readonly List<string> warnings = new List<string>();

        /// <summary>
        /// Gets validation errors found during JP2 file format validation.
        /// </summary>
        public IReadOnlyList<string> Errors => errors.AsReadOnly();

        /// <summary>
        /// Gets validation warnings found during JP2 file format validation.
        /// </summary>
        public IReadOnlyList<string> Warnings => warnings.AsReadOnly();

        /// <summary>
        /// Returns true if any validation errors were found.
        /// </summary>
        public bool HasErrors => errors.Count > 0;

        /// <summary>
        /// Returns true if any validation warnings were found.
        /// </summary>
        public bool HasWarnings => warnings.Count > 0;

        /// <summary>
        /// Validates the JP2 file format structure.
        /// </summary>
        public void ValidateFileFormat(JP2Structure structure)
        {
            errors.Clear();
            warnings.Clear();

            // Validate signature box
            ValidateSignatureBox(structure);

            // Validate file type box
            ValidateFileTypeBox(structure);

            // Validate JP2 header box
            ValidateJP2HeaderBox(structure);

            // Validate codestream box
            ValidateCodestreamBox(structure);

            // Validate box ordering
            ValidateBoxOrdering(structure);

            // Run enhanced compliance checks
            RunEnhancedValidationChecks(structure);
        }

        /// <summary>
        /// Validates the JP2 Signature Box per ISO/IEC 15444-1 Section I.5.1.
        /// </summary>
        private void ValidateSignatureBox(JP2Structure structure)
        {
            if (!structure.HasSignatureBox)
            {
                errors.Add("JP2 Signature Box is missing (required per ISO/IEC 15444-1 Section I.5.1)");
                return;
            }

            if (structure.SignatureBoxPosition != 0)
            {
                errors.Add($"JP2 Signature Box must be first box in file (found at position {structure.SignatureBoxPosition})");
            }

            if (structure.SignatureBoxLength != 12)
            {
                errors.Add($"JP2 Signature Box must be exactly 12 bytes (found {structure.SignatureBoxLength} bytes)");
            }
        }

        /// <summary>
        /// Validates the File Type Box per ISO/IEC 15444-1 Section I.5.2.
        /// </summary>
        private void ValidateFileTypeBox(JP2Structure structure)
        {
            if (!structure.HasFileTypeBox)
            {
                errors.Add("File Type Box is missing (required per ISO/IEC 15444-1 Section I.5.2)");
                return;
            }

            // File Type box should immediately follow signature box (at position 12)
            if (structure.HasSignatureBox && structure.FileTypeBoxPosition != 12)
            {
                warnings.Add($"File Type Box should immediately follow JP2 Signature Box (found at position {structure.FileTypeBoxPosition}, expected at 12)");
            }

            if (!structure.HasValidBrand)
            {
                errors.Add("File Type Box must have 'jp2 ' (0x6a703220) as brand for JP2 compliance");
            }

            if (!structure.HasJP2Compatibility)
            {
                errors.Add("File Type Box compatibility list must include 'jp2 ' (0x6a703220)");
            }

            if (structure.FileTypeBoxLength < 20)
            {
                errors.Add($"File Type Box is too short: {structure.FileTypeBoxLength} bytes (minimum 20 bytes)");
            }
        }

        /// <summary>
        /// Validates the JP2 Header Box per ISO/IEC 15444-1 Section I.5.3.
        /// </summary>
        private void ValidateJP2HeaderBox(JP2Structure structure)
        {
            if (!structure.HasJP2HeaderBox)
            {
                errors.Add("JP2 Header Box is missing (required per ISO/IEC 15444-1 Section I.5.3)");
                return;
            }

            // Validate required sub-boxes
            if (!structure.HasImageHeaderBox)
            {
                errors.Add("Image Header Box is missing from JP2 Header (required per ISO/IEC 15444-1 Section I.5.3.1)");
            }
            else if (structure.ImageHeaderBoxOrder != 0)
            {
                errors.Add($"Image Header Box must be first box in JP2 Header (found at position {structure.ImageHeaderBoxOrder})");
            }

            if (!structure.HasColourSpecificationBox)
            {
                errors.Add("Colour Specification Box is missing from JP2 Header (required per ISO/IEC 15444-1 Section I.5.3.3)");
            }

            // Validate optional boxes ordering
            if (structure.HasPaletteBox && structure.HasComponentMappingBox)
            {
                if (structure.PaletteBoxOrder >= structure.ComponentMappingBoxOrder)
                {
                    errors.Add("Palette Box must appear before Component Mapping Box in JP2 Header");
                }
            }

            if (structure.HasComponentMappingBox && !structure.HasPaletteBox)
            {
                warnings.Add("Component Mapping Box present but no Palette Box found (unusual configuration)");
            }

            // Validate bits per component box when needed
            if (structure.ImageHeaderBPCValue == 0xFF && !structure.HasBitsPerComponentBox)
            {
                errors.Add("Bits Per Component Box is required when Image Header BPC field is 0xFF");
            }

            if (!structure.ImageHeaderBPCValue.HasValue || structure.ImageHeaderBPCValue == 0xFF)
            {
                if (!structure.HasBitsPerComponentBox)
                {
                    warnings.Add("Could not determine bit depth information (no Image Header or Bits Per Component Box)");
                }
            }
        }

        /// <summary>
        /// Validates the Contiguous Codestream Box per ISO/IEC 15444-1 Section I.5.4.
        /// </summary>
        private void ValidateCodestreamBox(JP2Structure structure)
        {
            if (!structure.HasContiguousCodestreamBox)
            {
                errors.Add("Contiguous Codestream Box is missing (required per ISO/IEC 15444-1 Section I.5.4)");
                return;
            }

            if (structure.ContiguousCodestreamBoxPosition < structure.JP2HeaderBoxPosition + structure.JP2HeaderBoxLength)
            {
                errors.Add("Contiguous Codestream Box must appear after JP2 Header Box");
            }
        }

        // Ordered box names matching the expected ISO/IEC 15444-1 sequence.  Kept as a
        // static readonly to avoid allocating a new List<string> on every decode call.
        private static readonly string[] ExpectedBoxOrder =
            { "Signature", "FileType", "JP2Header", "Codestream" };

        /// <summary>
        /// Validates the overall box ordering per ISO/IEC 15444-1.
        /// </summary>
        private void ValidateBoxOrdering(JP2Structure structure)
        {
            // Compare box positions directly instead of building and sorting a List<string>.
            // The canonical order is: Signature(0) < FileType < JP2Header < Codestream.
            // We validate each consecutive pair that is actually present.

            // Signature must be first (position 0)
            if (structure.HasSignatureBox && structure.SignatureBoxPosition != 0)
            {
                warnings.Add($"Unexpected box ordering: expected Signature at position 0, found it at byte offset {structure.SignatureBoxPosition}");
            }

            // FileType must come after Signature
            if (structure.HasSignatureBox && structure.HasFileTypeBox &&
                structure.FileTypeBoxPosition < structure.SignatureBoxPosition)
            {
                warnings.Add($"Unexpected box ordering: expected FileType after Signature");
            }

            // JP2Header must come after FileType (when both present)
            if (structure.HasFileTypeBox && structure.HasJP2HeaderBox &&
                structure.JP2HeaderBoxPosition < structure.FileTypeBoxPosition)
            {
                warnings.Add($"Unexpected box ordering: expected JP2Header after FileType");
            }

            // Codestream must come after JP2Header (when both present)
            if (structure.HasJP2HeaderBox && structure.HasContiguousCodestreamBox &&
                structure.ContiguousCodestreamBoxPosition < structure.JP2HeaderBoxPosition)
            {
                warnings.Add($"Unexpected box ordering: expected Codestream after JP2Header");
            }

            // Check for multiple JP2 Header boxes
            if (structure.JP2HeaderBoxCount > 1)
            {
                errors.Add($"Multiple JP2 Header Boxes found ({structure.JP2HeaderBoxCount}). Only one is allowed.");
            }

            // Check for boxes appearing before JP2 Header that shouldn't
            if (structure.HasMetadataBeforeHeader)
            {
                warnings.Add("Metadata boxes (XML, UUID) found before JP2 Header Box (non-standard location)");
            }
        }

        /// <summary>
        /// Runs enhanced validation checks for improved compliance.
        /// </summary>
        private void RunEnhancedValidationChecks(JP2Structure structure)
        {
            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                "Running enhanced compliance validation checks...");

            // Validate MinorVersion
            if (structure.HasFileTypeBox)
            {
                ValidateMinorVersion(structure.MinorVersion);
                LogBrandInformation(structure.HasValidBrand ? FileFormatBoxes.FT_BR : 0);
            }

            // Check BPC box requirement
            if (structure.HasImageHeaderBox && structure.ImageHeaderBPCValue == 0xFF)
            {
                if (!structure.HasBitsPerComponentBox)
                {
                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                        "Image Header has BPC=0xFF but Bits Per Component box is missing (required)");
                }
                else
                {
                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                        "Bits Per Component box present as required (BPC varies)");
                }
            }

            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                "Enhanced validation checks complete");
        }

        // #region Enhanced Validation Methods

        /// <summary>
        /// Validates the MinorVersion (MinV) field in the File Type box per ISO/IEC 15444-1 Section I.5.2.
        /// The MinV field indicates the minor version of the JP2 specification.
        /// </summary>
        private void ValidateMinorVersion(int minorVersion)
        {
            if (minorVersion < 0)
            {
                errors.Add($"Invalid MinorVersion value: {minorVersion} (must be non-negative)");
            }

            if (minorVersion > 0)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                    $"File uses MinorVersion {minorVersion} (> 0). " +
                    "This may indicate extended features beyond baseline JP2. " +
                    "Some features may not be fully supported.");
            }
        }

        /// <summary>
        /// Logs information about the file brand.
        /// </summary>
        private void LogBrandInformation(int brand)
        {
            const int JP2_BRAND = 0x6a703220; // 'jp2 '
            const int JPX_BRAND = 0x6a707820; // 'jpx ' (Part 2)
            const int JPM_BRAND = 0x6a706d20; // 'jpm ' (Part 6)

            if (brand == JP2_BRAND)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                    "File brand: jp2 (baseline JPEG 2000 Part 1)");
            }
            else if (brand == JPX_BRAND)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                    "File brand: jpx (JPEG 2000 Part 2 - Extensions)");
            }
            else if (brand == JPM_BRAND)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                    "File brand: jpm (JPEG 2000 Part 6 - Compound Image)");
            }
            else if (brand != 0)
            {
                var brandStr = $"{(char)((brand >> 24) & 0xFF)}{(char)((brand >> 16) & 0xFF)}" +
                             $"{(char)((brand >> 8) & 0xFF)}{(char)(brand & 0xFF)}";
                warnings.Add($"Unknown file brand: '{brandStr}' (0x{brand:X8}). Expected 'jp2 ', 'jpx ', or 'jpm '");
            }
        }

        /// <summary>
        /// Performs basic ICC profile header validation per ICC.1:2010 specification.
        /// Validates profile size, signature, and version fields.
        /// </summary>
        /// <param name="profileBytes">The ICC profile data.</param>
        /// <returns>True if basic validation passes, false otherwise.</returns>
        public bool ValidateIccProfileBasic(byte[] profileBytes)
        {
            if (profileBytes == null || profileBytes.Length < 128)
            {
                warnings.Add("ICC profile is too small (< 128 bytes minimum header size)");
                return false;
            }

            // Check profile size field (bytes 0-3, big-endian)
            var profileSize = (profileBytes[0] << 24) | (profileBytes[1] << 16) |
                            (profileBytes[2] << 8) | profileBytes[3];

            if (profileSize != profileBytes.Length)
            {
                warnings.Add($"ICC profile size mismatch: header says {profileSize} bytes, actual {profileBytes.Length} bytes");
                return false;
            }

            // Check profile signature 'acsp' (bytes 36-39)
            if (profileBytes[36] != 'a' || profileBytes[37] != 'c' ||
                profileBytes[38] != 's' || profileBytes[39] != 'p')
            {
                warnings.Add("ICC profile missing 'acsp' signature at offset 36");
                return false;
            }

            // Check color space signature (bytes 16-19)
            var colorSpace = $"{(char)profileBytes[16]}{(char)profileBytes[17]}" +
                           $"{(char)profileBytes[18]}{(char)profileBytes[19]}";

            // Common colorspaces: 'RGB ', 'GRAY', 'CMYK', 'XYZ ', 'Lab ', etc.
            var validColorSpaces = new[] { "RGB ", "GRAY", "CMYK", "XYZ ", "Lab " };
            var isValidCs = false;
            foreach (var cs in validColorSpaces)
            {
                if (colorSpace == cs)
                {
                    isValidCs = true;
                    break;
                }
            }

            if (!isValidCs)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                    $"ICC profile uses colorspace '{colorSpace}' (may be a specialized colorspace)");
            }

            return true;
        }

        /// <summary>
        /// Detects if a box uses extended length (XLBox) format.
        /// Extended length is used for boxes > 4GB (when LBox = 1).
        /// </summary>
        /// <param name="length">The LBox value (first 4 bytes of box).</param>
        /// <param name="longLength">The XLBox value if present.</param>
        /// <returns>True if XLBox is detected.</returns>
        public bool DetectExtendedLength(int length, long longLength)
        {
            if (length == 1 && longLength > 0)
            {
                var sizeGB = longLength / (1024.0 * 1024.0 * 1024.0);
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Box uses extended length (XLBox) format: {sizeGB:F2} GB. " +
                    "Extended length boxes (>4GB) are not fully supported in current implementation.");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Validates basic codestream marker ordering per ISO/IEC 15444-1 Annex A.
        /// Checks that SOC marker is first and critical markers appear in correct order.
        /// </summary>
        /// <param name="codestreamBytes">The codestream data.</param>
        /// <returns>True if basic marker order is valid.</returns>
        public bool ValidateBasicCodestreamMarkers(byte[] codestreamBytes)
        {
            if (codestreamBytes == null || codestreamBytes.Length < 2)
            {
                errors.Add("Codestream too small to contain valid markers");
                return false;
            }

            // Check for SOC (Start of Codestream) marker (0xFF4F) at position 0
            if (codestreamBytes[0] != 0xFF || codestreamBytes[1] != 0x4F)
            {
                errors.Add($"Codestream must start with SOC marker (0xFF4F), found: 0x{codestreamBytes[0]:X2}{codestreamBytes[1]:X2}");
                return false;
            }

            // Check that codestream ends with EOC (End of Codestream) marker (0xFFD9)
            var len = codestreamBytes.Length;
            if (len >= 2)
            {
                if (codestreamBytes[len - 2] != 0xFF || codestreamBytes[len - 1] != 0xD9)
                {
                    warnings.Add($"Codestream should end with EOC marker (0xFFD9), found: 0x{codestreamBytes[len - 2]:X2}{codestreamBytes[len - 1]:X2}");
                }
            }

            // Check for SIZ marker (0xFF51) after SOC
            // Per ISO spec, SIZ must be the first marker segment after SOC
            if (codestreamBytes.Length >= 4)
            {
                if (codestreamBytes[2] != 0xFF || codestreamBytes[3] != 0x51)
                {
                    warnings.Add($"SIZ marker (0xFF51) should immediately follow SOC, found: 0x{codestreamBytes[2]:X2}{codestreamBytes[3]:X2}");
                }
            }

            return true;
        }

        /// <summary>
        /// Validates compatibility list in File Type box.
        /// Checks that required profiles are present.
        /// </summary>
        /// <param name="compatibilityList">Array of compatibility entries.</param>
        /// <param name="requireJP2">Whether JP2 compatibility is required.</param>
        public void ValidateCompatibilityList(int[] compatibilityList, bool requireJP2 = true)
        {
            if (compatibilityList == null || compatibilityList.Length == 0)
            {
                warnings.Add("Compatibility list is empty");
                return;
            }

            const int JP2_BRAND = 0x6a703220; // 'jp2 '
            var hasJP2 = false;

            foreach (var compat in compatibilityList)
            {
                if (compat == JP2_BRAND)
                {
                    hasJP2 = true;
                    break;
                }
            }

            if (requireJP2 && !hasJP2)
            {
                errors.Add("Compatibility list must include 'jp2 ' (0x6a703220) for baseline JP2 compliance");
            }

            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                $"Compatibility list contains {compatibilityList.Length} profile(s)");
        }

        /// <summary>
        /// Performs comprehensive codestream validation using CodestreamValidator.
        /// This validates the full codestream structure including all markers per Annex A.
        /// </summary>
        /// <param name="codestreamBytes">The codestream data.</param>
        /// <param name="maxBytesToRead">Maximum bytes to read (0 = read all available).</param>
        /// <returns>True if codestream validation passed without errors.</returns>
        public bool ValidateCodestreamComprehensive(byte[] codestreamBytes, int maxBytesToRead = 0)
        {
            var csValidator = new CoreJ2K.j2k.codestream.CodestreamValidator();
            var result = csValidator.ValidateCodestream(codestreamBytes, maxBytesToRead);

            // Merge errors and warnings from codestream validator
            foreach (var error in csValidator.Errors)
            {
                errors.Add($"Codestream: {error}");
            }

            foreach (var warning in csValidator.Warnings)
            {
                warnings.Add($"Codestream: {warning}");
            }

            // Log info messages
            foreach (var info in csValidator.Info)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, $"Codestream: {info}");
            }

            return result;
        }

        // #endregion

        /// <summary>
        /// Gets a formatted validation report.
        /// </summary>
        public string GetValidationReport()
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("=== JP2 File Format Validation Report ===");
            report.AppendLine();

            if (errors.Count == 0 && warnings.Count == 0)
            {
                report.AppendLine("? File is valid JP2 format (ISO/IEC 15444-1 compliant)");
            }
            else
            {
                if (errors.Count > 0)
                {
                    report.AppendLine($"ERRORS ({errors.Count}):");
                    foreach (var error in errors)
                    {
                        report.AppendLine($"  ? {error}");
                    }
                    report.AppendLine();
                }

                if (warnings.Count > 0)
                {
                    report.AppendLine($"WARNINGS ({warnings.Count}):");
                    foreach (var warning in warnings)
                    {
                        report.AppendLine($"  ? {warning}");
                    }
                }
            }

            return report.ToString();
        }
    }

    /// <summary>
    /// Represents the structure of a JP2 file for validation purposes.
    /// </summary>
    public class JP2Structure
    {
        // Signature Box
        public bool HasSignatureBox { get; set; }
        public int SignatureBoxPosition { get; set; }
        public int SignatureBoxLength { get; set; }

        // File Type Box
        public bool HasFileTypeBox { get; set; }
        public int FileTypeBoxPosition { get; set; }
        public int FileTypeBoxLength { get; set; }
        public bool HasValidBrand { get; set; }
        public bool HasJP2Compatibility { get; set; }
        public int MinorVersion { get; set; }

        // JP2 Header Box
        public bool HasJP2HeaderBox { get; set; }
        public int JP2HeaderBoxPosition { get; set; }
        public int JP2HeaderBoxLength { get; set; }
        public int JP2HeaderBoxCount { get; set; }

        // JP2 Header Sub-boxes
        public bool HasImageHeaderBox { get; set; }
        public int ImageHeaderBoxOrder { get; set; }
        public byte? ImageHeaderBPCValue { get; set; }

        public bool HasColourSpecificationBox { get; set; }
        public int ColourSpecificationBoxOrder { get; set; }

        public bool HasBitsPerComponentBox { get; set; }
        public int BitsPerComponentBoxOrder { get; set; }

        public bool HasPaletteBox { get; set; }
        public int PaletteBoxOrder { get; set; }

        public bool HasComponentMappingBox { get; set; }
        public int ComponentMappingBoxOrder { get; set; }

        public bool HasChannelDefinitionBox { get; set; }
        public int ChannelDefinitionBoxOrder { get; set; }

        public bool HasResolutionBox { get; set; }
        public int ResolutionBoxOrder { get; set; }

        // Contiguous Codestream Box
        public bool HasContiguousCodestreamBox { get; set; }
        public int ContiguousCodestreamBoxPosition { get; set; }

        // Other boxes
        public bool HasMetadataBeforeHeader { get; set; }

        /// <summary>
        /// Gets the top-level box order as a list of box names.
        /// </summary>
        public List<string> GetTopLevelBoxOrder()
        {
            var order = new List<(int position, string name)>();

            if (HasSignatureBox)
                order.Add((SignatureBoxPosition, "Signature"));
            if (HasFileTypeBox)
                order.Add((FileTypeBoxPosition, "FileType"));
            if (HasJP2HeaderBox)
                order.Add((JP2HeaderBoxPosition, "JP2Header"));
            if (HasContiguousCodestreamBox)
                order.Add((ContiguousCodestreamBoxPosition, "Codestream"));

            order.Sort((a, b) => a.position.CompareTo(b.position));

            return order.ConvertAll(item => item.name);
        }
    }
}
