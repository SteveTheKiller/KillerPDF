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
            for (int offset = 0; offset < Length; offset += 4)
            {
                SetAlpha(offset, preserveAlpha ? Data[offset + 3] : (byte)0);
                WriteInk(Data, offset, ink);
            }
        }

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
