using System.Buffers;
using System.Text;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Documents;

/// <summary>One labeled, normalized glyph used to train an engine OCR model.</summary>
public readonly record struct PdfOcrTrainingSample(
    string Label, ReadOnlyMemory<float> Features);

/// <summary>One labeled glyph rectangle in an OCR-prepared image.</summary>
public readonly record struct PdfOcrLabeledGlyph(
    string Label, PdfOcrImageRegion Bounds);

/// <summary>One observed expected and predicted label pair.</summary>
public sealed record PdfOcrConfusion(string Expected, string Predicted, int Count);

/// <summary>Assigns complete source documents to deterministic OCR evaluation partitions.</summary>
public static class PdfOcrTrainingPartition
{
    /// <summary>Returns whether every sample from a document belongs in the holdout set.</summary>
    public static bool IsHoldout(string documentName, int holdoutPercent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);
        if (holdoutPercent is < 1 or > 99)
            throw new ArgumentOutOfRangeException(nameof(holdoutPercent));

        uint hash = 2166136261;
        foreach (char supplied in documentName)
        {
            char value = supplied == '\\' ? '/' : char.ToUpperInvariant(supplied);
            hash = (hash ^ value) * 16777619;
        }
        return hash % 100 < holdoutPercent;
    }
}

/// <summary>Measured recognition quality for a labeled OCR sample set.</summary>
public sealed class PdfOcrModelEvaluation
{
    internal PdfOcrModelEvaluation(int sampleCount, int correctCount,
        double averageConfidence, IEnumerable<PdfOcrConfusion> confusion)
    {
        SampleCount = sampleCount;
        CorrectCount = correctCount;
        AverageConfidence = averageConfidence;
        Confusion = Array.AsReadOnly(confusion.ToArray());
    }

    /// <summary>Gets the number of evaluated glyphs.</summary>
    public int SampleCount { get; }
    /// <summary>Gets the number of correctly classified glyphs.</summary>
    public int CorrectCount { get; }
    /// <summary>Gets the correctly classified fraction.</summary>
    public double Accuracy => CorrectCount / (double)SampleCount;
    /// <summary>Gets the mean winning-class confidence.</summary>
    public double AverageConfidence { get; }
    /// <summary>Gets observed label pairs in stable ordinal order.</summary>
    public IReadOnlyList<PdfOcrConfusion> Confusion { get; }
}

/// <summary>Builds deterministic engine OCR models without a native inference runtime.</summary>
public static class PdfOcrModelTrainer
{
    private const int MaximumSamples = 10_000_000;
    private const int MaximumModelValues = 16 * 1024 * 1024;
    private const int MaximumPrototypesPerShape = 12;

    /// <summary>Trains a bounded nearest-prototype classifier from normalized glyph samples.</summary>
    public static PdfOcrRecognitionModel Train(int width, int height,
        IEnumerable<PdfOcrTrainingSample> samples,
        CancellationToken cancellationToken = default)
    {
        if (width is <= 0 or > 128) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is <= 0 or > 128) throw new ArgumentOutOfRangeException(nameof(height));
        ArgumentNullException.ThrowIfNull(samples);
        int featureCount = checked(width * height);
        var prototypes = new Dictionary<(string Label, int Shape), PrototypeBucket>();
        int sampleCount = 0;
        foreach (PdfOcrTrainingSample sample in samples)
        {
            if ((sampleCount & 0x3FF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (++sampleCount > MaximumSamples)
                throw new ArgumentException("The OCR training set exceeds the sample limit.",
                    nameof(samples));
            ValidateLabel(sample.Label, samples);
            ValidateFeatures(sample.Features.Span, featureCount, samples);
            var key = (sample.Label, ShapeBucket(sample.Features.Span, width, height));
            if (!prototypes.TryGetValue(key, out PrototypeBucket? bucket))
            {
                bucket = new PrototypeBucket();
                prototypes.Add(key, bucket);
            }
            bucket.Add(sample.Features.Span);
        }
        if (sampleCount == 0)
            throw new ArgumentException("At least one OCR training sample is required.",
                nameof(samples));

        (string Label, int Shape, ulong Hash, float[] Features)[] ordered =
        [.. prototypes.OrderBy(entry => entry.Key.Label, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key.Shape)
            .SelectMany(entry => entry.Value.Items.Select(item =>
                (entry.Key.Label, entry.Key.Shape, item.Key, item.Value)))];
        if (checked((long)ordered.Length * featureCount) > MaximumModelValues)
            throw new ArgumentException(
                "The OCR training set exceeds the model size limit.", nameof(samples));
        string[] orderedLabels = [.. ordered.Select(item => item.Label)];
        var weights = new float[checked(orderedLabels.Length * featureCount)];
        var biases = new float[orderedLabels.Length];
        double scoreScale = Math.Sqrt(featureCount);
        for (int label = 0; label < orderedLabels.Length; label++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double squaredLength = 0;
            int offset = label * featureCount;
            for (int feature = 0; feature < featureCount; feature++)
            {
                double value = ordered[label].Features[feature];
                weights[offset + feature] = checked((float)(2 * value / scoreScale));
                squaredLength += value * value;
            }
            biases[label] = checked((float)(-squaredLength / scoreScale));
        }
        return PdfOcrRecognitionModel.Create(
            width, height, orderedLabels, weights, biases);
    }

    /// <summary>Trains directly from labeled glyph rectangles in a prepared image.</summary>
    public static PdfOcrRecognitionModel Train(int width, int height,
        PdfOcrPreparedImage image, IEnumerable<PdfOcrLabeledGlyph> glyphs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(glyphs);
        return Train(width, height, Samples(), cancellationToken);

        IEnumerable<PdfOcrTrainingSample> Samples()
        {
            int count = 0;
            foreach (PdfOcrLabeledGlyph glyph in glyphs)
            {
                if ((count++ & 0x3FF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                PdfOcrImageRegion bounds = glyph.Bounds;
                if (bounds.Left < 0 || bounds.Top < 0 || bounds.Right > image.Width
                    || bounds.Bottom > image.Height || bounds.Width <= 0 || bounds.Height <= 0)
                    throw new ArgumentException(
                        "An OCR training glyph lies outside its prepared image.", nameof(glyphs));
                yield return new PdfOcrTrainingSample(glyph.Label,
                    PdfOcrRecognizer.NormalizeGlyph(image, bounds, width, height));
            }
        }
    }

    /// <summary>Creates labeled samples from a PDF page that already has a text layer.</summary>
    public static IReadOnlyList<PdfOcrTrainingSample> CreatePageSamples(
        PdfDocument document, int pageIndex, PdfRenderOptions renderOptions,
        PdfOcrOptions ocrOptions, int width, int height,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(renderOptions);
        ArgumentNullException.ThrowIfNull(ocrOptions);
        if (width is <= 0 or > 128) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is <= 0 or > 128) throw new ArgumentOutOfRangeException(nameof(height));
        if (ocrOptions.Deskew || ocrOptions.CorrectOrientation)
            throw new ArgumentException(
                "PDF text-layer training cannot remap deskewed or reoriented pixels.",
                nameof(ocrOptions));

        PdfPageContent content = new PdfPageContentReader(document).Read(
            pageIndex, cancellationToken);
        PdfPageInformation page = PdfPageInformation.Read(document)[pageIndex];
        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            pageIndex, renderOptions, cancellationToken);
        PdfOcrPreparedImage prepared = PdfOcrImagePreprocessor.PrepareBgra(
            rendered.Pixels, rendered.Width, rendered.Height, ocrOptions, cancellationToken);
        IReadOnlyList<PdfOcrImageRegion> components = PdfOcrLayoutAnalyzer.Analyze(
            prepared, detectPageSegments: false, cancellationToken).Components;
        int featureCount = checked(width * height);
        var labels = new List<(string Label, PdfOcrImageRegion Bounds)>();
        foreach (PdfExtractedLetter letter in content.Letters)
        {
            string label = letter.Value.Trim();
            if (!IsValidLabel(label)) continue;
            PdfOcrImageRegion? bounds = MapToPixels(
                letter.BoundingBox, page, rendered.Width, rendered.Height);
            if (bounds is not null) labels.Add((label, bounds));
        }
        var assigned = new List<PdfOcrImageRegion>[labels.Count];
        foreach (PdfOcrImageRegion component in components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double centerX = (component.Left + component.Right) / 2d;
            double centerY = (component.Top + component.Bottom) / 2d;
            int best = -1;
            double bestDistance = double.PositiveInfinity;
            for (int index = 0; index < labels.Count; index++)
            {
                PdfOcrImageRegion bounds = labels[index].Bounds;
                if (centerX < bounds.Left || centerX > bounds.Right
                    || centerY < bounds.Top || centerY > bounds.Bottom)
                    continue;
                double dx = (centerX - (bounds.Left + bounds.Right) / 2d)
                    / Math.Max(1, bounds.Width);
                double dy = (centerY - (bounds.Top + bounds.Bottom) / 2d)
                    / Math.Max(1, bounds.Height);
                double distance = dx * dx + dy * dy;
                if (distance < bestDistance)
                {
                    best = index;
                    bestDistance = distance;
                }
            }
            if (best < 0) continue;
            (assigned[best] ??= []).Add(component);
        }
        var samples = new List<PdfOcrTrainingSample>();
        for (int index = 0; index < labels.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<PdfOcrImageRegion>? glyph = assigned[index];
            if (glyph is null) continue;
            var bounds = new PdfOcrImageRegion(
                glyph.Min(item => item.Left), glyph.Min(item => item.Top),
                glyph.Max(item => item.Right), glyph.Max(item => item.Bottom));
            if (checked((samples.Count + 1L) * featureCount) > MaximumModelValues)
                throw new ArgumentException(
                    "The PDF page has too many OCR training values.", nameof(document));
            samples.Add(new PdfOcrTrainingSample(labels[index].Label,
                PdfOcrRecognizer.NormalizeGlyph(prepared, bounds, width, height)));
        }
        return Array.AsReadOnly(samples.ToArray());
    }

    /// <summary>Evaluates a model against labeled normalized glyphs.</summary>
    public static PdfOcrModelEvaluation Evaluate(PdfOcrRecognitionModel model,
        IEnumerable<PdfOcrTrainingSample> samples,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(samples);
        int featureCount = checked(model.Width * model.Height);
        double[] scores = ArrayPool<double>.Shared.Rent(model.LabelCount);
        var confusion = new Dictionary<(string Expected, string Predicted), int>();
        int sampleCount = 0, correctCount = 0;
        double confidenceSum = 0;
        try
        {
            foreach (PdfOcrTrainingSample sample in samples)
            {
                if ((sampleCount & 0x3FF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (++sampleCount > MaximumSamples)
                    throw new ArgumentException(
                        "The OCR evaluation set exceeds the sample limit.", nameof(samples));
                ValidateLabel(sample.Label, samples);
                ValidateFeatures(sample.Features.Span, featureCount, samples);
                (string predicted, double confidence) = model.Classify(
                    sample.Features.Span, scores.AsSpan(0, model.LabelCount));
                if (string.Equals(sample.Label, predicted, StringComparison.Ordinal))
                    correctCount++;
                confidenceSum += confidence;
                var pair = (sample.Label, predicted);
                confusion[pair] = confusion.GetValueOrDefault(pair) + 1;
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(scores);
        }
        if (sampleCount == 0)
            throw new ArgumentException("At least one OCR evaluation sample is required.",
                nameof(samples));
        PdfOcrConfusion[] entries = [.. confusion
            .OrderBy(item => item.Key.Expected, StringComparer.Ordinal)
            .ThenBy(item => item.Key.Predicted, StringComparer.Ordinal)
            .Select(item => new PdfOcrConfusion(
                item.Key.Expected, item.Key.Predicted, item.Value))];
        return new PdfOcrModelEvaluation(
            sampleCount, correctCount, confidenceSum / sampleCount, entries);
    }

    private static void ValidateLabel(string label,
        IEnumerable<PdfOcrTrainingSample> samples)
    {
        if (!IsValidLabel(label))
            throw new ArgumentException(
                "OCR training labels are empty, oversized, or invalid.", nameof(samples));
    }

    private static bool IsValidLabel(string label) =>
        !string.IsNullOrEmpty(label) && Encoding.UTF8.GetByteCount(label) <= 64
        && !label.EnumerateRunes().Any(rune => rune == Rune.ReplacementChar
            || Rune.IsControl(rune) || Rune.IsWhiteSpace(rune));

    private static void ValidateFeatures(ReadOnlySpan<float> features, int featureCount,
        IEnumerable<PdfOcrTrainingSample> samples)
    {
        if (features.Length != featureCount)
            throw new ArgumentException(
                "An OCR sample has the wrong feature count.", nameof(samples));
        foreach (float value in features)
            if (!float.IsFinite(value) || value is < 0 or > 1)
                throw new ArgumentException(
                    "OCR sample features must be finite values from zero through one.",
                    nameof(samples));
    }

    private static int ShapeBucket(ReadOnlySpan<float> features, int width, int height)
    {
        int left = width, top = height, right = 0, bottom = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (features[y * width + x] <= 0.125f) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
            }
        if (right <= left || bottom <= top) return 1;
        double aspect = (right - left) / (double)(bottom - top);
        return aspect < 0.5 ? 0 : aspect > 1 ? 2 : 1;
    }

    private static PdfOcrImageRegion? MapToPixels(PdfContentBounds bounds,
        PdfPageInformation page, int pixelWidth, int pixelHeight)
    {
        if (!double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Bottom)
            || !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Top))
            return null;
        (double X, double Y)[] points =
        [
            Rotate(bounds.Left, bounds.Bottom), Rotate(bounds.Right, bounds.Bottom),
            Rotate(bounds.Left, bounds.Top), Rotate(bounds.Right, bounds.Top)
        ];
        bool quarterTurn = page.Rotation is 90 or 270;
        double displayWidth = quarterTurn ? page.Height : page.Width;
        double displayHeight = quarterTurn ? page.Width : page.Height;
        int left = Math.Clamp((int)Math.Floor(
            points.Min(point => point.X) * pixelWidth / displayWidth), 0, pixelWidth);
        int right = Math.Clamp((int)Math.Ceiling(
            points.Max(point => point.X) * pixelWidth / displayWidth), 0, pixelWidth);
        int top = Math.Clamp((int)Math.Floor((displayHeight
            - points.Max(point => point.Y)) * pixelHeight / displayHeight), 0, pixelHeight);
        int bottom = Math.Clamp((int)Math.Ceiling((displayHeight
            - points.Min(point => point.Y)) * pixelHeight / displayHeight), 0, pixelHeight);
        return right > left && bottom > top
            ? new PdfOcrImageRegion(left, top, right, bottom) : null;

        (double X, double Y) Rotate(double x, double y) => page.Rotation switch
        {
            90 => (y, page.Width - x),
            180 => (page.Width - x, page.Height - y),
            270 => (page.Height - y, x),
            _ => (x, y)
        };
    }

    private sealed class PrototypeBucket
    {
        private readonly SortedDictionary<ulong, float[]> _items = [];

        internal IReadOnlyDictionary<ulong, float[]> Items => _items;

        internal void Add(ReadOnlySpan<float> features)
        {
            ulong hash = 14695981039346656037UL;
            foreach (float value in features)
            {
                hash ^= (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);
                hash *= 1099511628211UL;
            }
            float[] candidate = features.ToArray();
            if (_items.TryGetValue(hash, out float[]? existing))
            {
                if (Compare(candidate, existing) < 0) _items[hash] = candidate;
                return;
            }
            _items.Add(hash, candidate);
            if (_items.Count > MaximumPrototypesPerShape)
                _items.Remove(_items.Keys.Last());
        }

        private static int Compare(float[] left, float[] right)
        {
            for (int index = 0; index < left.Length; index++)
            {
                int comparison = left[index].CompareTo(right[index]);
                if (comparison != 0) return comparison;
            }
            return 0;
        }
    }
}
