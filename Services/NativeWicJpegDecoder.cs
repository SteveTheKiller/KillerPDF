using System.Runtime.InteropServices;

namespace KillerPDF.Services;

internal static class NativeWicJpegDecoder
{
    private static readonly Guid FactoryClass = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid FactoryInterface = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    private static readonly Guid SourceTransformInterface = new("3b16811b-6a43-4ec9-b713-3d5a0c13b940");
    private static readonly Guid CmykFormat = new("6fddc324-4e03-4bfe-b185-3d77768dc91c");

    internal static unsafe bool TryDecode(ReadOnlyMemory<byte> encoded,
        int expectedWidth, int expectedHeight, out byte[] samples)
    {
        samples = [];
        nint stream = SHCreateMemStream(encoded.ToArray(), checked((uint)encoded.Length));
        if (stream == 0) return false;
        nint factory = 0, decoder = 0, frame = 0, transform = 0;
        try
        {
            Guid classId = FactoryClass, factoryId = FactoryInterface;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref classId, 0, 1,
                ref factoryId, out factory));
            Marshal.ThrowExceptionForHR(Get<CreateDecoderFromStream>(factory, 4)(
                factory, stream, 0, 0, out decoder));
            Marshal.ThrowExceptionForHR(Get<GetFrame>(decoder, 13)(decoder, 0, out frame));
            Marshal.ThrowExceptionForHR(Get<GetPixelFormat>(frame, 4)(
                frame, out Guid sourceFormat));
            if (sourceFormat != CmykFormat) return false;
            Guid transformId = SourceTransformInterface;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(
                frame, in transformId, out transform));
            uint width = checked((uint)expectedWidth);
            uint height = checked((uint)expectedHeight);
            Marshal.ThrowExceptionForHR(Get<GetClosestSize>(transform, 4)(
                transform, ref width, ref height));
            if (width != expectedWidth || height != expectedHeight) return false;
            Guid outputFormat = CmykFormat;
            Marshal.ThrowExceptionForHR(Get<GetClosestPixelFormat>(transform, 5)(
                transform, ref outputFormat));
            if (outputFormat != CmykFormat) return false;
            byte[] decoded = new byte[checked(expectedWidth * expectedHeight * 4)];
            fixed (byte* destination = decoded)
                Marshal.ThrowExceptionForHR(Get<CopyPixels>(transform, 3)(
                    transform, 0, width, height, ref outputFormat, 0, width * 4,
                    checked((uint)decoded.Length), (nint)destination));
            samples = decoded;
            return true;
        }
        finally
        {
            if (transform != 0) Marshal.Release(transform);
            if (frame != 0) Marshal.Release(frame);
            if (decoder != 0) Marshal.Release(decoder);
            if (factory != 0) Marshal.Release(factory);
            Marshal.Release(stream);
        }
    }

    private static T Get<T>(nint unknown, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(
            Marshal.ReadIntPtr(unknown), slot * IntPtr.Size));

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid classId, nint outer, uint context,
        ref Guid interfaceId, out nint result);

    [DllImport("shlwapi.dll")]
    private static extern nint SHCreateMemStream(byte[] data, uint length);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDecoderFromStream(nint self, nint stream, nint vendor,
        uint options, out nint decoder);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFrame(nint self, uint index, out nint frame);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPixelFormat(nint self, out Guid format);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetClosestSize(nint self, ref uint width, ref uint height);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetClosestPixelFormat(nint self, ref Guid format);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CopyPixels(nint self, nint rectangle, uint width, uint height,
        ref Guid format, uint transform, uint stride, uint bufferLength, nint buffer);
}
