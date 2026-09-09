namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    // Drawing stays in page coordinates. Only pixel storage is local to the surface.
    private sealed class RasterSurface(byte[] data, int left, int top, int width, int height)
    {
        internal byte[] Data { get; } = data;
        internal byte[]? Ink { get; private set; }
        internal PdfColorTransform? InkProfile { get; private set; }
        internal PdfColorTransform? RgbProfile { get; private set; }
        internal PdfColorTransform? BlendProfile => InkProfile ?? RgbProfile;
        // Without a profile, paints use a color's quantized channels only; its XYZ
        // connection value is never read.
        internal bool HasProfile => InkProfile is not null || RgbProfile is not null;
        internal PdfColorTransform? InputProfile => _inputProfile ?? BlendProfile;
        private PdfColorTransform? _compositeProfile;
        private PdfColorTransform? _inputProfile;
        private Color _lastInkColor;
        private uint _lastInk;
        private bool _hasInkColor;
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
        /// <summary>Sets the alpha of <paramref name="count"/> consecutive pixels, like SetAlpha per pixel.</summary>
        internal void SetAlphaRun(int offset, int count, byte alpha)
        {
            if (Ink is null)
            {
                for (int index = 0; index < count; index++) Data[offset + index * 4 + 3] = alpha;
                return;
            }
            if (InkAlpha is null)
            {
                if (alpha == _constantAlpha) return;
                InkAlpha = RasterBuffers.Rent(Length / 4);
                InkAlpha.AsSpan(0, Length / 4).Fill(_constantAlpha);
            }
            InkAlpha.AsSpan(offset / 4, count).Fill(alpha);
        }
        internal void CopyInkAlphaTo(int offset, Span<byte> destination)
        {
            if (InkAlpha is null) destination.Fill(_constantAlpha);
            else InkAlpha.AsSpan(offset / 4, destination.Length).CopyTo(destination);
        }
        internal int Offset(int x, int y) => ((y - Top) * Width + x - Left) * 4;
        internal bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

        internal static RasterSurface Rent((int Left, int Top, int Right, int Bottom) bounds, bool cmyk = false,
            PdfColorTransform? profile = null)
        {
            int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            var surface = new RasterSurface(RasterBuffers.Rent(checked(width * height * 4)),
                bounds.Left, bounds.Top, width, height);
            try
            {
                if (cmyk) surface.EnableInk(Color.White, preserveAlpha: false, profile);
                else if (profile is { Components: 1 or 3 }) surface.EnableRgb(Color.White, profile, preserveAlpha: false);
                return surface;
            }
            catch
            {
                surface.Return();
                throw;
            }
        }

        internal void EnableInk(Color background, bool preserveAlpha = true, PdfColorTransform? profile = null)
        {
            InkProfile = profile;
            _hasInkColor = false;
            _hasReadInk = false;
            _constantAlpha = preserveAlpha && Length > 0 ? Data[3] : (byte)0;
            Ink = Data;
            uint ink = ColorInk(background, profile);
            if (preserveAlpha)
            {
                // Alpha varies only when some pixel differs from the first one; materialize
                // InkAlpha from the existing alpha bytes before the ink overwrites them.
                int pixelCount = Length / 4;
                int varying = -1;
                for (int pixel = 0; pixel < pixelCount; pixel++)
                {
                    if (Data[pixel * 4 + 3] != _constantAlpha)
                    {
                        varying = pixel;
                        break;
                    }
                }
                if (varying >= 0)
                {
                    InkAlpha = RasterBuffers.Rent(pixelCount);
                    InkAlpha.AsSpan(0, varying).Fill(_constantAlpha);
                    for (int pixel = varying; pixel < pixelCount; pixel++)
                        InkAlpha[pixel] = Data[pixel * 4 + 3];
                }
            }
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(Data.AsSpan(0, Length)).Fill(ink);
        }

        /// <summary>True when every pixel of a storage row still holds the given blank ink or RGBA value and alpha.</summary>
        internal bool RowIsBlank(int row, uint blank, byte blankAlpha)
        {
            int start = row * Width;
            if (System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
                Data.AsSpan(start * 4, Width * 4)).IndexOfAnyExcept(blank) >= 0) return false;
            if (Ink is null) return true;
            if (InkAlpha is null) return _constantAlpha == blankAlpha;
            return InkAlpha.AsSpan(start, Width).IndexOfAnyExcept(blankAlpha) < 0;
        }

        /// <summary>True when one pixel still holds the given blank ink or RGBA value and alpha.</summary>
        internal bool PixelIsBlank(int offset, uint blank, byte blankAlpha) =>
            ReadInk(Data, offset) == blank && (Ink is null || Alpha(offset) == blankAlpha);

        internal void EnableRgb(Color background, PdfColorTransform profile, bool preserveAlpha = true)
        {
            RgbProfile = profile;
            Color samples = ColorRgb(background, profile);
            for (int offset = 0; offset < Length; offset += 4)
            {
                Data[offset] = samples.Blue;
                Data[offset + 1] = samples.Green;
                Data[offset + 2] = samples.Red;
                if (!preserveAlpha) Data[offset + 3] = 0;
            }
        }

        internal void TrackGroupAlpha()
        {
            GroupAlpha = RasterBuffers.Rent(Length / 4);
            Array.Clear(GroupAlpha, 0, Length / 4);
        }

        internal InputProfileScope PrepareComposite(RasterSurface destination, int intent, HashSet<string> diagnostics)
        {
            // The group's internal blend space stays fixed. Only conversion of its completed
            // result uses the rendering intent captured at the invoking Do operator.
            if (ReferenceEquals(BlendProfile, destination.BlendProfile)) return default;
            if (BlendProfile is PdfIccProfileTransform profile)
            {
                _compositeProfile = profile.ForIntent(intent);
                if (_compositeProfile is null)
                    diagnostics.Add("The group rendering intent could not be used; its blend-profile conversion was used.");
            }
            _hasReadInk = false;
            if (destination.BlendProfile is not PdfIccProfileTransform targetProfile) return default;
            PdfIccProfileTransform? mapped = targetProfile.ForIntent(intent);
            if (ReferenceEquals(mapped, targetProfile)) return default;
            if (mapped is not { CanConvertFromXyz: true })
            {
                diagnostics.Add("The destination group rendering intent could not be used; its blend-profile conversion was used.");
                return default;
            }
            var scope = new InputProfileScope(destination, destination._inputProfile);
            destination._inputProfile = mapped;
            destination._hasInkColor = false;
            return scope;
        }

        internal readonly struct InputProfileScope(RasterSurface? surface, PdfColorTransform? previous) : IDisposable
        {
            public void Dispose()
            {
                if (surface is null) return;
                surface._inputProfile = previous;
                surface._hasInkColor = false;
            }
        }

        internal InputProfileScope PrepareInput(int intent)
        {
            if (BlendProfile is not PdfIccProfileTransform profile) return default;
            PdfIccProfileTransform? mapped = profile.ForIntent(intent);
            if (mapped is not { CanConvertFromXyz: true })
            {
                // Selecting a paint state does not itself require color conversion.
                // Native component paints can use a forward-only blend profile.
                mapped = profile;
            }
            if (ReferenceEquals(mapped, InputProfile)) return default;
            var scope = new InputProfileScope(this, _inputProfile);
            _inputProfile = mapped;
            _hasInkColor = false;
            return scope;
        }

        internal Color ReadColor(int offset, RasterSurface? destination = null) => Ink is null
            ? ColorFromRgb(new(Data[offset + 2], Data[offset + 1], Data[offset]))
            : ColorFromInk(ReadInk(Ink, offset), destination);

        internal Color ColorFromRgb(Color samples)
        {
            if (RgbProfile is null) return samples;
            uint key = (uint)(samples.Red << 16 | samples.Green << 8 | samples.Blue);
            if (_hasReadInk && key == _lastReadInk) return _lastReadColor;
            _lastReadColor = ProfileRgbToDisplay(samples, _compositeProfile ?? RgbProfile);
            _lastReadInk = key;
            _hasReadInk = true;
            return _lastReadColor;
        }

        internal Color GetRgb(in Color color)
        {
            if (RgbProfile is null) return color;
            if (!_hasInkColor || color != _lastInkColor)
            {
                Color samples = ColorRgb(color, _inputProfile ?? RgbProfile);
                _lastInk = (uint)(samples.Red << 16 | samples.Green << 8 | samples.Blue);
                _lastInkColor = color;
                _hasInkColor = true;
            }
            return new Color((byte)(_lastInk >> 16), (byte)(_lastInk >> 8), (byte)_lastInk);
        }

        private uint _lastReadInk;
        private Color _lastReadColor;
        private bool _hasReadInk;
        internal Color ColorFromInk(uint ink, RasterSurface? destination = null)
        {
            // A matching native destination consumes the ink directly; display conversion is unnecessary.
            if (InkProfile is null || destination?.Ink is not null
                && ReferenceEquals(InkProfile, destination.InkProfile)) return InkColor(ink);
            if (_hasReadInk && ink == _lastReadInk) return _lastReadColor;
            _lastReadColor = InkColor(ink, _compositeProfile ?? InkProfile);
            _lastReadInk = ink;
            _hasReadInk = true;
            return _lastReadColor;
        }

        internal uint GetInk(in Color color)
        {
            if (InkProfile is null) return ColorInk(color);
            if (color.Ink is uint ink && (color.InkProfile is null
                || ReferenceEquals(color.InkProfile, InkProfile))) return ink;
            if (_hasInkColor && color == _lastInkColor) return _lastInk;
            _lastInk = ColorInk(color, _inputProfile ?? InkProfile);
            _lastInkColor = color;
            _hasInkColor = true;
            return _lastInk;
        }

        internal void ReleaseInk()
        {
            if (InkAlpha is not null) RasterBuffers.Return(InkAlpha);
            InkAlpha = null;
            Ink = null;
            InkProfile = null;
            RgbProfile = null;
            _hasInkColor = false;
            _hasReadInk = false;
            _constantAlpha = 0;
        }

        internal void ConvertToBgra(CancellationToken cancellationToken = default)
        {
            if (Ink is null)
            {
                if (RgbProfile is null) return;
                for (int offset = 0; offset < Length; offset += 4)
                {
                    if ((offset & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    Color color = ReadColor(offset);
                    Data[offset] = color.Blue;
                    Data[offset + 1] = color.Green;
                    Data[offset + 2] = color.Red;
                }
                RgbProfile = null;
                return;
            }
            if (InkProfile is null)
            {
                // Unprofiled ink: the same table lookups InkColor makes, applied to the ink
                // bytes in place, with the alpha plane or constant read directly.
                byte[] data = Data;
                byte[] table = InkDisplayTable;
                byte[]? inkAlpha = InkAlpha;
                byte constantAlpha = _constantAlpha;
                int length = Length;
                for (int offset = 0; offset < length; offset += 4)
                {
                    if ((offset & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    int blackRow = data[offset + 3] * 256;
                    byte red = table[blackRow + data[offset]];
                    byte green = table[blackRow + data[offset + 1]];
                    byte blue = table[blackRow + data[offset + 2]];
                    data[offset] = blue;
                    data[offset + 1] = green;
                    data[offset + 2] = red;
                    data[offset + 3] = inkAlpha is null ? constantAlpha : inkAlpha[offset >> 2];
                }
                Ink = null;
                return;
            }
            uint previousInk = 0;
            Color previousColor = default;
            Span<ulong> colors = InkProfile is null ? [] : stackalloc ulong[4096];
            Span<uint> occupied = InkProfile is null ? [] : stackalloc uint[128];
            occupied.Clear();
            for (int offset = 0; offset < Length; offset += 4)
            {
                if ((offset & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                uint ink = ReadInk(Ink, offset);
                Color color;
                if (InkProfile is null) color = InkColor(ink);
                else if (offset > 0 && ink == previousInk) color = previousColor;
                else
                {
                    int slot = (int)((ink * 2654435761u) >> 20);
                    uint bit = 1u << (slot & 31);
                    if ((occupied[slot >> 5] & bit) != 0 && (uint)(colors[slot] >> 32) == ink)
                    {
                        uint rgb = (uint)colors[slot];
                        color = new Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
                    }
                    else
                    {
                        color = ProfileInkToDisplay(ink, InkProfile);
                        colors[slot] = ((ulong)ink << 32) | (uint)(color.Red << 16 | color.Green << 8 | color.Blue);
                        occupied[slot >> 5] |= bit;
                    }
                }
                previousInk = ink;
                previousColor = color;
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
                if ((Ink is null) == (source.Ink is null) && ReferenceEquals(BlendProfile, source.BlendProfile))
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
                        if (Ink is not null) WriteInk(Ink, offset, GetInk(color));
                        else
                        {
                            color = ColorRgb(color, RgbProfile);
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
