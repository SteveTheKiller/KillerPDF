using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Documents;

/// <summary>A bounded, versioned OCR glyph-classification model.</summary>
public sealed class PdfOcrRecognitionModel
{
    private static readonly byte[] Magic = "KPOCR1\0"u8.ToArray();
    private readonly string[] _labels;
    private readonly float[] _weights;
    private readonly float[] _biases;

    private PdfOcrRecognitionModel(int width, int height, string[] labels,
        float[] weights, float[] biases)
    {
        Width = width;
        Height = height;
        _labels = labels;
        _weights = weights;
        _biases = biases;
        Labels = Array.AsReadOnly(labels);
    }

    /// <summary>Gets the normalized glyph width expected by the model.</summary>
    public int Width { get; }
    /// <summary>Gets the normalized glyph height expected by the model.</summary>
    public int Height { get; }
    /// <summary>Gets model labels in classifier order.</summary>
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
            string.IsNullOrEmpty(label) || Encoding.UTF8.GetByteCount(label) > 64))
            throw new ArgumentException("OCR model labels are empty, oversized, or invalid.", nameof(labels));
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new ArgumentException("OCR model labels must be unique.", nameof(labels));
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

    /// <summary>Writes the stable model format used by the runtime.</summary>
    public byte[] Save()
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Width);
        writer.Write(Height);
        writer.Write(_labels.Length);
        foreach (string label in _labels)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(label);
            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }
        foreach (float value in _biases) writer.Write(value);
        foreach (float value in _weights) writer.Write(value);
        writer.Flush();
        return output.ToArray();
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
        using var input = new MemoryStream(source.ToArray(), writable: false);
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: false);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
            throw new FormatException("The OCR model header is invalid.");
        int width = reader.ReadInt32(), height = reader.ReadInt32(), count = reader.ReadInt32();
        if (width is <= 0 or > 128 || height is <= 0 or > 128 || count is <= 0 or > 65_536)
            throw new FormatException("The OCR model dimensions are invalid.");
        var labels = new string[count];
        for (int index = 0; index < count; index++)
        {
            int length = reader.ReadByte();
            if (length == 0 || input.Length - input.Position < length)
                throw new FormatException("An OCR model label is invalid.");
            labels[index] = new UTF8Encoding(false, true).GetString(reader.ReadBytes(length));
        }
        int features = checked(width * height);
        long remaining = input.Length - input.Position;
        long required = checked((long)count * (features + 1) * sizeof(float));
        if (remaining != required) throw new FormatException("The OCR model payload length is invalid.");
        float[] biases = ReadFloats(reader, count);
        float[] weights = ReadFloats(reader, checked(count * features));
        try { return Create(width, height, labels, weights, biases); }
        catch (ArgumentException exception) { throw new FormatException("The OCR model payload is invalid.", exception); }
    }

    internal (string Label, double Confidence) Classify(ReadOnlySpan<float> features)
    {
        int featureCount = Width * Height;
        if (features.Length != featureCount) throw new ArgumentException("Glyph feature size mismatch.");
        var scores = new double[_labels.Length];
        for (int label = 0; label < _labels.Length; label++)
        {
            double score = _biases[label];
            int offset = label * featureCount;
            for (int feature = 0; feature < featureCount; feature++)
                score += _weights[offset + feature] * features[feature];
            scores[label] = score;
        }
        int best = Array.IndexOf(scores, scores.Max());
        double maximum = scores[best];
        double denominator = scores.Sum(score => Math.Exp(score - maximum));
        return (_labels[best], 1 / denominator);
    }

    private static float[] ReadFloats(BinaryReader reader, int count)
    {
        var values = new float[count];
        for (int index = 0; index < count; index++) values[index] = reader.ReadSingle();
        return values;
    }
}

/// <summary>A recognized OCR word with pixel bounds and calibrated model confidence.</summary>
public sealed record PdfOcrRecognizedWord(string Text, double Confidence, PdfOcrImageRegion Bounds);

/// <summary>Runs the engine-owned glyph model over a detected page layout.</summary>
public static class PdfOcrRecognizer
{
    /// <summary>Recognizes each component and assembles the labels into words.</summary>
    public static IReadOnlyList<PdfOcrRecognizedWord> Recognize(PdfOcrPreparedImage image,
        PdfOcrPageLayout layout, PdfOcrRecognitionModel model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(model);
        var words = new List<PdfOcrRecognizedWord>(layout.Words.Count);
        foreach (PdfOcrWordRegion word in layout.Words)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = new StringBuilder();
            double confidence = 0;
            foreach (PdfOcrImageRegion component in word.Components)
            {
                float[] features = NormalizeGlyph(image, component, model.Width, model.Height);
                (string label, double score) = model.Classify(features);
                text.Append(label);
                confidence += score;
            }
            words.Add(new PdfOcrRecognizedWord(text.ToString(),
                word.Components.Count == 0 ? 0 : confidence / word.Components.Count, word.Bounds));
        }
        return Array.AsReadOnly(words.ToArray());
    }

    /// <summary>Normalizes one glyph into a centered, aspect-preserving model feature grid.</summary>
    public static float[] NormalizeGlyph(PdfOcrPreparedImage image, PdfOcrImageRegion region,
        int width, int height)
    {
        var result = new float[width * height];
        ReadOnlySpan<byte> source = image.Pixels.Span;
        double scale = Math.Min(width / (double)region.Width, height / (double)region.Height);
        int scaledWidth = Math.Clamp((int)Math.Round(region.Width * scale), 1, width);
        int scaledHeight = Math.Clamp((int)Math.Round(region.Height * scale), 1, height);
        int offsetX = (width - scaledWidth) / 2;
        int offsetY = (height - scaledHeight) / 2;
        for (int y = 0; y < scaledHeight; y++)
            for (int x = 0; x < scaledWidth; x++)
            {
                int sx = region.Left
                    + Math.Min(region.Width - 1, x * region.Width / scaledWidth);
                int sy = region.Top
                    + Math.Min(region.Height - 1, y * region.Height / scaledHeight);
                result[(offsetY + y) * width + offsetX + x] =
                    1 - source[sy * image.Width + sx] / 255f;
            }
        return result;
    }
}

/// <summary>An engine-rendered OCR page with reviewable words and pipeline diagnostics.</summary>
public sealed record PdfOcrPageRecognition(
    PdfOcrReview Review, IReadOnlyList<string> Diagnostics, int PixelWidth, int PixelHeight);

/// <summary>Renders, prepares, segments, and recognizes PDF pages entirely inside the engine.</summary>
public sealed class PdfOcrPageRecognizer
{
    private readonly PdfOcrRecognitionModel _model;
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
        PdfOcrPreparedImage prepared = PdfOcrImagePreprocessor.PrepareBgra(
            rendered.Pixels, rendered.Width, rendered.Height, ocrOptions, cancellationToken);
        PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(prepared, cancellationToken);
        IReadOnlyList<PdfOcrRecognizedWord> recognized = PdfOcrRecognizer.Recognize(
            prepared, layout, _model, cancellationToken);
        PdfPageInformation page = _pages[pageIndex];
        string? language = ocrOptions.Languages.Count == 1 ? ocrOptions.Languages[0] : null;
        var words = new PdfOcrWord[recognized.Count];
        for (int sequence = 0; sequence < recognized.Count; sequence++)
        {
            PdfOcrRecognizedWord word = recognized[sequence];
            PdfOcrImageRegion bounds = word.Bounds;
            PdfContentBounds pdfBounds = MapBounds(bounds, rendered.Width, rendered.Height, page);
            words[sequence] = new PdfOcrWord($"page-{pageIndex}-word-{sequence}",
                pageIndex, sequence, word.Text, word.Text, pdfBounds, word.Confidence, language);
        }
        string[] diagnostics = [.. rendered.Diagnostics.Concat(prepared.Diagnostics)
            .Distinct(StringComparer.Ordinal)];
        return new PdfOcrPageRecognition(new PdfOcrReview(words),
            Array.AsReadOnly(diagnostics), rendered.Width, rendered.Height);
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
}
