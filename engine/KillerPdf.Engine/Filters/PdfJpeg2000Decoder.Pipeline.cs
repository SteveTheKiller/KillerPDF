using System.Buffers;
using System.Numerics;
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
    private static readonly ArrayPool<int> IntegerFrames = PdfScratchBuffers.Integers;
    private static readonly ArrayPool<float> FloatFrames = PdfScratchBuffers.Floats;
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
                var geometry = new (int Bits, int FixedPoint, int StepX, int StepY,
                    int Width, int Height, int Left, int Top, bool Direct)[shape.Components];
                var blocks = new DataBlk[shape.Components];
                int rows = 0;
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
                    bool directRows = stepX == 1 && stepY == 1 && bits is 8 or 16
                        && left >= 0 && top >= 0 && (long)left + tileWidth <= width
                        && (long)top + tileHeight <= height;
                    geometry[component] = (bits, fixedPoint, stepX, stepY, tileWidth, tileHeight, left, top, directRows);
                    blocks[component] = new DataBlkInt();
                    rows = Math.Max(rows, tileHeight);
                }
                // Consume all components of a row while the inverse color transform still
                // has its other output channels cached for that row.
                for (int row = 0; row < rows; row++)
                {
                    for (int component = 0; component < shape.Components; component++)
                    {
                        var (bits, fixedPoint, stepX, stepY, tileWidth, tileHeight, left, top, directRows) = geometry[component];
                        if (row >= tileHeight) continue;
                        DataBlk block = blocks[component];
                        block.ulx = 0;
                        block.uly = row;
                        block.w = tileWidth;
                        block.h = 1;
                        block = blocks[component] = pixels.GetInternCompData(block, component);
                        int[] values = block.Data as int[]
                            ?? throw new PdfFilterException("JPEG 2000 has no decoded component samples.");
                        if (directRows)
                        {
                            int bytesPerSample = bits / 8;
                            int destination = (top + row) * rowBytes
                                + (left * shape.Components + component) * bytesPerSample;
                            int stride = shape.Components * bytesPerSample;
                            int bias = 1 << (bits - 1);
                            for (int column = 0; column < tileWidth; column++, destination += stride)
                            {
                                int value = (int)Math.Clamp((long)(values[block.offset + column] >> fixedPoint)
                                    + bias, 0, maximum);
                                if (bits == 8) samples[destination] = (byte)value;
                                else
                                {
                                    samples[destination] = (byte)(value >> 8);
                                    samples[destination + 1] = (byte)value;
                                }
                            }
                            continue;
                        }
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
            try
            {
                try { inverse.ReleaseFrames(); }
                finally { inverse.Close(); }
            }
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
        private readonly List<Array> _rentedFrames = [];
        private long _sampleBytes;

        internal ImageInverseTransform(CBlkWTDataSrcDec source, DecoderSpecs specifications)
            : base(source, specifications) => _source = source;

        public override void SetTile(int x, int y)
        {
            ReleaseFrames();
            base.SetTile(x, y);
        }

        internal void ReleaseFrames()
        {
            foreach (Array samples in _rentedFrames)
                if (samples is int[] integers) IntegerFrames.Return(integers);
                else FloatFrames.Return((float[])samples);
            _rentedFrames.Clear();
            _frames.Clear();
            _sampleBytes = 0;
        }

        public override DataBlk GetInternCompData(DataBlk block, int component)
        {
            if (!_frames.TryGetValue(component, out DataBlk? frame))
            {
                SubbandSyn tree = _source.GetSynSubbandTree(TileIdx, component);
                int width = GetTileCompWidth(TileIdx, component), height = GetTileCompHeight(TileIdx, component);
                long bytes = checked((long)width * height * sizeof(int));
                long scratchBytes = ((long)Math.Max(width, height) + height) * sizeof(int);
                if (width < 0 || height < 0 || bytes + scratchBytes > MaximumTemporarySampleBytes - _sampleBytes)
                    throw new PdfFilterException("JPEG 2000 temporary samples exceed the configured safety limit.");
                bool integer = tree.HorWFilter is null || tree.HorWFilter.DataType == DataBlk.TYPE_INT;
                int count = checked(width * height);
                long pooledBytes = (long)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, count)) * sizeof(int);
                // Oversized pool requests already allocate fresh, zeroed arrays.
                bool pooled = pooledBytes <= PdfScratchBuffers.MaximumSamplePoolBytes
                    && pooledBytes + scratchBytes <= MaximumTemporarySampleBytes - _sampleBytes;
                Array samples = integer
                    ? pooled ? IntegerFrames.Rent(count) : new int[count]
                    : pooled ? FloatFrames.Rent(count) : new float[count];
                if (pooled)
                {
                    _rentedFrames.Add(samples);
                    Array.Clear(samples, 0, count);
                    bytes = (long)samples.Length * sizeof(int);
                    if (bytes + scratchBytes > MaximumTemporarySampleBytes - _sampleBytes)
                        throw new PdfFilterException("JPEG 2000 temporary samples exceed the configured safety limit.");
                }
                frame = integer ? new DataBlkInt() : new DataBlkFloat();
                frame.w = frame.scanw = width;
                frame.h = height;
                frame.Data = samples;
                Array scratch = integer ? new int[Math.Max(width, height)] : new float[Math.Max(width, height)];
                int lanes = Vector<float>.Count;
                long vectorExtraBytes = (long)height * (2 * lanes - 1) * sizeof(float);
                bool vectorColumns = !integer && Vector.IsHardwareAccelerated && width >= lanes
                    && vectorExtraBytes <= MaximumTemporarySampleBytes - _sampleBytes - bytes - scratchBytes;
                float[]? columnInput = vectorColumns ? new float[checked(height * lanes)] : null;
                Array columnOutput = integer ? new int[height]
                    : new float[vectorColumns ? checked(height * lanes) : height];
                Reconstruct(frame, tree, component, Level(TileIdx, component), scratch, columnOutput, columnInput);
                _frames.Add(component, frame);
                _sampleBytes += bytes;
            }
            if (block.ulx < 0 || block.uly < 0 || block.w < 0 || block.h < 0
                || (long)block.ulx + block.w > frame.w || (long)block.uly + block.h > frame.h)
                throw new PdfFilterException("JPEG 2000 requested samples outside the tile.");
            if (block.DataType != frame.DataType)
            {
                DataBlk converted = frame.DataType == DataBlk.TYPE_INT
                    ? new DataBlkInt() : new DataBlkFloat();
                converted.ulx = block.ulx;
                converted.uly = block.uly;
                converted.w = block.w;
                converted.h = block.h;
                block = converted;
            }
            block.Data = frame.Data;
            block.offset = block.uly * frame.w + block.ulx;
            block.scanw = frame.w;
            block.progressive = false;
            return block;
        }

        private void Reconstruct(DataBlk frame, SubbandSyn tree, int component, int level,
            Array scratch, Array columnOutput, float[]? columnInput)
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
            Reconstruct(frame, (SubbandSyn)tree.LL, component, level, scratch, columnOutput, columnInput);
            if (tree.resLvl > level) return;
            Reconstruct(frame, (SubbandSyn)tree.HL, component, level, scratch, columnOutput, columnInput);
            Reconstruct(frame, (SubbandSyn)tree.LH, component, level, scratch, columnOutput, columnInput);
            Reconstruct(frame, (SubbandSyn)tree.HH, component, level, scratch, columnOutput, columnInput);
            Array values = SampleArray(frame);
            for (int row = 0; row < tree.h; row++)
            {
                int offset = checked((tree.uly + row) * frame.w + tree.ulx);
                Array.Copy(values, offset, scratch, 0, tree.w);
                Synthesize(tree.hFilter, tree.ulcx, tree.w, scratch, values, offset, 1);
            }
            int column = 0;
            if (columnInput is not null && values is float[] floatSamples
                && tree.vFilter is SynWTFilterFloatLift9x7)
            {
                int lanes = Vector<float>.Count;
                var output = (float[])columnOutput;
                for (; column <= tree.w - lanes; column += lanes)
                {
                    int offset = checked(tree.uly * frame.w + tree.ulx + column);
                    for (int row = 0; row < tree.h; row++)
                        floatSamples.AsSpan(offset + row * frame.w, lanes)
                            .CopyTo(columnInput.AsSpan(row * lanes));
                    SynthesizeFloatColumns(columnInput, output, tree.h, (tree.ulcy & 1) == 0);
                    for (int row = 0; row < tree.h; row++)
                        output.AsSpan(row * lanes, lanes)
                            .CopyTo(floatSamples.AsSpan(offset + row * frame.w));
                }
            }
            for (; column < tree.w; column++)
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
                // Keep the lifting filter's repeated reads and writes contiguous.
                // Only gathering and scattering traverse the tile's column stride.
                Synthesize(tree.vFilter, tree.ulcy, tree.h, scratch, columnOutput, 0, 1);
                if (values is int[] integerOutput)
                {
                    var line = (int[])columnOutput;
                    for (int row = 0; row < tree.h; row++) integerOutput[offset + row * frame.w] = line[row];
                }
                else
                {
                    var floatOutput = (float[])values;
                    var line = (float[])columnOutput;
                    for (int row = 0; row < tree.h; row++) floatOutput[offset + row * frame.w] = line[row];
                }
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
