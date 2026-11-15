namespace KillerPDF.Services;

/// <summary>
/// Maps the replacement-text blur slider (0 to 1) to blur in points on an exponential curve, so light blur, which is
/// what people use, takes most of the travel and the strongest blur sits in a short stretch at the right end. (#467)
/// </summary>
internal static class BlurSliderScale
{
    internal const double MaximumPoints = 2;

    // How steep the curve is: half the travel reaches about an eighth of the strongest blur.
    private const double Steepness = 4;
    private static readonly double Span = Math.Exp(Steepness) - 1;

    internal static double ToPoints(double slider) =>
        MaximumPoints * (Math.Exp(Steepness * Math.Clamp(slider, 0, 1)) - 1) / Span;

    internal static double ToSlider(double points) =>
        Math.Log(1 + Math.Clamp(points, 0, MaximumPoints) / MaximumPoints * Span) / Steepness;
}
