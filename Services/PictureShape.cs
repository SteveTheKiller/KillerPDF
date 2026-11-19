namespace KillerPDF.Services;

/// <summary>
/// The picture card's shape math. Reset Size gives a stretched or squashed picture back its own proportions, and a
/// flip, which the card makes as the picture looks on the page, lands on the picture's own pixels.
/// </summary>
internal static class PictureShape
{
    /// <summary>True when a width by height footprint is more than half a percent off the pixels' proportions.</summary>
    internal static bool OutOfShape(double width, double height, int pixelWidth, int pixelHeight) =>
        width > 0 && height > 0 && pixelWidth > 0 && pixelHeight > 0
        && Math.Abs(width / height / ((double)pixelWidth / pixelHeight) - 1) > 0.005;

    /// <summary>The height that gives a footprint of this width the pixels' own proportions.</summary>
    internal static double HeightFor(double width, int pixelWidth, int pixelHeight) => width * pixelHeight / pixelWidth;

    /// <summary>True when a flip seen on the page mirrors the stored pixels left to right. A picture drawn a quarter
    /// turn round shows its pixels sideways, so a flip across the page flips them top to bottom.</summary>
    internal static bool FlipsAcrossPixels(bool horizontal, int rotation) =>
        horizontal ^ (((rotation % 360) + 360) % 180 != 0);
}
