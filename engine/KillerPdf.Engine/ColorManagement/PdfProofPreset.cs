using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.ColorManagement;

/// <summary>Immutable requested proof preferences referencing an ICC profile by content.</summary>
public sealed class PdfProofPreset
{
    private PdfProofPreset(string name, string? profile, bool enabled, bool gamutWarnings, PdfProofOptions options)
    {
        Name = name;
        PrinterProfileSha256 = profile;
        Enabled = enabled;
        GamutWarnings = gamutWarnings;
        Options = options;
    }

    /// <summary>Gets the preset's display name.</summary>
    public string Name { get; }
    /// <summary>Gets the selected profile's content identity, or null when no profile was selected.</summary>
    public string? PrinterProfileSha256 { get; }
    /// <summary>Gets whether proofing is requested.</summary>
    public bool Enabled { get; }
    /// <summary>Gets whether printer-gamut warnings are requested.</summary>
    public bool GamutWarnings { get; }
    /// <summary>Gets the requested intent and simulation choices.</summary>
    public PdfProofOptions Options { get; }

    /// <summary>Captures immutable requested settings without bundling the selected profile's bytes.</summary>
    public static PdfProofPreset Capture(string name, PdfDocumentProofSettings settings)
    {
        if (!IsValidName(name))
            throw new ArgumentException("A proof preset name must contain 1 to 128 characters without controls.", nameof(name));
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.Options.RenderingIntent))
            throw new ArgumentOutOfRangeException(nameof(settings), "The proof rendering intent is invalid.");
        return new(name, settings.PrinterProfile?.ContentSha256, settings.Enabled, settings.GamutWarnings, settings.Options);
    }

    /// <summary>Writes the preset schema and profile content reference.</summary>
    public string ToJson(bool indented = false)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("name", Name);
            writer.WriteStartObject("proof");
            writer.WriteBoolean("enabled", Enabled);
            writer.WriteString("printerProfileSha256", PrinterProfileSha256);
            writer.WriteString("renderingIntent", Options.RenderingIntent switch
            {
                PdfRenderingIntent.Perceptual => "perceptual",
                PdfRenderingIntent.RelativeColorimetric => "relativeColorimetric",
                PdfRenderingIntent.Saturation => "saturation",
                PdfRenderingIntent.AbsoluteColorimetric => "absoluteColorimetric",
                _ => throw new ArgumentOutOfRangeException(nameof(Options.RenderingIntent))
            });
            writer.WriteBoolean("blackPointCompensation", Options.BlackPointCompensation);
            writer.WriteBoolean("simulatePaper", Options.SimulatePaper);
            writer.WriteBoolean("simulateBlack", Options.SimulateBlack);
            writer.WriteBoolean("gamutWarnings", GamutWarnings);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Reads a versioned proof preset without loading any external profile.</summary>
    public static PdfProofPreset FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > 1_048_576 || Encoding.UTF8.GetByteCount(json) > 1_048_576)
            throw new FormatException("The proof preset exceeds its 1 MiB UTF-8 limit.");
        try
        {
            return ReadPreset(json);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new FormatException("The proof preset is malformed or has invalid settings.");
        }
    }

    private static PdfProofPreset ReadPreset(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        ValidateProperties(root, "schemaVersion", "name", "proof");
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
            throw new NotSupportedException("The proof preset schema version is unsupported.");
        var proof = root.GetProperty("proof");
        ValidateProperties(proof, "enabled", "printerProfileSha256", "renderingIntent",
            "blackPointCompensation", "simulatePaper", "simulateBlack", "gamutWarnings");
        var hash = proof.GetProperty("printerProfileSha256");
        string? reference = hash.ValueKind == JsonValueKind.Null ? null : hash.GetString();
        if (reference is not null && (reference.Length != 64 || reference.Any(character => !char.IsAsciiHexDigit(character))))
            throw new FormatException("A proof profile reference must be a SHA256 content digest.");
        var name = root.GetProperty("name").GetString();
        if (!IsValidName(name))
            throw new FormatException("A proof preset name must contain 1 to 128 characters without controls.");
        return new(name!,
            reference?.ToUpperInvariant(),
            proof.GetProperty("enabled").GetBoolean(), proof.GetProperty("gamutWarnings").GetBoolean(),
            new PdfProofOptions
            {
                RenderingIntent = proof.GetProperty("renderingIntent").GetString() switch
                {
                    "perceptual" => PdfRenderingIntent.Perceptual,
                    "relativeColorimetric" => PdfRenderingIntent.RelativeColorimetric,
                    "saturation" => PdfRenderingIntent.Saturation,
                    "absoluteColorimetric" => PdfRenderingIntent.AbsoluteColorimetric,
                    _ => throw new FormatException("The proof preset rendering intent is invalid.")
                },
                BlackPointCompensation = proof.GetProperty("blackPointCompensation").GetBoolean(),
                SimulatePaper = proof.GetProperty("simulatePaper").GetBoolean(),
                SimulateBlack = proof.GetProperty("simulateBlack").GetBoolean()
            });
    }

    private static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 128 && !name.Any(char.IsControl);

    private static void ValidateProperties(JsonElement element, params string[] names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new FormatException("The proof preset has duplicate or unrecognized settings.");
        }
    }
}
