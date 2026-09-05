namespace KillerPdf.Engine.Filters;

internal static class PdfJpegDecoder
{
    private static readonly int[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63
    ];
    private static readonly double[,] Cosines = CreateCosines();
    private static readonly double[] Scales = [1 / Math.Sqrt(2), 1, 1, 1, 1, 1, 1, 1];

    internal static byte[] Decode(
        ReadOnlySpan<byte> source, int maximumDecodedBytes, int? colorTransform = null)
    {
        try
        {
            return new Decoder(source.ToArray(), maximumDecodedBytes, colorTransform).Decode();
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

    private sealed class Decoder(byte[] source, int maximumDecodedBytes, int? colorTransform)
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

        internal byte[] Decode()
        {
            if (source.Length < 4 || source[0] != 0xFF || source[1] != 0xD8)
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
            if (end - _position < 6 || source[_position++] != 8)
                throw Error("Only 8-bit baseline JPEG images are implemented.");
            _height = ReadUInt16();
            _width = ReadUInt16();
            int count = source[_position++];
            if (_width <= 0 || _height <= 0 || count is not (1 or 3 or 4))
                throw Error("The JPEG frame dimensions or component count are not supported.");
            if (_position + count * 3 != end) throw Error("The JPEG frame table is malformed.");
            var identifiers = new HashSet<byte>();
            for (int index = 0; index < count; index++)
            {
                byte id = source[_position++];
                byte sampling = source[_position++];
                int horizontal = sampling >> 4;
                int vertical = sampling & 15;
                int quantization = source[_position++];
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
                byte info = source[_position++];
                int precision = info >> 4;
                int identifier = info & 15;
                if (precision != 0 || identifier > 3 || _position + 64 > end)
                    throw Error("The JPEG quantization table is unsupported or truncated.");
                var table = new int[64];
                for (int index = 0; index < 64; index++)
                {
                    int value = source[_position++];
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
                byte info = source[_position++];
                int tableClass = info >> 4;
                int identifier = info & 15;
                if (tableClass > 1 || identifier > 3 || _position + 16 > end)
                    throw Error("The JPEG Huffman table header is invalid.");
                int[] counts = new int[16];
                int symbolCount = 0;
                for (int index = 0; index < 16; index++)
                    symbolCount += counts[index] = source[_position++];
                if (symbolCount > 256 || _position + symbolCount > end)
                    throw Error("The JPEG Huffman table is truncated.");
                _huffman[tableClass, identifier] = new HuffmanTable(counts,
                    source.AsSpan(_position, symbolCount));
                _position += symbolCount;
            }
        }

        private void ReadAdobeSegment(int end)
        {
            if (end - _position >= 12
                && source.AsSpan(_position, 5).SequenceEqual("Adobe"u8))
                _adobeTransform = source[_position + 11];
        }

        private void ReadScanHeader(int end)
        {
            if (_components.Count == 0 || _position >= end)
                throw Error("The JPEG scan precedes its frame.");
            int count = source[_position++];
            if (count < 1 || count > _components.Count || _position + count * 2 + 3 != end
                || !_progressive && count != _components.Count)
                throw Error("The JPEG scan component table is unsupported.");
            _scanComponents.Clear();
            foreach (Component component in _components) component.InScan = false;
            for (int index = 0; index < count; index++)
            {
                byte identifier = source[_position++];
                Component component = _components.SingleOrDefault(item => item.Identifier == identifier)
                    ?? throw Error("The JPEG scan references an undefined component.");
                if (component.InScan) throw Error("The JPEG scan repeats a component.");
                byte tables = source[_position++];
                component.DcTable = tables >> 4;
                component.AcTable = tables & 15;
                component.InScan = true;
                _scanComponents.Add(component);
            }
            _spectralStart = source[_position++];
            _spectralEnd = source[_position++];
            byte approximation = source[_position++];
            _successiveHigh = approximation >> 4;
            _successiveLow = approximation & 15;
            if (!_progressive && (_spectralStart != 0 || _spectralEnd != 63
                || approximation != 0))
                throw Error("Only sequential JPEG scan parameters are implemented.");
            if (_progressive && (_spectralStart > _spectralEnd || _spectralEnd > 63
                || _successiveHigh > 13 || _successiveLow > 13
                || _successiveHigh != 0 && _successiveHigh != _successiveLow + 1
                || _spectralStart == 0 && _spectralEnd != 0
                || _spectralStart > 0 && count != 1))
                throw Error("The progressive JPEG scan parameters are invalid.");
        }

        private byte[] DecodeScan()
        {
            int components = _components.Count;
            long outputLength = checked((long)_width * _height * components);
            if (outputLength > maximumDecodedBytes || outputLength > int.MaxValue)
                throw Error("Decoded JPEG data exceeds the configured safety limit.");
            int maxHorizontal = _components.Max(item => item.HorizontalSampling);
            int maxVertical = _components.Max(item => item.VerticalSampling);
            if (_components.Any(component => maxHorizontal % component.HorizontalSampling != 0
                || maxVertical % component.VerticalSampling != 0))
                throw Error("The JPEG component sampling factors are incompatible.");
            int mcuColumns = (_width + maxHorizontal * 8 - 1) / (maxHorizontal * 8);
            int mcuRows = (_height + maxVertical * 8 - 1) / (maxVertical * 8);
            foreach (Component component in _components)
            {
                if (_quantization[component.QuantizationTable] is null
                    || component.DcTable > 3 || component.AcTable > 3
                    || _huffman[0, component.DcTable] is null
                    || _huffman[1, component.AcTable] is null)
                    throw Error("The JPEG scan references an undefined decoding table.");
                component.Stride = checked(mcuColumns * component.HorizontalSampling * 8);
                component.Samples = new byte[checked(component.Stride
                    * mcuRows * component.VerticalSampling * 8)];
            }
            var bits = new BitReader(source, _position);
            int mcu = 0;
            for (int row = 0; row < mcuRows; row++)
                for (int column = 0; column < mcuColumns; column++, mcu++)
                {
                    if (_restartInterval > 0 && mcu > 0 && mcu % _restartInterval == 0)
                    {
                        bits.ConsumeRestart();
                        foreach (Component component in _components) component.DcPredictor = 0;
                    }
                    foreach (Component component in _scanComponents)
                        for (int vertical = 0; vertical < component.VerticalSampling; vertical++)
                            for (int horizontal = 0; horizontal < component.HorizontalSampling; horizontal++)
                                DecodeBlock(bits, component,
                                    (column * component.HorizontalSampling + horizontal) * 8,
                                    (row * component.VerticalSampling + vertical) * 8);
                }
            return BuildOutput(maxHorizontal, maxVertical, (int)outputLength);
        }

        private byte[] BuildOutput(int maxHorizontal, int maxVertical, int outputLength)
        {
            int components = _components.Count;
            var output = new byte[outputLength];
            int transform = colorTransform ?? _adobeTransform ?? (components == 3 ? 1 : 0);
            if (transform is < 0 or > 2 || components == 3 && transform == 2
                || components == 4 && transform == 1)
                throw Error("The JPEG color transform is not supported.");
            for (int y = 0, offset = 0; y < _height; y++)
                for (int x = 0; x < _width; x++)
                {
                    if (components == 1)
                    {
                        output[offset++] = Sample(_components[0], x, y, maxHorizontal, maxVertical);
                        continue;
                    }
                    int first = Sample(_components[0], x, y, maxHorizontal, maxVertical);
                    int second = Sample(_components[1], x, y, maxHorizontal, maxVertical);
                    int third = Sample(_components[2], x, y, maxHorizontal, maxVertical);
                    if (transform == 0)
                    {
                        output[offset++] = (byte)first;
                        output[offset++] = (byte)second;
                        output[offset++] = (byte)third;
                        if (components == 4)
                            output[offset++] = Sample(_components[3], x, y,
                                maxHorizontal, maxVertical);
                    }
                    else
                    {
                        double cb = second - 128, cr = third - 128;
                        output[offset++] = Clamp(first + 1.402 * cr);
                        output[offset++] = Clamp(first - 0.344136 * cb - 0.714136 * cr);
                        output[offset++] = Clamp(first + 1.772 * cb);
                        if (components == 4)
                            output[offset++] = Sample(_components[3], x, y,
                                maxHorizontal, maxVertical);
                    }
                }
            return output;
        }

        private void DecodeProgressiveScan()
        {
            var geometry = PrepareProgressiveComponents();
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
                for (int row = 0; row < geometry.McuRows; row++)
                    for (int column = 0; column < geometry.McuColumns; column++, unit++)
                    {
                        RestartIfNeeded(bits, unit, ref eobRun);
                        foreach (Component component in _scanComponents)
                            for (int vertical = 0; vertical < component.VerticalSampling; vertical++)
                                for (int horizontal = 0;
                                    horizontal < component.HorizontalSampling; horizontal++)
                                    if (DecodeProgressiveBlock(bits, component,
                                            column * component.HorizontalSampling + horizontal,
                                            row * component.VerticalSampling + vertical, ref eobRun))
                                        goto RecoverScan;
                    }
            }
            else
            {
                Component component = _scanComponents[0];
                for (int row = 0; row < component.VisibleBlockRows; row++)
                    for (int column = 0; column < component.VisibleBlockColumns; column++, unit++)
                    {
                        RestartIfNeeded(bits, unit, ref eobRun);
                        if (DecodeProgressiveBlock(bits, component, column, row, ref eobRun))
                            goto RecoverScan;
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
            long outputLength = checked((long)_width * _height * _components.Count);
            if (outputLength > maximumDecodedBytes || outputLength > int.MaxValue)
                throw Error("Decoded JPEG data exceeds the configured safety limit.");
            int maxHorizontal = _components.Max(item => item.HorizontalSampling);
            int maxVertical = _components.Max(item => item.VerticalSampling);
            if (_components.Any(component => maxHorizontal % component.HorizontalSampling != 0
                || maxVertical % component.VerticalSampling != 0))
                throw Error("The JPEG component sampling factors are incompatible.");
            int mcuColumns = (_width + maxHorizontal * 8 - 1) / (maxHorizontal * 8);
            int mcuRows = (_height + maxVertical * 8 - 1) / (maxVertical * 8);
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
                if (component.Coefficients.Length == 0)
                    component.Coefficients = new int[checked(
                        component.BlockColumns * component.BlockRows * 64)];
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

        private byte[] BuildProgressiveOutput()
        {
            var geometry = PrepareProgressiveComponents();
            if (_components.Any(component => !component.HasDc))
                throw Error("The progressive JPEG is missing a DC scan.");
            foreach (Component component in _components)
            {
                component.Stride = checked(component.BlockColumns * 8);
                component.Samples = new byte[checked(component.Stride * component.BlockRows * 8)];
                for (int row = 0; row < component.VisibleBlockRows; row++)
                    for (int column = 0; column < component.VisibleBlockColumns; column++)
                    {
                        int offset = (row * component.BlockColumns + column) * 64;
                        WriteBlock(component, column * 8, row * 8,
                            component.Coefficients.AsSpan(offset, 64));
                    }
            }
            return BuildOutput(geometry.MaxHorizontal, geometry.MaxVertical,
                checked(_width * _height * _components.Count));
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
            for (int index = 1; index < 64;)
            {
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
            }
            WriteBlock(component, left, top, coefficients);
        }

        private void WriteBlock(
            Component component, int left, int top, ReadOnlySpan<int> coefficients)
        {
            int[] quantization = _quantization[component.QuantizationTable];
            Span<double> horizontal = stackalloc double[64];
            for (int v = 0; v < 8; v++)
                for (int x = 0; x < 8; x++)
                {
                    double sum = 0;
                    for (int u = 0; u < 8; u++)
                        sum += Scales[u] * coefficients[v * 8 + u]
                            * quantization[v * 8 + u] * Cosines[x, u];
                    horizontal[v * 8 + x] = sum;
                }
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    double sum = 0;
                    for (int v = 0; v < 8; v++)
                        sum += Scales[v] * horizontal[v * 8 + x] * Cosines[y, v];
                    component.Samples[(top + y) * component.Stride + left + x]
                        = Clamp(128 + sum / 4);
                }
        }

        private static int Receive(BitReader bits, int count)
        {
            if (count == 0) return 0;
            int value = bits.ReadBits(count);
            int threshold = 1 << (count - 1);
            return value >= threshold ? value : value - (1 << count) + 1;
        }

        private static byte Sample(Component component, int x, int y,
            int maxHorizontal, int maxVertical)
        {
            int sampleX = x * component.HorizontalSampling / maxHorizontal;
            int sampleY = y * component.VerticalSampling / maxVertical;
            return component.Samples[sampleY * component.Stride + sampleX];
        }

        private byte ReadMarker()
        {
            while (_position < source.Length && source[_position] != 0xFF) _position++;
            while (_position < source.Length && source[_position] == 0xFF) _position++;
            if (_position >= source.Length) throw Error("A JPEG marker is truncated.");
            byte marker = source[_position++];
            if (marker == 0) throw Error("A stuffed byte appears outside JPEG scan data.");
            return marker;
        }

        private int ReadUInt16()
        {
            if (_position + 2 > source.Length) throw Error("A JPEG value is truncated.");
            int value = source[_position] << 8 | source[_position + 1];
            _position += 2;
            return value;
        }

        private static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
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
        internal bool HasDc { get; set; }
        internal bool InScan { get; set; }
    }

    private sealed class HuffmanTable
    {
        private readonly Dictionary<int, byte> _symbols = [];

        internal HuffmanTable(IReadOnlyList<int> counts, ReadOnlySpan<byte> symbols)
        {
            int code = 0;
            int symbol = 0;
            for (int length = 1; length <= 16; length++)
            {
                if (code + counts[length - 1] > 1 << length)
                    throw new PdfFilterException("The JPEG Huffman table is oversubscribed.");
                for (int index = 0; index < counts[length - 1]; index++)
                    _symbols.Add(length << 16 | code++, symbols[symbol++]);
                code <<= 1;
            }
        }

        internal int Decode(BitReader bits)
        {
            int code = 0;
            for (int length = 1; length <= 16; length++)
            {
                code = code << 1 | bits.ReadBits(1);
                if (_symbols.TryGetValue(length << 16 | code, out byte symbol)) return symbol;
            }
            throw new PdfFilterException("The JPEG scan contains an invalid Huffman code.");
        }
    }

    private sealed class BitReader(byte[] source, int position)
    {
        private int _position = position;
        private int _bits;
        private int _buffer;

        internal int Position => _position;

        internal int ReadBits(int count)
        {
            while (_bits < count)
            {
                if (_position >= source.Length)
                    throw new PdfFilterException("The JPEG scan data is truncated.");
                int value = source[_position++];
                if (value == 0xFF)
                {
                    while (_position < source.Length && source[_position] == 0xFF) _position++;
                    if (_position >= source.Length || source[_position++] != 0)
                        throw new PdfFilterException("A JPEG marker interrupted scan data.");
                }
                _buffer = _buffer << 8 | value;
                _bits += 8;
            }
            int result = _buffer >> (_bits - count) & ((1 << count) - 1);
            _bits -= count;
            return result;
        }

        internal void ConsumeRestart()
        {
            _bits = 0;
            _buffer = 0;
            while (_position < source.Length && source[_position] == 0xFF) _position++;
            if (_position >= source.Length || source[_position] is < 0xD0 or > 0xD7)
                throw new PdfFilterException("The JPEG restart marker is missing.");
            _position++;
        }

        internal void SkipToMarker()
        {
            _bits = 0;
            _buffer = 0;
            while (_position + 1 < source.Length)
            {
                if (source[_position] != 0xFF) { _position++; continue; }
                int marker = _position;
                while (_position < source.Length && source[_position] == 0xFF) _position++;
                if (_position >= source.Length) { _position = marker; return; }
                if (source[_position] == 0) { _position++; continue; }
                _position = marker;
                return;
            }
        }
    }
}
