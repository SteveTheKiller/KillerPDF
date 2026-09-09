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
using CoreJ2K.j2k.decoder;
using CoreJ2K.j2k.entropy;
using CoreJ2K.j2k.entropy.decoder;
using CoreJ2K.j2k.io;
using CoreJ2K.j2k.quantization.dequantizer;
using CoreJ2K.j2k.util;
using CoreJ2K.j2k.wavelet.synthesis;
using System;
using System.Collections.Generic;

namespace CoreJ2K.j2k.codestream.reader
{

    /// <summary> This class reads the bit stream (with the help of HeaderDecoder for tile
    /// headers and PktDecoder for packets header and body) and retrieves location
    /// of all code-block's codewords.
    /// 
    /// Note: All tile-parts headers are read by the constructor whereas packets
    /// are processed when decoding related tile (when SetTile method is
    /// called).
    /// 
    /// In parsing mode, the reader simulates a virtual layer-resolution
    /// progressive bit stream with the same truncation points in each code-block,
    /// whereas in truncation mode, only the first bytes are taken into account (it
    /// behaves like if it is a real truncated codestream).
    /// 
    /// </summary>
    /// <seealso cref="HeaderDecoder" />
    /// <seealso cref="PktDecoder" />
    public class FileBitstreamReaderAgent : BitstreamReaderAgent
    {
        /// <summary>Gets the reference to the CBlkInfo array </summary>
        public virtual CBlkInfo[][][][][] CBlkInfo => cbI;

        /// <summary>Whether the last read Psot value was zero. Only the Psot in the
        /// last tile-part in the codestream can have such a value. 
        /// </summary>
        private bool isPsotEqualsZero = true;

        /// <summary>Reference to the PktDecoder instance </summary>
        public PktDecoder pktDec;

        /// <summary>Reference to the ParameterList instance </summary>
        private ParameterList pl;

        /// <summary>The RandomAccessIO where to get data from </summary>
        private readonly RandomAccessIO inStream;

        /// <summary>Offset of the first packet in each tile-part in each tile </summary>
        private readonly int[][] firstPackOff;

        /// <summary> Returns the number of tile-part found for a given tile
        /// 
        /// </summary>
        /// <param name="t">Tile index
        /// 
        /// 
        /// </param>
        public virtual int GetNumTileParts(int t)
        {
            return firstPackOff?[t] == null 
                ? throw new InvalidOperationException($"Tile {t} not found in input codestream.") 
                : firstPackOff[t].Length;
        }

        /// <summary> Number of bytes allocated to each tile. In parsing mode, this number
        /// is related to the tile length in the codestream whereas in truncation
        /// mode all the rate is affected to the first tiles. 
        /// </summary>
        private readonly int[] nBytes;

        /// <summary>Whether to print information found in codestream </summary>
        private readonly bool printInfo = false;

        /// <summary> Backup of the number of bytes allocated to each tile. This array is
        /// used to restore the number of bytes to read in each tile when the
        /// codestream is read several times (for instance when decoding an R,G,B
        /// image to three output files)
        /// 
        /// </summary>
        private readonly int[] baknBytes;

        /// <summary>Length of each tile-part (written in Psot) </summary>
        private readonly int[][] tilePartLen;

        /// <summary>Total length of each tile </summary>
        private readonly int[] totTileLen;

        /// <summary>Total length of tiles' header </summary>
        private readonly int[] totTileHeadLen;

        /// <summary>First tile part header length</summary>
        private int firstTilePartHeadLen;

        /// <summary>Total length of all tile parts in all tiles </summary>
        private readonly double totAllTileLen;

        /// <summary>Length of main header </summary>
        private readonly int mainHeadLen;

        /// <summary>Length of main and tile-parts headers </summary>
        private readonly int headLen = 0;

        /// <summary>Length of all tile-part headers </summary>
        private readonly int[][] tilePartHeadLen;

        /// <summary>Length of each packet head found in the tile </summary>
        private List<int> pktHL;

        /// <summary>True if truncation mode is used. False if parsing mode </summary>
        private readonly bool isTruncMode;

        /// <summary>The number of tile-parts that remain to read </summary>
        private int remainingTileParts;

        /// <summary>The number of tile-parts read so far for each tile </summary>
        private readonly int[] tilePartsRead;

        /// <summary>The total number of tile-parts read so far </summary>
        private int totTilePartsRead = 0;

        /// <summary>The number of found tile-parts in each tile. </summary>
        private readonly int[] tileParts;

        /// <summary>The current tile part being used </summary>
        private int curTilePart;

        /// <summary>The number of the tile-parts found in the codestream after reading the
        /// tp'th tile-part of tile t 
        /// </summary>
        private readonly int[][] tilePartNum;

        /// <summary>Whether a EOC marker has been found instead of a SOT </summary>
        private bool isEOCFound = false;

        /// <summary>Reference to the HeaderInfo instance (used when reading SOT marker
        /// segments) 
        /// </summary>
        private readonly HeaderInfo hi;

        /// <summary>Array containing information for all the code-blocks:
        /// 
        /// <ul>
        /// <li>1st dim: component index.</li>
        /// <li>2nd dim: resolution level index.</li>
        /// <li>3rd dim: subband index.</li>
        /// <li>4th/5th dim: code-block index (vert. and horiz.).</li>
        /// </ul>
        /// </summary>
        private CBlkInfo[][][][][] cbI;

        /// <summary>The maximum number of layers to decode for any code-block </summary>
        private readonly int lQuit;

        /// <summary>Whether to use only first progression order </summary>
        private readonly bool usePOCQuit = false;

        /// <summary>
        /// Checks if fast random tile access is available via TLM markers.
        /// TLM markers contain tile-part lengths which enable O(1) seeking to any tile
        /// without parsing intermediate tiles.
        /// </summary>
        /// <returns>True if TLM data is available for fast seeking</returns>
        public virtual bool SupportsFastTileAccess()
        {
            var tlmData = hd.GetTLMData();
            return tlmData != null && tlmData.HasTilePartLengths;
        }

        /// <summary>
        /// Calculates the file offset for a specific tile using TLM data.
        /// This enables fast random tile access without sequential parsing.
        /// </summary>
        /// <param name="tileIdx">The tile index (linear, row-major order)</param>
        /// <returns>File offset in bytes, or -1 if TLM data unavailable or tile index invalid</returns>
        private long GetTileOffsetFromTLM(int tileIdx)
        {
            // Get TLM data from HeaderDecoder
            var tlmData = hd.GetTLMData();

            if (tlmData == null || !tlmData.HasTilePartLengths)
            {
                return -1; // TLM not available, use slow path
            }

            if (tileIdx < 0 || tileIdx > tlmData.MaxTileIndex)
            {
                return -1; // Invalid tile index
            }

            // Calculate offset: main header + sum of all previous tiles
            long offset = mainHeadLen;

            for (var t = 0; t < tileIdx; t++)
            {
                offset += tlmData.GetTotalTileLength(t);
            }

            return offset;
        }

        /// <summary>
        /// Seeks directly to a specific tile without parsing intermediate tiles.
        /// Only available when TLM markers are present in the codestream.
        /// Falls back to sequential tile navigation if TLM data is unavailable.
        /// </summary>
        /// <param name="tileIndex">The linear tile index (row-major order: tileIndex = y * numTilesX + x)</param>
        /// <returns>True if fast seek via TLM succeeded, false if sequential parsing was required</returns>
        /// <exception cref="ArgumentOutOfRangeException">If tileIndex is out of range</exception>
        public virtual bool SeekToTile(int tileIndex)
        {
            if (tileIndex < 0 || tileIndex >= nt)
            {
                throw new ArgumentOutOfRangeException(nameof(tileIndex),
                    $"Tile index must be between 0 and {nt - 1}");
            }

            // Convert linear index to x,y coordinates
            var x = tileIndex % ntX;
            var y = tileIndex / ntX;

            // Try TLM fast path first
            var tlmOffset = GetTileOffsetFromTLM(tileIndex);
            var usedFastPath = tlmOffset >= 0;

            // Use SetTile which has TLM fast path integrated
            SetTile(x, y);

            return usedFastPath;
        }

        /// <summary> Reads all tiles headers and keep offset of their first
        /// packet. Finally, it calls the rate allocation method.
        /// 
        /// </summary>
        /// <param name="hd">HeaderDecoder of the codestream.
        /// 
        /// </param>
        /// <param name="ehs">The input stream where to read bit-stream.
        /// 
        /// </param>
        /// <param name="decSpec">The decoder specifications 
        /// 
        /// </param>
        /// <param name="pl">The ParameterList instance created from the
        /// command-line arguments.
        /// 
        /// </param>
        /// <param name="cdstrInfo">Whether to print information found in
        /// codestream. 
        /// 
        /// </param>
        /// <seealso cref="allocateRate" />
        public FileBitstreamReaderAgent(HeaderDecoder hd, RandomAccessIO ehs, DecoderSpecs decSpec, ParameterList pl, bool cdstrInfo, HeaderInfo hi) : base(hd, decSpec)
        {

            this.pl = pl;
            printInfo = cdstrInfo;
            this.hi = hi;
            string strInfo = null;
            if (printInfo)
            {
                strInfo = "Codestream elements information in bytes " + "(offset, total length, header length):\n\n";
            }

            // Check whether quit conditions used
            usePOCQuit = pl.GetBooleanParameter("poc_quit");

            // Get decoding rate
            bool rateInBytes;
            var parsing = pl.GetBooleanParameter("parsing");
            try
            {
                trate = pl.GetFloatParameter("rate");
                if (trate == -1)
                {
                    trate = float.MaxValue;
                }
            }
            catch (FormatException)
            {
                throw new InvalidOperationException($"Invalid value in 'rate' option: {pl.GetParameter("rate")}");
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException("'rate' option is missing");
            }

            try
            {
                tnbytes = pl.GetIntParameter("nbytes");
            }
            catch (FormatException)
            {
                throw new InvalidOperationException($"Invalid value in 'nbytes' option: {pl.GetParameter("nbytes")}");
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException("'nbytes' option is missing");
            }

            // Check that '-rate' and '-nbytes' are not used at the same time
            var defaults = pl.DefaultParameterList;
            rateInBytes = tnbytes != defaults.GetFloatParameter("nbytes");

            // Validate HeaderDecoder has been properly initialized with SIZ marker
            if (hd == null)
            {
                throw new ArgumentNullException(nameof(hd), "HeaderDecoder cannot be null");
            }
            
            if (hi == null || hi.sizValue == null)
            {
                throw new CorruptedCodestreamException(
                    "Header information not available. SIZ marker may not have been properly parsed.");
            }
            
            // Validate image dimensions are valid
            if (hd.MaxCompImgWidth <= 0 || hd.MaxCompImgHeight <= 0)
            {
                throw new CorruptedCodestreamException(
                    $"Invalid image dimensions: width={hd.MaxCompImgWidth}, height={hd.MaxCompImgHeight}. " +
                    "Image dimensions must be positive.");
            }

            if (rateInBytes)
            {
                trate = tnbytes * 8f / hd.MaxCompImgWidth / hd.MaxCompImgHeight;
            }
            else
            {
                tnbytes = (int)(trate * hd.MaxCompImgWidth * hd.MaxCompImgHeight) / 8;
                if (tnbytes < 0) tnbytes = int.MaxValue;
            }
            isTruncMode = !pl.GetBooleanParameter("parsing");

            // Check if quit conditions are being used
            int ncbQuit;
            try
            {
                ncbQuit = pl.GetIntParameter("ncb_quit");
            }
            catch (FormatException)
            {
                throw new InvalidOperationException(
                    $"Invalid value in 'ncb_quit' option: {pl.GetParameter("ncb_quit")}");
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException("'ncb_quit' option is missing");
            }
            if (ncbQuit != -1 && !isTruncMode)
            {
                throw new InvalidOperationException("Cannot use -parsing and -ncb_quit condition at " + "the same time.");
            }

            try
            {
                lQuit = pl.GetIntParameter("l_quit");
            }
            catch (FormatException)
            {
                throw new InvalidOperationException($"Invalid value in 'l_quit' option: {pl.GetParameter("l_quit")}");
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException("'l_quit' option is missing");
            }

            // initializations
            inStream = ehs;
            pktDec = new PktDecoder(decSpec, hd, ehs, this, isTruncMode, ncbQuit);

            tileParts = new int[nt];
            totTileLen = new int[nt];
            tilePartLen = new int[nt][];
            tilePartNum = new int[nt][];
            firstPackOff = new int[nt][];
            tilePartsRead = new int[nt];
            totTileHeadLen = new int[nt];
            tilePartHeadLen = new int[nt][];
            nBytes = new int[nt];
            baknBytes = new int[nt];
            hd.nTileParts = new int[nt];

            // CONVERSION PROBLEM?
            //this.isTruncMode = isTruncMode;
            int t = 0, pos, tp = 0, tptot = 0;

            // Keeps main header's length, takes file format overhead into account
            var cdstreamStart = hd.mainHeadOff; // Codestream offset in the file
            mainHeadLen = inStream.Pos - cdstreamStart;
            headLen = mainHeadLen;

            // If ncb and lbody quit conditions are used, headers are not counted
            anbytes = ncbQuit == -1 ? mainHeadLen : 0;

            if (printInfo) strInfo += ("Main header length    : " + cdstreamStart + ", " + mainHeadLen + ", " + mainHeadLen + "\n");

            // If cannot even read the first tile-part
            if (anbytes > tnbytes)
            {
                throw new InvalidOperationException("Requested bitrate is too small.");
            }

            // Read all tile-part headers from all tiles.
            int tilePartStart;
            var rateReached = false;
            int mdl;
            //int numtp = 0;
            totAllTileLen = 0;
            remainingTileParts = nt; // at least as many tile-parts as tiles
            var maxTP = nt; // If maximum 1 tile part per tile specified

            try
            {
                while (remainingTileParts != 0)
                {

                    tilePartStart = inStream.Pos;
                    // Read tile-part header
                    try
                    {
                        t = readTilePartHeader();
                        if (isEOCFound)
                        {
                            // Some tiles are missing but the
                            // codestream is OK
                            break;
                        }
                        tp = tilePartsRead[t];
                        if (isPsotEqualsZero)
                        {
                            // Psot may equals zero for the
                            // last tile-part: it is assumed that this tile-part
                            // contain all data until EOC
                            tilePartLen[t][tp] = inStream.length() - 2 - tilePartStart;
                        }
                    }
                    catch (System.IO.EndOfStreamException)
                    {
                        firstPackOff[t][tp] = inStream.length();
                        throw;
                    }

                    pos = inStream.Pos;

                    // In truncation mode, if target decoding rate is reached in
                    // tile-part header, skips the tile-part and stop reading
                    // unless the ncb and lbody quit condition is in use
                    if (isTruncMode && ncbQuit == -1)
                    {
                        if ((pos - cdstreamStart) > tnbytes)
                        {
                            firstPackOff[t][tp] = inStream.length();
                            rateReached = true;
                            break;
                        }
                    }

                    // Set tile part position and header length
                    firstPackOff[t][tp] = pos;
                    tilePartHeadLen[t][tp] = (pos - tilePartStart);

                    if (printInfo) strInfo += ("Tile-part " + tp + " of tile " + t + " : " + tilePartStart + ", " + tilePartLen[t][tp] + ", " + tilePartHeadLen[t][tp] + "\n");

                    // Update length counters
                    totTileLen[t] += tilePartLen[t][tp];
                    totTileHeadLen[t] += tilePartHeadLen[t][tp];
                    totAllTileLen += tilePartLen[t][tp];
                    if (isTruncMode)
                    {
                        if (anbytes + tilePartLen[t][tp] > tnbytes)
                        {
                            anbytes += tilePartHeadLen[t][tp];
                            headLen += tilePartHeadLen[t][tp];
                            rateReached = true;
                            nBytes[t] += (tnbytes - anbytes);
                            break;
                        }
                        else
                        {
                            anbytes += tilePartHeadLen[t][tp];
                            headLen += tilePartHeadLen[t][tp];
                            nBytes[t] += (tilePartLen[t][tp] - tilePartHeadLen[t][tp]);
                        }
                    }
                    else
                    {
                        if (anbytes + tilePartHeadLen[t][tp] > tnbytes)
                        {
                            break;
                        }
                        else
                        {
                            anbytes += tilePartHeadLen[t][tp];
                            headLen += tilePartHeadLen[t][tp];
                        }
                    }

                    // If this is first tile-part, remember header length
                    if (tptot == 0)
                        firstTilePartHeadLen = tilePartHeadLen[t][tp];

                    // Go to the beginning of next tile part
                    tilePartsRead[t]++;
                    inStream.seek(tilePartStart + tilePartLen[t][tp]);
                    remainingTileParts--;
                    maxTP--;
                    tptot++;

                    // If Psot of the current tile-part was equal to zero, it is
                    // assumed that it contains all data until the EOC marker
                    if (isPsotEqualsZero)
                    {
                        if (remainingTileParts != 0)
                        {
                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING, "Some tile-parts have not " + "been found. The codestream may be corrupted.");
                        }
                        break;
                    }
                }
            }
            catch (System.IO.EndOfStreamException)
            {
                if (printInfo)
                {
                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
                }
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, $"Codestream truncated in tile {t}");

                // Set specified rate to end of file if valid
                var fileLen = inStream.length();
                if (fileLen < tnbytes)
                {
                    tnbytes = fileLen;
                    trate = tnbytes * 8f / hd.MaxCompImgWidth / hd.MaxCompImgHeight;
                }

                // Bit-rate allocation
                if (!isTruncMode)
                {
                    allocateRate();
                }

                // Update 'res' value once all tile-part headers are read
                if (pl.GetParameter("res") == null)
                {
                    targetRes = decSpec.dls.Min;
                }
                else
                {
                    try
                    {
                        targetRes = pl.GetIntParameter("res");
                        if (targetRes < 0)
                        {
                            throw new ArgumentException($"Specified negative resolution level index: {targetRes}");
                        }
                    }
                    catch (FormatException)
                    {
                        throw new ArgumentException(
                            $"Invalid resolution level index ('-res' option) {pl.GetParameter("res")}");
                    }
                }

                // Verify reduction in resolution level
                mdl = decSpec.dls.Min;
                if (targetRes > mdl)
                {
                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                        $"Specified resolution level ({targetRes}) is larger than the maximum value. Setting it to {mdl} (maximum value)");
                    targetRes = mdl;
                }

                // Backup nBytes
                for (var tIdx = 0; tIdx < nt; tIdx++)
                {
                    baknBytes[tIdx] = nBytes[tIdx];
                }

                return;
            }
            remainingTileParts = 0;

            // Update 'res' value once all tile-part headers are read
            if (pl.GetParameter("res") == null)
            {
                targetRes = decSpec.dls.Min;
            }
            else
            {
                try
                {
                    targetRes = pl.GetIntParameter("res");
                    if (targetRes < 0)
                    {
                        throw new ArgumentException($"Specified negative resolution level index: {targetRes}");
                    }
                }
                catch (FormatException)
                {
                    throw new ArgumentException(
                        $"Invalid resolution level index ('-res' option) {pl.GetParameter("res")}");
                }
            }

            // Verify reduction in resolution level
            mdl = decSpec.dls.Min;
            if (targetRes > mdl)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Specified resolution level ({targetRes}) is larger than the maximum possible. Setting it to {mdl} (maximum possible)");
                targetRes = mdl;
            }

            if (printInfo)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
            }

            // Check presence of EOC marker is decoding rate not reached or if
            // this marker has not been found yet
            if (!isEOCFound && !isPsotEqualsZero)
            {
                try
                {
                    if (!rateReached && !isPsotEqualsZero && inStream.readShort() != Markers.EOC)
                    {
                        FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING, "EOC marker not found. " + "Codestream is corrupted.");
                    }
                }
                catch (System.IO.EndOfStreamException)
                {
                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING, "EOC marker is missing");
                }
            }

            // Bit-rate allocation
            if (!isTruncMode)
            {
                allocateRate();
            }
            else
            {
                // Take EOC into account if rate is not reached
                if (inStream.Pos >= tnbytes)
                    anbytes += 2;
            }

            // Backup nBytes
            for (var tIdx = 0; tIdx < nt; tIdx++)
            {
                baknBytes[tIdx] = nBytes[tIdx];
                if (printInfo)
                {
                    FacilityManager.GetMsgLogger().println($"{hi.toStringTileHeader(tIdx, tilePartLen[tIdx].Length)}", 2, 2);
                }
            }
        }

        /// <summary> Allocates output bit-rate for each tile in parsing mode: The allocator
        /// simulates the truncation of a virtual layer-resolution progressive
        /// codestream.
        /// 
        /// </summary>
        private void allocateRate()
        {
            var stopOff = tnbytes;

            // In parsing mode, the bitrate is allocated related to each tile's
            // length in the bit stream

            // EOC marker's length 
            anbytes += 2;

            // If there are too few bytes to read the tile part headers throw an
            // error
            if (anbytes > stopOff)
            {
                throw new InvalidOperationException("Requested bitrate is too small for parsing");
            }

            // Validate arrays are initialized
            if (nBytes == null || totTileLen == null)
            {
                throw new InvalidOperationException(
                    "Tile information not initialized. Internal error in bitstream reader.");
            }
            
            // Validate tile count and array sizes
            if (nt <= 0)
            {
                throw new CorruptedCodestreamException(
                    $"Invalid number of tiles: {nt}. Must be at least 1.");
            }
            
            if (nBytes.Length < nt)
            {
                throw new CorruptedCodestreamException(
                    $"Tile byte allocation array too small: has {nBytes.Length} elements but need {nt}. " +
                    "Codestream structure may be corrupted.");
            }
            
            if (totTileLen.Length < nt)
            {
                throw new CorruptedCodestreamException(
                    $"Tile length array too small: has {totTileLen.Length} elements but need {nt}. " +
                    "Codestream structure may be corrupted.");
            }

            // Calculate bitrate for each tile
            var rem = stopOff - anbytes;
            var totnByte = rem;
            for (var t = nt - 1; t > 0; t--)
            {
                rem -= (nBytes[t] = (int)(totnByte * (totTileLen[t] / totAllTileLen)));
            }
            nBytes[0] = rem;
        }

        /// <summary> Reads SOT marker segment of the tile-part header and calls related
        /// methods of the HeaderDecoder to read other markers segments. The
        /// tile-part header is entirely read when a SOD marker is encountered.
        /// 
        /// </summary>
        /// <returns> The tile number of the tile part that was read
        /// 
        /// </returns>
        private int readTilePartHeader()
        {
            var ms = hi.NewSOT;

            // SOT marker
            var marker = inStream.readShort();
            if (marker != Markers.SOT)
            {
                if (marker == Markers.EOC)
                {
                    isEOCFound = true;
                    return -1;
                }
                else
                {
                    throw new CorruptedCodestreamException("SOT tag not found " + "in tile-part start");
                }
            }
            isEOCFound = false;

            // Lsot (shall equals 10)
            var lsot = inStream.readUnsignedShort();
            ms.lsot = lsot;
            if (lsot != 10)
                throw new CorruptedCodestreamException($"Wrong length for SOT marker segment: {lsot}");

            // Isot
            var tile = inStream.readUnsignedShort();
            ms.isot = tile;
            if (tile > 65534)
            {
                throw new CorruptedCodestreamException("Tile index too high in " + "tile-part.");
            }

            // Psot (tile-part length)
            // This is a 32-bit unsigned value, but we read it as signed int
            var psot = inStream.readInt();
            ms.psot = psot;
            isPsotEqualsZero = (psot == 0);
            
            // Validate tile-part length
            if (psot < 0)
            {
                // When read as signed int, negative means value > int.MaxValue (2GB)
                // JPEG 2000 spec allows up to 4GB, but we limit to int.MaxValue for practical reasons
                throw new CorruptedCodestreamException(
                    $"Tile-part length exceeds maximum supported size. " +
                    $"Psot value indicates length > {int.MaxValue} bytes (2GB). " +
                    "This may indicate a corrupted codestream or maliciously crafted file.");
            }
            
            // Additional sanity check for unreasonably large tile-parts
            const int maxReasonableTilePartSize = 100 * 1024 * 1024; // 100MB
            if (psot > maxReasonableTilePartSize && psot != 0)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Very large tile-part detected: {psot} bytes ({psot / (1024.0 * 1024.0):F2} MB). " +
                    "This may indicate a corrupted file or cause memory issues.");
            }
            // TPsot
            int tilePart = inStream.read();
            ms.tpsot = tilePart;
            if (tilePart != tilePartsRead[tile] || tilePart < 0 || tilePart > 254)
            {
                throw new CorruptedCodestreamException("Out of order tile-part");
            }
            // TNsot
            int nrOfTileParts = inStream.read();
            ms.tnsot = nrOfTileParts;
            hi.sotValue[$"t{tile}_tp{tilePart}"] = ms;
            if (nrOfTileParts == 0)
            {
                // The number of tile-part is not specified in
                // this tile-part header.

                // Assumes that there will be another tile-part in the codestream
                // that will indicate the number of tile-parts for this tile)
                int nExtraTp;
                if (tileParts[tile] == 0 || tileParts[tile] == tilePartLen.Length)
                {
                    // Then there are two tile-parts (one is the current and the
                    // other will indicate the number of tile-part for this tile)
                    nExtraTp = 2;
                    remainingTileParts += 1;
                }
                else
                {
                    // There is already one scheduled extra tile-part. In this
                    // case just add place for the current one
                    nExtraTp = 1;
                }

                tileParts[tile] += nExtraTp;
                nrOfTileParts = tileParts[tile];
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                    $"Header of tile-part {tilePart} of tile {tile}, does not indicate the total number of tile-parts. Assuming that there are {nrOfTileParts} tile-parts for this tile.");

                // Increase and re-copy tilePartLen array
                var tmpA = tilePartLen[tile];
                tilePartLen[tile] = new int[nrOfTileParts];
                for (var i = 0; i < nrOfTileParts - nExtraTp; i++)
                {
                    // Validate array bounds to prevent IndexOutOfRangeException
                    if (tmpA != null && i < tmpA.Length)
                    {
                        tilePartLen[tile][i] = tmpA[i];
                    }
                }

                // Increase and re-copy tilePartNum array
                tmpA = tilePartNum[tile];
                tilePartNum[tile] = new int[nrOfTileParts];
                for (var i = 0; i < nrOfTileParts - nExtraTp; i++)
                {
                    // Validate array bounds to prevent IndexOutOfRangeException
                    if (tmpA != null && i < tmpA.Length)
                    {
                        tilePartNum[tile][i] = tmpA[i];
                    }
                }

                // Increase and re-copy firsPackOff array
                tmpA = firstPackOff[tile];
                firstPackOff[tile] = new int[nrOfTileParts];
                for (var i = 0; i < nrOfTileParts - nExtraTp; i++)
                {
                    // Validate array bounds to prevent IndexOutOfRangeException
                    if (tmpA != null && i < tmpA.Length)
                    {
                        firstPackOff[tile][i] = tmpA[i];
                    }
                }

                // Increase and re-copy tilePartHeadLen array
                tmpA = tilePartHeadLen[tile];
                tilePartHeadLen[tile] = new int[nrOfTileParts];
                for (var i = 0; i < nrOfTileParts - nExtraTp; i++)
                {
                    // Validate array bounds to prevent IndexOutOfRangeException
                    if (tmpA != null && i < tmpA.Length)
                    {
                        tilePartHeadLen[tile][i] = tmpA[i];
                    }
                }
            }
            else
            {
                // The number of tile-parts is specified in the tile-part
                // header

                // Check if it is consistant with what was found in previous
                // tile-part headers

                if (tileParts[tile] == 0)
                {
                    // First tile-part: OK
                    remainingTileParts += nrOfTileParts - 1;
                    tileParts[tile] = nrOfTileParts;
                    tilePartLen[tile] = new int[nrOfTileParts];
                    tilePartNum[tile] = new int[nrOfTileParts];
                    firstPackOff[tile] = new int[nrOfTileParts];
                    tilePartHeadLen[tile] = new int[nrOfTileParts];
                }
                else if (tileParts[tile] > nrOfTileParts)
                {
                    // Already found more tile-parts than signaled here
                    throw new CorruptedCodestreamException(
                        $"Invalid number of tile-parts in tile {tile}: {nrOfTileParts}");
                }
                else
                {
                    // Signaled number of tile-part fits with number of
                    // previously found tile-parts
                    remainingTileParts += nrOfTileParts - tileParts[tile];

                    if (tileParts[tile] != nrOfTileParts)
                    {

                        // Increase and re-copy tilePartLen array
                        var tmpA = tilePartLen[tile];
                        tilePartLen[tile] = new int[nrOfTileParts];
                        var copyLen = Math.Min(tileParts[tile] - 1, tmpA != null ? tmpA.Length : 0);
                        for (var i = 0; i < copyLen; i++)
                        {
                            tilePartLen[tile][i] = tmpA[i];
                        }

                        // Increase and re-copy tilePartNum array                
                        tmpA = tilePartNum[tile];
                        tilePartNum[tile] = new int[nrOfTileParts];
                        copyLen = Math.Min(tileParts[tile] - 1, tmpA != null ? tmpA.Length : 0);
                        for (var i = 0; i < copyLen; i++)
                        {
                            tilePartNum[tile][i] = tmpA[i];
                        }

                        // Increase and re-copy firstPackOff array
                        tmpA = firstPackOff[tile];
                        firstPackOff[tile] = new int[nrOfTileParts];
                        copyLen = Math.Min(tileParts[tile] - 1, tmpA != null ? tmpA.Length : 0);
                        for (var i = 0; i < copyLen; i++)
                        {
                            firstPackOff[tile][i] = tmpA[i];
                        }

                        // Increase and re-copy tilePartHeadLen array
                        tmpA = tilePartHeadLen[tile];
                        tilePartHeadLen[tile] = new int[nrOfTileParts];
                        copyLen = Math.Min(tileParts[tile] - 1, tmpA != null ? tmpA.Length : 0);
                        for (var i = 0; i < copyLen; i++)
                        {
                            tilePartHeadLen[tile][i] = tmpA[i];
                        }
                    }
                }
            }

            // Other markers
            hd.resetHeaderMarkers();
            hd.nTileParts[tile] = nrOfTileParts;
            // Decode and store the tile-part header (i.e. until a SOD marker is
            // found)
            do
            {
                hd.extractTilePartMarkSeg(inStream.readShort(), inStream, tile, tilePart);
            }
            while ((hd.NumFoundMarkSeg & HeaderDecoder.SOD_FOUND) == 0);

            // Read each marker segment previously found
            hd.readFoundTilePartMarkSeg(tile, tilePart);

            tilePartLen[tile][tilePart] = psot;

            tilePartNum[tile][tilePart] = totTilePartsRead;
            totTilePartsRead++;

            // Add to list of which tile each successive tile-part belongs.
            // This list is needed if there are PPM markers used
            hd.TileOfTileParts = tile;

            return tile;
        }

        /// <summary> Reads packets of the current tile according to the
        /// layer-resolution-component-position progressiveness.
        /// 
        /// </summary>
        /// <param name="lys">Index of the first layer for each component and resolution.
        /// 
        /// </param>
        /// <param name="lye">Index of the last layer.
        /// 
        /// </param>
        /// <param name="ress">Index of the first resolution level.
        /// 
        /// </param>
        /// <param name="rese">Index of the last resolution level.
        /// 
        /// </param>
        /// <param name="comps">Index of the first component.
        /// 
        /// </param>
        /// <param name="compe">Index of the last component.
        /// 
        /// </param>
        /// <returns> True if rate has been reached.
        /// 
        /// </returns>
        private bool readLyResCompPos(int[][] lys, int lye, int ress, int rese, int comps, int compe)
        {

            var minlys = 10000;
            for (var c = comps; c < compe; c++)
            {
                //loop on components
                // Check if this component exists
                if (c >= mdl.Length)
                    continue;

                for (var r = ress; r < rese; r++)
                {
                    //loop on resolution levels
                    if (lys[c] != null && r < lys[c].Length && lys[c][r] < minlys)
                    {
                        minlys = lys[c][r];
                    }
                }
            }

            var t = TileIdx;
            int start;
            var status = false;
            var lastByte = firstPackOff[t][curTilePart] + tilePartLen[t][curTilePart] - 1 - tilePartHeadLen[t][curTilePart];
            var numLayers = ((int)decSpec.nls.GetTileDef(t));
            var nPrec = 1;
            int hlen, plen;
            string strInfo = printInfo ? $"Tile {TileIdx} (tile-part:{curTilePart}): offset, length, header length\n" : null;
            var pph = decSpec.pphs.GetBoolTileDef(t);
            for (var l = minlys; l < lye; l++)
            {
                // loop on layers
                for (var r = ress; r < rese; r++)
                {
                    // loop on resolution levels
                    for (var c = comps; c < compe; c++)
                    {
                        // loop on components
                        // Checks if component exists
                        if (c >= mdl.Length)
                            continue;
                        // Checks if resolution level exists
                        if (r >= lys[c].Length)
                            continue;
                        if (r > mdl[c])
                            continue;
                        // Checks if layer exists
                        if (l < lys[c][r] || l >= numLayers)
                            continue;

                        nPrec = pktDec.GetNumPrecinct(c, r);
                        for (var p = 0; p < nPrec; p++)
                        {
                            // loop on precincts
                            start = inStream.Pos;

                            // If packed packet headers are used, there is no need
                            // to check that there are bytes enough to read header
                            if (pph)
                            {
                                pktDec.readPktHead(l, r, c, p, cbI[c][r], nBytes);
                            }

                            // If we are about to read outside of tile-part,
                            // skip to next tile-part
                            if (start > lastByte && curTilePart < firstPackOff[t].Length - 1)
                            {
                                curTilePart++;
                                inStream.seek(firstPackOff[t][curTilePart]);
                                lastByte = inStream.Pos + tilePartLen[t][curTilePart] - 1 - tilePartHeadLen[t][curTilePart];
                            }

                            // Read SOP marker segment if necessary
                            status = pktDec.readSOPMarker(nBytes, p, c, r);

                            if (status)
                            {
                                if (printInfo)
                                {
                                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
                                }
                                return true;
                            }

                            if (!pph)
                            {
                                status = pktDec.readPktHead(l, r, c, p, cbI[c][r], nBytes);
                            }

                            if (status)
                            {
                                if (printInfo)
                                {
                                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
                                }
                                return true;
                            }

                            // Store packet's head length
                            hlen = inStream.Pos - start;
                            pktHL.Add(hlen);

                            // Reads packet's body
                            status = pktDec.readPktBody(l, r, c, p, cbI[c][r], nBytes);
                                                plen = inStream.Pos - start;
                            if (printInfo) strInfo += (" Pkt l=" + l + ",r=" + r + ",c=" + c + ",p=" + p + ": " + start + ", " + plen + ", " + hlen + "\n");

                            if (status)
                            {
                                if (printInfo)
                                {
                                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
                                }
                                return true;
                            }
                        } // end loop on precincts
                    } // end loop on components
                } // end loop on resolution levels
            } // end loop on layers

            if (printInfo)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
            }
            return false; // Decoding rate was not reached
        }

        /// <summary> Reads packets of the current tile according to the
        /// resolution-layer-component-position progressiveness.
        /// 
        /// </summary>
        /// <param name="lys">Index of the first layer for each component and resolution.
        /// 
        /// </param>
        /// <param name="lye">Index of the last layer.
        /// 
        /// </param>
        /// <param name="ress">Index of the first resolution level.
        /// 
        /// </param>
        /// <param name="rese">Index of the last resolution level.
        /// 
        /// </param>
        /// <param name="comps">Index of the first component.
        /// 
        /// </param>
        /// <param name="compe">Index of the last component.
        /// 
        /// </param>
        /// <returns> True if rate has been reached.
        /// 
        /// </returns>
        private bool readResLyCompPos(int[][] lys, int lye, int ress, int rese, int comps, int compe)
        {

            var t = TileIdx; // Current tile index
            var status = false; // True if decoding rate is reached when
            var lastByte = firstPackOff[t][curTilePart] + tilePartLen[t][curTilePart] - 1 - tilePartHeadLen[t][curTilePart];
            var minlys = 10000;
            for (var c = comps; c < compe; c++)
            {
                //loop on components
                // Check if this component exists
                if (c >= mdl.Length)
                    continue;

                for (var r = ress; r < rese; r++)
                {
                    //loop on resolution levels
                    if (r > mdl[c])
                        continue;
                    if (lys[c] != null && r < lys[c].Length && lys[c][r] < minlys)
                    {
                        minlys = lys[c][r];
                    }
                }
            }

            string strInfo = printInfo ? $"Tile {TileIdx} (tile-part:{curTilePart}): offset, length, header length\n" : null;
            var numLayers = ((int)decSpec.nls.GetTileDef(t));
            var pph = decSpec.pphs.GetBoolTileDef(t);
            var nPrec = 1;
            int start;
            int hlen, plen;
            for (var r = ress; r < rese; r++)
            {
                // loop on resolution levels
                for (var l = minlys; l < lye; l++)
                {
                    // loop on layers
                    for (var c = comps; c < compe; c++)
                    {
                        // loop on components
                        // Checks if component exists
                        if (c >= mdl.Length)
                            continue;
                        // Checks if resolution level exists
                        if (r > mdl[c])
                            continue;
                        if (r >= lys[c].Length)
                            continue;
                        // Checks if layer exists
                        if (l < lys[c][r] || l >= numLayers)
                            continue;

                        nPrec = pktDec.GetNumPrecinct(c, r);

                        for (var p = 0; p < nPrec; p++)
                        {
                            // loop on precincts
                            start = inStream.Pos;

                            // If packed packet headers are used, there is no need
                            // to check that there are bytes enough to read header
                            if (pph)
                            {
                                pktDec.readPktHead(l, r, c, p, cbI[c][r], nBytes);
                            }

                            // If we are about to read outside of tile-part,
                            // skip to next tile-part
                            if (start > lastByte && curTilePart < firstPackOff[t].Length - 1)
                            {
                                curTilePart++;
                                inStream.seek(firstPackOff[t][curTilePart]);
                                lastByte = inStream.Pos + tilePartLen[t][curTilePart] - 1 - tilePartHeadLen[t][curTilePart];
                            }

                            // Read SOP marker segment if necessary
                            status = pktDec.readSOPMarker(nBytes, p, c, r);

                            if (status)
                            {
                                if (printInfo)
                                {
                                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
                                }
                                return true;
                            }

                            if (!pph)
                            {
                                status = pktDec.readPktHead(l, r, c, p, cbI[c][r], nBytes);
                            }

                            if (status)
                            {
                                if (printInfo)
                                {
                                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
                                }
                                // Output rate of EOF reached
                                return true;
                            }

                            // Store packet's head length
                            hlen = inStream.Pos - start;
                            pktHL.Add(hlen);

                            // Reads packet's body
                            status = pktDec.readPktBody(l, r, c, p, cbI[c][r], nBytes);
                                                plen = inStream.Pos - start;
                                                if (printInfo) strInfo += (" Pkt l=" + l + ",r=" + r + ",c=" + c + ",p=" + p + ": " + start + ", " + plen + ", " + hlen + "\n");

                                                if (status)
                                                {
                                                    if (printInfo)
                                                    {
                                                        FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
                                                    }
                                                    // Output rate or EOF reached
                                                    return true;
                                                }
                                            } // end loop on precincts
                                        } // end loop on components
                                    } // end loop on layers
                                } // end loop on resolution levels

                                if (printInfo)
                                {
                                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
                                }
                                return false; // Decoding rate was not reached
                            }

        /// <summary> Reads packets of the current tile according to the
        /// resolution-position-component-layer progressiveness.
        /// 
        /// </summary>
        /// <param name="lys">Index of the first layer for each component and resolution.
        /// 
        /// </param>
        /// <param name="lye">Index of the last layer.
        /// 
        /// </param>
        /// <param name="ress">Index of the first resolution level.
        /// 
        /// </param>
        /// <param name="rese">Index of the last resolution level.
        /// 
        /// </param>
        /// <param name="comps">Index of the first component.
        /// 
        /// </param>
        /// <param name="compe">Index of the last component.
        /// 
        /// </param>
        /// <returns> True if rate has been reached.
        /// 
        /// </returns>
        private bool readResPosCompLy(int[][] lys, int lye, int ress, int rese, int comps, int compe)
        {
            // Computes current tile offset in the reference grid

            var nTiles = GetNumTiles(null);
            var tileI = GetTile(null);
            var x0siz = hd.ImgULX;
            var y0siz = hd.ImgULY;
            var xsiz = x0siz + hd.ImgWidth;
            var ysiz = y0siz + hd.ImgHeight;
            var xt0siz = TilePartULX;
            var yt0siz = TilePartULY;
            var xtsiz = NomTileWidth;
            var ytsiz = NomTileHeight;
            var tx0 = (tileI.x == 0) ? x0siz : xt0siz + tileI.x * xtsiz;
            var ty0 = (tileI.y == 0) ? y0siz : yt0siz + tileI.y * ytsiz;
            var tx1 = (tileI.x != nTiles.x - 1) ? xt0siz + (tileI.x + 1) * xtsiz : xsiz;
            var ty1 = (tileI.y != nTiles.y - 1) ? yt0siz + (tileI.y + 1) * ytsiz : ysiz;

            // Get precinct information (number,distance between two consecutive
            // precincts in the reference grid) in each component and resolution
            // level
            var t = TileIdx; // Current tile index
            PrecInfo prec; // temporary variable
            int p; // Current precinct index
            var gcd_x = 0; // Horiz. distance between 2 precincts in the ref. grid
            var gcd_y = 0; // Vert. distance between 2 precincts in the ref. grid
            var nPrec = 0; // Total number of found precincts
            var nextPrec = new int[compe][]; // Next precinct index in each
                                             // component and resolution level
            var minlys = 100000; // minimum layer start index of each component
            var minx = tx1; // Horiz. offset of the second precinct in the
                            // reference grid
            var miny = ty1; // Vert. offset of the second precinct in the
                            // reference grid. 
            var maxx = tx0; // Max. horiz. offset of precincts in the ref. grid
            var maxy = ty0; // Max. vert. offset of precincts in the ref. grid
                            //Coord numPrec;
            for (var c = comps; c < compe; c++)
            {
                // components
                for (var r = ress; r < rese; r++)
                {
                    // resolution levels
                    if (c >= mdl.Length)
                        continue;
                    if (r > mdl[c])
                        continue;
                    nextPrec[c] = new int[mdl[c] + 1];
                    if (lys[c] != null && r < lys[c].Length && lys[c][r] < minlys)
                    {
                        minlys = lys[c][r];
                    }
                    p = pktDec.GetNumPrecinct(c, r) - 1;
                    for (; p >= 0; p--)
                    {
                        prec = pktDec.GetPrecInfo(c, r, p);
                        if (prec.rgulx != tx0)
                        {
                            if (prec.rgulx < minx)
                                minx = prec.rgulx;
                            if (prec.rgulx > maxx)
                                maxx = prec.rgulx;
                        }
                        if (prec.rguly != ty0)
                        {
                            if (prec.rguly < miny)
                                miny = prec.rguly;
                            if (prec.rguly > maxy)
                                maxy = prec.rguly;
                        }

                        if (nPrec == 0)
                        {
                            gcd_x = prec.rgw;
                            gcd_y = prec.rgh;
                        }
                        else
                        {
                            gcd_x = MathUtil.gcd(gcd_x, prec.rgw);
                            gcd_y = MathUtil.gcd(gcd_y, prec.rgh);
                        }
                        nPrec++;
                    } // precincts
                } // resolution levels
            } // components

            if (nPrec == 0)
            {
                throw new InvalidOperationException("Image cannot have no precinct");
            }

            var pyend = (maxy - miny) / gcd_y + 1;
            var pxend = (maxx - minx) / gcd_x + 1;
            int x, y;
            int hlen, plen;
            int start;
            var status = false;
            var lastByte = firstPackOff[t][curTilePart] + tilePartLen[t][curTilePart] - 1 - tilePartHeadLen[t][curTilePart];
            var numLayers = ((int)decSpec.nls.GetTileDef(t));
            string strInfo = printInfo ? $"Tile {TileIdx} (tile-part:{curTilePart}): offset, length, header length\n" : null;
            var pph = decSpec.pphs.GetBoolTileDef(t);
            for (var r = ress; r < rese; r++)
            {
                // loop on resolution levels
                y = ty0;
                x = tx0;
                for (var py = 0; py <= pyend; py++)
                {
                    // Vertical precincts
                    for (var px = 0; px <= pxend; px++)
                    {
                        // Horiz. precincts
                        for (var c = comps; c < compe; c++)
                        {
                            // Components
                            if (c >= mdl.Length)
                                continue;
                            if (r > mdl[c])
                                continue;
                            if (nextPrec[c][r] >= pktDec.GetNumPrecinct(c, r))
                            {
                                continue;
                            }
                            prec = pktDec.GetPrecInfo(c, r, nextPrec[c][r]);
                            if ((prec.rgulx != x) || (prec.rguly != y))
                            {
                                continue;
                            }
                            for (var l = minlys; l < lye; l++)
                            {
                                // layers
                                if (r >= lys[c].Length)
                                    continue;
                                if (l < lys[c][r] || l >= numLayers)
                                    continue;

                                start = inStream.Pos;

                                // If packed packet headers are used, there is no
                                // need to check that there are bytes enough to
                                // read header
                                if (pph)
                                {
                                    pktDec.readPktHead(l, r, c, nextPrec[c][r], cbI[c][r], nBytes);
                                }

                                // If we are about to read outside of tile-part,
                                // skip to next tile-part
                                if (start > lastByte && curTilePart < firstPackOff[t].Length - 1)
                                {
                                    curTilePart++;
                                    inStream.seek(firstPackOff[t][curTilePart]);
                                    lastByte = inStream.Pos + tilePartLen[t][curTilePart] - 1 - tilePartHeadLen[t][curTilePart];
                                }

                                // Read SOP marker segment if necessary
                                status = pktDec.readSOPMarker(nBytes, nextPrec[c][r], c, r);

                                if (status)
                                {
                                    if (printInfo)
                                    {
                                        FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
                                    }
                                    return true;
                                }

                                if (!pph)
                                {
                                    status = pktDec.readPktHead(l, r, c, nextPrec[c][r], cbI[c][r], nBytes);
                                }

                                if (status)
                                {
                                    if (printInfo)
                                    {
                                        FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
                                    }
                                    return true;
                                }

                                // Store packet's head length
                                hlen = inStream.Pos - start;
                                pktHL.Add(hlen);

                                // Reads packet's body
                                status = pktDec.readPktBody(l, r, c, nextPrec[c][r], cbI[c][r], nBytes);
                                plen = inStream.Pos - start;
                                 if (printInfo) strInfo += (" Pkt l=" + l + ",r=" + r + ",c=" + c + ",p=" + nextPrec[c][r] + ": " + start + ", " + plen + ", " + hlen + "\n");

                                 if (status)
                                 {
                                     if (printInfo)
                                     {
                                         FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
                                     }
                                     return true;
                                 }
                             } // layers
                             nextPrec[c][r]++;
                         } // Components
                         if (px != pxend)
                         {
                             x = minx + px * gcd_x;
                         }
                         else
                         {
                             x = tx0;
                         }
                     } // Horizontal precincts
                     if (py != pyend)
                     {
                         y = miny + py * gcd_y;
                     }
                     else
                     {
                         y = ty0;
                     }
                 } // Vertical precincts
             } // end loop on resolution levels

             if (printInfo)
             {
                 FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
             }
             return false; // Decoding rate was not reached
         }

        /// <summary> Reads packets of the current tile according to the
        /// position-component-resolution-layer progressiveness.
        /// 
        /// </summary>
        /// <param name="lys">Index of the first layer for each component and resolution.
        /// 
        /// </param>
        /// <param name="lye">Index of the last layer.
        /// 
        /// </param>
        /// <param name="ress">Index of the first resolution level.
        /// 
        /// </param>
        /// <param name="rese">Index of the last resolution level.
        /// 
        /// </param>
        /// <param name="comps">Index of the first component.
        /// 
        /// </param>
        /// <param name="compe">Index of the last component.
        /// 
        /// </param>
        /// <returns> True if rate has been reached.
        /// 
        /// </returns>
        private bool readPosCompResLy(int[][] lys, int lye, int ress, int rese, int comps, int compe)
        {
            var nTiles = GetNumTiles(null);
            var tileI = GetTile(null);
            var x0siz = hd.ImgULX;
            var y0siz = hd.ImgULY;
            var xsiz = x0siz + hd.ImgWidth;
            var ysiz = y0siz + hd.ImgHeight;
            var xt0siz = TilePartULX;
            var yt0siz = TilePartULY;
            var xtsiz = NomTileWidth;
            var ytsiz = NomTileHeight;
            var tx0 = (tileI.x == 0) ? x0siz : xt0siz + tileI.x * xtsiz;
            var ty0 = (tileI.y == 0) ? y0siz : yt0siz + tileI.y * ytsiz;
            var tx1 = (tileI.x != nTiles.x - 1) ? xt0siz + (tileI.x + 1) * xtsiz : xsiz;
            var ty1 = (tileI.y != nTiles.y - 1) ? yt0siz + (tileI.y + 1) * ytsiz : ysiz;

            // Get precinct information (number,distance between two consecutive
            // precincts in the reference grid) in each component and resolution
            // level
            var t = TileIdx; // Current tile index
            PrecInfo prec; // temporary variable
            int p; // Current precinct index
            var gcd_x = 0; // Horiz. distance between 2 precincts in the ref. grid
            var gcd_y = 0; // Vert. distance between 2 precincts in the ref. grid
            var nPrec = 0; // Total number of found precincts
            var nextPrec = new int[compe][]; // Next precinct index in each
                                             // component and resolution level
            var minlys = 100000; // minimum layer start index of each component
            var minx = tx1; // Horiz. offset of the second precinct in the
                            // reference grid
            var miny = ty1; // Vert. offset of the second precinct in the
                            // reference grid. 
            var maxx = tx0; // Max. horiz. offset of precincts in the ref. grid
            var maxy = ty0; // Max. vert. offset of precincts in the ref. grid
                            //Coord numPrec;
            for (var c = comps; c < compe; c++)
            {
                // components
                for (var r = ress; r < rese; r++)
                {
                    // resolution levels
                    if (c >= mdl.Length)
                        continue;
                    if (r > mdl[c])
                        continue;
                    nextPrec[c] = new int[mdl[c] + 1];
                    if (lys[c] != null && r < lys[c].Length && lys[c][r] < minlys)
                    {
                        minlys = lys[c][r];
                    }
                    p = pktDec.GetNumPrecinct(c, r) - 1;
                    for (; p >= 0; p--)
                    {
                        prec = pktDec.GetPrecInfo(c, r, p);
                        if (prec.rgulx != tx0)
                        {
                            if (prec.rgulx < minx)
                                minx = prec.rgulx;
                            if (prec.rgulx > maxx)
                                maxx = prec.rgulx;
                        }
                        if (prec.rguly != ty0)
                        {
                            if (prec.rguly < miny)
                                miny = prec.rguly;
                            if (prec.rguly > maxy)
                                maxy = prec.rguly;
                        }

                        if (nPrec == 0)
                        {
                            gcd_x = prec.rgw;
                            gcd_y = prec.rgh;
                        }
                        else
                        {
                            gcd_x = MathUtil.gcd(gcd_x, prec.rgw);
                            gcd_y = MathUtil.gcd(gcd_y, prec.rgh);
                        }
                        nPrec++;
                    } // precincts
                } // resolution levels
            } // components

            if (nPrec == 0)
            {
                throw new InvalidOperationException("Image cannot have no precinct");
            }

            var pyend = (maxy - miny) / gcd_y + 1;
            var pxend = (maxx - minx) / gcd_x + 1;
            int hlen, plen;
            int start;
            var status = false;
            var lastByte = firstPackOff[t][curTilePart] + tilePartLen[t][curTilePart] - 1 - tilePartHeadLen[t][curTilePart];
            var numLayers = ((int)decSpec.nls.GetTileDef(t));
            string strInfo = printInfo ? $"Tile {TileIdx} (tile-part:{curTilePart}): offset, length, header length\n" : null;
            var pph = decSpec.pphs.GetBoolTileDef(t);

            var y = ty0;
            var x = tx0;
            for (var py = 0; py <= pyend; py++)
            {
                // Vertical precincts
                for (var px = 0; px <= pxend; px++)
                {
                    // Horiz. precincts
                    for (var c = comps; c < compe; c++)
                    {
                        // Components
                        if (c >= mdl.Length)
                            continue;
                        for (var r = ress; r < rese; r++)
                        {
                            // Resolution levels
                            if (r > mdl[c])
                                continue;
                            if (nextPrec[c][r] >= pktDec.GetNumPrecinct(c, r))
                            {
                                continue;
                            }
                            prec = pktDec.GetPrecInfo(c, r, nextPrec[c][r]);
                            if ((prec.rgulx != x) || (prec.rguly != y))
                            {
                                continue;
                            }
                            for (var l = minlys; l < lye; l++)
                            {
                                // Layers
                                if (r >= lys[c].Length)
                                    continue;
                                if (l < lys[c][r] || l >= numLayers)
                                    continue;

                                start = inStream.Pos;

                                // If packed packet headers are used, there is no
                                // need to check that there are bytes enough to
                                // read header
                                if (pph)
                                {
                                    pktDec.readPktHead(l, r, c, nextPrec[c][r], cbI[c][r], nBytes);
                                }

                                // If we are about to read outside of tile-part,
                                // skip to next tile-part
                                if (start > lastByte && curTilePart < firstPackOff[t].Length - 1)
                                {
                                    curTilePart++;
                                    inStream.seek(firstPackOff[t][curTilePart]);
                                    lastByte = inStream.Pos + tilePartLen[t][curTilePart] - 1 - tilePartHeadLen[t][curTilePart];
                                }

                                // Read SOP marker segment if necessary
                                status = pktDec.readSOPMarker(nBytes, nextPrec[c][r], c, r);

                                if (status)
                                {
                                    if (printInfo)
                                    {
                                        FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
                                    }
                                    return true;
                                }

                                if (!pph)
                                {
                                    status = pktDec.readPktHead(l, r, c, nextPrec[c][r], cbI[c][r], nBytes);
                                }

                                if (status)
                                {
                                    if (printInfo)
                                    {
                                        FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
                                    }
                                    return true;
                                }

                                // Store packet's head length
                                hlen = inStream.Pos - start;
                                pktHL.Add(hlen);

                                // Reads packet's body
                                status = pktDec.readPktBody(l, r, c, nextPrec[c][r], cbI[c][r], nBytes);
                                plen = inStream.Pos - start;
                                if (printInfo) strInfo += (" Pkt l=" + l + ",r=" + r + ",c=" + c + ",p=" + nextPrec[c][r] + ": " + start + ", " + plen + ", " + hlen + "\n");

                                if (status)
                                {
                                    if (printInfo)
                                    {
                                        FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
                                    }
                                    return true;
                                }
                            } // layers
                            nextPrec[c][r]++;
                        } // Resolution levels
                    } // Components
                    if (px != pxend)
                    {
                        x = minx + px * gcd_x;
                    }
                    else
                    {
                        x = tx0;
                    }
                } // Horizontal precincts
                if (py != pyend)
                {
                    y = miny + py * gcd_y;
                }
                else
                {
                    y = ty0;
                }
            } // Vertical precincts        

            if (printInfo)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
            }
            return false; // Decoding rate was not reached
        }

        /// <summary> Reads packets of the current tile according to the
        /// component-position-resolution-layer progressiveness.
        /// 
        /// </summary>
        /// <param name="lys">Index of the first layer for each component and resolution.
        /// 
        /// </param>
        /// <param name="lye">Index of the last layer.
        /// 
        /// </param>
        /// <param name="ress">Index of the first resolution level.
        /// 
        /// </param>
        /// <param name="rese">Index of the last resolution level.
        /// 
        /// </param>
        /// <param name="comps">Index of the first component.
        /// 
        /// </param>
        /// <param name="compe">Index of the last component.
        /// 
        /// </param>
        /// <returns> True if rate has been reached.
        /// 
        /// </returns>
        private bool readCompPosResLy(int[][] lys, int lye, int ress, int rese, int comps, int compe)
        {
            var nTiles = GetNumTiles(null);
            var tileI = GetTile(null);
            var x0siz = hd.ImgULX;
            var y0siz = hd.ImgULY;
            var xsiz = x0siz + hd.ImgWidth;
            var ysiz = y0siz + hd.ImgHeight;
            var xt0siz = TilePartULX;
            var yt0siz = TilePartULY;
            var xtsiz = NomTileWidth;
            var ytsiz = NomTileHeight;
            var tx0 = (tileI.x == 0) ? x0siz : xt0siz + tileI.x * xtsiz;
            var ty0 = (tileI.y == 0) ? y0siz : yt0siz + tileI.y * ytsiz;
            var tx1 = (tileI.x != nTiles.x - 1) ? xt0siz + (tileI.x + 1) * xtsiz : xsiz;
            var ty1 = (tileI.y != nTiles.y - 1) ? yt0siz + (tileI.y + 1) * ytsiz : ysiz;

            // Get precinct information (number,distance between two consecutive
            // precincts in the reference grid) in each component and resolution
            // level
            var t = TileIdx; // Current tile index
            PrecInfo prec; // temporary variable
            int p; // Current precinct index
            var gcd_x = 0; // Horiz. distance between 2 precincts in the ref. grid
            var gcd_y = 0; // Vert. distance between 2 precincts in the ref. grid
            var nPrec = 0; // Total number of found precincts
            var nextPrec = new int[compe][]; // Next precinct index in each
                                             // component and resolution level
            var minlys = 100000; // minimum layer start index of each component
            var minx = tx1; // Horiz. offset of the second precinct in the
                            // reference grid
            var miny = ty1; // Vert. offset of the second precinct in the
                            // reference grid. 
            var maxx = tx0; // Max. horiz. offset of precincts in the ref. grid
            var maxy = ty0; // Max. vert. offset of precincts in the ref. grid
                            //Coord numPrec;
            for (var c = comps; c < compe; c++)
            {
                // components
                for (var r = ress; r < rese; r++)
                {
                    // resolution levels
                    if (c >= mdl.Length)
                        continue;
                    if (r > mdl[c])
                        continue;
                    nextPrec[c] = new int[mdl[c] + 1];
                    if (lys[c] != null && r < lys[c].Length && lys[c][r] < minlys)
                    {
                        minlys = lys[c][r];
                    }
                    p = pktDec.GetNumPrecinct(c, r) - 1;
                    for (; p >= 0; p--)
                    {
                        prec = pktDec.GetPrecInfo(c, r, p);
                        if (prec.rgulx != tx0)
                        {
                            if (prec.rgulx < minx)
                                minx = prec.rgulx;
                            if (prec.rgulx > maxx)
                                maxx = prec.rgulx;
                        }
                        if (prec.rguly != ty0)
                        {
                            if (prec.rguly < miny)
                                miny = prec.rguly;
                            if (prec.rguly > maxy)
                                maxy = prec.rguly;
                        }

                        if (nPrec == 0)
                        {
                            gcd_x = prec.rgw;
                            gcd_y = prec.rgh;
                        }
                        else
                        {
                            gcd_x = MathUtil.gcd(gcd_x, prec.rgw);
                            gcd_y = MathUtil.gcd(gcd_y, prec.rgh);
                        }
                        nPrec++;
                    } // precincts
                } // resolution levels
            } // components

            if (nPrec == 0)
            {
                throw new InvalidOperationException("Image cannot have no precinct");
            }

            var pyend = (maxy - miny) / gcd_y + 1;
            var pxend = (maxx - minx) / gcd_x + 1;
            int hlen, plen;
            int start;
            var status = false;
            var lastByte = firstPackOff[t][curTilePart] + tilePartLen[t][curTilePart] - 1 - tilePartHeadLen[t][curTilePart];
            var numLayers = ((int)decSpec.nls.GetTileDef(t));
            string strInfo = printInfo ? $"Tile {TileIdx} (tile-part:{curTilePart}): offset, length, header length\n" : null;
            var pph = decSpec.pphs.GetBoolTileDef(t);

            int x, y;
            for (var c = comps; c < compe; c++)
            {
                // components
                if (c >= mdl.Length)
                    continue;
                y = ty0;
                x = tx0;
                for (var py = 0; py <= pyend; py++)
                {
                    // Vertical precincts
                    for (var px = 0; px <= pxend; px++)
                    {
                        // Horiz. precincts
                        for (var r = ress; r < rese; r++)
                        {
                            // Resolution levels
                            if (r > mdl[c])
                                continue;
                            if (nextPrec[c][r] >= pktDec.GetNumPrecinct(c, r))
                            {
                                continue;
                            }
                            prec = pktDec.GetPrecInfo(c, r, nextPrec[c][r]);
                            if ((prec.rgulx != x) || (prec.rguly != y))
                            {
                                continue;
                            }

                            for (var l = minlys; l < lye; l++)
                            {
                                // Layers
                                if (r >= lys[c].Length)
                                    continue;
                                if (l < lys[c][r])
                                    continue;

                                start = inStream.Pos;

                                // If packed packet headers are used, there is no
                                // need to check that there are bytes enough to
                                // read header
                                if (pph)
                                {
                                    pktDec.readPktHead(l, r, c, nextPrec[c][r], cbI[c][r], nBytes);
                                }

                                // If we are about to read outside of tile-part,
                                // skip to next tile-part
                                if (start > lastByte && curTilePart < firstPackOff[t].Length - 1)
                                {
                                    curTilePart++;
                                    inStream.seek(firstPackOff[t][curTilePart]);
                                    lastByte = inStream.Pos + tilePartLen[t][curTilePart] - 1 - tilePartHeadLen[t][curTilePart];
                                }

                                // Read SOP marker segment if necessary
                                status = pktDec.readSOPMarker(nBytes, nextPrec[c][r], c, r);

                                if (status)
                                {
                                    if (printInfo)
                                    {
                                        FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
                                    }
                                    return true;
                                }

                                if (!pph)
                                {
                                    status = pktDec.readPktHead(l, r, c, nextPrec[c][r], cbI[c][r], nBytes);
                                }

                                if (status)
                                {
                                    if (printInfo)
                                    {
                                        FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
                                    }
                                    return true;
                                }

                                // Store packet's head length
                                hlen = inStream.Pos - start;
                                pktHL.Add(hlen);

                                // Reads packet's body
                                status = pktDec.readPktBody(l, r, c, nextPrec[c][r], cbI[c][r], nBytes);
                                        plen = inStream.Pos - start;
                                        if (printInfo) strInfo += (" Pkt l=" + l + ",r=" + r + ",c=" + c + ",p=" + nextPrec[c][r] + ": " + start + ", " + plen + ", " + hlen + "\n");

                                        if (status)
                                        {
                                            if (printInfo)
                                            {
                                                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
                                            }
                                            return true;
                                        }
                                    } // layers                        
                                    nextPrec[c][r]++;
                                } // Resolution levels
                                if (px != pxend)
                        {
                            x = minx + px * gcd_x;
                        }
                        else
                        {
                            x = tx0;
                        }
                    } // Horizontal precincts
                    if (py != pyend)
                    {
                        y = miny + py * gcd_y;
                    }
                    else
                    {
                        y = ty0;
                    }
                } // Vertical precincts
            } // components

            if (printInfo)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, strInfo);
            }
            return false; // Decoding rate was not reached
        }

        /// <summary> Finish initialization of members for specified tile, reads packets head
        /// of each tile and keeps location of each code-block's codewords. The
        /// last 2 tasks are done by calling specific methods of PktDecoder.
        /// 
        /// Then, if a parsing output rate is defined, it keeps information of
        /// first layers only. This operation simulates a creation of a
        /// layer-resolution-component progressive bit-stream which will be next
        /// truncated and decoded.
        /// 
        /// </summary>
        /// <param name="t">Tile index
        /// 
        /// </param>
        /// <seealso cref="PktDecoder" />
        private void readTilePkts(int t)
        {
            pktHL = new List<int>(10);

            // Number of layers
            var nl = ((int)decSpec.nls.GetTileDef(t));

            // If packed packet headers was used, get the packet headers for this
            // tile
            if (decSpec.pphs.GetBoolTileDef(t))
            {
                // Gets packed headers as separate input stream
                var pphbais = hd.getPackedPktHead(t);

                // Restarts PktDecoder instance
                cbI = pktDec.restart(nc, mdl, nl, cbI, true, pphbais);
            }
            else
            {
                // Restarts PktDecoder instance
                cbI = pktDec.restart(nc, mdl, nl, cbI, false, null);
            }

            // Reads packets of the tile according to the progression order
            var pocSpec = ((int[][])decSpec.pcs.GetTileDef(t));
            var nChg = pocSpec?.Length ?? 1;

            // Create an array containing information about changes (progression
            // order type, layers index start, layer index end, resolution level
            // start, resolution level end, component index start, component index
            // end). There is one row per progresion order
            var change = new int[nChg][];
            for (var i = 0; i < nChg; i++)
            {
                change[i] = new int[6];
            }
            var idx = 0; // Index of the current progression order

            change[0][1] = 0; // layer start

            if (pocSpec == null)
            {
                change[idx][0] = ((int)decSpec.pos.GetTileDef(t));
                // Progression type found in COx marker segments
                change[idx][1] = nl; // Layer index end
                change[idx][2] = 0; // resolution level start
                change[idx][3] = decSpec.dls.GetMaxInTile(t) + 1; // res. level end
                change[idx][4] = 0; // Component index start
                change[idx][5] = nc; // Component index end
            }
            else
            {
                for (idx = 0; idx < nChg; idx++)
                {
                    change[idx][0] = pocSpec[idx][5];
                    change[idx][1] = pocSpec[idx][2]; // layer end
                    change[idx][2] = pocSpec[idx][0]; // res. lev. start
                    change[idx][3] = pocSpec[idx][3]; // res. lev. end
                    change[idx][4] = pocSpec[idx][1]; // Comp. index start
                    change[idx][5] = pocSpec[idx][4]; // Comp. index end
                }
            }

            // Seeks to the first packet of the first tile-part
            try
            {
                // If in truncation mode, the first tile-part may be beyond the
                // target decoding rate. In this case, the offset of the first
                // packet is not defined.
                if (isTruncMode && firstPackOff == null || firstPackOff[t] == null)
                {
                    return;
                }
                inStream.seek(firstPackOff[t][0]);
            }
            catch (System.IO.EndOfStreamException)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, $"Codestream truncated in tile {t}");
                return;
            }

            curTilePart = 0;

            // Start and end indexes for layers, resolution levels and components.
            int lye, ress, rese, comps, compe;
            var status = false;
            var nb = nBytes[t];
            var lys = new int[this.nc][];
            for (var c = 0; c < this.nc; c++)
            {
                lys[c] = new int[((int)decSpec.dls.GetTileCompVal(t, c)) + 1];
            }

            for (var chg = 0; chg < nChg; chg++)
            {

                lye = change[chg][1];
                ress = change[chg][2];
                rese = change[chg][3];
                comps = change[chg][4];
                compe = change[chg][5];

                switch (change[chg][0])
                {

                    case ProgressionType.LY_RES_COMP_POS_PROG:
                        status = readLyResCompPos(lys, lye, ress, rese, comps, compe);
                        break;

                    case ProgressionType.RES_LY_COMP_POS_PROG:
                        status = readResLyCompPos(lys, lye, ress, rese, comps, compe);
                        break;

                    case ProgressionType.RES_POS_COMP_LY_PROG:
                        status = readResPosCompLy(lys, lye, ress, rese, comps, compe);
                        break;

                    case ProgressionType.POS_COMP_RES_LY_PROG:
                        status = readPosCompResLy(lys, lye, ress, rese, comps, compe);
                        break;

                    case ProgressionType.COMP_POS_RES_LY_PROG:
                        status = readCompPosResLy(lys, lye, ress, rese, comps, compe);
                        break;

                    default:
                        throw new ArgumentException("Not recognized " + "progression type");

                }

                // Update next first layer index 
                for (var c = comps; c < compe; c++)
                {
                    if (c >= lys.Length)
                        continue;
                    for (var r = ress; r < rese; r++)
                    {
                        if (r >= lys[c].Length)
                            continue;
                        lys[c][r] = lye;
                    }
                }

                if (status || usePOCQuit)
                {
                    break;
                }
            }

            // In truncation mode, update the number of read bytes
            if (isTruncMode)
            {
                anbytes += nb - nBytes[t];

                // If truncation rate is reached
                if (status)
                {
                    nBytes[t] = 0;
                }
            }
            else if (nBytes[t] < (totTileLen[t] - totTileHeadLen[t]))
            {
                // In parsing mode, if there is not enough rate to entirely read the
                // tile. Then, parses the bit stream so as to create a virtual
                // layer-resolution-component progressive bit stream that will be
                // truncated and decoded afterwards.
                CBlkInfo cb;

                // Systematicaly reject all remaining code-blocks if one
                // code-block, at least, is refused.
                bool reject;
                // Stop reading any data from the bit stream
                var stopCount = false;
                // Length of each packet's head (in an array)
                var pktHeadLen = new int[pktHL.Count];
                for (var i = pktHL.Count - 1; i >= 0; i--)
                {
                    pktHeadLen[i] = pktHL[i];
                }

                // Parse each code-block, layer per layer until nBytes[t] is
                // reached
                reject = false;
                for (var l = 0; l < nl; l++)
                {
                    // layers
                    if (cbI == null)
                        continue;
                    var nc = cbI.Length;

                    var mres = 0;
                    for (var c = 0; c < nc; c++)
                    {
                        if (cbI[c] != null && cbI[c].Length > mres)
                            mres = cbI[c].Length;
                    }
                    for (var r = 0; r < mres; r++)
                    {
                        // resolutions

                        var msub = 0;
                        for (var c = 0; c < nc; c++)
                        {
                            if (cbI[c] != null && cbI[c][r] != null && cbI[c][r].Length > msub)
                                msub = cbI[c][r].Length;
                        }
                        for (var s = 0; s < msub; s++)
                        {
                            // subbands
                            // Only LL subband resolution level 0
                            if (r == 0 && s != 0)
                            {
                                continue;
                            }
                            else if (r != 0 && s == 0)
                            {
                                // No LL subband in resolution level > 0
                                continue;
                            }

                            var mnby = 0;
                            for (var c = 0; c < nc; c++)
                            {
                                if (cbI[c] != null && cbI[c][r] != null && cbI[c][r][s] != null && cbI[c][r][s].Length > mnby)
                                    mnby = cbI[c][r][s].Length;
                            }
                            for (var m = 0; m < mnby; m++)
                            {

                                var mnbx = 0;
                                for (var c = 0; c < nc; c++)
                                {
                                    if (cbI[c] != null && cbI[c][r] != null && cbI[c][r][s] != null && cbI[c][r][s][m] != null && cbI[c][r][s][m].Length > mnbx)
                                        mnbx = cbI[c][r][s][m].Length;
                                }
                                for (var n = 0; n < mnbx; n++)
                                {

                                    for (var c = 0; c < nc; c++)
                                    {

                                        if (cbI[c] == null || cbI[c][r] == null || cbI[c][r][s] == null || cbI[c][r][s][m] == null || cbI[c][r][s][m][n] == null)
                                        {
                                            continue;
                                        }
                                        cb = cbI[c][r][s][m][n];

                                        // If no code-block has been refused until
                                        // now
                                        if (!reject)
                                        {
                                            // Rate is to low to allow reading of
                                            // packet's head
                                            if (nBytes[t] < pktHeadLen[cb.pktIdx[l]])
                                            {
                                                // Stop parsing
                                                stopCount = true;
                                                // Reject all next
                                                // code-blocks
                                                reject = true;
                                            }
                                            else
                                            {
                                                // Rate is enough to read packet's
                                                // head 
                                                if (!stopCount)
                                                {
                                                    //If parsing was not stopped
                                                    //Takes into account packet's
                                                    //head length
                                                    nBytes[t] -= pktHeadLen[cb.pktIdx[l]];
                                                    anbytes += pktHeadLen[cb.pktIdx[l]];
                                                    // Set packet's head length to
                                                    // 0, so that it won't be
                                                    // taken into account next
                                                    // time
                                                    pktHeadLen[cb.pktIdx[l]] = 0;
                                                }
                                            }
                                        }
                                        // Code-block has no data in this layer
                                        if (cb.len[l] == 0)
                                        {
                                            continue;
                                        }

                                        // Accepts code-block if length is enough,
                                        // if this code-block was not refused in a
                                        // previous layer and if no code-block was
                                        // refused in current component
                                        if (cb.len[l] < nBytes[t] && !reject)
                                        {
                                            nBytes[t] -= cb.len[l];
                                            anbytes += cb.len[l];
                                        }
                                        else
                                        {
                                            // Refuses code-block
                                            // Forgets code-block's data
                                            cb.len[l] = cb.off[l] = cb.ntp[l] = 0;
                                            // Refuses all other code-block in
                                            // current and next component
                                            reject = true;
                                        }
                                    } // End loop on components
                                } // End loop on horiz. code-blocks
                            } // End loop on vert. code-blocks
                        } // End loop on subbands
                    } // End loop on resolutions
                } // End loop on layers
            }
            else
            {
                // No parsing for this tile, adds tile's body to the total
                // number of read bytes.
                anbytes += totTileLen[t] - totTileHeadLen[t];
                if (t < GetNumTiles() - 1)
                {
                    nBytes[t + 1] += nBytes[t] - (totTileLen[t] - totTileHeadLen[t]);
                }
            }
        }

        /// <summary> Changes the current tile, given the new indexes. An
        /// IllegalArgumentException is thrown if the indexes do not correspond to
        /// a valid tile.
        /// 
        /// </summary>
        /// <param name="x">The horizontal indexes the tile.
        /// 
        /// </param>
        /// <param name="y">The vertical indexes of the new tile.
        /// 
        /// </param>
        public override void SetTile(int x, int y)
        {

            int i; // counter
                   // Check validity of tile indexes
            if (x < 0 || y < 0 || x >= ntX || y >= ntY)
            {
                throw new ArgumentException();
            }
            var t = (y * ntX + x);

            // TLM Fast Path - O(1) tile access when TLM markers available
            var tlmOffset = GetTileOffsetFromTLM(t);

            if (tlmOffset >= 0 && !isTruncMode)
            {
                // TLM available - attempt fast path
                try
                {
                    // Reset byte counters if starting from tile 0
                    if (t == 0)
                    {
                        anbytes = headLen;
                        if (!isTruncMode)
                        {
                            anbytes += 2;
                        }
                        // Restore values of nBytes
                        for (var tIdx = 0; tIdx < nt; tIdx++)
                        {
                            nBytes[tIdx] = baknBytes[tIdx];
                        }
                    }

                    // Seek directly to tile start using TLM
                    inStream.seek((int)tlmOffset);

                    // Verify we're at the correct tile by reading SOT marker
                    var marker = inStream.readShort();
                    if (marker != Markers.SOT)
                    {
                        // Not at tile start, TLM might be incorrect
                        throw new System.IO.IOException("Expected SOT marker not found at TLM offset");
                    }

                    // Read SOT to verify tile index
                    inStream.readUnsignedShort(); // Lsot
                    var actualTile = inStream.readUnsignedShort(); // Isot

                    if (actualTile == t)
                    {
                        // TLM fast path succeeded!
                        // Seek back to start of SOT marker for normal processing
                        inStream.seek((int)tlmOffset);

                        // Set the new current tile
                        ctX = x;
                        ctY = y;

                        // Calculate tile relative points
                        var ctox = (x == 0) ? ax : px + x * ntW;
                        var ctoy = (y == 0) ? ay : py + y * ntH;
                        for (i = nc - 1; i >= 0; i--)
                        {
                            culx[i] = (ctox + hd.GetCompSubsX(i) - 1) / hd.GetCompSubsX(i);
                            culy[i] = (ctoy + hd.GetCompSubsY(i) - 1) / hd.GetCompSubsY(i);
                            offX[i] = (px + x * ntW + hd.GetCompSubsX(i) - 1) / hd.GetCompSubsX(i);
                            offY[i] = (py + y * ntH + hd.GetCompSubsY(i) - 1) / hd.GetCompSubsY(i);
                        }

                        // Initialize subband tree and number of resolution levels
                        subbTrees = new SubbandSyn[nc];
                        mdl = new int[nc];
                        derived = new bool[nc];
                        dequantParams = new StdDequantizerParams[nc];
                        gb = new int[nc];

                        for (var c = 0; c < nc; c++)
                        {
                            derived[c] = decSpec.qts.IsDerived(t, c);
                            dequantParams[c] = (StdDequantizerParams)decSpec.qsss.GetTileCompVal(t, c);
                            gb[c] = ((int)decSpec.gbs.GetTileCompVal(t, c));
                            mdl[c] = ((int)decSpec.dls.GetTileCompVal(t, c));

                            subbTrees[c] = new SubbandSyn(
                                GetTileCompWidth(t, c, mdl[c]),
                                GetTileCompHeight(t, c, mdl[c]),
                                GetResULX(c, mdl[c]),
                                GetResULY(c, mdl[c]),
                                mdl[c],
                                decSpec.wfs.GetHFilters(t, c),
                                decSpec.wfs.GetVFilters(t, c));
                            initSubbandsFields(c, subbTrees[c]);
                        }

                        // Read tile's packets
                        try
                        {
                            readTilePkts(t);
                        }
                        catch (System.IO.EndOfStreamException)
                        {
                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, $"Codestream truncated in tile {t}");
                            return;
                        }
                        catch (System.IO.IOException e)
                        {
                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.ERROR, $"{e.GetType().Name}: {e.Message}");
                            throw new InvalidOperationException($"IO Error when reading tile {x} x {y}");
                        }

                        // Fast path complete - return early
                        return;
                    }
                    else
                    {
                        // TLM was incorrect - log warning and fall through to sequential path
                        FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                            $"TLM marker data inconsistent for tile {t} (found tile {actualTile}), using sequential parsing");
                    }
                }
                catch (System.IO.IOException e)
                {
                    // TLM seek failed - log warning and fall through to sequential path
                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                        $"TLM fast seek failed for tile {t}: {e.Message}, using sequential parsing");
                }
            }
            else
            {
                // Reset number of read bytes if needed
                if (t == 0)
                {
                    anbytes = headLen;
                    if (!isTruncMode)
                    {
                        anbytes += 2;
                    }

                    // Restore values of nBytes
                    for (var tIdx = 0; tIdx < nt; tIdx++)
                    {
                        nBytes[tIdx] = baknBytes[tIdx];
                    }
                }

                // Set the new current tile
                ctX = x;
                ctY = y;
                // Calculate tile relative points
                var ctox = (x == 0) ? ax : px + x * ntW;
                var ctoy = (y == 0) ? ay : py + y * ntH;
                for (i = nc - 1; i >= 0; i--)
                {
                    culx[i] = (ctox + hd.GetCompSubsX(i) - 1) / hd.GetCompSubsX(i);
                    culy[i] = (ctoy + hd.GetCompSubsY(i) - 1) / hd.GetCompSubsY(i);
                    offX[i] = (px + x * ntW + hd.GetCompSubsX(i) - 1) / hd.GetCompSubsX(i);
                    offY[i] = (py + y * ntH + hd.GetCompSubsY(i) - 1) / hd.GetCompSubsY(i);
                }

                // Initialize subband tree and number of resolution levels
                subbTrees = new SubbandSyn[nc];
                mdl = new int[nc];
                derived = new bool[nc];
                dequantParams = new StdDequantizerParams[nc];
                gb = new int[nc];

                for (var c = 0; c < nc; c++)
                {
                    derived[c] = decSpec.qts.IsDerived(t, c);
                    dequantParams[c] = (StdDequantizerParams)decSpec.qsss.GetTileCompVal(t, c);
                    gb[c] = ((int)decSpec.gbs.GetTileCompVal(t, c));
                    mdl[c] = ((int)decSpec.dls.GetTileCompVal(t, c));

                    subbTrees[c] = new SubbandSyn(GetTileCompWidth(t, c, mdl[c]), GetTileCompHeight(t, c, mdl[c]),
                        GetResULX(c, mdl[c]), GetResULY(c, mdl[c]), mdl[c], decSpec.wfs.GetHFilters(t, c),
                        decSpec.wfs.GetVFilters(t, c));
                    initSubbandsFields(c, subbTrees[c]);
                }

                // Read tile's packets
                try
                {
                    readTilePkts(t);
                }
                catch (System.IO.EndOfStreamException)
                {
                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO, $"Codestream truncated in tile {t}");
                    return;
                }
                catch (System.IO.IOException e)
                {
                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.ERROR, $"{e.GetType().Name}: {e.Message}");
                    throw new InvalidOperationException($"IO Error when reading tile {x} x {y}");
                }
            }
        }

        /// <summary> Advances to the next tile, in standard scan-line order (by rows then
        /// columns). A NoNextElementException is thrown if the current tile is the
        /// last one (i.e. there is no next tile).
        /// 
        /// </summary>
        public override void NextTile()
        {
            if (ctX == ntX - 1 && ctY == ntY - 1)
            {
                // Already at last tile
                throw new NoNextElementException();
            }
            else if (ctX < ntX - 1)
            {
                // If not at end of current tile line
                SetTile(ctX + 1, ctY);
            }
            else
            {
                // Go to first tile at next line
                SetTile(0, ctY + 1);
            }
        }

        /// <summary> Returns the specified coded code-block, for the specified component, in
        /// the current tile. The first layer to return is indicated by 'fl'. The
        /// number of layers that is returned depends on 'nl' and the amount of
        /// available data.
        /// 
        /// The argument 'fl' is to be used by subsequent calls to this method
        /// for the same code-block. In this way supplemental data can be retrieved
        /// at a later time. The fact that data from more than one layer can be
        /// returned means that several packets from the same code-block, of the
        /// same component, and the same tile, have been concatenated.
        /// 
        /// The returned compressed code-block can have its progressive
        /// attribute set. If this attribute is set it means that more data can be
        /// obtained by subsequent calls to this method (subject to transmission
        /// delays, etc). If the progressive attribute is not set it means that the
        /// returned data is all the data that can be obtained for the specified
        /// code-block.
        /// 
        /// The compressed code-block is uniquely specified by the current tile,
        /// the component (identified by 'c'), the subband (indentified by 'sb')
        /// and the code-block vertical and horizontal indexes 'n' and 'm'.
        /// 
        /// The 'ulx' and 'uly' members of the returned 'DecLyrdCBlk' object
        /// contain the coordinates of the top-left corner of the block, with
        /// respect to the tile, not the subband.
        /// 
        /// </summary>
        /// <param name="c">The index of the component, from 0 to N-1.
        /// 
        /// </param>
        /// <param name="m">The vertical index of the code-block to return, in the
        /// specified subband.
        /// 
        /// </param>
        /// <param name="n">The horizontal index of the code-block to return, in the
        /// specified subband.
        /// 
        /// </param>
        /// <param name="sb">The subband in whic the requested code-block is.
        /// 
        /// </param>
        /// <param name="fl">The first layer to return.
        /// 
        /// </param>
        /// <param name="nl">The number of layers to return, if negative all available
        /// layers are returned, starting at 'fl'.
        /// 
        /// </param>
        /// <param name="ccb">If not null this object is used to return the compressed
        /// code-block. If null a new object is created and returned. If the data
        /// array in ccb is not null then it can be reused to return the compressed
        /// data.
        /// 
        /// </param>
        /// <returns> The compressed code-block, with a certain number of layers
        /// determined by the available data and 'nl'.
        /// 
        /// </returns>
        public override DecLyrdCBlk GetCodeBlock(int c, int m, int n, SubbandSyn sb, int fl, int nl, DecLyrdCBlk ccb)
        {

            var t = TileIdx;
            CBlkInfo rcb; // requested code-block
            var r = sb.resLvl; // Resolution level
            var s = sb.sbandIdx; // Subband index
            int tpidx;
            int passtype;

            // Number of layers
            var numLayers = ((int)decSpec.nls.GetTileDef(t));
            var options = ((int)decSpec.ecopts.GetTileCompVal(t, c));
            if (nl < 0)
            {
                nl = numLayers - fl + 1;
            }

            // If the l quit condition is used, Make sure that no layer 
            // after lquit is returned
            if (lQuit != -1 && fl + nl > lQuit)
            {
                nl = lQuit - fl;
            }

            // Check validity of resquested resolution level (according to the
            // "-res" option).
            var maxdl = GetSynSubbandTree(t, c).resLvl;
            if (r > targetRes + maxdl - decSpec.dls.Min)
            {
                throw new InvalidOperationException("JJ2000 error: requesting a code-block " + "disallowed by the '-res' option.");
            }

            // Check validity of all the arguments
            try
            {
                rcb = cbI[c][r][s][m][n];

                if (fl < 1 || fl > numLayers || fl + nl - 1 > numLayers)
                {
                    throw new ArgumentException();
                }
            }
            catch (IndexOutOfRangeException)
            {
                throw new ArgumentException(
                    $"Code-block (t:{t}, c:{c}, r:{r}, s:{s}, {m}x{(+n)}) not found in codestream");
            }
            catch (NullReferenceException)
            {
                throw new ArgumentException($"Code-block (t:{t}, c:{c}, r:{r}, s:{s}, {m}x{n}) not found in bit stream");
            }

            // Create DecLyrdCBlk object if necessary
            if (ccb == null)
            {
                ccb = new DecLyrdCBlk();
            }
            ccb.m = m;
            ccb.n = n;
            ccb.nl = 0;
            ccb.dl = 0;
            ccb.nTrunc = 0;

            if (rcb == null)
            {
                // This code-block was skipped when reading. Returns no data
                ccb.skipMSBP = 0;
                ccb.prog = false;
                ccb.w = ccb.h = ccb.ulx = ccb.uly = 0;
                return ccb;
            }

            // ccb initialization
            ccb.skipMSBP = rcb.msbSkipped;
            ccb.ulx = rcb.ulx;
            ccb.uly = rcb.uly;
            ccb.w = rcb.w;
            ccb.h = rcb.h;
            ccb.ftpIdx = 0;

            // Search for index of first truncation point (first layer where
            // length of data is not zero)
            var l = 0;
            while ((l < rcb.len.Length) && (rcb.len[l] == 0))
            {
                ccb.ftpIdx += rcb.ntp[l];
                l++;
            }

            // Calculate total length, number of included layer and number of
            // truncation points
            for (l = fl - 1; l < fl + nl - 1; l++)
            {
                ccb.nl++;
                ccb.dl += rcb.len[l];
                ccb.nTrunc += rcb.ntp[l];
            }

            // Calculate number of terminated segments
            int nts;
            if ((options & StdEntropyCoderOptions.OPT_TERM_PASS) != 0)
            {
                // Regular termination in use One segment per pass
                // (i.e. truncation point)
                nts = ccb.nTrunc - ccb.ftpIdx;
            }
            else if ((options & StdEntropyCoderOptions.OPT_BYPASS) != 0)
            {
                // Selective arithmetic coding bypass mode in use, but no regular
                // termination: 1 segment upto the end of the last pass of the 4th
                // most significant bit-plane, and, in each following bit-plane,
                // one segment upto the end of the 2nd pass and one upto the end
                // of the 3rd pass.

                if (ccb.nTrunc <= StdEntropyCoderOptions.FIRST_BYPASS_PASS_IDX)
                {
                    nts = 1;
                }
                else
                {
                    nts = 1;
                    // Adds one for each terminated pass
                    for (tpidx = ccb.ftpIdx; tpidx < ccb.nTrunc; tpidx++)
                    {
                        if (tpidx >= StdEntropyCoderOptions.FIRST_BYPASS_PASS_IDX - 1)
                        {
                            passtype = (tpidx + StdEntropyCoderOptions.NUM_EMPTY_PASSES_IN_MS_BP) % StdEntropyCoderOptions.NUM_PASSES;
                            if (passtype == 1 || passtype == 2)
                            {
                                // lazy pass just before MQ pass or MQ pass just
                                // before lazy pass => terminated
                                nts++;
                            }
                        }
                    }
                }
            }
            else
            {
                // Nothing special in use, just one terminated segment
                nts = 1;
            }

            // ccb.data creation
            if (ccb.data == null || ccb.data.Length < ccb.dl)
            {
                ccb.data = new byte[ccb.dl];
            }

            // ccb.tsLengths creation
            if (nts > 1 && (ccb.tsLengths == null || ccb.tsLengths.Length < nts))
            {
                ccb.tsLengths = new int[nts];
            }
            else if (nts > 1 && (options & (StdEntropyCoderOptions.OPT_BYPASS | StdEntropyCoderOptions.OPT_TERM_PASS)) == StdEntropyCoderOptions.OPT_BYPASS)
            {
                ArrayUtil.intArraySet(ccb.tsLengths, 0);
            }

            // Fill ccb with compressed data
            var dataIdx = -1;
            tpidx = ccb.ftpIdx;
            var ctp = ccb.ftpIdx; // Cumulative number of truncation
                                  // point for the current layer layer
            var tsidx = 0;
            int j;

            for (l = fl - 1; l < fl + nl - 1; l++)
            {
                ctp += rcb.ntp[l];
                // No data in this layer
                if (rcb.len[l] == 0)
                    continue;

                // Read data
                // NOTE: we should never get an EOFException here since all
                // data is checked to be within the file.
                try
                {
                    inStream.seek(rcb.off[l]);
                    inStream.readFully(ccb.data, dataIdx + 1, rcb.len[l]);
                    dataIdx += rcb.len[l];
                }
                catch (System.IO.IOException e)
                {
                    JJ2KExceptionHandler.handleException(e);
                }

                // Get the terminated segment lengths, if any
                if (nts == 1)
                    continue;
                if ((options & StdEntropyCoderOptions.OPT_TERM_PASS) != 0)
                {
                    // Regular termination => each pass is terminated
                    for (j = 0; tpidx < ctp; j++, tpidx++)
                    {
                        if (rcb.segLen[l] != null)
                        {
                            ccb.tsLengths[tsidx++] = rcb.segLen[l][j];
                        }
                        else
                        {
                            // Only one terminated segment in packet
                            ccb.tsLengths[tsidx++] = rcb.len[l];
                        }
                    }
                }
                else
                {
                    // Lazy coding without regular termination
                    for (j = 0; tpidx < ctp; tpidx++)
                    {
                        if (tpidx >= StdEntropyCoderOptions.FIRST_BYPASS_PASS_IDX - 1)
                        {
                            passtype = (tpidx + StdEntropyCoderOptions.NUM_EMPTY_PASSES_IN_MS_BP) % StdEntropyCoderOptions.NUM_PASSES;
                            if (passtype != 0)
                            {
                                // lazy pass just before MQ pass or MQ
                                // pass just before lazy pass =>
                                // terminated
                                if (rcb.segLen[l] != null)
                                {
                                    ccb.tsLengths[tsidx++] += rcb.segLen[l][j++];
                                    rcb.len[l] -= rcb.segLen[l][j - 1];
                                }
                                else
                                {
                                    // Only one terminated segment in packet
                                    ccb.tsLengths[tsidx++] += rcb.len[l];
                                    rcb.len[l] = 0;
                                }
                            }
                        }
                    }

                    // Last length in packet always in (either terminated segment
                    // or contribution to terminated segment)
                    if (rcb.segLen[l] != null && j < rcb.segLen[l].Length)
                    {
                        ccb.tsLengths[tsidx] += rcb.segLen[l][j];
                        rcb.len[l] -= rcb.segLen[l][j];
                    }
                    else
                    {
                        // Only one terminated segment in packet
                        if (tsidx < nts)
                        {
                            ccb.tsLengths[tsidx] += rcb.len[l];
                            rcb.len[l] = 0;
                        }
                    }
                }
            }
            if (nts == 1 && ccb.tsLengths != null)
            {
                ccb.tsLengths[0] = ccb.dl;
            }

            // Set the progressive flag
            var lastlayer = fl + nl - 1;
            if (lastlayer < numLayers - 1)
            {
                for (l = lastlayer + 1; l < numLayers; l++)
                {
                    // It remains data for this code-block in the bit stream
                    if (rcb.len[l] != 0)
                    {
                        ccb.prog = true;
                    }
                }
            }

            return ccb;
        }
    }
}