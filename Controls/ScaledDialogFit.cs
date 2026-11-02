namespace KillerPDF.Controls;

/// <summary>Keeps a dialog drawn at the app's scale usable. Its controls grow with the scale, so a
/// minimum size in plain pixels stops holding them, and a fixed field outgrows its own scroll area.</summary>
internal static class ScaledDialogFit
{
    /// <summary>The window's smallest size along one side: its own minimum, or what its content needs
    /// at this scale, and never more than the screen has.</summary>
    internal static double Minimum(double minimum, double contentAtFullSize, double scale, double workArea) =>
        Math.Min(Math.Max(minimum, contentAtFullSize * scale), workArea);

    /// <summary>The height of a text field in a scroll area, as near the wanted height as the area
    /// allows so the whole field can be brought into view.</summary>
    internal static double FieldHeight(double wanted, double lowest, double scrollAreaHeight) =>
        scrollAreaHeight <= 0 ? wanted : Math.Clamp(scrollAreaHeight, lowest, wanted);
}
