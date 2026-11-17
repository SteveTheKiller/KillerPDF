using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KillerPDF.Controls;

public partial class PdfViewer
{
    private string? _savedStateIdentity;
    private static readonly JsonSerializerOptions StateJson = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private static readonly JsonSerializerOptions StampStateJson = new(StateJson) { IncludeFields = true };

    internal void CaptureSavedCheckpoint(StampSpec? stamps) => _savedStateIdentity = CurrentStateIdentity(stamps);

    internal bool MatchesSavedCheckpoint(StampSpec? stamps) => _savedStateIdentity is { } saved
        && CurrentStateIdentity(stamps) is { } current && saved == current;

    private string? CurrentStateIdentity(StampSpec? stamps)
    {
        if (_doc is null) return null;
        try
        {
            using var hash = SHA256.Create();
            using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                writer.WriteStringValue(_doc.ContentIdentity);
                writer.WriteStartArray();
                foreach (var page in _annotations.Where(pair => pair.Value.Count != 0).OrderBy(pair => pair.Key))
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(page.Key);
                    foreach (var annotation in page.Value)
                    {
                        writer.WriteStringValue(annotation.GetType().Name);
                        JsonSerializer.Serialize(writer, annotation, annotation.GetType(), StateJson);
                    }
                    // These dimensions map editable overlay coordinates into the saved page.
                    // Raster caches for pages without burned overlays cannot affect saved bytes.
                    if (page.Value.Any(annotation => annotation is not MeasurementAnnotation))
                    {
                        if (_renderDims.TryGetValue(page.Key, out var dimensions))
                        {
                            writer.WriteNumberValue(dimensions.w);
                            writer.WriteNumberValue(dimensions.h);
                        }
                        else writer.WriteNullValue();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                JsonSerializer.Serialize(writer, _pageRotations.OrderBy(pair => pair.Key), StateJson);
                JsonSerializer.Serialize(writer, _formTextValues.OrderBy(pair => pair.Key, StringComparer.Ordinal), StateJson);
                JsonSerializer.Serialize(writer, _formChoiceValues.OrderBy(pair => pair.Key, StringComparer.Ordinal), StateJson);
                JsonSerializer.Serialize(writer, _formMultiChoiceValues.OrderBy(pair => pair.Key, StringComparer.Ordinal), StateJson);
                JsonSerializer.Serialize(writer, _formCheckValues.OrderBy(pair => pair.Key, StringComparer.Ordinal), StateJson);
                JsonSerializer.Serialize(writer, _formRadioValues.OrderBy(pair => pair.Key, StringComparer.Ordinal), StateJson);
                JsonSerializer.Serialize(writer, _formFontSizes.OrderBy(pair => pair.Key, StringComparer.Ordinal), StateJson);
                JsonSerializer.Serialize(writer, _formTextStyles.OrderBy(pair => pair.Key, StringComparer.Ordinal), StateJson);
                JsonSerializer.Serialize(writer, _redactMarks, StateJson);
                JsonSerializer.Serialize(writer, stamps, StampStateJson);
                if (stamps is { WmEnabled: true, WmIsImage: true })
                {
                    using var image = File.OpenRead(stamps.WmImagePath
                        ?? throw new IOException("The watermark image has no source."));
                    writer.WriteStringValue(Convert.ToHexString(SHA256.HashData(image)));
                }
                writer.WriteEndArray();
            }
            stream.FlushFinalBlock();
            return Convert.ToHexString(hash.Hash!);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // An unverified state must never authorize reuse of a previously saved file.
            return null;
        }
    }
}
