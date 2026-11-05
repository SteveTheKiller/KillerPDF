using System.Runtime.InteropServices;

namespace KillerPDF.Presentation;

// Owns one native surface. Only the viewport dispatcher calls this transport.
internal sealed unsafe class WindowsPdfNativeTransport(int width, int height) : IDisposable
{
    const uint FloatRgbaFormat = 10;
    const uint LinearSrgbColorSpace = 1;
    readonly List<nint> _owned = [];
    nint _composition, _target, _context, _swap, _swap3, _buffer, _visual, _viewport;
    bool _attached;
    bool _disposed;

    internal int Width { get; private set; } = width;
    internal int Height { get; private set; } = height;

    internal void Initialize(nint viewport)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _viewport = viewport;
        if (IntPtr.Size != 8 || sizeof(SwapDesc) != 48 || sizeof(TextureDesc) != 44)
            throw new PlatformNotSupportedException("Native presentation requires the Windows x64 SDK layout.");
        Guid compositionId = new("C37EA93A-E7AA-450D-B16F-9746CB0407F3");
        int hr = DCompositionCreateDevice(0, ref compositionId, out nint composition);
        Capture(composition); Check("DCompositionCreateDevice", hr); Require(composition); _composition = composition;
        nint target = 0;
        hr = ((delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)Slot(composition, 6))(composition, viewport, 0, &target);
        Capture(target); Check("CreateTargetForHwnd", hr); Require(target); _target = target;

        // Reserve the composition target before creating any GPU resources.
        hr = D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out nint device, out _, out nint context);
        if (hr < 0)
        {
            Release(context); Release(device);
            hr = D3D11CreateDevice(0, 5, 0, 0x20, 0, 0, 7, out device, out _, out context);
        }
        Capture(device); Capture(context); Check("D3D11CreateDevice", hr); Require(device); Require(context); _context = context;
        Guid factoryId = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
        hr = CreateDXGIFactory2(0, ref factoryId, out nint factory);
        Capture(factory); Check("CreateDXGIFactory2", hr); Require(factory);
        var requested = new SwapDesc
        {
            Width = checked((uint)Width), Height = checked((uint)Height), Format = FloatRgbaFormat,
            SampleCount = 1, BufferUsage = 0x20, BufferCount = 2, Scaling = 0, SwapEffect = 3, AlphaMode = 1
        };
        nint swap = 0;
        hr = ((delegate* unmanaged[Stdcall]<nint, nint, SwapDesc*, nint, nint*, int>)Slot(factory, 24))(factory, device, &requested, 0, &swap);
        Capture(swap); Check("CreateSwapChainForComposition", hr); Require(swap); _swap = swap;
        SwapDesc observed = default;
        Check("GetDesc1", ((delegate* unmanaged[Stdcall]<nint, SwapDesc*, int>)Slot(swap, 18))(swap, &observed));
        if (observed.Width != requested.Width || observed.Height != requested.Height || observed.Format != FloatRgbaFormat
            || observed.Stereo != 0 || observed.SampleCount != 1 || observed.SampleQuality != 0 || observed.BufferCount != 2
            || observed.Scaling != 0 || observed.SwapEffect != 3 || observed.AlphaMode != 1)
            throw new NotSupportedException("The native swap chain does not match linear premultiplied FP16 composition.");
        nint swap3 = Query(swap, new Guid("94d99bdb-f1f8-4ab0-b236-7da0170edab1"), "QueryInterface.IDXGISwapChain3");
        _swap3 = swap3;
        uint support = 0;
        Check("CheckColorSpaceSupport.Before", ((delegate* unmanaged[Stdcall]<nint, uint, uint*, int>)Slot(swap3, 37))(swap3, LinearSrgbColorSpace, &support));
        if ((support & 1) == 0) throw new NotSupportedException("The native swap chain does not support linear scRGB presentation.");
        Check("SetColorSpace1", ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(swap3, 38))(swap3, LinearSrgbColorSpace));
        Check("CheckColorSpaceSupport.After", ((delegate* unmanaged[Stdcall]<nint, uint, uint*, int>)Slot(swap3, 37))(swap3, LinearSrgbColorSpace, &support));
        if ((support & 1) == 0) throw new NotSupportedException("The native swap chain did not retain linear scRGB presentation support.");
        Guid textureId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        nint buffer = 0;
        hr = ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(swap, 9))(swap, 0, &textureId, &buffer);
        Capture(buffer); Check("GetBuffer", hr); Require(buffer); _buffer = buffer;
        TextureDesc actualBuffer = default;
        ((delegate* unmanaged[Stdcall]<nint, TextureDesc*, void>)Slot(buffer, 10))(buffer, &actualBuffer);
        if (actualBuffer.Width != requested.Width || actualBuffer.Height != requested.Height || actualBuffer.Format != FloatRgbaFormat
            || actualBuffer.SampleCount != 1 || actualBuffer.SampleQuality != 0 || actualBuffer.MipLevels != 1 || actualBuffer.ArraySize != 1
            || actualBuffer.Usage != 0 || actualBuffer.CpuAccessFlags != 0 || (actualBuffer.BindFlags & 0x20) == 0)
            throw new NotSupportedException("The native back buffer does not match the FP16 frame upload.");
        nint visual = 0;
        hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(composition, 7))(composition, &visual);
        Capture(visual); Check("CreateVisual", hr); Require(visual); _visual = visual;
        Check("SetContent", ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(visual, 15))(visual, swap));
    }

    internal void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width <= 0 || width > 16384) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0 || height > 16384) throw new ArgumentOutOfRangeException(nameof(height));
        if (Width == width && Height == height) return;
        // ResizeBuffers requires every owned back-buffer reference and context binding released.
        ((delegate* unmanaged[Stdcall]<nint, void>)Slot(_context, 110))(_context);
        nint previousBuffer = _buffer;
        _buffer = 0;
        _owned.Remove(previousBuffer);
        Release(previousBuffer);
        Check("ResizeBuffers", ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, uint, int>)Slot(_swap, 13))(
            _swap, 2, checked((uint)width), checked((uint)height), FloatRgbaFormat, 0));
        SwapDesc observed = default;
        Check("GetDesc1.AfterResize", ((delegate* unmanaged[Stdcall]<nint, SwapDesc*, int>)Slot(_swap, 18))(_swap, &observed));
        if (observed.Width != (uint)width || observed.Height != (uint)height || observed.Format != FloatRgbaFormat
            || observed.Stereo != 0 || observed.SampleCount != 1 || observed.SampleQuality != 0 || observed.BufferCount != 2
            || observed.BufferUsage != 0x20 || observed.Scaling != 0 || observed.SwapEffect != 3 || observed.AlphaMode != 1 || observed.Flags != 0)
            throw new NotSupportedException("The resized swap chain does not match linear premultiplied FP16 composition.");
        uint support = 0;
        Check("CheckColorSpaceSupport.Resize.Before", ((delegate* unmanaged[Stdcall]<nint, uint, uint*, int>)Slot(_swap3, 37))(_swap3, LinearSrgbColorSpace, &support));
        if ((support & 1) == 0) throw new NotSupportedException("The resized swap chain does not support linear scRGB presentation.");
        Check("SetColorSpace1.AfterResize", ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(_swap3, 38))(_swap3, LinearSrgbColorSpace));
        Check("CheckColorSpaceSupport.Resize.After", ((delegate* unmanaged[Stdcall]<nint, uint, uint*, int>)Slot(_swap3, 37))(_swap3, LinearSrgbColorSpace, &support));
        if ((support & 1) == 0) throw new NotSupportedException("The resized swap chain did not retain linear scRGB presentation support.");
        Guid textureId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        nint buffer = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(_swap, 9))(_swap, 0, &textureId, &buffer);
        Capture(buffer); Check("GetBuffer.AfterResize", hr); Require(buffer); _buffer = buffer;
        TextureDesc actualBuffer = default;
        ((delegate* unmanaged[Stdcall]<nint, TextureDesc*, void>)Slot(buffer, 10))(buffer, &actualBuffer);
        if (actualBuffer.Width != (uint)width || actualBuffer.Height != (uint)height || actualBuffer.Format != FloatRgbaFormat
            || actualBuffer.SampleCount != 1 || actualBuffer.SampleQuality != 0 || actualBuffer.MipLevels != 1 || actualBuffer.ArraySize != 1
            || actualBuffer.Usage != 0 || actualBuffer.CpuAccessFlags != 0 || (actualBuffer.BindFlags & 0x20) == 0)
            throw new NotSupportedException("The resized back buffer does not match the FP16 frame upload.");
        Width = width;
        Height = height;
    }

    internal void Upload(Half[] pixels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pixels.LongLength != (long)Width * Height * 4) throw new ArgumentException("Half upload dimensions do not match the native surface.", nameof(pixels));
        fixed (Half* source = pixels)
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, void*, uint, uint, void>)Slot(_context, 48))(_context, _buffer, 0, 0, source, checked((uint)Width * 8), 0);
    }

    internal void AttachRoot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_attached) return;
        Check("SetRoot", ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(_target, 3))(_target, _visual));
        _attached = true;
    }

    internal void Present()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Slot(_swap, 8))(_swap, 1, 0);
        Check("Present", hr);
        if (hr != 0) throw new NotSupportedException("The native swap chain did not complete a frame submission.");
    }

    internal void Commit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!GetClientRect(_viewport, out NativeRect client))
            throw new NativeFailure("GetClientRect", Marshal.GetHRForLastWin32Error());
        int clientWidth = client.Right - client.Left;
        int clientHeight = client.Bottom - client.Top;
        if (clientWidth <= 0 || clientHeight <= 0)
            throw new NativeFailure("ViewportDimensions", unchecked((int)0x80070057));
        var transform = new CompositionMatrix
        {
            M11 = (float)clientWidth / Width,
            M22 = (float)clientHeight / Height
        };
        Check("SetTransform", ((delegate* unmanaged[Stdcall]<nint, CompositionMatrix*, int>)Slot(_visual, 8))(_visual, &transform));
        Check("Commit", ((delegate* unmanaged[Stdcall]<nint, int>)Slot(_composition, 3))(_composition));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Detach only this transport's root. Cleanup still releases every reference if native detach fails.
        if (_attached && _target != 0 && _composition != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(_target, 3))(_target, 0);
            ((delegate* unmanaged[Stdcall]<nint, int>)Slot(_composition, 3))(_composition);
        }
        if (_context != 0) ((delegate* unmanaged[Stdcall]<nint, void>)Slot(_context, 110))(_context);
        for (int i = _owned.Count - 1; i >= 0; i--) Release(_owned[i]);
        _owned.Clear(); _composition = _target = _context = _swap = _swap3 = _buffer = _visual = 0;
    }

    nint Query(nint pointer, Guid id, string operation)
    {
        nint result = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(pointer, 0))(pointer, &id, &result);
        Capture(result); Check(operation, hr); Require(result); return result;
    }
    void Capture(nint pointer) { if (pointer != 0) _owned.Add(pointer); }
    static void Require(nint pointer) { if (pointer == 0) throw new InvalidOperationException("A successful native call returned no COM object."); }
    static void Check(string operation, int hr) { if (hr < 0) throw new NativeFailure(operation, hr); }
    static nint Slot(nint pointer, int index) => (*(nint**)pointer)[index];
    static void Release(nint pointer) { if (pointer != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(pointer, 2))(pointer); }

    internal sealed class NativeFailure(string operation, int hr) : COMException(operation + " failed.", hr)
    {
        internal string Operation { get; } = operation;
    }

    [StructLayout(LayoutKind.Sequential)] struct SwapDesc
    {
        public uint Width, Height, Format; public int Stereo;
        public uint SampleCount, SampleQuality, BufferUsage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags;
    }
    [StructLayout(LayoutKind.Sequential)] struct TextureDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }
    [StructLayout(LayoutKind.Sequential)] struct CompositionMatrix
    {
        public float M11, M12, M21, M22, M31, M32;
    }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetClientRect(nint window, out NativeRect rectangle);
    [DllImport("dcomp.dll", ExactSpelling = true)] static extern int DCompositionCreateDevice(nint dxgiDevice, ref Guid iid, out nint compositionDevice);
    [DllImport("d3d11.dll", ExactSpelling = true)] static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out uint featureLevel, out nint immediateContext);
    [DllImport("dxgi.dll", ExactSpelling = true)] static extern int CreateDXGIFactory2(uint flags, ref Guid iid, out nint factory);
}
