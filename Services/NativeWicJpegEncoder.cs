using System.Runtime.InteropServices;

namespace KillerPDF.Services;

internal static partial class NativeWicJpegEncoder
{
    internal static unsafe byte[] EncodePhoto(ReadOnlySpan<byte> samples, int width, int height, int components, int quality,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (components is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(components));
        if ((long)width * height * components != samples.Length)
            throw new ArgumentException("JPEG samples do not match the image dimensions.", nameof(samples));
        if (quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(quality));
        int initialized = CoInitializeEx(0, 0);
        if (initialized < 0 && initialized != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(initialized);
        nint factory = 0, encoder = 0, frame = 0, properties = 0, stream = 0;
        bool pressure = false;
        try
        {
            Guid factoryClass = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
            Guid factoryInterface = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref factoryClass, 0, 1, ref factoryInterface, out factory));
            Guid jpegContainer = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");
            Marshal.ThrowExceptionForHR(Get<CreateEncoder>(factory, 8)(factory, ref jpegContainer, 0, out encoder));
            Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(0, 1, out stream));
            GC.AddMemoryPressure(samples.Length);
            pressure = true;
            Marshal.ThrowExceptionForHR(Get<InitializeEncoder>(encoder, 3)(encoder, stream, 2));
            Marshal.ThrowExceptionForHR(Get<CreateFrame>(encoder, 10)(encoder, out frame, out properties));
            WriteOption(properties, "ImageQuality", new Variant { Type = 4, Float = quality / 100f });
            // WIC's default is 4:2:0. Full color resolution keeps thin colored text and rules sharp.
            WriteOption(properties, "JpegYCrCbSubsampling", new Variant { Type = 17, Byte = 3 });
            Marshal.ThrowExceptionForHR(Get<InitializeFrame>(frame, 3)(frame, properties));
            Marshal.ThrowExceptionForHR(Get<SetSize>(frame, 4)(frame, (uint)width, (uint)height));
            Marshal.ThrowExceptionForHR(Get<SetResolution>(frame, 5)(frame, 96, 96));
            Guid requested = components == 3 ? new("6fddc324-4e03-4bfe-b185-3d77768dc90c")
                : new("6fddc324-4e03-4bfe-b185-3d77768dc908");
            Guid format = requested;
            Marshal.ThrowExceptionForHR(Get<SetPixelFormat>(frame, 6)(frame, ref format));
            if (format != requested) throw new NotSupportedException("The photo JPEG encoder changed the requested pixel format.");
            int stride = checked(width * components);
            byte[] rows = new byte[checked(stride * Math.Min(height, 32))];
            var write = Get<WritePixels>(frame, 10);
            for (int top = 0; top < height; top += 32)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(height - top, 32), bytes = checked(stride * count);
                var source = samples.Slice(checked(top * stride), bytes);
                if (components == 1) source.CopyTo(rows);
                else
                    for (int pixel = 0; pixel < bytes; pixel += 3)
                    {
                        rows[pixel] = source[pixel + 2];
                        rows[pixel + 1] = source[pixel + 1];
                        rows[pixel + 2] = source[pixel];
                    }
                fixed (byte* buffer = rows)
                    Marshal.ThrowExceptionForHR(write(frame, (uint)count, (uint)stride, (uint)bytes, (nint)buffer));
            }
            cancellationToken.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(Get<Commit>(frame, 12)(frame));
            Marshal.ThrowExceptionForHR(Get<Commit>(encoder, 11)(encoder));
            cancellationToken.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(Get<Seek>(stream, 5)(stream, 0, 2, out ulong length));
            if (length > int.MaxValue) throw new NotSupportedException("The photo JPEG exceeds the supported byte limit.");
            Marshal.ThrowExceptionForHR(GetHGlobalFromStream(stream, out nint memory));
            nint address = GlobalLock(memory);
            if (address == 0) throw new InvalidOperationException("The encoded JPEG buffer is unavailable.");
            try
            {
                byte[] result = new byte[(int)length];
                Marshal.Copy(address, result, 0, result.Length);
                return result;
            }
            finally { GlobalUnlock(memory); }
        }
        finally
        {
            if (properties != 0) Marshal.Release(properties);
            if (frame != 0) Marshal.Release(frame);
            if (encoder != 0) Marshal.Release(encoder);
            if (factory != 0) Marshal.Release(factory);
            if (stream != 0) Marshal.Release(stream);
            if (pressure) GC.RemoveMemoryPressure(samples.Length);
            if (initialized >= 0) CoUninitialize();
        }
    }

    private static void WriteOption(nint bag, string name, Variant value)
    {
        nint text = Marshal.StringToCoTaskMemUni(name);
        try
        {
            var property = new Property
            {
                Kind = 0, Type = value.Type, ClipboardFormat = 0, Hint = 0, Name = text, Class = Guid.Empty
            };
            Marshal.ThrowExceptionForHR(Get<WriteProperty>(bag, 4)(bag, 1, ref property, ref value));
        }
        finally { Marshal.FreeCoTaskMem(text); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Property
    {
        public uint Kind;
        public ushort Type, ClipboardFormat;
        public uint Hint;
        public nint Name;
        public Guid Class;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public float Float;
        [FieldOffset(8)] public byte Byte;
    }

    private static T Get<T>(nint unknown, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(unknown), slot * IntPtr.Size));

    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint mode);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);
    [LibraryImport("ole32.dll")] private static partial int CreateStreamOnHGlobal(nint memory, int deleteOnRelease, out nint stream);
    [LibraryImport("ole32.dll")] private static partial int GetHGlobalFromStream(nint stream, out nint memory);
    [LibraryImport("kernel32.dll")] private static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32.dll")] private static partial int GlobalUnlock(nint memory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateEncoder(nint self, ref Guid format, nint vendor, out nint encoder);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InitializeEncoder(nint self, nint stream, uint cache);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateFrame(nint self, out nint frame, out nint properties);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InitializeFrame(nint self, nint properties);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int WriteProperty(nint self, uint count, ref Property property, ref Variant value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetSize(nint self, uint width, uint height);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetResolution(nint self, double x, double y);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetPixelFormat(nint self, ref Guid format);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int WritePixels(nint self, uint lines, uint stride, uint length, nint pixels);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Commit(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Seek(nint self, long move, uint origin, out ulong position);
}
