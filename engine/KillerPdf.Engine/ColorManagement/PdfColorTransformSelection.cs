using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.ColorManagement;

/// <summary>The direction through an ICC profile's device and connection spaces.</summary>
public enum PdfColorTransformDirection
{
    /// <summary>Device components to D50 XYZ connection values.</summary>
    DeviceToConnection,
    /// <summary>D50 XYZ connection values to device components.</summary>
    ConnectionToDevice
}

/// <summary>Describes the transform selected by the engine for an ICC profile.</summary>
public sealed class PdfColorTransformSelection
{
    internal PdfColorTransformSelection(PdfRenderingIntent requestedIntent, PdfColorTransformDirection direction,
        bool isAvailable, bool isMatrixTrc, string? tableSignature, PdfRenderingIntent? effectiveTableIntent,
        bool usesIntentFallback, bool appliesMediaWhiteScaling)
    {
        RequestedIntent = requestedIntent;
        Direction = direction;
        IsAvailable = isAvailable;
        IsMatrixTrc = isMatrixTrc;
        TableSignature = tableSignature;
        EffectiveTableIntent = effectiveTableIntent;
        UsesIntentFallback = usesIntentFallback;
        AppliesMediaWhiteScaling = appliesMediaWhiteScaling;
    }

    /// <summary>Gets the requested conversion intent.</summary>
    public PdfRenderingIntent RequestedIntent { get; }
    /// <summary>Gets the requested conversion direction.</summary>
    public PdfColorTransformDirection Direction { get; }
    /// <summary>Gets whether the engine can perform this conversion.</summary>
    public bool IsAvailable { get; }
    /// <summary>Gets whether the selected path uses matrix and tone-response curves.</summary>
    public bool IsMatrixTrc { get; }
    /// <summary>Gets the selected A2B or B2A tag, or null when no table is selected.</summary>
    public string? TableSignature { get; }
    /// <summary>Gets the intent of the selected table, separate from media-white scaling.</summary>
    public PdfRenderingIntent? EffectiveTableIntent { get; }
    /// <summary>Gets whether the selected table differs from the requested intent's table.</summary>
    public bool UsesIntentFallback { get; }
    /// <summary>Gets whether this transform applies media-white scaling.</summary>
    public bool AppliesMediaWhiteScaling { get; }
}
