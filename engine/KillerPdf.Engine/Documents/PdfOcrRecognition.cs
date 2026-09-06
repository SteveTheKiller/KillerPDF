using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Numerics;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Documents;

/// <summary>A bounded, versioned OCR glyph-classification model.</summary>
public sealed class PdfOcrRecognitionModel
{
    private static readonly byte[] Magic = "KPOCR1\0"u8.ToArray();
    private const double ShapeMismatchPenalty = 0.25;
    private readonly string[] _labels;
    private readonly float[] _weights;
    private readonly float[] _biases;
    private readonly sbyte[] _shapeBuckets;
    private readonly Dictionary<string, int> _labelShapeMasks;
    private readonly bool _usesPrototypeShapes;

    private PdfOcrRecognitionModel(int width, int height, string[] labels,
        float[] weights, float[] biases)
    {
        Width = width;
        Height = height;
        _labels = labels;
        _weights = weights;
        _biases = biases;
        _usesPrototypeShapes = !weights.AsSpan().ContainsAnyExceptInRange(0, float.MaxValue);
        int featureCount = checked(width * height);
        _shapeBuckets = new sbyte[labels.Length];
        _labelShapeMasks = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int label = 0; label < labels.Length; label++)
        {
            _shapeBuckets[label] = checked((sbyte)ShapeBucket(
                weights.AsSpan(label * featureCount, featureCount), width, height));
            if (_shapeBuckets[label] >= 0)
                _labelShapeMasks[labels[label]] = _labelShapeMasks.GetValueOrDefault(labels[label])
                    | 1 << _shapeBuckets[label];
        }
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
        if (width is <= 0 or > 128) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is <= 0 or > 128) throw new ArgumentOutOfRangeException(nameof(height));
        ArgumentNullException.ThrowIfNull(labels);
        string[] names = labels.ToArray();
        if (names.Length is <= 0 or > 65_536 || names.Any(label =>
            string.IsNullOrEmpty(label) || Encoding.UTF8.GetByteCount(label) > 64
            || label.EnumerateRunes().Any(rune => rune == Rune.ReplacementChar)))
            throw new ArgumentException("OCR model labels are empty, oversized, or invalid.", nameof(labels));
        int featureCount = checked(width * height);
        if (weights.Length != checked(featureCount * names.Length))
            throw new ArgumentException("OCR model weights do not match its dimensions.", nameof(weights));
        if (biases.Length != names.Length)
            throw new ArgumentException("OCR model biases do not match its labels.", nameof(biases));
        if (weights.Span.ContainsAnyExceptInRange(float.MinValue, float.MaxValue)
            || biases.Span.ContainsAnyExceptInRange(float.MinValue, float.MaxValue))
            throw new ArgumentException("OCR model values must be finite.");
        return new PdfOcrRecognitionModel(width, height, names,
            weights.ToArray(), biases.ToArray());
    }

    /// <summary>Combines compatible language models without changing their prototypes.</summary>
    public static PdfOcrRecognitionModel Combine(
        IEnumerable<PdfOcrRecognitionModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        PdfOcrRecognitionModel[] supplied = models.ToArray();
        if (supplied.Length == 0 || supplied.Any(model => model is null))
            throw new ArgumentException(
                "At least one OCR recognition model is required.", nameof(models));
        PdfOcrRecognitionModel first = supplied[0];
        if (supplied.Any(model => model.Width != first.Width
            || model.Height != first.Height))
            throw new ArgumentException(
                "OCR recognition model dimensions do not match.", nameof(models));
        int labelCount = checked(supplied.Sum(model => model._labels.Length));
        int featureCount = checked(first.Width * first.Height);
        var labels = new string[labelCount];
        var weights = new float[checked(labelCount * featureCount)];
        var biases = new float[labelCount];
        int labelOffset = 0;
        foreach (PdfOcrRecognitionModel model in supplied)
        {
            model._labels.CopyTo(labels, labelOffset);
            model._biases.CopyTo(biases, labelOffset);
            model._weights.CopyTo(weights, labelOffset * featureCount);
            labelOffset += model._labels.Length;
        }
        return Create(first.Width, first.Height, labels, weights, biases);
    }

    /// <summary>Writes the stable model format used by the runtime.</summary>
    public byte[] Save()
    {
        int labelBytes = _labels.Sum(label => 1 + Encoding.UTF8.GetByteCount(label));
        int length = checked(Magic.Length + sizeof(int) * 3 + labelBytes
            + checked((_biases.Length + _weights.Length) * sizeof(float)));
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
        foreach (float value in _biases)
            WriteSingle(destination, ref position, value);
        foreach (float value in _weights)
            WriteSingle(destination, ref position, value);
        return output;
    }

    /// <summary>Loads a model and optionally verifies its expected SHA-256 digest.</summary>
    public static PdfOcrRecognitionModel Load(ReadOnlyMemory<byte> source,
        string? expectedSha256 = null)
    {
        if (source.Length is <= 0 or > 256 * 1024 * 1024)
            throw new ArgumentException("The OCR model size is invalid.", nameof(source));
        if (expectedSha256 is not null)
        {
            string actual = Convert.ToHexString(SHA256.HashData(source.Span));
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("The OCR model SHA-256 digest does not match.");
        }
        ReadOnlySpan<byte> bytes = source.Span;
        int position = 0;
        if (bytes.Length < Magic.Length || !bytes[..Magic.Length].SequenceEqual(Magic))
            throw new FormatException("The OCR model header is invalid.");
        position += Magic.Length;
        int width = ReadInt32(bytes, ref position);
        int height = ReadInt32(bytes, ref position);
        int count = ReadInt32(bytes, ref position);
        if (width is <= 0 or > 128 || height is <= 0 or > 128 || count is <= 0 or > 65_536)
            throw new FormatException("The OCR model dimensions are invalid.");
        int features;
        int valueCount;
        try
        {
            features = checked(width * height);
            valueCount = checked(count * (features + 1));
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
        long remaining = bytes.Length - position;
        long required = (long)valueCount * sizeof(float);
        if (remaining != required) throw new FormatException("The OCR model payload length is invalid.");
        float[] biases = ReadFloats(bytes, ref position, count);
        float[] weights = ReadFloats(bytes, ref position, checked(count * features));
        try { return Create(width, height, labels, weights, biases); }
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
        return aspect < 0.5 ? 0 : aspect > 1 ? 2 : 1;
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
                && (_labelShapeMasks[_labels[label]] & 1 << shape) != 0)
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
            scores[label] = score;
            if (best < 0 || score > scores[best]) best = label;
        }
        if (best < 0)
            throw new ArgumentException(
                "The OCR character whitelist has no labels in this model.", nameof(allowedLabels));
        double runnerUp = double.NegativeInfinity;
        for (int label = 0; label < scores.Length; label++)
            if (!string.Equals(_labels[label], _labels[best], StringComparison.Ordinal)
                && scores[label] > runnerUp)
                runnerUp = scores[label];
        if (double.IsNegativeInfinity(runnerUp)) return (_labels[best], 1);
        return (_labels[best], 1 / (1 + Math.Exp(runnerUp - scores[best])));
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
        return selected.Count == 1 ? selected[0] : new PdfOcrRecognitionModelSelection(
            string.Join('+', selected.Select(item => item.Language)),
            PdfOcrRecognitionModel.Combine(selected.Select(item => item.Model)));
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
        RecognizeBgra(bgra, width, height, model, options,
            (IReadOnlySet<string>?)null, cancellationToken);

    /// <summary>Runs engine recognition while restricting results to supplied characters.</summary>
    public static PdfOcrResult RecognizeBgra(ReadOnlyMemory<byte> bgra, int width, int height,
        PdfOcrRecognitionModel model, PdfOcrOptions options, string characterWhitelist,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterWhitelist);
        var allowedLabels = new HashSet<string>(characterWhitelist.EnumerateRunes()
            .Where(rune => !Rune.IsControl(rune) && !Rune.IsWhiteSpace(rune))
            .Select(rune => rune.ToString()), StringComparer.Ordinal);
        if (allowedLabels.Count == 0 || !model.Labels.Any(allowedLabels.Contains))
            throw new ArgumentException(
                "The OCR character whitelist has no labels in this model.",
                nameof(characterWhitelist));
        return RecognizeBgra(
            bgra, width, height, model, options, allowedLabels, cancellationToken);
    }

    private static PdfOcrResult RecognizeBgra(ReadOnlyMemory<byte> bgra, int width, int height,
        PdfOcrRecognitionModel model, PdfOcrOptions options,
        IReadOnlySet<string>? allowedLabels, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        PdfOcrPreparedImage prepared = PdfOcrImagePreprocessor.PrepareBgra(
            bgra, width, height, options, cancellationToken);
        PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(
            prepared, options.DetectPageSegments, cancellationToken);
        IReadOnlyList<PdfOcrRecognizedWord> recognized = Recognize(
            prepared, layout, model, allowedLabels, cancellationToken);
        var words = new PdfOcrPixelWord[recognized.Count];
        for (int index = 0; index < recognized.Count; index++)
        {
            PdfOcrRecognizedWord word = recognized[index];
            PdfOcrImageRegion bounds = PdfOcrImagePreprocessor.RestoreDeskewedBounds(
                word.Bounds, prepared.DeskewDegrees, width, height);
            words[index] = new PdfOcrPixelWord(word.Text, (float)word.Confidence,
                bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        }
        var lines = new List<string>(layout.Lines.Count);
        int wordIndex = 0;
        foreach (PdfOcrTextLine line in layout.Lines)
        {
            int count = line.Words.Count;
            lines.Add(string.Join(' ', words.AsSpan(wordIndex, count).ToArray()
                .Select(word => word.Text)));
            wordIndex += count;
        }
        float confidence = words.Length == 0
            ? 0 : (float)words.Average(word => word.Confidence);
        return new PdfOcrResult(string.Join(Environment.NewLine, lines), confidence, words);
    }

    /// <summary>Recognizes each component and assembles the labels into words.</summary>
    public static IReadOnlyList<PdfOcrRecognizedWord> Recognize(PdfOcrPreparedImage image,
        PdfOcrPageLayout layout, PdfOcrRecognitionModel model,
        CancellationToken cancellationToken = default) =>
        Recognize(image, layout, model, null, cancellationToken);

    private static IReadOnlyList<PdfOcrRecognizedWord> Recognize(
        PdfOcrPreparedImage image, PdfOcrPageLayout layout, PdfOcrRecognitionModel model,
        IReadOnlySet<string>? allowedLabels, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(model);
        var words = new List<PdfOcrRecognizedWord>(layout.Words.Count);
        double[] scores = ArrayPool<double>.Shared.Rent(model.LabelCount);
        int featureCount = checked(model.Width * model.Height);
        float[] features = ArrayPool<float>.Shared.Rent(featureCount);
        try
        {
            foreach (PdfOcrWordRegion word in layout.Words)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = new StringBuilder();
                double confidence = 0;
                foreach (PdfOcrImageRegion component in word.Components)
                {
                    Span<float> glyph = features.AsSpan(0, featureCount);
                    NormalizeGlyph(image, component, model.Width, model.Height, glyph);
                    (string label, double score) = model.Classify(
                        glyph, scores.AsSpan(0, model.LabelCount), allowedLabels);
                    text.Append(label);
                    confidence += score;
                }
                words.Add(new PdfOcrRecognizedWord(text.ToString(),
                    word.Components.Count == 0 ? 0 : confidence / word.Components.Count, word.Bounds));
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
        int width, int height)
    {
        var result = new float[width * height];
        NormalizeGlyph(image, region, width, height, result);
        return result;
    }

    private static void NormalizeGlyph(PdfOcrPreparedImage image, PdfOcrImageRegion region,
        int width, int height, Span<float> result)
    {
        result.Clear();
        ReadOnlySpan<byte> source = image.Pixels.Span;
        double scale = Math.Min(width / (double)region.Width, height / (double)region.Height);
        int scaledWidth = Math.Clamp((int)Math.Round(region.Width * scale), 1, width);
        int scaledHeight = Math.Clamp((int)Math.Round(region.Height * scale), 1, height);
        int offsetX = (width - scaledWidth) / 2;
        int offsetY = (height - scaledHeight) / 2;
        for (int y = 0; y < scaledHeight; y++)
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

/// <summary>An engine-rendered OCR page with reviewable words and pipeline diagnostics.</summary>
public sealed record PdfOcrPageRecognition(
    PdfOcrReview Review, IReadOnlyList<string> Diagnostics, int PixelWidth, int PixelHeight);

/// <summary>Renders, prepares, segments, and recognizes PDF pages entirely inside the engine.</summary>
public sealed class PdfOcrPageRecognizer
{
    private readonly PdfOcrRecognitionModel? _model;
    private readonly PdfOcrRecognitionModelCatalog? _models;
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

    /// <summary>Creates an engine-owned pipeline with language-specific models.</summary>
    public PdfOcrPageRecognizer(PdfDocument document, PdfOcrRecognitionModelCatalog models)
    {
        ArgumentNullException.ThrowIfNull(document);
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _renderer = new PdfPageRenderer(document);
        _pages = PdfPageInformation.Read(document);
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
            PdfOcrImageRegion bounds = UnrotateImageBounds(
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
            IReadOnlyList<PdfOcrRecognizedWord> recognized = PdfOcrRecognizer.Recognize(
                prepared, layout, model, cancellationToken);
            int characters = recognized.Sum(word => word.Text.Length);
            double score = characters == 0 ? -1
                : recognized.Sum(word => word.Confidence * word.Text.Length) / characters;
            return new OrientationCandidate(
                rotation, prepared.DeskewDegrees,
                recognized, prepared.Diagnostics, score);
        }
    }

    private static PdfOcrImageRegion UnrotateImageBounds(
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
