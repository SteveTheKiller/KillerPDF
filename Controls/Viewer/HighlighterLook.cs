using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;

namespace KillerPDF.Controls;

/// <summary>
/// The on-screen look of a highlighter mark (PdfBurn.IsHighlighterMark). WPF has no blend modes, so a translucent
/// rectangle laid over the page covers the text as its opacity rises. The mark instead shows what lies under it, through
/// live brushes of the page image, any zoom detail, and everything that paints beneath it on the canvas, multiplied by
/// the highlight color: the paper takes the color and the text stays dark at any opacity, exactly as the saved copy
/// paints it (#200). Annotations drawn after the mark stay on top of it, as they do in the saved copy.
/// In dark mode the page is shown inverted except for kept pictures, so the mark shows the inverted saved look over the
/// inverted parts and the plain multiply over the kept ones.
/// </summary>
internal static class HighlighterLook
{
    /// <summary>How the page under a mark is shown: inverted by dark mode or not, and the page-relative regions dark
    /// mode keeps as they are (pictures, when images are not inverted).</summary>
    internal sealed record Dark(bool Inverted, IReadOnlyList<Rect> Kept)
    {
        internal static readonly Dark Off = new(false, []);
    }

    /// <summary>
    /// Builds the mark for <paramref name="bounds"/> on <paramref name="canvas"/>, the page's annotation canvas in its
    /// page grid; the mark paints above everything already on the canvas. <paramref name="carved"/> is the
    /// eraser-carved outline in canvas units, or null for the whole rectangle. Without a page image to show through, it
    /// falls back to the plain translucent rectangle.
    /// </summary>
    internal static FrameworkElement Build(Canvas canvas, Rect bounds, Geometry? carved, Color color, Dark dark) =>
        Build(canvas, bounds, carved, color, dark, canvas.Children.Count);

    private static FrameworkElement Build(Canvas canvas, Rect bounds, Geometry? carved, Color color, Dark dark, int earlier)
    {
        double canvasWidth = Size(canvas.Width, canvas.ActualWidth);
        double canvasHeight = Size(canvas.Height, canvas.ActualHeight);
        // A hidden page image (a native presenter draws the page instead) has nothing to show through.
        if (canvas.Parent is not Grid grid || grid.Children.Count == 0 || grid.Children[0] is not Image { Visibility: Visibility.Visible } page
            || canvasWidth <= 0 || canvasHeight <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return Plain(bounds, carved, color, dark);

        // The canvas lies exactly over the page image, so the mark's place on the page is its bounds over the canvas size.
        var onPage = new Rect(bounds.X / canvasWidth, bounds.Y / canvasHeight, bounds.Width / canvasWidth, bounds.Height / canvasHeight);
        var mark = new Grid { Width = bounds.Width, Height = bounds.Height, UseLayoutRounding = false };

        // Dark mode keeps pictures as they are: over those the saved highlight is a plain multiply.
        var kept = new List<Rect>();
        if (dark.Inverted)
            foreach (var region in dark.Kept)
            {
                var shared = Rect.Intersect(onPage, region);
                if (shared.IsEmpty || shared.Width <= 0 || shared.Height <= 0) continue;
                kept.Add(new Rect((shared.X - onPage.X) / onPage.Width * bounds.Width, (shared.Y - onPage.Y) / onPage.Height * bounds.Height,
                    shared.Width / onPage.Width * bounds.Width, shared.Height / onPage.Height * bounds.Height));
            }
        Geometry? keptArea = null, invertedArea = null;
        if (kept.Count > 0)
        {
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var rect in kept) group.Children.Add(new RectangleGeometry(rect));
            keptArea = group;
            invertedArea = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, bounds.Width, bounds.Height)), group);
        }
        // Where the page shows inverted, the inverted look; elsewhere, and everywhere outside dark mode, the plain one.
        void ByPage(Func<bool, Geometry?, Grid> layer)
        {
            if (!dark.Inverted) mark.Children.Add(layer(false, null));
            else if (keptArea is null) mark.Children.Add(layer(true, null));
            else
            {
                mark.Children.Add(layer(true, invertedArea!.CloneCurrentValue()));
                mark.Children.Add(layer(false, keptArea.CloneCurrentValue()));
            }
        }
        ByPage((inverted, clip) => PageStack(grid, page, bounds, onPage, color, inverted, clip));

        // What paints beneath the mark, layered in paint order. Annotations and form fields are never inverted on screen,
        // so they multiply plainly, in dark mode too; their transparent parts let the layers below show through. An
        // earlier mark already shows the page's look, so it is multiplied the way the page under it is, and overlaps
        // compound like the saved copy shown the same way.
        // Each earlier mark seen through this one draws its own earlier marks again, so the work a frame takes grows with
        // every link of a chain. Only the most recent few overlapping marks, themselves only a few links deep, are layered;
        // in a deeper pile-up the newest colors win where they overlap, instead of every frame doubling in cost.
        var under = Beneath(canvas, bounds, earlier).ToList();
        var layered = under.Where(child => child is FrameworkElement { Tag: MarkSpec { Depth: < MaxMarkDepth } })
            .TakeLast(MaxEarlierMarks).ToHashSet();
        int depth = 0;
        Grid? run = null;
        foreach (var child in under)
        {
            if (child is FrameworkElement { Tag: MarkSpec earlierMark })
            {
                if (!layered.Contains(child)) continue;
                depth = Math.Max(depth, earlierMark.Depth + 1);
                run = null;
                ByPage((inverted, clip) => Layer(child, bounds, color, inverted, clip));
                continue;
            }
            if (run is null)
            {
                run = new Grid { UseLayoutRounding = false, Effect = new MultiplyEffect(Tint(color, false)) };
                mark.Children.Add(run);
            }
            run.Children.Add(new Rectangle { Fill = Through(child, bounds, BrushMappingMode.Absolute) });
        }

        if (carved is not null) mark.Clip = InMark(carved, bounds);
        else mark.ClipToBounds = true;
        // Selection hit-tests annotations by their geometry; the visual itself must never take a click meant for a form
        // field or anything else under it.
        mark.IsHitTestVisible = false;
        mark.Tag = new MarkSpec(bounds, carved, color, dark, depth);
        Canvas.SetLeft(mark, bounds.X);
        Canvas.SetTop(mark, bounds.Y);
        return mark;
    }

    // The page under the mark, multiplied: a backing for a page not drawn yet, the page image and its zoom detail.
    private static Grid PageStack(Grid grid, Image page, Rect bounds, Rect onPage, Color color, bool inverted, Geometry? clip)
    {
        var stack = new Grid { UseLayoutRounding = false, Clip = clip };
        // A page not drawn yet shows its scaffold; black in dark mode so the inverted look still shows the mark there.
        stack.Children.Add(new Rectangle { Fill = inverted ? Brushes.Black : Brushes.White });
        stack.Children.Add(new Rectangle { Fill = Through(page, onPage, BrushMappingMode.RelativeToBoundingBox) });

        double pageWidth = Size(page.Width, page.ActualWidth), pageHeight = Size(page.Height, page.ActualHeight);
        if (pageWidth > 0 && pageHeight > 0)
            foreach (var detail in grid.Children.OfType<Image>())
            {
                if (ReferenceEquals(detail, page) || detail.Source is null) continue;
                double detailWidth = Size(detail.Width, detail.ActualWidth), detailHeight = Size(detail.Height, detail.ActualHeight);
                if (detailWidth <= 0 || detailHeight <= 0) continue;
                // The viewer places the zoom detail by margin in page-image units.
                var detailOnPage = new Rect(detail.Margin.Left / pageWidth, detail.Margin.Top / pageHeight,
                    detailWidth / pageWidth, detailHeight / pageHeight);
                var shared = Rect.Intersect(onPage, detailOnPage);
                if (shared.IsEmpty || shared.Width <= 0 || shared.Height <= 0) continue;
                stack.Children.Add(new Rectangle
                {
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    UseLayoutRounding = false,
                    Width = shared.Width / onPage.Width * bounds.Width,
                    Height = shared.Height / onPage.Height * bounds.Height,
                    Margin = new Thickness((shared.X - onPage.X) / onPage.Width * bounds.Width,
                        (shared.Y - onPage.Y) / onPage.Height * bounds.Height, 0, 0),
                    Fill = Through(detail, new Rect((shared.X - detailOnPage.X) / detailOnPage.Width,
                        (shared.Y - detailOnPage.Y) / detailOnPage.Height,
                        shared.Width / detailOnPage.Width, shared.Height / detailOnPage.Height), BrushMappingMode.RelativeToBoundingBox)
                });
            }

        stack.Effect = new MultiplyEffect(Tint(color, inverted));
        return stack;
    }

    // One canvas child seen through the mark: a brush of a canvas child paints it as it sits on the canvas, offset,
    // transform and opacity included, so the mark's bounds in canvas units crop it.
    private static Grid Layer(UIElement child, Rect bounds, Color color, bool inverted, Geometry? clip) => new()
    {
        UseLayoutRounding = false, Clip = clip, Effect = new MultiplyEffect(Tint(color, inverted)),
        Children = { new Rectangle { Fill = Through(child, bounds, BrushMappingMode.Absolute) } }
    };

    // Everything on the canvas that paints beneath the mark and may overlap it, in paint order: a lower ZIndex (form
    // fields sit at -1), or the same ZIndex and earlier.
    private static IEnumerable<UIElement> Beneath(Canvas canvas, Rect bounds, int markIndex)
    {
        var under = new List<(int Z, int Index, UIElement Child)>();
        for (int index = 0; index < canvas.Children.Count; index++)
        {
            if (index == markIndex) continue;
            UIElement child = canvas.Children[index];
            int z = Panel.GetZIndex(child);
            if (z > 0 || z == 0 && index > markIndex) continue;
            if (child.Visibility != Visibility.Visible) continue;
            // A mark knows its own bounds exactly; anything else gets the cheap, generous test.
            if (child is FrameworkElement { Tag: MarkSpec spec } ? !Shares(spec.Bounds, bounds) : !Overlaps(child, bounds)) continue;
            under.Add((z, index, child));
        }
        return under.OrderBy(item => item.Z).ThenBy(item => item.Index).Select(item => item.Child);
    }

    // Cheap bounds for a canvas child before layout. Anything turned, or of unknown size, counts as overlapping: a
    // missed child would vanish under the mark, an extra one only costs a brush.
    private static bool Overlaps(UIElement child, Rect bounds)
    {
        if (child is FrameworkElement { LayoutTransform: { } layout } && !layout.Value.IsIdentity
            || child.RenderTransform is { } turn && !turn.Value.IsIdentity) return true;
        Rect area;
        switch (child)
        {
            case Polyline { Points.Count: > 0 } line: area = PointsBounds(line.Points, line.StrokeThickness); break;
            case Polygon { Points.Count: > 0 } polygon: area = PointsBounds(polygon.Points, polygon.StrokeThickness); break;
            case System.Windows.Shapes.Path { Data: { } data } path when double.IsNaN(Canvas.GetLeft(path)):
                // An emptied outline (a Shapes rectangle erased away) has no bounds and paints nothing.
                area = data.Bounds;
                if (area.IsEmpty) return false;
                area.Inflate(path.StrokeThickness, path.StrokeThickness);
                break;
            case FrameworkElement element:
                double left = Canvas.GetLeft(element), top = Canvas.GetTop(element);
                double width = Size(element.Width, element.ActualWidth), height = Size(element.Height, element.ActualHeight);
                if (width <= 0 || height <= 0) return true;
                area = new Rect(double.IsFinite(left) ? left : 0, double.IsFinite(top) ? top : 0, width, height);
                break;
            default: return true;
        }
        return Shares(area, bounds);
    }

    // Overlap with area: rectangles that only touch along an edge share nothing.
    private static bool Shares(Rect a, Rect b)
    {
        var shared = Rect.Intersect(a, b);
        return !shared.IsEmpty && shared.Width > 0 && shared.Height > 0;
    }

    // At most 3 + 3 x 3 brushed marks per frame (twice that over dark mode's kept pictures); overlaps of up to three
    // marks still compound exactly.
    private const int MaxEarlierMarks = 3, MaxMarkDepth = 2;

    private static Rect PointsBounds(PointCollection points, double stroke)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var point in points)
        {
            minX = Math.Min(minX, point.X); minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X); maxY = Math.Max(maxY, point.Y);
        }
        var area = new Rect(minX, minY, maxX - minX, maxY - minY);
        area.Inflate(stroke, stroke);
        return area;
    }

    private static double Size(double set, double actual) => double.IsFinite(set) ? set : actual;

    /// <summary>
    /// A page-relative region of the upright page, turned the way the viewer turns a page bitmap (clockwise), so dark
    /// mode's kept picture boxes line up with a rotated page.
    /// </summary>
    internal static Rect KeptOnPage(double left, double top, double right, double bottom, int rotation) =>
        (((rotation % 360) + 360) % 360) switch
        {
            90 => new Rect(1 - bottom, left, bottom - top, right - left),
            180 => new Rect(1 - right, 1 - bottom, right - left, bottom - top),
            270 => new Rect(top, 1 - right, bottom - top, right - left),
            _ => new Rect(left, top, right - left, bottom - top)
        };

    /// <summary>The per-channel factor a page pixel is multiplied by: 1 - a + a * color, with the inverted look in w.</summary>
    internal static Point4D Tint(Color color, bool inverted)
    {
        double a = color.A / 255d;
        return new Point4D(1 - a + a * color.R / 255d, 1 - a + a * color.G / 255d, 1 - a + a * color.B / 255d,
            inverted ? 1 : 0);
    }

    private static VisualBrush Through(Visual layer, Rect viewbox, BrushMappingMode units) => new(layer)
    {
        Viewbox = viewbox, ViewboxUnits = units, Stretch = Stretch.Fill
    };

    private static Geometry InMark(Geometry carved, Rect bounds)
    {
        var shifted = carved.CloneCurrentValue();
        var move = new TranslateTransform(-bounds.X, -bounds.Y);
        shifted.Transform = shifted.Transform is { } own && !own.Value.IsIdentity
            ? new TransformGroup { Children = { own, move } } : move;
        return shifted;
    }

    // The translucent rectangle, for a page with nothing to show through yet. It keeps its spec, so a Refresh once the
    // page image shows turns it into the real mark.
    private static FrameworkElement Plain(Rect bounds, Geometry? carved, Color color, Dark dark)
    {
        var spec = new MarkSpec(bounds, carved, color, dark, 0);
        if (carved is not null)
            return new System.Windows.Shapes.Path { Fill = new SolidColorBrush(color), Data = carved, IsHitTestVisible = false, Tag = spec };
        var rect = new Rectangle { Fill = new SolidColorBrush(color), Width = bounds.Width, Height = bounds.Height,
            IsHitTestVisible = false, Tag = spec };
        Canvas.SetLeft(rect, bounds.X);
        Canvas.SetTop(rect, bounds.Y);
        return rect;
    }

    private sealed record MarkSpec(Rect Bounds, Geometry? Carved, Color Color, Dark Dark, int Depth);

    /// <summary>
    /// Rebuilds the marks on <paramref name="canvas"/> in place, in order, for zoom detail that arrived or left after
    /// they were drawn, or for dark mode turned on or off (<paramref name="dark"/>). Only marks are replaced, so selection
    /// chrome and a gesture in progress stay as they are.
    /// </summary>
    internal static void Refresh(Canvas canvas, Dark dark) => Refresh(canvas, () => dark);

    /// <summary>As above; <paramref name="dark"/> is asked once, and only when the canvas holds a mark, since working
    /// out dark mode's kept pictures can read the document.</summary>
    internal static void Refresh(Canvas canvas, Func<Dark>? dark = null)
    {
        Dark? asked = null;
        for (int index = 0; index < canvas.Children.Count; index++)
            if (canvas.Children[index] is FrameworkElement { Tag: MarkSpec spec })
            {
                Dark look = dark is null ? spec.Dark : asked ??= dark();
                // Built while the old mark still holds its place, so every other child keeps the index that orders it.
                var rebuilt = Build(canvas, spec.Bounds, spec.Carved, spec.Color, look, index);
                // A panel's children cannot be replaced by index: take the old mark out, then put the new one in its place.
                canvas.Children.RemoveAt(index);
                canvas.Children.Insert(index, rebuilt);
            }
    }

    // Compiled from this HLSL with fxc /T ps_2_0 /E main /Fo Highlighter.ps Highlighter.fx (Windows SDK 10.0.26100):
    //
    //   sampler2D input : register(s0);
    //   float4 tint : register(c0);
    //
    //   float4 main(float2 uv : TEXCOORD) : COLOR
    //   {
    //       float4 c = tex2D(input, uv);
    //       float3 multiplied = c.rgb * tint.rgb;
    //       float3 inverted = c.aaa - (c.aaa - c.rgb) * tint.rgb;
    //       return float4(lerp(multiplied, inverted, tint.a), c.a);
    //   }
    //
    // The input is premultiplied, so both results stay premultiplied. tint.rgb is the factor from Tint and tint.a is 1
    // over dark mode's inverted parts, where the mark shows the inverted saved look: 1 - (1 - page) * factor.
    internal sealed class MultiplyEffect : ShaderEffect
    {
        public static readonly DependencyProperty InputProperty =
            RegisterPixelShaderSamplerProperty("Input", typeof(MultiplyEffect), 0);
        public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
            "Tint", typeof(Point4D), typeof(MultiplyEffect), new UIPropertyMetadata(new Point4D(1, 1, 1, 0), PixelShaderConstantCallback(0)));

        private static readonly byte[] Bytecode =
        {
            0x00, 0x02, 0xFF, 0xFF, 0xFE, 0xFF, 0x2A, 0x00, 0x43, 0x54, 0x41, 0x42, 0x1C, 0x00, 0x00, 0x00, 0x7B, 0x00, 0x00, 0x00, 0x00, 0x02, 0xFF, 0xFF,
            0x02, 0x00, 0x00, 0x00, 0x1C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x74, 0x00, 0x00, 0x00, 0x44, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x02, 0x00, 0x4C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x5C, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00,
            0x64, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x69, 0x6E, 0x70, 0x75, 0x74, 0x00, 0xAB, 0xAB, 0x04, 0x00, 0x0C, 0x00, 0x01, 0x00, 0x01, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x74, 0x69, 0x6E, 0x74, 0x00, 0xAB, 0xAB, 0xAB, 0x01, 0x00, 0x03, 0x00, 0x01, 0x00, 0x04, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x70, 0x73, 0x5F, 0x32, 0x5F, 0x30, 0x00, 0x4D, 0x69, 0x63, 0x72, 0x6F, 0x73, 0x6F, 0x66, 0x74,
            0x20, 0x28, 0x52, 0x29, 0x20, 0x48, 0x4C, 0x53, 0x4C, 0x20, 0x53, 0x68, 0x61, 0x64, 0x65, 0x72, 0x20, 0x43, 0x6F, 0x6D, 0x70, 0x69, 0x6C, 0x65,
            0x72, 0x20, 0x31, 0x30, 0x2E, 0x31, 0x00, 0xAB, 0x1F, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x03, 0xB0, 0x1F, 0x00, 0x00, 0x02,
            0x00, 0x00, 0x00, 0x90, 0x00, 0x08, 0x0F, 0xA0, 0x42, 0x00, 0x00, 0x03, 0x00, 0x00, 0x0F, 0x80, 0x00, 0x00, 0xE4, 0xB0, 0x00, 0x08, 0xE4, 0xA0,
            0x02, 0x00, 0x00, 0x03, 0x01, 0x00, 0x07, 0x80, 0x00, 0x00, 0xE4, 0x81, 0x00, 0x00, 0xFF, 0x80, 0x04, 0x00, 0x00, 0x04, 0x01, 0x00, 0x07, 0x80,
            0x01, 0x00, 0xE4, 0x80, 0x00, 0x00, 0xE4, 0xA1, 0x00, 0x00, 0xFF, 0x80, 0x04, 0x00, 0x00, 0x04, 0x01, 0x00, 0x07, 0x80, 0x00, 0x00, 0xE4, 0x80,
            0x00, 0x00, 0xE4, 0xA1, 0x01, 0x00, 0xE4, 0x80, 0x05, 0x00, 0x00, 0x03, 0x02, 0x00, 0x07, 0x80, 0x00, 0x00, 0xE4, 0x80, 0x00, 0x00, 0xE4, 0xA0,
            0x04, 0x00, 0x00, 0x04, 0x00, 0x00, 0x07, 0x80, 0x00, 0x00, 0xFF, 0xA0, 0x01, 0x00, 0xE4, 0x80, 0x02, 0x00, 0xE4, 0x80, 0x01, 0x00, 0x00, 0x02,
            0x00, 0x08, 0x0F, 0x80, 0x00, 0x00, 0xE4, 0x80, 0xFF, 0xFF, 0x00, 0x00,
        };

        // Declared after Bytecode: static fields initialize in order.
        private static readonly PixelShader Shader = LoadShader();

        public MultiplyEffect(Point4D tint)
        {
            PixelShader = Shader;
            SetValue(TintProperty, tint);
            // A value equal to the default raises no change, so push every register once.
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(TintProperty);
        }

        public Point4D Tint => (Point4D)GetValue(TintProperty);

        private static PixelShader LoadShader()
        {
            var shader = new PixelShader();
            shader.SetStreamSource(new MemoryStream(Bytecode));
            shader.Freeze();
            return shader;
        }
    }
}
