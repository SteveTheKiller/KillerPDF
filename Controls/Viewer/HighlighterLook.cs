using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;

namespace KillerPDF.Controls;

/// <summary>
/// The on-screen look of a highlighter mark. WPF has no blend modes, so a translucent rectangle laid over the page
/// covers the text as its opacity rises. The mark instead shows the page pixels under it, through a live brush of the
/// page image and of any zoom detail it overlaps, multiplied by the highlight color: the paper takes the color and the
/// text stays dark at any opacity, the same as the saved copy (#200). In dark mode the page is shown inverted, so the
/// mark shows the inverted saved look. Covers, filled Shapes rectangles, strikethrough and underline are not marks.
/// </summary>
internal static class HighlighterLook
{
    internal static bool Multiplies(HighlightAnnotation highlight) =>
        highlight is not CoverAnnotation && !highlight.IsShape && highlight.Style == HighlightStyle.Fill;

    /// <summary>
    /// Builds the mark for <paramref name="bounds"/> on <paramref name="canvas"/>, the page's annotation canvas in its
    /// page grid. <paramref name="carved"/> is the eraser-carved outline in canvas units, or null for the whole rectangle.
    /// Without a page image to show through, it falls back to the plain translucent rectangle.
    /// </summary>
    internal static FrameworkElement Build(Canvas canvas, Rect bounds, Geometry? carved, Color color, bool inverted)
    {
        double canvasWidth = double.IsFinite(canvas.Width) ? canvas.Width : canvas.ActualWidth;
        double canvasHeight = double.IsFinite(canvas.Height) ? canvas.Height : canvas.ActualHeight;
        if (canvas.Parent is not Grid grid || grid.Children.Count == 0 || grid.Children[0] is not Image page
            || canvasWidth <= 0 || canvasHeight <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return Plain(bounds, carved, color);

        // The canvas lies exactly over the page image, so the mark's place on the page is its bounds over the canvas size.
        var onPage = new Rect(bounds.X / canvasWidth, bounds.Y / canvasHeight, bounds.Width / canvasWidth, bounds.Height / canvasHeight);
        var mark = new Grid { Width = bounds.Width, Height = bounds.Height };
        // A page not drawn yet shows its white scaffold, and the mark shows its color on it.
        mark.Children.Add(new Rectangle { Fill = Brushes.White });
        mark.Children.Add(new Rectangle { Fill = Through(page, onPage) });

        double pageWidth = double.IsFinite(page.Width) ? page.Width : page.ActualWidth;
        double pageHeight = double.IsFinite(page.Height) ? page.Height : page.ActualHeight;
        if (pageWidth > 0 && pageHeight > 0)
            foreach (var detail in grid.Children.OfType<Image>())
            {
                if (ReferenceEquals(detail, page) || detail.Source is null) continue;
                double detailWidth = double.IsFinite(detail.Width) ? detail.Width : detail.ActualWidth;
                double detailHeight = double.IsFinite(detail.Height) ? detail.Height : detail.ActualHeight;
                if (detailWidth <= 0 || detailHeight <= 0) continue;
                // The viewer places the zoom detail by margin in page-image units.
                var detailOnPage = new Rect(detail.Margin.Left / pageWidth, detail.Margin.Top / pageHeight,
                    detailWidth / pageWidth, detailHeight / pageHeight);
                var shared = Rect.Intersect(onPage, detailOnPage);
                if (shared.IsEmpty || shared.Width <= 0 || shared.Height <= 0) continue;
                mark.Children.Add(new Rectangle
                {
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Width = shared.Width / onPage.Width * bounds.Width,
                    Height = shared.Height / onPage.Height * bounds.Height,
                    Margin = new Thickness((shared.X - onPage.X) / onPage.Width * bounds.Width,
                        (shared.Y - onPage.Y) / onPage.Height * bounds.Height, 0, 0),
                    Fill = Through(detail, new Rect((shared.X - detailOnPage.X) / detailOnPage.Width,
                        (shared.Y - detailOnPage.Y) / detailOnPage.Height,
                        shared.Width / detailOnPage.Width, shared.Height / detailOnPage.Height))
                });
            }

        if (carved is not null)
        {
            mark.Clip = InMark(carved, bounds);
            mark.IsHitTestVisible = false;
        }
        else mark.ClipToBounds = true;
        mark.Effect = new MultiplyEffect(Tint(color, inverted));
        mark.Tag = new MarkSpec(bounds, carved, color, inverted);
        Canvas.SetLeft(mark, bounds.X);
        Canvas.SetTop(mark, bounds.Y);
        return mark;
    }

    /// <summary>
    /// Gives a highlight drag's preview rectangle the mark's look for its current place on the canvas: the page image
    /// through it, multiplied. Called as the drag grows the rectangle. The zoom detail is left out of the preview.
    /// </summary>
    internal static void Preview(Rectangle preview, Canvas canvas, Color color, bool inverted)
    {
        double canvasWidth = double.IsFinite(canvas.Width) ? canvas.Width : canvas.ActualWidth;
        double canvasHeight = double.IsFinite(canvas.Height) ? canvas.Height : canvas.ActualHeight;
        double left = Canvas.GetLeft(preview), top = Canvas.GetTop(preview);
        double width = preview.Width, height = preview.Height;
        if (canvas.Parent is not Grid grid || grid.Children.Count == 0 || grid.Children[0] is not Image page
            || page.Source is null || canvasWidth <= 0 || canvasHeight <= 0 || !double.IsFinite(left) || !double.IsFinite(top)
            || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            preview.Fill = new SolidColorBrush(color);
            preview.Effect = null;
            return;
        }
        preview.Fill = Through(page, new Rect(left / canvasWidth, top / canvasHeight, width / canvasWidth, height / canvasHeight));
        if (preview.Effect is not MultiplyEffect effect) preview.Effect = new MultiplyEffect(Tint(color, inverted));
        else effect.SetValue(MultiplyEffect.TintProperty, Tint(color, inverted));
    }

    private sealed record MarkSpec(Rect Bounds, Geometry? Carved, Color Color, bool Inverted);

    /// <summary>
    /// Rebuilds the marks on <paramref name="canvas"/> in place, for zoom detail that arrived or left after they were
    /// drawn, or for dark mode turned on or off (<paramref name="inverted"/>). Only marks are touched, so selection
    /// chrome and a gesture in progress stay as they are.
    /// </summary>
    internal static void Refresh(Canvas canvas, bool? inverted = null)
    {
        for (int index = 0; index < canvas.Children.Count; index++)
            if (canvas.Children[index] is Grid { Tag: MarkSpec spec })
            {
                // A panel's children cannot be replaced by index: take the old mark out, then put the new one in its place.
                canvas.Children.RemoveAt(index);
                canvas.Children.Insert(index, Build(canvas, spec.Bounds, spec.Carved, spec.Color, inverted ?? spec.Inverted));
            }
    }

    /// <summary>The per-channel factor a page pixel is multiplied by: 1 - a + a * color, with dark mode in w.</summary>
    internal static Point4D Tint(Color color, bool inverted)
    {
        double a = color.A / 255d;
        return new Point4D(1 - a + a * color.R / 255d, 1 - a + a * color.G / 255d, 1 - a + a * color.B / 255d,
            inverted ? 1 : 0);
    }

    private static VisualBrush Through(Visual layer, Rect viewbox) => new(layer)
    {
        Viewbox = viewbox, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.Fill
    };

    private static Geometry InMark(Geometry carved, Rect bounds)
    {
        var shifted = carved.CloneCurrentValue();
        var move = new TranslateTransform(-bounds.X, -bounds.Y);
        shifted.Transform = shifted.Transform is { } own && !own.Value.IsIdentity
            ? new TransformGroup { Children = { own, move } } : move;
        return shifted;
    }

    private static FrameworkElement Plain(Rect bounds, Geometry? carved, Color color)
    {
        if (carved is not null)
            return new System.Windows.Shapes.Path { Fill = new SolidColorBrush(color), Data = carved, IsHitTestVisible = false };
        var rect = new Rectangle { Fill = new SolidColorBrush(color), Width = bounds.Width, Height = bounds.Height };
        Canvas.SetLeft(rect, bounds.X);
        Canvas.SetTop(rect, bounds.Y);
        return rect;
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
    // The input is premultiplied, so both results stay premultiplied. tint.rgb is the factor from Tint and tint.a is 1 in
    // dark mode, where the page shows inverted and the mark shows the inverted saved look: 1 - (1 - page) * factor.
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
