using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerPDF.Services;

/// <summary>
/// Draws a page's unsaved edits over its thumbnail, so the page list shows what each page looks like before it is
/// saved. The edits are in editing-canvas units (<paramref name="canvas"/>); the thumbnail is the page's own
/// render, so everything is scaled from the canvas to it. Must run on the UI thread.
/// </summary>
internal static class ThumbnailOverlay
{
    /// <param name="picture">The decoded, turned bitmap of a placed picture or image signature, as the page shows
    /// it, or null when it has none.</param>
    internal static BitmapSource Compose(BitmapSource page, IReadOnlyList<PageAnnotation> items, Size canvas,
        Func<PlacedAnnotation, BitmapSource?> picture)
    {
        var shown = items.Where(item => item.IsVisible).ToList();
        if (shown.Count == 0 || canvas.Width <= 0 || canvas.Height <= 0) return page;
        int width = page.PixelWidth, height = page.PixelHeight;
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawImage(page, new Rect(0, 0, width, height));
            context.PushTransform(new ScaleTransform(width / canvas.Width, height / canvas.Height));
            foreach (PageAnnotation item in shown) Draw(context, item, picture);
            context.Pop();
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static void Draw(DrawingContext context, PageAnnotation item, Func<PlacedAnnotation, BitmapSource?> picture)
    {
        switch (item)
        {
            case HighlightAnnotation highlight:   // covers too
                context.DrawRectangle(new SolidColorBrush(highlight.GetColor()), null, highlight.DrawRect());
                break;

            case InkAnnotation ink when ink.Points.Count > 1:
                var line = new StreamGeometry();
                using (StreamGeometryContext stroke = line.Open())
                {
                    stroke.BeginFigure(ink.Points[0], ink.HasFill, ink.HasFill);
                    stroke.PolyLineTo(ink.Points.Skip(1).ToList(), true, true);
                }
                context.DrawGeometry(ink.HasFill ? new SolidColorBrush(ink.GetFillColor()) : null,
                    new Pen(new SolidColorBrush(ink.GetColor()), Math.Max(1, ink.StrokeWidth))
                    { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round },
                    line);
                break;

            case MeasurementAnnotation measure when measure.IsVisibleOnScreen && measure.Points.Count > 1:
                var pen = new Pen(new SolidColorBrush(measure.GetColor()), Math.Max(1, measure.StrokeWidth));
                for (int i = 1; i < measure.Points.Count; i++) context.DrawLine(pen, measure.Points[i - 1], measure.Points[i]);
                if (measure.Closed) context.DrawLine(pen, measure.Points[^1], measure.Points[0]);
                break;

            case TextAnnotation text:
                DrawText(context, text);
                break;

            case PlacedAnnotation placed:
                var box = new Rect(placed.Position, new Size(placed.SourceWidth * placed.Scale, placed.SourceHeight * placed.Scale));
                if (picture(placed) is { } image) context.DrawImage(image, box);
                else if (placed is SignatureAnnotation { Strokes.Count: > 0 } signature)
                {
                    var ink2 = new Pen(Brushes.Black, Math.Max(1, signature.StrokeWidth * signature.Scale));
                    foreach (var path in signature.Strokes)
                        for (int i = 1; i < path.Count; i++)
                            context.DrawLine(ink2, Place(path[i - 1], signature), Place(path[i], signature));
                }
                break;
        }
    }

    private static Point Place(Point source, PlacedAnnotation placed) =>
        new(placed.Position.X + source.X * placed.Scale, placed.Position.Y + source.Y * placed.Scale);

    // A text box's fill and its words, turned as the box is. At thumbnail size this is enough to show the box
    // sits there; the exact line breaks of the editor are not needed.
    private static void DrawText(DrawingContext context, TextAnnotation text)
    {
        Size footprint = text.FootprintSize;
        var center = new Point(text.Position.X + footprint.Width / 2, text.Position.Y + footprint.Height / 2);
        context.PushTransform(new RotateTransform(TextTurn.Normalize(text.Rotation), center.X, center.Y));
        var box = new Rect(center.X - text.Width / 2, center.Y - text.Height / 2, text.Width, text.Height);
        if (text.HasFill) context.DrawRectangle(new SolidColorBrush(text.GetFill()), null, box);
        if (!string.IsNullOrEmpty(text.Content))
        {
            var face = new Typeface(new FontFamily(text.FontName),
                text.Italic ? FontStyles.Italic : FontStyles.Normal,
                text.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            var formatted = new FormattedText(text.Content, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                face, Math.Max(1, text.FontSize), new SolidColorBrush(text.GetColor()), 1)
            { MaxTextWidth = Math.Max(1, text.Width), MaxTextHeight = Math.Max(1, text.Height) };
            context.DrawText(formatted, box.TopLeft);
        }
        context.Pop();
    }
}
