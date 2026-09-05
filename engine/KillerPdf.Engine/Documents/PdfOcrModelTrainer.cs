using System.Buffers;
using System.Text;

namespace KillerPdf.Engine.Documents;

/// <summary>One labeled, normalized glyph used to train an engine OCR model.</summary>
public readonly record struct PdfOcrTrainingSample(
    string Label, ReadOnlyMemory<float> Features);

/// <summary>One labeled glyph rectangle in an OCR-prepared image.</summary>
public readonly record struct PdfOcrLabeledGlyph(
    string Label, PdfOcrImageRegion Bounds);

/// <summary>One observed expected and predicted label pair.</summary>
public sealed record PdfOcrConfusion(string Expected, string Predicted, int Count);

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

    /// <summary>Trains a nearest-centroid linear classifier from normalized glyph samples.</summary>
    public static PdfOcrRecognitionModel Train(int width, int height,
        IEnumerable<PdfOcrTrainingSample> samples,
        CancellationToken cancellationToken = default)
    {
        if (width is <= 0 or > 128) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is <= 0 or > 128) throw new ArgumentOutOfRangeException(nameof(height));
        ArgumentNullException.ThrowIfNull(samples);
        int featureCount = checked(width * height);
        var labels = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        int sampleCount = 0;
        foreach (PdfOcrTrainingSample sample in samples)
        {
            if ((sampleCount & 0x3FF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (++sampleCount > MaximumSamples)
                throw new ArgumentException("The OCR training set exceeds the sample limit.",
                    nameof(samples));
            ValidateLabel(sample.Label, samples);
            if (sample.Features.Length != featureCount)
                throw new ArgumentException(
                    "An OCR training sample has the wrong feature count.", nameof(samples));
            if (!labels.TryGetValue(sample.Label, out Accumulator? accumulator))
            {
                if (checked((labels.Count + 1L) * featureCount) > MaximumModelValues)
                    throw new ArgumentException(
                        "The OCR training set exceeds the model size limit.", nameof(samples));
                accumulator = new Accumulator(featureCount);
                labels.Add(sample.Label, accumulator);
            }
            ReadOnlySpan<float> features = sample.Features.Span;
            for (int feature = 0; feature < features.Length; feature++)
            {
                float value = features[feature];
                if (!float.IsFinite(value) || value is < 0 or > 1)
                    throw new ArgumentException(
                        "OCR training features must be finite values from zero through one.",
                        nameof(samples));
                accumulator.Sums[feature] += (decimal)value;
            }
            accumulator.Count++;
        }
        if (sampleCount == 0)
            throw new ArgumentException("At least one OCR training sample is required.",
                nameof(samples));

        string[] orderedLabels = [.. labels.Keys.Order(StringComparer.Ordinal)];
        var weights = new float[checked(orderedLabels.Length * featureCount)];
        var biases = new float[orderedLabels.Length];
        for (int label = 0; label < orderedLabels.Length; label++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Accumulator accumulator = labels[orderedLabels[label]];
            double squaredLength = 0;
            int offset = label * featureCount;
            for (int feature = 0; feature < featureCount; feature++)
            {
                double centroid = (double)(accumulator.Sums[feature] / accumulator.Count);
                weights[offset + feature] = checked((float)(2 * centroid));
                squaredLength += centroid * centroid;
            }
            biases[label] = checked((float)-squaredLength);
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
        if (string.IsNullOrEmpty(label) || Encoding.UTF8.GetByteCount(label) > 64)
            throw new ArgumentException(
                "OCR training labels are empty, oversized, or invalid.", nameof(samples));
    }

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

    private sealed class Accumulator(int featureCount)
    {
        internal decimal[] Sums { get; } = new decimal[featureCount];
        internal int Count { get; set; }
    }
}
