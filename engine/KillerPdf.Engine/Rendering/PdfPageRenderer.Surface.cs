namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    // Drawing stays in page coordinates. Only pixel storage is local to the surface.
    private sealed class RasterSurface(byte[] data, int left, int top, int width, int height)
    {
        internal byte[] Data { get; } = data;
        internal byte[]? Ink { get; private set; }
        internal byte[]? InkAlpha { get; private set; }
        internal byte[]? GroupAlpha { get; private set; }
        internal int Left { get; } = left;
        internal int Top { get; } = top;
        internal int Width { get; } = width;
        internal int Height { get; } = height;
        internal int Right => Left + Width;
        internal int Bottom => Top + Height;
        internal int Length => checked(Width * Height * 4);
        // CMYK surfaces store native ink in Data and alpha in a compact plane.
        // Native color reads use ReadColor; RGB hot paths access Data directly.
        internal ref byte this[int offset] => ref Data[offset];
        internal ref byte Alpha(int offset) => ref (Ink is null
            ? ref Data[offset + 3] : ref InkAlpha![offset / 4]);
        internal int Offset(int x, int y) => ((y - Top) * Width + x - Left) * 4;
        internal bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

        internal static RasterSurface Rent((int Left, int Top, int Right, int Bottom) bounds, bool cmyk = false)
        {
            int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            var surface = new RasterSurface(RasterBuffers.Rent(checked(width * height * 4)),
                bounds.Left, bounds.Top, width, height);
            try
            {
                if (cmyk) surface.EnableInk(Color.White, preserveAlpha: false);
                return surface;
            }
            catch
            {
                surface.Return();
                throw;
            }
        }

        internal void EnableInk(Color background, bool preserveAlpha = true)
        {
            InkAlpha = RasterBuffers.Rent(Length / 4);
            uint ink = ColorInk(background);
            for (int offset = 0; offset < Length; offset += 4)
            {
                InkAlpha[offset / 4] = preserveAlpha ? Data[offset + 3] : (byte)0;
                WriteInk(Data, offset, ink);
            }
            Ink = Data;
        }

        internal void TrackGroupAlpha()
        {
            GroupAlpha = RasterBuffers.Rent(Length / 4);
            Array.Clear(GroupAlpha, 0, Length / 4);
        }

        internal Color ReadColor(int offset) => Ink is null
            ? new(Data[offset + 2], Data[offset + 1], Data[offset])
            : InkColor(ReadInk(Ink, offset));

        internal void ReleaseInk()
        {
            if (InkAlpha is null) return;
            RasterBuffers.Return(InkAlpha);
            InkAlpha = null;
            Ink = null;
        }

        internal void ConvertToBgra()
        {
            if (Ink is null) return;
            for (int offset = 0; offset < Length; offset += 4)
            {
                Color color = ReadColor(offset);
                Data[offset] = color.Blue;
                Data[offset + 1] = color.Green;
                Data[offset + 2] = color.Red;
                Data[offset + 3] = InkAlpha![offset / 4];
            }
            Ink = null;
        }

        internal void CopyFrom(RasterSurface source)
        {
            for (int y = Top; y < Bottom; y++)
            {
                if ((Ink is null) == (source.Ink is null))
                {
                    source.Data.AsSpan(source.Offset(Left, y), Width * 4)
                        .CopyTo(Data.AsSpan(Offset(Left, y), Width * 4));
                    if (Ink is not null)
                        source.InkAlpha!.AsSpan(source.Offset(Left, y) / 4, Width)
                            .CopyTo(InkAlpha!.AsSpan(Offset(Left, y) / 4, Width));
                }
                else
                    for (int x = Left; x < Right; x++)
                    {
                        int offset = Offset(x, y), sourceOffset = source.Offset(x, y);
                        Color color = source.ReadColor(sourceOffset);
                        if (Ink is not null) WriteInk(Ink, offset, ColorInk(color));
                        else
                        {
                            Data[offset] = color.Blue;
                            Data[offset + 1] = color.Green;
                            Data[offset + 2] = color.Red;
                        }
                        Alpha(offset) = source.Alpha(sourceOffset);
                    }
            }
        }

        internal void Return()
        {
            if (GroupAlpha is not null)
            {
                RasterBuffers.Return(GroupAlpha);
                GroupAlpha = null;
            }
            ReleaseInk();
            RasterBuffers.Return(Data);
        }
    }
}
