using KillerPdf.Engine.Rendering;
using System.Runtime.InteropServices;
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
    PdfPresentationStatus _status = new(PdfPresentationMode.Uninitialized,
        PdfMonitorTransformOwner.Unknown, null, null, null);
    PdfPresentationState? _state;
    bool _disposed;

    /// <summary>Creates a presenter for the supplied owned viewport window.</summary>
    public WindowsPdfFramePresenter(nint ownedViewport)
    {
        if (ownedViewport == 0) throw new ArgumentException("An owned viewport window is required.", nameof(ownedViewport));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _ownedViewport = ownedViewport;
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

        using var target = new CompositionTarget();
        var failure = target.Initialize(_ownedViewport);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(new PdfFrameSubmissionResult(PdfFrameSubmissionOutcome.Canceled, Status));
        if (_state is null || submission.Stamp != _state.Stamp)
            return ValueTask.FromResult(new PdfFrameSubmissionResult(PdfFrameSubmissionOutcome.Stale, Status));
        if (failure is { } nativeFailure)
        {
            var status = new PdfPresentationStatus(PdfPresentationMode.Unavailable,
                PdfMonitorTransformOwner.Unknown, null, nativeFailure.Operation, nativeFailure.ErrorCode);
            Volatile.Write(ref _status, status);
            return ValueTask.FromResult(new PdfFrameSubmissionResult(PdfFrameSubmissionOutcome.Unavailable, status));
        }
        throw new NotImplementedException("Windows current-frame native submission is not implemented.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _state = null;
    }

    // Target initialization owns no surfaces or GPU transport. Its references are scoped to one attempt.
    sealed class CompositionTarget : IDisposable
    {
        nint _device;
        nint _target;

        public (string Operation, int ErrorCode)? Initialize(nint viewport)
        {
            Guid deviceId = new("C37EA93A-E7AA-450D-B16F-9746CB0407F3");
            // The Windows SDK marks the DXGI device optional. Target-only creation needs no surface device.
            int hr = DCompositionCreateDevice(0, ref deviceId, out _device);
            if (hr < 0) return ("DCompositionCreateDevice", hr);
            if (_device == 0) throw new InvalidOperationException("DirectComposition returned no device after successful creation.");
            nint method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(_device), 6 * IntPtr.Size);
            var createTarget = Marshal.GetDelegateForFunctionPointer<CreateTargetForHwndDelegate>(method);
            hr = createTarget(_device, viewport, 0, out _target);
            if (hr < 0) return ("CreateTargetForHwnd", hr);
            if (_target == 0) throw new InvalidOperationException("DirectComposition returned no target after successful creation.");
            return null;
        }

        public void Dispose()
        {
            if (_target != 0) { Marshal.Release(_target); _target = 0; }
            if (_device != 0) { Marshal.Release(_device); _device = 0; }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CreateTargetForHwndDelegate(nint device, nint hwnd, int topmost, out nint target);

        [DllImport("dcomp.dll", ExactSpelling = true)]
        static extern int DCompositionCreateDevice(nint dxgiDevice, ref Guid iid, out nint compositionDevice);
    }
}
