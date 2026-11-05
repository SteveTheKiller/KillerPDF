using System.Collections;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace KillerPDF.Presentation;

/// <summary>Hosts existing annotation controls above native page windows.</summary>
internal sealed class WindowsPdfAnnotationHost : HwndHost
{
    readonly FrameworkElement root;
    readonly WindowsPdfViewportRegion region = new();
    HwndSource? source;
    string? installedClip;
    (Size Layout, int Width, int Height, uint Dpi) installedSize;
    bool updating, translatingAccelerator;

    internal WindowsPdfAnnotationHost(FrameworkElement root)
    {
        this.root = root;
        AddLogicalChild(root);
        LayoutUpdated += (_, _) => RefreshLayout();
    }

    protected override IEnumerator LogicalChildren => new[] { root }.GetEnumerator();

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        try
        {
            source = new HwndSource(new HwndSourceParameters("PDF annotation controls")
            {
                ParentWindow = parent.Handle,
                WindowStyle = 0x50000000,
                Width = 1, Height = 1,
                UsesPerPixelTransparency = true
            });
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            source.RootVisual = root;
            return new HandleRef(this, source.Handle);
        }
        catch
        {
            source?.Dispose();
            source = null;
            throw;
        }
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        source?.Dispose();
        source = null;
        installedClip = null;
        installedSize = default;
    }

    protected override void OnWindowPositionChanged(Rect bounds)
    {
        base.OnWindowPositionChanged(bounds);
        RefreshLayout();
    }

    protected override bool TranslateAcceleratorCore(ref MSG message, System.Windows.Input.ModifierKeys modifiers)
    {
        if (translatingAccelerator || source is not IKeyboardInputSink sink || !sink.HasFocusWithin()) return false;
        translatingAccelerator = true;
        try { return sink.TranslateAccelerator(ref message, modifiers); }
        finally { translatingAccelerator = false; }
    }

    void RefreshLayout()
    {
        if (source is null || updating || !IsArrangeValid || RenderSize.Width <= 0 || RenderSize.Height <= 0) return;
        updating = true;
        try
        {
            if (!GetClientRect(source.Handle, out var client)) throw new System.ComponentModel.Win32Exception();
            uint dpi = GetDpiForWindow(source.Handle);
            if (client.Right <= 0 || client.Bottom <= 0 || dpi == 0) return;
            double scaleX = client.Right / (RenderSize.Width * dpi / 96d);
            double scaleY = client.Bottom / (RenderSize.Height * dpi / 96d);
            var size = (RenderSize, client.Right, client.Bottom, dpi);
            if (size != installedSize)
            {
                root.Width = RenderSize.Width;
                root.Height = RenderSize.Height;
                root.LayoutTransform = new ScaleTransform(scaleX, scaleY);
            }
            Geometry clip = IsVisible ? WindowsPdfViewportLayout.Capture(this) : Geometry.Empty;
            string description = clip.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (description != installedClip || size != installedSize)
            {
                clip = clip.CloneCurrentValue();
                var transforms = new TransformGroup();
                if (clip.Transform is { } existing) transforms.Children.Add(existing);
                transforms.Children.Add(new ScaleTransform(scaleX, scaleY));
                clip.Transform = transforms;
                region.Update(source.Handle, clip);
                installedClip = description;
                installedSize = size;
            }
            // Order only within the owning window, without activating it.
            if (!SetWindowPos(source.Handle, 0, 0, 0, 0, 0, 0x13))
                throw new System.ComponentModel.Win32Exception();
        }
        finally { updating = false; }
    }

    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetClientRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(nint window, nint after,
        int x, int y, int width, int height, uint flags);
}
