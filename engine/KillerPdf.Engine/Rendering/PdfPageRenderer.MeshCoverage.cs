namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    /// <summary>
    /// Paints the triangles of one mesh shading (ISO 32000-2 8.7.4.5.5 to 8.7.4.5.8) as they are
    /// decoded. A pixel belongs to a triangle when its center lies inside it, so triangles and
    /// separate shadings that share an edge tile without a seam. Each painted pixel composites
    /// through its fractional clip coverage, like every other paint, so clipped edges are smooth.
    /// </summary>
    private sealed class MeshCoverage(Func<double[], Color> convert, ImageColorSpace colorSpace,
        GraphicsState state, RasterSurface target, int targetWidth, int targetHeight,
        double scaleX, double scaleY, CancellationToken cancellationToken)
    {
        private const double Tolerance = 1e-9;
        // The device pixels the clips and the target leave paintable.
        private readonly (int Left, int Top, int Right, int Bottom) _bounds =
            PaintableBounds(state, target, targetWidth, targetHeight, scaleX, scaleY);
        private double[] _values = [];

        private static (int Left, int Top, int Right, int Bottom) PaintableBounds(GraphicsState state,
            RasterSurface target, int targetWidth, int targetHeight, double scaleX, double scaleY)
        {
            (int left, int top, int right, int bottom) = GetRasterBounds(
                state.Clips, null, targetWidth, targetHeight, scaleX, scaleY);
            return (Math.Max(left, target.Left), Math.Max(top, target.Top),
                Math.Min(right, target.Right), Math.Min(bottom, target.Bottom));
        }

        /// <summary>Paints one triangle given in page space; degenerate triangles paint nothing.</summary>
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
            // Rows whose pixel centers fall within the triangle's height.
            int top = (int)Math.Clamp(Math.Ceiling(minimumY - Tolerance - 0.5), _bounds.Top, _bounds.Bottom);
            int bottom = (int)Math.Clamp(Math.Floor(maximumY + Tolerance - 0.5), _bounds.Top - 1L, _bounds.Bottom - 1L);
            if (top > bottom || _bounds.Left >= _bounds.Right) return;
            if (_values.Length != firstValues.Length) _values = new double[firstValues.Length];
            var firstEdge = new EdgeLine(a, b);
            var secondEdge = new EdgeLine(b, c);
            var thirdEdge = new EdgeLine(c, a);
            for (int y = top; y <= bottom; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double centerY = y + 0.5;
                double start = double.PositiveInfinity, end = double.NegativeInfinity;
                firstEdge.Cross(centerY, ref start, ref end);
                secondEdge.Cross(centerY, ref start, ref end);
                thirdEdge.Cross(centerY, ref start, ref end);
                if (start > end) continue;
                int left = (int)Math.Clamp(Math.Ceiling(start - Tolerance - 0.5), _bounds.Left, _bounds.Right);
                int right = (int)Math.Clamp(Math.Floor(end + Tolerance - 0.5), _bounds.Left - 1L, _bounds.Right - 1L);
                for (int x = left; x <= right; x++)
                {
                    double clipAlpha = ClipAlpha(state.Clips, x, y);
                    if (clipAlpha <= 0) continue;
                    Interpolate(a, b, c, area, x + 0.5, centerY,
                        firstValues, secondValues, thirdValues, _values);
                    Color color = OverprintColor(convert(_values), colorSpace, state.FillOverprint, 0);
                    SetPixel(target, targetWidth, x, y, color, state.FillAlpha * clipAlpha,
                        state.BlendMode, state.GraphicsSoftMask, state.Knockout,
                        shape: clipAlpha, alphaIsShape: state.AlphaIsShape);
                }
            }
        }

        private Point Device(Point page) => new(page.X * scaleX, targetHeight - page.Y * scaleY);

        /// <summary>
        /// One triangle edge ordered from its lower endpoint, so an edge shared by two triangles,
        /// in one shading or in two, gives both of them the same crossings.
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

        /// <summary>
        /// Gouraud-interpolates the triangle's color values at a pixel center. A center on an
        /// edge, within rounding, takes the color of that edge.
        /// </summary>
        private static void Interpolate(Point a, Point b, Point c, double area, double x, double y,
            double[] first, double[] second, double[] third, double[] values)
        {
            double wa = Math.Max(0, Edge(b, c, x, y) / area);
            double wb = Math.Max(0, Edge(c, a, x, y) / area);
            double wc = Math.Max(0, Edge(a, b, x, y) / area);
            double sum = wa + wb + wc;
            if (!(sum > 0)) (wa, wb, wc, sum) = (1, 0, 0, 1);
            wa /= sum; wb /= sum; wc /= sum;
            for (int component = 0; component < values.Length; component++)
                values[component] = first[component] * wa + second[component] * wb + third[component] * wc;

            static double Edge(Point first, Point second, double x, double y) =>
                (second.X - first.X) * (y - first.Y) - (second.Y - first.Y) * (x - first.X);
        }
    }
}
