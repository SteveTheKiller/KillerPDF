using KillerPdf.Engine.Rendering;
using System.Windows.Threading;

namespace KillerPDF.Presentation;

/// <summary>Owns the Windows presentation transport for an existing viewport window.</summary>
/// <remarks>
/// Construct this presenter on the viewport's owning dispatcher thread. UpdateState,
/// SubmitAsync and Dispose require that same thread. Immutable Status may be read from any thread.
/// </remarks>
public sealed class WindowsPdfFramePresenter : IPdfFramePresenter
{
    readonly nint _ownedViewport;
    readonly Dispatcher _dispatcher;
    readonly long _maximumCombinedPixelBytes;
    PdfPresentationStatus _status = new(PdfPresentationMode.Uninitialized,
        PdfMonitorTransformOwner.Unknown, null, null, null);
    PdfPresentationState? _state;
    WindowsPdfNativeTransport? _transport;
    bool _disposed;

    /// <summary>Creates a presenter for the supplied owned viewport window.</summary>
    /// <param name="ownedViewport">The caller-owned viewport window.</param>
    /// <param name="maximumCombinedPixelBytes">
    /// A positive pixel-storage budget capped at 512 MiB. Each pixel counts 40 bytes:
    /// 16 for the input, 8 for the Half upload and 16 for two native FP16 buffers.
    /// This policy does not measure total process memory or driver allocation overhead.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The pixel budget is outside the supported range.</exception>
    public WindowsPdfFramePresenter(nint ownedViewport,
        long maximumCombinedPixelBytes = PdfRenderedFrame.MaximumPixelBytes)
    {
        if (ownedViewport == 0) throw new ArgumentException("An owned viewport window is required.", nameof(ownedViewport));
        if (maximumCombinedPixelBytes <= 0 || maximumCombinedPixelBytes > PdfRenderedFrame.MaximumPixelBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumCombinedPixelBytes));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _ownedViewport = ownedViewport;
        _maximumCombinedPixelBytes = maximumCombinedPixelBytes;
    }

    /// <inheritdoc />
    public PdfPresentationStatus Status => Volatile.Read(ref _status);

    /// <inheritdoc />
    public void UpdateState(PdfPresentationState state)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <inheritdoc />
    public ValueTask<PdfFrameSubmissionResult> SubmitAsync(PdfFrameSubmission submission,
        CancellationToken cancellationToken = default)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(submission);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(new PdfFrameSubmissionResult(PdfFrameSubmissionOutcome.Canceled, Status));
        if (_state is null)
            throw new InvalidOperationException("Presentation state must be set before submitting a frame.");
        if (submission.Stamp != _state.Stamp)
            return ValueTask.FromResult(new PdfFrameSubmissionResult(PdfFrameSubmissionOutcome.Stale, Status));

        var state = _state;
        ArgumentNullException.ThrowIfNull(submission.Frame);
        if (state.TransformOwner != PdfMonitorTransformOwner.Compositor)
            throw new NotSupportedException("Linear presentation requires negotiated compositor conversion ownership.");
        if (submission.Frame.ColorEncoding != PdfFrameColorEncoding.LinearSrgbD65)
            throw new NotSupportedException("Linear presentation requires a linear sRGB D65 frame.");
        if (state.SurfacePixelWidth <= 0 || state.SurfacePixelHeight <= 0
            || state.SurfacePixelWidth > 16384 || state.SurfacePixelHeight > 16384)
            throw new ArgumentOutOfRangeException(nameof(state), "The surface dimensions exceed the supported native texture range.");
        if (submission.Frame.Width != state.SurfacePixelWidth || submission.Frame.Height != state.SurfacePixelHeight)
            throw new ArgumentException("Frame and surface dimensions must agree.", nameof(submission));
        if (!double.IsFinite(state.ReferenceWhiteScale) || state.ReferenceWhiteScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(state), "Reference white must be finite and positive.");

        // Per pixel: RGBA32F input (16), Half upload (8), two native FP16 buffers (16).
        if ((long)submission.Frame.Width * submission.Frame.Height > _maximumCombinedPixelBytes / 40)
            throw new ArgumentException("The frame exceeds the combined input, upload and native pixel budget.", nameof(submission));
        Half[] pixels;
        try { pixels = submission.Frame.ToPremultipliedLinearHalfRgba(state.ReferenceWhiteScale, cancellationToken: cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return ValueTask.FromResult(new PdfFrameSubmissionResult(PdfFrameSubmissionOutcome.Canceled, Status)); }
        var rejected = RejectChangedSubmission(submission.Stamp, cancellationToken);
        if (rejected != null) return ValueTask.FromResult(rejected);

        var transport = _transport ?? new WindowsPdfNativeTransport(state.SurfacePixelWidth, state.SurfacePixelHeight);
        try
        {
            if (_transport == null)
            {
                transport.Initialize(_ownedViewport);
                rejected = RejectChangedSubmission(submission.Stamp, cancellationToken);
                if (rejected != null) { transport.Dispose(); return ValueTask.FromResult(rejected); }
                _transport = transport;
            }
            if (transport.Width != state.SurfacePixelWidth || transport.Height != state.SurfacePixelHeight)
            {
                transport.Resize(state.SurfacePixelWidth, state.SurfacePixelHeight);
                rejected = RejectChangedSubmission(submission.Stamp, cancellationToken);
                if (rejected != null) return ValueTask.FromResult(rejected);
            }
            transport.Upload(pixels);
            rejected = RejectChangedSubmission(submission.Stamp, cancellationToken);
            if (rejected != null) return ValueTask.FromResult(rejected);
            transport.AttachRoot();
            rejected = RejectChangedSubmission(submission.Stamp, cancellationToken);
            if (rejected != null) return ValueTask.FromResult(rejected);
            transport.Present();
            rejected = RejectChangedSubmission(submission.Stamp, cancellationToken);
            if (rejected != null) return ValueTask.FromResult(rejected);
            transport.Commit();
            rejected = RejectChangedSubmission(submission.Stamp, cancellationToken);
            if (rejected != null) return ValueTask.FromResult(rejected);
            var status = new PdfPresentationStatus(PdfPresentationMode.LinearFloat,
                PdfMonitorTransformOwner.Compositor, submission.Stamp, null, null);
            Volatile.Write(ref _status, status);
            return ValueTask.FromResult(new PdfFrameSubmissionResult(PdfFrameSubmissionOutcome.Submitted, status));
        }
        catch (WindowsPdfNativeTransport.NativeFailure failure)
        {
            ReleaseTransport(transport);
            rejected = RejectChangedSubmission(submission.Stamp, cancellationToken);
            if (rejected != null) return ValueTask.FromResult(rejected);
            var status = new PdfPresentationStatus(PdfPresentationMode.Unavailable,
                PdfMonitorTransformOwner.Unknown, null, failure.Operation, failure.HResult);
            Volatile.Write(ref _status, status);
            return ValueTask.FromResult(new PdfFrameSubmissionResult(PdfFrameSubmissionOutcome.Unavailable, status));
        }
        catch { ReleaseTransport(transport); throw; }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _state = null;
        ReleaseTransport(_transport);
    }

    PdfFrameSubmissionResult? RejectChangedSubmission(PdfPresentationStamp stamp, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cancellationToken.IsCancellationRequested)
            return new(PdfFrameSubmissionOutcome.Canceled, Status);
        if (_state is null || stamp != _state.Stamp)
            return new(PdfFrameSubmissionOutcome.Stale, Status);
        return null;
    }

    void ReleaseTransport(WindowsPdfNativeTransport? transport)
    {
        if (ReferenceEquals(_transport, transport)) _transport = null;
        transport?.Dispose();
    }
}
