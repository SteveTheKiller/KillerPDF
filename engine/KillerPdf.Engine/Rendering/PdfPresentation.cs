namespace KillerPdf.Engine.Rendering;

/// <summary>Identifies the monitor conversion owner for a presentation surface.</summary>
public enum PdfMonitorTransformOwner
{
    /// <summary>No monitor conversion owner has been established.</summary>
    Unknown,
    /// <summary>The application owns monitor conversion.</summary>
    Application,
    /// <summary>The compositor owns monitor conversion.</summary>
    Compositor
}

/// <summary>Describes the active presentation transport.</summary>
public enum PdfPresentationMode
{
    /// <summary>The transport has not been initialized.</summary>
    Uninitialized,
    /// <summary>No presentation transport is available.</summary>
    Unavailable,
    /// <summary>The transport uses the compatibility SDR representation.</summary>
    CompatibilitySdr,
    /// <summary>The transport uses a linear floating-point representation.</summary>
    LinearFloat
}

/// <summary>Describes what happened to one frame submission.</summary>
public enum PdfFrameSubmissionOutcome
{
    /// <summary>Native submission succeeded. This does not establish display or physical color accuracy.</summary>
    Submitted,
    /// <summary>The frame does not belong to the current presentation state.</summary>
    Stale,
    /// <summary>The submission was canceled.</summary>
    Canceled,
    /// <summary>The presentation transport is unavailable.</summary>
    Unavailable
}

/// <summary>Identifies the document, generations, request and page region to which a frame belongs.</summary>
/// <param name="DocumentId">The document identity.</param>
/// <param name="DocumentRevision">The document revision.</param>
/// <param name="RenderGeneration">The render generation.</param>
/// <param name="ProofGeneration">The proof configuration generation.</param>
/// <param name="DisplayGeneration">The display configuration generation.</param>
/// <param name="SurfaceGeneration">The presentation surface generation.</param>
/// <param name="RequestSequence">The render request sequence.</param>
/// <param name="PageIndex">The zero-based page index.</param>
/// <param name="RegionId">The page region identity.</param>
public sealed record PdfPresentationStamp(Guid DocumentId, long DocumentRevision,
    long RenderGeneration, long ProofGeneration, long DisplayGeneration,
    long SurfaceGeneration, long RequestSequence, int PageIndex, long RegionId);

/// <summary>Describes the current frame identity and presentation surface configuration.</summary>
/// <param name="Stamp">The currently expected frame identity.</param>
/// <param name="SurfacePixelWidth">The surface width in pixels.</param>
/// <param name="SurfacePixelHeight">The surface height in pixels.</param>
/// <param name="ReferenceWhiteScale">The reference-white scale for presentation.</param>
/// <param name="TransformOwner">The monitor conversion owner.</param>
public sealed record PdfPresentationState(PdfPresentationStamp Stamp,
    int SurfacePixelWidth, int SurfacePixelHeight, double ReferenceWhiteScale,
    PdfMonitorTransformOwner TransformOwner);

/// <summary>Pairs an owned rendered frame with its presentation identity.</summary>
/// <param name="Frame">The actual rendered frame.</param>
/// <param name="Stamp">The frame's presentation identity.</param>
public sealed record PdfFrameSubmission(PdfRenderedFrame Frame, PdfPresentationStamp Stamp);

/// <summary>Provides an immutable snapshot of the presentation transport's status.</summary>
/// <param name="Mode">The active transport mode.</param>
/// <param name="TransformOwner">The active monitor conversion owner.</param>
/// <param name="LastAcceptedStamp">The last natively submitted frame identity, when available.</param>
/// <param name="FailureOperation">The last failed native operation, when available.</param>
/// <param name="NativeErrorCode">The native failure code, when available.</param>
public sealed record PdfPresentationStatus(PdfPresentationMode Mode,
    PdfMonitorTransformOwner TransformOwner, PdfPresentationStamp? LastAcceptedStamp,
    string? FailureOperation, int? NativeErrorCode);

/// <summary>Returns the submission outcome and an immutable transport status snapshot.</summary>
/// <param name="Outcome">The result of the submission attempt.</param>
/// <param name="Status">The resulting transport status.</param>
public sealed record PdfFrameSubmissionResult(PdfFrameSubmissionOutcome Outcome,
    PdfPresentationStatus Status);

/// <summary>Presents owned frames through a platform-specific transport without exposing operating-system types.</summary>
public interface IPdfFramePresenter : IDisposable
{
    /// <summary>Gets the current immutable transport status.</summary>
    PdfPresentationStatus Status { get; }

    /// <summary>Updates the expected frame identity and surface configuration.</summary>
    void UpdateState(PdfPresentationState state);

    /// <summary>Submits a frame. Submitted means native submission, not verified display or physical accuracy.</summary>
    ValueTask<PdfFrameSubmissionResult> SubmitAsync(PdfFrameSubmission submission,
        CancellationToken cancellationToken = default);
}
