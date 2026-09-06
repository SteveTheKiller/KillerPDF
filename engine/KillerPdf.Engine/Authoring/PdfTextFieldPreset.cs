using System.Text.Json;
using System.Text.Json.Serialization;

namespace KillerPdf.Engine.Authoring;

/// <summary>A reusable fillable text-field size and visual style.</summary>
public sealed record PdfTextFieldPreset
{
    /// <summary>Creates a validated text-field preset.</summary>
    public PdfTextFieldPreset(string name, double width, double height, double fontSize,
        PdfFormFieldAppearanceStyle? appearanceStyle = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A text-field preset name is required.", nameof(name));
        if (!double.IsFinite(width) || width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        AppearanceStyle = Validate(appearanceStyle ?? new PdfFormFieldAppearanceStyle());
        Name = name.Trim();
        Width = width;
        Height = height;
        FontSize = fontSize;
    }

    /// <summary>Gets the preset name.</summary>
    public string Name { get; }
    /// <summary>Gets the field width in PDF points.</summary>
    public double Width { get; }
    /// <summary>Gets the field height in PDF points.</summary>
    public double Height { get; }
    /// <summary>Gets the field font size in points.</summary>
    public double FontSize { get; }
    /// <summary>Gets the field colors and border style.</summary>
    public PdfFormFieldAppearanceStyle AppearanceStyle { get; }

    private static PdfFormFieldAppearanceStyle Validate(PdfFormFieldAppearanceStyle style)
    {
        if (!double.IsFinite(style.BorderWidth) || style.BorderWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(style),
                "The preset border width must be nonnegative.");
        if (!Enum.IsDefined(style.BorderStyle))
            throw new ArgumentOutOfRangeException(nameof(style),
                "The preset border style is not defined.");
        if (style.DashPattern is not null && (style.DashPattern.Count == 0
            || style.DashPattern.Any(value => !double.IsFinite(value) || value <= 0)))
            throw new ArgumentException(
                "Preset border dash lengths must be positive.", nameof(style));
        return style with
        {
            DashPattern = style.DashPattern is null
                ? null : Array.AsReadOnly(style.DashPattern.ToArray())
        };
    }
}

/// <summary>A locally persisted ordered collection of text-field presets.</summary>
public sealed partial class PdfTextFieldPresetCollection
{
    private static readonly PdfTextFieldPresetJsonContext CompactJson = new(Options(false));
    private static readonly PdfTextFieldPresetJsonContext IndentedJson = new(Options(true));
    private readonly PdfTextFieldPreset[] _presets;

    /// <summary>Creates a collection with unique preset names.</summary>
    public PdfTextFieldPresetCollection(IEnumerable<PdfTextFieldPreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        _presets = presets.ToArray();
        if (_presets.Any(preset => preset is null))
            throw new ArgumentException("A preset cannot be null.", nameof(presets));
        if (_presets.Select(preset => preset.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != _presets.Length)
            throw new ArgumentException("Text-field preset names must be unique.", nameof(presets));
        Presets = Array.AsReadOnly(_presets);
    }

    /// <summary>Gets presets in menu order.</summary>
    public IReadOnlyList<PdfTextFieldPreset> Presets { get; }

    /// <summary>Returns a copy with one preset renamed.</summary>
    public PdfTextFieldPresetCollection Rename(string name, string replacement)
    {
        int index = Find(name);
        PdfTextFieldPreset[] changed = (PdfTextFieldPreset[])_presets.Clone();
        PdfTextFieldPreset preset = changed[index];
        changed[index] = new PdfTextFieldPreset(replacement, preset.Width, preset.Height,
            preset.FontSize, preset.AppearanceStyle);
        return new PdfTextFieldPresetCollection(changed);
    }

    /// <summary>Returns a copy with a preset added at the end.</summary>
    public PdfTextFieldPresetCollection Add(PdfTextFieldPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return new PdfTextFieldPresetCollection([.. _presets, preset]);
    }

    /// <summary>Returns a copy with a named preset replaced in place.</summary>
    public PdfTextFieldPresetCollection Replace(
        string name, PdfTextFieldPreset replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        int index = Find(name);
        PdfTextFieldPreset[] changed = (PdfTextFieldPreset[])_presets.Clone();
        changed[index] = replacement;
        return new PdfTextFieldPresetCollection(changed);
    }

    /// <summary>Returns a copy without the named preset.</summary>
    public PdfTextFieldPresetCollection Remove(string name)
    {
        int index = Find(name);
        return new PdfTextFieldPresetCollection(_presets.Where(
            (_, presetIndex) => presetIndex != index));
    }

    /// <summary>Returns a copy with one preset moved to a new menu position.</summary>
    public PdfTextFieldPresetCollection Move(int fromIndex, int toIndex)
    {
        if ((uint)fromIndex >= (uint)_presets.Length)
            throw new ArgumentOutOfRangeException(nameof(fromIndex));
        if ((uint)toIndex >= (uint)_presets.Length)
            throw new ArgumentOutOfRangeException(nameof(toIndex));
        PdfTextFieldPreset[] values = (PdfTextFieldPreset[])_presets.Clone();
        PdfTextFieldPreset moved = values[fromIndex];
        if (fromIndex < toIndex)
            Array.Copy(values, fromIndex + 1, values, fromIndex, toIndex - fromIndex);
        else if (fromIndex > toIndex)
            Array.Copy(values, toIndex, values, toIndex + 1, fromIndex - toIndex);
        values[toIndex] = moved;
        return new PdfTextFieldPresetCollection(values);
    }

    /// <summary>Serializes the local preset collection.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(
        new PresetFile(1, _presets),
        indented ? IndentedJson.PresetFile : CompactJson.PresetFile);

    /// <summary>Reads and validates a local preset collection.</summary>
    public static PdfTextFieldPresetCollection FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        PresetFile file = JsonSerializer.Deserialize(json, CompactJson.PresetFile)
            ?? throw new JsonException("The text-field preset file is empty.");
        if (file.Version != 1)
            throw new NotSupportedException(
                $"Text-field preset version {file.Version} is not supported.");
        return new PdfTextFieldPresetCollection(file.Presets
            ?? throw new JsonException("The text-field preset file has no presets."));
    }

    private int Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A text-field preset name is required.", nameof(name));
        int index = Array.FindIndex(_presets, preset => string.Equals(
            preset.Name, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : throw new KeyNotFoundException(
            $"Text-field preset '{name}' was not found.");
    }

    private static JsonSerializerOptions Options(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = indented
        };
        options.Converters.Add(
            new JsonStringEnumConverter<PdfFormFieldBorderStyle>(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record PresetFile(int Version, PdfTextFieldPreset[]? Presets);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(PresetFile))]
    private sealed partial class PdfTextFieldPresetJsonContext : JsonSerializerContext;
}
