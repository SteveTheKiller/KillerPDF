using System.Windows;

namespace KillerPDF.Services;

internal static class AnnotationScale
{
    internal static void Remap(PageAnnotation annotation, int sourceWidth, int sourceHeight,
        int targetWidth, int targetHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        double sx = (double)targetWidth / sourceWidth, sy = (double)targetHeight / sourceHeight;
        Point Map(Point point) => new(point.X * sx, point.Y * sy);
        switch (annotation)
        {
            case TextAnnotation text:
                text.Position = Map(text.Position);
                text.Width *= text.Rotation is 90 or 270 ? sy : sx;
                text.Height *= text.Rotation is 90 or 270 ? sx : sy;
                text.FontSize *= sy;
                text.LetterSpacing *= sy;
                text.BlurRadius *= sy;
                break;
            case HighlightAnnotation highlight:
                highlight.Bounds = new Rect(Map(highlight.Bounds.TopLeft), Map(highlight.Bounds.BottomRight));
                if (highlight.Erases is not null)
                    foreach (HighlightErase erase in highlight.Erases)
                    {
                        for (int i = 0; i < erase.Points.Count; i++) erase.Points[i] = Map(erase.Points[i]);
                        erase.Radius *= sy;
                    }
                break;
            case InkAnnotation ink:
                for (int i = 0; i < ink.Points.Count; i++) ink.Points[i] = Map(ink.Points[i]);
                ink.StrokeWidth *= sy;
                break;
            case PlacedAnnotation placed:
                placed.Position = Map(placed.Position);
                placed.Scale *= sy;
                break;
            default:
                throw new NotSupportedException("This annotation has no canvas scaling policy.");
        }
    }
}
