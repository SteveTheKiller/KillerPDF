namespace KillerPdf.Engine.Rendering;

/// <summary>Describes discovery of the display selected for a viewport.</summary>
public enum PdfDisplayDiscoveryStatus
{
    /// <summary>No observation has been made.</summary>
    Uninitialized,
    /// <summary>The selected display could not be observed.</summary>
    Unavailable,
    /// <summary>The selected display and its desktop bounds were observed.</summary>
    Available
}

/// <summary>Describes a display rectangle in desktop coordinates.</summary>
public sealed record PdfDisplayBounds(int Left, int Top, int Right, int Bottom);

/// <summary>Provides an immutable observation of a viewport's selected display.</summary>
/// <param name="Generation">Changes when the observed selection, bounds or failure changes.</param>
/// <param name="Status">Whether the selected display was observed.</param>
/// <param name="MonitorIdentity">An opaque live association, not a persistent physical identity.</param>
/// <param name="Bounds">The selected display's desktop bounds, when available.</param>
/// <param name="FailureOperation">The failed observation, when unavailable.</param>
/// <param name="NativeErrorCode">A Win32 error code when supplied by the failing operation.</param>
public sealed record PdfDisplayDiscoverySnapshot(long Generation,
    PdfDisplayDiscoveryStatus Status, string? MonitorIdentity, PdfDisplayBounds? Bounds,
    string? FailureOperation, int? NativeErrorCode);
