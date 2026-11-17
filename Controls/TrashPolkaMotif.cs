using System.Windows;
using System.Windows.Media;

namespace KillerPDF.Controls
{
    /// <summary>Where the Trash Polka motif sits: the full sleeve covers the window; the corner cluster is the quieter
    /// layout the second pane uses in split view.</summary>
    internal enum TrashPolkaLayout { FullSleeve, CornerCluster, Chrome }

    /// <summary>
    /// The Trash Polka motif from Steve's forearm piece, as one frozen drawing on a 340 by 210 canvas: a cluster of
    /// solid black hexagons where some cells are hollow bone rings with a circuit trace ending in a node and some hold
    /// a single accent or bone dot (one cell is filled with the accent), a halftone run of accent dots shrinking in
    /// staggered rows, accent and black ink spatter, faint technical linework, and accent brush slashes. A fixed seed
    /// draws the same picture every time; the accent recolors only the accent marks.
    /// </summary>
    internal static class TrashPolkaMotif
    {
        internal const double Width = 340, Height = 210;

        /// <summary>The chrome frame on the canvas: the title bar and toolbar fill the top band down to ChromeTop, the
        /// sidebar the side band out to ChromeSide (the proportions of a maximized KillerPDF window).</summary>
        internal const double ChromeTop = 24, ChromeSide = 52;

        private static readonly Color Ink = Color.FromRgb(0x05, 0x05, 0x05);
        private static readonly Color Bone = Color.FromRgb(0xED, 0xE8, 0xDF);
        private static readonly Color CellEdge = Color.FromRgb(0x3A, 0x3A, 0x3A);

        /// <summary>How strongly the chrome panes show their piece: about what showed through 82 percent charcoal in the
        /// mockup Steve liked, so the panes stay quiet raised cards.</summary>
        internal const double ChromeOpacity = 0.18;

        /// <summary>Device-independent pixels per motif unit: the 340 by 210 piece draws 476 by 294.</summary>
        internal const double MotifScale = 1.4;

        /// <summary>
        /// A pane's brush: its base color with the motif placed over it as if the motif covered the whole
        /// <paramref name="window"/>, so each pane shows only its own part and neighbouring panes join up. One scale
        /// both ways, so hexagons never stretch; <paramref name="anchorRight"/> pins the motif to the window's right
        /// edge instead of its left; <paramref name="opacity"/> draws it faint (the chrome panes).
        /// </summary>
        internal static DrawingBrush Compose(Color baseColor, Drawing motif, Rect paneInWindow, Size window, bool anchorRight = false,
            double opacity = 1)
        {
            // A fixed, small scale: stretching the piece to cover the window blew every dot and hex up to poster size
            // (Steve, 2026-11-16: "everything is way too big").
            double scale = MotifScale;
            double left = anchorRight ? window.Width - Width * scale : 0;
            var pane = new Rect(0, 0, paneInWindow.Width, paneInWindow.Height);

            var fill = new SolidColorBrush(baseColor);
            fill.Freeze();
            var group = new DrawingGroup { ClipGeometry = new RectangleGeometry(pane) };
            group.Children.Add(new GeometryDrawing(fill, null, new RectangleGeometry(pane)));
            var placed = new DrawingGroup
            {
                Transform = new MatrixTransform(scale, 0, 0, scale, left - paneInWindow.X, -paneInWindow.Y), Opacity = opacity
            };
            placed.Children.Add(motif);
            group.Children.Add(placed);

            var brush = new DrawingBrush(group)
            {
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                ViewportUnits = BrushMappingMode.Absolute, Viewport = pane,
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = pane,
            };
            brush.Freeze();
            return brush;
        }

        internal static DrawingGroup Build(TrashPolkaLayout layout, Color accent)
        {
            var canvas = new Canvas(accent, layout switch { TrashPolkaLayout.FullSleeve => 21, TrashPolkaLayout.Chrome => 37, _ => 3 }).Framed();
            if (layout == TrashPolkaLayout.Chrome)
            {
                // The title bar, toolbar and sidebar are one frame of raised panes over the window, so they carry one
                // continuous piece of their own, drawn in window coordinates and never a view of the sleeve: a circuit
                // cluster in the corner where the bars meet the sidebar running down the side, a halftone run along
                // the top band, spatter down the side and a slash across the top. Each pane paints its own part.
                // The sidebar carries the circuit block down most of its height and a halftone run below it, with no
                // spatter (Steve, 2026-11-16: "more hex and dots to the sidebar, not that weird splat").
                canvas.Hexes(6, 6, 3, 11, 8.5, 0.1);
                canvas.Halftone(3, 150, 5, 6, 10, 4.4, 0.7);
                canvas.Halftone(46, 4, 26, 2, 11, 3.6, 0.12);
                canvas.Slash("M120,2 L230,2 L222,16 L112,16 Z", 0.5);
                canvas.Group.Freeze();
                return canvas.Group;
            }
            if (layout == TrashPolkaLayout.FullSleeve)
            {
                // The page sits in the middle of the window, so the sleeve fills the margins: the halftone run down the
                // left behind the sidebar, the hex circuit block down the right, spatter and linework between them.
                canvas.Halftone(0, 0, 10, 20, 11, 4.6, 0.42);
                canvas.Hexes(228, 12, 6, 10, 13, 0.12);
                // No spatter anywhere (Steve, 2026-11-16: "the splatters look terrible").
                canvas.Linework(292, 178, 30);
                canvas.Slash("M0,120 L120,60 L130,70 L0,140 Z", 0.6);
                canvas.Slash("M250,0 L340,0 L340,14 L232,24 Z", 0.5);
            }
            else
            {
                canvas.Halftone(250, 20, 8, 12, 11, 4.4, 0.45);
                canvas.Hexes(215, 95, 5, 5, 13, 0.3);
                canvas.Linework(50, 170, 30);
                canvas.Slash("M140,0 L220,0 L110,40 L95,40 Z", 0.55);
            }
            canvas.Group.Freeze();
            return canvas.Group;
        }

        private sealed class Canvas(Color accent, int seed)
        {
            internal readonly DrawingGroup Group = new();
            private int _seed = seed;

            // A brush sizes a drawing by what it covers, so an empty frame pins every layout to the same canvas and the
            // motif keeps its place in the window instead of being stretched around its marks.
            public Canvas Framed()
            {
                var frame = new RectangleGeometry(new Rect(0, 0, Width, Height));
                frame.Freeze();
                Group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, frame));
                // Marks that run off the edge are cut there, like ink at the edge of the skin.
                Group.ClipGeometry = frame;
                return this;
            }

            // The mockup's linear congruential sequence, so the app draws what Steve approved.
            private double Next()
            {
                // 64-bit: the product passes int's range and would wrap negative, skipping most hexagons.
                _seed = (int)(((long)_seed * 9301 + 49297) % 233280);
                return _seed / 233280.0;
            }

            private static Brush Solid(Color color, double opacity = 1)
            {
                var brush = new SolidColorBrush(color) { Opacity = opacity };
                brush.Freeze();
                return brush;
            }

            private void Add(Geometry geometry, Brush? fill, Pen? pen = null)
            {
                geometry.Freeze();
                Group.Children.Add(new GeometryDrawing(fill, pen, geometry));
            }

            private static Pen Stroke(Color color, double thickness, double opacity = 1)
            {
                var pen = new Pen(Solid(color, opacity), thickness);
                pen.Freeze();
                return pen;
            }

            private static StreamGeometry Hexagon(double cx, double cy, double r)
            {
                var geometry = new StreamGeometry();
                using var context = geometry.Open();
                for (int k = 0; k < 6; k++)
                {
                    double t = Math.PI / 3 * k + Math.PI / 6;
                    var point = new Point(cx + r * Math.Cos(t), cy + r * Math.Sin(t));
                    if (k == 0) context.BeginFigure(point, isFilled: true, isClosed: true);
                    else context.LineTo(point, isStroked: true, isSmoothJoin: false);
                }
                return geometry;
            }

            internal void Hexes(double ox, double oy, int cols, int rows, double r, double skip)
            {
                // Traces run over the neighbouring cells, as on the arm, so they are drawn after the whole block.
                var traces = new List<(Geometry Path, Point Node)>();
                double w = r * Math.Sqrt(3);
                for (int row = 0; row < rows; row++)
                    for (int col = 0; col < cols; col++)
                    {
                        if (Next() < skip) continue;
                        double cx = ox + col * w + (row % 2) * w / 2, cy = oy + row * r * 1.5, kind = Next();
                        if (kind < 0.07) Add(Hexagon(cx, cy, r - 1.2), Solid(accent), Stroke(Ink, 1.5));
                        else Add(Hexagon(cx, cy, r - 1.2), Solid(Ink), Stroke(CellEdge, 1));
                        if (kind > 0.07 && kind < 0.3)
                        {
                            Add(Hexagon(cx, cy, r * 0.55), null, Stroke(Bone, 1.6));
                            if (Next() < 0.6)
                            {
                                var trace = new StreamGeometry();
                                using (var context = trace.Open())
                                {
                                    context.BeginFigure(new Point(cx + r * 0.45, cy + r * 0.25), false, false);
                                    context.LineTo(new Point(cx + r * 1.35, cy + r * 0.75), true, false);
                                    context.LineTo(new Point(cx + r * 1.35, cy + r * 1.85), true, false);
                                }
                                traces.Add((trace, new Point(cx + r * 1.35, cy + r * 1.85)));
                            }
                        }
                        else if (kind > 0.3 && kind < 0.4) Add(new EllipseGeometry(new Point(cx, cy), r * 0.22, r * 0.22), Solid(accent));
                        else if (kind > 0.4 && kind < 0.45) Add(new EllipseGeometry(new Point(cx, cy), r * 0.22, r * 0.22), Solid(Bone));
                    }
                foreach (var (path, node) in traces)
                {
                    Add(path, null, Stroke(Bone, 1.4, 0.75));
                    Add(new EllipseGeometry(node, 2, 2), Solid(Bone));
                }
            }

            internal void Halftone(double ox, double oy, int cols, int rows, double step, double largest, double fade)
            {
                for (int row = 0; row < rows; row++)
                    for (int col = 0; col < cols; col++)
                    {
                        double size = largest - col * fade - Math.Abs(row - rows / 2.0) * 0.05;
                        if (size <= 0.4) continue;
                        Add(new EllipseGeometry(new Point(ox + col * step + (row % 2) * step / 2, oy + row * step), size, size), Solid(accent));
                    }
            }

            // A real ink splatter (Steve: "it should look like a real splatter of ink"): one irregular main drop with a
            // lumpy edge, tapered streaks flung out of it that each end in a teardrop, and satellite drops that get
            // smaller and more stretched the farther out they land, all leaning along their direction of flight.
            internal void Spatter(double cx, double cy, int drops, double spread, double largest, bool ink)
            {
                var fill = Solid(ink ? Ink : accent, 0.85);
                double body = largest * 3.2;

                var blob = new StreamGeometry();
                using (var context = blob.Open())
                {
                    const int points = 28;
                    var edge = new Point[points];
                    for (int k = 0; k < points; k++)
                    {
                        double t = 2 * Math.PI * k / points, wobble = 0.72 + Next() * 0.5;
                        edge[k] = new Point(cx + Math.Cos(t) * body * wobble, cy + Math.Sin(t) * body * wobble);
                    }
                    context.BeginFigure(edge[0], true, true);
                    for (int k = 1; k <= points; k++)
                    {
                        Point a = edge[k - 1], b = edge[k % points];
                        context.QuadraticBezierTo(a, new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2), true, true);
                    }
                }
                Add(blob, fill);

                int streaks = 5 + (int)(Next() * 4);
                for (int i = 0; i < streaks; i++)
                {
                    double angle = Next() * 2 * Math.PI, reach = body * (1.3 + Next() * 1.5), width = body * (0.24 + Next() * 0.2);
                    var direction = new Vector(Math.Cos(angle), Math.Sin(angle));
                    var side = new Vector(-direction.Y, direction.X);
                    Point root = new Point(cx, cy) + direction * body * 0.6, tip = new Point(cx, cy) + direction * reach;
                    var streak = new StreamGeometry();
                    using (var context = streak.Open())
                    {
                        context.BeginFigure(root + side * width, true, true);
                        context.LineTo(tip, true, true);
                        context.LineTo(root - side * width, true, true);
                    }
                    Add(streak, fill);
                    Add(Teardrop(tip + direction * width * 0.8, direction, width * 1.4), fill);
                }

                for (int i = 0; i < drops; i++)
                {
                    double angle = Next() * 2 * Math.PI, distance = body * 1.2 + Next() * spread;
                    double size = Math.Max(0.4, largest * (1 - distance / (body * 1.2 + spread)) * (0.4 + Next() * 0.7));
                    var direction = new Vector(Math.Cos(angle), Math.Sin(angle));
                    Point at = new Point(cx, cy) + direction * distance;
                    Add(distance > body * 2 && Next() < 0.5 ? Teardrop(at, direction, size) : new EllipseGeometry(at, size, size), fill);
                }
            }

            // A drop stretched along its direction of flight: round at the front, pulled to a point behind.
            private static Geometry Teardrop(Point head, Vector direction, double radius)
            {
                var side = new Vector(-direction.Y, direction.X);
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    Point tail = head - direction * radius * 2.4;
                    context.BeginFigure(tail, true, true);
                    context.QuadraticBezierTo(head - direction * radius * 0.6 + side * radius * 1.1, head + direction * radius, true, true);
                    context.QuadraticBezierTo(head - direction * radius * 0.6 - side * radius * 1.1, tail, true, true);
                }
                return geometry;
            }

            internal void Linework(double cx, double cy, double r)
            {
                var pen = Stroke(Bone, 0.7, 0.18);
                Add(new EllipseGeometry(new Point(cx, cy), r, r), null, pen);
                Add(Geometry.Parse(FormattableString.Invariant(
                    $"M{cx - r * 1.3},{cy} L{cx + r * 1.3},{cy} M{cx},{cy - r * 1.3} L{cx},{cy + r * 1.3} M{cx - r},{cy + r} L{cx + r * 0.9},{cy - r * 1.1}")), null, pen);
                Add(Geometry.Parse(FormattableString.Invariant(
                    $"M{cx},{cy - r * 1.6} L{cx + r * 1.4},{cy + r} L{cx - r * 1.4},{cy + r} Z")), null, pen);
            }

            internal void Slash(string path, double opacity) => Add(Geometry.Parse(path), Solid(accent, opacity));
        }
    }
}
