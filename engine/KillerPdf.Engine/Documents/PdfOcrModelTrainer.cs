using System.Buffers;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;
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

/// <summary>Measured OCR accuracy for one Unicode script.</summary>
public sealed record PdfOcrScriptAccuracy(
    string Script, int SampleCount, int CorrectCount)
{
    /// <summary>Gets the correctly classified fraction for the script.</summary>
    public double Accuracy => CorrectCount / (double)SampleCount;
}

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
        double averageConfidence, double calibrationError, double brierScore,
        IEnumerable<PdfOcrConfusion> confusion,
        IEnumerable<PdfOcrScriptAccuracy> scripts)
    {
        SampleCount = sampleCount;
        CorrectCount = correctCount;
        AverageConfidence = averageConfidence;
        CalibrationError = calibrationError;
        BrierScore = brierScore;
        Confusion = Array.AsReadOnly(confusion.ToArray());
        Scripts = Array.AsReadOnly(scripts.ToArray());
    }

    /// <summary>Gets the number of evaluated glyphs.</summary>
    public int SampleCount { get; }
    /// <summary>Gets the number of correctly classified glyphs.</summary>
    public int CorrectCount { get; }
    /// <summary>Gets the correctly classified fraction.</summary>
    public double Accuracy => CorrectCount / (double)SampleCount;
    /// <summary>Gets the mean winning-class confidence.</summary>
    public double AverageConfidence { get; }
    /// <summary>Gets the ten-bin expected calibration error.</summary>
    public double CalibrationError { get; }
    /// <summary>Gets the mean squared confidence error.</summary>
    public double BrierScore { get; }
    /// <summary>Gets observed label pairs in stable ordinal order.</summary>
    public IReadOnlyList<PdfOcrConfusion> Confusion { get; }
    /// <summary>Gets accuracy grouped by the expected label's Unicode script.</summary>
    public IReadOnlyList<PdfOcrScriptAccuracy> Scripts { get; }
}

/// <summary>Builds deterministic engine OCR models without a native inference runtime.</summary>
public static class PdfOcrModelTrainer
{
    private const int MaximumSamples = 10_000_000;
    private const int MaximumModelValues = 16 * 1024 * 1024;
    private const int MaximumPrototypesPerShape = 96;
    private const int MaximumLabelsPerComponent = 4;
    private const double LabelPriorWeight = 0.25;
    private const double PrototypeSupportWeight = 0.0015;
    private static readonly int[] StandardTrainingRenderScales = [1, 2, 3, 4, 6];

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
        var labelCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int sampleCount = 0;
        foreach (PdfOcrTrainingSample sample in samples)
        {
            if ((sampleCount & 0x3FF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (++sampleCount > MaximumSamples)
                throw new ArgumentException("The OCR training set exceeds the sample limit.",
                    nameof(samples));
            ValidateLabel(sample.Label, samples);
            ValidateFeatures(sample.Features.Span, featureCount, samples);
            labelCounts[sample.Label] = labelCounts.GetValueOrDefault(sample.Label) + 1;
            int shape = PdfOcrRecognitionModel.ShapeBucket(
                sample.Features.Span, width, height);
            var key = (sample.Label, shape < 0 ? 1 : shape);
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

        (string Label, int Shape, ulong Hash, float[] Features, int Count)[] ordered =
        [.. prototypes.OrderBy(entry => entry.Key.Label, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key.Shape)
            .SelectMany(entry => entry.Value.Items.Select(item =>
                (entry.Key.Label, entry.Key.Shape,
                    item.Hash, item.Features, item.Count)))];
        if (checked((long)ordered.Length * featureCount) > MaximumModelValues)
            throw new ArgumentException(
                "The OCR training set exceeds the model size limit.", nameof(samples));
        string[] orderedLabels = [.. ordered.Select(item => item.Label)];
        var weights = new float[checked(orderedLabels.Length * featureCount)];
        var biases = new float[orderedLabels.Length];
        var priors = new float[orderedLabels.Length];
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
            double prior = Math.Log((labelCounts[orderedLabels[label]] + 1d)
                / (sampleCount + labelCounts.Count));
            biases[label] = checked((float)(-squaredLength / scoreScale
                + PrototypeSupportWeight * Math.Log(ordered[label].Count + 1d)));
            priors[label] = checked((float)(LabelPriorWeight * prior));
        }
        return PdfOcrRecognitionModel.CreatePrototype(
            width, height, orderedLabels, weights, biases, priors);
    }

    /// <summary>Returns whether the bundled training faces contain one requested glyph.</summary>
    public static bool SupportsStandardFontLabel(string label)
    {
        if (!IsValidLabel(label)) return false;
        return PdfStandardFontSubstitutes.OcrTrainingFonts().Any(
            font => MapsOneGlyph(font, label));
    }

    /// <summary>Creates Unicode training coverage from bundled standard-font substitutes.</summary>
    public static IReadOnlyList<PdfOcrTrainingSample> CreateStandardFontSamples(
        IEnumerable<string> labels, int width, int height,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (width is <= 0 or > 128) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is <= 0 or > 128) throw new ArgumentOutOfRangeException(nameof(height));
        string[] requested = [.. labels.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
        if (requested.Length == 0 || requested.Any(label =>
            !SupportsStandardFontLabel(label)))
            throw new ArgumentException(
                "Every standard-font OCR label must map to one bundled glyph.", nameof(labels));

        IReadOnlyList<TrueTypeFont> fonts = PdfStandardFontSubstitutes.OcrTrainingFonts();
        var samples = new List<PdfOcrTrainingSample>(checked(
            requested.Length * fonts.Count * StandardTrainingRenderScales.Length));
        foreach (TrueTypeFont font in fonts)
        {
            string[] supported = [.. requested.Where(label => MapsOneGlyph(font, label))];
            if (supported.Length > 0)
                samples.AddRange(CreateEmbeddedFontSamples(
                    font, supported, width, height, cancellationToken));
        }
        return Array.AsReadOnly(samples.ToArray());
    }

    private static bool MapsOneGlyph(TrueTypeFont font, string label)
    {
        IReadOnlyList<FontGlyphMapping> mappings = font.MapText(label);
        return mappings.Count == 1 && mappings[0].Glyph != 0;
    }

    /// <summary>Creates Unicode training samples from a supplied embeddable OpenType font.</summary>
    public static IReadOnlyList<PdfOcrTrainingSample> CreateEmbeddedFontSamples(
        TrueTypeFont font, IEnumerable<string> labels, int width, int height,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(labels);
        if (width is <= 0 or > 128) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is <= 0 or > 128) throw new ArgumentOutOfRangeException(nameof(height));
        if (!font.EmbeddingAllowed)
            throw new ArgumentException(
                "The OCR training font does not permit embedding.", nameof(font));
        string[] requested = [.. labels.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
        if (requested.Length == 0 || requested.Any(label => !IsValidLabel(label)))
            throw new ArgumentException(
                "Embedded-font OCR labels must be valid Unicode text.", nameof(labels));
        if (requested.Any(label =>
            {
                IReadOnlyList<FontGlyphMapping> mappings = font.MapText(label);
                return mappings.Count != 1 || mappings[0].Glyph == 0;
            }))
            throw new ArgumentException(
                "Every embedded-font OCR label must map to one font glyph.", nameof(labels));

        const int columns = 10;
        const int cellSize = 80;
        const int margin = 40;
        const int fontSize = 32;
        int rows = (requested.Length + columns - 1) / columns;
        int pageWidth = columns * cellSize + margin * 2;
        int pageHeight = rows * cellSize + margin * 2;
        var content = new PdfContentStreamBuilder();
        for (int index = 0; index < requested.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int column = index % columns;
            int row = index / columns;
            content.BeginText().SetFont(font, fontSize)
                .SetTextMatrix(1, 0, 0, 1,
                    margin + column * cellSize,
                    pageHeight - margin - (row + 1) * cellSize + 20)
                .ShowUnicodeText(requested[index]).EndText();
        }
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(pageWidth, pageHeight, content).Build());
        var options = new PdfOcrOptions(["und"], deskew: false,
            correctOrientation: false, removeBackground: true, removeNoise: true,
            detectPageSegments: false);
        var samples = new List<PdfOcrTrainingSample>(
            checked(requested.Length * StandardTrainingRenderScales.Length));
        foreach (int scale in StandardTrainingRenderScales)
        {
            cancellationToken.ThrowIfCancellationRequested();
            samples.AddRange(CreatePageSamples(document, 0,
                new PdfRenderOptions(pageWidth * scale, pageHeight * scale,
                    includeAnnotations: false, includeFormFields: false),
                options, width, height, cancellationToken));
        }
        return Array.AsReadOnly(samples.ToArray());
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
                    PdfOcrRecognizer.NormalizeGlyph(
                        image, bounds, width, height, cancellationToken));
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
        PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(
            prepared, ocrOptions.DetectPageSegments, cancellationToken);
        IReadOnlyList<PdfOcrImageRegion> components = layout.Components;
        int featureCount = checked(width * height);
        var labels = new List<(string Label, PdfOcrImageRegion Bounds)>();
        var seenLabels = new HashSet<(string Label, PdfOcrImageRegion Bounds)>();
        foreach (PdfExtractedLetter letter in content.Letters)
        {
            if (!HasUprightBaseline(letter))
                continue;
            string label = letter.Value.Trim();
            if (!IsValidLabel(label)) continue;
            PdfOcrImageRegion? bounds = MapToPixels(
                letter.BoundingBox, page, rendered.Width, rendered.Height);
            if (bounds is not null && seenLabels.Add((label, bounds)))
                labels.Add((label, bounds));
        }
        IReadOnlyList<IReadOnlyList<PdfOcrImageRegion>> assignments =
            AssignComponentsToLabels(components,
                [.. labels.Select(label => label.Bounds)], cancellationToken);
        Dictionary<PdfOcrImageRegion, int> assignmentCounts = assignments
            .SelectMany(assignment => assignment)
            .GroupBy(component => component)
            .ToDictionary(group => group.Key, group => group.Count());
        var candidates = new List<(string Label, PdfOcrImageRegion Bounds,
            PdfOcrImageRegion LineBounds)>();
        for (int index = 0; index < labels.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfOcrImageRegion labelBounds = labels[index].Bounds;
            PdfOcrImageRegion[] glyph = [.. assignments[index]
                .Select(component => assignmentCounts[component] > 1
                    ? new PdfOcrImageRegion(
                        Math.Max(component.Left, labelBounds.Left),
                        Math.Max(component.Top, labelBounds.Top),
                        Math.Min(component.Right, labelBounds.Right),
                        Math.Min(component.Bottom, labelBounds.Bottom))
                    : component)
                .Where(overlap => overlap.Width > 0 && overlap.Height > 0)];
            if (glyph.Length == 0) continue;
            var bounds = new PdfOcrImageRegion(
                glyph.Min(item => item.Left), glyph.Min(item => item.Top),
                glyph.Max(item => item.Right), glyph.Max(item => item.Bottom));
            if (!IsPlausibleSampleBounds(labelBounds, bounds)) continue;
            int centerX = (bounds.Left + bounds.Right) / 2;
            int centerY = (bounds.Top + bounds.Bottom) / 2;
            PdfOcrTextLine? line = layout.Lines.FirstOrDefault(line =>
                centerX >= line.Bounds.Left && centerX < line.Bounds.Right
                && centerY >= line.Bounds.Top && centerY < line.Bounds.Bottom);
            PdfOcrImageRegion lineBounds = line is null
                ? bounds : PdfOcrRecognizer.NormalizationLineBounds(line, bounds);
            candidates.Add((labels[index].Label, bounds, lineBounds));
        }
        IReadOnlySet<PdfOcrImageRegion> ambiguousBounds =
            FindAmbiguousSampleBounds(candidates.Select(candidate =>
                (candidate.Label, candidate.Bounds)));
        var samples = new List<PdfOcrTrainingSample>(candidates.Count);
        foreach ((string label, PdfOcrImageRegion bounds,
            PdfOcrImageRegion lineBounds) in candidates)
        {
            if (ambiguousBounds.Contains(bounds)) continue;
            if (checked((samples.Count + 1L) * featureCount) > MaximumModelValues)
                throw new ArgumentException(
                    "The PDF page has too many OCR training values.", nameof(document));
            samples.Add(new PdfOcrTrainingSample(label,
                PdfOcrRecognizer.NormalizeGlyph(
                    prepared, bounds, lineBounds, width, height, cancellationToken)));
        }
        return Array.AsReadOnly(samples.ToArray());
    }

    internal static bool IsPlausibleSampleBounds(
        PdfOcrImageRegion label, PdfOcrImageRegion sample)
    {
        if (label.Width <= 0 || label.Height <= 0
            || sample.Width <= 0 || sample.Height <= 0)
            return false;
        int overlapWidth = Math.Min(label.Right, sample.Right)
            - Math.Max(label.Left, sample.Left);
        int overlapHeight = Math.Min(label.Bottom, sample.Bottom)
            - Math.Max(label.Top, sample.Top);
        if (overlapWidth <= 0 || overlapHeight <= 0) return false;
        long overlapArea = (long)overlapWidth * overlapHeight;
        long sampleArea = (long)sample.Width * sample.Height;
        long labelArea = (long)label.Width * label.Height;
        return overlapArea * 2 >= sampleArea
            && sampleArea <= labelArea * 2
            && sampleArea * 20 >= labelArea
            && (long)sample.Width * 5 >= label.Width
            && (long)sample.Height * 5 >= label.Height;
    }

    private static bool HasUprightBaseline(PdfExtractedLetter letter)
    {
        double horizontal = Math.Abs(letter.EndBaseLine.X - letter.StartBaseLine.X);
        double vertical = Math.Abs(letter.EndBaseLine.Y - letter.StartBaseLine.Y);
        return horizontal > 0 && vertical * 8 <= horizontal;
    }

    internal static IReadOnlySet<PdfOcrImageRegion> FindAmbiguousSampleBounds(
        IEnumerable<(string Label, PdfOcrImageRegion Bounds)> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        (string Label, PdfOcrImageRegion Bounds)[] ordered = [.. candidates
            .OrderBy(candidate => candidate.Bounds.Left)
            .ThenBy(candidate => candidate.Bounds.Top)];
        var ambiguous = new HashSet<PdfOcrImageRegion>();
        for (int left = 0; left < ordered.Length; left++)
            for (int right = left + 1; right < ordered.Length
                && ordered[right].Bounds.Left < ordered[left].Bounds.Right; right++)
            {
                if (string.Equals(ordered[left].Label,
                    ordered[right].Label, StringComparison.Ordinal))
                    continue;
                PdfOcrImageRegion first = ordered[left].Bounds;
                PdfOcrImageRegion second = ordered[right].Bounds;
                int overlapWidth = Math.Min(first.Right, second.Right)
                    - Math.Max(first.Left, second.Left);
                int overlapHeight = Math.Min(first.Bottom, second.Bottom)
                    - Math.Max(first.Top, second.Top);
                if (overlapWidth <= 0 || overlapHeight <= 0) continue;
                long intersection = (long)overlapWidth * overlapHeight;
                long union = (long)first.Width * first.Height
                    + (long)second.Width * second.Height - intersection;
                if (intersection * 10 < union * 9) continue;
                ambiguous.Add(first);
                ambiguous.Add(second);
            }
        return ambiguous;
    }

    internal static IReadOnlyList<IReadOnlyList<PdfOcrImageRegion>>
        AssignComponentsToLabels(IReadOnlyList<PdfOcrImageRegion> components,
            IReadOnlyList<PdfOcrImageRegion> labels,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(labels);
        var assignments = labels.Select(_ => new List<PdfOcrImageRegion>()).ToArray();
        for (int componentIndex = 0; componentIndex < components.Count; componentIndex++)
        {
            if ((componentIndex & 0x3FF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            PdfOcrImageRegion component = components[componentIndex];
            int[] centerOwners = [.. Enumerable.Range(0, labels.Count)
                .Where(labelIndex => Contains(component, CenterX(labels[labelIndex]),
                    CenterY(labels[labelIndex])))];
            if (centerOwners.Length > MaximumLabelsPerComponent) continue;
            if (centerOwners.Length > 0)
            {
                foreach (int sharedOwner in centerOwners)
                    assignments[sharedOwner].Add(component);
                continue;
            }

            int componentCenterX = CenterX(component);
            int componentCenterY = CenterY(component);
            int owner = -1;
            int bestOverlap = -1;
            long bestDistance = long.MaxValue;
            for (int labelIndex = 0; labelIndex < labels.Count; labelIndex++)
            {
                PdfOcrImageRegion label = labels[labelIndex];
                if (!Contains(label, componentCenterX, componentCenterY)) continue;
                int overlap = Math.Max(0, Math.Min(component.Right, label.Right)
                        - Math.Max(component.Left, label.Left))
                    * Math.Max(0, Math.Min(component.Bottom, label.Bottom)
                        - Math.Max(component.Top, label.Top));
                long dx = componentCenterX - CenterX(label);
                long dy = componentCenterY - CenterY(label);
                long distance = dx * dx + dy * dy;
                if (overlap > bestOverlap || overlap == bestOverlap && distance < bestDistance)
                {
                    owner = labelIndex;
                    bestOverlap = overlap;
                    bestDistance = distance;
                }
            }
            if (owner >= 0) assignments[owner].Add(component);
        }
        return Array.AsReadOnly(assignments.Select(assignment =>
            (IReadOnlyList<PdfOcrImageRegion>)Array.AsReadOnly(assignment.ToArray())).ToArray());

        static int CenterX(PdfOcrImageRegion region) =>
            (region.Left + region.Right) / 2;
        static int CenterY(PdfOcrImageRegion region) =>
            (region.Top + region.Bottom) / 2;
        static bool Contains(PdfOcrImageRegion region, int x, int y) =>
            x >= region.Left && x < region.Right
            && y >= region.Top && y < region.Bottom;
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
        double confidenceSum = 0, squaredConfidenceErrorSum = 0;
        int[] calibrationCounts = new int[10];
        int[] calibrationCorrect = new int[10];
        double[] calibrationConfidence = new double[10];
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
                bool correct = string.Equals(
                    sample.Label, predicted, StringComparison.Ordinal);
                if (correct)
                    correctCount++;
                confidenceSum += confidence;
                double confidenceError = confidence - (correct ? 1 : 0);
                squaredConfidenceErrorSum += confidenceError * confidenceError;
                int calibrationBin = Math.Min(9, (int)(confidence * 10));
                calibrationCounts[calibrationBin]++;
                if (correct) calibrationCorrect[calibrationBin]++;
                calibrationConfidence[calibrationBin] += confidence;
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
        PdfOcrScriptAccuracy[] scripts = [.. entries
            .GroupBy(item => ScriptForLabel(item.Expected), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new PdfOcrScriptAccuracy(group.Key,
                group.Sum(item => item.Count),
                group.Where(item => string.Equals(item.Expected, item.Predicted,
                    StringComparison.Ordinal)).Sum(item => item.Count)))];
        double calibrationError = 0;
        for (int bin = 0; bin < calibrationCounts.Length; bin++)
        {
            if (calibrationCounts[bin] == 0) continue;
            double binAccuracy = calibrationCorrect[bin] / (double)calibrationCounts[bin];
            double binConfidence = calibrationConfidence[bin] / calibrationCounts[bin];
            calibrationError += calibrationCounts[bin] / (double)sampleCount
                * Math.Abs(binAccuracy - binConfidence);
        }
        return new PdfOcrModelEvaluation(sampleCount, correctCount,
            confidenceSum / sampleCount, calibrationError,
            squaredConfidenceErrorSum / sampleCount, entries, scripts);
    }

    private static string ScriptForLabel(string label)
    {
        int value = Rune.GetRuneAt(label, 0).Value;
        return value switch
        {
            >= 0x0041 and <= 0x024F or >= 0x1E00 and <= 0x1EFF => "Latin",
            >= 0x0370 and <= 0x03FF or >= 0x1F00 and <= 0x1FFF => "Greek",
            >= 0x0400 and <= 0x052F => "Cyrillic",
            >= 0x0590 and <= 0x05FF => "Hebrew",
            >= 0x0600 and <= 0x08FF => "Arabic",
            >= 0x0900 and <= 0x0DFF => "Indic",
            >= 0x3040 and <= 0x30FF => "Kana",
            >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF
                or >= 0x20000 and <= 0x323AF => "Han",
            >= 0xAC00 and <= 0xD7AF => "Hangul",
            _ => "Common"
        };
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
        private readonly SortedDictionary<ulong, PrototypeAccumulator> _items = [];
        private long[]? _quantizedSum;
        private int _sampleCount;

        internal IReadOnlyList<PrototypeItem> Items
        {
            get
            {
                PrototypeItem[] items = [.. _items.Select(item =>
                    new PrototypeItem(item.Key,
                        item.Value.Features, item.Value.Count))];
                if (_sampleCount < 2) return items;
                float[] centroid = [.. _quantizedSum!.Select(value =>
                    (float)(value / (65535d * _sampleCount)))];
                ulong hash = Hash(centroid);
                var supportedCentroid = new PrototypeItem(
                    hash, centroid, _sampleCount);
                return _items.ContainsKey(hash)
                    ? [.. items.Select(item => item.Hash == hash
                        ? supportedCentroid : item)]
                    : [.. items.Append(supportedCentroid)
                        .OrderBy(item => item.Hash)];
            }
        }

        internal void Add(ReadOnlySpan<float> features)
        {
            _quantizedSum ??= new long[features.Length];
            for (int index = 0; index < features.Length; index++)
            {
                _quantizedSum[index] += Math.Clamp(
                    (int)Math.Round(features[index] * 65535), 0, 65535);
            }
            _sampleCount++;
            ulong hash = Hash(features);
            if (_items.TryGetValue(hash, out PrototypeAccumulator? existing))
            {
                existing.Add(features);
                return;
            }
            _items.Add(hash, new PrototypeAccumulator(features));
            if (_items.Count > MaximumPrototypesPerShape)
                _items.Remove(_items.Keys.Last());
        }

        private static ulong Hash(ReadOnlySpan<float> features)
        {
            ulong hash = 14695981039346656037UL;
            foreach (float value in features)
            {
                hash ^= (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);
                hash *= 1099511628211UL;
            }
            return hash;
        }

        private sealed class PrototypeAccumulator
        {
            private readonly long[] _sum;
            private int _count;

            internal PrototypeAccumulator(ReadOnlySpan<float> features)
            {
                _sum = new long[features.Length];
                Add(features);
            }

            internal float[] Features => [.. _sum.Select(value =>
                (float)(value / (65535d * _count)))];
            internal int Count => _count;

            internal void Add(ReadOnlySpan<float> features)
            {
                for (int index = 0; index < features.Length; index++)
                    _sum[index] += Math.Clamp(
                        (int)Math.Round(features[index] * 65535), 0, 65535);
                _count++;
            }
        }

        internal readonly record struct PrototypeItem(
            ulong Hash, float[] Features, int Count);
    }
}
