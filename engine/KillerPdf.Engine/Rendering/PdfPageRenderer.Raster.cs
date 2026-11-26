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
                Vector.Widen(new Vector<byte>(first[x..]), out Vector<ushort> a0, out Vector<ushort> a1);
                Vector.Widen(new Vector<byte>(second[x..]), out Vector<ushort> b0, out Vector<ushort> b1);
                Vector<ushort> low = a0 * b0 + rounding, high = a1 * b1 + rounding;
                // Exact division by 255 for products rounded within the byte-coverage range.
                low = (low + one + (low >> 8)) >> 8;
                high = (high + one + (high >> 8)) >> 8;
                Vector.Narrow(low, high).CopyTo(destination[x..]);
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

        /// <summary>Returns whether both masks describe the same pixel coverage.</summary>
        internal bool HasSameCoverage(CoverageMask other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (Left != other.Left || Top != other.Top || Right != other.Right
                || Bottom != other.Bottom || (Coverage is null) != (other.Coverage is null))
                return false;
            if (Coverage is null) return true;
            for (int y = Top; y < Bottom; y++)
                if (!Coverage.AsSpan(RowOffset(y), Width)
                    .SequenceEqual(other.Coverage!.AsSpan(other.RowOffset(y), Width)))
                    return false;
            return true;
        }

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

    /// <summary>Rasterizes pixel-space polygons into a trimmed coverage mask.</summary>
    [ThreadStatic]
    private static CoverageRasterizer? _sharedRasterizer;

    private static CoverageMask RasterizePolygons(IEnumerable<Point[]> pixelPolygons, bool evenOdd,
        int width, int height, bool rent = false,
        (int Left, int Top, int Right, int Bottom)? bounds = null)
    {
        if (pixelPolygons is IReadOnlyList<Point[]> { Count: 1 } polygons
            && CoverageRasterizer.TryRectangle(polygons[0], width, height) is { } rectangle)
            return rectangle;
        CoverageRasterizer? rasterizer = _sharedRasterizer;
        if (rasterizer is null || rasterizer.Width != width || rasterizer.Height != height)
            _sharedRasterizer = rasterizer = new CoverageRasterizer(width, height);
        rasterizer.Reset();
        rasterizer.AddPolygons(pixelPolygons);
        return rasterizer.Sweep(evenOdd, rent, bounds);
    }

    // Filled text glyphs repeat constantly at the same size, so their coverage masks are
    // cached per outline, device matrix, and fractional origin offset. Retain positions
    // to one sixty-fourth of a pixel so caching does not visibly move text edges.
    // The entry and byte budgets still bound storage; large glyphs bypass the cache.
    private const int GlyphSubpixelSteps = 4;

    /// <summary>
    /// Moves a glyph's device origin onto the quarter-pixel grid the glyph cache uses, so filled,
    /// stroked, pattern-filled and clipping text place a glyph identically whether or not its
    /// mask is cached.
    /// </summary>
    private static Matrix QuantizeGlyphOrigin(Matrix transform, RasterFrame frame)
    {
        double x = transform.E * frame.ScaleX;
        double y = frame.Height - transform.F * frame.ScaleY;
        if (!double.IsFinite(x) || !double.IsFinite(y) || frame.ScaleX == 0 || frame.ScaleY == 0
            || Math.Abs(x) > int.MaxValue / 2 || Math.Abs(y) > int.MaxValue / 2)
            return transform;
        double gridX = Math.Round(x * GlyphSubpixelSteps) / GlyphSubpixelSteps;
        double gridY = Math.Round(y * GlyphSubpixelSteps) / GlyphSubpixelSteps;
        return transform with { E = gridX / frame.ScaleX, F = (frame.Height - gridY) / frame.ScaleY };
    }
    private const int MaximumCachedGlyphPixels = 128 * 128;
    private const long MaximumGlyphMaskCacheBytes = 8L * 1024 * 1024;
    private const long MaximumGlyphGeometryCacheBytes = 512L * 1024;

    internal readonly record struct GlyphMaskKey(PdfGlyphOutline Outline,
        long A, long B, long C, long D, int SubX, int SubY);

    /// <summary>A glyph mask whose bounds are relative to the glyph origin's pixel.</summary>
    internal sealed record GlyphMask(CoverageMask Mask);

    internal readonly record struct GlyphPoint(int X, int Y);

    internal sealed record GlyphGeometry(GlyphPoint[][]? Paths,
        double MinX, double MinY, double MaxX, double MaxY, int ShiftX, int ShiftY, long Bytes);

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
    private readonly BoundedCache<GlyphMaskKey, GlyphGeometry?> _glyphGeometryCache;

    private static BoundedCache<GlyphMaskKey, GlyphMask?> CreateGlyphMaskCache() => new(
        8192, GlyphMaskKeyComparer.Instance, MaximumGlyphMaskCacheBytes,
        glyph => glyph?.Mask.Coverage?.LongLength ?? 0);

    private static BoundedCache<GlyphMaskKey, GlyphGeometry?> CreateGlyphGeometryCache() => new(
        256, GlyphMaskKeyComparer.Instance, MaximumGlyphGeometryCacheBytes,
        geometry => geometry?.Bytes ?? 256);

    [ThreadStatic]
    private static CoverageRasterizer? _glyphRasterizer;

    // Row parallelism requested by the render in progress on this thread. Large paints whose
    // rows are independent split into row ranges; every row computes the same pixels as the
    // sequential loop, so output does not depend on the thread count.
    [ThreadStatic]
    private static int _rowParallelism;

    [ThreadStatic]
    private static int _jpeg2000PaintParallelism;

    private const long ParallelPaintThreshold = 262_144;

    /// <summary>Runs a row loop sequentially or across row ranges when large enough.</summary>
    internal static void ForEachRow(int top, int bottom, long pixelCount,
        Action<int, int> body, int? maximumParallelism, CancellationToken cancellationToken)
    {
        int parallelism = maximumParallelism ?? _rowParallelism;
        if (parallelism <= 1 || pixelCount < ParallelPaintThreshold || bottom - top < 2)
        {
            body(top, bottom);
            return;
        }
        int rows = bottom - top;
        int chunks = Math.Min(parallelism * 4, rows);
        int chunkRows = (rows + chunks - 1) / chunks;
        var failures = new System.Collections.Concurrent.ConcurrentBag<(int Chunk, Exception Error)>();
        try
        {
            Parallel.For(0, chunks, new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = cancellationToken
            }, chunk =>
            {
                int start = top + chunk * chunkRows;
                int end = Math.Min(bottom, start + chunkRows);
                try { if (start < end) body(start, end); }
                catch (Exception error) { failures.Add((chunk, error)); throw; }
            });
        }
        catch (AggregateException)
        {
            // Rows fail the way the serial path does: cancellation stays cancellation, and otherwise the first
            // failing row range's exception surfaces when it is an expected type, which callers fall back on,
            // whatever later row ranges threw. A first failure of any other type stays an aggregate, so an
            // engine bug is not hidden behind a later range's refusal.
            Exception[] ordered = [.. failures.OrderBy(failure => failure.Chunk).Select(failure => failure.Error)];
            Exception? chosen = ordered.FirstOrDefault(error => error is OperationCanceledException)
                ?? ordered.Take(1).FirstOrDefault(error => error is NotSupportedException or InvalidDataException or ArgumentException);
            if (chosen is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(chosen).Throw();
            throw;
        }
    }

    /// <summary>Lets tests compare cached glyph fills against the direct fill path.</summary>
    internal bool UseGlyphMaskCache { get; set; } = true;

    /// <summary>Lets tests compare translated integer vertices against scalar glyph rasterization.</summary>
    internal bool UseTransformedGlyphPoints { get; set; } = true;

    internal int GlyphMaskCacheCount => _glyphMaskCache.Count;
    internal int GlyphGeometryCacheCount => _glyphGeometryCache.Count;
    internal long GlyphGeometryCacheBytes => _glyphGeometryCache.Weight;

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
        // Whole-pixel and quarter-pixel positions remain exact; other origins move at most
        // one eighth of a pixel, so repeated glyphs share a small set of cached phases.
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
        Dictionary<GlyphMaskKey, GlyphMask?>? pageMasks =
            ReferenceEquals(_pageGlyphMaskOwner, this) ? _pageGlyphMasks : null;
        if (pageMasks is null || !pageMasks.TryGetValue(key, out GlyphMask? glyph))
        {
            glyph = _glyphMaskCache.GetOrAdd(key, RasterizeGlyphMask);
            if (pageMasks is not null && pageMasks.Count < MaximumPageGlyphMasks)
                pageMasks[key] = glyph;
        }
        if (glyph is null) return null;
        if (glyph.Mask.IsEmpty) return CoverageMask.Empty;
        CoverageMask placed = glyph.Mask.Translate((int)pixelX, (int)pixelY);
        // A glyph inside the page needs no clipping to the page rectangle.
        if (placed.Left >= 0 && placed.Top >= 0
            && placed.Right <= frame.Width && placed.Bottom <= frame.Height)
            return placed;
        return CoverageMask.Intersect(
            CoverageMask.Rectangle(0, 0, frame.Width, frame.Height), placed);
    }

    // Glyphs repeat many times on one page. A map owned by the page render answers those repeats
    // without the shared cache's lock and usage bookkeeping, and is emptied when the render ends.
    private const int MaximumPageGlyphMasks = 4096;

    [ThreadStatic]
    private static Dictionary<GlyphMaskKey, GlyphMask?>? _pageGlyphMasks;

    [ThreadStatic]
    private static PdfPageRenderer? _pageGlyphMaskOwner;

    /// <summary>Starts the page glyph map for this thread unless an outer render already owns it.</summary>
    private PageGlyphMaskScope EnterPageGlyphMasks()
    {
        if (_pageGlyphMaskOwner is not null) return default;
        _pageGlyphMaskOwner = this;
        _pageGlyphMasks ??= new Dictionary<GlyphMaskKey, GlyphMask?>(GlyphMaskKeyComparer.Instance);
        return new PageGlyphMaskScope(true);
    }

    private readonly struct PageGlyphMaskScope(bool owner) : IDisposable
    {
        public void Dispose()
        {
            if (!owner) return;
            _pageGlyphMaskOwner = null;
            _pageGlyphMasks?.Clear();
        }
    }

    /// <summary>Rasterizes one glyph relative to its origin pixel, or returns null when too large.</summary>
    private GlyphMask? RasterizeGlyphMask(GlyphMaskKey key)
    {
        double offsetX = (double)key.SubX / GlyphSubpixelSteps;
        double offsetY = (double)key.SubY / GlyphSubpixelSteps;
        // Fractional origins change coverage, but not the transformed outline. Share
        // that setup across phases without increasing the existing mask budget.
        GlyphGeometry? geometry = _glyphGeometryCache.GetOrAdd(
            key with { SubX = 0, SubY = 0 }, TransformGlyphGeometry);
        if (geometry is null) return null;
        double minX = geometry.MinX + offsetX, minY = geometry.MinY + offsetY;
        double maxX = geometry.MaxX + offsetX, maxY = geometry.MaxY + offsetY;
        if (double.IsPositiveInfinity(minX)) return EmptyGlyphMask;
        if (minX < int.MinValue / 2 || minY < int.MinValue / 2
            || maxX > int.MaxValue / 2 || maxY > int.MaxValue / 2) return null;
        // One pixel of margin on every side keeps antialiased edges inside the local raster.
        int shiftX = (int)Math.Floor(minX) - 1, shiftY = (int)Math.Floor(minY) - 1;
        long width = (long)Math.Ceiling(maxX) + 2 - shiftX;
        long height = (long)Math.Ceiling(maxY) + 2 - shiftY;
        if (width <= 0 || height <= 0 || width * height > MaximumCachedGlyphPixels) return null;
        CoverageRasterizer rasterizer = _glyphRasterizer ??= new CoverageRasterizer((int)width, (int)height);
        rasterizer.Reset((int)width, (int)height);
        if (UseTransformedGlyphPoints && geometry.Paths is { } paths)
            rasterizer.AddDeviceGlyphPolygons(paths,
                key.SubX * (CoverageRasterizer.Scale / GlyphSubpixelSteps)
                    + (geometry.ShiftX - shiftX) * CoverageRasterizer.Scale,
                key.SubY * (CoverageRasterizer.Scale / GlyphSubpixelSteps)
                    + (geometry.ShiftY - shiftY) * CoverageRasterizer.Scale);
        else
            rasterizer.AddGlyphPolygons(
                _glyphPathCache.GetOrAdd(key.Outline, FlattenGlyphOutlineCore),
                BitConverter.Int64BitsToDouble(key.A), BitConverter.Int64BitsToDouble(key.B),
                BitConverter.Int64BitsToDouble(key.C), BitConverter.Int64BitsToDouble(key.D),
                offsetX, offsetY, shiftX, shiftY);
        CoverageMask local = rasterizer.Sweep(evenOdd: false);
        return local.IsEmpty ? EmptyGlyphMask : new GlyphMask(local.Translate(shiftX, shiftY));
    }

    private GlyphGeometry? TransformGlyphGeometry(GlyphMaskKey key)
    {
        double a = BitConverter.Int64BitsToDouble(key.A), b = BitConverter.Int64BitsToDouble(key.B);
        double c = BitConverter.Int64BitsToDouble(key.C), d = BitConverter.Int64BitsToDouble(key.D);
        IReadOnlyList<Point[]> source = _glyphPathCache.GetOrAdd(key.Outline, FlattenGlyphOutlineCore);
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        long bytes = 256;
        foreach (Point[] sourcePath in source)
        {
            bytes += 40 + sourcePath.LongLength * 8;
            if (sourcePath.Length < 3) continue;
            for (int index = 0; index < sourcePath.Length; index++)
            {
                Point point = sourcePath[index];
                double x = point.X * a + point.Y * c;
                double y = point.X * b + point.Y * d;
                if (!double.IsFinite(x) || !double.IsFinite(y)) return null;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        // Oversized glyphs need only bounds; do not duplicate their point arrays.
        if (bytes > MaximumGlyphGeometryCacheBytes || double.IsPositiveInfinity(minX)
            || minX < int.MinValue / 2 || minY < int.MinValue / 2
            || maxX > int.MaxValue / 2 || maxY > int.MaxValue / 2
            || (Math.Floor(maxX - minX) + 2) * (Math.Floor(maxY - minY) + 2)
                > MaximumCachedGlyphPixels)
            return new(null, minX, minY, maxX, maxY, 0, 0, 256);
        int shiftX = (int)Math.Floor(minX) - 1, shiftY = (int)Math.Floor(minY) - 1;
        // A phase is four fixed-point units. Away from rounding boundaries its even
        // translation preserves midpoint-to-even rounding without repeated vertex setup.
        var paths = new GlyphPoint[source.Count][];
        for (int path = 0; path < source.Count; path++)
        {
            Point[] points = source[path];
            var transformed = new GlyphPoint[points.Length];
            for (int index = 0; index < points.Length; index++)
            {
                Point point = points[index];
                double x = point.X * a + point.Y * c, y = point.X * b + point.Y * d;
                double fixedX = (x - shiftX) * CoverageRasterizer.Scale;
                double fixedY = (y - shiftY) * CoverageRasterizer.Scale;
                // Adding a phase can round a floating-point value onto an exact half.
                // Keep the scalar operation order for these outlines, including large
                // coordinates whose representation has a wider spacing between values.
                double toleranceX = Math.Max(1e-8,
                    (Math.BitIncrement(x) - x) * CoverageRasterizer.Scale * 4);
                double toleranceY = Math.Max(1e-8,
                    (Math.BitIncrement(y) - y) * CoverageRasterizer.Scale * 4);
                if (Math.Abs(fixedX - Math.Floor(fixedX) - 0.5) <= toleranceX
                    || Math.Abs(fixedY - Math.Floor(fixedY) - 0.5) <= toleranceY)
                    return new(null, minX, minY, maxX, maxY, 0, 0, 256);
                transformed[index] = new((int)Math.Round(fixedX), (int)Math.Round(fixedY));
            }
            paths[path] = transformed;
        }
        return new(paths, minX, minY, maxX, maxY, shiftX, shiftY, bytes);
    }

    /// <summary>Rasterizes page-space paths into a coverage mask.</summary>
    private static CoverageMask RasterizeFill(IReadOnlyList<List<Point>> paths, bool evenOdd,
        RasterFrame frame, bool rent = false,
        (int Left, int Top, int Right, int Bottom)? bounds = null)
    {
        List<Point>? single = null;
        int count = 0;
        foreach (List<Point> path in paths)
            if (path.Count >= 3)
            {
                single = path;
                count++;
            }
        if (count == 1 && single!.Count is 4 or 5
            && CoverageRasterizer.TryRectangle(frame.ToPixels(single), frame.Width, frame.Height) is { } rectangle)
            return rectangle;
        CoverageRasterizer? rasterizer = _sharedRasterizer;
        if (rasterizer is null || rasterizer.Width != frame.Width || rasterizer.Height != frame.Height)
            _sharedRasterizer = rasterizer = new CoverageRasterizer(frame.Width, frame.Height);
        rasterizer.Reset();
        rasterizer.AddPagePolygons(paths, frame);
        return rasterizer.Sweep(evenOdd, rent, bounds);
    }

    private static CoverageMask RasterizeFill(IReadOnlyList<Point[]> polygons, bool evenOdd,
        RasterFrame frame) =>
        RasterizePolygons(frame.ToPixels(polygons, 3), evenOdd, frame.Width, frame.Height);

    /// <summary>
    /// Moves every edge of a nonzero-filled glyph outline away from its filled side by
    /// <paramref name="radius"/>, giving the glyph's fill and its centered stroke as one region.
    /// Painted once, the inner half of the stroke is not counted a second time on pixels it
    /// shares with the fill. Convex corners take a miter within <paramref name="miterLimit"/>
    /// and a bevel beyond it; returns null when the outline has no usable contour.
    /// </summary>
    private static List<List<Point>>? WidenGlyphOutline(IReadOnlyList<List<Point>> contours,
        double radius, double miterLimit)
    {
        var closed = new List<List<Point>>(contours.Count);
        double area = 0;
        foreach (List<Point> contour in contours)
        {
            var points = new List<Point>(contour.Count);
            foreach (Point point in contour)
            {
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return null;
                if (points.Count == 0 || Distance(points[^1], point) > radius * 1e-3) points.Add(point);
            }
            while (points.Count > 1 && Distance(points[0], points[^1]) <= radius * 1e-3) points.RemoveAt(points.Count - 1);
            if (points.Count < 3) continue;
            for (int index = 0; index < points.Count; index++)
            {
                Point a = points[index], b = points[(index + 1) % points.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            closed.Add(points);
        }
        if (closed.Count == 0 || area == 0) return null;
        // The filled side of every edge is the same side for the whole glyph, given by the sign
        // of its total area: outer contours outweigh their holes, which run the other way.
        double side = area > 0 ? 1 : -1;
        var widened = new List<List<Point>>(closed.Count);
        foreach (List<Point> points in closed)
        {
            int count = points.Count;
            var result = new List<Point>(count + 8);
            for (int index = 0; index < count; index++)
            {
                Point previous = points[(index + count - 1) % count], current = points[index],
                    next = points[(index + 1) % count];
                (double inX, double inY) = Unit(current.X - previous.X, current.Y - previous.Y);
                (double outX, double outY) = Unit(next.X - current.X, next.Y - current.Y);
                // Normals that point away from the filled side.
                double n1x = inY * side, n1y = -inX * side, n2x = outY * side, n2y = -outX * side;
                double mx = n1x + n2x, my = n1y + n2y, length = Math.Sqrt(mx * mx + my * my);
                double cosine = length / 2;
                bool convex = (inX * outY - inY * outX) * side > 0;
                if (length > 1e-9 && 1 / cosine <= (convex ? miterLimit : 4))
                {
                    double distance = radius / cosine;
                    result.Add(new Point(current.X + mx / length * distance, current.Y + my / length * distance));
                }
                else
                {
                    result.Add(new Point(current.X + n1x * radius, current.Y + n1y * radius));
                    result.Add(new Point(current.X + n2x * radius, current.Y + n2y * radius));
                }
            }
            widened.Add(result);
        }
        return widened;

        static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        static (double X, double Y) Unit(double x, double y)
        {
            double length = Math.Sqrt(x * x + y * y);
            return (x / length, y / length);
        }
    }

    /// <summary>Converts a page-space stroke into pixel-space outline polygons and rasterizes them.</summary>
    private static CoverageMask RasterizeStroke(IReadOnlyList<List<Point>> paths,
        double pageLineWidth, RendererLineCap lineCap, RendererLineJoin lineJoin,
        double miterLimit, RasterFrame frame, bool rent = false,
        (int Left, int Top, int Right, int Bottom)? bounds = null, bool strokeAdjust = false,
        Matrix? userTransform = null, bool strokeAddsWeight = false)
    {
        // A lone stroke keeps a one-pixel minimum so thin lines stay visible. A stroke over its
        // own fill (text mode 2, synthetic bold) only adds weight, and a floor or a snap to
        // whole pixels would make that weight grow as the page zooms out. A zero width still
        // means the thinnest line the device can draw.
        double radius = pageLineWidth * frame.StrokeScale / 2;
        if (strokeAddsWeight) strokeAdjust = false;
        if (!strokeAddsWeight || !(radius > 0)) radius = Math.Max(radius, 0.5);
        var polygons = new List<Point[]>();
        UserSpacePen? pen = userTransform is Matrix user && !strokeAdjust
            ? UserSpacePen.Create(user, frame, pageLineWidth) : null;
        foreach (List<Point> path in paths)
        {
            if (path.Count == 0) continue;
            Point[] pixels = frame.ToPixels(path);
            if (pen is UserSpacePen userPen && path is not ZeroLengthDash)
            {
                userPen.Stroke(pixels, lineCap, lineJoin, miterLimit, polygons);
                continue;
            }
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
            double pathRadius = radius;
            if (strokeAdjust) TryAdjustOrthogonalStroke(pixels, pageLineWidth * frame.StrokeScale, ref pathRadius);
            StrokeOutline(pixels, pathRadius, lineCap, lineJoin, miterLimit, polygons);
        }
        return RasterizePolygons(polygons, false, frame.Width, frame.Height, rent, bounds);
    }

    // Line width is a user-space length. Under a transform that scales x and y differently the
    // pen is an ellipse and caps stay perpendicular in user space, so such strokes are outlined
    // in a scaled copy of user space and mapped back to pixels.
    private readonly struct UserSpacePen
    {
        private readonly double _m11, _m12, _m21, _m22, _i11, _i12, _i21, _i22, _radius;

        private UserSpacePen(double m11, double m12, double m21, double m22, double scale, double radius)
        {
            double determinant = m11 * m22 - m12 * m21;
            // Forward maps the scaled user space to pixels; inverse maps pixels back.
            (_m11, _m12, _m21, _m22) = (m11 / scale, m12 / scale, m21 / scale, m22 / scale);
            (_i11, _i12, _i21, _i22) = (m22 / determinant * scale, -m12 / determinant * scale,
                -m21 / determinant * scale, m11 / determinant * scale);
            _radius = radius;
        }

        internal static UserSpacePen? Create(Matrix user, RasterFrame frame, double pageLineWidth)
        {
            double userScale = user.StrokeScale;
            if (!(userScale > 0) || !(pageLineWidth > 0)) return null;
            double m11 = frame.ScaleX * user.A, m12 = frame.ScaleX * user.C;
            double m21 = -frame.ScaleY * user.B, m22 = -frame.ScaleY * user.D;
            double determinant = m11 * m22 - m12 * m21;
            if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-12) return null;
            double sum = m11 * m11 + m12 * m12 + m21 * m21 + m22 * m22;
            double spread = Math.Sqrt(Math.Max(0, sum * sum - 4 * determinant * determinant));
            double largest = Math.Sqrt((sum + spread) / 2), smallest = Math.Sqrt(Math.Max(0, (sum - spread) / 2));
            double userRadius = pageLineWidth / userScale / 2;
            // Near-uniform scaling and strokes thinner than a pixel keep the device-space path.
            if (!(smallest > 0) || largest / smallest < 1.02 || userRadius * smallest < 0.5) return null;
            return new UserSpacePen(m11, m12, m21, m22, largest, userRadius * largest);
        }

        internal void Stroke(Point[] pixels, RendererLineCap lineCap, RendererLineJoin lineJoin,
            double miterLimit, List<Point[]> output)
        {
            var user = new Point[pixels.Length];
            for (int index = 0; index < pixels.Length; index++)
                user[index] = new Point(_i11 * pixels[index].X + _i12 * pixels[index].Y,
                    _i21 * pixels[index].X + _i22 * pixels[index].Y);
            var outline = new List<Point[]>();
            StrokeOutline(user, _radius, lineCap, lineJoin, miterLimit, outline);
            foreach (Point[] polygon in outline)
            {
                for (int index = 0; index < polygon.Length; index++)
                    polygon[index] = new Point(_m11 * polygon[index].X + _m12 * polygon[index].Y,
                        _m21 * polygon[index].X + _m22 * polygon[index].Y);
                output.Add(Oriented(polygon));
            }
        }
    }

    // Snap only the normal coordinates of orthogonal segments. Tangential
    // endpoints keep their length, and mixed or curved paths keep their geometry.
    private static void TryAdjustOrthogonalStroke(Point[] points, double width, ref double radius)
    {
        if (points.Length < 2 || !double.IsFinite(width)) return;
        bool anySegment = false;
        for (int index = 1; index < points.Length; index++)
        {
            Point from = points[index - 1], to = points[index];
            if (!double.IsFinite(from.X) || !double.IsFinite(from.Y)
                || !double.IsFinite(to.X) || !double.IsFinite(to.Y)) return;
            double dx = to.X - from.X, dy = to.Y - from.Y;
            if (dx != 0 && dy != 0) return;
            anySegment |= dx != 0 || dy != 0;
        }
        if (!anySegment) return;
        Span<byte> coordinates = points.Length <= 256 ? stackalloc byte[points.Length] : new byte[points.Length];
        coordinates.Clear();
        for (int index = 1; index < points.Length; index++)
        {
            byte flag = points[index].X != points[index - 1].X ? (byte)2
                : points[index].Y != points[index - 1].Y ? (byte)1 : (byte)0;
            coordinates[index - 1] |= flag;
            coordinates[index] |= flag;
        }
        // Repeated vertices are one vertex and share every adjacent normal constraint, so they
        // snap to one point; a closed path's last vertex is its first. A line back to the start
        // followed by closepath repeats the start, and snapping its copies separately parted
        // them into a short diagonal whose miter spiked.
        bool closedPath = points[0] == points[^1];
        for (int pass = 0; pass < 2; pass++)
        {
            for (int index = 1; index < points.Length; index++)
                if (points[index] == points[index - 1])
                    coordinates[index] = coordinates[index - 1] = (byte)(coordinates[index] | coordinates[index - 1]);
            for (int index = points.Length - 2; index >= 0; index--)
                if (points[index] == points[index + 1])
                    coordinates[index] = coordinates[index + 1] = (byte)(coordinates[index] | coordinates[index + 1]);
            if (closedPath) coordinates[0] = coordinates[^1] = (byte)(coordinates[0] | coordinates[^1]);
        }
        double pixels = Math.Max(1, Math.Floor(width + 0.5));
        double offset = pixels % 2 == 1 ? 0.5 : 0;
        for (int index = 1; index < points.Length; index++)
            if (points[index] != points[index - 1]
                && Adjusted(points[index], coordinates[index]) == Adjusted(points[index - 1], coordinates[index - 1]))
                return;
        for (int index = 0; index < points.Length; index++)
            points[index] = Adjusted(points[index], coordinates[index]);
        radius = pixels / 2;

        Point Adjusted(Point point, byte coordinate) => new(
            (coordinate & 1) != 0 ? Math.Floor(point.X + 0.5 - offset) + offset : point.X,
            (coordinate & 2) != 0 ? Math.Floor(point.Y + 0.5 - offset) + offset : point.Y);
    }

    /// <summary>Emits consistently oriented outline polygons for one stroked polyline.</summary>
    private static void StrokeOutline(Point[] input, double radius, RendererLineCap lineCap,
        RendererLineJoin lineJoin, double miterLimit, List<Point[]> output)
    {
        // A segment shorter than a thousandth of a pixel has no visible length and no reliable
        // direction; joined, it drew a miter spike, as when a line back to the start ends a
        // hair away from it before closepath. Such points merge with their neighbor.
        const double minimumSegment = 1e-3;
        var points = new List<Point>(input.Length);
        foreach (Point point in input)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) continue;
            if (points.Count == 0 || Distance(points[^1], point) > minimumSegment) points.Add(point);
        }
        bool closed = points.Count > 2 && Distance(points[0], points[^1]) <= minimumSegment;
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

    /// <summary>
    /// Fills the combined coverage of every active clip for pixels left to right of one row,
    /// with the same per-pixel arithmetic as <see cref="ClipCoverage"/>.
    /// </summary>
    private static void FillClipRow(IReadOnlyList<ClipRegion> clips, int y, int left, int right,
        byte[] buffer)
    {
        Span<byte> row = buffer.AsSpan(0, right - left);
        row.Fill(255);
        for (int index = 0; index < clips.Count; index++)
        {
            CoverageMask mask = clips[index].Mask;
            if (y < mask.Top || y >= mask.Bottom || mask.Right <= left || mask.Left >= right)
            {
                row.Clear();
                return;
            }
            int start = Math.Max(left, mask.Left), end = Math.Min(right, mask.Right);
            row[..(start - left)].Clear();
            row[(end - left)..].Clear();
            if (mask.Coverage is null) continue;
            ReadOnlySpan<byte> source = mask.Coverage.AsSpan(mask.RowOffset(y) + start - mask.Left, end - start);
            Span<byte> target = row.Slice(start - left, end - start);
            for (int i = 0; i < target.Length; i++)
            {
                int coverage = target[i], clip = source[i];
                target[i] = (byte)(clip == 255 ? coverage : coverage == 255 ? clip : (coverage * clip + 127) / 255);
            }
        }
    }

    /// <summary>Returns the combined clip coverage at one pixel as an opacity factor.</summary>
    private static double ClipAlpha(IReadOnlyList<ClipRegion> clips, int x, int y) =>
        clips.Count == 0 ? 1 : ClipCoverage(clips, x, y) / 255d;

    /// <summary>Paints one color through a coverage mask, honoring clips, masks, and blending.</summary>
    private static void PaintCoverage(RasterSurface pixels, int width, CoverageMask mask,
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
        // Indexing avoids an enumerator allocation for every glyph paint.
        for (int index = 0; index < clips.Count; index++)
        {
            ClipRegion clip = clips[index];
            left = Math.Max(left, clip.Mask.Left);
            top = Math.Max(top, clip.Mask.Top);
            right = Math.Min(right, clip.Mask.Right);
            bottom = Math.Min(bottom, clip.Mask.Bottom);
            if (clip.Mask.Coverage is not null) rectangularClips = false;
        }
        if (right <= left || bottom <= top) return;
        graphicsSoftMask = graphicsSoftMask?.ForBounds(left, top, right, bottom);
        bool perPixelClip = clips.Count > 0 && !rectangularClips;
        // Applying an identical geometric clip is idempotent. Multiplying two separately
        // rasterized copies would square their antialiased edge coverage and lighten it.
        if (perPixelClip && clips.Count(clip => !clip.StorageBounds) == 1
            && mask.HasSameCoverage(clips.First(clip => !clip.StorageBounds).Mask))
            perPixelClip = false;
        if (pixels.PreciseRgb is not null)
        {
            PaintPreciseCoverage(pixels, width, mask, color, alpha, blendMode, clips,
                graphicsSoftMask, knockout, left, top, right, bottom, perPixelClip, cancellationToken);
            return;
        }
        // Group alpha is maintained alongside the direct RGB paths with the compositor's own
        // formula, so tracked groups take the same fast paths as plain pages.
        bool simpleBlend = pixels.Ink is null && pixels.RgbProfile is null && graphicsSoftMask is null && knockout is null
            && pixels.GroupShape is null
            && blendMode is RendererBlendMode.Normal or RendererBlendMode.Compatible;
        byte[]? groupAlpha = pixels.GroupAlpha;
        bool directProfiled = pixels.RgbProfile is not null && pixels.Ink is null
            && graphicsSoftMask is null && knockout is null && pixels.GroupShape is null
            && blendMode is RendererBlendMode.Normal or RendererBlendMode.Compatible;
        bool rgbShadowSpot = pixels.HasRgbSpotShadow && color.SpotName is not null;
        bool direct = alpha >= 1 && (simpleBlend || directProfiled) && !rgbShadowSpot
            && (!pixels.HasRgbSpotShadow || (color.OverprintComponents & ~15) == 0);
        bool directInk = pixels.Ink is not null && alpha >= 1
            && (color.OverprintComponents & 16) == 0
            && color.SpotName is null && !pixels.HasSpotPlates
            && graphicsSoftMask is null && knockout is null
            && pixels.GroupShape is null
            && blendMode is RendererBlendMode.Normal or RendererBlendMode.Compatible;
        bool directSpot = (pixels.Ink is not null || pixels.HasRgbSpotShadow)
            && color.SpotName is not null && alpha >= 1
            && graphicsSoftMask is null && knockout is null && pixels.GroupShape is null
            && blendMode is RendererBlendMode.Normal or RendererBlendMode.Compatible;
        uint ink = pixels.Ink is not null || pixels.HasRgbSpotShadow ? pixels.GetInk(color) : 0;
        // On ink surfaces the compositor resolves the paint's ink through GetInk for every
        // pixel; a color that already carries this surface's ink resolves to the same value
        // without the per-pixel color comparison.
        Color paint = pixels.Ink is not null
            ? color with { Ink = ink, InkProfile = pixels.InkProfile } : color;
        byte[]? opaqueBlend = simpleBlend && alpha > 0 && alpha < 1
            && (long)(right - left) * (bottom - top) >= 4096
            ? CreateOpaqueBlendLookup(color, alpha * 255 / 255d, blendMode) : null;
        byte[]? coverage = mask.Coverage;
        if (directSpot)
        {
            PaintSpotCoverageRows(pixels, width, mask, paint, alpha, blendMode,
                alphaIsShape, left, top, right, bottom, coverage, ink, groupAlpha,
                clips, perPixelClip, cancellationToken);
            return;
        }
        if (directInk && !perPixelClip)
        {
            // Opaque interiors share bulk ink and alpha writes; partial edges keep the compositor.
            byte[] inkData = pixels.Ink!;
            if (coverage is null && left == pixels.Left && top == pixels.Top
                && right == pixels.Right && bottom == pixels.Bottom)
                pixels.SetAlphaRun(0, pixels.Length / 4, 255);
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
        if (direct && !directProfiled && !perPixelClip)
        {
            PaintDirectCoverageRows(pixels, width, mask, color, alpha, blendMode,
                graphicsSoftMask, knockout, left, top, right,
                bottom, groupAlpha, coverage,
                pixels.HasRgbSpotShadow ? ink : null, cancellationToken);
            return;
        }
        // Row chunks write disjoint pixels, disjoint group alpha entries and disjoint knockout
        // object slots, and the graphics soft mask was materialized by ForBounds above, so this
        // loop parallelizes by row on plain RGB surfaces. Ink and profiled RGB surfaces stay
        // serial: their per-pixel GetInk and GetRgb calls memoize into surface state.
        // A local function cannot capture the 'in' parameter, so the direct path reads a copy.
        Color directColor = directProfiled ? pixels.GetRgb(color) : color;
        if (pixels.Ink is null && pixels.RgbProfile is null)
        {
            PaintCoverageRowsParallel(pixels, width, mask, directColor, paint, alpha,
                blendMode, clips, graphicsSoftMask, knockout,
                alphaIsShape, left, top, right, bottom, perPixelClip, directInk,
                ink, direct, groupAlpha, coverage, opaqueBlend, cancellationToken);
        }
        else PaintCoverageRows(pixels, width, mask, directColor, paint, alpha,
            blendMode, clips, graphicsSoftMask, knockout,
            alphaIsShape, left, top, right, bottom, perPixelClip, directInk,
            ink, direct, groupAlpha, coverage, opaqueBlend, cancellationToken);
        return;
    }

    private static void PaintSpotCoverageRows(RasterSurface pixels, int width,
        CoverageMask mask, Color paint, double alpha, RendererBlendMode blendMode,
        bool alphaIsShape, int left, int top, int right, int bottom,
        byte[]? coverage, uint ink, byte[]? groupAlpha,
        IReadOnlyList<ClipRegion> clips, bool perPixelClip, CancellationToken cancellationToken)
    {
        RasterSurface.PreparedSpotPaint? prepared = null;
        RasterSurface.PreparedSpotPaint? additional = null;
        bool overprint = (paint.OverprintComponents & 16) != 0;
        for (int y = top; y < bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int maskRow = coverage is null ? 0 : mask.RowOffset(y) - mask.Left;
            for (int x = left; x < right;)
            {
                int cover = coverage is null ? 255 : coverage[maskRow + x];
                if (perPixelClip && cover != 0)
                    cover = (cover * ClipCoverage(clips, x, y) + 127) / 255;
                int offset = pixels.Offset(x, y);
                if (cover == 255 && pixels.Alpha(offset) == 255 && pixels.CanPaintSpot(offset))
                {
                    int start = x++;
                    while (x < right && (coverage is null || coverage[maskRow + x] == 255)
                        && (!perPixelClip || ClipCoverage(clips, x, y) == 255)
                        && pixels.Alpha(pixels.Offset(x, y)) == 255
                        && pixels.CanPaintSpot(pixels.Offset(x, y))) x++;
                    int count = x - start;
                    pixels.PaintSpotRun(offset, count, paint, overprint, ink);
                    groupAlpha?.AsSpan(offset / 4, count).Fill(255);
                    continue;
                }
                if (cover != 0)
                {
                    if (pixels.Alpha(offset) == 255 && pixels.CanPaintSpot(offset))
                    {
                        if (prepared is null)
                        {
                            prepared = pixels.PrepareSpotPaint(paint.SpotName!);
                            if (paint.AdditionalSpot is { } second)
                                additional = pixels.PrepareSpotPaint(second.Name);
                        }
                        pixels.PaintSpotCoveragePrepared(offset, paint, ink, (byte)cover,
                            overprint, prepared.Plate, additional?.Plate);
                        groupAlpha?[offset / 4] = (byte)Math.Round(cover
                                + groupAlpha[offset / 4] * (1 - cover / 255d));
                    }
                    else
                        SetPixel(pixels, width, x, y, paint, alpha * cover / 255d,
                            blendMode, shape: cover / 255d, alphaIsShape: alphaIsShape,
                            resolvedInk: ink);
                }
                x++;
            }
        }
    }

    private static void PaintDirectCoverageRows(RasterSurface pixels, int width,
        CoverageMask mask, Color fillColor, double alpha, RendererBlendMode blendMode,
        GraphicsSoftMask? graphicsSoftMask, KnockoutState? knockout,
        int left, int top, int right, int bottom,
        byte[]? groupAlpha, byte[]? coverage, uint? rgbShadowInk,
        CancellationToken cancellationToken)
    {
        // Opaque normal-blend fills on a plain RGB surface: full rows of a rectangular
        // mask are one span fill, and antialiased edges blend against opaque pixels in
        // place. Pixels with a transparent destination still use the compositor.
        // Small paints such as glyphs run inline, so they allocate no row closure; the
        // condition matches ForEachRow's sequential case.
        if (_rowParallelism <= 1 || (long)(right - left) * (bottom - top) < ParallelPaintThreshold
            || bottom - top < 2)
        {
            PaintDirectCoverageRowRange(pixels, width, mask, fillColor, alpha, blendMode,
                graphicsSoftMask, knockout, left, right, groupAlpha, coverage, rgbShadowInk,
                top, bottom, cancellationToken);
            return;
        }
        PaintDirectCoverageRowsParallel(pixels, width, mask, fillColor, alpha, blendMode,
            graphicsSoftMask, knockout, left, top, right, bottom, groupAlpha, coverage,
            rgbShadowInk, cancellationToken);
    }

    private static void PaintDirectCoverageRowsParallel(RasterSurface pixels, int width,
        CoverageMask mask, Color fillColor, double alpha, RendererBlendMode blendMode,
        GraphicsSoftMask? graphicsSoftMask, KnockoutState? knockout,
        int left, int top, int right, int bottom,
        byte[]? groupAlpha, byte[]? coverage, uint? rgbShadowInk,
        CancellationToken cancellationToken) =>
        ForEachRow(top, bottom, (long)(right - left) * (bottom - top),
            (rowStart, rowEnd) => PaintDirectCoverageRowRange(pixels, width, mask, fillColor,
                alpha, blendMode, graphicsSoftMask, knockout, left, right, groupAlpha,
                coverage, rgbShadowInk, rowStart, rowEnd, cancellationToken),
            null, cancellationToken);

    private static void PaintDirectCoverageRowRange(RasterSurface pixels, int width,
        CoverageMask mask, in Color fillColor, double alpha, RendererBlendMode blendMode,
        GraphicsSoftMask? graphicsSoftMask, KnockoutState? knockout, int left, int right,
        byte[]? groupAlpha, byte[]? coverage, uint? rgbShadowInk,
        int rowStart, int rowEnd, CancellationToken cancellationToken)
    {
        byte[] data = pixels.Data;
        uint packed = fillColor.Blue | (uint)fillColor.Green << 8
            | (uint)fillColor.Red << 16 | 0xFF000000u;
        if (coverage is not null && groupAlpha is null && rgbShadowInk is null
            && !pixels.HasRgbSpotShadow)
        {
            // Without group alpha or spot shadows, each pixel is a write or an in-place
            // blend with the same arithmetic as the general loop below.
            int blue = fillColor.Blue, green = fillColor.Green, red = fillColor.Red;
            int count = right - left;
            for (int y = rowStart; y < rowEnd; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<byte> cover = coverage.AsSpan(mask.RowOffset(y) + left - mask.Left, count);
                Span<byte> row = data.AsSpan(pixels.Offset(left, y), count * 4);
                for (int index = 0; index < cover.Length; index++)
                {
                    int amount = cover[index];
                    if (amount == 0) continue;
                    int offset = index * 4;
                    if (amount == 255)
                    {
                        Unsafe.WriteUnaligned(ref row[offset], packed);
                        continue;
                    }
                    if (row[offset + 3] == 255)
                    {
                        int inverse = 255 - amount;
                        row[offset] = (byte)((blue * amount + row[offset] * inverse + 127) / 255);
                        row[offset + 1] = (byte)((green * amount + row[offset + 1] * inverse + 127) / 255);
                        row[offset + 2] = (byte)((red * amount + row[offset + 2] * inverse + 127) / 255);
                        continue;
                    }
                    SetPixel(pixels, width, left + index, y, fillColor, alpha * amount / 255d,
                        blendMode, graphicsSoftMask, knockout);
                }
            }
            return;
        }
        for (int y = rowStart; y < rowEnd; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int rowOffset = pixels.Offset(left, y);
            if (coverage is null)
            {
                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
                    data.AsSpan(rowOffset, (right - left) * 4)).Fill(packed);
                if (rgbShadowInk is uint rowInk)
                    pixels.TrackRgbProcessRun(rowOffset, right - left, rowInk, fillColor);
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
                    if (rgbShadowInk is uint pixelInk)
                        pixels.TrackRgbProcessRun(offset, 1, pixelInk, fillColor);
                    groupAlpha?[offset / 4] = 255;
                    continue;
                }
                if (data[offset + 3] == 255)
                {
                    if (pixels.HasRgbSpotShadow && fillColor.Ink is not null && groupAlpha is null
                        && pixels.TryBlendRgbProcessKnockout(offset, fillColor,
                            rgbShadowInk, 1, cover))
                        continue;
                    pixels.InvalidateRgbSpotPixel(offset);
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
    }

    private static void PaintCoverageRowsParallel(RasterSurface pixels, int width,
        CoverageMask mask, Color directColor, Color paint, double alpha,
        RendererBlendMode blendMode, IReadOnlyList<ClipRegion> clips,
        GraphicsSoftMask? graphicsSoftMask, KnockoutState? knockout,
        bool alphaIsShape, int left, int top,
        int right, int bottom, bool perPixelClip, bool directInk, uint ink,
        bool direct, byte[]? groupAlpha, byte[]? coverage, byte[]? opaqueBlend,
        CancellationToken cancellationToken)
    {
        ForEachRow(top, bottom, (long)(right - left) * (bottom - top),
            (rowStart, rowEnd) => PaintCoverageRows(pixels, width, mask, directColor,
                paint, alpha, blendMode, clips, graphicsSoftMask, knockout,
                alphaIsShape, left, rowStart, right, rowEnd,
                perPixelClip, directInk, ink, direct, groupAlpha, coverage, opaqueBlend,
                cancellationToken), null, cancellationToken);
    }

    private static void PaintCoverageRows(RasterSurface pixels, int width,
        CoverageMask mask, Color directColor, Color paint, double alpha,
        RendererBlendMode blendMode, IReadOnlyList<ClipRegion> clips,
        GraphicsSoftMask? graphicsSoftMask, KnockoutState? knockout,
        bool alphaIsShape, int left, int firstRow,
        int right, int lastRow, bool perPixelClip, bool directInk, uint ink,
        bool direct, byte[]? groupAlpha, byte[]? coverage, byte[]? opaqueBlend,
        CancellationToken cancellationToken)
    {
        bool processOverprint = pixels.Ink is not null && pixels.HasSpotPlates
            && paint.SpotName is null && alpha == 1
            && ((paint.OverprintComponents & PreserveNamedSpots) != 0
                || (paint.OverprintComponents & (16 | 64)) == 16)
            && graphicsSoftMask is null && knockout is null
            && pixels.GroupAlpha is null && pixels.GroupShape is null
            && blendMode is RendererBlendMode.Normal or RendererBlendMode.Compatible;
        int preserveComponents = (paint.OverprintComponents & 16) != 0
            ? paint.OverprintComponents & 15 : 0;
        uint directPacked = directColor.Blue | (uint)directColor.Green << 8
            | (uint)directColor.Red << 16 | 0xFF000000u;
        // Combined clip coverage for the current row, filled once per row instead of looking up
        // every clip mask for every pixel.
        byte[]? clipRow = perPixelClip ? RasterBuffers.Rent(right - left) : null;
        for (int y = firstRow; y < lastRow; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int maskRow = mask.RowOffset(y) - mask.Left;
            if (clipRow is not null) FillClipRow(clips, y, left, right, clipRow);
            for (int x = left; x < right; x++)
            {
                int cover = coverage is null ? 255 : coverage[maskRow + x];
                if (cover == 0) continue;
                if (perPixelClip)
                {
                    int clipCover = clipRow![x - left];
                    if (clipCover == 0) continue;
                    cover = clipCover == 255 ? cover : (cover * clipCover + 127) / 255;
                }
                if (directInk && cover == 255)
                {
                    int offset = pixels.Offset(x, y);
                    WriteInk(pixels.Ink!, offset, ink);
                    pixels.SetAlpha(offset, 255);
                    pixels.GroupAlpha?[offset / 4] = 255;
                    continue;
                }
                if (processOverprint)
                {
                    int offset = pixels.Offset(x, y);
                    if (pixels.Alpha(offset) == 255)
                    {
                        pixels.ReplaceProcessInkKeepingSpotsPrepared(offset, ink, cover, preserveComponents);
                        continue;
                    }
                }
                if (direct)
                {
                    int offset = pixels.Offset(x, y);
                    if (cover == 255)
                    {
                        Unsafe.WriteUnaligned(ref pixels.Data[offset], directPacked);
                        if (pixels.HasRgbSpotShadow)
                            pixels.TrackRgbProcessPixel(offset, paint, ink);
                        groupAlpha?[offset / 4] = 255;
                        continue;
                    }
                    if (pixels.RgbProfile is null && pixels[offset + 3] == 255)
                    {
                        if (pixels.HasRgbSpotShadow && paint.Ink is not null && groupAlpha is null
                            && pixels.TryBlendRgbProcessKnockout(offset, paint, ink, 1, cover))
                            continue;
                        pixels.InvalidateRgbSpotPixel(offset);
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
                        pixels.InvalidateRgbSpotPixel(offset);
                        pixels[offset] = opaqueBlend[pixels[offset] * 4];
                        pixels[offset + 1] = opaqueBlend[pixels[offset + 1] * 4 + 1];
                        pixels[offset + 2] = opaqueBlend[pixels[offset + 2] * 4 + 2];
                        if (groupAlpha is not null) TrackGroupAlpha(groupAlpha, offset, alpha * cover / 255d);
                        continue;
                    }
                }
                SetPixel(pixels, width, x, y, paint, alpha * cover / 255d, blendMode,
                    graphicsSoftMask, knockout, shape: cover / 255d, alphaIsShape: alphaIsShape,
                    resolvedInk: pixels.Ink is not null || pixels.HasRgbSpotShadow ? ink : null);
            }
        }
        if (clipRow is not null) RasterBuffers.Return(clipRow);
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
