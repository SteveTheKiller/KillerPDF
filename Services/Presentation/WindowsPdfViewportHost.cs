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
    IPdfFramePresenter? presenter;
    readonly Func<nint, IPdfFramePresenter> presenterFactory;
    readonly Transform overlayTransform;
    (double Width, double Height, double ScaleX, double ScaleY) overlayLayout;
    readonly WindowsPdfViewportRegion clipRegion = new();
    string? installedClip;
    uint installedClipDpi;
    (Size Layout, int Width, int Height) installedClipSize;
    bool refreshingClip;
    bool registered;
    internal FrameworkElement OverlayRoot { get; }
    internal nint Container { get; private set; }
    internal nint Viewport { get; private set; }
    internal HwndSource? Overlay { get; private set; }
    protected override IEnumerator LogicalChildren => new[] { OverlayRoot }.GetEnumerator();

    internal WindowsPdfViewportHost(FrameworkElement overlayRoot, Func<nint, IPdfFramePresenter>? presenterFactory = null)
    {
        ArgumentNullException.ThrowIfNull(overlayRoot);
        OverlayRoot = overlayRoot;
        overlayTransform = overlayRoot.LayoutTransform;
        this.presenterFactory = presenterFactory ?? (window => new WindowsPdfFramePresenter(window));
        AddLogicalChild(overlayRoot);
        procedure = WindowProc;
        LayoutUpdated += (_, _) => RefreshNativeClip();
    }

    internal IPdfFramePresenter GetPresenter()
    {
        VerifyAccess();
        if (Viewport == 0) throw new InvalidOperationException("The native viewport is not attached.");
        return presenter ??= presenterFactory(Viewport);
    }

    /// <summary>Releases the native windows and returns the overlay content so its owner can show it directly.</summary>
    internal FrameworkElement Abandon()
    {
        VerifyAccess();
        Dispose();
        RemoveLogicalChild(OverlayRoot);
        return OverlayRoot;
    }

    internal (int Width, int Height) PixelSize => GetClientRect(Viewport, out var client)
        ? (client.Right, client.Bottom) : (0, 0);

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
            // Join the owning window's keyboard chain so Tab and mnemonics reach overlay editors.
            ((System.Windows.Interop.IKeyboardInputSink)Overlay).KeyboardInputSite =
                ((System.Windows.Interop.IKeyboardInputSink)this).RegisterKeyboardInputSink(Overlay);
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
        {
            // Window placement runs from layout; a failed move is retried on the next pass.
            WindowsPdfLayoutFailure.Record("viewport placement", new System.ComponentModel.Win32Exception());
            return;
        }
        FitOverlay(client);
        // Placement runs from layout: a detail window that cannot be moved is recorded, never raised, and retried on
        // the next pass. Preparing the detail window still throws, where its caller releases the window.
        try { PositionDetail(); }
        catch (System.ComponentModel.Win32Exception error) { WindowsPdfLayoutFailure.Record("detail placement", error); }
        if (presenter is WindowsPdfFramePresenter native) native.RefreshViewport();
    }

    void FitOverlay(RectI client)
    {
        double width = RenderSize.Width, height = RenderSize.Height;
        double dpi = GetDpiForWindow(Container) / 96d;
        if (width <= 0 || height <= 0 || dpi <= 0) return;
        var layout = (width, height, client.Right / (width * dpi), client.Bottom / (height * dpi));
        if (layout == overlayLayout) return;
        overlayLayout = layout;
        var scale = new ScaleTransform(layout.Item3, layout.Item4);
        Transform transform = scale;
        if (!overlayTransform.Value.IsIdentity)
        {
            var combined = new TransformGroup();
            combined.Children.Add(overlayTransform);
            combined.Children.Add(scale);
            transform = combined;
        }
        OverlayRoot.SetCurrentValue(WidthProperty, width);
        OverlayRoot.SetCurrentValue(HeightProperty, height);
        OverlayRoot.SetCurrentValue(LayoutTransformProperty, transform);
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
            var pixels = PixelSize;
            var size = (RenderSize, pixels.Width, pixels.Height);
            if (description == installedClip && dpi == installedClipDpi && size == installedClipSize) return;
            if (!clip.IsEmpty())
            {
                if (RenderSize.Width <= 0 || RenderSize.Height <= 0 || pixels.Width <= 0 || pixels.Height <= 0 || dpi == 0)
                    throw new InvalidOperationException("Viewport clip geometry is unavailable.");
                clip = clip.CloneCurrentValue();
                var transforms = new TransformGroup();
                if (clip.Transform is { } existing) transforms.Children.Add(existing);
                transforms.Children.Add(new ScaleTransform(pixels.Width / (RenderSize.Width * dpi / 96d),
                    pixels.Height / (RenderSize.Height * dpi / 96d)));
                clip.Transform = transforms;
            }
            clipRegion.Update(Container, clip);
            installedClip = description;
            installedClipDpi = dpi;
            installedClipSize = size;
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException
            or ArgumentException or System.ComponentModel.Win32Exception)
        {
            // Layout passes during animations or teardown can be momentarily unclippable, for
            // example under a zero scale. Keep the clip already installed, retry on the next
            // pass, and never raise from a layout event.
            installedClip = null;
            WindowsPdfLayoutFailure.Record("viewport clip", error);
        }
        finally { refreshingClip = false; }
    }

    void ReleaseWindows()
    {
        ClearDetail();
        installedClip = null;
        installedClipDpi = 0;
        installedClipSize = default;
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
