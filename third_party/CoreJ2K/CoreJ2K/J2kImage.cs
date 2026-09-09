// Copyright (c) 2007-2016 CSJ2K contributors.
// Licensed under the BSD 3-Clause License.

using CoreJ2K.Util;
using System.Linq;

namespace CoreJ2K
{
    using Color;
    using j2k.codestream;
    using j2k.codestream.reader;
    using j2k.codestream.writer;
    using j2k.encoder;
    using j2k.entropy.decoder;
    using j2k.entropy.encoder;
    using j2k.fileformat.reader;
    using j2k.fileformat.writer;
    using j2k.image;
    using j2k.image.forwcomptransf;
    using j2k.image.input;
    using j2k.image.nlt;
    using j2k.image.dco;
    using j2k.image.invcomptransf;
    using j2k.io;
    using j2k.quantization.dequantizer;
    using j2k.quantization.quantizer;
    using j2k.roi;
    using j2k.roi.encoder;
    using j2k.util;
    using j2k.wavelet.analysis;
    using j2k.wavelet.synthesis;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Util;

    public partial class J2kImage
    {

        #region Static Decoder Methods

        public static InterleavedImage FromFile(string filename, ParameterList? parameters = null)
        {
            using (var stream = FileStreamFactory.New(filename, "r"))
            {
                return FromStream(stream, parameters);
            }
        }

        /// <summary>
        /// Decodes a JPEG2000 file using modern configuration API.
        /// </summary>
        /// <param name="filename">Path to the JPEG2000 file.</param>
        /// <param name="configuration">Modern decoder configuration.</param>
        /// <returns>The decoded image.</returns>
        public static InterleavedImage FromFile(string filename, Configuration.J2KDecoderConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));
            
            if (!configuration.IsValid)
                throw new ArgumentException($"Invalid configuration: {string.Join(", ", configuration.Validate())}");
            
            using (var stream = FileStreamFactory.New(filename, "r"))
            {
                return FromStream(stream, configuration);
            }
        }

        public static InterleavedImage FromBytes(byte[] j2kdata, ParameterList? parameters = null)
        {
            using (var stream = new MemoryStream(j2kdata))
            {
                return FromStream(stream, parameters);
            }
        }

        /// <summary>
        /// Decodes JPEG2000 data from a byte array using modern configuration API.
        /// </summary>
        /// <param name="j2kdata">The JPEG2000 compressed data.</param>
        /// <param name="configuration">Modern decoder configuration.</param>
        /// <returns>The decoded image.</returns>
        public static InterleavedImage FromBytes(byte[] j2kdata, Configuration.J2KDecoderConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));
            
            if (!configuration.IsValid)
                throw new ArgumentException($"Invalid configuration: {string.Join(", ", configuration.Validate())}");
            
            using (var stream = new MemoryStream(j2kdata))
            {
                return FromStream(stream, configuration);
            }
        }

        /// <summary>Decodes JPEG 2000 data from a <see cref="ReadOnlyMemory{T}"/> buffer.</summary>
        public static InterleavedImage FromBytes(ReadOnlyMemory<byte> data, ParameterList? parameters = null)
        {
            using var stream = MemoryStreamFromMemory(data);
            return FromStream(stream, parameters);
        }

        /// <summary>Decodes JPEG 2000 data from a <see cref="ReadOnlyMemory{T}"/> buffer using modern configuration.</summary>
        public static InterleavedImage FromBytes(ReadOnlyMemory<byte> data, Configuration.J2KDecoderConfiguration configuration)
        {
            using var stream = MemoryStreamFromMemory(data);
            return FromStream(stream, configuration);
        }

        public static InterleavedImage FromStream(Stream stream, ParameterList? parameters = null)
        {
            RandomAccessIO? in_stream = null;
            InverseWT? invWT = null;

            try
            {
                in_stream = new ISRandomAccessIO(stream);

                // Create parameter list using defaults
                var pl = parameters ?? new ParameterList(GetDefaultDecoderParameterList(decoder_pinfo));

                // **** File Format ****
                // If the codestream is wrapped in the jp2 file format, Read the
                // file format wrapper
                var ff = new FileFormatReader(in_stream);
                ff.readFileFormat();
                if (ff.JP2FFUsed)
                {
                    in_stream.seek(ff.FirstCodeStreamPos);
                }

                // +----------------------------+
                // | Instantiate decoding chain |
                // +----------------------------+

                // **** Header decoder ****
                // Instantiate header decoder and read main header 
                var hi = new HeaderInfo();
                HeaderDecoder hd;
                try
                {
                    hd = new HeaderDecoder(in_stream, pl, hi);
                }
                catch (EndOfStreamException e)
                {
                    throw new InvalidOperationException("Codestream too short or bad header, unable to decode.", e);
                }

            var nCompCod = hd.NumComps;
            var nTiles = hi.sizValue.NumTiles;
            var decSpec = hd.DecoderSpecs;

            // Get demixed bitdepths
            var depth = new int[nCompCod];
            for (var i = 0; i < nCompCod; i++)
            {
                depth[i] = hd.GetOriginalBitDepth(i);
            }

            // **** Bit stream reader ****
            BitstreamReaderAgent breader;
            try
            {
                breader = BitstreamReaderAgent.createInstance(in_stream, hd, pl, decSpec, false, hi);
            }
            catch (IOException e)
            {
                throw new InvalidOperationException("Error while reading bit stream header or parsing packets.", e);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException("Cannot instantiate bit stream reader.", e);
            }

            // **** Entropy decoder ****
            EntropyDecoder entdec;
            try
            {
                entdec = hd.createEntropyDecoder(breader, pl);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException("Cannot instantiate entropy decoder.", e);
            }

            // **** ROI de-scaler ****
            ROIDeScaler roids;
            try
            {
                roids = hd.createROIDeScaler(entdec, pl, decSpec);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException("Cannot instantiate roi de-scaler.", e);
            }

            // **** Dequantizer ****
            Dequantizer deq;
            try
            {
                deq = HeaderDecoder.createDequantizer(roids, depth, decSpec);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException("Cannot instantiate dequantizer.", e);
            }

            // **** Inverse wavelet transform ***

            try
            {
                // full page inverse wavelet transform
                invWT = InverseWT.createInstance(deq, decSpec);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException("Cannot instantiate inverse wavelet transform.", e);
            }

            var res = breader.ImgRes;
            invWT.ImgResLevel = res;

            // **** Data converter **** (after inverse transform module)
            var converter = new ImgDataConverter(invWT, 0);

            // **** Inverse component transformation ****
            var ictransf = new InvCompTransf(converter, decSpec, depth, pl);

            // **** Inverse multiple component transform (MCT, ISO/IEC 15444-2) ****
            BlkImgDataSrc afterCt = ictransf;
            var mctStages = j2k.codestream.MctTransform.AssembleDecodeStages(hd.MctArrays, hd.MccSegments, hd.McoSegment);
            if (mctStages.Count > 0)
            {
                afterCt = j2k.image.mct.ComponentTransform.BuildChain(ictransf, mctStages, inverse: true);
            }

            // **** Inverse non-linearity point transform (NLT, ISO/IEC 15444-2) ****
            BlkImgDataSrc postCt = afterCt;
            if (hd.NLTSegments != null && hd.NLTSegments.Count > 0)
            {
                postCt = new InvNLT(afterCt, hd.NLTSegments);
            }

            // **** Inverse variable DC offset (DCO, ISO/IEC 15444-2) ****
            if (hd.DcoSegment != null)
            {
                postCt = new InvDCO(postCt, hd.DcoSegment);
            }

            // **** Color space mapping ****
            BlkImgDataSrc color;
            if (ff.JP2FFUsed && pl.GetParameter("nocolorspace").Equals("off"))
            {
                try
                {
                    var csMap = new ColorSpace(in_stream, hd, pl);
                    var channels = hd.createChannelDefinitionMapper(postCt, csMap);
                    var resampled = hd.createResampler(channels, csMap);
                    var palettized = hd.createPalettizedColorSpaceMapper(resampled, csMap);
                    color = hd.createColorSpaceMapper(palettized, csMap);
                }
                catch (ArgumentException e)
                {
                    throw new InvalidOperationException("Could not instantiate ICC profiler.", e);
                }
                catch (ColorSpaceException e)
                {
                    throw new InvalidOperationException("Error processing ColorSpace information.", e);
                }
            }
            else
            {
                // Skip colorspace mapping
                color = postCt;
            }

            // This is the last image in the decoding chain and should be
            // assigned by the last transformation:
            var decodedImage = color;
            if (color == null)
            {
                decodedImage = postCt;
            }
            var numComps = decodedImage.NumComps;
            var imgWidth = decodedImage.ImgWidth;

            // **** Copy to Bitmap ****

            var bitsUsed = new int[numComps];
            for (var j = 0; j < numComps; ++j) bitsUsed[j] = decodedImage.GetNomRangeBits(j);

            var dst = new InterleavedImage(imgWidth, decodedImage.ImgHeight, numComps, bitsUsed);

            var numTiles = decodedImage.GetNumTiles(null);

            var tIdx = 0;

            for (var y = 0; y < numTiles.y; y++)
            {
                // Loop on horizontal tiles
                for (var x = 0; x < numTiles.x; x++, tIdx++)
                {
                    decodedImage.SetTile(x, y);

                    var height = decodedImage.GetTileCompHeight(tIdx, 0);
                    var width = decodedImage.GetTileCompWidth(tIdx, 0);

                    var tOffx = decodedImage.GetCompULX(0)
                                - (int)Math.Ceiling(decodedImage.ImgULX / (double)decodedImage.GetCompSubsX(0));

                    var tOffy = decodedImage.GetCompULY(0)
                                - (int)Math.Ceiling(decodedImage.ImgULY / (double)decodedImage.GetCompSubsY(0));

                    var db = new DataBlkInt[numComps];
                    var ls = new int[numComps];
                    var mv = new int[numComps];
                    var fb = new int[numComps];
                    for (var i = 0; i < numComps; i++)
                    {
                        db[i] = new DataBlkInt();
                        // Use per-component nominal bits and fixed point
                        ls[i] = 1 << (decodedImage.GetNomRangeBits(i) - 1);
                        mv[i] = (1 << decodedImage.GetNomRangeBits(i)) - 1;
                        fb[i] = decodedImage.GetFixedPoint(i);
                    }

                    // Reuse arrays across rows to avoid per-pixel allocations
                    // Use checked block to catch integer overflow for very large images
                    int rowSize;
                    try
                    {
                        checked
                        {
                            rowSize = width * numComps;
                        }
                    }
                    catch (OverflowException)
                    {
                        throw new InvalidOperationException(
                            $"Image tile dimensions too large: width={width}, components={numComps}. " +
                            $"Row buffer size would exceed maximum array size.");
                    }
                    var rowvalues = new int[rowSize];
                    var k = new int[numComps];

                    for (var l = 0; l < height; l++)
                    {
                        // Map tile-local row 'l' to destination image line
                        var destLine = tOffy + l;
                        // Skip rows that end up above the destination image
                        if (destLine < 0) continue;
                        // Stop processing when we've reached the bottom of destination image
                        if (destLine >= dst.Height) break;

                        // Load each component line into its DataBlk and initialize indices
                        for (var i = 0; i < numComps; i++)
                        {
                            db[i].ulx = 0;
                            db[i].uly = l;
                            db[i].w = width;
                            db[i].h = 1;
                            decodedImage.GetInternCompData(db[i], i);
                            k[i] = db[i].offset; // start index for forward iteration
                        }

                        // Fill rowvalues left-to-right, writing component samples interleaved
                        for (var col = 0; col < width; col++)
                        {
                            // Use checked arithmetic to detect overflow in offset calculation
                            int baseOffset;
                            checked
                            {
                                baseOffset = col * numComps;
                            }
                            for (var comp = 0; comp < numComps; comp++)
                            {
                                var v = (db[comp].data_array[k[comp]++] >> fb[comp]) + ls[comp];
                                if (v < 0) v = 0;
                                else if (v > mv[comp]) v = mv[comp];
                                rowvalues[baseOffset + comp] = v;
                            }
                        }

                        dst.FillRow(tOffx, destLine, imgWidth, rowvalues);
                    }
                }
            }

            return dst;
            }
            finally
            {
                // Ensure resources are cleaned up even if an exception occurs
                // InverseWT owns the decoding chain and will clean up all upstream resources
                if (invWT != null)
                {
                    try
                    {
                        invWT.Close();
                    }
                    catch
                    {
                        // Suppress exceptions during cleanup to avoid masking the original exception
                    }
                }
            }
        }

        /// <summary>
        /// Decodes a JPEG2000 stream and returns both the image and any metadata found.
        /// </summary>
        /// <param name="stream">The stream containing JPEG2000 data.</param>
        /// <param name="metadata">Output parameter that receives the metadata (comments, XML, UUIDs).</param>
        /// <param name="parameters">Optional decoder parameters.</param>
        /// <returns>The decoded image.</returns>
        public static InterleavedImage FromStream(Stream stream, out j2k.fileformat.metadata.J2KMetadata metadata, ParameterList? parameters = null)
        {
            RandomAccessIO? in_stream = null;
            InverseWT? invWT = null;

            try
            {
                in_stream = new ISRandomAccessIO(stream);
                var pl = parameters ?? new ParameterList(GetDefaultDecoderParameterList(decoder_pinfo));

                var ff = new FileFormatReader(in_stream);
                ff.readFileFormat();
                
                // Extract metadata
                metadata = ff.Metadata;
                
                if (ff.JP2FFUsed)
                {
                    in_stream.seek(ff.FirstCodeStreamPos);
                }

            // Decode image (rest of existing code)
            // ... continue with existing decoding logic ...
            
            var hi = new HeaderInfo();
            HeaderDecoder hd;
            try
            {
                hd = new HeaderDecoder(in_stream, pl, hi);
            }
            catch (EndOfStreamException e)
            {
                throw new InvalidOperationException("Codestream too short or bad header, unable to decode.", e);
            }

            var nCompCod = hd.NumComps;
            var nTiles = hi.sizValue.NumTiles;
            var decSpec = hd.DecoderSpecs;

            // Get demixed bitdepths
            var depth = new int[nCompCod];
            for (var i = 0; i < nCompCod; i++)
            {
                depth[i] = hd.GetOriginalBitDepth(i);
            }

            // **** Bit stream reader ****
            BitstreamReaderAgent breader;
            try
            {
                breader = BitstreamReaderAgent.createInstance(in_stream, hd, pl, decSpec, false, hi);
            }
            catch (IOException e)
            {
                throw new InvalidOperationException("Error while reading bit stream header or parsing packets.", e);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException("Cannot instantiate bit stream reader.", e);
            }

            // **** Entropy decoder ****
            EntropyDecoder entdec;
            try
            {
                entdec = hd.createEntropyDecoder(breader, pl);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException("Cannot instantiate entropy decoder.", e);
            }

            // **** ROI de-scaler ****
            ROIDeScaler roids;
            try
            {
                roids = hd.createROIDeScaler(entdec, pl, decSpec);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException("Cannot instantiate roi de-scaler.", e);
            }

            // **** Dequantizer ****
            Dequantizer deq;
            try
            {
                deq = HeaderDecoder.createDequantizer(roids, depth, decSpec);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException("Cannot instantiate dequantizer.", e);
            }

            // **** Inverse wavelet transform ***

            try
            {
                // full page inverse wavelet transform
                invWT = InverseWT.createInstance(deq, decSpec);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException("Cannot instantiate inverse wavelet transform.", e);
            }

            var res = breader.ImgRes;
            invWT.ImgResLevel = res;

            // **** Data converter **** (after inverse transform module)
            var converter = new ImgDataConverter(invWT, 0);

            // **** Inverse component transformation ****
            var ictransf = new InvCompTransf(converter, decSpec, depth, pl);

            // **** Inverse multiple component transform (MCT, ISO/IEC 15444-2) ****
            BlkImgDataSrc afterCt = ictransf;
            var mctStages = j2k.codestream.MctTransform.AssembleDecodeStages(hd.MctArrays, hd.MccSegments, hd.McoSegment);
            if (mctStages.Count > 0)
            {
                afterCt = j2k.image.mct.ComponentTransform.BuildChain(ictransf, mctStages, inverse: true);
            }

            // **** Inverse non-linearity point transform (NLT, ISO/IEC 15444-2) ****
            BlkImgDataSrc postCt = afterCt;
            if (hd.NLTSegments != null && hd.NLTSegments.Count > 0)
            {
                postCt = new InvNLT(afterCt, hd.NLTSegments);
            }

            // **** Inverse variable DC offset (DCO, ISO/IEC 15444-2) ****
            if (hd.DcoSegment != null)
            {
                postCt = new InvDCO(postCt, hd.DcoSegment);
            }

            // **** Color space mapping ****
            BlkImgDataSrc color;
            if (ff.JP2FFUsed && pl.GetParameter("nocolorspace").Equals("off"))
            {
                try
                {
                    var csMap = new ColorSpace(in_stream, hd, pl);
                    var channels = hd.createChannelDefinitionMapper(postCt, csMap);
                    var resampled = hd.createResampler(channels, csMap);
                    var palettized = hd.createPalettizedColorSpaceMapper(resampled, csMap);
                    color = hd.createColorSpaceMapper(palettized, csMap);
                }
                catch (ArgumentException e)
                {
                    throw new InvalidOperationException("Could not instantiate ICC profiler.", e);
                }
                catch (ColorSpaceException e)
                {
                    throw new InvalidOperationException("Error processing ColorSpace information.", e);
                }
            }
            else
            {
                // Skip colorspace mapping
                color = postCt;
            }

            // This is the last image in the decoding chain and should be
            // assigned by the last transformation:
            var decodedImage = color;
            if (color == null)
            {
                decodedImage = postCt;
            }
            var numComps = decodedImage.NumComps;
            var imgWidth = decodedImage.ImgWidth;

            // **** Copy to Bitmap ****

            var bitsUsed = new int[numComps];
            for (var j = 0; j < numComps; ++j) bitsUsed[j] = decodedImage.GetNomRangeBits(j);

            var dst = new InterleavedImage(imgWidth, decodedImage.ImgHeight, numComps, bitsUsed);

            var numTiles = decodedImage.GetNumTiles(null);

            var tIdx = 0;

            for (var y = 0; y < numTiles.y; y++)
            {
                // Loop on horizontal tiles
                for (var x = 0; x < numTiles.x; x++, tIdx++)
                {
                    decodedImage.SetTile(x, y);

                    var height = decodedImage.GetTileCompHeight(tIdx, 0);
                    var width = decodedImage.GetTileCompWidth(tIdx, 0);

                    var tOffx = decodedImage.GetCompULX(0)
                                - (int)Math.Ceiling(decodedImage.ImgULX / (double)decodedImage.GetCompSubsX(0));

                    var tOffy = decodedImage.GetCompULY(0)
                                - (int)Math.Ceiling(decodedImage.ImgULY / (double)decodedImage.GetCompSubsY(0));

                    var db = new DataBlkInt[numComps];
                    var ls = new int[numComps];
                    var mv = new int[numComps];
                    var fb = new int[numComps];
                    for (var i = 0; i < numComps; i++)
                    {
                        db[i] = new DataBlkInt();
                        // Use per-component nominal bits and fixed point
                        ls[i] = 1 << (decodedImage.GetNomRangeBits(i) - 1);
                        mv[i] = (1 << decodedImage.GetNomRangeBits(i)) - 1;
                        fb[i] = decodedImage.GetFixedPoint(i);
                    }

                    // Reuse arrays across rows to avoid per-pixel allocations
                    // Use checked block to catch integer overflow for very large images
                    int rowSize;
                    try
                    {
                        checked
                        {
                            rowSize = width * numComps;
                        }
                    }
                    catch (OverflowException)
                    {
                        throw new InvalidOperationException(
                            $"Image tile dimensions too large: width={width}, components={numComps}. " +
                            $"Row buffer size would exceed maximum array size.");
                    }
                    var rowvalues = new int[rowSize];
                    var k = new int[numComps];

                    for (var l = 0; l < height; l++)
                    {
                        // Map tile-local row 'l' to destination image line
                        var destLine = tOffy + l;
                        // Skip rows that end up above the destination image
                        if (destLine < 0) continue;
                        // Stop processing when we've reached the bottom of destination image
                        if (destLine >= dst.Height) break;

                        // Load each component line into its DataBlk and initialize indices
                        for (var i = 0; i < numComps; i++)
                        {
                            db[i].ulx = 0;
                            db[i].uly = l;
                            db[i].w = width;
                            db[i].h = 1;
                            decodedImage.GetInternCompData(db[i], i);
                            k[i] = db[i].offset; // start index for forward iteration
                        }

                        // Fill rowvalues left-to-right, writing component samples interleaved
                        for (var col = 0; col < width; col++)
                        {
                            // Use checked arithmetic to detect overflow in offset calculation
                            int baseOffset;
                            checked
                            {
                                baseOffset = col * numComps;
                            }
                            for (var comp = 0; comp < numComps; comp++)
                            {
                                var v = (db[comp].data_array[k[comp]++] >> fb[comp]) + ls[comp];
                                if (v < 0) v = 0;
                                else if (v > mv[comp]) v = mv[comp];
                                rowvalues[baseOffset + comp] = v;
                            }
                        }

                        dst.FillRow(tOffx, destLine, imgWidth, rowvalues);
                    }
                }
            }

            return dst;
            }
            finally
            {
                // Ensure resources are cleaned up even if an exception occurs
                // InverseWT owns the decoding chain and will clean up all upstream resources
                if (invWT != null)
                {
                    try
                    {
                        invWT.Close();
                    }
                    catch
                    {
                        // Suppress exceptions during cleanup to avoid masking the original exception
                    }
                }
            }
        }

        /// <summary>
        /// Decodes a JPEG2000 stream using modern configuration API.
        /// </summary>
        /// <param name="stream">The stream containing JPEG2000 data.</param>
        /// <param name="configuration">Modern decoder configuration.</param>
        /// <returns>The decoded image.</returns>
        public static InterleavedImage FromStream(Stream stream, Configuration.J2KDecoderConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));
            
            if (!configuration.IsValid)
                throw new ArgumentException($"Invalid configuration: {string.Join(", ", configuration.Validate())}");
            
            // Convert modern configuration to ParameterList
            var pl = configuration.ToParameterList();
            
            return FromStream(stream, pl);
        }

        /// <summary>
        /// Decodes a JPEG2000 stream using modern configuration API and returns metadata.
        /// </summary>
        /// <param name="stream">The stream containing JPEG2000 data.</param>
        /// <param name="metadata">Output parameter that receives the metadata.</param>
        /// <param name="configuration">Modern decoder configuration.</param>
        /// <returns>The decoded image.</returns>
        public static InterleavedImage FromStream(Stream stream, out j2k.fileformat.metadata.J2KMetadata metadata, Configuration.J2KDecoderConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));
            
            if (!configuration.IsValid)
                throw new ArgumentException($"Invalid configuration: {string.Join(", ", configuration.Validate())}");
            
            // Convert modern configuration to ParameterList
            var pl = configuration.ToParameterList();
            
            return FromStream(stream, out metadata, pl);
        }

        #endregion

        #region Decode Methods (return J2kDecodeResult)

        /// <summary>
        /// Decodes a JPEG 2000 file and returns both the image and any file-format metadata.
        /// </summary>
        public static J2kDecodeResult DecodeFile(string filename, ParameterList? parameters = null)
        {
            using var stream = FileStreamFactory.New(filename, "r");
            return DecodeStream(stream, parameters);
        }

        /// <summary>Decodes a JPEG 2000 file using modern configuration and returns image and metadata.</summary>
        public static J2kDecodeResult DecodeFile(string filename, Configuration.J2KDecoderConfiguration configuration)
        {
            using var stream = FileStreamFactory.New(filename, "r");
            return DecodeStream(stream, configuration);
        }

        /// <summary>Decodes JPEG 2000 data from a byte array and returns image and metadata.</summary>
        public static J2kDecodeResult DecodeBytes(byte[] data, ParameterList? parameters = null)
        {
            using var stream = new MemoryStream(data);
            return DecodeStream(stream, parameters);
        }

        /// <summary>Decodes JPEG 2000 data from a byte array using modern configuration and returns image and metadata.</summary>
        public static J2kDecodeResult DecodeBytes(byte[] data, Configuration.J2KDecoderConfiguration configuration)
        {
            using var stream = new MemoryStream(data);
            return DecodeStream(stream, configuration);
        }

        /// <summary>Decodes JPEG 2000 data from a <see cref="ReadOnlyMemory{T}"/> buffer and returns image and metadata.</summary>
        public static J2kDecodeResult DecodeBytes(ReadOnlyMemory<byte> data, ParameterList? parameters = null)
        {
            using var stream = MemoryStreamFromMemory(data);
            return DecodeStream(stream, parameters);
        }

        /// <summary>Decodes JPEG 2000 data from a <see cref="ReadOnlyMemory{T}"/> buffer using modern configuration and returns image and metadata.</summary>
        public static J2kDecodeResult DecodeBytes(ReadOnlyMemory<byte> data, Configuration.J2KDecoderConfiguration configuration)
        {
            using var stream = MemoryStreamFromMemory(data);
            return DecodeStream(stream, configuration);
        }

        /// <summary>Decodes a JPEG 2000 stream and returns both the image and any file-format metadata.</summary>
        public static J2kDecodeResult DecodeStream(Stream stream, ParameterList? parameters = null)
        {
            var image = FromStream(stream, out var metadata, parameters);
            return new J2kDecodeResult(image, metadata);
        }

        /// <summary>Decodes a JPEG 2000 stream using modern configuration and returns image and metadata.</summary>
        public static J2kDecodeResult DecodeStream(Stream stream, Configuration.J2KDecoderConfiguration configuration)
        {
            var image = FromStream(stream, out var metadata, configuration);
            return new J2kDecodeResult(image, metadata);
        }

        private static MemoryStream MemoryStreamFromMemory(ReadOnlyMemory<byte> data)
        {
            if (MemoryMarshal.TryGetArray(data, out var segment))
                return new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
            return new MemoryStream(data.ToArray());
        }

        #endregion

        #region Static Encoder Methods

        public static BlkImgDataSrc? CreateEncodableSource(Stream stream)
        {
            return CreateEncodableSource(new[] { stream });
        }

        public static BlkImgDataSrc? CreateEncodableSource(IEnumerable<Stream> streams)
        {
            if (streams == null)
            {
                throw new ArgumentNullException(nameof(streams));
            }

            var counter = 0;
            var ncomp = 0;
            var ppminput = false;
            var imageReaders = new List<ImgReader>();

            foreach (var stream in streams)
            {
                ++counter;
                var imgType = GetImageType(stream);

                switch (imgType)
                {
                    case "P5":
                        imageReaders.Add(new ImgReaderPGM(stream));
                        ncomp += 1;
                        break;
                    case "P6":
                        imageReaders.Add(new ImgReaderPPM(stream));
                        ncomp += 3;
                        ppminput = true;
                        break;
                    case "PG":
                        imageReaders.Add(new ImgReaderPGX(stream));
                        ncomp += 1;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(streams), "Invalid image type");
                }
            }

            if (ppminput && counter > 1)
                throw new ArgumentException("With PPM input format only one input stream can be specified.", nameof(streams));

            BlkImgDataSrc imgsrc;

            // **** ImgDataJoiner (if needed) ****
            if (ppminput || ncomp == 1)
            {
                // Just one input
                imgsrc = imageReaders[0];
            }
            else
            {
                // More than one reader => join all readers into 1
                var imgcmpidxs = new int[ncomp];
                imgsrc = new ImgDataJoiner(imageReaders, imgcmpidxs);
            }

            return imgsrc;
        }

        public static byte[] ToBytes(object imageObject, ParameterList? parameters = null)
        {
            var imgsrc = ImageFactory.ToPortableImageSource(imageObject);
            return ToBytes(imgsrc, parameters);
        }

        public static byte[] ToBytes(BlkImgDataSrc imgsrc, ParameterList? parameters = null)
        {
            return ToBytes(imgsrc, null, parameters);
        }

        /// <summary>
        /// Encodes an image source to JPEG2000 bytes with optional metadata.
        /// </summary>
        /// <param name="imgsrc">The image source to encode.</param>
        /// <param name="metadata">Optional metadata (comments, XML, UUIDs) to include.</param>
        /// <param name="parameters">Optional encoder parameters.</param>
        /// <returns>The encoded JPEG2000 data.</returns>
        public static byte[] ToBytes(BlkImgDataSrc imgsrc, j2k.fileformat.metadata.J2KMetadata? metadata, ParameterList? parameters = null)
            => ToBytes(imgsrc, metadata, parameters, null);

        /// <summary>
        /// Encodes the image, optionally applying JPEG 2000 Part 2 (ISO/IEC 15444-2)
        /// Non-linearity point transformation (NLT) segments. When <paramref name="nltSegments"/>
        /// is non-empty, the forward point transform is applied before compression, the codestream
        /// is branded with Part 2 capabilities (Rsiz + CAP), and the NLT marker segments are written.
        /// </summary>
        public static byte[] ToBytes(BlkImgDataSrc imgsrc, j2k.fileformat.metadata.J2KMetadata? metadata, ParameterList? parameters,
            System.Collections.Generic.IList<j2k.codestream.NLTMarkerSegment>? nltSegments,
            System.Collections.Generic.IList<j2k.codestream.MctEncodeSpec>? mctSpecs = null,
            j2k.codestream.DCOMarkerSegment? dcoSegment = null,
            j2k.codestream.AtkMarkerSegment? atkKernel = null)
        {
            if (imgsrc == null)
            {
                throw new ArgumentNullException(nameof(imgsrc), "Image source cannot be null. Use ImageFactory.ToPortableImageSource(image) to convert image objects to a portable source.");
            }

            // Initialize default parameters
            var defpl = GetDefaultEncoderParameterList(encoder_pinfo);

            // Create parameter list using defaults
            var pl = parameters ?? new ParameterList(defpl);

            // **** Arbitrary Transformation Kernel (ATK, ISO/IEC 15444-2) setup ****
            // The custom kernel replaces the Part 1 wavelet filter for all
            // tile-components, so it cannot be combined with an explicit 'Ffilters'
            // choice or the Part 1 inter-component transform (RCT/ICT).
            if (atkKernel != null)
            {
                atkKernel.Validate();
                if (pl.GetParameter("Ffilters") != null)
                {
                    throw new ArgumentException(
                        "Cannot combine the 'Ffilters' option with a custom ATK wavelet kernel.");
                }
                var mctParam = pl.GetParameter("Mct");
                if (mctParam != null && mctParam.Equals("on", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "The Part 1 component transform ('Mct on') cannot be combined with a custom ATK wavelet kernel.");
                }
                pl["Mct"] = "off";
            }

            var useFileFormat = false;
            var pphTile = false;
            var pphMain = false;
            var tempSop = false;
            var tempEph = false;

            // **** Get general parameters ****

            if (string.Equals(pl.GetParameter("file_format"), "on", StringComparison.OrdinalIgnoreCase))
            {
                useFileFormat = true;
                if (pl.GetParameter("rate") != null
                    && pl.GetFloatParameter("rate") != defpl.GetFloatParameter("rate"))
                {
                    warning("Specified bit-rate applies only on the codestream but not on the whole file.");
                }
            }

            if (pl.GetParameter("tiles") == null)
                throw new InvalidOperationException("No tiles option specified.");

            if (string.Equals(pl.GetParameter("pph_tile"), "on", StringComparison.OrdinalIgnoreCase))
            {
                pphTile = true;

                if (string.Equals(pl.GetParameter("Psop"), "off", StringComparison.OrdinalIgnoreCase))
                {
                    pl["Psop"] = "on";
                    tempSop = true;
                }
                if (string.Equals(pl.GetParameter("Peph"), "off", StringComparison.OrdinalIgnoreCase))
                {
                    pl["Peph"] = "on";
                    tempEph = true;
                }
            }

            if (string.Equals(pl.GetParameter("pph_main"), "on", StringComparison.OrdinalIgnoreCase))
            {
                pphMain = true;

                if (string.Equals(pl.GetParameter("Psop"), "off", StringComparison.OrdinalIgnoreCase))
                {
                    pl["Psop"] = "on";
                    tempSop = true;
                }
                if (string.Equals(pl.GetParameter("Peph"), "off", StringComparison.OrdinalIgnoreCase))
                {
                    pl["Peph"] = "on";
                    tempEph = true;
                }
            }

            if (pphTile && pphMain) error("Can't have packed packet headers in both main and tile headers", 2);

            if (pl.GetBooleanParameter("lossless") && pl.GetParameter("rate") != null
                && pl.GetFloatParameter("rate") != defpl.GetFloatParameter("rate"))
                throw new ArgumentException("Cannot use '-rate' and '-lossless' option at  the same time.");

            if (pl.GetParameter("rate") == null)
                throw new InvalidOperationException("Target bitrate not specified.");
            float rate;
            try
            {
                rate = pl.GetFloatParameter("rate");
                if (rate == -1)
                {
                    rate = float.MaxValue;
                }
            }
            catch (FormatException)
            {
                throw new ArgumentException($"Invalid value in 'rate' option: {pl.GetParameter("rate")}");
            }
            int pktspertp;
            try
            {
                pktspertp = pl.GetIntParameter("tile_parts");
                if (pktspertp != 0)
                {
                    if (string.Equals(pl.GetParameter("Psop"), "off", StringComparison.OrdinalIgnoreCase))
                    {
                        pl["Psop"] = "on";
                        tempSop = true;
                    }
                    if (string.Equals(pl.GetParameter("Peph"), "off", StringComparison.OrdinalIgnoreCase))
                    {
                        pl["Peph"] = "on";
                        tempEph = true;
                    }
                }
            }
            catch (FormatException)
            {
                throw new ArgumentException($"Invalid value in 'tile_parts' option: {pl.GetParameter("tile_parts")}");
            }

            // **** ImgReader ****
            var ncomp = imgsrc.NumComps;
            var ppminput = imgsrc.NumComps > 1;

            // **** Tiler ****
            // get nominal tile dimensions
            var stok =
                new StreamTokenizerSupport(new StringReader(pl.GetParameter("tiles")));
            stok.EOLIsSignificant(false);

            stok.NextToken();
            if (stok.ttype != StreamTokenizerSupport.TT_NUMBER)
                throw new ArgumentException($"An error occurred while parsing the tiles option: {pl.GetParameter("tiles")}");
            var tw = (int)stok.nval;
            stok.NextToken();
            if (stok.ttype != StreamTokenizerSupport.TT_NUMBER)
                throw new ArgumentException($"An error occurred while parsing the tiles option: {pl.GetParameter("tiles")}");
            var th = (int)stok.nval;

            // Validate tile dimensions
            // Both zero means no tiling, which is valid
            if ((tw == 0 && th != 0) || (tw != 0 && th == 0))
            {
                throw new ArgumentException(
                    $"Invalid tile dimensions: width={tw}, height={th}. " +
                    "Both dimensions must be zero (no tiling) or both must be positive.");
            }

            // Check for negative dimensions
            if (tw < 0 || th < 0)
            {
                throw new ArgumentException(
                    $"Invalid tile dimensions: width={tw}, height={th}. " +
                    "Tile dimensions cannot be negative.");
            }

            // Validate against image dimensions if tiling is enabled
            if (tw > 0 && th > 0)
            {
                var imgWidth = imgsrc.ImgWidth;
                var imgHeight = imgsrc.ImgHeight;

                // Warn if tile dimensions exceed image dimensions
                if (tw > imgWidth || th > imgHeight)
                {
                    warning(
                        $"Tile dimensions ({tw}x{th}) exceed image dimensions ({imgWidth}x{imgHeight}). " +
                        "This will result in a single tile.");
                }

                // Check for unreasonably large tile dimensions that could cause overflow
                const int maxReasonableTileSize = 65536; // 64K pixels per dimension
                if (tw > maxReasonableTileSize || th > maxReasonableTileSize)
                {
                    throw new ArgumentException(
                        $"Tile dimensions too large: width={tw}, height={th}. " +
                        $"Maximum supported tile dimension is {maxReasonableTileSize} pixels.");
                }

                // Validate that tile size won't cause integer overflow when calculating tile count
                try
                {
                    checked
                    {
                        // Test calculation to verify no overflow
                        var testTileCountX = (imgWidth + tw - 1) / tw;
                        var testTileCountY = (imgHeight + th - 1) / th;
                        var testTotalTiles = testTileCountX * testTileCountY;
                        
                        // Sanity check on total tile count
                        if (testTotalTiles > 65535)
                        {
                            warning(
                                $"Image will be split into {testTotalTiles} tiles. " +
                                "This may result in very large file headers and slow encoding.");
                        }
                    }
                }
                catch (OverflowException)
                {
                    throw new ArgumentException(
                        $"Tile dimensions ({tw}x{th}) would result in integer overflow when calculating tile count.");
                }

                // Warn if tiles are very small (performance concern)
                const int minRecommendedTileSize = 64;
                if (tw < minRecommendedTileSize || th < minRecommendedTileSize)
                {
                    warning(
                        $"Tile dimensions ({tw}x{th}) are very small. " +
                        $"Tiles smaller than {minRecommendedTileSize}x{minRecommendedTileSize} may result in poor compression efficiency.");
                }
            }

            // Get image reference point
            var refs = pl.GetParameter("ref").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int refx;
            int refy;
            try
            {
                refx = int.Parse(refs[0]);
                refy = int.Parse(refs[1]);
            }
            catch (IndexOutOfRangeException)
            {
                throw new ArgumentException("Error while parsing 'ref' option");
            }
            catch (FormatException)
            {
                throw new ArgumentException("Invalid number type in 'ref' option");
            }
            if (refx < 0 || refy < 0)
            {
                throw new ArgumentException("Invalid value in 'ref' option ");
            }

            // Get tiling reference point
            var trefs = pl.GetParameter("tref").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int trefx;
            int trefy;
            try
            {
                trefx = int.Parse(trefs[0]);
                trefy = int.Parse(trefs[1]);
            }
            catch (IndexOutOfRangeException)
            {
                throw new ArgumentException("Error while parsing 'tref' option");
            }
            catch (FormatException)
            {
                throw new ArgumentException("Invalid number type in 'tref' option");
            }
            if (trefx < 0 || trefy < 0 || trefx > refx || trefy > refy)
            {
                throw new ArgumentException("Invalid value in 'tref' option ");
            }

            // Instantiate tiler
            Tiler imgtiler;
            try
            {
                imgtiler = new Tiler(imgsrc, refx, refy, trefx, trefy, tw, th);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException($"Could not tile image{((e.Message != null) ? (":\n" + e.Message) : "")}", e);
            }
            var ntiles = imgtiler.GetNumTiles();

            // **** Encoder specifications ****
            var encSpec = new EncoderSpecs(ntiles, ncomp, imgsrc, pl);

            // **** ATK: substitute the custom kernel for the default wavelet filter ****
            var hasAtk = atkKernel != null;
            if (atkKernel != null)
            {
                var reversibleQuant = ((string)encSpec.qts.GetDefault()).Equals("reversible");
                if (atkKernel.Reversible != reversibleQuant)
                {
                    throw new ArgumentException(atkKernel.Reversible
                        ? "A reversible ATK kernel requires reversible quantization; enable the 'lossless' option or set 'Qtype reversible'."
                        : "An irreversible ATK kernel cannot be used with reversible quantization; disable the 'lossless' option.");
                }
                j2k.wavelet.analysis.AnWTFilter atkFilter = atkKernel.Reversible
                    ? new j2k.wavelet.analysis.AnWTFilterIntArbitrary(atkKernel)
                    : (j2k.wavelet.analysis.AnWTFilter)new j2k.wavelet.analysis.AnWTFilterFloatArbitrary(atkKernel);
                var atkFilters = new j2k.wavelet.analysis.AnWTFilter[2][];
                atkFilters[0] = new[] { atkFilter };
                atkFilters[1] = new[] { atkFilter };
                encSpec.wfs.SetDefault(atkFilters);
            }

            // **** Component transformation ****
            if (ppminput && pl.GetParameter("Mct") != null && pl.GetParameter("Mct").Equals("off"))
            {
                FacilityManager.GetMsgLogger()
                    .printmsg(
                        MsgLogger_Fields.WARNING,
                        "Input image is RGB and no color transform has "
                        + "been specified. Compression performance and "
                        + "image quality might be greatly degraded. Use "
                        + "the 'Mct' option to specify a color transform");
            }
            // **** Forward variable DC offset (DCO, ISO/IEC 15444-2) ****
            var hasDco = dcoSegment != null;
            BlkImgDataSrc ctSource = imgtiler;
            if (hasDco)
            {
                ctSource = new ForwDCO(imgtiler, dcoSegment);
            }

            // **** Forward non-linearity point transform (NLT, ISO/IEC 15444-2) ****
            var hasNlt = nltSegments != null && nltSegments.Count > 0;
            if (hasNlt)
            {
                ctSource = new j2k.image.nlt.ForwNLT(ctSource, nltSegments);
            }

            // **** Forward multiple component transform (MCT, ISO/IEC 15444-2) ****
            var hasMct = mctSpecs != null && mctSpecs.Count > 0;
            j2k.codestream.McoMarkerSegment? mctMco = null;
            System.Collections.Generic.List<j2k.codestream.MctArrayMarkerSegment>? mctArraysToWrite = null;
            System.Collections.Generic.List<j2k.codestream.MccMarkerSegment>? mctMccsToWrite = null;
            if (hasMct)
            {
                var forwardStages = j2k.codestream.MctTransform.BuildForwardStages(mctSpecs);
                ctSource = j2k.image.mct.ComponentTransform.BuildChain(ctSource, forwardStages, inverse: false);

                var built = j2k.codestream.MctTransform.BuildMarkers(mctSpecs);
                mctArraysToWrite = built.Arrays;
                mctMccsToWrite = built.Mccs;
                mctMco = built.Mco;
            }

            ForwCompTransf fctransf;
            try
            {
                fctransf = new ForwCompTransf(ctSource, encSpec);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException(
                    $"Could not instantiate forward component transformation{((e.Message != null) ? (":\n" + e.Message) : "")}", e);
            }

            // **** ImgDataConverter ****
            var converter = new ImgDataConverter(fctransf);


            // **** ForwardWT ****
            ForwardWT dwt;
            try
            {
                dwt = ForwardWT.createInstance(converter, pl, encSpec);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException($"Could not instantiate wavelet transform{((e.Message != null) ? (":\n" + e.Message) : "")}", e);
            }

            // **** Quantizer ****
            Quantizer quant;
            try
            {
                quant = Quantizer.createInstance(dwt, encSpec);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException($"Could not instantiate quantizer{((e.Message != null) ? (":\n" + e.Message) : "")}", e);
            }

            // **** ROIScaler ****
            ROIScaler rois;
            try
            {
                rois = ROIScaler.createInstance(quant, pl, encSpec);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException($"Could not instantiate ROI scaler{((e.Message != null) ? (":\n" + e.Message) : "")}", e);
            }

            // **** EntropyCoder ****
            EntropyCoder ecoder;
            try
            {
                ecoder = EntropyCoder.createInstance(
                    rois,
                    pl,
                    encSpec.cblks,
                    encSpec.pss,
                    encSpec.bms,
                    encSpec.mqrs,
                    encSpec.rts,
                    encSpec.css,
                    encSpec.sss,
                    encSpec.lcs,
                    encSpec.tts);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException($"Could not instantiate entropy coder{((e.Message != null) ? (":\n" + e.Message) : "")}", e);
            }

            // **** CodestreamWriter ****
            using (var outStream = new MemoryStream())
            {
                CodestreamWriter bwriter;
                try
                {
                    // Rely on rate allocator to limit amount of data
                    bwriter = new FileCodestreamWriter(outStream, int.MaxValue);
                }
                catch (IOException e)
                {
                    throw new InvalidOperationException($"Could not open output stream for writing: {e.Message}", e);
                }

                // **** Rate allocator ****
                PostCompRateAllocator ralloc;
                try
                {
                    ralloc = PostCompRateAllocator.createInstance(ecoder, pl, rate, bwriter, encSpec);
                }
                catch (ArgumentException e)
                {
                    throw new InvalidOperationException($"Could not instantiate rate allocator{((e.Message != null) ? (":\n" + e.Message) : "")}", e);
                }

                // **** HeaderEncoder ****
                var imsigned = Enumerable.Repeat(false, ncomp).ToArray();   // TODO Consider supporting signed components.
                var headenc = new HeaderEncoder(imgsrc, imsigned, dwt, imgtiler, encSpec, rois, ralloc, pl);
                if (hasDco)
                {
                    headenc.DcoSegment = dcoSegment;
                }
                if (hasNlt)
                {
                    headenc.NLTSegments = nltSegments;
                }
                if (hasAtk)
                {
                    headenc.AtkSegment = atkKernel;
                }
                if (hasMct)
                {
                    headenc.MctArrays = mctArraysToWrite;
                    headenc.MccSegments = mctMccsToWrite;
                    headenc.McoSegment = mctMco;

                    var cbdDepths = new byte[ncomp];
                    for (var ci = 0; ci < ncomp; ci++)
                    {
                        var bits = imgsrc.GetNomRangeBits(ci);
                        cbdDepths[ci] = (byte)(((bits - 1) & 0x7F) | (imgsrc.IsOrigSigned(ci) ? 0x80 : 0x00));
                    }
                    headenc.CbdSegment = new j2k.codestream.CbdMarkerSegment { ComponentDepths = cbdDepths };
                }
                ralloc.HeaderEncoder = headenc;

                // **** Write header to be able to estimate header overhead ****
                headenc.encodeMainHeader();

                // **** Initialize rate allocator, with proper header
                // overhead. This will also encode all the data ****
                ralloc.initialize();

                // **** Write header (final) ****
                headenc.reset();
                headenc.encodeMainHeader();

                // Insert header into the codestream
                bwriter.commitBitstreamHeader(headenc);

                // **** Now do the rate-allocation and write result ****
                ralloc.runAndWrite();

                // **** Done ****
                bwriter.Close();

                // **** Calculate file length ****
                var fileLength = bwriter.Length;

                // **** Tile-parts and packed packet headers ****
                if (pktspertp > 0 || pphTile || pphMain)
                {
                    try
                    {
                        var cm = new CodestreamManipulator(
                            outStream,
                            ntiles,
                            pktspertp,
                            pphMain,
                            pphTile,
                            tempSop,
                            tempEph);
                        fileLength += cm.doCodestreamManipulation();
                        //String res="";
                        if (pktspertp > 0)
                        {
                            FacilityManager.GetMsgLogger()
                                .println(
                                    $"Created tile-parts containing at most {pktspertp} packets per tile.",
                                    4,
                                    6);
                        }
                        if (pphTile)
                        {
                            FacilityManager.GetMsgLogger().println("Moved packet headers to tile headers", 4, 6);
                        }
                        if (pphMain)
                        {
                            FacilityManager.GetMsgLogger().println("Moved packet headers to main header", 4, 6);
                        }
                    }
                    catch (IOException e)
                    {
                        throw new InvalidOperationException($"Error while creating tile-parts or packed packet headers: {e.Message}", e);
                    }
                }

                // **** File Format ****
                if (useFileFormat)
                {
                    // Auto-generate Reader Requirements for JPX output when Part 2 features are active.
                    // The rreq box is required in every conformant JPX file (ISO/IEC 15444-2 §M.9.2).
                    var isJpx = hasNlt || hasMct || hasDco || hasAtk
                                || (metadata?.HasJpxBoxes ?? false)
                                || (metadata?.UseJpxBrand ?? false);
                    if (isJpx)
                    {
                        metadata ??= new j2k.fileformat.metadata.J2KMetadata();
                        metadata.ReaderRequirements ??= j2k.fileformat.metadata.ReaderRequirementsBox.BuildForJpx(hasMct, hasNlt, hasDco, hasAtk);
                    }

                    try
                    {
                        var nc = imgsrc.NumComps;
                        var bpc = new int[nc];
                        for (var comp = 0; comp < nc; comp++)
                        {
                            bpc[comp] = imgsrc.GetNomRangeBits(comp);
                        }

                        outStream.Seek(0, SeekOrigin.Begin);
                        var ffw = new FileFormatWriter(
                            outStream,
                            imgsrc.ImgHeight,
                            imgsrc.ImgWidth,
                            nc,
                            bpc,
                            fileLength);

                        // Attach metadata if provided
                        if (metadata != null)
                        {
                            ffw.Metadata = metadata;
                        }
                        
                        fileLength += ffw.writeFileFormat();
                    }
                    catch (IOException e)
                    {
                        throw new InvalidOperationException($"Error while writing JP2 file format: {e.Message}");
                    }
                }

                // **** Close image readers ***

                imgsrc.Close();

                return outStream.ToArray();
            }
        }

        /// <summary>
        /// Encodes an image object to JPEG 2000 bytes using modern configuration API.
        /// </summary>
        /// <param name="imageObject">The image to encode (SKBitmap, Bitmap, etc.).</param>
        /// <param name="configuration">Modern encoder configuration.</param>
        /// <returns>The encoded JPEG 2000 data.</returns>
        public static byte[] ToBytes(object imageObject, Configuration.J2KEncoderConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));
            
            if (!configuration.IsValid)
                throw new ArgumentException($"Invalid configuration: {string.Join(", ", configuration.Validate())}");
            
            var imgsrc = ImageFactory.ToPortableImageSource(imageObject);
            return ToBytes(imgsrc, configuration);
        }
        
        /// <summary>
        /// Encodes an image source to JPEG 2000 bytes using modern configuration API.
        /// </summary>
        /// <param name="imgsrc">The image source to encode.</param>
        /// <param name="configuration">Modern encoder configuration.</param>
        /// <returns>The encoded JPEG 2000 data.</returns>
        public static byte[] ToBytes(BlkImgDataSrc imgsrc, Configuration.J2KEncoderConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));
            
            if (!configuration.IsValid)
                throw new ArgumentException($"Invalid configuration: {string.Join(", ", configuration.Validate())}");
            
            // Convert modern configuration to ParameterList
            var pl = configuration.ToParameterList();
            
            // Handle ROI if configured
            j2k.fileformat.metadata.J2KMetadata? metadata = null;
            // Note: ROI is handled through ParameterList in the existing encoding pipeline
            
            return ToBytes(imgsrc, metadata, pl);
        }
        
        /// <summary>
        /// Encodes an image source to JPEG 2000 bytes using modern configuration API with metadata.
        /// </summary>
        /// <param name="imgsrc">The image source to encode.</param>
        /// <param name="metadata">Optional metadata to include in the JP2 file.</param>
        /// <param name="configuration">Modern encoder configuration.</param>
        /// <returns>The encoded JPEG 2000 data.</returns>
        public static byte[] ToBytes(BlkImgDataSrc imgsrc, j2k.fileformat.metadata.J2KMetadata? metadata, Configuration.J2KEncoderConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));
            
            if (!configuration.IsValid)
                throw new ArgumentException($"Invalid configuration: {string.Join(", ", configuration.Validate())}");
            
            // Convert modern configuration to ParameterList
            var pl = configuration.ToParameterList();
            
            return ToBytes(imgsrc, metadata, pl);
        }

        #endregion

        #region WriteTo Methods

        /// <summary>
        /// Encodes an image and writes the result directly to <paramref name="output"/>.
        /// </summary>
        /// <remarks>
        /// This is a convenience wrapper over <see cref="ToBytes(BlkImgDataSrc,ParameterList)"/>.
        /// The encoded bytes are currently buffered internally before being written to the stream;
        /// a future version may eliminate that intermediate allocation for seekable streams.
        /// </remarks>
        public static void WriteTo(Stream output, BlkImgDataSrc imgsrc, ParameterList? parameters = null)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            var data = ToBytes(imgsrc, parameters);
            output.Write(data, 0, data.Length);
        }

        /// <summary>Encodes with metadata and writes to <paramref name="output"/>.</summary>
        public static void WriteTo(Stream output, BlkImgDataSrc imgsrc,
            j2k.fileformat.metadata.J2KMetadata? metadata, ParameterList? parameters = null)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            var data = ToBytes(imgsrc, metadata, parameters);
            output.Write(data, 0, data.Length);
        }

        /// <summary>Encodes with metadata and Part 2 transforms, then writes to <paramref name="output"/>.</summary>
        public static void WriteTo(Stream output, BlkImgDataSrc imgsrc,
            j2k.fileformat.metadata.J2KMetadata? metadata, ParameterList? parameters,
            System.Collections.Generic.IList<j2k.codestream.NLTMarkerSegment>? nltSegments,
            System.Collections.Generic.IList<j2k.codestream.MctEncodeSpec>? mctSpecs = null,
            j2k.codestream.DCOMarkerSegment? dcoSegment = null,
            j2k.codestream.AtkMarkerSegment? atkKernel = null)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            var data = ToBytes(imgsrc, metadata, parameters, nltSegments, mctSpecs, dcoSegment, atkKernel);
            output.Write(data, 0, data.Length);
        }

        /// <summary>Encodes using modern configuration and writes to <paramref name="output"/>.</summary>
        public static void WriteTo(Stream output, BlkImgDataSrc imgsrc,
            Configuration.J2KEncoderConfiguration configuration)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            var d1 = ToBytes(imgsrc, configuration);
            output.Write(d1, 0, d1.Length);
        }

        /// <summary>Encodes using modern configuration with metadata and writes to <paramref name="output"/>.</summary>
        public static void WriteTo(Stream output, BlkImgDataSrc imgsrc,
            j2k.fileformat.metadata.J2KMetadata? metadata,
            Configuration.J2KEncoderConfiguration configuration)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            var d2 = ToBytes(imgsrc, metadata, configuration);
            output.Write(d2, 0, d2.Length);
        }

        #endregion

        #region Async Methods

        /// <summary>Decodes a JPEG 2000 stream asynchronously and returns image and metadata.</summary>
        public static Task<J2kDecodeResult> DecodeStreamAsync(Stream stream,
            ParameterList? parameters = null, CancellationToken cancellationToken = default)
            => Task.Run(() => DecodeStream(stream, parameters), cancellationToken);

        /// <summary>Decodes a JPEG 2000 stream asynchronously using modern configuration.</summary>
        public static Task<J2kDecodeResult> DecodeStreamAsync(Stream stream,
            Configuration.J2KDecoderConfiguration configuration,
            CancellationToken cancellationToken = default)
            => Task.Run(() => DecodeStream(stream, configuration), cancellationToken);

        /// <summary>Decodes JPEG 2000 data from a byte array asynchronously.</summary>
        public static Task<J2kDecodeResult> DecodeBytesAsync(byte[] data,
            ParameterList? parameters = null, CancellationToken cancellationToken = default)
            => Task.Run(() => DecodeBytes(data, parameters), cancellationToken);

        /// <summary>Decodes JPEG 2000 data from a byte array asynchronously using modern configuration.</summary>
        public static Task<J2kDecodeResult> DecodeBytesAsync(byte[] data,
            Configuration.J2KDecoderConfiguration configuration,
            CancellationToken cancellationToken = default)
            => Task.Run(() => DecodeBytes(data, configuration), cancellationToken);

        /// <summary>Decodes JPEG 2000 data from a <see cref="ReadOnlyMemory{T}"/> buffer asynchronously.</summary>
        public static Task<J2kDecodeResult> DecodeBytesAsync(ReadOnlyMemory<byte> data,
            ParameterList? parameters = null, CancellationToken cancellationToken = default)
            => Task.Run(() => DecodeBytes(data, parameters), cancellationToken);

        /// <summary>Decodes JPEG 2000 data from a <see cref="ReadOnlyMemory{T}"/> buffer asynchronously using modern configuration.</summary>
        public static Task<J2kDecodeResult> DecodeBytesAsync(ReadOnlyMemory<byte> data,
            Configuration.J2KDecoderConfiguration configuration,
            CancellationToken cancellationToken = default)
            => Task.Run(() => DecodeBytes(data, configuration), cancellationToken);

        /// <summary>Encodes an image source asynchronously and returns the encoded bytes.</summary>
        public static Task<byte[]> ToBytesAsync(BlkImgDataSrc imgsrc,
            ParameterList? parameters = null, CancellationToken cancellationToken = default)
            => Task.Run(() => ToBytes(imgsrc, parameters), cancellationToken);

        /// <summary>Encodes an image source asynchronously using modern configuration.</summary>
        public static Task<byte[]> ToBytesAsync(BlkImgDataSrc imgsrc,
            Configuration.J2KEncoderConfiguration configuration,
            CancellationToken cancellationToken = default)
            => Task.Run(() => ToBytes(imgsrc, configuration), cancellationToken);

        /// <summary>Encodes an image source asynchronously and writes the result to <paramref name="output"/>.</summary>
        public static Task WriteToAsync(Stream output, BlkImgDataSrc imgsrc,
            ParameterList? parameters = null, CancellationToken cancellationToken = default)
            => Task.Run(() => WriteTo(output, imgsrc, parameters), cancellationToken);

        /// <summary>Encodes an image source asynchronously using modern configuration and writes to <paramref name="output"/>.</summary>
        public static Task WriteToAsync(Stream output, BlkImgDataSrc imgsrc,
            Configuration.J2KEncoderConfiguration configuration,
            CancellationToken cancellationToken = default)
            => Task.Run(() => WriteTo(output, imgsrc, configuration), cancellationToken);

        #endregion

        #region Default Parameter Loaders

        public static ParameterList GetDefaultDecoderParameterList(string?[][] pinfo)
        {
            var pl = new ParameterList();
            string?[][] str;

            str = BitstreamReaderAgent.ParameterInfo;
            if (str != null) for (var i = str.Length - 1; i >= 0; i--) pl[str[i][0]!] = str[i][3]!;

            str = EntropyDecoder.ParameterInfo;
            if (str != null) for (var i = str.Length - 1; i >= 0; i--) pl[str[i][0]!] = str[i][3]!;

            str = ROIDeScaler.ParameterInfo;
            if (str != null) for (var i = str.Length - 1; i >= 0; i--) pl[str[i][0]!] = str[i][3]!;

            str = Dequantizer.ParameterInfo;
            if (str != null) for (var i = str.Length - 1; i >= 0; i--) pl[str[i][0]!] = str[i][3]!;

            str = InvCompTransf.ParameterInfo;
            if (str != null) for (var i = str.Length - 1; i >= 0; i--) pl[str[i][0]!] = str[i][3]!;

            str = HeaderDecoder.ParameterInfo;
            if (str != null) for (var i = str.Length - 1; i >= 0; i--) pl[str[i][0]!] = str[i][3]!;

            str = ColorSpaceMapper.ParameterInfo;
            if (str != null) for (var i = str.Length - 1; i >= 0; i--) pl[str[i][0]!] = str[i][3]!;

            str = pinfo ?? decoder_pinfo;
            if (str != null) for (var i = str.Length - 1; i >= 0; i--) pl[str[i][0]!] = str[i][3]!;

            return pl;
        }

        public static ParameterList GetDefaultDecoderParameterList()
        {
            return GetDefaultDecoderParameterList(decoder_pinfo);
        }

        public static ParameterList GetDefaultEncoderParameterList(string?[][] pinfo)
        {
            var pl = new ParameterList();
            string?[][] str;

            str = pinfo ?? encoder_pinfo;
            if (str != null) for (var i = str.Length - 1; i >= 0; i--) pl[str[i][0]!] = str[i][3]!;

            str = ForwCompTransf.ParameterInfo;
            if (str != null) for (var i = str.Length - 1; i >= 0; i--) pl[str[i][0]!] = str[i][3]!;

            str = AnWTFilter.ParameterInfo;
            if (str != null) for (var i = str.Length - 1; i >= 0; i--) pl[str[i][0]!] = str[i][3]!;

            str = ForwardWT.ParameterInfo;
            if (str != null) for (var i = str.Length - 1; i >= 0; i--) pl[str[i][0]!] = str[i][3]!;

            str = Quantizer.ParameterInfo;
            if (str != null) for (var i = str.Length - 1; i >= 0; i--) pl[str[i][0]!] = str[i][3]!;

            str = ROIScaler.ParameterInfo;
            if (str != null) for (var i = str.Length - 1; i >= 0; i--) pl[str[i][0]!] = str[i][3]!;

            str = EntropyCoder.ParameterInfo;
            if (str != null) for (var i = str.Length - 1; i >= 0; i--) pl[str[i][0]!] = str[i][3]!;

            str = HeaderEncoder.ParameterInfo;
            if (str != null) for (var i = str.Length - 1; i >= 0; i--) pl[str[i][0]!] = str[i][3]!;

            str = PostCompRateAllocator.ParameterInfo;
            if (str != null) for (var i = str.Length - 1; i >= 0; i--) pl[str[i][0]!] = str[i][3]!;

            str = PktEncoder.ParameterInfo;
            if (str != null) for (var i = str.Length - 1; i >= 0; i--) pl[str[i][0]!] = str[i][3]!;

            return pl;
        }

        public static ParameterList GetDefaultEncoderParameterList()
        {
            return GetDefaultEncoderParameterList(encoder_pinfo);
        }

        #endregion

        #region Decoder Parameters

        private static readonly string?[][] decoder_pinfo =
            {
                new string?[]
                    {
                        "u", "[on|off]",
                        "Prints usage information. "
                        + "If specified all other arguments (except 'v') are ignored",
                        "off"
                    },
                new string?[]
                    {
                        "v", "[on|off]", "Prints version and copyright information",
                        "off"
                    },
                new string?[]
                    {
                        "verbose", "[on|off]",
                        "Prints information about the decoded codestream", "on"
                    },
                new string?[]
                    {
                        "pfile", "<filename>",
                        "Loads the arguments from the specified file. Arguments that are "
                        + "specified on the command line override the ones from the file.\n"
                        + "The arguments file is a simple text file with one argument per "
                        + "line of the following form:\n"
                        + "  <argument name>=<argument value>\n"
                        + "If the argument is of boolean type (i.e. its presence turns a "
                        + "feature on), then the 'on' value turns it on, while the 'off' "
                        + "value turns it off. The argument name does not include the '-' "
                        + "or '+' character. Long lines can be broken into several lines "
                        + "by terminating them with '\\'. Lines starting with '#' are "
                        + "considered as comments. This option is not recursive: any 'pfile' "
                        + "argument appearing in the file is ignored",
                        null
                    },
                new string?[]
                    {
                        "res", "<resolution level index>",
                        "The resolution level at which to reconstruct the image "
                        + " (0 means the lowest available resolution whereas the maximum "
                        + "resolution level corresponds to the original image resolution). "
                        + "If the given index"
                        + " is greater than the number of available resolution levels of the "
                        + "compressed image, the image is reconstructed at its highest "
                        + "resolution (among all tile-components). Note that this option"
                        + " affects only the inverse wavelet transform and not the number "
                        + " of bytes read by the codestream parser: this number of bytes "
                        + "depends only on options '-nbytes' or '-rate'.",
                        null
                    },
                new string?[]
                    {
                        "i", "<filename or url>",
                        "The file containing the JPEG 2000 compressed data. This can be "
                        + "either a JPEG 2000 codestream or a JP2 file containing a "
                        + "JPEG 2000 "
                        + "codestream. In the latter case the first codestream in the file "
                        + "will be decoded. If an URL is specified (e.g., http://...) "
                        + "the data will be downloaded and cached in memory before decoding. "
                        + "This is intended for easy use in applets, but it is not a very "
                        + "efficient way of decoding network served data.",
                        null
                    },
                new string?[]
                    {
                        "o", "<filename>",
                        "This is the name of the file to which the decompressed image "
                        + "is written. If no output filename is given, the image is "
                        + "displayed on the screen. "
                        + "Output file format is PGX by default. If the extension"
                        + " is '.pgm' then a PGM file is written as output, however this is "
                        + "only permitted if the component bitdepth does not exceed 8. If "
                        + "the extension is '.ppm' then a PPM file is written, however this "
                        + "is only permitted if there are 3 components and none of them has "
                        + "a bitdepth of more than 8. If there is more than 1 component, "
                        + "suffices '-1', '-2', '-3', ... are added to the file name, just "
                        + "before the extension, except for PPM files where all three "
                        + "components are written to the same file.",
                        null
                    },
                new string?[]
                    {
                        "rate", "<decoding rate in bpp>",
                        "Specifies the decoding rate in bits per pixel (bpp) where the "
                        + "number of pixels is related to the image's original size (Note:" 
                        + " this number is not affected by the '-res' option). If it is equal"
                        + "to -1, the whole codestream is decoded. "
                        + "The codestream is either parsed (default) or truncated depending "
                        + "the command line option '-parsing'. To specify the decoding "
                        + "rate in bytes, use '-nbytes' options instead.",
                        "-1"
                    },
                new string?[]
                    {
                        "nbytes", "<decoding rate in bytes>",
                        "Specifies the decoding rate in bytes. "
                        + "The codestream is either parsed (default) or truncated depending "
                        + "the command line option '-parsing'. To specify the decoding "
                        + "rate in bits per pixel, use '-rate' options instead.",
                        "-1"
                    },
                new string?[]
                    {
                        "parsing", null,
                        "Enable or not the parsing mode when decoding rate is specified "
                        + "('-nbytes' or '-rate' options). If it is false, the codestream "
                        + "is decoded as if it were truncated to the given rate. If it is "
                        + "true, the decoder creates, truncates and decodes a virtual layer"
                        + " progressive codestream with the same truncation points in each "
                        + "code-block.",
                        "on"
                    },
                new string?[]
                    {
                        "ncb_quit", "<max number of code blocks>",
                        "Use the ncb and lbody quit conditions. If state information is "
                        + "found for more code blocks than is indicated with this option, "
                        + "the decoder "
                        + "will decode using only information found before that point. "
                        + "Using this otion implies that the 'rate' or 'nbyte' parameter "
                        + "is used to indicate the lbody parameter which is the number of "
                        + "packet body bytes the decoder will decode.",
                        "-1"
                    },
                new string?[]
                    {
                        "l_quit", "<max number of layers>",
                        "Specifies the maximum number of layers to decode for any code-"
                        + "block",
                        "-1"
                    },
                new string?[]
                    {
                        "m_quit", "<max number of bit planes>",
                        "Specifies the maximum number of bit planes to decode for any code"
                        + "-block",
                        "-1"
                    },
                new string?[]
                    {
                        "poc_quit", null,
                        "Specifies the whether the decoder should only decode code-blocks "
                        + "included in the first progression order.",
                        "off"
                    },
                new string?[]
                    {
                        "one_tp", null,
                        "Specifies whether the decoder should only decode the first "
                        + "tile part of each tile.",
                        "off"
                    },
                new string?[]
                    {
                        "comp_transf", null,
                        "Specifies whether the component transform indicated in the "
                        + "codestream should be used.",
                        "on"
                    },
                new string?[]
                    {
                        "debug", null,
                        "Print debugging messages when an error is encountered.",
                        "off"
                    },
                new string?[]
                    {
                        "cdstr_info", null,
                        "Display information about the codestream. This information is: "
                        + "\n- Marker segments value in main and tile-part headers,"
                        + "\n- Tile-part length and position within the code-stream.",
                        "off"
                    },
                new string?[]
                    {
                        "nocolorspace", null,
                        "Ignore any colorspace information in the image.", "off"
                    },
                new string?[]
                    {
                        "colorspace_debug", null,
                        "Print debugging messages when an error is encountered in the"
                        + " colorspace module.",
                        "off"
                    }
            };

        #endregion

        #region Encoder Parameters

        private static readonly string?[][] encoder_pinfo =
            {
                new string?[]
                    {
                        "debug", null,
                        "Print debugging messages when an error is encountered.",
                        "off"
                    },
                new string?[]
                    {
                        "disable_jp2_extension", "[on|off]",
                        "JJ2000 automatically adds .jp2 extension when using 'file_format'"
                        + "option. This option disables it when on.",
                        "off"
                    },
                new string?[]
                    {
                        "file_format", "[on|off]",
                        "Puts the JPEG 2000 codestream in a JP2 file format wrapper.",
                        "on"
                    },
                new string?[]
                    {
                        "pph_tile", "[on|off]",
                        "Packs the packet headers in the tile headers.", "off"
                    },
                new string?[]
                    {
                        "pph_main", "[on|off]",
                        "Packs the packet headers in the main header.", "off"
                    },
                new string?[]
                    {
                        "pfile", "<filename of arguments file>",
                        "Loads the arguments from the specified file. Arguments that are "
                        + "specified on the command line override the ones from the file.\n"
                        + "The arguments file is a simple text file with one argument per "
                        + "line of the following form:\n"
                        + "  <argument name>=<argument value>\n"
                        + "If the argument is of boolean type (i.e. its presence turns a "
                        + "feature on), then the 'on' value turns it on, while the 'off' "
                        + "value turns it off. The argument name does not include the '-' "
                        + "or '+' character. Long lines can be broken into several lines "
                        + "by terminating them with '\'. Lines starting with '#' are "
                        + "considered as comments. This option is not recursive: any 'pfile' "
                        + "argument appearing in the file is ignored.",
                        null
                    },
                new string?[]
                    {
                        "tile_parts", "<packets per tile-part>",
                        "This option specifies the maximum number of packets to have in "
                        + "one tile-part. 0 means include all packets in first tile-part "
                        + "of each tile",
                        "0"
                    },
                new string?[]
                    {
                        "tiles", "<nominal tile width> <nominal tile height>",
                        "This option specifies the maximum tile dimensions to use. "
                        + "If both dimensions are 0 then no tiling is used.",
                        "0 0"
                    },
                new string?[]
                    {
                        "ref", "<x> <y>",
                        "Sets the origin of the image in the canvas system. It sets the "
                        + "coordinate of the top-left corner of the image reference grid, "
                        + "with respect to the canvas origin",
                        "0 0"
                    },
                new string?[]
                    {
                        "tref", "<x> <y>",
                        "Sets the origin of the tile partitioning on the reference grid, "
                        + "with respect to the canvas origin. The value of 'x' ('y') "
                        + "specified can not be larger than the 'x' one specified in the ref "
                        + "option.",
                        "0 0"
                    },
                new string?[]
                    {
                        "rate", "<output bitrate in bpp>",
                        "This is the output bitrate of the codestream in bits per pixel."
                        + " When equal to -1, no image information (beside quantization "
                        + "effects) is discarded during compression.\n"
                        + "Note: In the case where '-file_format' option is used, the "
                        + "resulting file may have a larger bitrate.",
                        "-1"
                    },
                new string?[]
                    {
                        "lossless", "[on|off]",
                        "Specifies a lossless compression for the encoder. This options"
                        + " is equivalent to use reversible quantization ('-Qtype "
                        + "reversible')"
                        + " and 5x3 wavelet filters pair ('-Ffilters w5x3'). Note that "
                        + "this option cannot be used with '-rate'. When this option is "
                        + "off, the quantization type and the filters pair is defined by "
                        + "'-Qtype' and '-Ffilters' respectively.",
                        "off"
                    },
                new string?[]
                    {
                        "i", "<image file> [,<image file> [,<image file> ... ]]",
                        "Mandatory argument. This option specifies the name of the input "
                        + "image files. If several image files are provided, they have to be"
                        + " separated by commas in the command line. Supported formats are "
                        + "PGM (raw), PPM (raw) and PGX, "
                        + "which is a simple extension of the PGM file format for single "
                        + "component data supporting arbitrary bitdepths. If the extension "
                        + "is '.pgm', PGM-raw file format is assumed, if the extension is "
                        + "'.ppm', PPM-raw file format is assumed, otherwise PGX file "
                        + "format is assumed. PGM and PPM files are assumed to be 8 bits "
                        + "deep. A multi-component image can be specified by either "
                        + "specifying several PPM and/or PGX files, or by specifying one "
                        + "PPM file.",
                        null
                    },
                new string?[]
                    {
                        "o", "<file name>",
                        "Mandatory argument. This option specifies the name of the output "
                        + "file to which the codestream will be written.",
                        null
                    },
                new string?[]
                    {
                        "verbose", null,
                        "Prints information about the obtained bit stream.", "on"
                    },
                new string?[]
                    {
                        "v", "[on|off]", "Prints version and copyright information.",
                        "off"
                    },
                new string?[]
                    {
                        "u", "[on|off]",
                        "Prints usage information. "
                        + "If specified all other arguments (except 'v') are ignored",
                        "off"
                    },
            };

        #endregion

        /// <summary>Prints the error message 'msg' to standard err, prepending "ERROR" to
        /// it, and sets the exitCode to 'code'. An exit code different than 0
        /// indicates that there were problems.</summary>
        /// <param name="msg">The error message</param>
        /// <param name="code">The exit code to set</param>
        private static void error(string msg, int code)
        {
            //exitCode = code;
            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.ERROR, msg);
        }

        /// <summary>Prints the warning message 'msg' to standard err, prepending "WARNING" to it.</summary>
        /// <param name="msg">The warning message</param>
        private static void warning(string msg)
        {
            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING, msg);
        }

        private static string? GetImageType(Stream inStream)
        {
            try
            {
                var bytes = new byte[2];
                inStream.Position = 0;
                StreamHelper.ReadExact(inStream, bytes, 0, 2);
                inStream.Position = 0;
                var imgType = Encoding.UTF8.GetString(bytes, 0, 2);
                return imgType;
            }
            catch
            {
                return null;
            }
        }
    }
}


