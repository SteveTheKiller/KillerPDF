using System.Windows;

namespace KillerPDF.Services;

/// <summary>The editable copy of a picture lifted from a page by a click, cut or copy. Its picture is read upright,
/// so on a page shown turned it records the page's turn, and cut and paste puts it back as the page showed it.</summary>
internal static class LiftedPicture
{
    internal static ImageAnnotation Copy(int page, Rect bounds, int pageRotation) => new()
    {
        PageIndex = page, Position = bounds.TopLeft, Scale = 1,
        SourceWidth = bounds.Width, SourceHeight = bounds.Height,
        Rotation = ((pageRotation % 360) + 360) % 360,
    };
}
