using System.Text.Json;
using System.Text.Json.Serialization;

namespace KillerPdf.Engine.Documents;

/// <summary>The dimension order of an ONNX image input tensor.</summary>
public enum PdfOnnxOcrInputLayout
{
    /// <summary>Batch, channel, height, width.</summary>
    Nchw,
    /// <summary>Batch, height, width, channel.</summary>
    Nhwc
}

/// <summary>The channel content an ONNX OCR model expects for each pixel.</summary>
public enum PdfOnnxOcrChannelOrder
{
    /// <summary>One grayscale channel.</summary>
    Gray,
    /// <summary>Three channels ordered red, green, blue.</summary>
    Rgb,
    /// <summary>Three channels ordered blue, green, red.</summary>
    Bgr
}

/// <summary>The dimension order of a CTC recognition output tensor.</summary>
public enum PdfOnnxOcrOutputLayout
{
    /// <summary>Batch, time step, class.</summary>
    BatchTimeClass,
    /// <summary>Time step, batch, class.</summary>
    TimeBatchClass
}

/// <summary>Describes one CTC text-line recognition model precisely enough to run it.</summary>
/// <remarks>
/// ONNX OCR models do not share one tensor contract. This description records the exact
/// input tensor, pixel normalization, output tensor, and vocabulary one model needs. It only
/// covers single-line CTC recognizers; detection or attention-decoded models need their own
/// contract rather than a guess.
/// </remarks>
public sealed partial class PdfOnnxOcrModelDescription
{
    private const int MaximumVocabulary = 65_536;
    private const int InputHeightLimit = 512;
    private const int InputWidthLimit = 8_192;

    /// <summary>Creates a validated line-recognition model description.</summary>
    public PdfOnnxOcrModelDescription(string displayName, Version version,
        IEnumerable<string> languages, string inputName, PdfOnnxOcrInputLayout inputLayout,
        PdfOnnxOcrChannelOrder channelOrder, int inputHeight, int inputWidth,
        int maximumInputWidth, int widthMultiple, IReadOnlyList<float> mean,
        IReadOnlyList<float> scale, string outputName, PdfOnnxOcrOutputLayout outputLayout,
        bool outputIsProbability, IEnumerable<string> vocabulary, int blankIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);
        ArgumentNullException.ThrowIfNull(mean);
        ArgumentNullException.ThrowIfNull(scale);
        ArgumentNullException.ThrowIfNull(vocabulary);
        if (displayName.Length > 160)
            throw new ArgumentException("The model display name is too long.", nameof(displayName));
        if (!Enum.IsDefined(inputLayout)) throw new ArgumentOutOfRangeException(nameof(inputLayout));
        if (!Enum.IsDefined(channelOrder)) throw new ArgumentOutOfRangeException(nameof(channelOrder));
        if (!Enum.IsDefined(outputLayout)) throw new ArgumentOutOfRangeException(nameof(outputLayout));
        if (inputHeight is < 1 or > InputHeightLimit)
            throw new ArgumentOutOfRangeException(nameof(inputHeight));
        if (inputWidth is < 0 or > InputWidthLimit)
            throw new ArgumentOutOfRangeException(nameof(inputWidth));
        if (maximumInputWidth is < 1 or > InputWidthLimit)
            throw new ArgumentOutOfRangeException(nameof(maximumInputWidth));
        if (inputWidth > maximumInputWidth)
            throw new ArgumentException("The fixed input width exceeds the maximum input width.",
                nameof(inputWidth));
        if (widthMultiple is < 1 or > InputWidthLimit)
            throw new ArgumentOutOfRangeException(nameof(widthMultiple));
        int channels = channelOrder == PdfOnnxOcrChannelOrder.Gray ? 1 : 3;
        if (mean.Count != channels || scale.Count != channels)
            throw new ArgumentException(
                $"Normalization needs exactly {channels} mean and scale values for this channel order.",
                nameof(mean));
        if (mean.Any(value => !float.IsFinite(value)) || scale.Any(value => !float.IsFinite(value) || value == 0))
            throw new ArgumentException("Normalization values must be finite and scales nonzero.",
                nameof(scale));
        string[] tokens = [.. vocabulary];
        if (tokens.Length is < 2 or > MaximumVocabulary)
            throw new ArgumentException(
                "The vocabulary needs at least a blank and one character and no more than 65536 tokens.",
                nameof(vocabulary));
        if (tokens.Any(token => token is null))
            throw new ArgumentException("The vocabulary contains a null token.", nameof(vocabulary));
        if (blankIndex < 0 || blankIndex >= tokens.Length)
            throw new ArgumentOutOfRangeException(nameof(blankIndex));
        string[] normalizedLanguages = [.. languages.Select(language => language?.Trim())
            .Where(language => !string.IsNullOrEmpty(language)).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (normalizedLanguages.Length == 0)
            throw new ArgumentException("At least one model language is required.", nameof(languages));
        DisplayName = displayName;
        Version = version;
        Languages = Array.AsReadOnly(normalizedLanguages);
        InputName = inputName;
        InputLayout = inputLayout;
        ChannelOrder = channelOrder;
        Channels = channels;
        InputHeight = inputHeight;
        InputWidth = inputWidth;
        MaximumInputWidth = maximumInputWidth;
        WidthMultiple = widthMultiple;
        Mean = Array.AsReadOnly(mean.ToArray());
        Scale = Array.AsReadOnly(scale.ToArray());
        OutputName = outputName;
        OutputLayout = outputLayout;
        OutputIsProbability = outputIsProbability;
        Vocabulary = Array.AsReadOnly(tokens);
        BlankIndex = blankIndex;
    }

    /// <summary>Gets the human-readable model name.</summary>
    public string DisplayName { get; }
    /// <summary>Gets the model version.</summary>
    public Version Version { get; }
    /// <summary>Gets the languages the vocabulary covers.</summary>
    public IReadOnlyList<string> Languages { get; }
    /// <summary>Gets the image input tensor name.</summary>
    public string InputName { get; }
    /// <summary>Gets the image input dimension order.</summary>
    public PdfOnnxOcrInputLayout InputLayout { get; }
    /// <summary>Gets the expected pixel channel order.</summary>
    public PdfOnnxOcrChannelOrder ChannelOrder { get; }
    /// <summary>Gets the input channel count implied by the channel order.</summary>
    public int Channels { get; }
    /// <summary>Gets the fixed line height in pixels.</summary>
    public int InputHeight { get; }
    /// <summary>Gets the fixed line width, or zero when the width follows the line.</summary>
    public int InputWidth { get; }
    /// <summary>Gets the widest line the model accepts.</summary>
    public int MaximumInputWidth { get; }
    /// <summary>Gets the multiple variable widths are padded to.</summary>
    public int WidthMultiple { get; }
    /// <summary>Gets the per-channel mean subtracted from unit-range samples.</summary>
    public IReadOnlyList<float> Mean { get; }
    /// <summary>Gets the per-channel divisor applied after mean subtraction.</summary>
    public IReadOnlyList<float> Scale { get; }
    /// <summary>Gets the CTC output tensor name.</summary>
    public string OutputName { get; }
    /// <summary>Gets the CTC output dimension order.</summary>
    public PdfOnnxOcrOutputLayout OutputLayout { get; }
    /// <summary>Gets whether output rows are already probabilities rather than logits.</summary>
    public bool OutputIsProbability { get; }
    /// <summary>Gets the class-index to text mapping, including the blank token.</summary>
    public IReadOnlyList<string> Vocabulary { get; }
    /// <summary>Gets the CTC blank class index.</summary>
    public int BlankIndex { get; }

    /// <summary>Returns the padded input width for one line of the given scaled width.</summary>
    public int ResolveInputWidth(int scaledWidth)
    {
        if (scaledWidth < 1) throw new ArgumentOutOfRangeException(nameof(scaledWidth));
        if (InputWidth > 0) return InputWidth;
        int padded = checked((scaledWidth + WidthMultiple - 1) / WidthMultiple * WidthMultiple);
        return Math.Min(Math.Max(padded, WidthMultiple), MaximumInputWidth);
    }

    /// <summary>Reads a description from its JSON sidecar text.</summary>
    public static PdfOnnxOcrModelDescription FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (json.Length > 4 * 1024 * 1024)
            throw new ArgumentException("The model description is too large.", nameof(json));
        JsonModel model;
        try
        {
            model = JsonSerializer.Deserialize(json, JsonModelContext.Default.JsonModel)
                ?? throw new FormatException("The model description is empty.");
        }
        catch (JsonException error)
        {
            throw new FormatException("The model description is not valid JSON.", error);
        }
        if (model.Format != "killerpdf-onnx-ocr-ctc-line/1")
            throw new FormatException(
                "The model description format is not a supported CTC line-recognition contract.");
        try
        {
            return new PdfOnnxOcrModelDescription(
                model.DisplayName ?? "", ParseVersion(model.Version), model.Languages ?? [],
                model.InputName ?? "", ParseEnum<PdfOnnxOcrInputLayout>(model.InputLayout, "inputLayout"),
                ParseEnum<PdfOnnxOcrChannelOrder>(model.ChannelOrder, "channelOrder"),
                model.InputHeight, model.InputWidth, model.MaximumInputWidth,
                model.WidthMultiple, model.Mean ?? [], model.Scale ?? [], model.OutputName ?? "",
                ParseEnum<PdfOnnxOcrOutputLayout>(model.OutputLayout, "outputLayout"),
                model.OutputIsProbability, model.Vocabulary ?? [], model.BlankIndex);
        }
        catch (ArgumentException error)
        {
            throw new FormatException("The model description has an invalid value: "
                + error.Message, error);
        }
    }

    private static Version ParseVersion(string? text) =>
        Version.TryParse(text, out Version? version)
            ? version : throw new FormatException("The model description version is invalid.");

    private static TEnum ParseEnum<TEnum>(string? text, string field) where TEnum : struct, Enum =>
        Enum.TryParse(text, ignoreCase: true, out TEnum value) && Enum.IsDefined(value)
            ? value : throw new FormatException($"The model description '{field}' value is invalid.");

    private sealed class JsonModel
    {
        public string? Format { get; set; }
        public string? DisplayName { get; set; }
        public string? Version { get; set; }
        public string[]? Languages { get; set; }
        public string? InputName { get; set; }
        public string? InputLayout { get; set; }
        public string? ChannelOrder { get; set; }
        public int InputHeight { get; set; }
        public int InputWidth { get; set; }
        public int MaximumInputWidth { get; set; }
        public int WidthMultiple { get; set; } = 1;
        public float[]? Mean { get; set; }
        public float[]? Scale { get; set; }
        public string? OutputName { get; set; }
        public string? OutputLayout { get; set; }
        public bool OutputIsProbability { get; set; }
        public string[]? Vocabulary { get; set; }
        public int BlankIndex { get; set; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true, AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip)]
    [JsonSerializable(typeof(JsonModel))]
    private sealed partial class JsonModelContext : JsonSerializerContext;
}
