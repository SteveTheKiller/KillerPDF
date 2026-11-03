using System.Buffers.Binary;

namespace KillerPdf.Engine.Fonts;

internal sealed class TrueTypeOutlineReader
{
    internal sealed class GlyphRecursionException() : FormatException(
        "A compound TrueType glyph is cyclic or too deeply nested.");

    private readonly TrueTypeFont _font;
    private readonly int _loca;
    private readonly int _locaLength;
    private readonly int _glyf;
    private readonly int _glyfLength;
    private readonly bool _longLocations;
    private readonly Dictionary<ushort, PdfGlyphOutline?> _outlineCache = [];
    private readonly Lock _outlineLock = new();

    internal TrueTypeOutlineReader(TrueTypeFont font)
    {
        _font = font;
        var data = font.FontData.Span;
        // Take table locations from the loaded face: a collection face's directory is not at
        // the start of the data, and damaged directories may hold records the face skipped.
        if (font.TryGetTableRange("loca", out int loca, out int locaLength)) { _loca = loca; _locaLength = locaLength; }
        if (font.TryGetTableRange("glyf", out int glyf, out int glyfLength)) { _glyf = glyf; _glyfLength = glyfLength; }
        int head = font.TryGetTableRange("head", out int headOffset, out int headLength) && headLength >= 54
            ? headOffset : 0;
        int locationFormat = head != 0 ? BinaryPrimitives.ReadInt16BigEndian(data[(head + 50)..]) : 0;
        // Some writers leave head.indexToLocFormat invalid; the loca size then shows the format.
        _longLocations = locationFormat is 0 or 1 ? locationFormat == 1
            : _locaLength >= (font.GlyphCount + 1) * 4;
    }

    internal PdfGlyphBounds? Bounds(ushort glyph)
    {
        int stride = _longLocations ? 4 : 2;
        if (_loca == 0 || _glyf == 0 || glyph >= _font.GlyphCount || (glyph + 2) * stride > _locaLength) return null;
        var data = _font.FontData.Span;
        int address = _loca + glyph * stride;
        long start = _longLocations ? BinaryPrimitives.ReadUInt32BigEndian(data[address..])
            : BinaryPrimitives.ReadUInt16BigEndian(data[address..]) * 2L;
        long end = _longLocations ? BinaryPrimitives.ReadUInt32BigEndian(data[(address + stride)..])
            : BinaryPrimitives.ReadUInt16BigEndian(data[(address + stride)..]) * 2L;
        if (start == end) return new PdfGlyphBounds(0, 0, 0, 0);
        if (start < 0 || end < start + 10 || end > _glyfLength) return null;
        var box = data.Slice(_glyf + (int)start + 2, 8);
        double scale = 1000d / _font.UnitsPerEm;
        return new PdfGlyphBounds(BinaryPrimitives.ReadInt16BigEndian(box) * scale,
            BinaryPrimitives.ReadInt16BigEndian(box[2..]) * scale,
            BinaryPrimitives.ReadInt16BigEndian(box[4..]) * scale,
            BinaryPrimitives.ReadInt16BigEndian(box[6..]) * scale);
    }

    internal PdfGlyphOutline? Outline(ushort glyph)
    {
        lock (_outlineLock)
        {
            if (_outlineCache.TryGetValue(glyph, out PdfGlyphOutline? cached))
                return cached;
            PdfGlyphOutline? outline = Outline(glyph, [], 0);
            _outlineCache.Add(glyph, outline);
            return outline;
        }
    }

    private PdfGlyphOutline? Outline(ushort glyph, HashSet<ushort> active, int depth)
    {
        if (depth > 32 || !active.Add(glyph))
            throw new GlyphRecursionException();
        try
        {
            if (!TryGlyphRange(glyph, out ReadOnlySpan<byte> data)) return null;
            if (data.Length == 0) return new PdfGlyphOutline([]);
            int contourCount = BinaryPrimitives.ReadInt16BigEndian(data);
            if (contourCount < 0) return CompoundOutline(data, active, depth);
            if (contourCount == 0) return new PdfGlyphOutline([]);
            if (contourCount > 4_096 || data.Length < 10 + contourCount * 2 + 2)
                throw new FormatException("A TrueType simple glyph is invalid.");
            int position = 10;
            var endPoints = new ushort[contourCount];
            for (int contour = 0; contour < contourCount; contour++)
            {
                endPoints[contour] = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
                if (contour > 0 && endPoints[contour] <= endPoints[contour - 1])
                    throw new FormatException("TrueType contour endpoints are invalid.");
                position += 2;
            }
            int pointCount = endPoints[^1] + 1;
            if (pointCount > 1_000_000 || position + 2 > data.Length)
                throw new FormatException("A TrueType glyph point count is invalid.");
            int instructionLength = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            position += 2;
            if (instructionLength > data.Length - position)
                throw new FormatException("TrueType glyph instructions are truncated.");
            position += instructionLength;
            var flags = new byte[pointCount];
            for (int point = 0; point < pointCount;)
            {
                if (position >= data.Length)
                    throw new FormatException("TrueType glyph flags are truncated.");
                byte flag = data[position++];
                int repeat = 1;
                if ((flag & 0x08) != 0)
                {
                    if (position >= data.Length)
                        throw new FormatException("A TrueType glyph flag repeat is truncated.");
                    repeat += data[position++];
                }
                if (repeat > pointCount - point)
                    throw new FormatException("A TrueType glyph flag repeat is invalid.");
                Array.Fill(flags, flag, point, repeat);
                point += repeat;
            }
            var x = new int[pointCount];
            var y = new int[pointCount];
            ReadCoordinates(data, ref position, x, flags, shortMask: 0x02, sameMask: 0x10);
            ReadCoordinates(data, ref position, y, flags, shortMask: 0x04, sameMask: 0x20);
            double scale = 1000d / _font.UnitsPerEm;
            var contours = new PdfGlyphContour[contourCount];
            int start = 0;
            for (int contour = 0; contour < contourCount; contour++)
            {
                int end = endPoints[contour];
                var points = new PdfGlyphPoint[end - start + 1];
                for (int point = start; point <= end; point++)
                    points[point - start] = new PdfGlyphPoint(
                        x[point] * scale, y[point] * scale, (flags[point] & 1) != 0);
                contours[contour] = new PdfGlyphContour(Array.AsReadOnly(points));
                start = end + 1;
            }
            return new PdfGlyphOutline(Array.AsReadOnly(contours));
        }
        finally
        {
            active.Remove(glyph);
        }
    }

    private PdfGlyphOutline CompoundOutline(
        ReadOnlySpan<byte> data, HashSet<ushort> active, int depth)
    {
        const ushort ArgumentsAreWords = 0x0001;
        const ushort ArgumentsAreCoordinates = 0x0002;
        const ushort HasScale = 0x0008;
        const ushort MoreComponents = 0x0020;
        const ushort HasXYScale = 0x0040;
        const ushort HasMatrix = 0x0080;
        const ushort HasInstructions = 0x0100;
        const ushort ScaledOffset = 0x0800;
        int position = 10;
        int componentCount = 0;
        ushort flags;
        var contours = new List<PdfGlyphContour>();
        do
        {
            if (++componentCount > 4_096 || position + 4 > data.Length)
                throw new FormatException("A compound TrueType glyph is invalid.");
            flags = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            ushort componentGlyph = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 2)..]);
            position += 4;
            int firstArgument, secondArgument;
            if ((flags & ArgumentsAreWords) != 0)
            {
                if (position + 4 > data.Length)
                    throw new FormatException("Compound TrueType glyph arguments are truncated.");
                if ((flags & ArgumentsAreCoordinates) != 0)
                {
                    firstArgument = BinaryPrimitives.ReadInt16BigEndian(data[position..]);
                    secondArgument = BinaryPrimitives.ReadInt16BigEndian(data[(position + 2)..]);
                }
                else
                {
                    firstArgument = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
                    secondArgument = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 2)..]);
                }
                position += 4;
            }
            else
            {
                if (position + 2 > data.Length)
                    throw new FormatException("Compound TrueType glyph arguments are truncated.");
                if ((flags & ArgumentsAreCoordinates) != 0)
                {
                    firstArgument = (sbyte)data[position];
                    secondArgument = (sbyte)data[position + 1];
                }
                else
                {
                    firstArgument = data[position];
                    secondArgument = data[position + 1];
                }
                position += 2;
            }
            double a = 1, b = 0, c = 0, d = 1;
            if ((flags & HasScale) != 0)
                a = d = ReadF2Dot14(data, ref position);
            else if ((flags & HasXYScale) != 0)
            {
                a = ReadF2Dot14(data, ref position);
                d = ReadF2Dot14(data, ref position);
            }
            else if ((flags & HasMatrix) != 0)
            {
                a = ReadF2Dot14(data, ref position);
                b = ReadF2Dot14(data, ref position);
                c = ReadF2Dot14(data, ref position);
                d = ReadF2Dot14(data, ref position);
            }
            PdfGlyphOutline component = Outline(componentGlyph, active, depth + 1)
                ?? new PdfGlyphOutline([]);
            double offsetX, offsetY;
            double unitScale = 1000d / _font.UnitsPerEm;
            if ((flags & ArgumentsAreCoordinates) != 0)
            {
                offsetX = firstArgument * unitScale;
                offsetY = secondArgument * unitScale;
                if ((flags & ScaledOffset) != 0)
                    (offsetX, offsetY) = (a * offsetX + c * offsetY,
                        b * offsetX + d * offsetY);
            }
            else
            {
                PdfGlyphPoint[] parentPoints = [.. contours.SelectMany(item => item.Points)];
                PdfGlyphPoint[] componentPoints = [.. component.Contours.SelectMany(item => item.Points)];
                if (firstArgument >= parentPoints.Length || secondArgument >= componentPoints.Length)
                    throw new FormatException("Compound TrueType glyph point matching is invalid.");
                PdfGlyphPoint parent = parentPoints[firstArgument];
                PdfGlyphPoint child = componentPoints[secondArgument];
                offsetX = parent.X - (a * child.X + c * child.Y);
                offsetY = parent.Y - (b * child.X + d * child.Y);
            }
            foreach (PdfGlyphContour contour in component.Contours)
                contours.Add(new PdfGlyphContour(Array.AsReadOnly([
                    .. contour.Points.Select(point => new PdfGlyphPoint(
                        a * point.X + c * point.Y + offsetX,
                        b * point.X + d * point.Y + offsetY, point.OnCurve))])));
        }
        while ((flags & MoreComponents) != 0);
        if ((flags & HasInstructions) != 0)
        {
            if (position + 2 > data.Length)
                throw new FormatException("Compound TrueType glyph instructions are truncated.");
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            position += 2;
            if (length > data.Length - position)
                throw new FormatException("Compound TrueType glyph instructions are truncated.");
        }
        return new PdfGlyphOutline(contours.AsReadOnly());
    }

    private static double ReadF2Dot14(ReadOnlySpan<byte> data, ref int position)
    {
        if (position + 2 > data.Length)
            throw new FormatException("A compound TrueType glyph transform is truncated.");
        double value = BinaryPrimitives.ReadInt16BigEndian(data[position..]) / 16384d;
        position += 2;
        return value;
    }

    private static void ReadCoordinates(ReadOnlySpan<byte> data, ref int position,
        int[] coordinates, byte[] pointFlags, byte shortMask, byte sameMask)
    {
        int current = 0;
        for (int point = 0; point < coordinates.Length; point++)
        {
            byte flag = pointFlags[point];
            int delta;
            if ((flag & shortMask) != 0)
            {
                if (position >= data.Length)
                    throw new FormatException("TrueType glyph coordinates are truncated.");
                int magnitude = data[position++];
                delta = (flag & sameMask) != 0 ? magnitude : -magnitude;
            }
            else if ((flag & sameMask) != 0) delta = 0;
            else
            {
                if (position + 2 > data.Length)
                    throw new FormatException("TrueType glyph coordinates are truncated.");
                delta = BinaryPrimitives.ReadInt16BigEndian(data[position..]);
                position += 2;
            }
            current = checked(current + delta);
            coordinates[point] = current;
        }
    }

    private bool TryGlyphRange(ushort glyph, out ReadOnlySpan<byte> glyphData)
    {
        glyphData = default;
        int stride = _longLocations ? 4 : 2;
        if (_loca == 0 || _glyf == 0 || glyph >= _font.GlyphCount
            || (glyph + 2) * stride > _locaLength) return false;
        ReadOnlySpan<byte> data = _font.FontData.Span;
        int address = _loca + glyph * stride;
        long start = _longLocations ? BinaryPrimitives.ReadUInt32BigEndian(data[address..])
            : BinaryPrimitives.ReadUInt16BigEndian(data[address..]) * 2L;
        long end = _longLocations ? BinaryPrimitives.ReadUInt32BigEndian(data[(address + stride)..])
            : BinaryPrimitives.ReadUInt16BigEndian(data[(address + stride)..]) * 2L;
        if (start == end)
        {
            glyphData = [];
            return true;
        }
        if (start < 0 || end < start + 10 || end > _glyfLength) return false;
        glyphData = data.Slice(_glyf + (int)start, (int)(end - start));
        return true;
    }
}
