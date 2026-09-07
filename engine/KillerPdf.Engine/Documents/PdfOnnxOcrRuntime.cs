using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace KillerPdf.Engine.Documents;

/// <summary>Bounded execution settings for ONNX Runtime OCR sessions.</summary>
public sealed record PdfOnnxOcrRuntimeOptions
{
    /// <summary>Creates runtime options with explicit thread limits.</summary>
    public PdfOnnxOcrRuntimeOptions(int intraOperatorThreads = 1, int interOperatorThreads = 1)
    {
        if (intraOperatorThreads is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(intraOperatorThreads));
        if (interOperatorThreads is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(interOperatorThreads));
        IntraOperatorThreads = intraOperatorThreads;
        InterOperatorThreads = interOperatorThreads;
    }

    /// <summary>Gets the default single-threaded options.</summary>
    public static PdfOnnxOcrRuntimeOptions Default { get; } = new();

    /// <summary>Gets the thread count used inside one operator.</summary>
    public int IntraOperatorThreads { get; }
    /// <summary>Gets the thread count used across independent operators.</summary>
    public int InterOperatorThreads { get; }
}

/// <summary>Creates OCR sessions backed by the ONNX Runtime CPU execution provider.</summary>
public static class PdfOnnxOcrRuntime
{
    private const long MaximumModelBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Loads a model file and validates it against its line-recognition description.</summary>
    public static IPdfOnnxOcrSession CreateSession(string modelPath,
        PdfOnnxOcrModelDescription description, PdfOnnxOcrRuntimeOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(description);
        IPdfOnnxOcrTensorRunner runner = LoadRunner(modelPath, options ?? PdfOnnxOcrRuntimeOptions.Default);
        return new PdfOnnxOcrLineSession(description, runner);
    }

    /// <summary>Creates a selectable provider that loads an isolated session per recognition call.</summary>
    public static PdfOnnxOcrProvider CreateProvider(string modelPath,
        PdfOnnxOcrModelDescription description, PdfOnnxOcrRuntimeOptions? options = null,
        int automaticPriority = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(description);
        var descriptor = new PdfOcrProviderDescriptor("onnx", description.DisplayName,
            description.Version, description.Languages, automaticPriority);
        return new PdfOnnxOcrProvider(descriptor,
            () => CreateSession(modelPath, description, options),
            requested => descriptor.SupportsLanguages(requested.Languages));
    }

    /// <summary>Loads a model file into a runtime-backed tensor runner.</summary>
    public static IPdfOnnxOcrTensorRunner LoadRunner(string modelPath,
        PdfOnnxOcrRuntimeOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        options ??= PdfOnnxOcrRuntimeOptions.Default;
        var file = new FileInfo(modelPath);
        if (!file.Exists)
            throw new FileNotFoundException("The ONNX model file was not found.", modelPath);
        if (file.Length == 0 || file.Length > MaximumModelBytes)
            throw new InvalidDataException("The ONNX model file is empty or larger than 2 GB.");
        var sessionOptions = new SessionOptions
        {
            IntraOpNumThreads = options.IntraOperatorThreads,
            InterOpNumThreads = options.InterOperatorThreads,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        try
        {
            return new RuntimeTensorRunner(new InferenceSession(file.FullName, sessionOptions), sessionOptions);
        }
        catch (OnnxRuntimeException error)
        {
            sessionOptions.Dispose();
            throw new InvalidDataException("ONNX Runtime could not load the model: " + error.Message, error);
        }
        catch
        {
            sessionOptions.Dispose();
            throw;
        }
    }

    private sealed class RuntimeTensorRunner : IPdfOnnxOcrTensorRunner
    {
        private readonly InferenceSession _session;
        private readonly SessionOptions _options;
        private bool _disposed;

        public RuntimeTensorRunner(InferenceSession session, SessionOptions options)
        {
            _session = session;
            _options = options;
            InputNames = Array.AsReadOnly(session.InputMetadata.Keys.ToArray());
            OutputNames = Array.AsReadOnly(session.OutputMetadata.Keys.ToArray());
        }

        public IReadOnlyList<string> InputNames { get; }
        public IReadOnlyList<string> OutputNames { get; }

        public PdfOnnxOcrTensor Run(string inputName, PdfOnnxOcrTensor input, string outputName,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(inputName);
            ArgumentNullException.ThrowIfNull(input);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputName);
            if (!_session.InputMetadata.TryGetValue(inputName, out NodeMetadata? inputMetadata))
                throw new InvalidOperationException($"The ONNX model has no input named '{inputName}'.");
            if (!inputMetadata.IsTensor || inputMetadata.ElementType != typeof(float))
                throw new InvalidOperationException(
                    $"The ONNX input '{inputName}' is not a float tensor; only float inputs are supported.");
            int[] declared = inputMetadata.Dimensions;
            if (declared.Length != input.Shape.Length)
                throw new InvalidOperationException(
                    $"The ONNX input '{inputName}' has rank {declared.Length} but the description builds rank {input.Shape.Length}.");
            for (int index = 0; index < declared.Length; index++)
                if (declared[index] > 0 && declared[index] != input.Shape[index])
                    throw new InvalidOperationException(
                        $"The ONNX input '{inputName}' dimension {index} is fixed at {declared[index]} but the description builds {input.Shape[index]}.");
            cancellationToken.ThrowIfCancellationRequested();
            var tensor = new DenseTensor<float>(input.Data, input.Shape);
            using var runOptions = new RunOptions();
            using CancellationTokenRegistration registration = cancellationToken.Register(
                () => runOptions.Terminate = true);
            try
            {
                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(
                    [NamedOnnxValue.CreateFromTensor(inputName, tensor)], [outputName], runOptions);
                DisposableNamedOnnxValue result = results[0];
                Tensor<float> output = result.AsTensor<float>()
                    ?? throw new InvalidOperationException(
                        $"The ONNX output '{outputName}' is not a float tensor.");
                return new PdfOnnxOcrTensor(output.ToArray(), output.Dimensions.ToArray());
            }
            catch (OnnxRuntimeException error)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("ONNX Runtime inference failed: " + error.Message, error);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _session.Dispose();
            _options.Dispose();
        }
    }
}
