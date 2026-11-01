namespace KillerPDF.Services;

/// <summary>The size a signature takes while it is being placed.</summary>
internal static class SignaturePlacementScale
{
    internal const double Minimum = 0.05;
    internal const double Maximum = 3;
    private const double NotchFactor = 1.1;
    private const double WheelNotch = 120;

    internal static double Default(bool initials) => initials ? 0.3 : 0.5;

    /// <summary>The scale after a mouse wheel delta; one notch is ten percent.</summary>
    internal static double Step(double scale, int wheelDelta) =>
        Math.Clamp(scale * Math.Pow(NotchFactor, wheelDelta / WheelNotch), Minimum, Maximum);
}
