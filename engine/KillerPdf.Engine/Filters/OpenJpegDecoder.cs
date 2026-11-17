using System.Runtime.InteropServices;

namespace KillerPdf.Engine.Filters;

// OpenJPEG 2.5.4 C ABI. The packaged native library targets Windows x64.
internal static class OpenJpegDecoder
{
    private const string ImportName = "openjp2";

    internal static Jpeg2000DecodedImage Decode(ReadOnlySpan<byte> codestream, Jpeg2000Shape shape,
        int reduction, int width, int height, int rowBytes, int length, int maximumParallelism)
    {
        if ((long)width * height * shape.Components * sizeof(int) > 256L * 1024 * 1024)
            throw new PdfFilterException("OpenJPEG sample allocation exceeds the configured safety limit.");
        string file = Path.GetTempFileName();
        IntPtr codec = IntPtr.Zero, stream = IntPtr.Zero, image = IntPtr.Zero;
        try
        {
            using (FileStream input = File.OpenWrite(file))
                input.Write(codestream);
            codec = CreateDecoder(0);
            stream = CreateFileStream(file, 1);
            var parameters = new DecoderParameters();
            SetDefaults(ref parameters);
            parameters.Reduction = (uint)reduction;
            if (codec == IntPtr.Zero || stream == IntPtr.Zero || Setup(codec, ref parameters) == 0)
                throw new PdfFilterException("OpenJPEG could not initialize the JPEG 2000 decoder.");
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
            File.Delete(file);
        }
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
    [DllImport(ImportName, EntryPoint = "opj_stream_create_default_file_stream", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CreateFileStream([MarshalAs(UnmanagedType.LPStr)] string path, int read);
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
