using System.Windows;
using System.Windows.Media;

namespace KillerPDF.Controls
{
    /// <summary>
    /// Trash Polka's document-canvas piece, the E7 mockup Steve picked on 2026-11-16: his hexagons (black and accent,
    /// filled and outlined) layered with dry-brush slashes, smeared accent bars, ink splatter with drips, wavy and
    /// swept halftone and thin linework as one image across the whole canvas, with his tattoo (honeycomb band, circuit
    /// traces, gauge ring) leading, plus bubble halos, dot clumps and streak splatter. Drawn to the pane's real
    /// size at 1:1 from a fixed seed, so the same pane size always gives the same piece.
    /// </summary>
    internal static class TrashPolkaInk
    {
        /// <summary>The seed of the picked mockup (E7) for the main canvas.</summary>
        internal const int WindowSeed = 160;

        /// <summary>A different seed for the second pane in split view, so the two panes are two pieces.</summary>
        internal const int SplitSeed = 161;

        /// <summary>The piece draws this much larger than its design units (Steve, 2026-11-16: "a bit larger").</summary>
        internal const double Scale = 1.25;

        /// <summary>A third seed for the window background and its chrome, a different image from the panes on it.</summary>
        internal const int BackgroundSeed = 162;

        /// <summary>The panes' piece, toned down a little (Steve, 2026-11-16: "lower the opacity of the design a little").</summary>
        internal const double PaneOpacity = 0.75;

        /// <summary>The background piece sits under the toolbar and sidebar, so it stays faint enough to read them over.</summary>
        internal const double BackgroundOpacity = 0.35;

        internal static DrawingGroup Draw(Color accent, Size size, int seed)
        {
            var group = new DrawingGroup();
            using (var dc = group.Open())
            {
                var area = new Rect(0, 0, size.Width, size.Height);
                // A transparent frame so the drawing's bounds are the pane, whatever the ink reaches.
                dc.DrawRectangle(Brushes.Transparent, null, area);
                dc.PushClip(new RectangleGeometry(area));
                new Ink(dc, accent, seed).Piece(area);
                dc.Pop();
            }
            group.Freeze();
            return group;
        }

        private sealed class Ink(DrawingContext dc, Color accent, int seed)
        {
            private readonly Random _r = new(seed);
            private static readonly Color Black = Color.FromRgb(0x0b, 0x0b, 0x0b);
            private static readonly Color Bone = Color.FromRgb(0xE8, 0xE4, 0xDC);

            private double R(double a, double b) => a + _r.NextDouble() * (b - a);

            private static SolidColorBrush B(Color c, double a = 1)
            {
                var s = new SolidColorBrush(Color.FromArgb((byte)(a * 255), c.R, c.G, c.B));
                s.Freeze();
                return s;
            }

            public void Piece(Rect area)
            {
                Sleeve(area);
                double l = area.Left, t = area.Top, r = area.Right, h = area.Height;
                Splatter(new Point(r - 0.4 * 430, t + 0.82 * h), 44, Black);
                Splatter(new Point(l + 0.6 * 430, t + 0.25 * h), 24, accent);
            }

            private void Sleeve(Rect area)
            {
                double right = area.Right, top = area.Top, bottom = area.Bottom, left = area.Left;
                // Back layer: a faint ground so no part of the canvas is bare.
                dc.PushOpacity(0.35);
                // One continuous image across the whole canvas, behind the page too (Steve, 2026-11-16: "a large image
                // that continues throughout a full screen"), so every layer spreads over the full width.
                int across = Math.Max(3, (int)(area.Width / 300));
                for (int i = 0; i < across * 3; i++)
                {
                    double x = left + (i % across + R(0.1, 0.9)) * area.Width / across;
                    double y = top + (i / across + R(0.1, 0.9)) * area.Height / 3;
                    HexCluster(new Point(x, y), R(12, 30), 6 + _r.Next(8), outlineOnly: R(0, 1) < 0.5, colorBone: R(0, 1) < 0.5);
                    HalftoneArc(new Point(x + R(-80, 80), y + R(-80, 80)), R(50, 90), R(110, 180), R(0, 360), R(70, 160), R(0, 1) < 0.5 ? accent : Black);
                    Splatter(new Point(x + R(-120, 120), y + R(-120, 120)), R(14, 30), R(0, 1) < 0.3 ? accent : Black);
                }
                dc.Pop();
                // Middle layer: mini-pieces in loose columns across the whole width, each at its own angle, overlapping.
                double[] angles = [-62, -35, -12, 8, 27, 51, 74];
                for (double col = left + 60; col < right; col += R(230, 320))
                    for (double y = top + R(20, 120); y < bottom; y += R(170, 260))
                    {
                        double x = col + R(-70, 70);
                        var c = new Point(x, y);
                        double ang = angles[_r.Next(angles.Length)];
                        switch (_r.Next(4))
                        {
                            case 0:
                                Tilted(c, ang, () => DryBrush(new Point(c.X - 180, c.Y), new Point(c.X + 180, c.Y), R(26, 50), Black));
                                HexCluster(new Point(c.X + R(-60, 60), c.Y + R(-60, 60)), R(14, 24), 5 + _r.Next(6), false, R(0, 1) < 0.5);
                                break;
                            case 1:
                                Tilted(c, ang, () => SmearBar(new Rect(c.X - 160, c.Y - 13, 320, R(16, 30)), vertical: false, accent));
                                Splatter(new Point(c.X + R(-50, 50), c.Y + R(-30, 30)), R(22, 44), Black);
                                break;
                            case 2:
                                BrushArc(c, R(70, 130), R(0, 360), R(90, 170), R(22, 38), Black);
                                HalftoneArc(c, 40, 90, R(0, 360), R(80, 140), accent);
                                break;
                            default:
                                Starburst(c, 14, R(70, 120));
                                dc.DrawEllipse(null, new Pen(B(accent, 0.9), 2), c, R(50, 90), R(50, 90));
                                HexCluster(new Point(c.X + R(-40, 40), c.Y + R(-40, 40)), R(10, 18), 6, true, R(0, 1) < 0.5);
                                break;
                        }
                    }
                // Wavy dot ribbons snaking across the canvas.
                for (int i = 0; i < across * 3; i++)
                {
                    double x0 = R(left - 60, right - 200);
                    double y0 = R(top, bottom - 150);
                    DotWave(new Point(x0, y0), R(260, 420), R(-50, 50), R(18, 40), R(0.012, 0.03), 4 + _r.Next(4), i % 3 == 0 ? Black : accent);
                }
                // Hexagon clusters, black and accent, filled and outlined.
                for (int i = 0; i < across * 5; i++)
                {
                    var p = new Point(R(left, right), R(top + 20, bottom - 40));
                    HexMix(p, R(11, 30), 5 + _r.Next(9));
                }
                // Center piece: the middle of the canvas carries its own focal piece, so the image runs straight through.
                var mid = new Point(left + area.Width * 0.5, top + area.Height * 0.42);
                HexCluster(new Point(mid.X - 60, mid.Y - 80), 28, 12, outlineOnly: false, colorBone: false);
                Tilted(mid, -18, () => SmearBar(new Rect(mid.X - area.Width * 0.45, mid.Y + 40, area.Width * 0.9, 36), vertical: false, accent));
                BrushArc(mid, 220, 160, 150, 56, Black);
                DryBrush(new Point(mid.X - 300, mid.Y + 260), new Point(mid.X + 320, mid.Y - 240), 60, Black);
                HalftoneArc(new Point(mid.X + 80, mid.Y + 60), 120, 220, 200, 130, accent);
                Splatter(new Point(mid.X + 140, mid.Y + 200), 58, Black);
                Starburst(new Point(mid.X - 220, mid.Y - 60), 20, 160);
                dc.DrawEllipse(null, new Pen(B(accent, 0.9), 2.4), mid, 170, 160);
                // Front layer, main piece: right margin, centered low.
                var m = new Point(right - 200, top + area.Height * 0.58);
                HalftoneArc(m, 150, 200, -150, 120, accent);
                Tilted(m, 12, () => SmearBar(new Rect(m.X - 22, top - 20, 44, area.Height * 0.9), vertical: true, accent));
                BrushArc(new Point(m.X + 40, m.Y - 30), 175, 200, 140, 46, Black);
                HexCluster(new Point(m.X + 50, m.Y - 230), 26, 9, outlineOnly: false, colorBone: false);
                HexCluster(new Point(m.X - 120, m.Y - 150), 14, 7, outlineOnly: true, colorBone: true);
                Splatter(new Point(m.X - 10, m.Y + 90), 70, Black);
                DryBrush(new Point(m.X - 230, m.Y + 210), new Point(m.X + 230, m.Y - 20), 52, Black);
                Starburst(new Point(m.X - 150, m.Y - 20), 22, 140);
                dc.DrawEllipse(null, new Pen(B(accent, 0.9), 2.2), new Point(m.X + 20, m.Y - 60), 110, 104);
                Splatter(new Point(m.X + 120, m.Y - 330), 20, accent);
                // Second piece: left margin, high, smaller, angled the other way.
                var s = new Point(left + 140, top + 230);
                HexCluster(new Point(s.X - 40, s.Y - 90), 20, 10, outlineOnly: false, colorBone: true);
                HexCluster(new Point(s.X + 70, s.Y + 40), 11, 6, outlineOnly: true, colorBone: false);
                Tilted(s, -28, () => SmearBar(new Rect(s.X - 200, s.Y + 30, 380, 24), vertical: false, accent));
                DryBrush(new Point(s.X + 60, s.Y - 220), new Point(s.X - 40, s.Y + 260), 40, Black);
                HalftoneArc(new Point(s.X + 20, s.Y + 120), 70, 120, 20, 140, Black);
                Splatter(new Point(s.X + 90, s.Y + 170), 30, accent);
                dc.DrawEllipse(null, new Pen(B(Black, 0.85), 1.4), s, 120, 112);
                // Steve's own tattoo on top of everything: the honeycomb band with its gauge ring, big enough to lead,
                // one across the center behind the page and one down the right.
                var t1 = new Point(left + area.Width * 0.38, top + area.Height * 0.18);
                BubbleHalo(new Point(t1.X + 180, t1.Y + 260), 300);
                TattooPiece(t1, Math.Clamp(area.Height / 22, 26, 44));
                var t2 = new Point(right - area.Width * 0.2, top + area.Height * 0.12);
                BubbleHalo(new Point(t2.X + 120, t2.Y + 220), 220);
                TattooPiece(t2, Math.Clamp(area.Height / 30, 20, 34));
                DotClump(new Point(left + area.Width * 0.18, top + area.Height * 0.78), 90);
                DotClump(new Point(right - area.Width * 0.12, top + area.Height * 0.82), 70);
                StreakSplat(new Point(left + area.Width * 0.12, top + area.Height * 0.3), 46);
                StreakSplat(new Point(left + area.Width * 0.7, top + area.Height * 0.85), 40);
                // Stray drops so no part is ever empty.
                for (int i = 0; i < across * 20; i++)
                {
                    var p = new Point(R(left, right), R(top, bottom));
                    double rr = Math.Pow(R(0, 1), 3) * 4 + 0.7;
                    dc.DrawEllipse(B(i % 4 == 0 ? accent : Black, 0.9), null, p, rr, rr);
                }
            }

            // ---- Steve's tattoo ---------------------------------------------------------------------------------------

            /// <summary>
            /// The tattoo itself: a gauge ring at the top, then a diagonal band of solid ink hexes with thin gaps, cell
            /// variants (nested bone ring with a black core, accent dot, hollow), circuit traces between cell centres that
            /// end in ring nodes, and parallel circuit lines fanning out of the band.
            /// </summary>
            private void TattooPiece(Point at, double r)
            {
                double gap = Math.Max(3, r * 0.12);
                double dx = r * Math.Sqrt(3) + gap, dy = r * 1.5 + gap * 0.87;
                Point Cell(int q, int s) => new(at.X + dx * (q + s / 2.0), at.Y + r * 4.2 + dy * s);
                // The band: rows slide right as they go down, three to four cells wide, like the forearm piece.
                var cells = new List<(int q, int s)>();
                for (int s = 0; s < 7; s++)
                {
                    int width = s is 0 or 6 ? 2 : 3 + (s % 2);
                    int start = -s / 2 + (s > 3 ? 1 : 0);
                    for (int q = start; q < start + width; q++) cells.Add((q, s));
                }
                var bone = B(Bone);
                var ink = B(Black);
                foreach (var (q, s) in cells)
                {
                    var p = Cell(q, s);
                    Hex(p, r, Black, true);
                    switch (_r.Next(7))
                    {
                        case 0: case 1: Hex(p, r * 0.62, Bone, true); Hex(p, r * 0.4, Black, true); break;     // nested
                        case 2: dc.DrawEllipse(B(accent), null, p, r * 0.17, r * 0.17); break;                // accent dot
                        case 3: Hex(p, r * 0.6, Bone, true); Hex(p, r * 0.46, Black, true); Hex(p, r * 0.3, Bone, true); break;
                        default: break;                                                                        // solid
                    }
                }
                // Traces inside the band: centre to centre with a ring node at each end.
                var trace = new Pen(bone, Math.Max(2, r * 0.1));
                for (int i = 0; i < 4; i++)
                {
                    var a = cells[_r.Next(cells.Count)];
                    var b = (q: a.q + (_r.Next(2) == 0 ? 0 : -1), s: a.s + 1 + _r.Next(2));
                    Point pa = Cell(a.q, a.s), pb = Cell(b.q, b.s);
                    dc.DrawLine(trace, pa, new Point(pa.X, (pa.Y + pb.Y) / 2));
                    dc.DrawLine(trace, new Point(pa.X, (pa.Y + pb.Y) / 2), pb);
                    dc.DrawEllipse(ink, trace, pb, r * 0.24, r * 0.24);
                }
                // Circuit fan out of the band's lower right: parallel lines with a bend and a node dot.
                var fan = new Pen(ink, Math.Max(1.6, r * 0.07));
                var origin = Cell(cells[^1].q, cells[^1].s);
                for (int k = 0; k < 6; k++)
                {
                    var s0 = origin + new Vector(r * 0.3 * k, r);
                    var s1 = s0 + new Vector(0, r * (1.2 + k * 0.35));
                    var s2 = s1 + new Vector(r * (1 + k * 0.2), r * (1 + k * 0.2));
                    var s3 = s2 + new Vector(0, r * R(0.8, 2));
                    dc.DrawLine(fan, s0, s1); dc.DrawLine(fan, s1, s2); dc.DrawLine(fan, s2, s3);
                    dc.DrawEllipse(ink, null, s3, r * 0.13, r * 0.13);
                }
                // A second fan up the left side, the long traces of the tattoo's upper arm.
                var first = Cell(cells[0].q, cells[0].s);
                for (int k = 0; k < 4; k++)
                {
                    var s0 = first + new Vector(-r * 1.2, -r * 0.4 * k);
                    var s1 = s0 + new Vector(-r * (1.5 + k * 0.5), -r * (1.5 + k * 0.5));
                    var s2 = s1 + new Vector(-r * 2, 0);
                    dc.DrawLine(fan, s0, s1); dc.DrawLine(fan, s1, s2);
                    dc.DrawEllipse(ink, null, s2, r * 0.12, r * 0.12);
                }
                // The gauge ring above the band.
                Gauge(new Point(at.X + dx * 0.2, at.Y + r * 0.6), r * 2.6);
            }

            /// <summary>The tattoo's ring: a thick ragged ink ring with accent spatter, circuit arcs and spokes inside, and
            /// an accent hex at the hub.</summary>
            private void Gauge(Point c, double rad)
            {
                var ink = B(Black);
                // Ragged outer ring: a thick arc band drawn as many short strokes.
                BrushArc(c, rad, 0, 360, rad * 0.22, Black);
                dc.DrawEllipse(null, new Pen(B(accent, 0.8), 1.6), c, rad * 1.04, rad * 1.04);
                for (int i = 0; i < 40; i++)
                {
                    double a = R(0, Math.PI * 2), d = rad * R(0.95, 1.25), rr = R(0.6, 2.4);
                    dc.DrawEllipse(B(i % 3 == 0 ? Black : accent, 0.9), null, new Point(c.X + Math.Cos(a) * d, c.Y + Math.Sin(a) * d), rr, rr);
                }
                // Circuit arcs inside: broken concentric arcs at several radii.
                var thin = new Pen(ink, Math.Max(1.4, rad * 0.025));
                foreach (double f in new[] { 0.78, 0.62, 0.48 })
                {
                    double a0 = R(0, 360), sweep = R(120, 260);
                    double rr = rad * f;
                    var p0 = new Point(c.X + rr * Math.Cos(a0 * Math.PI / 180), c.Y + rr * Math.Sin(a0 * Math.PI / 180));
                    double a1 = a0 + sweep;
                    var p1 = new Point(c.X + rr * Math.Cos(a1 * Math.PI / 180), c.Y + rr * Math.Sin(a1 * Math.PI / 180));
                    var g = new PathGeometry([new PathFigure(p0, [new ArcSegment(p1, new Size(rr, rr), 0, sweep > 180, SweepDirection.Clockwise, true)], false)]);
                    g.Freeze();
                    dc.DrawGeometry(null, new Pen(ink, rad * 0.06), g);
                }
                // Spokes stepping in from the ring, each with a short right-angle jog.
                for (int i = 0; i < 9; i++)
                {
                    double a = i * Math.PI * 2 / 9 + R(-0.1, 0.1);
                    var o = new Point(c.X + Math.Cos(a) * rad * 0.86, c.Y + Math.Sin(a) * rad * 0.86);
                    var m = new Point(c.X + Math.Cos(a) * rad * 0.55, c.Y + Math.Sin(a) * rad * 0.55);
                    var n = new Point(c.X + Math.Cos(a + 0.3) * rad * 0.4, c.Y + Math.Sin(a + 0.3) * rad * 0.4);
                    dc.DrawLine(thin, o, m); dc.DrawLine(thin, m, n);
                }
                // Hub: accent hex with a target ring.
                dc.DrawEllipse(null, new Pen(B(accent), rad * 0.04), c, rad * 0.24, rad * 0.24);
                Hex(c, rad * 0.16, accent, true);
                Hex(c, rad * 0.08, Black, true);
            }

            /// <summary>A halo of small round bubble dots, dense near the middle and thinning out, from the clock piece.</summary>
            private void BubbleHalo(Point c, double radius)
            {
                var ink = B(Black, 0.9);
                for (int i = 0; i < 420; i++)
                {
                    double a = R(0, Math.PI * 2), d = radius * Math.Sqrt(R(0, 1));
                    if (R(0, 1) < d / radius * 0.6) continue;
                    double rr = R(2.5, 6.5) * (1 - d / radius * 0.5);
                    var p = new Point(c.X + Math.Cos(a) * d, c.Y + Math.Sin(a) * d);
                    if (R(0, 1) < 0.5) dc.DrawEllipse(ink, null, p, rr, rr);
                    else dc.DrawEllipse(null, new Pen(ink, rr * 0.5), p, rr * 0.8, rr * 0.8);
                }
            }

            /// <summary>Big accent dots bunched together with smaller ones falling away, from the bird piece.</summary>
            private void DotClump(Point c, double radius)
            {
                for (int i = 0; i < 70; i++)
                {
                    double a = R(0, Math.PI * 2), d = radius * Math.Pow(R(0, 1), 0.8);
                    double rr = (1 - d / radius) * R(6, 16) + 1.2;
                    dc.DrawEllipse(B(accent, 0.9), null, new Point(c.X + Math.Cos(a) * d, c.Y + Math.Sin(a) * d), rr, rr);
                }
            }

            /// <summary>A black splatter with long thin streak lines shooting straight out of it, from the bird piece.</summary>
            private void StreakSplat(Point c, double size)
            {
                Splatter(c, size, Black);
                var ink = B(Black);
                for (int i = 0; i < 5; i++)
                {
                    double a = R(0, Math.PI * 2), len = size * R(2.5, 5);
                    var end = new Point(c.X + Math.Cos(a) * len, c.Y + Math.Sin(a) * len);
                    dc.DrawLine(new Pen(ink, R(1.5, 3.5)) { EndLineCap = PenLineCap.Triangle }, c, end);
                    dc.DrawEllipse(ink, null, end, R(2, 5), R(2, 5));
                }
            }

            private void Tilted(Point about, double degrees, Action draw)
            {
                dc.PushTransform(new RotateTransform(degrees, about.X, about.Y));
                draw();
                dc.Pop();
            }

            private void BrushArc(Point c, double radius, double startDeg, double sweepDeg, double width, Color col)
            {
                int bristles = (int)(width * 1.4);
                for (int i = 0; i < bristles; i++)
                {
                    double rr = radius + (i / (double)bristles - 0.5) * width;
                    double edge = Math.Abs(i / (double)bristles - 0.5) * 2;
                    double a0 = startDeg + R(0, 15 + edge * 30), a1 = startDeg + sweepDeg - R(0, 10 + edge * 50);
                    double a = a0;
                    while (a < a1)
                    {
                        double b = Math.Min(a1, a + R(15, 70));
                        var p0 = new Point(c.X + rr * Math.Cos(a * Math.PI / 180), c.Y + rr * Math.Sin(a * Math.PI / 180));
                        var p1 = new Point(c.X + rr * Math.Cos(b * Math.PI / 180), c.Y + rr * Math.Sin(b * Math.PI / 180));
                        var g = new PathGeometry([new PathFigure(p0, [new ArcSegment(p1, new Size(rr, rr), 0, b - a > 180, SweepDirection.Clockwise, true)], false)]);
                        g.Freeze();
                        double w = edge < 0.6 ? R(2.4, 4) : R(1, 2.4);
                        dc.DrawGeometry(null, new Pen(B(col, R(0.7, 1)), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Triangle }, g);
                        a = b + (R(0, 1) < 0.1 + edge * 0.45 ? R(2, 10) : 0);
                    }
                }
            }

            private void DotWave(Point start, double length, double angleDeg, double amp, double freq, int rows, Color col)
            {
                double a = angleDeg * Math.PI / 180, ph = R(0, 6);
                var dir = new Vector(Math.Cos(a), Math.Sin(a));
                var nrm = new Vector(-dir.Y, dir.X);
                for (int row = 0; row < rows; row++)
                {
                    double across = 1 - Math.Abs(row - (rows - 1) / 2.0) / ((rows + 1) / 2.0);
                    for (double t = 0; t < length; t += 11)
                    {
                        double wave = amp * Math.Sin(t * freq * Math.PI * 2 / 10 + ph);
                        double pulse = 0.5 + 0.5 * Math.Sin(t / length * Math.PI);
                        double rad = 4.8 * across * pulse;
                        if (rad < 0.9) continue;
                        var p = start + dir * t + nrm * (wave + (row - (rows - 1) / 2.0) * 11 + (t / 11 % 2) * 2);
                        dc.DrawEllipse(B(col, 0.92), null, p, rad, rad);
                    }
                }
            }

            private void HalftoneArc(Point c, double r0, double r1, double startDeg, double sweepDeg, Color col)
            {
                for (double r = r0; r <= r1; r += 13)
                {
                    double step = 13 / r * 180 / Math.PI;
                    for (double a = 0; a <= sweepDeg; a += step)
                    {
                        double t = a / sweepDeg, across = 1 - Math.Abs((r - r0) / (r1 - r0) - 0.5) * 2;
                        double rad = 5.6 * Math.Pow(t, 0.9) * (0.45 + 0.55 * across);
                        if (rad < 0.9) continue;
                        double ang = (startDeg + a) * Math.PI / 180;
                        dc.DrawEllipse(B(col, 0.95), null, new Point(c.X + r * Math.Cos(ang), c.Y + r * Math.Sin(ang)), rad, rad);
                    }
                }
            }

            private void Starburst(Point c, int rays, double reach)
            {
                var pen = new Pen(B(Black, 0.8), 1);
                for (int i = 0; i < rays; i++)
                {
                    double a = R(0, Math.PI * 2), len = reach * Math.Pow(R(0.2, 1), 0.6);
                    var end = new Point(c.X + Math.Cos(a) * len, c.Y + Math.Sin(a) * len);
                    dc.DrawLine(pen, c, end);
                    if (R(0, 1) < 0.35) dc.DrawEllipse(B(Black), null, end, 3.2, 3.2);
                }
                dc.DrawEllipse(B(Black), null, c, 4.5, 4.5);
            }

            private void HexCluster(Point at, double r, int count, bool outlineOnly, bool colorBone)
            {
                double dx = r * Math.Sqrt(3) + 4, dy = r * 1.5 + 3.5;
                var taken = new HashSet<(int, int)> { (0, 0) };
                var cells = new List<(int, int)> { (0, 0) };
                while (cells.Count < count)
                {
                    var (q, s) = cells[_r.Next(cells.Count)];
                    int[][] n = [[1, 0], [-1, 0], [0, 1], [0, -1], [1, -1], [-1, 1]];
                    var d = n[_r.Next(6)];
                    var c = (q + d[0], s + d[1]);
                    if (taken.Add(c)) cells.Add(c);
                }
                foreach (var (q, s) in cells)
                {
                    var c = new Point(at.X + dx * (q + s / 2.0), at.Y + dy * s);
                    int kind = _r.Next(10);
                    Color col = kind < 4 ? accent : colorBone ? Bone : Black;
                    bool fill = !outlineOnly && kind % 3 == 0;
                    Hex(c, r, col, fill);
                }
            }

            private void HexMix(Point at, double r, int count)
            {
                double dx = r * Math.Sqrt(3) + 4, dy = r * 1.5 + 3.5;
                var taken = new HashSet<(int, int)> { (0, 0) };
                var cells = new List<(int, int)> { (0, 0) };
                int[][] n = [[1, 0], [-1, 0], [0, 1], [0, -1], [1, -1], [-1, 1]];
                while (cells.Count < count)
                {
                    var (q, s) = cells[_r.Next(cells.Count)];
                    var d = n[_r.Next(6)];
                    if (taken.Add((q + d[0], s + d[1]))) cells.Add((q + d[0], s + d[1]));
                }
                foreach (var (q, s) in cells)
                    Hex(new Point(at.X + dx * (q + s / 2.0), at.Y + dy * s), r, R(0, 1) < 0.5 ? accent : Black, R(0, 1) < 0.45);
            }

            private void Hex(Point c, double r, Color col, bool fill)
            {
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    for (int i = 0; i < 6; i++)
                    {
                        double a = Math.PI / 6 + i * Math.PI / 3;
                        var p = new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
                        if (i == 0) ctx.BeginFigure(p, fill, true); else ctx.LineTo(p, true, true);
                    }
                }
                g.Freeze();
                dc.DrawGeometry(fill ? B(col, 0.92) : null, fill ? null : new Pen(B(col, 0.95), Math.Max(2, r * 0.13)) { LineJoin = PenLineJoin.Miter }, g);
            }

            private void DryBrush(Point a, Point b, double width, Color col)
            {
                var dir = b - a; double len = dir.Length; dir.Normalize();
                var nrm = new Vector(-dir.Y, dir.X);
                var core = new StreamGeometry();
                using (var ctx = core.Open())
                {
                    int n = 40;
                    var top = new List<Point>(); var bot = new List<Point>();
                    for (int i = 0; i <= n; i++)
                    {
                        double t = i / (double)n;
                        double half = width * 0.38 * Math.Sin(Math.PI * Math.Pow(t, 0.7)) * (0.85 + R(0, 0.15));
                        top.Add(a + dir * (len * t * 0.92) + nrm * (half + R(-2, 2)));
                        bot.Add(a + dir * (len * t * 0.86) - nrm * (half * R(0.75, 1) + R(-2, 2)));
                    }
                    ctx.BeginFigure(top[0], true, true);
                    foreach (var p in top.Skip(1)) ctx.LineTo(p, true, true);
                    for (int i = bot.Count - 1; i >= 0; i--) ctx.LineTo(bot[i], true, true);
                }
                core.Freeze();
                dc.DrawGeometry(B(col, 0.95), null, core);
                int bristles = (int)(width * 1.6);
                for (int i = 0; i < bristles; i++)
                {
                    double off = (i / (double)bristles - 0.5) * width;
                    double edge = Math.Abs(off) / (width / 2);
                    double start = R(0, 0.18 + edge * 0.25) * len, end = len - R(0, 0.1 + edge * 0.45) * len;
                    double t = start;
                    while (t < end)
                    {
                        double seg = R(len * 0.08, len * 0.5);
                        double t2 = Math.Min(end, t + seg);
                        var p1 = a + dir * t + nrm * (off + R(-0.6, 0.6));
                        var p2 = a + dir * t2 + nrm * (off + R(-1.2, 1.2));
                        dc.DrawLine(new Pen(B(col, R(0.75, 1)), R(1.2, 2.6)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Triangle }, p1, p2);
                        t = t2 + (R(0, 1) < 0.15 + edge * 0.4 ? R(4, 18) : 0);
                    }
                }
                for (int i = 0; i < 14; i++)
                {
                    double off = R(-width / 2, width / 2);
                    var p1 = a + dir * (len * R(0.85, 0.98)) + nrm * off;
                    var p2 = p1 + dir * R(20, 70) + nrm * R(-6, 6);
                    dc.DrawLine(new Pen(B(col, R(0.5, 0.9)), R(0.8, 1.6)) { EndLineCap = PenLineCap.Triangle }, p1, p2);
                }
            }

            private void SmearBar(Rect r, bool vertical, Color col)
            {
                var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = vertical ? new Point(0, 1) : new Point(1, 0) };
                fade.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0));
                fade.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0.62));
                fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
                fade.Freeze();
                dc.PushOpacityMask(fade);
                dc.DrawRectangle(B(col, 0.55), null, r);
                double across = vertical ? r.Width : r.Height;
                for (double o = 0; o < across; o += R(0.8, 2.2))
                {
                    double a1 = R(0, 0.12), a2 = R(0.75, 1.05);
                    Point p1 = vertical ? new Point(r.X + o, r.Y + r.Height * a1) : new Point(r.X + r.Width * a1, r.Y + o);
                    Point p2 = vertical ? new Point(r.X + o + R(-2, 2), r.Y + r.Height * a2) : new Point(r.X + r.Width * a2, r.Y + o + R(-2, 2));
                    var c = R(0, 1) < 0.15 ? Color.FromRgb((byte)(col.R * 0.55), (byte)(col.G * 0.55), (byte)(col.B * 0.55)) : col;
                    dc.DrawLine(new Pen(B(c, R(0.35, 0.95)), R(0.8, 2)), p1, p2);
                }
                dc.Pop();
            }

            private void Splatter(Point c, double size, Color col)
            {
                var brush = B(col, 0.96);
                for (int k = 0; k < 9; k++)
                {
                    var bc = new Point(c.X + R(-size * 0.6, size * 0.6), c.Y + R(-size * 0.3, size * 0.3));
                    double br = size * R(0.28, 0.55);
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        int n = 40; double ph = R(0, 6), ph2 = R(0, 6);
                        for (int i = 0; i <= n; i++)
                        {
                            double a = i * Math.PI * 2 / n;
                            double rr = br * (1 + 0.12 * Math.Sin(a * 4 + ph) + 0.07 * Math.Sin(a * 7 + ph2) + R(-0.04, 0.04));
                            var p = new Point(bc.X + rr * Math.Cos(a), bc.Y + rr * Math.Sin(a) * 0.85);
                            if (i == 0) ctx.BeginFigure(p, true, true); else ctx.LineTo(p, true, true);
                        }
                    }
                    g.Freeze();
                    dc.DrawGeometry(brush, null, g);
                }
                for (int i = 0; i < 260; i++)
                {
                    double a = R(0, Math.PI * 2), d = size * (0.75 + Math.Pow(R(0, 1), 1.8) * 1.9);
                    double rad = Math.Max(0.6, size * 0.075 * Math.Pow(R(0, 1), 3) * (2.7 - d / size));
                    var p = new Point(c.X + Math.Cos(a) * d * 1.15, c.Y + Math.Sin(a) * d * 0.8);
                    if (R(0, 1) < 0.08 && rad > 1.4)
                    {
                        var tail = p + new Vector(Math.Cos(a), Math.Sin(a)) * rad * R(2, 3.5);
                        dc.DrawLine(new Pen(brush, rad * 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, p, tail);
                    }
                    else dc.DrawEllipse(brush, null, p, rad, rad * R(0.85, 1.15));
                }
                int drips = 4 + _r.Next(4);
                for (int i = 0; i < drips; i++)
                {
                    double x = c.X + R(-size * 0.75, size * 0.75);
                    double y0 = c.Y + size * 0.15, y1 = y0 + R(size * 0.5, size * 1.8);
                    double wTop = R(4, 9), wBot = wTop * 0.45;
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        ctx.BeginFigure(new Point(x - wTop / 2, y0), true, true);
                        ctx.LineTo(new Point(x + wTop / 2, y0), true, true);
                        ctx.LineTo(new Point(x + wBot / 2, y1), true, true);
                        ctx.LineTo(new Point(x - wBot / 2, y1), true, true);
                    }
                    g.Freeze();
                    dc.DrawGeometry(brush, null, g);
                    dc.DrawEllipse(brush, null, new Point(x, y1), wBot * 1.1, wBot * 1.35);
                }
            }
        }
    }
}
