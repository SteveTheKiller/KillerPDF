using System.Numerics;
using System.Runtime.CompilerServices;
using KillerPdf.Engine.Fonts;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    internal static void MultiplyCoverage(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second,
        Span<byte> destination)
    {
        int x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var rounding = new Vector<ushort>(127);
            var one = Vector<ushort>.One;
            for (; x <= destination.Length - Vector<byte>.Count; x += Vector<byte>.Count)
            {
                Vector.Widen(new Vector<byte>(first.Slice(x)), out Vector<ushort> a0, out Vector<ushort> a1);
                Vector.Widen(new Vector<byte>(second.Slice(x)), out Vector<ushort> b0, out Vector<ushort> b1);
                Vector<ushort> low = a0 * b0 + rounding, high = a1 * b1 + rounding;
                // Exact division by 255 for products rounded within the byte-coverage range.
                low = (low + one + (low >> 8)) >> 8;
                high = (high + one + (high >> 8)) >> 8;
                Vector.Narrow(low, high).CopyTo(destination.Slice(x));
            }
        }
        for (; x < destination.Length; x++)
            destination[x] = (byte)((first[x] * second[x] + 127) / 255);
    }

    /// <summary>A rectangular coverage mask in device pixels. Null coverage means fully covered.</summary>
    internal sealed class CoverageMask
    {
        private bool _rented;
        private readonly bool _repeatedMiddleRows;

        internal CoverageMask(int left, int top, int right, int bottom, byte[]? coverage,
            bool rented = false, bool repeatedMiddleRows = false)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
            Coverage = coverage;
            _rented = rented && coverage is not null;
            _repeatedMiddleRows = repeatedMiddleRows;
        }

        internal static CoverageMask Empty { get; } = new(0, 0, 0, 0, null);

        /// <summary>
        /// Returns a pooled coverage buffer after painting or after its clip scope ends.
        /// Translated masks borrow storage from their owner; cached glyphs are not pooled.
        /// The mask and any borrowing views must not be read after this call.
        /// </summary>
        internal void Return()
        {
            if (!_rented) return;
            _rented = false;
            RasterBuffers.Return(Coverage!);
        }

        internal int Left { get; }
        internal int Top { get; }
        internal int Right { get; }
        internal int Bottom { get; }
        internal int Width => Right - Left;
        internal int Height => Bottom - Top;
        internal bool IsEmpty => Right <= Left || Bottom <= Top;
        /// <summary>Coverage rows, or null when every pixel is covered.</summary>
        internal byte[]? Coverage { get; }

        internal int RowOffset(int y) => (_repeatedMiddleRows
            ? y == Top ? 0 : y == Bottom - 1 ? 2 : 1
            : y - Top) * Width;

        internal byte At(int x, int y)
        {
            if (x < Left || x >= Right || y < Top || y >= Bottom) return 0;
            return Coverage is null ? (byte)255 : Coverage[RowOffset(y) + (x - Left)];
        }

        internal static CoverageMask Rectangle(int left, int top, int right, int bottom) =>
            right <= left || bottom <= top ? Empty : new(left, top, right, bottom, null);

        /// <summary>Moves the bounds by whole pixels, sharing the coverage array.</summary>
        internal CoverageMask Translate(int dx, int dy) =>
            IsEmpty ? Empty : new(Left + dx, Top + dy, Right + dx, Bottom + dy, Coverage,
                repeatedMiddleRows: _repeatedMiddleRows);

        /// <summary>Intersects two masks by multiplying coverage.</summary>
        internal static CoverageMask Intersect(CoverageMask first, CoverageMask second)
        {
            int left = Math.Max(first.Left, second.Left);
            int top = Math.Max(first.Top, second.Top);
            int right = Math.Min(first.Right, second.Right);
            int bottom = Math.Min(first.Bottom, second.Bottom);
            if (right <= left || bottom <= top) return Empty;
            if (first.Coverage is null && second.Coverage is null)
                return new CoverageMask(left, top, right, bottom, null);
            if (first.Coverage is null && left == second.Left && top == second.Top
                && right == second.Right && bottom == second.Bottom)
                return second;
            if (second.Coverage is null && left == first.Left && top == first.Top
                && right == first.Right && bottom == first.Bottom)
                return first;
            int width = right - left;
            bool repeated = bottom - top > 3
                && (first.Coverage is null || first._repeatedMiddleRows)
                && (second.Coverage is null || second._repeatedMiddleRows);
            int rows = repeated ? 3 : bottom - top;
            ClipScratchScope? scope = ClipScratchScope.Current;
            int size = checked(width * rows);
            byte[] coverage = scope is null ? new byte[size] : RasterBuffers.Rent(size);
            var result = new CoverageMask(left, top, right, bottom, coverage,
                rented: scope is not null, repeatedMiddleRows: repeated);
            scope?.Track(result);
            if (first.Coverage is null || second.Coverage is null)
            {
                CoverageMask source = first.Coverage is null ? second : first;
                for (int row = 0; row < rows; row++)
                {
                    int y = repeated && row == 2 ? bottom - 1 : top + row;
                    source.Coverage!.AsSpan(source.RowOffset(y) + left - source.Left,
                        width).CopyTo(coverage.AsSpan(row * width, width));
                }
                return result;
            }
            for (int rowIndex = 0; rowIndex < rows; rowIndex++)
            {
                int y = repeated && rowIndex == 2 ? bottom - 1 : top + rowIndex;
                int row = rowIndex * width;
                int firstRow = first.RowOffset(y) + left - first.Left;
                int secondRow = second.RowOffset(y) + left - second.Left;
                MultiplyCoverage(first.Coverage.AsSpan(firstRow, width),
                    second.Coverage.AsSpan(secondRow, width), coverage.AsSpan(row, width));
            }
            return result;
        }

        /// <summary>Builds a mask from a page-sized coverage buffer, trimming to its nonzero bounds.</summary>
        internal static CoverageMask FromPageBuffer(byte[] page, int width, int height, int stride = 1,
            int offset = 0)
        {
            int left = width, top = height, right = 0, bottom = 0;
            for (int y = 0; y < height; y++)
            {
                int row = y * width * stride + offset;
                for (int x = 0; x < width; x++)
                {
                    if (page[row + x * stride] == 0) continue;
                    if (x < left) left = x;
                    if (x + 1 > right) right = x + 1;
                    if (y < top) top = y;
                    bottom = y + 1;
                }
            }
            if (right <= left || bottom <= top) return Empty;
            int maskWidth = right - left;
            var coverage = new byte[checked(maskWidth * (bottom - top))];
            for (int y = top; y < bottom; y++)
            {
                int row = y * width * stride + offset;
                for (int x = left; x < right; x++)
                    coverage[(y - top) * maskWidth + x - left] = page[row + x * stride];
            }
            return new CoverageMask(left, top, right, bottom, coverage);
        }
    }

    /// <summary>Device-space raster frame for converting page coordinates to pixels.</summary>
    private readonly record struct RasterFrame(int Width, int Height, double ScaleX, double ScaleY)
    {
        internal double PixelX(double pageX) => pageX * ScaleX;
        internal double PixelY(double pageY) => Height - pageY * ScaleY;
        internal double StrokeScale => Math.Sqrt(ScaleX * ScaleY);

        internal Point[] ToPixels(IReadOnlyList<Point> polygon)
        {
            var result = new Point[polygon.Count];
            for (int index = 0; index < result.Length; index++)
                result[index] = new Point(PixelX(polygon[index].X), PixelY(polygon[index].Y));
            return result;
        }

        internal List<Point[]> ToPixels(IReadOnlyList<List<Point>> polygons, int minimumPoints)
        {
            var result = new List<Point[]>(polygons.Count);
            foreach (List<Point> polygon in polygons)
                if (polygon.Count >= minimumPoints) result.Add(ToPixels(polygon));
            return result;
        }

        internal List<Point[]> ToPixels(IReadOnlyList<Point[]> polygons, int minimumPoints)
        {
            var result = new List<Point[]>(polygons.Count);
            foreach (Point[] polygon in polygons)
                if (polygon.Length >= minimumPoints) result.Add(ToPixels(polygon));
            return result;
        }
    }

    /// <summary>
    /// Anti-aliased scanline rasterizer. Edges accumulate signed cover and area per cell, then
    /// each scanline is swept to exact pixel coverage for nonzero or even-odd filling.
    /// </summary>
    private sealed class CellRasterizer
    {
        private const int Shift = 8;
        private const int Scale = 1 << Shift;
        private const int Mask = Scale - 1;
        private const int AreaShift = Shift * 2 + 1 - 8;
        private const int DeltaLimit = 16384 << Shift;

        private int _width;
        private int _height;
        private Cell[] _cells = new Cell[1024];
        private int _count;
        private int _currentX = int.MaxValue;
        private int _currentY = int.MaxValue;
        private int _currentCover;
        private int _currentArea;
        private int _minimumX = int.MaxValue;
        private int _maximumX = int.MinValue;
        private int _minimumY = int.MaxValue;
        private int _maximumY = int.MinValue;

        private Cell[] _sorted = new Cell[1024];
        private int[] _starts = new int[256];

        internal CellRasterizer(int width, int height)
        {
            _width = width;
            _height = height;
        }

        internal int Width => _width;
        internal int Height => _height;

        /// <summary>Clears pending cells and adopts a new raster size.</summary>
        internal void Reset(int width, int height)
        {
            _width = width;
            _height = height;
            Reset();
        }

        internal void Reset()
        {
            _count = 0;
            _currentX = _currentY = int.MaxValue;
            _currentCover = _currentArea = 0;
            _minimumX = _minimumY = int.MaxValue;
            _maximumX = _maximumY = int.MinValue;
        }

        private struct Cell
        {
            public int X;
            public int Y;
            public int Cover;
            public int Area;
        }

        /// <summary>Adds closed polygons after clipping them to the raster.</summary>
        internal void AddPolygons(IEnumerable<Point[]> polygons)
        {
            foreach (Point[] polygon in polygons)
            {
                if (polygon.Length < 2) continue;
                Point[] clipped = ClipToRaster(polygon);
                if (clipped.Length < 2) continue;
                for (int index = 0; index < clipped.Length; index++)
                {
                    Point from = clipped[index];
                    Point to = clipped[(index + 1) % clipped.Length];
                    Line(Fixed(from.X), Fixed(from.Y), Fixed(to.X), Fixed(to.Y));
                }
            }
        }

        // Glyph bounds include every transformed point and a one-pixel margin.
        internal void AddGlyphPolygons(IReadOnlyList<Point[]> polygons,
            double a, double b, double c, double d, double offsetX, double offsetY,
            int shiftX, int shiftY)
        {
            foreach (Point[] polygon in polygons)
            {
                if (polygon.Length < 3) continue;
                Point first = polygon[0];
                int firstX = Fixed((first.X * a + first.Y * c + offsetX) - shiftX);
                int firstY = Fixed((first.X * b + first.Y * d + offsetY) - shiftY);
                int fromX = firstX, fromY = firstY;
                for (int index = 1; index < polygon.Length; index++)
                {
                    Point point = polygon[index];
                    int toX = Fixed((point.X * a + point.Y * c + offsetX) - shiftX);
                    int toY = Fixed((point.X * b + point.Y * d + offsetY) - shiftY);
                    Line(fromX, fromY, toX, toY);
                    fromX = toX;
                    fromY = toY;
                }
                Line(fromX, fromY, firstX, firstY);
            }
        }

        private static int Fixed(double value) => (int)Math.Round(value * Scale);

        internal static CoverageMask? TryRectangle(Point[] polygon, int width, int height)
        {
            if (polygon.Length != 4 && (polygon.Length != 5 || polygon[0] != polygon[4]))
                return null;
            bool axisAligned =
                polygon[0].X == polygon[1].X && polygon[1].Y == polygon[2].Y
                    && polygon[2].X == polygon[3].X && polygon[3].Y == polygon[0].Y
                || polygon[0].Y == polygon[1].Y && polygon[1].X == polygon[2].X
                    && polygon[2].Y == polygon[3].Y && polygon[3].X == polygon[0].X;
            Span<(int X, int Y)> corners = stackalloc (int, int)[4];
            bool pixelAligned = true;
            for (int i = 0; i < 4; i++)
            {
                Point point = polygon[i];
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                    return null;
                if (!axisAligned && (point.X < -1 || point.X > width + 1
                    || point.Y < -1 || point.Y > height + 1)) return null;
                // Clamping an axis-aligned rectangle matches clipping its edges to the raster margin.
                int x = Fixed(Math.Clamp(point.X, -1, width + 1));
                int y = Fixed(Math.Clamp(point.Y, -1, height + 1));
                pixelAligned &= (x & Mask) == 0 && (y & Mask) == 0;
                corners[i] = (x, y);
            }
            var a = corners[0];
            var b = corners[1];
            var c = corners[2];
            var d = corners[3];
            if (!(a.X == b.X && b.Y == c.Y && c.X == d.X && d.Y == a.Y)
                && !(a.Y == b.Y && b.X == c.X && c.Y == d.Y && d.X == a.X))
                return null;
            if (pixelAligned)
                return CoverageMask.Rectangle(Math.Max(0, Math.Min(a.X, c.X) >> Shift),
                    Math.Max(0, Math.Min(a.Y, c.Y) >> Shift), Math.Min(width, Math.Max(a.X, c.X) >> Shift),
                    Math.Min(height, Math.Max(a.Y, c.Y) >> Shift));

            int minimumY = Math.Min(a.Y, c.Y), maximumY = Math.Max(a.Y, c.Y);
            int top = Math.Max(0, minimumY >> Shift);
            int bottom = Math.Min(height, (maximumY + Mask) >> Shift);
            if (bottom - top <= 3) return null;
            // An axis-aligned rectangle has one top row, one repeated interior row,
            // and one bottom row. Rasterize those rows with the same fixed-point
            // sweep so corner rounding and winding stay identical to the full mask.
            var compact = new Point[4];
            for (int i = 0; i < compact.Length; i++)
            {
                int y = corners[i].Y;
                double localY = y / (double)Scale - top;
                if (y == maximumY) localY -= bottom - top - 3;
                compact[i] = new Point(corners[i].X / (double)Scale, localY);
            }
            CellRasterizer rasterizer = _rectangleRasterizer ??= new CellRasterizer(width, 3);
            rasterizer.Reset(width, 3);
            rasterizer.AddPolygons([compact]);
            CoverageMask local = rasterizer.Sweep(evenOdd: false);
            if (local.IsEmpty) return CoverageMask.Empty;
            if (local.Top != 0 || local.Bottom != 3) return null;
            return new CoverageMask(local.Left, top, local.Right, bottom, local.Coverage,
                repeatedMiddleRows: true);
        }

        [ThreadStatic]
        private static CellRasterizer? _rectangleRasterizer;

        private Point[] ClipToRaster(Point[] polygon)
        {
            const double margin = 1;
            double left = -margin, top = -margin, right = _width + margin, bottom = _height + margin;
            bool inside = true;
            foreach (Point point in polygon)
            {
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return [];
                if (point.X < left || point.X > right || point.Y < top || point.Y > bottom)
                {
                    inside = false;
                    break;
                }
            }
            if (inside) return polygon;
            List<Point> current = [.. polygon];
            current = ClipEdge(current, point => point.X >= left,
                (a, b) => Intersect(a, b, left, true));
            current = ClipEdge(current, point => point.X <= right,
                (a, b) => Intersect(a, b, right, true));
            current = ClipEdge(current, point => point.Y >= top,
                (a, b) => Intersect(a, b, top, false));
            current = ClipEdge(current, point => point.Y <= bottom,
                (a, b) => Intersect(a, b, bottom, false));
            return [.. current];

            static List<Point> ClipEdge(List<Point> input, Func<Point, bool> inside,
                Func<Point, Point, Point> intersect)
            {
                if (input.Count == 0) return input;
                var output = new List<Point>(input.Count + 4);
                Point previous = input[^1];
                bool previousInside = inside(previous);
                foreach (Point point in input)
                {
                    bool currentInside = inside(point);
                    if (currentInside)
                    {
                        if (!previousInside) output.Add(intersect(previous, point));
                        output.Add(point);
                    }
                    else if (previousInside) output.Add(intersect(previous, point));
                    previous = point;
                    previousInside = currentInside;
                }
                return output;
            }

            static Point Intersect(Point a, Point b, double value, bool vertical)
            {
                if (vertical)
                {
                    double t = (value - a.X) / (b.X - a.X);
                    return new Point(value, a.Y + (b.Y - a.Y) * t);
                }
                else
                {
                    double t = (value - a.Y) / (b.Y - a.Y);
                    return new Point(a.X + (b.X - a.X) * t, value);
                }
            }
        }

        private void SetCurrentCell(int x, int y)
        {
            if (_currentX == x && _currentY == y) return;
            AddCurrentCell();
            _currentX = x;
            _currentY = y;
            _currentCover = 0;
            _currentArea = 0;
        }

        private void AddCurrentCell()
        {
            if ((_currentCover | _currentArea) == 0) return;
            if (_currentY < -1 || _currentY > _height) return;
            if (_count == _cells.Length) Array.Resize(ref _cells, _cells.Length * 2);
            _cells[_count++] = new Cell
            {
                X = _currentX, Y = _currentY, Cover = _currentCover, Area = _currentArea
            };
            if (_currentX < _minimumX) _minimumX = _currentX;
            if (_currentX > _maximumX) _maximumX = _currentX;
            if (_currentY < _minimumY) _minimumY = _currentY;
            if (_currentY > _maximumY) _maximumY = _currentY;
        }

        private void RenderHorizontalLine(int ey, int x1, int y1, int x2, int y2)
        {
            int ex1 = x1 >> Shift;
            int ex2 = x2 >> Shift;
            int fx1 = x1 & Mask;
            int fx2 = x2 & Mask;
            if (y1 == y2)
            {
                SetCurrentCell(ex2, ey);
                return;
            }
            if (ex1 == ex2)
            {
                int delta = y2 - y1;
                _currentCover += delta;
                _currentArea += (fx1 + fx2) * delta;
                return;
            }
            int p = (Scale - fx1) * (y2 - y1);
            int first = Scale;
            int increment = 1;
            int dx = x2 - x1;
            if (dx < 0)
            {
                p = fx1 * (y2 - y1);
                first = 0;
                increment = -1;
                dx = -dx;
            }
            int step = p / dx;
            int mod = p % dx;
            if (mod < 0)
            {
                step--;
                mod += dx;
            }
            _currentCover += step;
            _currentArea += (fx1 + first) * step;
            ex1 += increment;
            SetCurrentCell(ex1, ey);
            y1 += step;
            if (ex1 != ex2)
            {
                p = Scale * (y2 - y1 + step);
                int lift = p / dx;
                int rem = p % dx;
                if (rem < 0)
                {
                    lift--;
                    rem += dx;
                }
                mod -= dx;
                while (ex1 != ex2)
                {
                    step = lift;
                    mod += rem;
                    if (mod >= 0)
                    {
                        mod -= dx;
                        step++;
                    }
                    _currentCover += step;
                    _currentArea += Scale * step;
                    y1 += step;
                    ex1 += increment;
                    SetCurrentCell(ex1, ey);
                }
            }
            int last = y2 - y1;
            _currentCover += last;
            _currentArea += (fx2 + Scale - first) * last;
        }

        private void Line(int x1, int y1, int x2, int y2)
        {
            long dxLong = (long)x2 - x1;
            if (dxLong >= DeltaLimit || dxLong <= -DeltaLimit)
            {
                int cx = (int)(((long)x1 + x2) >> 1);
                int cy = (int)(((long)y1 + y2) >> 1);
                Line(x1, y1, cx, cy);
                Line(cx, cy, x2, y2);
                return;
            }
            int dx = (int)dxLong;
            int dy = y2 - y1;
            int ex1 = x1 >> Shift;
            int ex2 = x2 >> Shift;
            int ey1 = y1 >> Shift;
            int ey2 = y2 >> Shift;
            int fy1 = y1 & Mask;
            int fy2 = y2 & Mask;
            SetCurrentCell(ex1, ey1);
            if (ey1 == ey2)
            {
                RenderHorizontalLine(ey1, x1, fy1, x2, fy2);
                return;
            }
            int increment = 1;
            if (dx == 0)
            {
                int ex = x1 >> Shift;
                int twoFx = (x1 - (ex << Shift)) << 1;
                int first = Scale;
                if (dy < 0)
                {
                    first = 0;
                    increment = -1;
                }
                int delta = first - fy1;
                _currentCover += delta;
                _currentArea += twoFx * delta;
                ey1 += increment;
                SetCurrentCell(ex, ey1);
                delta = first + first - Scale;
                int area = twoFx * delta;
                while (ey1 != ey2)
                {
                    _currentCover = delta;
                    _currentArea = area;
                    ey1 += increment;
                    SetCurrentCell(ex, ey1);
                }
                delta = fy2 - Scale + first;
                _currentCover += delta;
                _currentArea += twoFx * delta;
                return;
            }
            int p = (Scale - fy1) * dx;
            int firstY = Scale;
            if (dy < 0)
            {
                p = fy1 * dx;
                firstY = 0;
                increment = -1;
                dy = -dy;
            }
            int stepX = p / dy;
            int mod = p % dy;
            if (mod < 0)
            {
                stepX--;
                mod += dy;
            }
            int xFrom = x1 + stepX;
            RenderHorizontalLine(ey1, x1, fy1, xFrom, firstY);
            ey1 += increment;
            SetCurrentCell(xFrom >> Shift, ey1);
            if (ey1 != ey2)
            {
                p = Scale * dx;
                int lift = p / dy;
                int rem = p % dy;
                if (rem < 0)
                {
                    lift--;
                    rem += dy;
                }
                mod -= dy;
                while (ey1 != ey2)
                {
                    stepX = lift;
                    mod += rem;
                    if (mod >= 0)
                    {
                        mod -= dy;
                        stepX++;
                    }
                    int xTo = xFrom + stepX;
                    RenderHorizontalLine(ey1, xFrom, Scale - firstY, xTo, firstY);
                    xFrom = xTo;
                    ey1 += increment;
                    SetCurrentCell(xFrom >> Shift, ey1);
                }
            }
            RenderHorizontalLine(ey1, xFrom, Scale - firstY, x2, fy2);
        }

        private static byte CalculateAlpha(int area, bool evenOdd)
        {
            int cover = area >> AreaShift;
            if (cover < 0) cover = -cover;
            if (evenOdd)
            {
                cover &= 0x1FF;
                if (cover > 256) cover = 512 - cover;
            }
            if (cover > 255) cover = 255;
            return (byte)cover;
        }

        /// <summary>Sweeps the accumulated cells into a trimmed coverage mask.</summary>
        /// <summary>
        /// Sweeps the accumulated cells into a coverage mask. With <paramref name="rent"/>
        /// the mask owns a pooled buffer that the consumer returns after painting.
        /// </summary>
        internal CoverageMask Sweep(bool evenOdd, bool rent = false,
            (int Left, int Top, int Right, int Bottom)? bounds = null)
        {
            AddCurrentCell();
            _currentX = _currentY = int.MaxValue;
            if (_count == 0) return CoverageMask.Empty;
            int left = Math.Max(0, _minimumX);
            int right = Math.Min(_width, _maximumX + 2);
            int top = Math.Max(0, _minimumY);
            int bottom = Math.Min(_height, _maximumY + 1);
            if (bounds is { } clip)
            {
                left = Math.Max(left, clip.Left);
                top = Math.Max(top, clip.Top);
                right = Math.Min(right, clip.Right);
                bottom = Math.Min(bottom, clip.Bottom);
            }
            if (right <= left || bottom <= top) return CoverageMask.Empty;
            int maskWidth = right - left;
            int size = checked(maskWidth * (bottom - top));
            byte[] coverage;
            if (rent)
            {
                // Large fills would otherwise allocate a page-sized array on the large object
                // heap for every paint; pooled buffers keep those out of gen2 collections.
                coverage = RasterBuffers.Rent(size);
                Array.Clear(coverage, 0, size);
            }
            else coverage = new byte[size];
            SortCells();
            int index = 0;
            while (index < _count)
            {
                int y = _cells[index].Y;
                int rowEnd = index;
                while (rowEnd < _count && _cells[rowEnd].Y == y) rowEnd++;
                if (y >= top && y < bottom)
                {
                    int rowOffset = (y - top) * maskWidth;
                    int cover = 0;
                    int cellIndex = index;
                    while (cellIndex < rowEnd)
                    {
                        int x = _cells[cellIndex].X;
                        int area = _cells[cellIndex].Area;
                        cover += _cells[cellIndex].Cover;
                        cellIndex++;
                        while (cellIndex < rowEnd && _cells[cellIndex].X == x)
                        {
                            area += _cells[cellIndex].Area;
                            cover += _cells[cellIndex].Cover;
                            cellIndex++;
                        }
                        if (area != 0)
                        {
                            byte alpha = CalculateAlpha((cover << (Shift + 1)) - area, evenOdd);
                            if (alpha != 0 && x >= left && x < right)
                                coverage[rowOffset + x - left] = alpha;
                            x++;
                        }
                        if (cellIndex < rowEnd && _cells[cellIndex].X > x)
                        {
                            byte alpha = CalculateAlpha(cover << (Shift + 1), evenOdd);
                            if (alpha != 0)
                            {
                                int spanLeft = Math.Max(x, left);
                                int spanRight = Math.Min(_cells[cellIndex].X, right);
                                if (spanRight > spanLeft)
                                    coverage.AsSpan(rowOffset + spanLeft - left,
                                        spanRight - spanLeft).Fill(alpha);
                            }
                        }
                    }
                }
                index = rowEnd;
            }
            _count = 0;
            _minimumX = _minimumY = int.MaxValue;
            _maximumX = _maximumY = int.MinValue;
            return new CoverageMask(left, top, right, bottom, coverage, rent);
        }

        private void SortCells()
        {
            int rows = _maximumY - _minimumY + 1;
            if (_starts.Length < rows + 1) _starts = new int[Math.Max(rows + 1, _starts.Length * 2)];
            int[] starts = _starts;
            Array.Clear(starts, 0, rows + 1);
            for (int index = 0; index < _count; index++) starts[_cells[index].Y - _minimumY + 1]++;
            for (int row = 0; row < rows; row++) starts[row + 1] += starts[row];
            if (_sorted.Length < _count) _sorted = new Cell[Math.Max(_count, _sorted.Length * 2)];
            Cell[] sorted = _sorted;
            for (int index = 0; index < _count; index++)
            {
                int row = _cells[index].Y - _minimumY;
                sorted[starts[row]++] = _cells[index];
            }
            for (int row = rows; row > 0; row--) starts[row] = starts[row - 1];
            starts[0] = 0;
            for (int row = 0; row < rows; row++)
            {
                int start = starts[row], end = starts[row + 1];
                if (end - start > 1)
                    sorted.AsSpan(start, end - start).Sort(CompareCellX);
            }
            Array.Copy(sorted, _cells, _count);
        }

        private static readonly Comparison<Cell> CompareCellX =
            static (first, second) => first.X.CompareTo(second.X);
    }

    /// <summary>Rasterizes pixel-space polygons into a trimmed coverage mask.</summary>
    [ThreadStatic]
    private static CellRasterizer? _sharedRasterizer;

    private static CoverageMask RasterizePolygons(IEnumerable<Point[]> pixelPolygons, bool evenOdd,
        int width, int height, bool rent = false,
        (int Left, int Top, int Right, int Bottom)? bounds = null)
    {
        if (pixelPolygons is IReadOnlyList<Point[]> { Count: 1 } polygons
            && CellRasterizer.TryRectangle(polygons[0], width, height) is { } rectangle)
            return rectangle;
        CellRasterizer? rasterizer = _sharedRasterizer;
        if (rasterizer is null || rasterizer.Width != width || rasterizer.Height != height)
            _sharedRasterizer = rasterizer = new CellRasterizer(width, height);
        rasterizer.Reset();
        rasterizer.AddPolygons(pixelPolygons);
        return rasterizer.Sweep(evenOdd, rent, bounds);
    }

    // Filled text glyphs repeat constantly at the same size, so their coverage masks are
    // cached per outline, device matrix, and quarter-pixel origin offset. This is the approach
    // FreeType-based viewers take; the quantized origin means a cached glyph can sit up to
    // one eighth of a pixel from its exact position. Large glyphs bypass the cache.
    private const int GlyphSubpixelSteps = 4;
    private const int MaximumCachedGlyphPixels = 128 * 128;
    private const long MaximumGlyphMaskCacheBytes = 8L * 1024 * 1024;

    internal readonly record struct GlyphMaskKey(PdfGlyphOutline Outline,
        long A, long B, long C, long D, int SubX, int SubY);

    /// <summary>A glyph mask whose bounds are relative to the glyph origin's pixel.</summary>
    internal sealed record GlyphMask(CoverageMask Mask);

    private static readonly GlyphMask EmptyGlyphMask = new(CoverageMask.Empty);

    private sealed class GlyphMaskKeyComparer : IEqualityComparer<GlyphMaskKey>
    {
        internal static GlyphMaskKeyComparer Instance { get; } = new();

        public bool Equals(GlyphMaskKey x, GlyphMaskKey y) =>
            ReferenceEquals(x.Outline, y.Outline) && x.A == y.A && x.B == y.B
            && x.C == y.C && x.D == y.D && x.SubX == y.SubX && x.SubY == y.SubY;

        public int GetHashCode(GlyphMaskKey key) => HashCode.Combine(
            RuntimeHelpers.GetHashCode(key.Outline), key.A, key.B, key.C, key.D,
            key.SubX, key.SubY);
    }

    // A null entry records a glyph that was too large to cache, so it is not re-measured.
    // Initialized from the constructor so callers passing a SharedCache can share this
    // instance across every renderer that opens the same document.
    private readonly BoundedCache<GlyphMaskKey, GlyphMask?> _glyphMaskCache;

    private static BoundedCache<GlyphMaskKey, GlyphMask?> CreateGlyphMaskCache() => new(
        8192, GlyphMaskKeyComparer.Instance, MaximumGlyphMaskCacheBytes,
        glyph => glyph?.Mask.Coverage?.LongLength ?? 0);

    [ThreadStatic]
    private static CellRasterizer? _glyphRasterizer;

    // Row parallelism requested by the render in progress on this thread. Large paints whose
    // rows are independent split into row ranges; every row computes the same pixels as the
    // sequential loop, so output does not depend on the thread count.
    [ThreadStatic]
    private static int _rowParallelism;

    private const long ParallelPaintThreshold = 262_144;

    /// <summary>Runs a row loop sequentially or across row ranges when large enough.</summary>
    private static void ForEachRow(int top, int bottom, long pixelCount,
        CancellationToken cancellationToken, Action<int, int> body)
    {
        int parallelism = _rowParallelism;
        if (parallelism <= 1 || pixelCount < ParallelPaintThreshold || bottom - top < 2)
        {
            body(top, bottom);
            return;
        }
        int rows = bottom - top;
        int chunks = Math.Min(parallelism * 4, rows);
        int chunkRows = (rows + chunks - 1) / chunks;
        Parallel.For(0, chunks, new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = cancellationToken
        }, chunk =>
        {
            int start = top + chunk * chunkRows;
            int end = Math.Min(bottom, start + chunkRows);
            if (start < end) body(start, end);
        });
    }

    /// <summary>Lets tests compare cached glyph fills against the direct fill path.</summary>
    internal bool UseGlyphMaskCache { get; set; } = true;

    internal int GlyphMaskCacheCount => _glyphMaskCache.Count;

    /// <summary>
    /// Returns the filled coverage of a glyph in page pixels, from the cache when the glyph is
    /// small enough. Returns null when the glyph must take the ordinary fill path.
    /// </summary>
    private CoverageMask? TryCachedGlyphFill(PdfGlyphOutline outline, Matrix glyphTransform,
        RasterFrame frame)
    {
        if (!UseGlyphMaskCache) return null;
        // Device matrix for text-space thousandths, including the frame's Y flip.
        double a = glyphTransform.A * frame.ScaleX, b = -glyphTransform.B * frame.ScaleY;
        double c = glyphTransform.C * frame.ScaleX, d = -glyphTransform.D * frame.ScaleY;
        double originX = glyphTransform.E * frame.ScaleX;
        double originY = frame.Height - glyphTransform.F * frame.ScaleY;
        if (!double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(c)
            || !double.IsFinite(d) || !double.IsFinite(originX) || !double.IsFinite(originY))
            return null;
        // Round the origin to the nearest quarter pixel so whole-pixel and quarter-pixel
        // positions are rasterized exactly and any other position moves at most one eighth.
        double stepsX = Math.Round(originX * GlyphSubpixelSteps);
        double stepsY = Math.Round(originY * GlyphSubpixelSteps);
        double pixelX = Math.Floor(stepsX / GlyphSubpixelSteps);
        double pixelY = Math.Floor(stepsY / GlyphSubpixelSteps);
        if (pixelX < int.MinValue / 2 || pixelX > int.MaxValue / 2
            || pixelY < int.MinValue / 2 || pixelY > int.MaxValue / 2)
            return null;
        int subX = (int)(stepsX - pixelX * GlyphSubpixelSteps);
        int subY = (int)(stepsY - pixelY * GlyphSubpixelSteps);
        var key = new GlyphMaskKey(outline, BitConverter.DoubleToInt64Bits(a),
            BitConverter.DoubleToInt64Bits(b), BitConverter.DoubleToInt64Bits(c),
            BitConverter.DoubleToInt64Bits(d), subX, subY);
        GlyphMask? glyph = _glyphMaskCache.GetOrAdd(key, RasterizeGlyphMask);
        if (glyph is null) return null;
        if (glyph.Mask.IsEmpty) return CoverageMask.Empty;
        CoverageMask placed = glyph.Mask.Translate((int)pixelX, (int)pixelY);
        return CoverageMask.Intersect(
            CoverageMask.Rectangle(0, 0, frame.Width, frame.Height), placed);
    }

    /// <summary>Rasterizes one glyph relative to its origin pixel, or returns null when too large.</summary>
    private GlyphMask? RasterizeGlyphMask(GlyphMaskKey key)
    {
        double a = BitConverter.Int64BitsToDouble(key.A), b = BitConverter.Int64BitsToDouble(key.B);
        double c = BitConverter.Int64BitsToDouble(key.C), d = BitConverter.Int64BitsToDouble(key.D);
        double offsetX = (double)key.SubX / GlyphSubpixelSteps;
        double offsetY = (double)key.SubY / GlyphSubpixelSteps;
        IReadOnlyList<Point[]> source = _glyphPathCache.GetOrAdd(key.Outline, FlattenGlyphOutlineCore);
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (Point[] sourcePath in source)
        {
            if (sourcePath.Length < 3) continue;
            for (int index = 0; index < sourcePath.Length; index++)
            {
                Point point = sourcePath[index];
                double x = point.X * a + point.Y * c + offsetX;
                double y = point.X * b + point.Y * d + offsetY;
                if (!double.IsFinite(x) || !double.IsFinite(y)) return null;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (double.IsPositiveInfinity(minX)) return EmptyGlyphMask;
        if (minX < int.MinValue / 2 || minY < int.MinValue / 2
            || maxX > int.MaxValue / 2 || maxY > int.MaxValue / 2) return null;
        // One pixel of margin on every side keeps antialiased edges inside the local raster.
        int shiftX = (int)Math.Floor(minX) - 1, shiftY = (int)Math.Floor(minY) - 1;
        long width = (long)Math.Ceiling(maxX) + 2 - shiftX;
        long height = (long)Math.Ceiling(maxY) + 2 - shiftY;
        if (width <= 0 || height <= 0 || width * height > MaximumCachedGlyphPixels) return null;
        CellRasterizer rasterizer = _glyphRasterizer ??= new CellRasterizer((int)width, (int)height);
        rasterizer.Reset((int)width, (int)height);
        rasterizer.AddGlyphPolygons(source, a, b, c, d, offsetX, offsetY, shiftX, shiftY);
        CoverageMask local = rasterizer.Sweep(evenOdd: false);
        return local.IsEmpty ? EmptyGlyphMask : new GlyphMask(local.Translate(shiftX, shiftY));
    }

    /// <summary>Rasterizes page-space paths into a coverage mask.</summary>
    private static CoverageMask RasterizeFill(IReadOnlyList<List<Point>> paths, bool evenOdd,
        RasterFrame frame, bool rent = false) =>
        RasterizePolygons(frame.ToPixels(paths, 3), evenOdd, frame.Width, frame.Height, rent);

    private static CoverageMask RasterizeFill(IReadOnlyList<Point[]> polygons, bool evenOdd,
        RasterFrame frame) =>
        RasterizePolygons(frame.ToPixels(polygons, 3), evenOdd, frame.Width, frame.Height);

    /// <summary>Converts a page-space stroke into pixel-space outline polygons and rasterizes them.</summary>
    private static CoverageMask RasterizeStroke(IReadOnlyList<List<Point>> paths,
        double pageLineWidth, RendererLineCap lineCap, RendererLineJoin lineJoin,
        double miterLimit, RasterFrame frame, bool rent = false,
        (int Left, int Top, int Right, int Bottom)? bounds = null)
    {
        double radius = Math.Max(pageLineWidth * frame.StrokeScale / 2, 0.5);
        var polygons = new List<Point[]>();
        foreach (List<Point> path in paths)
        {
            if (path.Count == 0) continue;
            Point[] pixels = frame.ToPixels(path);
            if (path is ZeroLengthDash dash && lineCap == RendererLineCap.ProjectingSquare)
            {
                Point origin = path[0];
                Point[] axis = frame.ToPixels([origin,
                    new Point(origin.X + dash.Direction.X, origin.Y + dash.Direction.Y)]);
                double dx = axis[1].X - axis[0].X, dy = axis[1].Y - axis[0].Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length == 0 || !double.IsFinite(length)) continue;
                double ux = dx / length * radius, uy = dy / length * radius;
                Point center = pixels[0];
                polygons.Add(Oriented([
                    new Point(center.X - ux + uy, center.Y - uy - ux),
                    new Point(center.X + ux + uy, center.Y + uy - ux),
                    new Point(center.X + ux - uy, center.Y + uy + ux),
                    new Point(center.X - ux - uy, center.Y - uy + ux)]));
                continue;
            }
            StrokeOutline(pixels, radius, lineCap, lineJoin, miterLimit, polygons);
        }
        return RasterizePolygons(polygons, false, frame.Width, frame.Height, rent, bounds);
    }

    /// <summary>Emits consistently oriented outline polygons for one stroked polyline.</summary>
    private static void StrokeOutline(Point[] input, double radius, RendererLineCap lineCap,
        RendererLineJoin lineJoin, double miterLimit, List<Point[]> output)
    {
        var points = new List<Point>(input.Length);
        foreach (Point point in input)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) continue;
            if (points.Count == 0 || Distance(points[^1], point) > 1e-9) points.Add(point);
        }
        bool closed = points.Count > 2 && Distance(points[0], points[^1]) <= 1e-9;
        if (closed) points.RemoveAt(points.Count - 1);
        if (points.Count == 1)
        {
            if (lineCap == RendererLineCap.Round || input.Length > 1 && closed)
                output.Add(Circle(points[0], radius));
            else if (lineCap == RendererLineCap.ProjectingSquare)
                output.Add(Oriented(
                [
                    new Point(points[0].X - radius, points[0].Y - radius),
                    new Point(points[0].X + radius, points[0].Y - radius),
                    new Point(points[0].X + radius, points[0].Y + radius),
                    new Point(points[0].X - radius, points[0].Y + radius)
                ]));
            return;
        }
        if (points.Count < 2) return;
        int segmentCount = closed ? points.Count : points.Count - 1;
        for (int index = 0; index < segmentCount; index++)
        {
            Point from = points[index];
            Point to = points[(index + 1) % points.Count];
            double dx = to.X - from.X, dy = to.Y - from.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            double ux = dx / length, uy = dy / length;
            double nx = -uy * radius, ny = ux * radius;
            double extendStart = 0, extendEnd = 0;
            if (!closed && lineCap == RendererLineCap.ProjectingSquare)
            {
                if (index == 0) extendStart = radius;
                if (index == segmentCount - 1) extendEnd = radius;
            }
            Point start = new(from.X - ux * extendStart, from.Y - uy * extendStart);
            Point end = new(to.X + ux * extendEnd, to.Y + uy * extendEnd);
            output.Add(Oriented(
            [
                new Point(start.X + nx, start.Y + ny), new Point(end.X + nx, end.Y + ny),
                new Point(end.X - nx, end.Y - ny), new Point(start.X - nx, start.Y - ny)
            ]));
        }
        int firstJoin = closed ? 0 : 1;
        int lastJoin = closed ? points.Count - 1 : points.Count - 2;
        for (int index = firstJoin; index <= lastJoin; index++)
        {
            Point previous = points[(index - 1 + points.Count) % points.Count];
            Point vertex = points[index];
            Point next = points[(index + 1) % points.Count];
            AddJoin(previous, vertex, next);
        }
        if (!closed && lineCap == RendererLineCap.Round)
        {
            output.Add(Circle(points[0], radius));
            output.Add(Circle(points[^1], radius));
        }

        void AddJoin(Point previous, Point vertex, Point next)
        {
            double inX = vertex.X - previous.X, inY = vertex.Y - previous.Y;
            double outX = next.X - vertex.X, outY = next.Y - vertex.Y;
            double inLength = Math.Sqrt(inX * inX + inY * inY);
            double outLength = Math.Sqrt(outX * outX + outY * outY);
            if (inLength <= 1e-12 || outLength <= 1e-12) return;
            inX /= inLength;
            inY /= inLength;
            outX /= outLength;
            outY /= outLength;
            double turn = inX * outY - inY * outX;
            double dot = inX * outX + inY * outY;
            if (lineJoin == RendererLineJoin.Round)
            {
                if (Math.Abs(turn) > 1e-9 || dot < 0) output.Add(Circle(vertex, radius));
                return;
            }
            if (Math.Abs(turn) <= 1e-9) return;
            double side = turn > 0 ? -1 : 1;
            Point firstOuter = new(vertex.X - side * inY * radius, vertex.Y + side * inX * radius);
            Point secondOuter = new(vertex.X - side * outY * radius, vertex.Y + side * outX * radius);
            if (lineJoin == RendererLineJoin.Miter)
            {
                double cosHalf = Math.Sqrt(Math.Max(0, (1 + dot) / 2));
                if (cosHalf > 1e-9 && 1 / cosHalf <= miterLimit)
                {
                    double miterLength = radius / cosHalf;
                    double bisectX = inX - outX, bisectY = inY - outY;
                    double bisectLength = Math.Sqrt(bisectX * bisectX + bisectY * bisectY);
                    if (bisectLength > 1e-12)
                    {
                        Point miter = new(vertex.X + bisectX / bisectLength * miterLength,
                            vertex.Y + bisectY / bisectLength * miterLength);
                        output.Add(Oriented([vertex, firstOuter, miter, secondOuter]));
                        return;
                    }
                }
            }
            output.Add(Oriented([vertex, firstOuter, secondOuter]));
        }

        static double Distance(Point a, Point b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    private static Point[] Circle(Point center, double radius)
    {
        int segments = Math.Clamp((int)Math.Ceiling(Math.PI * radius), 8, 96);
        var polygon = new Point[segments];
        for (int index = 0; index < segments; index++)
        {
            double angle = 2 * Math.PI * index / segments;
            polygon[index] = new Point(center.X + Math.Cos(angle) * radius,
                center.Y + Math.Sin(angle) * radius);
        }
        return polygon;
    }

    /// <summary>Returns the polygon with positive signed area so unions accumulate with nonzero winding.</summary>
    private static Point[] Oriented(Point[] polygon)
    {
        double twiceArea = 0;
        for (int index = 0; index < polygon.Length; index++)
        {
            Point from = polygon[index], to = polygon[(index + 1) % polygon.Length];
            twiceArea += from.X * to.Y - to.X * from.Y;
        }
        if (twiceArea < 0) Array.Reverse(polygon);
        return polygon;
    }

    /// <summary>Multiplies the coverage of every active clip at one pixel.</summary>
    private static int ClipCoverage(IReadOnlyList<ClipRegion> clips, int x, int y)
    {
        int coverage = 255;
        for (int index = 0; index < clips.Count && coverage != 0; index++)
        {
            int clip = clips[index].Mask.At(x, y);
            coverage = clip == 255 ? coverage : coverage == 255 ? clip : (coverage * clip + 127) / 255;
        }
        return coverage;
    }

    /// <summary>Returns the combined clip coverage at one pixel as an opacity factor.</summary>
    private static double ClipAlpha(IReadOnlyList<ClipRegion> clips, int x, int y) =>
        clips.Count == 0 ? 1 : ClipCoverage(clips, x, y) / 255d;

    /// <summary>Paints one color through a coverage mask, honoring clips, masks, and blending.</summary>
    private static void PaintCoverage(RasterSurface pixels, int width, int height, CoverageMask mask,
        in Color color, double alpha, RendererBlendMode blendMode, IReadOnlyList<ClipRegion> clips,
        GraphicsSoftMask? graphicsSoftMask, KnockoutState? knockout,
        CancellationToken cancellationToken, bool alphaIsShape = false)
    {
        if (mask.IsEmpty || color.DoesNotPaint) return;
        // Zero opacity on a plain RGB surface leaves every pixel and group alpha unchanged;
        // knockout groups still need the object recorded, so they keep the full path.
        if (alpha <= 0 && knockout is null && pixels.GroupShape is null
            && pixels.Ink is null && pixels.RgbProfile is null) return;
        int left = Math.Max(mask.Left, pixels.Left), top = Math.Max(mask.Top, pixels.Top);
        int right = Math.Min(mask.Right, pixels.Right), bottom = Math.Min(mask.Bottom, pixels.Bottom);
        // Rectangular clips are fully applied by these bounds, so only antialiased clip
        // masks need the per-pixel coverage lookup.
        bool rectangularClips = true;
        foreach (ClipRegion clip in clips)
        {
            left = Math.Max(left, clip.Mask.Left);
            top = Math.Max(top, clip.Mask.Top);
            right = Math.Min(right, clip.Mask.Right);
            bottom = Math.Min(bottom, clip.Mask.Bottom);
            if (clip.Mask.Coverage is not null) rectangularClips = false;
        }
        if (right <= left || bottom <= top) return;
        graphicsSoftMask = graphicsSoftMask?.ForBounds(left, top, right, bottom);
        bool perPixelClip = clips.Count > 0 && !rectangularClips;
        // Group alpha is maintained alongside the direct RGB paths with the compositor's own
        // formula, so tracked groups take the same fast paths as plain pages.
        bool simpleBlend = pixels.Ink is null && pixels.RgbProfile is null && graphicsSoftMask is null && knockout is null
            && pixels.GroupShape is null
            && blendMode is RendererBlendMode.Normal or RendererBlendMode.Compatible;
        byte[]? groupAlpha = pixels.GroupAlpha;
        bool direct = alpha >= 1 && simpleBlend;
        bool directInk = pixels.Ink is not null && alpha >= 1
            && (color.OverprintComponents & 16) == 0
            && graphicsSoftMask is null && knockout is null
            && pixels.GroupShape is null
            && blendMode is RendererBlendMode.Normal or RendererBlendMode.Compatible;
        uint ink = pixels.Ink is not null ? pixels.GetInk(color) : 0;
        // On ink surfaces the compositor resolves the paint's ink through GetInk for every
        // pixel; a color that already carries this surface's ink resolves to the same value
        // without the per-pixel color comparison.
        Color paint = pixels.Ink is not null
            ? color with { Ink = ink, InkProfile = pixels.InkProfile } : color;
        byte[]? opaqueBlend = simpleBlend && alpha > 0 && alpha < 1
            && (long)(right - left) * (bottom - top) >= 4096
            ? CreateOpaqueBlendLookup(color, alpha * 255 / 255d, blendMode) : null;
        byte[]? coverage = mask.Coverage;
        if (directInk && !perPixelClip)
        {
            // Opaque interiors share bulk ink and alpha writes; partial edges keep the compositor.
            byte[] inkData = pixels.Ink!;
            int count = right - left;
            for (int y = top; y < bottom; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int rowOffset = pixels.Offset(left, y);
                if (coverage is not null)
                {
                    int maskRow = mask.RowOffset(y) - mask.Left;
                    for (int x = left; x < right;)
                    {
                        int cover = coverage[maskRow + x];
                        if (cover == 255)
                        {
                            int start = x++;
                            while (x < right && coverage[maskRow + x] == 255) x++;
                            int offset = rowOffset + (start - left) * 4;
                            int length = x - start;
                            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
                                inkData.AsSpan(offset, length * 4)).Fill(ink);
                            pixels.SetAlphaRun(offset, length, 255);
                            pixels.GroupAlpha?.AsSpan(offset / 4, length).Fill(255);
                        }
                        else
                        {
                            if (cover != 0)
                            {
                                int offset = rowOffset + (x - left) * 4;
                                double sourceAlpha = Math.Clamp(alpha * cover / 255d, 0, 1);
                                if (pixels.GroupAlpha is not null)
                                    TrackGroupAlpha(pixels.GroupAlpha, offset, sourceAlpha);
                                if (alpha == 1 && pixels.Alpha(offset) == 255)
                                    WriteInk(inkData, offset,
                                        BlendCoverageInk(ink, ReadInk(inkData, offset), cover));
                                else SetInkPixel(pixels, offset, ink, 0, sourceAlpha, blendMode);
                            }
                            x++;
                        }
                    }
                    continue;
                }
                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
                    inkData.AsSpan(rowOffset, count * 4)).Fill(ink);
                pixels.SetAlphaRun(rowOffset, count, 255);
                pixels.GroupAlpha?.AsSpan(rowOffset / 4, count).Fill(255);
            }
            return;
        }
        if (direct && !perPixelClip)
        {
            PaintDirectCoverageRows(pixels, width, mask, color, alpha, blendMode,
                graphicsSoftMask, knockout, cancellationToken, left, top, right,
                bottom, groupAlpha, coverage);
            return;
        }
        // Row chunks write disjoint pixels, disjoint group alpha entries and disjoint knockout
        // object slots, and the graphics soft mask was materialized by ForBounds above, so this
        // loop parallelizes by row on plain RGB surfaces. Ink and profiled RGB surfaces stay
        // serial: their per-pixel GetInk and GetRgb calls memoize into surface state.
        // A local function cannot capture the 'in' parameter, so the direct path reads a copy.
        Color directColor = color;
        if (pixels.Ink is null && pixels.RgbProfile is null)
        {
            PaintCoverageRowsParallel(pixels, width, mask, directColor, paint, alpha,
                blendMode, clips, graphicsSoftMask, knockout, cancellationToken,
                alphaIsShape, left, top, right, bottom, perPixelClip, directInk,
                ink, direct, groupAlpha, coverage, opaqueBlend);
        }
        else PaintCoverageRows(pixels, width, mask, directColor, paint, alpha,
            blendMode, clips, graphicsSoftMask, knockout, cancellationToken,
            alphaIsShape, left, top, right, bottom, perPixelClip, directInk,
            ink, direct, groupAlpha, coverage, opaqueBlend);
        return;
    }

    private static void PaintDirectCoverageRows(RasterSurface pixels, int width,
        CoverageMask mask, Color fillColor, double alpha, RendererBlendMode blendMode,
        GraphicsSoftMask? graphicsSoftMask, KnockoutState? knockout,
        CancellationToken cancellationToken, int left, int top, int right, int bottom,
        byte[]? groupAlpha, byte[]? coverage)
    {
        // Opaque normal-blend fills on a plain RGB surface: full rows of a rectangular
        // mask are one span fill, and antialiased edges blend against opaque pixels in
        // place. Pixels with a transparent destination still use the compositor.
        byte[] data = pixels.Data;
        uint packed = fillColor.Blue | (uint)fillColor.Green << 8
            | (uint)fillColor.Red << 16 | 0xFF000000u;
        ForEachRow(top, bottom, (long)(right - left) * (bottom - top), cancellationToken,
            (rowStart, rowEnd) =>
            {
                for (int y = rowStart; y < rowEnd; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int rowOffset = pixels.Offset(left, y);
                    if (coverage is null)
                    {
                        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
                            data.AsSpan(rowOffset, (right - left) * 4)).Fill(packed);
                        groupAlpha?.AsSpan(rowOffset / 4, right - left).Fill(255);
                        continue;
                    }
                    int maskRow = mask.RowOffset(y) - mask.Left;
                    for (int x = left; x < right; x++)
                    {
                        int cover = coverage[maskRow + x];
                        if (cover == 0) continue;
                        int offset = rowOffset + (x - left) * 4;
                        if (cover == 255)
                        {
                            Unsafe.WriteUnaligned(ref data[offset], packed);
                            if (groupAlpha is not null) groupAlpha[offset / 4] = 255;
                            continue;
                        }
                        if (data[offset + 3] == 255)
                        {
                            int inverse = 255 - cover;
                            data[offset] = (byte)((fillColor.Blue * cover + data[offset] * inverse + 127) / 255);
                            data[offset + 1] = (byte)((fillColor.Green * cover + data[offset + 1] * inverse + 127) / 255);
                            data[offset + 2] = (byte)((fillColor.Red * cover + data[offset + 2] * inverse + 127) / 255);
                            if (groupAlpha is not null)
                                TrackGroupAlpha(groupAlpha, offset, alpha * cover / 255d);
                            continue;
                        }
                        SetPixel(pixels, width, x, y, fillColor, alpha * cover / 255d,
                            blendMode, graphicsSoftMask, knockout);
                    }
                }
            });
    }

    private static void PaintCoverageRowsParallel(RasterSurface pixels, int width,
        CoverageMask mask, Color directColor, Color paint, double alpha,
        RendererBlendMode blendMode, IReadOnlyList<ClipRegion> clips,
        GraphicsSoftMask? graphicsSoftMask, KnockoutState? knockout,
        CancellationToken cancellationToken, bool alphaIsShape, int left, int top,
        int right, int bottom, bool perPixelClip, bool directInk, uint ink,
        bool direct, byte[]? groupAlpha, byte[]? coverage, byte[]? opaqueBlend)
    {
        ForEachRow(top, bottom, (long)(right - left) * (bottom - top), cancellationToken,
            (rowStart, rowEnd) => PaintCoverageRows(pixels, width, mask, directColor,
                paint, alpha, blendMode, clips, graphicsSoftMask, knockout,
                cancellationToken, alphaIsShape, left, rowStart, right, rowEnd,
                perPixelClip, directInk, ink, direct, groupAlpha, coverage, opaqueBlend));
    }

    private static void PaintCoverageRows(RasterSurface pixels, int width,
        CoverageMask mask, Color directColor, Color paint, double alpha,
        RendererBlendMode blendMode, IReadOnlyList<ClipRegion> clips,
        GraphicsSoftMask? graphicsSoftMask, KnockoutState? knockout,
        CancellationToken cancellationToken, bool alphaIsShape, int left, int firstRow,
        int right, int lastRow, bool perPixelClip, bool directInk, uint ink,
        bool direct, byte[]? groupAlpha, byte[]? coverage, byte[]? opaqueBlend)
    {
        uint directPacked = directColor.Blue | (uint)directColor.Green << 8
            | (uint)directColor.Red << 16 | 0xFF000000u;
        for (int y = firstRow; y < lastRow; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int maskRow = mask.RowOffset(y) - mask.Left;
            for (int x = left; x < right; x++)
            {
                int cover = coverage is null ? 255 : coverage[maskRow + x];
                if (cover == 0) continue;
                if (perPixelClip)
                {
                    int clipCover = ClipCoverage(clips, x, y);
                    if (clipCover == 0) continue;
                    cover = clipCover == 255 ? cover : (cover * clipCover + 127) / 255;
                }
                if (directInk && cover == 255)
                {
                    int offset = pixels.Offset(x, y);
                    WriteInk(pixels.Ink!, offset, ink);
                    pixels.SetAlpha(offset, 255);
                    if (pixels.GroupAlpha is not null) pixels.GroupAlpha[offset / 4] = 255;
                    continue;
                }
                if (direct)
                {
                    int offset = pixels.Offset(x, y);
                    if (cover == 255)
                    {
                        Unsafe.WriteUnaligned(ref pixels.Data[offset], directPacked);
                        if (groupAlpha is not null) groupAlpha[offset / 4] = 255;
                        continue;
                    }
                    if (pixels[offset + 3] == 255)
                    {
                        int inverse = 255 - cover;
                        pixels[offset] = (byte)((directColor.Blue * cover + pixels[offset] * inverse + 127) / 255);
                        pixels[offset + 1] = (byte)((directColor.Green * cover + pixels[offset + 1] * inverse + 127) / 255);
                        pixels[offset + 2] = (byte)((directColor.Red * cover + pixels[offset + 2] * inverse + 127) / 255);
                        if (groupAlpha is not null) TrackGroupAlpha(groupAlpha, offset, alpha * cover / 255d);
                        continue;
                    }
                }
                if (opaqueBlend is not null && cover == 255)
                {
                    int offset = pixels.Offset(x, y);
                    if (pixels[offset + 3] == 255)
                    {
                        pixels[offset] = opaqueBlend[pixels[offset] * 4];
                        pixels[offset + 1] = opaqueBlend[pixels[offset + 1] * 4 + 1];
                        pixels[offset + 2] = opaqueBlend[pixels[offset + 2] * 4 + 2];
                        if (groupAlpha is not null) TrackGroupAlpha(groupAlpha, offset, alpha * cover / 255d);
                        continue;
                    }
                }
                SetPixel(pixels, width, x, y, paint, alpha * cover / 255d, blendMode,
                    graphicsSoftMask, knockout, shape: cover / 255d, alphaIsShape: alphaIsShape,
                    resolvedInk: pixels.Ink is not null ? ink : null);
            }
        }
    }

    private static void TrackGroupAlpha(byte[] groupAlpha, int offset, double opacity)
    {
        double sourceAlpha = Math.Clamp(opacity, 0, 1);
        groupAlpha[offset / 4] = (byte)Math.Round(sourceAlpha * 255
            + groupAlpha[offset / 4] * (1 - sourceAlpha));
    }

    private static byte[] CreateOpaqueBlendLookup(Color color, double alpha, RendererBlendMode blendMode)
    {
        var lookup = new byte[256 * 4];
        var surface = new RasterSurface(lookup, 0, 0, 256, 1);
        for (int value = 0; value < 256; value++)
        {
            int offset = value * 4;
            lookup[offset] = lookup[offset + 1] = lookup[offset + 2] = (byte)value;
            lookup[offset + 3] = 255;
            // Use the original compositor to preserve its floating-point rounding.
            SetPixel(surface, 256, value, 0, color, alpha, blendMode);
        }
        return lookup;
    }

    /// <summary>Unions two masks by taking the larger coverage.</summary>
    private static CoverageMask UnionMasks(CoverageMask first, CoverageMask second, int width,
        int height)
    {
        if (first.IsEmpty) return second;
        if (second.IsEmpty) return first;
        var page = new byte[checked(width * height)];
        UnionInto(page, width, first);
        UnionInto(page, width, second);
        return CoverageMask.FromPageBuffer(page, width, height);
    }

    /// <summary>Unions a coverage mask into a page-sized byte buffer.</summary>
    private static void UnionInto(byte[] page, int width, CoverageMask mask)
    {
        if (mask.IsEmpty) return;
        for (int y = mask.Top; y < mask.Bottom; y++)
        {
            int row = y * width;
            int maskRow = mask.RowOffset(y);
            for (int x = mask.Left; x < mask.Right; x++)
            {
                byte value = mask.Coverage is null ? (byte)255 : mask.Coverage[maskRow + x - mask.Left];
                if (value > page[row + x]) page[row + x] = value;
            }
        }
    }
}
