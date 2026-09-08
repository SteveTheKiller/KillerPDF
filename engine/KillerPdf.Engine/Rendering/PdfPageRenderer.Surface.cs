namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    // Drawing stays in page coordinates. Only pixel storage is local to the surface.
    private sealed class RasterSurface(byte[] data, int left, int top, int width, int height)
    {
        internal byte[] Data { get; } = data;
        internal byte[]? Ink { get; private set; }
        internal byte[]? InkAlpha { get; private set; }
        private byte _constantAlpha;
        internal byte[]? GroupAlpha { get; private set; }
        internal int Left { get; } = left;
        internal int Top { get; } = top;
        internal int Width { get; } = width;
        internal int Height { get; } = height;
        internal int Right => Left + Width;
        internal int Bottom => Top + Height;
        internal int Length => checked(Width * Height * 4);
        // CMYK surfaces store native ink in Data and materialize alpha only when it varies.
        // Native color reads use ReadColor; RGB hot paths access Data directly.
        internal ref byte this[int offset] => ref Data[offset];
        internal byte Alpha(int offset) => Ink is null ? Data[offset + 3]
            : InkAlpha is null ? _constantAlpha : InkAlpha[offset / 4];
        internal void SetAlpha(int offset, byte alpha)
        {
            if (Ink is null)
            {
                Data[offset + 3] = alpha;
                return;
            }
            if (InkAlpha is null)
            {
                if (alpha == _constantAlpha) return;
                InkAlpha = RasterBuffers.Rent(Length / 4);
                InkAlpha.AsSpan(0, Length / 4).Fill(_constantAlpha);
            }
            InkAlpha[offset / 4] = alpha;
        }
        internal void CopyInkAlphaTo(int offset, Span<byte> destination)
        {
            if (InkAlpha is null) destination.Fill(_constantAlpha);
            else InkAlpha.AsSpan(offset / 4, destination.Length).CopyTo(destination);
        }
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
            _constantAlpha = preserveAlpha && Length > 0 ? Data[3] : (byte)0;
            Ink = Data;
            uint ink = ColorInk(background);
            for (int offset = 0; offset < Length; offset += 4)
            {
                SetAlpha(offset, preserveAlpha ? Data[offset + 3] : (byte)0);
                WriteInk(Data, offset, ink);
            }
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
            if (InkAlpha is not null) RasterBuffers.Return(InkAlpha);
            InkAlpha = null;
            Ink = null;
            _constantAlpha = 0;
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
                Data[offset + 3] = Alpha(offset);
            }
            Ink = null;
        }

        internal void CopyFrom(RasterSurface source)
        {
            if (ReferenceEquals(this, source)) return;
            if (Ink is not null && source.Ink is not null)
            {
                if (source.InkAlpha is null)
                {
                    if (InkAlpha is not null) RasterBuffers.Return(InkAlpha);
                    InkAlpha = null;
                    _constantAlpha = source._constantAlpha;
                }
                else InkAlpha ??= RasterBuffers.Rent(Length / 4);
            }
            for (int y = Top; y < Bottom; y++)
            {
                if ((Ink is null) == (source.Ink is null))
                {
                    source.Data.AsSpan(source.Offset(Left, y), Width * 4)
                        .CopyTo(Data.AsSpan(Offset(Left, y), Width * 4));
                    if (Ink is not null && source.InkAlpha is not null)
                        source.InkAlpha.AsSpan(source.Offset(Left, y) / 4, Width)
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
                        SetAlpha(offset, source.Alpha(sourceOffset));
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
