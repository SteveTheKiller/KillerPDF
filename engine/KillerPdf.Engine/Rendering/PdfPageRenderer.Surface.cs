namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    // Drawing stays in page coordinates. Only pixel storage is local to the surface.
    private sealed class RasterSurface(byte[] data, int left, int top, int width, int height,
        bool pdf20BlendEndpoints = true)
    {
        internal bool Pdf20BlendEndpoints { get; } = pdf20BlendEndpoints;
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
        private readonly Lock _inkAlphaSync = new();
        private readonly Lock _spotSync = new();
        private byte[]? _spotBaseInk;
        private Dictionary<string, byte[]>? _spotPlates;
        private byte[]? _rgbSpotValid;
        private bool _rgbSpotShadow;
        internal bool HasSpotPlates => _spotPlates is { Count: > 0 };
        internal bool HasRgbSpotShadow => _rgbSpotShadow;
        private byte[]? _inkAlpha;
        internal byte[]? InkAlpha
        {
            get => System.Threading.Volatile.Read(ref _inkAlpha);
            private set => System.Threading.Volatile.Write(ref _inkAlpha, value);
        }
        private byte _constantAlpha;
        internal byte[]? GroupAlpha { get; private set; }
        internal byte[]? GroupShape { get; private set; }
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
        internal byte Alpha(int offset)
        {
            if (Ink is null) return Data[offset + 3];
            byte[]? inkAlpha = InkAlpha;
            return inkAlpha is null ? _constantAlpha : inkAlpha[offset / 4];
        }
        private byte[] EnsureInkAlpha()
        {
            byte[]? inkAlpha = InkAlpha;
            if (inkAlpha is not null) return inkAlpha;
            lock (_inkAlphaSync)
            {
                inkAlpha = InkAlpha;
                if (inkAlpha is null)
                {
                    inkAlpha = RasterBuffers.Rent(Length / 4);
                    inkAlpha.AsSpan(0, Length / 4).Fill(_constantAlpha);
                    InkAlpha = inkAlpha;
                }
            }
            return inkAlpha;
        }
        internal void SetAlpha(int offset, byte alpha)
        {
            if (Ink is null)
            {
                Data[offset + 3] = alpha;
                return;
            }
            byte[]? inkAlpha = InkAlpha;
            if (inkAlpha is null && alpha == _constantAlpha) return;
            (inkAlpha ?? EnsureInkAlpha())[offset / 4] = alpha;
        }
        /// <summary>Sets the alpha of <paramref name="count"/> consecutive pixels, like SetAlpha per pixel.</summary>
        internal void SetAlphaRun(int offset, int count, byte alpha)
        {
            if (Ink is null)
            {
                for (int index = 0; index < count; index++) Data[offset + index * 4 + 3] = alpha;
                return;
            }
            byte[]? inkAlpha = InkAlpha;
            if (inkAlpha is null && alpha == _constantAlpha) return;
            (inkAlpha ?? EnsureInkAlpha()).AsSpan(offset / 4, count).Fill(alpha);
        }
        internal void CopyInkAlphaTo(int offset, Span<byte> destination)
        {
            if (InkAlpha is null) destination.Fill(_constantAlpha);
            else InkAlpha.AsSpan(offset / 4, destination.Length).CopyTo(destination);
        }
        internal int Offset(int x, int y) => ((y - Top) * Width + x - Left) * 4;
        internal bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

        internal static RasterSurface Rent((int Left, int Top, int Right, int Bottom) bounds, bool cmyk = false,
            PdfColorTransform? profile = null, bool pdf20BlendEndpoints = true)
        {
            int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            var surface = new RasterSurface(RasterBuffers.Rent(checked(width * height * 4)),
                bounds.Left, bounds.Top, width, height, pdf20BlendEndpoints);
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

        internal void TrackGroupShape()
        {
            GroupShape = RasterBuffers.Rent(Length / 4);
            Array.Clear(GroupShape, 0, Length / 4);
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

        internal void EnableRgbSpotShadow()
        {
            if (Ink is not null || _rgbSpotShadow) return;
            _rgbSpotShadow = true;
            _spotBaseInk = RasterBuffers.Rent(Length);
            Array.Clear(_spotBaseInk, 0, Length);
            _spotPlates = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            _rgbSpotValid = RasterBuffers.Rent(Length / 4);
            _rgbSpotValid.AsSpan(0, Length / 4).Fill(1);
        }

        internal void TrackRgbProcessPixel(int offset, in Color color, uint? resolvedInk)
        {
            if (!_rgbSpotShadow) return;
            lock (_spotSync)
            {
                uint? sourceInk = resolvedInk ?? color.Ink;
                if (sourceInk is not uint ink || (color.OverprintComponents & ~15) != 0)
                {
                    _rgbSpotValid![offset / 4] = 0;
                    return;
                }
                WriteInk(_spotBaseInk!, offset, ink);
                foreach (byte[] plate in _spotPlates!.Values) WriteInk(plate, offset, 0);
                _rgbSpotValid![offset / 4] = 1;
            }
        }

        internal void TrackRgbProcessRun(int offset, int count, uint ink)
        {
            if (!_rgbSpotShadow) return;
            lock (_spotSync)
            {
                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
                    _spotBaseInk!.AsSpan(offset, count * 4)).Fill(ink);
                foreach (byte[] plate in _spotPlates!.Values)
                    plate.AsSpan(offset, count * 4).Clear();
                _rgbSpotValid!.AsSpan(offset / 4, count).Fill(1);
            }
        }

        internal void InvalidateRgbSpotPixel(int offset)
        {
            if (_rgbSpotShadow) _rgbSpotValid![offset / 4] = 0;
        }

        internal bool TryPaintRgbProcessOverprint(int offset, in Color color, uint sourceInk)
        {
            int components = color.OverprintComponents;
            if (!_rgbSpotShadow || (components & (16 | PreserveNamedSpots)) == 0
                || (components & ~(31 | PreserveNamedSpots)) != 0)
                return false;
            lock (_spotSync)
            {
                if (_rgbSpotValid![offset / 4] == 0) return false;
                foreach (byte[] plate in _spotPlates!.Values)
                    if (ReadInk(plate, offset) != 0) return false;
                int channels = (components & 16) != 0 ? components & 15 : 0;
                uint preserve = (channels & 1) != 0 ? 0x000000ffu : 0;
                if ((channels & 2) != 0) preserve |= 0x0000ff00u;
                if ((channels & 4) != 0) preserve |= 0x00ff0000u;
                if ((channels & 8) != 0) preserve |= 0xff000000u;
                uint backdropInk = ReadInk(_spotBaseInk!, offset);
                WriteInk(_spotBaseInk!, offset, sourceInk & ~preserve | backdropInk & preserve);
                ComposeSpotPixel(offset);
                return true;
            }
        }

        internal bool TryPaintRgbSpot(int offset, in Color color, bool overprint, uint sourceInk)
        {
            if (!_rgbSpotShadow || _rgbSpotValid![offset / 4] == 0) return false;
            PaintSpot(offset, color, overprint, sourceInk);
            return true;
        }

        internal bool CanPaintSpot(int offset) => Ink is not null
            || _rgbSpotShadow && _rgbSpotValid![offset / 4] != 0;

        internal sealed record PreparedSpotPaint(string Name, byte[] Plate);

        internal PreparedSpotPaint PrepareSpotPaint(string name)
        {
            lock (_spotSync)
            {
                if (_spotBaseInk is null)
                {
                    _spotBaseInk = RasterBuffers.Rent(Length);
                    Data.AsSpan(0, Length).CopyTo(_spotBaseInk);
                    _spotPlates = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                }
                if (!_spotPlates!.TryGetValue(name, out byte[]? plate))
                {
                    plate = RasterBuffers.Rent(Length);
                    Array.Clear(plate, 0, Length);
                    _spotPlates.Add(name, plate);
                }
                return new PreparedSpotPaint(name, plate);
            }
        }

        internal void PaintSpotPrepared(int offset, in Color color, bool overprint,
            uint sourceInk, PreparedSpotPaint prepared)
        {
            if (!overprint)
            {
                WriteInk(_spotBaseInk!, offset, 0);
                foreach (byte[] existing in _spotPlates!.Values)
                    WriteInk(existing, offset, 0);
            }
            if (color.ProcessInk is uint processInk)
            {
                uint mask = 0;
                for (int channel = 0; channel < 4; channel++)
                    if ((color.SpotProcessMask & (1 << channel)) != 0)
                        mask |= 0xffu << (channel * 8);
                uint baseInk = ReadInk(_spotBaseInk!, offset);
                WriteInk(_spotBaseInk!, offset, baseInk & ~mask | processInk & mask);
            }
            WriteInk(prepared.Plate, offset, color.SpotTint <= 0 ? 0 : color.SpotInk ?? sourceInk);
            ComposeSpotPixel(offset);
        }

        internal void PaintSpot(int offset, in Color color, bool overprint, uint sourceInk)
        {
            lock (_spotSync)
            {
                if (_spotBaseInk is null)
                {
                    _spotBaseInk = RasterBuffers.Rent(Length);
                    Data.AsSpan(0, Length).CopyTo(_spotBaseInk);
                    _spotPlates = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                }
                if (!overprint)
                {
                    WriteInk(_spotBaseInk, offset, 0);
                    foreach (byte[] existing in _spotPlates!.Values)
                        WriteInk(existing, offset, 0);
                }
                if (color.ProcessInk is uint processInk)
                {
                    uint mask = 0;
                    for (int channel = 0; channel < 4; channel++)
                        if ((color.SpotProcessMask & (1 << channel)) != 0)
                            mask |= 0xffu << (channel * 8);
                    uint baseInk = ReadInk(_spotBaseInk, offset);
                    WriteInk(_spotBaseInk, offset, baseInk & ~mask | processInk & mask);
                }
                if (!_spotPlates!.TryGetValue(color.SpotName!, out byte[]? plate))
                {
                    plate = RasterBuffers.Rent(Length);
                    Array.Clear(plate, 0, Length);
                    _spotPlates.Add(color.SpotName!, plate);
                }
                WriteInk(plate, offset, color.SpotTint <= 0 ? 0 : color.SpotInk ?? sourceInk);
                if (color.AdditionalSpot is { } additional)
                {
                    if (!_spotPlates.TryGetValue(additional.Name, out byte[]? otherPlate))
                    {
                        otherPlate = RasterBuffers.Rent(Length);
                        Array.Clear(otherPlate, 0, Length);
                        _spotPlates.Add(additional.Name, otherPlate);
                    }
                    WriteInk(otherPlate, offset, additional.Tint <= 0 ? 0 : additional.Ink);
                }
                ComposeSpotPixel(offset);
            }
        }

        internal void PaintSpotCoverage(int offset, in Color color, uint sourceInk, byte coverage,
            bool overprint = true)
        {
            lock (_spotSync)
            {
                if (_spotBaseInk is null)
                {
                    _spotBaseInk = RasterBuffers.Rent(Length);
                    Data.AsSpan(0, Length).CopyTo(_spotBaseInk);
                    _spotPlates = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                }
                if (!_spotPlates!.TryGetValue(color.SpotName!, out byte[]? plate))
                {
                    plate = RasterBuffers.Rent(Length);
                    Array.Clear(plate, 0, Length);
                    _spotPlates.Add(color.SpotName!, plate);
                }
                byte[]? additionalPlate = null;
                if (color.AdditionalSpot is { } additional
                    && !_spotPlates.TryGetValue(additional.Name, out additionalPlate))
                {
                    additionalPlate = RasterBuffers.Rent(Length);
                    Array.Clear(additionalPlate, 0, Length);
                    _spotPlates.Add(additional.Name, additionalPlate);
                }
                uint spotInk = color.SpotTint <= 0 ? 0 : color.SpotInk ?? sourceInk;
                if (!overprint)
                {
                    WriteInk(_spotBaseInk, offset, BlendCoverageInk(0, ReadInk(_spotBaseInk, offset), coverage));
                    foreach (byte[] existing in _spotPlates.Values)
                        if (!ReferenceEquals(existing, plate)
                            && !ReferenceEquals(existing, additionalPlate))
                            WriteInk(existing, offset, BlendCoverageInk(0, ReadInk(existing, offset), coverage));
                }
                WriteInk(plate, offset, BlendCoverageInk(spotInk, ReadInk(plate, offset), coverage));
                if (additionalPlate is not null)
                {
                    SpotColorant secondSpot = color.AdditionalSpot!;
                    uint additionalInk = secondSpot.Tint <= 0 ? 0 : secondSpot.Ink;
                    WriteInk(additionalPlate, offset,
                        BlendCoverageInk(additionalInk, ReadInk(additionalPlate, offset), coverage));
                }
                ComposeSpotPixel(offset);
            }
        }

        internal void PaintSpotRun(int offset, int count, in Color color, bool overprint, uint sourceInk)
        {
            lock (_spotSync)
            {
                if (_spotBaseInk is null)
                {
                    _spotBaseInk = RasterBuffers.Rent(Length);
                    Data.AsSpan(0, Length).CopyTo(_spotBaseInk);
                    _spotPlates = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                }
                if (!_spotPlates!.TryGetValue(color.SpotName!, out byte[]? plate))
                {
                    plate = RasterBuffers.Rent(Length);
                    Array.Clear(plate, 0, Length);
                    _spotPlates.Add(color.SpotName!, plate);
                }
                byte[]? additionalPlate = null;
                if (color.AdditionalSpot is { } additional
                    && !_spotPlates.TryGetValue(additional.Name, out additionalPlate))
                {
                    additionalPlate = RasterBuffers.Rent(Length);
                    Array.Clear(additionalPlate, 0, Length);
                    _spotPlates.Add(additional.Name, additionalPlate);
                }
                uint mask = 0;
                if (color.ProcessInk is not null)
                    for (int channel = 0; channel < 4; channel++)
                        if ((color.SpotProcessMask & (1 << channel)) != 0)
                            mask |= 0xffu << (channel * 8);
                uint spotInk = color.SpotTint <= 0 ? 0 : color.SpotInk ?? sourceInk;
                uint additionalInk = color.AdditionalSpot is { } other && other.Tint > 0
                    ? other.Ink : 0;
                for (int index = 0; index < count; index++, offset += 4)
                {
                    if (!overprint)
                    {
                        WriteInk(_spotBaseInk, offset, 0);
                        foreach (byte[] existing in _spotPlates.Values)
                            WriteInk(existing, offset, 0);
                    }
                    if (color.ProcessInk is uint processInk)
                    {
                        uint baseInk = ReadInk(_spotBaseInk, offset);
                        WriteInk(_spotBaseInk, offset, baseInk & ~mask | processInk & mask);
                    }
                    WriteInk(plate, offset, spotInk);
                    if (additionalPlate is not null)
                        WriteInk(additionalPlate, offset, additionalInk);
                    ComposeSpotPixel(offset);
                }
            }
        }

        internal bool TryReplaceProcessInkKeepingSpots(int offset, uint processInk, int coverage = 255,
            int preserveComponents = 0)
        {
            if (_spotBaseInk is null) return false;
            lock (_spotSync)
            {
                if (_spotBaseInk is null) return false;
                processInk = MergeProcessComponents(processInk, ReadInk(_spotBaseInk, offset), preserveComponents);
                WriteInk(_spotBaseInk, offset, coverage == 255 ? processInk
                    : BlendCoverageInk(processInk, ReadInk(_spotBaseInk, offset), coverage));
                ComposeSpotPixel(offset);
                return true;
            }
        }

        internal bool TryBlendProcessInkKeepingSpots(int offset, uint processInk, double opacity,
            int preserveComponents)
        {
            if (_spotBaseInk is null) return false;
            lock (_spotSync)
            {
                if (_spotBaseInk is null) return false;
                uint baseInk = ReadInk(_spotBaseInk, offset);
                processInk = MergeProcessComponents(processInk, baseInk, preserveComponents);
                WriteInk(_spotBaseInk, offset, BlendOpaqueInk(processInk, baseInk, opacity, 1));
                ComposeSpotPixel(offset);
                return true;
            }
        }

        private static uint MergeProcessComponents(uint processInk, uint baseInk, int preserveComponents)
        {
            uint preserve = (preserveComponents & 1) != 0 ? 0x000000ffu : 0;
            if ((preserveComponents & 2) != 0) preserve |= 0x0000ff00u;
            if ((preserveComponents & 4) != 0) preserve |= 0x00ff0000u;
            if ((preserveComponents & 8) != 0) preserve |= 0xff000000u;
            return processInk & ~preserve | baseInk & preserve;
        }

        private void ComposeSpotPixel(int offset)
        {
            uint combined = ReadInk(_spotBaseInk!, offset);
            foreach (byte[] current in _spotPlates!.Values)
            {
                uint spot = ReadInk(current, offset);
                if (spot == 0) continue;
                if (combined == 0)
                {
                    combined = spot;
                    continue;
                }
                uint mixed = 0;
                for (int channel = 0; channel < 4; channel++)
                {
                    double baseValue = (byte)(combined >> (channel * 8)) / 255d;
                    double spotValue = (byte)(spot >> (channel * 8)) / 255d;
                    mixed |= (uint)(byte)Math.Round((baseValue + spotValue
                        - baseValue * spotValue) * 255) << (channel * 8);
                }
                combined = mixed;
            }
            if (_rgbSpotShadow)
            {
                uint rgb = PdfDeviceCmyk.ToRgb(combined);
                Data[offset] = (byte)rgb;
                Data[offset + 1] = (byte)(rgb >> 8);
                Data[offset + 2] = (byte)(rgb >> 16);
            }
            else WriteInk(Data, offset, combined);
            SetAlpha(offset, 255);
        }

        internal void ClearSpotPixel(int offset)
        {
            if (_spotBaseInk is null) return;
            lock (_spotSync)
            {
                WriteInk(_spotBaseInk, offset, ReadInk(Data, offset));
                foreach (byte[] plate in _spotPlates!.Values)
                    WriteInk(plate, offset, 0);
            }
        }

        private void ReleaseSpotPlates()
        {
            if (_spotBaseInk is not null) RasterBuffers.Return(_spotBaseInk);
            _spotBaseInk = null;
            if (_rgbSpotValid is not null) RasterBuffers.Return(_rgbSpotValid);
            _rgbSpotValid = null;
            _rgbSpotShadow = false;
            if (_spotPlates is null) return;
            foreach (byte[] plate in _spotPlates.Values) RasterBuffers.Return(plate);
            _spotPlates = null;
        }

        internal void ReleaseInk()
        {
            ReleaseSpotPlates();
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
            byte[] inkData = Ink;
            long[]? sharedRgb = InkProfile is PdfIccProfileTransform && Length >= 4_000_000
                ? new long[65536] : null;
            ForEachRow(0, Height, Length / 4, (startRow, endRow) =>
                {
                    ConvertInkRange(inkData, startRow * Width * 4,
                        endRow * Width * 4, sharedRgb, cancellationToken);
                }, null, cancellationToken);
            ReleaseSpotPlates();
            Ink = null;
        }

        private void ConvertInkRange(byte[] inkData, int start, int end,
            long[]? sharedRgb, CancellationToken cancellationToken)
        {
            uint previousInk = 0;
            uint previousRgb = 0;
            Span<ulong> colors = stackalloc ulong[16384];
            Span<uint> occupied = stackalloc uint[512];
            occupied.Clear();
            byte[]? inkAlpha = InkAlpha;
            byte constantAlpha = _constantAlpha;
            for (int offset = start; offset < end; offset += 4)
            {
                if ((offset & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                uint ink = ReadInk(inkData, offset);
                uint rgb;
                if (offset > start && ink == previousInk) rgb = previousRgb;
                else
                {
                    int slot = (int)((ink * 2654435761u) >> 18);
                    uint bit = 1u << (slot & 31);
                    if ((occupied[slot >> 5] & bit) != 0 && (uint)(colors[slot] >> 32) == ink)
                    {
                        rgb = (uint)colors[slot];
                    }
                    else
                    {
                        int sharedSlot = sharedRgb is null ? 0 : (int)((ink * 2654435761u) >> 16);
                        long sharedEntry = sharedRgb is null ? 0 : Volatile.Read(ref sharedRgb[sharedSlot]);
                        if (sharedEntry != 0 && (uint)(sharedEntry >> 32) == ink)
                        {
                            rgb = (uint)sharedEntry;
                        }
                        else
                        {
                            if (InkProfile is null) rgb = PdfDeviceCmyk.ToRgb(ink);
                            else
                            {
                                Color color = ProfileInkToDisplay(ink, InkProfile);
                                rgb = (uint)(color.Red << 16 | color.Green << 8 | color.Blue);
                            }
                            // Keep the ink key and RGB value together for concurrent rows.
                            if (sharedRgb is not null)
                                Interlocked.Exchange(ref sharedRgb[sharedSlot],
                                    unchecked((long)(((ulong)ink << 32) | rgb)));
                        }
                        colors[slot] = ((ulong)ink << 32) | rgb;
                        occupied[slot >> 5] |= bit;
                    }
                }
                previousInk = ink;
                previousRgb = rgb;
                WriteInk(Data, offset, rgb | (uint)(inkAlpha is null ? constantAlpha : inkAlpha[offset / 4]) << 24);
            }
        }

        internal void CopyFrom(RasterSurface source)
        {
            if (ReferenceEquals(this, source)) return;
            ReleaseSpotPlates();
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
            if (Ink is not null && source.Ink is not null
                && ReferenceEquals(BlendProfile, source.BlendProfile)
                && source._spotBaseInk is not null)
            {
                _spotBaseInk = RasterBuffers.Rent(Length);
                _spotPlates = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                for (int y = Top; y < Bottom; y++)
                    source._spotBaseInk.AsSpan(source.Offset(Left, y), Width * 4)
                        .CopyTo(_spotBaseInk.AsSpan(Offset(Left, y), Width * 4));
                foreach ((string name, byte[] sourcePlate) in source._spotPlates!)
                {
                    byte[] plate = RasterBuffers.Rent(Length);
                    for (int y = Top; y < Bottom; y++)
                        sourcePlate.AsSpan(source.Offset(Left, y), Width * 4)
                            .CopyTo(plate.AsSpan(Offset(Left, y), Width * 4));
                    _spotPlates.Add(name, plate);
                }
            }
        }

        internal void Return()
        {
            if (GroupShape is not null)
            {
                RasterBuffers.Return(GroupShape);
                GroupShape = null;
            }
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
