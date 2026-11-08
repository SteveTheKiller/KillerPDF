using System.Numerics;

namespace KillerPdf.Engine.Filters;

internal static class PdfJpegDecoder
{
    private const long MaximumTemporarySampleBytes = 256L * 1024 * 1024;
    private static readonly int[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63
    ];
    private static readonly double[,] Cosines = CreateCosines();
    private static readonly double[,,] ReducedCosines = CreateReducedCosines();
    private static readonly double[] VectorCosines = CreateVectorCosines();
    private static readonly double[] Scales = [1 / Math.Sqrt(2), 1, 1, 1, 1, 1, 1, 1];
    private static readonly double ReducedSingleAcBound = CreateReducedSingleAcBound();

    private static double CreateReducedSingleAcBound()
    {
        double bound = 0;
        // Baseline AC magnitudes are at most 1023 and quantization entries at most 65535.
        for (int v = 0; v < 8; v++)
            for (int u = 0; u < 8; u++)
                if (u != 0 || v != 0)
                    bound += 1023d * 65535 * Math.Abs(Scales[u] * ReducedCosines[3, 0, u]
                        * Scales[v] * ReducedCosines[3, 0, v]) / 4;
        // Cover floating-point accumulation error for the bounded sample interval below.
        return bound + 1e-9;
    }

    private const int ChromaShift = 16;
    private const int ChromaHalf = 1 << (ChromaShift - 1);
    // Red and blue tables carry their rounding; the two green tables are summed before one
    // shared rounding and shift.
    private static readonly int[] CrRed = CreateChromaTable(1.402, shifted: true, addHalf: true);
    private static readonly int[] CbGreen = CreateChromaTable(-0.344136, shifted: false, addHalf: true);
    private static readonly int[] CrGreen = CreateChromaTable(-0.714136, shifted: false, addHalf: false);
    private static readonly int[] CbBlue = CreateChromaTable(1.772, shifted: true, addHalf: true);

    // Range limit table in libjpeg's style: one indexed read replaces the compare pair per
    // component in the color conversion loops. Index RangeLimitOffset is sample zero. The
    // fixed-point conversion above reaches -227 at the low end and 480 at the high end, so
    // the table spans -256 to 767 and every reachable value lands inside it.
    private const int RangeLimitOffset = 256;
    private static readonly byte[] RangeLimit = CreateRangeLimit();

    private static byte[] CreateRangeLimit()
    {
        var table = new byte[1024];
        for (int index = 0; index < table.Length; index++)
            table[index] = (byte)Math.Clamp(index - RangeLimitOffset, 0, 255);
        return table;
    }

    private static int[] CreateChromaTable(double factor, bool shifted, bool addHalf)
    {
        var table = new int[256];
        long fixedFactor = (long)Math.Round(factor * (1 << ChromaShift));
        for (int sample = 0; sample < 256; sample++)
        {
            long value = fixedFactor * (sample - 128) + (addHalf ? ChromaHalf : 0);
            table[sample] = (int)(shifted ? value >> ChromaShift : value);
        }
        return table;
    }

    internal static byte[] Decode(
        ReadOnlyMemory<byte> source, int maximumDecodedBytes, int? colorTransform = null,
        bool compatibilityRecovery = false)
        => DecodeImage(source, maximumDecodedBytes, 1, colorTransform,
            compatibilityRecovery).Samples;

    internal static JpegDecodedImage DecodeImage(
        ReadOnlyMemory<byte> source, int maximumDecodedBytes, int reduction,
        int? colorTransform = null, bool compatibilityRecovery = false)
    {
        try
        {
            if (reduction is not (1 or 2 or 4 or 8))
                throw new PdfFilterException("JPEG reduction must be 1, 2, 4, or 8.");
            return new Decoder(
                source, maximumDecodedBytes, reduction, colorTransform,
                compatibilityRecovery).Decode();
        }
        catch (PdfFilterException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or OverflowException
            or ArgumentException or InvalidOperationException)
        {
            throw new PdfFilterException("The DCTDecode stream contains malformed JPEG data.", ex);
        }
    }

    private sealed class Decoder(
        ReadOnlyMemory<byte> source, int maximumDecodedBytes, int reduction, int? colorTransform,
        bool compatibilityRecovery)
    {
        private readonly int[][] _quantization = new int[4][];
        private readonly HuffmanTable?[,] _huffman = new HuffmanTable?[2, 4];
        private readonly List<Component> _components = [];
        private readonly List<Component> _scanComponents = [];
        private int _position;
        private int _width;
        private int _height;
        private int _restartInterval;
        private int? _adobeTransform;
        private bool _progressive;
        private bool _sawScan;
        private int _spectralStart;
        private int _spectralEnd;
        private int _successiveHigh;
        private int _successiveLow;

        internal JpegDecodedImage Decode()
        {
            if (source.Length < 4 || source.Span[0] != 0xFF || source.Span[1] != 0xD8)
                throw Error("The DCTDecode stream has no JPEG SOI marker.");
            _position = 2;
            while (_position < source.Length)
            {
                byte marker = ReadMarker();
                if (marker == 0xD9)
                    return _progressive && _sawScan ? BuildProgressiveOutput()
                        : throw Error("The JPEG ended before a decodable scan.");
                if (marker is 0x01 or >= 0xD0 and <= 0xD7) continue;
                ReadSegment(marker);
                if (marker == 0xDA)
                {
                    _sawScan = true;
                    if (!_progressive) return DecodeScan();
                    DecodeProgressiveScan();
                }
            }
            if (compatibilityRecovery && _progressive && _sawScan) return BuildProgressiveOutput();
            throw Error("The JPEG is missing its EOI marker.");
        }

        private void ReadSegment(byte marker)
        {
            int length = ReadUInt16();
            if (length < 2 || _position + length - 2 > source.Length)
                throw Error("A JPEG segment is truncated.");
            int end = _position + length - 2;
            switch (marker)
            {
                case 0xC0 or 0xC1: ReadFrame(end, progressive: false); break;
                case 0xC2: ReadFrame(end, progressive: true); break;
                case 0xC4: ReadHuffmanTables(end); break;
                case 0xDB: ReadQuantizationTables(end); break;
                case 0xDD:
                    if (end - _position != 2) throw Error("The JPEG restart interval is invalid.");
                    _restartInterval = ReadUInt16();
                    break;
                case 0xEE: ReadAdobeSegment(end); break;
                case 0xDA: ReadScanHeader(end); break;
                case 0xC3 or 0xC5 or 0xC6 or 0xC7
                    or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF:
                    throw Error("Only baseline sequential JPEG images are implemented.");
            }
            _position = end;
        }

        private void ReadFrame(int end, bool progressive)
        {
            if (_components.Count != 0) throw Error("Multiple JPEG frames are not supported.");
            _progressive = progressive;
            if (end - _position < 6 || source.Span[_position++] != 8)
                throw Error("Only 8-bit baseline JPEG images are implemented.");
            _height = ReadUInt16();
            _width = ReadUInt16();
            int count = source.Span[_position++];
            if (_width <= 0 || _height <= 0 || count is not (1 or 3 or 4))
                throw Error("The JPEG frame dimensions or component count are not supported.");
            if (_position + count * 3 != end) throw Error("The JPEG frame table is malformed.");
            var identifiers = new HashSet<byte>();
            for (int index = 0; index < count; index++)
            {
                byte id = source.Span[_position++];
                byte sampling = source.Span[_position++];
                int horizontal = sampling >> 4;
                int vertical = sampling & 15;
                int quantization = source.Span[_position++];
                if (!identifiers.Add(id) || horizontal is < 1 or > 4 || vertical is < 1 or > 4
                    || quantization > 3)
                    throw Error("The JPEG frame component table is invalid.");
                _components.Add(new Component(id, horizontal, vertical, quantization));
            }
        }

        private void ReadQuantizationTables(int end)
        {
            while (_position < end)
            {
                byte info = source.Span[_position++];
                int precision = info >> 4;
                int identifier = info & 15;
                if (precision != 0 || identifier > 3 || _position + 64 > end)
                    throw Error("The JPEG quantization table is unsupported or truncated.");
                var table = new int[64];
                for (int index = 0; index < 64; index++)
                {
                    int value = source.Span[_position++];
                    if (value == 0) throw Error("A JPEG quantization value is zero.");
                    table[ZigZag[index]] = value;
                }
                _quantization[identifier] = table;
            }
        }

        private void ReadHuffmanTables(int end)
        {
            while (_position < end)
            {
                byte info = source.Span[_position++];
                int tableClass = info >> 4;
                int identifier = info & 15;
                if (tableClass > 1 || identifier > 3 || _position + 16 > end)
                    throw Error("The JPEG Huffman table header is invalid.");
                int[] counts = new int[16];
                int symbolCount = 0;
                for (int index = 0; index < 16; index++)
                    symbolCount += counts[index] = source.Span[_position++];
                if (symbolCount > 256 || _position + symbolCount > end)
                    throw Error("The JPEG Huffman table is truncated.");
                _huffman[tableClass, identifier] = new HuffmanTable(counts,
                    source.Span.Slice(_position, symbolCount));
                _position += symbolCount;
            }
        }

        // Without an Adobe marker, three components are YCbCr unless their identifiers spell
        // R, G, B, which libjpeg and other readers take as untransformed RGB.
        private int DefaultTransform() =>
            _components.Count == 3 && !(_components[0].Identifier == (byte)'R'
                && _components[1].Identifier == (byte)'G' && _components[2].Identifier == (byte)'B') ? 1 : 0;

        private void ReadAdobeSegment(int end)
        {
            if (end - _position >= 12
                && source.Span.Slice(_position, 5).SequenceEqual("Adobe"u8))
                _adobeTransform = source.Span[_position + 11];
        }

        private void ReadScanHeader(int end)
        {
            if (_components.Count == 0 || _position >= end)
                throw Error("The JPEG scan precedes its frame.");
            int count = source.Span[_position++];
            if (count < 1 || count > _components.Count || _position + count * 2 + 3 != end
                || !_progressive && count != _components.Count)
                throw Error("The JPEG scan component table is unsupported.");
            _scanComponents.Clear();
            foreach (Component component in _components) component.InScan = false;
            for (int index = 0; index < count; index++)
            {
                byte identifier = source.Span[_position++];
                Component component = _components.SingleOrDefault(item => item.Identifier == identifier)
                    ?? throw Error("The JPEG scan references an undefined component.");
                if (component.InScan) throw Error("The JPEG scan repeats a component.");
                byte tables = source.Span[_position++];
                component.DcTable = tables >> 4;
                component.AcTable = tables & 15;
                component.InScan = true;
                _scanComponents.Add(component);
            }
            _spectralStart = source.Span[_position++];
            _spectralEnd = source.Span[_position++];
            byte approximation = source.Span[_position++];
            _successiveHigh = approximation >> 4;
            _successiveLow = approximation & 15;
            if (!_progressive && (_spectralStart != 0 || _spectralEnd != 63
                || approximation != 0))
            {
                if (compatibilityRecovery && _spectralStart == 0
                    && _spectralEnd == 0 && approximation == 0)
                    _spectralEnd = 63;
                else
                    throw Error("Only sequential JPEG scan parameters are implemented.");
            }
            if (_progressive && (_spectralStart > _spectralEnd || _spectralEnd > 63
                || _successiveHigh > 13 || _successiveLow > 13
                || _successiveHigh != 0 && _successiveHigh != _successiveLow + 1
                || _spectralStart == 0 && _spectralEnd != 0
                || _spectralStart > 0 && count != 1))
                throw Error("The progressive JPEG scan parameters are invalid.");
        }

        private JpegDecodedImage DecodeScan()
        {
            int components = _components.Count;
            int outputWidth = ReducedDimension(_width);
            int outputHeight = ReducedDimension(_height);
            long outputLength = checked((long)outputWidth * outputHeight * components);
            if (outputLength > maximumDecodedBytes || outputLength > int.MaxValue)
                throw Error("Decoded JPEG data exceeds the configured safety limit.");
            int maxHorizontal = _components.Max(item => item.HorizontalSampling);
            int maxVertical = _components.Max(item => item.VerticalSampling);
            if (_components.Any(component => maxHorizontal % component.HorizontalSampling != 0
                || maxVertical % component.VerticalSampling != 0))
                throw Error("The JPEG component sampling factors are incompatible.");
            int mcuColumns = (_width + maxHorizontal * 8 - 1) / (maxHorizontal * 8);
            int mcuRows = (_height + maxVertical * 8 - 1) / (maxVertical * 8);
            bool rowSamples = _scanComponents.Count > 1 || components == 1;
            // Rows of one MCU row are written once the next MCU row is decoded whenever a
            // component is interpolated vertically, so three MCU rows stay in storage.
            int slots = rowSamples && VerticalLag(maxVertical) ? 3 : 1;
            long temporarySampleBytes = 0;
            foreach (Component component in _components)
            {
                if (_quantization[component.QuantizationTable] is null
                    || component.DcTable > 3 || component.AcTable > 3
                    || _huffman[0, component.DcTable] is null
                    || _huffman[1, component.AcTable] is null)
                    throw Error("The JPEG scan references an undefined decoding table.");
                int blockSize = 8 / reduction;
                component.Stride = checked(
                    mcuColumns * component.HorizontalSampling * blockSize);
                component.RingRows = checked(
                    (rowSamples ? slots : mcuRows) * component.VerticalSampling * blockSize);
                // A single-component image is stored one block row at a time.
                if (_scanComponents.Count == 1 && rowSamples) component.RingRows = blockSize;
                component.DecodedRows = rowSamples ? 0 : int.MaxValue;
                int sampleCount = checked(component.Stride
                    * (rowSamples ? slots : mcuRows) * component.VerticalSampling * blockSize);
                temporarySampleBytes = checked(temporarySampleBytes + sampleCount);
                if (temporarySampleBytes > MaximumTemporarySampleBytes)
                    throw Error("JPEG temporary samples exceed the configured safety limit.");
                component.Samples = new byte[sampleCount];
                if (compatibilityRecovery && !rowSamples) Array.Fill(component.Samples, (byte)128);
            }
            PrepareUpsampling(maxHorizontal, maxVertical, outputWidth, outputHeight);
            var bits = new BitReader(source, _position);
            byte[]? output = rowSamples ? new byte[(int)outputLength] : null;
            if (compatibilityRecovery && output is not null) Array.Fill(output, (byte)128);
            int mcu = 0;
            if (_scanComponents.Count == 1)
            {
                Component component = _scanComponents[0];
                int blockSize = 8 / reduction;
                int blockColumns = checked((_width * component.HorizontalSampling
                    + maxHorizontal * 8 - 1) / (maxHorizontal * 8));
                int blockRows = checked((_height * component.VerticalSampling
                    + maxVertical * 8 - 1) / (maxVertical * 8));
                for (int row = 0; row < blockRows; row++)
                {
                    try
                    {
                        for (int column = 0; column < blockColumns; column++, mcu++)
                        {
                            if (_restartInterval > 0 && mcu > 0 && mcu % _restartInterval == 0)
                            {
                                bits.ConsumeRestart();
                                component.DcPredictor = 0;
                            }
                            DecodeBlock(bits, component,
                                column * blockSize, rowSamples ? 0 : row * blockSize);
                        }
                    }
                    catch (PdfFilterException error) when (compatibilityRecovery && error.Message is
                        "The JPEG scan data is truncated." or "A JPEG marker interrupted scan data.")
                    {
                        return RecoveredOutput();
                    }
                    if (output is not null)
                    {
                        component.DecodedRows = (row + 1) * blockSize;
                        WriteRows(row * blockSize, blockSize);
                    }
                }
                if (output is not null)
                    return new JpegDecodedImage(output, outputWidth, outputHeight,
                        components, _width, _height);
                return BuildOutput(maxHorizontal, maxVertical, outputWidth, outputHeight,
                    (int)outputLength);
            }
            for (int row = 0; row < mcuRows; row++)
            {
                try
                {
                    for (int column = 0; column < mcuColumns; column++, mcu++)
                    {
                        if (_restartInterval > 0 && mcu > 0 && mcu % _restartInterval == 0)
                        {
                            bits.ConsumeRestart();
                            foreach (Component component in _components) component.DcPredictor = 0;
                        }
                        foreach (Component component in _scanComponents)
                        {
                            int slotTop = row % slots * component.VerticalSampling * (8 / reduction);
                            for (int vertical = 0; vertical < component.VerticalSampling; vertical++)
                                for (int horizontal = 0; horizontal < component.HorizontalSampling; horizontal++)
                                    DecodeBlock(bits, component,
                                        (column * component.HorizontalSampling + horizontal)
                                            * (8 / reduction),
                                        slotTop + vertical * (8 / reduction));
                        }
                    }
                }
                catch (PdfFilterException error) when (compatibilityRecovery && error.Message is
                    "The JPEG scan data is truncated." or "A JPEG marker interrupted scan data.")
                {
                    // The complete row held back for its successor is written with its
                    // chroma clamped at its own last row.
                    if (slots > 1 && row > 0) WriteMcuRow(row - 1);
                    return RecoveredOutput();
                }
                foreach (Component component in _components)
                    component.DecodedRows = (row + 1) * component.VerticalSampling * (8 / reduction);
                if (slots == 1) WriteMcuRow(row);
                else if (row > 0) WriteMcuRow(row - 1);
            }
            if (slots > 1 && mcuRows > 0) WriteMcuRow(mcuRows - 1);
            return new JpegDecodedImage(output!, outputWidth, outputHeight,
                components, _width, _height);

            JpegDecodedImage RecoveredOutput() => output is not null
                ? new JpegDecodedImage(output, outputWidth, outputHeight, components, _width, _height)
                : BuildOutput(maxHorizontal, maxVertical, outputWidth, outputHeight, (int)outputLength);

            void WriteMcuRow(int row) =>
                WriteRows(row * maxVertical * (8 / reduction), maxVertical * (8 / reduction));

            void WriteRows(int firstRow, int rowCount)
            {
                int rows = Math.Min(rowCount, outputHeight - firstRow);
                if (rows <= 0) return;
                WriteOutput(output.AsSpan(firstRow * outputWidth * components,
                    rows * outputWidth * components), outputWidth, firstRow, rows);
            }
        }

        private JpegDecodedImage BuildOutput(
            int maxHorizontal, int maxVertical, int outputWidth, int outputHeight,
            int outputLength)
        {
            var output = new byte[outputLength];
            PrepareUpsampling(maxHorizontal, maxVertical, outputWidth, outputHeight);
            WriteOutput(output, outputWidth, 0, outputHeight);
            return new JpegDecodedImage(
                output, outputWidth, outputHeight, _components.Count, _width, _height);
        }

        private bool VerticalLag(int maxVertical) =>
            _components.Any(component => component.VerticalSampling != maxVertical);

        // Chroma siting. ITU-T T.871 places each sample of a subsampled component at the
        // center of the full-resolution samples it covers. Along one axis with ratio s (the
        // largest sampling factor over this component's), component sample i covers
        // full-resolution samples s*i through s*i + s - 1, so its center is at
        //     s*i + (s - 1) / 2.
        // Full-resolution sample x therefore lies at component coordinate
        //     p = (x - (s - 1) / 2) / s = (2x + 1 - s) / (2s).
        // Writing n = 2x + 1 - s, the two nearest component samples are i0 = floor(n / 2s)
        // and i0 + 1, and with r = n - 2s*i0 (0 <= r < 2s) x lies r / 2s of the way from
        // i0 to i0 + 1. Linear interpolation gives
        //     ((2s - r) * c[i0] + r * c[i0 + 1]) / 2s.
        // For s = 2 the nearer sample weighs 3/4 and the farther 1/4 (the triangle filter),
        // for s = 4 the nearer weighs 7/8 or 5/8, and for s = 3 every third sample lands
        // exactly on a component sample while the others weigh 2/3 and 1/3. Indices outside the component
        // clamp to its first or last real sample, which is the nearest one. The two
        // axes are separable: the vertical blend is kept as an exact integer numerator,
        // the horizontal blend multiplies in its own, and one rounding to nearest (halves
        // up) divides by the product of both denominators. The same geometry holds on a
        // reduced grid, because a reduced component sample covers s reduced
        // full-resolution samples in the same arrangement.
        private void PrepareUpsampling(int maxHorizontal, int maxVertical, int outputWidth, int outputHeight)
        {
            foreach (Component component in _components)
            {
                int stepH = maxHorizontal / component.HorizontalSampling;
                int stepV = maxVertical / component.VerticalSampling;
                component.StepH = stepH;
                component.StepV = stepV;
                component.RealColumns = (outputWidth + stepH - 1) / stepH;
                component.RealRows = (outputHeight + stepV - 1) / stepV;
                if (stepH == 1 && stepV == 1) continue;
                component.UpsampledRow = new byte[outputWidth];
                if (stepV > 1 || stepH == 2) component.VerticalSums = new int[component.RealColumns];
                if (stepH == 1) continue;
                component.LeftColumn = new int[outputWidth];
                component.RightColumn = new int[outputWidth];
                component.RightWeight = new int[outputWidth];
                int lastColumn = component.RealColumns - 1;
                for (int x = 0; x < outputWidth; x++)
                {
                    int n = 2 * x + 1 - stepH;
                    int left = FloorDivide(n, 2 * stepH);
                    component.LeftColumn[x] = Math.Clamp(left, 0, lastColumn);
                    component.RightColumn[x] = Math.Clamp(left + 1, 0, lastColumn);
                    component.RightWeight[x] = n - 2 * stepH * left;
                }
            }
        }

        private static int FloorDivide(int value, int divisor) =>
            value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);

        private static ReadOnlySpan<byte> StoredRow(Component component, int row) =>
            component.Samples.AsSpan(row % component.RingRows * component.Stride, component.Stride);

        private static ReadOnlySpan<byte> OutputRow(Component component, int y, int outputWidth)
        {
            int stepH = component.StepH, stepV = component.StepV;
            if (stepH == 1 && stepV == 1) return StoredRow(component, y);
            int lastRow = Math.Min(component.RealRows, component.DecodedRows) - 1;
            int lastColumn = component.RealColumns - 1;
            byte[] result = component.UpsampledRow;
            int verticalScale = 1;
            ReadOnlySpan<byte> single = default;
            int[] sums = component.VerticalSums;
            if (stepV == 1)
                single = StoredRow(component, y);
            else
            {
                int n = 2 * y + 1 - stepV;
                int top = FloorDivide(n, 2 * stepV);
                int bottomWeight = n - 2 * stepV * top;
                int topWeight = 2 * stepV - bottomWeight;
                ReadOnlySpan<byte> upper = StoredRow(component, Math.Clamp(top, 0, lastRow));
                ReadOnlySpan<byte> lower = StoredRow(component, Math.Clamp(top + 1, 0, lastRow));
                for (int i = 0; i <= lastColumn; i++)
                    sums[i] = topWeight * upper[i] + bottomWeight * lower[i];
                verticalScale = 2 * stepV;
            }
            int denominator = verticalScale * (stepH == 1 ? 1 : 2 * stepH);
            int half = denominator / 2;
            int shift = (denominator & (denominator - 1)) == 0 ? BitOperations.Log2((uint)denominator) : -1;
            if (stepH == 1)
            {
                // Vertical only; stepV > 1 here.
                for (int x = 0; x < outputWidth; x++)
                    result[x] = (byte)(shift >= 0 ? (sums[x] + half) >> shift : (sums[x] + half) / denominator);
                return result;
            }
            if (stepH == 2 && shift >= 0)
            {
                if (stepV == 1)
                    for (int i = 0; i <= lastColumn; i++) sums[i] = single[i];
                UpsampleHalves(sums, lastColumn, result, outputWidth, half, shift);
                return result;
            }
            int[] leftColumn = component.LeftColumn, rightColumn = component.RightColumn,
                rightWeight = component.RightWeight;
            int fullWeight = 2 * stepH;
            if (stepV == 1)
            {
                for (int x = 0; x < outputWidth; x++)
                {
                    int weight = rightWeight[x];
                    int sum = (fullWeight - weight) * single[leftColumn[x]] + weight * single[rightColumn[x]];
                    result[x] = (byte)(shift >= 0 ? (sum + half) >> shift : (sum + half) / denominator);
                }
                return result;
            }
            for (int x = 0; x < outputWidth; x++)
            {
                int weight = rightWeight[x];
                int sum = (fullWeight - weight) * sums[leftColumn[x]] + weight * sums[rightColumn[x]];
                result[x] = (byte)(shift >= 0 ? (sum + half) >> shift : (sum + half) / denominator);
            }
            return result;
        }

        // The ratio-two case of the general blend: x = 2i reads component coordinate
        // i - 1/4 (weights 1 and 3 on i - 1 and i) and x = 2i + 1 reads i + 1/4 (weights 3
        // and 1 on i and i + 1). Only the first and last columns need the edge clamp.
        private static void UpsampleHalves(
            int[] sums, int last, byte[] result, int width, int half, int shift)
        {
            if (last == 0)
            {
                byte only = (byte)((4 * sums[0] + half) >> shift);
                result.AsSpan(0, width).Fill(only);
                return;
            }
            result[0] = (byte)((4 * sums[0] + half) >> shift);
            result[1] = (byte)((3 * sums[0] + sums[1] + half) >> shift);
            for (int i = 1; i < last; i++)
            {
                int near = 3 * sums[i] + half;
                result[2 * i] = (byte)((near + sums[i - 1]) >> shift);
                result[2 * i + 1] = (byte)((near + sums[i + 1]) >> shift);
            }
            int lastNear = 3 * sums[last] + half;
            result[2 * last] = (byte)((lastNear + sums[last - 1]) >> shift);
            if (2 * last + 1 < width) result[2 * last + 1] = (byte)((lastNear + sums[last]) >> shift);
        }

        private void WriteOutput(Span<byte> output, int outputWidth, int startRow, int outputHeight)
        {
            int components = _components.Count;
            int transform = colorTransform ?? _adobeTransform ?? DefaultTransform();
            if (compatibilityRecovery && colorTransform is not null
                && (components == 1 && transform != 0
                    || components == 3 && transform == 2))
                transform = _adobeTransform ?? DefaultTransform();
            if (transform is < 0 or > 2 || components == 1 && transform != 0
                || components == 3 && transform == 2)
                throw Error("The JPEG color transform is not supported.");
            if (components == 1)
            {
                Component gray = _components[0];
                for (int y = 0; y < outputHeight; y++)
                    StoredRow(gray, startRow + y)[..outputWidth]
                        .CopyTo(output.Slice(y * outputWidth, outputWidth));
                return;
            }
            // Every row below is at full resolution: subsampled components arrive already
            // interpolated at their centered sites, and full-resolution ones as stored.
            Component firstComponent = _components[0], secondComponent = _components[1],
                thirdComponent = _components[2];
            Component? fourthComponent = components == 4 ? _components[3] : null;
            if (fourthComponent is not null && transform == 0)
            {
                for (int y = 0, offset = 0; y < outputHeight; y++)
                {
                    ReadOnlySpan<byte> firstRow = OutputRow(firstComponent, startRow + y, outputWidth);
                    ReadOnlySpan<byte> secondRow = OutputRow(secondComponent, startRow + y, outputWidth);
                    ReadOnlySpan<byte> thirdRow = OutputRow(thirdComponent, startRow + y, outputWidth);
                    ReadOnlySpan<byte> fourthRow = OutputRow(fourthComponent, startRow + y, outputWidth);
                    for (int x = 0; x < outputWidth; x++)
                    {
                        output[offset++] = firstRow[x];
                        output[offset++] = secondRow[x];
                        output[offset++] = thirdRow[x];
                        output[offset++] = fourthRow[x];
                    }
                }
                return;
            }
            if (fourthComponent is not null && transform == 2)
            {
                int[] crRed = CrRed, cbGreen = CbGreen, crGreen = CrGreen, cbBlue = CbBlue;
                byte[] rangeLimit = RangeLimit;
                for (int y = 0, offset = 0; y < outputHeight; y++)
                {
                    ReadOnlySpan<byte> lumaRow = OutputRow(firstComponent, startRow + y, outputWidth);
                    ReadOnlySpan<byte> cbRow = OutputRow(secondComponent, startRow + y, outputWidth);
                    ReadOnlySpan<byte> crRow = OutputRow(thirdComponent, startRow + y, outputWidth);
                    ReadOnlySpan<byte> blackRow = OutputRow(fourthComponent, startRow + y, outputWidth);
                    for (int x = 0; x < outputWidth; x++)
                    {
                        int luma = lumaRow[x], cb = cbRow[x], cr = crRow[x];
                        int red = luma + crRed[cr];
                        int green = luma + ((cbGreen[cb] + crGreen[cr]) >> ChromaShift);
                        int blue = luma + cbBlue[cb];
                        output[offset++] = (byte)(255 - rangeLimit[red + RangeLimitOffset]);
                        output[offset++] = (byte)(255 - rangeLimit[green + RangeLimitOffset]);
                        output[offset++] = (byte)(255 - rangeLimit[blue + RangeLimitOffset]);
                        output[offset++] = blackRow[x];
                    }
                }
                return;
            }
            if (components == 3 && transform == 1)
            {
                // Three-component YCbCr, the common case: the same arithmetic as the general
                // loop below without per-sample transform dispatch.
                int[] crRed = CrRed, cbGreen = CbGreen, crGreen = CrGreen, cbBlue = CbBlue;
                byte[] rangeLimit = RangeLimit;
                for (int y = 0, offset = 0; y < outputHeight; y++)
                {
                    ReadOnlySpan<byte> lumaRow = OutputRow(firstComponent, startRow + y, outputWidth);
                    ReadOnlySpan<byte> cbRow = OutputRow(secondComponent, startRow + y, outputWidth);
                    ReadOnlySpan<byte> crRow = OutputRow(thirdComponent, startRow + y, outputWidth);
                    Span<byte> outputRow = output.Slice(offset, outputWidth * 3);
                    for (int x = 0, target = 0; x < outputWidth; x++, target += 3)
                    {
                        int luma = lumaRow[x];
                        int cb = cbRow[x];
                        int cr = crRow[x];
                        int red = luma + crRed[cr];
                        int green = luma + ((cbGreen[cb] + crGreen[cr]) >> ChromaShift);
                        int blue = luma + cbBlue[cb];
                        outputRow[target] = rangeLimit[red + RangeLimitOffset];
                        outputRow[target + 1] = rangeLimit[green + RangeLimitOffset];
                        outputRow[target + 2] = rangeLimit[blue + RangeLimitOffset];
                    }
                    offset += outputWidth * 3;
                }
                return;
            }
            for (int y = 0, offset = 0; y < outputHeight; y++)
            {
                ReadOnlySpan<byte> firstRow = OutputRow(firstComponent, startRow + y, outputWidth);
                ReadOnlySpan<byte> secondRow = OutputRow(secondComponent, startRow + y, outputWidth);
                ReadOnlySpan<byte> thirdRow = OutputRow(thirdComponent, startRow + y, outputWidth);
                ReadOnlySpan<byte> fourthRow = fourthComponent is null ? []
                    : OutputRow(fourthComponent, startRow + y, outputWidth);
                for (int x = 0; x < outputWidth; x++)
                {
                    int first = firstRow[x];
                    int second = secondRow[x];
                    int third = thirdRow[x];
                    if (transform == 0)
                    {
                        output[offset++] = (byte)first;
                        output[offset++] = (byte)second;
                        output[offset++] = (byte)third;
                        if (components == 4)
                            output[offset++] = fourthRow[x];
                    }
                    else
                    {
                        // Fixed-point YCbCr to RGB with 16 fractional bits and per-chroma tables,
                        // the libjpeg formulation; results are within one step of the real-valued
                        // conversion.
                        int red = first + CrRed[third];
                        int green = first + ((CbGreen[second] + CrGreen[third]) >> ChromaShift);
                        int blue = first + CbBlue[second];
                        red = RangeLimit[red + RangeLimitOffset];
                        green = RangeLimit[green + RangeLimitOffset];
                        blue = RangeLimit[blue + RangeLimitOffset];
                        output[offset++] = (byte)(components == 4 ? 255 - red : red);
                        output[offset++] = (byte)(components == 4 ? 255 - green : green);
                        output[offset++] = (byte)(components == 4 ? 255 - blue : blue);
                        if (components == 4) output[offset++] = fourthRow[x];
                    }
                }
            }
        }

        private void DecodeProgressiveScan()
        {
            var (MaxHorizontal, MaxVertical, McuColumns, McuRows) = PrepareProgressiveComponents();
            if (_spectralStart == 0 && _successiveHigh == 0
                && _scanComponents.Any(component => component.DcTable > 3
                    || _huffman[0, component.DcTable] is null))
                throw Error("The progressive JPEG scan references an undefined DC table.");
            if (_spectralStart > 0 && (_scanComponents[0].AcTable > 3
                || _huffman[1, _scanComponents[0].AcTable] is null))
                throw Error("The progressive JPEG scan references an undefined AC table.");
            foreach (Component component in _scanComponents) component.DcPredictor = 0;
            var bits = new BitReader(source, _position);
            int eobRun = 0;
            int unit = 0;

            if (_scanComponents.Count > 1)
            {
                for (int row = 0; row < McuRows; row++)
                    for (int column = 0; column < McuColumns; column++, unit++)
                    {
                        RestartIfNeeded(bits, unit, ref eobRun);
                        foreach (Component component in _scanComponents)
                            for (int vertical = 0; vertical < component.VerticalSampling; vertical++)
                                for (int horizontal = 0;
                                    horizontal < component.HorizontalSampling; horizontal++)
                                    try
                                    {
                                        if (DecodeProgressiveBlock(bits, component,
                                                column * component.HorizontalSampling + horizontal,
                                                row * component.VerticalSampling + vertical,
                                                ref eobRun))
                                            goto RecoverScan;
                                    }
                                    catch (PdfFilterException error) when (
                                        error.Message == "A JPEG marker interrupted scan data."
                                        || compatibilityRecovery && error.Message == "The JPEG scan data is truncated.")
                                    {
                                        goto RecoverScan;
                                    }
                    }
            }
            else
            {
                Component component = _scanComponents[0];
                for (int row = 0; row < component.VisibleBlockRows; row++)
                    for (int column = 0; column < component.VisibleBlockColumns; column++, unit++)
                    {
                        RestartIfNeeded(bits, unit, ref eobRun);
                        try
                        {
                            if (DecodeProgressiveBlock(bits, component,
                                    column, row, ref eobRun))
                                goto RecoverScan;
                        }
                        catch (PdfFilterException error) when (
                            error.Message == "A JPEG marker interrupted scan data."
                            || compatibilityRecovery && error.Message == "The JPEG scan data is truncated.")
                        {
                            goto RecoverScan;
                        }
                    }
            }
            _position = bits.Position;
            return;

        RecoverScan:
            bits.SkipToMarker();
            _position = bits.Position;

            void RestartIfNeeded(BitReader reader, int index, ref int run)
            {
                if (_restartInterval <= 0 || index == 0 || index % _restartInterval != 0) return;
                reader.ConsumeRestart();
                foreach (Component component in _scanComponents) component.DcPredictor = 0;
                run = 0;
            }
        }

        private (int MaxHorizontal, int MaxVertical, int McuColumns, int McuRows)
            PrepareProgressiveComponents()
        {
            int outputWidth = ReducedDimension(_width);
            int outputHeight = ReducedDimension(_height);
            long outputLength = checked((long)outputWidth * outputHeight * _components.Count);
            if (outputLength > maximumDecodedBytes || outputLength > int.MaxValue)
                throw Error("Decoded JPEG data exceeds the configured safety limit.");
            int maxHorizontal = _components.Max(item => item.HorizontalSampling);
            int maxVertical = _components.Max(item => item.VerticalSampling);
            if (_components.Any(component => maxHorizontal % component.HorizontalSampling != 0
                || maxVertical % component.VerticalSampling != 0))
                throw Error("The JPEG component sampling factors are incompatible.");
            int mcuColumns = (_width + maxHorizontal * 8 - 1) / (maxHorizontal * 8);
            int mcuRows = (_height + maxVertical * 8 - 1) / (maxVertical * 8);
            long coefficientBytes = 0;
            foreach (Component component in _components)
            {
                if (_quantization[component.QuantizationTable] is null)
                    throw Error("The JPEG frame references an undefined quantization table.");
                component.BlockColumns = checked(mcuColumns * component.HorizontalSampling);
                component.BlockRows = checked(mcuRows * component.VerticalSampling);
                component.VisibleBlockColumns = checked(
                    (_width * component.HorizontalSampling + maxHorizontal * 8 - 1)
                    / (maxHorizontal * 8));
                component.VisibleBlockRows = checked(
                    (_height * component.VerticalSampling + maxVertical * 8 - 1)
                    / (maxVertical * 8));
                int coefficientCount = checked(
                    component.BlockColumns * component.BlockRows * 64);
                coefficientBytes = checked(
                    coefficientBytes + (long)coefficientCount * sizeof(int));
                if (coefficientBytes > MaximumTemporarySampleBytes)
                    throw Error("JPEG temporary coefficients exceed the configured safety limit.");
                if (component.Coefficients.Length == 0)
                    component.Coefficients = new int[coefficientCount];
            }
            return (maxHorizontal, maxVertical, mcuColumns, mcuRows);
        }

        private bool DecodeProgressiveBlock(
            BitReader bits, Component component, int blockX, int blockY, ref int eobRun)
        {
            int offset = checked((blockY * component.BlockColumns + blockX) * 64);
            Span<int> coefficients = component.Coefficients.AsSpan(offset, 64);
            if (_spectralStart == 0)
            {
                if (_successiveHigh == 0)
                {
                    int category = _huffman[0, component.DcTable]!.Decode(bits);
                    if (category > 11) throw Error("A progressive JPEG DC coefficient is invalid.");
                    component.DcPredictor += Receive(bits, category);
                    coefficients[0] = component.DcPredictor << _successiveLow;
                    component.HasDc = true;
                }
                else if (bits.ReadBits(1) != 0)
                {
                    int bit = 1 << _successiveLow;
                    if ((Math.Abs(coefficients[0]) & bit) == 0)
                        coefficients[0] += coefficients[0] >= 0 ? bit : -bit;
                }
                return false;
            }
            if (_successiveHigh == 0)
                DecodeInitialAc(bits, component, coefficients, ref eobRun);
            else
                return DecodeRefinedAc(bits, component, coefficients, ref eobRun);
            return false;
        }

        private void DecodeInitialAc(
            BitReader bits, Component component, Span<int> coefficients, ref int eobRun)
        {
            if (eobRun > 0) { eobRun--; return; }
            HuffmanTable table = _huffman[1, component.AcTable]!;
            for (int index = _spectralStart; index <= _spectralEnd;)
            {
                int value = table.Decode(bits);
                int run = value >> 4;
                int size = value & 15;
                if (size == 0)
                {
                    if (run < 15)
                    {
                        eobRun = (1 << run) + bits.ReadBits(run) - 1;
                        return;
                    }
                    index += 16;
                    continue;
                }
                index += run;
                if (index > _spectralEnd || size > 10)
                    throw Error("A progressive JPEG AC coefficient is invalid.");
                coefficients[ZigZag[index++]] = Receive(bits, size) << _successiveLow;
            }
        }

        private bool DecodeRefinedAc(
            BitReader bits, Component component, Span<int> coefficients, ref int eobRun)
        {
            HuffmanTable table = _huffman[1, component.AcTable]!;
            int bit = 1 << _successiveLow;
            int index = _spectralStart;
            if (eobRun == 0)
            {
                while (index <= _spectralEnd)
                {
                    int value = table.Decode(bits);
                    int run = value >> 4;
                    int size = value & 15;
                    int newCoefficient = 0;
                    if (size == 0)
                    {
                        if (run < 15)
                        {
                            eobRun = (1 << run) + bits.ReadBits(run);
                            break;
                        }
                        run = 16;
                    }
                    else
                    {
                        if (size != 1)
                            throw Error("A progressive JPEG AC refinement is invalid.");
                        newCoefficient = bits.ReadBits(1) == 0 ? -bit : bit;
                    }
                    while (index <= _spectralEnd)
                    {
                        int position = ZigZag[index];
                        if (coefficients[position] != 0)
                            Refine(bits, coefficients, position, bit);
                        else if (run-- == 0)
                            break;
                        index++;
                    }
                    if (newCoefficient != 0)
                    {
                        if (index > _spectralEnd)
                            return true;
                        coefficients[ZigZag[index++]] = newCoefficient;
                    }
                }
            }
            if (eobRun > 0)
            {
                while (index <= _spectralEnd)
                {
                    int position = ZigZag[index++];
                    if (coefficients[position] != 0)
                        Refine(bits, coefficients, position, bit);
                }
                eobRun--;
            }
            return false;
        }

        private static void Refine(BitReader bits, Span<int> coefficients, int position, int bit)
        {
            if (bits.ReadBits(1) == 0 || (Math.Abs(coefficients[position]) & bit) != 0) return;
            coefficients[position] += coefficients[position] > 0 ? bit : -bit;
        }

        private JpegDecodedImage BuildProgressiveOutput()
        {
            var (MaxHorizontal, MaxVertical, McuColumns, McuRows) = PrepareProgressiveComponents();
            if (_components.Any(component => !component.HasDc))
                throw Error("The progressive JPEG is missing a DC scan.");
            // As in the baseline path, vertical interpolation writes each MCU row once the
            // next one is in storage.
            int slots = VerticalLag(MaxVertical) ? 3 : 1;
            foreach (Component component in _components)
            {
                int blockSize = 8 / reduction;
                component.Stride = checked(component.BlockColumns * blockSize);
                component.RingRows = checked(slots * component.VerticalSampling * blockSize);
                component.DecodedRows = 0;
                component.Samples = new byte[checked(component.Stride * component.RingRows)];
            }
            int outputWidth = ReducedDimension(_width);
            int outputHeight = ReducedDimension(_height);
            int components = _components.Count;
            var output = new byte[checked(outputWidth * outputHeight * components)];
            PrepareUpsampling(MaxHorizontal, MaxVertical, outputWidth, outputHeight);
            int rowHeight = MaxVertical * (8 / reduction);
            for (int row = 0; row < McuRows; row++)
            {
                foreach (Component component in _components)
                {
                    int blockSize = 8 / reduction;
                    int slotTop = row % slots * component.VerticalSampling * blockSize;
                    for (int vertical = 0; vertical < component.VerticalSampling; vertical++)
                    {
                        int sourceRow = row * component.VerticalSampling + vertical;
                        if (sourceRow >= component.VisibleBlockRows) break;
                        for (int column = 0; column < component.VisibleBlockColumns; column++)
                        {
                            int offset = (sourceRow * component.BlockColumns + column) * 64;
                            ReadOnlySpan<int> block = component.Coefficients.AsSpan(offset, 64);
                            WriteBlock(component, column * blockSize, slotTop + vertical * blockSize,
                                block, block[1..].IndexOfAnyExcept(0) >= 0);
                        }
                    }
                    component.DecodedRows = (row + 1) * component.VerticalSampling * blockSize;
                }
                if (slots == 1) WriteMcuRow(row);
                else if (row > 0) WriteMcuRow(row - 1);
            }
            if (slots > 1 && McuRows > 0) WriteMcuRow(McuRows - 1);
            return new JpegDecodedImage(output, outputWidth, outputHeight, components, _width, _height);

            void WriteMcuRow(int row)
            {
                int firstRow = row * rowHeight;
                int rows = Math.Min(rowHeight, outputHeight - firstRow);
                if (rows <= 0) return;
                WriteOutput(output.AsSpan(firstRow * outputWidth * components,
                    rows * outputWidth * components), outputWidth, firstRow, rows);
            }
        }

        private void DecodeBlock(BitReader bits, Component component, int left, int top)
        {
            Span<int> coefficients = stackalloc int[64];
            HuffmanTable dc = _huffman[0, component.DcTable]!;
            HuffmanTable ac = _huffman[1, component.AcTable]!;
            int category = dc.Decode(bits);
            if (category > 11) throw Error("A JPEG DC coefficient is invalid.");
            component.DcPredictor += Receive(bits, category);
            coefficients[0] = component.DcPredictor;
            bool hasAc = false;
            short[] fastAc = ac.FastAc;
            for (int index = 1; index < 64;)
            {
                // A short code whose value bits also fit the lookahead decodes in one step,
                // with the same run, value and bounds as the general path below.
                if (bits.TryPeekBits(HuffmanTable.FastBits, out int peek) && fastAc[peek] != 0)
                {
                    int entry = fastAc[peek];
                    bits.SkipBits(entry & 15);
                    index += (entry >> 4) & 15;
                    if (index >= 64) throw Error("A JPEG AC coefficient is invalid.");
                    coefficients[ZigZag[index++]] = entry >> 8;
                    hasAc = true;
                    continue;
                }
                int value = ac.Decode(bits);
                int run = value >> 4;
                int size = value & 15;
                if (size == 0)
                {
                    if (run == 0) break;
                    if (run != 15) throw Error("A JPEG AC run is invalid.");
                    index += 16;
                    if (index > 64) throw Error("A JPEG AC run exceeds its block.");
                    continue;
                }
                index += run;
                if (index >= 64 || size > 10) throw Error("A JPEG AC coefficient is invalid.");
                coefficients[ZigZag[index++]] = Receive(bits, size);
                hasAc = true;
            }
            WriteBlock(component, left, top, coefficients, hasAc);
        }

        private void WriteBlock(
            Component component, int left, int top, ReadOnlySpan<int> coefficients, bool hasAc)
        {
            int[] quantization = _quantization[component.QuantizationTable];
            int blockSize = 8 / reduction;
            if (!hasAc)
            {
                // Retain the full transform's operation order at rounding boundaries.
                double dc = Scales[0] * coefficients[0] * quantization[0];
                byte sample = Clamp(128 + Scales[0] * dc / 4);
                for (int y = 0; y < blockSize; y++)
                    component.Samples.AsSpan((top + y) * component.Stride + left, blockSize)
                        .Fill(sample);
                return;
            }
            int reductionIndex = reduction switch { 1 => 0, 2 => 1, 4 => 2, _ => 3 };
            if (blockSize == 1)
            {
                if (!_progressive)
                {
                    double dc = Scales[0] * coefficients[0] * quantization[0];
                    double sample = 128 + Scales[0] * dc / 4;
                    // Keep the full calculation wherever an AC term could affect rounding.
                    if (sample is >= -256 and <= 512
                        && Math.Abs(sample - Math.Round(sample)) < 0.5 - ReducedSingleAcBound)
                    {
                        component.Samples[top * component.Stride + left] = Clamp(sample);
                        return;
                    }
                }
                double sum = 0;
                for (int v = 0; v < 8; v++)
                {
                    int lastU = 7;
                    while (lastU >= 0 && coefficients[v * 8 + lastU] == 0) lastU--;
                    if (lastU < 0) continue;
                    double row = 0;
                    for (int u = 0; u <= lastU; u++)
                        row += Scales[u] * coefficients[v * 8 + u]
                            * quantization[v * 8 + u] * VectorCosines[reductionIndex * 64 + u * 8];
                    sum += Scales[v] * row * VectorCosines[reductionIndex * 64 + v * 8];
                }
                component.Samples[top * component.Stride + left] = Clamp(128 + sum / 4);
                return;
            }
            if (blockSize == 8)
            {
                WriteFullBlock(component, left, top, coefficients, ScaledQuantization(component.QuantizationTable));
                return;
            }
            if (Vector.IsHardwareAccelerated && blockSize >= Vector<double>.Count)
            {
                WriteVectorBlock(component, left, top, coefficients, quantization, blockSize, reductionIndex);
                return;
            }
            Span<double> horizontal = stackalloc double[64];
            Span<double> dequantized = stackalloc double[8];
            Span<bool> rowHasCoefficients = stackalloc bool[8];
            // Zero coefficients add an exact zero to every sum, so trailing zeros in a row
            // and all-zero rows are skipped without changing any rounding.
            for (int v = 0; v < 8; v++)
            {
                int lastU = 7;
                while (lastU >= 0 && coefficients[v * 8 + lastU] == 0) lastU--;
                rowHasCoefficients[v] = lastU >= 0;
                if (lastU < 0)
                {
                    horizontal.Slice(v * blockSize, blockSize).Clear();
                    continue;
                }
                for (int u = 0; u <= lastU; u++)
                    dequantized[u] = Scales[u] * coefficients[v * 8 + u]
                        * quantization[v * 8 + u];
                for (int x = 0; x < blockSize; x++)
                {
                    double sum = 0;
                    for (int u = 0; u <= lastU; u++)
                        sum += dequantized[u] * VectorCosines[reductionIndex * 64 + u * 8 + x];
                    horizontal[v * blockSize + x] = Scales[v] * sum;
                }
            }
            for (int y = 0; y < blockSize; y++)
                for (int x = 0; x < blockSize; x++)
                {
                    double sum = 0;
                    for (int v = 0; v < 8; v++)
                        if (rowHasCoefficients[v])
                            sum += horizontal[v * blockSize + x]
                                * VectorCosines[reductionIndex * 64 + v * 8 + y];
                    component.Samples[(top + y) * component.Stride + left + x]
                        = Clamp(128 + sum / 4);
                }
        }

        private readonly double[]?[] _scaledQuantization = new double[]?[4];

        // Quantization multiplied by the Arai, Agui, and Nakajima scale factors and the 1/8
        // normalization of the two-dimensional transform, so the full-size block transform
        // below needs no per-coefficient scaling of its own.
        private double[] ScaledQuantization(int table)
        {
            double[]? scaled = _scaledQuantization[table];
            if (scaled is not null) return scaled;
            int[] quantization = _quantization[table];
            scaled = new double[64];
            for (int v = 0; v < 8; v++)
                for (int u = 0; u < 8; u++)
                    scaled[v * 8 + u] = quantization[v * 8 + u] * AanScale[v] * AanScale[u] / 8;
            return _scaledQuantization[table] = scaled;
        }

        // Full-size 8x8 inverse transform by the Arai, Agui, and Nakajima factorization in
        // double precision (the same algorithm as libjpeg's floating-point IDCT with exact
        // constants). It computes the same function as the direct transform; the two differ
        // only by floating-point rounding on the order of 1e-12 before samples are rounded.
        private static void WriteFullBlock(Component component, int left, int top,
            ReadOnlySpan<int> coefficients, double[] scaledQuantization)
        {
            Span<double> workspace = stackalloc double[64];
            // Columns first. A column with only a DC term transforms to eight copies of it.
            for (int u = 0; u < 8; u++)
            {
                if (coefficients[8 + u] == 0 && coefficients[16 + u] == 0 && coefficients[24 + u] == 0
                    && coefficients[32 + u] == 0 && coefficients[40 + u] == 0 && coefficients[48 + u] == 0
                    && coefficients[56 + u] == 0)
                {
                    double dc = coefficients[u] * scaledQuantization[u];
                    for (int v = 0; v < 8; v++) workspace[v * 8 + u] = dc;
                    continue;
                }
                InverseTransform8(
                    coefficients[u] * scaledQuantization[u],
                    coefficients[8 + u] * scaledQuantization[8 + u],
                    coefficients[16 + u] * scaledQuantization[16 + u],
                    coefficients[24 + u] * scaledQuantization[24 + u],
                    coefficients[32 + u] * scaledQuantization[32 + u],
                    coefficients[40 + u] * scaledQuantization[40 + u],
                    coefficients[48 + u] * scaledQuantization[48 + u],
                    coefficients[56 + u] * scaledQuantization[56 + u],
                    workspace, u, 8);
            }
            Span<double> row = stackalloc double[8];
            for (int y = 0; y < 8; y++)
            {
                int rowStart = y * 8;
                InverseTransform8(workspace[rowStart], workspace[rowStart + 1], workspace[rowStart + 2],
                    workspace[rowStart + 3], workspace[rowStart + 4], workspace[rowStart + 5],
                    workspace[rowStart + 6], workspace[rowStart + 7], row, 0, 1);
                int output = (top + y) * component.Stride + left;
                for (int x = 0; x < 8; x++)
                    component.Samples[output + x] = Clamp(128 + row[x]);
            }
        }

        private static readonly double[] AanScale =
        [
            1, Math.Cos(Math.PI / 16) * Math.Sqrt(2), Math.Cos(2 * Math.PI / 16) * Math.Sqrt(2),
            Math.Cos(3 * Math.PI / 16) * Math.Sqrt(2), 1, Math.Cos(5 * Math.PI / 16) * Math.Sqrt(2),
            Math.Cos(6 * Math.PI / 16) * Math.Sqrt(2), Math.Cos(7 * Math.PI / 16) * Math.Sqrt(2)
        ];
        private static readonly double Sqrt2 = Math.Sqrt(2);
        private static readonly double Cos8x2 = 2 * Math.Cos(Math.PI / 8);
        private static readonly double Cos3x8x2Sqrt2 = 2 * Math.Cos(3 * Math.PI / 8) * Math.Sqrt(2);
        private static readonly double Cos8x2Sqrt2 = 2 * Math.Cos(Math.PI / 8) * Math.Sqrt(2);

        /// <summary>One eight-point AAN inverse transform written to <paramref name="destination"/> at <paramref name="start"/> with the given stride.</summary>
        private static void InverseTransform8(double in0, double in1, double in2, double in3,
            double in4, double in5, double in6, double in7, Span<double> destination, int start, int stride)
        {
            double tmp10 = in0 + in4, tmp11 = in0 - in4;
            double tmp13 = in2 + in6, tmp12 = (in2 - in6) * Sqrt2 - tmp13;
            double tmp0 = tmp10 + tmp13, tmp3 = tmp10 - tmp13;
            double tmp1 = tmp11 + tmp12, tmp2 = tmp11 - tmp12;
            double z13 = in5 + in3, z10 = in5 - in3, z11 = in1 + in7, z12 = in1 - in7;
            double tmp7 = z11 + z13;
            double tmp11b = (z11 - z13) * Sqrt2;
            double z5 = (z10 + z12) * Cos8x2;
            double tmp10b = Cos3x8x2Sqrt2 * z12 - z5;
            double tmp12b = -Cos8x2Sqrt2 * z10 + z5;
            double tmp6 = tmp12b - tmp7, tmp5 = tmp11b - tmp6, tmp4 = tmp10b + tmp5;
            destination[start] = tmp0 + tmp7;
            destination[start + stride] = tmp1 + tmp6;
            destination[start + 2 * stride] = tmp2 + tmp5;
            destination[start + 3 * stride] = tmp3 - tmp4;
            destination[start + 4 * stride] = tmp3 + tmp4;
            destination[start + 5 * stride] = tmp2 - tmp5;
            destination[start + 6 * stride] = tmp1 - tmp6;
            destination[start + 7 * stride] = tmp0 - tmp7;
        }

        private static void WriteVectorBlock(Component component, int left, int top,
            ReadOnlySpan<int> coefficients, int[] quantization, int blockSize, int reductionIndex)
        {
            Span<double> horizontal = stackalloc double[64];
            Span<double> dequantized = stackalloc double[8];
            Span<bool> rowHasCoefficients = stackalloc bool[8];
            int lanes = Vector<double>.Count;
            // Each lane is a separate output sample. Preserve coefficient order
            // within every lane so vectorization does not change rounding. Zero
            // coefficients contribute an exact zero to every sum, so trailing zeros in a
            // row and all-zero rows are skipped without changing any rounding.
            for (int v = 0; v < 8; v++)
            {
                int lastU = 7;
                while (lastU >= 0 && coefficients[v * 8 + lastU] == 0) lastU--;
                rowHasCoefficients[v] = lastU >= 0;
                if (lastU < 0)
                {
                    horizontal.Slice(v * blockSize, blockSize).Clear();
                    continue;
                }
                for (int u = 0; u <= lastU; u++)
                    dequantized[u] = Scales[u] * coefficients[v * 8 + u] * quantization[v * 8 + u];
                for (int x = 0; x < blockSize; x += lanes)
                {
                    Vector<double> sum = Vector<double>.Zero;
                    for (int u = 0; u <= lastU; u++)
                        sum += new Vector<double>(dequantized[u])
                            * new Vector<double>(VectorCosines, reductionIndex * 64 + u * 8 + x);
                    (new Vector<double>(Scales[v]) * sum).CopyTo(horizontal.Slice(v * blockSize + x, lanes));
                }
            }
            for (int y = 0; y < blockSize; y++)
            for (int x = 0; x < blockSize; x += lanes)
            {
                Vector<double> sum = Vector<double>.Zero;
                for (int v = 0; v < 8; v++)
                    if (rowHasCoefficients[v])
                        sum += new Vector<double>(horizontal.Slice(v * blockSize + x, lanes))
                            * new Vector<double>(VectorCosines[reductionIndex * 64 + v * 8 + y]);
                for (int lane = 0; lane < lanes; lane++)
                    component.Samples[(top + y) * component.Stride + left + x + lane]
                        = Clamp(128 + sum[lane] / 4);
            }
        }

        private static int Receive(BitReader bits, int count)
        {
            if (count == 0) return 0;
            int value = bits.ReadBits(count);
            int threshold = 1 << (count - 1);
            return value >= threshold ? value : value - (1 << count) + 1;
        }

        private byte ReadMarker()
        {
            while (_position < source.Length && source.Span[_position] != 0xFF) _position++;
            while (_position < source.Length && source.Span[_position] == 0xFF) _position++;
            if (_position >= source.Length) throw Error("A JPEG marker is truncated.");
            byte marker = source.Span[_position++];
            if (marker == 0) throw Error("A stuffed byte appears outside JPEG scan data.");
            return marker;
        }

        private int ReadUInt16()
        {
            if (_position + 2 > source.Length) throw Error("A JPEG value is truncated.");
            int value = source.Span[_position] << 8 | source.Span[_position + 1];
            _position += 2;
            return value;
        }

        private static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
        private int ReducedDimension(int value) => checked((value + reduction - 1) / reduction);
        private static PdfFilterException Error(string message) => new(message);
    }

    private static double[,] CreateCosines()
    {
        var result = new double[8, 8];
        for (int sample = 0; sample < 8; sample++)
            for (int frequency = 0; frequency < 8; frequency++)
                result[sample, frequency] = Math.Cos(
                    (2 * sample + 1) * frequency * Math.PI / 16);
        return result;
    }

    private static double[] CreateVectorCosines()
    {
        var values = new double[4 * 64];
        for (int level = 0; level < 4; level++)
        for (int frequency = 0; frequency < 8; frequency++)
        for (int sample = 0; sample < 8; sample++)
            values[level * 64 + frequency * 8 + sample] = ReducedCosines[level, sample, frequency];
        return values;
    }

    private static double[,,] CreateReducedCosines()
    {
        var result = new double[4, 8, 8];
        for (int reductionIndex = 0; reductionIndex < 4; reductionIndex++)
        {
            int reduction = 1 << reductionIndex;
            int blockSize = 8 / reduction;
            for (int sample = 0; sample < blockSize; sample++)
                for (int frequency = 0; frequency < 8; frequency++)
                {
                    double sum = 0;
                    for (int offset = 0; offset < reduction; offset++)
                        sum += Cosines[sample * reduction + offset, frequency];
                    result[reductionIndex, sample, frequency] = sum / reduction;
                }
        }
        return result;
    }

    private sealed class Component(
        byte identifier, int horizontalSampling, int verticalSampling, int quantizationTable)
    {
        internal byte Identifier { get; } = identifier;
        internal int HorizontalSampling { get; } = horizontalSampling;
        internal int VerticalSampling { get; } = verticalSampling;
        internal int QuantizationTable { get; } = quantizationTable;
        internal int DcTable { get; set; }
        internal int AcTable { get; set; }
        internal int DcPredictor { get; set; }
        internal int Stride { get; set; }
        internal int BlockColumns { get; set; }
        internal int BlockRows { get; set; }
        internal int VisibleBlockColumns { get; set; }
        internal int VisibleBlockRows { get; set; }
        internal int[] Coefficients { get; set; } = [];
        internal byte[] Samples { get; set; } = [];
        /// <summary>Rows held in <see cref="Samples"/>; component row r is stored at r modulo this.</summary>
        internal int RingRows { get; set; }
        /// <summary>Component rows decoded so far; interpolation never reads past them.</summary>
        internal int DecodedRows { get; set; }
        internal int StepH { get; set; }
        internal int StepV { get; set; }
        internal int RealColumns { get; set; }
        internal int RealRows { get; set; }
        internal byte[] UpsampledRow { get; set; } = [];
        internal int[] VerticalSums { get; set; } = [];
        internal int[] LeftColumn { get; set; } = [];
        internal int[] RightColumn { get; set; } = [];
        internal int[] RightWeight { get; set; } = [];
        internal bool HasDc { get; set; }
        internal bool InScan { get; set; }
    }

    private sealed class HuffmanTable
    {
        internal const int FastBits = 9;
        private readonly ushort[] _lookahead = new ushort[256];

        /// <summary>
        /// For AC tables: by the next nine bits, the coefficient value, zero run and total bit
        /// count of a code whose value bits also fit those nine bits, or zero otherwise.
        /// </summary>
        internal short[] FastAc { get; } = new short[1 << FastBits];
        private readonly int[] _firstCode = new int[17];
        private readonly int[] _counts = new int[17];
        private readonly int[] _firstSymbol = new int[17];
        private readonly byte[] _symbols;

        internal HuffmanTable(IReadOnlyList<int> counts, ReadOnlySpan<byte> symbols)
        {
            _symbols = symbols.ToArray();
            int code = 0;
            int symbol = 0;
            for (int length = 1; length <= 16; length++)
            {
                if (code + counts[length - 1] > 1 << length)
                    throw new PdfFilterException("The JPEG Huffman table is oversubscribed.");
                _firstCode[length] = code;
                _counts[length] = counts[length - 1];
                _firstSymbol[length] = symbol;
                for (int index = 0; index < counts[length - 1]; index++, code++, symbol++)
                {
                    if (length > 8) continue;
                    int suffixBits = 8 - length;
                    Array.Fill(_lookahead, (ushort)(length << 8 | symbols[symbol]),
                        code << suffixBits, 1 << suffixBits);
                }
                code <<= 1;
            }
            BuildFastAc();
        }

        private void BuildFastAc()
        {
            for (int length = 1; length <= FastBits; length++)
                for (int index = 0; index < _counts[length]; index++)
                {
                    int code = _firstCode[length] + index;
                    int symbol = _symbols[_firstSymbol[length] + index];
                    int run = symbol >> 4, size = symbol & 15;
                    if (size == 0 || length + size > FastBits) continue;
                    int span = 1 << (FastBits - length);
                    for (int fill = 0; fill < span; fill++)
                    {
                        int window = code << (FastBits - length) | fill;
                        int raw = window >> (FastBits - length - size) & ((1 << size) - 1);
                        int value = raw >= 1 << (size - 1) ? raw : raw - (1 << size) + 1;
                        if (value is < -128 or > 127) continue;
                        FastAc[window] = (short)(value << 8 | run << 4 | (length + size));
                    }
                }
        }

        internal int Decode(BitReader bits)
        {
            int code;
            int firstLength;
            // Lookahead must not consume a marker when a short code ends the scan.
            if (bits.TryPeekBits(8, out int prefix))
            {
                int entry = _lookahead[prefix];
                if (entry != 0)
                {
                    bits.SkipBits(entry >> 8);
                    return entry & 255;
                }
                code = prefix;
                bits.SkipBits(8);
                firstLength = 9;
            }
            else
                return DecodeBitwise(bits, 0, 1);
            // A long code resolves from one peek when sixteen bits are buffered; nearer the
            // end of the scan it is read bit by bit as before.
            if (bits.TryPeekBits(8, out int rest))
            {
                int window = prefix << 8 | rest;
                for (int length = 9; length <= 16; length++)
                {
                    int candidate = window >> (16 - length);
                    int index = candidate - _firstCode[length];
                    if ((uint)index < (uint)_counts[length])
                    {
                        bits.SkipBits(length - 8);
                        return _symbols[_firstSymbol[length] + index];
                    }
                }
                throw new PdfFilterException("The JPEG scan contains an invalid Huffman code.");
            }
            return DecodeBitwise(bits, code, firstLength);
        }

        private int DecodeBitwise(BitReader bits, int code, int firstLength)
        {
            for (int length = firstLength; length <= 16; length++)
            {
                code = code << 1 | bits.ReadBits(1);
                int offset = code - _firstCode[length];
                if ((uint)offset < (uint)_counts[length])
                    return _symbols[_firstSymbol[length] + offset];
            }
            throw new PdfFilterException("The JPEG scan contains an invalid Huffman code.");
        }
    }

    private sealed class BitReader(ReadOnlyMemory<byte> source, int position)
    {
        private int _position = position;
        private int _bits;
        private ulong _buffer;

        internal int Position => _position;

        internal int ReadBits(int count)
        {
            if (!TryPeekBits(count, out int result))
                throw new PdfFilterException(_position >= source.Length
                    ? "The JPEG scan data is truncated."
                    : "A JPEG marker interrupted scan data.");
            SkipBits(count);
            return result;
        }

        internal void SkipBits(int count) => _bits -= count;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool TryPeekBits(int count, out int result)
        {
            if (_bits < count && !TryFillBits(count))
            {
                result = 0;
                return false;
            }
            result = (int)(_buffer >> (_bits - count)) & ((1 << count) - 1);
            return true;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private bool TryFillBits(int count)
        {
            ReadOnlySpan<byte> bytes = source.Span;
            // Bytes are buffered ahead, up to the next marker, so most reads need no refill.
            // A marker stops the buffering where it stood before, and only a read needing
            // more bits than remain ahead of it fails.
            while (_bits <= 56)
            {
                if (_position >= bytes.Length) return _bits >= count;
                int value = bytes[_position++];
                if (value == 0xFF)
                {
                    int markerPosition = _position - 1;
                    while (_position < bytes.Length && bytes[_position] == 0xFF) _position++;
                    if (_position >= bytes.Length || bytes[_position++] != 0)
                    {
                        _position = markerPosition;
                        return _bits >= count;
                    }
                }
                _buffer = _buffer << 8 | (uint)value;
                _bits += 8;
            }
            return true;
        }

        internal void ConsumeRestart()
        {
            _bits = 0;
            _buffer = 0;
            while (_position < source.Length && source.Span[_position] == 0xFF) _position++;
            if (_position >= source.Length || source.Span[_position] is < 0xD0 or > 0xD7)
                throw new PdfFilterException("The JPEG restart marker is missing.");
            _position++;
        }

        internal void SkipToMarker()
        {
            _bits = 0;
            _buffer = 0;
            while (_position + 1 < source.Length)
            {
                if (source.Span[_position] != 0xFF) { _position++; continue; }
                int marker = _position;
                while (_position < source.Length && source.Span[_position] == 0xFF) _position++;
                if (_position >= source.Length) { _position = marker; return; }
                if (source.Span[_position] == 0) { _position++; continue; }
                _position = marker;
                return;
            }
        }
    }
}

/// <summary>Decoded JPEG samples and source dimensions.</summary>
public readonly record struct JpegDecodedImage(
    byte[] Samples, int Width, int Height, int Components,
    int SourceWidth, int SourceHeight);
