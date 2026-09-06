using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Numerics;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Documents;

/// <summary>A bounded, versioned OCR glyph-classification model.</summary>
public sealed class PdfOcrRecognitionModel
{
    private static readonly byte[] Magic = "KPOCR4\0"u8.ToArray();
    private static readonly byte[] PreviousMagic = "KPOCR3\0"u8.ToArray();
    private static readonly byte[] LegacyMagic = "KPOCR2\0"u8.ToArray();
    internal const int MaximumModelBytes = 256 * 1024 * 1024;
    internal const int MaximumTrainingModelValues = 4 * 1024 * 1024;
    private const double PriorTieWindow = 1e-9;
    private const double ShapeMismatchPenalty = 0.25;
    private const double CoarseGradientDistanceWeight = 24;
    private const double FineGradientDistanceWeight = 24;
    private const int CoarseGradientCellCount = 2;
    private const int FineGradientCellCount = 8;
    private const int GradientBinCount = 4;
    private const int CoarseGradientDescriptorLength =
        CoarseGradientCellCount * CoarseGradientCellCount * GradientBinCount;
    private const int FineGradientDescriptorLength =
        FineGradientCellCount * FineGradientCellCount * GradientBinCount;
    private readonly string[] _labels;
    private readonly float[] _weights;
    private readonly float[] _biases;
    private readonly float[] _priors;
    private readonly sbyte[] _shapeBuckets;
    private readonly float[] _rowProjections;
    private readonly float[] _columnProjections;
    private readonly float[] _coarseGradientDescriptors;
    private readonly float[] _fineGradientDescriptors;
    private readonly Dictionary<string, ulong> _labelShapeMasks;
    private readonly Dictionary<string, float> _labelPriors;
    private readonly Dictionary<string, int[]> _prototypeIndexesByLabel;
    private readonly bool _usesPrototypeShapes;

    private PdfOcrRecognitionModel(int width, int height, string[] labels,
        float[] weights, float[] biases, float[] priors)
    {
        Width = width;
        Height = height;
        _labels = labels;
        _weights = weights;
        _biases = biases;
        _priors = priors;
        _usesPrototypeShapes = !weights.AsSpan().ContainsAnyExceptInRange(0, float.MaxValue);
        int featureCount = checked(width * height);
        _shapeBuckets = new sbyte[labels.Length];
        _rowProjections = new float[checked(labels.Length * height)];
        _columnProjections = new float[checked(labels.Length * width)];
        _coarseGradientDescriptors = new float[
            checked(labels.Length * CoarseGradientDescriptorLength)];
        _fineGradientDescriptors = new float[
            checked(labels.Length * FineGradientDescriptorLength)];
        _labelShapeMasks = new Dictionary<string, ulong>(StringComparer.Ordinal);
        _labelPriors = new Dictionary<string, float>(StringComparer.Ordinal);
        var prototypeIndexes = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int label = 0; label < labels.Length; label++)
        {
            if (!prototypeIndexes.TryGetValue(labels[label], out List<int>? indexes))
                prototypeIndexes.Add(labels[label], indexes = []);
            indexes.Add(label);
            if (!_labelPriors.TryGetValue(labels[label], out float prior)
                || priors[label] > prior)
                _labelPriors[labels[label]] = priors[label];
            _shapeBuckets[label] = checked((sbyte)ShapeBucket(
                weights.AsSpan(label * featureCount, featureCount), width, height));
            BuildProjections(weights.AsSpan(label * featureCount, featureCount),
                width, height, _rowProjections.AsSpan(label * height, height),
                _columnProjections.AsSpan(label * width, width));
            if (_usesPrototypeShapes && featureCount >= 64)
            {
                BuildGradientDescriptor(
                    weights.AsSpan(label * featureCount, featureCount), width, height,
                    CoarseGradientCellCount, _coarseGradientDescriptors.AsSpan(
                        label * CoarseGradientDescriptorLength,
                        CoarseGradientDescriptorLength));
                BuildGradientDescriptor(
                    weights.AsSpan(label * featureCount, featureCount), width, height,
                    FineGradientCellCount, _fineGradientDescriptors.AsSpan(
                        label * FineGradientDescriptorLength,
                        FineGradientDescriptorLength));
            }
            if (_shapeBuckets[label] >= 0)
                _labelShapeMasks[labels[label]] = _labelShapeMasks.GetValueOrDefault(labels[label])
                    | 1UL << _shapeBuckets[label];
        }
        _prototypeIndexesByLabel = prototypeIndexes.ToDictionary(
            item => item.Key, item => item.Value.ToArray(), StringComparer.Ordinal);
        Labels = Array.AsReadOnly(labels.Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Gets the normalized glyph width expected by the model.</summary>
    public int Width { get; }
    /// <summary>Gets the normalized glyph height expected by the model.</summary>
    public int Height { get; }
    /// <summary>Gets unique model labels in classifier order.</summary>
    public IReadOnlyList<string> Labels { get; }

    /// <summary>Creates a model for offline training and deterministic packaging.</summary>
    public static PdfOcrRecognitionModel Create(int width, int height,
        IEnumerable<string> labels, ReadOnlyMemory<float> weights,
        ReadOnlyMemory<float> biases)
    {
        ArgumentNullException.ThrowIfNull(labels);
        string[] names = labels.ToArray();
        return CreateCore(width, height, names, weights, biases,
            new float[names.Length]);
    }

    internal static PdfOcrRecognitionModel CreatePrototype(int width, int height,
        IEnumerable<string> labels, ReadOnlyMemory<float> weights,
        ReadOnlyMemory<float> biases, ReadOnlyMemory<float> priors)
    {
        ArgumentNullException.ThrowIfNull(labels);
        return CreateCore(width, height, labels.ToArray(), weights, biases, priors);
    }

    private static PdfOcrRecognitionModel CreateCore(int width, int height,
        string[] names, ReadOnlyMemory<float> weights,
        ReadOnlyMemory<float> biases, ReadOnlyMemory<float> priors)
    {
        if (width is <= 0 or > 128) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is <= 0 or > 128) throw new ArgumentOutOfRangeException(nameof(height));
        if (names.Length is <= 0 or > 65_536 || names.Any(label =>
            string.IsNullOrEmpty(label) || Encoding.UTF8.GetByteCount(label) > 64
            || label.EnumerateRunes().Any(rune => rune == Rune.ReplacementChar)))
            throw new ArgumentException(
                "OCR model labels are empty, oversized, or invalid.", "labels");
        int featureCount = checked(width * height);
        if (weights.Length != checked(featureCount * names.Length))
            throw new ArgumentException("OCR model weights do not match its dimensions.", nameof(weights));
        if (biases.Length != names.Length)
            throw new ArgumentException("OCR model biases do not match its labels.", nameof(biases));
        if (priors.Length != names.Length)
            throw new ArgumentException("OCR model priors do not match its labels.", nameof(priors));
        long labelBytes = names.Sum(label =>
            1L + Encoding.UTF8.GetByteCount(label));
        if (!FitsSerializedSize(labelBytes, names.Length, weights.Length))
            throw new ArgumentException(
                "The OCR recognition model exceeds the size limit.", nameof(weights));
        if (weights.Span.ContainsAnyExceptInRange(float.MinValue, float.MaxValue)
            || biases.Span.ContainsAnyExceptInRange(float.MinValue, float.MaxValue)
            || priors.Span.ContainsAnyExceptInRange(float.MinValue, float.MaxValue))
            throw new ArgumentException("OCR model values must be finite.");
        return new PdfOcrRecognitionModel(width, height, names,
            weights.ToArray(), biases.ToArray(), priors.ToArray());
    }

    /// <summary>Combines compatible language models without changing their prototypes.</summary>
    public static PdfOcrRecognitionModel Combine(
        IEnumerable<PdfOcrRecognitionModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        PdfOcrRecognitionModel[] supplied = models.ToArray();
        if (supplied.Length is < 1 or > 16 || supplied.Any(model => model is null))
            throw new ArgumentException(
                "One through sixteen OCR recognition models are required.", nameof(models));
        PdfOcrRecognitionModel first = supplied[0];
        if (supplied.Any(model => model.Width != first.Width
            || model.Height != first.Height))
            throw new ArgumentException(
                "OCR recognition model dimensions do not match.", nameof(models));
        int labelCount = checked(supplied.Sum(model => model._labels.Length));
        int featureCount = checked(first.Width * first.Height);
        long labelBytes = supplied.Sum(model => model._labels.Sum(
            label => 1L + Encoding.UTF8.GetByteCount(label)));
        long valueCount = supplied.Sum(model => (long)model._weights.Length
            + model._biases.Length + model._priors.Length);
        if (labelCount > 65_536
            || !FitsSerializedSize(labelBytes, labelCount,
                valueCount - labelCount * 2L))
            throw new ArgumentException(
                "The combined OCR recognition model exceeds the size limit.",
                nameof(models));
        var labels = new string[labelCount];
        var weights = new float[checked(labelCount * featureCount)];
        var biases = new float[labelCount];
        var priors = new float[labelCount];
        int labelOffset = 0;
        foreach (PdfOcrRecognitionModel model in supplied)
        {
            model._labels.CopyTo(labels, labelOffset);
            model._biases.CopyTo(biases, labelOffset);
            model._priors.CopyTo(priors, labelOffset);
            model._weights.CopyTo(weights, labelOffset * featureCount);
            labelOffset += model._labels.Length;
        }
        return CreatePrototype(first.Width, first.Height, labels, weights, biases, priors);
    }

    /// <summary>Merges successive training models without retaining duplicate prototypes.</summary>
    public static PdfOcrRecognitionModel MergeTrainingModels(
        IEnumerable<PdfOcrRecognitionModel> models, int maximumModelValues = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (maximumModelValues <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumModelValues));
        PdfOcrRecognitionModel[] supplied = models.ToArray();
        if (supplied.Length is < 1 or > 16 || supplied.Any(model => model is null))
            throw new ArgumentException(
                "One through sixteen OCR recognition models are required.", nameof(models));
        PdfOcrRecognitionModel first = supplied[0];
        if (supplied.Any(model => model.Width != first.Width
            || model.Height != first.Height))
            throw new ArgumentException(
                "OCR recognition model dimensions do not match.", nameof(models));

        int featureCount = checked(first.Width * first.Height);
        var entries = new List<(PdfOcrRecognitionModel Model, int Index)>();
        var entriesByLabel = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (PdfOcrRecognitionModel model in supplied)
            for (int index = 0; index < model._labels.Length; index++)
            {
                ReadOnlySpan<float> candidateWeights = model._weights.AsSpan(
                    index * featureCount, featureCount);
                int existing = -1;
                string label = model._labels[index];
                if (entriesByLabel.TryGetValue(label, out List<int>? sameLabelEntries))
                    foreach (int entryIndex in sameLabelEntries)
                    {
                        (PdfOcrRecognitionModel Model, int Index) entry = entries[entryIndex];
                        if (!SameCanonicalWeights(entry.Model._weights.AsSpan(
                            entry.Index * featureCount, featureCount), candidateWeights))
                            continue;
                        existing = entryIndex;
                        break;
                    }
                if (existing < 0)
                {
                    sameLabelEntries ??= [];
                    entriesByLabel[label] = sameLabelEntries;
                    sameLabelEntries.Add(entries.Count);
                    entries.Add((model, index));
                    continue;
                }
                (PdfOcrRecognitionModel Model, int Index) current = entries[existing];
                if (model._biases[index] > current.Model._biases[current.Index])
                    entries[existing] = (model, index);
            }

        int maximumEntries = Math.Max(1, maximumModelValues / featureCount);
        if (entries.Count > maximumEntries)
        {
            IEnumerable<int[]> grouped = entries.Select((entry, index) =>
                    (Entry: entry, Index: index))
                .GroupBy(item => (item.Entry.Model._labels[item.Entry.Index],
                    item.Entry.Model._shapeBuckets[item.Entry.Index]))
                .OrderBy(group => group.Key.Item1, StringComparer.Ordinal)
                .ThenBy(group => group.Key.Item2)
                .Select(group => group.OrderByDescending(item =>
                        Array.LastIndexOf(supplied, item.Entry.Model))
                    .ThenByDescending(item =>
                        item.Entry.Model._biases[item.Entry.Index])
                    .ThenBy(item => item.Index)
                    .Select(item => item.Index).ToArray());
            int[] selected = [.. SelectManyRoundRobin(grouped, maximumEntries)];
            entries = [.. selected.Select(index => entries[index])];
        }

        int labelCount = entries.Count;
        long labelBytes = entries.Sum(entry =>
            1L + Encoding.UTF8.GetByteCount(entry.Model._labels[entry.Index]));
        if (labelCount > 65_536
            || !FitsSerializedSize(labelBytes, labelCount,
                checked((long)labelCount * featureCount)))
            throw new ArgumentException(
                "The merged OCR recognition model exceeds the size limit.", nameof(models));

        var labels = new string[labelCount];
        var weights = new float[checked(labelCount * featureCount)];
        var biases = new float[labelCount];
        var priors = new float[labelCount];
        for (int target = 0; target < entries.Count; target++)
        {
            (PdfOcrRecognitionModel model, int source) = entries[target];
            labels[target] = model._labels[source];
            model._weights.AsSpan(source * featureCount, featureCount).CopyTo(
                weights.AsSpan(target * featureCount, featureCount));
            biases[target] = model._biases[source];
            priors[target] = model._priors[source];
        }
        return CreatePrototype(first.Width, first.Height, labels, weights, biases, priors);

        static bool SameCanonicalWeights(ReadOnlySpan<float> left,
            ReadOnlySpan<float> right)
        {
            if (left.Length != right.Length) return false;
            for (int index = 0; index < left.Length; index++)
            {
                bool leftCompact = left[index] >= 0 && left[index] <= (float)Half.MaxValue;
                bool rightCompact = right[index] >= 0 && right[index] <= (float)Half.MaxValue;
                if (leftCompact != rightCompact) return false;
                if (leftCompact)
                {
                    if (BitConverter.HalfToInt16Bits((Half)left[index])
                        != BitConverter.HalfToInt16Bits((Half)right[index]))
                        return false;
                }
                else if (BitConverter.SingleToInt32Bits(left[index])
                    != BitConverter.SingleToInt32Bits(right[index]))
                    return false;
            }
            return true;
        }
    }

    private static IEnumerable<int> SelectManyRoundRobin(
        IEnumerable<int[]> groups, int maximumCount)
    {
        int[][] ordered = groups.ToArray();
        for (int offset = 0, count = 0; count < maximumCount; offset++)
        {
            bool found = false;
            foreach (int[] group in ordered)
            {
                if (offset >= group.Length) continue;
                yield return group[offset];
                found = true;
                if (++count == maximumCount) yield break;
            }
            if (!found) yield break;
        }
    }

    internal static bool FitsSerializedSize(long labelBytes, int labelCount,
        long weightCount)
    {
        if (labelBytes < 0 || labelCount < 0 || weightCount < 0)
            return false;
        try
        {
            long valueCount = checked(weightCount + labelCount * 2L);
            long length = checked(Magic.Length + sizeof(int) * 3L
                + labelBytes + valueCount * sizeof(float));
            return length <= MaximumModelBytes;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>Writes the stable model format used by the runtime.</summary>
    public byte[] Save()
    {
        int labelBytes = _labels.Sum(label => 1 + Encoding.UTF8.GetByteCount(label));
        bool compactWeights = _usesPrototypeShapes
            && !_weights.AsSpan().ContainsAnyExceptInRange(0, (float)Half.MaxValue);
        int weightBytes = compactWeights ? sizeof(ushort) : sizeof(float);
        int length = checked(Magic.Length + sizeof(int) * 3 + labelBytes + 1
            + checked((_priors.Length + _biases.Length) * sizeof(float))
            + checked(_weights.Length * weightBytes));
        var output = new byte[length];
        Span<byte> destination = output;
        Magic.CopyTo(destination);
        int position = Magic.Length;
        WriteInt32(destination, ref position, Width);
        WriteInt32(destination, ref position, Height);
        WriteInt32(destination, ref position, _labels.Length);
        foreach (string label in _labels)
        {
            int bytes = Encoding.UTF8.GetByteCount(label);
            destination[position++] = checked((byte)bytes);
            position += Encoding.UTF8.GetBytes(label, destination[position..]);
        }
        destination[position++] = compactWeights ? (byte)1 : (byte)0;
        foreach (float value in _priors)
            WriteSingle(destination, ref position, value);
        foreach (float value in _biases)
            WriteSingle(destination, ref position, value);
        foreach (float value in _weights)
            if (compactWeights)
                WriteHalf(destination, ref position, (Half)value);
            else
                WriteSingle(destination, ref position, value);
        return output;
    }

    /// <summary>Loads a model and optionally verifies its expected SHA-256 digest.</summary>
    public static PdfOcrRecognitionModel Load(ReadOnlyMemory<byte> source,
        string? expectedSha256 = null)
    {
        if (source.Length is <= 0 or > MaximumModelBytes)
            throw new ArgumentException("The OCR model size is invalid.", nameof(source));
        if (expectedSha256 is not null)
        {
            string actual = Convert.ToHexString(SHA256.HashData(source.Span));
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("The OCR model SHA-256 digest does not match.");
        }
        ReadOnlySpan<byte> bytes = source.Span;
        int position = 0;
        bool legacy = bytes.Length >= LegacyMagic.Length
            && bytes[..LegacyMagic.Length].SequenceEqual(LegacyMagic);
        bool previous = bytes.Length >= PreviousMagic.Length
            && bytes[..PreviousMagic.Length].SequenceEqual(PreviousMagic);
        if (bytes.Length < Magic.Length
            || !legacy && !previous && !bytes[..Magic.Length].SequenceEqual(Magic))
            throw new FormatException("The OCR model header is invalid.");
        position += Magic.Length;
        int width = ReadInt32(bytes, ref position);
        int height = ReadInt32(bytes, ref position);
        int count = ReadInt32(bytes, ref position);
        if (width is <= 0 or > 128 || height is <= 0 or > 128 || count is <= 0 or > 65_536)
            throw new FormatException("The OCR model dimensions are invalid.");
        int features;
        try
        {
            features = checked(width * height);
        }
        catch (OverflowException exception)
        {
            throw new FormatException("The OCR model dimensions are invalid.", exception);
        }
        var labels = new string[count];
        var utf8 = new UTF8Encoding(false, true);
        for (int index = 0; index < count; index++)
        {
            if (position >= bytes.Length)
                throw new FormatException("An OCR model label is invalid.");
            int length = bytes[position++];
            if (length == 0 || bytes.Length - position < length)
                throw new FormatException("An OCR model label is invalid.");
            labels[index] = utf8.GetString(bytes.Slice(position, length));
            position += length;
        }
        bool compactWeights = false;
        if (!legacy && !previous)
        {
            if (position >= bytes.Length || bytes[position] > 1)
                throw new FormatException("The OCR model weight encoding is invalid.");
            compactWeights = bytes[position++] == 1;
        }
        long remaining = bytes.Length - position;
        long required = checked((long)count * (legacy ? 1 : 2) * sizeof(float)
            + (long)count * features
            * (compactWeights ? sizeof(ushort) : sizeof(float)));
        if (remaining != required) throw new FormatException("The OCR model payload length is invalid.");
        float[] priors = legacy ? new float[count] : ReadFloats(bytes, ref position, count);
        float[] biases = ReadFloats(bytes, ref position, count);
        int weightCount = checked(count * features);
        float[] weights = compactWeights
            ? ReadHalfs(bytes, ref position, weightCount)
            : ReadFloats(bytes, ref position, weightCount);
        try { return CreatePrototype(width, height, labels, weights, biases, priors); }
        catch (ArgumentException exception) { throw new FormatException("The OCR model payload is invalid.", exception); }
    }

    private static int ReadInt32(ReadOnlySpan<byte> source, ref int position)
    {
        if (source.Length - position < sizeof(int))
            throw new FormatException("The OCR model header is truncated.");
        int value = BinaryPrimitives.ReadInt32LittleEndian(source[position..]);
        position += sizeof(int);
        return value;
    }

    private static void WriteInt32(Span<byte> destination, ref int position, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination[position..], value);
        position += sizeof(int);
    }

    private static void WriteSingle(Span<byte> destination, ref int position, float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(destination[position..], value);
        position += sizeof(float);
    }

    private static void WriteHalf(Span<byte> destination, ref int position, Half value)
    {
        BinaryPrimitives.WriteHalfLittleEndian(destination[position..], value);
        position += sizeof(ushort);
    }

    private static float[] ReadFloats(
        ReadOnlySpan<byte> source, ref int position, int length)
    {
        var values = new float[length];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = BinaryPrimitives.ReadSingleLittleEndian(source[position..]);
            position += sizeof(float);
        }
        return values;
    }

    private static float[] ReadHalfs(
        ReadOnlySpan<byte> source, ref int position, int length)
    {
        var values = new float[length];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = (float)BinaryPrimitives.ReadHalfLittleEndian(source[position..]);
            position += sizeof(ushort);
        }
        return values;
    }

    internal int LabelCount => _labels.Length;

    internal static int ShapeBucket(ReadOnlySpan<float> features, int width, int height)
    {
        float maximum = 0;
        foreach (float value in features)
            maximum = Math.Max(maximum, value);
        if (maximum <= 0) return -1;

        float threshold = maximum * 0.125f;
        int left = width, top = height, right = 0, bottom = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (features[y * width + x] <= threshold) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
            }
        if (right <= left || bottom <= top) return -1;
        double aspect = (right - left) / (double)(bottom - top);
        int aspectBucket = aspect < 0.35 ? 0
            : aspect < 0.65 ? 1
            : aspect < 1 ? 2
            : aspect < 1.5 ? 3 : 4;
        int componentBucket = Math.Min(CountInkComponents(
            features, width, height, threshold), 3) - 1;
        int holeBucket = Math.Min(CountInkHoles(
            features, width, height, threshold), 2);
        return (aspectBucket * 3 + componentBucket) * 3 + holeBucket;
    }

    private static int CountInkComponents(
        ReadOnlySpan<float> features, int width, int height, float threshold)
    {
        int featureCount = checked(width * height);
        Span<byte> visited = featureCount <= 4096
            ? stackalloc byte[featureCount] : new byte[featureCount];
        Span<int> pending = featureCount <= 4096
            ? stackalloc int[featureCount] : new int[featureCount];
        int components = 0;
        for (int origin = 0; origin < featureCount; origin++)
        {
            if (visited[origin] != 0 || features[origin] <= threshold) continue;
            if (++components >= 3) return components;
            int read = 0, write = 0;
            pending[write++] = origin;
            visited[origin] = 1;
            while (read < write)
            {
                int current = pending[read++];
                int x = current % width;
                int y = current / width;
                int left = Math.Max(0, x - 1);
                int right = Math.Min(width - 1, x + 1);
                int top = Math.Max(0, y - 1);
                int bottom = Math.Min(height - 1, y + 1);
                for (int neighborY = top; neighborY <= bottom; neighborY++)
                    for (int neighborX = left; neighborX <= right; neighborX++)
                    {
                        int neighbor = neighborY * width + neighborX;
                        if (visited[neighbor] != 0 || features[neighbor] <= threshold)
                            continue;
                        visited[neighbor] = 1;
                        pending[write++] = neighbor;
                    }
            }
        }
        return components;
    }

    private static int CountInkHoles(
        ReadOnlySpan<float> features, int width, int height, float threshold)
    {
        int featureCount = checked(width * height);
        Span<byte> visited = featureCount <= 4096
            ? stackalloc byte[featureCount] : new byte[featureCount];
        Span<int> pending = featureCount <= 4096
            ? stackalloc int[featureCount] : new int[featureCount];
        int holes = 0;
        for (int origin = 0; origin < featureCount; origin++)
        {
            if (visited[origin] != 0 || features[origin] > threshold) continue;
            int read = 0, write = 0;
            bool touchesEdge = false;
            pending[write++] = origin;
            visited[origin] = 1;
            while (read < write)
            {
                int current = pending[read++];
                int x = current % width;
                int y = current / width;
                if (x == 0 || x == width - 1 || y == 0 || y == height - 1)
                    touchesEdge = true;
                for (int direction = 0; direction < 4; direction++)
                {
                    int neighborX = direction switch
                    {
                        0 => x - 1,
                        1 => x + 1,
                        _ => x
                    };
                    int neighborY = direction switch
                    {
                        2 => y - 1,
                        3 => y + 1,
                        _ => y
                    };
                    if (neighborX < 0 || neighborX >= width
                        || neighborY < 0 || neighborY >= height)
                        continue;
                    int neighbor = neighborY * width + neighborX;
                    if (visited[neighbor] != 0 || features[neighbor] > threshold)
                        continue;
                    visited[neighbor] = 1;
                    pending[write++] = neighbor;
                }
            }
            if (!touchesEdge && ++holes >= 2) return holes;
        }
        return holes;
    }

    internal (string Label, double Confidence) Classify(
        ReadOnlySpan<float> features, Span<double> scores,
        IReadOnlySet<string>? allowedLabels = null)
    {
        int featureCount = Width * Height;
        if (features.Length != featureCount) throw new ArgumentException("Glyph feature size mismatch.");
        if (scores.Length < _labels.Length) throw new ArgumentException("OCR score workspace is too small.");
        scores = scores[.._labels.Length];
        int shape = ShapeBucket(features, Width, Height);
        Span<float> rowProjection = stackalloc float[Height];
        Span<float> columnProjection = stackalloc float[Width];
        BuildProjections(features, Width, Height, rowProjection, columnProjection);
        Span<float> coarseGradientDescriptor =
            stackalloc float[CoarseGradientDescriptorLength];
        Span<float> fineGradientDescriptor =
            stackalloc float[FineGradientDescriptorLength];
        if (_usesPrototypeShapes && featureCount >= 64)
        {
            BuildGradientDescriptor(
                features, Width, Height, CoarseGradientCellCount,
                coarseGradientDescriptor);
            BuildGradientDescriptor(
                features, Width, Height, FineGradientCellCount,
                fineGradientDescriptor);
        }
        int best = -1;
        for (int label = 0; label < _labels.Length; label++)
        {
            if (allowedLabels is not null && !allowedLabels.Contains(_labels[label]))
            {
                scores[label] = double.NegativeInfinity;
                continue;
            }
            bool shapeMismatch = _usesPrototypeShapes && shape >= 0
                && _shapeBuckets[label] >= 0 && _shapeBuckets[label] != shape;
            if (shapeMismatch
                && (_labelShapeMasks[_labels[label]] & 1UL << shape) != 0)
            {
                scores[label] = double.NegativeInfinity;
                continue;
            }
            double score = _biases[label];
            int offset = label * featureCount;
            int feature = 0;
            for (; feature <= featureCount - Vector<float>.Count;
                feature += Vector<float>.Count)
            {
                var weights = new Vector<float>(
                    _weights.AsSpan(offset + feature, Vector<float>.Count));
                var inputs = new Vector<float>(
                    features.Slice(feature, Vector<float>.Count));
                score += Vector.Dot(weights, inputs);
            }
            for (; feature < featureCount; feature++)
                score += _weights[offset + feature] * features[feature];
            if (shapeMismatch)
                score -= ShapeMismatchPenalty;
            if (_usesPrototypeShapes && featureCount >= 64)
            {
                score -= 0.25 * ProjectionDistance(
                    label, rowProjection, columnProjection);
                score -= CoarseGradientDistanceWeight * GradientDistance(
                    label, coarseGradientDescriptor, _coarseGradientDescriptors);
                score -= FineGradientDistanceWeight * GradientDistance(
                    label, fineGradientDescriptor, _fineGradientDescriptors);
            }
            scores[label] = score;
            if (best < 0 || score > scores[best]) best = label;
        }
        if (best < 0)
            throw new ArgumentException(
                "The OCR character whitelist has no labels in this model.", nameof(allowedLabels));
        Span<double> labelVotes = Labels.Count <= 256
            ? stackalloc double[Labels.Count] : new double[Labels.Count];
        int visualIndex = 0;
        double visualVote = double.NegativeInfinity;
        for (int labelIndex = 0; labelIndex < Labels.Count; labelIndex++)
        {
            double vote = PrototypeVote(Labels[labelIndex], scores);
            labelVotes[labelIndex] = vote;
            if (vote > visualVote)
            {
                visualIndex = labelIndex;
                visualVote = vote;
            }
        }
        float maximumPrior = float.NegativeInfinity;
        for (int labelIndex = 0; labelIndex < Labels.Count; labelIndex++)
        {
            if (visualVote - labelVotes[labelIndex] <= PriorTieWindow)
                maximumPrior = Math.Max(
                    maximumPrior, _labelPriors[Labels[labelIndex]]);
        }
        int bestLabelIndex = visualIndex;
        double bestAdjustedVote = double.NegativeInfinity;
        for (int labelIndex = 0; labelIndex < Labels.Count; labelIndex++)
        {
            double vote = labelVotes[labelIndex];
            if (visualVote - vote > PriorTieWindow) continue;
            double adjusted = vote + _labelPriors[Labels[labelIndex]] - maximumPrior;
            if (adjusted > bestAdjustedVote)
            {
                bestLabelIndex = labelIndex;
                bestAdjustedVote = adjusted;
            }
        }
        string bestLabel = Labels[bestLabelIndex];
        double bestVote = labelVotes[bestLabelIndex];
        double runnerUpVote = double.NegativeInfinity;
        for (int labelIndex = 0; labelIndex < Labels.Count; labelIndex++)
            if (labelIndex != bestLabelIndex)
                runnerUpVote = Math.Max(runnerUpVote, labelVotes[labelIndex]);
        if (double.IsNegativeInfinity(runnerUpVote)) return (bestLabel, 1);
        return (bestLabel, 1 / (1 + Math.Exp(runnerUpVote - bestVote)));
    }

    internal IReadOnlyList<PdfOcrLanguageCandidate> RankCandidates(
        ReadOnlySpan<double> prototypeScores, int maximumCandidates,
        IReadOnlySet<string>? allowedLabels = null)
    {
        if (prototypeScores.Length < _labels.Length)
            throw new ArgumentException("OCR prototype scores are incomplete.",
                nameof(prototypeScores));
        if (maximumCandidates <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        int capacity = Math.Min(maximumCandidates, allowedLabels is null
            ? Labels.Count : Labels.Count(allowedLabels.Contains));
        var candidates = new PdfOcrLanguageCandidate[capacity];
        int count = 0;
        foreach (string label in Labels)
        {
            if (allowedLabels is not null && !allowedLabels.Contains(label)) continue;
            var candidate = new PdfOcrLanguageCandidate(
                label, PrototypeVote(label, prototypeScores));
            if (!double.IsFinite(candidate.Score)) continue;
            int insertion = 0;
            while (insertion < count && ComesBefore(candidates[insertion], candidate))
                insertion++;
            if (insertion >= capacity) continue;
            int move = Math.Min(count, capacity - 1) - insertion;
            if (move > 0)
                Array.Copy(candidates, insertion, candidates,
                    insertion + 1, move);
            candidates[insertion] = candidate;
            if (count < capacity) count++;
        }
        if (count < capacity) Array.Resize(ref candidates, count);
        return Array.AsReadOnly(candidates);

        static bool ComesBefore(PdfOcrLanguageCandidate left,
            PdfOcrLanguageCandidate right) => left.Score > right.Score
            || left.Score == right.Score
            && string.CompareOrdinal(left.Label, right.Label) <= 0;
    }

    private double PrototypeVote(string label, ReadOnlySpan<double> prototypeScores)
    {
        double best = double.NegativeInfinity;
        double second = double.NegativeInfinity;
        foreach (int index in _prototypeIndexesByLabel[label])
        {
            double score = prototypeScores[index];
            if (score > best)
            {
                second = best;
                best = score;
            }
            else if (score > second)
            {
                second = score;
            }
        }
        return double.IsNegativeInfinity(second) ? best : best * 0.99 + second * 0.01;
    }

    private static double GradientDistance(int label,
        ReadOnlySpan<float> descriptor, ReadOnlySpan<float> prototypes)
    {
        double distance = 0;
        int offset = label * descriptor.Length;
        for (int index = 0; index < descriptor.Length; index++)
            distance += Math.Abs(descriptor[index] - prototypes[offset + index]);
        return distance;
    }

    private static void BuildGradientDescriptor(ReadOnlySpan<float> features,
        int width, int height, int cellCount, Span<float> descriptor)
    {
        descriptor.Clear();
        float total = 0;
        for (int y = 1; y < height - 1; y++)
            for (int x = 1; x < width - 1; x++)
            {
                float horizontal = features[y * width + x + 1]
                    - features[y * width + x - 1];
                float vertical = features[(y + 1) * width + x]
                    - features[(y - 1) * width + x];
                float horizontalMagnitude = Math.Abs(horizontal);
                float verticalMagnitude = Math.Abs(vertical);
                float magnitude = horizontalMagnitude + verticalMagnitude;
                if (magnitude <= 0) continue;
                int bin = horizontalMagnitude > verticalMagnitude * 2 ? 0
                    : verticalMagnitude > horizontalMagnitude * 2 ? 1
                    : horizontal * vertical >= 0 ? 2 : 3;
                int cellX = Math.Min(cellCount - 1, x * cellCount / width);
                int cellY = Math.Min(cellCount - 1, y * cellCount / height);
                descriptor[(cellY * cellCount + cellX)
                    * GradientBinCount + bin] += magnitude;
                total += magnitude;
            }
        if (total <= 0) return;
        for (int index = 0; index < descriptor.Length; index++)
            descriptor[index] /= total;
    }

    private double ProjectionDistance(int label, ReadOnlySpan<float> rows,
        ReadOnlySpan<float> columns)
    {
        double distance = 0;
        int rowOffset = label * Height;
        for (int index = 0; index < Height; index++)
            distance += Math.Abs(rows[index] - _rowProjections[rowOffset + index]);
        int columnOffset = label * Width;
        for (int index = 0; index < Width; index++)
            distance += Math.Abs(columns[index] - _columnProjections[columnOffset + index]);
        return distance;
    }

    private static void BuildProjections(ReadOnlySpan<float> features,
        int width, int height, Span<float> rows, Span<float> columns)
    {
        rows.Clear();
        columns.Clear();
        float total = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float value = features[y * width + x];
                rows[y] += value;
                columns[x] += value;
                total += value;
            }
        if (total <= 0) return;
        for (int index = 0; index < rows.Length; index++) rows[index] /= total;
        for (int index = 0; index < columns.Length; index++) columns[index] /= total;
    }

}

/// <summary>A recognized OCR word with pixel bounds and model confidence.</summary>
public sealed record PdfOcrRecognizedWord(string Text, double Confidence, PdfOcrImageRegion Bounds);

/// <summary>A language-specific OCR recognition model selected from a catalog.</summary>
public sealed record PdfOcrRecognitionModelSelection(
    string Language, PdfOcrRecognitionModel Model);

/// <summary>Describes a lazily loaded, integrity-checked OCR language model.</summary>
public sealed record PdfOcrRecognitionModelSource(
    string Language, Func<ReadOnlyMemory<byte>> Read, string? ExpectedSha256 = null);

/// <summary>Maps requested OCR languages to bounded engine-owned recognition models.</summary>
public sealed class PdfOcrRecognitionModelCatalog
{
    private readonly Dictionary<string, Lazy<PdfOcrRecognitionModel>> _models;
    private readonly ConcurrentDictionary<string,
        Lazy<PdfOcrRecognitionModelSelection>> _combined = new(StringComparer.Ordinal);

    /// <summary>Creates a catalog from language and model pairs.</summary>
    public PdfOcrRecognitionModelCatalog(
        IEnumerable<KeyValuePair<string, PdfOcrRecognitionModel>> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        _models = new Dictionary<string, Lazy<PdfOcrRecognitionModel>>(StringComparer.Ordinal);
        foreach ((string language, PdfOcrRecognitionModel model) in models)
        {
            string normalized = Normalize(language);
            PdfOcrRecognitionModel validated = model
                ?? throw new ArgumentException("An OCR language model is null.", nameof(models));
            if (!_models.TryAdd(normalized, new Lazy<PdfOcrRecognitionModel>(
                    () => validated, LazyThreadSafetyMode.ExecutionAndPublication)))
                throw new ArgumentException(
                    $"OCR language model '{normalized}' is registered more than once.", nameof(models));
        }
        Languages = FinishConstruction(_models, nameof(models));
    }

    /// <summary>Creates a catalog that loads and verifies only a selected language model.</summary>
    public static PdfOcrRecognitionModelCatalog Create(
        IEnumerable<PdfOcrRecognitionModelSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var models = new Dictionary<string, Lazy<PdfOcrRecognitionModel>>(StringComparer.Ordinal);
        foreach (PdfOcrRecognitionModelSource source in sources)
        {
            if (source is null)
                throw new ArgumentException("An OCR language model source is null.", nameof(sources));
            string normalized = Normalize(source.Language);
            Func<ReadOnlyMemory<byte>> read = source.Read
                ?? throw new ArgumentException("An OCR language model reader is null.", nameof(sources));
            if (!models.TryAdd(normalized, new Lazy<PdfOcrRecognitionModel>(
                    () => PdfOcrRecognitionModel.Load(read(), source.ExpectedSha256),
                    LazyThreadSafetyMode.ExecutionAndPublication)))
                throw new ArgumentException(
                    $"OCR language model '{normalized}' is registered more than once.", nameof(sources));
        }
        return new PdfOcrRecognitionModelCatalog(models);
    }

    private PdfOcrRecognitionModelCatalog(
        Dictionary<string, Lazy<PdfOcrRecognitionModel>> models)
    {
        _models = models;
        Languages = FinishConstruction(_models, nameof(models));
    }

    /// <summary>Gets normalized language names available in the catalog.</summary>
    public IReadOnlyList<string> Languages { get; }

    /// <summary>Selects the first exact or primary-language model requested.</summary>
    public PdfOcrRecognitionModelSelection Select(IEnumerable<string> languages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        foreach (string language in languages)
        {
            string normalized = Normalize(language);
            if (TrySelect(normalized, out PdfOcrRecognitionModelSelection selection))
                return selection;
        }
        throw new NotSupportedException(
            "No engine OCR recognition model matches the requested languages.");
    }

    /// <summary>Selects and combines every distinct requested language model available.</summary>
    public PdfOcrRecognitionModelSelection SelectCombined(IEnumerable<string> languages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        var selected = new List<PdfOcrRecognitionModelSelection>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string language in languages)
        {
            string normalized = Normalize(language);
            if (TrySelect(normalized, out PdfOcrRecognitionModelSelection selection)
                && seen.Add(selection.Language))
                selected.Add(selection);
        }
        if (selected.Count == 0)
            throw new NotSupportedException(
                "No engine OCR recognition model matches the requested languages.");
        if (selected.Count == 1) return selected[0];
        string key = string.Join('+', selected.Select(item => item.Language));
        return _combined.GetOrAdd(key, _ => new Lazy<PdfOcrRecognitionModelSelection>(
            () => new PdfOcrRecognitionModelSelection(key,
                PdfOcrRecognitionModel.Combine(selected.Select(item => item.Model))),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private bool TrySelect(string normalized,
        out PdfOcrRecognitionModelSelection selection)
    {
        if (_models.TryGetValue(normalized, out Lazy<PdfOcrRecognitionModel>? exact))
        {
            selection = new PdfOcrRecognitionModelSelection(normalized, exact.Value);
            return true;
        }
        int separator = normalized.IndexOf('-');
        if (separator > 0)
        {
            string primary = normalized[..separator];
            if (_models.TryGetValue(primary, out Lazy<PdfOcrRecognitionModel>? fallback))
            {
                selection = new PdfOcrRecognitionModelSelection(primary, fallback.Value);
                return true;
            }
        }
        selection = null!;
        return false;
    }

    private static IReadOnlyList<string> FinishConstruction(
        Dictionary<string, Lazy<PdfOcrRecognitionModel>> models, string parameterName)
    {
        if (models.Count == 0)
            throw new ArgumentException("At least one OCR language model is required.", parameterName);
        return Array.AsReadOnly(models.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    private static string Normalize(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
            throw new ArgumentException("An OCR model language is empty.", nameof(language));
        string normalized = language.Trim().Replace('_', '-').ToLowerInvariant();
        if (normalized.Length > 35 || normalized.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("An OCR model language is invalid.", nameof(language));
        return normalized;
    }
}

/// <summary>Runs the engine-owned glyph model over a detected page layout.</summary>
public static class PdfOcrRecognizer
{
    /// <summary>Runs the complete engine-owned recognition pipeline over raw BGRA pixels.</summary>
    public static PdfOcrResult RecognizeBgra(ReadOnlyMemory<byte> bgra, int width, int height,
        PdfOcrRecognitionModel model, PdfOcrOptions options,
        CancellationToken cancellationToken = default) =>
        RecognizeBgra(bgra, width, height, model, options, null, null, cancellationToken);

    /// <summary>Runs complete recognition with a character language model.</summary>
    public static PdfOcrResult RecognizeBgra(ReadOnlyMemory<byte> bgra, int width, int height,
        PdfOcrRecognitionModel model, PdfOcrLanguageModel languageModel,
        PdfOcrOptions options, CancellationToken cancellationToken = default) =>
        RecognizeBgra(bgra, width, height, model, options,
            languageModel ?? throw new ArgumentNullException(nameof(languageModel)),
            null, cancellationToken);

    /// <summary>Runs engine recognition while restricting results to supplied characters.</summary>
    public static PdfOcrResult RecognizeBgra(ReadOnlyMemory<byte> bgra, int width, int height,
        PdfOcrRecognitionModel model, PdfOcrOptions options, string characterWhitelist,
        CancellationToken cancellationToken = default)
    {
        return RecognizeBgra(
            bgra, width, height, model, options, null,
            CreateAllowedLabels(model, characterWhitelist), cancellationToken);
    }

    /// <summary>Runs contextual recognition while restricting results to supplied characters.</summary>
    public static PdfOcrResult RecognizeBgra(ReadOnlyMemory<byte> bgra, int width, int height,
        PdfOcrRecognitionModel model, PdfOcrLanguageModel languageModel,
        PdfOcrOptions options, string characterWhitelist,
        CancellationToken cancellationToken = default) =>
        RecognizeBgra(bgra, width, height, model, options,
            languageModel ?? throw new ArgumentNullException(nameof(languageModel)),
            CreateAllowedLabels(model, characterWhitelist), cancellationToken);

    private static IReadOnlySet<string> CreateAllowedLabels(
        PdfOcrRecognitionModel model, string characterWhitelist)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterWhitelist);
        var allowedLabels = new HashSet<string>(characterWhitelist.EnumerateRunes()
            .Where(rune => !Rune.IsControl(rune) && !Rune.IsWhiteSpace(rune))
            .Select(rune => rune.ToString()), StringComparer.Ordinal);
        if (allowedLabels.Count == 0 || !model.Labels.Any(allowedLabels.Contains))
            throw new ArgumentException(
                "The OCR character whitelist has no labels in this model.",
                nameof(characterWhitelist));
        return allowedLabels;
    }

    private static PdfOcrResult RecognizeBgra(ReadOnlyMemory<byte> bgra, int width, int height,
        PdfOcrRecognitionModel model, PdfOcrOptions options,
        PdfOcrLanguageModel? languageModel, IReadOnlySet<string>? allowedLabels,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        PdfOcrOptions pipelineOptions = options.CorrectOrientation
            ? new PdfOcrOptions(options.Languages, options.OutputMode,
                options.Deskew, false, options.RemoveBackground,
                options.RemoveNoise, options.DetectPageSegments)
            : options;
        RawOrientationCandidate candidate = RecognizeOrientation(0);
        if (options.CorrectOrientation)
            foreach (int rotation in (ReadOnlySpan<int>)[90, 180, 270])
            {
                cancellationToken.ThrowIfCancellationRequested();
                RawOrientationCandidate alternative = RecognizeOrientation(rotation);
                if (alternative.Score > candidate.Score) candidate = alternative;
            }
        var words = new PdfOcrPixelWord[candidate.Words.Count];
        for (int index = 0; index < candidate.Words.Count; index++)
        {
            PdfOcrRecognizedWord word = candidate.Words[index];
            PdfOcrImageRegion restored = PdfOcrImagePreprocessor.RestoreDeskewedBounds(
                word.Bounds, candidate.Image.DeskewDegrees,
                candidate.Image.Width, candidate.Image.Height);
            PdfOcrImageRegion bounds = UnrotateImageBounds(
                restored, candidate.Rotation, width, height);
            words[index] = new PdfOcrPixelWord(word.Text, (float)word.Confidence,
                bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        }
        var lines = new List<string>(candidate.Layout.Lines.Count);
        int wordIndex = 0;
        foreach (PdfOcrTextLine line in candidate.Layout.Lines)
        {
            int count = line.Words.Count;
            lines.Add(string.Join(' ', words.AsSpan(wordIndex, count).ToArray()
                .Select(word => word.Text)));
            wordIndex += count;
        }
        float confidence = CalculateMeanConfidence(candidate.Words);
        return new PdfOcrResult(string.Join(Environment.NewLine, lines), confidence, words);

        RawOrientationCandidate RecognizeOrientation(int rotation)
        {
            PdfOcrPreparedImage prepared = rotation == 0
                ? PdfOcrImagePreprocessor.PrepareBgra(
                    bgra, width, height, pipelineOptions, cancellationToken)
                : PdfOcrImagePreprocessor.PrepareBgraRotated(
                    bgra, width, height, rotation, pipelineOptions, cancellationToken);
            PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(
                prepared, pipelineOptions.DetectPageSegments, cancellationToken);
            IReadOnlyList<PdfOcrRecognizedWord> recognized = Recognize(
                prepared, layout, model, languageModel, allowedLabels, cancellationToken);
            double score = recognized.Count == 0
                ? -1 : CalculateMeanConfidence(recognized);
            return new RawOrientationCandidate(
                rotation, prepared, layout, recognized, score);
        }
    }

    internal static float CalculateMeanConfidence(
        IReadOnlyList<PdfOcrRecognizedWord> words)
    {
        int characters = words.Sum(word => word.Text.EnumerateRunes().Count());
        return characters == 0 ? 0 : (float)(words.Sum(
            word => word.Confidence * word.Text.EnumerateRunes().Count()) / characters);
    }

    internal static PdfOcrImageRegion UnrotateImageBounds(
        PdfOcrImageRegion bounds, int rotation, int width, int height)
    {
        (int X, int Y)[] points =
        [
            Unrotate(bounds.Left, bounds.Top), Unrotate(bounds.Right, bounds.Top),
            Unrotate(bounds.Left, bounds.Bottom), Unrotate(bounds.Right, bounds.Bottom)
        ];
        return new PdfOcrImageRegion(points.Min(point => point.X), points.Min(point => point.Y),
            points.Max(point => point.X), points.Max(point => point.Y));

        (int X, int Y) Unrotate(int x, int y) => rotation switch
        {
            90 => (y, height - x),
            180 => (width - x, height - y),
            270 => (width - y, x),
            _ => (x, y)
        };
    }

    /// <summary>Recognizes each component and assembles the labels into words.</summary>
    public static IReadOnlyList<PdfOcrRecognizedWord> Recognize(PdfOcrPreparedImage image,
        PdfOcrPageLayout layout, PdfOcrRecognitionModel model,
        CancellationToken cancellationToken = default) =>
        Recognize(image, layout, model, null, null, cancellationToken);

    /// <summary>Recognizes words and resolves ambiguous glyphs with language context.</summary>
    public static IReadOnlyList<PdfOcrRecognizedWord> Recognize(PdfOcrPreparedImage image,
        PdfOcrPageLayout layout, PdfOcrRecognitionModel model,
        PdfOcrLanguageModel languageModel,
        CancellationToken cancellationToken = default) =>
        Recognize(image, layout, model, languageModel, null, cancellationToken);

    private static IReadOnlyList<PdfOcrRecognizedWord> Recognize(
        PdfOcrPreparedImage image, PdfOcrPageLayout layout, PdfOcrRecognitionModel model,
        PdfOcrLanguageModel? languageModel, IReadOnlySet<string>? allowedLabels,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        var words = new List<PdfOcrRecognizedWord>(layout.Words.Count);
        double[] scores = ArrayPool<double>.Shared.Rent(model.LabelCount);
        int featureCount = checked(model.Width * model.Height);
        float[] features = ArrayPool<float>.Shared.Rent(featureCount);
        try
        {
            foreach (PdfOcrTextLine line in layout.Lines)
            {
                PdfOcrImageRegion lineBounds = NormalizationLineBounds(line);
                foreach (PdfOcrWordRegion word in line.Words)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var text = new StringBuilder();
                    List<IReadOnlyList<PdfOcrLanguageCandidate>>? candidates = languageModel is null
                        ? null : new List<IReadOnlyList<PdfOcrLanguageCandidate>>(
                            word.Components.Count);
                    double confidence = 0;
                    foreach (PdfOcrImageRegion component in word.Components)
                    {
                        Span<float> glyph = features.AsSpan(0, featureCount);
                        NormalizeGlyph(image, component, lineBounds,
                            model.Width, model.Height, glyph, cancellationToken);
                        (string label, double score) = model.Classify(
                            glyph, scores.AsSpan(0, model.LabelCount), allowedLabels);
                        text.Append(label);
                        candidates?.Add(model.RankCandidates(
                            scores.AsSpan(0, model.LabelCount), maximumCandidates: 16,
                            allowedLabels));
                        confidence += score;
                    }
                    string recognizedText;
                    if (candidates is null)
                    {
                        recognizedText = text.ToString();
                    }
                    else
                    {
                        PdfOcrLanguageDecode decoded = languageModel!.DecodeWithConfidence(
                            candidates, cancellationToken: cancellationToken);
                        recognizedText = string.Concat(decoded.Labels);
                        confidence = decoded.Confidence * word.Components.Count;
                    }
                    words.Add(new PdfOcrRecognizedWord(recognizedText,
                        word.Components.Count == 0
                            ? 0 : confidence / word.Components.Count, word.Bounds));
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(features);
            ArrayPool<double>.Shared.Return(scores);
        }
        return Array.AsReadOnly(words.ToArray());

    }

    /// <summary>Normalizes one glyph into a centered, aspect-preserving model feature grid.</summary>
    public static float[] NormalizeGlyph(PdfOcrPreparedImage image, PdfOcrImageRegion region,
        int width, int height, CancellationToken cancellationToken = default)
    {
        ValidateNormalizationArguments(image, region, region, width, height);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new float[checked(width * height)];
        NormalizeGlyph(image, region, width, height, result, cancellationToken);
        return result;
    }

    /// <summary>Normalizes one glyph while preserving its size and baseline within a text line.</summary>
    public static float[] NormalizeGlyph(PdfOcrPreparedImage image,
        PdfOcrImageRegion region, PdfOcrImageRegion lineBounds,
        int width, int height, CancellationToken cancellationToken = default)
    {
        ValidateNormalizationArguments(image, region, lineBounds, width, height);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new float[checked(width * height)];
        NormalizeGlyph(image, region, lineBounds, width, height,
            result, cancellationToken);
        return result;
    }

    private static void ValidateNormalizationArguments(PdfOcrPreparedImage image,
        PdfOcrImageRegion region, PdfOcrImageRegion lineBounds, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(lineBounds);
        if (width is <= 0 or > 128) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is <= 0 or > 128) throw new ArgumentOutOfRangeException(nameof(height));
        if (region.Left < 0 || region.Top < 0 || region.Right > image.Width
            || region.Bottom > image.Height || region.Width <= 0 || region.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(region));
        if (lineBounds.Left < 0 || lineBounds.Top < 0 || lineBounds.Right > image.Width
            || lineBounds.Bottom > image.Height || lineBounds.Width <= 0
            || lineBounds.Height <= 0 || lineBounds.Left > region.Left
            || lineBounds.Top > region.Top || lineBounds.Right < region.Right
            || lineBounds.Bottom < region.Bottom)
            throw new ArgumentOutOfRangeException(nameof(lineBounds));
    }

    internal static PdfOcrImageRegion NormalizationLineBounds(PdfOcrTextLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Components.Count == 0) return line.Bounds;
        int count = line.Components.Count;
        int valueCount = checked(count * 2);
        int[]? rented = null;
        Span<int> values = valueCount <= 256
            ? stackalloc int[valueCount]
            : (rented = ArrayPool<int>.Shared.Rent(valueCount));
        try
        {
            Span<int> heights = values[..count];
            Span<int> bottoms = values.Slice(count, count);
            for (int index = 0; index < count; index++)
            {
                heights[index] = line.Components[index].Height;
                bottoms[index] = line.Components[index].Bottom;
            }
            heights.Sort();
            bottoms.Sort();
            int height = heights[count / 2];
            if ((long)line.Bounds.Height <= (long)height * 2) return line.Bounds;
            int bottom = bottoms[count / 2];
            return new PdfOcrImageRegion(
                line.Bounds.Left, bottom - height, line.Bounds.Right, bottom);
        }
        finally
        {
            if (rented is not null) ArrayPool<int>.Shared.Return(rented);
        }
    }

    internal static PdfOcrImageRegion NormalizationLineBounds(PdfOcrTextLine line,
        PdfOcrImageRegion region)
    {
        PdfOcrImageRegion bounds = NormalizationLineBounds(line);
        return new PdfOcrImageRegion(
            Math.Min(bounds.Left, region.Left), Math.Min(bounds.Top, region.Top),
            Math.Max(bounds.Right, region.Right), Math.Max(bounds.Bottom, region.Bottom));
    }

    private static void NormalizeGlyph(PdfOcrPreparedImage image, PdfOcrImageRegion region,
        int width, int height, Span<float> result, CancellationToken cancellationToken)
        => NormalizeGlyph(image, region, region, width, height, result, cancellationToken);

    private static void NormalizeGlyph(PdfOcrPreparedImage image, PdfOcrImageRegion region,
        PdfOcrImageRegion lineBounds, int width, int height,
        Span<float> result, CancellationToken cancellationToken)
    {
        result.Clear();
        ReadOnlySpan<byte> source = image.Pixels.Span;
        double scale = Math.Min(width / (double)region.Width,
            height / (double)lineBounds.Height);
        int scaledWidth = Math.Clamp((int)Math.Round(region.Width * scale), 1, width);
        int scaledHeight = Math.Clamp((int)Math.Round(region.Height * scale), 1, height);
        int offsetX = (width - scaledWidth) / 2;
        int scaledLineHeight = Math.Clamp(
            (int)Math.Round(lineBounds.Height * scale), 1, height);
        int offsetY = (height - scaledLineHeight) / 2 + (int)Math.Round(
            (region.Top - lineBounds.Top) * scale);
        offsetY = Math.Clamp(offsetY, 0, height - scaledHeight);
        for (int y = 0; y < scaledHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < scaledWidth; x++)
            {
                double sourceLeft = x * region.Width / (double)scaledWidth;
                double sourceRight = (x + 1) * region.Width / (double)scaledWidth;
                double sourceTop = y * region.Height / (double)scaledHeight;
                double sourceBottom = (y + 1) * region.Height / (double)scaledHeight;
                int firstX = (int)Math.Floor(sourceLeft);
                int lastX = Math.Min(region.Width - 1, (int)Math.Ceiling(sourceRight) - 1);
                int firstY = (int)Math.Floor(sourceTop);
                int lastY = Math.Min(region.Height - 1, (int)Math.Ceiling(sourceBottom) - 1);
                double darkness = 0;
                double area = 0;
                for (int sy = firstY; sy <= lastY; sy++)
                {
                    double vertical = Math.Min(sourceBottom, sy + 1) - Math.Max(sourceTop, sy);
                    for (int sx = firstX; sx <= lastX; sx++)
                    {
                        double horizontal = Math.Min(sourceRight, sx + 1)
                            - Math.Max(sourceLeft, sx);
                        double weight = horizontal * vertical;
                        darkness += (1 - source[(region.Top + sy) * image.Width
                            + region.Left + sx] / 255d) * weight;
                        area += weight;
                    }
                }
                result[(offsetY + y) * width + offsetX + x] =
                    area <= 0 ? 0 : (float)(darkness / area);
            }
        }
    }

    private sealed record RawOrientationCandidate(
        int Rotation, PdfOcrPreparedImage Image, PdfOcrPageLayout Layout,
        IReadOnlyList<PdfOcrRecognizedWord> Words, double Score);
}

/// <summary>An engine-rendered OCR page with reviewable words and pipeline diagnostics.</summary>
public sealed record PdfOcrPageRecognition(
    PdfOcrReview Review, IReadOnlyList<string> Diagnostics, int PixelWidth, int PixelHeight);

/// <summary>Renders, prepares, segments, and recognizes PDF pages entirely inside the engine.</summary>
public sealed class PdfOcrPageRecognizer
{
    private readonly PdfOcrRecognitionModel? _model;
    private readonly PdfOcrRecognitionModelCatalog? _models;
    private readonly PdfOcrLanguageModel? _languageModel;
    private readonly PdfPageRenderer _renderer;
    private readonly IReadOnlyList<PdfPageInformation> _pages;

    /// <summary>Creates an engine-owned page recognition pipeline.</summary>
    public PdfOcrPageRecognizer(PdfDocument document, PdfOcrRecognitionModel model)
    {
        ArgumentNullException.ThrowIfNull(document);
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _renderer = new PdfPageRenderer(document);
        _pages = PdfPageInformation.Read(document);
    }

    /// <summary>Creates an engine-owned pipeline with character language context.</summary>
    public PdfOcrPageRecognizer(PdfDocument document, PdfOcrRecognitionModel model,
        PdfOcrLanguageModel languageModel) : this(document, model)
    {
        _languageModel = languageModel
            ?? throw new ArgumentNullException(nameof(languageModel));
    }

    /// <summary>Creates an engine-owned pipeline with language-specific models.</summary>
    public PdfOcrPageRecognizer(PdfDocument document, PdfOcrRecognitionModelCatalog models)
    {
        ArgumentNullException.ThrowIfNull(document);
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _renderer = new PdfPageRenderer(document);
        _pages = PdfPageInformation.Read(document);
    }

    /// <summary>Creates a language-specific pipeline with character language context.</summary>
    public PdfOcrPageRecognizer(PdfDocument document, PdfOcrRecognitionModelCatalog models,
        PdfOcrLanguageModel languageModel) : this(document, models)
    {
        _languageModel = languageModel
            ?? throw new ArgumentNullException(nameof(languageModel));
    }

    /// <summary>Recognizes one page directly from its engine-rendered BGRA pixels.</summary>
    public PdfOcrPageRecognition Recognize(int pageIndex, PdfRenderOptions renderOptions,
        PdfOcrOptions ocrOptions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(renderOptions);
        ArgumentNullException.ThrowIfNull(ocrOptions);
        if (pageIndex < 0 || pageIndex >= _pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        cancellationToken.ThrowIfCancellationRequested();

        PdfRenderedPage rendered = _renderer.Render(pageIndex, renderOptions, cancellationToken);
        PdfOcrRecognitionModelSelection? selection =
            _models?.SelectCombined(ocrOptions.Languages);
        PdfOcrRecognitionModel model = selection?.Model ?? _model!;
        PdfOcrOptions pipelineOptions = ocrOptions.CorrectOrientation
            ? new PdfOcrOptions(ocrOptions.Languages, ocrOptions.OutputMode,
                ocrOptions.Deskew, false, ocrOptions.RemoveBackground,
                ocrOptions.RemoveNoise, ocrOptions.DetectPageSegments)
            : ocrOptions;
        OrientationCandidate candidate = RecognizeOrientation(0);
        if (ocrOptions.CorrectOrientation)
            foreach (int rotation in (ReadOnlySpan<int>)[90, 180, 270])
            {
                cancellationToken.ThrowIfCancellationRequested();
                OrientationCandidate alternative = RecognizeOrientation(rotation);
                if (alternative.Score > candidate.Score) candidate = alternative;
            }
        PdfPageInformation page = _pages[pageIndex];
        string? language = selection?.Language
            ?? (ocrOptions.Languages.Count == 1 ? ocrOptions.Languages[0] : null);
        var words = new PdfOcrWord[candidate.Words.Count];
        for (int sequence = 0; sequence < candidate.Words.Count; sequence++)
        {
            PdfOcrRecognizedWord word = candidate.Words[sequence];
            int preparedWidth = candidate.Rotation is 90 or 270
                ? rendered.Height : rendered.Width;
            int preparedHeight = candidate.Rotation is 90 or 270
                ? rendered.Width : rendered.Height;
            PdfOcrImageRegion restored = PdfOcrImagePreprocessor.RestoreDeskewedBounds(
                word.Bounds, candidate.DeskewDegrees, preparedWidth, preparedHeight);
            PdfOcrImageRegion bounds = PdfOcrRecognizer.UnrotateImageBounds(
                restored, candidate.Rotation, rendered.Width, rendered.Height);
            PdfContentBounds pdfBounds = MapBounds(bounds, rendered.Width, rendered.Height, page);
            words[sequence] = new PdfOcrWord($"page-{pageIndex}-word-{sequence}",
                pageIndex, sequence, word.Text, word.Text, pdfBounds, word.Confidence, language);
        }
        string[] diagnostics = [.. rendered.Diagnostics.Concat(candidate.Diagnostics)
            .Distinct(StringComparer.Ordinal)];
        return new PdfOcrPageRecognition(new PdfOcrReview(words),
            Array.AsReadOnly(diagnostics), rendered.Width, rendered.Height);

        OrientationCandidate RecognizeOrientation(int rotation)
        {
            PdfOcrPreparedImage prepared = rotation == 0
                ? PdfOcrImagePreprocessor.PrepareBgra(rendered.Pixels,
                    rendered.Width, rendered.Height, pipelineOptions, cancellationToken)
                : PdfOcrImagePreprocessor.PrepareBgraRotated(rendered.Pixels,
                    rendered.Width, rendered.Height, rotation,
                    pipelineOptions, cancellationToken);
            PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(
                prepared, pipelineOptions.DetectPageSegments, cancellationToken);
            IReadOnlyList<PdfOcrRecognizedWord> recognized = _languageModel is null
                ? PdfOcrRecognizer.Recognize(
                    prepared, layout, model, cancellationToken)
                : PdfOcrRecognizer.Recognize(
                    prepared, layout, model, _languageModel, cancellationToken);
            double score = recognized.Count == 0
                ? -1 : PdfOcrRecognizer.CalculateMeanConfidence(recognized);
            return new OrientationCandidate(
                rotation, prepared.DeskewDegrees,
                recognized, prepared.Diagnostics, score);
        }
    }

    private static PdfContentBounds MapBounds(PdfOcrImageRegion bounds,
        int pixelWidth, int pixelHeight, PdfPageInformation page)
    {
        bool quarterTurn = page.Rotation is 90 or 270;
        double displayWidth = quarterTurn ? page.Height : page.Width;
        double displayHeight = quarterTurn ? page.Width : page.Height;
        double left = bounds.Left * displayWidth / pixelWidth;
        double right = bounds.Right * displayWidth / pixelWidth;
        double bottom = (pixelHeight - bounds.Bottom) * displayHeight / pixelHeight;
        double top = (pixelHeight - bounds.Top) * displayHeight / pixelHeight;
        (double X, double Y)[] points =
        [
            Unrotate(left, bottom), Unrotate(right, bottom),
            Unrotate(left, top), Unrotate(right, top)
        ];
        return new PdfContentBounds(points.Min(point => point.X), points.Min(point => point.Y),
            points.Max(point => point.X), points.Max(point => point.Y));

        (double X, double Y) Unrotate(double x, double y) => page.Rotation switch
        {
            90 => (page.Width - y, x),
            180 => (page.Width - x, page.Height - y),
            270 => (y, page.Height - x),
            _ => (x, y)
        };
    }

    private sealed record OrientationCandidate(
        int Rotation, double DeskewDegrees, IReadOnlyList<PdfOcrRecognizedWord> Words,
        IReadOnlyList<string> Diagnostics, double Score);
}
