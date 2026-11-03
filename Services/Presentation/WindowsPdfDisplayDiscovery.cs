using KillerPdf.Engine.Rendering;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace KillerPDF.Presentation;

/// <summary>Observes the selected display for a caller-owned viewport window.</summary>
/// <remarks>
/// Construct, capture and dispose on the viewport's owning dispatcher thread.
/// Status is immutable and may be read from any thread. The caller retains its window.
/// </remarks>
public sealed class WindowsPdfDisplayDiscovery : IDisposable
{
    readonly Dispatcher _dispatcher;
    readonly nint _ownedViewport;
    PdfDisplayDiscoverySnapshot _status = new(0, PdfDisplayDiscoveryStatus.Uninitialized,
        null, null, null, null);
    bool _disposed;

    /// <summary>Creates discovery for a nonzero caller-owned viewport window.</summary>
    public WindowsPdfDisplayDiscovery(nint ownedViewport)
    {
        if (ownedViewport == 0)
            throw new ArgumentException("An owned viewport window is required.", nameof(ownedViewport));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _ownedViewport = ownedViewport;
    }

    /// <summary>Gets the latest immutable observation.</summary>
    public PdfDisplayDiscoverySnapshot Status => Volatile.Read(ref _status);

    /// <summary>Captures the viewport's selected display without changing display settings.</summary>
    public PdfDisplayDiscoverySnapshot Capture(CancellationToken cancellationToken = default)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        nint monitor = MonitorFromWindow(_ownedViewport, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = string.Empty };
        PdfDisplayDiscoverySnapshot observed;
        if (monitor == 0)
            observed = new(Status.Generation, PdfDisplayDiscoveryStatus.Unavailable,
                null, null, "MonitorFromWindow", null);
        else if (!GetMonitorInfoW(monitor, ref info)
            || string.IsNullOrWhiteSpace(info.Device)
            || info.Monitor.Right <= info.Monitor.Left || info.Monitor.Bottom <= info.Monitor.Top)
            observed = new(Status.Generation, PdfDisplayDiscoveryStatus.Unavailable,
                null, null, "GetMonitorInfoW", null);
        else
            observed = new(Status.Generation, PdfDisplayDiscoveryStatus.Available, info.Device,
                new(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom), null, null);
        cancellationToken.ThrowIfCancellationRequested();
        var previous = Status;
        if (observed == previous) return previous;
        observed = observed with { Generation = checked(previous.Generation + 1) };
        Volatile.Write(ref _status, observed);
        return observed;
    }

    /// <summary>Ends discovery without destroying the caller-owned window.</summary>
    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        _disposed = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DesktopRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MonitorInfo
    {
        public uint Size;
        public DesktopRect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
}
