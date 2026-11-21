using System.Buffers.Binary;
using System.Runtime.InteropServices;

// openjp2 loads only from beside the engine or from System32, never from the current or download
// folder or PATH, where a planted copy would run inside the app. Other platforms ignore System32.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]

namespace KillerPdf.Engine.Filters;

// OpenJPEG 2.5.4 C ABI. The packaged native libraries cover Windows x64, Linux x64 and macOS.
internal static class OpenJpegDecoder
{
    private const string ImportName = "openjp2";

    // On Windows the library loads by full path from the app's folder or its NuGet runtimes folder,
    // which also covers an engine loaded from memory with no folder of its own.
    static OpenJpegDecoder() =>
        NativeLibrary.SetDllImportResolver(typeof(OpenJpegDecoder).Assembly, (name, _, _) =>
        {
            if (name != ImportName || !OperatingSystem.IsWindows()) return IntPtr.Zero;
            string folder = AppContext.BaseDirectory;
            foreach (string candidate in new[] { Path.Combine(folder, "openjp2.dll"),
                Path.Combine(folder, "runtimes", "win-x64", "native", "openjp2.dll") })
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle)) return handle;
            return IntPtr.Zero;
        });

    internal static Jpeg2000DecodedImage Decode(byte[] codestream, Jpeg2000Shape shape,
        int reduction, int width, int height, int rowBytes, int length, int maximumParallelism)
    {
        if ((long)width * height * shape.Components * sizeof(int) > 256L * 1024 * 1024)
            throw new PdfFilterException("OpenJPEG sample allocation exceeds the configured safety limit.");
        // A whole-image decode holds a 32-bit sample per pixel in native memory. A tiled image is decoded
        // one tile at a time and packed straight into the result, so only one tile is held at once.
        byte[]? samples = TileCount(codestream) > 1
            ? Run(codestream, shape, reduction, width, height, rowBytes, length, maximumParallelism, byTile: true)
            : null;
        samples ??= Run(codestream, shape, reduction, width, height, rowBytes, length, maximumParallelism, byTile: false);
        return new Jpeg2000DecodedImage(samples!, width, height, shape.Components, shape.Bits);
    }

    // Returns null only when the tile path meets geometry it does not handle, such as subsampled
    // components or a tile whose decoded size differs from its header; the caller then decodes whole.
    internal static byte[]? Run(byte[] codestream, Jpeg2000Shape shape, int reduction, int width, int height,
        int rowBytes, int length, int maximumParallelism, bool byTile)
    {
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
            if (ReadHeader(stream, codec, out image) == 0)
                throw new PdfFilterException("OpenJPEG could not decode the JPEG 2000 codestream.");
            if (byTile)
                return DecodeTiles(codec, stream, image, shape, reduction, width, height, rowBytes, length);
            if (DecodeImage(codec, stream, image) == 0 || EndDecode(codec, stream) == 0)
                throw new PdfFilterException("OpenJPEG could not decode the JPEG 2000 codestream.");
            return Pack(image, shape, reduction, width, height, rowBytes, length);
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

    // Tile grid from the SIZ segment, which always directly follows the start-of-codestream marker.
    internal static long TileCount(byte[] codestream)
    {
        ReadOnlySpan<byte> siz = codestream;
        if (siz.Length < 42 || siz[0] != 0xFF || siz[1] != 0x4F || siz[2] != 0xFF || siz[3] != 0x51) return 0;
        uint xSize = BinaryPrimitives.ReadUInt32BigEndian(siz[8..]), ySize = BinaryPrimitives.ReadUInt32BigEndian(siz[12..]);
        uint tileWidth = BinaryPrimitives.ReadUInt32BigEndian(siz[24..]), tileHeight = BinaryPrimitives.ReadUInt32BigEndian(siz[28..]);
        uint tileX = BinaryPrimitives.ReadUInt32BigEndian(siz[32..]), tileY = BinaryPrimitives.ReadUInt32BigEndian(siz[36..]);
        if (tileWidth == 0 || tileHeight == 0 || xSize <= tileX || ySize <= tileY) return 0;
        return ((xSize - tileX + (long)tileWidth - 1) / tileWidth) * ((ySize - tileY + (long)tileHeight - 1) / tileHeight);
    }

    private static byte[]? DecodeTiles(IntPtr codec, IntPtr stream, IntPtr image, Jpeg2000Shape shape,
        int reduction, int width, int height, int rowBytes, int length)
    {
        if (Marshal.ReadInt32(image, 16) != shape.Components)
            throw new PdfFilterException("OpenJPEG returned an unexpected component count.");
        IntPtr components = Marshal.ReadIntPtr(image, 24);
        var signed = new bool[shape.Components];
        for (int component = 0; component < shape.Components; component++)
        {
            IntPtr item = components + component * 64;
            if (Marshal.ReadInt32(item, 0) != 1 || Marshal.ReadInt32(item, 4) != 1
                || Marshal.ReadInt32(item, 24) != shape.Bits) return null;
            signed[component] = Marshal.ReadInt32(item, 32) != 0;
        }

        var result = new byte[length];
        int maximum = (1 << shape.Bits) - 1;
        int sampleBytes = shape.Bits <= 8 ? 1 : 2;
        long divisor = 1L << Math.Min(reduction, 32);
        long originX = (shape.XOrigin + divisor - 1) / divisor;
        long originY = (shape.YOrigin + divisor - 1) / divisor;
        byte[] buffer = [];
        while (true)
        {
            if (ReadTileHeader(codec, stream, out uint index, out uint size, out int x0, out int y0,
                    out int x1, out int y1, out uint count, out int goOn) == 0)
                throw new PdfFilterException("OpenJPEG could not decode the JPEG 2000 codestream.");
            if (goOn == 0) break;
            long left = (x0 + divisor - 1) / divisor, top = (y0 + divisor - 1) / divisor;
            long tileWidth = (x1 + divisor - 1) / divisor - left, tileHeight = (y1 + divisor - 1) / divisor - top;
            if (count != shape.Components || tileWidth <= 0 || tileHeight <= 0
                || tileWidth * tileHeight * shape.Components * sampleBytes != size)
                return null;
            if (buffer.Length < size) buffer = new byte[size];
            if (DecodeTileData(codec, index, buffer, size, stream) == 0)
                throw new PdfFilterException("OpenJPEG could not decode the JPEG 2000 codestream.");

            int plane = checked((int)(tileWidth * tileHeight * sampleBytes));
            // Columns and rows that land inside the output, worked out once per tile instead of per sample.
            int firstColumn = (int)Math.Max(0, originX - left);
            int endColumn = (int)Math.Min(tileWidth, width + originX - left);
            int firstRow = (int)Math.Max(0, originY - top);
            int endRow = (int)Math.Min(tileHeight, height + originY - top);
            if (firstColumn >= endColumn || firstRow >= endRow) continue;
            int outputLeft = (int)(left - originX);
            for (int component = 0; component < shape.Components; component++)
            {
                bool isSigned = signed[component];
                int bias = isSigned ? 1 << (shape.Bits - 1) : 0;
                for (int row = firstRow; row < endRow; row++)
                {
                    int y = (int)(top - originY) + row;
                    int source = component * plane + (int)(row * tileWidth) * sampleBytes;
                    if (shape.Bits == 8)
                    {
                        // OpenJPEG already clamped each sample to its range, so 8-bit samples copy directly.
                        int target = y * rowBytes + (outputLeft + firstColumn) * shape.Components + component;
                        byte flip = isSigned ? (byte)0x80 : (byte)0;
                        for (int column = firstColumn; column < endColumn; column++, target += shape.Components)
                            result[target] = (byte)(buffer[source + column] ^ flip);
                        continue;
                    }
                    for (int column = firstColumn; column < endColumn; column++)
                    {
                        int offset = source + column * sampleBytes;
                        int raw = sampleBytes == 1
                            ? isSigned ? (sbyte)buffer[offset] : buffer[offset]
                            : isSigned ? BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset))
                                : BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset));
                        Store(result, rowBytes, shape.Components, shape.Bits, outputLeft + column, y, component,
                            Math.Clamp(raw + bias, 0, maximum));
                    }
                }
            }
        }
        if (EndDecode(codec, stream) == 0)
            throw new PdfFilterException("OpenJPEG could not decode the JPEG 2000 codestream.");
        return result;
    }

    private static void Store(byte[] result, int rowBytes, int components, int bits,
        int x, int y, int component, int value)
    {
        if (bits == 8)
        {
            result[y * rowBytes + x * components + component] = (byte)value;
            return;
        }
        long bitOffset = (long)y * rowBytes * 8 + (x * components + component) * bits;
        for (int bit = bits - 1; bit >= 0; bit--, bitOffset++)
            if ((value & (1 << bit)) != 0)
                result[bitOffset / 8] |= (byte)(1 << (7 - (int)(bitOffset % 8)));
    }

    private static byte[] Pack(IntPtr image, Jpeg2000Shape shape, int reduction,
        int width, int height, int rowBytes, int length)
    {
        if (Marshal.ReadInt32(image, 16) != shape.Components)
            throw new PdfFilterException("OpenJPEG returned an unexpected component count.");
        IntPtr components = Marshal.ReadIntPtr(image, 24);
        var result = new byte[length];
        int maximum = (1 << shape.Bits) - 1;
        int[]? row = null;
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
            // OpenJPEG keeps a 32-bit sample per pixel. Copying one source row at a time keeps a large
            // image from holding a second full-size managed copy of every component.
            if (row is null || row.Length < columns) row = new int[columns];
            long loadedRow = -1;
            for (int y = (int)Math.Max(0, top); y < height && (y - top) / stepY < rows; y++)
            {
                long sourceRow = (y - top) / stepY;
                if (sourceRow != loadedRow)
                {
                    Marshal.Copy(data + (nint)(sourceRow * columns * sizeof(int)), row, 0, columns);
                    loadedRow = sourceRow;
                }
                for (int x = (int)Math.Max(0, left); x < width && (x - left) / stepX < columns; x++)
                {
                    Store(result, rowBytes, shape.Components, bits, x, y, component,
                        Math.Clamp(row[(x - left) / stepX] + bias, 0, maximum));
                }
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
    [DllImport(ImportName, EntryPoint = "opj_read_tile_header", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ReadTileHeader(IntPtr codec, IntPtr stream, out uint tileIndex, out uint dataSize,
        out int x0, out int y0, out int x1, out int y1, out uint componentCount, out int shouldGoOn);
    [DllImport(ImportName, EntryPoint = "opj_decode_tile_data", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecodeTileData(IntPtr codec, uint tileIndex, byte[] data, uint dataSize, IntPtr stream);
    [DllImport(ImportName, EntryPoint = "opj_end_decompress", CallingConvention = CallingConvention.Cdecl)]
    private static extern int EndDecode(IntPtr codec, IntPtr stream);
    [DllImport(ImportName, EntryPoint = "opj_image_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern void DestroyImage(IntPtr image);
    [DllImport(ImportName, EntryPoint = "opj_stream_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern void DestroyStream(IntPtr stream);
    [DllImport(ImportName, EntryPoint = "opj_destroy_codec", CallingConvention = CallingConvention.Cdecl)]
    private static extern void DestroyDecoder(IntPtr codec);
}
