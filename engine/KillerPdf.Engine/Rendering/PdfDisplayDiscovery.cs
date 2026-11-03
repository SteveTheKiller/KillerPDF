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

/// <summary>Describes whether a selected display has an unambiguous output association.</summary>
public enum PdfDisplayTargetStatus
{
    /// <summary>No complete output association was established.</summary>
    Unavailable,
    /// <summary>Exactly one active output was associated.</summary>
    SingleTarget,
    /// <summary>Several active outputs share the selected display.</summary>
    MultipleTargets
}

/// <summary>Describes the observed active display color mode.</summary>
public enum PdfDisplayColorMode
{
    /// <summary>The active mode could not be established.</summary>
    Unknown,
    /// <summary>The display reports conventional SDR.</summary>
    ConventionalSdr,
    /// <summary>The display reports Advanced Color SDR.</summary>
    AdvancedColorSdr,
    /// <summary>The display reports active HDR.</summary>
    Hdr
}

/// <summary>Retains active color-mode observations, query provenance and Win32 failures.</summary>
/// <remarks>
/// Raw flags belong to QueryType. A legacy query can preserve facts without identifying
/// an active mode. Reported bit depth does not establish physical output accuracy.
/// </remarks>
public sealed record PdfDisplayColorState(PdfDisplayColorMode Mode, uint? QueryType,
    uint? Flags, uint? ActiveColorMode, uint? ColorEncoding, uint? BitsPerColorChannel,
    int? AdvancedQueryError, int? LegacyQueryError);

/// <summary>Describes the standard display profile association observation.</summary>
public enum PdfDisplayProfileStatus
{
    /// <summary>The association could not be queried completely.</summary>
    Unavailable,
    /// <summary>The successful query returned no profile name.</summary>
    NoProfile,
    /// <summary>The query returned a profile name, without validating its contents.</summary>
    Named,
    /// <summary>The source and target belong to different adapters.</summary>
    CrossAdapterAmbiguous
}

/// <summary>Retains a modern standard-mode ICC association and any HRESULT failure.</summary>
/// <remarks>A returned name does not establish readable profile bytes or a usable transform.</remarks>
public sealed record PdfDisplayProfileAssociation(PdfDisplayProfileStatus Status,
    uint? Scope, string? Name, string? FailureOperation, int? HResult);

/// <summary>Identifies one live output association without promising persistent physical identity.</summary>
public sealed record PdfDisplayTarget(string Identity, string? FriendlyName)
{
    /// <summary>Gets active color observations, or null before they have been queried.</summary>
    public PdfDisplayColorState? ColorState { get; init; }
    /// <summary>Gets the standard-mode ICC association, or null before it has been queried.</summary>
    public PdfDisplayProfileAssociation? StandardProfile { get; init; }
}

/// <summary>Provides an immutable output association and any failed query.</summary>
public sealed class PdfDisplayTargetAssociation
{
    /// <summary>Creates an association with an owned immutable copy of its targets.</summary>
    public PdfDisplayTargetAssociation(PdfDisplayTargetStatus status,
        IEnumerable<PdfDisplayTarget> targets, string? failureOperation = null, int? nativeErrorCode = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        Status = status;
        Targets = Array.AsReadOnly(targets.ToArray());
        FailureOperation = failureOperation;
        NativeErrorCode = nativeErrorCode;
    }

    /// <summary>Gets the completeness and multiplicity of the association.</summary>
    public PdfDisplayTargetStatus Status { get; }
    /// <summary>Gets the observed live outputs.</summary>
    public IReadOnlyList<PdfDisplayTarget> Targets { get; }
    /// <summary>Gets the failed query, when association is unavailable.</summary>
    public string? FailureOperation { get; }
    /// <summary>Gets a Win32 query error, when supplied by the operation.</summary>
    public int? NativeErrorCode { get; }
}

/// <summary>Provides an immutable observation of a viewport's selected display.</summary>
/// <param name="Generation">Changes when the observed selection, bounds or failure changes.</param>
/// <param name="Status">Whether the selected display was observed.</param>
/// <param name="MonitorIdentity">An opaque live association, not a persistent physical identity.</param>
/// <param name="Bounds">The selected display's desktop bounds, when available.</param>
/// <param name="FailureOperation">The failed observation, when unavailable.</param>
/// <param name="NativeErrorCode">A Win32 error code when supplied by the failing operation.</param>
public sealed record PdfDisplayDiscoverySnapshot(long Generation,
    PdfDisplayDiscoveryStatus Status, string? MonitorIdentity, PdfDisplayBounds? Bounds,
    string? FailureOperation, int? NativeErrorCode)
{
    /// <summary>Gets output associations, or null before they have been queried.</summary>
    public PdfDisplayTargetAssociation? TargetAssociation { get; init; }
}
