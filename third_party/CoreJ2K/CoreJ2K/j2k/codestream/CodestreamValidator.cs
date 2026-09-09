// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using CoreJ2K.j2k.util;

namespace CoreJ2K.j2k.codestream
{
    /// <summary>
    /// Provides comprehensive validation of JPEG 2000 codestream markers per ISO/IEC 15444-1 Annex A.
    /// Validates marker ordering, presence of required markers, and marker segment syntax.
    /// </summary>
    public class CodestreamValidator
    {
        private readonly List<string> errors = new List<string>();
        private readonly List<string> warnings = new List<string>();
        private readonly List<string> info = new List<string>();

        // Validation state
        private int numComponents;
        private int numTilesX;
        private int numTilesY;
        private int decompositionLevels;
        private bool usesSOP;
        private bool usesEPH;
        private bool[] componentHasCOC;
        private bool[] componentHasQCC;

        /// <summary>
        /// Gets validation errors found during codestream validation.
        /// </summary>
        public IReadOnlyList<string> Errors => errors.AsReadOnly();

        /// <summary>
        /// Gets validation warnings found during codestream validation.
        /// </summary>
        public IReadOnlyList<string> Warnings => warnings.AsReadOnly();

        /// <summary>
        /// Gets informational messages from validation.
        /// </summary>
        public IReadOnlyList<string> Info => info.AsReadOnly();

        /// <summary>
        /// Returns true if any validation errors were found.
        /// </summary>
        public bool HasErrors => errors.Count > 0;

        /// <summary>
        /// Returns true if any validation warnings were found.
        /// </summary>
        public bool HasWarnings => warnings.Count > 0;

        /// <summary>
        /// Validates a complete JPEG 2000 codestream.
        /// </summary>
        /// <param name="codestream">The codestream bytes to validate.</param>
        /// <param name="maxBytesToRead">Maximum bytes to read (0 = read all).</param>
        /// <returns>True if validation passed without errors.</returns>
        public bool ValidateCodestream(byte[] codestream, int maxBytesToRead = 0)
        {
            errors.Clear();
            warnings.Clear();
            info.Clear();

            if (codestream == null || codestream.Length < 2)
            {
                errors.Add("Codestream is null or too small");
                return false;
            }

            var maxBytes = maxBytesToRead > 0 ? Math.Min(maxBytesToRead, codestream.Length) : codestream.Length;

            try
            {
                // Validate main header
                var pos = ValidateMainHeader(codestream, maxBytes);
                if (pos < 0)
                    return false;

                // Validate tile-parts if we have enough data
                if (pos < maxBytes - 1)
                {
                    pos = ValidateTileParts(codestream, pos, maxBytes);
                    if (pos < 0)
                        return false;
                }

                // Check for EOC marker
                if (pos < maxBytes - 1)
                {
                    if (!ValidateEOC(codestream, ref pos, maxBytes))
                    {
                        warnings.Add("EOC marker not found at end of examined data");
                    }
                }

                info.Add($"Codestream validated successfully (examined {pos} bytes)");

                return !HasErrors;
            }
            catch (IndexOutOfRangeException ex)
            {
                errors.Add($"Codestream truncated or corrupted: {ex.Message}");
                return false;
            }
            catch (ArgumentOutOfRangeException ex)
            {
                errors.Add($"Invalid marker segment length or position: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                errors.Add($"Exception during validation: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Validates the main header of a codestream.
        /// Returns the position after the main header, or -1 on error.
        /// </summary>
        private int ValidateMainHeader(byte[] data, int maxBytes)
        {
            var pos = 0;

            // 1. SOC marker must be first (0xFF4F)
            if (!ValidateSOC(data, ref pos, maxBytes))
                return -1;

            // 2. SIZ marker must immediately follow SOC (0xFF51)
            if (!ValidateSIZ(data, ref pos, maxBytes))
                return -1;

            // 3. Main header markers (in flexible order, but COD must precede first tile-part)
            var hasCOD = false;
            var hasQCD = false;
            var codCount = 0;
            var qcdCount = 0;

            // Initialize component tracking arrays
            componentHasCOC = new bool[numComponents];
            componentHasQCC = new bool[numComponents];

            while (pos < maxBytes - 1)
            {
                // Safety check: ensure we have at least 2 bytes for marker
                if (pos + 1 >= maxBytes)
                {
                    warnings.Add($"Main header validation incomplete (reached maxBytes limit at position {pos})");
                    break;
                }

                // Check if we've reached SOT (start of tile-part headers)
                if (data[pos] == 0xFF && data[pos + 1] == 0x90) // SOT marker
                {
                    // Main header complete
                    if (!hasCOD)
                    {
                        errors.Add("Main header missing required COD marker");
                        return -1;
                    }
                    if (!hasQCD)
                    {
                        errors.Add("Main header missing required QCD marker");
                        return -1;
                    }
                    return pos;
                }

                // Check if we've reached SOD (start of data)
                if (data[pos] == 0xFF && data[pos + 1] == 0x93) // SOD marker
                {
                    errors.Add("SOD marker found before SOT (no tile-parts defined)");
                    return -1;
                }

                // Check if we've reached EOC (end of codestream)
                if (data[pos] == 0xFF && data[pos + 1] == 0xD9) // EOC marker
                {
                    warnings.Add("EOC marker found in main header (before any tile-parts)");
                    return pos;
                }

                // Read marker
                if (data[pos] != 0xFF)
                {
                    errors.Add($"Expected marker at position {pos}, found 0x{data[pos]:X2}");
                    return -1;
                }

                var marker = (data[pos] << 8) | data[pos + 1];
                pos += 2;

                switch (marker)
                {
                    case Markers.COD: // 0xFF52
                        if (codCount > 0)
                            warnings.Add("Multiple COD markers in main header (last one takes precedence)");
                        if (!ValidateCOD(data, ref pos, maxBytes))
                            return -1;
                        hasCOD = true;
                        codCount++;
                        break;

                    case Markers.COC: // 0xFF53
                        if (!hasCOD)
                            warnings.Add("COC marker before COD marker");
                        if (!ValidateCOC(data, ref pos, maxBytes))
                            return -1;
                        break;

                    case Markers.QCD: // 0xFF5C
                        if (qcdCount > 0)
                            warnings.Add("Multiple QCD markers in main header (last one takes precedence)");
                        if (!ValidateQCD(data, ref pos, maxBytes))
                            return -1;
                        hasQCD = true;
                        qcdCount++;
                        break;

                    case Markers.QCC: // 0xFF5D
                        if (!hasQCD)
                            warnings.Add("QCC marker before QCD marker");
                        if (!ValidateQCC(data, ref pos, maxBytes))
                            return -1;
                        break;

                    case Markers.RGN: // 0xFF5E
                        if (!ValidateRGN(data, ref pos, maxBytes))
                            return -1;
                        break;

                    case Markers.POC: // 0xFF5F
                        if (!ValidatePOC(data, ref pos, maxBytes))
                            return -1;
                        break;

                    case Markers.PPM: // 0xFF60
                        if (!ValidatePPM(data, ref pos, maxBytes))
                            return -1;
                        break;

                    case Markers.TLM: // 0xFF55
                        if (!ValidateTLM(data, ref pos, maxBytes))
                            return -1;
                        break;

                    case Markers.PLM: // 0xFF57
                        if (!ValidatePLM(data, ref pos, maxBytes))
                            return -1;
                        break;

                    case Markers.CRG: // 0xFF63
                        if (!ValidateCRG(data, ref pos, maxBytes))
                            return -1;
                        break;

                    case Markers.COM: // 0xFF64
                        if (!ValidateCOM(data, ref pos, maxBytes))
                            return -1;
                        break;

                    default:
                        // Don't fail on unknown markers, just warn and try to skip
                        warnings.Add($"Unknown or unexpected marker in main header: 0x{marker:X4} at position {pos - 2}");
                        // Try to skip the marker if it has a length field
                        if (!TrySkipUnknownMarker(data, ref pos, maxBytes))
                            return -1;
                        break;
                }
            }

            // If we reach here, we didn't find SOT or EOC
            warnings.Add("Main header validation incomplete (no SOT or tile-parts found in examined data)");
            return pos;
        }

        /// <summary>
        /// Validates tile-part headers and data.
        /// Returns the position after tile-parts, or -1 on error.
        /// </summary>
        private int ValidateTileParts(byte[] data, int startPos, int maxBytes)
        {
            var pos = startPos;
            var tilePartCount = 0;
            var expectedTileParts = numTilesX * numTilesY;

            info.Add($"Validating tile-parts (expecting {expectedTileParts} tiles)");

            while (pos < maxBytes - 1)
            {
                // Check for SOT marker
                if (pos + 1 < maxBytes && data[pos] == 0xFF && data[pos + 1] == 0x90)
                {
                    if (!ValidateSOT(data, ref pos, maxBytes))
                        return -1;
                    
                    tilePartCount++;

                    // Validate tile-part header markers (COD, COC, QCD, QCC, RGN, POC, PPT, PLT, COM)
                    pos = ValidateTilePartHeader(data, pos, maxBytes);
                    if (pos < 0)
                        return -1;

                    // Expect SOD marker
                    if (pos + 1 < maxBytes && data[pos] == 0xFF && data[pos + 1] == 0x93)
                    {
                        if (!ValidateSOD(data, ref pos, maxBytes))
                            return -1;

                        // Skip tile-part data (we don't validate the bitstream itself)
                        // The tile-part data continues until the next SOT, EOC, or end of data
                        pos = SkipTilePartData(data, pos, maxBytes);
                        if (pos < 0)
                            return -1;
                    }
                    else if (pos + 1 < maxBytes)
                    {
                        errors.Add($"Expected SOD marker after tile-part header at position {pos}, found: 0x{data[pos]:X2}{data[pos + 1]:X2}");
                        return -1;
                    }
                }
                else if (data[pos] == 0xFF && data[pos + 1] == 0xD9) // EOC
                {
                    // End of codestream found
                    break;
                }
                else
                {
                    // Unexpected marker or data
                    warnings.Add($"Unexpected data at position {pos} (not SOT or EOC): 0x{data[pos]:X2}{data[pos + 1]:X2}");
                    break;
                }
            }

            if (tilePartCount < expectedTileParts)
            {
                warnings.Add($"Only {tilePartCount} of {expectedTileParts} expected tile-parts were found in examined data");
            }
            else
            {
                info.Add($"Validated {tilePartCount} tile-parts");
            }

            return pos;
        }

        /// <summary>
        /// Validates a tile-part header.
        /// Returns the position after the header, or -1 on error.
        /// </summary>
        private int ValidateTilePartHeader(byte[] data, int startPos, int maxBytes)
        {
            var pos = startPos;

            while (pos < maxBytes - 1)
            {
                if (data[pos] != 0xFF)
                {
                    // End of tile-part header (reached tile data or SOD)
                    break;
                }

                var marker = (data[pos] << 8) | data[pos + 1];

                // SOD marks end of tile-part header
                if (marker == Markers.SOD)
                    break;

                pos += 2;

                switch (marker)
                {
                    case Markers.COD:
                        if (!ValidateCOD(data, ref pos, maxBytes))
                            return -1;
                        break;

                    case Markers.COC:
                        if (!ValidateCOC(data, ref pos, maxBytes))
                            return -1;
                        break;

                    case Markers.QCD:
                        if (!ValidateQCD(data, ref pos, maxBytes))
                            return -1;
                        break;

                    case Markers.QCC:
                        if (!ValidateQCC(data, ref pos, maxBytes))
                            return -1;
                        break;

                    case Markers.RGN:
                        if (!ValidateRGN(data, ref pos, maxBytes))
                            return -1;
                        break;

                    case Markers.POC:
                        if (!ValidatePOC(data, ref pos, maxBytes))
                            return -1;
                        break;

                    case Markers.PPT: // 0xFF61
                        if (!ValidatePPT(data, ref pos, maxBytes))
                            return -1;
                        break;

                    case Markers.PLT: // 0xFF58
                        if (!ValidatePLT(data, ref pos, maxBytes))
                            return -1;
                        break;

                    case Markers.COM:
                        if (!ValidateCOM(data, ref pos, maxBytes))
                            return -1;
                        break;

                    default:
                        warnings.Add($"Unexpected marker in tile-part header: 0x{marker:X4} at position {pos - 2}");
                        if (!TrySkipUnknownMarker(data, ref pos, maxBytes))
                            return -1;
                        break;
                }
            }

            return pos;
        }

        /// <summary>
        /// Skips over tile-part data, looking for SOP markers if enabled.
        /// Returns the position after the tile data, or -1 on error.
        /// </summary>
        private int SkipTilePartData(byte[] data, int startPos, int maxBytes)
        {
            var pos = startPos;
            var sopCount = 0;
            var ephCount = 0;

            // Scan for packet markers if SOP/EPH are used
            while (pos < maxBytes - 1)
            {
                // Check for next marker (SOT, EOC, or packet markers)
                if (data[pos] == 0xFF)
                {
                    var marker = (data[pos] << 8) | data[pos + 1];

                    if (marker == Markers.SOT || marker == Markers.EOC)
                    {
                        // Reached next tile or end
                        return pos;
                    }
                    else if (marker == Markers.SOP) // 0xFF91
                    {
                        if (!ValidateSOP(data, ref pos, maxBytes))
                            return -1;
                        sopCount++;
                    }
                    else if (marker == Markers.EPH) // 0xFF92
                    {
                        if (!ValidateEPH(data, ref pos, maxBytes))
                            return -1;
                        ephCount++;
                    }
                    else
                    {
                        // Continue through tile data
                        pos++;
                    }
                }
                else
                {
                    pos++;
                }
            }

            if (sopCount > 0)
            {
                info.Add($"Found {sopCount} SOP markers in tile data");
            }
            if (ephCount > 0)
            {
                info.Add($"Found {ephCount} EPH markers in tile data");
            }

            return pos;
        }

        private bool ValidateSOC(byte[] data, ref int pos, int maxBytes)
        {
            if (pos + 1 >= maxBytes)
            {
                errors.Add("Codestream too short for SOC marker");
                return false;
            }

            if (data[pos] != 0xFF || data[pos + 1] != 0x4F)
            {
                errors.Add($"Codestream must start with SOC marker (0xFF4F), found: 0x{data[pos]:X2}{data[pos + 1]:X2}");
                return false;
            }

            pos += 2;
            info.Add("SOC marker validated");
            return true;
        }

        private bool ValidateSIZ(byte[] data, ref int pos, int maxBytes)
        {
            if (pos + 1 >= maxBytes)
            {
                errors.Add("Insufficient data for SIZ marker");
                return false;
            }

            if (data[pos] != 0xFF || data[pos + 1] != 0x51)
            {
                errors.Add($"SIZ marker (0xFF51) must immediately follow SOC, found: 0x{data[pos]:X2}{data[pos + 1]:X2}");
                return false;
            }

            pos += 2;

            // Read Lsiz
            if (pos + 2 > maxBytes)
            {
                errors.Add("Insufficient data for SIZ length");
                return false;
            }

            var lsiz = (data[pos] << 8) | data[pos + 1];
            pos += 2;

            if (lsiz < 41) // Minimum SIZ size
            {
                errors.Add($"Invalid SIZ marker length: {lsiz} (minimum is 41)");
                return false;
            }

            if (pos + lsiz - 2 > maxBytes)
            {
                errors.Add($"Insufficient data for SIZ marker segment (need {lsiz - 2} more bytes)");
                return false;
            }

            // Read Rsiz (capabilities)
            var rsiz = (data[pos] << 8) | data[pos + 1];
            pos += 2;

            // Validate Rsiz field
            if (rsiz > 0 && rsiz != Markers.RSIZ_ER_FLAG && rsiz != Markers.RSIZ_ROI && rsiz != (Markers.RSIZ_ER_FLAG | Markers.RSIZ_ROI))
            {
                warnings.Add($"Non-standard Rsiz value: 0x{rsiz:X4} (may indicate extended capabilities)");
            }

            // Read image dimensions
            var xsiz = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;
            var ysiz = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;

            if (xsiz == 0 || ysiz == 0)
            {
                errors.Add($"Invalid image dimensions in SIZ: {xsiz}x{ysiz}");
                return false;
            }

            if (xsiz > 65535 || ysiz > 65535)
            {
                warnings.Add($"Large image dimensions: {xsiz}x{ysiz} (may cause memory issues)");
            }

            // Read offsets
            var x0siz = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;
            var y0siz = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;

            if (x0siz >= xsiz || y0siz >= ysiz)
            {
                errors.Add($"Invalid image offsets: ({x0siz},{y0siz}) must be less than image size ({xsiz},{ysiz})");
                return false;
            }

            // Read tile dimensions
            var xtsiz = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;
            var ytsiz = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;

            if (xtsiz == 0 || ytsiz == 0)
            {
                errors.Add($"Invalid tile dimensions in SIZ: {xtsiz}x{ytsiz}");
                return false;
            }

            // Read tile offsets
            var xt0siz = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;
            var yt0siz = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;

            // Calculate number of tiles
            numTilesX = (int)Math.Ceiling((double)(xsiz - xt0siz) / xtsiz);
            numTilesY = (int)Math.Ceiling((double)(ysiz - yt0siz) / ytsiz);
            var totalTiles = numTilesX * numTilesY;

            if (totalTiles > 65535)
            {
                warnings.Add($"Very large number of tiles: {totalTiles} ({numTilesX}x{numTilesY})");
            }

            // Read Csiz (number of components)
            var csiz = (data[pos] << 8) | data[pos + 1];
            pos += 2;

            if (csiz == 0 || csiz > 16384)
            {
                errors.Add($"Invalid number of components in SIZ: {csiz}");
                return false;
            }

            numComponents = csiz;

            // Validate that we have data for all components (3 bytes each)
            var expectedLength = 38 + (3 * csiz);
            if (lsiz != expectedLength)
            {
                errors.Add($"SIZ length mismatch: expected {expectedLength}, got {lsiz}");
                return false;
            }

            // Validate component bit depths
            for (int i = 0; i < csiz; i++)
            {
                var ssiz = data[pos++];
                var bitDepth = (ssiz & 0x7F) + 1;
                var IsSigned = (ssiz & 0x80) != 0;

                if (bitDepth > Markers.MAX_COMP_BITDEPTH)
                {
                    errors.Add($"Component {i} bit depth {bitDepth} exceeds maximum {Markers.MAX_COMP_BITDEPTH}");
                    return false;
                }

                // Read subsampling
                var xrsiz = data[pos++];
                var yrsiz = data[pos++];

                if (xrsiz == 0 || yrsiz == 0 || xrsiz > 255 || yrsiz > 255)
                {
                    errors.Add($"Invalid subsampling for component {i}: {xrsiz}x{yrsiz}");
                    return false;
                }
            }

            info.Add($"SIZ marker validated: {xsiz}x{ysiz}, {csiz} components, {totalTiles} tiles, Rsiz=0x{rsiz:X4}");
            return true;
        }

        private bool ValidateCOD(byte[] data, ref int pos, int maxBytes)
        {
            if (pos + 2 > maxBytes)
            {
                errors.Add("Insufficient data for COD length");
                return false;
            }

            var lcod = (data[pos] << 8) | data[pos + 1];
            pos += 2;

            if (lcod < 12)
            {
                errors.Add($"Invalid COD marker length: {lcod} (minimum is 12)");
                return false;
            }

            if (pos + lcod - 2 > maxBytes)
            {
                errors.Add($"Insufficient data for COD marker segment");
                return false;
            }

            // Read Scod
            var scod = data[pos++];
            
            // Check for SOP/EPH usage
            usesSOP = (scod & Markers.SCOX_USE_SOP) != 0;
            usesEPH = (scod & Markers.SCOX_USE_EPH) != 0;
            
            var precinctUsed = (scod & Markers.SCOX_PRECINCT_PARTITION) != 0;

            // Read progression order
            var progressionOrder = data[pos++];
            if (progressionOrder > 4)
            {
                errors.Add($"Invalid progression order in COD: {progressionOrder} (must be 0-4)");
                return false;
            }

            // Read number of layers
            var numLayers = (data[pos] << 8) | data[pos + 1];
            pos += 2;

            if (numLayers == 0 || numLayers > 65535)
            {
                errors.Add($"Invalid number of layers in COD: {numLayers}");
                return false;
            }

            // Read MCT
            var mct = data[pos++];
            if (mct > 1)
            {
                warnings.Add($"Non-standard MCT value in COD: {mct}");
            }

            // Read decomposition levels
            var levels = data[pos++];
            if (levels > 32)
            {
                warnings.Add($"High number of decomposition levels: {levels}");
            }
            decompositionLevels = levels;

            // Read code-block dimensions
            var cbWidth = data[pos++];
            var cbHeight = data[pos++];

            if (cbWidth > 8 || cbHeight > 8)
            {
                errors.Add($"Invalid code-block dimensions: 2^({cbWidth}+2) x 2^({cbHeight}+2) (exponents must be ? 8)");
                return false;
            }

            if ((cbWidth + cbHeight) > 12)
            {
                errors.Add($"Code-block area too large: 2^({cbWidth}+{cbHeight}+4) (sum must be ? 12)");
                return false;
            }

            // Read code-block style
            var cbStyle = data[pos++];
            if ((cbStyle & 0xC0) != 0)
            {
                warnings.Add($"Reserved bits set in code-block style: 0x{cbStyle:X2}");
            }

            // Read transformation type
            var transform = data[pos++];
            if (transform > 1)
            {
                errors.Add($"Invalid wavelet transform in COD: {transform} (must be 0 or 1)");
                return false;
            }

            // Read precinct sizes if used
            if (precinctUsed)
            {
                var expectedPrecincts = levels + 1;
                var remainingBytes = lcod - 11;
                
                if (remainingBytes != expectedPrecincts)
                {
                    errors.Add($"COD precinct size mismatch: expected {expectedPrecincts} bytes, got {remainingBytes}");
                    return false;
                }

                for (int i = 0; i < expectedPrecincts; i++)
                {
                    var ppx = data[pos] & 0x0F;
                    var ppy = (data[pos] >> 4) & 0x0F;
                    pos++;

                    if (ppx > 15 || ppy > 15)
                    {
                        errors.Add($"Invalid precinct size exponents at level {i}: PPx={ppx}, PPy={ppy}");
                        return false;
                    }
                }
            }

            info.Add($"COD marker validated: {levels} decomposition levels, {numLayers} layers, progression={progressionOrder}");
            return true;
        }

        private bool ValidateCOC(byte[] data, ref int pos, int maxBytes)
        {
            if (pos + 2 > maxBytes)
            {
                errors.Add("Insufficient data for COC length");
                return false;
            }

            var lcoc = (data[pos] << 8) | data[pos + 1];
            pos += 2;

            var minLength = numComponents < 257 ? 9 : 10;
            if (lcoc < minLength)
            {
                errors.Add($"Invalid COC marker length: {lcoc} (minimum is {minLength})");
                return false;
            }

            if (pos + lcoc - 2 > maxBytes)
            {
                errors.Add($"Insufficient data for COC marker segment");
                return false;
            }

            // Read component index
            int compIdx;
            if (numComponents < 257)
            {
                compIdx = data[pos++];
            }
            else
            {
                compIdx = (data[pos] << 8) | data[pos + 1];
                pos += 2;
            }

            if (compIdx >= numComponents)
            {
                errors.Add($"COC component index {compIdx} exceeds number of components {numComponents}");
                return false;
            }

            componentHasCOC[compIdx] = true;

            // Skip rest of COC data (similar structure to COD)
            pos += lcoc - (numComponents < 257 ? 3 : 4);

            info.Add($"COC marker validated for component {compIdx} ({lcoc} bytes)");
            return true;
        }

        private bool ValidateQCD(byte[] data, ref int pos, int maxBytes)
        {
            if (pos + 2 > maxBytes)
            {
                errors.Add("Insufficient data for QCD length");
                return false;
            }

            var lqcd = (data[pos] << 8) | data[pos + 1];
            pos += 2;

            if (lqcd < 4)
            {
                errors.Add($"Invalid QCD marker length: {lqcd}");
                return false;
            }

            if (pos + lqcd - 2 > maxBytes)
            {
                errors.Add($"Insufficient data for QCD marker segment");
                return false;
            }

            // Read Sqcd (quantization style)
            var sqcd = data[pos++];
            var qstyle = sqcd & 0x1F;
            var guardBits = (sqcd >> 5) & 0x07;

            if (qstyle > 2)
            {
                errors.Add($"Invalid quantization style in QCD: {qstyle}");
                return false;
            }

            // Validate quantization parameters based on style
            var numSubbands = 3 * decompositionLevels + 1;
            var expectedLength = 0;

            switch (qstyle)
            {
                case Markers.SQCX_NO_QUANTIZATION: // Reversible
                    expectedLength = 4 + numSubbands; // 1 byte per subband
                    break;
                case Markers.SQCX_SCALAR_DERIVED: // Irreversible, derived
                    expectedLength = 5; // Only LL band
                    break;
                case Markers.SQCX_SCALAR_EXPOUNDED: // Irreversible, expounded
                    expectedLength = 4 + (numSubbands * 2); // 2 bytes per subband
                    break;
            }

            if (lqcd != expectedLength)
            {
                warnings.Add($"QCD length {lqcd} doesn't match expected {expectedLength} for quantization style {qstyle} with {decompositionLevels} levels");
            }

            // Skip rest of marker
            pos += lqcd - 3;

            info.Add($"QCD marker validated: style={qstyle}, guard bits={guardBits}");
            return true;
        }

        private bool ValidateQCC(byte[] data, ref int pos, int maxBytes)
        {
            if (pos + 2 > maxBytes)
            {
                errors.Add("Insufficient data for QCC length");
                return false;
            }

            var lqcc = (data[pos] << 8) | data[pos + 1];
            pos += 2;

            var minLength = numComponents < 257 ? 5 : 6;
            if (lqcc < minLength)
            {
                errors.Add($"Invalid QCC marker length: {lqcc} (minimum is {minLength})");
                return false;
            }

            if (pos + lqcc - 2 > maxBytes)
            {
                errors.Add($"Insufficient data for QCC marker segment");
                return false;
            }

            // Read component index
            int compIdx;
            if (numComponents < 257)
            {
                compIdx = data[pos++];
            }
            else
            {
                compIdx = (data[pos] << 8) | data[pos + 1];
                pos += 2;
            }

            if (compIdx >= numComponents)
            {
                errors.Add($"QCC component index {compIdx} exceeds number of components {numComponents}");
                return false;
            }

            componentHasQCC[compIdx] = true;

            // Skip rest of QCC data
            pos += lqcc - (numComponents < 257 ? 3 : 4);

            info.Add($"QCC marker validated for component {compIdx}");
            return true;
        }

        private bool ValidateRGN(byte[] data, ref int pos, int maxBytes)
        {
            return SkipMarkerSegment(data, ref pos, maxBytes, "RGN");
        }

        private bool ValidatePOC(byte[] data, ref int pos, int maxBytes)
        {
            if (pos + 2 > maxBytes)
            {
                errors.Add("Insufficient data for POC length");
                return false;
            }

            var lpoc = (data[pos] << 8) | data[pos + 1];
            pos += 2;

            if (lpoc < 9)
            {
                errors.Add($"Invalid POC marker length: {lpoc}");
                return false;
            }

            if (pos + lpoc - 2 > maxBytes)
            {
                errors.Add($"Insufficient data for POC marker segment");
                return false;
            }

            // POC entries are variable size depending on number of components
            var entrySize = numComponents < 257 ? 7 : 9;
            var numEntries = (lpoc - 2) / entrySize;

            if ((lpoc - 2) % entrySize != 0)
            {
                errors.Add($"POC marker length {lpoc} is not valid for entry size {entrySize}");
                return false;
            }

            pos += lpoc - 2;
            info.Add($"POC marker validated ({numEntries} progression changes)");
            return true;
        }

        private bool ValidatePPM(byte[] data, ref int pos, int maxBytes)
        {
            return SkipMarkerSegment(data, ref pos, maxBytes, "PPM");
        }

        private bool ValidatePPT(byte[] data, ref int pos, int maxBytes)
        {
            return SkipMarkerSegment(data, ref pos, maxBytes, "PPT");
        }

        private bool ValidateTLM(byte[] data, ref int pos, int maxBytes)
        {
            return SkipMarkerSegment(data, ref pos, maxBytes, "TLM");
        }

        private bool ValidatePLM(byte[] data, ref int pos, int maxBytes)
        {
            return SkipMarkerSegment(data, ref pos, maxBytes, "PLM");
        }

        private bool ValidatePLT(byte[] data, ref int pos, int maxBytes)
        {
            return SkipMarkerSegment(data, ref pos, maxBytes, "PLT");
        }

        private bool ValidateCRG(byte[] data, ref int pos, int maxBytes)
        {
            return SkipMarkerSegment(data, ref pos, maxBytes, "CRG");
        }

        private bool ValidateCOM(byte[] data, ref int pos, int maxBytes)
        {
            if (pos + 2 > maxBytes)
            {
                errors.Add("Insufficient data for COM length");
                return false;
            }

            var lcom = (data[pos] << 8) | data[pos + 1];
            pos += 2;

            if (lcom < 5) // Minimum: length(2) + Rcom(2) + at least 1 byte
            {
                errors.Add($"Invalid COM marker length: {lcom}");
                return false;
            }

            if (pos + lcom - 2 > maxBytes)
            {
                errors.Add($"Insufficient data for COM marker segment");
                return false;
            }

            // Read Rcom (registration value)
            var rcom = (data[pos] << 8) | data[pos + 1];
            pos += 2;

            // Skip comment data
            pos += lcom - 4;

            if (rcom == 1)
            {
                info.Add($"COM marker (Latin-1 text, {lcom - 4} bytes)");
            }
            else if (rcom == 0)
            {
                info.Add($"COM marker (binary data, {lcom - 4} bytes)");
            }
            else
            {
                warnings.Add($"COM marker with non-standard Rcom value: {rcom}");
            }

            return true;
        }

        private bool ValidateSOT(byte[] data, ref int pos, int maxBytes)
        {
            pos += 2; // Skip marker

            if (pos + 2 > maxBytes)
            {
                errors.Add("Insufficient data for SOT length");
                return false;
            }

            var lsot = (data[pos] << 8) | data[pos + 1];
            pos += 2;

            if (lsot != 10)
            {
                errors.Add($"Invalid SOT marker length: {lsot} (must be 10)");
                return false;
            }

            if (pos + 8 > maxBytes)
            {
                errors.Add("Insufficient data for SOT marker segment");
                return false;
            }

            var isot = (data[pos] << 8) | data[pos + 1];
            pos += 2;

            var psot = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;

            var tpsot = data[pos++];
            var tnsot = data[pos++];

            if (tnsot > 0 && tpsot >= tnsot)
            {
                errors.Add($"SOT tile-part index {tpsot} must be less than tile-part count {tnsot}");
                return false;
            }

            info.Add($"SOT marker validated: tile={isot}, length={psot}, part {tpsot} of {tnsot}");
            return true;
        }

        private bool ValidateSOD(byte[] data, ref int pos, int maxBytes)
        {
            if (pos + 1 >= maxBytes)
            {
                errors.Add("Insufficient data for SOD marker");
                return false;
            }

            if (data[pos] != 0xFF || data[pos + 1] != 0x93)
            {
                errors.Add($"Expected SOD marker (0xFF93), found: 0x{data[pos]:X2}{data[pos + 1]:X2}");
                return false;
            }

            pos += 2;
            info.Add("SOD marker validated");
            return true;
        }

        private bool ValidateSOP(byte[] data, ref int pos, int maxBytes)
        {
            pos += 2; // Skip marker

            if (pos + 4 > maxBytes)
            {
                warnings.Add("Insufficient data for SOP marker (truncated)");
                return true; // Don't fail, just warn
            }

            var lsop = (data[pos] << 8) | data[pos + 1];
            pos += 2;

            if (lsop != 4)
            {
                warnings.Add($"Invalid SOP marker length: {lsop} (should be 4)");
            }

            var nsop = (data[pos] << 8) | data[pos + 1];
            pos += 2;

            // SOP packet sequence number
            // Don't validate sequence here (would need to track)

            return true;
        }

        private bool ValidateEPH(byte[] data, ref int pos, int maxBytes)
        {
            if (pos + 1 >= maxBytes)
            {
                warnings.Add("Insufficient data for EPH marker (truncated)");
                return true;
            }

            if (data[pos] != 0xFF || data[pos + 1] != 0x92)
            {
                warnings.Add($"Expected EPH marker (0xFF92), found: 0x{data[pos]:X2}{data[pos + 1]:X2}");
                return true;
            }

            pos += 2;
            return true;
        }

        private bool ValidateEOC(byte[] data, ref int pos, int maxBytes)
        {
            if (pos + 1 >= maxBytes)
            {
                return false;
            }

            if (data[pos] != 0xFF || data[pos + 1] != 0xD9)
            {
                return false;
            }

            pos += 2;
            info.Add("EOC marker validated");
            return true;
        }

        private bool SkipMarkerSegment(byte[] data, ref int pos, int maxBytes, string markerName)
        {
            if (pos + 2 > maxBytes)
            {
                errors.Add($"Insufficient data for {markerName} length");
                return false;
            }

            var length = (data[pos] << 8) | data[pos + 1];
            pos += 2;

            if (length < 2)
            {
                errors.Add($"Invalid {markerName} marker length: {length}");
                return false;
            }

            if (pos + length - 2 > maxBytes)
            {
                errors.Add($"Insufficient data for {markerName} marker segment");
                return false;
            }

            pos += length - 2;
            info.Add($"{markerName} marker validated ({length} bytes)");
            return true;
        }

        /// <summary>
        /// Attempts to skip an unknown marker by reading its length field.
        /// Returns false if the marker cannot be skipped safely.
        /// </summary>
        private bool TrySkipUnknownMarker(byte[] data, ref int pos, int maxBytes)
        {
            try
            {
                // Check if we have room for a length field
                if (pos + 2 > maxBytes)
                {
                    errors.Add($"Cannot read length of unknown marker at position {pos - 2} (insufficient data)");
                    return false;
                }

                var length = (data[pos] << 8) | data[pos + 1];
                
                if (length < 2)
                {
                    errors.Add($"Invalid marker segment length: {length}");
                    return false;
                }

                if (pos + length > maxBytes)
                {
                    warnings.Add($"Marker segment extends beyond available data (need {length} bytes, have {maxBytes - pos})");
                    pos = maxBytes; // Skip to end
                    return true; // Return true to allow continuation
                }

                pos += length;
                info.Add($"Skipped unknown marker ({length} bytes)");
                return true;
            }
            catch (Exception)
            {
                errors.Add($"Failed to skip unknown marker at position {pos - 2}");
                return false;
            }
        }

        /// <summary>
        /// Gets a formatted validation report.
        /// </summary>
        public string GetValidationReport()
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("=== Codestream Validation Report ===");
            report.AppendLine();

            if (errors.Count == 0 && warnings.Count == 0)
            {
                report.AppendLine("? Codestream is valid (ISO/IEC 15444-1 Annex A compliant)");
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
                    report.AppendLine();
                }
            }

            if (info.Count > 0)
            {
                report.AppendLine($"INFORMATION ({info.Count}):");
                foreach (var infoMsg in info)
                {
                    report.AppendLine($"  ? {infoMsg}");
                }
            }

            return report.ToString();
        }
    }
}
