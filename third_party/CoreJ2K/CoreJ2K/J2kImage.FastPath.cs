// Copyright (c) 2007-2016 CSJ2K contributors.
// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

namespace CoreJ2K
{
    using CoreJ2K.Util;
    using j2k.codestream;
    using j2k.codestream.reader;
    using j2k.entropy.decoder;
    using j2k.fileformat.reader;
    using j2k.image;
    using j2k.image.dco;
    using j2k.image.invcomptransf;
    using j2k.io;
    using j2k.quantization.dequantizer;
    using j2k.roi;
    using j2k.util;
    using j2k.wavelet.synthesis;
    using Color;
    using System;
    using System.IO;

    public partial class J2kImage
    {
        #region Public fast-path API

        /// <summary>
        /// Decodes a JPEG 2000 codestream directly into a backend image of type
        /// <typeparamref name="T"/> using an 8-bit fast path when all components have
        /// nominal bit depth ≤ 8. The fast path avoids the full-size <see cref="InterleavedImage"/>
        /// <c>int[]</c> allocation entirely, writing scaled bytes directly into a single byte
        /// buffer that is handed to the registered <see cref="IImageCreator"/>.
        /// For components wider than 8 bits the method falls back to
        /// <see cref="FromStream(Stream, ParameterList?)"/> followed by
        /// <see cref="InterleavedImage.As{T}"/>.
        /// </summary>
        /// <typeparam name="T">Backend image type registered with <see cref="ImageFactory"/>.</typeparam>
        /// <param name="stream">Stream containing JPEG 2000 data.</param>
        /// <param name="parameters">Optional decoder parameters.</param>
        /// <returns>The decoded image converted to <typeparamref name="T"/>.</returns>
        public static T DecodeToImage<T>(Stream stream, ParameterList? parameters = null)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            return DecodeToImageCore<T>(stream, parameters);
        }

        /// <summary>
        /// Decodes JPEG 2000 data from a byte array directly into a backend image of type
        /// <typeparamref name="T"/> using the 8-bit fast path when possible.
        /// </summary>
        /// <typeparam name="T">Backend image type registered with <see cref="ImageFactory"/>.</typeparam>
        /// <param name="j2kdata">The JPEG 2000 compressed data.</param>
        /// <param name="parameters">Optional decoder parameters.</param>
        /// <returns>The decoded image converted to <typeparamref name="T"/>.</returns>
        public static T DecodeToImage<T>(byte[] j2kdata, ParameterList? parameters = null)
        {
            if (j2kdata == null) throw new ArgumentNullException(nameof(j2kdata));
            using (var ms = new MemoryStream(j2kdata))
            {
                return DecodeToImage<T>(ms, parameters);
            }
        }

        /// <summary>
        /// Decodes a JPEG 2000 file directly into a backend image of type
        /// <typeparamref name="T"/> using the 8-bit fast path when possible.
        /// </summary>
        /// <typeparam name="T">Backend image type registered with <see cref="ImageFactory"/>.</typeparam>
        /// <param name="filename">Path to the JPEG 2000 file.</param>
        /// <param name="parameters">Optional decoder parameters.</param>
        /// <returns>The decoded image converted to <typeparamref name="T"/>.</returns>
        public static T DecodeFileToImage<T>(string filename, ParameterList? parameters = null)
        {
            if (filename == null) throw new ArgumentNullException(nameof(filename));
            using (var stream = FileStreamFactory.New(filename, "r"))
            {
                return DecodeToImage<T>(stream, parameters);
            }
        }

        #endregion

        #region Core decode

        /// <summary>
        /// Core implementation of the 8-bit fast-path decode.
        /// </summary>
        /// <remarks>
        /// <para>
        /// When all components of the decoded image have a nominal bit depth of 8 or fewer bits,
        /// this method writes interleaved, scaled byte samples directly into a
        /// <c>byte[Width × Height × NumComponents]</c> array and forwards that array to
        /// <see cref="ImageFactory.New{T}"/>.  This avoids the full-size
        /// <c>int[Width × Height × NumComponents]</c> allocation that
        /// <see cref="InterleavedImage"/> normally carries, cutting peak transient memory
        /// from roughly <b>5 bytes/sample</b> to <b>1 byte/sample</b> for the common
        /// 8-bit decode-and-display path.
        /// </para>
        /// <para>
        /// For images with any component wider than 8 bits the method falls back to
        /// <see cref="FromStream(Stream, ParameterList?)"/> followed by
        /// <see cref="InterleavedImage.As{T}"/> so that HDR/scientific data is never
        /// silently truncated.  The source stream must be seekable in that case.
        /// </para>
        /// </remarks>
        private static T DecodeToImageCore<T>(Stream stream, ParameterList? parameters)
        {
            InverseWT? invWT = null;
            try
            {
                var in_stream = new ISRandomAccessIO(stream);
                var pl = parameters ?? new ParameterList(GetDefaultDecoderParameterList(decoder_pinfo));

                var ff = new FileFormatReader(in_stream);
                ff.readFileFormat();
                if (ff.JP2FFUsed)
                {
                    in_stream.seek(ff.FirstCodeStreamPos);
                }

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
                var decSpec = hd.DecoderSpecs;

                var depth = new int[nCompCod];
                for (var i = 0; i < nCompCod; i++)
                {
                    depth[i] = hd.GetOriginalBitDepth(i);
                }

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

                EntropyDecoder entdec;
                try { entdec = hd.createEntropyDecoder(breader, pl); }
                catch (ArgumentException e) { throw new InvalidOperationException("Cannot instantiate entropy decoder.", e); }

                ROIDeScaler roids;
                try { roids = hd.createROIDeScaler(entdec, pl, decSpec); }
                catch (ArgumentException e) { throw new InvalidOperationException("Cannot instantiate roi de-scaler.", e); }

                Dequantizer deq;
                try { deq = HeaderDecoder.createDequantizer(roids, depth, decSpec); }
                catch (ArgumentException e) { throw new InvalidOperationException("Cannot instantiate dequantizer.", e); }

                try { invWT = InverseWT.createInstance(deq, decSpec); }
                catch (ArgumentException e) { throw new InvalidOperationException("Cannot instantiate inverse wavelet transform.", e); }

                var res = breader.ImgRes;
                invWT.ImgResLevel = res;

                var converter = new ImgDataConverter(invWT, 0);
                var ictransf = new InvCompTransf(converter, decSpec, depth, pl);

                // Inverse multiple component transform (MCT, ISO/IEC 15444-2)
                BlkImgDataSrc afterCt = ictransf;
                var mctStages = j2k.codestream.MctTransform.AssembleDecodeStages(hd.MctArrays, hd.MccSegments, hd.McoSegment);
                if (mctStages.Count > 0)
                {
                    afterCt = j2k.image.mct.ComponentTransform.BuildChain(ictransf, mctStages, inverse: true);
                }

                // Inverse non-linearity point transform (NLT, ISO/IEC 15444-2)
                BlkImgDataSrc postCt = afterCt;
                if (hd.NLTSegments != null && hd.NLTSegments.Count > 0)
                {
                    postCt = new j2k.image.nlt.InvNLT(afterCt, hd.NLTSegments);
                }

                // Inverse variable DC offset (DCO, ISO/IEC 15444-2)
                if (hd.DcoSegment != null)
                {
                    postCt = new InvDCO(postCt, hd.DcoSegment);
                }

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
                    color = postCt;
                }

                var decodedImage = color ?? postCt;
                var numComps = decodedImage.NumComps;
                var imgWidth = decodedImage.ImgWidth;
                var imgHeight = decodedImage.ImgHeight;

                // Determine whether all components fit the 8-bit fast path
                var allEightBit = true;
                for (var i = 0; i < numComps; i++)
                {
                    if (decodedImage.GetNomRangeBits(i) > 8)
                    {
                        allEightBit = false;
                        break;
                    }
                }

                if (!allEightBit)
                {
                    // Fall back to legacy path: build InterleavedImage and convert.
                    // Restart from the beginning since we already consumed in_stream.
                    // The cleanest correct behaviour is to delegate by re-seeking the source stream.
                    if (!stream.CanSeek)
                    {
                        throw new InvalidOperationException(
                            "DecodeToImage fast path requires a seekable stream for >8-bit images (legacy fallback).");
                    }
                    // Close our chain first so the legacy decoder owns a fresh pipeline.
                    try { invWT.Close(); } catch { /* ignore */ }
                    invWT = null;
                    stream.Position = 0;
                    using (var img = FromStream(stream, parameters))
                    {
                        return img.As<T>();
                    }
                }

                // **** 8-bit fast path: decode straight into a byte buffer ****
                var totalBytes = checked(imgWidth * imgHeight * numComps);
                var pixelBytes = new byte[totalBytes];

                var numTiles = decodedImage.GetNumTiles(null);
                var tIdx = 0;
                for (var y = 0; y < numTiles.y; y++)
                {
                    for (var x = 0; x < numTiles.x; x++, tIdx++)
                    {
                        decodedImage.SetTile(x, y);

                        var tileHeight = decodedImage.GetTileCompHeight(tIdx, 0);
                        var tileWidth = decodedImage.GetTileCompWidth(tIdx, 0);

                        var tOffx = decodedImage.GetCompULX(0)
                                    - (int)Math.Ceiling(decodedImage.ImgULX / (double)decodedImage.GetCompSubsX(0));
                        var tOffy = decodedImage.GetCompULY(0)
                                    - (int)Math.Ceiling(decodedImage.ImgULY / (double)decodedImage.GetCompSubsY(0));

                        var db = new DataBlkInt[numComps];
                        var ls = new int[numComps];
                        var mv = new int[numComps];
                        var fb = new int[numComps];
                        var scale = new double[numComps];
                        for (var i = 0; i < numComps; i++)
                        {
                            db[i] = new DataBlkInt();
                            var bits = decodedImage.GetNomRangeBits(i);
                            ls[i] = 1 << (bits - 1);
                            mv[i] = (1 << bits) - 1;
                            fb[i] = decodedImage.GetFixedPoint(i);
                            scale[i] = 255.0 / mv[i];
                        }

                        var k = new int[numComps];

                        for (var l = 0; l < tileHeight; l++)
                        {
                            var destLine = tOffy + l;
                            if (destLine < 0) continue;
                            if (destLine >= imgHeight) break;

                            for (var i = 0; i < numComps; i++)
                            {
                                db[i].ulx = 0;
                                db[i].uly = l;
                                db[i].w = tileWidth;
                                db[i].h = 1;
                                decodedImage.GetInternCompData(db[i], i);
                                k[i] = db[i].offset;
                            }

                            // Compute source horizontal range that maps into image bounds
                            var dstX = tOffx;
                            var srcX = 0;
                            if (dstX < 0)
                            {
                                srcX = -dstX;
                                dstX = 0;
                            }
                            var remainingDst = imgWidth - dstX;
                            if (remainingDst <= 0) continue;
                            var remainingSrc = tileWidth - srcX;
                            if (remainingSrc <= 0) continue;
                            var copyPixels = Math.Min(remainingDst, remainingSrc);
                            if (copyPixels <= 0) continue;

                            // Advance k[i] past skipped source pixels
                            if (srcX > 0)
                            {
                                for (var i = 0; i < numComps; i++)
                                {
                                    k[i] += srcX;
                                }
                            }

                            var dstOffset = (destLine * imgWidth + dstX) * numComps;

                            for (var col = 0; col < copyPixels; col++)
                            {
                                for (var c = 0; c < numComps; c++)
                                {
                                    var v = (db[c].data_array[k[c]++] >> fb[c]) + ls[c];
                                    if (v < 0) v = 0;
                                    else if (v > mv[c]) v = mv[c];

                                    // Scale to 0..255. For 8-bit components scale == 1.0 so this is a no-op cast.
                                    var b = scale[c] == 1.0 ? v : (int)Math.Round(scale[c] * v);
                                    if (b < 0) b = 0;
                                    else if (b > 255) b = 255;
                                    pixelBytes[dstOffset++] = (byte)b;
                                }
                            }
                        }
                    }
                }

                var iimg = ImageFactory.New<T>(imgWidth, imgHeight, numComps, pixelBytes);
                if (iimg == null)
                {
                    throw new InvalidOperationException(
                        $"No image creator registered for target type {typeof(T).FullName}. " +
                        $"Registered creators: {ImageFactory.DescribeRegistered()}.");
                }
                return iimg.As<T>();
            }
            finally
            {
                if (invWT != null)
                {
                    try { invWT.Close(); }
                    catch { /* suppress to avoid masking originals */ }
                }
            }
        }

        #endregion
    }
}
