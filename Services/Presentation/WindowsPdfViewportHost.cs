using System.Runtime.InteropServices;
using System.Collections;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using KillerPdf.Engine.Rendering;

namespace KillerPDF.Presentation;

/// <summary>Owns a native frame window and its transparent WPF overlay.</summary>
internal partial class WindowsPdfViewportHost : HwndHost
{
    readonly WindowProcedure procedure;
    readonly string className = "KillerPdfViewport." + Guid.NewGuid().ToString("N");
    WindowsPdfFramePresenter? presenter;
    readonly WindowsPdfViewportRegion clipRegion = new();
    string? installedClip;
    uint installedClipDpi;
    bool refreshingClip;
    bool registered;
    internal FrameworkElement OverlayRoot { get; }
    internal nint Container { get; private set; }
    internal nint Viewport { get; private set; }
    internal HwndSource? Overlay { get; private set; }
    protected override IEnumerator LogicalChildren => new[] { OverlayRoot }.GetEnumerator();

    internal WindowsPdfViewportHost(FrameworkElement overlayRoot)
    {
        ArgumentNullException.ThrowIfNull(overlayRoot);
        OverlayRoot = overlayRoot;
        AddLogicalChild(overlayRoot);
        procedure = WindowProc;
        LayoutUpdated += (_, _) => RefreshNativeClip();
    }

    internal IPdfFramePresenter GetPresenter()
    {
        VerifyAccess();
        if (Viewport == 0) throw new InvalidOperationException("The native viewport is not attached.");
        return presenter ??= new WindowsPdfFramePresenter(Viewport);
    }

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        try
        {
            var windowClass = new WindowClass { Procedure = procedure, Name = className, Cursor = LoadCursor(0, 32512) };
            if (RegisterClass(ref windowClass) == 0) throw new System.ComponentModel.Win32Exception();
            registered = true;
            Container = CreateWindowEx(0, className, "PDF viewport container", 0x56000000, 0, 0, 1, 1, parent.Handle, 0, 0, 0);
            if (Container == 0) throw new System.ComponentModel.Win32Exception();
            Viewport = CreateWindowEx(0, className, "PDF frame", 0x50000000, 0, 0, 1, 1, Container, 0, 0, 0);
            if (Viewport == 0) throw new System.ComponentModel.Win32Exception();
            Overlay = new HwndSource(new HwndSourceParameters("PDF editing overlay")
            {
                ParentWindow = Container, WindowStyle = 0x50000000, Width = 1, Height = 1,
                UsesPerPixelTransparency = true, HwndSourceHook = OnOverlayMessage
            });
            Overlay.CompositionTarget.BackgroundColor = Colors.Transparent;
            Overlay.RootVisual = OverlayRoot;
            return new HandleRef(this, Container);
        }
        catch { ReleaseWindows(); throw; }
    }

    protected override void OnWindowPositionChanged(Rect bounds)
    {
        base.OnWindowPositionChanged(bounds);
        if (Overlay is null || !GetClientRect(Container, out var client)) return;
        if (!SetWindowPos(Viewport, 0, 0, 0, Math.Max(1, client.Right), Math.Max(1, client.Bottom), 0x14)
            || !SetWindowPos(Overlay.Handle, 0, 0, 0, Math.Max(1, client.Right), Math.Max(1, client.Bottom), 0x10))
            throw new System.ComponentModel.Win32Exception();
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => ReleaseWindows();

    void RefreshNativeClip()
    {
        if (Container == 0 || !IsArrangeValid || refreshingClip) return;
        refreshingClip = true;
        try
        {
            Geometry clip = IsVisible ? WindowsPdfViewportLayout.Capture(this) : Geometry.Empty;
            string description = clip.ToString(System.Globalization.CultureInfo.InvariantCulture);
            uint dpi = GetDpiForWindow(Container);
            if (description == installedClip && dpi == installedClipDpi) return;
            clipRegion.Update(Container, clip);
            installedClip = description;
            installedClipDpi = dpi;
        }
        finally { refreshingClip = false; }
    }

    void ReleaseWindows()
    {
        installedClip = null;
        installedClipDpi = 0;
        editorMouseBridge = false;
        pendingEditorMouse = null;
        try { presenter?.Dispose(); }
        finally
        {
            presenter = null;
            try { Overlay?.Dispose(); }
            finally
            {
                Overlay = null;
                if (Container != 0) DestroyWindow(Container);
                Container = Viewport = 0;
                if (registered) UnregisterClass(className, 0);
                registered = false;
            }
        }
    }

    nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        bool handled = false;
        nint result = OnNativeMessage(hwnd, message, wParam, lParam, ref handled);
        if (handled) return result;
        if (message == 0xF)
        {
            BeginPaint(hwnd, out var paint);
            EndPaint(hwnd, ref paint);
            return 0;
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }
    protected virtual nint OnNativeMessage(nint hwnd, uint message, nint wParam, nint lParam, ref bool handled)
    {
        if (hwnd == Viewport && message == 0x20A)
            handled = BridgeWheel(wParam, lParam);
        else if (hwnd == Viewport && (message == 0x201 || editorMouseBridge && message is 0x200 or 0x202))
            handled = BridgeEditorMouse(message, wParam, lParam);
        return 0;
    }
    protected virtual nint OnOverlayMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        ReleaseEditorMouse(hwnd, message);
        return 0;
    }

    delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WindowClass
    {
        public uint Style;
        public WindowProcedure Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? Menu;
        public string Name;
    }
    [StructLayout(LayoutKind.Sequential)] struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct Paint
    {
        public nint Dc;
        public int Erase;
        public RectI Rect;
        public int Restore, Update;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClass(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateWindowEx(int extendedStyle, string name, string title, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern nint LoadCursor(nint instance, int cursor);
    [DllImport("user32.dll")] static extern bool GetClientRect(nint window, out RectI rect);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern nint BeginPaint(nint window, out Paint paint);
    [DllImport("user32.dll")] static extern bool EndPaint(nint window, ref Paint paint);
}
