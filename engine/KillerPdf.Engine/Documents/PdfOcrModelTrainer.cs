using System.Text;

namespace KillerPdf.Engine.Documents;

/// <summary>One labeled, normalized glyph used to train an engine OCR model.</summary>
public readonly record struct PdfOcrTrainingSample(
    string Label, ReadOnlyMemory<float> Features);

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

    private static void ValidateLabel(string label,
        IEnumerable<PdfOcrTrainingSample> samples)
    {
        if (string.IsNullOrEmpty(label) || Encoding.UTF8.GetByteCount(label) > 64)
            throw new ArgumentException(
                "OCR training labels are empty, oversized, or invalid.", nameof(samples));
    }

    private sealed class Accumulator(int featureCount)
    {
        internal decimal[] Sums { get; } = new decimal[featureCount];
        internal int Count { get; set; }
    }
}
