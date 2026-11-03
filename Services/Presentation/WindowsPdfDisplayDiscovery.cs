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
                new(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom), null, null)
                { TargetAssociation = CaptureTargets(info.Device, cancellationToken) };
        cancellationToken.ThrowIfCancellationRequested();
        var previous = Status;
        if (observed.Status == previous.Status && observed.MonitorIdentity == previous.MonitorIdentity
            && observed.Bounds == previous.Bounds && observed.FailureOperation == previous.FailureOperation
            && observed.NativeErrorCode == previous.NativeErrorCode
            && SameTargets(observed.TargetAssociation, previous.TargetAssociation)) return previous;
        observed = observed with { Generation = checked(previous.Generation + 1) };
        Volatile.Write(ref _status, observed);
        return observed;
    }

    static bool SameTargets(PdfDisplayTargetAssociation? first, PdfDisplayTargetAssociation? second) =>
        ReferenceEquals(first, second) || first is not null && second is not null
            && first.Status == second.Status && first.FailureOperation == second.FailureOperation
            && first.NativeErrorCode == second.NativeErrorCode && first.Targets.SequenceEqual(second.Targets);

    static PdfDisplayTargetAssociation CaptureTargets(string device, CancellationToken cancellationToken)
    {
        const uint activeVirtualAware = 0x12;
        var targets = new SortedDictionary<string, PdfDisplayTarget>(StringComparer.Ordinal);
        string? failureOperation = null;
        int? failureCode = null;
        void Fail(string operation, int? code)
        {
            if (failureOperation is not null) return;
            failureOperation = operation;
            failureCode = code;
        }
        PdfDisplayTargetAssociation Result() => new(failureOperation is not null
            ? PdfDisplayTargetStatus.Unavailable : targets.Count == 1
            ? PdfDisplayTargetStatus.SingleTarget : PdfDisplayTargetStatus.MultipleTargets,
            targets.Values, failureOperation, failureCode);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int error = GetDisplayConfigBufferSizes(activeVirtualAware, out uint pathCount, out uint modeCount);
            if (error != 0)
            {
                if (error == 122 && attempt < 2) continue;
                Fail("GetDisplayConfigBufferSizes", error);
                return Result();
            }
            if (pathCount > 256 || modeCount > 1024)
            {
                Fail("DisplayConfigLimits", null);
                return Result();
            }
            uint pathCapacity = Math.Max(1u, pathCount), modeCapacity = Math.Max(1u, modeCount);
            var paths = new DisplayPath[pathCapacity];
            // Mode records are 64-byte opaque SDK structures. No union or index is interpreted here.
            var modes = new byte[checked((int)modeCapacity * 64)];
            pathCount = pathCapacity;
            modeCount = modeCapacity;
            error = QueryDisplayConfig(activeVirtualAware, ref pathCount, paths, ref modeCount, modes, 0);
            if (error != 0)
            {
                if (error == 122 && attempt < 2) continue;
                Fail("QueryDisplayConfig", error);
                return Result();
            }
            if (pathCount > pathCapacity || modeCount > modeCapacity || pathCount > 256 || modeCount > 1024)
            {
                Fail("DisplayConfigLimits", null);
                return Result();
            }
            for (int index = 0; index < pathCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = paths[index];
                var source = new DisplaySourceName
                {
                    Header = new() { Type = 1, Size = (uint)Marshal.SizeOf<DisplaySourceName>(),
                        Adapter = path.Source.Adapter, Id = path.Source.Id },
                    DeviceName = string.Empty
                };
                error = DisplayConfigGetDeviceInfo(ref source);
                if (error != 0) { Fail("DisplayConfigGetDeviceInfo", error); continue; }
                if (!string.Equals(source.DeviceName, device, StringComparison.OrdinalIgnoreCase)) continue;
                var target = new DisplayTargetName
                {
                    Header = new() { Type = 2, Size = (uint)Marshal.SizeOf<DisplayTargetName>(),
                        Adapter = path.Target.Adapter, Id = path.Target.Id },
                    FriendlyName = string.Empty, DevicePath = string.Empty
                };
                error = DisplayConfigGetDeviceInfo(ref target);
                if (error != 0) { Fail("DisplayConfigGetDeviceInfo", error); continue; }
                string identity = $"target:{unchecked((uint)path.Target.Adapter.High):X8}{path.Target.Adapter.Low:X8}/{path.Target.Id:X8}";
                if (!targets.ContainsKey(identity))
                    targets.Add(identity, new(identity, string.IsNullOrEmpty(target.FriendlyName) ? null : target.FriendlyName)
                        { ColorState = CaptureColorState(path.Target, cancellationToken) });
            }
            if (targets.Count == 0 && failureOperation is null) Fail("NoMatchingDisplayTarget", null);
            return Result();
        }
        throw new InvalidOperationException("Display configuration observation did not complete.");
    }

    static PdfDisplayColorState CaptureColorState(DisplayTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var advanced = new AdvancedColorInfo
        {
            Header = new() { Type = 15, Size = (uint)Marshal.SizeOf<AdvancedColorInfo>(),
                Adapter = target.Adapter, Id = target.Id }
        };
        int advancedError = DisplayConfigGetDeviceInfo(ref advanced);
        if (advancedError == 0)
        {
            bool active = (advanced.Flags & 2) != 0;
            var mode = advanced.ActiveMode switch
            {
                0 when !active => PdfDisplayColorMode.ConventionalSdr,
                1 when active => PdfDisplayColorMode.AdvancedColorSdr,
                2 when active => PdfDisplayColorMode.Hdr,
                _ => PdfDisplayColorMode.Unknown
            };
            return new(mode, 15, advanced.Flags, advanced.ActiveMode, advanced.Encoding,
                advanced.Bits, null, null);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var legacy = new LegacyColorInfo
        {
            Header = new() { Type = 9, Size = (uint)Marshal.SizeOf<LegacyColorInfo>(),
                Adapter = target.Adapter, Id = target.Id }
        };
        int legacyError = DisplayConfigGetDeviceInfo(ref legacy);
        return legacyError == 0
            ? new(PdfDisplayColorMode.Unknown, 9, legacy.Flags, null, legacy.Encoding,
                legacy.Bits, advancedError, null)
            : new(PdfDisplayColorMode.Unknown, null, null, null, null, null, advancedError, legacyError);
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

    [StructLayout(LayoutKind.Sequential)]
    struct DisplayAdapterId { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    struct DisplaySource { public DisplayAdapterId Adapter; public uint Id, ModeIndex, Status; }

    [StructLayout(LayoutKind.Sequential)]
    struct DisplayTarget
    {
        public DisplayAdapterId Adapter;
        public uint Id, ModeIndex, Technology, Rotation, Scaling, RefreshNumerator, RefreshDenominator, ScanOrder;
        public int Available;
        public uint Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DisplayPath { public DisplaySource Source; public DisplayTarget Target; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    struct DisplayQueryHeader { public uint Type, Size; public DisplayAdapterId Adapter; public uint Id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DisplaySourceName
    {
        public DisplayQueryHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DisplayTargetName
    {
        public DisplayQueryHeader Header;
        public uint Flags, Technology;
        public ushort Manufacturer, Product;
        public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AdvancedColorInfo { public DisplayQueryHeader Header; public uint Flags, Encoding, Bits, ActiveMode; }

    [StructLayout(LayoutKind.Sequential)]
    struct LegacyColorInfo { public DisplayQueryHeader Header; public uint Flags, Encoding, Bits; }

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayPath[] paths,
        ref uint modeCount, [Out] byte[] modes, nint topologyId);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    static extern int DisplayConfigGetDeviceInfo(ref DisplaySourceName request);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    static extern int DisplayConfigGetDeviceInfo(ref DisplayTargetName request);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo request);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int DisplayConfigGetDeviceInfo(ref LegacyColorInfo request);
}
