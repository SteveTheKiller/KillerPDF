using System.Numerics;
using System.Runtime.InteropServices;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    /// <summary>
    /// Collects the triangles of one mesh shading (ISO 32000-2 8.7.4.5.5 to 8.7.4.5.8) and paints
    /// them as a single object. Coverage is the union of the triangles over an 8 by 8 grid of
    /// samples per pixel, so the mesh's outer boundary is antialiased while edges shared by
    /// adjacent triangles or patches add up to full coverage, with no gap and no double paint.
    /// Each covered pixel is composited once, at its mesh coverage times its clip coverage.
    /// </summary>
    private sealed class MeshCoverage(Func<double[], Color> convert, ImageColorSpace colorSpace,
        GraphicsState state, RasterSurface target, int targetWidth, int targetHeight,
        double scaleX, double scaleY, CancellationToken cancellationToken)
    {
        private const int Samples = 8;
        private const double Tolerance = 1e-9;
        // Painting in batches keeps the triangle list bounded for very large meshes.
        private const int MaximumBatch = 1 << 20;
        private readonly List<Triangle> _triangles = [];
        private double _minimumX = double.PositiveInfinity, _minimumY = double.PositiveInfinity;
        private double _maximumX = double.NegativeInfinity, _maximumY = double.NegativeInfinity;
        // The device pixels the clips and the target leave paintable. Triangles wholly outside
        // them are dropped as they are decoded.
        private readonly (int Left, int Top, int Right, int Bottom) _bounds =
            PaintableBounds(state, target, targetWidth, targetHeight, scaleX, scaleY);

        private static (int Left, int Top, int Right, int Bottom) PaintableBounds(GraphicsState state,
            RasterSurface target, int targetWidth, int targetHeight, double scaleX, double scaleY)
        {
            (int left, int top, int right, int bottom) = GetRasterBounds(
                state.Clips, null, targetWidth, targetHeight, scaleX, scaleY);
            return (Math.Max(left, target.Left), Math.Max(top, target.Top),
                Math.Min(right, target.Right), Math.Min(bottom, target.Bottom));
        }

        // Vertex color values live in one flat array, three vertices per triangle, so a large
        // mesh keeps no per-vertex objects alive for the collector to trace.
        private double[] _values = [];
        private int _components = -1;

        private readonly record struct Triangle(Point A, Point B, Point C, int Values,
            double MinimumY, double MaximumY);

        /// <summary>Adds one triangle given in page space; degenerate triangles add nothing.</summary>
        internal void Add(Point first, double[] firstValues, Point second, double[] secondValues,
            Point third, double[] thirdValues)
        {
            Point a = Device(first), b = Device(second), c = Device(third);
            if (!double.IsFinite(a.X) || !double.IsFinite(a.Y) || !double.IsFinite(b.X)
                || !double.IsFinite(b.Y) || !double.IsFinite(c.X) || !double.IsFinite(c.Y)) return;
            double area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (Math.Abs(area) < 1e-12) return;
            double minimumY = Math.Min(a.Y, Math.Min(b.Y, c.Y));
            double maximumY = Math.Max(a.Y, Math.Max(b.Y, c.Y));
            double minimumX = Math.Min(a.X, Math.Min(b.X, c.X));
            double maximumX = Math.Max(a.X, Math.Max(b.X, c.X));
            if (maximumX < _bounds.Left || minimumX > _bounds.Right
                || maximumY < _bounds.Top || minimumY > _bounds.Bottom) return;
            _minimumX = Math.Min(_minimumX, minimumX);
            _maximumX = Math.Max(_maximumX, maximumX);
            _minimumY = Math.Min(_minimumY, minimumY);
            _maximumY = Math.Max(_maximumY, maximumY);
            if (_components < 0) _components = firstValues.Length;
            int offset = _triangles.Count * 3 * _components;
            if (offset + 3 * _components > _values.Length)
                Array.Resize(ref _values, Math.Max(64, Math.Max(_values.Length * 2, offset + 3 * _components)));
            firstValues.AsSpan(0, _components).CopyTo(_values.AsSpan(offset));
            secondValues.AsSpan(0, _components).CopyTo(_values.AsSpan(offset + _components));
            thirdValues.AsSpan(0, _components).CopyTo(_values.AsSpan(offset + 2 * _components));
            _triangles.Add(new Triangle(a, b, c, offset, minimumY, maximumY));
            if (_triangles.Count >= MaximumBatch) Paint();
        }

        /// <summary>
        /// Runs a mesh decoder that adds triangles, then paints them. Triangles decoded before a
        /// malformed record still paint before the error propagates, as they did when each
        /// triangle painted on its own.
        /// </summary>
        internal void Collect(Action decode)
        {
            try { decode(); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Paint();
                throw;
            }
            Paint();
        }

        private Point Device(Point page) => new(page.X * scaleX, targetHeight - page.Y * scaleY);

        /// <summary>Composites every collected triangle and starts a new batch.</summary>
        internal void Paint()
        {
            if (_triangles.Count == 0) return;
            try { PaintBatch(); }
            finally
            {
                _triangles.Clear();
                _minimumX = _minimumY = double.PositiveInfinity;
                _maximumX = _maximumY = double.NegativeInfinity;
            }
        }

        private void PaintBatch()
        {
            int left = Math.Max(_bounds.Left, ToPixel(Math.Floor(_minimumX)));
            int top = Math.Max(_bounds.Top, ToPixel(Math.Floor(_minimumY)));
            int right = Math.Min(_bounds.Right, ToPixel(Math.Ceiling(_maximumX)));
            int bottom = Math.Min(_bounds.Bottom, ToPixel(Math.Ceiling(_maximumY)));
            if (right <= left || bottom <= top) return;
            int width = right - left;
            int bandHeight = Math.Clamp((1 << 20) / width, 1, bottom - top);
            var masks = new ulong[checked(width * bandHeight)];
            // Owner per pixel: index + 1 when the triangle covers the pixel center, the negated
            // value when it only reaches the pixel's edge, zero when nothing touched the pixel.
            var owners = new int[masks.Length];
            var values = new double[_components];
            for (int bandTop = top; bandTop < bottom; bandTop += bandHeight)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int bandBottom = Math.Min(bottom, bandTop + bandHeight);
                Array.Clear(masks);
                Array.Clear(owners);
                ReadOnlySpan<Triangle> triangles = CollectionsMarshal.AsSpan(_triangles);
                for (int index = 0; index < triangles.Length; index++)
                {
                    if ((index & 0x3FF) == 0) cancellationToken.ThrowIfCancellationRequested();
                    ref readonly Triangle triangle = ref triangles[index];
                    if (triangle.MaximumY < bandTop || triangle.MinimumY > bandBottom) continue;
                    Cover(triangle, index, left, right, bandTop, bandBottom, width, masks, owners);
                }
                Composite(left, right, bandTop, bandBottom, width, masks, owners, values);
            }
        }

        private static int ToPixel(double value) => (int)Math.Clamp(value, int.MinValue / 2, int.MaxValue / 2);

        private void Cover(in Triangle triangle, int index, int left, int right, int bandTop,
            int bandBottom, int width, ulong[] masks, int[] owners)
        {
            int firstRow = Math.Max(bandTop, ToPixel(Math.Floor(triangle.MinimumY)));
            int lastRow = Math.Min(bandBottom - 1, ToPixel(Math.Floor(triangle.MaximumY)));
            long firstSample = (long)left * Samples, lastSample = (long)right * Samples - 1;
            Span<long> froms = stackalloc long[Samples];
            Span<long> tos = stackalloc long[Samples];
            var edges = new TriangleEdges(new EdgeLine(triangle.A, triangle.B),
                new EdgeLine(triangle.B, triangle.C), new EdgeLine(triangle.C, triangle.A),
                triangle.MinimumY, triangle.MaximumY);
            for (int row = firstRow; row <= lastRow; row++)
            {
                int offset = (row - bandTop) * width - left;
                int touchedLeft = int.MaxValue, touchedRight = int.MinValue;
                // Pixels every sample line covers end to end are filled whole; only the pixels
                // at each line's ends need their sample bits worked out.
                long fullLeft = long.MinValue, fullRight = long.MaxValue;
                // Only the sample lines within the triangle's height can cross it.
                int firstLine = (int)Math.Clamp(Math.Ceiling((triangle.MinimumY - Tolerance - row) * Samples - 0.5), 0, Samples);
                int lastLine = (int)Math.Clamp(Math.Floor((triangle.MaximumY + Tolerance - row) * Samples - 0.5), -1, Samples - 1);
                if (firstLine > 0 || lastLine < Samples - 1) fullLeft = long.MaxValue;
                for (int line = firstLine; line <= lastLine; line++)
                {
                    if (!Crossing(edges, row + (line + 0.5) / Samples, out double start, out double end))
                    {
                        froms[line] = 1;
                        tos[line] = 0;
                        fullLeft = long.MaxValue;
                        continue;
                    }
                    start = Math.Max(start, left - 1);
                    end = Math.Min(end, right + 1);
                    long from = Math.Max(firstSample, (long)Math.Ceiling(start * Samples - 0.5 - Tolerance));
                    long to = Math.Min(lastSample, (long)Math.Floor(end * Samples - 0.5 + Tolerance));
                    froms[line] = from;
                    tos[line] = to;
                    if (from > to)
                    {
                        fullLeft = long.MaxValue;
                        continue;
                    }
                    fullLeft = Math.Max(fullLeft, (from + Samples - 1) / Samples);
                    fullRight = Math.Min(fullRight, (to + 1) / Samples - 1);
                    touchedLeft = Math.Min(touchedLeft, (int)(from / Samples));
                    touchedRight = Math.Max(touchedRight, (int)(to / Samples));
                }
                if (touchedLeft > touchedRight) continue;
                for (int line = firstLine; line <= lastLine; line++)
                {
                    long from = froms[line], to = tos[line];
                    if (from > to) continue;
                    int fromPixel = (int)(from / Samples), toPixel = (int)(to / Samples);
                    for (int pixel = fromPixel; pixel <= toPixel; pixel++)
                    {
                        if (pixel >= fullLeft && pixel <= fullRight)
                        {
                            pixel = (int)fullRight;
                            continue;
                        }
                        int low = (int)Math.Max(from - (long)pixel * Samples, 0);
                        int high = (int)Math.Min(to - (long)pixel * Samples, Samples - 1);
                        ulong bits = ((1UL << (high + 1)) - 1) & ~((1UL << low) - 1);
                        masks[offset + pixel] |= bits << (line * Samples);
                    }
                }
                if (fullLeft <= fullRight)
                    masks.AsSpan(offset + (int)fullLeft, (int)(fullRight - fullLeft + 1)).Fill(ulong.MaxValue);
                // The triangle that covers a pixel's center owns its color; one that only reaches
                // the pixel's edge colors it until a center owner arrives.
                int centerLeft = touchedRight + 1, centerRight = touchedRight;
                if (Crossing(edges, row + 0.5, out double centerStart, out double centerEnd))
                {
                    centerLeft = (int)Math.Clamp(Math.Ceiling(centerStart - Tolerance - 0.5), touchedLeft, touchedRight + 1L);
                    centerRight = (int)Math.Clamp(Math.Floor(centerEnd + Tolerance - 0.5), touchedLeft - 1L, touchedRight);
                }
                if (centerLeft <= centerRight)
                    owners.AsSpan(offset + centerLeft, centerRight - centerLeft + 1).Fill(index + 1);
                else
                    (centerLeft, centerRight) = (touchedRight + 1, touchedRight);
                for (int pixel = touchedLeft; pixel <= touchedRight; pixel++)
                {
                    if (pixel == centerLeft)
                    {
                        pixel = centerRight;
                        continue;
                    }
                    if (owners[offset + pixel] <= 0) owners[offset + pixel] = -(index + 1);
                }
            }
        }

        /// <summary>
        /// One triangle edge ordered from its lower endpoint, so an edge shared by two triangles
        /// gives both of them the same crossings.
        /// </summary>
        private readonly struct EdgeLine
        {
            private readonly double _lowX, _lowY, _highY, _minimumX, _maximumX, _slope;
            private readonly bool _flat;

            internal EdgeLine(Point first, Point second)
            {
                (Point low, Point high) = first.Y < second.Y || first.Y == second.Y && first.X <= second.X
                    ? (first, second) : (second, first);
                _lowX = low.X;
                _lowY = low.Y;
                _highY = high.Y;
                _minimumX = Math.Min(low.X, high.X);
                _maximumX = Math.Max(low.X, high.X);
                _flat = high.Y - low.Y <= Tolerance;
                _slope = _flat ? 0 : (high.X - low.X) / (high.Y - low.Y);
            }

            internal void Cross(double y, ref double start, ref double end)
            {
                if (y < _lowY - Tolerance || y > _highY + Tolerance) return;
                if (_flat)
                {
                    start = Math.Min(start, _minimumX);
                    end = Math.Max(end, _maximumX);
                    return;
                }
                double x = Math.Clamp(_lowX + (y - _lowY) * _slope, _minimumX, _maximumX);
                start = Math.Min(start, x);
                end = Math.Max(end, x);
            }
        }

        private readonly record struct TriangleEdges(EdgeLine First, EdgeLine Second, EdgeLine Third,
            double MinimumY, double MaximumY);

        /// <summary>Finds where the horizontal line at device y crosses the triangle.</summary>
        private static bool Crossing(in TriangleEdges edges, double y, out double start, out double end)
        {
            start = double.PositiveInfinity;
            end = double.NegativeInfinity;
            if (y < edges.MinimumY - Tolerance || y > edges.MaximumY + Tolerance) return false;
            edges.First.Cross(y, ref start, ref end);
            edges.Second.Cross(y, ref start, ref end);
            edges.Third.Cross(y, ref start, ref end);
            return start <= end;
        }

        private void Composite(int left, int right, int bandTop, int bandBottom, int width,
            ulong[] masks, int[] owners, double[] values)
        {
            const double fullCoverage = Samples * Samples;
            for (int y = bandTop; y < bandBottom; y++)
            {
                int offset = (y - bandTop) * width - left;
                for (int x = left; x < right; x++)
                {
                    ulong mask = masks[offset + x];
                    if (mask == 0) continue;
                    double shape = BitOperations.PopCount(mask) / fullCoverage
                        * ClipAlpha(state.Clips, x, y);
                    if (shape <= 0) continue;
                    Interpolate(CollectionsMarshal.AsSpan(_triangles)[Math.Abs(owners[offset + x]) - 1],
                        x + 0.5, y + 0.5, values);
                    Color color = OverprintColor(convert(values), colorSpace, state.FillOverprint, 0);
                    SetPixel(target, targetWidth, x, y, color, state.FillAlpha * shape,
                        state.BlendMode, state.GraphicsSoftMask, state.Knockout,
                        shape: shape, alphaIsShape: state.AlphaIsShape);
                }
            }
        }

        /// <summary>
        /// Gouraud-interpolates the triangle's color values at one point. A point just outside the
        /// triangle, at a partly covered edge pixel, takes the color of the nearest edge.
        /// </summary>
        private void Interpolate(in Triangle triangle, double x, double y, double[] values)
        {
            ReadOnlySpan<double> first = _values.AsSpan(triangle.Values, values.Length);
            ReadOnlySpan<double> second = _values.AsSpan(triangle.Values + values.Length, values.Length);
            ReadOnlySpan<double> third = _values.AsSpan(triangle.Values + 2 * values.Length, values.Length);
            double area = Edge(triangle.A, triangle.B, triangle.C.X, triangle.C.Y);
            double a = Math.Max(0, Edge(triangle.B, triangle.C, x, y) / area);
            double b = Math.Max(0, Edge(triangle.C, triangle.A, x, y) / area);
            double c = Math.Max(0, Edge(triangle.A, triangle.B, x, y) / area);
            double sum = a + b + c;
            if (!(sum > 0)) (a, b, c, sum) = (1, 0, 0, 1);
            a /= sum; b /= sum; c /= sum;
            for (int component = 0; component < values.Length; component++)
                values[component] = first[component] * a + second[component] * b + third[component] * c;

            static double Edge(Point first, Point second, double x, double y) =>
                (second.X - first.X) * (y - first.Y) - (second.Y - first.Y) * (x - first.X);
        }
    }
}
