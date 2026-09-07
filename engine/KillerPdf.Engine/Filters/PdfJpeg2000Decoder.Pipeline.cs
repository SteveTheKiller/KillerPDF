using CoreJ2K.Configuration;
using CoreJ2K.j2k.codestream;
using CoreJ2K.j2k.codestream.reader;
using CoreJ2K.j2k.decoder;
using CoreJ2K.j2k.fileformat.reader;
using CoreJ2K.j2k.image;
using CoreJ2K.j2k.image.dco;
using CoreJ2K.j2k.image.invcomptransf;
using CoreJ2K.j2k.image.mct;
using CoreJ2K.j2k.image.nlt;
using CoreJ2K.j2k.util;
using CoreJ2K.j2k.wavelet.synthesis;

namespace KillerPdf.Engine.Filters;

internal static partial class PdfJpeg2000Decoder
{
    private static Jpeg2000DecodedImage DecodePixels(ReadOnlyMemory<byte> encoded,
        Jpeg2000Shape shape, int resolutionLevel, int width, int height, int rowBytes, int length)
    {
        using MemoryStream input = CreateReadStream(encoded);
        var access = new ISRandomAccessIO(input);
        var parameters = new J2KDecoderConfiguration
        {
            UseColorSpace = false,
            Verbose = false,
            ResolutionLevel = resolutionLevel
        }.ToParameterList();
        var format = new FileFormatReader(access);
        format.readFileFormat();
        if (format.JP2FFUsed) access.seek(format.FirstCodeStreamPos);
        var information = new HeaderInfo();
        var header = new HeaderDecoder(access, parameters, information);
        DecoderSpecs specifications = header.DecoderSpecs;
        int[] depths = Enumerable.Range(0, header.NumComps).Select(header.GetOriginalBitDepth).ToArray();
        var packets = BitstreamReaderAgent.createInstance(
            access, header, parameters, specifications, false, information);
        var entropy = header.createEntropyDecoder(packets, parameters);
        var roi = header.createROIDeScaler(entropy, parameters, specifications);
        var quantized = HeaderDecoder.createDequantizer(roi, depths, specifications);
        var inverse = new ImageInverseTransform(quantized, specifications)
        {
            ImgResLevel = packets.ImgRes
        };
        try
        {
            var converter = new ImgDataConverter(inverse, 0);
            BlkImgDataSrc pixels = new InvCompTransf(converter, specifications, depths, parameters);
            var stages = MctTransform.AssembleDecodeStages(header.MctArrays, header.MccSegments, header.McoSegment);
            if (stages.Count > 0) pixels = ComponentTransform.BuildChain(pixels, stages, inverse: true);
            if (header.NLTSegments is { Count: > 0 }) pixels = new InvNLT(pixels, header.NLTSegments);
            if (header.DcoSegment is not null) pixels = new InvDCO(pixels, header.DcoSegment);
            if (pixels.ImgWidth != width || pixels.ImgHeight != height || pixels.NumComps != shape.Components)
                throw new PdfFilterException("JPEG 2000 decoded dimensions do not match its codestream header.");

            var samples = new byte[length];
            var tiles = pixels.GetNumTiles(null);
            int maximum = (1 << shape.Bits) - 1;
            for (int tileY = 0, tile = 0; tileY < tiles.y; tileY++)
            for (int tileX = 0; tileX < tiles.x; tileX++, tile++)
            {
                pixels.SetTile(tileX, tileY);
                for (int component = 0; component < shape.Components; component++)
                {
                    int bits = pixels.GetNomRangeBits(component);
                    int fixedPoint = pixels.GetFixedPoint(component);
                    int stepX = pixels.GetCompSubsX(component), stepY = pixels.GetCompSubsY(component);
                    if (bits != shape.Bits || fixedPoint is < 0 or > 30 || stepX <= 0 || stepY <= 0)
                        throw new PdfFilterException("JPEG 2000 components have unsupported sample geometry.");
                    int tileWidth = pixels.GetTileCompWidth(tile, component);
                    int tileHeight = pixels.GetTileCompHeight(tile, component);
                    int left = checked(pixels.GetCompULX(component) * stepX - pixels.ImgULX);
                    int top = checked(pixels.GetCompULY(component) * stepY - pixels.ImgULY);
                    DataBlk block = new DataBlkInt();
                    for (int row = 0; row < tileHeight; row++)
                    {
                        block.ulx = 0;
                        block.uly = row;
                        block.w = tileWidth;
                        block.h = 1;
                        block = pixels.GetInternCompData(block, component);
                        int[] values = block.Data as int[]
                            ?? throw new PdfFilterException("JPEG 2000 has no decoded component samples.");
                        int firstY = Math.Max(0, checked(top + row * stepY));
                        int lastY = Math.Min(height, checked(top + (row + 1) * stepY));
                        for (int column = 0; column < tileWidth; column++)
                        {
                            int value = (int)Math.Clamp((long)(values[block.offset + column] >> fixedPoint)
                                + (1 << (bits - 1)), 0, maximum);
                            int firstX = Math.Max(0, checked(left + column * stepX));
                            int lastX = Math.Min(width, checked(left + (column + 1) * stepX));
                            for (int y = firstY; y < lastY; y++)
                            for (int x = firstX; x < lastX; x++)
                            {
                                int sample = x * shape.Components + component;
                                if (bits == 8) samples[y * rowBytes + sample] = (byte)value;
                                else
                                {
                                    int bitOffset = checked(y * rowBytes * 8 + sample * bits);
                                    for (int bit = bits - 1; bit >= 0; bit--, bitOffset++)
                                        if ((value & (1 << bit)) != 0)
                                            samples[bitOffset / 8] |= (byte)(1 << (7 - bitOffset % 8));
                                }
                            }
                        }
                    }
                }
            }
            return new Jpeg2000DecodedImage(samples, width, height, shape.Components, shape.Bits);
        }
        finally
        {
            try { inverse.Close(); }
            finally { access.Close(); }
        }
    }

    // CoreJ2K's tile geometry remains at full resolution after selecting a reduced
    // image level. Its pooled reconstruction buffers can also retain coefficients
    // where a damaged stream supplies no code blocks. Own both geometry and storage.
    private sealed class ImageInverseTransform : InvWTFull
    {
        private readonly CBlkWTDataSrcDec _source;
        private readonly Dictionary<int, DataBlk> _frames = [];
        private long _sampleBytes;

        internal ImageInverseTransform(CBlkWTDataSrcDec source, DecoderSpecs specifications)
            : base(source, specifications) => _source = source;

        public override void SetTile(int x, int y)
        {
            _frames.Clear();
            _sampleBytes = 0;
            base.SetTile(x, y);
        }

        public override DataBlk GetInternCompData(DataBlk block, int component)
        {
            if (!_frames.TryGetValue(component, out DataBlk? frame))
            {
                SubbandSyn tree = _source.GetSynSubbandTree(TileIdx, component);
                int width = GetTileCompWidth(TileIdx, component), height = GetTileCompHeight(TileIdx, component);
                long bytes = checked((long)width * height * sizeof(int));
                long scratchBytes = (long)Math.Max(width, height) * sizeof(int);
                if (width < 0 || height < 0 || bytes + scratchBytes > MaximumTemporarySampleBytes - _sampleBytes)
                    throw new PdfFilterException("JPEG 2000 temporary samples exceed the configured safety limit.");
                bool integer = tree.HorWFilter is null || tree.HorWFilter.DataType == DataBlk.TYPE_INT;
                frame = integer ? new DataBlkInt(0, 0, width, height) : new DataBlkFloat(0, 0, width, height);
                Array scratch = integer ? new int[Math.Max(width, height)] : new float[Math.Max(width, height)];
                Reconstruct(frame, tree, component, Level(TileIdx, component), scratch);
                _frames.Add(component, frame);
                _sampleBytes += bytes;
            }
            if (block.ulx < 0 || block.uly < 0 || block.w < 0 || block.h < 0
                || (long)block.ulx + block.w > frame.w || (long)block.uly + block.h > frame.h)
                throw new PdfFilterException("JPEG 2000 requested samples outside the tile.");
            if (block.DataType != frame.DataType)
                block = frame.DataType == DataBlk.TYPE_INT
                    ? new DataBlkInt(block.ulx, block.uly, block.w, block.h)
                    : new DataBlkFloat(block.ulx, block.uly, block.w, block.h);
            block.Data = frame.Data;
            block.offset = block.uly * frame.w + block.ulx;
            block.scanw = frame.w;
            block.progressive = false;
            return block;
        }

        private void Reconstruct(DataBlk frame, SubbandSyn tree, int component, int level, Array scratch)
        {
            if (tree.w == 0 || tree.h == 0) return;
            if (!tree.isNode)
            {
                DataBlk data = frame.DataType == DataBlk.TYPE_INT ? new DataBlkInt() : new DataBlkFloat();
                for (int y = 0; y < tree.numCb.y; y++)
                for (int x = 0; x < tree.numCb.x; x++)
                {
                    data = _source.GetInternCodeBlock(component, y, x, tree, data);
                    for (int row = 0; row < data.h; row++)
                        Array.Copy(SampleArray(data), data.offset + row * data.scanw,
                            SampleArray(frame), checked((data.uly + row) * frame.w + data.ulx), data.w);
                }
                return;
            }
            Reconstruct(frame, (SubbandSyn)tree.LL, component, level, scratch);
            if (tree.resLvl > level) return;
            Reconstruct(frame, (SubbandSyn)tree.HL, component, level, scratch);
            Reconstruct(frame, (SubbandSyn)tree.LH, component, level, scratch);
            Reconstruct(frame, (SubbandSyn)tree.HH, component, level, scratch);
            Array values = SampleArray(frame);
            for (int row = 0; row < tree.h; row++)
            {
                int offset = checked((tree.uly + row) * frame.w + tree.ulx);
                Array.Copy(values, offset, scratch, 0, tree.w);
                Synthesize(tree.hFilter, tree.ulcx, tree.w, scratch, values, offset, 1);
            }
            for (int column = 0; column < tree.w; column++)
            {
                int offset = checked(tree.uly * frame.w + tree.ulx + column);
                if (values is int[] integers)
                {
                    var line = (int[])scratch;
                    for (int row = 0; row < tree.h; row++) line[row] = integers[offset + row * frame.w];
                }
                else
                {
                    var floats = (float[])values;
                    var line = (float[])scratch;
                    for (int row = 0; row < tree.h; row++) line[row] = floats[offset + row * frame.w];
                }
                Synthesize(tree.vFilter, tree.ulcy, tree.h, scratch, values, offset, frame.w);
            }
        }

        private static Array SampleArray(DataBlk block) => block.Data as Array
            ?? throw new PdfFilterException("JPEG 2000 has no wavelet samples.");

        private static void Synthesize(SynWTFilter? filter, int origin, int length,
            Array scratch, Array target, int offset, int stride)
        {
            if (filter is null) throw new PdfFilterException("JPEG 2000 has no wavelet filter.");
            int low = length / 2, high = (length + 1) / 2;
            if ((origin & 1) == 0)
                filter.synthetize_lpf(scratch, 0, high, 1, scratch, high, low, 1, target, offset, stride);
            else
                filter.synthetize_hpf(scratch, 0, low, 1, scratch, low, high, 1, target, offset, stride);
        }

        private int Level(int tile, int component) =>
            reslvl - maxImgRes + mressrc.GetSynSubbandTree(tile, component).resLvl;

        public override int GetTileCompWidth(int t, int c) => mressrc.GetTileCompWidth(t, c, Level(t, c));
        public override int GetTileCompHeight(int t, int c) => mressrc.GetTileCompHeight(t, c, Level(t, c));
        public override int GetCompULX(int c) => mressrc.GetResULX(c, Level(TileIdx, c));
        public override int GetCompULY(int c) => mressrc.GetResULY(c, Level(TileIdx, c));
        public override int GetCompImgWidth(int c) => mressrc.GetCompImgWidth(c, reslvl - maxImgRes + decSpec.dls.GetMinInComp(c));
        public override int GetCompImgHeight(int c) => mressrc.GetCompImgHeight(c, reslvl - maxImgRes + decSpec.dls.GetMinInComp(c));
    }
}
