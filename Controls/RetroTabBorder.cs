using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerPDF.Controls
{
    /// <summary>
    /// A document tab's face, drawn the way KillerShell's tabs are (its TabSurfaceBorder). The
    /// active tab draws its own outline and face; on 98SE with the strip below the page it also
    /// draws the tab's Win98 edges.
    /// </summary>
    /// <remarks>
    /// The template flips a bottom strip's tabs upside down, and flipping the top-strip bevels
    /// would flip Win98's light, which always comes from the top-left. So on a bottom strip the
    /// template's bevel borders are hidden and the edges are drawn here pixel by pixel: white left
    /// edge, #808080 then black on the right and along the foot, the shadow owning both foot
    /// corners, which are cut on the diagonal. No top edge: the tab hangs from the page.
    /// </remarks>
    public sealed class RetroTabBorder : Border
    {
        public static readonly DependencyProperty RetroBottomEdgesProperty = DependencyProperty.Register(
            nameof(RetroBottomEdges), typeof(bool), typeof(RetroTabBorder),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool RetroBottomEdges
        {
            get => (bool)GetValue(RetroBottomEdgesProperty);
            set => SetValue(RetroBottomEdgesProperty, value);
        }

        /// <summary>The card's edge color, run up the active tab's sides beneath a fading outline.</summary>
        public static readonly DependencyProperty DividerBrushProperty = DependencyProperty.Register(
            nameof(DividerBrush), typeof(Brush), typeof(RetroTabBorder),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush? DividerBrush
        {
            get => (Brush?)GetValue(DividerBrushProperty);
            set => SetValue(DividerBrushProperty, value);
        }

        /// <summary>
        /// Whether the foot curves out on the left. False for a tab standing on the card's left edge:
        /// there is no card top edge to curve into, the curve falls outside the strip and is clipped,
        /// and its face fill would cut 4px out of the side line. The side runs straight down instead.
        /// </summary>
        public static readonly DependencyProperty FlareLeftProperty = DependencyProperty.Register(
            nameof(FlareLeft), typeof(bool), typeof(RetroTabBorder),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool FlareLeft
        {
            get => (bool)GetValue(FlareLeftProperty);
            set => SetValue(FlareLeftProperty, value);
        }

        /// <summary>The same as <see cref="FlareLeft"/> for a tab standing on the card's right edge.</summary>
        public static readonly DependencyProperty FlareRightProperty = DependencyProperty.Register(
            nameof(FlareRight), typeof(bool), typeof(RetroTabBorder),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool FlareRight
        {
            get => (bool)GetValue(FlareRightProperty);
            set => SetValue(FlareRightProperty, value);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (RetroBottomEdges || !DrawRoundedTop(drawingContext)) base.OnRender(drawingContext);
            if (RetroBottomEdges) DrawRetroBottomEdges(drawingContext);
        }

        /// <summary>
        /// The active tab: its outline rounds the top corners at CornerRadius and the face inside
        /// rounds them 1px less, so the face nests in the outline. A plain Border works the inner
        /// curve out from each side's thickness, which under the 3px top stripe flattened the
        /// face's corners almost square. False for any other tab, which Border draws as before.
        /// </summary>
        private bool DrawRoundedTop(DrawingContext dc)
        {
            CornerRadius r = CornerRadius;
            Thickness b = BorderThickness;
            if ((r.TopLeft <= 0 && r.TopRight <= 0) || r.BottomLeft > 0 || r.BottomRight > 0 || b.Top <= 0) return false;
            var outer = new Rect(RenderSize);
            if (outer.Width <= b.Left + b.Right || outer.Height <= b.Top + b.Bottom) return false;
            var inner = new Rect(b.Left, b.Top, outer.Width - b.Left - b.Right, outer.Height - b.Top - b.Bottom);
            Geometry outerShape = TopRounded(outer, r.TopLeft, r.TopRight);
            Geometry face = TopRounded(inner, Math.Max(0, r.TopLeft - 1), Math.Max(0, r.TopRight - 1));
            // On a single pane the outline's sides fade out toward the tab's foot, and the card's
            // own edge only starts below the tab, which left a gap between the two. The card's
            // edge runs on up the sides beneath the outline, so the accent fades into it.
            if (DividerBrush is Brush edge && BorderBrush is not SolidColorBrush)
            {
                if (b.Left > 0) dc.DrawRectangle(edge, null, new Rect(0, r.TopLeft, b.Left, outer.Height - r.TopLeft));
                if (b.Right > 0)
                    dc.DrawRectangle(edge, null, new Rect(outer.Width - b.Right, r.TopRight, b.Right, outer.Height - r.TopRight));
            }
            if (BorderBrush != null)
                dc.DrawGeometry(BorderBrush, null, new CombinedGeometry(GeometryCombineMode.Exclude, outerShape, face));
            if (Background != null) dc.DrawGeometry(Background, null, face);
            DrawFlare(dc, outer, b);
            return true;
        }

        /// <summary>
        /// The active tab's grain sits in the child and reaches past the padding to cover the
        /// whole face, so it kept square corners over the rounded face. Clipping the child to the
        /// face (in the child's own coordinates) rounds the grain with it; the title and close
        /// button sit inside the padding and are untouched.
        /// </summary>
        protected override Size ArrangeOverride(Size arrangeSize)
        {
            Size arranged = base.ArrangeOverride(arrangeSize);
            if (Child is UIElement child)
            {
                CornerRadius r = CornerRadius;
                Thickness b = BorderThickness, p = Padding;
                bool rounded = !RetroBottomEdges && (r.TopLeft > 0 || r.TopRight > 0) &&
                               r.BottomLeft <= 0 && r.BottomRight <= 0 && b.Top > 0;
                double w = arranged.Width - b.Left - b.Right, h = arranged.Height - b.Top - b.Bottom;
                child.Clip = rounded && w > 0 && h > 0
                    ? TopRounded(new Rect(-p.Left, -p.Top, w, h), Math.Max(0, r.TopLeft - 1), Math.Max(0, r.TopRight - 1))
                    : null;
            }
            return arranged;
        }

        /// <summary>
        /// The tab's foot curves out into the card's top edge on both sides (#528), so the outline
        /// runs from the tab into the pane's border without a hard corner.
        /// </summary>
        /// <remarks>
        /// Drawn pixel by pixel in the edge's own color, like the 98SE edges below. An anti-aliased
        /// arc this small came out as dim half-pixels that read as a break between the tab's side
        /// and the card. The tab's last row is the card's top border row (the card tucks into the
        /// band by 1px), so the curve lands on that row.
        /// </remarks>
        private void DrawFlare(DrawingContext dc, Rect outer, Thickness b)
        {
            int w = (int)Math.Round(outer.Width), h = (int)Math.Round(outer.Height);
            if (h < 12 || w < 24 || BorderBrush is not Brush edge || b.Left <= 0 || b.Right <= 0) return;
            // Per row, counted up from the tab's last row: the curve's pixels and how far out the
            // face reaches, both as offsets outward from the side column.
            (int From, int To, int Face)[] rows = { (5, 6, 4), (3, 4, 2), (2, 2, 1), (1, 1, 0) };
            void Px(Brush brush, int x, int y, int cw = 1) => dc.DrawRectangle(brush, null, new Rect(x, y, cw, 1));
            // The face inside the curve lies outside the tab. A theme's painted face (Trash Polka's ink, ThemeMotif) is
            // clipped to the tab's own rectangle and paints nothing there, so the card's top line under the next tab
            // showed through and ran on to the tab's side (Steve 2026-11-26). Out there the face takes the pane's own
            // solid color: the active tab's face is ContentPaneBrush, the base the painted face is laid over.
            Brush? outside = Background is SolidColorBrush ? Background
                : TryFindResource("ContentPaneBrush") as SolidColorBrush ?? Background;
            for (int k = 0; k < rows.Length; k++)
            {
                int y = h - 1 - k;
                var (from, to, face) = rows[k];
                // Left: the side column is 0 and outward is negative.
                if (FlareLeft)
                {
                    if (Background != null) Px(Background, 0, y);
                    if (outside != null && face > 0) Px(outside, -face, y, face);
                    Px(edge, -to, y, to - from + 1);
                }
                // Right: the side column is w - 1 and outward is positive.
                if (FlareRight)
                {
                    if (Background != null) Px(Background, w - 1, y);
                    if (outside != null && face > 0) Px(outside, w, y, face);
                    Px(edge, w - 1 + from, y, to - from + 1);
                }
            }
        }

        private static Geometry TopRounded(Rect rect, double left, double right)
        {
            left = Math.Min(left, Math.Min(rect.Width / 2, rect.Height));
            right = Math.Min(right, Math.Min(rect.Width / 2, rect.Height));
            var shape = new StreamGeometry();
            using (StreamGeometryContext c = shape.Open())
            {
                c.BeginFigure(new Point(rect.Left, rect.Bottom), isFilled: true, isClosed: true);
                c.LineTo(new Point(rect.Left, rect.Top + left), true, false);
                if (left > 0) c.ArcTo(new Point(rect.Left + left, rect.Top), new Size(left, left), 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(rect.Right - right, rect.Top), true, false);
                if (right > 0) c.ArcTo(new Point(rect.Right, rect.Top + right), new Size(right, right), 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(rect.Right, rect.Bottom), true, false);
            }
            shape.Freeze();
            return shape;
        }

        private void DrawRetroBottomEdges(DrawingContext dc)
        {
            int w = (int)Math.Round(RenderSize.Width), h = (int)Math.Round(RenderSize.Height);
            if (w < 6 || h < 4) return;
            // Drawn in screen orientation: undo the template's flip.
            dc.PushTransform(new MatrixTransform(1, 0, 0, -1, 0, h));
            Brush light = (TryFindResource("BevelLightBrush") as Brush) ?? Brushes.White;
            Brush shadow = (TryFindResource("BevelDarkBrush") as Brush) ?? Brushes.Gray;
            Brush dark = (TryFindResource("BevelOuterDarkBrush") as Brush) ?? Brushes.Black;
            Brush face = (TryFindResource("BackgroundBrush") as Brush) ?? Brushes.Silver;
            void Px(Brush b, int x, int y, int cw = 1, int ch = 1) => dc.DrawRectangle(b, null, new Rect(x, y, cw, ch));

            // Cut corners show the strip behind the tab.
            Px(face, 0, h - 2, 1, 2); Px(face, 1, h - 1);
            Px(face, w - 1, h - 2, 1, 2); Px(face, w - 2, h - 1);
            Px(light, 0, 0, 1, h - 2);            // left highlight
            Px(light, 1, h - 2);                  // its diagonal into the foot
            Px(shadow, w - 2, 0, 1, h - 2);       // right, inner
            Px(dark, w - 1, 0, 1, h - 2);         // right, outer
            Px(shadow, 2, h - 2, w - 4, 1);       // foot, inner
            Px(dark, 2, h - 1, w - 4, 1);         // foot, outer
            Px(dark, w - 2, h - 2);               // the shadow's diagonal
            dc.Pop();
        }
    }
}
