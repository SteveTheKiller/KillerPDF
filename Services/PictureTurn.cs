using System.Windows;

namespace KillerPDF.Services;

/// <summary>
/// Turns a placed picture in place for the picture card's rotate buttons. The picture keeps its center; a quarter
/// turn swaps its footprint, and its Rotation adds the turn so the preview and the saved page both draw it turned.
/// </summary>
internal static class PictureTurn
{
    internal static void Turn(ImageAnnotation picture, int degrees)
    {
        int turn = ((degrees % 360) + 360) % 360;
        if (turn % 90 != 0) throw new ArgumentOutOfRangeException(nameof(degrees), "Pictures turn in quarter turns.");
        picture.Rotation = (picture.Rotation + turn) % 360;
        if (turn % 180 != 0)
        {
            double w = picture.SourceWidth * picture.Scale, h = picture.SourceHeight * picture.Scale;
            double cx = picture.Position.X + w / 2, cy = picture.Position.Y + h / 2;
            (picture.SourceWidth, picture.SourceHeight) = (picture.SourceHeight, picture.SourceWidth);
            picture.Position = new Point(cx - h / 2, cy - w / 2);
        }
        picture.CachedBitmap = null;
    }
}
