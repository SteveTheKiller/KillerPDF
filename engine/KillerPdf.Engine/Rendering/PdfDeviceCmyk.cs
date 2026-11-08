namespace KillerPdf.Engine.Rendering;

/// <summary>Converts unprofiled process inks through the working CMYK profile of the current render.</summary>
/// <remarks>
/// Outside a render this is the built-in CGATS TR 001 profile, the same one managed color and
/// proof views assume. Hot loops should capture <see cref="PdfCmykDisplayTable.Current"/> once.
/// </remarks>
internal static class PdfDeviceCmyk
{
    internal static uint ToRgb(uint ink) => PdfCmykDisplayTable.Current.ToRgb(ink);

    internal static uint FromRgb(byte red, byte green, byte blue) =>
        PdfCmykDisplayTable.Current.FromRgb(red, green, blue);
}
