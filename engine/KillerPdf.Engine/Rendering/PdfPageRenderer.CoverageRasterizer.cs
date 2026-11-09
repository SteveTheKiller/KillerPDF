using System.Numerics;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    /// <summary>
    /// Exact-area coverage rasterizer. Each pixel receives the integral of the winding number
    /// over its square, then the nonzero or even-odd rule and the byte map min(255, floor(256c)).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derivation. Count winding with a ray to the left: a point (x, y) has winding
    /// w(x, y) = sum over edges e crossing height y left of x of s_e, where s_e is +1 for an edge
    /// that runs down the page and -1 for one that runs up. For pixel column i and row j the
    /// coverage integral is then
    /// </para>
    /// <para>
    ///   A(i, j) = integral over y in [j, j+1] of sum_e s_e * clamp(i + 1 - x_e(y), 0, 1) dy,
    /// </para>
    /// <para>
    /// so every edge acts on a row independently and linearly. Cut an edge's part inside row j
    /// at the column boundaries it crosses. On a piece inside column c with height h and end
    /// abscissae c + f0 and c + f1, x_e is linear, so the piece adds exactly
    /// s_e * h * (1 - (f0 + f1) / 2) to pixel c and s_e * h to every pixel right of it. Storing
    /// the difference between neighbouring pixels instead, the piece adds
    /// s_e * h * (1 - (f0 + f1) / 2) to cell c and s_e * h * (f0 + f1) / 2 to cell c + 1, and a
    /// running sum along the row turns the cells back into A. Rows are independent, so the
    /// sweep keeps one row of cells and visits only the rows and columns an edge reaches.
    /// </para>
    /// <para>
    /// Clipping. For a visible pixel (i &gt;= 0) clamp(i + 1 - x, 0, 1) is the same for every
    /// x &lt;= 0, and for a pixel left of the raster's right side it is zero for x &gt;= width.
    /// Edge pieces left of the raster therefore add their whole height to cell 0, and pieces
    /// right of it are dropped. Neither moves a vertex, so in-range geometry is never re-rounded.
    /// Rows outside the raster or the requested bounds are simply not visited.
    /// </para>
    /// <para>
    /// Fixed point. Vertices are quantized to integers in 1/256 pixel with ties to even. Areas
    /// are integers in units of 1/131072 pixel (twice a unit square), held in 64-bit cells, so
    /// sums never overflow and are independent of the order edges are added. Within a row an
    /// edge's pieces are measured by their progress along y from the row segment's left end,
    /// and each piece's share is the difference of rounded progress values. Those differences
    /// telescope, so an edge always adds exactly 512 times its integer height in the row: a pixel
    /// no edge crosses gets an exact integer winding times the full area, which maps to exactly 0
    /// or 255. Only the split of a piece between cells c and c + 1 is rounded, by at most half a
    /// unit. Every intermediate is computed from offsets to the edge's first vertex and to the
    /// pixel grid, never from absolute coordinates, so a whole-pixel translation reproduces the
    /// same integers. Edges are stored with y increasing and the direction kept as a sign, so a
    /// reversed polygon produces the same integers negated and identical coverage.
    /// </para>
    /// <para>
    /// Coordinates up to 2^25 units on every axis are used directly; products stay well inside
    /// the 53-bit double mantissa. Larger finite edges are first cut to the raster's rows and
    /// clamped to x in [0, width] in double precision. A polygon with a non-finite vertex adds
    /// nothing.
    /// </para>
    /// <para>
    /// Accumulation routes. Because the cell values are integers and sums do not depend on
    /// order, each edge-row can be computed whenever convenient. Rasters of up to 32,768 pixels
    /// keep a full grid of cells and add every edge-row as the edge arrives. Larger rasters keep
    /// one row of cells: an edge that stays inside the raster and spans only a few rows is turned
    /// into its cell values when it is added, filed by row and merged with repeats of the same
    /// cell; every other edge is stored and visited row by row in the sweep. Both routes use the
    /// same per-row formulas, so they give the same integers.
    /// </para>
    /// </remarks>
    private sealed class CoverageRasterizer
    {
        internal const int Scale = 256;
        private const int ScaleShift = 8;
        private const long FullArea = 2L * Scale * Scale;
        private const int ByteShift = 9;
        private const double DirectLimit = 1 << 25;
        private const int SmallWorkingBytes = 64 * 1024;
        private const int PreclearBytes = 256 * 1024;
        private const int MaximumRetainedEdges = 4 * 1024 * 1024 / 48;
        private const int MaximumRetainedCells = 4 * 1024 * 1024 / 12;
        private const int MaximumRetainedChunks = MaximumRetainedCells / 32;
        private const int TrimAfterSmallSweeps = 8;
        private const long MaximumRetainedBytes = 16L * 1024 * 1024;
        // Interior edges spanning fewer rows than this are turned into cell entries when added.
        private const int EagerRows = 4;
        private int _smallSweeps;

        private struct Edge
        {
            internal int X0, Y0, Dx, Dy, Sign, FirstRow, LastRow;
            internal double DxPerDy, DyPerDx;
            // Every cell the edge can touch lies inside the raster, so no clipping applies.
            internal bool Interior;
        }

        // Two cell values for one raster cell. Key holds the row in its high half and the column
        // in its low half; rasters have at most 32,768 rows and columns, so both fit.
        private struct CellEntry
        {
            internal int Key, Left, Right;
        }

        // An open entry merges repeats until its values could leave the int range. One piece
        // adds at most 512 * 256 units, so this bound leaves ample room.
        private const long MergeLimit = 1L << 30;

        private Edge[] _edges = new Edge[64];
        // Geometry added since the reset; the stored edges and cell entries are its parts.
        private int _edgeCount, _storedEdges;
        // Cell entries are filed by row as they arrive, in chains of fixed-size chunks, so the
        // sweep reads each row's entries without sorting them. A row's chain is valid only
        // while its stamp equals the current one, which spares clearing the row tables.
        private const int ChunkEntries = 32;
        private CellEntry[] _chunkData = new CellEntry[ChunkEntries * 64];
        private int[] _chunkNext = new int[64];
        private int _chunkCount;
        private int[] _rowHead = [], _rowTail = [], _tailFill = [], _rowStamp = [];
        private int _stamp = 1;
        private int _pendingKey;
        private long _pendingLeft, _pendingRight;
        private int[] _quantX = [], _quantY = [];
        private bool _pendingOpen;
        private int _minColumn, _maxColumn, _minRow, _maxRow;
        private long[] _cells = [];
        private ulong[] _touched = [];
        private int _wordMin, _wordMax;
        private int[] _rowStarts = [];
        private Edge[] _sorted = [];
        private int[] _active = [];
        private byte[] _working = [];
        // Rasters up to this many pixels, glyph masks among them, accumulate straight into a
        // full grid of cells instead of storing and sorting edges.
        private const int DenseCells = 2 * 128 * 128;
        private bool _dense;
        private long[] _grid = [];
        private int _rowBase;

        internal CoverageRasterizer(int width, int height)
        {
            Width = width;
            Height = height;
            _dense = (long)width * height <= DenseCells;
            Reset();
        }

        internal int Width { get; private set; }
        internal int Height { get; private set; }

        internal void Reset()
        {
            if (_dense && _edgeCount > 0) ClearGrid();
            if (_chunkCount > 0)
            {
                // A new stamp empties every row chain at once.
                _chunkCount = 0;
                if (++_stamp == int.MaxValue)
                {
                    Array.Clear(_rowStamp);
                    _stamp = 1;
                }
            }
            _edgeCount = _storedEdges = 0;
            _pendingOpen = false;
            _rowBase = 0;
            _minColumn = _minRow = int.MaxValue;
            _maxColumn = _maxRow = int.MinValue;
        }

        internal void Reset(int width, int height)
        {
            Reset();
            Width = width;
            Height = height;
            _dense = (long)width * height <= DenseCells;
        }

        internal void AddPolygons(IEnumerable<Point[]> polygons)
        {
            foreach (Point[] polygon in polygons) AddPolygon(new PixelVertices(polygon));
        }

        internal void AddPagePolygons(IReadOnlyList<List<Point>> polygons, RasterFrame frame)
        {
            for (int index = 0; index < polygons.Count; index++)
                AddPolygon(new PageVertices(polygons[index], frame));
        }

        internal void AddGlyphPolygons(IReadOnlyList<Point[]> polygons, double a, double b, double c,
            double d, double offsetX, double offsetY, int shiftX, int shiftY)
        {
            for (int index = 0; index < polygons.Count; index++)
                AddPolygon(new GlyphVertices(polygons[index], a, b, c, d, offsetX, offsetY, shiftX, shiftY));
        }

        internal void AddDeviceGlyphPolygons(GlyphPoint[][] polygons, int offsetX, int offsetY)
        {
            foreach (GlyphPoint[] polygon in polygons)
                if (!_dense || !TryAddInteriorPolygon(polygon, offsetX, offsetY))
                    AddPolygon(new DeviceVertices(polygon, offsetX, offsetY));
        }

        // Glyph outlines sit at least a pixel inside their raster. When an integer polygon stays
        // clear of the right edge, edges inside one cell go straight into the grid with the
        // same integers Accumulate would add; every other edge takes the general route.
        private bool TryAddInteriorPolygon(GlyphPoint[] polygon, int offsetX, int offsetY)
        {
            int count = polygon.Length;
            if (count < 3) return true;
            long minX = long.MaxValue, maxX = long.MinValue, minY = long.MaxValue, maxY = long.MinValue;
            foreach (GlyphPoint point in polygon)
            {
                long x = (long)point.X + offsetX, y = (long)point.Y + offsetY;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            if (minX < 0 || minY < 0 || maxX >= (long)(Width - 1) * Scale || maxY > (long)Height * Scale)
                return false;
            int needed = Width * Height;
            if (_grid.Length < needed) _grid = new long[needed];
            long[] grid = _grid;
            int width = Width;
            bool stored = false;
            int previousX = polygon[count - 1].X + offsetX, previousY = polygon[count - 1].Y + offsetY;
            for (int index = 0; index < count; index++)
            {
                int x0 = previousX, y0 = previousY;
                int x1 = polygon[index].X + offsetX, y1 = polygon[index].Y + offsetY;
                previousX = x1;
                previousY = y1;
                if (y0 == y1) continue;
                int sign = 1;
                if (y0 > y1)
                {
                    (x0, y0, x1, y1) = (x1, y1, x0, y0);
                    sign = -1;
                }
                int firstRow = y0 >> ScaleShift, lastRow = (y1 - 1) >> ScaleShift;
                if (firstRow < _minRow) _minRow = firstRow;
                if (lastRow > _maxRow) _maxRow = lastRow;
                stored = true;
                if (firstRow != lastRow)
                {
                    AddInteriorEdge(x0, y0, x1, y1, sign, firstRow, lastRow);
                    continue;
                }
                int column0 = x0 >> ScaleShift, offset = x0 & (Scale - 1), dx = x1 - x0;
                long height = y1 - y0, total = height * (2 * Scale);
                long low = Math.Min(0, dx) + offset, high = Math.Max(0, dx) + offset;
                int first = (int)(low >> ScaleShift);
                if (dx != 0 && first != (int)((high - 1) >> ScaleShift))
                {
                    AddInteriorEdge(x0, y0, x1, y1, sign, firstRow, lastRow);
                    continue;
                }
                long share = dx == 0 ? height * (2 * offset)
                    : Math.Clamp(height * (low + high - 2L * first * Scale), 0, total);
                int cell = firstRow * width + column0 + first;
                grid[cell] += sign * (total - share);
                grid[cell + 1] += sign * share;
            }
            if (stored)
            {
                _edgeCount++;
                NoteColumns((int)(minX >> ScaleShift), (int)(maxX >> ScaleShift));
            }
            return true;
        }

        // An interior edge (y0 < y1) that leaves one cell. Rows where it stays in one column use
        // Accumulate's own formulas inline; rows that cross columns go through Accumulate.
        private void AddInteriorEdge(int x0, int y0, int x1, int y1, int sign, int firstRow, int lastRow)
        {
            var edge = new Edge
            {
                X0 = x0, Y0 = y0, Dx = x1 - x0, Dy = y1 - y0, Sign = sign,
                FirstRow = firstRow, LastRow = lastRow,
            };
            if (firstRow != lastRow) edge.DxPerDy = (double)edge.Dx / edge.Dy;
            int column0 = x0 >> ScaleShift;
            for (int row = firstRow; row <= lastRow; row++)
                InteriorRow<LeaveCells>(ref edge, row, _grid, row * Width + column0);
        }

        private readonly struct MarkCells { }

        private readonly struct LeaveCells { }

        private readonly struct EmitCells { }

        // One row of an edge whose cells all lie inside the raster, written at
        // cells[origin + column] for local columns. The integers equal Accumulate's: rows inside
        // one column use its single-column formula and the others its column walk, without
        // the clipping an interior edge never needs. With TMark as MarkCells, touched cells are
        // recorded for the row sweep; the type argument lets the JIT drop the other branch.
        private void InteriorRow<TMark>(ref Edge edge, int row, long[] cells, int origin)
            where TMark : struct
        {
            bool mark = typeof(TMark) == typeof(MarkCells), emit = typeof(TMark) == typeof(EmitCells);
            int rowTop = (row << ScaleShift) - edge.Y0;
            int ya = Math.Max(0, rowTop), yb = Math.Min(edge.Dy, rowTop + Scale);
            if (yb <= ya) return;
            int sign = edge.Sign, offset = edge.X0 & (Scale - 1);
            double dxPerDy = edge.DxPerDy;
            long height = yb - ya, total = height * (2 * Scale);
            if (edge.FirstRow == edge.LastRow)
            {
                // A one-row edge has integer ends; inside one column its share is exact.
                long low = Math.Min(0, edge.Dx) + offset, high = Math.Max(0, edge.Dx) + offset;
                int column = (int)(low >> ScaleShift);
                if (edge.Dx == 0 || column == (int)((high - 1) >> ScaleShift))
                {
                    long share = Math.Clamp(height * (low + high - 2L * column * Scale), 0, total);
                    Put<TMark>(cells, origin + column, sign * (total - share), sign * share, row);
                    return;
                }
            }
            double xa = ya * dxPerDy + offset;
            double xb = (yb == edge.Dy ? edge.Dx : yb * dxPerDy) + offset;
            double leftX = Math.Min(xa, xb), rightX = Math.Max(xa, xb);
            int first = (int)Math.Floor(leftX * (1.0 / Scale));
            int last = (int)Math.Ceiling(rightX * (1.0 / Scale)) - 1;
            if (last <= first)
            {
                double mean = leftX + rightX - 2.0 * first * Scale;
                long share = Math.Clamp((long)Math.Round(height * mean), 0, total);
                Put<TMark>(cells, origin + first, sign * (total - share), sign * share, row);
                return;
            }
            // Several columns: Accumulate's walk, with no clipping needed inside the raster.
            double dyPerDx = edge.DyPerDx;
            if (dyPerDx == 0) edge.DyPerDx = dyPerDx = (double)edge.Dy / edge.Dx;
            double leftY = xa <= xb ? ya : yb;
            double previousX = leftX, previousProgress = 0;
            long previousShare = 0;
            int rowCell = origin;
            if (mark) Mark(origin + first, origin + last + 1);
            {
                for (int column = first; column <= last; column++)
                {
                    double nextX, nextProgress;
                    long nextShare;
                    if (column == last)
                    {
                        nextX = rightX;
                        nextProgress = height;
                        nextShare = total;
                    }
                    else
                    {
                        nextX = (double)(column + 1) * Scale;
                        double y = Math.Clamp((nextX - offset) * dyPerDx, ya, yb);
                        nextProgress = Math.Abs(y - leftY);
                        nextShare = (long)Math.Round(nextProgress * (2 * Scale));
                    }
                    long piece = nextShare - previousShare;
                    double sum = previousX + nextX - 2.0 * column * Scale;
                    long right = Math.Clamp((long)Math.Round((nextProgress - previousProgress) * sum), 0, Math.Max(piece, 0));
                    if (emit) AddCellEntry(row, rowCell + column, sign * (piece - right), sign * right);
                    else
                    {
                        cells[rowCell + column] += sign * (piece - right);
                        cells[rowCell + column + 1] += sign * right;
                    }
                    previousX = nextX;
                    previousProgress = nextProgress;
                    previousShare = nextShare;
                }
            }
        }

        // Adds a piece's two cell values: into the row cells, marked for the row sweep, or as a
        // cell entry for the given row.
        private void Put<TMark>(long[] cells, int at, long left, long right, int row) where TMark : struct
        {
            if (typeof(TMark) == typeof(EmitCells))
            {
                AddCellEntry(row, at, left, right);
                return;
            }
            cells[at] += left;
            cells[at + 1] += right;
            if (typeof(TMark) != typeof(MarkCells)) return;
            int lowWord = at >> 6, highWord = (at + 1) >> 6;
            _touched[lowWord] |= 1UL << at;
            _touched[highWord] |= 1UL << (at + 1);
            if (lowWord < _wordMin) _wordMin = lowWord;
            if (highWord > _wordMax) _wordMax = highWord;
        }

        // Records cells [from, to] as touched in the current row.
        private void Mark(int from, int to)
        {
            int firstWord = from >> 6, lastWord = to >> 6;
            if (firstWord == lastWord)
                _touched[firstWord] |= (ulong.MaxValue << from) & (ulong.MaxValue >> (63 - (to & 63)));
            else
            {
                _touched[firstWord] |= ulong.MaxValue << from;
                for (int word = firstWord + 1; word < lastWord; word++) _touched[word] = ulong.MaxValue;
                _touched[lastWord] |= ulong.MaxValue >> (63 - (to & 63));
            }
            if (firstWord < _wordMin) _wordMin = firstWord;
            if (lastWord > _wordMax) _wordMax = lastWord;
        }

        // Vertex sources yield quantized fixed-point coordinates as integral doubles.
        private interface IVertices
        {
            int Count { get; }
            bool AlwaysFinite { get; }
            void Get(int index, out double x, out double y);
        }

        private readonly struct PixelVertices(Point[] points) : IVertices
        {
            public int Count => points.Length;
            public bool AlwaysFinite => false;

            public void Get(int index, out double x, out double y)
            {
                x = Math.Round(points[index].X * Scale);
                y = Math.Round(points[index].Y * Scale);
            }
        }

        private readonly struct PageVertices(List<Point> points, RasterFrame frame) : IVertices
        {
            public int Count => points.Count;
            public bool AlwaysFinite => false;

            public void Get(int index, out double x, out double y)
            {
                Point point = points[index];
                x = Math.Round(frame.PixelX(point.X) * Scale);
                y = Math.Round(frame.PixelY(point.Y) * Scale);
            }
        }

        private readonly struct GlyphVertices(Point[] points, double a, double b, double c, double d,
            double offsetX, double offsetY, int shiftX, int shiftY) : IVertices
        {
            public int Count => points.Length;
            public bool AlwaysFinite => false;

            public void Get(int index, out double x, out double y)
            {
                Point point = points[index];
                double dx = point.X * a + point.Y * c + offsetX;
                double dy = point.X * b + point.Y * d + offsetY;
                x = Math.Round((dx - shiftX) * Scale);
                y = Math.Round((dy - shiftY) * Scale);
            }
        }

        private readonly struct DeviceVertices(GlyphPoint[] points, int offsetX, int offsetY) : IVertices
        {
            public int Count => points.Length;
            public bool AlwaysFinite => true;

            public void Get(int index, out double x, out double y)
            {
                x = (long)points[index].X + offsetX;
                y = (long)points[index].Y + offsetY;
            }
        }

        private void AddPolygon<T>(T polygon) where T : struct, IVertices
        {
            int count = polygon.Count;
            if (count < 3) return;
            double x, y;
            if (!_dense)
            {
                // Quantize once into integers; a polygon that stays inside the raster then takes
                // the integer route, and every other one the general route below.
                if (_quantX.Length < count)
                {
                    _quantX = new int[Math.Max(count, _quantX.Length * 2)];
                    _quantY = new int[_quantX.Length];
                }
                double limitX = (double)(Width - 1) * Scale, limitY = (double)Height * Scale;
                bool interior = true;
                for (int index = 0; index < count; index++)
                {
                    polygon.Get(index, out x, out y);
                    // A non-finite vertex anywhere drops the whole polygon.
                    if (!double.IsFinite(x) || !double.IsFinite(y)) return;
                    if (x >= 0 && x < limitX && y >= 0 && y <= limitY)
                    {
                        _quantX[index] = (int)x;
                        _quantY[index] = (int)y;
                    }
                    else interior = false;
                }
                if (interior)
                {
                    AddInteriorPolygon(count);
                    return;
                }
            }
            else
            {
                // A non-finite vertex anywhere drops the whole polygon before any edge is added.
                for (int index = 0; !polygon.AlwaysFinite && index < count; index++)
                {
                    polygon.Get(index, out x, out y);
                    if (!double.IsFinite(x) || !double.IsFinite(y)) return;
                }
            }
            polygon.Get(0, out double firstX, out double firstY);
            x = firstX;
            y = firstY;
            for (int index = 1; index < count; index++)
            {
                polygon.Get(index, out double nextX, out double nextY);
                AddEdge(x, y, nextX, nextY);
                x = nextX;
                y = nextY;
            }
            AddEdge(x, y, firstX, firstY);
        }

        // Adds a quantized polygon whose vertices all lie inside the raster, with no left
        // folding or far-edge cutting to consider. Every value matches the general route:
        // single-cell edges become the same cell entries and the rest the same stored edges.
        private void AddInteriorPolygon(int count)
        {
            int[] xs = _quantX, ys = _quantY;
            int minX = int.MaxValue, maxX = int.MinValue, minRow = _minRow, maxRow = _maxRow;
            bool stored = false;
            int previousX = xs[count - 1], previousY = ys[count - 1];
            for (int index = 0; index < count; index++)
            {
                int x0 = previousX, y0 = previousY, x1 = xs[index], y1 = ys[index];
                previousX = x1;
                previousY = y1;
                if (y0 == y1) continue;
                int sign = 1;
                if (y0 > y1)
                {
                    (x0, y0, x1, y1) = (x1, y1, x0, y0);
                    sign = -1;
                }
                int firstRow = y0 >> ScaleShift, lastRow = (y1 - 1) >> ScaleShift;
                if (firstRow < minRow) minRow = firstRow;
                if (lastRow > maxRow) maxRow = lastRow;
                int low = Math.Min(x0, x1), high = Math.Max(x0, x1);
                if (low < minX) minX = low;
                if (high > maxX) maxX = high;
                stored = true;
                int dx = x1 - x0;
                if (firstRow == lastRow)
                {
                    int offset = x0 & (Scale - 1);
                    long left = Math.Min(0, dx) + offset, right = Math.Max(0, dx) + offset;
                    int first = (int)(left >> ScaleShift);
                    if (dx == 0 || first == (int)((right - 1) >> ScaleShift))
                    {
                        long height = y1 - y0, total = height * (2 * Scale);
                        long share = Math.Clamp(height * (left + right - 2L * first * Scale), 0, total);
                        AddCellEntry(firstRow, (x0 >> ScaleShift) + first, sign * (total - share), sign * share);
                        continue;
                    }
                }
                var edge = new Edge
                {
                    X0 = x0, Y0 = y0, Dx = dx, Dy = y1 - y0, Sign = sign,
                    FirstRow = firstRow, LastRow = lastRow, Interior = true,
                };
                if (firstRow != lastRow) edge.DxPerDy = (double)edge.Dx / edge.Dy;
                if (lastRow - firstRow < EagerRows)
                {
                    for (int row = firstRow; row <= lastRow; row++)
                        InteriorRow<EmitCells>(ref edge, row, _cells, x0 >> ScaleShift);
                    continue;
                }
                if (_storedEdges == _edges.Length) Array.Resize(ref _edges, Math.Max(64, _edges.Length * 2));
                _edges[_storedEdges++] = edge;
            }
            if (!stored) return;
            _minRow = minRow;
            _maxRow = maxRow;
            _edgeCount++;
            NoteColumns(minX >> ScaleShift, maxX >> ScaleShift);
        }

        /// <summary>
        /// Rasterizes an axis-aligned rectangle directly: its coverage is the product of the
        /// row and column overlaps, which is what the general sweep computes for two vertical
        /// edges. Returns null for any other quad.
        /// </summary>
        internal static CoverageMask? TryRectangle(Point[] polygon, int width, int height)
        {
            if (polygon.Length != 4 && (polygon.Length != 5 || polygon[0] != polygon[4])) return null;
            Span<double> xs = stackalloc double[4];
            Span<double> ys = stackalloc double[4];
            for (int index = 0; index < 4; index++)
            {
                xs[index] = Math.Round(polygon[index].X * Scale);
                ys[index] = Math.Round(polygon[index].Y * Scale);
                if (!double.IsFinite(xs[index]) || !double.IsFinite(ys[index])) return null;
            }
            bool across = ys[0] == ys[1] && xs[1] == xs[2] && ys[2] == ys[3] && xs[3] == xs[0];
            bool down = xs[0] == xs[1] && ys[1] == ys[2] && xs[2] == xs[3] && ys[3] == ys[0];
            if (!across && !down) return null;
            // Clamping an axis-aligned rectangle to the raster changes no visible pixel.
            long left = (long)Math.Clamp(Math.Min(xs[0], xs[2]), 0, (double)width * Scale);
            long right = (long)Math.Clamp(Math.Max(xs[0], xs[2]), 0, (double)width * Scale);
            long top = (long)Math.Clamp(Math.Min(ys[0], ys[2]), 0, (double)height * Scale);
            long bottom = (long)Math.Clamp(Math.Max(ys[0], ys[2]), 0, (double)height * Scale);
            if (right <= left || bottom <= top) return CoverageMask.Empty;
            if (((left | right | top | bottom) & (Scale - 1)) == 0)
                return CoverageMask.Rectangle((int)(left >> ScaleShift), (int)(top >> ScaleShift),
                    (int)(right >> ScaleShift), (int)(bottom >> ScaleShift));
            int firstColumn = (int)(left >> ScaleShift), lastColumn = (int)((right - 1) >> ScaleShift);
            int firstRow = (int)(top >> ScaleShift), lastRow = (int)((bottom - 1) >> ScaleShift);
            long widest = Math.Max(Overlap(left, right, firstColumn), Overlap(left, right, lastColumn));
            if (lastColumn - firstColumn >= 2) widest = Scale;
            long tallest = Math.Max(Overlap(top, bottom, firstRow), Overlap(top, bottom, lastRow));
            if (lastRow - firstRow >= 2) tallest = Scale;
            // Trim edge rows and columns whose coverage maps to zero, as the general sweep does.
            if (Level(Overlap(left, right, firstColumn), tallest) == 0) firstColumn++;
            if (lastColumn >= firstColumn && Level(Overlap(left, right, lastColumn), tallest) == 0) lastColumn--;
            if (Level(Overlap(top, bottom, firstRow), widest) == 0) firstRow++;
            if (lastRow >= firstRow && Level(Overlap(top, bottom, lastRow), widest) == 0) lastRow--;
            if (lastColumn < firstColumn || lastRow < firstRow) return CoverageMask.Empty;
            int maskWidth = lastColumn - firstColumn + 1, rows = lastRow - firstRow + 1;
            bool repeated = rows > 3;
            var coverage = new byte[checked(maskWidth * (repeated ? 3 : rows))];
            for (int row = 0; row < (repeated ? 3 : rows); row++)
            {
                int y = repeated && row == 2 ? lastRow : firstRow + row;
                long rowOverlap = Overlap(top, bottom, y);
                Span<byte> line = coverage.AsSpan(row * maskWidth, maskWidth);
                byte middle = Level(Scale, rowOverlap);
                line.Fill(middle);
                line[0] = Level(Overlap(left, right, firstColumn), rowOverlap);
                line[^1] = Level(Overlap(left, right, lastColumn), rowOverlap);
            }
            return new CoverageMask(firstColumn, firstRow, lastColumn + 1, lastRow + 1, coverage,
                repeatedMiddleRows: repeated);

            static long Overlap(long low, long high, int cell) =>
                Math.Min(high, (long)(cell + 1) << ScaleShift) - Math.Max(low, (long)cell << ScaleShift);

            // Coverage ox * oy / 65536 of a pixel, as min(255, floor(256c)).
            static byte Level(long across, long down) => (byte)Math.Min(255, across * down >> ScaleShift);
        }

        // Adds one edge between quantized vertices, given as integral doubles in fixed units.
        private void AddEdge(double x0, double y0, double x1, double y1)
        {
            if (y0 == y1) return;
            int sign = 1;
            if (y0 > y1)
            {
                (x0, y0, x1, y1) = (x1, y1, x0, y0);
                sign = -1;
            }
            double width = (double)Width * Scale, height = (double)Height * Scale;
            if (y1 <= 0 || y0 >= height) return;
            if (x0 >= width && x1 >= width)
            {
                // Right of the raster: no visible pixel lies right of it, but the shape may
                // still reach the right side, so the row bounds must too.
                NoteColumns(Width, Width);
                return;
            }
            if (Math.Abs(x0) <= DirectLimit && Math.Abs(x1) <= DirectLimit
                && Math.Abs(y0) <= DirectLimit && Math.Abs(y1) <= DirectLimit)
            {
                // Wholly left of the raster acts exactly like the left side itself.
                if (x0 <= 0 && x1 <= 0) x0 = x1 = 0;
                StoreEdge((int)x0, (int)y0, (int)x1, (int)y1, sign);
                return;
            }
            AddFarEdge(x0, y0, x1, y1, sign, width, height);
        }

        // Cuts a far-reaching edge (y0 < y1) to the raster's rows and clamps it to x in
        // [0, width]. Each cut point is computed from the endpoint nearer to it, so an edge with
        // one vertex near the raster keeps its precision there.
        private void AddFarEdge(double x0, double y0, double x1, double y1, int sign,
            double width, double height)
        {
            double startX = y0 < 0 ? Lerp(x0, x1, y0, y1, 0) : x0, startY = Math.Max(y0, 0);
            double endX = y1 > height ? Lerp(x0, x1, y0, y1, height) : x1, endY = Math.Min(y1, height);
            if (!double.IsFinite(startX) || !double.IsFinite(endX)) return;
            Span<double> xs = stackalloc double[4];
            Span<double> ys = stackalloc double[4];
            int count = 0;
            xs[count] = startX;
            ys[count++] = startY;
            // Crossings of x = 0 and x = width, in order of increasing y.
            double low = Math.Min(startX, endX), high = Math.Max(startX, endX);
            double firstBorder = startX <= endX ? 0 : width, secondBorder = startX <= endX ? width : 0;
            for (int crossing = 0; crossing < 2; crossing++)
            {
                double border = crossing == 0 ? firstBorder : secondBorder;
                if (border <= low || border >= high) continue;
                double y = Lerp(startY, endY, startX, endX, border);
                if (!double.IsFinite(y)) return;
                xs[count] = border;
                ys[count++] = Math.Clamp(y, startY, endY);
            }
            xs[count] = endX;
            ys[count++] = endY;
            for (int index = 0; index + 1 < count; index++)
            {
                double ax = xs[index], bx = xs[index + 1];
                if ((ax + bx) / 2 >= width)
                {
                    NoteColumns(Width, Width);
                    continue;
                }
                double ay = Math.Round(ys[index]), by = Math.Round(ys[index + 1]);
                if (by <= ay) continue;
                StoreEdge((int)Math.Round(Math.Clamp(ax, 0, width)), (int)ay,
                    (int)Math.Round(Math.Clamp(bx, 0, width)), (int)by, sign);
            }
        }

        // The first coordinate of the point where the edge reaches the given second coordinate.
        private static double Lerp(double a0, double a1, double b0, double b1, double b)
        {
            double span = b1 - b0;
            if (Math.Abs(b - b0) <= Math.Abs(b1 - b))
            {
                double t = (b - b0) / span, delta = a1 - a0;
                return double.IsFinite(delta) ? a0 + t * delta : a0 * (1 - t) + a1 * t;
            }
            double s = (b1 - b) / span, back = a0 - a1;
            return double.IsFinite(back) ? a1 + s * back : a1 * (1 - s) + a0 * s;
        }

        private void NoteColumns(int low, int high)
        {
            if (low < _minColumn) _minColumn = low;
            if (high > _maxColumn) _maxColumn = high;
        }

        private void StoreEdge(int x0, int y0, int x1, int y1, int sign)
        {
            var edge = new Edge
            {
                X0 = x0, Y0 = y0, Dx = x1 - x0, Dy = y1 - y0, Sign = sign,
                FirstRow = y0 >> ScaleShift, LastRow = (y1 - 1) >> ScaleShift,
            };
            // An edge inside one row only ever uses its exact endpoints.
            if (edge.FirstRow != edge.LastRow) edge.DxPerDy = (double)edge.Dx / edge.Dy;
            NoteColumns(Math.Min(x0, x1) >> ScaleShift, Math.Max(x0, x1) >> ScaleShift);
            if (edge.FirstRow < _minRow) _minRow = edge.FirstRow;
            if (edge.LastRow > _maxRow) _maxRow = edge.LastRow;
            _edgeCount++;
            if (_dense)
            {
                // Small rasters keep a cell row per raster row, so edges land at once.
                int needed = Width * Height;
                if (_grid.Length < needed) _grid = new long[needed];
                int last = Math.Min(edge.LastRow, Height - 1);
                for (int row = Math.Max(edge.FirstRow, 0); row <= last; row++)
                {
                    _rowBase = row * Width;
                    Accumulate(ref edge, row);
                }
                return;
            }
            edge.Interior = Math.Min(x0, x1) >= 0 && Math.Max(x0, x1) < (Width - 1) * Scale;
            if (edge.Interior && edge.LastRow - edge.FirstRow < EagerRows)
            {
                // A short interior edge becomes its cell values right away, computed by the same
                // row routine the sweep uses, so it never joins the active edges.
                int last = Math.Min(edge.LastRow, Height - 1);
                for (int row = Math.Max(edge.FirstRow, 0); row <= last; row++)
                    InteriorRow<EmitCells>(ref edge, row, _cells, x0 >> ScaleShift);
                return;
            }
            if (_storedEdges == _edges.Length) Array.Resize(ref _edges, Math.Max(64, _edges.Length * 2));
            _edges[_storedEdges++] = edge;
        }

        // Cell entries from one polygon often land in the same cell one after another, so the
        // latest entry stays open and absorbs repeats before it is stored.
        private void AddCellEntry(int row, int column, long left, long right)
        {
            int key = row << 16 | column;
            if (_pendingOpen && _pendingKey == key)
            {
                long mergedLeft = _pendingLeft + left, mergedRight = _pendingRight + right;
                if (Math.Abs(mergedLeft) < MergeLimit && Math.Abs(mergedRight) < MergeLimit)
                {
                    _pendingLeft = mergedLeft;
                    _pendingRight = mergedRight;
                    return;
                }
            }
            FlushCellEntry();
            _pendingKey = key;
            _pendingLeft = left;
            _pendingRight = right;
            _pendingOpen = true;
        }

        private void FlushCellEntry()
        {
            if (!_pendingOpen) return;
            _pendingOpen = false;
            int row = _pendingKey >> 16;
            if (_rowStamp.Length < Height)
            {
                int length = Math.Max(Height, _rowStamp.Length * 2);
                Array.Resize(ref _rowStamp, length);
                Array.Resize(ref _rowHead, length);
                Array.Resize(ref _rowTail, length);
                Array.Resize(ref _tailFill, length);
            }
            int tail;
            if (_rowStamp[row] != _stamp)
            {
                _rowStamp[row] = _stamp;
                _rowHead[row] = _rowTail[row] = tail = NewChunk();
                _tailFill[row] = 0;
            }
            else if (_tailFill[row] == ChunkEntries)
            {
                tail = NewChunk();
                _chunkNext[_rowTail[row]] = tail;
                _rowTail[row] = tail;
                _tailFill[row] = 0;
            }
            else tail = _rowTail[row];
            _chunkData[tail * ChunkEntries + _tailFill[row]++] = new CellEntry
            {
                Key = _pendingKey, Left = (int)_pendingLeft, Right = (int)_pendingRight,
            };
        }

        private int NewChunk()
        {
            if (_chunkCount == _chunkNext.Length)
            {
                Array.Resize(ref _chunkNext, _chunkNext.Length * 2);
                Array.Resize(ref _chunkData, _chunkNext.Length * ChunkEntries);
            }
            _chunkNext[_chunkCount] = -1;
            return _chunkCount++;
        }

        // Clears every grid cell the current geometry can have touched.
        private void ClearGrid()
        {
            int top = Math.Max(_minRow, 0), bottom = Math.Min(_maxRow + 1, Height);
            int left = Math.Max(_minColumn, 0), right = Math.Min(_maxColumn + 2, Width);
            for (int row = top; row < bottom && left < right; row++)
                _grid.AsSpan(row * Width + left, right - left).Clear();
        }

        internal CoverageMask Sweep(bool evenOdd, bool rent = false,
            (int Left, int Top, int Right, int Bottom)? bounds = null)
        {
            int top = Math.Max(_minRow, 0), bottom = Math.Min(_maxRow + 1, Height);
            int left = Math.Max(_minColumn, 0), right = Math.Min(_maxColumn + 1, Width);
            if (bounds is { } limit)
            {
                // Bounds trim only the rows visited and the pixels written; cells left of them
                // still accumulate, so every written pixel keeps its unbounded value.
                top = Math.Max(top, limit.Top);
                bottom = Math.Min(bottom, limit.Bottom);
                left = Math.Max(left, limit.Left);
                right = Math.Min(right, limit.Right);
            }
            int edgeCount = _edgeCount;
            if (edgeCount == 0 || right <= left || bottom <= top)
            {
                Reset();
                return CoverageMask.Empty;
            }
            int rows = bottom - top, maskWidth = right - left;
            int size = checked(maskWidth * rows);
            byte[] buffer;
            // An owned mask is swept into pooled scratch and copied once into its exact array, so
            // trimming never costs a second allocation.
            bool pooled;
            if (!rent && size <= SmallWorkingBytes)
            {
                if (_working.Length < size) _working = new byte[Math.Max(size, Math.Min(SmallWorkingBytes, _working.Length * 2))];
                buffer = _working;
                pooled = false;
            }
            else
            {
                buffer = RasterBuffers.Rent(size);
                pooled = true;
            }
            int tightLeft = int.MaxValue, tightRight = int.MinValue, tightTop = -1, tightBottom = -1;
            if (_dense)
            {
                int start = Math.Max(_minColumn, 0);
                for (int row = top; row < bottom; row++)
                {
                    Span<long> cells = _grid.AsSpan(row * Width, right);
                    Span<byte> line = buffer.AsSpan((row - top) * maskWidth, maskWidth);
                    long sum = 0;
                    int first = -1, last = -1;
                    for (int column = start; column < right; column++)
                    {
                        sum += cells[column];
                        if (column < left) continue;
                        byte value = ToByte(sum, evenOdd);
                        line[column - left] = value;
                        if (value == 0) continue;
                        if (first < 0) first = column;
                        last = column;
                    }
                    if (first < 0) continue;
                    if (tightTop < 0) tightTop = row;
                    tightBottom = row + 1;
                    if (first < tightLeft) tightLeft = first;
                    if (last + 1 > tightRight) tightRight = last + 1;
                }
                Reset();
                return Produce(buffer, pooled, rent, top, left, maskWidth,
                    tightLeft, tightTop, tightRight, tightBottom);
            }
            FlushCellEntry();
            int storedEdges = _storedEdges, chunkCount = _chunkCount;
            PrepareScratch(rows, storedEdges);
            // A small mask is cleared in one call, so rows write only their nonzero runs. Large
            // masks write their zero runs row by row instead of writing most bytes twice.
            bool cleared = size <= PreclearBytes;
            if (cleared) buffer.AsSpan(0, size).Clear();

            // Order the edges by their first visible row with a counting sort.
            Span<int> starts = _rowStarts.AsSpan(0, rows + 1);
            starts.Clear();
            for (int index = 0; index < storedEdges; index++)
            {
                ref Edge edge = ref _edges[index];
                if (edge.LastRow < top || edge.FirstRow >= bottom) continue;
                starts[Math.Max(edge.FirstRow, top) - top + 1]++;
            }
            for (int row = 1; row <= rows; row++) starts[row] += starts[row - 1];
            // The edges themselves move into row order, so the edges active in a row sit close
            // together in memory however the path ordered its subpaths.
            Edge[] edges = _sorted;
            for (int index = 0; index < storedEdges; index++)
            {
                ref Edge edge = ref _edges[index];
                if (edge.LastRow < top || edge.FirstRow >= bottom) continue;
                edges[starts[Math.Max(edge.FirstRow, top) - top]++] = edge;
            }
            // starts[row] now holds the end of that row's run, which is the next row's start.

            int activeCount = 0, next = 0;
            CellEntry[] chunkData = _chunkData;
            int[] chunkNext = _chunkNext;
            int[] active = _active;
            long[] rowCells = _cells;
            ulong[] touched = _touched;
            for (int row = top; row < bottom; row++)
            {
                int end = starts[row - top];
                while (next < end) active[activeCount++] = next++;
                _wordMin = int.MaxValue;
                _wordMax = -1;
                if (chunkCount > 0 && _rowStamp.Length > row && _rowStamp[row] == _stamp)
                {
                    int wordMin = _wordMin, wordMax = _wordMax, tail = _rowTail[row];
                    for (int chunk = _rowHead[row]; chunk >= 0; chunk = chunkNext[chunk])
                    {
                        int start = chunk * ChunkEntries;
                        int stop = start + (chunk == tail ? _tailFill[row] : ChunkEntries);
                        for (int index = start; index < stop; index++)
                        {
                            ref CellEntry entry = ref chunkData[index];
                            int at = entry.Key & 0xFFFF;
                            rowCells[at] += entry.Left;
                            rowCells[at + 1] += entry.Right;
                            int lowWord = at >> 6, highWord = (at + 1) >> 6;
                            touched[lowWord] |= 1UL << at;
                            touched[highWord] |= 1UL << (at + 1);
                            if (lowWord < wordMin) wordMin = lowWord;
                            if (highWord > wordMax) wordMax = highWord;
                        }
                    }
                    _wordMin = wordMin;
                    _wordMax = wordMax;
                }
                for (int index = activeCount - 1; index >= 0; index--)
                {
                    ref Edge edge = ref edges[active[index]];
                    if (edge.Interior) InteriorRow<MarkCells>(ref edge, row, rowCells, edge.X0 >> ScaleShift);
                    else Accumulate(ref edge, row);
                    if (edge.LastRow <= row) active[index] = active[--activeCount];
                }
                Span<byte> line = buffer.AsSpan((row - top) * maskWidth, maskWidth);
                (int first, int last) = ResolveRow(line, left, right, evenOdd, cleared);
                if (first >= 0)
                {
                    if (tightTop < 0) tightTop = row;
                    tightBottom = row + 1;
                    if (first < tightLeft) tightLeft = first;
                    if (last + 1 > tightRight) tightRight = last + 1;
                }
            }
            Reset();
            TrimRetained(storedEdges, chunkCount);
            return Produce(buffer, pooled, rent, top, left, maskWidth,
                tightLeft, tightTop, tightRight, tightBottom);
        }

        // Moves the nonzero part of the working rows into the returned mask.
        private static CoverageMask Produce(byte[] buffer, bool pooled, bool rent, int top, int left,
            int maskWidth, int tightLeft, int tightTop, int tightRight, int tightBottom)
        {
            if (tightTop < 0)
            {
                if (pooled) RasterBuffers.Return(buffer);
                return CoverageMask.Empty;
            }
            int tightWidth = tightRight - tightLeft, tightHeight = tightBottom - tightTop;
            if (rent)
            {
                // Rows already sit at their final place when only bottom rows were trimmed.
                if (tightLeft == left && tightWidth == maskWidth && tightTop == top)
                    return new CoverageMask(tightLeft, tightTop, tightRight, tightBottom, buffer, rented: true);
                for (int row = 0; row < tightHeight; row++)
                    buffer.AsSpan((tightTop - top + row) * maskWidth + tightLeft - left, tightWidth)
                        .CopyTo(buffer.AsSpan(row * tightWidth, tightWidth));
                return new CoverageMask(tightLeft, tightTop, tightRight, tightBottom, buffer, rented: true);
            }
            // Every byte is copied in below, so the owned array skips the runtime's zeroing.
            byte[] coverage = GC.AllocateUninitializedArray<byte>(checked(tightWidth * tightHeight));
            for (int row = 0; row < tightHeight; row++)
                buffer.AsSpan((tightTop - top + row) * maskWidth + tightLeft - left, tightWidth)
                    .CopyTo(coverage.AsSpan(row * tightWidth, tightWidth));
            if (pooled) RasterBuffers.Return(buffer);
            return new CoverageMask(tightLeft, tightTop, tightRight, tightBottom, coverage);
        }

        private void PrepareScratch(int rows, int edgeCount)
        {
            if (_cells.Length < Width + 1) _cells = new long[Width + 1];
            int words = (Width >> 6) + 1;
            if (_touched.Length < words) _touched = new ulong[words];
            if (_rowStarts.Length < rows + 1) _rowStarts = new int[Math.Max(rows + 1, _rowStarts.Length * 2)];
            if (_sorted.Length < edgeCount)
            {
                _sorted = new Edge[Math.Max(edgeCount, _sorted.Length * 2)];
                _active = new int[_sorted.Length];
            }
        }

        // Keeps per-thread scratch modest. Storage past the limit stays while large paths keep
        // arriving, so a page of them does not reallocate per fill, and is released once
        // several sweeps in a row have needed little of it.
        private void TrimRetained(int storedEdges, int chunkCount)
        {
            bool large = _edges.Length > MaximumRetainedEdges || _sorted.Length > MaximumRetainedEdges
                || _chunkNext.Length > MaximumRetainedChunks;
            if (!large) return;
            // Past a hard ceiling the storage goes at once, so an idle thread never keeps it.
            long retained = (_edges.Length + (long)_sorted.Length) * 48 + _active.Length * 4L
                + _chunkData.Length * 12L + _chunkNext.Length * 4L + (_quantX.Length + (long)_quantY.Length) * 4;
            if (retained > MaximumRetainedBytes) _smallSweeps = TrimAfterSmallSweeps;
            else if (storedEdges > MaximumRetainedEdges / 4 || chunkCount > MaximumRetainedChunks / 4)
            {
                _smallSweeps = 0;
                return;
            }
            if (++_smallSweeps < TrimAfterSmallSweeps) return;
            _smallSweeps = 0;
            if (_edges.Length > MaximumRetainedEdges) _edges = new Edge[1024];
            if (_sorted.Length > MaximumRetainedEdges)
            {
                _sorted = [];
                _active = [];
            }
            if (_chunkNext.Length > MaximumRetainedChunks)
            {
                _chunkNext = new int[64];
                _chunkData = new CellEntry[64 * ChunkEntries];
            }
            if (_quantX.Length > MaximumRetainedCells) _quantX = _quantY = [];
        }

        // Adds a piece's values to a cell and its right neighbour, the same as two AddCell calls.
        private void AddPair(int column, long left, long right)
        {
            if (column < 0 || column + 1 >= Width)
            {
                AddCell(column, left);
                AddCell(column + 1, right);
                return;
            }
            if (_dense)
            {
                _grid[_rowBase + column] += left;
                _grid[_rowBase + column + 1] += right;
                return;
            }
            _cells[column] += left;
            _cells[column + 1] += right;
            int lowWord = column >> 6, highWord = (column + 1) >> 6;
            _touched[lowWord] |= 1UL << column;
            _touched[highWord] |= 1UL << (column + 1);
            if (lowWord < _wordMin) _wordMin = lowWord;
            if (highWord > _wordMax) _wordMax = highWord;
        }

        private void AddCell(int column, long value)
        {
            if (column >= Width || value == 0) return;
            if (_dense)
            {
                _grid[_rowBase + column] += value;
                return;
            }
            _cells[column] += value;
            int word = column >> 6;
            _touched[word] |= 1UL << column;
            if (word < _wordMin) _wordMin = word;
            if (word > _wordMax) _wordMax = word;
        }

        // Adds one edge's part inside a row. Every quantity is an offset from the edge's first
        // vertex or from the pixel grid, so whole-pixel translation reproduces it exactly.
        private void Accumulate(ref Edge edge, int row)
        {
            int rowTop = (row << ScaleShift) - edge.Y0;
            int ya = Math.Max(0, rowTop), yb = Math.Min(edge.Dy, rowTop + Scale);
            if (yb <= ya) return;
            int sign = edge.Sign;
            int column0 = edge.X0 >> ScaleShift, offset = edge.X0 & (Scale - 1);
            long height = yb - ya;
            if (edge.Dx == 0)
            {
                long full = sign * height * (2 * Scale);
                if (column0 < 0) AddCell(0, full);
                else
                {
                    long share = sign * height * (2 * offset);
                    AddPair(column0, full - share, share);
                }
                return;
            }
            if (edge.FirstRow == edge.LastRow)
            {
                // The whole edge lies in this row, so its ends are integers. Inside one column
                // the share is exact; this matches the walk below for the same inputs.
                long low = Math.Min(0, edge.Dx) + offset, high = Math.Max(0, edge.Dx) + offset;
                int first = (int)(low >> ScaleShift), last = (int)((high - 1) >> ScaleShift);
                if (first == last)
                {
                    long total = height * (2 * Scale);
                    int column = column0 + first;
                    if (column < 0) AddCell(0, sign * total);
                    else if (column < Width)
                    {
                        long share = Math.Clamp(height * (low + high - 2L * first * Scale), 0, total);
                        AddPair(column, sign * (total - share), sign * share);
                    }
                    return;
                }
            }
            double xa = ya * edge.DxPerDy + offset;
            double xb = (yb == edge.Dy ? edge.Dx : yb * edge.DxPerDy) + offset;
            double leftX, rightX, leftY;
            if (xa <= xb) { leftX = xa; rightX = xb; leftY = ya; }
            else { leftX = xb; rightX = xa; leftY = yb; }
            int firstColumn = (int)Math.Floor(leftX * (1.0 / Scale));
            int lastColumn = (int)Math.Ceiling(rightX * (1.0 / Scale)) - 1;
            if (lastColumn < firstColumn) lastColumn = firstColumn;
            long totalShare = height * (2 * Scale);
            if (lastColumn == firstColumn)
            {
                // One column: the whole piece, split by its mean abscissa. The general walk
                // below gives the same integers for this case.
                int column = column0 + firstColumn;
                if (column < 0) AddCell(0, sign * totalShare);
                else if (column < Width)
                {
                    double mean = leftX + rightX - 2.0 * firstColumn * Scale;
                    long share = Math.Clamp((long)Math.Round(height * mean), 0, totalShare);
                    AddPair(column, sign * (totalShare - share), sign * share);
                }
                return;
            }
            // The inverse slope is needed only by edges that cross a column boundary.
            double dyPerDx = edge.DyPerDx;
            if (dyPerDx == 0) edge.DyPerDx = dyPerDx = (double)edge.Dy / edge.Dx;

            double previousX = leftX, previousProgress = 0;
            long previousShare = 0;
            int startColumn = firstColumn;
            if (firstColumn < -column0)
            {
                // The part left of the raster covers every visible pixel of this row.
                double border = -(double)column0 * Scale;
                if (rightX <= border)
                {
                    AddCell(0, sign * totalShare);
                    return;
                }
                double y = Math.Clamp((border - offset) * dyPerDx, ya, yb);
                previousProgress = Math.Abs(y - leftY);
                previousShare = (long)Math.Round(previousProgress * (2 * Scale));
                AddCell(0, sign * previousShare);
                previousX = border;
                startColumn = -column0;
            }
            int endColumn = Math.Min(lastColumn, Width - 1 - column0);
            for (int column = startColumn; column <= endColumn; column++)
            {
                double nextX, nextProgress;
                long nextShare;
                if (column == lastColumn)
                {
                    nextX = rightX;
                    nextProgress = height;
                    nextShare = totalShare;
                }
                else
                {
                    nextX = (double)(column + 1) * Scale;
                    double y = Math.Clamp((nextX - offset) * dyPerDx, ya, yb);
                    nextProgress = Math.Abs(y - leftY);
                    nextShare = (long)Math.Round(nextProgress * (2 * Scale));
                }
                long piece = nextShare - previousShare;
                double sum = previousX + nextX - 2.0 * column * Scale;
                long right = Math.Clamp((long)Math.Round((nextProgress - previousProgress) * sum), 0, Math.Max(piece, 0));
                AddPair(column0 + column, sign * (piece - right), sign * right);
                previousX = nextX;
                previousProgress = nextProgress;
                previousShare = nextShare;
            }
        }

        // Turns the row's cells into coverage bytes for [left, right) and clears the cells. On a
        // line already cleared only nonzero runs are written. Returns the first and last
        // nonzero pixel, or -1 when the row is empty.
        private (int First, int Last) ResolveRow(Span<byte> line, int left, int right, bool evenOdd,
            bool cleared)
        {
            long[] cells = _cells;
            ulong[] touched = _touched;
            long sum = 0;
            byte current = 0;
            // next is the first pixel not yet written; pixels before it hold their bytes.
            int next = left, first = -1, last = -1;
            for (int word = _wordMin; word <= _wordMax; word++)
            {
                ulong bits = touched[word];
                if (bits == 0) continue;
                touched[word] = 0;
                do
                {
                    int column = (word << 6) + BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    long value = cells[column];
                    cells[column] = 0;
                    // Cells right of the output only need clearing.
                    if (column >= right) continue;
                    if (column > next)
                    {
                        // The pixels since the last touched cell share the running coverage.
                        if (current != 0)
                        {
                            FillRun(line, next - left, column - next, current);
                            if (first < 0) first = next;
                            last = column - 1;
                        }
                        else if (!cleared) FillRun(line, next - left, column - next, 0);
                    }
                    sum += value;
                    current = evenOdd ? ToByte(sum, true) : NonzeroByte(sum);
                    if (column < left) continue;
                    line[column - left] = current;
                    if (current != 0)
                    {
                        if (first < 0) first = column;
                        last = column;
                    }
                    next = column + 1;
                }
                while (bits != 0);
            }
            if (next < right && current != 0)
            {
                FillRun(line, next - left, right - next, current);
                if (first < 0) first = next;
                last = right - 1;
            }
            else if (next < right && !cleared) FillRun(line, next - left, right - next, 0);
            return (first, last);
        }

        private static void FillRun(Span<byte> line, int start, int count, byte value)
        {
            if (count <= 8)
                for (int index = start; index < start + count; index++) line[index] = value;
            else line.Slice(start, count).Fill(value);
        }

        private static byte NonzeroByte(long sum)
        {
            long area = Math.Abs(sum);
            return area >= FullArea ? (byte)255 : (byte)(area >> ByteShift);
        }

        private static byte ToByte(long sum, bool evenOdd)
        {
            long area = Math.Abs(sum);
            if (evenOdd)
            {
                area %= 2 * FullArea;
                if (area > FullArea) area = 2 * FullArea - area;
            }
            return area >= FullArea ? (byte)255 : (byte)(area >> ByteShift);
        }
    }
}
