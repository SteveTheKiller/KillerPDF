using System.Text;

namespace KillerPdf.Engine.Documents;

/// <summary>Runs one float tensor through a loaded ONNX model without exposing the runtime.</summary>
public interface IPdfOnnxOcrTensorRunner : IDisposable
{
    /// <summary>Gets the input tensor names the loaded model declares.</summary>
    IReadOnlyList<string> InputNames { get; }
    /// <summary>Gets the output tensor names the loaded model declares.</summary>
    IReadOnlyList<string> OutputNames { get; }

    /// <summary>Runs the model and returns the requested output as a flat tensor.</summary>
    PdfOnnxOcrTensor Run(string inputName, PdfOnnxOcrTensor input, string outputName,
        CancellationToken cancellationToken = default);
}

/// <summary>A flat row-major float tensor with its shape.</summary>
public sealed class PdfOnnxOcrTensor
{
    /// <summary>Creates a tensor after validating that the shape matches the data.</summary>
    public PdfOnnxOcrTensor(float[] data, int[] shape)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(shape);
        if (shape.Length == 0 || shape.Any(dimension => dimension < 0))
            throw new ArgumentException("Tensor shapes need at least one non-negative dimension.",
                nameof(shape));
        long expected = 1;
        foreach (int dimension in shape) expected = checked(expected * dimension);
        if (expected != data.LongLength)
            throw new ArgumentException("The tensor data length does not match its shape.",
                nameof(data));
        Data = data;
        Shape = shape;
    }

    /// <summary>Gets the row-major tensor values.</summary>
    public float[] Data { get; }
    /// <summary>Gets the tensor dimensions.</summary>
    public int[] Shape { get; }
}

/// <summary>Recognizes page text by running detected lines through a CTC ONNX recognizer.</summary>
/// <remarks>
/// Line detection, deskew, and orientation handling use the engine's own preprocessing so the
/// ONNX model only receives the single-line rasters it was trained on. Color models receive the
/// prepared grayscale replicated across channels.
/// </remarks>
public sealed class PdfOnnxOcrLineSession : IPdfOnnxOcrSession
{
    private const int MaximumLines = 4_096;
    private readonly PdfOnnxOcrModelDescription _model;
    private readonly IPdfOnnxOcrTensorRunner _runner;
    private bool _disposed;

    /// <summary>Creates a session that owns and disposes the supplied tensor runner.</summary>
    public PdfOnnxOcrLineSession(PdfOnnxOcrModelDescription model, IPdfOnnxOcrTensorRunner runner)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        try
        {
            if (!runner.InputNames.Contains(model.InputName, StringComparer.Ordinal))
                throw new InvalidOperationException(
                    $"The ONNX model has no input named '{model.InputName}'. Inputs: "
                    + string.Join(", ", runner.InputNames) + ".");
            if (!runner.OutputNames.Contains(model.OutputName, StringComparer.Ordinal))
                throw new InvalidOperationException(
                    $"The ONNX model has no output named '{model.OutputName}'. Outputs: "
                    + string.Join(", ", runner.OutputNames) + ".");
        }
        catch
        {
            runner.Dispose();
            throw;
        }
    }

    /// <summary>Gets the description of the loaded model.</summary>
    public PdfOnnxOcrModelDescription Model => _model;

    /// <inheritdoc />
    public PdfOcrResult RecognizeBgra(ReadOnlyMemory<byte> bgra, int width, int height, int stride,
        PdfOcrOptions options, string? characterWhitelist = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int rowBytes = checked(width * 4);
        if (stride < rowBytes) throw new ArgumentOutOfRangeException(nameof(stride));
        if (bgra.Length < checked(stride * height))
            throw new ArgumentException("The BGRA buffer is shorter than the requested image.", nameof(bgra));
        ArgumentNullException.ThrowIfNull(options);
        bool[]? allowed = CreateAllowedClasses(characterWhitelist);
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlyMemory<byte> packed = stride == rowBytes ? bgra : PackRows(bgra, width, height, stride);
        PdfOcrPreparedImage prepared = PdfOcrImagePreprocessor.PrepareBgra(
            packed, width, height, options, cancellationToken);
        PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(
            prepared, options.DetectPageSegments, cancellationToken);
        if (layout.Lines.Count > MaximumLines)
            throw new InvalidOperationException(
                $"The page has more than {MaximumLines} text lines.");
        var words = new List<PdfOcrPixelWord>();
        var lineTexts = new List<string>();
        double weightedConfidence = 0;
        int characters = 0;
        foreach (PdfOcrTextLine line in layout.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfOcrImageRegion bounds = line.Bounds;
            if (bounds.Width < 1 || bounds.Height < 1) continue;
            LineResult decoded = RecognizeLine(prepared, bounds, allowed, cancellationToken);
            if (decoded.Words.Count == 0) continue;
            var texts = new List<string>(decoded.Words.Count);
            foreach (DecodedWord word in decoded.Words)
            {
                PdfOcrImageRegion restored = PdfOcrImagePreprocessor.RestoreDeskewedBounds(
                    word.Bounds, prepared.DeskewDegrees, prepared.Width, prepared.Height);
                PdfOcrImageRegion clipped = new(
                    Math.Clamp(restored.Left, 0, Math.Max(0, width - 1)),
                    Math.Clamp(restored.Top, 0, Math.Max(0, height - 1)),
                    Math.Clamp(restored.Right, 1, width), Math.Clamp(restored.Bottom, 1, height));
                if (clipped.Right <= clipped.Left)
                    clipped = clipped with { Right = clipped.Left + 1 };
                if (clipped.Bottom <= clipped.Top)
                    clipped = clipped with { Bottom = clipped.Top + 1 };
                words.Add(new PdfOcrPixelWord(word.Text, (float)word.Confidence,
                    clipped.Left, clipped.Top, clipped.Right, clipped.Bottom));
                texts.Add(word.Text);
                int runes = word.Text.EnumerateRunes().Count();
                weightedConfidence += word.Confidence * runes;
                characters += runes;
            }
            lineTexts.Add(string.Join(' ', texts));
        }
        float mean = characters == 0 ? 0 : (float)Math.Clamp(weightedConfidence / characters, 0, 1);
        return new PdfOcrResult(string.Join(Environment.NewLine, lineTexts), mean, words);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runner.Dispose();
    }

    private bool[]? CreateAllowedClasses(string? characterWhitelist)
    {
        if (characterWhitelist is null) return null;
        var allowedText = new HashSet<string>(characterWhitelist.EnumerateRunes()
            .Where(rune => !Rune.IsControl(rune) && !Rune.IsWhiteSpace(rune))
            .Select(rune => rune.ToString()), StringComparer.Ordinal);
        var allowed = new bool[_model.Vocabulary.Count];
        bool any = false;
        for (int index = 0; index < allowed.Length; index++)
        {
            string token = _model.Vocabulary[index];
            bool keep = index == _model.BlankIndex || string.IsNullOrWhiteSpace(token)
                || token.EnumerateRunes().All(rune => allowedText.Contains(rune.ToString()));
            allowed[index] = keep;
            any |= keep && index != _model.BlankIndex && !string.IsNullOrWhiteSpace(token);
        }
        if (!any)
            throw new ArgumentException(
                "The OCR character whitelist has no tokens in this model's vocabulary.",
                nameof(characterWhitelist));
        return allowed;
    }

    private LineResult RecognizeLine(PdfOcrPreparedImage prepared, PdfOcrImageRegion bounds,
        bool[]? allowed, CancellationToken cancellationToken)
    {
        int targetHeight = _model.InputHeight;
        double scale = (double)targetHeight / bounds.Height;
        int scaledWidth = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        int inputWidth = _model.ResolveInputWidth(scaledWidth);
        int drawnWidth = Math.Min(scaledWidth, inputWidth);
        PdfOnnxOcrTensor input = BuildInput(prepared, bounds, drawnWidth, inputWidth);
        PdfOnnxOcrTensor output = _runner.Run(_model.InputName, input, _model.OutputName,
            cancellationToken)
            ?? throw new InvalidOperationException("The ONNX tensor runner returned no output.");
        (int steps, int classes, Func<int, int, float> read) = ReadOutput(output);
        if (classes != _model.Vocabulary.Count)
            throw new InvalidOperationException(
                $"The ONNX model emits {classes} classes but the vocabulary lists {_model.Vocabulary.Count} tokens.");
        var decodedWords = new List<DecodedWord>();
        var text = new StringBuilder();
        double confidenceSum = 0;
        int confidenceCount = 0;
        int wordStartStep = -1, previous = _model.BlankIndex;
        double pixelsPerStep = (double)drawnWidth / Math.Max(1, steps);
        double sourcePerPixel = 1.0 / scale;
        for (int step = 0; step < steps; step++)
        {
            (int best, double probability) = ArgMax(step, classes, read, allowed);
            bool repeat = best == previous;
            previous = best;
            if (best == _model.BlankIndex || repeat) continue;
            string token = _model.Vocabulary[best];
            if (string.IsNullOrWhiteSpace(token))
            {
                FlushWord(step);
                continue;
            }
            if (wordStartStep < 0) wordStartStep = step;
            text.Append(token);
            confidenceSum += probability;
            confidenceCount++;
        }
        FlushWord(steps);
        return new LineResult(decodedWords);

        void FlushWord(int endStep)
        {
            if (text.Length == 0 || wordStartStep < 0)
            {
                text.Clear();
                wordStartStep = -1;
                return;
            }
            int left = bounds.Left + (int)Math.Floor(wordStartStep * pixelsPerStep * sourcePerPixel);
            int right = bounds.Left + (int)Math.Ceiling(endStep * pixelsPerStep * sourcePerPixel);
            left = Math.Clamp(left, bounds.Left, Math.Max(bounds.Left, bounds.Right - 1));
            right = Math.Clamp(right, left + 1, bounds.Right);
            double confidence = confidenceCount == 0 ? 0 : confidenceSum / confidenceCount;
            decodedWords.Add(new DecodedWord(text.ToString(), Math.Clamp(confidence, 0, 1),
                new PdfOcrImageRegion(left, bounds.Top, right, bounds.Bottom)));
            text.Clear();
            confidenceSum = 0;
            confidenceCount = 0;
            wordStartStep = -1;
        }
    }

    private PdfOnnxOcrTensor BuildInput(PdfOcrPreparedImage prepared, PdfOcrImageRegion bounds,
        int drawnWidth, int inputWidth)
    {
        int channels = _model.Channels, height = _model.InputHeight;
        var data = new float[checked(channels * height * inputWidth)];
        ReadOnlySpan<byte> pixels = prepared.Pixels.Span;
        var gray = new float[height * inputWidth];
        Array.Fill(gray, 1f);
        for (int y = 0; y < height; y++)
        {
            int sourceY = bounds.Top + Math.Min(bounds.Height - 1,
                (int)((y + 0.5) * bounds.Height / height));
            int rowOffset = sourceY * prepared.Width;
            for (int x = 0; x < drawnWidth; x++)
            {
                int sourceX = bounds.Left + Math.Min(bounds.Width - 1,
                    (int)((x + 0.5) * bounds.Width / drawnWidth));
                gray[y * inputWidth + x] = pixels[rowOffset + sourceX] / 255f;
            }
        }
        for (int channel = 0; channel < channels; channel++)
        {
            float mean = _model.Mean[channel], scale = _model.Scale[channel];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < inputWidth; x++)
                {
                    float value = (gray[y * inputWidth + x] - mean) / scale;
                    int index = _model.InputLayout == PdfOnnxOcrInputLayout.Nchw
                        ? (channel * height + y) * inputWidth + x
                        : (y * inputWidth + x) * channels + channel;
                    data[index] = value;
                }
        }
        int[] shape = _model.InputLayout == PdfOnnxOcrInputLayout.Nchw
            ? [1, channels, height, inputWidth]
            : [1, height, inputWidth, channels];
        return new PdfOnnxOcrTensor(data, shape);
    }

    private (int Steps, int Classes, Func<int, int, float> Read) ReadOutput(PdfOnnxOcrTensor output)
    {
        int[] shape = output.Shape;
        int steps, classes;
        if (shape.Length == 3)
        {
            int batch = _model.OutputLayout == PdfOnnxOcrOutputLayout.BatchTimeClass ? shape[0] : shape[1];
            if (batch != 1)
                throw new InvalidOperationException("The ONNX model emitted a batch other than one.");
            steps = _model.OutputLayout == PdfOnnxOcrOutputLayout.BatchTimeClass ? shape[1] : shape[0];
            classes = shape[2];
        }
        else if (shape.Length == 2)
        {
            steps = shape[0];
            classes = shape[1];
        }
        else
            throw new InvalidOperationException(
                "The ONNX model output must have two or three dimensions for CTC decoding.");
        float[] data = output.Data;
        return (steps, classes, (step, cls) => data[step * classes + cls]);
    }

    private (int Best, double Probability) ArgMax(int step, int classes,
        Func<int, int, float> read, bool[]? allowed)
    {
        int best = -1;
        float bestValue = float.NegativeInfinity;
        for (int cls = 0; cls < classes; cls++)
        {
            if (allowed is not null && !allowed[cls]) continue;
            float value = read(step, cls);
            if (!float.IsNaN(value) && value > bestValue)
            {
                bestValue = value;
                best = cls;
            }
        }
        if (best < 0) return (_model.BlankIndex, 0);
        if (_model.OutputIsProbability) return (best, Math.Clamp(bestValue, 0, 1));
        double denominator = 0;
        for (int cls = 0; cls < classes; cls++)
        {
            float value = read(step, cls);
            if (!float.IsNaN(value)) denominator += Math.Exp(value - bestValue);
        }
        return (best, denominator <= 0 ? 0 : Math.Clamp(1 / denominator, 0, 1));
    }

    private static byte[] PackRows(ReadOnlyMemory<byte> bgra, int width, int height, int stride)
    {
        int rowBytes = width * 4;
        var pixels = new byte[checked(rowBytes * height)];
        ReadOnlySpan<byte> source = bgra.Span;
        for (int row = 0; row < height; row++)
            source.Slice(row * stride, rowBytes).CopyTo(pixels.AsSpan(row * rowBytes, rowBytes));
        return pixels;
    }

    private sealed record DecodedWord(string Text, double Confidence, PdfOcrImageRegion Bounds);

    private sealed record LineResult(IReadOnlyList<DecodedWord> Words);
}
