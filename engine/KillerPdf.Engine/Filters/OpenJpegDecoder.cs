using System.Runtime.InteropServices;

namespace KillerPdf.Engine.Filters;

// OpenJPEG 2.5.4 C ABI. The packaged native library targets Windows x64.
internal static class OpenJpegDecoder
{
    private const string ImportName = "openjp2";

    internal static Jpeg2000DecodedImage Decode(byte[] codestream, Jpeg2000Shape shape,
        int reduction, int width, int height, int rowBytes, int length, int maximumParallelism)
    {
        if ((long)width * height * shape.Components * sizeof(int) > 256L * 1024 * 1024)
            throw new PdfFilterException("OpenJPEG sample allocation exceeds the configured safety limit.");
        var source = new MemorySource(codestream);
        GCHandle handle = GCHandle.Alloc(source);
        IntPtr codec = IntPtr.Zero, stream = IntPtr.Zero, image = IntPtr.Zero;
        try
        {
            codec = CreateDecoder(0);
            stream = CreateMemoryStream(handle, codestream.Length);
            var parameters = new DecoderParameters();
            SetDefaults(ref parameters);
            parameters.Reduction = (uint)reduction;
            if (codec == IntPtr.Zero || stream == IntPtr.Zero || Setup(codec, ref parameters) == 0)
                throw new PdfFilterException("OpenJPEG could not initialize the JPEG 2000 decoder.");
            // OpenJPEG builds a fresh thread pool per codec, which costs more than decoding a small image.
            if ((long)width * height >= 1 << 20)
                SetThreads(codec, Math.Clamp(maximumParallelism, 1, Environment.ProcessorCount));
            if (ReadHeader(stream, codec, out image) == 0
                || DecodeImage(codec, stream, image) == 0
                || EndDecode(codec, stream) == 0)
                throw new PdfFilterException("OpenJPEG could not decode the JPEG 2000 codestream.");

            return new Jpeg2000DecodedImage(Pack(image, shape, reduction, width, height, rowBytes, length),
                width, height, shape.Components, shape.Bits);
        }
        finally
        {
            if (image != IntPtr.Zero) DestroyImage(image);
            if (stream != IntPtr.Zero) DestroyStream(stream);
            if (codec != IntPtr.Zero) DestroyDecoder(codec);
            handle.Free();
        }
    }

    // The codestream is read from memory through OpenJPEG's stream callbacks instead of a temp file,
    // which cost more than the decode itself on pages with hundreds of small images.
    private sealed class MemorySource(byte[] data)
    {
        public readonly byte[] Data = data;
        public long Position;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint ReadCallback(IntPtr buffer, nuint count, IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long SkipCallback(long count, IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SeekCallback(long position, IntPtr user);

    private static readonly ReadCallback Reader = ReadSource;
    private static readonly SkipCallback Skipper = SkipSource;
    private static readonly SeekCallback Seeker = SeekSource;
    private static readonly IntPtr ReaderPointer = Marshal.GetFunctionPointerForDelegate(Reader);
    private static readonly IntPtr SkipperPointer = Marshal.GetFunctionPointerForDelegate(Skipper);
    private static readonly IntPtr SeekerPointer = Marshal.GetFunctionPointerForDelegate(Seeker);

    private static IntPtr CreateMemoryStream(GCHandle handle, int length)
    {
        IntPtr stream = CreateStream(1 << 20, 1);
        if (stream == IntPtr.Zero) return stream;
        SetReadFunction(stream, ReaderPointer);
        SetSkipFunction(stream, SkipperPointer);
        SetSeekFunction(stream, SeekerPointer);
        SetUserData(stream, GCHandle.ToIntPtr(handle), IntPtr.Zero);
        SetUserDataLength(stream, (ulong)length);
        return stream;
    }

    private static nuint ReadSource(IntPtr buffer, nuint count, IntPtr user)
    {
        var source = (MemorySource)GCHandle.FromIntPtr(user).Target!;
        long available = source.Data.Length - source.Position;
        if (available <= 0) return nuint.MaxValue;
        int read = (int)Math.Min(available, (long)Math.Min(count, (nuint)int.MaxValue));
        Marshal.Copy(source.Data, (int)source.Position, buffer, read);
        source.Position += read;
        return (nuint)read;
    }

    private static long SkipSource(long count, IntPtr user)
    {
        var source = (MemorySource)GCHandle.FromIntPtr(user).Target!;
        long target = source.Position + count;
        if (target < 0 || target > source.Data.Length) return -1;
        source.Position = target;
        return count;
    }

    private static int SeekSource(long position, IntPtr user)
    {
        var source = (MemorySource)GCHandle.FromIntPtr(user).Target!;
        if (position < 0 || position > source.Data.Length) return 0;
        source.Position = position;
        return 1;
    }

    private static byte[] Pack(IntPtr image, Jpeg2000Shape shape, int reduction,
        int width, int height, int rowBytes, int length)
    {
        if (Marshal.ReadInt32(image, 16) != shape.Components)
            throw new PdfFilterException("OpenJPEG returned an unexpected component count.");
        IntPtr components = Marshal.ReadIntPtr(image, 24);
        var result = new byte[length];
        int maximum = (1 << shape.Bits) - 1;
        for (int component = 0; component < shape.Components; component++)
        {
            IntPtr item = components + component * 64;
            int stepX = Marshal.ReadInt32(item, 0);
            int stepY = Marshal.ReadInt32(item, 4);
            int columns = Marshal.ReadInt32(item, 8);
            int rows = Marshal.ReadInt32(item, 12);
            int originX = Marshal.ReadInt32(item, 16);
            int originY = Marshal.ReadInt32(item, 20);
            int bits = Marshal.ReadInt32(item, 24);
            int signed = Marshal.ReadInt32(item, 32);
            IntPtr data = Marshal.ReadIntPtr(item, 48);
            if (stepX <= 0 || stepY <= 0 || columns <= 0 || rows <= 0
                || bits != shape.Bits || data == IntPtr.Zero)
                throw new PdfFilterException("OpenJPEG returned unsupported component geometry.");

            long divisor = 1L << Math.Min(reduction, 32);
            long left = ((originX + divisor - 1) / divisor) * stepX
                - ((shape.XOrigin + divisor - 1) / divisor);
            long top = ((originY + divisor - 1) / divisor) * stepY
                - ((shape.YOrigin + divisor - 1) / divisor);
            int bias = signed == 0 ? 0 : 1 << (bits - 1);
            var samples = new int[checked(columns * rows)];
            Marshal.Copy(data, samples, 0, samples.Length);
            for (int y = (int)Math.Max(0, top); y < height && (y - top) / stepY < rows; y++)
            for (int x = (int)Math.Max(0, left); x < width && (x - left) / stepX < columns; x++)
            {
                int index = checked((int)((y - top) / stepY * columns + (x - left) / stepX));
                int value = Math.Clamp(samples[index] + bias, 0, maximum);
                if (bits == 8)
                {
                    result[y * rowBytes + x * shape.Components + component] = (byte)value;
                    continue;
                }
                long bitOffset = (long)y * rowBytes * 8 + (x * shape.Components + component) * bits;
                for (int bit = bits - 1; bit >= 0; bit--, bitOffset++)
                    if ((value & (1 << bit)) != 0)
                        result[bitOffset / 8] |= (byte)(1 << (7 - (int)(bitOffset % 8)));
            }
        }
        return result;
    }

    // opj_dparameters_t contains two OPJ_PATH_LEN (4096 byte) arrays.
    // Only the reduction field is overridden after OpenJPEG fills the defaults.
    [StructLayout(LayoutKind.Explicit, Size = 8252)]
    private struct DecoderParameters
    {
        [FieldOffset(0)] public uint Reduction;
    }

    [DllImport(ImportName, EntryPoint = "opj_create_decompress", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CreateDecoder(int format);
    [DllImport(ImportName, EntryPoint = "opj_set_default_decoder_parameters", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetDefaults(ref DecoderParameters parameters);
    [DllImport(ImportName, EntryPoint = "opj_setup_decoder", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Setup(IntPtr codec, ref DecoderParameters parameters);
    [DllImport(ImportName, EntryPoint = "opj_codec_set_threads", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SetThreads(IntPtr codec, int count);
    [DllImport(ImportName, EntryPoint = "opj_stream_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CreateStream(nuint bufferSize, int input);
    [DllImport(ImportName, EntryPoint = "opj_stream_set_read_function", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetReadFunction(IntPtr stream, IntPtr function);
    [DllImport(ImportName, EntryPoint = "opj_stream_set_skip_function", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetSkipFunction(IntPtr stream, IntPtr function);
    [DllImport(ImportName, EntryPoint = "opj_stream_set_seek_function", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetSeekFunction(IntPtr stream, IntPtr function);
    [DllImport(ImportName, EntryPoint = "opj_stream_set_user_data", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetUserData(IntPtr stream, IntPtr data, IntPtr free);
    [DllImport(ImportName, EntryPoint = "opj_stream_set_user_data_length", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetUserDataLength(IntPtr stream, ulong length);
    [DllImport(ImportName, EntryPoint = "opj_read_header", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ReadHeader(IntPtr stream, IntPtr codec, out IntPtr image);
    [DllImport(ImportName, EntryPoint = "opj_decode", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecodeImage(IntPtr codec, IntPtr stream, IntPtr image);
    [DllImport(ImportName, EntryPoint = "opj_end_decompress", CallingConvention = CallingConvention.Cdecl)]
    private static extern int EndDecode(IntPtr codec, IntPtr stream);
    [DllImport(ImportName, EntryPoint = "opj_image_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern void DestroyImage(IntPtr image);
    [DllImport(ImportName, EntryPoint = "opj_stream_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern void DestroyStream(IntPtr stream);
    [DllImport(ImportName, EntryPoint = "opj_destroy_codec", CallingConvention = CallingConvention.Cdecl)]
    private static extern void DestroyDecoder(IntPtr codec);
}
