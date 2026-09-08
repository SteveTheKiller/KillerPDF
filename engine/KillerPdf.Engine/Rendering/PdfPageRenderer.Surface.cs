namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    // Drawing stays in page coordinates. Only pixel storage is local to the surface.
    private sealed class RasterSurface(byte[] data, int left, int top, int width, int height)
    {
        internal byte[] Data { get; } = data;
        internal int Left { get; } = left;
        internal int Top { get; } = top;
        internal int Width { get; } = width;
        internal int Height { get; } = height;
        internal int Right => Left + Width;
        internal int Bottom => Top + Height;
        internal int Length => checked(Width * Height * 4);
        internal ref byte this[int offset] => ref Data[offset];
        internal int Offset(int x, int y) => ((y - Top) * Width + x - Left) * 4;
        internal bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

        internal static RasterSurface Rent((int Left, int Top, int Right, int Bottom) bounds)
        {
            int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            return new RasterSurface(RasterBuffers.Rent(checked(width * height * 4)),
                bounds.Left, bounds.Top, width, height);
        }

        internal void CopyFrom(RasterSurface source)
        {
            for (int y = Top; y < Bottom; y++)
                source.Data.AsSpan(source.Offset(Left, y), Width * 4)
                    .CopyTo(Data.AsSpan(Offset(Left, y), Width * 4));
        }

        internal void Return() => RasterBuffers.Return(Data);
    }
}
